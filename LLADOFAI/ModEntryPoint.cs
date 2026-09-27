using HarmonyLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityModManagerNet;

namespace LLADOFAI
{
    public static class ModEntryPoint
    {
        public static AsioDriverManager asioManager;
        public static WasapiDeviceManager wasapiManager;
        public static ModConfiguration modConfiguration;
        public static UnityModManager.ModEntry.ModLogger Logger;
        public static Harmony harmony;
        public static bool IsEnabled = false;
        public static volatile bool IsShuttingDown = false;
        public static bool ASIODriverChanged = false;
        public static bool UseASIOChanged = false;
        public static bool WASAPIDeviceChanged = false;
        public static bool WASAPIModeChanged = false;
        public static bool UseWASAPIChanged = false;

        private static Texture2D _asioWatermarkTexture;
        private static GameObject _asioObj;
        private static bool _applyLowLatencyDspAtStartup;
        private static AudioOutputBridge _rateMeasuredBridge;
        private static long _rateMeasuredTimestamp;
        private static long _lastFedFrameCount;
        private static long _lastRequestedOutputFrameCount;
        private static double _fedFramesPerSecond;
        private static double _outputFramesPerSecond;
        private static double _maximumFeedGapMilliseconds;
        private static double _maximumReadGapMilliseconds;

        public static void Setup(UnityModManager.ModEntry modEntry)
        {
            IsShuttingDown = false;
            Logger = modEntry.Logger;
            asioManager = new AsioDriverManager();
            wasapiManager = new WasapiDeviceManager();
            modConfiguration = ModConfiguration.Load<ModConfiguration>(modEntry);

            if (modConfiguration.asioEnabled && modConfiguration.wasapiEnabled)
            {
                modConfiguration.wasapiEnabled = false;
                Logger.Log("ASIO and WASAPI were both enabled; keeping the saved ASIO selection.");
            }

            _applyLowLatencyDspAtStartup =
                (modConfiguration.wasapiEnabled && modConfiguration.wasapiLowLatencyDsp) ||
                (modConfiguration.asioEnabled && modConfiguration.asioLowLatencyDsp);

            LoadWatermarkTexture(modEntry);

            ModConfiguration.instance = modConfiguration;

            modEntry.OnToggle = OnToggle;
            modEntry.OnGUI = OnGUI;
            modEntry.OnSaveGUI = OnSaveGUI;
        }

        private static void LoadWatermarkTexture(UnityModManager.ModEntry modEntry)
        {
            try
            {
                string modPath = modEntry.Path;
                string imagePath = Path.Combine(modPath, "asio_logo.png");

                if (File.Exists(imagePath))
                {
                    using (System.Drawing.Bitmap bmp = new System.Drawing.Bitmap(imagePath))
                    {
                        bmp.RotateFlip(System.Drawing.RotateFlipType.RotateNoneFlipY);

                        var rect = new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height);
                        var data = bmp.LockBits(
                            rect,
                            System.Drawing.Imaging.ImageLockMode.ReadOnly,
                            System.Drawing.Imaging.PixelFormat.Format32bppArgb);

                        try
                        {
                            // Remove the PNG's faint black background pixels.
                            const int BytesPerPixel = 4;
                            const int AlphaChannelOffset = BytesPerPixel - 1;
                            const byte NearTransparentAlpha = 3;

                            int rowLength = bmp.Width * BytesPerPixel;
                            byte[] pixelData = new byte[rowLength * bmp.Height];

                            for (int y = 0; y < bmp.Height; y++)
                            {
                                int rowOffset = y * rowLength;
                                IntPtr row = IntPtr.Add(data.Scan0, y * data.Stride);
                                Marshal.Copy(row, pixelData, rowOffset, rowLength);

                                for (int alphaOffset = rowOffset + AlphaChannelOffset;
                                    alphaOffset < rowOffset + rowLength;
                                    alphaOffset += BytesPerPixel)
                                {
                                    if (pixelData[alphaOffset] <= NearTransparentAlpha)
                                    {
                                        pixelData[alphaOffset] = 0;
                                    }
                                }
                            }

                            _asioWatermarkTexture = new Texture2D(bmp.Width, bmp.Height, TextureFormat.BGRA32, false);
                            _asioWatermarkTexture.LoadRawTextureData(pixelData);
                            _asioWatermarkTexture.Apply();
                        }
                        finally
                        {
                            bmp.UnlockBits(data);
                        }
                    }
                }
                else
                {
                    Logger.Error("Unable to find asio_logo.png.");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error while loading asio_logo.png: {ex.Message}");
            }
        }

