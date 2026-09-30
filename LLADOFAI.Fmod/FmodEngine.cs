using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using UnityEngine;
using UnityEngine.Audio;

namespace LLADOFAI.Fmod
{
    internal sealed class FmodException : Exception
    {
        internal FmodException(string operation, int result)
            : base(operation + " returned FMOD_RESULT " + result) { }
    }

    // Owns the FMOD System and plays AudioSource requests on FMOD channels.
    //
    // Clock model: every voice of a source that follows AudioListener.pause plays in
    // the "pausable" channel group, and AudioSettings.dspTime is that group's DSP
    // clock. Pausing the group stops its clock and every delayed start inside it, so
    // listener pause keeps game time, scheduled sounds and audio consistent exactly
    // like Unity's own DSP clock. Sources with ignoreListenerPause play in a second
    // group whose clock never pauses.
    //
    // Everything except DspTime is called on Unity's main thread.
    internal sealed class FmodEngine
    {
        private const int SoundWaitMilliseconds = 3000;
        private const int MaxLoggedErrors = 20;

        private enum StartMode { Now, Relative, Absolute }

        private sealed class Voice
        {
            internal IntPtr Channel;
            internal IntPtr Group;
            internal FmodSound Sound;
            internal float Scale;
            internal bool OneShot;
            internal ulong StartClock;
            internal ulong EndClock;
        }

        private sealed class SourceState
        {
            internal AudioSource Source;
            internal int Id;
            internal int Index;
            internal Voice Primary;
            internal readonly List<Voice> OneShots = new List<Voice>(4);
            internal bool Paused;
            internal int PendingSamples = -1;
            internal float Volume = 1f;
            internal float MixerGain = 1f;
            internal int MixerMask;
            internal IntPtr Spectrum;
            internal IntPtr SpectrumChannel;
            internal FmodSourceLink Link;
        }

        internal static volatile FmodEngine Active;
        // Set while the engine calls Unity's own AudioSource implementation.
        [ThreadStatic] internal static bool Passthrough;

        private readonly Action<string> _log;
        private readonly Action<string> _error;
        private readonly int _mainThread;
        private readonly Dictionary<int, SourceState> _states = new Dictionary<int, SourceState>();
        private readonly List<SourceState> _stateList = new List<SourceState>();
        private readonly Stack<SourceState> _statePool = new Stack<SourceState>();
        private readonly Stack<Voice> _voicePool = new Stack<Voice>();
        private readonly List<FmodSourceLink> _links = new List<FmodSourceLink>();
        private readonly FmodMixer _mixer = new FmodMixer();
        private IntPtr _system, _master, _pausable, _unpausable, _limiter, _listenerFft;
        private FmodSoundCache _sounds;
        private FmodStaThread _comThread;
        private FmodClock _clock;
        private int _spectrumParameter = -1, _fftSizeParameter = -1;
        private bool _listenerPaused;
        private float _listenerVolume = 1f;
        private int _loggedErrors;
        // Mixer load, sampled twice a second. FMOD reports DSP time as a percentage of
        // real time; near 100% the output underruns and crackles.
        private readonly float[] _cpuUsage = new float[16];
        private readonly Stopwatch _cpuTimer = Stopwatch.StartNew();
        private int _overloadedSamples;
        private bool _overloadLogged;
        internal float MixerCpu { get; private set; }
        private double _failedAtTime;
        private double _lastDspTime;
        private Stopwatch _failedTimer;

        internal bool Failed { get; private set; }
        internal string FailureMessage { get; private set; }
        internal int SoftwareRate { get; private set; }
        internal int BufferLength { get; private set; }
        internal int BufferCount { get; private set; }
        internal string DriverName { get; private set; }
        internal int OutputType { get; private set; }
        internal string RuntimeVersion { get; private set; }
        internal int FallbackPlays { get; private set; }

        internal FmodEngine(Action<string> log, Action<string> error)
        {
            _log = log;
            _error = error;
            _mainThread = Thread.CurrentThread.ManagedThreadId;
        }

        private static void Check(int result, string operation)
        {
            if (result != FmodNative.Ok) throw new FmodException(operation, result);
        }

        // ---------------------------------------------------------------- lifecycle

        internal void Initialize(FmodSettings settings, double unityDspTime)
        {
            // Keep Unity's voice limits so the game gets the same virtual/real voice
            // behaviour it was tuned for.
            AudioConfiguration unity = AudioSettings.GetConfiguration();
            int realVoices = Mathf.Clamp(unity.numRealVoices, 64, 1024);
            int virtualVoices = Mathf.Clamp(unity.numVirtualVoices, 256, 4095);
            if (settings.Output == FmodOutput.Asio)
            {
                _comThread = new FmodStaThread("LLADOFAI FMOD ASIO");
                _comThread.Invoke(() => CreateSystem(settings, realVoices, virtualVoices));
            }
            else
            {
                CreateSystem(settings, realVoices, virtualVoices);
            }

            _pausable = CreateGroup("LLADOFAI.Pausable");
            _unpausable = CreateGroup("LLADOFAI.IgnoreListenerPause");
            _limiter = CreateMasterLimiter();
            _sounds = new FmodSoundCache(_system, new FmodAssetAudio(Application.dataPath, _log), _log);

            ulong clock, parent;
            Check(FmodNative.FMOD_ChannelGroup_GetDSPClock(_pausable, out clock, out parent), "ChannelGroup_GetDSPClock");
            _clock = new FmodClock(SoftwareRate, clock, unityDspTime);
            _lastDspTime = unityDspTime;
            _listenerPaused = !AudioListener.pause;
            _listenerVolume = -1f;
            SyncListener();

            _log?.Invoke(string.Format("FMOD {0}: {1} '{2}', {3} Hz, DSP buffer {4} x {5} ({6:F1} ms), {7} real / {8} virtual voices.",
                RuntimeVersion, settings.Output, DriverName, SoftwareRate, BufferLength, BufferCount,
                BufferLength * BufferCount * 1000.0 / SoftwareRate, realVoices, virtualVoices));
        }

