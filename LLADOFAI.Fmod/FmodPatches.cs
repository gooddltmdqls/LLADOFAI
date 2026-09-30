using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Audio;

namespace LLADOFAI.Fmod
{
    // These patches are installed only while the FMOD backend runs. Every prefix
    // lets Unity's implementation run when FMOD is inactive, when the engine calls
    // Unity itself (Passthrough), or when FMOD declines a request.
    //
    // Where Unity's public method is a tiny wrapper (Play(), Stop(), pitch, clip),
    // the internal helper it calls is patched instead: Mono may inline methods of up
    // to 20 IL bytes into their callers, which would bypass a detour.

    // Play() and Play(ulong delay).
    [HarmonyPatch(typeof(AudioSource), "PlayHelper")]
    internal static class PlayHelperPatch
    {
        private static bool Prefix(AudioSource __0, ulong __1)
        {
            FmodEngine engine = FmodEngine.Active;
            return engine == null || FmodEngine.Passthrough || ReferenceEquals(__0, null) || !engine.PlayNow(__0, __1);
        }
    }

    // PlayScheduled(time) and PlayDelayed(delay).
    [HarmonyPatch(typeof(AudioSource), "Play", new[] { typeof(double) })]
    internal static class PlayDoublePatch
    {
        private static bool Prefix(AudioSource __instance, ref double __0)
        {
            FmodEngine engine = FmodEngine.Active;
            return engine == null || FmodEngine.Passthrough || !engine.PlayAt(__instance, ref __0);
        }
    }

    [HarmonyPatch(typeof(AudioSource), "PlayOneShotHelper")]
    internal static class PlayOneShotPatch
    {
        private static bool Prefix(AudioSource __0, AudioClip __1, float __2)
        {
            FmodEngine engine = FmodEngine.Active;
            return engine == null || FmodEngine.Passthrough || ReferenceEquals(__0, null) || !engine.PlayOneShot(__0, __1, __2);
        }
    }

    // Stop() calls Stop(bool). Unity's implementation always runs as well.
    [HarmonyPatch(typeof(AudioSource), "Stop", new[] { typeof(bool) })]
    internal static class StopPatch
    {
        private static void Prefix(AudioSource __instance, bool __0)
        {
            FmodEngine engine = FmodEngine.Active;
            if (engine == null || FmodEngine.Passthrough) return;
            try { engine.Stop(__instance, __0); }
            catch (Exception ex) { engine.Fail(ex, "stopping a source"); }
        }
    }

    [HarmonyPatch(typeof(AudioSource), nameof(AudioSource.Pause))]
    internal static class PausePatch
    {
        private static void Prefix(AudioSource __instance)
        {
            FmodEngine engine = FmodEngine.Active;
            if (engine != null && !FmodEngine.Passthrough) engine.SetPaused(__instance, true);
        }
    }

    [HarmonyPatch(typeof(AudioSource), nameof(AudioSource.UnPause))]
    internal static class UnPausePatch
    {
        private static void Prefix(AudioSource __instance)
        {
            FmodEngine engine = FmodEngine.Active;
            if (engine != null && !FmodEngine.Passthrough) engine.SetPaused(__instance, false);
        }
    }

    [HarmonyPatch(typeof(AudioSource), nameof(AudioSource.SetScheduledStartTime))]
    internal static class ScheduledStartPatch
    {
        private static bool Prefix(AudioSource __instance, double __0)
        {
            FmodEngine engine = FmodEngine.Active;
            return engine == null || FmodEngine.Passthrough || !engine.SetScheduledStart(__instance, __0);
        }
    }

    [HarmonyPatch(typeof(AudioSource), nameof(AudioSource.SetScheduledEndTime))]
    internal static class ScheduledEndPatch
    {
        private static bool Prefix(AudioSource __instance, double __0)
        {
            FmodEngine engine = FmodEngine.Active;
            return engine == null || FmodEngine.Passthrough || !engine.SetScheduledEnd(__instance, __0);
        }
    }

    [HarmonyPatch(typeof(AudioSource), nameof(AudioSource.isPlaying), MethodType.Getter)]
    internal static class IsPlayingPatch
    {
        private static bool Prefix(AudioSource __instance, ref bool __result)
        {
            FmodEngine engine = FmodEngine.Active;
            return engine == null || FmodEngine.Passthrough || !engine.IsPlaying(__instance, out __result);
        }
    }

    [HarmonyPatch(typeof(AudioSource), nameof(AudioSource.time), MethodType.Getter)]
    internal static class GetTimePatch
    {
        private static bool Prefix(AudioSource __instance, ref float __result)
        {
            FmodEngine engine = FmodEngine.Active;
            return engine == null || FmodEngine.Passthrough || !engine.TryGetTime(__instance, out __result);
        }
    }

