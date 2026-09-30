using System;
using System.Runtime.InteropServices;
using System.Text;

namespace LLADOFAI.Fmod
{
    // Minimal binding to the FMOD Core C API (2.03), written against the public API
    // reference. Only the calls LLADOFAI uses are declared. No FMOD SDK source or
    // native binary is part of this assembly; the runtime is loaded from fmod.dll
    // next to the mod or on the DLL search path. FMOD_BOOL is a 32-bit int.
    internal static class FmodNative
    {
        private const string Library = "fmod";
        private const CallingConvention Convention = CallingConvention.StdCall;

        // FMOD_VERSION of the headers this binding was written against. The runtime
        // accepts any 2.03.x build for a 2.03 header version.
        internal const uint HeaderVersion = 0x00020314;

        internal const int Ok = 0;
        internal const int ErrInvalidHandle = 30;
        internal const int ErrChannelStolen = 3;

        // FMOD_OUTPUTTYPE
        internal const int OutputNoSound = 2;
        internal const int OutputWasapi = 6;
        internal const int OutputAsio = 7;

        // FMOD_INITFLAGS
        internal const uint InitNormal = 0x0;
        internal const uint InitClipOutput = 0x8;

        // FMOD_MODE
        internal const uint ModeLoopOff = 0x1;
        internal const uint ModeLoopNormal = 0x2;
        internal const uint Mode2D = 0x8;
        internal const uint ModeCreateSample = 0x100;
        internal const uint ModeOpenMemory = 0x800;
        internal const uint ModeOpenRaw = 0x1000;
        internal const uint ModeAccurateTime = 0x4000;
        internal const uint ModeNonBlocking = 0x10000;
        internal const uint ModeIgnoreTags = 0x2000000;

        // FMOD_TIMEUNIT
        internal const uint TimeUnitMs = 0x1;
        internal const uint TimeUnitPcm = 0x2;

        // FMOD_SOUND_FORMAT
        internal const int FormatPcmFloat = 5;

        // FMOD_OPENSTATE
        internal const int OpenStateReady = 0;
        internal const int OpenStateError = 2;

        // FMOD_SPEAKERMODE
        internal const int SpeakerModeDefault = 0;

        // FMOD_DSP_TYPE values changed in 2.03 (the obsolete plug-in types were
        // removed). The engine confirms each DSP by name before using it.
        internal const int DspTypeCompressor = 16;
        internal const int DspTypeFft = 26;

        [StructLayout(LayoutKind.Sequential)]
        internal struct CreateSoundExInfo
        {
            public int cbsize;
            public uint length;
            public uint fileoffset;
            public int numchannels;
            public int defaultfrequency;
            public int format;
            public uint decodebuffersize;
            public int initialsubsound;
            public int numsubsounds;
            public IntPtr inclusionlist;
            public int inclusionlistnum;
            public IntPtr pcmreadcallback;
            public IntPtr pcmsetposcallback;
            public IntPtr nonblockcallback;
            public IntPtr dlsname;
            public IntPtr encryptionkey;
            public int maxpolyphony;
            public IntPtr userdata;
            public int suggestedsoundtype;
            public IntPtr fileuseropen;
            public IntPtr fileuserclose;
            public IntPtr fileuserread;
            public IntPtr fileuserseek;
            public IntPtr fileuserasyncread;
            public IntPtr fileuserasynccancel;
            public IntPtr fileuserdata;
            public int filebuffersize;
            public int channelorder;
            public IntPtr initialsoundgroup;
            public uint initialseekposition;
            public uint initialseekpostype;
            public int ignoresetfilesystem;
            public uint audioqueuepolicy;
            public uint minmidigranularity;
            public int nonblockthreadid;
            public IntPtr fsbguid;
        }

