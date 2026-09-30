using NAudio.CoreAudioApi;
using NAudio.Wave;
using System;
using UnityEngine;

namespace LLADOFAI
{
    public static class WasapiOutputController
    {
        private const int SharedModeBufferMilliseconds = 20;
        // Reserve at least 20 ms in the endpoint for the polling target.
        // Some endpoints signal exclusive events too slowly.
        private const int ExclusiveModeBufferMilliseconds = 20;
        private const int BufferSizeNotAligned = unchecked((int)0x88890019);
        private const int UnsupportedFormat = unchecked((int)0x88890008);
        private const int EndpointCreateFailed = unchecked((int)0x8889000F);
        private const int DeviceInUse = unchecked((int)0x8889000A);
        private const int ExclusiveModeNotAllowed = unchecked((int)0x8889000E);
        private static readonly object _initLock = new object();
        private static MMDevice _activeEndpoint;
        private static string _currentDeviceId = "";
        private static bool _currentExclusiveMode;
        private static volatile string _message;
        private static volatile string _captureMessage;
        private static volatile bool _error;

        public static AudioOutputBridge Bridge { get; private set; }
        public static WasapiRenderOutput Device { get; private set; }
        public static string Message => _message;
        public static string CaptureMessage => _captureMessage;
        public static bool Error => _error;

        public static void SetCaptureMessage(string message)
        {
            _captureMessage = message;
        }

        public static bool IsDeviceActive(string deviceId, bool exclusiveMode)
        {
            lock (_initLock)
            {
                return !_error && _currentDeviceId == deviceId &&
                    _currentExclusiveMode == exclusiveMode && IsPlaying(Device);
            }
        }

        public static void SetDevice(bool enabled, string deviceId, bool exclusiveMode = false)
        {
            lock (_initLock)
            {
                if (enabled && NAudioHost.IsShuttingDown)
                {
                    return;
                }

                if (!enabled)
                {
                    DisposeDevice();
                    _error = false;
                    _message = null;
                    _captureMessage = null;
                    return;
                }

                if (string.IsNullOrWhiteSpace(deviceId))
                {
                    DisposeDevice();
                    _error = false;
                    _message = NAudioHost.Get("selectWasapiRequired");
                    _captureMessage = null;
                    return;
                }

                if (!_error && _currentDeviceId == deviceId &&
                    _currentExclusiveMode == exclusiveMode && IsPlaying(Device))
                {
                    return;
                }

                DisposeDevice();
                InitDevice(deviceId, exclusiveMode);
            }
        }

