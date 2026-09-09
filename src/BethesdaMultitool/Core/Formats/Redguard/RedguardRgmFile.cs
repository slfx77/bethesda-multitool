using System.Buffers.Binary;
using System.Text;

namespace BethesdaMultitool.Core.Formats.Redguard;

/// <summary>
///     Redguard's <c>maps\*.RGM</c> map database: one file per location holding the map's script
///     objects, their placements, the static meshes, lights, flats, markers, ropes and the AI
///     navigation graphs. Original RE, measured 2026-09-05 against all 27 retail files — the full
///     field-level spec, with tiling counts and the engine-string evidence for each chunk's name,
///     is <c>docs/research/redguard_rgm_format.md</c>; this reader implements the chunks that
///     describe the map and keeps the script-side ones (bytecode, variables, hooks, attributes) as
///     measured spans.
///     <para>
///         Same house IFF style as <see cref="RedguardRobParser" />, <see cref="RedguardGxaFile" />
///         and <see cref="RedguardSfxFile" />: <c>tag + BIG-endian u32 length + payload</c>,
///         repeated, then a bare 4-byte <c>"END "</c>. Every value inside a chunk is LITTLE-endian.
///         Positions are XnGine fixed point — world units × 256, second component vertical.
///     </para>
///     <para>
///         The load-bearing structure is <c>RAHD</c>: an 8-byte header and N × 165-byte script
///         OBJECT records whose (offset, length) pairs partition seven sibling chunks. Placements
///         (<c>MPOB</c>) name those objects, and every mesh name in <c>MPOB</c>/<c>MPSO</c>/<c>MPRP</c>
///         resolves in the map's own <c>3dart\&lt;MAP&gt;.ROB</c> — 100% on retail.
///     </para>
/// </summary>
internal sealed class RedguardRgmFile
{
    /// <summary>Bytes in a chunk header: a 4-char tag and a big-endian length.</summary>
    public const int ChunkHeaderLength = 8;

    /// <summary>Bytes of the <c>RAHD</c> header before its records.</summary>
    public const int ObjectTableHeaderLength = 8;

    /// <summary>Bytes in one <c>RAHD</c> script-object record. Odd — it is a packed struct.</summary>
    public const int ObjectRecordLength = 165;

    /// <summary>Bytes in one <c>MPOB</c> placement and one <c>MPSO</c> static mesh record.</summary>
    public const int PlacementRecordLength = 66;

    /// <summary>Bytes in one <c>MPSL</c> light record.</summary>
    public const int LightRecordLength = 42;

    /// <summary>Bytes in one <c>MPSF</c> flat record.</summary>
    public const int FlatRecordLength = 24;

    /// <summary>Bytes in one <c>MPRP</c> rope record.</summary>
    public const int RopeRecordLength = 80;

    /// <summary>Bytes in one <c>MPSZ</c> mesh-size row.</summary>
    public const int MeshSizeRowLength = 49;

    /// <summary>Bytes in one <c>RAVC</c> collision-sphere record.</summary>
    public const int CollisionSphereLength = 9;

    /// <summary>Fixed-point scale of every position: world units × 256.</summary>
    public const int UnitsPerWorldUnit = 256;

    /// <summary>The 24 engine objects that open every retail object table, in order.</summary>
    public static readonly IReadOnlyList<string> FixedObjectLabels =
    [
        "MAPCOUNT", "LASER", "ITORCH", "ITEMLIST", "ISTRENTH", "ISHOVEL", "ISAP", "INOUSE", "IMILK", "IIRONSK",
        "IHEALTH", "IFLASK", "IFLAG", "IECTO", "IBOTALOE", "IBOTWAT", "IBLOOD", "IBANDAGE", "GREMLIN", "FIREBALL",
        "COMMON", "CAMERA", "CYRUS", "DEBUG"
    ];

    private RedguardRgmFile(string name)
    {
        Name = name;
    }

    private static ReadOnlySpan<byte> Terminator => "END "u8;

    /// <summary>Source file name, for messages.</summary>
    public string Name { get; }

    /// <summary>Every chunk in file order, whether or not this reader interprets it.</summary>
    public IReadOnlyList<RedguardRgmChunk> Chunks { get; private set; } = [];

