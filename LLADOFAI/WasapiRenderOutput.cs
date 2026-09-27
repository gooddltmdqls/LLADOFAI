using NAudio.CoreAudioApi;
using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

namespace LLADOFAI
{
    /// <summary>
    /// WASAPI renderer using the endpoint mix format in shared mode and a
    /// probed device format in exclusive mode.
    /// </summary>
    public sealed class WasapiRenderOutput : IDisposable
    {
        private static readonly Guid AudioClientInterfaceId =
            new Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
        private const int BufferSizeNotAligned = unchecked((int)0x88890019);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int IsFormatSupportedDelegate(
            IntPtr client,
            AudioClientShareMode shareMode,
            IntPtr waveFormat,
            IntPtr closestMatch);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int InitializeAudioClientDelegate(
            IntPtr client,
            AudioClientShareMode shareMode,
            AudioClientStreamFlags streamFlags,
            long bufferDuration,
            long periodicity,
            IntPtr waveFormat,
            ref Guid sessionId);

        [DllImport("winmm.dll")]
        private static extern uint timeBeginPeriod(uint period);

        [DllImport("winmm.dll")]
        private static extern uint timeEndPeriod(uint period);

        [DllImport("avrt.dll", EntryPoint = "AvSetMmThreadCharacteristicsW", CharSet = CharSet.Unicode)]
        private static extern IntPtr AvSetMmThreadCharacteristics(
            string taskName, ref uint taskIndex);

