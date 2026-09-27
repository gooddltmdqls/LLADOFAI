using System.Collections.Generic;
using System.Globalization;

namespace LLADOFAI
{
    internal static class Localization
    {
        private static readonly Dictionary<string, string> English = new Dictionary<string, string>
        {
            { "language", "Language" },
            { "languageAuto", "System default" },
            { "useAsio", "Use ASIO" },
            { "useWasapi", "Use WASAPI" },
            { "unityDspBlock", "Unity DSP block: {0} frames ({1:F1} ms)." },
            { "requestDsp", "Request 256-frame Unity DSP at next game launch (lower latency)" },
            { "asioQueueSafety", "ASIO fixed queue safety margin: {0} ms (restart game to apply)" },
            { "increaseQueueSafety", "Increase if ASIO underruns during play; recalibrate audio offset after changing." },
            { "noAsioDrivers", "No ASIO drivers found." },
            { "selectAsio", "Select ASIO device: " },
            { "selectAsioPopup", "Select ASIO Device" },
            { "asioBuffer", "ASIO driver buffer: {0} frames ({1:F1} ms)." },
            { "openControlPanel", "Open control panel" },
            { "saveToApply", "Save settings to apply the selected device first." },
            { "wasapiExclusive", "WASAPI exclusive mode (other apps cannot use this device)" },
            { "exclusiveDefaultHelp", "This is the Windows default output. Set a different default output and restart the game before using exclusive mode." },
            { "saveToSwitchModes", "Save settings to switch WASAPI modes." },
            { "noWasapiDevices", "No active WASAPI output devices found." },
            { "selectWasapi", "Select WASAPI device: " },
            { "selectWasapiPopup", "Select WASAPI Device" },
            { "savedDeviceUnavailable", "Saved device is unavailable." },
            { "saveToApplyWasapi", "Save settings to apply the selected WASAPI device." },
            { "retryWasapi", "Retry WASAPI initialization" },
            { "showStatistics", "Show statistics" },
            { "hideStatistics", "Hide statistics" },
            { "bufferStatistics", "{0} buffer — underruns: {1}, dropped blocks: {2}, queued: {3:F1} ms, speed adjustment: {4:F3}%" },
            { "asioQueueTarget", "ASIO fixed queue target: {0:F1} ms." },
            { "audioClock", "Audio clock: Unity {0:F0} frames/s, {1} {2:F0} frames/s (5 s average)" },
            { "largestGap", "Largest gap (5 s): Unity audio {0:F1} ms, ASIO callback {1:F1} ms." },
            { "selectAsioRequired", "Select an ASIO device to enable ASIO output." },
            { "selectWasapiRequired", "Select a WASAPI output device to enable WASAPI output." },
            { "initializingAsio", "Initializing ASIO device: {0}" },
            { "openAsioPanelError", "Unable to open the ASIO control panel: {0}" },
            { "initializeAsioError", "Unable to initialize ASIO device: {0}\n\nError message: {1}" },
            { "captureChannelMismatch", "Audio channel mismatch: game sent {0}, WASAPI expects {1}." },
            { "captureConnected", "Game audio is reaching the WASAPI output." },
            { "wasapiInitialized", "WASAPI {0} initialized: {1}" },
            { "initializingWasapi", "Initializing WASAPI output device." },
            { "waitingForAudioListener", "Waiting for a game AudioListener." },
            { "wasapiInitFailed", "WASAPI initialization failed for {0} while {1} ({2}, HRESULT {3}): {4}" },
            { "emptyDriverError", "The driver returned an empty error message." },
            { "exclusiveEndpointIsDefault", "The exclusive WASAPI endpoint is the Windows default output. Unity needs a different default output to keep producing game audio. Choose another default output and restart the game." },
            { "exclusiveEndpointInUse", "The endpoint is already in use. If Unity or another app uses this headset as its Windows output, choose a different Windows default output device and restart the game before enabling exclusive mode." },
            { "exclusiveNotAllowed", "Windows has disabled exclusive access for this endpoint." },
            { "selectedEndpoint", "the selected endpoint" },
            { "stageEnumerate", "creating the WASAPI device enumerator" },
            { "stageOpenEndpoint", "opening the selected output endpoint" },
            { "stageCheckDefault", "checking the game's Windows default output" },
            { "stageReadUnity", "reading Unity audio settings" },
            { "stageCreateBridge", "creating the audio bridge" },
            { "stageCreateOutput", "creating the {0} WASAPI output" },
            { "stageAdaptFormat", "adapting Unity audio to the selected WASAPI stream format {0}" },
            { "stageInitNative", "initializing the WASAPI audio client with a native format pointer using {0}" },
            { "stageRetryExclusive", "initializing the WASAPI audio client with another exclusive format {0}" },
            { "stagePolling", "initializing the WASAPI audio client with a native format pointer and polling using {0}" },
            { "stageStartPlayback", "starting WASAPI playback" },
            { "wasapiExclusiveMode", "exclusive" },
            { "wasapiSharedMode", "shared" },
            { "wasapiOutputStopped", "WASAPI output stopped; retry initialization." },
            { "wasapiPlaybackStopped", "WASAPI playback stopped: {0}" }
        };

