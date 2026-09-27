# LLADOFAI

Low-latency ASIO and WASAPI audio output for *A Dance of Fire and Ice* on Windows.

## Introduction

**LLADOFAI** means **Low Latency A Dance of Fire and Ice**. It is a Windows audio output mod for *A Dance of Fire and Ice* that routes the game's audio to a selected ASIO driver or WASAPI device. WASAPI supports shared and exclusive modes, and both output options include an optional low-latency Unity DSP setting.

## Why LLADOFAI?

LLADOFAI can reduce audio output latency by **up to around 100 ms**, depending on your audio device and system configuration.

The included screenshots compare the in-game device offset shown for the default output and LLADOFAI output modes. These are example readings; actual results depend on your device, drivers, buffer size, and system configuration.

| Output setup | Device offset shown | Screenshot |
| --- | ---: | --- |
| Default output (without the mod) | 102 ms | <img src="images/without_mod.png" width="360" alt="Default output showing a 102 ms device offset"> |
| WASAPI shared | 47 ms | <img src="images/with_wasapi_shared.png" width="360" alt="WASAPI shared showing a 47 ms device offset"> |
| WASAPI exclusive | 37 ms | <img src="images/with_wasapi_exclusive.png" width="360" alt="WASAPI exclusive showing a 37 ms device offset"> |
| ASIO | 24 ms | <img src="images/with_asio.png" width="360" alt="ASIO showing a 24 ms device offset"> |

## Usage

> [!NOTE]
> ASIO output requires an ASIO driver. This is typically provided by audio interfaces with native ASIO support.

1. Install Unity Mod Manager for *A Dance of Fire and Ice*, then install the LLADOFAI ZIP through Unity Mod Manager.
2. Enable LLADOFAI and open its settings. Choose either **Use ASIO** or **Use WASAPI**, then select an audio device.
3. For WASAPI, choose shared or exclusive mode. Exclusive mode requires the selected device to differ from the Windows default output; change the default and restart the game if necessary.
4. Click **Save settings** to apply your selection.

The optional 256-frame Unity DSP setting takes effect the next time you launch the game.

## Troubleshooting

- If audio crackles, drops out, or underruns, increase the audio buffer size in ADOFAI's audio settings.
- **ASIO only:** Increase the **ASIO fixed queue safety margin** in LLADOFAI's settings. Recalibrate the in-game audio offset after changing it.

## Contributing

Bug reports and pull requests are welcome. For audio issues, include your game and Unity Mod Manager versions, selected output mode and device, and any relevant mod log messages.

To build, use Visual Studio or MSBuild on Windows. The solution targets .NET Framework 4.8 and references assemblies from the ADOFAI and Unity Mod Manager game installation. Run `make_release.bat` to build and package the mod.

## License

MIT License