        [DllImport("avrt.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AvRevertMmThreadCharacteristics(IntPtr taskHandle);

        private readonly AudioClient _audioClient;
        private readonly WaveFormat _mixFormat;
        private readonly WaveFormat _outputFormat;
        private readonly long _bufferDuration;
        private readonly int _bufferMilliseconds;
        private readonly int _devicePeriodFrames;
        private readonly bool _useEventSync;
        private readonly bool _exclusiveMode;
        private readonly string _outputFormatSelectionDetails;
        private AudioRenderClient _renderClient;
        private EventWaitHandle _renderEvent;
        private IWaveProvider _provider;
        private byte[] _readBuffer = Array.Empty<byte>();
        private Thread _renderThread;
        private int _bufferFrameCount;
        private volatile bool _isPlaying;
        private bool _initialized;
        private bool _started;
        private int _alignedBufferFrameCount;
        private bool _oneMillisecondTimerActive;

        public WasapiRenderOutput(
            MMDevice endpoint,
            int bufferMilliseconds,
            bool useEventSync,
            bool exclusiveMode,
            WaveFormat sourceFormat,
            long alignedBufferDuration = 0,
            int exclusiveFormatIndex = 0)
        {
            if (endpoint == null)
            {
                throw new ArgumentNullException(nameof(endpoint));
            }

            if (bufferMilliseconds <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(bufferMilliseconds));
            }

            _audioClient = endpoint.AudioClient;
            try
            {
                _mixFormat = _audioClient.MixFormat;
                _exclusiveMode = exclusiveMode;
                _useEventSync = useEventSync;
                _outputFormat = exclusiveMode
                    ? SelectExclusiveFormat(_audioClient, sourceFormat, _mixFormat,
                        exclusiveFormatIndex)
                    : _mixFormat;
                _outputFormatSelectionDetails = exclusiveMode
                    ? "supported by the endpoint in exclusive mode"
                    : "the endpoint mix format serialized for native WASAPI initialization";
                long requestedDuration = exclusiveMode
                    ? Math.Max(bufferMilliseconds * 10000L, _audioClient.MinimumDevicePeriod)
                    : bufferMilliseconds * 10000L;
                _bufferDuration = alignedBufferDuration > 0
                    ? alignedBufferDuration : requestedDuration;
                _bufferMilliseconds = Math.Max(1, (int)Math.Ceiling(_bufferDuration / 10000.0));
                // A polling exclusive client passes periodicity=0 to Initialize,
                // so Windows uses the endpoint's default period.
                long period = exclusiveMode && useEventSync
                    ? _bufferDuration : _audioClient.DefaultDevicePeriod;
                _devicePeriodFrames = Math.Max(1, (int)Math.Ceiling(
                    _outputFormat.SampleRate * period / 10000000.0));
            }
            catch
            {
                _audioClient.Dispose();
                throw;
            }
        }

        public WaveFormat MixFormat => _mixFormat;
        public WaveFormat OutputFormat => _outputFormat;
        public string OutputFormatSelectionDetails => _outputFormatSelectionDetails;
        public int BufferFrameCount => _bufferFrameCount;
        public int TargetPaddingFrames => GetTargetPadding();
        public int DevicePeriodFrames => _devicePeriodFrames;
        public int AlignedBufferFrameCount => _alignedBufferFrameCount;
        public bool ExclusiveMode => _exclusiveMode;
        public bool UsesEventSynchronization => _useEventSync;
        public bool IsPlaying => _isPlaying;

        public event Action<object, Exception> PlaybackFailed;

        public void Initialize(IWaveProvider provider)
        {
            if (provider == null)
            {
                throw new ArgumentNullException(nameof(provider));
            }

            if (!ReferenceEquals(provider.WaveFormat, _outputFormat))
            {
                throw new ArgumentException(
                    "The provider must use the exact selected WASAPI stream format.",
                    nameof(provider));
            }

            _provider = provider;

            try
            {
                InitializeAudioClient(
                    _audioClient,
                    _outputFormat,
                    _exclusiveMode ? AudioClientShareMode.Exclusive : AudioClientShareMode.Shared,
                    _useEventSync ? AudioClientStreamFlags.EventCallback : AudioClientStreamFlags.None,
                    _bufferDuration,
                    _exclusiveMode && _useEventSync ? _bufferDuration : 0);
            }
            catch (COMException ex) when (_exclusiveMode && _useEventSync &&
                ex.HResult == BufferSizeNotAligned)
            {
                // Windows reports the aligned frame count on the failed client.
                // Its replacement must be a newly activated IAudioClient.
                _alignedBufferFrameCount = _audioClient.BufferSize;
                throw;
            }

            if (_useEventSync)
            {
                _renderEvent = new EventWaitHandle(false, EventResetMode.AutoReset);
                _audioClient.SetEventHandle(_renderEvent.SafeWaitHandle.DangerousGetHandle());
            }

            _bufferFrameCount = _audioClient.BufferSize;
            _renderClient = _audioClient.AudioRenderClient;
            _readBuffer = new byte[_bufferFrameCount * _outputFormat.BlockAlign];
            _initialized = true;
        }

        private static void InitializeAudioClient(
            AudioClient audioClient,
            WaveFormat waveFormat,
            AudioClientShareMode shareMode,
            AudioClientStreamFlags streamFlags,
            long bufferDuration,
            long periodicity)
        {
            // NAudio 2.3's COM interface accepts a managed WaveFormat class.
            // Unity's Mono runtime can marshal it differently from desktop .NET.
            // Serialize the complete WAVEFORMATEX(TENSIBLE) and pass its pointer
            // through the already activated NAudio IAudioClient instead.
            byte[] serializedFormat = SerializeFormat(waveFormat);
            int formatLength = serializedFormat.Length - sizeof(int);
            ModEntryPoint.Logger?.Log("WASAPI native Initialize: " + formatLength +
                " format bytes, mode " + shareMode + ", flags " + streamFlags +
                ", duration " + bufferDuration + ", periodicity " + periodicity + ".");

            IntPtr formatPointer = IntPtr.Zero;
            IntPtr clientPointer = IntPtr.Zero;
            try
            {
                formatPointer = Marshal.AllocHGlobal(formatLength);
                Marshal.Copy(serializedFormat, sizeof(int), formatPointer, formatLength);

                clientPointer = GetNativeClientPointer(audioClient);
                ModEntryPoint.Logger?.Log("WASAPI native IAudioClient pointer acquired.");

                IntPtr vtable = Marshal.ReadIntPtr(clientPointer);
                IntPtr initializeMethod = Marshal.ReadIntPtr(vtable, 3 * IntPtr.Size);
                InitializeAudioClientDelegate initialize =
                    (InitializeAudioClientDelegate)Marshal.GetDelegateForFunctionPointer(
                        initializeMethod, typeof(InitializeAudioClientDelegate));

                Guid sessionId = Guid.Empty;
                int result = initialize(
                    clientPointer,
                    shareMode,
                    streamFlags,
                    bufferDuration,
                    periodicity,
                    formatPointer,
                    ref sessionId);
                ModEntryPoint.Logger?.Log("WASAPI native Initialize returned HRESULT 0x" +
                    result.ToString("X8") + ".");
                Marshal.ThrowExceptionForHR(result);
            }
            finally
            {
                if (clientPointer != IntPtr.Zero)
                {
                    Marshal.Release(clientPointer);
                }
                if (formatPointer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(formatPointer);
                }
            }
        }

        private static byte[] SerializeFormat(WaveFormat waveFormat)
        {
            using (MemoryStream stream = new MemoryStream())
            {
                using (BinaryWriter writer = new BinaryWriter(stream))
                {
                    waveFormat.Serialize(writer);
                    byte[] serializedFormat = stream.ToArray();
                    int formatLength = BitConverter.ToInt32(serializedFormat, 0);
                    if (formatLength != serializedFormat.Length - sizeof(int))
                    {
                        throw new InvalidOperationException(
                            "The WASAPI wave format could not be serialized.");
                    }
                    return serializedFormat;
                }
            }
        }

        private static IntPtr GetNativeClientPointer(AudioClient audioClient)
        {
            FieldInfo nativeClientField = typeof(AudioClient).GetField(
                "audioClientInterface", BindingFlags.Instance | BindingFlags.NonPublic);
            if (nativeClientField == null)
            {
                throw new NotSupportedException("The NAudio audio client layout has changed.");
            }

            object nativeClient = nativeClientField.GetValue(audioClient);
            if (nativeClient == null)
            {
                throw new InvalidOperationException("The WASAPI audio client is unavailable.");
            }

            IntPtr unknownPointer = Marshal.GetIUnknownForObject(nativeClient);
            try
            {
                Guid clientId = AudioClientInterfaceId;
                IntPtr clientPointer;
                Marshal.ThrowExceptionForHR(Marshal.QueryInterface(
                    unknownPointer, ref clientId, out clientPointer));
                return clientPointer;
            }
            finally
            {
                Marshal.Release(unknownPointer);
            }
        }

        private static bool IsExclusiveFormatSupported(AudioClient audioClient, WaveFormat waveFormat)
        {
            byte[] serializedFormat = SerializeFormat(waveFormat);
            int formatLength = serializedFormat.Length - sizeof(int);
            IntPtr formatPointer = IntPtr.Zero;
            IntPtr clientPointer = IntPtr.Zero;
            try
            {
                formatPointer = Marshal.AllocHGlobal(formatLength);
                Marshal.Copy(serializedFormat, sizeof(int), formatPointer, formatLength);
                clientPointer = GetNativeClientPointer(audioClient);
                IntPtr vtable = Marshal.ReadIntPtr(clientPointer);
                IntPtr method = Marshal.ReadIntPtr(vtable, 7 * IntPtr.Size);
                IsFormatSupportedDelegate probe =
                    (IsFormatSupportedDelegate)Marshal.GetDelegateForFunctionPointer(
                        method, typeof(IsFormatSupportedDelegate));
                int result = probe(
                    clientPointer, AudioClientShareMode.Exclusive, formatPointer, IntPtr.Zero);
                if (result == 0)
                {
                    return true;
                }
                if (result == unchecked((int)0x88890008))
                {
                    return false;
                }
                Marshal.ThrowExceptionForHR(result);
                return false;
            }
            finally
            {
                if (clientPointer != IntPtr.Zero)
                {
                    Marshal.Release(clientPointer);
                }
                if (formatPointer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(formatPointer);
                }
            }
        }

        private static WaveFormat SelectExclusiveFormat(
            AudioClient audioClient, WaveFormat sourceFormat, WaveFormat mixFormat,
            int formatIndex)
        {
            if (sourceFormat == null)
            {
                throw new ArgumentNullException(nameof(sourceFormat));
            }
            if (formatIndex < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(formatIndex));
            }

            List<WaveFormat> candidates = new List<WaveFormat>();
            AddExclusiveCandidates(candidates, sourceFormat.SampleRate, sourceFormat.Channels);
            AddExclusiveCandidates(candidates, mixFormat.SampleRate, sourceFormat.Channels);
            AddExclusiveCandidates(candidates, mixFormat.SampleRate, 2);
            AddExclusiveCandidates(candidates, 48000, 2);
            AddExclusiveCandidates(candidates, 44100, 2);
            candidates.Add(mixFormat);
            AddExclusiveCandidates(candidates, mixFormat.SampleRate, mixFormat.Channels);

            HashSet<string> checkedFormats = new HashSet<string>();
            foreach (WaveFormat candidate in candidates)
            {
                string key = candidate.GetType().FullName + ":" + candidate;
                if (checkedFormats.Add(key) && IsExclusiveFormatSupported(audioClient, candidate))
                {
                    if (formatIndex-- == 0)
                    {
                        return candidate;
                    }
                }
            }

            throw new NotSupportedException(
                "The selected endpoint did not support any tested WASAPI exclusive-mode " +
                "PCM or floating-point stream format.");
        }