        private static bool OnToggle(UnityModManager.ModEntry modEntry, bool value)
        {
            if (value)
            {
                IsShuttingDown = false;
            }

            IsEnabled = value;

            if (value)
            {
                if (_asioObj != null)
                {
                    StopCaptureWatchers(_asioObj);
                    UnityEngine.Object.Destroy(_asioObj);
                    _asioObj = null;
                }

                // Remove filters left by earlier versions that attached directly
                // to game objects containing both an AudioSource and AudioListener.
                RemoveCaptureFilters();

                harmony = new Harmony(modEntry.Info.Id);
                harmony.PatchAll(Assembly.GetExecutingAssembly());

                asioManager.LoadAsioDrivers();
                wasapiManager.LoadDevices();
                SelectDefaultWasapiDeviceIfNeeded();
                if (_applyLowLatencyDspAtStartup &&
                    (IsWASAPISelected() || modConfiguration.asioEnabled))
                {
                    _applyLowLatencyDspAtStartup = false;
                    ApplyLowLatencyDsp();
                }
                ApplyAudioOutput();

                CreateCaptureWatcher();

                Logger.Log("Enabled!");
            }
            else
            {
                // Stop the watcher first so it cannot add another capture filter
                // while the existing listener filters are being removed.
                GameObject captureObject = _asioObj;
                _asioObj = null;
                if (captureObject != null)
                {
                    StopCaptureWatchers(captureObject);
                    UnityEngine.Object.Destroy(captureObject);
                }

                RemoveCaptureFilters();

                AsioAudioFilter.SetDevice(false, null);
                WasapiOutputController.SetDevice(false, null);

                harmony?.UnpatchAll(modEntry.Info.Id);
                harmony = null;

                Logger.Log("Disabled.");
            }

            return true;
        }

        private static void RemoveCaptureFilters()
        {
            AsioAudioFilter[] components = UnityEngine.Object.FindObjectsByType<AsioAudioFilter>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);

            foreach (AsioAudioFilter component in components)
            {
                if (component != null)
                {
                    Logger.Log("Destroyed AsioAudioFilter on " + component.gameObject.name);
                    UnityEngine.Object.Destroy(component);
                }
            }

            WasapiAudioFilter[] wasapiComponents = UnityEngine.Object.FindObjectsByType<WasapiAudioFilter>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);

