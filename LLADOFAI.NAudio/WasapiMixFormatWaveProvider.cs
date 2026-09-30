using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System;

namespace LLADOFAI
{
    /// <summary>
    /// Adapts Unity's float audio to the selected endpoint's shared-mode mix format.
    /// </summary>
    internal sealed class WasapiMixFormatWaveProvider : IWaveProvider
    {
        private static readonly Guid PcmSubFormat = new Guid("00000001-0000-0010-8000-00aa00389b71");
        private static readonly Guid IeeeFloatSubFormat = new Guid("00000003-0000-0010-8000-00aa00389b71");

        private readonly ISampleProvider _samples;
        private readonly int _channels;
        private readonly int _bitsPerSample;
        private readonly bool _isFloat;
        private float[] _sampleBuffer = Array.Empty<float>();
        private double[] _doubleBuffer = Array.Empty<double>();

        public WasapiMixFormatWaveProvider(AudioOutputBridge source, WaveFormat mixFormat)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            WaveFormat = mixFormat ?? throw new ArgumentNullException(nameof(mixFormat));
            _channels = WaveFormat.Channels;
            _bitsPerSample = WaveFormat.BitsPerSample;
            _isFloat = IsIeeeFloat(WaveFormat);

            if (!_isFloat && !IsPcm(WaveFormat))
            {
                throw new NotSupportedException(
                    "The endpoint mix format must use PCM or IEEE floating-point audio: " + WaveFormat);
            }

            if (_isFloat && _bitsPerSample != 32 && _bitsPerSample != 64)
            {
                throw new NotSupportedException(
                    "The endpoint floating-point format has an unsupported bit depth: " + WaveFormat);
            }

            if (!_isFloat && _bitsPerSample != 8 && _bitsPerSample != 16 &&
                _bitsPerSample != 24 && _bitsPerSample != 32)
            {
                throw new NotSupportedException(
                    "The endpoint PCM format has an unsupported bit depth: " + WaveFormat);
            }

            ISampleProvider samples = new ChannelRemappingSampleProvider(
                new WaveToSampleProvider(source),
                _channels);

            _samples = samples.WaveFormat.SampleRate == WaveFormat.SampleRate
                ? samples
                : new WdlResamplingSampleProvider(samples, WaveFormat.SampleRate);
        }

        public WaveFormat WaveFormat { get; }

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

            int alignedBytes = count - count % WaveFormat.BlockAlign;
            int requestedFrames = alignedBytes / WaveFormat.BlockAlign;
            int requestedSamples = requestedFrames * _channels;
            int bytesPerSample = _bitsPerSample / 8;

            if (_sampleBuffer.Length < requestedSamples)
            {
                Array.Resize(ref _sampleBuffer, requestedSamples);
            }

            int samplesRead = _samples.Read(_sampleBuffer, 0, requestedSamples);
            samplesRead = Math.Max(0, Math.Min(requestedSamples, samplesRead));

            if (_isFloat && _bitsPerSample == 32)
            {
                Buffer.BlockCopy(_sampleBuffer, 0, buffer, offset, samplesRead * sizeof(float));
            }
            else if (_isFloat)
            {
                if (_doubleBuffer.Length < samplesRead)
                {
                    Array.Resize(ref _doubleBuffer, samplesRead);
                }

                for (int sampleIndex = 0; sampleIndex < samplesRead; sampleIndex++)
                {
                    float sample = _sampleBuffer[sampleIndex];
                    _doubleBuffer[sampleIndex] = float.IsNaN(sample) || float.IsInfinity(sample)
                        ? 0.0
                        : sample;
                }

                Buffer.BlockCopy(_doubleBuffer, 0, buffer, offset, samplesRead * sizeof(double));
            }
            else
            {
                for (int sampleIndex = 0; sampleIndex < samplesRead; sampleIndex++)
                {
                    float sample = _sampleBuffer[sampleIndex];
                    if (float.IsNaN(sample) || float.IsInfinity(sample))
                    {
                        sample = 0f;
                    }

                    int destinationOffset = offset + sampleIndex * bytesPerSample;
                    WritePcmSample(buffer, destinationOffset, sample, _bitsPerSample);
                }
            }

            int convertedBytes = samplesRead * bytesPerSample;
            if (convertedBytes < count)
            {
                Array.Clear(buffer, offset + convertedBytes, count - convertedBytes);
            }

