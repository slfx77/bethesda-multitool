// Ported from daggerfall-unity's DaggerfallConnect API (MIT License),
//   https://github.com/Interkarma/daggerfall-unity — Assets/Scripts/API/BlocksFile.cs (the RMB
//   readers) and DFBlock.cs (the RMB structures). License texts are collected centrally in
//   THIRD_PARTY_LICENSES.

using System.Buffers.Binary;
using System.Text;

namespace BethesdaMultitool.Core.Formats.Daggerfall;

/// <summary>
///     A Daggerfall exterior block, <c>*.RMB</c> in <c>BLOCKS.BSA</c>: a 6,776-byte FLD header
///     (three counts, 32 sub-block positions, 32 building entries, 32 sub-block data sizes, the
///     16x16 ground tile and scenery grids, a 64x64 automap and 33 names) followed by the
///     sub-block records (an exterior and an interior object set each, advanced by the declared
///     size), then the block's own 3D objects and flats.
///     <para>
///         Measured on retail (2026-09-03): all 920 blocks tile exactly; 5,549 of the 9,005
///         sub-block records carry ONE trailing byte after their interior set (kept as
///         <see cref="DaggerfallRmbSubRecord.TrailingByte" />), the rest none. Positions are in
///         the block's units (256 per ground tile, 4,096 per block side); Y rotations are in
///         1/5.68889 of a degree (2,048 = 360).
///     </para>
/// </summary>
internal sealed class DaggerfallRmbBlock
{
    /// <summary>Bytes in the FLD header.</summary>
    public const int FldHeaderLength = 6776;

    /// <summary>Ground tiles per block side.</summary>
    public const int TilesPerSide = 16;

    /// <summary>Automap pixels per side.</summary>
    public const int AutoMapSize = 64;

    /// <summary>Block units per ground tile.</summary>
    public const int UnitsPerTile = 256;

    /// <summary>Block units per block side.</summary>
    public const int UnitsPerBlock = 4096;

    /// <summary>Rotation units per degree.</summary>
    public const float RotationDivisor = 5.68888888888889f;

    private const int SlotCount = 32;
    private const int BlockPositionLength = 20;
    private const int BuildingLength = 26;
    private const int NameLength = 13;
    private const int BlockDataHeaderLength = 17;
    private const int ModelLength = 66;
    private const int FlatLength = 17;
    private const int Section3Length = 16;
    private const int DoorLength = 19;

    private DaggerfallRmbBlock()
    {
    }

    /// <summary>Archive entry name (e.g. WALLAA03.RMB).</summary>
    public required string Name { get; init; }

    /// <summary>The 13-byte name in the FLD header.</summary>
    public required string HeaderName { get; init; }

    /// <summary>The 32 other names in the FLD header (empty strings for unused slots).</summary>
    public required IReadOnlyList<string> OtherNames { get; init; }

    /// <summary>All 32 sub-block position slots (only the first <c>SubRecords.Count</c> are used).</summary>
    public required IReadOnlyList<DaggerfallRmbBlockPosition> BlockPositions { get; init; }

    /// <summary>All 32 building slots (the first <c>SubRecords.Count</c> describe the sub-blocks).</summary>
    public required IReadOnlyList<DaggerfallBuilding> Buildings { get; init; }

    /// <summary>The 32 u32 values of the FLD header's second unknown section.</summary>
    public required IReadOnlyList<uint> Section2Unknown { get; init; }

    /// <summary>The 32 declared sub-block record sizes.</summary>
    public required IReadOnlyList<int> BlockDataSizes { get; init; }

    /// <summary>The 8-byte ground data header.</summary>
    public required ReadOnlyMemory<byte> GroundHeader { get; init; }

    /// <summary>Ground tiles, row-major (<c>y * 16 + x</c>).</summary>
    public required IReadOnlyList<DaggerfallRmbGroundTile> GroundTiles { get; init; }

    /// <summary>Ground scenery, row-major (<c>y * 16 + x</c>).</summary>
    public required IReadOnlyList<DaggerfallRmbGroundScenery> GroundScenery { get; init; }

