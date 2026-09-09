// Ported from twogood/unshield (https://github.com/twogood/unshield), MIT licence,
// Copyright (c) 2003 David Eriksson — lib/cabfile.h (on-disk structs), lib/libunshield.c
// (common header, cabinet descriptor, file table, file-group / component offset lists),
// lib/file.c (IS5 file descriptors, volume header, chunked zlib extraction, deobfuscation),
// lib/file_group.c, lib/component.c, lib/directory.c and lib/helper.c. Sources taken from the
// master branch on 2026-09-05. Full licence text in THIRD_PARTY_LICENSES.

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace BethesdaMultitool.Core.Formats.InstallShield;

/// <summary>
///     An InstallShield 5 cabinet (<c>DATA1.CAB</c>, magic <c>"ISc("</c>). Redguard's Disc 1 ships the
///     entire install — including the 3dfx <c>fxart</c> textures the Steam build omits — inside one
///     such cabinet, so the game files are unreachable through the ISO9660 layer alone.
///     <para>
///         Layout, after unshield: a 20-byte common header (signature, version, volume info, and the
///         offset + size of the cabinet descriptor), a 40-byte IS5 volume header, then the cabinet
///         descriptor. The descriptor holds a file-table offset, the directory and file counts and
///         two 71-slot offset arrays for file groups and components. The file table is an array of
///         <c>directoryCount + fileCount</c> dword offsets (relative to the table) — the first run
///         names the directories, the rest point at 0x3a-byte file descriptors, and every string
///         offset in the file is relative to the same table.
///     </para>
///     <para>
///         File data sits at the descriptor's absolute <c>DataOffset</c>. A compressed file is a
///         sequence of chunks, each a little-endian u16 length followed by that many bytes of a
///         complete raw-deflate stream; a stored file is its bytes verbatim. An obfuscated file has
///         every byte transformed by a position-seeded rotate/xor before either interpretation.
///     </para>
///     <para>
///         Scope: the InstallShield 5 layout (major version 0 or 5 by unshield's decoding of the
///         version dword) in a single volume. IS6+ descriptors are a different shape and multi-volume
///         spans need the neighbouring <c>DATAn.CAB</c>; both are rejected explicitly rather than
///         guessed at. unshield's "old" fallback (scanning for end-of-chunk markers) is not ported —
///         no retail cabinet here needs it.
///     </para>
/// </summary>
internal sealed class InstallShieldCabinet
{
    /// <summary><c>"ISc("</c> as a little-endian dword.</summary>
    public const uint Signature = 0x28635349;

    /// <summary>Bytes in the common header that opens every cabinet and header file.</summary>
    public const int CommonHeaderSize = 20;

    /// <summary>Bytes in the IS5 volume header that follows the common header.</summary>
    public const int VolumeHeaderSize = 40;

    /// <summary>Bytes in an IS5 file descriptor.</summary>
    public const int FileDescriptorSize = 0x3a;

    /// <summary>Slots in each of the descriptor's file-group and component offset arrays.</summary>
    public const int OffsetSlotCount = 71;

    /// <summary>Offset of the file-group offset array within the cabinet descriptor.</summary>
    public const int FileGroupOffsetsPosition = 0x3e;

    /// <summary>Offset of the component offset array within the cabinet descriptor.</summary>
    public const int ComponentOffsetsPosition = 0x15a;

    /// <summary>Bytes of fixed cabinet-descriptor fields: both offset arrays end here.</summary>
    public const int CabDescriptorFixedSize = ComponentOffsetsPosition + OffsetSlotCount * sizeof(uint);

    /// <summary>File descriptor flag: the file continues in the next volume.</summary>
    public const ushort FlagSplit = 1;

    /// <summary>File descriptor flag: every data byte is obfuscated.</summary>
    public const ushort FlagObfuscated = 2;

    /// <summary>File descriptor flag: the data is chunked raw deflate.</summary>
    public const ushort FlagCompressed = 4;

    /// <summary>File descriptor flag: the entry is a placeholder with no data.</summary>
    public const ushort FlagInvalid = 8;

