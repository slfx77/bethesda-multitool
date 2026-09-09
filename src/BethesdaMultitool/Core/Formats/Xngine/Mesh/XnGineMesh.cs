// Ported from daggerfall-unity's DaggerfallConnect API (MIT License),
//   https://github.com/Interkarma/daggerfall-unity — Assets/Scripts/API/Arch3dFile.cs (header,
//   plane list and point/normal readers, UV unpacking). License texts are collected centrally in
//   THIRD_PARTY_LICENSES.

using System.Buffers.Binary;
using System.Text;

namespace BethesdaMultitool.Core.Formats.Xngine.Mesh;

/// <summary>Version tag of an XnGine mesh record (the first four bytes).</summary>
internal enum XnGineMeshVersion
{
    V25,
    V26,
    V27
}

/// <summary>
///     Which game's plane-list layout a record uses. Both label themselves <c>v2.7</c>, so the
///     layout follows the SOURCE, never the version tag: Daggerfall's plane header is 8 bytes,
///     Battlespire's is 10 (measured on all 245 loose <c>.3D</c> files — none parses as 8).
/// </summary>
internal enum XnGineMeshLayout
{
    Daggerfall,
    Battlespire
}

/// <summary>A point or normal in native units (1/256 of a world unit).</summary>
internal readonly record struct XnGineMeshPoint(int X, int Y, int Z);

/// <summary>
///     One vertex of a plane: an index into the mesh point list and the plane's per-point UV
///     values in 1/16 texel units. The first three UVs of a plane are DELTAS (each relative to
///     the previous), as the reference reads them; the decomposer accumulates them.
/// </summary>
internal readonly record struct XnGinePlanePoint(int PointIndex, int U, int V);

/// <summary>One polygon of an XnGine mesh.</summary>
internal sealed class XnGinePlane
{
    /// <summary>Position in the plane list.</summary>
    public required int Index { get; init; }

    /// <summary>Byte after the point count (unread by the reference).</summary>
    public required byte Unknown1 { get; init; }

    /// <summary>The u16 at plane header +2 — Daggerfall's packed texture reference: archive in the high 9 bits, record in the low 7.</summary>
    public required ushort TextureBits { get; init; }

    /// <summary>
    ///     The plane's texture key as its game reads it. Daggerfall: <see cref="TextureBits" />.
    ///     Battlespire: the 32-bit dword at +2 (GAME.EXE <c>FUN_00087E40</c> reads
    ///     <c>*(uint*)(plane + 2)</c>), a base-40 encoding of the BSI stem — see
    ///     <c>BattlespireTextureName</c>. On Battlespire <see cref="TextureBits" /> is only its low word.
    /// </summary>
    public required uint TextureKey { get; init; }

    /// <summary>
    ///     Daggerfall: the TEXTURE.nnn archive number (<c>TextureBits &gt;&gt; 7</c>). Battlespire: the
    ///     key's HIGH word, so that (archive, record) still identifies one material and a
    ///     Battlespire resolver can rebuild the key as <c>(archive &lt;&lt; 16) | record</c>.
    /// </summary>
    public required int TextureArchive { get; init; }

    /// <summary>Daggerfall: the record within the archive (<c>TextureBits &amp; 0x7F</c>). Battlespire: the key's LOW word.</summary>
    public required int TextureRecord { get; init; }

    /// <summary>
    ///     The bytes after the texture reference: four on Daggerfall, six on Battlespire. Neither
    ///     reference interprets them; on Battlespire's loose meshes the last four are always zero.
    /// </summary>
    public required ReadOnlyMemory<byte> HeaderTail { get; init; }

    /// <summary>The first four bytes of <see cref="HeaderTail" /> as a word.</summary>
    public uint Unknown2 => HeaderTail.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(HeaderTail.Span) : 0;

    /// <summary>Plane normal from the normal list, in native units.</summary>
    public required XnGineMeshPoint Normal { get; init; }

    /// <summary>The plane's vertices in authored order.</summary>
    public required IReadOnlyList<XnGinePlanePoint> Points { get; init; }

