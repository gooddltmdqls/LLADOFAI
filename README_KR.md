# LLADOFAI

[EN](README.md) | [KR](README_KR.md)

Windows용 *A Dance of Fire and Ice* 저지연 ASIO 및 WASAPI 오디오 출력 모드입니다.

## 소개

**LLADOFAI**는 **Low Latency A Dance of Fire and Ice**의 약자입니다. 이 모드는 게임 오디오를 선택한 ASIO 드라이버 또는 WASAPI 장치로 출력합니다.

## LLADOFAI를 사용하는 이유

LLADOFAI를 사용하면 오디오 장치와 시스템 구성에 따라 오디오 출력 지연을 **최대 약 100ms까지 줄일 수 있습니다**.

아래 스크린샷은 기본 출력과 LLADOFAI 출력 모드에서 게임 내에 표시되는 장치 오프셋을 비교한 예시입니다. 실제 결과는 오디오 장치, 드라이버, 버퍼 크기 및 시스템 구성에 따라 달라질 수 있습니다.

| 출력 설정 | 표시된 장치 오프셋 | 스크린샷 |
| --- | ---: | --- |
| 기본 출력 (모드 미사용) | 102ms | <img src="images/without_mod.png" width="360" alt="기본 출력에서 장치 오프셋 102ms가 표시된 화면"> |
| WASAPI 공유 모드 | 47ms | <img src="images/with_wasapi_shared.png" width="360" alt="WASAPI 공유 모드에서 장치 오프셋 47ms가 표시된 화면"> |
| WASAPI 단독 모드 | 37ms | <img src="images/with_wasapi_exclusive.png" width="360" alt="WASAPI 단독 모드에서 장치 오프셋 37ms가 표시된 화면"> |
| ASIO | 24ms | <img src="images/with_asio.png" width="360" alt="ASIO에서 장치 오프셋 24ms가 표시된 화면"> |

## 사용 방법

> [!NOTE]
> ASIO 출력을 사용하려면 ASIO 드라이버가 필요합니다. 일반적으로 ASIO를 기본 지원하는 오디오 인터페이스에서 드라이버를 제공합니다.

> [!NOTE]
> 모드 설치가 처음이라면 [설치 가이드](https://the-universal-forums.notion.site/Mod-info-3ca484d6071f80cf8f98c5b0337602e4)를 참고하세요.

1. *A Dance of Fire and Ice*용 UnityModManager를 설치한 다음, UnityModManager에서 LLADOFAI ZIP 파일을 설치합니다.
2. LLADOFAI를 활성화하고 설정을 엽니다. **Use ASIO** 또는 **Use WASAPI**를 선택한 뒤 오디오 장치를 고릅니다.
3. WASAPI를 선택했다면 공유 모드 또는 단독 모드를 고릅니다. 단독 모드를 사용하려면 선택한 장치가 Windows 기본 출력 장치와 달라야 합니다. 필요한 경우 기본 출력 장치를 변경한 다음 게임을 다시 시작하세요.
4. **Save settings**를 눌러 설정을 적용합니다.

## 문제 해결

- 오디오가 지직거리거나 끊기거나 버퍼 언더런이 발생하면 ADOFAI 오디오 설정에서 버퍼 크기를 늘려 보세요.
- **ASIO 전용:** LLADOFAI 설정에서 **ASIO fixed queue safety margin** 값을 높여 보세요. 값을 변경한 뒤에는 게임 내 오디오 오프셋을 다시 보정하세요.

## 기여

버그 제보와 풀 리퀘스트를 환영합니다. 오디오 문제를 제보할 때는 게임 및 UnityModManager 버전, 선택한 출력 모드와 장치, 관련 모드 로그 메시지를 함께 알려 주세요.

Windows에서 Visual Studio 또는 MSBuild를 사용해 빌드할 수 있습니다. 솔루션은 .NET Framework 4.8을 대상으로 하며, ADOFAI 및 UnityModManager 게임 설치 폴더의 어셈블리를 참조합니다. `make_release.bat`을 실행하면 모드를 빌드하고 패키징합니다.

## 라이선스

MIT License