    /// <summary>The <c>RAHD</c> header's second dword — tracks the script compile (0x37801B on 26 retail files).</summary>
    public uint CompileWord { get; private set; }

    /// <summary>The script objects, in <c>RAHD</c> order.</summary>
    public IReadOnlyList<RedguardRgmObject> Objects { get; private set; } = [];

    /// <summary>The <c>RAST</c> string pool.</summary>
    public IReadOnlyList<string> Strings { get; private set; } = [];

    /// <summary><c>MPOB</c> placements.</summary>
    public IReadOnlyList<RedguardRgmPlacement> Placements { get; private set; } = [];

    /// <summary><c>MPSO</c> static mesh placements.</summary>
    public IReadOnlyList<RedguardRgmStaticMesh> StaticMeshes { get; private set; } = [];

    /// <summary><c>MPSL</c> point lights.</summary>
    public IReadOnlyList<RedguardRgmLight> Lights { get; private set; } = [];

    /// <summary><c>MPSF</c> flat (billboard) placements.</summary>
    public IReadOnlyList<RedguardRgmFlat> Flats { get; private set; } = [];

    /// <summary><c>MPMK</c> markers, by ordinal (WORLD.INI's <c>start_marker</c> indexes this list).</summary>
    public IReadOnlyList<RedguardRgmMarker> Markers { get; private set; } = [];

    /// <summary><c>MPRP</c> rope and chain placements.</summary>
    public IReadOnlyList<RedguardRgmRope> Ropes { get; private set; } = [];

    /// <summary><c>MPSZ</c> mesh bounding-size rows.</summary>
    public IReadOnlyList<RedguardRgmMeshSize> MeshSizes { get; private set; } = [];

    /// <summary><c>RAAN</c> animation-mesh entries, in object order.</summary>
    public IReadOnlyList<RedguardRgmAnimationMesh> AnimationMeshes { get; private set; } = [];

    /// <summary><c>WDNM</c> AI navigation graphs.</summary>
    public IReadOnlyList<RedguardRgmNavigationMap> NavigationMaps { get; private set; } = [];

    /// <summary><c>RAVC</c> vertex-anchored collision spheres; empty except in two retail files.</summary>
    public IReadOnlyList<RedguardRgmCollisionSphere> CollisionSpheres { get; private set; } = [];

    /// <summary>Length of a chunk by tag, or 0 when absent — for the script-side spans this reader does not interpret.</summary>
    public int ChunkLength(string tag)
    {
        foreach (var chunk in Chunks)
        {
            if (chunk.Tag == tag)
            {
                return chunk.Length;
            }
        }

        return 0;
    }

    /// <summary>Content probe: the file opens with the <c>RAHD</c> tag.</summary>
    public static bool IsRgmFile(ReadOnlySpan<byte> bytes)
    {
        return bytes.Length >= ChunkHeaderLength && bytes[..4].SequenceEqual("RAHD"u8);
    }

    /// <summary>Parses a map database, throwing <see cref="InvalidDataException" /> when a chunk does not tile.</summary>
    public static RedguardRgmFile Parse(ReadOnlyMemory<byte> bytes, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var file = new RedguardRgmFile(name);
        var span = bytes.Span;

        var chunks = WalkChunks(span, name);
        file.Chunks = chunks;
        var byTag = new Dictionary<string, RedguardRgmChunk>(StringComparer.Ordinal);
        foreach (var chunk in chunks)
        {
            byTag.TryAdd(chunk.Tag, chunk);
        }

        var objectTable = Require(byTag, "RAHD", name);
        var strings = Payload(span, byTag, "RAST");
        file.Strings = SplitStrings(strings);
        file.CompileWord = BinaryPrimitives.ReadUInt32LittleEndian(span[(objectTable.Offset + 4)..]);
        file.Objects = ReadObjects(span, objectTable, Payload(span, byTag, "RANM"), Payload(span, byTag, "RASB"),
            strings, name);
        file.AnimationMeshes = ReadAnimationMeshes(Payload(span, byTag, "RAAN"), file.Objects, name);
        file.Placements = ReadPlacements(Payload(span, byTag, "MPOB"), name);
        file.StaticMeshes = ReadStaticMeshes(Payload(span, byTag, "MPSO"), name);
        file.Lights = ReadLights(Payload(span, byTag, "MPSL"), name);
        file.Flats = ReadFlats(Payload(span, byTag, "MPSF"), name);
        file.Markers = ReadMarkers(Payload(span, byTag, "MPMK"), name);
        file.Ropes = ReadRopes(Payload(span, byTag, "MPRP"), name);
        file.MeshSizes = ReadMeshSizes(Payload(span, byTag, "MPSZ"), name);
        file.NavigationMaps = ReadNavigationMaps(Payload(span, byTag, "WDNM"), name);
        file.CollisionSpheres = ReadCollisionSpheres(Payload(span, byTag, "RAVC"), name);
        return file;
    }