    /// <summary>The 64x64 automap, row-major.</summary>
    public required ReadOnlyMemory<byte> AutoMap { get; init; }

    /// <summary>The sub-blocks (buildings) in the block.</summary>
    public required IReadOnlyList<DaggerfallRmbSubRecord> SubRecords { get; init; }

    /// <summary>3D objects placed directly in the block.</summary>
    public required IReadOnlyList<DaggerfallRmbModel> Misc3dObjects { get; init; }

    /// <summary>Flats placed directly in the block.</summary>
    public required IReadOnlyList<DaggerfallRmbFlat> MiscFlats { get; init; }

    /// <summary>Bytes consumed by the parse (equal to the record length on every retail block).</summary>
    public required int ParsedLength { get; init; }

    /// <summary>The ground tile at a grid position.</summary>
    public DaggerfallRmbGroundTile GroundTileAt(int x, int y)
    {
        return GroundTiles[y * TilesPerSide + x];
    }

    /// <summary>Every 3D object in the block: sub-block exteriors and interiors plus the loose ones.</summary>
    public IEnumerable<DaggerfallRmbModel> AllModels =>
        SubRecords.SelectMany(s => s.Exterior.Models.Concat(s.Interior.Models)).Concat(Misc3dObjects);

    /// <summary>Parses one RMB record.</summary>
    public static DaggerfallRmbBlock Parse(ReadOnlyMemory<byte> bytes, string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var span = bytes.Span;
        if (span.Length < FldHeaderLength)
        {
            throw new InvalidDataException($"{name}: {span.Length} bytes is shorter than the {FldHeaderLength}-byte FLD header.");
        }

        var subRecordCount = span[0];
        var misc3dCount = span[1];
        var miscFlatCount = span[2];

        var positions = new DaggerfallRmbBlockPosition[SlotCount];
        var offset = 3;
        for (var i = 0; i < SlotCount; i++)
        {
            var slot = span.Slice(offset + i * BlockPositionLength, BlockPositionLength);
            positions[i] = new DaggerfallRmbBlockPosition(
                BinaryPrimitives.ReadUInt32LittleEndian(slot),
                BinaryPrimitives.ReadUInt32LittleEndian(slot[4..]),
                BinaryPrimitives.ReadInt32LittleEndian(slot[8..]),
                BinaryPrimitives.ReadInt32LittleEndian(slot[12..]),
                BinaryPrimitives.ReadInt32LittleEndian(slot[16..]));
        }

        offset += SlotCount * BlockPositionLength;
        var buildings = new DaggerfallBuilding[SlotCount];
        for (var i = 0; i < SlotCount; i++)
        {
            buildings[i] = ReadBuilding(span.Slice(offset + i * BuildingLength, BuildingLength));
        }

        offset += SlotCount * BuildingLength;
        var section2 = new uint[SlotCount];
        for (var i = 0; i < SlotCount; i++)
        {
            section2[i] = BinaryPrimitives.ReadUInt32LittleEndian(span[(offset + i * 4)..]);
        }

        offset += SlotCount * 4;
        var sizes = new int[SlotCount];
        for (var i = 0; i < SlotCount; i++)
        {
            sizes[i] = BinaryPrimitives.ReadInt32LittleEndian(span[(offset + i * 4)..]);
        }

        offset += SlotCount * 4;
        var groundHeader = bytes.Slice(offset, 8);
        offset += 8;
        var tiles = new DaggerfallRmbGroundTile[TilesPerSide * TilesPerSide];
        for (var i = 0; i < tiles.Length; i++)
        {
            tiles[i] = new DaggerfallRmbGroundTile(span[offset + i]);
        }

        offset += tiles.Length;
        var scenery = new DaggerfallRmbGroundScenery[TilesPerSide * TilesPerSide];
        for (var i = 0; i < scenery.Length; i++)
        {
            scenery[i] = new DaggerfallRmbGroundScenery(span[offset + i]);
        }

        offset += scenery.Length;
        var autoMap = bytes.Slice(offset, AutoMapSize * AutoMapSize);
        offset += AutoMapSize * AutoMapSize;
        var headerName = ReadCString(span.Slice(offset, NameLength));
        offset += NameLength;
        var otherNames = new string[SlotCount];
        for (var i = 0; i < SlotCount; i++)
        {
            otherNames[i] = ReadCString(span.Slice(offset + i * NameLength, NameLength));
        }

        offset += SlotCount * NameLength;

        var subRecords = new DaggerfallRmbSubRecord[subRecordCount];
        for (var i = 0; i < subRecordCount; i++)
        {
            var declared = sizes[i];
            if (declared < 2 * BlockDataHeaderLength || offset + declared > span.Length)
            {
                throw new InvalidDataException($"{name}: sub-block {i} declares {declared} bytes at {offset}, which does not fit the {span.Length}-byte record.");
            }

            var exterior = ReadBlockData(span, offset, offset + declared, name, i, "exterior", out var interiorStart);
            var interior = ReadBlockData(span, interiorStart, offset + declared, name, i, "interior", out var end);
            var trailing = offset + declared - end;
            if (trailing > 1)
            {
                throw new InvalidDataException($"{name}: sub-block {i} leaves {trailing} bytes after its interior set; retail leaves at most one.");
            }

            subRecords[i] = new DaggerfallRmbSubRecord
            {
                Index = i,
                XPos = positions[i].XPos,
                ZPos = positions[i].ZPos,
                YRotation = positions[i].YRotation,
                DeclaredSize = declared,
                Exterior = exterior,
                Interior = interior,
                TrailingByte = trailing == 1 ? span[end] : null
            };
            offset += declared;
        }

        var misc3d = ReadModels(span, ref offset, misc3dCount, name, "misc 3D");
        var miscFlats = ReadFlats(span, ref offset, miscFlatCount, name, "misc flat");

        return new DaggerfallRmbBlock
        {
            Name = name,
            HeaderName = headerName,
            OtherNames = otherNames,
            BlockPositions = positions,
            Buildings = buildings,
            Section2Unknown = section2,
            BlockDataSizes = sizes,
            GroundHeader = groundHeader,
            GroundTiles = tiles,
            GroundScenery = scenery,
            AutoMap = autoMap,
            SubRecords = subRecords,
            Misc3dObjects = misc3d,
            MiscFlats = miscFlats,
            ParsedLength = offset
        };
    }

