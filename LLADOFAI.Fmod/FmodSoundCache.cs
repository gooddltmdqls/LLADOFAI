using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;

namespace LLADOFAI.Fmod
{
    internal sealed class FmodSound
    {
        internal enum LoadState { Loading, Ready, Failed }

        internal int ClipId;
        internal string Name;
        internal LoadState State;
        internal IntPtr Handle;
        // What channels play: Handle itself, or its first subsound for FSB5 banks.
        internal IntPtr Playable;
        // Native PCM that FMOD plays in place (FMOD_OPENMEMORY_POINT); freed after
        // the FMOD Sound is released.
        internal IntPtr Pcm;
        internal long Bytes;
        internal float Frequency;
        internal uint Length;
        internal bool FromFile;
        // Channels currently playing this sound. A released entry is freed once
        // this reaches zero so a playing channel never loses its sample data.
        internal int Voices;
        internal bool Released;
    }

    // Unity 6 adds Span<float> overloads of GetData/SetData whose Span type cannot be
    // resolved against the .NET Framework reference assemblies, so the float[]
    // overloads are bound once through delegates.
    internal static class ClipData
    {
        internal static readonly Func<AudioClip, float[], int, bool> Get = Bind("GetData");
        internal static readonly Func<AudioClip, float[], int, bool> Set = Bind("SetData");

        private static Func<AudioClip, float[], int, bool> Bind(string name)
        {
            var method = typeof(AudioClip).GetMethod(name, new[] { typeof(float[]), typeof(int) });
            return (Func<AudioClip, float[], int, bool>)Delegate.CreateDelegate(typeof(Func<AudioClip, float[], int, bool>), method);
        }
    }

    // AudioClip -> FMOD Sound cache. Entries are keyed by instance ID and hold no
    // reference to the AudioClip, so Resources.UnloadUnusedAssets can still destroy
    // clips; destroyed clips are detected by the periodic sweep.
    internal sealed class FmodSoundCache
    {
        private const int ChunkSamples = 1 << 16;
        private const uint PcmMode = FmodNative.Mode2D | FmodNative.ModeCreateSample |
            FmodNative.ModeOpenRaw | 0x10000000u /* FMOD_OPENMEMORY_POINT */ | FmodNative.ModeLoopOff;
        private const uint FileMode = FmodNative.Mode2D | FmodNative.ModeCreateSample |
            FmodNative.ModeNonBlocking | FmodNative.ModeAccurateTime | FmodNative.ModeIgnoreTags |
            FmodNative.ModeLoopOff;

        private readonly IntPtr _system;
        private readonly Action<string> _log;
        private readonly FmodAssetAudio _assets;
        private readonly Dictionary<int, FmodSound> _entries = new Dictionary<int, FmodSound>();
        private readonly List<FmodSound> _list = new List<FmodSound>();
        private readonly List<FmodSound> _released = new List<FmodSound>();
        // Songs loaded by AudioManager.FindOrLoadAudioClipExternal, keyed by the
        // clip name the game assigns ("<file name>*external").
        private readonly Dictionary<string, string> _externalPaths = new Dictionary<string, string>();
        private float[] _chunk = new float[ChunkSamples];
        private int _sweepIndex;
        private readonly Stopwatch _sweepTimer = Stopwatch.StartNew();

        internal FmodSoundCache(IntPtr system, FmodAssetAudio assets, Action<string> log)
        {
            _system = system;
            _assets = assets;
            _log = log;
        }

        internal int Count => _list.Count;

        internal long Bytes
        {
            get
            {
                long total = 0;
                for (int i = 0; i < _list.Count; i++) total += _list[i].Bytes;
                return total;
            }
        }

        internal void RegisterExternalFile(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            _externalPaths[Path.GetFileName(path) + "*external"] = path;
        }

        // Returns a ready sound, or null when Unity has to play the clip itself.
        internal FmodSound Get(AudioClip clip, int waitMilliseconds)
        {
            int id = clip.GetInstanceID();
            FmodSound sound;
            if (!_entries.TryGetValue(id, out sound)) sound = Create(clip, id);
            if (sound.State == FmodSound.LoadState.Loading) Poll(sound, waitMilliseconds);
            return sound.State == FmodSound.LoadState.Ready ? sound : null;
        }

        internal void Preload(AudioClip clip)
        {
            if (!_entries.ContainsKey(clip.GetInstanceID())) Create(clip, clip.GetInstanceID());
        }

        internal void Unload(int clipId)
        {
            FmodSound sound;
            if (!_entries.TryGetValue(clipId, out sound)) return;
            Forget(sound);
        }

        internal void VoiceStarted(FmodSound sound) { sound.Voices++; }

        internal void VoiceEnded(FmodSound sound)
        {
            sound.Voices--;
            if (sound.Released && sound.Voices <= 0) Free(sound);
        }