        private static void AddExclusiveCandidates(
            List<WaveFormat> candidates, int sampleRate, int channels)
        {
            if (sampleRate <= 0 || channels <= 0 || channels > 8)
            {
                return;
            }

            candidates.Add(WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels));
            candidates.Add(new WaveFormatExtensible(sampleRate, 32, channels, 32));
            candidates.Add(new WaveFormat(sampleRate, 16, channels));
            candidates.Add(new WaveFormatExtensible(sampleRate, 16, channels, 16));
            candidates.Add(new WaveFormat(sampleRate, 24, channels));
            candidates.Add(new WaveFormatExtensible(sampleRate, 24, channels, 24));
            candidates.Add(new WaveFormat(sampleRate, 32, channels));
        }

        public void Start()
        {
            if (!_initialized || _provider == null)
            {
                throw new InvalidOperationException("The WASAPI output has not been initialized.");
            }

            if (_isPlaying)
            {
                return;
            }

            FillBuffer(GetTargetPadding());
            _audioClient.Start();
            _started = true;
            _isPlaying = true;
            if (_renderEvent == null)
            {
                _oneMillisecondTimerActive = timeBeginPeriod(1) == 0;
            }

            _renderThread = new Thread(RenderLoop)
            {
                IsBackground = true,
                Name = "LLADOFAI WASAPI renderer",
                Priority = ThreadPriority.AboveNormal
            };
            _renderThread.Start();
        }

