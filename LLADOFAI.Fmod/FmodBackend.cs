using HarmonyLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;

namespace LLADOFAI.Fmod
{
    public enum FmodOutput { Wasapi, Asio }

    public enum FmodBackendState { Stopped, Running, Failed }

    public sealed class FmodSettings
    {
        public static readonly int[] BufferLengths = { 8, 16, 32, 64, 128, 256, 512, 1024, 2048 };
        // Below this, mixing overhead rises sharply and dropouts become likely.
        public const int SmallBufferLength = 256;
        public const int MinBufferCount = 2;
        public const int MaxBufferCount = 8;
        public const int DefaultBufferLength = 512;
        public const int DefaultBufferCount = 3;

        public FmodOutput Output = FmodOutput.Wasapi;
        // FmodDeviceInfo.Id of the output, or null/empty for the system default.
        public string DeviceId;
        public int DspBufferLength = DefaultBufferLength;
        public int DspBufferCount = DefaultBufferCount;

        internal static int ClampBufferLength(int length)
        {
            return length < 8 ? 8 : length > 4096 ? 4096 : length;
        }

        internal static int ClampBufferCount(int count)
        {
            return count < MinBufferCount ? MinBufferCount : count > MaxBufferCount ? MaxBufferCount : count;
        }
    }

    public sealed class FmodDeviceInfo
    {
        public string Id;
        public string Name;
        public int SampleRate;
        public int Channels;
    }

    public static class FmodDevices
    {
        // Lists output drivers with a temporary, uninitialised FMOD System; this does
        // not open any device and can run while the engine is active.
        public static List<FmodDeviceInfo> Enumerate(FmodOutput output, string nativeDirectory, out string error)
        {
            error = null;
            if (!FmodBackend.LoadNativeLibrary(nativeDirectory, out error)) return new List<FmodDeviceInfo>();
            if (output != FmodOutput.Asio) return EnumerateHere(output, out error);
            // ASIO drivers are only visible from an STA thread (see FmodStaThread).
            string threadError = null;
            List<FmodDeviceInfo> devices = FmodStaThread.RunOnce(() => EnumerateHere(output, out threadError));
            error = threadError;
            return devices;
        }

        private static List<FmodDeviceInfo> EnumerateHere(FmodOutput output, out string error)
        {
            error = null;
            IntPtr system = IntPtr.Zero;
            try
            {
                Check(FmodNative.FMOD_System_Create(out system, FmodNative.HeaderVersion), "System_Create");
                Check(FmodNative.FMOD_System_SetOutput(system, output == FmodOutput.Asio ? FmodNative.OutputAsio : FmodNative.OutputWasapi), "System_SetOutput");
                int count;
                Check(FmodNative.FMOD_System_GetNumDrivers(system, out count), "System_GetNumDrivers");
                return Read(system, count);
            }
            catch (Exception ex)
            {
                error = FmodBackend.Describe(ex);
                return new List<FmodDeviceInfo>();
            }
            finally
            {
                if (system != IntPtr.Zero) FmodNative.FMOD_System_Release(system);
            }
        }

        internal static List<FmodDeviceInfo> Read(IntPtr system, int count)
        {
            var devices = new List<FmodDeviceInfo>(count);
            byte[] name = new byte[512];
            for (int i = 0; i < count; i++)
            {
                Guid guid;
                int rate, speakerMode, channels;
                Array.Clear(name, 0, name.Length);
                Check(FmodNative.FMOD_System_GetDriverInfo(system, i, name, name.Length, out guid, out rate, out speakerMode, out channels), "System_GetDriverInfo");
                string label = FmodNative.FromUtf8(name);
                devices.Add(new FmodDeviceInfo
                {
                    Id = guid == Guid.Empty ? label : guid.ToString("D"),
                    Name = label,
                    SampleRate = rate,
                    Channels = channels,
                });
            }
            return devices;
        }

        private static void Check(int result, string operation)
        {
            if (result != FmodNative.Ok) throw new FmodException(operation, result);
        }
    }

    // Lifecycle of the FMOD backend: native library, engine, Harmony patches and the
    // per-frame updater. Any failure leaves Unity's own audio path in place.
    public static class FmodBackend
    {
        public const string HarmonyId = "LLADOFAI.Fmod";
        private const string ClockHarmonyId = "LLADOFAI.Fmod.Clock";

