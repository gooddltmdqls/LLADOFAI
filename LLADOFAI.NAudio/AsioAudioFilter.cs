using NAudio.Wave;
using System;
using UnityEngine;

namespace LLADOFAI
{
    public class AsioAudioFilter : MonoBehaviour
    {
        public static AudioOutputBridge Bridge;
        public static AsioOut AsioDevice;
        public static string Message = null;
        public static bool Error = false;

        private static string _currentDeviceName = "";
        private static readonly object _initLock = new object();

        private void Awake()
        {
            INAudioConfiguration configuration = NAudioHost.Configuration;
            if (configuration != null && configuration.asioEnabled)
            {
                SetDevice(true, configuration.audioDeviceName);
            }
        }

        private void OnAudioFilterRead(float[] data, int channels)
        {
            if (NAudioHost.IsShuttingDown)
            {
                return;
            }

            AudioOutputBridge bridge = Bridge;
            INAudioConfiguration configuration = NAudioHost.Configuration;

            if (!NAudioHost.IsEnabled || bridge == null || data == null || data.Length == 0 ||
                configuration == null || !configuration.asioEnabled)
            {
                return;
            }

            // The device format must match Unity's interleaved listener stream.
            // If it does not, leave Unity's output intact instead of routing malformed audio.
            if (channels != bridge.WaveFormat.Channels)
            {
                return;
            }

            bridge.FeedBuffer(data);

            // ASIO replaces Unity's normal output while Use ASIO is enabled.
            Array.Clear(data, 0, data.Length);
        }

        public static void UpdateDevice(string newDeviceName)
        {
            INAudioConfiguration configuration = NAudioHost.Configuration;
            SetDevice(configuration != null && configuration.asioEnabled, newDeviceName);
        }

        public static bool IsDeviceActive(string deviceName)
        {
            lock (_initLock)
            {
                return AsioDevice != null && _currentDeviceName == deviceName;
            }
        }

        public static bool OpenControlPanel(string deviceName)
        {
            lock (_initLock)
            {
                if (AsioDevice == null || _currentDeviceName != deviceName)
                {
                    return false;
                }

                try
                {
                    AsioDevice.ShowControlPanel();
                    return true;
                }
                catch (Exception ex)
                {
                    Error = true;
                    Message = NAudioHost.Format("openAsioPanelError", ex.Message);
                    NAudioHost.Error(Message);
                    return false;
                }
            }
        }

        public static void SetDevice(bool enabled, string deviceName)
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
                    Error = false;
                    Message = null;
                    return;
                }

                if (string.IsNullOrWhiteSpace(deviceName))
                {
                    DisposeDevice();
                    Error = false;
                    Message = NAudioHost.Get("selectAsioRequired");
                    return;
                }

                if (AsioDevice != null && _currentDeviceName == deviceName)
                {
                    return;
                }

                DisposeDevice();
                InitAsio(deviceName);
            }
        }

        private static void InitAsio(string deviceName)
        {
            AsioOut device = null;

            try
            {
                Message = NAudioHost.Format("initializingAsio", deviceName);

                device = new AsioOut(deviceName);
                int channels = GetChannelCount(AudioSettings.speakerMode);
                int sampleRate = AudioSettings.outputSampleRate;
                int dspBufferLength;
                int dspBufferCount;
                AudioSettings.GetDSPBufferSize(out dspBufferLength, out dspBufferCount);
                int safetyMilliseconds = Math.Max(0, Math.Min(40,
                    NAudioHost.Configuration?.asioQueueSafetyMilliseconds ?? 10));
                // Keep one fixed latency target for rhythm-game calibration.
                // The clocks still need resampling, but underruns must not move
                // the target during a song.
                AudioOutputBridge bridge = new AudioOutputBridge(
                    sampleRate, channels, dspBufferLength, 0.03, 0.015);

                device.Init(bridge);
                int fixedQueueFrames = Math.Min(65536,
                    Math.Max(dspBufferLength, dspBufferLength +
                        device.FramesPerBuffer + sampleRate * safetyMilliseconds / 1000));
                bridge.SetMinimumBufferedFrames(fixedQueueFrames);
                NAudioHost.Log($"ASIO fixed queue target: {fixedQueueFrames} frames " +
                    $"({fixedQueueFrames * 1000.0 / sampleRate:F1} ms); " +
                    $"Unity block: {dspBufferLength} frames; " +
                    $"ASIO callback: {device.FramesPerBuffer} frames; " +
                    $"safety margin: {safetyMilliseconds} ms; " +
                    "burst tolerance: 30 ms; " +
                    "rate correction limit: 1.5%.");
                device.Play();

                AsioDevice = device;
                Bridge = bridge;
                _currentDeviceName = deviceName;
                Error = false;
                Message = null;

                NAudioHost.Log("Initialized ASIO Driver.");
            }
            catch (Exception ex)
            {
                try
                {
                    device?.Stop();
                }
                catch
                {
                    // Continue disposing a partially initialized driver.
                }

                try
                {
                    device?.Dispose();
                }
                catch
                {
                    // Preserve the initialization error as the reported failure.
                }
                AsioDevice = null;
                Bridge = null;
                _currentDeviceName = "";
                Error = true;
                Message = NAudioHost.Format("initializeAsioError", deviceName, ex.Message);
                NAudioHost.Error(Message);
            }
        }

        private static void DisposeDevice()
        {
            AsioOut device = AsioDevice;

            AsioDevice = null;
            Bridge = null;
            _currentDeviceName = "";

            if (device == null)
            {
                return;
            }

            try
            {
                device.Stop();
            }
            catch (Exception ex)
            {
                NAudioHost.Error($"Error while stopping ASIO device: {ex.Message}");
            }

            try
            {
                device.Dispose();
            }
            catch (Exception ex)
            {
                NAudioHost.Error($"Error while disposing ASIO device: {ex.Message}");
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