    /// <summary>The plane's 24-byte plane-data block, or empty when the record has none for it.</summary>
    public required ReadOnlyMemory<byte> PlaneData { get; init; }
}

/// <summary>
///     An XnGine 3D mesh record as stored in Daggerfall's <c>ARCH3D.BSA</c> (versions v2.5, v2.6
///     and v2.7 — the same family Battlespire and Redguard <c>.3D</c> files belong to). A 64-byte
///     header names the point, normal, plane-list and plane-data areas; each plane list entry is
///     an 8-byte header followed by 8 bytes per point (i32 point offset, i16 u, i16 v). The point
///     offset is a BYTE offset into the 12-byte-per-point list, except in v2.5 where it is stored
///     divided by three.
///     <para>
///         Retail ARCH3D.BSA (measured 2026-09-03): 10,251 records — 10,109 v2.7, 134 v2.6, 8 v2.5;
///         every point offset resolves inside its point list; 10 object ids are duplicated; the
///         areas are NOT laid out sequentially (normals do not follow points in 10,244 records),
///         so the header offsets are the only truth.
///     </para>
/// </summary>
internal sealed class XnGineMesh
{
    /// <summary>Bytes in the fixed header.</summary>
    public const int HeaderLength = 64;

    /// <summary>Native units per world unit.</summary>
    public const float PointDivisor = 256f;

    /// <summary>UV units per texel.</summary>
    public const float TextureDivisor = 16f;

    private const int PointLength = 12;
    private const int DaggerfallPlaneHeaderLength = 8;
    private const int BattlespirePlaneHeaderLength = 10;
    private const int PlanePointLength = 8;
    private const int PlaneDataLength = 24;

    /// <summary>
    ///     Object ids below this use a packed UV encoding on their first three plane points that
    ///     the reference unpacks (its <c>UVunpack</c>).
    /// </summary>
    public const uint PackedUvObjectIdLimit = 905;

    private XnGineMesh()
    {
    }

    /// <summary>The archive record id the mesh came from.</summary>
    public required uint ObjectId { get; init; }

    /// <summary>Which game's plane-list layout the record was read with.</summary>
    public required XnGineMeshLayout Layout { get; init; }

    /// <summary>Parsed version.</summary>
    public required XnGineMeshVersion Version { get; init; }

    /// <summary>The literal four-byte tag ("v2.7").</summary>
    public required string VersionTag { get; init; }

    /// <summary>Bounding radius in native units.</summary>
    public required uint Radius { get; init; }

    /// <summary>Bounding radius in world units.</summary>
    public float RadiusUnits => Radius / PointDivisor;

    /// <summary>Header offset of the plane-data area.</summary>
    public required int PlaneDataOffset { get; init; }

    /// <summary>Header offset of the (undecoded) object-data area.</summary>
    public required int ObjectDataOffset { get; init; }

    /// <summary>Header count of object-data entries.</summary>
    public required int ObjectDataCount { get; init; }

    /// <summary>Header u32 at 36.</summary>
    public required uint Unknown2 { get; init; }

    /// <summary>Header u32 at 56.</summary>
    public required uint Unknown3 { get; init; }

    /// <summary>Header offset of the point list.</summary>
    public required int PointListOffset { get; init; }

    /// <summary>Header offset of the normal list (one per plane).</summary>
    public required int NormalListOffset { get; init; }

    /// <summary>Header offset of the plane list.</summary>
    public required int PlaneListOffset { get; init; }

    /// <summary>All points in the point list.</summary>
    public required IReadOnlyList<XnGineMeshPoint> Points { get; init; }

    /// <summary>All planes in list order.</summary>
    public required IReadOnlyList<XnGinePlane> Planes { get; init; }

    /// <summary>Distinct (archive, record) texture references in first-use order.</summary>
    public required IReadOnlyList<(int Archive, int Record)> UniqueTextures { get; init; }

    /// <summary>Smallest coordinates over the point list (native units).</summary>
    public required XnGineMeshPoint Min { get; init; }

    /// <summary>Largest coordinates over the point list (native units).</summary>
    public required XnGineMeshPoint Max { get; init; }