        // Called once per frame. Every half second, checks a few entries for
        // destroyed clips and finished loads.
        internal void Update()
        {
            if (_sweepTimer.ElapsedMilliseconds < 500) return;
            _sweepTimer.Reset();
            _sweepTimer.Start();
            int checks = Math.Min(16, _list.Count);
            for (int i = 0; i < checks; i++)
            {
                if (_sweepIndex >= _list.Count) _sweepIndex = 0;
                FmodSound sound = _list[_sweepIndex];
                if (sound.State == FmodSound.LoadState.Loading) Poll(sound, 0);
#pragma warning disable 618 // Obsolete in Unity 6.3 in favour of EntityId; still supported.
                bool destroyed = Resources.InstanceIDToObject(sound.ClipId) == null;
#pragma warning restore 618
                if (destroyed)
                {
                    Forget(sound);
                    continue;
                }
                _sweepIndex++;
            }
        }

        internal void ReleaseAll()
        {
            for (int i = 0; i < _list.Count; i++) Free(_list[i]);
            for (int i = 0; i < _released.Count; i++) Free(_released[i]);
            _list.Clear();
            _released.Clear();
            _entries.Clear();
        }

        private void Forget(FmodSound sound)
        {
            _entries.Remove(sound.ClipId);
            _list.Remove(sound);
            sound.Released = true;
            if (sound.Voices <= 0) Free(sound);
            else _released.Add(sound);
        }

        private void Free(FmodSound sound)
        {
            if (sound.Handle != IntPtr.Zero)
            {
                int result = FmodNative.FMOD_Sound_Release(sound.Handle);
                if (result != FmodNative.Ok) _log?.Invoke("FMOD Sound_Release returned " + result + " for " + sound.Name);
                sound.Handle = IntPtr.Zero;
            }
            sound.Playable = IntPtr.Zero;
            if (sound.Pcm != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(sound.Pcm);
                sound.Pcm = IntPtr.Zero;
            }
            sound.Bytes = 0;
            _released.Remove(sound);
        }

        private FmodSound Create(AudioClip clip, int id)
        {
            var sound = new FmodSound { ClipId = id, Name = clip.name, State = FmodSound.LoadState.Failed };
            _entries[id] = sound;
            _list.Add(sound);
            string reason = null;
            try
            {
                // Decompressed clips: copy Unity's PCM. Compressed built-in clips: the
                // FSB5 bank Unity itself decodes. Streamed custom songs: the song file.
                AudioClipLoadType loadType = clip.loadType;
                // Custom-level songs may be streamed even when their load type does not
                // say so; open their file directly instead of trying GetData first.
                string path;
                bool external = _externalPaths.TryGetValue(sound.Name, out path) && File.Exists(path);
                if (!external && loadType == AudioClipLoadType.DecompressOnLoad && LoadPcm(clip, sound, out reason)) return sound;
                FmodAssetAudio.Entry bank;
                string bankReason;
                if (_assets != null && !sound.Name.EndsWith("*external", StringComparison.Ordinal) &&
                    _assets.TryFind(sound.Name, clip.channels, clip.frequency, clip.samples, out bank, out bankReason))
                {
                    OpenBank(bank, sound);
                    return sound;
                }
                if (external)
                {
                    OpenFile(path, sound);
                    return sound;
                }
                if (reason == null)
                    reason = loadType == AudioClipLoadType.Streaming ? "streamed clip without a known source file"
                        : loadType + " clip not found in the game's asset files";
            }
            catch (Exception ex)
            {
                reason = ex.Message;
                Free(sound);
            }
            sound.State = FmodSound.LoadState.Failed;
            _log?.Invoke("FMOD: clip '" + sound.Name + "' stays on Unity audio (" + reason + ").");
            return sound;
        }