        private static void InitDevice(string deviceId, bool exclusiveMode)
        {
            MMDeviceEnumerator enumerator = null;
            MMDevice endpoint = null;
            WasapiRenderOutput output = null;
            string stage = NAudioHost.Get("stageEnumerate");

            try
            {
                _message = NAudioHost.Get("initializingWasapi");
                _captureMessage = NAudioHost.Get("waitingForAudioListener");
                _error = false;

                enumerator = new MMDeviceEnumerator();

                stage = NAudioHost.Get("stageOpenEndpoint");
                endpoint = enumerator.GetDevice(deviceId);

                if (exclusiveMode)
                {
                    stage = NAudioHost.Get("stageCheckDefault");
                    foreach (Role role in new[] { Role.Multimedia, Role.Console })
                    {
                        MMDevice defaultEndpoint = null;
                        try
                        {
                            defaultEndpoint = enumerator.GetDefaultAudioEndpoint(
                                DataFlow.Render, role);
                            if (defaultEndpoint.ID == deviceId)
                            {
                                throw new InvalidOperationException(
                                    NAudioHost.Get("exclusiveEndpointIsDefault"));
                            }
                        }
                        finally
                        {
                            defaultEndpoint?.Dispose();
                        }
                    }
                }

                stage = NAudioHost.Get("stageReadUnity");
                int channels = GetChannelCount(AudioSettings.speakerMode);
                int sampleRate = AudioSettings.outputSampleRate;
                int dspBufferLength;
                int dspBufferCount;
                AudioSettings.GetDSPBufferSize(out dspBufferLength, out dspBufferCount);
                int unityBufferFrames = dspBufferLength * Math.Max(1, dspBufferCount);
                int minimumBufferedFrames = Math.Max(
                    dspBufferLength,
                    Math.Min(unityBufferFrames, sampleRate / 100));

                stage = NAudioHost.Get("stageCreateBridge");
                AudioOutputBridge bridge = new AudioOutputBridge(sampleRate, channels, minimumBufferedFrames);

                stage = NAudioHost.Format("stageCreateOutput",
                    NAudioHost.Get(exclusiveMode ? "wasapiExclusiveMode" : "wasapiSharedMode"));
                bool useEventSync = !exclusiveMode;
                output = new WasapiRenderOutput(
                    endpoint,
                    exclusiveMode ? ExclusiveModeBufferMilliseconds : SharedModeBufferMilliseconds,
                    useEventSync,
                    exclusiveMode,
                    bridge.WaveFormat);

                WaveFormat mixFormat = output.MixFormat;
                WaveFormat streamFormat = output.OutputFormat;
                NAudioHost.Log(
                    $"WASAPI {(exclusiveMode ? "exclusive" : "shared")} endpoint " +
                    $"'{endpoint.FriendlyName}' ({endpoint.ID}) mix format: {mixFormat}; " +
                    $"selected stream format: {streamFormat} ({output.OutputFormatSelectionDetails}); " +
                    $"Unity source format: {bridge.WaveFormat}.");

                stage = NAudioHost.Format("stageAdaptFormat", streamFormat);
                WasapiMixFormatWaveProvider wasapiProvider =
                    new WasapiMixFormatWaveProvider(bridge, streamFormat);

                stage = NAudioHost.Format("stageInitNative", wasapiProvider.WaveFormat);
                Exception eventFailure = null;
                try
                {
                    output.Initialize(wasapiProvider);
                }
                catch (Exception ex)
                {
                    eventFailure = ex;
                }

                if (exclusiveMode && eventFailure != null &&
                    eventFailure.HResult == BufferSizeNotAligned &&
                    output.AlignedBufferFrameCount > 0)
                {
                    int alignedFrames = output.AlignedBufferFrameCount;
                    long alignedDuration = (long)Math.Round(
                        alignedFrames * 10000000.0 / streamFormat.SampleRate);
                    NAudioHost.Log(
                        $"WASAPI exclusive event buffer aligned to {alignedFrames} frames; " +
                        $"retrying with {alignedDuration} reference-time units.");
                    output.Dispose();
                    output = new WasapiRenderOutput(
                        endpoint,
                        ExclusiveModeBufferMilliseconds,
                        true,
                        true,
                        bridge.WaveFormat,
                        alignedDuration);
                    wasapiProvider = new WasapiMixFormatWaveProvider(
                        bridge, output.OutputFormat);
                    eventFailure = null;
                    try
                    {
                        output.Initialize(wasapiProvider);
                    }
                    catch (Exception ex)
                    {
                        eventFailure = ex;
                    }
                }

                int exclusiveFormatIndex = 0;
                while (exclusiveMode && eventFailure != null &&
                    (eventFailure.HResult == UnsupportedFormat ||
                    eventFailure.HResult == EndpointCreateFailed))
                {
                    NAudioHost.Log(
                        $"WASAPI exclusive Initialize rejected {output.OutputFormat} " +
                        $"(HRESULT 0x{eventFailure.HResult:X8}); trying another supported format.");
                    output.Dispose();
                    output = null;
                    exclusiveFormatIndex++;
                    try
                    {
                        output = new WasapiRenderOutput(
                            endpoint,
                            ExclusiveModeBufferMilliseconds,
                            false,
                            true,
                            bridge.WaveFormat,
                            exclusiveFormatIndex: exclusiveFormatIndex);
                    }
                    catch (NotSupportedException)
                    {
                        throw new InvalidOperationException(
                            "Every reported exclusive stream format failed to initialize.",
                            eventFailure);
                    }

                    mixFormat = output.MixFormat;
                    streamFormat = output.OutputFormat;
                    NAudioHost.Log(
                        $"WASAPI exclusive retry format: {streamFormat} " +
                        $"({output.OutputFormatSelectionDetails}).");
                    wasapiProvider = new WasapiMixFormatWaveProvider(bridge, streamFormat);
                    stage = NAudioHost.Format("stageRetryExclusive", streamFormat);
                    eventFailure = null;
                    try
                    {
                        output.Initialize(wasapiProvider);
                    }
                    catch (Exception ex)
                    {
                        eventFailure = ex;
                    }
                }

                if (eventFailure != null)
                {
                    if (!useEventSync ||
                        (exclusiveMode && (eventFailure.HResult == DeviceInUse ||
                        eventFailure.HResult == ExclusiveModeNotAllowed)))
                    {
                        throw eventFailure;
                    }

                    NAudioHost.Log(
                        $"WASAPI {(exclusiveMode ? "exclusive" : "shared")} initialization with " +
                        "event synchronization failed; retrying with polling " +
                        $"({eventFailure.GetType().FullName}, " +
                        $"HRESULT 0x{eventFailure.HResult:X8}): {eventFailure}");
                    output.Dispose();
                    output = new WasapiRenderOutput(
                        endpoint,
                        exclusiveMode ? ExclusiveModeBufferMilliseconds : SharedModeBufferMilliseconds,
                        false,
                        exclusiveMode,
                        bridge.WaveFormat);
                    mixFormat = output.MixFormat;
                    streamFormat = output.OutputFormat;
                    NAudioHost.Log(
                        $"WASAPI {(exclusiveMode ? "exclusive" : "shared")} polling retry uses " +
                        $"mix format {mixFormat} and stream format {streamFormat} " +
                        $"({output.OutputFormatSelectionDetails}).");
                    wasapiProvider = new WasapiMixFormatWaveProvider(bridge, streamFormat);
                    stage = NAudioHost.Format("stagePolling", wasapiProvider.WaveFormat);
                    output.Initialize(wasapiProvider);
                }

                // WASAPI buffer counts use the selected endpoint's sample rate;
                // the bridge's queue counts use Unity's sample rate.
                int targetPaddingSourceFrames = (int)Math.Ceiling(
                    output.TargetPaddingFrames * (double)sampleRate /
                    output.OutputFormat.SampleRate);
                int devicePeriodSourceFrames = (int)Math.Ceiling(
                    output.DevicePeriodFrames * (double)sampleRate /
                    output.OutputFormat.SampleRate);
                int startupBufferFrames = Math.Max(
                    minimumBufferedFrames,
                    dspBufferLength + targetPaddingSourceFrames +
                    (exclusiveMode ? sampleRate / 100 : 0));
                int steadyStateBufferFrames = Math.Max(
                    minimumBufferedFrames,
                    dspBufferLength + (exclusiveMode
                        ? targetPaddingSourceFrames + sampleRate / 200
                        : devicePeriodSourceFrames));
                bridge.SetMinimumBufferedFrames(startupBufferFrames);
                bridge.SetSteadyStateBufferedFrames(
                    Math.Min(startupBufferFrames, steadyStateBufferFrames));
                NAudioHost.Log(
                    $"WASAPI bridge buffer: {startupBufferFrames} frames at startup " +
                    $"({startupBufferFrames * 1000.0 / sampleRate:F1} ms), " +
                    $"{steadyStateBufferFrames} frames after stabilization " +
                    $"({steadyStateBufferFrames * 1000.0 / sampleRate:F1} ms); " +
                    $"Unity block: {dspBufferLength} frames; " +
                    $"WASAPI period: {output.DevicePeriodFrames} frames; " +
                    $"WASAPI target padding: {output.TargetPaddingFrames} frames; " +
                    $"WASAPI buffer capacity: {output.BufferFrameCount} frames.");

                output.PlaybackFailed += OnPlaybackFailed;

                Device = output;
                Bridge = bridge;
                _activeEndpoint = endpoint;
                _currentDeviceId = deviceId;
                _currentExclusiveMode = exclusiveMode;

                _message = NAudioHost.Format("wasapiInitialized",
                    NAudioHost.Get(exclusiveMode ? "wasapiExclusiveMode" : "wasapiSharedMode"),
                    endpoint.FriendlyName);
                _error = false;

                stage = NAudioHost.Get("stageStartPlayback");
                output.Start();

                output = null;
                endpoint = null;

                NAudioHost.Log(_message);
            }
            catch (Exception ex)
            {
                if (ReferenceEquals(Device, output))
                {
                    Device = null;
                    Bridge = null;
                    _activeEndpoint = null;
                    _currentDeviceId = "";
                    _currentExclusiveMode = false;
                }

                try
                {
                    output?.Stop();
                }
                catch
                {
                    // Continue disposing a partially initialized output.
                }

                try
                {
                    output?.Dispose();
                }
                catch
                {
                    // Preserve the initialization error as the reported failure.
                }

                string errorText = string.IsNullOrWhiteSpace(ex.Message)
                    ? NAudioHost.Get("emptyDriverError")
                    : ex.Message;
                if (exclusiveMode && ex.HResult == DeviceInUse)
                {
                    errorText = NAudioHost.Get("exclusiveEndpointInUse");
                }
                else if (exclusiveMode && ex.HResult == ExclusiveModeNotAllowed)
                {
                    errorText = NAudioHost.Get("exclusiveNotAllowed");
                }
                string endpointDescription = endpoint == null
                    ? NAudioHost.Get("selectedEndpoint")
                    : $"'{endpoint.FriendlyName}' ({endpoint.ID})";
                _message = NAudioHost.Format("wasapiInitFailed",
                    endpointDescription, stage, ex.GetType().FullName,
                    $"0x{ex.HResult:X8}", errorText);
                _error = true;
                NAudioHost.Error(_message + Environment.NewLine + ex);
            }
            finally
            {
                try
                {
                    endpoint?.Dispose();
                }
                catch (Exception ex)
                {
                    NAudioHost.Error("Error while releasing the WASAPI endpoint: " + ex);
                }

                try
                {
                    enumerator?.Dispose();
                }
                catch (Exception ex)
                {
                    NAudioHost.Error("Error while releasing the WASAPI enumerator: " + ex);
                }
            }
        }

