using System;
using System.Collections.Generic;
using LLADOFAI.Fmod;
using UnityEngine;
using UnityModManagerNet;

namespace LLADOFAI
{
    // Host side of the experimental FMOD backend: starting/stopping it with the mod
    // and drawing its settings. FMOD settings apply on the next start of the
    // backend (game restart or toggling the mod), never by hot-swapping outputs.
    internal static class FmodEngineHost
    {
        private static readonly string[] OutputNames = { "WASAPI", "ASIO" };

        private static List<FmodDeviceInfo> _devices;
        private static string[] _deviceNames;
        private static FmodOutput _devicesOutput;
        private static string _deviceError;
        private static FmodSettings _started;

        internal static void Enable(UnityModManager.ModEntry modEntry, ModConfiguration configuration)
        {
            _started = SettingsFrom(configuration);
            FmodBackend.Start(_started, modEntry.Path, ModEntryPoint.Logger.Log, ModEntryPoint.Logger.Error);
        }

        internal static void Disable()
        {
            FmodBackend.Stop();
        }

        private static FmodSettings SettingsFrom(ModConfiguration configuration)
        {
            return new FmodSettings
            {
                Output = configuration.fmodOutput == "asio" ? FmodOutput.Asio : FmodOutput.Wasapi,
                DeviceId = configuration.fmodDeviceId,
                DspBufferLength = configuration.fmodDspBufferFrames,
                DspBufferCount = configuration.fmodDspBufferCount,
            };
        }

        private static bool Differs(FmodSettings a, FmodSettings b)
        {
            return a.Output != b.Output || (a.DeviceId ?? "") != (b.DeviceId ?? "") ||
                a.DspBufferLength != b.DspBufferLength || a.DspBufferCount != b.DspBufferCount;
        }

        internal static void OnGUI(UnityModManager.ModEntry modEntry, ModConfiguration configuration)
        {
            FmodOutput output = configuration.fmodOutput == "asio" ? FmodOutput.Asio : FmodOutput.Wasapi;
            GUILayout.BeginHorizontal();
            GUILayout.Label(Localization.Get("fmodOutputApi"), GUILayout.Width(150));
            int selectedOutput = GUILayout.Toolbar((int)output, OutputNames, GUILayout.Width(220));
            GUILayout.EndHorizontal();
            if (selectedOutput != (int)output)
            {
                output = (FmodOutput)selectedOutput;
                configuration.fmodOutput = output == FmodOutput.Asio ? "asio" : "wasapi";
                configuration.fmodDeviceId = null;
            }

            DrawDevices(modEntry, configuration, output);
            DrawBuffers(configuration);
            DrawStatus(configuration);
        }

        private static void DrawDevices(UnityModManager.ModEntry modEntry, ModConfiguration configuration, FmodOutput output)
        {
            if (_devices == null || _devicesOutput != output) RefreshDevices(modEntry, output);

            if (_deviceError != null)
            {
                GUILayout.Label(Localization.Format("fmodDeviceError", _deviceError), GUILayout.MaxWidth(400f));
            }
            else if (_devices.Count == 0)
            {
                GUILayout.Label(Localization.Format("fmodNoDevices", OutputNames[(int)output]));
            }
            else
            {
                int selected = 0;
                for (int i = 0; i < _devices.Count; i++)
                    if (_devices[i].Id == configuration.fmodDeviceId) selected = i + 1;
                _deviceNames[0] = Localization.Get("fmodDefaultDevice");

                GUILayout.BeginHorizontal();
                GUILayout.Label(Localization.Get("fmodDevice"), GUILayout.Width(150));
                int next = selected;
                if (UnityModManager.UI.PopupToggleGroup(ref next, _deviceNames, Localization.Get("fmodDevicePopup")) &&
                    next >= 0 && next <= _devices.Count && next != selected)
                {
                    configuration.fmodDeviceId = next == 0 ? null : _devices[next - 1].Id;
                    ModEntryPoint.Logger.Log("Selected FMOD output: " + _deviceNames[next]);
                }
                GUILayout.EndHorizontal();

                if (!string.IsNullOrEmpty(configuration.fmodDeviceId) && selected == 0)
                    GUILayout.Label(Localization.Get("fmodSavedDeviceUnavailable"), GUILayout.MaxWidth(400f));
            }

            if (GUILayout.Button(Localization.Get("fmodRefreshDevices"), GUILayout.Width(180f)))
                RefreshDevices(modEntry, output);
        }

        private static void RefreshDevices(UnityModManager.ModEntry modEntry, FmodOutput output)
        {
            _devices = FmodDevices.Enumerate(output, modEntry.Path, out _deviceError);
            _devicesOutput = output;
            _deviceNames = new string[_devices.Count + 1];
            for (int i = 0; i < _devices.Count; i++) _deviceNames[i + 1] = _devices[i].Name;
            if (_deviceError != null) ModEntryPoint.Logger.Log("FMOD device list unavailable: " + _deviceError);
        }