    [HarmonyPatch(typeof(AudioSource), nameof(AudioSource.timeSamples), MethodType.Getter)]
    internal static class GetTimeSamplesPatch
    {
        private static bool Prefix(AudioSource __instance, ref int __result)
        {
            FmodEngine engine = FmodEngine.Active;
            return engine == null || FmodEngine.Passthrough || !engine.TryGetSamples(__instance, out __result);
        }
    }

    [HarmonyPatch(typeof(AudioSource), nameof(AudioSource.time), MethodType.Setter)]
    internal static class SetTimePatch
    {
        private static bool Prefix(AudioSource __instance, float __0)
        {
            FmodEngine engine = FmodEngine.Active;
            return engine == null || FmodEngine.Passthrough || !engine.SetTime(__instance, __0);
        }
    }

    [HarmonyPatch(typeof(AudioSource), nameof(AudioSource.timeSamples), MethodType.Setter)]
    internal static class SetTimeSamplesPatch
    {
        private static bool Prefix(AudioSource __instance, int __0)
        {
            FmodEngine engine = FmodEngine.Active;
            return engine == null || FmodEngine.Passthrough || !engine.SetSamples(__instance, __0);
        }
    }

    // AudioSource.clip and AudioSource.resource both assign generatorObject.
    [HarmonyPatch(typeof(AudioSource), "generatorObject", MethodType.Setter)]
    internal static class ClipPatch
    {
        private static void Prefix(AudioSource __instance, UnityEngine.Object __0)
        {
            FmodEngine engine = FmodEngine.Active;
            if (engine != null && !FmodEngine.Passthrough) engine.ClipChanging(__instance, __0);
        }

        private static void Postfix(UnityEngine.Object __0)
        {
            FmodEngine engine = FmodEngine.Active;
            AudioClip clip = __0 as AudioClip;
            if (engine != null && !ReferenceEquals(clip, null) && clip != null) engine.Preload(clip);
        }
    }

    [HarmonyPatch(typeof(AudioSource), nameof(AudioSource.volume), MethodType.Setter)]
    internal static class VolumePatch
    {
        private static void Postfix(AudioSource __instance, float __0) { FmodEngine.Active?.VolumeChanged(__instance, __0); }
    }

    [HarmonyPatch(typeof(AudioSource), nameof(AudioSource.mute), MethodType.Setter)]
    internal static class MutePatch
    {
        private static void Postfix(AudioSource __instance) { FmodEngine.Active?.MuteChanged(__instance); }
    }

    // pitch's setter is a 10-byte wrapper around this helper.
    [HarmonyPatch(typeof(AudioSource), "SetPitch")]
    internal static class PitchPatch
    {
        private static void Postfix(AudioSource __0, float __1) { FmodEngine.Active?.PitchChanged(__0, __1); }
    }

    [HarmonyPatch(typeof(AudioSource), nameof(AudioSource.loop), MethodType.Setter)]
    internal static class LoopPatch
    {
        private static void Postfix(AudioSource __instance, bool __0) { FmodEngine.Active?.LoopChanged(__instance, __0); }
    }

    [HarmonyPatch(typeof(AudioSource), nameof(AudioSource.priority), MethodType.Setter)]
    internal static class PriorityPatch
    {
        private static void Postfix(AudioSource __instance, int __0) { FmodEngine.Active?.PriorityChanged(__instance, __0); }
    }

    [HarmonyPatch(typeof(AudioSource), nameof(AudioSource.panStereo), MethodType.Setter)]
    internal static class PanPatch
    {
        private static void Postfix(AudioSource __instance, float __0) { FmodEngine.Active?.PanChanged(__instance, __0); }
    }

    [HarmonyPatch(typeof(AudioSource), nameof(AudioSource.ignoreListenerPause), MethodType.Setter)]
    internal static class IgnoreListenerPausePatch
    {
        private static void Postfix(AudioSource __instance, bool __0) { FmodEngine.Active?.IgnoreListenerPauseChanged(__instance, __0); }
    }

    [HarmonyPatch(typeof(AudioSource), nameof(AudioSource.outputAudioMixerGroup), MethodType.Setter)]
    internal static class MixerGroupPatch
    {
        private static void Postfix(AudioSource __instance) { FmodEngine.Active?.MixerGroupChanged(__instance); }
    }

