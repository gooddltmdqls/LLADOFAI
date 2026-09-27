using System;
using System.Threading;
using UnityEngine;

namespace LLADOFAI
{
    public class WasapiAudioFilter : MonoBehaviour
    {
        private int _hasReceivedAudio;
        private int _hasReportedChannelMismatch;

        private void OnAudioFilterRead(float[] data, int channels)
        {
            if (ModEntryPoint.IsShuttingDown)
            {
                return;
            }

            AudioOutputBridge bridge = WasapiOutputController.Bridge;
            ModConfiguration configuration = ModConfiguration.instance;

            if (!ModEntryPoint.IsEnabled || bridge == null || data == null || data.Length == 0 ||
                configuration == null || !configuration.wasapiEnabled)
            {
                return;
            }

            if (channels != bridge.WaveFormat.Channels)
            {
                if (Interlocked.Exchange(ref _hasReportedChannelMismatch, 1) == 0)
                {
                    WasapiOutputController.SetCaptureMessage(
                        Localization.Format("captureChannelMismatch",
                            channels, bridge.WaveFormat.Channels));
                }

                return;
            }

            Interlocked.Exchange(ref _hasReportedChannelMismatch, 0);
            if (Interlocked.Exchange(ref _hasReceivedAudio, 1) == 0 ||
                WasapiOutputController.CaptureMessage != Localization.Get("captureConnected"))
            {
                WasapiOutputController.SetCaptureMessage(Localization.Get("captureConnected"));
            }

            bridge.FeedBuffer(data);
            Array.Clear(data, 0, data.Length);
        }
    }
}
