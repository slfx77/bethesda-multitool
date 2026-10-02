using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Nif.Parser;

namespace BethesdaMultitool.Core.Formats.Nif.Decoding;

/// <summary>
///     Where each part of a Bethesda NIF header sits, re-read independently of <see cref="NifParser" /> so the
///     decoder can keep the raw string table and check the parser's block table against the bytes. NifParser exposes
///     no positions and decodes strings lossily, so this walk exists; it is checked against the parser's
///     <see cref="NifInfo" /> and refuses to proceed if the two disagree. Headers at 20.2.0.5 and later carry the
///     Block Size array and the string table; the one earlier identity the decoder reads, little-endian 20.0.0.4 at
///     user 10 or 11 with BS 11 (the five FNV-shipped <c>.kf</c>, cut 2), has neither, so its block sizes come from
///     NifParser's legacy measure walk (Parser/NifParser.cs, MeasureLegacyBlocks) and its string table is empty
///     (every string is stored inline; nif.xml <c>string</c> until 20.0.0.5).
/// </summary>
/// <remarks>
///     Byte order: the header string, version, user version, block count and BS version are little-endian even in a
///     big-endian file (Parser/NifParser.cs:453-515); everything from Num Block Types on (type names, type indices,
///     block sizes, the string table and groups) is in the file's byte order (NifParser.cs:81, 569, 590-597, 650-674,
///     614), as are all block bodies and the footer.
/// </remarks>
internal sealed class NifHeaderLayout
{
    private const uint FirstVersionWithBlockSizes = 0x14020005;
    private const int MaxHeaderLineSearch = 60;
    private const int MaxBlockTypeNameLength = 256;

    /// <summary>The one pre-20.2.0.5 identity the decoder reads (<see cref="NifVersions.Gamebryo20004" />).</summary>
    private const uint LegacyKfVersion = NifVersions.Gamebryo20004;

    /// <summary>The BS stream version of that identity (the FNV-shipped 20.0.0.4 <c>.kf</c> are BS 11).</summary>
    private const uint LegacyKfBsVersion = 11;

    private NifHeaderLayout()
    {
    }

    /// <summary>The header line (without its newline) as Latin-1 text.</summary>
    public string HeaderString { get; private init; } = "";

    /// <summary>The binary version, e.g. 0x14020007.</summary>
    public uint Version { get; private init; }

    /// <summary>True when the endian byte is 0.</summary>
    public bool IsBigEndian { get; private init; }

    /// <summary>The user version.</summary>
    public uint UserVersion { get; private init; }

    /// <summary>The BS stream version (0 when the header has no BSStreamHeader).</summary>
    public uint BsVersion { get; private init; }

    /// <summary>The number of blocks.</summary>
    public int BlockCount { get; private init; }

    /// <summary>The block-type names as Latin-1 text.</summary>
    public IReadOnlyList<string> BlockTypeNames { get; private init; } = [];

    /// <summary>Each block's index into <see cref="BlockTypeNames" />.</summary>
    public IReadOnlyList<ushort> BlockTypeIndices { get; private init; } = [];

    /// <summary>Each block's declared size in bytes.</summary>
    public IReadOnlyList<uint> BlockSizes { get; private init; } = [];

    /// <summary>The raw string table.</summary>
    public NifHeaderStringTable Strings { get; private init; } = new(0, 0, []);

    /// <summary>The group sizes.</summary>
    public IReadOnlyList<uint> Groups { get; private init; } = [];

    /// <summary>The absolute offset one past the header, where the first block must start.</summary>
    public int HeaderEnd { get; private init; }