    /// <summary>Extent in world units.</summary>
    public (float X, float Y, float Z) Size => (
        (Max.X - Min.X) / PointDivisor,
        (Max.Y - Min.Y) / PointDivisor,
        (Max.Z - Min.Z) / PointDivisor);

    /// <summary>
    ///     Parses one mesh record in the given game's plane-list layout.
    ///     <para>
    ///         <paramref name="suppliedPoints" /> and <paramref name="suppliedNormals" /> replace the
    ///         lists the header points at. Redguard's animated <c>.3DC</c> needs both: its geometry
    ///         is named by a frame table rather than by the header (whose offsets are frame 1's), and
    ///         it stores no per-plane normal list a <c>.3D</c> reader could use. Everything else —
    ///         the header, the plane walk and the UV handling — is identical, so it stays shared.
    ///     </para>
    /// </summary>
    public static XnGineMesh Parse(
        ReadOnlyMemory<byte> bytes,
        uint objectId,
        XnGineMeshLayout layout = XnGineMeshLayout.Daggerfall,
        IReadOnlyList<XnGineMeshPoint>? suppliedPoints = null,
        IReadOnlyList<XnGineMeshPoint>? suppliedNormals = null)
    {
        var planeHeaderLength = layout == XnGineMeshLayout.Battlespire
            ? BattlespirePlaneHeaderLength
            : DaggerfallPlaneHeaderLength;
        var span = bytes.Span;
        if (span.Length < HeaderLength)
        {
            throw new InvalidDataException(
                $"Mesh {objectId}: {span.Length} bytes is shorter than the {HeaderLength}-byte header.");
        }

        var tag = ReadTag(span[..4]);
        var version = tag switch
        {
            "v2.5" => XnGineMeshVersion.V25,
            "v2.6" => XnGineMeshVersion.V26,
            "v2.7" => XnGineMeshVersion.V27,
            _ => throw new InvalidDataException($"Mesh {objectId}: unknown version tag '{tag}'.")
        };

        var pointCount = BinaryPrimitives.ReadInt32LittleEndian(span[4..]);
        var planeCount = BinaryPrimitives.ReadInt32LittleEndian(span[8..]);
        var radius = BinaryPrimitives.ReadUInt32LittleEndian(span[12..]);
        var planeDataOffset = BinaryPrimitives.ReadInt32LittleEndian(span[24..]);
        var objectDataOffset = BinaryPrimitives.ReadInt32LittleEndian(span[28..]);
        var objectDataCount = BinaryPrimitives.ReadInt32LittleEndian(span[32..]);
        var unknown2 = BinaryPrimitives.ReadUInt32LittleEndian(span[36..]);
        var pointListOffset = BinaryPrimitives.ReadInt32LittleEndian(span[48..]);
        var normalListOffset = BinaryPrimitives.ReadInt32LittleEndian(span[52..]);
        var unknown3 = BinaryPrimitives.ReadUInt32LittleEndian(span[56..]);
        var planeListOffset = BinaryPrimitives.ReadInt32LittleEndian(span[60..]);

        if (suppliedPoints is null &&
            (pointCount < 0 || pointListOffset < 0 || pointListOffset + (long)pointCount * PointLength > span.Length))
        {
            throw new InvalidDataException(
                $"Mesh {objectId}: {pointCount} points at {pointListOffset} do not fit in {span.Length} bytes.");
        }

        if (suppliedNormals is null &&
            (planeCount < 0 || normalListOffset < 0 || normalListOffset + (long)planeCount * PointLength > span.Length))
        {
            throw new InvalidDataException(
                $"Mesh {objectId}: {planeCount} normals at {normalListOffset} do not fit in {span.Length} bytes.");
        }

        if (suppliedPoints is not null && suppliedPoints.Count != pointCount)
        {
            throw new InvalidDataException(
                $"Mesh {objectId}: {suppliedPoints.Count} supplied points contradict the header's {pointCount}.");
        }

        if (suppliedNormals is not null && suppliedNormals.Count != planeCount)
        {
            throw new InvalidDataException(
                $"Mesh {objectId}: {suppliedNormals.Count} supplied normals contradict the header's {planeCount}.");
        }

        if (planeListOffset < 0 || planeListOffset > span.Length)
        {
            throw new InvalidDataException(
                $"Mesh {objectId}: plane list offset {planeListOffset} lies outside the record.");
        }

        var points = new XnGineMeshPoint[pointCount];
        for (var i = 0; i < pointCount; i++)
        {
            points[i] = suppliedPoints is not null
                ? suppliedPoints[i]
                : ReadPoint(span[(pointListOffset + i * PointLength)..]);
        }

        var context = new PlaneContext(bytes, objectId, layout, version, planeHeaderLength, pointCount,
            planeDataOffset, normalListOffset, suppliedNormals);
        var planes = new XnGinePlane[planeCount];
        var textures = new List<(int Archive, int Record)>();
        var position = planeListOffset;
        for (var k = 0; k < planeCount; k++)
        {
            planes[k] = ReadPlane(context, k, ref position);
            var texture = (Archive: planes[k].TextureArchive, Record: planes[k].TextureRecord);
            if (!textures.Contains(texture))
            {
                textures.Add(texture);
            }
        }

        var (min, max) = Bounds(points);
        return new XnGineMesh
        {
            ObjectId = objectId,
            Layout = layout,
            Version = version,
            VersionTag = tag,
            Radius = radius,
            PlaneDataOffset = planeDataOffset,
            ObjectDataOffset = objectDataOffset,
            ObjectDataCount = objectDataCount,
            Unknown2 = unknown2,
            Unknown3 = unknown3,
            PointListOffset = pointListOffset,
            NormalListOffset = normalListOffset,
            PlaneListOffset = planeListOffset,
            Points = points,
            Planes = planes,
            UniqueTextures = textures,
            Min = min,
            Max = max
        };
    }