        private void RenderLoop()
        {
            IntPtr mmcssHandle = IntPtr.Zero;
            try
            {
                if (_useEventSync || _exclusiveMode)
                {
                    uint taskIndex = 0;
                    string taskName = _exclusiveMode ? "Pro Audio" : "Audio";
                    mmcssHandle = AvSetMmThreadCharacteristics(taskName, ref taskIndex);
                    if (mmcssHandle == IntPtr.Zero)
                    {
                        ModEntryPoint.Logger?.Log("WASAPI renderer could not join the " +
                            taskName + " scheduler task.");
                    }
                }

                while (_isPlaying)
                {
                    bool eventSignaled = true;
                    if (_renderEvent != null)
                    {
                        eventSignaled = _renderEvent.WaitOne(_bufferMilliseconds * 3);
                    }
                    else
                    {
                        Thread.Sleep(1);
                    }

                    if (!_isPlaying)
                    {
                        break;
                    }

                    if (_exclusiveMode && _renderEvent != null && !eventSignaled)
                    {
                        // Exclusive event mode accepts one full packet only when
                        // the endpoint signals that its other buffer is ready.
                        continue;
                    }

                    int framesAvailable;
                    if (_exclusiveMode && _renderEvent != null)
                    {
                        framesAvailable = _bufferFrameCount;
                    }
                    else
                    {
                        int padding = _audioClient.CurrentPadding;
                        framesAvailable = Math.Max(0,
                            GetTargetPadding() - padding);
                        framesAvailable = Math.Min(framesAvailable,
                            _bufferFrameCount - padding);
                    }
                    if (framesAvailable > 0)
                    {
                        FillBuffer(framesAvailable);
                    }

                }
            }
            catch (Exception ex)
            {
                _isPlaying = false;
                PlaybackFailed?.Invoke(this, ex);
            }
            finally
            {
                if (mmcssHandle != IntPtr.Zero)
                {
                    AvRevertMmThreadCharacteristics(mmcssHandle);
                }
            }
        }

