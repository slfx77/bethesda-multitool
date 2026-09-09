using System.Buffers.Binary;
using System.Text;

namespace BethesdaMultitool.Core.Formats.VanBuren;

/// <summary>A point or rotation triple as the map stores it: three little-endian floats.</summary>
internal readonly record struct VanBurenVector3(float X, float Y, float Z);

/// <summary>One chunk of an <c>EMAP</c> stream, header included.</summary>
/// <param name="Tag">Four printable characters.</param>
/// <param name="Version">The second header dword — 5 on the map header, 0 or 1 elsewhere.</param>
/// <param name="Offset">Offset of the chunk HEADER within the payload.</param>
/// <param name="Length">Total chunk length INCLUDING its 12-byte header, as the file declares it.</param>
internal readonly record struct VanBurenMapChunk(string Tag, uint Version, int Offset, int Length)
{
    /// <summary>Where the body begins.</summary>
    public int BodyOffset => Offset + VanBurenMapFile.ChunkHeaderLength;

    /// <summary>Body length.</summary>
    public int BodyLength => Length - VanBurenMapFile.ChunkHeaderLength;
}

/// <summary>
///     The <c>EMAP</c> header chunk, as <c>GameMap::ReadHeader</c> (<c>F3.exe</c> 0x0056c9d0) reads
///     it: a dword, FOUR u16-length-prefixed names, then fields gated on the chunk VERSION.
/// </summary>
/// <param name="Version">The header chunk's version; 5 on every shipped map.</param>
/// <param name="LeadingValue">The dword before the names; 0 on every shipped map.</param>
/// <param name="SceneName">The <c>.8</c> name — the <c>8TRE</c> octree scene, by stem.</param>
/// <param name="WalkGridName">The <c>.rle</c> name — the run-length cell grid, by stem.</param>
/// <param name="AreaMapName">The <c>_AM.tga</c>/<c>.dds</c> name — the area-map texture.</param>
/// <param name="FourthName">A fourth name slot the reader always consumes; EMPTY on every shipped map.</param>
/// <param name="ColourA">Three bytes read when the version is at least 1 (stored with alpha 0x80 by the game).</param>
/// <param name="FlagA">A byte read when the version is at least 2.</param>
/// <param name="ColourB">Four bytes read when the version is at least 3.</param>
/// <param name="FlagB">A byte read when the version is at least 4.</param>
/// <param name="FlagC">A byte read when the version is at least 5, alongside the last colour and floats.</param>
/// <param name="ColourC">Three bytes (version 5) the game packs into one colour word.</param>
/// <param name="FloatA">First of three floats (version 5): 250 on 36 of 38 shipped maps.</param>
/// <param name="FloatB">Second (version 5): 500 on 36 of 38.</param>
/// <param name="FloatC">Third (version 5): 1.0 on all 38.</param>
internal sealed record VanBurenMapHeader(
    uint Version,
    uint LeadingValue,
    string SceneName,
    string WalkGridName,
    string AreaMapName,
    string FourthName,
    byte[] ColourA,
    byte FlagA,
    byte[] ColourB,
    byte FlagB,
    byte FlagC,
    byte[] ColourC,
    float FloatA,
    float FloatB,
    float FloatC)
{
    /// <summary>The map's stem — the scene name without its <c>.8</c> extension.</summary>
    public string Stem => Path.GetFileNameWithoutExtension(SceneName);
}

/// <summary>An <c>EME2</c> entity placement.</summary>
/// <param name="Template">The entity template it instances, e.g. <c>CrittersCow.CRT</c>.</param>
/// <param name="Position">World position.</param>
/// <param name="Rotation">Rotation in RADIANS — the game multiplies each component by 57.2958 on read.</param>
/// <param name="Flag">The byte after the rotation (the game stores it as a bool on the entity).</param>
/// <param name="InstanceName">The name inside the nested <c>EEOV</c> override, e.g. <c>CowCritters_000</c>.</param>
/// <param name="OverrideVersion">The nested <c>EEOV</c> chunk's version (1 or 2 on the shipped maps).</param>
/// <param name="OverrideLength">The nested <c>EEOV</c> chunk's total length; its interior is not decoded.</param>
internal sealed record VanBurenMapEntity(
    string Template,
    VanBurenVector3 Position,
    VanBurenVector3 Rotation,
    byte Flag,
    string InstanceName,
    uint OverrideVersion,
    int OverrideLength);