        private static void DrawBuffers(ModConfiguration configuration)
        {
            int rate = FmodBackend.SampleRate > 0 ? FmodBackend.SampleRate : 48000;
            int[] lengths = FmodSettings.BufferLengths;
            string[] lengthLabels = new string[lengths.Length];
            int lengthIndex = Array.IndexOf(lengths, configuration.fmodDspBufferFrames);
            for (int i = 0; i < lengths.Length; i++)
                lengthLabels[i] = Localization.Format("fmodBufferFrames", lengths[i], lengths[i] * 1000.0 / rate);

            GUILayout.BeginHorizontal();
            GUILayout.Label(Localization.Get("fmodBufferSize"), GUILayout.Width(150));
            int nextLength = lengthIndex < 0 ? Array.IndexOf(lengths, FmodSettings.DefaultBufferLength) : lengthIndex;
            if (UnityModManager.UI.PopupToggleGroup(ref nextLength, lengthLabels, Localization.Get("fmodBufferSizePopup")) &&
                nextLength >= 0 && nextLength < lengths.Length)
                configuration.fmodDspBufferFrames = lengths[nextLength];
            GUILayout.EndHorizontal();

            int countOptions = FmodSettings.MaxBufferCount - FmodSettings.MinBufferCount + 1;
            string[] countLabels = new string[countOptions];
            for (int i = 0; i < countOptions; i++) countLabels[i] = (FmodSettings.MinBufferCount + i).ToString();
            GUILayout.BeginHorizontal();
            GUILayout.Label(Localization.Get("fmodBufferCount"), GUILayout.Width(150));
            int countIndex = Mathf.Clamp(configuration.fmodDspBufferCount, FmodSettings.MinBufferCount,
                FmodSettings.MaxBufferCount) - FmodSettings.MinBufferCount;
            if (UnityModManager.UI.PopupToggleGroup(ref countIndex, countLabels, Localization.Get("fmodBufferCountPopup")) &&
                countIndex >= 0 && countIndex < countOptions)
                configuration.fmodDspBufferCount = FmodSettings.MinBufferCount + countIndex;
            GUILayout.EndHorizontal();

            if (configuration.fmodDspBufferFrames < FmodSettings.SmallBufferLength)
                GUILayout.Label(Localization.Get("fmodSmallBuffer"), GUILayout.MaxWidth(400f));
            GUILayout.Label(Localization.Format("fmodLatency",
                configuration.fmodDspBufferFrames * configuration.fmodDspBufferCount * 1000.0 / rate), GUILayout.MaxWidth(400f));
        }

        private static void DrawStatus(ModConfiguration configuration)
        {
            GUILayout.Space(10);
            switch (FmodBackend.State)
            {
                case FmodBackendState.Running:
                    GUILayout.Label(Localization.Format("fmodRunning", FmodBackend.RuntimeVersion,
                        _started == null ? "" : OutputNames[(int)_started.Output], FmodBackend.DriverName,
                        FmodBackend.SampleRate, FmodBackend.BufferLength, FmodBackend.BufferCount,
                        FmodBackend.BufferLength * FmodBackend.BufferCount * 1000.0 / Math.Max(1, FmodBackend.SampleRate)),
                        GUILayout.MaxWidth(400f));
                    break;
                case FmodBackendState.Failed:
                    GUI.contentColor = Color.red;
                    GUILayout.Label(Localization.Format("fmodFailed", FmodBackend.LastError), GUILayout.MaxWidth(400f));
                    GUI.contentColor = Color.white;
                    break;
                default:
                    GUILayout.Label(Localization.Get("fmodStopped"), GUILayout.MaxWidth(400f));
                    break;
            }

            if (FmodBackend.MixerOverloaded)
            {
                GUI.contentColor = Color.red;
                GUILayout.Label(Localization.Format("fmodOverloaded", FmodBackend.Statistics.MixerCpu), GUILayout.MaxWidth(400f));
                GUI.contentColor = Color.white;
            }

            if (_started == null || Differs(_started, SettingsFrom(configuration)))
                GUILayout.Label(Localization.Get("fmodApplyOnRestart"), GUILayout.MaxWidth(400f));

            if (GUILayout.Button(Localization.Get(configuration.showStatistics ? "hideStatistics" : "showStatistics"), GUILayout.Width(180f)))
                configuration.showStatistics = !configuration.showStatistics;
            if (configuration.showStatistics && FmodBackend.State == FmodBackendState.Running)
            {
                FmodStatistics stats = FmodBackend.Statistics;
                GUILayout.Label(Localization.Format("fmodStatistics", stats.Channels, stats.RealChannels, stats.Sources,
                    stats.Sounds, stats.SoundBytes / (1024.0 * 1024.0), stats.FallbackPlays, stats.MixerCpu), GUILayout.MaxWidth(400f));
            }
        }
    }
}