    /// <summary>Upper bound on offset-list walks, so a cyclic list cannot spin forever.</summary>
    private const int MaxListWalk = 4096;

    private InstallShieldCabinet(
        string filePath,
        long fileLength,
        uint version,
        int majorVersion,
        uint volumeInfo,
        InstallShieldVolumeHeader volume,
        IReadOnlyList<string> directories,
        IReadOnlyList<InstallShieldFileDescriptor> files,
        IReadOnlyList<InstallShieldFileGroup> fileGroups,
        IReadOnlyList<InstallShieldComponent> components)
    {
        FilePath = filePath;
        FileLength = fileLength;
        Version = version;
        MajorVersion = majorVersion;
        VolumeInfo = volumeInfo;
        Volume = volume;
        Directories = directories;
        Files = files;
        FileGroups = fileGroups;
        Components = components;
    }

    public string FilePath { get; }

    public long FileLength { get; }

    /// <summary>The raw version dword (Redguard: <c>0x01000004</c>).</summary>
    public uint Version { get; }

    /// <summary>unshield's decoding of <see cref="Version" />: 0 or 5 for the IS5 layout.</summary>
    public int MajorVersion { get; }

    public uint VolumeInfo { get; }

    public InstallShieldVolumeHeader Volume { get; }

    /// <summary>Directory names in table order, backslash-separated as InstallShield wrote them.</summary>
    public IReadOnlyList<string> Directories { get; }

    /// <summary>Every file descriptor in table order, including invalid placeholders.</summary>
    public IReadOnlyList<InstallShieldFileDescriptor> Files { get; }

    public IReadOnlyList<InstallShieldFileGroup> FileGroups { get; }

    public IReadOnlyList<InstallShieldComponent> Components { get; }