/// <summary>An <c>EMEF</c> map effect placement: a named point that plays one or more <c>.veg</c> effects.</summary>
internal sealed record VanBurenMapEffect(
    string Name,
    VanBurenVector3 Position,
    VanBurenVector3 Rotation,
    IReadOnlyList<string> Effects,
    byte Flag);

/// <summary>One <c>EMEP</c> entry point slot: a position and the dword the game reads beside it.</summary>
internal readonly record struct VanBurenMapEntryPoint(VanBurenVector3 Position, uint Value);

/// <summary>
///     The chunk that FOLLOWS an <c>EMTR</c> and completes it — the trigger's own record, read by the
///     trigger object the map created for the kind: <c>EBTR</c>, <c>ESTR</c> (script) or
///     <c>ETTR</c> (transition).
/// </summary>
/// <param name="Tag">Which record it is.</param>
/// <param name="Version">Its chunk version.</param>
/// <param name="Name">ESTR: the script (<c>.amx</c>); ETTR: the destination map; EBTR: null.</param>
/// <param name="Value">EBTR: the leading dword; ETTR: the byte after the name; ESTR: the variable count.</param>
/// <param name="Colour">EBTR: three bytes the game defaults to 0xC0 0xC0 0xC0; otherwise null.</param>
/// <param name="Flag">ETTR at version 1 or later: the trailing bool; otherwise null.</param>
/// <param name="Variables">ESTR: name/value pairs after the script; empty on every shipped map.</param>
internal sealed record VanBurenMapTriggerDetail(
    string Tag,
    uint Version,
    string? Name,
    uint Value,
    byte[]? Colour,
    bool? Flag,
    IReadOnlyList<(string Name, uint Value)> Variables);

/// <summary>An <c>EMTR</c> trigger region: a kind, a polygon, and the kind's own record.</summary>
/// <param name="Kind">The dword the game hands to its trigger factory; 6, 1 and 0 ship.</param>
/// <param name="Points">The polygon, in world units.</param>
/// <param name="Detail">The chunk that followed and completed it.</param>
internal sealed record VanBurenMapTrigger(
    uint Kind,
    IReadOnlyList<VanBurenVector3> Points,
    VanBurenMapTriggerDetail Detail);

/// <summary>An <c>EMNO</c> area-map note: a position, an icon texture and a dword.</summary>
internal sealed record VanBurenMapNote(VanBurenVector3 Position, string Icon, uint Value);

/// <summary>One <c>EMNP</c> nav point: a flag byte, two triples and five link slots.</summary>
/// <param name="Flag">The leading byte; the game keeps <c>== 1</c> as a bool.</param>
/// <param name="Position">First triple.</param>
/// <param name="Second">Second triple; zero on every shipped node.</param>
/// <param name="Links">Five signed bytes indexing other nodes; -1 is "none".</param>
internal sealed record VanBurenMapNavPoint(
    byte Flag,
    VanBurenVector3 Position,
    VanBurenVector3 Second,
    IReadOnlyList<int> Links);

/// <summary>One <c>EPTH</c> way point: position plus the triple the game keeps as its orientation.</summary>
internal readonly record struct VanBurenMapWayPoint(VanBurenVector3 Position, VanBurenVector3 Orientation);

/// <summary>An <c>EPTH</c> named way-point path; the game links the nodes into a ring.</summary>
internal sealed record VanBurenMapPath(string Name, IReadOnlyList<VanBurenMapWayPoint> Points);

/// <summary>An <c>EMSD</c> map sound: an instance name, a position, the sound file and two bytes.</summary>
internal sealed record VanBurenMapSound(string Name, VanBurenVector3 Position, string File, byte FlagA, byte FlagB);

/// <summary>A <c>2MWT</c> water body: only its name is read; the rest is a large parameter block.</summary>
internal sealed record VanBurenMapWater(string Name, int BodyLength);