    private static List<RedguardRgmChunk> WalkChunks(ReadOnlySpan<byte> bytes, string name)
    {
        var chunks = new List<RedguardRgmChunk>();
        var position = 0;
        while (position < bytes.Length)
        {
            if (position + Terminator.Length == bytes.Length && bytes[position..].SequenceEqual(Terminator))
            {
                return chunks;
            }

            if (position + ChunkHeaderLength > bytes.Length)
            {
                throw new InvalidDataException(
                    $"{name}: {bytes.Length - position} trailing bytes at {position} are neither a chunk nor \"END \".");
            }

            for (var i = 0; i < 4; i++)
            {
                if (bytes[position + i] is < 0x20 or > 0x7E)
                {
                    throw new InvalidDataException($"{name}: no chunk tag at offset {position}.");
                }
            }

            var tag = Encoding.ASCII.GetString(bytes.Slice(position, 4));
            var length = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(position + 4, 4));
            var offset = position + ChunkHeaderLength;
            if (length > int.MaxValue || offset + length > bytes.Length)
            {
                throw new InvalidDataException(
                    $"{name}: chunk '{tag}' declares {length} bytes at {offset}, past the end of the file.");
            }

            chunks.Add(new RedguardRgmChunk(tag, offset, (int)length));
            position = offset + (int)length;
        }