        private void CreateSystem(FmodSettings settings, int realVoices, int virtualVoices)
        {
            Check(FmodNative.FMOD_System_Create(out _system, FmodNative.HeaderVersion), "System_Create");
            uint version, build;
            Check(FmodNative.FMOD_System_GetVersion(_system, out version, out build), "System_GetVersion");
            RuntimeVersion = string.Format("{0:X}.{1:X2}.{2:X2} (build {3})", version >> 16, (version >> 8) & 0xFF, version & 0xFF, build);
            if ((version & 0xFFFF00) != (FmodNative.HeaderVersion & 0xFFFF00))
                throw new InvalidOperationException("fmod.dll is version " + RuntimeVersion + "; LLADOFAI needs FMOD Core 2.03.x.");

            OutputType = settings.Output == FmodOutput.Asio ? FmodNative.OutputAsio : FmodNative.OutputWasapi;
            Check(FmodNative.FMOD_System_SetOutput(_system, OutputType), "System_SetOutput(" + settings.Output + ")");
            int count;
            Check(FmodNative.FMOD_System_GetNumDrivers(_system, out count), "System_GetNumDrivers");
            if (count <= 0) throw new InvalidOperationException("FMOD found no " + settings.Output + " output drivers.");

            int driver = 0, driverRate = 0;
            string driverName = null;
            List<FmodDeviceInfo> drivers = FmodDevices.Read(_system, count);
            for (int i = 0; i < drivers.Count; i++)
            {
                if (drivers[i].Id != settings.DeviceId) continue;
                driver = i;
                break;
            }
            if (!string.IsNullOrEmpty(settings.DeviceId) && drivers[driver].Id != settings.DeviceId)
                _error?.Invoke("FMOD output device '" + settings.DeviceId + "' is not available; using '" + drivers[0].Name + "'.");
            driverName = drivers[driver].Name;
            driverRate = drivers[driver].SampleRate;
            Check(FmodNative.FMOD_System_SetDriver(_system, driver), "System_SetDriver");

            // Mix at the device rate so FMOD does not resample the output. 48 kHz is
            // only used when the driver does not report a rate.
            int rate = driverRate > 0 ? driverRate : 48000;
            Check(FmodNative.FMOD_System_SetSoftwareFormat(_system, rate, 3 /* FMOD_SPEAKERMODE_STEREO */, 0), "System_SetSoftwareFormat");
            Check(FmodNative.FMOD_System_SetDSPBufferSize(_system, (uint)FmodSettings.ClampBufferLength(settings.DspBufferLength),
                FmodSettings.ClampBufferCount(settings.DspBufferCount)), "System_SetDSPBufferSize");

            Check(FmodNative.FMOD_System_SetSoftwareChannels(_system, realVoices), "System_SetSoftwareChannels");
            Check(FmodNative.FMOD_System_Init(_system, virtualVoices, FmodNative.InitClipOutput, IntPtr.Zero), "System_Init");

            int speakerMode, rawSpeakers, actualRate;
            Check(FmodNative.FMOD_System_GetSoftwareFormat(_system, out actualRate, out speakerMode, out rawSpeakers), "System_GetSoftwareFormat");
            uint bufferLength;
            int bufferCount;
            Check(FmodNative.FMOD_System_GetDSPBufferSize(_system, out bufferLength, out bufferCount), "System_GetDSPBufferSize");
            if (actualRate <= 0) throw new InvalidOperationException("FMOD reported an invalid software sample rate.");
            SoftwareRate = actualRate;
            BufferLength = (int)bufferLength;
            BufferCount = bufferCount;
            DriverName = driverName;

            Check(FmodNative.FMOD_System_GetMasterChannelGroup(_system, out _master), "System_GetMasterChannelGroup");
        }

        // Makes the FMOD clock continue from the given Unity DSP time.
        internal void Reanchor(double unityDspTime)
        {
            _clock = new FmodClock(SoftwareRate, GroupClock(_pausable), unityDspTime);
            _lastDspTime = unityDspTime;
        }

        private IntPtr CreateGroup(string name)
        {
            IntPtr group;
            Check(FmodNative.FMOD_System_CreateChannelGroup(_system, FmodNative.Utf8(name), out group), "System_CreateChannelGroup");
            Check(FmodNative.FMOD_ChannelGroup_AddGroup(_master, group, 1, IntPtr.Zero), "ChannelGroup_AddGroup");
            return group;
        }

        // ADOFAI's MasterMixer ends in a Duck Volume effect keyed by the whole mix
        // (threshold -1.2 dB, ratio 9.5), i.e. a peak limiter. A compressor with the
        // same threshold and ratio keeps FMOD's output level comparable.
        private IntPtr CreateMasterLimiter()
        {
            IntPtr dsp;
            if (FmodNative.FMOD_System_CreateDSPByType(_system, FmodNative.DspTypeCompressor, out dsp) != FmodNative.Ok)
                return IntPtr.Zero;
            if (FmodNative.DspName(dsp) != "FMOD Compressor")
            {
                FmodNative.FMOD_DSP_Release(dsp);
                _log?.Invoke("FMOD: compressor DSP not found; master limiter disabled.");
                return IntPtr.Zero;
            }
            SetFloat(dsp, "Threshold", -1.2f);
            SetFloat(dsp, "Ratio", 9.5f);
            SetFloat(dsp, "Attack", 0.1f);
            SetFloat(dsp, "Release", 120f);
            SetFloat(dsp, "Make up gain", 0f);
            if (FmodNative.FMOD_ChannelGroup_AddDSP(_master, 0, dsp) != FmodNative.Ok)
            {
                FmodNative.FMOD_DSP_Release(dsp);
                return IntPtr.Zero;
            }
            return dsp;
        }