    /// <summary>
    ///     Re-reads the header of <paramref name="file" /> and checks it against <paramref name="info" />.
    /// </summary>
    /// <exception cref="NotSupportedException">
    ///     The version predates the Block Size array (20.2.0.5) and is not the one legacy identity the decoder reads
    ///     (little-endian 20.0.0.4, user 10 or 11, BS 11).
    /// </exception>
    /// <exception cref="InvalidDataException">
    ///     The header is truncated or implausible, or disagrees with what <see cref="NifParser" /> recorded.
    /// </exception>
    public static NifHeaderLayout Read(ReadOnlySpan<byte> file, NifInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (info.BinaryVersion < FirstVersionWithBlockSizes)
        {
            return ReadLegacyOblivionKf(file, info);
        }

        var newline = file[..Math.Min(MaxHeaderLineSearch, file.Length)].IndexOf((byte)0x0A);
        if (newline < 0)
        {
            throw new InvalidDataException("NIF header line has no newline within its first 60 bytes.");
        }

        var reader = new HeaderReader(file, newline + 1);
        var version = reader.U32Le("Version");
        var bigEndian = reader.U8("Endian Type") == 0;
        var userVersion = NifVersions.HasUserVersion(version) ? reader.U32Le("User Version") : 0u;
        var blockCount = reader.U32Le("Num Blocks");
        if (blockCount > int.MaxValue)
        {
            throw new InvalidDataException($"NIF header declares {blockCount} blocks.");
        }

        var bsVersion = 0u;
        if (NifVersions.HasBsStreamHeader(version, userVersion))
        {
            bsVersion = reader.U32Le("BS Version");
            SkipStreamHeaderStrings(ref reader, bsVersion);
        }

        reader.BigEndian = bigEndian;
        var typeCount = reader.U16("Num Block Types");
        var typeNames = new string[typeCount];
        for (var i = 0; i < typeCount; i++)
        {
            var length = reader.U32("Block Type name length");
            if (length > MaxBlockTypeNameLength)
            {
                throw new InvalidDataException($"NIF block type name {i} declares {length} bytes.");
            }

            typeNames[i] = Encoding.Latin1.GetString(reader.Bytes((int)length, "Block Type name"));
        }

        var count = (int)blockCount;
        reader.RequireCount(count, 6, "Block Type Index / Block Size arrays");
        var typeIndices = new ushort[count];
        for (var i = 0; i < count; i++)
        {
            typeIndices[i] = reader.U16("Block Type Index");
        }

        var sizes = new uint[count];
        for (var i = 0; i < count; i++)
        {
            sizes[i] = reader.U32("Block Size");
        }

        var stringTableOffset = reader.Position;
        var stringCount = reader.U32("Num Strings");
        var maxStringLength = reader.U32("Max String Length");
        reader.RequireCount(stringCount, 4, "string table");
        var strings = new byte[(int)stringCount][];
        for (var i = 0; i < strings.Length; i++)
        {
            var length = reader.U32("string length");
            reader.RequireCount(length, 1, "string");
            strings[i] = reader.Bytes((int)length, "string").ToArray();
        }

        var groupCount = reader.U32("Num Groups");
        reader.RequireCount(groupCount, 4, "groups");
        var groups = new uint[(int)groupCount];
        for (var i = 0; i < groups.Length; i++)
        {
            groups[i] = reader.U32("Group");
        }

        var layout = new NifHeaderLayout
        {
            HeaderString = Encoding.Latin1.GetString(file[..newline]),
            Version = version,
            IsBigEndian = bigEndian,
            UserVersion = userVersion,
            BsVersion = bsVersion,
            BlockCount = count,
            BlockTypeNames = typeNames,
            BlockTypeIndices = typeIndices,
            BlockSizes = sizes,
            Strings = new NifHeaderStringTable(stringTableOffset, maxStringLength, strings),
            Groups = groups,
            HeaderEnd = reader.Position
        };
        layout.CheckAgainst(info);
        return layout;
    }