/// <summary>
///     A Van Buren (cancelled Fallout 3, Dec 2003) <c>EMAP</c> map file. Original RE 2026-09-08 from
///     the data and the prototype's own executable; <c>kran27/VanBurenTools</c> is GPL and was not
///     consulted.
///     <para>
///         ⚑ <b>It is a CHUNK STREAM, not a tile grid.</b> Every chunk is
///         <c>
///             tag[4] + u32 version +
///             u32 length
///         </c>
///         where the length INCLUDES the 12-byte header — pinned by the game's own
///         <c>Chunk::Read</c> (<c>F3.exe</c> 0x004a1cc0, which reads the three dwords) and
///         <c>Chunk::SkipToEnd</c> (0x004a1d50, which seeks to <c>start + length</c>), and by exact
///         tiling of all 38 shipped maps. <c>GameMap::ReadChunk</c> (0x0056c770) dispatches on the
///         tag and SKIPS any it does not know, so unknown chunks are kept, not refused.
///     </para>
///     <para>
///         The game's own error strings name every chunk: <c>EMAP</c> header, <c>EMEP</c> entry
///         point, <c>EMTR</c> trigger, <c>EME2</c> entity, <c>2MWT</c> water, <c>ECAM</c> camera
///         constraints, <c>EMFG</c> fog, <c>EMNO</c> note, <c>EMNP</c> nav point, <c>EPTH</c> way
///         point, <c>EMSD</c> map sound, <c>EMEF</c> map effect (plus the obsolete <c>EMEN</c>
///         entity). Each typed reader here follows the matching handler and must consume its body
///         EXACTLY, so a mis-read layout fails loudly.
///     </para>
///     <para>
///         ⚑ The header's three names are the level's trio: <c>&lt;stem&gt;.8</c> is the
///         <c>8TRE</c> OCTREE scene, <c>&lt;stem&gt;.rle</c> the run-length walk grid
///         (<see cref="VanBurenWalkGrid" />), <c>&lt;stem&gt;_AM</c> the area-map texture. The
///         <c>.grp</c> container stores no names, so the trio is resolved through
///         <c>resource.rht</c> (<see cref="VanBurenResourceIndex" />) or, failing that, by the
///         scene's own texture names (<c>&lt;stem&gt;_N.ctx</c>).
///     </para>
///     <para>
///         ⚠ Rotations are stored in RADIANS (the entity handler at 0x00573f30 multiplies by
///         57.295776 after reading). ⚠ An <c>EMTR</c> is completed by the chunk AFTER it
///         (<c>EBTR</c>/<c>ESTR</c>/<c>ETTR</c>), which the trigger object reads from the stream
///         inside the <c>EMTR</c> handler; the outer walk then meets the same chunk again and
///         skips it as unknown. Here it is folded into the trigger.
///     </para>
/// </summary>
internal sealed class VanBurenMapFile
{
    /// <summary>The tag the first chunk must carry.</summary>
    public const string Tag = "EMAP";

    /// <summary>Tag, version and total length.</summary>
    public const int ChunkHeaderLength = 12;

    private VanBurenMapFile(string name, IReadOnlyList<VanBurenMapChunk> chunks, VanBurenMapHeader header)
    {
        Name = name;
        Chunks = chunks;
        Header = header;
    }

    /// <summary>Source name, for messages.</summary>
    public string Name { get; }

    /// <summary>Every chunk in file order, including the ones folded into typed records.</summary>
    public IReadOnlyList<VanBurenMapChunk> Chunks { get; }

    /// <summary>The header chunk's fields.</summary>
    public VanBurenMapHeader Header { get; }

    /// <summary>Entity placements (<c>EME2</c>).</summary>
    public IReadOnlyList<VanBurenMapEntity> Entities { get; private set; } = [];

    /// <summary>Map effect placements (<c>EMEF</c>).</summary>
    public IReadOnlyList<VanBurenMapEffect> Effects { get; private set; } = [];

    /// <summary>Entry point slots (<c>EMEP</c>); the game reads exactly six of them.</summary>
    public IReadOnlyList<VanBurenMapEntryPoint> EntryPoints { get; private set; } = [];

    /// <summary>Trigger regions (<c>EMTR</c> plus the record that follows each).</summary>
    public IReadOnlyList<VanBurenMapTrigger> Triggers { get; private set; } = [];

    /// <summary>The four camera-constraint floats (<c>ECAM</c>), or null when the map has none.</summary>
    public IReadOnlyList<float>? CameraConstraints { get; private set; }

    /// <summary>Area-map notes (<c>EMNO</c>).</summary>
    public IReadOnlyList<VanBurenMapNote> Notes { get; private set; } = [];

    /// <summary>Nav points (<c>EMNP</c>); an empty list on 37 of 38 shipped maps.</summary>
    public IReadOnlyList<VanBurenMapNavPoint> NavPoints { get; private set; } = [];

    /// <summary>Way-point paths (<c>EPTH</c>).</summary>
    public IReadOnlyList<VanBurenMapPath> Paths { get; private set; } = [];

    /// <summary>Map sounds (<c>EMSD</c>).</summary>
    public IReadOnlyList<VanBurenMapSound> Sounds { get; private set; } = [];

    /// <summary>Water bodies (<c>2MWT</c>).</summary>
    public IReadOnlyList<VanBurenMapWater> Waters { get; private set; } = [];