        private static void SetFloat(IntPtr dsp, string name, float value)
        {
            int index = FmodNative.FindDspParameter(dsp, name);
            if (index >= 0) FmodNative.FMOD_DSP_SetParameterFloat(dsp, index, value);
        }

        internal void Shutdown()
        {
            if (Active == this) Active = null;
            for (int i = 0; i < _stateList.Count; i++)
            {
                SourceState state = _stateList[i];
                StopVoices(state, true);
                ReleaseSpectrum(state);
            }
            _states.Clear();
            _stateList.Clear();
            for (int i = 0; i < _links.Count; i++)
            {
                try { if (_links[i] != null) UnityEngine.Object.Destroy(_links[i]); }
                catch (Exception) { }
            }
            _links.Clear();
            _sounds?.ReleaseAll();
            _sounds = null;
            ReleaseDsp(ref _listenerFft, _master);
            ReleaseDsp(ref _limiter, _master);
            ReleaseGroup(ref _pausable);
            ReleaseGroup(ref _unpausable);
            if (_system != IntPtr.Zero)
            {
                // ASIO drivers are COM objects and must be released in their apartment.
                if (_comThread != null) _comThread.Invoke(ReleaseSystem);
                else ReleaseSystem();
            }
            if (_comThread != null)
            {
                _comThread.Dispose();
                _comThread = null;
            }
            _master = IntPtr.Zero;
            int current, peak;
            if (FmodNative.FMOD_Memory_GetStats(out current, out peak, 0) == FmodNative.Ok)
                _log?.Invoke("FMOD shut down; FMOD memory in use " + current + " bytes (peak " + peak + ").");
        }

        private void ReleaseSystem()
        {
            int result = FmodNative.FMOD_System_Release(_system);
            if (result != FmodNative.Ok) _error?.Invoke("FMOD System_Release returned " + result);
            _system = IntPtr.Zero;
        }

        private void ReleaseDsp(ref IntPtr dsp, IntPtr group)
        {
            if (dsp == IntPtr.Zero) return;
            if (group != IntPtr.Zero) FmodNative.FMOD_ChannelGroup_RemoveDSP(group, dsp);
            int result = FmodNative.FMOD_DSP_Release(dsp);
            if (result != FmodNative.Ok) _error?.Invoke("FMOD DSP_Release returned " + result);
            dsp = IntPtr.Zero;
        }

        private void ReleaseGroup(ref IntPtr group)
        {
            if (group == IntPtr.Zero) return;
            FmodNative.FMOD_ChannelGroup_Stop(group);
            int result = FmodNative.FMOD_ChannelGroup_Release(group);
            if (result != FmodNative.Ok) _error?.Invoke("FMOD ChannelGroup_Release returned " + result);
            group = IntPtr.Zero;
        }

        // Stops routing new work to FMOD. The backend removes the patches and shuts
        // the engine down on the next frame; until then DspTime keeps advancing
        // from the last FMOD time so the game clock does not jump backwards.
        internal void Fail(Exception exception, string context)
        {
            if (Failed) return;
            // Mark the failure before anything else: reading the time below must not
            // route back into this engine through the patched dspTime getter.
            Failed = true;
            if (Active == this) Active = null;
            _failedAtTime = _lastDspTime;
            _failedTimer = Stopwatch.StartNew();
            FailureMessage = context + ": " + exception.Message;
            _error?.Invoke("FMOD failed while " + context + "; returning to Unity audio. " + exception);
        }

        internal double FailedDspTime()
        {
            return _failedAtTime + (_failedTimer == null ? 0.0 : _failedTimer.Elapsed.TotalSeconds);
        }

        private void LogError(string message)
        {
            if (_loggedErrors >= MaxLoggedErrors) return;
            _loggedErrors++;
            _error?.Invoke("FMOD: " + message + (_loggedErrors == MaxLoggedErrors ? " (further errors are not logged)" : ""));
        }

        // -------------------------------------------------------------------- clock

        internal double DspTime()
        {
            if (Thread.CurrentThread.ManagedThreadId == _mainThread)
            {
                try { SyncListener(); }
                catch (Exception ex) { Fail(ex, "applying AudioListener state"); }
            }
            ulong clock, parent;
            int result = FmodNative.FMOD_ChannelGroup_GetDSPClock(_pausable, out clock, out parent);
            if (result != FmodNative.Ok)
            {
                Fail(new FmodException("ChannelGroup_GetDSPClock", result), "reading the DSP clock");
                return FailedDspTime();
            }
            double time = _clock.ToSeconds(clock);
            _lastDspTime = time;
            return time;
        }

        // AudioListener.pause and volume are native properties that cannot be
        // patched without replacing Unity's implementation, so their state is
        // mirrored whenever the game touches audio or time, and once per frame.
        private void SyncListener()
        {
            bool paused = AudioListener.pause;
            if (paused != _listenerPaused)
            {
                _listenerPaused = paused;
                Check(FmodNative.FMOD_ChannelGroup_SetPaused(_pausable, paused ? 1 : 0), "ChannelGroup_SetPaused");
            }
            float volume = AudioListener.volume;
            if (volume != _listenerVolume)
            {
                _listenerVolume = volume;
                Check(FmodNative.FMOD_ChannelGroup_SetVolume(_pausable, volume), "ChannelGroup_SetVolume");
                Check(FmodNative.FMOD_ChannelGroup_SetVolume(_unpausable, volume), "ChannelGroup_SetVolume");
            }
        }

        private ulong GroupClock(IntPtr group)
        {
            ulong clock, parent;
            Check(FmodNative.FMOD_ChannelGroup_GetDSPClock(group, out clock, out parent), "ChannelGroup_GetDSPClock");
            return clock;
        }

        // Converts a game time to a start/end clock in the voice's own group.
        // Returns 0 when the time is not in the future.
        private ulong ClockAt(IntPtr group, double time)
        {
            ulong target = _clock.ToClock(time);
            ulong now = GroupClock(_pausable);
            if (target <= now) return 0;
            return group == _pausable ? target : GroupClock(group) + (target - now);
        }