    /// <summary>Magic-only gate: the file opens with <c>"ISc("</c>.</summary>
    public static bool TryProbe(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

            // The magic alone would claim any 4-byte file that happens to start with it, and a
            // probe that claims what Parse cannot open breaks the chain — so require room for the
            // headers and the descriptor's fixed part too.
            if (stream.Length < CommonHeaderSize + VolumeHeaderSize + CabDescriptorFixedSize)
            {
                return false;
            }

            Span<byte> head = stackalloc byte[sizeof(uint)];
            return stream.Read(head) == head.Length &&
                   BinaryPrimitives.ReadUInt32LittleEndian(head) == Signature;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    ///     Parses the cabinet's header region. Throws <see cref="InvalidDataException" /> when the
    ///     signature is wrong, the version is not an IS5 layout, or any table, string or descriptor
    ///     lies outside the file.
    /// </summary>
    public static InstallShieldCabinet Parse(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var fileName = Path.GetFileName(path);
        var fileLength = stream.Length;

        if (fileLength < CommonHeaderSize + VolumeHeaderSize)
        {
            throw Invalid(fileName, "it is shorter than the common and volume headers");
        }

        Span<byte> head = stackalloc byte[CommonHeaderSize + VolumeHeaderSize];
        stream.ReadExactly(head);

        if (BinaryPrimitives.ReadUInt32LittleEndian(head) != Signature)
        {
            throw Invalid(fileName, "it does not open with the \"ISc(\" signature");
        }

        var version = BinaryPrimitives.ReadUInt32LittleEndian(head[4..]);
        var volumeInfo = BinaryPrimitives.ReadUInt32LittleEndian(head[8..]);
        var descriptorOffset = BinaryPrimitives.ReadUInt32LittleEndian(head[12..]);
        var descriptorSize = BinaryPrimitives.ReadUInt32LittleEndian(head[16..]);

        var majorVersion = DecodeMajorVersion(version);
        if (majorVersion is not (0 or 5))
        {
            throw Invalid(fileName,
                $"version 0x{version:X8} decodes to InstallShield {majorVersion}; only the InstallShield 5 layout is supported");
        }

        if (descriptorSize == 0)
        {
            throw Invalid(fileName, "it has no cabinet descriptor");
        }

        var volume = ReadVolumeHeader(head[CommonHeaderSize..]);

        // The descriptor's fixed fields must be present before anything can be located.
        if (descriptorSize < CabDescriptorFixedSize ||
            (long)descriptorOffset + CabDescriptorFixedSize > fileLength)
        {
            throw Invalid(fileName, "its cabinet descriptor lies outside the file");
        }

        Span<byte> fixedFields = stackalloc byte[CabDescriptorFixedSize];
        stream.Position = descriptorOffset;
        stream.ReadExactly(fixedFields);

        var fileTableOffset = BinaryPrimitives.ReadUInt32LittleEndian(fixedFields[0x0c..]);
        var fileTableSize = BinaryPrimitives.ReadUInt32LittleEndian(fixedFields[0x14..]);
        var directoryCount = BinaryPrimitives.ReadUInt32LittleEndian(fixedFields[0x1c..]);
        var fileCount = BinaryPrimitives.ReadUInt32LittleEndian(fixedFields[0x28..]);

        // Header region: everything the descriptor and the file table can reach. Both must be
        // inside the file — a cabinet cut short anywhere in here is rejected, never guessed at.
        var headerEnd = descriptorOffset +
                        Math.Max(descriptorSize, (long)fileTableOffset + fileTableSize);
        if (headerEnd > fileLength || headerEnd > int.MaxValue)
        {
            throw Invalid(fileName,
                $"its descriptor tables end at 0x{headerEnd:X} but the file is 0x{fileLength:X} bytes");
        }

        var header = new byte[headerEnd];
        stream.Position = 0;
        stream.ReadExactly(header);

        var region = new HeaderRegion(header, (int)descriptorOffset, (int)(descriptorOffset + fileTableOffset),
            fileName);

        var tableEntries = (long)directoryCount + fileCount;
        if (tableEntries * sizeof(uint) > fileTableSize)
        {
            throw Invalid(fileName,
                $"its file table declares {directoryCount} directories + {fileCount} files but is only {fileTableSize} bytes");
        }

        var fileTable = new uint[tableEntries];
        for (var i = 0; i < fileTable.Length; i++)
        {
            fileTable[i] = region.ReadTableUInt32(i * sizeof(uint));
        }

        var directories = new string[directoryCount];
        for (var i = 0; i < directories.Length; i++)
        {
            directories[i] = region.ReadTableString(fileTable[i]);
        }

        var files = new InstallShieldFileDescriptor[fileCount];
        for (var i = 0; i < files.Length; i++)
        {
            files[i] = ReadFileDescriptor(region, fileTable[directoryCount + i], i, directories.Length, fileLength);
        }

        var fileGroups = ReadFileGroups(region, fixedFields);
        var components = ReadComponents(region, fixedFields);

        return new InstallShieldCabinet(
            path, fileLength, version, majorVersion, volumeInfo, volume,
            directories, files, fileGroups, components);
    }

    /// <summary>Extracts one file through a fresh handle; see the handle overload for the mechanics.</summary>
    public byte[] Extract(InstallShieldFileDescriptor file)
    {
        using var handle = File.OpenHandle(FilePath);
        return Extract(handle, file);
    }

    /// <summary>
    ///     Extracts one file, reading through <paramref name="handle" /> with positioned reads only —
    ///     safe for any number of concurrent callers sharing the handle. The result is exactly the
    ///     declared expanded size or an <see cref="InvalidDataException" /> is thrown.
    /// </summary>
    public byte[] Extract(SafeFileHandle handle, InstallShieldFileDescriptor file)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(file);

        if (!file.IsValid)
        {
            throw new InvalidDataException(
                $"InstallShield entry '{file.Name}' is an invalid placeholder with no data.");
        }

        if (file.IsSplit)
        {
            throw new InvalidDataException(
                $"InstallShield entry '{file.Name}' continues in another volume; only single-volume cabinets are supported.");
        }

        var reader = new DataReader(handle, file, FileLength);
        var output = new byte[file.ExpandedSize];

        if (!file.IsCompressed)
        {
            reader.Read(output);
            return output;
        }

        var bytesLeft = (long)file.CompressedSize;
        var written = 0;
        Span<byte> lengthBytes = stackalloc byte[sizeof(ushort)];
        var chunk = Array.Empty<byte>();

        while (bytesLeft > 0)
        {
            if (bytesLeft < sizeof(ushort))
            {
                throw Corrupt(file, "a chunk length straddles the end of the compressed data");
            }

            reader.Read(lengthBytes);
            var chunkLength = BinaryPrimitives.ReadUInt16LittleEndian(lengthBytes);
            bytesLeft -= sizeof(ushort);

            if (chunkLength == 0)
            {
                throw Corrupt(file, "a chunk declares a length of zero");
            }

            if (chunkLength > bytesLeft)
            {
                throw Corrupt(file, $"a chunk of {chunkLength} bytes overruns the {bytesLeft} compressed bytes left");
            }

            // unshield appends one zero byte "to make inflate happy"; a complete stream ignores it.
            if (chunk.Length < chunkLength + 1)
            {
                chunk = new byte[Math.Max(chunkLength + 1, 64 * 1024)];
            }

            reader.Read(chunk.AsSpan(0, chunkLength));
            chunk[chunkLength] = 0;
            bytesLeft -= chunkLength;

            written += Inflate(chunk, chunkLength + 1, output.AsSpan(written), file);
        }

        if (written != output.Length)
        {
            throw Corrupt(file, $"it expanded to {written} bytes, not the declared {output.Length}");
        }

        return output;
    }