        // System
        [DllImport(Library, CallingConvention = Convention)] internal static extern int FMOD_System_Create(out IntPtr system, uint headerVersion);
        [DllImport(Library, CallingConvention = Convention)] internal static extern int FMOD_System_Release(IntPtr system);
        [DllImport(Library, CallingConvention = Convention)] internal static extern int FMOD_System_GetVersion(IntPtr system, out uint version, out uint buildNumber);
        [DllImport(Library, CallingConvention = Convention)] internal static extern int FMOD_System_SetOutput(IntPtr system, int output);
        [DllImport(Library, CallingConvention = Convention)] internal static extern int FMOD_System_GetOutput(IntPtr system, out int output);
        [DllImport(Library, CallingConvention = Convention)] internal static extern int FMOD_System_GetNumDrivers(IntPtr system, out int count);
        [DllImport(Library, CallingConvention = Convention)] internal static extern int FMOD_System_GetDriverInfo(IntPtr system, int id, byte[] name, int nameLength, out Guid guid, out int systemRate, out int speakerMode, out int speakerModeChannels);
        [DllImport(Library, CallingConvention = Convention)] internal static extern int FMOD_System_SetDriver(IntPtr system, int driver);
        [DllImport(Library, CallingConvention = Convention)] internal static extern int FMOD_System_GetDriver(IntPtr system, out int driver);
        [DllImport(Library, CallingConvention = Convention)] internal static extern int FMOD_System_SetSoftwareChannels(IntPtr system, int count);
        [DllImport(Library, CallingConvention = Convention)] internal static extern int FMOD_System_SetSoftwareFormat(IntPtr system, int sampleRate, int speakerMode, int rawSpeakers);
        [DllImport(Library, CallingConvention = Convention)] internal static extern int FMOD_System_GetSoftwareFormat(IntPtr system, out int sampleRate, out int speakerMode, out int rawSpeakers);
        [DllImport(Library, CallingConvention = Convention)] internal static extern int FMOD_System_SetDSPBufferSize(IntPtr system, uint length, int count);
        [DllImport(Library, CallingConvention = Convention)] internal static extern int FMOD_System_GetDSPBufferSize(IntPtr system, out uint length, out int count);
        [DllImport(Library, CallingConvention = Convention)] internal static extern int FMOD_System_Init(IntPtr system, int maxChannels, uint flags, IntPtr extraDriverData);
        [DllImport(Library, CallingConvention = Convention)] internal static extern int FMOD_System_Update(IntPtr system);
        [DllImport(Library, CallingConvention = Convention)] internal static extern int FMOD_System_GetMasterChannelGroup(IntPtr system, out IntPtr group);
        [DllImport(Library, CallingConvention = Convention)] internal static extern int FMOD_System_CreateChannelGroup(IntPtr system, byte[] name, out IntPtr group);
        [DllImport(Library, CallingConvention = Convention)] internal static extern int FMOD_System_CreateSound(IntPtr system, IntPtr data, uint mode, ref CreateSoundExInfo info, out IntPtr sound);
        [DllImport(Library, CallingConvention = Convention)] internal static extern int FMOD_System_CreateSound(IntPtr system, byte[] path, uint mode, IntPtr info, out IntPtr sound);
        [DllImport(Library, CallingConvention = Convention)] internal static extern int FMOD_System_CreateSound(IntPtr system, byte[] path, uint mode, ref CreateSoundExInfo info, out IntPtr sound);
        [DllImport(Library, CallingConvention = Convention)] internal static extern int FMOD_System_CreateDSPByType(IntPtr system, int type, out IntPtr dsp);
        [DllImport(Library, CallingConvention = Convention)] internal static extern int FMOD_System_PlaySound(IntPtr system, IntPtr sound, IntPtr group, int paused, out IntPtr channel);
        [DllImport(Library, CallingConvention = Convention)] internal static extern int FMOD_System_GetChannelsPlaying(IntPtr system, out int channels, out int realChannels);
        // FMOD_CPU_USAGE is a struct of floats (dsp first); the array is larger than the
        // struct so later versions that add fields cannot overrun it.
        [DllImport(Library, CallingConvention = Convention)] internal static extern int FMOD_System_GetCPUUsage(IntPtr system, [Out] float[] usage);
        [DllImport(Library, CallingConvention = Convention)] internal static extern int FMOD_Memory_GetStats(out int currentAllocated, out int maxAllocated, int blocking);

