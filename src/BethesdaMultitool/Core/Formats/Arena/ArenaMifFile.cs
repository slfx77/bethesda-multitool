// Ported from OpenTESArena (MIT License), https://github.com/afritz1/OpenTESArena
//   OpenTESArena/src/Assets/MIFFile.cpp / MIFFile.h and the MIFHeader / MIFLock / MIFTarget /
//   MIFTrigger layouts in ArenaTypes.cpp. License texts are collected centrally in
//   THIRD_PARTY_LICENSES.

using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Compression;

namespace BethesdaMultitool.Core.Formats.Arena;

/// <summary>
///     An Arena <c>.MIF</c> map: an <c>MHDR</c> header giving the map dimensions and up to four
///     player start points, followed by one or more <c>LEVL</c> blocks. Each level is a bag of
///     four-character tag chunks — three voxel layers (<c>FLOR</c> floors, <c>MAP1</c> walls,
///     <c>MAP2</c> upper storeys), the <c>INFO</c> name of the <c>.INF</c> that supplies its
///     textures, plus lock, trigger and start-target tables.
///     <para>
///         The voxel layers are LZHUF (compression type 08) streams of little-endian 16-bit voxel
///         ids, row-major, west-to-east within a north-to-south row. A .MIF alone describes a
///         dungeon or a single city block; whole cities and the wilderness are assembled at
///         runtime from tables inside the game executable, so a map viewer built on .MIF sees real
///         geometry but not a complete city.
///     </para>
/// </summary>
internal sealed class ArenaMifFile
{
    /// <summary>Documented size of the MHDR payload.</summary>
    public const int HeaderPayloadSize = 61;

    /// <summary>Player start points a header can carry; unused slots are (0, 0).</summary>
    public const int StartPointCount = 4;

    private ArenaMifFile(
        string name,
        int width,
        int depth,
        int startingLevelIndex,
        int declaredLevelCount,
        IReadOnlyList<ArenaMifStartPoint> startPoints,
        IReadOnlyList<ArenaMifLevel> levels)
    {
        Name = name;
        Width = width;
        Depth = depth;
        StartingLevelIndex = startingLevelIndex;
        DeclaredLevelCount = declaredLevelCount;
        StartPoints = startPoints;
        Levels = levels;
    }

    /// <summary>Logical file name this map was parsed from.</summary>
    public string Name { get; }

    /// <summary>Map width in voxels (west-to-east). Shared by every level.</summary>
    public int Width { get; }

    /// <summary>Map depth in voxels (north-to-south). Shared by every level.</summary>
    public int Depth { get; }

    /// <summary>Level the player enters on.</summary>
    public int StartingLevelIndex { get; }

    /// <summary>
    ///     Level count as declared in the header. The parser trusts the actual <c>LEVL</c> blocks
    ///     instead, so a mismatch is data worth reporting rather than an error.
    /// </summary>
    public int DeclaredLevelCount { get; }

    /// <summary>Player start points, in the game's fine positional units.</summary>
    public IReadOnlyList<ArenaMifStartPoint> StartPoints { get; }

    /// <summary>The parsed levels, in file order.</summary>
    public IReadOnlyList<ArenaMifLevel> Levels { get; }

    /// <summary>Parses a .MIF file.</summary>
    public static ArenaMifFile Parse(ReadOnlySpan<byte> bytes, string name)
    {
        if (bytes.Length < 6 + HeaderPayloadSize)
        {
            throw new InvalidDataException($"'{name}' is too small to be an Arena .MIF ({bytes.Length} bytes).");
        }

        if (!bytes[..4].SequenceEqual("MHDR"u8))
        {
            throw new InvalidDataException(
                $"'{name}' does not start with the MHDR tag (found '{SafeTag(bytes[..4])}').");
        }

        var headerSize = BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]);
        var header = bytes.Slice(6, Math.Min(headerSize, bytes.Length - 6));
        if (header.Length < 25)
        {
            throw new InvalidDataException($"'{name}' has a truncated MHDR ({header.Length} bytes).");
        }

        var startPoints = new List<ArenaMifStartPoint>(StartPointCount);
        for (var i = 0; i < StartPointCount; i++)
        {
            startPoints.Add(new ArenaMifStartPoint(
                BinaryPrimitives.ReadUInt16LittleEndian(header[(2 + (i * 2))..]),
                BinaryPrimitives.ReadUInt16LittleEndian(header[(10 + (i * 2))..])));
        }

        var startingLevelIndex = header[18];
        var declaredLevelCount = header[19];
        var width = BinaryPrimitives.ReadUInt16LittleEndian(header[21..]);
        var depth = BinaryPrimitives.ReadUInt16LittleEndian(header[23..]);

        if (width <= 0 || depth <= 0)
        {
            throw new InvalidDataException($"'{name}' declares an empty map ({width}x{depth}).");
        }

        var levels = new List<ArenaMifLevel>();
        var offset = headerSize + 6;
        while (offset < bytes.Length)
        {
            var consumed = ArenaMifLevel.TryParse(bytes[offset..], width, depth, name, out var level);
            if (consumed <= 0 || level is null)
            {
                break;
            }

            levels.Add(level);
            offset += consumed;
        }

        return new ArenaMifFile(name, width, depth, startingLevelIndex, declaredLevelCount, startPoints, levels);
    }

    internal static string SafeTag(ReadOnlySpan<byte> raw)
    {
        Span<char> chars = stackalloc char[raw.Length];
        for (var i = 0; i < raw.Length; i++)
        {
            chars[i] = raw[i] is >= 0x20 and <= 0x7E ? (char)raw[i] : '?';
        }

        return new string(chars);
    }
}