        private static readonly Dictionary<string, string> Korean = new Dictionary<string, string>
        {
            { "language", "언어" },
            { "languageAuto", "시스템 설정 따르기" },
            { "useAsio", "ASIO 사용" },
            { "useWasapi", "WASAPI 사용" },
            { "unityDspBlock", "게임 소리 처리 단위: {0}개 ({1:F1}밀리초)." },
            { "requestDsp", "다음에 게임을 실행할 때 소리 처리 단위를 256개로 설정 (소리 지연 감소)" },
            { "asioQueueSafety", "ASIO 소리 대기 여유: {0}밀리초 (값을 바꾼 뒤 모드를 껐다 켜세요)" },
            { "increaseQueueSafety", "재생 중 ASIO 소리가 끊기면 값을 높이세요. 변경한 뒤 입력 오프셋을 다시 보정하세요." },
            { "noAsioDrivers", "ASIO 소리 출력용 드라이버를 찾을 수 없습니다." },
            { "selectAsio", "ASIO 기기 선택: " },
            { "selectAsioPopup", "ASIO 기기 선택" },
            { "asioBuffer", "ASIO 소리 처리 단위: {0}개 ({1:F1}밀리초)." },
            { "openControlPanel", "설정 창 열기" },
            { "saveToApply", "선택한 기기를 적용하려면 설정을 저장하세요." },
            { "wasapiExclusive", "WASAPI 단독 사용 (다른 앱은 이 기기를 함께 사용할 수 없음)" },
            { "exclusiveDefaultHelp", "현재 Windows 기본 소리 출력 기기입니다. 단독으로 사용하려면 다른 기기를 기본으로 설정하고 게임을 다시 시작하세요." },
            { "saveToSwitchModes", "WASAPI 사용 방식을 바꾸려면 설정을 저장하세요." },
            { "noWasapiDevices", "사용할 수 있는 WASAPI 소리 출력 기기가 없습니다." },
            { "selectWasapi", "WASAPI 기기 선택: " },
            { "selectWasapiPopup", "WASAPI 기기 선택" },
            { "savedDeviceUnavailable", "저장한 기기를 찾을 수 없습니다." },
            { "saveToApplyWasapi", "선택한 WASAPI 기기를 적용하려면 설정을 저장하세요." },
            { "retryWasapi", "WASAPI 다시 시작" },
            { "showStatistics", "통계 표시" },
            { "hideStatistics", "통계 숨기기" },
            { "bufferStatistics", "{0} 소리 처리 상태 — 끊김: {1}회, 빠진 데이터 묶음: {2}개, 대기 중인 소리: {3:F1}밀리초, 재생 속도 조정: {4:F3}%" },
            { "asioQueueTarget", "ASIO 소리 대기 시간 목표: {0:F1}밀리초." },
            { "audioClock", "초당 처리한 소리 단위: Unity {0:F0}개, {1} {2:F0}개 (최근 5초 평균)" },
            { "largestGap", "가장 긴 처리 지연 (최근 5초): Unity 소리 {0:F1}밀리초, ASIO 요청 {1:F1}밀리초." },
            { "selectAsioRequired", "ASIO 소리를 출력하려면 ASIO 기기를 선택하세요." },
            { "selectWasapiRequired", "WASAPI 소리를 출력하려면 WASAPI 기기를 선택하세요." },
            { "initializingAsio", "ASIO 기기를 연결하는 중: {0}" },
            { "openAsioPanelError", "ASIO 설정 창을 열 수 없습니다: {0}" },
            { "initializeAsioError", "ASIO 기기를 준비할 수 없습니다: {0}\n\n오류 내용: {1}" },
            { "captureChannelMismatch", "게임과 WASAPI가 처리하는 소리 개수가 다릅니다. 게임: {0}, WASAPI: {1}." },
            { "captureConnected", "게임 소리가 WASAPI로 전달되고 있습니다." },
            { "wasapiInitialized", "WASAPI {0} 사용 중: {1}" },
            { "initializingWasapi", "WASAPI 소리 출력을 연결하는 중." },
            { "waitingForAudioListener", "게임의 소리 출력을 기다리는 중." },
            { "wasapiInitFailed", "WASAPI 준비 실패: {0} ({1} 단계, {2}, 오류 코드 {3}) — {4}" },
            { "emptyDriverError", "오류 내용이 전달되지 않았습니다." },
            { "exclusiveEndpointIsDefault", "선택한 WASAPI 기기가 Windows 기본 소리 출력 기기입니다. 게임 소리를 계속 들으려면 Unity가 다른 기본 기기를 사용해야 합니다. Windows 기본 기기를 바꾼 뒤 게임을 다시 시작하세요." },
            { "exclusiveEndpointInUse", "이 기기는 이미 사용 중입니다. Unity나 다른 앱에서 헤드셋을 사용 중이면 Windows 기본 소리 출력 기기를 바꾸고 게임을 다시 시작한 뒤 단독 사용을 켜세요." },
            { "exclusiveNotAllowed", "Windows에서 이 기기의 단독 사용을 허용하지 않습니다." },
            { "selectedEndpoint", "선택한 기기" },
            { "stageEnumerate", "WASAPI 기기 목록을 불러오는 중" },
            { "stageOpenEndpoint", "선택한 소리 출력 기기를 여는 중" },
            { "stageCheckDefault", "게임의 Windows 기본 소리 출력 기기를 확인하는 중" },
            { "stageReadUnity", "Unity 소리 설정을 읽는 중" },
            { "stageCreateBridge", "소리 연결을 연결하는 중" },
            { "stageCreateOutput", "{0} WASAPI 출력을 연결하는 중" },
            { "stageAdaptFormat", "게임 소리를 WASAPI에서 재생할 수 있게 바꾸는 중" },
            { "stageInitNative", "WASAPI 소리 출력을 연결하는 중 ({0})" },
            { "stageRetryExclusive", "다른 포맷으로 다시 연결하는 중" },
            { "stagePolling", "WASAPI 소리 출력을 다시 연결하는 중 ({0})" },
            { "stageStartPlayback", "WASAPI 재생을 시작하는 중" },
            { "wasapiExclusiveMode", "단독 모드" },
            { "wasapiSharedMode", "공유 모드" },
            { "wasapiOutputStopped", "WASAPI 출력이 멈췄습니다. 다시 시도하세요." },
            { "wasapiPlaybackStopped", "WASAPI 재생이 멈췄습니다: {0}" }
        };