        private static FmodEngine _engine;
        private static Harmony _harmony;
        private static Harmony _clockHarmony;
        private static GameObject _updater;
        private static Action<string> _log;
        private static Action<string> _error;
        private static bool _libraryLoaded;

        public static FmodBackendState State { get; private set; }
        public static string LastError { get; private set; }
        public static string DriverName => _engine?.DriverName;
        public static string RuntimeVersion => _engine?.RuntimeVersion;
        public static int SampleRate => _engine == null ? 0 : _engine.SoftwareRate;
        public static int BufferLength => _engine == null ? 0 : _engine.BufferLength;
        public static int BufferCount => _engine == null ? 0 : _engine.BufferCount;
        public static FmodStatistics Statistics => _engine == null ? default(FmodStatistics) : _engine.Statistics();
        public static bool MixerOverloaded => _engine != null && _engine.MixerCpu >= FmodEngine.OverloadedCpu;

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryW(string path);

        // Mono resolves [DllImport("fmod")] through the normal DLL search path, which
        // does not include the mod folder. Loading the mod's copy by full path first
        // makes later imports bind to that module.
        public static bool LoadNativeLibrary(string directory, out string error)
        {
            error = null;
            if (_libraryLoaded) return true;
            string path = string.IsNullOrEmpty(directory) ? null : Path.Combine(directory, "fmod.dll");
            if (path != null && File.Exists(path))
            {
                if (LoadLibraryW(path) == IntPtr.Zero)
                {
                    int code = Marshal.GetLastWin32Error();
                    error = "Could not load " + path + " (Win32 error " + code +
                        (code == 193 ? ": not a 64-bit fmod.dll" : "") + ").";
                    return false;
                }
                _libraryLoaded = true;
                return true;
            }
            try
            {
                // Accept an fmod.dll that is already on the search path.
                IntPtr probe;
                if (FmodNative.FMOD_System_Create(out probe, FmodNative.HeaderVersion) == FmodNative.Ok)
                    FmodNative.FMOD_System_Release(probe);
                _libraryLoaded = true;
                return true;
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException || ex is BadImageFormatException)
            {
                error = "fmod.dll was not found. Copy the 64-bit FMOD Core 2.03 runtime (fmod.dll) into " +
                    (directory ?? "the mod folder") + ".";
                return false;
            }
        }

        public static bool Start(FmodSettings settings, string nativeDirectory, Action<string> log, Action<string> error)
        {
            if (State == FmodBackendState.Running) return true;
            _log = log;
            _error = error;
            LastError = null;
            string loadError;
            if (!LoadNativeLibrary(nativeDirectory, out loadError))
                return Abort(loadError, null);

            var engine = new FmodEngine(log, error);
            try
            {
                engine.Initialize(settings, AudioSettings.dspTime);
            }
            catch (Exception ex)
            {
                return Abort("FMOD initialization failed: " + Describe(ex), engine);
            }

            try
            {
                _engine = engine;
                if (_clockHarmony == null) UnityDspClock.Resolve(_log);
                // Continue the game clock from Unity's current DSP time so nothing that
                // compares dspTime across the switch sees a jump.
                engine.Reanchor(AudioSettings.dspTime);
                FmodEngine.Active = engine;
                if (_clockHarmony == null)
                {
                    // Installed once per process; see DspTimePatch.
                    var clockHarmony = new Harmony(ClockHarmonyId);
                    clockHarmony.CreateClassProcessor(typeof(DspTimePatch)).Patch();
                    _clockHarmony = clockHarmony;
                }
                _harmony = new Harmony(HarmonyId);
                foreach (Type patch in FmodPatchList.AudioSourcePatches)
                    _harmony.CreateClassProcessor(patch).Patch();
                _updater = new GameObject("LLADOFAI.Fmod");
                _updater.hideFlags = HideFlags.HideAndDontSave;
                UnityEngine.Object.DontDestroyOnLoad(_updater);
                _updater.AddComponent<FmodUpdater>();
                SelfTest(engine);
            }
            catch (Exception ex)
            {
                return Abort("FMOD could not take over AudioSource playback: " + Describe(ex), engine);
            }

            State = FmodBackendState.Running;
            _log?.Invoke("FMOD backend active; Unity AudioSource playback is routed to FMOD.");
            return true;
        }