    /// <summary>Tags of chunks no typed reader claimed — kept, as the game keeps them.</summary>
    public IReadOnlyList<string> UnclaimedTags { get; private set; } = [];

    /// <summary>Content probe: the tag, a version, and a header length that fits.</summary>
    public static bool IsMapFile(ReadOnlySpan<byte> bytes)
    {
        return bytes.Length >= ChunkHeaderLength
               && bytes[..4].SequenceEqual("EMAP"u8)
               && BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]) is var length
               && length >= ChunkHeaderLength
               && length <= bytes.Length;
    }

    /// <summary>Parses a map, throwing <see cref="InvalidDataException" /> when it does not tile.</summary>
    public static VanBurenMapFile Parse(ReadOnlySpan<byte> bytes, string name)
    {
        if (!TryParse(bytes, name, out var map, out var error))
        {
            throw new InvalidDataException(error);
        }

        return map;
    }

    /// <summary>Parses a map, reporting why rather than throwing.</summary>
    public static bool TryParse(ReadOnlySpan<byte> bytes, string name, out VanBurenMapFile map, out string error)
    {
        map = null!;
        if (!IsMapFile(bytes))
        {
            error = $"{name}: does not open with '{Tag}' and a chunk length that fits.";
            return false;
        }

        if (!TryWalkChunks(bytes, name, out var chunks, out error))
        {
            return false;
        }

        if (!TryReadHeader(bytes, chunks[0], name, out var header, out error))
        {
            return false;
        }

        var result = new VanBurenMapFile(name, chunks, header);
        if (!result.TryReadBodies(bytes, out error))
        {
            return false;
        }

        map = result;
        error = string.Empty;
        return true;
    }

    /// <summary>
    ///     The tiling walk: chunk after chunk, each ending where the next begins, the last ending
    ///     exactly at the end of the payload. ⚠ Every tag must be four printable characters — a
    ///     zero-filled region would otherwise never be caught, since its "length" is zero.
    /// </summary>
    private static bool TryWalkChunks(ReadOnlySpan<byte> bytes, string name, out List<VanBurenMapChunk> chunks,
        out string error)
    {
        chunks = [];
        var at = 0;
        while (at < bytes.Length)
        {
            if (at + ChunkHeaderLength > bytes.Length)
            {
                error = $"{name}: {bytes.Length - at} trailing bytes at {at} are too short for a chunk header.";
                return false;
            }

            var raw = bytes.Slice(at, 4);
            foreach (var c in raw)
            {
                if (c is < 0x20 or >= 0x7F)
                {
                    error = $"{name}: the bytes at {at} are not a four-character chunk tag.";
                    return false;
                }
            }

            var version = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(at + 4)..]);
            var length = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(at + 8)..]);
            if (length < ChunkHeaderLength || at + length > bytes.Length)
            {
                error =
                    $"{name}: chunk '{Encoding.ASCII.GetString(raw)}' at {at} declares {length} bytes, which does not fit.";
                return false;
            }

            chunks.Add(new VanBurenMapChunk(Encoding.ASCII.GetString(raw), version, at, (int)length));
            at += (int)length;
        }

        if (chunks.Count == 0 || chunks[0].Tag != Tag)
        {
            error = $"{name}: the first chunk is not '{Tag}'.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    /// <summary>
    ///     <c>GameMap::ReadHeader</c> (0x0056c9d0): a dword, four strings, then version-gated
    ///     fields — 3 bytes at version 1, a byte at 2, 4 bytes at 3, a byte at 4, and at 5 a byte,
    ///     3 bytes and three floats. The chunk must be consumed exactly.
    /// </summary>
    private static bool TryReadHeader(ReadOnlySpan<byte> bytes, VanBurenMapChunk chunk, string name,
        out VanBurenMapHeader header, out string error)
    {
        header = null!;
        var body = bytes.Slice(chunk.BodyOffset, chunk.BodyLength);
        var at = 0;
        if (!TryReadUInt32(body, ref at, out var leading)
            || !TryReadString(body, ref at, out var scene)
            || !TryReadString(body, ref at, out var grid)
            || !TryReadString(body, ref at, out var areaMap)
            || !TryReadString(body, ref at, out var fourth))
        {
            error = $"{name}: the header's four names run past the chunk.";
            return false;
        }

        var version = chunk.Version;
        var colourA = Array.Empty<byte>();
        byte flagA = 0;
        var colourB = Array.Empty<byte>();
        byte flagB = 0;
        byte flagC = 0;
        var colourC = Array.Empty<byte>();
        float fa = 0, fb = 0, fc = 0;

        var ok = true;
        if (version >= 1)
        {
            ok = TryReadBytes(body, ref at, 3, out colourA);
        }

        if (ok && version >= 2)
        {
            ok = TryReadByte(body, ref at, out flagA);
        }

        if (ok && version >= 3)
        {
            ok = TryReadBytes(body, ref at, 4, out colourB);
        }

        if (ok && version >= 4)
        {
            ok = TryReadByte(body, ref at, out flagB);
        }

        if (ok && version >= 5)
        {
            ok = TryReadByte(body, ref at, out flagC)
                 && TryReadBytes(body, ref at, 3, out colourC)
                 && TryReadSingle(body, ref at, out fa)
                 && TryReadSingle(body, ref at, out fb)
                 && TryReadSingle(body, ref at, out fc);
        }

        if (!ok)
        {
            error = $"{name}: the version-{version} header fields run past the chunk.";
            return false;
        }

        if (at != body.Length)
        {
            error = $"{name}: the version-{version} header leaves {body.Length - at} bytes unread.";
            return false;
        }

        header = new VanBurenMapHeader(version, leading, scene, grid, areaMap, fourth,
            colourA, flagA, colourB, flagB, flagC, colourC, fa, fb, fc);
        error = string.Empty;
        return true;
    }

    private bool TryReadBodies(ReadOnlySpan<byte> bytes, out string error)
    {
        var entities = new List<VanBurenMapEntity>();
        var effects = new List<VanBurenMapEffect>();
        var entryPoints = new List<VanBurenMapEntryPoint>();
        var triggers = new List<VanBurenMapTrigger>();
        var notes = new List<VanBurenMapNote>();
        var navPoints = new List<VanBurenMapNavPoint>();
        var paths = new List<VanBurenMapPath>();
        var sounds = new List<VanBurenMapSound>();
        var waters = new List<VanBurenMapWater>();
        var unclaimed = new List<string>();

        for (var i = 1; i < Chunks.Count; i++)
        {
            var chunk = Chunks[i];
            var body = bytes.Slice(chunk.BodyOffset, chunk.BodyLength);
            var at = 0;
            var ok = true;
            switch (chunk.Tag)
            {
                case "EME2":
                    ok = TryReadEntity(body, ref at, out var entity);
                    if (ok)
                    {
                        entities.Add(entity!);
                    }

                    break;

                case "EMEF":
                    ok = TryReadEffect(body, ref at, out var effect);
                    if (ok)
                    {
                        effects.Add(effect!);
                    }

                    break;

                case "EMEP":
                    ok = TryReadEntryPoints(body, ref at, entryPoints);
                    break;

                case "EMTR":
                    if (i + 1 >= Chunks.Count)
                    {
                        error = $"{Name}: the trigger at {chunk.Offset} has no record chunk after it.";
                        return false;
                    }

                    var detailChunk = Chunks[i + 1];
                    ok = TryReadTrigger(body, ref at, bytes.Slice(detailChunk.BodyOffset, detailChunk.BodyLength),
                        detailChunk, out var trigger, out var detailError);
                    if (!ok)
                    {
                        error = $"{Name}: {detailError}";
                        return false;
                    }

                    triggers.Add(trigger!);
#pragma warning disable S127 // An EMTR trigger consumes the following detail chunk as part of the same record.
                    i++;
#pragma warning restore S127
                    break;

                case "ECAM":
                {
                    var camera = new float[4];
                    for (var c = 0; c < 4 && ok; c++)
                    {
                        ok = TryReadSingle(body, ref at, out camera[c]);
                    }

                    if (ok)
                    {
                        CameraConstraints = camera;
                    }

                    break;
                }

                case "EMNO":
                {
                    var icon = string.Empty;
                    uint noteValue = 0;
                    ok = TryReadVector(body, ref at, out var notePosition)
                         && TryReadString(body, ref at, out icon)
                         && TryReadUInt32(body, ref at, out noteValue);
                    if (ok)
                    {
                        notes.Add(new VanBurenMapNote(notePosition, icon, noteValue));
                    }

                    break;
                }

                case "EMNP":
                    ok = TryReadNavPoints(body, ref at, navPoints);
                    break;

                case "EPTH":
                    ok = TryReadPath(body, ref at, out var path);
                    if (ok)
                    {
                        paths.Add(path!);
                    }

                    break;

                case "EMSD":
                {
                    VanBurenVector3 soundPosition = default;
                    var soundFile = string.Empty;
                    byte sa = 0;
                    byte sb = 0;
                    ok = TryReadString(body, ref at, out var soundName)
                         && TryReadVector(body, ref at, out soundPosition)
                         && TryReadString(body, ref at, out soundFile)
                         && TryReadByte(body, ref at, out sa)
                         && TryReadByte(body, ref at, out sb);
                    if (ok)
                    {
                        sounds.Add(new VanBurenMapSound(soundName, soundPosition, soundFile, sa, sb));
                    }

                    break;
                }

                case "2MWT":
                    // The water reader (0x00575a40) goes on for ~40 dwords of parameters and a
                    // texture list; only the name is claimed here, and the body is left whole.
                    ok = TryReadString(body, ref at, out var waterName);
                    if (ok)
                    {
                        waters.Add(new VanBurenMapWater(waterName, body.Length));
                        at = body.Length;
                    }

                    break;

                default:
                    unclaimed.Add(chunk.Tag);
                    at = body.Length;
                    break;
            }

            if (!ok)
            {
                error =
                    $"{Name}: the '{chunk.Tag}' chunk at {chunk.Offset} runs past its {chunk.BodyLength}-byte body.";
                return false;
            }

            // ⚑ THE GATE per chunk: the handler's reads must land exactly on the chunk end.
            if (at != body.Length)
            {
                error =
                    $"{Name}: the '{chunk.Tag}' chunk at {chunk.Offset} leaves {body.Length - at} of {body.Length} bytes unread.";
                return false;
            }
        }

        Entities = entities;
        Effects = effects;
        EntryPoints = entryPoints;
        Triggers = triggers;
        Notes = notes;
        NavPoints = navPoints;
        Paths = paths;
        Sounds = sounds;
        Waters = waters;
        UnclaimedTags = unclaimed;
        error = string.Empty;
        return true;
    }

    /// <summary>
    ///     <c>GameMap::ReadEntity</c> (0x00573f30): name, position, rotation (radians), a byte,
    ///     then a nested chunk read with <c>Chunk::Read</c> and handed to the entity's
    ///     override reader — always <c>EEOV</c> on the shipped maps, opening with the instance name.
    /// </summary>
    private static bool TryReadEntity(ReadOnlySpan<byte> body, ref int at, out VanBurenMapEntity? entity)
    {
        entity = null;
        if (!TryReadString(body, ref at, out var template)
            || !TryReadVector(body, ref at, out var position)
            || !TryReadVector(body, ref at, out var rotation)
            || !TryReadByte(body, ref at, out var flag)
            || at + ChunkHeaderLength > body.Length)
        {
            return false;
        }

        var nestedTag = Encoding.ASCII.GetString(body.Slice(at, 4));
        var nestedVersion = BinaryPrimitives.ReadUInt32LittleEndian(body[(at + 4)..]);
        var nestedLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(body[(at + 8)..]);
        if (nestedTag != "EEOV" || nestedLength < ChunkHeaderLength || at + nestedLength > body.Length)
        {
            return false;
        }

        var inner = at + ChunkHeaderLength;
        if (!TryReadString(body[..(at + nestedLength)], ref inner, out var instanceName))
        {
            return false;
        }

        at += nestedLength;
        entity = new VanBurenMapEntity(template, position, rotation, flag, instanceName, nestedVersion, nestedLength);
        return true;
    }

    /// <summary>
    ///     <c>GameMap::ReadMapEffect</c> (0x00578fb0): name, six dwords (position and rotation),
    ///     a count byte, that many effect names, and a trailing byte.
    /// </summary>
    private static bool TryReadEffect(ReadOnlySpan<byte> body, ref int at, out VanBurenMapEffect? effect)
    {
        effect = null;
        if (!TryReadString(body, ref at, out var name)
            || !TryReadVector(body, ref at, out var position)
            || !TryReadVector(body, ref at, out var rotation)
            || !TryReadByte(body, ref at, out var count))
        {
            return false;
        }

        var effects = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            if (!TryReadString(body, ref at, out var effectName))
            {
                return false;
            }

            effects.Add(effectName);
        }

        if (!TryReadByte(body, ref at, out var flag))
        {
            return false;
        }

        effect = new VanBurenMapEffect(name, position, rotation, effects, flag);
        return true;
    }

    /// <summary>
    ///     <c>GameMap::ReadEntryPoints</c> (0x00573b00): a byte, then SIX positions followed by
    ///     SIX dwords. ⚠ One shipped map (Test_Mesa_2) carries a 17-byte body — a byte, one
    ///     position and one dword — which the game's fixed six-slot reader would refuse; it is
    ///     accepted here as N slots where N is what the body holds, so the data is not lost.
    /// </summary>
    private static bool TryReadEntryPoints(ReadOnlySpan<byte> body, ref int at, List<VanBurenMapEntryPoint> into)
    {
        if (!TryReadByte(body, ref at, out _))
        {
            return false;
        }

        var remaining = body.Length - at;
        if (remaining % 16 != 0)
        {
            return false;
        }

        var slots = remaining / 16;
        var positions = new VanBurenVector3[slots];
        for (var i = 0; i < slots; i++)
        {
            if (!TryReadVector(body, ref at, out positions[i]))
            {
                return false;
            }
        }

        for (var i = 0; i < slots; i++)
        {
            if (!TryReadUInt32(body, ref at, out var value))
            {
                return false;
            }

            into.Add(new VanBurenMapEntryPoint(positions[i], value));
        }

        return true;
    }

    /// <summary>
    ///     <c>GameMap::ReadTrigger</c> (0x00575050): a kind dword, a point count, the points; the
    ///     trigger object then reads its own chunk — <c>EBTR</c> (0x005b1c70: dword + 3 bytes),
    ///     <c>ESTR</c> (0x005b27xx: script name, count, name/dword pairs) or <c>ETTR</c>
    ///     (0x005b1980: destination map, a byte, and at version 1 a bool).
    /// </summary>
    private static bool TryReadTrigger(ReadOnlySpan<byte> body, ref int at, ReadOnlySpan<byte> detailBody,
        VanBurenMapChunk detailChunk, out VanBurenMapTrigger? trigger, out string error)
    {
        trigger = null;
        if (!TryReadUInt32(body, ref at, out var kind) || !TryReadUInt32(body, ref at, out var count))
        {
            error = "a trigger body is shorter than its kind and count.";
            return false;
        }

        var points = new List<VanBurenVector3>((int)Math.Min(count, 4096));
        for (var i = 0; i < count; i++)
        {
            if (!TryReadVector(body, ref at, out var point))
            {
                error = "a trigger declares more points than its body holds.";
                return false;
            }

            points.Add(point);
        }

        var detailAt = 0;
        VanBurenMapTriggerDetail detail;
        switch (detailChunk.Tag)
        {
            case "EBTR":
            {
                if (!TryReadUInt32(detailBody, ref detailAt, out var value) ||
                    !TryReadBytes(detailBody, ref detailAt, 3, out var colour))
                {
                    error = $"the EBTR record at {detailChunk.Offset} is shorter than a dword and three bytes.";
                    return false;
                }

                detail = new VanBurenMapTriggerDetail("EBTR", detailChunk.Version, null, value, colour, null, []);
                break;
            }

            case "ESTR":
            {
                if (!TryReadString(detailBody, ref detailAt, out var script) ||
                    !TryReadUInt32(detailBody, ref detailAt, out var variableCount))
                {
                    error = $"the ESTR record at {detailChunk.Offset} is shorter than a script name and count.";
                    return false;
                }

                var variables = new List<(string, uint)>();
                for (var i = 0; i < variableCount; i++)
                {
                    if (!TryReadString(detailBody, ref detailAt, out var variable) ||
                        !TryReadUInt32(detailBody, ref detailAt, out var value))
                    {
                        error = $"the ESTR record at {detailChunk.Offset} declares more variables than it holds.";
                        return false;
                    }

                    variables.Add((variable, value));
                }

                detail = new VanBurenMapTriggerDetail("ESTR", detailChunk.Version, script, variableCount, null, null,
                    variables);
                break;
            }

            case "ETTR":
            {
                if (!TryReadString(detailBody, ref detailAt, out var destination) ||
                    !TryReadByte(detailBody, ref detailAt, out var value))
                {
                    error = $"the ETTR record at {detailChunk.Offset} is shorter than a destination and a byte.";
                    return false;
                }

                bool? flag = null;
                if (detailChunk.Version >= 1)
                {
                    if (!TryReadByte(detailBody, ref detailAt, out var flagByte))
                    {
                        error =
                            $"the version-{detailChunk.Version} ETTR record at {detailChunk.Offset} lacks its flag byte.";
                        return false;
                    }

                    flag = flagByte != 0;
                }

                detail = new VanBurenMapTriggerDetail("ETTR", detailChunk.Version, destination, value, null, flag, []);
                break;
            }

            default:
                error =
                    $"the trigger is followed at {detailChunk.Offset} by '{detailChunk.Tag}', not a trigger record.";
                return false;
        }

        if (detailAt != detailBody.Length)
        {
            error =
                $"the {detailChunk.Tag} record at {detailChunk.Offset} leaves {detailBody.Length - detailAt} bytes unread.";
            return false;
        }

        trigger = new VanBurenMapTrigger(kind, points, detail);
        error = string.Empty;
        return true;
    }

    /// <summary>
    ///     <c>GameMap::ReadNavPoints</c> (0x00575180): a count, then per node a byte, two triples
    ///     and five signed link bytes (the game nulls any outside 0..4).
    /// </summary>
    private static bool TryReadNavPoints(ReadOnlySpan<byte> body, ref int at, List<VanBurenMapNavPoint> into)
    {
        if (!TryReadUInt32(body, ref at, out var count))
        {
            return false;
        }

        for (var i = 0; i < count; i++)
        {
            if (!TryReadByte(body, ref at, out var flag)
                || !TryReadVector(body, ref at, out var position)
                || !TryReadVector(body, ref at, out var second)
                || !TryReadBytes(body, ref at, 5, out var linkBytes))
            {
                return false;
            }

            var links = new int[5];
            for (var l = 0; l < 5; l++)
            {
                links[l] = (sbyte)linkBytes[l];
            }

            into.Add(new VanBurenMapNavPoint(flag, position, second, links));
        }

        return true;
    }

    /// <summary>
    ///     <c>GameMap::ReadWayPoints</c> (0x00576940): a path name, a count, then per node two
    ///     triples; the game links the nodes into a ring afterwards.
    /// </summary>
    private static bool TryReadPath(ReadOnlySpan<byte> body, ref int at, out VanBurenMapPath? path)
    {
        path = null;
        if (!TryReadString(body, ref at, out var name) || !TryReadUInt32(body, ref at, out var count))
        {
            return false;
        }

        var points = new List<VanBurenMapWayPoint>((int)Math.Min(count, 4096));
        for (var i = 0; i < count; i++)
        {
            if (!TryReadVector(body, ref at, out var position) || !TryReadVector(body, ref at, out var orientation))
            {
                return false;
            }

            points.Add(new VanBurenMapWayPoint(position, orientation));
        }

        path = new VanBurenMapPath(name, points);
        return true;
    }

    /// <summary>Reads a u16 length then that many ASCII bytes — the game's <c>ReadString</c> (0x0049c8d0).</summary>
    internal static bool TryReadString(ReadOnlySpan<byte> bytes, ref int at, out string value)
    {
        value = string.Empty;
        if (at + 2 > bytes.Length)
        {
            return false;
        }

        var length = BinaryPrimitives.ReadUInt16LittleEndian(bytes[at..]);
        if (at + 2 + length > bytes.Length)
        {
            return false;
        }

        value = Encoding.ASCII.GetString(bytes.Slice(at + 2, length));
        at += 2 + length;
        return true;
    }

    private static bool TryReadVector(ReadOnlySpan<byte> bytes, ref int at, out VanBurenVector3 value)
    {
        value = default;
        if (!TryReadSingle(bytes, ref at, out var x) || !TryReadSingle(bytes, ref at, out var y) ||
            !TryReadSingle(bytes, ref at, out var z))
        {
            return false;
        }

        value = new VanBurenVector3(x, y, z);
        return true;
    }

    private static bool TryReadSingle(ReadOnlySpan<byte> bytes, ref int at, out float value)
    {
        value = 0;
        if (at + 4 > bytes.Length)
        {
            return false;
        }

        value = BinaryPrimitives.ReadSingleLittleEndian(bytes[at..]);
        at += 4;
        return true;
    }

    private static bool TryReadUInt32(ReadOnlySpan<byte> bytes, ref int at, out uint value)
    {
        value = 0;
        if (at + 4 > bytes.Length)
        {
            return false;
        }

        value = BinaryPrimitives.ReadUInt32LittleEndian(bytes[at..]);
        at += 4;
        return true;
    }

    private static bool TryReadByte(ReadOnlySpan<byte> bytes, ref int at, out byte value)
    {
        value = 0;
        if (at + 1 > bytes.Length)
        {
            return false;
        }

        value = bytes[at];
        at += 1;
        return true;
    }

    private static bool TryReadBytes(ReadOnlySpan<byte> bytes, ref int at, int count, out byte[] value)
    {
        value = [];
        if (at + count > bytes.Length)
        {
            return false;
        }

        value = bytes.Slice(at, count).ToArray();
        at += count;
        return true;
    }
}