        private int GetTargetPadding()
        {
            if (_exclusiveMode && !_useEventSync)
            {
                // Push mode can allocate far more capacity than requested.
                // Actual latency follows queued padding, not total capacity.
                return Math.Min(_bufferFrameCount,
                    _devicePeriodFrames * 2);
            }

            if (!_exclusiveMode && _useEventSync)
            {
                // Shared event notifications arrive once per engine period.
                // Keep one period plus a small scheduling margin queued.
                return Math.Min(_bufferFrameCount,
                    _devicePeriodFrames + Math.Max(1, _outputFormat.SampleRate / 200));
            }

            return _bufferFrameCount;
        }

        private void FillBuffer(int frameCount)
        {
            int byteCount = frameCount * _outputFormat.BlockAlign;
            if (_readBuffer.Length < byteCount)
            {
                Array.Resize(ref _readBuffer, byteCount);
            }

            int bytesRead = _provider.Read(_readBuffer, 0, byteCount);
            bytesRead = Math.Max(0, Math.Min(byteCount, bytesRead));
            if (bytesRead < byteCount)
            {
                Array.Clear(_readBuffer, bytesRead, byteCount - bytesRead);
            }

            IntPtr nativeBuffer = _renderClient.GetBuffer(frameCount);
            bool copied = false;
            try
            {
                Marshal.Copy(_readBuffer, 0, nativeBuffer, byteCount);
                copied = true;
            }
            finally
            {
                _renderClient.ReleaseBuffer(
                    frameCount,
                    copied ? AudioClientBufferFlags.None : AudioClientBufferFlags.Silent);
            }
        }

        public void Stop()
        {
            _isPlaying = false;
            _renderEvent?.Set();

            if (_started)
            {
                try
                {
                    _audioClient.Stop();
                }
                catch
                {
                    // Continue joining and releasing a partially stopped stream.
                }
            }

            Thread renderThread = _renderThread;
            if (renderThread != null && renderThread != Thread.CurrentThread)
            {
                renderThread.Join();
            }
            _renderThread = null;

            if (_oneMillisecondTimerActive)
            {
                timeEndPeriod(1);
                _oneMillisecondTimerActive = false;
            }

            if (_started)
            {
                try
                {
                    _audioClient.Reset();
                }
                catch
                {
                    // The client may already have stopped after a render-thread error.
                }
            }

            _started = false;
        }

        public void Dispose()
        {
            Stop();
            _renderClient?.Dispose();
            _renderClient = null;
            _audioClient?.Dispose();
            _renderEvent?.Dispose();
            _renderEvent = null;
        }
    }
}