        private static bool Abort(string message, FmodEngine engine)
        {
            LastError = message;
            _error?.Invoke(message + " Unity audio remains active.");
            StopInternal(engine);
            State = FmodBackendState.Failed;
            return false;
        }

        public static void Stop()
        {
            StopInternal(_engine);
            if (State == FmodBackendState.Running) State = FmodBackendState.Stopped;
        }

        private static void StopInternal(FmodEngine engine)
        {
            FmodEngine.Active = null;
            if (_harmony != null)
            {
                try { _harmony.UnpatchAll(HarmonyId); }
                catch (Exception ex) { _error?.Invoke("Removing FMOD patches failed: " + ex); }
                _harmony = null;
            }
            if (engine != null)
            {
                try { engine.Shutdown(); }
                catch (Exception ex) { _error?.Invoke("FMOD shutdown failed: " + ex); }
            }
            _engine = null;
            if (_updater != null)
            {
                UnityEngine.Object.Destroy(_updater);
                _updater = null;
            }
        }

        internal static void Update()
        {
            FmodEngine engine = _engine;
            if (engine == null) return;
            if (engine.Failed)
            {
                LastError = engine.FailureMessage;
                StopInternal(engine);
                State = FmodBackendState.Failed;
                return;
            }
            engine.Update();
        }

        internal static void ApplicationQuitting()
        {
            Stop();
        }

        internal static double FallbackDspTime()
        {
            FmodEngine engine = _engine;
            return engine != null && engine.Failed ? engine.FailedDspTime() : UnityDspClock.Extrapolate();
        }

        internal static string Describe(Exception ex)
        {
            if (ex is DllNotFoundException) return "fmod.dll could not be loaded (" + ex.Message + ")";
            if (ex is EntryPointNotFoundException) return "fmod.dll is missing an FMOD Core 2.03 export (" + ex.Message + ")";
            if (ex is BadImageFormatException) return "fmod.dll is not a 64-bit Windows library";
            return ex.GetType().Name + ": " + ex.Message;
        }

        // Verifies on this Unity/Mono runtime that the clock and AudioSource patches
        // are really in effect, with a silent clip scheduled far in the future.
        private static void SelfTest(FmodEngine engine)
        {
            double before = engine.DspTime();
            double patched = AudioSettings.dspTime;
            double after = engine.DspTime();
            if (!(before <= patched && patched <= after))
                throw new InvalidOperationException("AudioSettings.dspTime is not reading the FMOD clock (" +
                    before + " / " + patched + " / " + after + ").");

            var gameObject = new GameObject("LLADOFAI.Fmod.SelfTest");
            gameObject.hideFlags = HideFlags.HideAndDontSave;
            AudioClip clip = null;
            try
            {
                AudioSource source = gameObject.AddComponent<AudioSource>();
                source.playOnAwake = false;
                source.volume = 0f;
                int frames = Math.Max(1, engine.SoftwareRate / 10);
                clip = AudioClip.Create("LLADOFAI.Fmod.SelfTest", frames, 1, engine.SoftwareRate, false);
                ClipData.Set(clip, new float[frames], 0);
                source.clip = clip;
                source.PlayScheduled(AudioSettings.dspTime + 10.0);
                if (!engine.IsHandling(source) || !source.isPlaying)
                    throw new InvalidOperationException("AudioSource.PlayScheduled was not routed to FMOD.");
                source.timeSamples = frames / 2;
                if (source.timeSamples != frames / 2)
                    throw new InvalidOperationException("AudioSource.timeSamples does not follow the FMOD channel.");
                source.Stop();
                if (engine.IsHandling(source) || source.isPlaying)
                    throw new InvalidOperationException("AudioSource.Stop did not stop the FMOD channel.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
                if (clip != null)
                {
                    engine.ClipUnloaded(clip);
                    UnityEngine.Object.DestroyImmediate(clip);
                }
            }
        }
    }
}
