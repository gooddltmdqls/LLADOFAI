using System;

namespace LLADOFAI.Fmod
{
    // Maps the DSP clock of the pausable FMOD group to the time the game reads from
    // AudioSettings.dspTime. Both directions use one fixed anchor, so converting a
    // time the game derived from DspTime back to a clock is exact to the nearest
    // sample and errors cannot accumulate over a long session.
    internal sealed class FmodClock
    {
        internal readonly int Rate;
        private readonly ulong _anchorClock;
        private readonly double _anchorSeconds;

        internal FmodClock(int rate, ulong anchorClock, double anchorSeconds)
        {
            if (rate <= 0) throw new ArgumentOutOfRangeException(nameof(rate));
            Rate = rate;
            _anchorClock = anchorClock;
            _anchorSeconds = anchorSeconds;
        }

        internal double ToSeconds(ulong clock)
        {
            return _anchorSeconds + (double)unchecked((long)(clock - _anchorClock)) / Rate;
        }

        // Times before the anchor map to the anchor; callers treat any clock at or
        // before the current one as "start now".
        internal ulong ToClock(double seconds)
        {
            double frames = Math.Round((seconds - _anchorSeconds) * Rate, MidpointRounding.AwayFromZero);
            if (double.IsNaN(frames) || frames <= 0) return _anchorClock;
            if (frames >= long.MaxValue / 2) return _anchorClock + (ulong)(long.MaxValue / 2);
            return _anchorClock + (ulong)frames;
        }

        internal ulong FramesFor(double seconds)
        {
            double frames = Math.Round(seconds * Rate, MidpointRounding.AwayFromZero);
            if (double.IsNaN(frames) || frames <= 0) return 0;
            return frames >= long.MaxValue / 2 ? (ulong)(long.MaxValue / 2) : (ulong)frames;
        }
    }
}