    // Unity declares the samples array [Out]. Harmony treats [Out] parameters as
    // by-reference, so the patch parameter must be declared the same way or Harmony
    // dereferences the array reference.
    [HarmonyPatch(typeof(AudioSource), "GetSpectrumDataHelper")]
    internal static class SourceSpectrumPatch
    {
        private static bool Prefix(AudioSource __0, [Out] float[] __1, int __2)
        {
            FmodEngine engine = FmodEngine.Active;
            return engine == null || FmodEngine.Passthrough || !engine.GetSpectrum(__0, __1, __2);
        }
    }

    [HarmonyPatch(typeof(AudioListener), "GetSpectrumDataHelper")]
    internal static class ListenerSpectrumPatch
    {
        private static bool Prefix([Out] float[] __0, int __1)
        {
            FmodEngine engine = FmodEngine.Active;
            return engine == null || !engine.GetListenerSpectrum(__0, __1);
        }
    }

    [HarmonyPatch(typeof(AudioClip), nameof(AudioClip.UnloadAudioData))]
    internal static class ClipUnloadPatch
    {
        private static void Postfix(AudioClip __instance, bool __result)
        {
            if (__result) FmodEngine.Active?.ClipUnloaded(__instance);
        }
    }

    [HarmonyPatch(typeof(AudioMixer), nameof(AudioMixer.SetFloat))]
    internal static class MixerSetFloatPatch
    {
        private static void Postfix(AudioMixer __instance, string __0, bool __result)
        {
            if (__result) FmodEngine.Active?.MixerParameterChanged(__instance, __0);
        }
    }

    [HarmonyPatch(typeof(AudioMixer), nameof(AudioMixer.ClearFloat))]
    internal static class MixerClearFloatPatch
    {
        private static void Postfix(AudioMixer __instance, string __0, bool __result)
        {
            if (__result) FmodEngine.Active?.MixerParameterChanged(__instance, __0);
        }
    }

    // dspTime is a native property with no managed body, so the whole getter is
    // replaced. Harmony cannot restore a native method (unpatching regenerates an
    // empty body), so this patch stays for the rest of the process: while FMOD runs
    // it returns the pausable group's clock, otherwise Unity's own native clock.
    [HarmonyPatch(typeof(AudioSettings), nameof(AudioSettings.dspTime), MethodType.Getter)]
    internal static class DspTimePatch
    {
        internal static double Now()
        {
            FmodEngine engine = FmodEngine.Active;
            if (engine != null)
            {
                double time = engine.DspTime();
                if (!UnityDspClock.Available) UnityDspClock.Remember(time);
                return time;
            }
            if (UnityDspClock.Available) return UnityDspClock.Read();
            return FmodBackend.FallbackDspTime();
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(DspTimePatch), nameof(Now)));
            yield return new CodeInstruction(OpCodes.Ret);
        }
    }

    internal static class FmodPatchList
    {
        // Everything except DspTimePatch, which is installed separately and never removed.
        internal static readonly Type[] AudioSourcePatches =
        {
            typeof(PlayHelperPatch), typeof(PlayDoublePatch), typeof(PlayOneShotPatch), typeof(StopPatch),
            typeof(PausePatch), typeof(UnPausePatch), typeof(ScheduledStartPatch), typeof(ScheduledEndPatch),
            typeof(IsPlayingPatch), typeof(GetTimePatch), typeof(GetTimeSamplesPatch), typeof(SetTimePatch),
            typeof(SetTimeSamplesPatch), typeof(ClipPatch), typeof(VolumePatch), typeof(MutePatch), typeof(PitchPatch),
            typeof(LoopPatch), typeof(PriorityPatch), typeof(PanPatch), typeof(IgnoreListenerPausePatch),
            typeof(MixerGroupPatch), typeof(SourceSpectrumPatch), typeof(ListenerSpectrumPatch), typeof(ClipUnloadPatch),
            typeof(MixerSetFloatPatch), typeof(MixerClearFloatPatch), typeof(ExternalSongPatch),
        };
    }

    // ADOFAI streams custom-level songs, which AudioClip.GetData cannot read.
    // Remember the file behind each "<file>*external" clip so FMOD can decode it.
    [HarmonyPatch]
    internal static class ExternalSongPatch
    {
        private static MethodBase Target()
        {
            Type manager = Type.GetType("AudioManager, Assembly-CSharp");
            return manager == null ? null : AccessTools.Method(manager, "FindOrLoadAudioClipExternal");
        }

        private static bool Prepare() { return Target() != null; }

        private static MethodBase TargetMethod() { return Target(); }

        private static void Prefix(string __0) { FmodEngine.Active?.RegisterExternalFile(__0); }
    }
}