        private static void OnPlaybackFailed(object sender, Exception exception)
        {
            // This callback runs on the WASAPI render thread. Keep it lock-free.
            if (!ReferenceEquals(Device, sender))
            {
                return;
            }

            _message = NAudioHost.Format("wasapiPlaybackStopped", exception.Message);
            _error = true;
            _captureMessage = NAudioHost.Get("wasapiOutputStopped");
        }

        private static bool IsPlaying(WasapiRenderOutput output)
        {
            try
            {
                return output != null && output.IsPlaying;
            }
            catch
            {
                return false;
            }
        }

        private static void DisposeDevice()
        {
            WasapiRenderOutput output = Device;
            MMDevice endpoint = _activeEndpoint;

            Device = null;
            Bridge = null;
            _activeEndpoint = null;
            _currentDeviceId = "";
            _currentExclusiveMode = false;

            if (output != null)
            {
                try
                {
                    output.Stop();
                }
                catch (Exception ex)
                {
                    NAudioHost.Error("Error while stopping WASAPI output: " + ex.Message);
                }

                try
                {
                    output.Dispose();
                }
                catch (Exception ex)
                {
                    NAudioHost.Error("Error while disposing WASAPI output: " + ex.Message);
                }
            }

            try
            {
                endpoint?.Dispose();
            }
            catch (Exception ex)
            {
                NAudioHost.Error("Error while disposing the WASAPI endpoint: " + ex.Message);
            }
        }

        private static int GetChannelCount(AudioSpeakerMode speakerMode)
        {
            switch (speakerMode)
            {
                case AudioSpeakerMode.Mono:
                    return 1;
                case AudioSpeakerMode.Quad:
                    return 4;
                case AudioSpeakerMode.Surround:
                    return 5;
                case AudioSpeakerMode.Mode5point1:
                    return 6;
                case AudioSpeakerMode.Mode7point1:
                    return 8;
                default:
                    return 2;
            }
        }
    }
}