        throw new InvalidDataException($"{name}: file ends without an \"END \" terminator.");
    }

    private static RedguardRgmChunk Require(Dictionary<string, RedguardRgmChunk> byTag, string tag, string name)
    {
        return byTag.TryGetValue(tag, out var chunk)
            ? chunk
            : throw new InvalidDataException($"{name}: no '{tag}' chunk.");
    }

    private static ReadOnlySpan<byte> Payload(ReadOnlySpan<byte> bytes, Dictionary<string, RedguardRgmChunk> byTag,
        string tag)
    {
        return byTag.TryGetValue(tag, out var chunk) ? bytes.Slice(chunk.Offset, chunk.Length) : default;
    }

    private static int CountRecords(ReadOnlySpan<byte> payload, int stride, string tag, string name)
    {
        if (payload.Length == 0)
        {
            return 0;
        }

        if (payload.Length < 4)
        {
            throw new InvalidDataException($"{name}: '{tag}' is {payload.Length} bytes, too short for its count.");
        }

        var count = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        if (count > int.MaxValue / stride || 4 + count * stride != payload.Length)
        {
            throw new InvalidDataException(
                $"{name}: '{tag}' declares {count} records of {stride} bytes, which do not tile its {payload.Length}-byte payload.");
        }

        return (int)count;
    }

    private static List<string> SplitStrings(ReadOnlySpan<byte> pool)
    {
        var list = new List<string>();
        var start = 0;
        for (var i = 0; i < pool.Length; i++)
        {
            if (pool[i] == 0)
            {
                list.Add(Encoding.ASCII.GetString(pool[start..i]));
                start = i + 1;
            }
        }

        return list;
    }

    private static string ReadCString(ReadOnlySpan<byte> pool, int offset, string name, string what)
    {
        if (offset < 0 || offset >= pool.Length)
        {
            throw new InvalidDataException($"{name}: {what} offset {offset} lies outside its {pool.Length}-byte pool.");
        }

        var end = pool[offset..].IndexOf((byte)0);
        return Encoding.ASCII.GetString(end < 0 ? pool[offset..] : pool.Slice(offset, end));
    }

    /// <summary>A NUL-padded field that may fill its width without a terminator (MPOB's 9-byte mesh names do).</summary>
    private static string ReadPaddedName(ReadOnlySpan<byte> field)
    {
        var end = field.IndexOf((byte)0);
        return Encoding.ASCII.GetString(end < 0 ? field : field[..end]);
    }

    private static List<RedguardRgmObject> ReadObjects(
        ReadOnlySpan<byte> bytes, RedguardRgmChunk table, ReadOnlySpan<byte> names, ReadOnlySpan<byte> stringRefs,
        ReadOnlySpan<byte> strings, string name)
    {
        var payload = bytes.Slice(table.Offset, table.Length);
        if (payload.Length < ObjectTableHeaderLength)
        {
            throw new InvalidDataException($"{name}: RAHD is {payload.Length} bytes, shorter than its header.");
        }

        var count = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        if (ObjectTableHeaderLength + (long)count * ObjectRecordLength != payload.Length)
        {
            throw new InvalidDataException(
                $"{name}: RAHD declares {count} objects of {ObjectRecordLength} bytes, which do not tile its {payload.Length}-byte payload.");
        }

        var objects = new List<RedguardRgmObject>((int)count);
        for (var i = 0; i < count; i++)
        {
            var record = payload.Slice(ObjectTableHeaderLength + i * ObjectRecordLength, ObjectRecordLength);
            var nameLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(record[21..]);
            var nameOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(record[25..]);
            var scriptName = nameLength > 1
                ? ReadCString(names, nameOffset, name, $"object {i} RANM name")
                : string.Empty;

            var refCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(record[65..]);
            var refOffset = BinaryPrimitives.ReadInt32LittleEndian(record[73..]);
            var refs = new List<string>(refCount);
            for (var k = 0; k < refCount; k++)
            {
                var at = refOffset + 4 * k;
                if (at < 0 || at + 4 > stringRefs.Length)
                {
                    throw new InvalidDataException(
                        $"{name}: object {i} references RASB entry {k} at {at}, outside the {stringRefs.Length}-byte chunk.");
                }

                refs.Add(ReadCString(strings, (int)BinaryPrimitives.ReadUInt32LittleEndian(stringRefs[at..]), name,
                    $"object {i} string reference {k}"));
            }

            objects.Add(new RedguardRgmObject(
                i,
                ReadPaddedName(record.Slice(4, 9)),
                scriptName,
                (int)BinaryPrimitives.ReadUInt32LittleEndian(record[13..]),
                (int)BinaryPrimitives.ReadUInt32LittleEndian(record[117..]),
                (int)BinaryPrimitives.ReadUInt32LittleEndian(record[33..]),
                (int)BinaryPrimitives.ReadUInt32LittleEndian(record[133..]),
                (int)BinaryPrimitives.ReadUInt32LittleEndian(record[81..]),
                (int)BinaryPrimitives.ReadUInt32LittleEndian(record[77..]),
                (int)BinaryPrimitives.ReadUInt32LittleEndian(record[89..]),
                (int)BinaryPrimitives.ReadUInt32LittleEndian(record[97..]),
                [
                    BinaryPrimitives.ReadInt32LittleEndian(record[137..]),
                    BinaryPrimitives.ReadInt32LittleEndian(record[141..]),
                    BinaryPrimitives.ReadInt32LittleEndian(record[145..])
                ],
                BinaryPrimitives.ReadUInt16LittleEndian(record[155..]),
                BinaryPrimitives.ReadUInt16LittleEndian(record[149..]) == 1,
                refs,
                (int)BinaryPrimitives.ReadUInt32LittleEndian(record[161..])));
        }

        return objects;
    }

    private static List<RedguardRgmAnimationMesh> ReadAnimationMeshes(ReadOnlySpan<byte> payload,
        IReadOnlyList<RedguardRgmObject> objects, string name)
    {
        var list = new List<RedguardRgmAnimationMesh>();
        var position = 0;
        foreach (var owner in objects)
        {
            for (var k = 0; k < owner.AnimationMeshCount; k++)
            {
                if (position + 6 > payload.Length)
                {
                    throw new InvalidDataException(
                        $"{name}: RAAN runs out at entry {list.Count} for object {owner.Label}.");
                }

                var planeCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(payload[position..]);
                var frameCount = payload[position + 4];
                var flag = (char)payload[position + 5];
                var path = ReadCString(payload, position + 6, name, $"RAAN entry {list.Count} path");
                list.Add(new RedguardRgmAnimationMesh(owner.Index, path, planeCount, frameCount, flag));
                position += 6 + path.Length + 1;
            }
        }

        if (position != payload.Length)
        {
            throw new InvalidDataException($"{name}: RAAN entries end at {position} of {payload.Length} bytes.");
        }

        return list;
    }

    private static List<RedguardRgmPlacement> ReadPlacements(ReadOnlySpan<byte> payload, string name)
    {
        var count = CountRecords(payload, PlacementRecordLength, "MPOB", name);
        var list = new List<RedguardRgmPlacement>(count);
        for (var i = 0; i < count; i++)
        {
            var r = payload.Slice(4 + i * PlacementRecordLength, PlacementRecordLength);
            list.Add(new RedguardRgmPlacement(
                i,
                BinaryPrimitives.ReadUInt16LittleEndian(r[4..]),
                ReadPaddedName(r.Slice(6, 9)),
                ReadPaddedName(r.Slice(15, 9)),
                BinaryPrimitives.ReadUInt16LittleEndian(r[24..]) != 0,
                ReadVector(r[26..]),
                ReadVector(r[38..]),
                BinaryPrimitives.ReadInt16LittleEndian(r[56..]),
                BinaryPrimitives.ReadUInt16LittleEndian(r[58..]),
                BinaryPrimitives.ReadUInt16LittleEndian(r[54..])));
        }

        return list;
    }

    private static List<RedguardRgmStaticMesh> ReadStaticMeshes(ReadOnlySpan<byte> payload, string name)
    {
        var count = CountRecords(payload, PlacementRecordLength, "MPSO", name);
        var list = new List<RedguardRgmStaticMesh>(count);
        for (var i = 0; i < count; i++)
        {
            var r = payload.Slice(4 + i * PlacementRecordLength, PlacementRecordLength);
            var matrix = new int[9];
            for (var k = 0; k < 9; k++)
            {
                matrix[k] = BinaryPrimitives.ReadInt32LittleEndian(r[(28 + 4 * k)..]);
            }

            list.Add(new RedguardRgmStaticMesh(i, ReadPaddedName(r.Slice(4, 12)), ReadVector(r[16..]), matrix));
        }

        return list;
    }

    private static List<RedguardRgmLight> ReadLights(ReadOnlySpan<byte> payload, string name)
    {
        var count = CountRecords(payload, LightRecordLength, "MPSL", name);
        var list = new List<RedguardRgmLight>(count);
        for (var i = 0; i < count; i++)
        {
            var r = payload.Slice(4 + i * LightRecordLength, LightRecordLength);
            list.Add(new RedguardRgmLight(
                i,
                (int)BinaryPrimitives.ReadUInt32LittleEndian(r[4..]),
                ReadVector(r[8..]),
                BinaryPrimitives.ReadUInt16LittleEndian(r[20..]),
                BinaryPrimitives.ReadInt16LittleEndian(r[22..]),
                BinaryPrimitives.ReadInt16LittleEndian(r[24..]),
                BinaryPrimitives.ReadInt16LittleEndian(r[26..]),
                BinaryPrimitives.ReadInt16LittleEndian(r[28..])));
        }

        return list;
    }

    private static List<RedguardRgmFlat> ReadFlats(ReadOnlySpan<byte> payload, string name)
    {
        var count = CountRecords(payload, FlatRecordLength, "MPSF", name);
        var list = new List<RedguardRgmFlat>(count);
        for (var i = 0; i < count; i++)
        {
            var r = payload.Slice(4 + i * FlatRecordLength, FlatRecordLength);
            var textureId = BinaryPrimitives.ReadUInt16LittleEndian(r[20..]);
            list.Add(new RedguardRgmFlat(i, ReadVector(r[8..]), textureId >> 7, textureId & 127));
        }

        return list;
    }

    private static List<RedguardRgmMarker> ReadMarkers(ReadOnlySpan<byte> payload, string name)
    {
        // Two arrays behind one count: positions, then one used-flag byte per marker. It is
        // 12n + n bytes, NOT 13-byte records — reading records misaligns from marker 1.
        var count = CountRecords(payload, 13, "MPMK", name);
        var list = new List<RedguardRgmMarker>(count);
        for (var i = 0; i < count; i++)
        {
            list.Add(new RedguardRgmMarker(i, ReadVector(payload[(4 + 12 * i)..]), payload[4 + 12 * count + i] != 0));
        }

        return list;
    }

    private static List<RedguardRgmRope> ReadRopes(ReadOnlySpan<byte> payload, string name)
    {
        var count = CountRecords(payload, RopeRecordLength, "MPRP", name);
        var list = new List<RedguardRgmRope>(count);
        for (var i = 0; i < count; i++)
        {
            var r = payload.Slice(4 + i * RopeRecordLength, RopeRecordLength);
            list.Add(new RedguardRgmRope(
                i,
                ReadVector(r[4..]),
                BinaryPrimitives.ReadUInt16LittleEndian(r[32..]),
                ReadPaddedName(r.Slice(34, 9)),
                ReadPaddedName(r.Slice(43, 9)),
                ReadVector(r[52..])));
        }

        return list;
    }

    private static List<RedguardRgmMeshSize> ReadMeshSizes(ReadOnlySpan<byte> payload, string name)
    {
        if (payload.Length % MeshSizeRowLength != 0)
        {
            throw new InvalidDataException(
                $"{name}: MPSZ is {payload.Length} bytes, not a multiple of {MeshSizeRowLength}.");
        }

        var count = payload.Length / MeshSizeRowLength;
        var list = new List<RedguardRgmMeshSize>(count);
        for (var i = 0; i < count; i++)
        {
            var r = payload.Slice(i * MeshSizeRowLength, MeshSizeRowLength);
            list.Add(new RedguardRgmMeshSize(i, ReadVector(r[1..]), ReadVector(r[25..]), ReadVector(r[37..])));
        }

        return list;
    }

    private static List<RedguardRgmNavigationMap> ReadNavigationMaps(ReadOnlySpan<byte> payload, string name)
    {
        if (payload.Length == 0)
        {
            return [];
        }

        var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(payload);
        var maps = new List<RedguardRgmNavigationMap>(count);
        var position = 4;
        for (var m = 0; m < count; m++)
        {
            if (position + 4 > payload.Length)
            {
                throw new InvalidDataException($"{name}: WDNM map {m} has no length word.");
            }

            var bodyLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(payload[position..]);
            var body = payload.Slice(position + 4, bodyLength);
            if (body.Length < 24)
            {
                throw new InvalidDataException(
                    $"{name}: WDNM map {m} body is {body.Length} bytes, shorter than its header.");
            }

            var nodeCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(body);
            var nodes = new List<RedguardRgmNavigationNode>(nodeCount);
            var at = 24;
            for (var n = 0; n < nodeCount; n++)
            {
                var nodeLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(body[at..]);
                var routeCount = body[at + 11];
                if (nodeLength != 8 + 4 * routeCount)
                {
                    throw new InvalidDataException(
                        $"{name}: WDNM map {m} node {n} declares {nodeLength} bytes for {routeCount} routes.");
                }

                var routes = new RedguardRgmNavigationRoute[routeCount];
                for (var k = 0; k < routeCount; k++)
                {
                    var route = body[(at + 12 + 4 * k)..];
                    routes[k] = new RedguardRgmNavigationRoute(
                        BinaryPrimitives.ReadUInt16LittleEndian(route),
                        BinaryPrimitives.ReadUInt16LittleEndian(route[2..]));
                }

                nodes.Add(new RedguardRgmNavigationNode(
                    n,
                    BinaryPrimitives.ReadUInt16LittleEndian(body[(at + 4)..]),
                    BinaryPrimitives.ReadInt16LittleEndian(body[(at + 6)..]),
                    BinaryPrimitives.ReadUInt16LittleEndian(body[(at + 8)..]),
                    routes));
                at += 4 + nodeLength;
            }

            if (at != body.Length)
            {
                throw new InvalidDataException($"{name}: WDNM map {m} nodes end at {at} of {body.Length} bytes.");
            }

            maps.Add(new RedguardRgmNavigationMap(
                m,
                ReadVector(body[8..]),
                (int)BinaryPrimitives.ReadUInt32LittleEndian(body[20..]),
                nodes));
            position += 4 + bodyLength;
        }

        if (position != payload.Length)
        {
            throw new InvalidDataException($"{name}: WDNM maps end at {position} of {payload.Length} bytes.");
        }

        return maps;
    }

    private static List<RedguardRgmCollisionSphere> ReadCollisionSpheres(ReadOnlySpan<byte> payload, string name)
    {
        if (payload.Length % CollisionSphereLength != 0)
        {
            throw new InvalidDataException(
                $"{name}: RAVC is {payload.Length} bytes, not a multiple of {CollisionSphereLength}.");
        }

        var list = new List<RedguardRgmCollisionSphere>(payload.Length / CollisionSphereLength);
        for (var i = 0; i < payload.Length / CollisionSphereLength; i++)
        {
            var r = payload.Slice(i * CollisionSphereLength, CollisionSphereLength);
            list.Add(new RedguardRgmCollisionSphere(
                (sbyte)r[0], (sbyte)r[1], (sbyte)r[2],
                BinaryPrimitives.ReadUInt16LittleEndian(r[3..]),
                BinaryPrimitives.ReadUInt32LittleEndian(r[5..])));
        }

        return list;
    }

    private static RedguardRgmVector ReadVector(ReadOnlySpan<byte> at)
    {
        return new RedguardRgmVector(
            BinaryPrimitives.ReadInt32LittleEndian(at),
            BinaryPrimitives.ReadInt32LittleEndian(at[4..]),
            BinaryPrimitives.ReadInt32LittleEndian(at[8..]));
    }
}