        private ulong StartClock(IntPtr group, StartMode mode, double seconds)
        {
            if (mode == StartMode.Absolute) return ClockAt(group, seconds);
            if (mode == StartMode.Relative)
            {
                ulong frames = _clock.FramesFor(seconds);
                return frames == 0 ? 0 : GroupClock(group) + frames;
            }
            return 0;
        }

        // -------------------------------------------------------------- per frame

        internal void Update()
        {
            try
            {
                Check(FmodNative.FMOD_System_Update(_system), "System_Update");
                SyncListener();
                for (int i = _stateList.Count - 1; i >= 0; i--)
                {
                    SourceState state = _stateList[i];
                    AudioSource source = state.Source;
                    if (source == null)
                    {
                        RemoveState(state, true);
                        continue;
                    }
                    bool hasVoices = state.Primary != null || state.OneShots.Count > 0;
                    if (hasVoices && !source.isActiveAndEnabled)
                    {
                        StopVoices(state, true);
                        hasVoices = false;
                    }
                    if (state.Primary != null && !Alive(state.Primary))
                    {
                        ReleaseVoice(state.Primary, false);
                        state.Primary = null;
                    }
                    for (int v = state.OneShots.Count - 1; v >= 0; v--)
                    {
                        if (Alive(state.OneShots[v])) continue;
                        ReleaseVoice(state.OneShots[v], false);
                        state.OneShots.RemoveAt(v);
                    }
                    if (state.Primary == null && state.OneShots.Count == 0 && state.PendingSamples < 0)
                        RemoveState(state, false);
                }
                _sounds.Update();
                SampleMixerLoad();
            }
            catch (Exception ex)
            {
                Fail(ex, "updating FMOD");
            }
        }

        private void SampleMixerLoad()
        {
            if (_cpuTimer.ElapsedMilliseconds < 500) return;
            _cpuTimer.Reset();
            _cpuTimer.Start();
            if (FmodNative.FMOD_System_GetCPUUsage(_system, _cpuUsage) != FmodNative.Ok) return;
            MixerCpu = _cpuUsage[0];
            _overloadedSamples = MixerCpu >= OverloadedCpu ? _overloadedSamples + 1 : 0;
            if (_overloadedSamples < 3 || _overloadLogged) return;
            _overloadLogged = true;
            int channels, real;
            FmodNative.FMOD_System_GetChannelsPlaying(_system, out channels, out real);
            _error?.Invoke(string.Format("FMOD mixer is using {0:F0}% of real time with {1} channels at a {2}-frame DSP buffer; " +
                "audio will crackle. Increase the FMOD DSP buffer size.", MixerCpu, channels, BufferLength));
        }

        internal const float OverloadedCpu = 60f;

        // ---------------------------------------------------------- source state

        private SourceState Find(AudioSource source)
        {
            SourceState state;
            if (ReferenceEquals(source, null) || !_states.TryGetValue(source.GetInstanceID(), out state)) return null;
            return ReferenceEquals(state.Source, source) ? state : null;
        }

        private SourceState GetOrCreate(AudioSource source)
        {
            SourceState state = Find(source);
            if (state != null) return state;
            int id = source.GetInstanceID();
            SourceState stale;
            if (_states.TryGetValue(id, out stale)) RemoveState(stale, true);
            state = _statePool.Count > 0 ? _statePool.Pop() : new SourceState();
            state.Source = source;
            state.Id = id;
            state.Volume = source.volume;
            state.MixerGain = _mixer.Gain(source.outputAudioMixerGroup, out state.MixerMask);
            state.Index = _stateList.Count;
            _stateList.Add(state);
            _states[id] = state;
            AttachLink(state);
            return state;
        }

        private void AttachLink(SourceState state)
        {
            GameObject gameObject = state.Source.gameObject;
            FmodSourceLink link = gameObject.GetComponent<FmodSourceLink>();
            if (link == null)
            {
                link = gameObject.AddComponent<FmodSourceLink>();
                link.hideFlags = HideFlags.HideInInspector | HideFlags.DontSave;
                _links.Add(link);
            }
            state.Link = link;
        }

        private void RemoveState(SourceState state, bool stop)
        {
            StopVoices(state, stop);
            ReleaseSpectrum(state);
            _states.Remove(state.Id);
            int last = _stateList.Count - 1;
            SourceState moved = _stateList[last];
            _stateList[state.Index] = moved;
            moved.Index = state.Index;
            _stateList.RemoveAt(last);
            state.Source = null;
            state.Link = null;
            state.Paused = false;
            state.PendingSamples = -1;
            _statePool.Push(state);
        }

        // Unity stops every AudioSource on a GameObject that is disabled or destroyed.
        internal void GameObjectDisabled(FmodSourceLink link)
        {
            for (int i = _stateList.Count - 1; i >= 0; i--)
            {
                SourceState state = _stateList[i];
                if (ReferenceEquals(state.Link, link)) RemoveState(state, true);
            }
        }

        internal void LinkDestroyed(FmodSourceLink link)
        {
            _links.Remove(link);
        }

        // ----------------------------------------------------------------- voices

        private static bool Alive(Voice voice)
        {
            int playing;
            return FmodNative.FMOD_Channel_IsPlaying(voice.Channel, out playing) == FmodNative.Ok && playing != 0;
        }

