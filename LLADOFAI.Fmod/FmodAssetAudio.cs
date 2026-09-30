using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace LLADOFAI.Fmod
{
    // Locates the audio data of built-in AudioClips whose load type is
    // CompressedInMemory (including the default Kick hitsound and the calibration
    // song). AudioClip.GetData cannot read those clips, but Unity stores each one
    // as a single-sample FSB5 bank inside a .resource file next to the game's
    // .assets files, which FMOD can open directly at that offset.
    //
    // The index reads only the object tables and AudioClip records of the
    // player's serialized files (format 22 or later, type trees stripped). Every
    // match is confirmed against the FSB5 header before use, so an unexpected file
    // layout makes the clip fall back to Unity instead of playing the wrong data.
    internal sealed class FmodAssetAudio
    {
        private const int AudioClipClassId = 83;

        internal sealed class Entry
        {
            internal string Name;
            internal int LoadType;
            internal int Channels;
            internal int Frequency;
            internal string ResourcePath;
            internal long Offset;
            internal long Size;
        }

        private readonly Dictionary<string, List<Entry>> _byName = new Dictionary<string, List<Entry>>();
        private readonly Thread _builder;
        private string _buildError;

        internal FmodAssetAudio(string dataDirectory, Action<string> log)
        {
            _builder = new Thread(() => Build(dataDirectory, log)) { IsBackground = true, Name = "LLADOFAI FMOD asset index" };
            _builder.Start();
        }

        private void Build(string dataDirectory, Action<string> log)
        {
            try
            {
                int clips = 0, files = 0;
                var paths = new List<string>();
                paths.Add(Path.Combine(dataDirectory, "resources.assets"));
                paths.AddRange(Directory.GetFiles(dataDirectory, "sharedassets*.assets"));
                foreach (string path in paths)
                {
                    if (!File.Exists(path)) continue;
                    files++;
                    foreach (Entry entry in ReadAudioClips(path))
                    {
                        List<Entry> list;
                        if (!_byName.TryGetValue(entry.Name, out list)) _byName[entry.Name] = list = new List<Entry>(1);
                        list.Add(entry);
                        clips++;
                    }
                }
                log?.Invoke("FMOD: indexed " + clips + " built-in audio clips in " + files + " asset files.");
            }
            catch (Exception ex)
            {
                _buildError = ex.Message;
                log?.Invoke("FMOD: built-in audio index unavailable (" + ex.Message + ").");
            }
        }

        // Finds the FSB5 bank of a clip that GetData cannot read.
        internal bool TryFind(string name, int channels, int frequency, int samples, out Entry entry, out string reason)
        {
            entry = null;
            if (!_builder.Join(10000))
            {
                reason = "the built-in audio index is still loading";
                return false;
            }
            List<Entry> list;
            if (_buildError != null || !_byName.TryGetValue(name, out list))
            {
                reason = _buildError ?? "not found in the game's asset files";
                return false;
            }
            string content = null;
            foreach (Entry candidate in list)
            {
                if (candidate.Channels != channels || candidate.Frequency != frequency || candidate.ResourcePath == null) continue;
                string hash;
                if (!Fsb5Matches(candidate, samples, out hash)) continue;
                if (entry == null)
                {
                    entry = candidate;
                    content = hash;
                }
                else if (hash != content)
                {
                    entry = null;
                    reason = "several different clips share this name";
                    return false;
                }
            }
            reason = entry == null ? "no matching FSB5 data" : null;
            return entry != null;
        }

        // Checks the bank header and returns a cheap content fingerprint.
        private static bool Fsb5Matches(Entry entry, int samples, out string fingerprint)
        {
            fingerprint = null;
            byte[] header = new byte[72];
            using (var stream = new FileStream(entry.ResourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                if (entry.Offset + entry.Size > stream.Length) return false;
                stream.Position = entry.Offset;
                if (stream.Read(header, 0, header.Length) != header.Length) return false;
                if (header[0] != 'F' || header[1] != 'S' || header[2] != 'B' || header[3] != '5') return false;
                uint version = BitConverter.ToUInt32(header, 4);
                uint count = BitConverter.ToUInt32(header, 8);
                uint total = (version == 1 ? 60u : 64u) + BitConverter.ToUInt32(header, 12) +
                    BitConverter.ToUInt32(header, 16) + BitConverter.ToUInt32(header, 20);
                ulong sampleHeader = BitConverter.ToUInt64(header, version == 1 ? 60 : 64);
                if (count != 1 || total != entry.Size || (long)(sampleHeader >> 34) != samples) return false;
                // Banks with the same header can still differ; sample the payload.
                byte[] tail = new byte[64];
                stream.Position = entry.Offset + entry.Size - tail.Length;
                stream.Read(tail, 0, tail.Length);
                fingerprint = BitConverter.ToString(tail);
                return true;
            }
        }

        internal static List<Entry> ReadAudioClips(string path)
        {
            var result = new List<Entry>();
            string directory = Path.GetDirectoryName(path);
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16))
            {
                byte[] header = ReadBytes(stream, 0, 48);
                uint version = BigEndian32(header, 8);
                if (version < 22) throw new NotSupportedException(Path.GetFileName(path) + " uses serialized file format " + version);
                bool littleEndian = header[16] == 0;
                uint metadataSize = BigEndian32(header, 20);
                long dataOffset = (long)BigEndian64(header, 32);
                var metadata = new Reader(ReadBytes(stream, 48, (int)metadataSize), littleEndian);

                metadata.CString();                  // Unity version
                metadata.Int32();                    // target platform
                if (metadata.Byte() != 0) throw new NotSupportedException(Path.GetFileName(path) + " contains type trees");
                int typeCount = metadata.Int32();
                var classIds = new int[typeCount];
                for (int i = 0; i < typeCount; i++)
                {
                    classIds[i] = metadata.Int32();
                    metadata.Byte();                 // stripped type
                    metadata.Int16();                // script type index
                    if (classIds[i] == 114) metadata.Skip(16);
                    metadata.Skip(16);               // old type hash
                }

                int objectCount = metadata.Int32();
                var clips = new List<KeyValuePair<long, int>>();
                for (int i = 0; i < objectCount; i++)
                {
                    metadata.Align();
                    metadata.Int64();                // path id
                    long start = metadata.Int64();
                    int size = (int)metadata.UInt32();
                    int type = metadata.Int32();
                    if (type >= 0 && type < typeCount && classIds[type] == AudioClipClassId)
                        clips.Add(new KeyValuePair<long, int>(start, size));
                }

                foreach (KeyValuePair<long, int> clip in clips)
                {
                    var data = new Reader(ReadBytes(stream, dataOffset + clip.Key, clip.Value), littleEndian);
                    var entry = new Entry { Name = data.AlignedString() };
                    entry.LoadType = data.Int32();
                    entry.Channels = data.Int32();
                    entry.Frequency = data.Int32();
                    data.Int32();                    // bits per sample
                    data.Skip(4);                    // length
                    data.Byte();                     // tracker format
                    data.Byte();                     // ambisonic
                    data.Align();
                    data.Int32();                    // subsound index
                    data.Skip(3);                    // preload, load in background, legacy 3D
                    data.Align();
                    string source = data.AlignedString();
                    entry.Offset = data.Int64();
                    entry.Size = data.Int64();
                    if (!string.IsNullOrEmpty(source) && source.IndexOf(':') < 0 && entry.Size > 0)
                    {
                        string resource = Path.Combine(directory, source);
                        if (File.Exists(resource)) entry.ResourcePath = resource;
                    }
                    result.Add(entry);
                }
            }
            return result;
        }

        private static byte[] ReadBytes(Stream stream, long position, int count)
        {
            byte[] buffer = new byte[count];
            stream.Position = position;
            int read = 0;
            while (read < count)
            {
                int n = stream.Read(buffer, read, count - read);
                if (n <= 0) throw new EndOfStreamException();
                read += n;
            }
            return buffer;
        }

        private static uint BigEndian32(byte[] b, int i)
        {
            return (uint)(b[i] << 24 | b[i + 1] << 16 | b[i + 2] << 8 | b[i + 3]);
        }

        private static ulong BigEndian64(byte[] b, int i)
        {
            return (ulong)BigEndian32(b, i) << 32 | BigEndian32(b, i + 4);
        }

        private sealed class Reader
        {
            private readonly byte[] _data;
            private readonly bool _little;
            private int _position;

            internal Reader(byte[] data, bool littleEndian)
            {
                _data = data;
                _little = littleEndian;
            }

            internal void Skip(int count) { _position += count; }
            internal void Align() { _position = (_position + 3) & ~3; }
            internal byte Byte() { return _data[_position++]; }

            internal short Int16() { return (short)Number(2); }
            internal int Int32() { return (int)Number(4); }
            internal uint UInt32() { return (uint)Number(4); }
            internal long Int64() { return (long)Number(8); }

            private ulong Number(int size)
            {
                ulong value = 0;
                for (int i = 0; i < size; i++)
                {
                    int index = _little ? _position + size - 1 - i : _position + i;
                    value = value << 8 | _data[index];
                }
                _position += size;
                return value;
            }

            internal string CString()
            {
                int end = Array.IndexOf(_data, (byte)0, _position);
                string value = Encoding.UTF8.GetString(_data, _position, end - _position);
                _position = end + 1;
                return value;
            }

            internal string AlignedString()
            {
                int length = Int32();
                string value = Encoding.UTF8.GetString(_data, _position, length);
                _position += length;
                Align();
                return value;
            }
        }
    }
}