    /// <summary>
    ///     unshield's <c>unshield_deobfuscate</c>: each byte is xor'd with 0xD5, rotated right by two,
    ///     and reduced by its position modulo 0x47. <paramref name="seed" /> is the running position
    ///     and advances by the span length so successive reads continue the sequence.
    /// </summary>
    public static void Deobfuscate(Span<byte> buffer, ref uint seed)
    {
        var position = seed;
        for (var i = 0; i < buffer.Length; i++, position++)
        {
            var value = (byte)(buffer[i] ^ 0xd5);
            value = (byte)((value >> 2) | (value << 6));
            buffer[i] = (byte)(value - position % 0x47);
        }

        seed = position;
    }

    /// <summary>
    ///     unshield's version decoding: a 0x01xxxxxx dword carries the major version in bits 12-15
    ///     (Redguard's 0x01000004 → 0), a 0x02/0x04 dword carries it as a decimal times 100.
    /// </summary>
    public static int DecodeMajorVersion(uint version)
    {
        var family = version >> 24;
        if (family == 1)
        {
            return (int)((version >> 12) & 0xf);
        }

        if (family is 2 or 4)
        {
            var major = (int)(version & 0xffff);
            return major == 0 ? 0 : major / 100;
        }

        return -1;
    }

    private static InstallShieldVolumeHeader ReadVolumeHeader(ReadOnlySpan<byte> raw)
    {
        var lastFileOffset = BinaryPrimitives.ReadUInt32LittleEndian(raw[28..]);
        return new InstallShieldVolumeHeader(
            BinaryPrimitives.ReadUInt32LittleEndian(raw),
            BinaryPrimitives.ReadUInt32LittleEndian(raw[8..]),
            BinaryPrimitives.ReadUInt32LittleEndian(raw[12..]),
            BinaryPrimitives.ReadUInt32LittleEndian(raw[16..]),
            BinaryPrimitives.ReadUInt32LittleEndian(raw[20..]),
            BinaryPrimitives.ReadUInt32LittleEndian(raw[24..]),
            lastFileOffset == 0 ? int.MaxValue : lastFileOffset,
            BinaryPrimitives.ReadUInt32LittleEndian(raw[32..]),
            BinaryPrimitives.ReadUInt32LittleEndian(raw[36..]));
    }