        private Voice StartVoice(SourceState state, FmodSound sound, IntPtr group, float scale, bool oneShot, ulong startClock)
        {
            IntPtr channel;
            Check(FmodNative.FMOD_System_PlaySound(_system, sound.Playable, group, 1, out channel), "System_PlaySound");
            Voice voice = _voicePool.Count > 0 ? _voicePool.Pop() : new Voice();
            voice.Channel = channel;
            voice.Group = group;
            voice.Sound = sound;
            voice.Scale = scale;
            voice.OneShot = oneShot;
            voice.StartClock = startClock;
            voice.EndClock = 0;
            _sounds.VoiceStarted(sound);
            try
            {
                AudioSource source = state.Source;
                Check(FmodNative.FMOD_Channel_SetVolume(channel, VolumeOf(state, voice, source.mute)), "Channel_SetVolume");
                Check(FmodNative.FMOD_Channel_SetPitch(channel, Math.Max(0f, source.pitch)), "Channel_SetPitch");
                Check(FmodNative.FMOD_Channel_SetPan(channel, source.panStereo), "Channel_SetPan");
                Check(FmodNative.FMOD_Channel_SetPriority(channel, source.priority), "Channel_SetPriority");
                if (!oneShot && source.loop)
                    Check(FmodNative.FMOD_Channel_SetMode(channel, FmodNative.ModeLoopNormal), "Channel_SetMode");
                if (startClock != 0)
                    Check(FmodNative.FMOD_Channel_SetDelay(channel, startClock, 0, 1), "Channel_SetDelay");
                Check(FmodNative.FMOD_Channel_SetPaused(channel, state.Paused ? 1 : 0), "Channel_SetPaused");
                return voice;
            }
            catch
            {
                ReleaseVoice(voice, true);
                throw;
            }
        }

        private void ReleaseVoice(Voice voice, bool stop)
        {
            if (voice == null) return;
            if (stop) FmodNative.FMOD_Channel_Stop(voice.Channel);
            if (voice.Sound != null) _sounds?.VoiceEnded(voice.Sound);
            voice.Sound = null;
            voice.Channel = IntPtr.Zero;
            _voicePool.Push(voice);
        }

        private void StopVoices(SourceState state, bool stop)
        {
            ReleaseVoice(state.Primary, stop);
            state.Primary = null;
            for (int i = 0; i < state.OneShots.Count; i++) ReleaseVoice(state.OneShots[i], stop);
            state.OneShots.Clear();
        }

        private float VolumeOf(SourceState state, Voice voice, bool mute)
        {
            return mute ? 0f : state.Volume * voice.Scale * state.MixerGain;
        }

        private void ApplyVolume(SourceState state)
        {
            bool mute = state.Source.mute;
            if (state.Primary != null)
                FmodNative.FMOD_Channel_SetVolume(state.Primary.Channel, VolumeOf(state, state.Primary, mute));
            for (int i = 0; i < state.OneShots.Count; i++)
                FmodNative.FMOD_Channel_SetVolume(state.OneShots[i].Channel, VolumeOf(state, state.OneShots[i], mute));
        }

        private bool Eligible(AudioSource source)
        {
            // Unity refuses to play disabled sources and positions 3D sources in the
            // scene; both stay on Unity's own path.
            return source.isActiveAndEnabled && source.spatialBlend <= 0f;
        }

        private FmodSound SoundFor(AudioClip clip)
        {
            FmodSound sound = _sounds.Get(clip, SoundWaitMilliseconds);
            if (sound == null) FallbackPlays++;
            return sound;
        }

        private static bool NativeIsPlaying(AudioSource source)
        {
            Passthrough = true;
            try { return source.isPlaying; }
            finally { Passthrough = false; }
        }

        private static void NativeStop(AudioSource source)
        {
            Passthrough = true;
            try { source.Stop(); }
            finally { Passthrough = false; }
        }

        // ------------------------------------------------------------ playback API

        internal bool PlayNow(AudioSource source, ulong delaySamples)
        {
            // Play(ulong) is documented as a delay in samples at 44.1 kHz.
            return delaySamples == 0
                ? Start(source, StartMode.Now, 0)
                : Start(source, StartMode.Relative, delaySamples / 44100.0);
        }

        // AudioSource.Play(double): positive values are PlayScheduled times, negative
        // values are PlayDelayed delays and zero plays immediately.
        internal bool PlayAt(AudioSource source, ref double delay)
        {
            bool handled = delay > 0 ? Start(source, StartMode.Absolute, delay)
                : delay < 0 ? Start(source, StartMode.Relative, -delay)
                : Start(source, StartMode.Now, 0);
            if (!handled && delay > 0 && !Failed)
            {
                // Unity plays this clip, but the requested time is on the FMOD clock.
                // Hand Unity the remaining delay, which does not depend on the clock.
                double remaining = delay - DspTime();
                delay = remaining > 0 ? -remaining : 0;
            }
            return handled;
        }

        private bool Start(AudioSource source, StartMode mode, double seconds)
        {
            if (StartVoiceFor(source, mode, seconds)) return true;
            // Unity plays this source; a position stored for FMOD no longer applies.
            SourceState state = Find(source);
            if (state != null && state.Primary == null && state.OneShots.Count == 0) RemoveState(state, false);
            else if (state != null) state.PendingSamples = -1;
            return false;
        }

        private bool StartVoiceFor(AudioSource source, StartMode mode, double seconds)
        {
            try
            {
                if (!Eligible(source)) return false;
                AudioClip clip = source.clip;
                if (clip == null) return false;
                FmodSound sound = SoundFor(clip);
                if (sound == null) return false;
                SyncListener();
                if (NativeIsPlaying(source)) NativeStop(source);
                SourceState state = GetOrCreate(source);
                int startSamples = state.PendingSamples;
                state.PendingSamples = -1;
                ReleaseVoice(state.Primary, true);
                state.Primary = null;
                // Play() resumes a paused source's one-shots and clears its pause.
                if (state.Paused)
                {
                    state.Paused = false;
                    for (int i = 0; i < state.OneShots.Count; i++)
                        FmodNative.FMOD_Channel_SetPaused(state.OneShots[i].Channel, 0);
                }
                state.Volume = source.volume;
                IntPtr group = source.ignoreListenerPause ? _unpausable : _pausable;
                Voice voice = StartVoice(state, sound, group, 1f, false, StartClock(group, mode, seconds));
                state.Primary = voice;
                if (startSamples > 0) SeekVoice(voice, startSamples);
                return true;
            }
            catch (FmodException ex)
            {
                LogError("could not play '" + SafeName(source) + "': " + ex.Message);
                return false;
            }
            catch (Exception ex)
            {
                Fail(ex, "starting playback");
                return false;
            }
        }