    /// <summary>Reads one plane's header and points, advancing <paramref name="position" />.</summary>
    private static XnGinePlane ReadPlane(PlaneContext context, int index, ref int position)
    {
        var span = context.Bytes.Span;
        if (position + context.PlaneHeaderLength > span.Length)
        {
            throw new InvalidDataException($"Mesh {context.ObjectId}: plane list ends inside plane {index}'s header.");
        }

        var planePointCount = span[position];
        var unknown1 = span[position + 1];
        var textureBits = BinaryPrimitives.ReadUInt16LittleEndian(span[(position + 2)..]);
        var headerTail = context.Bytes.Slice(position + 4, context.PlaneHeaderLength - 4);

        // Battlespire's texture field is a 32-bit dword whose high word sits where Daggerfall's
        // header ends; splitting it into (high, low) words keeps the material identity a pair.
        uint textureKey;
        int textureArchive, textureRecord;
        if (context.Layout == XnGineMeshLayout.Battlespire)
        {
            textureKey = BinaryPrimitives.ReadUInt32LittleEndian(span[(position + 2)..]);
            textureArchive = (int)(textureKey >> 16);
            textureRecord = (int)(textureKey & 0xFFFF);
        }
        else
        {
            textureKey = textureBits;
            textureArchive = textureBits >> 7;
            textureRecord = textureBits & 0x7F;
        }

        position += context.PlaneHeaderLength;

        var planePoints = new XnGinePlanePoint[planePointCount];
        for (var q = 0; q < planePointCount; q++)
        {
            if (position + PlanePointLength > span.Length)
            {
                throw new InvalidDataException(
                    $"Mesh {context.ObjectId}: plane {index} point {q} lies past the record.");
            }

            var offset = BinaryPrimitives.ReadInt32LittleEndian(span[position..]);
            int u = BinaryPrimitives.ReadInt16LittleEndian(span[(position + 4)..]);
            int v = BinaryPrimitives.ReadInt16LittleEndian(span[(position + 6)..]);
            position += PlanePointLength;

            var byteOffset = context.Version == XnGineMeshVersion.V25 ? offset * 3L : offset;
            if (byteOffset < 0 || byteOffset % PointLength != 0 || byteOffset / PointLength >= context.PointCount)
            {
                throw new InvalidDataException(
                    $"Mesh {context.ObjectId}: plane {index} point {q} offset {offset} does not address one of the {context.PointCount} points.");
            }

            // The packed-UV encoding is Daggerfall's, and only on its low object ids.
            if (context.Layout == XnGineMeshLayout.Daggerfall && q < 3 && context.ObjectId < PackedUvObjectIdLimit)
            {
                u = UnpackUv(u);
                v = UnpackUv(v);
            }

            planePoints[q] = new XnGinePlanePoint((int)(byteOffset / PointLength), u, v);
        }

        var planeDataStart = context.PlaneDataOffset + (long)index * PlaneDataLength;
        var planeData = context.PlaneDataOffset >= 0 && planeDataStart + PlaneDataLength <= span.Length
            ? context.Bytes.Slice((int)planeDataStart, PlaneDataLength)
            : ReadOnlyMemory<byte>.Empty;

        return new XnGinePlane
        {
            Index = index,
            Unknown1 = unknown1,
            TextureBits = textureBits,
            TextureKey = textureKey,
            TextureArchive = textureArchive,
            TextureRecord = textureRecord,
            HeaderTail = headerTail,
            Normal = context.SuppliedNormals is { } normals
                ? normals[index]
                : ReadPoint(span[(context.NormalListOffset + index * PointLength)..]),
            Points = planePoints,
            PlaneData = planeData
        };
    }