        // Sound
        [DllImport(Library, CallingConvention = Convention)] internal static extern int FMOD_Sound_Release(IntPtr sound);
        [DllImport(Library, CallingConvention = Convention)] internal static extern int FMOD_Sound_GetOpenState(IntPtr sound, out int openState, out uint percentBuffered, out int starving, out int diskBusy);
        [DllImport(Library, CallingConvention = Convention)] internal static extern int FMOD_Sound_GetLength(IntPtr sound, out uint length, uint timeUnit);
        [DllImport(Library, CallingConvention = Convention)] internal static extern int FMOD_Sound_GetDefaults(IntPtr sound, out float frequency, out int priority);
        [DllImport(Library, CallingConvention = Convention)] internal static extern int FMOD_Sound_GetFormat(IntPtr sound, out int type, out int format, out int channels, out int bits);
        [DllImport(Library, CallingConvention = Convention)] internal static extern int FMOD_Sound_GetNumSubSounds(IntPtr sound, out int count);
        [DllImport(Library, CallingConvention = Convention)] internal static extern int FMOD_Sound_GetSubSound(IntPtr sound, int index, out IntPtr subsound);
        [DllImport(Library, CallingConvention = Convention)] internal static extern int FMOD_Sound_Lock(IntPtr sound, uint offset, uint length, out IntPtr ptr1, out IntPtr ptr2, out uint len1, out uint len2);
        [DllImport(Library, CallingConvention = Convention)] internal static extern int FMOD_Sound_Unlock(IntPtr sound, IntPtr ptr1, IntPtr ptr2, uint len1, uint len2);

        // Channel
        [DllImport(Library, CallingConvention = Convention)] internal static extern int FMOD_Channel_Stop(IntPtr channel);
        [DllImport(Library, CallingConvention = Convention)] internal static extern int FMOD_Channel_IsPlaying(IntPtr channel, out int playing);
        [DllImport(Library, CallingConvention = Convention)] internal static extern int FMOD_Channel_SetPaused(IntPtr channel, int paused);
        [DllImport(Library, CallingConvention = Convention)] internal static extern int FMOD_Channel_SetVolume(IntPtr channel, float volume);
        [DllImport(Library, CallingConvention = Convention)] internal static extern int FMOD_Channel_SetPitch(IntPtr channel, float pitch);
        [DllImport(Library, CallingConvention = Convention)] internal static extern int FMOD_Channel_SetMode(IntPtr channel, uint mode);
        [DllImport(Library, CallingConvention = Convention)] internal static extern int FMOD_Channel_SetPriority(IntPtr channel, int priority);
        [DllImport(Library, CallingConvention = Convention)] internal static extern int FMOD_Channel_SetPan(IntPtr channel, float pan);
        [DllImport(Library, CallingConvention = Convention)] internal static extern int FMOD_Channel_SetPosition(IntPtr channel, uint position, uint timeUnit);
        [DllImport(Library, CallingConvention = Convention)] internal static extern int FMOD_Channel_GetPosition(IntPtr channel, out uint position, uint timeUnit);
        [DllImport(Library, CallingConvention = Convention)] internal static extern int FMOD_Channel_GetDSPClock(IntPtr channel, out ulong clock, out ulong parentClock);
        [DllImport(Library, CallingConvention = Convention)] internal static extern int FMOD_Channel_SetDelay(IntPtr channel, ulong startClock, ulong endClock, int stopChannels);
        [DllImport(Library, CallingConvention = Convention)] internal static extern int FMOD_Channel_GetDelay(IntPtr channel, out ulong startClock, out ulong endClock, out int stopChannels);
        [DllImport(Library, CallingConvention = Convention)] internal static extern int FMOD_Channel_AddDSP(IntPtr channel, int index, IntPtr dsp);
        [DllImport(Library, CallingConvention = Convention)] internal static extern int FMOD_Channel_RemoveDSP(IntPtr channel, IntPtr dsp);
        [DllImport(Library, CallingConvention = Convention)] internal static extern int FMOD_Channel_SetChannelGroup(IntPtr channel, IntPtr group);

