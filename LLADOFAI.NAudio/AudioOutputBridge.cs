using NAudio.Wave;
using System;
using System.Diagnostics;
using System.Threading;

namespace LLADOFAI
{
    public class AudioOutputBridge : IWaveProvider
    {
        private const int BufferFrameCount = 1 << 16;
        private const float LimiterCeiling = 0.8912509f; // -1 dBFS
        private const float LimiterReleaseSeconds = 0.05f;
        private const double DefaultMaximumRateCorrection = 0.005;
        private const double QueueProportionalGain = 0.00001;
        private const double QueueIntegralGain = 0.000002;
        private const double QueueSmoothingSeconds = 0.5;
        private const double DefaultMaximumExtraQueueSeconds = 0.01;

        private readonly WaveFormat _waveFormat;
        private readonly float[] _ringBuffer;
        private readonly float[] _resampleBuffer;
        private readonly float[] _lastOutputSamples;
        private readonly float[] _queueTrimFadeFromSamples;
        private readonly int _frameMask;
        private readonly int _channels;
        private readonly int _blockAlign;
        private readonly float _limiterReleaseCoefficient;
        private readonly int _fadeInFrameCount;
        private readonly double _maximumExtraQueueSeconds;
        private readonly double _maximumRateCorrection;
        private int _minimumBufferedFrames;
        private int _steadyStateBufferedFrames;

        private long _writeFramePosition;
        private long _readFramePosition;
        private long _underrunCount;
        private long _overrunCount;
        private long _rateCorrectionPpm;
        private long _fedFrameCount;
        private long _requestedOutputFrameCount;
        private long _lastFeedTimestamp;
        private long _lastReadTimestamp;
        private long _maximumFeedGapTicks;
        private long _maximumReadGapTicks;
        private float _limiterGain = 1f;
        private bool _playbackStarted;
        private bool _queueFilterInitialized;
        private double _readFraction;
        private double _filteredQueueFrames;
        private double _integralRateCorrection;
        private long _stableOutputFrames;
        private int _fadeInFramesRemaining;

        public AudioOutputBridge(int sampleRate, int channels, int minimumBufferedFrames,
            double maximumExtraQueueSeconds = DefaultMaximumExtraQueueSeconds,
            double maximumRateCorrection = DefaultMaximumRateCorrection)
        {
            if (sampleRate <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sampleRate));
            }