    /// <summary>
    ///     The reference's <c>UVunpack</c>: values outside (-14336, 14336), or exactly -7168, are
    ///     folded back by the nearest multiple of 8192 above or below.
    /// </summary>
    public static int UnpackUv(int value)
    {
        if (value > -14336 && value < 14336 && value != -7168)
        {
            return value;
        }

        var nextMultiple = (((value - 1) >> 13) + 1) << 13;
        var previousMultiple = nextMultiple - 8192;
        var multiple = value - previousMultiple < nextMultiple - value ? previousMultiple : nextMultiple;
        return value - multiple;
    }

    private static string ReadTag(ReadOnlySpan<byte> bytes)
    {
        var end = bytes.IndexOf((byte)0);
        return Encoding.ASCII.GetString(end < 0 ? bytes : bytes[..end]);
    }

    private static XnGineMeshPoint ReadPoint(ReadOnlySpan<byte> bytes)
    {
        return new XnGineMeshPoint(
            BinaryPrimitives.ReadInt32LittleEndian(bytes),
            BinaryPrimitives.ReadInt32LittleEndian(bytes[4..]),
            BinaryPrimitives.ReadInt32LittleEndian(bytes[8..]));
    }

    private static (XnGineMeshPoint Min, XnGineMeshPoint Max) Bounds(XnGineMeshPoint[] points)
    {
        if (points.Length == 0)
        {
            return (default, default);
        }

        int minX = points[0].X, minY = points[0].Y, minZ = points[0].Z;
        int maxX = minX, maxY = minY, maxZ = minZ;
        foreach (var point in points)
        {
            minX = Math.Min(minX, point.X);
            minY = Math.Min(minY, point.Y);
            minZ = Math.Min(minZ, point.Z);
            maxX = Math.Max(maxX, point.X);
            maxY = Math.Max(maxY, point.Y);
            maxZ = Math.Max(maxZ, point.Z);
        }

        return (new XnGineMeshPoint(minX, minY, minZ), new XnGineMeshPoint(maxX, maxY, maxZ));
    }

    /// <summary>Everything <see cref="ReadPlane" /> needs that does not change between planes.</summary>
    private readonly record struct PlaneContext(
        ReadOnlyMemory<byte> Bytes,
        uint ObjectId,
        XnGineMeshLayout Layout,
        XnGineMeshVersion Version,
        int PlaneHeaderLength,
        int PointCount,
        int PlaneDataOffset,
        int NormalListOffset,
        IReadOnlyList<XnGineMeshPoint>? SuppliedNormals);
}