        internal bool PlayOneShot(AudioSource source, AudioClip clip, float volumeScale)
        {
            try
            {
                if (clip == null || !Eligible(source)) return false;
                FmodSound sound = SoundFor(clip);
                if (sound == null) return false;
                SyncListener();
                SourceState state = GetOrCreate(source);
                IntPtr group = source.ignoreListenerPause ? _unpausable : _pausable;
                state.OneShots.Add(StartVoice(state, sound, group, volumeScale, true, 0));
                return true;
            }
            catch (FmodException ex)
            {
                LogError("could not play a one-shot on '" + SafeName(source) + "': " + ex.Message);
                return false;
            }
            catch (Exception ex)
            {
                Fail(ex, "starting a one-shot");
                return false;
            }
        }

        private static string SafeName(AudioSource source)
        {
            try { return source.name; }
            catch (Exception) { return "?"; }
        }

        // Unity's own Stop always runs afterwards, for sources Unity was playing.
        internal void Stop(AudioSource source, bool stopOneShots)
        {
            SourceState state = Find(source);
            if (state == null) return;
            ReleaseVoice(state.Primary, true);
            state.Primary = null;
            if (stopOneShots)
            {
                for (int i = 0; i < state.OneShots.Count; i++) ReleaseVoice(state.OneShots[i], true);
                state.OneShots.Clear();
            }
            state.Paused = false;
            state.PendingSamples = -1;
            if (state.OneShots.Count == 0) RemoveState(state, false);
        }

        internal void SetPaused(AudioSource source, bool paused)
        {
            SourceState state = Find(source);
            if (state == null) return;
            state.Paused = paused;
            int value = paused ? 1 : 0;
            if (state.Primary != null) FmodNative.FMOD_Channel_SetPaused(state.Primary.Channel, value);
            for (int i = 0; i < state.OneShots.Count; i++)
                FmodNative.FMOD_Channel_SetPaused(state.OneShots[i].Channel, value);
        }

        internal bool IsPlaying(AudioSource source, out bool playing)
        {
            playing = false;
            SourceState state = Find(source);
            if (state == null || (state.Primary == null && state.OneShots.Count == 0)) return false;
            if (state.Paused) return true;
            if (state.Primary != null && Alive(state.Primary))
            {
                playing = true;
                return true;
            }
            for (int i = 0; i < state.OneShots.Count; i++)
            {
                if (!Alive(state.OneShots[i])) continue;
                playing = true;
                break;
            }
            return true;
        }

        internal bool TryGetSamples(AudioSource source, out int samples)
        {
            samples = 0;
            SourceState state = Find(source);
            if (state == null) return false;
            if (state.Primary != null)
            {
                uint position;
                if (FmodNative.FMOD_Channel_GetPosition(state.Primary.Channel, out position, FmodNative.TimeUnitPcm) == FmodNative.Ok)
                    samples = position > int.MaxValue ? int.MaxValue : (int)position;
                return true;
            }
            if (state.PendingSamples >= 0)
            {
                samples = state.PendingSamples;
                return true;
            }
            return false;
        }

        internal bool TryGetTime(AudioSource source, out float time)
        {
            time = 0f;
            int samples;
            if (!TryGetSamples(source, out samples)) return false;
            SourceState state = Find(source);
            double frequency = state.Primary != null ? state.Primary.Sound.Frequency : ClipFrequency(source);
            time = frequency > 0 ? (float)(samples / frequency) : 0f;
            return true;
        }

        private static int ClipFrequency(AudioSource source)
        {
            AudioClip clip = source.clip;
            return clip == null ? 0 : clip.frequency;
        }

        // Returns true when the position was applied to an FMOD voice and Unity's
        // setter should be skipped.
        internal bool SetSamples(AudioSource source, int samples)
        {
            SourceState state = Find(source);
            if (state != null && state.Primary != null)
            {
                SeekVoice(state.Primary, samples);
                return true;
            }
            // Not playing yet: remember the position for the next Play, as Unity does.
            if (samples < 0) samples = 0;
            try { GetOrCreate(source).PendingSamples = samples; }
            catch (Exception ex) { Fail(ex, "storing a playback position"); }
            return false;
        }

        internal bool SetTime(AudioSource source, float seconds)
        {
            SourceState state = Find(source);
            double frequency = state != null && state.Primary != null ? state.Primary.Sound.Frequency : ClipFrequency(source);
            if (frequency <= 0) return false;
            return SetSamples(source, (int)Math.Round(seconds * frequency, MidpointRounding.AwayFromZero));
        }

        private void SeekVoice(Voice voice, int samples)
        {
            uint length = voice.Sound.Length;
            uint position = samples <= 0 ? 0u : (uint)samples;
            if (length > 0 && position >= length) position = length - 1;
            int result = FmodNative.FMOD_Channel_SetPosition(voice.Channel, position, FmodNative.TimeUnitPcm);
            if (result != FmodNative.Ok && result != FmodNative.ErrInvalidHandle)
                LogError("Channel_SetPosition returned " + result);
        }

        internal bool SetScheduledStart(AudioSource source, double time)
        {
            SourceState state = Find(source);
            if (state == null || state.Primary == null) return false;
            try
            {
                Voice voice = state.Primary;
                voice.StartClock = ClockAt(voice.Group, time);
                int result = FmodNative.FMOD_Channel_SetDelay(voice.Channel, voice.StartClock, voice.EndClock, 1);
                if (result != FmodNative.Ok && result != FmodNative.ErrInvalidHandle) LogError("Channel_SetDelay returned " + result);
            }
            catch (Exception ex) { Fail(ex, "rescheduling a sound"); }
            return true;
        }