/// <summary>One container chunk: its tag and payload span.</summary>
internal readonly record struct RedguardRgmChunk(string Tag, int Offset, int Length);

/// <summary>A fixed-point triple: world units × 256, <see cref="Y" /> vertical.</summary>
internal readonly record struct RedguardRgmVector(int X, int Y, int Z)
{
    /// <summary>The triple in whole world units.</summary>
    public (int X, int Y, int Z) WorldUnits =>
        (X / RedguardRgmFile.UnitsPerWorldUnit, Y / RedguardRgmFile.UnitsPerWorldUnit,
            Z / RedguardRgmFile.UnitsPerWorldUnit);
}

/// <summary>
///     One <c>RAHD</c> script object — the directory entry that partitions the script-side chunks.
///     <see cref="Label" /> is the 8-char engine name; <see cref="ScriptName" /> its lowercase
///     script alias from <c>RANM</c> (empty when it has none).
/// </summary>
internal sealed record RedguardRgmObject(
    int Index,
    string Label,
    string ScriptName,
    int InstanceCount,
    int VariablesPerInstance,
    int AnimationMeshCount,
    int FrameCount,
    int ScriptOffset,
    int ScriptLength,
    int HookLength,
    int LocalVectorCount,
    IReadOnlyList<int> MeshSizeIndices,
    ushort CharacterId,
    bool IsActor,
    IReadOnlyList<string> StringReferences,
    int CollisionSphereCount)
{
    /// <summary>True for the 24 engine objects that open every retail table.</summary>
    public bool IsFixed => Index < RedguardRgmFile.FixedObjectLabels.Count;
}