            foreach (WasapiAudioFilter component in wasapiComponents)
            {
                if (component != null)
                {
                    Logger.Log("Destroyed WasapiAudioFilter on " + component.gameObject.name);
                    UnityEngine.Object.Destroy(component);
                }
            }
        }

        private static bool IsWASAPISelected()
        {
            return modConfiguration.wasapiEnabled && !modConfiguration.asioEnabled;
        }

        private static void StopCaptureWatchers(GameObject captureObject)
        {
            if (captureObject == null)
            {
                return;
            }

            captureObject.GetComponent<AsioAudioCaptureWatcher>()?.StopCapture();
            captureObject.GetComponent<WasapiAudioCaptureWatcher>()?.StopCapture();
        }

        private static void CreateCaptureWatcher()
        {
            bool useWasapi = IsWASAPISelected();
            _asioObj = new GameObject(useWasapi ? "WASAPICapture" : "ASIOCapture");
            UnityEngine.Object.DontDestroyOnLoad(_asioObj);

            if (useWasapi)
            {
                _asioObj.AddComponent<WasapiAudioCaptureWatcher>();
            }
            else
            {
                _asioObj.AddComponent<AsioAudioCaptureWatcher>();
            }
        }

        private static void EnsureCaptureWatcher()
        {
            if (!IsEnabled)
            {
                return;
            }

            bool shouldUseWasapi = IsWASAPISelected();
            if (_asioObj != null &&
                (_asioObj.GetComponent<WasapiAudioCaptureWatcher>() != null) == shouldUseWasapi)
            {
                return;
            }

            if (_asioObj != null)
            {
                StopCaptureWatchers(_asioObj);
                UnityEngine.Object.Destroy(_asioObj);
                _asioObj = null;
                RemoveCaptureFilters();
            }

            CreateCaptureWatcher();
        }

        private static void SelectDefaultWasapiDeviceIfNeeded()
        {
            if (!modConfiguration.wasapiEnabled ||
                !string.IsNullOrEmpty(modConfiguration.wasapiDeviceId))
            {
                return;
            }

            List<WasapiDeviceInfo> devices = wasapiManager.GetDevices();
            if (devices.Count == 0)
            {
                return;
            }

            WasapiDeviceInfo selectedDevice = null;
            foreach (WasapiDeviceInfo device in devices)
            {
                if (device.Id == wasapiManager.DefaultDeviceId)
                {
                    selectedDevice = device;
                    break;
                }
            }

            if (selectedDevice == null)
            {
                selectedDevice = devices[0];
            }

            modConfiguration.wasapiDeviceId = selectedDevice.Id;
            WASAPIDeviceChanged = true;
        }

        private static void ApplyAudioOutput()
        {
            if (IsWASAPISelected())
            {
                AsioAudioFilter.SetDevice(false, null);
                WasapiOutputController.SetDevice(
                    true, modConfiguration.wasapiDeviceId, modConfiguration.wasapiExclusiveMode);
            }
            else
            {
                WasapiOutputController.SetDevice(false, null);
                AsioAudioFilter.SetDevice(modConfiguration.asioEnabled, modConfiguration.audioDeviceName);
            }
        }

        private static void ApplyLowLatencyDsp()
        {
            try
            {
                AudioConfiguration audioConfiguration = AudioSettings.GetConfiguration();
                if (audioConfiguration.dspBufferSize <= 256)
                {
                    return;
                }

                int previousSize = audioConfiguration.dspBufferSize;
                audioConfiguration.dspBufferSize = 256;
                bool applied = AudioSettings.Reset(audioConfiguration);
                int actualSize;
                int bufferCount;
                AudioSettings.GetDSPBufferSize(out actualSize, out bufferCount);
                Logger.Log($"{(IsWASAPISelected() ? "WASAPI" : "ASIO")} " +
                    "low-latency Unity DSP: requested 256 frames from " +
                    $"{previousSize}, actual {actualSize} frames; " +
                    $"AudioSettings.Reset returned {applied}.");
            }
            catch (Exception ex)
            {
                Logger.Error("Unable to apply the low-latency Unity DSP setting: " + ex);
            }
        }

        public static void OnGUI(UnityModManager.ModEntry modEntry)
        {
            List<string> asioDrivers = asioManager.GetAsioDrivers();

            GUILayout.BeginHorizontal();
            GUILayout.Label(Localization.Get("language") + ": ", GUILayout.Width(100));
            int selectedLanguage = Localization.GetSelectedLanguageIndex();
            if (UnityModManager.UI.PopupToggleGroup(
                ref selectedLanguage,
                Localization.GetLanguageOptions(),
                Localization.Get("language")) &&
                selectedLanguage >= 0 && selectedLanguage <= 3)
            {
                modConfiguration.language = selectedLanguage == 1 ? "en"
                    : selectedLanguage == 2 ? "ko"
                    : selectedLanguage == 3 ? "zh-CN"
                    : "auto";
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            bool useASIO = GUILayout.Toggle(
                modConfiguration.asioEnabled,
                Localization.Get("useAsio"),
                GUILayout.Width(100));
            if (useASIO != modConfiguration.asioEnabled)
            {
                modConfiguration.asioEnabled = useASIO;
                UseASIOChanged = true;

                if (useASIO && modConfiguration.wasapiEnabled)
                {
                    modConfiguration.wasapiEnabled = false;
                    UseWASAPIChanged = true;
                }
            }

            bool useWASAPI = GUILayout.Toggle(
                modConfiguration.wasapiEnabled,
                Localization.Get("useWasapi"),
                GUILayout.Width(120));
            if (useWASAPI != modConfiguration.wasapiEnabled)
            {
                modConfiguration.wasapiEnabled = useWASAPI;
                UseWASAPIChanged = true;

                if (useWASAPI && modConfiguration.asioEnabled)
                {
                    modConfiguration.asioEnabled = false;
                    UseASIOChanged = true;
                }
            }
            GUILayout.EndHorizontal();

            SelectDefaultWasapiDeviceIfNeeded();

            if (modConfiguration.asioEnabled)
            {
                int dspBufferLength;
                int dspBufferCount;
                AudioSettings.GetDSPBufferSize(out dspBufferLength, out dspBufferCount);
                GUILayout.Label(Localization.Format("unityDspBlock", dspBufferLength,
                    dspBufferLength * 1000.0 / AudioSettings.outputSampleRate));
                modConfiguration.asioLowLatencyDsp = GUILayout.Toggle(
                    modConfiguration.asioLowLatencyDsp,
                    Localization.Get("requestDsp"));
                int queueSafetyMilliseconds = Math.Max(0,
                    Math.Min(40, modConfiguration.asioQueueSafetyMilliseconds));
                GUILayout.Label(Localization.Format("asioQueueSafety", queueSafetyMilliseconds));
                modConfiguration.asioQueueSafetyMilliseconds = 5 * Mathf.RoundToInt(
                    GUILayout.HorizontalSlider(queueSafetyMilliseconds, 0f, 40f,
                        GUILayout.Width(240f)) / 5f);
                GUILayout.Label(Localization.Get("increaseQueueSafety"));

                if (asioDrivers.Count == 0)
                {
                    GUILayout.Label(Localization.Get("noAsioDrivers"));
                }
                else
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label(Localization.Get("selectAsio"), GUILayout.Width(150));

                    int selectedIndex = string.IsNullOrEmpty(modConfiguration.audioDeviceName)
                        ? -1
                        : asioDrivers.IndexOf(modConfiguration.audioDeviceName);

                    if (!string.IsNullOrEmpty(modConfiguration.audioDeviceName) && selectedIndex == -1)
                    {
                        Logger.Log($"Saved ASIO device '{modConfiguration.audioDeviceName}' not found in available drivers.");
                    }

                    if (UnityModManager.UI.PopupToggleGroup(ref selectedIndex, asioDrivers.ToArray(), Localization.Get("selectAsioPopup")) &&
                        selectedIndex >= 0 && selectedIndex < asioDrivers.Count)
                    {
                        string selectedDriver = asioDrivers[selectedIndex];
                        if (selectedDriver != modConfiguration.audioDeviceName)
                        {
                            modConfiguration.audioDeviceName = selectedDriver;
                            ASIODriverChanged = true;
                            Logger.Log($"Selected ASIO device: {selectedDriver}");
                        }
                    }

                    GUILayout.EndHorizontal();

                    string selectedDeviceName = selectedIndex >= 0 && selectedIndex < asioDrivers.Count
                        ? asioDrivers[selectedIndex]
                        : null;
                    bool deviceIsActive = AsioAudioFilter.IsDeviceActive(selectedDeviceName);
                    if (deviceIsActive && AsioAudioFilter.AsioDevice != null)
                    {
                        int asioFrames = AsioAudioFilter.AsioDevice.FramesPerBuffer;
                        GUILayout.Label(Localization.Format("asioBuffer", asioFrames,
                            asioFrames * 1000.0 / AudioSettings.outputSampleRate));
                    }
                    bool guiWasEnabled = GUI.enabled;
                    GUI.enabled = guiWasEnabled && deviceIsActive;
                    if (GUILayout.Button(Localization.Get("openControlPanel"), GUILayout.Width(180f)))
                    {
                        AsioAudioFilter.OpenControlPanel(selectedDeviceName);
                    }
                    GUI.enabled = guiWasEnabled;

                    if (!deviceIsActive)
                    {
                        GUILayout.Label(Localization.Get("saveToApply"));
                    }
                }
            }

            if (IsWASAPISelected())
            {
                int dspBufferLength;
                int dspBufferCount;
                AudioSettings.GetDSPBufferSize(out dspBufferLength, out dspBufferCount);
                GUILayout.Label(Localization.Format("unityDspBlock", dspBufferLength,
                    dspBufferLength * 1000.0 / AudioSettings.outputSampleRate));
                modConfiguration.wasapiLowLatencyDsp = GUILayout.Toggle(
                    modConfiguration.wasapiLowLatencyDsp,
                    Localization.Get("requestDsp"));
                bool exclusiveMode = GUILayout.Toggle(
                    modConfiguration.wasapiExclusiveMode,
                    Localization.Get("wasapiExclusive"));
                if (exclusiveMode != modConfiguration.wasapiExclusiveMode)
                {
                    modConfiguration.wasapiExclusiveMode = exclusiveMode;
                    WASAPIModeChanged = true;
                }
                if (exclusiveMode)
                {
                    GUILayout.Label(modConfiguration.wasapiDeviceId == wasapiManager.DefaultDeviceId
                        ? Localization.Get("exclusiveDefaultHelp")
                        : Localization.Get("saveToSwitchModes"));
                }

                List<WasapiDeviceInfo> devices = wasapiManager.GetDevices();
                if (devices.Count == 0)
                {
                    GUILayout.Label(Localization.Get("noWasapiDevices"));
                }
                else
                {
                    int selectedIndex = devices.FindIndex(
                        device => device.Id == modConfiguration.wasapiDeviceId);
                    string[] deviceNames = new string[devices.Count];
                    for (int i = 0; i < devices.Count; i++)
                    {
                        deviceNames[i] = devices[i].Name;
                    }

                    GUILayout.BeginHorizontal();
                    GUILayout.Label(Localization.Get("selectWasapi"), GUILayout.Width(150));

                    if (!string.IsNullOrEmpty(modConfiguration.wasapiDeviceId) && selectedIndex < 0)
                    {
                        GUILayout.Label(Localization.Get("savedDeviceUnavailable"));
                    }

                    if (UnityModManager.UI.PopupToggleGroup(
                        ref selectedIndex,
                        deviceNames,
                        Localization.Get("selectWasapiPopup")) &&
                        selectedIndex >= 0 && selectedIndex < devices.Count)
                    {
                        string selectedDeviceId = devices[selectedIndex].Id;
                        if (selectedDeviceId != modConfiguration.wasapiDeviceId)
                        {
                            modConfiguration.wasapiDeviceId = selectedDeviceId;
                            WASAPIDeviceChanged = true;
                            Logger.Log("Selected WASAPI device: " + devices[selectedIndex].Name);
                        }
                    }

                    GUILayout.EndHorizontal();

                    if (!WasapiOutputController.IsDeviceActive(
                        modConfiguration.wasapiDeviceId,
                        modConfiguration.wasapiExclusiveMode) &&
                        !WasapiOutputController.Error)
                    {
                        GUILayout.Label(Localization.Get("saveToApplyWasapi"));
                    }

                    if (WasapiOutputController.Error && GUILayout.Button(Localization.Get("retryWasapi"), GUILayout.Width(220f)))
                    {
                        WasapiOutputController.SetDevice(
                            true,
                            modConfiguration.wasapiDeviceId,
                            modConfiguration.wasapiExclusiveMode);
                    }
                }
            }

            bool wasapiSelected = IsWASAPISelected();
            string outputMessage = wasapiSelected ? WasapiOutputController.Message : AsioAudioFilter.Message;
            bool outputError = wasapiSelected ? WasapiOutputController.Error : AsioAudioFilter.Error;
            if (outputMessage != null)
            {
                GUILayout.Space(10);

                if (outputError)
                {
                    GUI.contentColor = Color.red;
                }
                else
                {
                    GUI.contentColor = Color.white;
                }

                GUILayout.Label(outputMessage, GUILayout.MaxWidth(400f));

                GUI.contentColor = Color.white;
            }

            if (wasapiSelected && !string.IsNullOrEmpty(WasapiOutputController.CaptureMessage))
            {
                GUILayout.Label(WasapiOutputController.CaptureMessage, GUILayout.MaxWidth(400f));
            }

            if (GUILayout.Button(Localization.Get(modConfiguration.showStatistics
                ? "hideStatistics" : "showStatistics"), GUILayout.Width(180f)))
            {
                modConfiguration.showStatistics = !modConfiguration.showStatistics;
            }

            AudioOutputBridge bridge = wasapiSelected ? WasapiOutputController.Bridge : AsioAudioFilter.Bridge;
            if (bridge != null && modConfiguration.showStatistics)
            {
                GUILayout.Label(
                    Localization.Format("bufferStatistics",
                        wasapiSelected ? "WASAPI" : "ASIO",
                        bridge.UnderrunCount,
                        bridge.OverrunCount,
                        bridge.QueuedMilliseconds,
                        bridge.RateCorrectionPpm / 10000.0),
                    GUILayout.MaxWidth(400f));
                if (!wasapiSelected)
                {
                    GUILayout.Label(Localization.Format("asioQueueTarget",
                        bridge.SteadyQueueTargetMilliseconds));
                }
                UpdateAudioRates(bridge);
                if (_rateMeasuredTimestamp != 0 && _fedFramesPerSecond > 0)
                {
                    GUILayout.Label(Localization.Format("audioClock",
                        _fedFramesPerSecond,
                        wasapiSelected ? "WASAPI" : "ASIO",
                        _outputFramesPerSecond),
                        GUILayout.MaxWidth(400f));
                    if (!wasapiSelected)
                    {
                        GUILayout.Label(Localization.Format("largestGap",
                            _maximumFeedGapMilliseconds,
                            _maximumReadGapMilliseconds),
                            GUILayout.MaxWidth(400f));
                    }
                }
            }

            // Show the ASIO-compatible image.
            if (_asioWatermarkTexture == null)
            {
                return;
            }

            Rect watermarkRect = GUILayoutUtility.GetRect(113f, 72f);
            GUI.DrawTexture(watermarkRect, _asioWatermarkTexture, ScaleMode.ScaleToFit, true);
        }

        private static void UpdateAudioRates(AudioOutputBridge bridge)
        {
            long now = DateTime.UtcNow.Ticks;
            long fed = bridge.FedFrameCount;
            long output = bridge.RequestedOutputFrameCount;
            if (!ReferenceEquals(_rateMeasuredBridge, bridge))
            {
                _rateMeasuredBridge = bridge;
                _rateMeasuredTimestamp = now;
                _lastFedFrameCount = fed;
                _lastRequestedOutputFrameCount = output;
                _fedFramesPerSecond = 0;
                _outputFramesPerSecond = 0;
                _maximumFeedGapMilliseconds = 0;
                _maximumReadGapMilliseconds = 0;
                bridge.TakeMaximumFeedGapMilliseconds();
                bridge.TakeMaximumReadGapMilliseconds();
                return;
            }

            double seconds = (now - _rateMeasuredTimestamp) /
                (double)TimeSpan.TicksPerSecond;
            if (seconds < 5.0)
            {
                return;
            }

            _fedFramesPerSecond = (fed - _lastFedFrameCount) / seconds;
            _outputFramesPerSecond = (output - _lastRequestedOutputFrameCount) / seconds;
            _maximumFeedGapMilliseconds = bridge.TakeMaximumFeedGapMilliseconds();
            _maximumReadGapMilliseconds = bridge.TakeMaximumReadGapMilliseconds();
            _rateMeasuredTimestamp = now;
            _lastFedFrameCount = fed;
            _lastRequestedOutputFrameCount = output;
        }

        public static void OnSaveGUI(UnityModManager.ModEntry modEntry)
        {
            Logger.Log("Saving...");

            modConfiguration.Save(modEntry);

            if (ASIODriverChanged || UseASIOChanged || WASAPIDeviceChanged ||
                WASAPIModeChanged || UseWASAPIChanged)
            {
                if (IsEnabled)
                {
                    SelectDefaultWasapiDeviceIfNeeded();
                    ApplyAudioOutput();
                    EnsureCaptureWatcher();
                }

                ASIODriverChanged = false;
                UseASIOChanged = false;
                WASAPIDeviceChanged = false;
                WASAPIModeChanged = false;
                UseWASAPIChanged = false;
            }
        }
    }
}