        internal bool SetScheduledEnd(AudioSource source, double time)
        {
            SourceState state = Find(source);
            if (state == null || state.Primary == null) return false;
            try
            {
                Voice voice = state.Primary;
                // An end time that has already passed stops the voice at the next mix.
                ulong end = ClockAt(voice.Group, time);
                voice.EndClock = end != 0 ? end : GroupClock(voice.Group) + 1;
                int result = FmodNative.FMOD_Channel_SetDelay(voice.Channel, voice.StartClock, voice.EndClock, 1);
                if (result != FmodNative.Ok && result != FmodNative.ErrInvalidHandle) LogError("Channel_SetDelay returned " + result);
            }
            catch (Exception ex) { Fail(ex, "scheduling a sound's end"); }
            return true;
        }

        // ------------------------------------------------------- property changes

        internal void ClipChanging(AudioSource source, UnityEngine.Object value)
        {
            SourceState state = Find(source);
            // Assigning the clip that is already set does not interrupt Unity playback.
            if (state == null || value == (UnityEngine.Object)source.clip) return;
            ReleaseVoice(state.Primary, true);
            state.Primary = null;
            state.PendingSamples = -1;
            state.Paused = false;
        }

        internal void Preload(AudioClip clip)
        {
            try { _sounds.Preload(clip); }
            catch (Exception ex) { LogError("could not prepare clip: " + ex.Message); }
        }

        internal void ClipUnloaded(AudioClip clip)
        {
            _sounds.Unload(clip.GetInstanceID());
        }

        internal void VolumeChanged(AudioSource source, float volume)
        {
            SourceState state = Find(source);
            if (state == null) return;
            state.Volume = volume;
            ApplyVolume(state);
        }

        internal void MuteChanged(AudioSource source)
        {
            SourceState state = Find(source);
            if (state != null) ApplyVolume(state);
        }

        internal void MixerGroupChanged(AudioSource source)
        {
            SourceState state = Find(source);
            if (state == null) return;
            state.MixerGain = _mixer.Gain(source.outputAudioMixerGroup, out state.MixerMask);
            ApplyVolume(state);
        }

        internal void MixerParameterChanged(AudioMixer mixer, string name)
        {
            int changed = _mixer.ParameterChanged(mixer, name);
            if (changed == 0) return;
            for (int i = 0; i < _stateList.Count; i++)
            {
                SourceState state = _stateList[i];
                if ((state.MixerMask & changed) == 0) continue;
                state.MixerGain = _mixer.GainFor(state.MixerMask);
                ApplyVolume(state);
            }
        }

        internal void PitchChanged(AudioSource source, float pitch)
        {
            SourceState state = Find(source);
            if (state == null) return;
            float value = Math.Max(0f, pitch);
            if (state.Primary != null) FmodNative.FMOD_Channel_SetPitch(state.Primary.Channel, value);
            for (int i = 0; i < state.OneShots.Count; i++) FmodNative.FMOD_Channel_SetPitch(state.OneShots[i].Channel, value);
        }

        internal void LoopChanged(AudioSource source, bool loop)
        {
            SourceState state = Find(source);
            if (state == null || state.Primary == null) return;
            FmodNative.FMOD_Channel_SetMode(state.Primary.Channel, loop ? FmodNative.ModeLoopNormal : FmodNative.ModeLoopOff);
        }

        internal void PriorityChanged(AudioSource source, int priority)
        {
            SourceState state = Find(source);
            if (state == null) return;
            if (state.Primary != null) FmodNative.FMOD_Channel_SetPriority(state.Primary.Channel, priority);
            for (int i = 0; i < state.OneShots.Count; i++) FmodNative.FMOD_Channel_SetPriority(state.OneShots[i].Channel, priority);
        }

        internal void PanChanged(AudioSource source, float pan)
        {
            SourceState state = Find(source);
            if (state == null) return;
            if (state.Primary != null) FmodNative.FMOD_Channel_SetPan(state.Primary.Channel, pan);
            for (int i = 0; i < state.OneShots.Count; i++) FmodNative.FMOD_Channel_SetPan(state.OneShots[i].Channel, pan);
        }

        internal void IgnoreListenerPauseChanged(AudioSource source, bool ignore)
        {
            SourceState state = Find(source);
            if (state == null) return;
            try
            {
                IntPtr group = ignore ? _unpausable : _pausable;
                if (state.Primary != null) MoveVoice(state.Primary, group);
                for (int i = 0; i < state.OneShots.Count; i++) MoveVoice(state.OneShots[i], group);
            }
            catch (Exception ex) { Fail(ex, "moving a voice between listener groups"); }
        }

        private void MoveVoice(Voice voice, IntPtr group)
        {
            if (voice.Group == group) return;
            // Keep pending start/end times at the same game time in the new clock.
            ulong fromNow = GroupClock(voice.Group), toNow = GroupClock(group);
            if (voice.StartClock > fromNow) voice.StartClock = toNow + (voice.StartClock - fromNow);
            else voice.StartClock = 0;
            if (voice.EndClock > fromNow) voice.EndClock = toNow + (voice.EndClock - fromNow);
            voice.Group = group;
            FmodNative.FMOD_Channel_SetChannelGroup(voice.Channel, group);
            if (voice.StartClock != 0 || voice.EndClock != 0)
                FmodNative.FMOD_Channel_SetDelay(voice.Channel, voice.StartClock, voice.EndClock, 1);
        }

        // ---------------------------------------------------------------- spectrum