        private static readonly Dictionary<string, string> Chinese = new Dictionary<string, string>
        {
            { "language", "语言" },
            { "languageAuto", "跟随系统" },
            { "useAsio", "启用 ASIO" },
            { "useWasapi", "启用 WASAPI" },
            { "unityDspBlock", "Unity DSP 块：{0} 帧（{1:F1} ms）。" },
            { "requestDsp", "下次启动游戏时将 Unity DSP 设为 256 帧（降低延迟）" },
            { "asioQueueSafety", "ASIO 固定队列安全余量：{0} ms（重启游戏后生效）" },
            { "increaseQueueSafety", "播放时发生 ASIO 缓冲区欠载时可增加此值；更改后请重新校准音频偏移。" },
            { "noAsioDrivers", "未找到 ASIO 驱动程序。" },
            { "selectAsio", "选择 ASIO 设备：" },
            { "selectAsioPopup", "选择 ASIO 设备" },
            { "asioBuffer", "ASIO 驱动缓冲区：{0} 帧（{1:F1} ms）。" },
            { "openControlPanel", "打开控制面板" },
            { "saveToApply", "保存设置以应用所选设备。" },
            { "wasapiExclusive", "WASAPI 独占模式（其他应用无法使用此设备）" },
            { "exclusiveDefaultHelp", "这是 Windows 默认输出设备。请先更改默认输出设备并重启游戏，再启用独占模式。" },
            { "saveToSwitchModes", "保存设置以切换 WASAPI 模式。" },
            { "noWasapiDevices", "未找到正在使用的 WASAPI 输出设备。" },
            { "selectWasapi", "选择 WASAPI 设备：" },
            { "selectWasapiPopup", "选择 WASAPI 设备" },
            { "savedDeviceUnavailable", "保存的设备不可用。" },
            { "saveToApplyWasapi", "保存设置以应用所选 WASAPI 设备。" },
            { "retryWasapi", "重试初始化 WASAPI" },
            { "showStatistics", "显示统计信息" },
            { "hideStatistics", "隐藏统计信息" },
            { "bufferStatistics", "{0} 缓冲区 — 欠载：{1}，丢弃块：{2}，排队：{3:F1} ms，速度调整：{4:F3}%" },
            { "asioQueueTarget", "ASIO 固定队列目标：{0:F1} ms。" },
            { "audioClock", "音频时钟：Unity {0:F0} 帧/秒，{1} {2:F0} 帧/秒（5 秒平均）" },
            { "largestGap", "最大间隔（5 秒）：Unity 音频 {0:F1} ms，ASIO 回调 {1:F1} ms。" },
            { "selectAsioRequired", "请选择 ASIO 设备以启用 ASIO 输出。" },
            { "selectWasapiRequired", "请选择 WASAPI 输出设备以启用 WASAPI 输出。" },
            { "initializingAsio", "正在初始化 ASIO 设备：{0}" },
            { "openAsioPanelError", "无法打开 ASIO 控制面板：{0}" },
            { "initializeAsioError", "无法初始化 ASIO 设备：{0}\n\n错误信息：{1}" },
            { "captureChannelMismatch", "音频通道不匹配：游戏发送 {0}，WASAPI 需要 {1}。" },
            { "captureConnected", "游戏音频正在传输到 WASAPI 输出。" },
            { "wasapiInitialized", "WASAPI {0}模式已初始化：{1}" },
            { "initializingWasapi", "正在初始化 WASAPI 输出设备。" },
            { "waitingForAudioListener", "正在等待游戏 AudioListener。" },
            { "wasapiInitFailed", "WASAPI 初始化失败，设备：{0}；阶段：{1}（{2}，HRESULT {3}）：{4}" },
            { "emptyDriverError", "驱动程序返回了空错误信息。" },
            { "exclusiveEndpointIsDefault", "独占 WASAPI 设备是 Windows 默认输出设备。Unity 需要使用其他默认输出设备才能继续输出游戏音频。请选择其他默认输出设备并重启游戏。" },
            { "exclusiveEndpointInUse", "此设备已在使用中。如果 Unity 或其他应用正在使用此耳机作为 Windows 输出，请选择其他 Windows 默认输出设备并重启游戏，然后再启用独占模式。" },
            { "exclusiveNotAllowed", "Windows 已禁用此设备的独占访问权限。" },
            { "selectedEndpoint", "所选设备" },
            { "stageEnumerate", "正在创建 WASAPI 设备枚举器" },
            { "stageOpenEndpoint", "正在打开所选输出设备" },
            { "stageCheckDefault", "正在检查游戏的 Windows 默认输出设备" },
            { "stageReadUnity", "正在读取 Unity 音频设置" },
            { "stageCreateBridge", "正在创建音频桥接" },
            { "stageCreateOutput", "正在创建 {0} WASAPI 输出" },
            { "stageAdaptFormat", "正在将 Unity 音频转换为所选 WASAPI 流格式 {0}" },
            { "stageInitNative", "正在使用本机格式指针初始化 WASAPI 音频客户端（{0}）" },
            { "stageRetryExclusive", "正在使用其他独占格式 {0} 初始化 WASAPI 音频客户端" },
            { "stagePolling", "正在使用本机格式指针和轮询初始化 WASAPI 音频客户端（{0}）" },
            { "stageStartPlayback", "正在启动 WASAPI 播放" },
            { "wasapiExclusiveMode", "独占" },
            { "wasapiSharedMode", "共享" },
            { "wasapiOutputStopped", "WASAPI 输出已停止；请重试初始化。" },
            { "wasapiPlaybackStopped", "WASAPI 播放已停止：{0}" }
        };