            return count;
        }

        private static bool IsPcm(WaveFormat format)
        {
            if (format.Encoding == WaveFormatEncoding.Pcm)
            {
                return true;
            }

            WaveFormatExtensible extensible = format as WaveFormatExtensible;
            return extensible != null && extensible.SubFormat == PcmSubFormat;
        }

        private static bool IsIeeeFloat(WaveFormat format)
        {
            if (format.Encoding == WaveFormatEncoding.IeeeFloat)
            {
                return true;
            }

            WaveFormatExtensible extensible = format as WaveFormatExtensible;
            return extensible != null && extensible.SubFormat == IeeeFloatSubFormat;
        }

        private static void WritePcmSample(byte[] buffer, int offset, float sample, int bitsPerSample)
        {
            sample = Math.Max(-1f, Math.Min(1f, sample));

            if (bitsPerSample == 8)
            {
                int unsignedSample = (int)Math.Round((sample + 1f) * 127.5f);
                buffer[offset] = (byte)Math.Max(0, Math.Min(255, unsignedSample));
                return;
            }

            long maximum = (1L << (bitsPerSample - 1)) - 1;
            long minimum = -(1L << (bitsPerSample - 1));
            long pcm = sample <= -1f
                ? minimum
                : (long)Math.Round(sample * maximum);
            pcm = Math.Max(minimum, Math.Min(maximum, pcm));

            for (int byteIndex = 0; byteIndex < bitsPerSample / 8; byteIndex++)
            {
                buffer[offset + byteIndex] = (byte)((pcm >> (byteIndex * 8)) & 0xff);
            }
        }

        private sealed class ChannelRemappingSampleProvider : ISampleProvider
        {
            private readonly ISampleProvider _source;
            private readonly int _sourceChannels;
            private readonly int _destinationChannels;
            private float[] _sourceBuffer = Array.Empty<float>();

            public ChannelRemappingSampleProvider(ISampleProvider source, int destinationChannels)
            {
                _source = source ?? throw new ArgumentNullException(nameof(source));
                if (destinationChannels <= 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(destinationChannels));
                }

                _sourceChannels = source.WaveFormat.Channels;
                _destinationChannels = destinationChannels;
                WaveFormat = NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(
                    source.WaveFormat.SampleRate,
                    destinationChannels);
            }

            public WaveFormat WaveFormat { get; }

            public int Read(float[] buffer, int offset, int count)
            {
                if (buffer == null)
                {
                    throw new ArgumentNullException(nameof(buffer));
                }

                if (offset < 0 || count < 0 || offset > buffer.Length - count)
                {
                    throw new ArgumentOutOfRangeException();
                }

                int outputFrames = count / _destinationChannels;
                int inputSampleCount = outputFrames * _sourceChannels;
                if (_sourceBuffer.Length < inputSampleCount)
                {
                    Array.Resize(ref _sourceBuffer, inputSampleCount);
                }

                int inputSamplesRead = _source.Read(_sourceBuffer, 0, inputSampleCount);
                int inputFramesRead = inputSamplesRead / _sourceChannels;

                for (int frame = 0; frame < inputFramesRead; frame++)
                {
                    int sourceOffset = frame * _sourceChannels;
                    int destinationOffset = offset + frame * _destinationChannels;
                    RemapFrame(_sourceBuffer, sourceOffset, buffer, destinationOffset);
                }

                int outputSamplesRead = inputFramesRead * _destinationChannels;
                if (outputSamplesRead < count)
                {
                    Array.Clear(buffer, offset + outputSamplesRead, count - outputSamplesRead);
                }

                return count;
            }

            private void RemapFrame(float[] source, int sourceOffset, float[] destination, int destinationOffset)
            {
                if (_sourceChannels == _destinationChannels)
                {
                    Array.Copy(source, sourceOffset, destination, destinationOffset, _sourceChannels);
                    return;
                }

                Array.Clear(destination, destinationOffset, _destinationChannels);

                if (_destinationChannels == 1)
                {
                    float sum = 0f;
                    for (int channel = 0; channel < _sourceChannels; channel++)
                    {
                        sum += source[sourceOffset + channel];
                    }

                    destination[destinationOffset] = sum / _sourceChannels;
                    return;
                }

                if (_sourceChannels == 1)
                {
                    destination[destinationOffset] = source[sourceOffset];
                    destination[destinationOffset + 1] = source[sourceOffset];
                    return;
                }

                if (_destinationChannels == 2 && _sourceChannels >= 3)
                {
                    float left = source[sourceOffset];
                    float right = source[sourceOffset + 1];
                    float leftWeight = 1f;
                    float rightWeight = 1f;

                    // Unity surround order is FL, FR, C, LFE, surround-left, surround-right.
                    if (_sourceChannels >= 3)
                    {
                        left += source[sourceOffset + 2] * 0.70710678f;
                        right += source[sourceOffset + 2] * 0.70710678f;
                        leftWeight += 0.70710678f;
                        rightWeight += 0.70710678f;
                    }

                    if (_sourceChannels >= 6)
                    {
                        left += source[sourceOffset + 4] * 0.70710678f;
                        right += source[sourceOffset + 5] * 0.70710678f;
                        leftWeight += 0.70710678f;
                        rightWeight += 0.70710678f;
                    }

                    if (_sourceChannels >= 8)
                    {
                        left += source[sourceOffset + 6] * 0.70710678f;
                        right += source[sourceOffset + 7] * 0.70710678f;
                        leftWeight += 0.70710678f;
                        rightWeight += 0.70710678f;
                    }

                    destination[destinationOffset] = left / leftWeight;
                    destination[destinationOffset + 1] = right / rightWeight;
                    return;
                }

                int channelsToCopy = Math.Min(_sourceChannels, _destinationChannels);
                Array.Copy(source, sourceOffset, destination, destinationOffset, channelsToCopy);
            }
        }
    }
}