            if (channels <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(channels));
            }

            if (minimumBufferedFrames < 0 || minimumBufferedFrames > BufferFrameCount)
            {
                throw new ArgumentOutOfRangeException(nameof(minimumBufferedFrames));
            }

            if (maximumExtraQueueSeconds < 0 || maximumExtraQueueSeconds > 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumExtraQueueSeconds));
            }
            if (maximumRateCorrection <= 0 || maximumRateCorrection > 0.05)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumRateCorrection));
            }

            _channels = channels;
            _blockAlign = channels * sizeof(float);
            _waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
            _minimumBufferedFrames = minimumBufferedFrames;
            _steadyStateBufferedFrames = minimumBufferedFrames;
            _fadeInFrameCount = Math.Max(1, sampleRate / 200);
            _maximumExtraQueueSeconds = maximumExtraQueueSeconds;
            _maximumRateCorrection = maximumRateCorrection;
            _limiterReleaseCoefficient = 1f - (float)Math.Exp(
                -1.0 / (sampleRate * LimiterReleaseSeconds));

            _ringBuffer = new float[BufferFrameCount * channels];
            _resampleBuffer = new float[BufferFrameCount * channels];
            _lastOutputSamples = new float[channels];
            _queueTrimFadeFromSamples = new float[channels];
            _frameMask = BufferFrameCount - 1;
        }

        public WaveFormat WaveFormat => _waveFormat;
        public long UnderrunCount => Interlocked.Read(ref _underrunCount);
        public long OverrunCount => Interlocked.Read(ref _overrunCount);
        public long RateCorrectionPpm => Interlocked.Read(ref _rateCorrectionPpm);
        public long FedFrameCount => Interlocked.Read(ref _fedFrameCount);
        public long RequestedOutputFrameCount => Interlocked.Read(ref _requestedOutputFrameCount);
        public double QueuedMilliseconds
        {
            get
            {
                long queuedFrames = Interlocked.Read(ref _writeFramePosition) -
                    Interlocked.Read(ref _readFramePosition);
                queuedFrames = Math.Max(0, Math.Min(BufferFrameCount, queuedFrames));
                return queuedFrames * 1000.0 / _waveFormat.SampleRate;
            }
        }
        public double SteadyQueueTargetMilliseconds =>
            _steadyStateBufferedFrames * 1000.0 / _waveFormat.SampleRate;
        public double TakeMaximumFeedGapMilliseconds() =>
            Interlocked.Exchange(ref _maximumFeedGapTicks, 0) *
            1000.0 / Stopwatch.Frequency;
        public double TakeMaximumReadGapMilliseconds() =>
            Interlocked.Exchange(ref _maximumReadGapTicks, 0) *
            1000.0 / Stopwatch.Frequency;

        private static void RecordGap(ref long lastTimestamp, ref long maximumGapTicks)
        {
            long now = Stopwatch.GetTimestamp();
            long previous = Interlocked.Exchange(ref lastTimestamp, now);
            if (previous == 0)
            {
                return;
            }

            long gap = now - previous;
            long maximum;
            while (gap > (maximum = Interlocked.Read(ref maximumGapTicks)) &&
                Interlocked.CompareExchange(ref maximumGapTicks, gap, maximum) != maximum)
            {
            }
        }

        public void ResetBuffer()
        {
            long writePosition = Interlocked.Read(ref _writeFramePosition);
            Interlocked.Exchange(ref _readFramePosition, writePosition);
        }

        public void SetMinimumBufferedFrames(int frames)
        {
            if (frames < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(frames));
            }

            _minimumBufferedFrames = Math.Min(frames, BufferFrameCount);
            _steadyStateBufferedFrames = _minimumBufferedFrames;
        }

        public void SetSteadyStateBufferedFrames(int frames)
        {
            if (frames < 0 || frames > _minimumBufferedFrames)
            {
                throw new ArgumentOutOfRangeException(nameof(frames));
            }

            _steadyStateBufferedFrames = frames;
        }

        public void FeedBuffer(float[] data)
        {
            if (data == null || data.Length == 0 || data.Length % _channels != 0)
            {
                return;
            }

            int frames = data.Length / _channels;
            if (frames <= 0 || frames > BufferFrameCount)
            {
                return;
            }

            RecordGap(ref _lastFeedTimestamp, ref _maximumFeedGapTicks);

            long writePosition = Interlocked.Read(ref _writeFramePosition);
            long readPosition = Interlocked.Read(ref _readFramePosition);
            long queuedFrames = writePosition - readPosition;
            int sourceFrameOffset = 0;
            int framesToWrite = frames;

            if (queuedFrames < 0)
            {
                // The ASIO clock advanced through a previous underrun. Skip input
                // frames that are now too late to play, so latency cannot accumulate.
                int staleFrames = (int)Math.Min(framesToWrite, -queuedFrames);
                sourceFrameOffset += staleFrames;
                framesToWrite -= staleFrames;
                writePosition += staleFrames;
                queuedFrames = writePosition - readPosition;
            }
            else if (queuedFrames > BufferFrameCount)
            {
                Interlocked.Exchange(ref _readFramePosition, writePosition);
                Interlocked.Increment(ref _overrunCount);
                queuedFrames = 0;
            }

            if (framesToWrite == 0)
            {
                Interlocked.Exchange(ref _writeFramePosition, writePosition);
                return;
            }

            // Never overwrite samples that the ASIO callback has not consumed.
            // The large frame-aligned ring makes this path an exceptional overrun.
            if (framesToWrite > BufferFrameCount - queuedFrames)
            {
                Interlocked.Exchange(ref _writeFramePosition, writePosition);
                Interlocked.Increment(ref _overrunCount);
                return;
            }

            for (int frameOffset = 0; frameOffset < framesToWrite; frameOffset++)
            {
                int sourceOffset = (sourceFrameOffset + frameOffset) * _channels;
                int destinationOffset = (int)((writePosition + frameOffset) & _frameMask) * _channels;
                float framePeak = 0f;

                for (int channel = 0; channel < _channels; channel++)
                {
                    float sample = data[sourceOffset + channel];
                    if (!float.IsNaN(sample) && !float.IsInfinity(sample))
                    {
                        framePeak = Math.Max(framePeak, Math.Abs(sample));
                    }
                }

                float targetGain = framePeak > LimiterCeiling
                    ? LimiterCeiling / framePeak
                    : 1f;

                if (targetGain < _limiterGain)
                {
                    _limiterGain = targetGain;
                }
                else
                {
                    _limiterGain += (1f - _limiterGain) * _limiterReleaseCoefficient;
                }

                for (int channel = 0; channel < _channels; channel++)
                {
                    float sample = data[sourceOffset + channel];
                    if (float.IsNaN(sample) || float.IsInfinity(sample))
                    {
                        sample = 0f;
                    }

                    _ringBuffer[destinationOffset + channel] = sample * _limiterGain;
                }
            }

            Interlocked.Exchange(ref _writeFramePosition, writePosition + framesToWrite);
            Interlocked.Add(ref _fedFrameCount, framesToWrite);
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            if (buffer == null)
            {
                throw new ArgumentNullException(nameof(buffer));
            }

            if (offset < 0 || count < 0 || offset > buffer.Length - count)
            {
                throw new ArgumentOutOfRangeException();
            }

            if (count == 0)
            {
                return 0;
            }

            RecordGap(ref _lastReadTimestamp, ref _maximumReadGapTicks);

            int alignedCount = count - count % _blockAlign;
            int requestedFrames = alignedCount / _blockAlign;
            Interlocked.Add(ref _requestedOutputFrameCount, requestedFrames);

            if (requestedFrames == 0)
            {
                Array.Clear(buffer, offset, count);
                return count;
            }

            if (requestedFrames > BufferFrameCount)
            {
                Interlocked.Increment(ref _underrunCount);
                Interlocked.Exchange(ref _readFramePosition,
                    Interlocked.Read(ref _writeFramePosition));
                _readFraction = 0;
                _playbackStarted = false;
                _stableOutputFrames = 0;
                Array.Clear(buffer, offset, count);
                return count;
            }

            long readPosition = Interlocked.Read(ref _readFramePosition);
            long writePosition = Interlocked.Read(ref _writeFramePosition);
            long availableFrames = writePosition - readPosition;

            if (availableFrames < 0)
            {
                readPosition = writePosition;
                Interlocked.Exchange(ref _readFramePosition, readPosition);
                availableFrames = 0;
            }
            else if (availableFrames > BufferFrameCount)
            {
                readPosition = writePosition;
                Interlocked.Exchange(ref _readFramePosition, readPosition);
                availableFrames = 0;
            }

            int minimumQueueForBlock = requestedFrames +
                (int)Math.Ceiling(requestedFrames * _maximumRateCorrection) + 2;

            if (_playbackStarted && availableFrames < minimumQueueForBlock)
            {
                // Keep the unread source frames. Advancing the read clock through
                // silence makes subsequent Unity blocks late and prolongs crackle.
                Interlocked.Increment(ref _underrunCount);
                _playbackStarted = false;
                _stableOutputFrames = 0;
                _readFraction = 0;
                _queueFilterInitialized = false;
                _integralRateCorrection = 0;
                _fadeInFramesRemaining = 0;
                Interlocked.Exchange(ref _rateCorrectionPpm, 0);

                int fadeFrames = Math.Min(requestedFrames, _fadeInFrameCount);
                Array.Clear(_resampleBuffer, 0, requestedFrames * _channels);
                for (int frame = 0; frame < fadeFrames; frame++)
                {
                    float gain = 1f - (frame + 1f) / fadeFrames;
                    for (int channel = 0; channel < _channels; channel++)
                    {
                        _resampleBuffer[frame * _channels + channel] =
                            _lastOutputSamples[channel] * gain;
                    }
                }
                Array.Clear(_lastOutputSamples, 0, _channels);
                Buffer.BlockCopy(_resampleBuffer, 0, buffer, offset, alignedCount);
                if (alignedCount < count)
                {
                    Array.Clear(buffer, offset + alignedCount, count - alignedCount);
                }
                return count;
            }

            if (!_playbackStarted)
            {
                if (availableFrames < Math.Max(_minimumBufferedFrames, minimumQueueForBlock))
                {
                    // Deliberate silence while the capture buffer fills or refills.
                    Array.Clear(buffer, offset, count);
                    return count;
                }

                _playbackStarted = true;
                _fadeInFramesRemaining = _fadeInFrameCount;
            }

            // Keep the larger startup queue until playback has run continuously.
            // Then the rate correction gradually reduces latency toward the
            // configured steady-state target.
            int desiredQueueFrames = _stableOutputFrames >= _waveFormat.SampleRate / 2
                ? _steadyStateBufferedFrames : _minimumBufferedFrames;
            int targetQueueFrames = Math.Max(desiredQueueFrames, minimumQueueForBlock);
            int maximumQueueFrames = Math.Max(_minimumBufferedFrames, targetQueueFrames) + Math.Max(
                requestedFrames,
                (int)(_waveFormat.SampleRate * _maximumExtraQueueSeconds));
            int queueTrimFadeFrames = 0;

            if (availableFrames > maximumQueueFrames)
            {
                // A sustained clock mismatch can exceed the resampler's correction
                // range. Drop stale audio to restore sync instead of letting input
                // latency grow for the rest of the session.
                int staleFrames = (int)(availableFrames - targetQueueFrames);
                readPosition += staleFrames;
                availableFrames = targetQueueFrames;
                _readFraction = 0;
                _filteredQueueFrames = availableFrames;
                queueTrimFadeFrames = Math.Min(
                    requestedFrames,
                    Math.Max(1, _waveFormat.SampleRate / 200));
                Array.Copy(_lastOutputSamples, _queueTrimFadeFromSamples, _channels);
                Interlocked.Exchange(ref _readFramePosition, readPosition);
                Interlocked.Increment(ref _overrunCount);
            }

            if (!_queueFilterInitialized)
            {
                _filteredQueueFrames = availableFrames;
                _queueFilterInitialized = true;
            }

            double queueFilterAmount = 1.0 - Math.Exp(
                -requestedFrames / (_waveFormat.SampleRate * QueueSmoothingSeconds));
            _filteredQueueFrames += (availableFrames - _filteredQueueFrames) * queueFilterAmount;

            double queueError = _filteredQueueFrames - targetQueueFrames;
            double elapsedSeconds = (double)requestedFrames / _waveFormat.SampleRate;
            double proposedIntegral = _integralRateCorrection +
                queueError * QueueIntegralGain * elapsedSeconds;
            double proportionalCorrection = queueError * QueueProportionalGain;
            double proposedCorrection = proportionalCorrection + proposedIntegral;
            double correction = Math.Max(
                -_maximumRateCorrection,
                Math.Min(_maximumRateCorrection, proposedCorrection));

            // Anti-windup: stop integrating while saturated unless the error would
            // bring the requested correction back toward the supported range.
            if ((proposedCorrection <= _maximumRateCorrection || queueError < 0) &&
                (proposedCorrection >= -_maximumRateCorrection || queueError > 0))
            {
                _integralRateCorrection = proposedIntegral;
            }

            Interlocked.Exchange(
                ref _rateCorrectionPpm,
                (long)Math.Round(correction * 1000000.0));
            double inputFramesPerOutputFrame = 1.0 + correction;

            bool underflow = false;
            int outputOffset = 0;

            for (int outputFrame = 0; outputFrame < requestedFrames; outputFrame++)
            {
                int sourceFrame = (int)(readPosition & _frameMask);
                int nextFrame = (sourceFrame + 1) & _frameMask;
                bool hasCurrentFrame = readPosition < writePosition;
                bool hasNextFrame = readPosition + 1 < writePosition;
                float fraction = (float)_readFraction;

                if (hasCurrentFrame)
                {
                    int sourceOffset = sourceFrame * _channels;
                    int nextOffset = nextFrame * _channels;
                    for (int channel = 0; channel < _channels; channel++)
                    {
                        float first = _ringBuffer[sourceOffset + channel];
                        float second = hasNextFrame ? _ringBuffer[nextOffset + channel] : first;
                        float sample = first + (second - first) * fraction;

                        if (outputFrame < queueTrimFadeFrames)
                        {
                            float fadeIn = (outputFrame + 1f) / queueTrimFadeFrames;
                            sample = _queueTrimFadeFromSamples[channel] * (1f - fadeIn) +
                                sample * fadeIn;
                        }

                        if (_fadeInFramesRemaining > 0)
                        {
                            sample *= 1f - (_fadeInFramesRemaining - 1f) / _fadeInFrameCount;
                        }

                        _resampleBuffer[outputOffset + channel] = sample;
                    }

                    if (!hasNextFrame && fraction > 0f)
                    {
                        underflow = true;
                    }
                }
                else
                {
                    Array.Clear(_resampleBuffer, outputOffset, _channels);
                    underflow = true;
                }

                _readFraction += inputFramesPerOutputFrame;
                int framesToAdvance = (int)_readFraction;
                _readFraction -= framesToAdvance;
                readPosition += framesToAdvance;
                outputOffset += _channels;
                if (_fadeInFramesRemaining > 0)
                {
                    _fadeInFramesRemaining--;
                }
            }

            int lastOutputOffset = (requestedFrames - 1) * _channels;
            for (int channel = 0; channel < _channels; channel++)
            {
                _lastOutputSamples[channel] = _resampleBuffer[lastOutputOffset + channel];
            }

            if (underflow)
            {
                Interlocked.Increment(ref _underrunCount);
                readPosition = Math.Min(readPosition, writePosition);
                _playbackStarted = false;
                _stableOutputFrames = 0;
                _readFraction = 0;
                _queueFilterInitialized = false;
                _integralRateCorrection = 0;
                Interlocked.Exchange(ref _rateCorrectionPpm, 0);
            }
            else
            {
                _stableOutputFrames += requestedFrames;
            }

            Interlocked.Exchange(ref _readFramePosition, readPosition);

            Buffer.BlockCopy(_resampleBuffer, 0, buffer, offset, alignedCount);
            if (alignedCount < count)
            {
                Array.Clear(buffer, offset + alignedCount, count - alignedCount);
            }

            return count;
        }
    }
}
