# FmodProbe

Standalone checks of LLADOFAI's FMOD binding against the real FMOD Core runtime, outside the game. The results are recorded in [FMOD_ENGINE.md](../../FMOD_ENGINE.md#verification). This tool is not part of the mod or the solution.

Requirements:
- the .NET 9 SDK;
- a 64-bit FMOD Core 2.03 `fmod.dll` in `packages/`;
- for some checks, ADOFAI installed. Set `ADOFAI_DATA` to the game's `..._Data` folder if it is not in the default Steam location.

```text
dotnet run -c Release                 # binding, DSP layout, group-pause clock, scheduling, leaks, decoding, FFT
dotnet run -c Release -- clock        # FmodClock conversion over 48 hours at 44.1-192 kHz
dotnet run -c Release -- assets       # built-in compressed clip lookup against the game's asset files
dotnet run -c Release -- asiosta      # ASIO through FmodStaThread (uses "Voicemeeter Virtual ASIO")
```

The decoding checks read files from `media/` in the working directory. To create them:

```text
ffmpeg -f lavfi -i "aevalsrc='if(eq(n,48000)+eq(n,96000),0.9,0)|if(eq(n,48000)+eq(n,96000),0.9,0)':s=48000:d=3" -c:a pcm_s16le media/click48.wav
ffmpeg -i media/click48.wav -c:a libmp3lame -b:a 192k media/click48.mp3
ffmpeg -i media/click48.wav -c:a libvorbis -q:a 5 media/click48.ogg
```

Each file contains clicks at samples 48000 and 96000. The probe prints where FMOD, and the game's MP3Sharp decoder for MP3 files, place them.