    private static DaggerfallRmbBlockData ReadBlockData(ReadOnlySpan<byte> span, int start, int limit, string name, int subRecord, string set, out int end)
    {
        if (start + BlockDataHeaderLength > limit)
        {
            throw new InvalidDataException($"{name}: sub-block {subRecord} {set} set has no room for its 17-byte header.");
        }

        var header = span.Slice(start, BlockDataHeaderLength);
        var unknowns = new short[6];
        for (var i = 0; i < 6; i++)
        {
            unknowns[i] = BinaryPrimitives.ReadInt16LittleEndian(header[(5 + i * 2)..]);
        }

        var offset = start + BlockDataHeaderLength;
        var models = ReadModels(span, ref offset, header[0], name, $"sub-block {subRecord} {set} 3D");
        var flats = ReadFlats(span, ref offset, header[1], name, $"sub-block {subRecord} {set} flat");

        var section3 = new DaggerfallRmbSection3[header[2]];
        Require(span, offset, section3.Length * Section3Length, name, $"sub-block {subRecord} {set} section-3");
        for (var i = 0; i < section3.Length; i++)
        {
            var record = span.Slice(offset + i * Section3Length, Section3Length);
            section3[i] = new DaggerfallRmbSection3(
                BinaryPrimitives.ReadInt32LittleEndian(record),
                BinaryPrimitives.ReadInt32LittleEndian(record[4..]),
                BinaryPrimitives.ReadInt32LittleEndian(record[8..]),
                record[12],
                record[13],
                BinaryPrimitives.ReadInt16LittleEndian(record[14..]));
        }

        offset += section3.Length * Section3Length;
        var people = ReadFlats(span, ref offset, header[3], name, $"sub-block {subRecord} {set} people");

        var doors = new DaggerfallRmbDoor[header[4]];
        Require(span, offset, doors.Length * DoorLength, name, $"sub-block {subRecord} {set} door");
        for (var i = 0; i < doors.Length; i++)
        {
            var record = span.Slice(offset + i * DoorLength, DoorLength);
            doors[i] = new DaggerfallRmbDoor(
                BinaryPrimitives.ReadInt32LittleEndian(record),
                BinaryPrimitives.ReadInt32LittleEndian(record[4..]),
                BinaryPrimitives.ReadInt32LittleEndian(record[8..]),
                BinaryPrimitives.ReadInt16LittleEndian(record[12..]),
                BinaryPrimitives.ReadInt16LittleEndian(record[14..]),
                record[16],
                record[17],
                record[18]);
        }

        offset += doors.Length * DoorLength;
        if (offset > limit)
        {
            throw new InvalidDataException($"{name}: sub-block {subRecord} {set} set overruns its declared size.");
        }

        end = offset;
        return new DaggerfallRmbBlockData(unknowns, models, flats, section3, people, doors);
    }

