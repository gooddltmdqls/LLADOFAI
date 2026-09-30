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
            if (NAudioHost.IsShuttingDown)
            {
                return;
            }

            AudioOutputBridge bridge = WasapiOutputController.Bridge;
            INAudioConfiguration configuration = NAudioHost.Configuration;

            if (!NAudioHost.IsEnabled || bridge == null || data == null || data.Length == 0 ||
                configuration == null || !configuration.wasapiEnabled)
            {
                return;
            }

            if (channels != bridge.WaveFormat.Channels)
            {
                if (Interlocked.Exchange(ref _hasReportedChannelMismatch, 1) == 0)
                {
                    WasapiOutputController.SetCaptureMessage(
                        NAudioHost.Format("captureChannelMismatch",
                            channels, bridge.WaveFormat.Channels));
                }

                return;
            }

            Interlocked.Exchange(ref _hasReportedChannelMismatch, 0);
            if (Interlocked.Exchange(ref _hasReceivedAudio, 1) == 0 ||
                WasapiOutputController.CaptureMessage != NAudioHost.Get("captureConnected"))
            {
                WasapiOutputController.SetCaptureMessage(NAudioHost.Get("captureConnected"));
            }

            bridge.FeedBuffer(data);
            Array.Clear(data, 0, data.Length);
        }
    }
}