    private static InstallShieldFileDescriptor ReadFileDescriptor(
        HeaderRegion region, uint tableOffset, int index, int directoryCount, long fileLength)
    {
        var raw = region.SliceTable(tableOffset, FileDescriptorSize, $"file descriptor {index}");

        var nameOffset = BinaryPrimitives.ReadUInt32LittleEndian(raw);
        var directoryIndex = BinaryPrimitives.ReadUInt16LittleEndian(raw[4..]);
        var flags = BinaryPrimitives.ReadUInt16LittleEndian(raw[8..]);
        var expandedSize = BinaryPrimitives.ReadUInt32LittleEndian(raw[0x0a..]);
        var compressedSize = BinaryPrimitives.ReadUInt32LittleEndian(raw[0x0e..]);
        var dataOffset = BinaryPrimitives.ReadUInt32LittleEndian(raw[0x26..]);

        if (directoryIndex >= directoryCount)
        {
            throw Invalid(region.FileName,
                $"file descriptor {index} names directory {directoryIndex} of {directoryCount}");
        }

        var name = nameOffset == 0 ? string.Empty : region.ReadTableString(nameOffset);
        var descriptor = new InstallShieldFileDescriptor(
            index, name, directoryIndex, flags, expandedSize, compressedSize, dataOffset);

        // A live entry's data must lie inside this volume. Split entries are the one legitimate
        // exception (their tail is in the next DATAn.CAB) and are refused at extraction instead.
        if (descriptor.IsValid && !descriptor.IsSplit &&
            dataOffset + (long)descriptor.StoredSize > fileLength)
        {
            throw Invalid(region.FileName,
                $"file descriptor {index} ('{name}') stores {descriptor.StoredSize} bytes at 0x{dataOffset:X}, past the end of the file");
        }

        return descriptor;
    }

    private static List<InstallShieldFileGroup> ReadFileGroups(HeaderRegion region, ReadOnlySpan<byte> fixedFields)
    {
        var groups = new List<InstallShieldFileGroup>();
        foreach (var descriptorOffset in WalkOffsetLists(region, fixedFields, FileGroupOffsetsPosition))
        {
            // IS5 group descriptor: name offset, 0x48 bytes of settings, then first/last file index.
            var raw = region.SliceDescriptor(descriptorOffset, 0x54, "file group descriptor");
            groups.Add(new InstallShieldFileGroup(
                region.ReadDescriptorString(BinaryPrimitives.ReadUInt32LittleEndian(raw)),
                BinaryPrimitives.ReadInt32LittleEndian(raw[0x4c..]),
                BinaryPrimitives.ReadInt32LittleEndian(raw[0x50..])));
        }

        return groups;
    }

    private static List<InstallShieldComponent> ReadComponents(HeaderRegion region, ReadOnlySpan<byte> fixedFields)
    {
        var components = new List<InstallShieldComponent>();
        foreach (var descriptorOffset in WalkOffsetLists(region, fixedFields, ComponentOffsetsPosition))
        {
            // IS5 component descriptor: name offset, 0x6c bytes of settings, u16 group count, then
            // the offset of a table of group-name offsets.
            var raw = region.SliceDescriptor(descriptorOffset, 0x76, "component descriptor");
            var name = region.ReadDescriptorString(BinaryPrimitives.ReadUInt32LittleEndian(raw));
            int groupCount = BinaryPrimitives.ReadUInt16LittleEndian(raw[0x70..]);
            if (groupCount > OffsetSlotCount)
            {
                throw Invalid(region.FileName, $"component '{name}' claims {groupCount} file groups");
            }

            var tableOffset = BinaryPrimitives.ReadUInt32LittleEndian(raw[0x72..]);
            var table = region.SliceDescriptor(tableOffset, groupCount * sizeof(uint), "component file-group table");
            var groupNames = new string[groupCount];
            for (var i = 0; i < groupCount; i++)
            {
                groupNames[i] = region.ReadDescriptorString(
                    BinaryPrimitives.ReadUInt32LittleEndian(table[(i * sizeof(uint))..]));
            }

            components.Add(new InstallShieldComponent(name, groupNames));
        }

        return components;
    }

    /// <summary>
    ///     Walks the 71-slot offset array at <paramref name="arrayPosition" />: each non-zero slot
    ///     heads a linked list of (name offset, descriptor offset, next offset) triples.
    /// </summary>
    private static List<uint> WalkOffsetLists(HeaderRegion region, ReadOnlySpan<byte> fixedFields, int arrayPosition)
    {
        var descriptors = new List<uint>();
        for (var slot = 0; slot < OffsetSlotCount; slot++)
        {
            var next = BinaryPrimitives.ReadUInt32LittleEndian(fixedFields[(arrayPosition + slot * sizeof(uint))..]);
            var walked = 0;
            while (next != 0)
            {
                if (++walked > MaxListWalk)
                {
                    throw Invalid(region.FileName, $"offset list in slot {slot} does not terminate");
                }

                var raw = region.SliceDescriptor(next, 3 * sizeof(uint), "offset list node");
                descriptors.Add(BinaryPrimitives.ReadUInt32LittleEndian(raw[4..]));
                next = BinaryPrimitives.ReadUInt32LittleEndian(raw[8..]);
            }
        }

        return descriptors;
    }