/// <summary>A player entry position, in Arena's fine positional units (not voxels).</summary>
internal readonly record struct ArenaMifStartPoint(int X, int Y)
{
    /// <summary>Unused header slots are left at the origin.</summary>
    public bool IsUnset => X == 0 && Y == 0;
}

/// <summary>One <c>LEVL</c> block of a .MIF map.</summary>
internal sealed class ArenaMifLevel
{
    /// <summary>Bytes of tag name plus the u16 payload size that precede every chunk's data.</summary>
    private const int ChunkHeaderSize = 6;

    private ArenaMifLevel(int width, int depth)
    {
        Width = width;
        Depth = depth;
    }

    /// <summary>Level name from the <c>NAME</c> chunk.</summary>
    public string? LevelName { get; private set; }

    /// <summary>The <c>.INF</c> file supplying this level's textures, from the <c>INFO</c> chunk.</summary>
    public string? InfoFile { get; private set; }

    /// <summary>Floor-texture count from the <c>NUMF</c> chunk.</summary>
    public int FloorTextureCount { get; private set; }

    public int Width { get; }

    public int Depth { get; }

    /// <summary>Floor voxel ids, row-major (<c>index = x + (z * Width)</c>). Empty when absent.</summary>
    public ushort[] Floor { get; private set; } = [];

    /// <summary>Wall/ground-storey voxel ids, row-major. Empty when absent.</summary>
    public ushort[] Map1 { get; private set; } = [];

    /// <summary>Upper-storey voxel ids, row-major. Empty when absent.</summary>
    public ushort[] Map2 { get; private set; } = [];

    /// <summary>Door/chest lock positions and their lock levels.</summary>
    public IReadOnlyList<ArenaMifLock> Locks { get; private set; } = [];

    /// <summary>Trigger voxels, each naming an <c>*TEXT</c> and/or <c>@SOUND</c> id from the .INF.</summary>
    public IReadOnlyList<ArenaMifTrigger> Triggers { get; private set; } = [];

    /// <summary>Start-target voxel positions.</summary>
    public IReadOnlyList<ArenaMifTarget> Targets { get; private set; } = [];

    /// <summary>
    ///     Chunks whose meaning is not documented — <c>FLAT</c>, <c>INNS</c>, <c>LOOT</c>,
    ///     <c>STOR</c>. Kept as raw payloads rather than dropped, so their sizes and contents stay
    ///     inspectable; the reference stores them the same way and likewise does not interpret them.
    /// </summary>
    public IReadOnlyDictionary<string, byte[]> UndecodedChunks { get; private set; } =
        System.Collections.ObjectModel.ReadOnlyDictionary<string, byte[]>.Empty;

