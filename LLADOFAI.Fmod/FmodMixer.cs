using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Audio;

namespace LLADOFAI.Fmod
{
    // Reproduces the volume routing of ADOFAI's MasterMixer for FMOD voices. Unity
    // does not expose mixer group parents at runtime, so each group's chain of
    // exposed volume parameters is taken from the game's MasterMixer asset:
    //
    // Master
    // └─ MasterPrivate (MainVolume)
    //    ├─ ConductorParent
    //    │  └─ WorldBalancer (WorldVolume)
    //    │     ├─ ConductorMusic (MusicVolume)
    //    │     └─ ConductorHitsounds (HitsoundsVolume)
    //    ├─ SfxParent (SfxVolume): ConductorSfx, ConductorPlaySound
    //    ├─ InterfaceParent (InterfaceVolume)
    //    ├─ OverrideParent (OverrideVolume): OverrideHitsounds
    //    └─ Fallback
    //
    // Groups that are not part of this mixer play at unity gain.
    internal sealed class FmodMixer
    {
        private const int Main = 1, World = 2, Music = 4, Hitsounds = 8, Sfx = 16, Interface = 32, Override = 64;
        private static readonly string[] Parameters =
            { "MainVolume", "WorldVolume", "MusicVolume", "HitsoundsVolume", "SfxVolume", "InterfaceVolume", "OverrideVolume" };

        private static readonly Dictionary<string, int> Chains = new Dictionary<string, int>
        {
            { "Master", 0 },
            { "MasterPrivate", Main },
            { "ConductorParent", Main },
            { "WorldBalancer", Main | World },
            { "ConductorMusic", Main | World | Music },
            { "ConductorHitsounds", Main | World | Hitsounds },
            { "SfxParent", Main | Sfx },
            { "ConductorSfx", Main | Sfx },
            { "ConductorPlaySound", Main | Sfx },
            { "InterfaceParent", Main | Interface },
            { "OverrideParent", Main | Override },
            { "OverrideHitsounds", Main | Override },
            { "Fallback", Main },
        };

        private readonly float[] _gains = { 1f, 1f, 1f, 1f, 1f, 1f, 1f };
        private readonly Dictionary<int, int> _groupMasks = new Dictionary<int, int>();
        private AudioMixer _mixer;

        // Returns the combined gain for a group; mask identifies the parameters it uses.
        internal float Gain(AudioMixerGroup group, out int mask)
        {
            mask = 0;
            if (ReferenceEquals(group, null) || group == null) return 1f;
            int id = group.GetInstanceID();
            if (!_groupMasks.TryGetValue(id, out mask))
            {
                mask = 0;
                AudioMixer mixer = group.audioMixer;
                int chain;
                if (mixer != null && Chains.TryGetValue(group.name, out chain) && (_mixer == null || _mixer == mixer))
                {
                    if (_mixer == null)
                    {
                        _mixer = mixer;
                        for (int i = 0; i < Parameters.Length; i++) Read(i);
                    }
                    mask = chain;
                }
                _groupMasks[id] = mask;
            }
            return GainFor(mask);
        }

        internal float GainFor(int mask)
        {
            float gain = 1f;
            for (int i = 0; mask != 0; i++, mask >>= 1)
                if ((mask & 1) != 0) gain *= _gains[i];
            return gain;
        }

        // Called after AudioMixer.SetFloat/ClearFloat. Returns the changed mask bit or 0.
        internal int ParameterChanged(AudioMixer mixer, string name)
        {
            if (_mixer == null || name == null || !ReferenceEquals(mixer, _mixer)) return 0;
            for (int i = 0; i < Parameters.Length; i++)
            {
                if (name != Parameters[i]) continue;
                float previous = _gains[i];
                Read(i);
                return previous != _gains[i] ? 1 << i : 0;
            }
            return 0;
        }

        private void Read(int index)
        {
            float decibels;
            if (!_mixer.GetFloat(Parameters[index], out decibels)) decibels = 0f;
            // The game writes -80 dB for silence; treat it as a hard mute like Unity.
            _gains[index] = decibels <= -80f ? 0f : Mathf.Pow(10f, decibels / 20f);
        }
    }
}