        internal bool GetSpectrum(AudioSource source, float[] samples, int channel)
        {
            SourceState state = Find(source);
            if (state == null || state.Primary == null || samples == null) return false;
            try
            {
                if (state.Spectrum == IntPtr.Zero) state.Spectrum = CreateFft(samples.Length);
                if (state.SpectrumChannel != state.Primary.Channel)
                {
                    // The tap follows the source's current voice. The previous channel
                    // may already have ended, so detach the DSP from the graph first.
                    if (state.SpectrumChannel != IntPtr.Zero)
                        FmodNative.FMOD_Channel_RemoveDSP(state.SpectrumChannel, state.Spectrum);
                    FmodNative.FMOD_DSP_DisconnectAll(state.Spectrum, 1, 1);
                    state.SpectrumChannel = IntPtr.Zero;
                    Check(FmodNative.FMOD_Channel_AddDSP(state.Primary.Channel, 0, state.Spectrum), "Channel_AddDSP(FFT)");
                    state.SpectrumChannel = state.Primary.Channel;
                }
                CopySpectrum(state.Spectrum, samples, channel);
                return true;
            }
            catch (Exception ex)
            {
                LogError("spectrum analysis failed: " + ex.Message);
                return false;
            }
        }

        internal bool GetListenerSpectrum(float[] samples, int channel)
        {
            if (samples == null) return false;
            try
            {
                if (_listenerFft == IntPtr.Zero)
                {
                    IntPtr fft = CreateFft(samples.Length);
                    int result = FmodNative.FMOD_ChannelGroup_AddDSP(_master, 0, fft);
                    if (result != FmodNative.Ok)
                    {
                        FmodNative.FMOD_DSP_Release(fft);
                        throw new FmodException("ChannelGroup_AddDSP(FFT)", result);
                    }
                    _listenerFft = fft;
                }
                CopySpectrum(_listenerFft, samples, channel);
                return true;
            }
            catch (Exception ex)
            {
                LogError("listener spectrum analysis failed: " + ex.Message);
                return false;
            }
        }

        private IntPtr CreateFft(int bins)
        {
            IntPtr fft;
            Check(FmodNative.FMOD_System_CreateDSPByType(_system, FmodNative.DspTypeFft, out fft), "System_CreateDSPByType(FFT)");
            if (_spectrumParameter < 0)
            {
                _spectrumParameter = FmodNative.FindDspParameter(fft, "Spectrum Data");
                _fftSizeParameter = FmodNative.FindDspParameter(fft, "Size");
            }
            if (_spectrumParameter < 0 || FmodNative.DspName(fft) != "FMOD FFT")
            {
                FmodNative.FMOD_DSP_Release(fft);
                throw new InvalidOperationException("the FMOD FFT DSP does not match the 2.03 layout");
            }
            // Unity returns `bins` values from an FFT of twice that many samples.
            int size = 128;
            while (size < bins * 2 && size < 16384) size *= 2;
            if (_fftSizeParameter >= 0) FmodNative.FMOD_DSP_SetParameterInt(fft, _fftSizeParameter, size);
            return fft;
        }

        private void CopySpectrum(IntPtr fft, float[] samples, int channel)
        {
            // FMOD_DSP_PARAMETER_FFT: int length; int numchannels; float *spectrum[32].
            IntPtr data;
            uint length;
            if (FmodNative.FMOD_DSP_GetParameterData(fft, _spectrumParameter, out data, out length, IntPtr.Zero, 0) != FmodNative.Ok ||
                data == IntPtr.Zero)
            {
                Array.Clear(samples, 0, samples.Length);
                return;
            }
            int bins = System.Runtime.InteropServices.Marshal.ReadInt32(data, 0);
            int channels = System.Runtime.InteropServices.Marshal.ReadInt32(data, 4);
            if (channel < 0 || channel >= channels || channels > 32 || bins <= 0)
            {
                Array.Clear(samples, 0, samples.Length);
                return;
            }
            IntPtr spectrum = System.Runtime.InteropServices.Marshal.ReadIntPtr(data, 8 + channel * IntPtr.Size);
            int count = Math.Min(samples.Length, bins / 2);
            if (spectrum == IntPtr.Zero) count = 0;
            else System.Runtime.InteropServices.Marshal.Copy(spectrum, samples, 0, count);
            if (count < samples.Length) Array.Clear(samples, count, samples.Length - count);
        }

        private void ReleaseSpectrum(SourceState state)
        {
            if (state.Spectrum == IntPtr.Zero) return;
            if (state.SpectrumChannel != IntPtr.Zero) FmodNative.FMOD_Channel_RemoveDSP(state.SpectrumChannel, state.Spectrum);
            FmodNative.FMOD_DSP_DisconnectAll(state.Spectrum, 1, 1);
            int result = FmodNative.FMOD_DSP_Release(state.Spectrum);
            if (result != FmodNative.Ok) LogError("DSP_Release(FFT) returned " + result);
            state.Spectrum = IntPtr.Zero;
            state.SpectrumChannel = IntPtr.Zero;
        }

        // ------------------------------------------------------------ bookkeeping

        internal void RegisterExternalFile(string path)
        {
            _sounds?.RegisterExternalFile(path);
        }

        internal bool IsHandling(AudioSource source)
        {
            SourceState state = Find(source);
            return state != null && state.Primary != null;
        }

        internal FmodStatistics Statistics()
        {
            int channels = 0, real = 0;
            if (_system != IntPtr.Zero) FmodNative.FMOD_System_GetChannelsPlaying(_system, out channels, out real);
            return new FmodStatistics
            {
                Channels = channels,
                RealChannels = real,
                Sources = _stateList.Count,
                Sounds = _sounds == null ? 0 : _sounds.Count,
                SoundBytes = _sounds == null ? 0 : _sounds.Bytes,
                FallbackPlays = FallbackPlays,
                MixerCpu = MixerCpu,
            };
        }
    }

    public struct FmodStatistics
    {
        public int Channels;
        public int RealChannels;
        public int Sources;
        public int Sounds;
        public long SoundBytes;
        public int FallbackPlays;
        public float MixerCpu;
    }
}