    /// <summary>Reads one LEVL block, returning how many bytes it consumed (0 if it is not a level).</summary>
    internal static int TryParse(
        ReadOnlySpan<byte> bytes,
        int width,
        int depth,
        string fileName,
        out ArenaMifLevel? level)
    {
        level = null;
        if (bytes.Length < ChunkHeaderSize || !bytes[..4].SequenceEqual("LEVL"u8))
        {
            return 0;
        }

        var result = new ArenaMifLevel(width, depth);
        var undecoded = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var locks = new List<ArenaMifLock>();
        var triggers = new List<ArenaMifTrigger>();
        var targets = new List<ArenaMifTarget>();

        var declaredSize = BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]);
        var cursor = ChunkHeaderSize;
        var declaredEnd = Math.Min(cursor + declaredSize, bytes.Length);

        while (cursor + ChunkHeaderSize <= declaredEnd)
        {
            var tag = ArenaMifFile.SafeTag(bytes.Slice(cursor, 4));
            var payloadSize = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(cursor + 4)..]);
            var payloadStart = cursor + ChunkHeaderSize;
            if (payloadStart > bytes.Length)
            {
                break;
            }

            var available = Math.Min(payloadSize, bytes.Length - payloadStart);
            var payload = bytes.Slice(payloadStart, available);

            switch (tag)
            {
                case "FLOR":
                    result.Floor = DecodeLayer(payload, width, depth, fileName, tag);
                    break;
                case "MAP1":
                    result.Map1 = DecodeLayer(payload, width, depth, fileName, tag);
                    break;
                case "MAP2":
                    result.Map2 = DecodeLayer(payload, width, depth, fileName, tag);
                    break;
                case "NAME":
                    result.LevelName = ReadNulTerminated(payload);
                    break;
                case "INFO":
                    result.InfoFile = ReadNulTerminated(payload);
                    break;
                case "NUMF":
                    result.FloorTextureCount = payload.Length > 0 ? payload[0] : 0;
                    break;
                case "LOCK":
                    for (var i = 0; i + 3 <= payload.Length; i += 3)
                    {
                        locks.Add(new ArenaMifLock(payload[i], payload[i + 1], payload[i + 2]));
                    }

                    break;
                case "TRIG":
                    for (var i = 0; i + 4 <= payload.Length; i += 4)
                    {
                        triggers.Add(new ArenaMifTrigger(
                            payload[i], payload[i + 1], (sbyte)payload[i + 2], (sbyte)payload[i + 3]));
                    }

                    break;
                case "TARG":
                    for (var i = 0; i + 2 <= payload.Length; i += 2)
                    {
                        targets.Add(new ArenaMifTarget(payload[i], payload[i + 1]));
                    }

                    break;
                case "FLAT":
                case "INNS":
                case "LOOT":
                case "STOR":
                    undecoded[tag] = payload.ToArray();
                    break;
                default:
                    // An unrecognized tag means the next chunk boundary is unknown, so stop here
                    // rather than resynchronizing onto garbage.
                    result.Finish(locks, triggers, targets, undecoded);
                    level = result;
                    return cursor;
            }

            // A voxel layer's own size field counts only its compressed payload, so the advance
            // uses the same "size + 6" rule as every other chunk.
            cursor = payloadStart + payloadSize;
        }

        result.Finish(locks, triggers, targets, undecoded);
        level = result;

        // Deliberately the cursor, not the declared end: WILD.MIF's LEVL size is six bytes short
        // of the truth (its FLAT chunk header is unaccounted for), and trusting it would start a
        // phantom second level six bytes from end of file.
        return Math.Max(cursor, ChunkHeaderSize);
    }

    private void Finish(
        List<ArenaMifLock> locks,
        List<ArenaMifTrigger> triggers,
        List<ArenaMifTarget> targets,
        Dictionary<string, byte[]> undecoded)
    {
        Locks = locks;
        Triggers = triggers;
        Targets = targets;
        UndecodedChunks = undecoded;
    }

    /// <summary>Voxel id at a coordinate in the given layer, or 0 when the layer is absent.</summary>
    public static ushort VoxelAt(ushort[] layer, int width, int x, int z)
    {
        if (layer.Length == 0)
        {
            return 0;
        }

        var index = x + (z * width);
        return (uint)index < (uint)layer.Length ? layer[index] : (ushort)0;
    }

    /// <summary>
    ///     A voxel layer chunk. Its u16 size field is the COMPRESSED size, and it is measured from
    ///     the field that follows it, so the payload reads: u16 uncompressed size, then an LZHUF
    ///     stream two bytes shorter than the declared size.
    /// </summary>
    private static ushort[] DecodeLayer(
        ReadOnlySpan<byte> payload,
        int width,
        int depth,
        string fileName,
        string tag)
    {
        if (payload.Length < 2)
        {
            throw new InvalidDataException($"'{fileName}' has a truncated {tag} chunk.");
        }

        var uncompressedSize = BinaryPrimitives.ReadUInt16LittleEndian(payload);
        var decompressed = LzhufCodec.Decompress(payload[2..], uncompressedSize);

        var voxels = new ushort[width * depth];
        var available = decompressed.Length / 2;
        for (var i = 0; i < voxels.Length && i < available; i++)
        {
            voxels[i] = BinaryPrimitives.ReadUInt16LittleEndian(decompressed.AsSpan(i * 2));
        }

        return voxels;
    }

    private static string ReadNulTerminated(ReadOnlySpan<byte> payload)
    {
        var end = payload.IndexOf((byte)0);
        return Encoding.Latin1.GetString(end < 0 ? payload : payload[..end]);
    }
}

/// <summary>A locked voxel and its lock level.</summary>
internal readonly record struct ArenaMifLock(int X, int Y, int LockLevel);

/// <summary>A start-target voxel position.</summary>
internal readonly record struct ArenaMifTarget(int X, int Y);

/// <summary>
///     A trigger voxel. <see cref="TextIndex" /> selects an <c>*TEXT</c> entry of the level's .INF
///     (lore text, a riddle, or a door-key id) and <see cref="SoundIndex" /> an <c>@SOUND</c>
///     entry; either is inactive when negative.
/// </summary>
internal readonly record struct ArenaMifTrigger(int X, int Y, sbyte TextIndex, sbyte SoundIndex)
{
    public bool HasText => TextIndex >= 0;

    public bool HasSound => SoundIndex >= 0;
}