    /// <summary>
    ///     Inflates one chunk (a complete raw-deflate stream) into <paramref name="destination" />,
    ///     returning the byte count. Producing more than fits means the declared expanded size was
    ///     wrong, which is reported as corruption rather than truncated silently.
    /// </summary>
    private static int Inflate(byte[] chunk, int chunkLength, Span<byte> destination, InstallShieldFileDescriptor file)
    {
        using var source = new MemoryStream(chunk, 0, chunkLength, false);
        using var inflater = new DeflateStream(source, CompressionMode.Decompress);

        int total;
        bool overflow;
        try
        {
            total = ReadToEnd(inflater, destination, out overflow);
        }
        catch (InvalidDataException e)
        {
            throw Corrupt(file, $"a deflate chunk is malformed ({e.Message})");
        }

        if (overflow)
        {
            throw Corrupt(file, "it expands past the declared expanded size");
        }

        return total;
    }

    private static int ReadToEnd(DeflateStream inflater, Span<byte> destination, out bool overflow)
    {
        var total = 0;
        while (total < destination.Length)
        {
            var read = inflater.Read(destination[total..]);
            if (read == 0)
            {
                overflow = false;
                return total;
            }

            total += read;
        }

        // The output is full; the stream must be exhausted too.
        Span<byte> probe = stackalloc byte[1];
        overflow = inflater.Read(probe) != 0;
        return total;
    }

    private static InvalidDataException Invalid(string fileName, string reason)
    {
        return new InvalidDataException($"'{fileName}' is not a readable InstallShield 5 cabinet: {reason}.");
    }

    private static InvalidDataException Corrupt(InstallShieldFileDescriptor file, string reason)
    {
        return new InvalidDataException($"InstallShield entry '{file.Name}' is corrupt: {reason}.");
    }

    /// <summary>
    ///     The in-memory header prefix with the two bases every offset is relative to: the cabinet
    ///     descriptor (file-group/component structures) and the file table (names and file
    ///     descriptors). Every slice is bounds-checked against the region.
    /// </summary>
    private sealed class HeaderRegion
    {
        private readonly byte[] _bytes;
        private readonly int _descriptorBase;
        private readonly int _tableBase;

        public HeaderRegion(byte[] bytes, int descriptorBase, int tableBase, string fileName)
        {
            _bytes = bytes;
            _descriptorBase = descriptorBase;
            _tableBase = tableBase;
            FileName = fileName;
        }

        public string FileName { get; }

        public uint ReadTableUInt32(long offset)
        {
            return BinaryPrimitives.ReadUInt32LittleEndian(Slice(_tableBase, offset, sizeof(uint), "file table"));
        }

        public ReadOnlySpan<byte> SliceTable(long offset, int length, string what)
        {
            return Slice(_tableBase, offset, length, what);
        }

        public ReadOnlySpan<byte> SliceDescriptor(long offset, int length, string what)
        {
            return Slice(_descriptorBase, offset, length, what);
        }

        public string ReadTableString(long offset)
        {
            return ReadString(_tableBase, offset);
        }

        public string ReadDescriptorString(long offset)
        {
            return ReadString(_descriptorBase, offset);
        }

        private ReadOnlySpan<byte> Slice(int baseOffset, long offset, int length, string what)
        {
            var start = baseOffset + offset;
            if (offset < 0 || start + length > _bytes.Length)
            {
                throw Invalid(FileName, $"{what} at 0x{start:X} lies outside the header region");
            }

            return _bytes.AsSpan((int)start, length);
        }