    /// <summary>
    ///     Re-reads a pre-20.2.0.5 header. Only little-endian 20.0.0.4 at user 10 or 11 with BS 11 is read (the
    ///     FNV-shipped <c>.kf</c> identity, measured over the five files in
    ///     <c>TestOutput/cut2-prep-20260928/kf2004/report.txt</c>); every other early identity keeps the
    ///     <see cref="NotSupportedException" /> refusal. The header holds no Block Size array (since 20.2.0.5) and no
    ///     string table (since 20.1.0.1), so the block sizes are taken from NifParser's legacy measure walk (the
    ///     schema converter, an implementation independent of <see cref="NifBlockWalker" />, whose walk the decoder
    ///     then checks block by block against those sizes) and <see cref="Strings" /> is empty with its offset at the
    ///     header end (nothing is stored there; block strings are inline SizedStrings).
    /// </summary>
    /// <exception cref="NotSupportedException">The identity is not the one legacy identity the decoder reads.</exception>
    /// <exception cref="InvalidDataException">
    ///     The header is truncated or implausible, the measure walk could not size every block, or the re-read
    ///     disagrees with what <see cref="NifParser" /> recorded.
    /// </exception>
    private static NifHeaderLayout ReadLegacyOblivionKf(ReadOnlySpan<byte> file, NifInfo info)
    {
        if (info.BinaryVersion != LegacyKfVersion || info.IsBigEndian ||
            info.UserVersion is not (10 or 11) || info.BsVersion != LegacyKfBsVersion)
        {
            throw new NotSupportedException(
                $"NIF version 0x{info.BinaryVersion:X8} has no Block Size array; the block decoder reads 20.2.0.5 " +
                "or later, and of the earlier versions only little-endian 20.0.0.4 at user 10 or 11, BS 11 (the " +
                "FNV-shipped .kf identity), whose block sizes come from the legacy measure walk.");
        }

        if (info.Blocks.Count != info.BlockCount)
        {
            throw new InvalidDataException(
                $"NIF legacy header at 20.0.0.4 declares {info.BlockCount} blocks but the measure walk sized " +
                $"{info.Blocks.Count}; without a header Block Size array an unsized block leaves no trustworthy " +
                "layout.");
        }

        var newline = file[..Math.Min(MaxHeaderLineSearch, file.Length)].IndexOf((byte)0x0A);
        if (newline < 0)
        {
            throw new InvalidDataException("NIF header line has no newline within its first 60 bytes.");
        }

        var reader = new HeaderReader(file, newline + 1);
        var version = reader.U32Le("Version");
        var bigEndian = reader.U8("Endian Type") == 0;
        var userVersion = reader.U32Le("User Version"); // present since 10.0.1.8
        var blockCount = reader.U32Le("Num Blocks");
        if (blockCount > int.MaxValue)
        {
            throw new InvalidDataException($"NIF header declares {blockCount} blocks.");
        }

        // #BSSTREAMHEADER# holds for this identity (10.1.0.0 to 20.0.0.4 with user version 3 to 11).
        var bsVersion = reader.U32Le("BS Version");
        SkipStreamHeaderStrings(ref reader, bsVersion);
        reader.BigEndian = bigEndian;
        var typeCount = reader.U16("Num Block Types");
        var typeNames = new string[typeCount];
        for (var i = 0; i < typeCount; i++)
        {
            var length = reader.U32("Block Type name length");
            if (length > MaxBlockTypeNameLength)
            {
                throw new InvalidDataException($"NIF block type name {i} declares {length} bytes.");
            }

            typeNames[i] = Encoding.Latin1.GetString(reader.Bytes((int)length, "Block Type name"));
        }

        var count = (int)blockCount;
        reader.RequireCount(count, 2, "Block Type Index array");
        var typeIndices = new ushort[count];
        for (var i = 0; i < count; i++)
        {
            typeIndices[i] = reader.U16("Block Type Index");
        }

        // No Block Size array and no Num Strings / Max String Length / Strings here: 20.0.0.4 predates both.
        var groupCount = reader.U32("Num Groups");
        reader.RequireCount(groupCount, 4, "groups");
        var groups = new uint[(int)groupCount];
        for (var i = 0; i < groups.Length; i++)
        {
            groups[i] = reader.U32("Group");
        }

        var sizes = new uint[count];
        for (var i = 0; i < count; i++)
        {
            sizes[i] = (uint)info.Blocks[i].Size;
        }

        var layout = new NifHeaderLayout
        {
            HeaderString = Encoding.Latin1.GetString(file[..newline]),
            Version = version,
            IsBigEndian = bigEndian,
            UserVersion = userVersion,
            BsVersion = bsVersion,
            BlockCount = count,
            BlockTypeNames = typeNames,
            BlockTypeIndices = typeIndices,
            BlockSizes = sizes,
            Strings = new NifHeaderStringTable(reader.Position, 0, []),
            Groups = groups,
            HeaderEnd = reader.Position
        };
        layout.CheckAgainst(info);
        return layout;
    }