        public static string Get(string key)
        {
            string value;
            Dictionary<string, string> strings = GetStrings();
            if (strings.TryGetValue(key, out value))
            {
                return value;
            }

            return English.TryGetValue(key, out value) ? value : key;
        }

        public static string Format(string key, params object[] arguments)
        {
            return string.Format(CultureInfo.CurrentCulture, Get(key), arguments);
        }

        public static string[] GetLanguageOptions()
        {
            return new[] { Get("languageAuto"), "English", "한국어", "简体中文" };
        }

        public static int GetSelectedLanguageIndex()
        {
            string language = ModEntryPoint.modConfiguration == null ? null : ModEntryPoint.modConfiguration.language;
            if (string.Equals(language, "en", System.StringComparison.OrdinalIgnoreCase)) return 1;
            if (string.Equals(language, "ko", System.StringComparison.OrdinalIgnoreCase)) return 2;
            if (string.Equals(language, "zh-CN", System.StringComparison.OrdinalIgnoreCase) ||
                string.Equals(language, "zh", System.StringComparison.OrdinalIgnoreCase)) return 3;
            return 0;
        }

        private static Dictionary<string, string> GetStrings()
        {
            string language = ModEntryPoint.modConfiguration == null ? null : ModEntryPoint.modConfiguration.language;
            if (string.Equals(language, "ko", System.StringComparison.OrdinalIgnoreCase))
            {
                return Korean;
            }

            if (string.Equals(language, "zh-CN", System.StringComparison.OrdinalIgnoreCase) ||
                string.Equals(language, "zh", System.StringComparison.OrdinalIgnoreCase))
            {
                return Chinese;
            }

            if (string.IsNullOrEmpty(language) ||
                string.Equals(language, "auto", System.StringComparison.OrdinalIgnoreCase))
            {
                string systemLanguage = CultureInfo.CurrentUICulture.Name;
                if (systemLanguage.StartsWith("ko", System.StringComparison.OrdinalIgnoreCase))
                {
                    return Korean;
                }

                if (systemLanguage.StartsWith("zh", System.StringComparison.OrdinalIgnoreCase))
                {
                    return Chinese;
                }
            }

            return English;
        }
    }
}