        /// <summary>NUL-terminated single-byte string; IS5 predates the UTF-16 tables of IS17+.</summary>
        private string ReadString(int baseOffset, long offset)
        {
            var start = baseOffset + offset;
            if (offset < 0 || start >= _bytes.Length)
            {
                throw Invalid(FileName, $"string at 0x{start:X} lies outside the header region");
            }

            var span = _bytes.AsSpan((int)start);
            var terminator = span.IndexOf((byte)0);
            if (terminator < 0)
            {
                throw Invalid(FileName, $"string at 0x{start:X} is not terminated inside the header region");
            }

            return Encoding.Latin1.GetString(span[..terminator]);
        }
    }

    /// <summary>
    ///     Sequential positioned reads over one file's stored bytes, with the obfuscation seed
    ///     carried across reads exactly as unshield's reader does. One instance per extraction, so
    ///     the shared handle needs no lock.
    /// </summary>
    private sealed class DataReader
    {
        private readonly long _end;
        private readonly InstallShieldFileDescriptor _file;
        private readonly SafeFileHandle _handle;
        private long _position;
        private uint _seed;

        public DataReader(SafeFileHandle handle, InstallShieldFileDescriptor file, long fileLength)
        {
            _handle = handle;
            _file = file;
            _position = file.DataOffset;
            _end = Math.Min(fileLength, file.DataOffset + file.StoredSize);
        }

        public void Read(Span<byte> destination)
        {
            if (_position + destination.Length > _end)
            {
                throw Corrupt(_file,
                    $"a read of {destination.Length} bytes at 0x{_position:X} runs past its stored data");
            }

            var filled = 0;
            while (filled < destination.Length)
            {
                var read = RandomAccess.Read(_handle, destination[filled..], _position + filled);
                if (read <= 0)
                {
                    throw Corrupt(_file, $"the cabinet ended at 0x{_position + filled:X}");
                }

                filled += read;
            }

            _position += destination.Length;

            if (_file.IsObfuscated)
            {
                Deobfuscate(destination, ref _seed);
            }
        }
    }
}

/// <summary>
///     The IS5 volume header: where this volume's data starts and which file indices span its
///     first and last entries (with their in-volume offsets and sizes, for split files).
///     <see cref="LastFileOffset" /> reads as <see cref="int.MaxValue" /> when stored as zero, as
///     unshield normalises it.
/// </summary>
internal sealed record InstallShieldVolumeHeader(
    uint DataOffset,
    uint FirstFileIndex,
    uint LastFileIndex,
    uint FirstFileOffset,
    uint FirstFileSizeExpanded,
    uint FirstFileSizeCompressed,
    uint LastFileOffset,
    uint LastFileSizeExpanded,
    uint LastFileSizeCompressed);

/// <summary>One IS5 file descriptor. <see cref="DataOffset" /> is absolute within the cabinet.</summary>
internal sealed record InstallShieldFileDescriptor(
    int Index,
    string Name,
    int DirectoryIndex,
    ushort Flags,
    uint ExpandedSize,
    uint CompressedSize,
    uint DataOffset)
{
    public bool IsCompressed => (Flags & InstallShieldCabinet.FlagCompressed) != 0;

    public bool IsObfuscated => (Flags & InstallShieldCabinet.FlagObfuscated) != 0;

    public bool IsSplit => (Flags & InstallShieldCabinet.FlagSplit) != 0;

    /// <summary>unshield's <c>unshield_file_is_valid</c>: not flagged invalid, and both a name and data present.</summary>
    public bool IsValid => (Flags & InstallShieldCabinet.FlagInvalid) == 0 && Name.Length > 0 && DataOffset != 0;

    /// <summary>Bytes occupied in the cabinet: the compressed size, or the expanded size when stored.</summary>
    public uint StoredSize => IsCompressed ? CompressedSize : ExpandedSize;
}

/// <summary>A file group: a named, contiguous run of file indices (inclusive).</summary>
internal sealed record InstallShieldFileGroup(string Name, int FirstFile, int LastFile);

/// <summary>An installable component and the file groups it selects.</summary>
internal sealed record InstallShieldComponent(string Name, IReadOnlyList<string> FileGroupNames);
