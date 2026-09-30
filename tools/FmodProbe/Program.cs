// FMOD probe: checks LLADOFAI's FMOD binding and the FMOD runtime behaviour the
// engine relies on, outside the game. See README.md in this folder.
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using LLADOFAI.Fmod;
using static LLADOFAI.Fmod.FmodNative;

internal static class Program
{
    static void C(int r, string op) { if (r != 0) throw new Exception(op + " -> FMOD_RESULT " + r); }

    // Game data folder for the MP3Sharp and built-in clip checks; override with ADOFAI_DATA.
    static readonly string GameData = Environment.GetEnvironmentVariable("ADOFAI_DATA") ??
        @"C:\Program Files (x86)\Steam\steamapps\common\A Dance of Fire and Ice\A Dance of Fire and Ice_Data";

    static IntPtr sys, master;
    static int rate;

    [STAThread]
    static int Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0] : "all";
        Console.WriteLine("apartment=" + System.Threading.Thread.CurrentThread.GetApartmentState());
        if (mode == "clock") return ClockTests();
        if (mode == "assets") return AssetTests();
        if (mode == "asiosta") { var t = new Thread(AsioSta); t.SetApartmentState(ApartmentState.MTA); t.Start(); t.Join(); return 0; }
        if (mode == "asio") { Enumerate(OutputAsio, "ASIO"); AsioInit(); return 0; }
        Console.WriteLine("sizeof(CreateSoundExInfo)=" + Marshal.SizeOf<CreateSoundExInfo>());
        C(FMOD_System_Create(out sys, HeaderVersion), "System_Create");
        C(FMOD_System_GetVersion(sys, out uint ver, out uint build), "GetVersion");
        Console.WriteLine($"version=0x{ver:X8} build={build}");
        Enumerate(OutputWasapi, "WASAPI");
        Enumerate(OutputAsio, "ASIO");

        int output = mode == "nosound" ? OutputNoSound : OutputWasapi;
        C(FMOD_System_SetOutput(sys, output), "SetOutput");
        int driverRate = 48000;
        if (output == OutputWasapi)
        {
            byte[] name = new byte[256];
            C(FMOD_System_GetDriverInfo(sys, 0, name, name.Length, out Guid g, out driverRate, out int sm, out int ch), "GetDriverInfo");
            Console.WriteLine("driver0 " + FromUtf8(name) + " rate=" + driverRate);
        }
        C(FMOD_System_SetSoftwareFormat(sys, driverRate, SpeakerModeDefault, 0), "SetSoftwareFormat");
        C(FMOD_System_SetDSPBufferSize(sys, 512, 4), "SetDSPBufferSize");
        C(FMOD_System_Init(sys, 256, InitClipOutput, IntPtr.Zero), "Init");
        C(FMOD_System_GetSoftwareFormat(sys, out rate, out int spk, out int raw), "GetSoftwareFormat");
        C(FMOD_System_GetDSPBufferSize(sys, out uint bl, out int bc), "GetDSPBufferSize");
        C(FMOD_System_GetOutput(sys, out int outNow), "GetOutput");
        Console.WriteLine($"init ok output={outNow} rate={rate} speakermode={spk} dsp={bl}x{bc}");
        C(FMOD_System_GetMasterChannelGroup(sys, out master), "GetMaster");

        DspNames();
        ClockRate();
        GroupPause();
        DelayAcrossGroupPause();
        LeakTest();
        FileTests();
        FftTest();
        ExtraTests();

        C(FMOD_System_Release(sys), "System_Release");
        FMOD_Memory_GetStats(out int cur, out int max, 0);
        Console.WriteLine($"after release memory current={cur} max={max}");
        return 0;
    }

    static int AssetTests()
    {
        string data = GameData;
        var sw = Stopwatch.StartNew();
        var index = new FmodAssetAudio(data, Console.WriteLine);
        var cases = new (string name, int ch, int freq, int samples, long expectOffset)[]
        {
            ("sndKick", 1, 44100, 9499, 19755776), ("calibration", 2, 48000, 614400, 75712928),
            ("1-2", 2, 44100, 512640, 41206240), ("1-3", 2, 44100, 512640, 10007104), ("1-4", 2, 44100, 512640, 52329760),
            ("1-5", 2, 44100, 512640, 18540160), ("2-2", 2, 44100, 531072, 52714048), ("2-3", 2, 44100, 531072, 75488064),
            ("1-X_rabbit", 2, 44100, 3598560, 2046432), ("sndBeepHigh", 2, 44100, 3456, 66684448),
            ("sndKick", 1, 44100, 9500, -1), ("NoSuchClip", 2, 44100, 1, -1),
        };
        int failures = 0;
        foreach (var c in cases)
        {
            bool found = index.TryFind(c.name, c.ch, c.freq, c.samples, out var e, out string reason);
            bool ok = c.expectOffset < 0 ? !found : found && e.Offset == c.expectOffset;
            if (!ok) failures++;
            Console.WriteLine($"{c.name} samples={c.samples}: {(found ? Path.GetFileName(e.ResourcePath) + "@" + e.Offset + "+" + e.Size : "not found (" + reason + ")")} -> {(ok ? "PASS" : "FAIL")}");
        }
        Console.WriteLine("index + lookups took " + sw.ElapsedMilliseconds + " ms");
        // Open one bank with FMOD the way the engine does and check its subsound.
        C(FMOD_System_Create(out sys, HeaderVersion), "create");
        C(FMOD_System_SetOutput(sys, OutputNoSound), "nosound");
        C(FMOD_System_Init(sys, 32, 0, IntPtr.Zero), "init");
        index.TryFind("calibration", 2, 48000, 614400, out var cal, out _);
        var info = new CreateSoundExInfo { cbsize = Marshal.SizeOf<CreateSoundExInfo>(), fileoffset = (uint)cal.Offset, length = (uint)cal.Size };
        C(FMOD_System_CreateSound(sys, Utf8(cal.ResourcePath), Mode2D | ModeCreateSample | ModeLoopOff, ref info, out IntPtr bank), "open bank");
        C(FMOD_Sound_GetNumSubSounds(bank, out int subs), "subsounds");
        C(FMOD_Sound_GetSubSound(bank, 0, out IntPtr sub), "subsound");
        FMOD_Sound_GetLength(sub, out uint len, TimeUnitPcm); FMOD_Sound_GetDefaults(sub, out float f, out _); FMOD_Sound_GetFormat(sub, out _, out int fmt, out int chs, out _);
        bool bankOk = subs == 1 && len == 614400 && f == 48000 && chs == 2;
        Console.WriteLine($"FMOD opened calibration bank: subsounds={subs} length={len} freq={f} channels={chs} format={fmt} -> {(bankOk ? "PASS" : "FAIL")}");
        FMOD_Sound_Release(bank); FMOD_System_Release(sys);
        return failures + (bankOk ? 0 : 1);
    }

    static int ClockTests()
    {
        var rng = new Random(1234);
        int failures = 0;
        foreach (int r in new[] { 44100, 48000, 96000, 192000 })
        {
            ulong anchorClock = 987654321UL;
            double anchorSeconds = 1234.5678901;
            var clock = new FmodClock(r, anchorClock, anchorSeconds);
            long maxRoundTrip = 0, maxGame = 0;
            double prev = double.MinValue;
            for (int i = 0; i < 200000; i++)
            {
                // Up to 48 hours of continuous play after the anchor.
                ulong now = anchorClock + (ulong)(rng.NextDouble() * r * 3600.0 * 48);
                double t = clock.ToSeconds(now);
                long rt = (long)clock.ToClock(t) - (long)now;
                maxRoundTrip = Math.Max(maxRoundTrip, Math.Abs(rt));
                // Game-style schedule: dspTime + offset computed in double.
                double offset = rng.NextDouble() * 5.0;
                long expected = (long)now + (long)Math.Round(offset * r, MidpointRounding.AwayFromZero);
                long game = (long)clock.ToClock(t + offset) - expected;
                maxGame = Math.Max(maxGame, Math.Abs(game));
            }
            for (ulong c = anchorClock; c < anchorClock + (ulong)r * 10; c += 511)
            {
                double t = clock.ToSeconds(c);
                if (t < prev) { failures++; break; }
                prev = t;
            }
            bool ok = maxRoundTrip == 0 && maxGame <= 1;
            if (!ok) failures++;
            Console.WriteLine($"clock {r} Hz: max round-trip error {maxRoundTrip} samples, max schedule error {maxGame} samples over 48 h -> {(ok ? "PASS" : "FAIL")}");
        }
        var c2 = new FmodClock(48000, 1000, 10.0);
        bool past = c2.ToClock(9.0) == 1000 && c2.ToClock(double.NaN) == 1000 && c2.FramesFor(-1) == 0 && c2.FramesFor(0.5) == 24000;
        Console.WriteLine("clock edge cases (past time, NaN, negative delay, 0.5 s) -> " + (past ? "PASS" : "FAIL"));
        return failures + (past ? 0 : 1);
    }

    static void AsioSta()
    {
        Console.WriteLine("caller apartment=" + Thread.CurrentThread.GetApartmentState());
        FMOD_System_Create(out IntPtr probe, HeaderVersion);
        FMOD_System_SetOutput(probe, OutputAsio);
        FMOD_System_GetNumDrivers(probe, out int mtaCount);
        FMOD_System_Release(probe);
        Console.WriteLine("ASIO drivers seen from MTA caller: " + mtaCount);
        var com = new FmodStaThread("probe ASIO");
        IntPtr s = IntPtr.Zero; int driver = -1, count = 0; string name = null;
        com.Invoke(() =>
        {
            C(FMOD_System_Create(out s, HeaderVersion), "create");
            C(FMOD_System_SetOutput(s, OutputAsio), "setoutput");
            C(FMOD_System_GetNumDrivers(s, out count), "numdrivers");
            byte[] buf = new byte[256];
            for (int i = 0; i < count; i++)
            {
                FMOD_System_GetDriverInfo(s, i, buf, buf.Length, out Guid g, out int sr, out int sm, out int ch);
                if (FromUtf8(buf) == "Voicemeeter Virtual ASIO") { driver = i; name = FromUtf8(buf); }
            }
        });
        Console.WriteLine("ASIO drivers seen through FmodStaThread: " + count);
        if (driver < 0) { com.Invoke(() => FMOD_System_Release(s)); com.Dispose(); Console.WriteLine("Voicemeeter Virtual ASIO not listed"); return; }
        int initResult = -1;
        com.Invoke(() =>
        {
            C(FMOD_System_SetDriver(s, driver), "setdriver");
            FMOD_System_SetSoftwareFormat(s, 48000, 3, 0);
            FMOD_System_SetDSPBufferSize(s, 512, 3);
            initResult = FMOD_System_Init(s, 256, InitClipOutput, IntPtr.Zero);
        });
        Console.WriteLine("ASIO init on STA thread (" + name + ") -> " + initResult);
        if (initResult == 0)
        {
            // Everything else from this (MTA) thread, as Unity's main thread would.
            sys = s;
            C(FMOD_System_GetMasterChannelGroup(s, out master), "master");
            FMOD_System_GetSoftwareFormat(s, out rate, out _, out _);
            FMOD_System_GetDSPBufferSize(s, out uint bl, out int bc);
            ulong c0 = MasterClock(); Thread.Sleep(1000); ulong c1 = MasterClock();
            Console.WriteLine($"ASIO clock advanced {c1 - c0} samples in 1 s at {rate} Hz, buffer {bl}x{bc}");
            IntPtr snd = CreatePcmSound(0.5f, 440);
            C(FMOD_System_PlaySound(s, snd, master, 0, out IntPtr chn), "play");
            Thread.Sleep(200);
            Console.WriteLine("ASIO voice position after 200 ms: " + Pos(chn));
            FMOD_Channel_Stop(chn);
            FMOD_Sound_Release(snd);
        }
        com.Invoke(() => Console.WriteLine("ASIO System_Release on STA -> " + FMOD_System_Release(s)));
        com.Dispose();
        FMOD_Memory_GetStats(out int cur, out _, 0);
        Console.WriteLine("FMOD memory after ASIO release: " + cur);
    }

    static void AsioInit()
    {
        C(FMOD_System_Create(out IntPtr s, HeaderVersion), "create");
        C(FMOD_System_SetOutput(s, OutputAsio), "setoutput");
        int r = FMOD_System_Init(s, 64, 0, IntPtr.Zero);
        FMOD_System_GetNumDrivers(s, out int n);
        Console.WriteLine("ASIO init -> " + r + ", drivers after init=" + n);
        FMOD_System_Release(s);
    }

    static void Enumerate(int output, string label)
    {
        C(FMOD_System_Create(out IntPtr s, HeaderVersion), "probe create");
        try
        {
            int r = FMOD_System_SetOutput(s, output);
            if (r != 0) { Console.WriteLine(label + " SetOutput -> " + r); return; }
            r = FMOD_System_GetNumDrivers(s, out int n);
            Console.WriteLine($"{label}: GetNumDrivers -> {r}, {n} drivers");
            byte[] name = new byte[256];
            for (int i = 0; i < n; i++)
            {
                Array.Clear(name);
                r = FMOD_System_GetDriverInfo(s, i, name, name.Length, out Guid g, out int sr, out int sm, out int ch);
                Console.WriteLine($"  [{i}] r={r} '{FromUtf8(name)}' guid={g} rate={sr} spk={sm} ch={ch}");
            }
        }
        finally { FMOD_System_Release(s); }
    }

    static void DspNames()
    {
        for (int t = 0; t < 40; t++)
        {
            if (FMOD_System_CreateDSPByType(sys, t, out IntPtr dsp) != 0) continue;
            string name = DspName(dsp);
            FMOD_DSP_GetNumParameters(dsp, out int n);
            string ps = "";
            if (t == DspTypeFft || t == DspTypeCompressor || (name != null && (name.Contains("FFT") || name.Contains("Compressor"))))
                for (int i = 0; i < n; i++) ps += i + ":" + DspParameterName(dsp, i) + " ";
            Console.WriteLine($"dsp type {t} = '{name}' params={n} {ps}");
            FMOD_DSP_Release(dsp);
        }
    }

    static ulong MasterClock()
    {
        C(FMOD_ChannelGroup_GetDSPClock(master, out ulong c, out ulong p), "GetDSPClock");
        return c;
    }

    static void ClockRate()
    {
        ulong c0 = MasterClock();
        var sw = Stopwatch.StartNew();
        ulong last = c0; int steps = 0; ulong minStep = ulong.MaxValue, maxStep = 0;
        while (sw.ElapsedMilliseconds < 2000)
        {
            ulong c = MasterClock();
            if (c != last)
            {
                ulong d = c - last; steps++;
                if (steps > 1) { minStep = Math.Min(minStep, d); maxStep = Math.Max(maxStep, d); }
                last = c;
            }
            Thread.Sleep(1);
        }
        double seconds = sw.Elapsed.TotalSeconds;
        ulong c1 = MasterClock();
        Console.WriteLine($"clock rate measured={(c1 - c0) / seconds:F1} Hz (software {rate}); clock steps={steps} step min={minStep} max={maxStep}");
    }

    static IntPtr CreateGroup(string name)
    {
        C(FMOD_System_CreateChannelGroup(sys, Utf8(name), out IntPtr g), "CreateChannelGroup");
        C(FMOD_ChannelGroup_AddGroup(master, g, 1, IntPtr.Zero), "AddGroup");
        return g;
    }

    static void GroupPause()
    {
        IntPtr p = CreateGroup("P");
        FMOD_ChannelGroup_GetDSPClock(p, out ulong pc0, out ulong pp0);
        ulong m0 = MasterClock();
        C(FMOD_ChannelGroup_SetPaused(p, 1), "SetPaused");
        Thread.Sleep(400);
        FMOD_ChannelGroup_GetDSPClock(p, out ulong pc1, out ulong pp1);
        ulong m1 = MasterClock();
        C(FMOD_ChannelGroup_SetPaused(p, 0), "SetPaused");
        Thread.Sleep(200);
        FMOD_ChannelGroup_GetDSPClock(p, out ulong pc2, out ulong pp2);
        ulong m2 = MasterClock();
        Console.WriteLine($"group pause: P clock advanced {(long)(pc1 - pc0)} while paused 400ms (master {(long)(m1 - m0)}); parent clocks {pp0}->{pp1}; after unpause P +{(long)(pc2 - pc1)} master +{(long)(m2 - m1)}; P-master offset {(long)pc2 - (long)m2}");
        FMOD_ChannelGroup_Release(p);
    }

    static IntPtr CreatePcmSound(float seconds, float frequency, int channels = 2)
    {
        int frames = (int)(seconds * rate);
        float[] pcm = new float[frames * channels];
        for (int i = 0; i < frames; i++)
            for (int c = 0; c < channels; c++)
                pcm[i * channels + c] = frequency <= 0 ? 0 : 0.2f * (float)Math.Sin(2 * Math.PI * frequency * i / rate);
        var info = new CreateSoundExInfo
        {
            cbsize = Marshal.SizeOf<CreateSoundExInfo>(),
            length = (uint)(pcm.Length * 4),
            numchannels = channels,
            defaultfrequency = rate,
            format = FormatPcmFloat,
        };
        GCHandle h = GCHandle.Alloc(pcm, GCHandleType.Pinned);
        try
        {
            C(FMOD_System_CreateSound(sys, h.AddrOfPinnedObject(), Mode2D | ModeCreateSample | ModeOpenMemory | ModeOpenRaw | ModeLoopOff, ref info, out IntPtr s), "CreateSound(pcm)");
            return s;
        }
        finally { h.Free(); }
    }

    static void DelayAcrossGroupPause()
    {
        IntPtr p = CreateGroup("P2");
        IntPtr sound = CreatePcmSound(1f, 0);
        C(FMOD_System_PlaySound(sys, sound, p, 1, out IntPtr ch), "PlaySound");
        C(FMOD_Channel_GetDSPClock(ch, out ulong chc, out ulong parent), "Channel_GetDSPClock");
        FMOD_ChannelGroup_GetDSPClock(p, out ulong pClock, out _);
        ulong target = parent + (ulong)(rate / 2);
        C(FMOD_Channel_SetDelay(ch, target, 0, 1), "SetDelay");
        C(FMOD_Channel_SetPaused(ch, 0), "Channel_SetPaused");
        var sw = Stopwatch.StartNew();
        Console.WriteLine($"delay test: channel parentclock={parent} group clock={pClock} master={MasterClock()} target={target}");
        Thread.Sleep(200);
        C(FMOD_ChannelGroup_SetPaused(p, 1), "pause");
        Thread.Sleep(500);
        C(FMOD_ChannelGroup_SetPaused(p, 0), "unpause");
        double startedAt = -1; ulong pAtStart = 0;
        while (sw.ElapsedMilliseconds < 3000)
        {
            FMOD_Channel_GetPosition(ch, out uint pos, TimeUnitPcm);
            if (pos > 0)
            {
                startedAt = sw.Elapsed.TotalSeconds;
                FMOD_ChannelGroup_GetDSPClock(p, out pAtStart, out _);
                Console.WriteLine($"  channel started at wall {startedAt:F3}s (expect ~1.0 if group pause delays schedule, ~0.5 if not); pos={pos}; group clock-target={(long)(pAtStart - target)}");
                break;
            }
            Thread.Sleep(1);
        }
        FMOD_Channel_Stop(ch);
        FMOD_Sound_Release(sound);
        FMOD_ChannelGroup_Release(p);
    }

    static void LeakTest()
    {
        FMOD_System_Update(sys);
        FMOD_Memory_GetStats(out int before, out _, 0);
        for (int i = 0; i < 200; i++)
        {
            IntPtr s = CreatePcmSound(0.25f, 440);
            C(FMOD_System_PlaySound(sys, s, master, 1, out IntPtr ch), "PlaySound");
            FMOD_Channel_SetVolume(ch, 0);
            FMOD_Channel_SetPaused(ch, 0);
            FMOD_Channel_Stop(ch);
            C(FMOD_Sound_Release(s), "Sound_Release");
        }
        FMOD_System_Update(sys);
        FMOD_Memory_GetStats(out int after, out int max, 0);
        Console.WriteLine($"leak test: FMOD memory before={before} after={after} (delta {after - before}) peak={max}");
    }

    static void FileTests()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "media");
        if (!Directory.Exists(dir)) dir = Path.Combine(Directory.GetCurrentDirectory(), "media");
        if (!Directory.Exists(dir)) { Console.WriteLine("no media dir"); return; }
        foreach (string file in Directory.GetFiles(dir))
        {
            var sw = Stopwatch.StartNew();
            int r = FMOD_System_CreateSound(sys, Utf8(file), Mode2D | ModeCreateSample | ModeAccurateTime | ModeNonBlocking | ModeIgnoreTags | ModeLoopOff, IntPtr.Zero, out IntPtr s);
            if (r != 0) { Console.WriteLine(Path.GetFileName(file) + " CreateSound -> " + r); continue; }
            int state;
            do { FMOD_System_Update(sys); FMOD_Sound_GetOpenState(s, out state, out _, out _, out _); Thread.Sleep(1); }
            while (state != OpenStateReady && state != OpenStateError && sw.ElapsedMilliseconds < 10000);
            FMOD_Sound_GetLength(s, out uint len, TimeUnitPcm);
            FMOD_Sound_GetDefaults(s, out float freq, out _);
            FMOD_Sound_GetFormat(s, out int type, out int fmt, out int chs, out int bits);
            string first = FirstClicks(s, len, chs, bits, fmt);
            Console.WriteLine($"{Path.GetFileName(file)}: state={state} {sw.ElapsedMilliseconds}ms len={len} freq={freq} type={type} fmt={fmt} ch={chs} bits={bits} clicks@{first}");
            FMOD_Sound_Release(s);
            if (file.EndsWith(".mp3")) Mp3Sharp(file);
        }
    }

    // Decode with the MP3Sharp copy compiled into the game's Assembly-CSharp,
    // which is what RDMP3Stream uses, to compare click positions with FMOD.
    static void Mp3Sharp(string file)
    {
        try
        {
            string managed = Path.Combine(GameData, "Managed");
            var asm = System.Reflection.Assembly.LoadFrom(Path.Combine(managed, "Assembly-CSharp-firstpass.dll"));
            Type t = asm.GetType("MP3Sharp.MP3Stream");
            object stream = Activator.CreateInstance(t, file);
            var read = t.GetMethod("Read", new[] { typeof(byte[]), typeof(int), typeof(int) });
            int channels = Convert.ToInt32(t.GetProperty("ChannelCount").GetValue(stream));
            var all = new System.Collections.Generic.List<byte>();
            byte[] buf = new byte[8192];
            int n;
            while ((n = (int)read.Invoke(stream, new object[] { buf, 0, buf.Length })) > 0)
                for (int i = 0; i < n; i++) all.Add(buf[i]);
            string clicks = ""; int found = 0; long last = -100000;
            int stride = 2 * channels;
            for (long f = 0; f < all.Count / stride && found < 2; f++)
            {
                short v = (short)(all[(int)(f * stride)] | (all[(int)(f * stride) + 1] << 8));
                if (Math.Abs(v / 32768f) > 0.3f && f - last > 1000) { clicks += f + " "; found++; last = f; }
            }
            Console.WriteLine($"  MP3Sharp: channels={channels} frames={all.Count / stride} clicks@{clicks}");
        }
        catch (Exception ex) { Console.WriteLine("  MP3Sharp failed: " + ex.GetBaseException().Message); }
    }

    static string FirstClicks(IntPtr s, uint frames, int channels, int bits, int fmt)
    {
        uint bytes = frames * (uint)channels * (uint)(bits / 8);
        if (FMOD_Sound_Lock(s, 0, bytes, out IntPtr p1, out IntPtr p2, out uint l1, out uint l2) != 0) return "lock failed";
        try
        {
            string result = "";
            int found = 0; long lastFrame = -100000;
            int stride = channels * bits / 8;
            for (long f = 0; f < l1 / stride && found < 2; f++)
            {
                float v = fmt == FormatPcmFloat
                    ? BitConverter.Int32BitsToSingle(Marshal.ReadInt32(p1, (int)(f * stride)))
                    : Marshal.ReadInt16(p1, (int)(f * stride)) / 32768f;
                if (Math.Abs(v) > 0.3f && f - lastFrame > 1000)
                {
                    result += f + " ";
                    found++; lastFrame = f;
                }
            }
            return result;
        }
        finally { FMOD_Sound_Unlock(s, p1, p2, l1, l2); }
    }

    static uint Pos(IntPtr ch) { FMOD_Channel_GetPosition(ch, out uint p, TimeUnitPcm); return p; }
    static bool Playing(IntPtr ch) { return FMOD_Channel_IsPlaying(ch, out int p) == 0 && p != 0; }
    static ulong Parent(IntPtr ch) { FMOD_Channel_GetDSPClock(ch, out _, out ulong p); return p; }

    static void ExtraTests()
    {
        IntPtr sound = CreatePcmSound(3f, 0);
        // 1. Reschedule an already playing channel to a later start.
        C(FMOD_System_PlaySound(sys, sound, master, 0, out IntPtr ch), "play");
        Thread.Sleep(200);
        uint p0 = Pos(ch);
        C(FMOD_Channel_SetDelay(ch, Parent(ch) + (ulong)(rate / 2), 0, 1), "reschedule");
        Thread.Sleep(300);
        uint p1 = Pos(ch);
        Thread.Sleep(500);
        uint p2 = Pos(ch);
        Console.WriteLine($"reschedule playing channel: pos {p0} -> {p1} (during new delay) -> {p2} (after)");
        FMOD_Channel_Stop(ch);

        // 2. Seek a delayed channel before it starts.
        C(FMOD_System_PlaySound(sys, sound, master, 1, out ch), "play");
        C(FMOD_Channel_SetDelay(ch, Parent(ch) + (ulong)(rate * 3 / 10), 0, 1), "delay");
        C(FMOD_Channel_SetPosition(ch, (uint)(rate / 2), TimeUnitPcm), "seek");
        C(FMOD_Channel_SetPaused(ch, 0), "unpause");
        Thread.Sleep(150);
        uint s0 = Pos(ch);
        Thread.Sleep(350);
        uint s1 = Pos(ch);
        Console.WriteLine($"seek before scheduled start: pos {s0} at 150ms (expect {rate / 2}), {s1} at 500ms (expect ~{rate / 2 + rate / 5})");
        FMOD_Channel_Stop(ch);

        // 3. Scheduled end frees the channel.
        C(FMOD_System_PlaySound(sys, sound, master, 0, out ch), "play");
        C(FMOD_Channel_SetDelay(ch, 0, Parent(ch) + (ulong)(rate * 3 / 10), 1), "end");
        Thread.Sleep(150);
        bool e0 = Playing(ch);
        Thread.Sleep(400);
        int r = FMOD_Channel_IsPlaying(ch, out int e1);
        Console.WriteLine($"scheduled end: playing at 150ms={e0}, at 550ms result={r} playing={e1}");

        // 4. Channel loop mode on a sample.
        IntPtr shortSound = CreatePcmSound(0.2f, 0);
        C(FMOD_System_PlaySound(sys, shortSound, master, 0, out ch), "play");
        C(FMOD_Channel_SetMode(ch, ModeLoopNormal), "loop");
        Thread.Sleep(600);
        Console.WriteLine($"loop: playing after 3 lengths={Playing(ch)} pos={Pos(ch)} (< {rate / 5})");
        C(FMOD_Channel_SetMode(ch, ModeLoopOff), "loop off");
        Thread.Sleep(400);
        Console.WriteLine($"loop off: playing={Playing(ch)}");
        FMOD_Channel_Stop(ch);
        FMOD_Sound_Release(shortSound);

        // 5. OPENMEMORY_POINT keeps using caller memory.
        FMOD_Memory_GetStats(out int m0, out _, 0);
        int frames = rate * 10;
        IntPtr native = Marshal.AllocHGlobal(frames * 2 * 4);
        unsafe { new Span<byte>((void*)native, frames * 8).Clear(); }
        var info = new CreateSoundExInfo { cbsize = Marshal.SizeOf<CreateSoundExInfo>(), length = (uint)(frames * 8), numchannels = 2, defaultfrequency = rate, format = FormatPcmFloat };
        C(FMOD_System_CreateSound(sys, native, Mode2D | ModeCreateSample | ModeOpenRaw | 0x10000000u | ModeLoopOff, ref info, out IntPtr pointSound), "CreateSound(point)");
        FMOD_Memory_GetStats(out int m1, out _, 0);
        C(FMOD_System_PlaySound(sys, pointSound, master, 0, out ch), "play point");
        Thread.Sleep(200);
        Console.WriteLine($"OPENMEMORY_POINT: FMOD memory delta={m1 - m0} bytes for {frames * 8} bytes of PCM; playing={Playing(ch)} pos={Pos(ch)}");
        FMOD_Channel_Stop(ch);
        FMOD_Sound_Release(pointSound);
        Marshal.FreeHGlobal(native);
        FMOD_Sound_Release(sound);
    }

    static void FftTest()
    {
        C(FMOD_System_CreateDSPByType(sys, DspTypeFft, out IntPtr fft), "Create FFT");
        Console.WriteLine("fft dsp name=" + DspName(fft) + " spectrum param=" + FindDspParameter(fft, "Spectrum Data") + " size=" + FindDspParameter(fft, "Size"));
        int spectrumIndex = FindDspParameter(fft, "Spectrum Data");
        C(FMOD_DSP_SetParameterInt(fft, FindDspParameter(fft, "Size"), 2048), "fft size");
        C(FMOD_ChannelGroup_AddDSP(master, 0, fft), "AddDSP");
        IntPtr sound = CreatePcmSound(1f, 1000);
        C(FMOD_System_PlaySound(sys, sound, master, 0, out IntPtr ch), "PlaySound");
        Thread.Sleep(300);
        int r = FMOD_DSP_GetParameterData(fft, spectrumIndex, out IntPtr data, out uint length, IntPtr.Zero, 0);
        if (r == 0 && data != IntPtr.Zero)
        {
            int n = Marshal.ReadInt32(data, 0);
            int chs = Marshal.ReadInt32(data, 4);
            IntPtr spec = Marshal.ReadIntPtr(data, 8);
            int peak = 0; float peakV = 0;
            for (int i = 0; i < n / 2; i++) { float v = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(spec, i * 4)); if (v > peakV) { peakV = v; peak = i; } }
            Console.WriteLine($"fft data length={length} bins={n} channels={chs} peak bin={peak} ({peak * (double)rate / n:F0} Hz) value={peakV}");
        }
        else Console.WriteLine("fft GetParameterData -> " + r);
        FMOD_Channel_Stop(ch);
        FMOD_ChannelGroup_RemoveDSP(master, fft);
        FMOD_DSP_Release(fft);
        FMOD_Sound_Release(sound);
    }
}