        private bool LoadPcm(AudioClip clip, FmodSound sound, out string reason)
        {
            reason = null;
            int channels = clip.channels, frames = clip.samples, frequency = clip.frequency;
            if (channels <= 0 || frames <= 0 || frequency <= 0)
            {
                reason = "clip has no PCM data";
                return false;
            }
            if (clip.loadState != AudioDataLoadState.Loaded)
            {
                if (clip.loadState == AudioDataLoadState.Unloaded) clip.LoadAudioData();
                if (clip.loadState != AudioDataLoadState.Loaded)
                {
                    reason = "audio data is not loaded (" + clip.loadState + ")";
                    return false;
                }
            }
            long samples = (long)frames * channels;
            if (samples * sizeof(float) > int.MaxValue)
            {
                reason = "clip is too large";
                return false;
            }

            int framesPerChunk = ChunkSamples / channels;
            if (_chunk.Length != framesPerChunk * channels) _chunk = new float[framesPerChunk * channels];
            IntPtr pcm = Marshal.AllocHGlobal((IntPtr)(samples * sizeof(float)));
            try
            {
                for (int offset = 0; offset < frames; offset += framesPerChunk)
                {
                    int count = Math.Min(framesPerChunk, frames - offset);
                    // GetData wraps around at the end of the clip, so the last read
                    // must be exactly as long as the remaining data.
                    float[] chunk = count == framesPerChunk ? _chunk : new float[count * channels];
                    if (!ClipData.Get(clip, chunk, offset))
                    {
                        reason = "AudioClip.GetData failed (" + clip.loadType + ")";
                        return false;
                    }
                    Marshal.Copy(chunk, 0, IntPtr.Add(pcm, offset * channels * sizeof(float)), count * channels);
                }

                var info = new FmodNative.CreateSoundExInfo
                {
                    cbsize = Marshal.SizeOf(typeof(FmodNative.CreateSoundExInfo)),
                    length = (uint)(samples * sizeof(float)),
                    numchannels = channels,
                    defaultfrequency = frequency,
                    format = FmodNative.FormatPcmFloat,
                };
                int result = FmodNative.FMOD_System_CreateSound(_system, pcm, PcmMode, ref info, out sound.Handle);
                if (result != FmodNative.Ok)
                {
                    reason = "System_CreateSound returned " + result;
                    return false;
                }
                sound.Pcm = pcm;
                sound.Playable = sound.Handle;
                pcm = IntPtr.Zero;
                sound.Bytes = samples * sizeof(float);
                sound.Frequency = frequency;
                sound.Length = (uint)frames;
                sound.State = FmodSound.LoadState.Ready;
                return true;
            }
            finally
            {
                if (pcm != IntPtr.Zero) Marshal.FreeHGlobal(pcm);
            }
        }

        // Streamed songs cannot be read through GetData. FMOD decodes the same file
        // into memory on its loader thread; OGG, WAV and MP3 decode to the same
        // sample positions as the game's decoders.
        private void OpenFile(string path, FmodSound sound)
        {
            int result = FmodNative.FMOD_System_CreateSound(_system, FmodNative.Utf8(path), FileMode, IntPtr.Zero, out sound.Handle);
            if (result != FmodNative.Ok)
                throw new InvalidOperationException("System_CreateSound(" + Path.GetFileName(path) + ") returned " + result);
            sound.FromFile = true;
            sound.State = FmodSound.LoadState.Loading;
            _log?.Invoke("FMOD: loading '" + sound.Name + "' from " + path);
        }

        private void OpenBank(FmodAssetAudio.Entry bank, FmodSound sound)
        {
            var info = new FmodNative.CreateSoundExInfo
            {
                cbsize = Marshal.SizeOf(typeof(FmodNative.CreateSoundExInfo)),
                fileoffset = (uint)bank.Offset,
                length = (uint)bank.Size,
            };
            int result = FmodNative.FMOD_System_CreateSound(_system, FmodNative.Utf8(bank.ResourcePath),
                FileMode & ~FmodNative.ModeAccurateTime, ref info, out sound.Handle);
            if (result != FmodNative.Ok)
                throw new InvalidOperationException("System_CreateSound(" + Path.GetFileName(bank.ResourcePath) + "@" + bank.Offset + ") returned " + result);
            sound.FromFile = true;
            sound.State = FmodSound.LoadState.Loading;
        }

        private void Poll(FmodSound sound, int waitMilliseconds)
        {
            Stopwatch timer = waitMilliseconds > 0 ? Stopwatch.StartNew() : null;
            while (true)
            {
                int state;
                uint percent;
                int starving, busy;
                int result = FmodNative.FMOD_Sound_GetOpenState(sound.Handle, out state, out percent, out starving, out busy);
                if (result != FmodNative.Ok || state == FmodNative.OpenStateError)
                {
                    sound.State = FmodSound.LoadState.Failed;
                    _log?.Invoke("FMOD: could not decode '" + sound.Name + "' (open state " + state + ", result " + result + "); Unity plays it.");
                    Free(sound);
                    return;
                }
                if (state == FmodNative.OpenStateReady)
                {
                    int priority, type, format, channels, bits, subsounds;
                    sound.Playable = sound.Handle;
                    if (FmodNative.FMOD_Sound_GetNumSubSounds(sound.Handle, out subsounds) == FmodNative.Ok && subsounds > 0)
                        FmodNative.FMOD_Sound_GetSubSound(sound.Handle, 0, out sound.Playable);
                    FmodNative.FMOD_Sound_GetDefaults(sound.Playable, out sound.Frequency, out priority);
                    FmodNative.FMOD_Sound_GetLength(sound.Playable, out sound.Length, FmodNative.TimeUnitPcm);
                    FmodNative.FMOD_Sound_GetFormat(sound.Playable, out type, out format, out channels, out bits);
                    sound.Bytes = (long)sound.Length * channels * (bits / 8);
                    sound.State = FmodSound.LoadState.Ready;
                    _log?.Invoke("FMOD: decoded '" + sound.Name + "' (" + sound.Length + " samples at " + sound.Frequency + " Hz).");
                    return;
                }
                if (timer == null || timer.ElapsedMilliseconds >= waitMilliseconds) return;
                FmodNative.FMOD_System_Update(_system);
                Thread.Sleep(1);
            }
        }
    }
}