        // ChannelGroup
        [DllImport(Library, CallingConvention = Convention)] internal static extern int FMOD_ChannelGroup_Release(IntPtr group);
        [DllImport(Library, CallingConvention = Convention)] internal static extern int FMOD_ChannelGroup_Stop(IntPtr group);
        [DllImport(Library, CallingConvention = Convention)] internal static extern int FMOD_ChannelGroup_AddGroup(IntPtr group, IntPtr child, int propagateDspClock, IntPtr connection);
        [DllImport(Library, CallingConvention = Convention)] internal static extern int FMOD_ChannelGroup_SetPaused(IntPtr group, int paused);
        [DllImport(Library, CallingConvention = Convention)] internal static extern int FMOD_ChannelGroup_SetVolume(IntPtr group, float volume);
        [DllImport(Library, CallingConvention = Convention)] internal static extern int FMOD_ChannelGroup_GetDSPClock(IntPtr group, out ulong clock, out ulong parentClock);
        [DllImport(Library, CallingConvention = Convention)] internal static extern int FMOD_ChannelGroup_AddDSP(IntPtr group, int index, IntPtr dsp);
        [DllImport(Library, CallingConvention = Convention)] internal static extern int FMOD_ChannelGroup_RemoveDSP(IntPtr group, IntPtr dsp);

        // DSP
        [DllImport(Library, CallingConvention = Convention)] internal static extern int FMOD_DSP_Release(IntPtr dsp);
        [DllImport(Library, CallingConvention = Convention)] internal static extern int FMOD_DSP_DisconnectAll(IntPtr dsp, int inputs, int outputs);
        [DllImport(Library, CallingConvention = Convention)] internal static extern int FMOD_DSP_GetInfo(IntPtr dsp, byte[] name, out uint version, out int channels, out int configWidth, out int configHeight);
        [DllImport(Library, CallingConvention = Convention)] internal static extern int FMOD_DSP_GetNumParameters(IntPtr dsp, out int count);
        [DllImport(Library, CallingConvention = Convention)] internal static extern int FMOD_DSP_GetParameterInfo(IntPtr dsp, int index, out IntPtr description);
        [DllImport(Library, CallingConvention = Convention)] internal static extern int FMOD_DSP_SetParameterFloat(IntPtr dsp, int index, float value);
        [DllImport(Library, CallingConvention = Convention)] internal static extern int FMOD_DSP_SetParameterInt(IntPtr dsp, int index, int value);
        [DllImport(Library, CallingConvention = Convention)] internal static extern int FMOD_DSP_GetParameterData(IntPtr dsp, int index, out IntPtr data, out uint length, IntPtr valueString, int valueStringLength);

        internal static byte[] Utf8(string value)
        {
            byte[] bytes = new byte[Encoding.UTF8.GetByteCount(value) + 1];
            Encoding.UTF8.GetBytes(value, 0, value.Length, bytes, 0);
            return bytes;
        }

        internal static string FromUtf8(byte[] buffer)
        {
            int end = Array.IndexOf(buffer, (byte)0);
            return Encoding.UTF8.GetString(buffer, 0, end < 0 ? buffer.Length : end);
        }

        internal static string DspName(IntPtr dsp)
        {
            byte[] name = new byte[32];
            uint version;
            int channels, width, height;
            return FMOD_DSP_GetInfo(dsp, name, out version, out channels, out width, out height) == Ok
                ? FromUtf8(name) : null;
        }

        // FMOD_DSP_PARAMETER_DESC begins with a 32-bit type followed by char name[16].
        internal static string DspParameterName(IntPtr dsp, int index)
        {
            IntPtr description;
            if (FMOD_DSP_GetParameterInfo(dsp, index, out description) != Ok || description == IntPtr.Zero)
                return null;
            byte[] name = new byte[16];
            Marshal.Copy(IntPtr.Add(description, 4), name, 0, name.Length);
            return FromUtf8(name);
        }

        internal static int FindDspParameter(IntPtr dsp, string name)
        {
            int count;
            if (FMOD_DSP_GetNumParameters(dsp, out count) != Ok) return -1;
            for (int i = 0; i < count; i++)
                if (string.Equals(DspParameterName(dsp, i), name, StringComparison.OrdinalIgnoreCase))
                    return i;
            return -1;
        }
    }
}