    /// <summary>
    ///     Skips the BSStreamHeader export strings with exactly NifParser's rules (Parser/NifParser.cs:508-552):
    ///     Author; an extra uint above BS 130; Process Script below BS 131; Export Script; Max Filepath for BS 103-169;
    ///     Starfield's extra export string from BS 170. Each string is a length byte (terminator included) plus bytes.
    /// </summary>
    private static void SkipStreamHeaderStrings(ref HeaderReader reader, uint bsVersion)
    {
        reader.SkipExportString("Author");
        if (bsVersion > 130)
        {
            reader.Bytes(4, "Unknown Int");
        }

        if (bsVersion < 131)
        {
            reader.SkipExportString("Process Script");
        }

        reader.SkipExportString("Export Script");
        if (bsVersion is >= 103 and < 170)
        {
            reader.SkipExportString("Max Filepath");
        }

        if (bsVersion >= 170)
        {
            reader.SkipExportString("Unknown Data");
        }
    }

    private void CheckAgainst(NifInfo info)
    {
        Require(Version == info.BinaryVersion, "version");
        Require(IsBigEndian == info.IsBigEndian, "endianness");
        Require(UserVersion == info.UserVersion, "user version");
        Require(BsVersion == info.BsVersion, "BS version");
        Require(BlockCount == info.BlockCount && info.Blocks.Count == BlockCount,
            $"block count (header {BlockCount}, parser {info.BlockCount} with {info.Blocks.Count} listed)");
        Require(BlockTypeNames.Count == info.BlockTypeNames.Count, "block type count");
        for (var i = 0; i < BlockTypeNames.Count; i++)
        {
            // NifParser decodes the names as ASCII; compare in its terms.
            var ascii = Encoding.ASCII.GetString(Encoding.Latin1.GetBytes(BlockTypeNames[i]));
            Require(string.Equals(ascii, info.BlockTypeNames[i], StringComparison.Ordinal), $"block type name {i}");
        }

        Require(Strings.Count == info.Strings.Count, "string count");
        var expectedOffset = HeaderEnd;
        for (var i = 0; i < BlockCount; i++)
        {
            var block = info.Blocks[i];
            Require(block.Index == i && block.TypeIndex == BlockTypeIndices[i], $"block {i} type index");
            Require(BlockSizes[i] <= int.MaxValue && block.Size == (int)BlockSizes[i], $"block {i} size");
            Require(block.DataOffset == expectedOffset, $"block {i} offset");
            expectedOffset = checked(expectedOffset + block.Size);
        }
    }

    private static void Require(bool condition, string what)
    {
        if (!condition)
        {
            throw new InvalidDataException($"NIF header re-read disagrees with NifParser on the {what}.");
        }
    }

    /// <summary>A bounded forward reader for the header walk; every read names the field it is reading.</summary>
    private ref struct HeaderReader
    {
        private readonly ReadOnlySpan<byte> _data;

        public HeaderReader(ReadOnlySpan<byte> data, int position)
        {
            _data = data;
            Position = position;
            BigEndian = false;
        }

        public int Position { get; private set; }

        public bool BigEndian { get; set; }

        public byte U8(string field)
        {
            return Bytes(1, field)[0];
        }

        public uint U32Le(string field)
        {
            return BinaryPrimitives.ReadUInt32LittleEndian(Bytes(4, field));
        }

        public ushort U16(string field)
        {
            var bytes = Bytes(2, field);
            return BigEndian
                ? BinaryPrimitives.ReadUInt16BigEndian(bytes)
                : BinaryPrimitives.ReadUInt16LittleEndian(bytes);
        }

        public uint U32(string field)
        {
            var bytes = Bytes(4, field);
            return BigEndian
                ? BinaryPrimitives.ReadUInt32BigEndian(bytes)
                : BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        }

        public ReadOnlySpan<byte> Bytes(int count, string field)
        {
            if (count < 0 || count > _data.Length - Position)
            {
                throw new InvalidDataException(
                    $"NIF header is truncated in {field}: needs {count} byte(s) at 0x{Position:X}, " +
                    $"{_data.Length - Position} remain.");
            }

            var span = _data.Slice(Position, count);
            Position += count;
            return span;
        }

        public void SkipExportString(string field)
        {
            var length = U8(field);
            Bytes(length, field);
        }

        public readonly void RequireCount(long count, int minimumBytesEach, string what)
        {
            if (count < 0 || count > (_data.Length - Position) / minimumBytesEach)
            {
                throw new InvalidDataException(
                    $"NIF header declares {count} entries for the {what}, more than the {_data.Length - Position} " +
                    "remaining bytes can hold.");
            }
        }
    }
}