/// <summary>
///     One <c>MPOB</c> placement. <see cref="Type" />: 1 object, 2 point marker (<c>ENT*</c>/<c>EXT*</c>
///     entries and exits, ambient sound points, doors), 3 particle emitter, 4 light, 6 <c>X</c>
///     world-entry marker carrying <see cref="WorldIndex" />, 257 = type 1 with an unidentified
///     0x100 flag. <see cref="MeshName" /> is the <c>NAME.3D</c> name truncated to 9 bytes; its
///     stem resolves in the map's own ROB. <see cref="Rotation" /> is in 2048-per-turn units by
///     analogy with Battlespire — inferred from the quarter-turn peaks, not proven in-file.
/// </summary>
internal sealed record RedguardRgmPlacement(
    int Index,
    ushort Type,
    string ObjectName,
    string MeshName,
    bool HasMesh,
    RedguardRgmVector Position,
    RedguardRgmVector Rotation,
    short MeshSlot,
    ushort WorldIndex,
    ushort LightRadius)
{
    /// <summary>The mesh's ROB segment name — the part of <see cref="MeshName" /> before any extension.</summary>
    public string MeshStem
    {
        get
        {
            var dot = MeshName.IndexOf('.');
            return dot < 0 ? MeshName : MeshName[..dot];
        }
    }

    /// <summary>A type-2 point marker named <c>ENT*</c> or <c>EXT*</c> — where the map is entered or left.</summary>
    public bool IsEntryOrExit =>
        Type == 2 && (ObjectName.StartsWith("ENT", StringComparison.Ordinal) ||
                      ObjectName.StartsWith("EXT", StringComparison.Ordinal));
}

