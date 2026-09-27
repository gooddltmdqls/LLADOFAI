using NAudio.CoreAudioApi;
using System;
using System.Collections.Generic;

namespace LLADOFAI
{
    public class WasapiDeviceManager
    {
        private readonly List<WasapiDeviceInfo> _devices = new List<WasapiDeviceInfo>();

        public string DefaultDeviceId { get; private set; }

        public List<WasapiDeviceInfo> GetDevices()
        {
            return _devices;
        }

        public void LoadDevices()
        {
            _devices.Clear();
            DefaultDeviceId = null;

            try
            {
                using (MMDeviceEnumerator enumerator = new MMDeviceEnumerator())
                {
                    try
                    {
                        using (MMDevice defaultDevice = enumerator.GetDefaultAudioEndpoint(
                            DataFlow.Render,
                            Role.Multimedia))
                        {
                            DefaultDeviceId = defaultDevice.ID;
                        }
                    }
                    catch (Exception ex)
                    {
                        ModEntryPoint.Logger.Log("Unable to get the default WASAPI output device: " + ex.Message);
                    }

                    MMDeviceCollection endpoints = enumerator.EnumerateAudioEndPoints(
                        DataFlow.Render,
                        DeviceState.Active);

                    for (int i = 0; i < endpoints.Count; i++)
                    {
                        using (MMDevice endpoint = endpoints[i])
                        {
                            _devices.Add(new WasapiDeviceInfo(endpoint.ID, endpoint.FriendlyName));
                        }
                    }
                }

                ModEntryPoint.Logger.Log($"Found {_devices.Count} active WASAPI output devices.");
            }
            catch (Exception ex)
            {
                _devices.Clear();
                ModEntryPoint.Logger.Error("Unable to enumerate WASAPI output devices: " + ex.Message);
            }
        }
    }
}
