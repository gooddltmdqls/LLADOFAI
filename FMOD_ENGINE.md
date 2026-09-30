# FMOD audio engine (experimental)

LLADOFAI has two audio engines, chosen in the mod settings under **Audio engine**:

| Engine | How it works | Status |
| --- | --- | --- |
| **NAudio** (default) | Unity mixes as usual; LLADOFAI captures Unity's final PCM at the `AudioListener` and plays it through NAudio (WASAPI shared/exclusive or ASIO). | Stable, unchanged |
| **FMOD** | Unity `AudioSource` playback is intercepted with Harmony and played directly by FMOD Core, which outputs through WASAPI or ASIO. Unity's DSP output is bypassed for every sound FMOD handles. | Experimental |

Switching engines takes effect after restarting the game. FMOD settings (output API, device, DSP buffer) apply when the FMOD backend starts, i.e. after a game restart or after turning the mod off and on in UnityModManager.

## Requirements

- The 64-bit **FMOD Core 2.03.x** runtime (`fmod.dll`) in the mod folder (`Mods/ASIOAdofai`). It is **not** part of the release ZIP; see [Licensing](#licensing).
- If `fmod.dll` is missing, of the wrong version or architecture, or the output cannot be opened, the mod logs the reason, shows it in the settings and leaves Unity's audio untouched.

## Project layout

```text
LLADOFAI            UMM entry point, settings, GUI, engine selection and lifecycle
LLADOFAI.NAudio     the NAudio backend, moved unchanged (WASAPI shared/exclusive, ASIO, Unity PCM bridge)
LLADOFAI.Fmod       the FMOD backend
```

`LLADOFAI.NAudio` and `LLADOFAI.Fmod` do not reference each other. The only change to the NAudio code is that it reaches the host (configuration, logging, localization) through `NAudioHost` instead of `ModEntryPoint`. There is no shared PCM-sink abstraction because the two backends work at different layers. `LLADOFAI/FmodEngineHost.cs` is the host side of the FMOD backend.

`LLADOFAI.Fmod`:

| File | Responsibility |
| --- | --- |
| `FmodNative.cs` | Minimal P/Invoke binding to the FMOD Core C API, written from the public API reference. |
| `FmodBackend.cs` | Public entry: native library loading, device enumeration, start/stop, Harmony ownership, start-up self-test. |
| `FmodEngine.cs` | FMOD System, channel groups, per-`AudioSource` voices, scheduling, clock, spectrum, failure handling. |
| `FmodClock.cs` | Conversion between the FMOD DSP clock and the game's `AudioSettings.dspTime`. |
| `FmodSoundCache.cs` | `AudioClip` → FMOD `Sound` cache and lifetime. |
| `FmodAssetAudio.cs` | Finds the FSB5 data of built-in compressed clips in the game's `.resource` files. |
| `FmodMixer.cs` | Reproduces ADOFAI's `MasterMixer` volume routing. |
| `FmodPatches.cs` | Harmony patches (installed only while the backend runs). |
| `FmodStaThread.cs` | STA thread for FMOD's ASIO output. |
| `UnityDspClock.cs` | Unity's native `dspTime`, used once FMOD stops. |
| `FmodBehaviours.cs` | Per-frame update, and stopping voices when a GameObject is disabled or destroyed. |

## How ADOFAI uses audio (what had to be reproduced)

Findings from the game code (Unity 6000.3, Mono):

- `scrConductor` derives song position entirely from `AudioSettings.dspTime`. The song starts with `song.PlayScheduled(t)` followed by `song.time = …`, and scrubbing uses `SetScheduledStartTime` and `time`.
- Hitsounds, hold sounds and countdown ticks are scheduled up to 5 s ahead with `AudioManager.Play` (`PlayScheduled`). Hold loops also use `SetScheduledEndTime`. Pooled sources are recycled once `isPlaying` becomes false.
- The pause menu and scene loads use `AudioListener.pause`. **The game does not reschedule anything on unpause**, so it relies on Unity's DSP clock stopping while the listener is paused. Menu and interface sounds use `ignoreListenerPause`.
- Volume settings are exposed parameters of the `MasterMixer` (`MainVolume`, `WorldVolume`, `MusicVolume`, `HitsoundsVolume`, `SfxVolume`, `InterfaceVolume`, `OverrideVolume`). `AudioListener.volume` is used for fades.
- `GetSpectrumData` feeds visual effects.
- Clip sources:
  - built-in clips are mostly DecompressOnLoad;
  - 14 built-in clips are CompressedInMemory, including `sndKick` (the default hitsound), `calibration`, the tutorial songs and `1-X_rabbit`;
  - custom-level songs are loaded as *streamed* clips (`UnityWebRequest` with `streamAudio`, or `RDMP3Stream`), which `AudioClip.GetData` cannot read.

## Design

### Playback path

```text
AudioSource API → Harmony prefix → FmodEngine → FMOD channel in LLADOFAI.Pausable / LLADOFAI.IgnoreListenerPause
                                                   → FMOD master (compressor) → WASAPI or ASIO
```

Mono can inline methods of 20 IL bytes or less into their callers, which would bypass a Harmony detour. Where Unity's public method is such a wrapper, the helper behind it is patched instead:

| Public API | Patched method |
| --- | --- |
| `Play()`, `Play(ulong)` | `PlayHelper` |
| `PlayScheduled`, `PlayDelayed` | `Play(double)` |
| `PlayOneShot` | `PlayOneShotHelper` |
| `Stop()` | `Stop(bool)` |
| `pitch` | `SetPitch` |
| `clip`, `resource` | `generatorObject` |
| `GetSpectrumData` | `GetSpectrumDataHelper` |

All other targets are larger than the inlining limit.

Handled `AudioSource` behavior:
- playback: `Play`, `PlayScheduled`, `PlayDelayed`, `PlayOneShot`, `Stop`, `Pause`, `UnPause`;
- scheduling: `SetScheduledStartTime`, `SetScheduledEndTime`;
- state: `isPlaying`, `time`/`timeSamples` (get, set, and set before `Play`), `clip`;
- properties: `volume`, `mute`, `pitch`, `loop`, `priority`, `panStereo`, `ignoreListenerPause`, `outputAudioMixerGroup`;
- `GetSpectrumData`;
- `AudioListener.pause`/`volume`/`GetSpectrumData`;
- `AudioMixer.SetFloat`/`ClearFloat`;
- `AudioClip.UnloadAudioData`.

Disabling or destroying a source's GameObject stops its voices in the same frame, like Unity. FMOD also mirrors Unity's real/virtual voice limits from `AudioSettings.GetConfiguration()`.

Left to Unity:
- 3D sources (`spatialBlend > 0`);
- disabled sources;
- sources started by Unity natively (`playOnAwake`);
- clips FMOD cannot load.

When such a source is scheduled with `PlayScheduled`, the FMOD-clock time is converted into a relative delay for Unity (`PlayDelayed` semantics), which does not depend on either clock.

### Clock

- Every voice that follows `AudioListener.pause` plays in the **pausable** channel group. `AudioSettings.dspTime` is replaced (Harmony transpiler) with that group's DSP clock divided by the FMOD software sample rate; there are no hard-coded rates.
- Pausing an FMOD channel group stops its DSP clock and every delayed start inside it (verified against the runtime, see below). `AudioListener.pause` therefore keeps game time, scheduled sounds and audio consistent in the same way Unity's own clock does.
- `ignoreListenerPause` voices play in a second group whose clock never pauses. Their scheduled times are converted between the two clocks.
- `PlayScheduled(t)` converts `t` to a clock value with one fixed anchor and rounds to the nearest sample. It then uses `Channel::setDelay`, which is sample-accurate and independent of pitch. The conversion adds no error when the game derives `t` from `dspTime` (0 samples over 48 h in tests), so it cannot drift.
- The music and the game clock are both the FMOD mixer clock, so they cannot drift apart over a long session, and no resampling or rate correction is involved.
- At start-up the FMOD clock continues from Unity's current `dspTime`, so the switch causes no jump.
- `AudioListener.pause` and `volume` are native properties; patching their setters would replace Unity's implementation. Their state is mirrored whenever the game reads `dspTime` or starts a sound, and once per frame.
- A native getter cannot be unpatched with Harmony: unpatching regenerates an empty body, which Mono rejects. The `dspTime` patch therefore stays installed for the rest of the process. When FMOD is off it calls Unity's original native function, looked up in Mono's internal-call table and validated against the real value before it is used.

### Sounds

| Clip | Route |
| --- | --- |
| DecompressOnLoad | PCM copied once through `AudioClip.GetData` in 64 K-sample chunks into native memory, which FMOD plays in place (`FMOD_OPENMEMORY_POINT`). No per-play copy, no managed PCM array kept alive. |
| CompressedInMemory (built-in) | The clip's FSB5 bank inside the game's `.resource` file, found by reading the `AudioClip` record (name → `m_Resource`) from the `.assets` files and confirmed against the FSB5 header. FMOD decodes it with the same codec Unity uses. |
| Streamed custom-level songs | The song file recorded in `AudioManager.FindOrLoadAudioClipExternal`, decoded fully into memory by FMOD on its loader thread (`FMOD_NONBLOCKING`). |
| Anything else | Played by Unity; the reason is logged once per clip. |

Cache entries are keyed by instance ID and hold no `AudioClip` reference, so `Resources.UnloadUnusedAssets` still unloads clips. The sweep that notices destroyed clips runs twice a second. `UnloadAudioData` releases the entry, and a sound that is still playing is released when its last channel ends.

### Output

- **WASAPI and ASIO drivers** are enumerated with FMOD and selectable in the settings, together with the DSP buffer size (8–2048 frames; sizes under 256 show a dropout warning) and count (2–8). The default is 512 × 3 (32 ms at 48 kHz).
- **Sample rate.** The software format is the device's reported rate (48 kHz if the driver reports none), in stereo.
- **ASIO threading.** FMOD's ASIO output only sees drivers on a COM single-threaded apartment thread (with FMOD 2.03.14 it reports 0 drivers from an MTA thread). An ASIO System is therefore created, initialized and released on a dedicated STA thread for its whole lifetime; everything else stays on Unity's main thread.
- **Master compressor.** ADOFAI's MasterMixer ends in a Duck Volume effect acting as a peak limiter (threshold −1.2 dB, ratio 9.5). An FMOD Compressor with the same threshold and ratio sits on the FMOD master.
- **Missing device.** If the saved device is not present, the first listed device is used and the log says so.

### Failure handling

- **Start-up failures.** Any failure before or during start-up leaves Unity audio untouched: DLL not found, wrong FMOD version, no drivers, init failure, or a patch or self-test failure. The self-test confirms that `dspTime` is the FMOD clock and that `PlayScheduled`, `timeSamples` and `Stop` are really routed to FMOD, using a silent clip scheduled 10 s ahead.
- **Runtime failures.** A runtime FMOD failure (system update or clock read failing) marks the engine failed. Requests go back to Unity immediately, `dspTime` keeps advancing from the last FMOD value, and on the next frame the patches are removed and FMOD is shut down.
- **Per-sound errors.** An error that affects only one sound (for example `PlaySound` failing) hands that sound to Unity and is logged, up to 20 messages.

### Resources released

At shutdown, in order: channels, per-source FFT DSPs, sounds and their native PCM buffers, the listener FFT, the master compressor, both channel groups, then the System. Link components added to GameObjects are destroyed. FMOD reports its remaining memory in the log; it was 0 bytes in every test.

### Realtime considerations

- **Main-thread work.** Patches run on Unity's main thread and use dictionary lookups by instance ID, pooled voice and state objects, and no LINQ, logging or reflection in the per-call path.
- **Allocations.** The only per-clip allocations happen when a clip is first prepared, usually when it is assigned to a source, not when it plays.
- **Mixing thread.** FMOD mixes on its own thread; the mod never blocks it.
- **Blocking waits.** The only blocking wait is when a sound that is still decoding is played: up to 3 s, after which Unity plays it instead.

## Verification

### Build

The whole solution builds in Release with MSBuild (VS 2022 Build Tools), with no new warnings.

### Against the FMOD 2.03.14 runtime (standalone probe, outside the game)

| Check | Result |
| --- | --- |
| Header version, `System_GetVersion`, `FMOD_CREATESOUNDEXINFO` size (224) | accepted |
| DSP types in 2.03 | Compressor = 16, FFT = 26. FFT "Spectrum Data" is parameter 4. The code looks parameters up by name. |
| Pausing a child group | its DSP clock stops; the parent clock continues |
| `setDelay` start inside a group paused for 500 ms | started 500 ms later (1.00 s instead of 0.5 s) |
| Rescheduling a playing channel to a later start | holds position, then continues |
| `setPosition` before a scheduled start | honored |
| Scheduled end | channel freed |
| Loop mode set on a sample channel | loops; turning it off ends the sound |
| `OPENMEMORY_POINT` float PCM | 896 bytes of FMOD memory for 3.84 MB of PCM (played in place) |
| OGG / WAV decoding | clicks at exactly the encoded sample positions |
| MP3 decoding | identical sample positions and length to the game's own MP3Sharp decoder (44.1 kHz and 48 kHz) |
| 5-minute MP3 / OGG load | 380–400 ms / 250–260 ms, non-blocking |
| FSB5 bank in `resources.resource` (calibration) | 1 subsound, 614400 samples, 48 kHz, stereo |
| Built-in clip index | 435 clips from 148 asset files in ≈ 56 ms; all 14 compressed clips resolved to their exact offsets, including names that share a sample count; non-matching lookups rejected |
| Clock conversion (44.1, 48, 96, 192 kHz) | 0-sample round-trip and schedule error over 48 h |
| Create / play / release 200 sounds | FMOD memory back to baseline; 0 bytes after `System_Release` |
| ASIO | 0 drivers from MTA, 5 from the STA helper; init, playback driven from an MTA thread, and release all work with Voicemeeter Virtual ASIO |

### In the game (ADOFAI with Unity 6000.3.10f1, UMM, Harmony 2.3.6)

- **Start-up.** The FMOD backend started on WASAPI (VB-Audio Cable and a headset; DSP buffers 512 × 3 and 256 × 2), and the self-test passed. This proves that the `dspTime` transpiler on the native getter and the `AudioSource` patches take effect on this runtime.
- **First build found three problems, all fixed:**
  - Turning the mod off showed that Harmony cannot unpatch a native getter, which led to the permanent `dspTime` patch described above.
  - `GetSpectrumDataHelper`'s `[Out] float[]` parameter must also be declared `[Out]` in the patch; otherwise Harmony dereferences the array.
  - A compressed built-in clip (`1-X_rabbit`) fell back to Unity, which led to the FSB5 route described above.
- **Fixed build.**
  - Turning the mod off and on stopped FMOD cleanly (0 bytes of FMOD memory left, no patch errors), handed `dspTime` back to Unity's native clock, and restarted FMOD with the self-test passing again.
  - `1-X_rabbit` was decoded through its FSB5 bank.
  - The asset index built in the background at start-up.
  - Quitting the game shut FMOD down through `OnApplicationQuit`.
- **Session on a headset at 256 × 2.** The session played:
  - compressed built-in clips (`1-X_rabbit`, `Applause`) through their FSB5 banks;
  - a custom level whose OGG song (with a non-ASCII path) went through the streamed-song file route, 11.1 M samples at 48 kHz.

  It ended with 0 bytes of FMOD memory in use (peak 85 MB) and no errors or exceptions in the log.

## What still needs manual testing

The following cannot be verified without playing the game and listening. Recalibrate the input offset for the FMOD output first.

1. **Music and SFX.** Main menu, world select, an official level, a custom level with an OGG song, and a custom level with an MP3 song. Check the settings statistics: "plays left to Unity" should stay near 0.
2. **Timing.** Play a long level (5 min or more) with hitsounds on. Check that hitsound alignment and judgements do not drift, including after pitch/speed-trial changes and when starting from a checkpoint (seek via `song.time`).
3. **Pause and resume.** Use the pause menu mid-level several times. The song and scheduled hitsounds must resume in sync, and interface sounds must keep playing while paused.
4. **Restart and scenes.** Restart a level many times, change scenes, and open the editor and the calibration scene. With statistics on, the source and sound counts should return to a stable level; no growth over time.
5. **Volume.** Master, music, hitsound and SFX sliders; level fades (`AudioListener.volume`); ducking.
6. **ASIO on real hardware.** Latency and stability at 256/512 frames, and the driver's own buffer size.
7. **Device changes.** Unplug or change the default device while FMOD runs. FMOD keeps its mixer clock running, but device behaviour depends on the driver.
8. **Failure paths.** Remove `fmod.dll`, or select a device that is not present, and confirm the game still runs on Unity audio with a clear message.

## Known limitations

- **Unsupported Unity audio features.** Sounds played by FMOD skip Unity-side effects: AudioSource filter components and `OnAudioFilterRead`, mixer effects other than the master limiter, 3D positioning (such sources stay on Unity), and `ignoreListenerVolume`. ADOFAI does not use these on the sources FMOD handles.
- **Spectrum values.** `GetSpectrumData` comes from FMOD's FFT DSP. It follows the music but its scaling is not identical to Unity's.
- **DLC songs.** Songs in Addressables bundles (DLC) that are compressed or streamed are not in the `.resource` files; they fall back to Unity.
- **MP3 stream padding.** For `RDMP3Stream` songs, `isPlaying` ends at the real end of the MP3 rather than after the game's 72 s padding. Only the menu song-loop logic reads this, and it is not used in gameplay.
- **`playOnAwake` sources.** Sources that Unity starts natively play through Unity.
- **WASAPI exclusive mode.** Not offered for FMOD.
- **Restoring `dspTime` after turning FMOD off.** If Mono's internal-call lookup is unavailable, `dspTime` keeps extrapolating after FMOD is turned off until the game restarts. The log says so.

## Licensing

- **Code.** All FMOD backend code was written for LLADOFAI from the FMOD Core API reference, the Unity and Harmony APIs, ADOFAI's own code (decompiled for analysis only) and general audio-programming knowledge. No code from third-party ADOFAI FMOD mods was used. The code is under LLADOFAI's MIT license.
- **FMOD runtime.** `fmod.dll` is proprietary Firelight Technologies software.