/// <summary>
///     One <c>MPSO</c> static mesh: a ROB segment name, a position and a 3×3 rotation in 4.28 fixed point (1.0 =
///     0x10000000).
/// </summary>
internal sealed record RedguardRgmStaticMesh(
    int Index,
    string MeshName,
    RedguardRgmVector Position,
    IReadOnlyList<int> Rotation);

/// <summary>One <c>MPSL</c> light. <see cref="WorldIndex" /> is a WORLD.INI world index, or 0 for the second record kind.</summary>
internal readonly record struct RedguardRgmLight(
    int Index,
    int WorldIndex,
    RedguardRgmVector Position,
    ushort Radius,
    short KindAValue,
    short Red,
    short Green,
    short Blue);

/// <summary>
///     One <c>MPSF</c> flat: a position and a <c>3dart\TEXTURE.nnn</c> record in Daggerfall's archive×128+record
///     packing.
/// </summary>
internal readonly record struct RedguardRgmFlat(
    int Index,
    RedguardRgmVector Position,
    int TextureArchive,
    int TextureRecord);

/// <summary>One <c>MPMK</c> marker; <see cref="IsUsed" /> is false on the reserved empty slots.</summary>
internal readonly record struct RedguardRgmMarker(int Index, RedguardRgmVector Position, bool IsUsed);