    private static DaggerfallRmbModel[] ReadModels(ReadOnlySpan<byte> span, ref int offset, int count, string name, string what)
    {
        Require(span, offset, count * ModelLength, name, what);
        var models = new DaggerfallRmbModel[count];
        for (var i = 0; i < count; i++)
        {
            var record = span.Slice(offset + i * ModelLength, ModelLength);
            var objectId1 = BinaryPrimitives.ReadInt16LittleEndian(record);
            var objectId2 = record[2];
            models[i] = new DaggerfallRmbModel(
                objectId1,
                objectId2,
                record[3],
                BinaryPrimitives.ReadUInt32LittleEndian(record[4..]),
                BinaryPrimitives.ReadUInt32LittleEndian(record[8..]),
                BinaryPrimitives.ReadUInt32LittleEndian(record[12..]),
                BinaryPrimitives.ReadInt32LittleEndian(record[24..]),
                BinaryPrimitives.ReadInt32LittleEndian(record[28..]),
                BinaryPrimitives.ReadInt32LittleEndian(record[32..]),
                BinaryPrimitives.ReadInt32LittleEndian(record[36..]),
                BinaryPrimitives.ReadInt32LittleEndian(record[40..]),
                BinaryPrimitives.ReadInt32LittleEndian(record[44..]),
                BinaryPrimitives.ReadInt16LittleEndian(record[52..]),
                BinaryPrimitives.ReadUInt16LittleEndian(record[54..]),
                BinaryPrimitives.ReadUInt32LittleEndian(record[60..]));
        }

        offset += count * ModelLength;
        return models;
    }

    private static DaggerfallRmbFlat[] ReadFlats(ReadOnlySpan<byte> span, ref int offset, int count, string name, string what)
    {
        Require(span, offset, count * FlatLength, name, what);
        var flats = new DaggerfallRmbFlat[count];
        for (var i = 0; i < count; i++)
        {
            var record = span.Slice(offset + i * FlatLength, FlatLength);
            flats[i] = new DaggerfallRmbFlat(
                BinaryPrimitives.ReadInt32LittleEndian(record),
                BinaryPrimitives.ReadInt32LittleEndian(record[4..]),
                BinaryPrimitives.ReadInt32LittleEndian(record[8..]),
                BinaryPrimitives.ReadUInt16LittleEndian(record[12..]),
                BinaryPrimitives.ReadInt16LittleEndian(record[14..]),
                record[16]);
        }

        offset += count * FlatLength;
        return flats;
    }

    private static void Require(ReadOnlySpan<byte> span, int offset, int length, string name, string what)
    {
        if (offset < 0 || offset + length > span.Length)
        {
            throw new InvalidDataException($"{name}: {what} records need {length} bytes at {offset}, the record has {span.Length}.");
        }
    }

    internal static DaggerfallBuilding ReadBuilding(ReadOnlySpan<byte> building)
    {
        return new DaggerfallBuilding(
            BinaryPrimitives.ReadUInt16LittleEndian(building),
            BinaryPrimitives.ReadUInt32LittleEndian(building[2..]),
            BinaryPrimitives.ReadUInt16LittleEndian(building[18..]),
            BinaryPrimitives.ReadInt16LittleEndian(building[20..]),
            BinaryPrimitives.ReadUInt16LittleEndian(building[22..]),
            (DaggerfallBuildingType)building[24],
            building[25]);
    }

