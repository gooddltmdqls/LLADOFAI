using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using UnityEngine;

namespace LLADOFAI.Fmod
{
    // Unity's own AudioSettings.dspTime implementation, reachable after the getter
    // has been replaced. The native function is looked up in Mono's internal call
    // table, which is keyed by name and unaffected by Harmony's detour. It is only
    // used after it has returned the same value as the unpatched property.
    internal static class UnityDspClock
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate double NativeGetter();

        [DllImport("mono-2.0-bdwgc", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr mono_lookup_internal_call(IntPtr method);

        private static NativeGetter _getter;
        private static double _lastSeconds;
        private static long _lastTimestamp;

        internal static bool Available => _getter != null;

        // Must run before AudioSettings.dspTime is patched.
        internal static void Resolve(Action<string> log)
        {
            if (_getter != null) return;
            try
            {
                MethodInfo property = typeof(AudioSettings).GetProperty(nameof(AudioSettings.dspTime)).GetGetMethod();
                IntPtr function = mono_lookup_internal_call(property.MethodHandle.Value);
                if (function == IntPtr.Zero)
                {
                    log?.Invoke("Unity's native dspTime was not found; it cannot be restored until the game restarts.");
                    return;
                }
                var getter = (NativeGetter)Marshal.GetDelegateForFunctionPointer(function, typeof(NativeGetter));
                double before = AudioSettings.dspTime, native = getter(), after = AudioSettings.dspTime;
                if (native < before - 0.001 || native > after + 0.25)
                {
                    log?.Invoke("Unity's native dspTime lookup returned " + native + " (expected " + before + "); not used.");
                    return;
                }
                _getter = getter;
            }
            catch (Exception ex)
            {
                log?.Invoke("Unity's native dspTime is unavailable (" + ex.GetType().Name + ": " + ex.Message +
                    "); it cannot be restored until the game restarts.");
            }
        }

        internal static double Read()
        {
            return _getter();
        }

        // Without the native clock, keep time moving from the last value the game saw.
        internal static void Remember(double seconds)
        {
            _lastSeconds = seconds;
            _lastTimestamp = Stopwatch.GetTimestamp();
        }

        internal static double Extrapolate()
        {
            if (_lastTimestamp == 0) return 0.0;
            return _lastSeconds + (Stopwatch.GetTimestamp() - _lastTimestamp) / (double)Stopwatch.Frequency;
        }
    }
}