/// <summary>One <c>MPRP</c> rope or chain, built from <see cref="LinkMeshName" /> links.</summary>
internal sealed record RedguardRgmRope(
    int Index,
    RedguardRgmVector Anchor,
    ushort LinkCount,
    string AnchorMeshName,
    string LinkMeshName,
    RedguardRgmVector Colour);

/// <summary>One <c>MPSZ</c> row: a mesh's clamped bounding half-extents in whole units, and their sum.</summary>
internal readonly record struct RedguardRgmMeshSize(
    int Index,
    RedguardRgmVector Extent,
    RedguardRgmVector PositiveHalfExtent,
    RedguardRgmVector NegativeHalfExtent);

/// <summary>
///     One <c>RAAN</c> entry: an animation mesh an object uses. <see cref="Path" /> says <c>.3d</c>; retail ships it
///     as <c>.3DC</c>.
/// </summary>
internal sealed record RedguardRgmAnimationMesh(
    int ObjectIndex,
    string Path,
    int PlaneCount,
    byte FrameCount,
    char Flag);

/// <summary>
///     One <c>WDNM</c> route: <see cref="Distance" /> is exactly ⌊3-D Euclidean distance⌋ to
///     <see cref="TargetNode" />.
/// </summary>
internal readonly record struct RedguardRgmNavigationRoute(ushort TargetNode, ushort Distance);

/// <summary>One <c>WDNM</c> node. X and Z are unsigned (graphs straddle 32,767); Y is signed.</summary>
internal sealed record RedguardRgmNavigationNode(
    int Index,
    ushort X,
    short Y,
    ushort Z,
    IReadOnlyList<RedguardRgmNavigationRoute> Routes);

/// <summary>One <c>WDNM</c> graph: the engine's "Total nodes in map %d at (%d,%d,%d) radius %d".</summary>
internal sealed record RedguardRgmNavigationMap(
    int Index,
    RedguardRgmVector Centre,
    int Radius,
    IReadOnlyList<RedguardRgmNavigationNode> Nodes);

/// <summary>One <c>RAVC</c> collision sphere anchored at a model vertex plus an offset.</summary>
internal readonly record struct RedguardRgmCollisionSphere(
    sbyte OffsetX,
    sbyte OffsetY,
    sbyte OffsetZ,
    ushort VertexIndex,
    uint Radius);