    private static string ReadCString(ReadOnlySpan<byte> bytes)
    {
        var end = bytes.IndexOf((byte)0);
        return Encoding.Latin1.GetString(end < 0 ? bytes : bytes[..end]);
    }
}

/// <summary>One of the 32 sub-block position slots.</summary>
internal readonly record struct DaggerfallRmbBlockPosition(uint Unknown1, uint Unknown2, int XPos, int ZPos, int YRotation);

/// <summary>A ground tile byte: texture record in the low six bits, rotate and flip flags above.</summary>
internal readonly record struct DaggerfallRmbGroundTile(byte Raw)
{
    public int TextureRecord => Raw & 0x3F;

    public bool IsRotated => (Raw & 0x40) != 0;

    public bool IsFlipped => (Raw & 0x80) != 0;
}

/// <summary>A ground scenery byte: 255 = none, else <c>raw / 4 - 1</c> is the texture record.</summary>
internal readonly record struct DaggerfallRmbGroundScenery(byte Raw)
{
    public bool HasScenery => Raw < 255;

    public int TextureRecord => Raw < 255 ? Raw / 4 - 1 : -1;

    public int Unknown1 => Raw < 255 ? Raw & 0x03 : 0;
}

/// <summary>A sub-block (building): its placement and its exterior and interior object sets.</summary>
internal sealed class DaggerfallRmbSubRecord
{
    public required int Index { get; init; }

    public required int XPos { get; init; }

    public required int ZPos { get; init; }

    public required int YRotation { get; init; }

    /// <summary>Bytes declared for the sub-block in the FLD header.</summary>
    public required int DeclaredSize { get; init; }

    public required DaggerfallRmbBlockData Exterior { get; init; }

    public required DaggerfallRmbBlockData Interior { get; init; }

    /// <summary>The single byte after the interior set when the declared size leaves one (retail: 5,549 of 9,005).</summary>
    public required byte? TrailingByte { get; init; }
}

/// <summary>One object set (exterior or interior) of a sub-block.</summary>
internal sealed record DaggerfallRmbBlockData(
    IReadOnlyList<short> HeaderUnknowns,
    IReadOnlyList<DaggerfallRmbModel> Models,
    IReadOnlyList<DaggerfallRmbFlat> Flats,
    IReadOnlyList<DaggerfallRmbSection3> Section3,
    IReadOnlyList<DaggerfallRmbFlat> People,
    IReadOnlyList<DaggerfallRmbDoor> Doors);

/// <summary>A placed 3D object; its ARCH3D id is <c>ObjectId1 * 100 + ObjectId2</c>.</summary>
internal readonly record struct DaggerfallRmbModel(
    short ObjectId1,
    byte ObjectId2,
    byte ObjectType,
    uint Unknown1,
    uint Unknown2,
    uint Unknown3,
    int XPos1,
    int YPos1,
    int ZPos1,
    int XPos,
    int YPos,
    int ZPos,
    short YRotation,
    ushort Unknown4,
    uint Unknown5)
{
    /// <summary>The ARCH3D object id.</summary>
    public uint ModelId => (uint)(ObjectId1 * 100 + ObjectId2);
}

/// <summary>A placed flat (billboard) or person: position, texture reference, faction and flags.</summary>
internal readonly record struct DaggerfallRmbFlat(int XPos, int YPos, int ZPos, ushort TextureBits, short FactionId, byte Flags)
{
    public int TextureArchive => TextureBits >> 7;

    public int TextureRecord => TextureBits & 0x7F;
}

/// <summary>A "section 3" record (undecoded by the reference beyond its position).</summary>
internal readonly record struct DaggerfallRmbSection3(int XPos, int YPos, int ZPos, byte Unknown1, byte Unknown2, short Unknown3);

/// <summary>A door: position, closed and open rotations, and which door model it uses.</summary>
internal readonly record struct DaggerfallRmbDoor(int XPos, int YPos, int ZPos, short YRotation, short OpenRotation, byte DoorModelIndex, byte Unknown, byte NullValue);
