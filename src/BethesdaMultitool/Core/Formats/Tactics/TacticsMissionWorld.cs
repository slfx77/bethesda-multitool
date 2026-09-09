using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace BethesdaMultitool.Core.Formats.Tactics;

/// <summary>
///     One placed tile of a mission — the 56-byte record <c>FUN_00704630</c> reads for a
///     <c>&lt;region&gt;</c> v8 and <c>FUN_00704460</c> copies into the live object, field for field.
///     All positions are in TILE UNITS (a floor tile is 6 x 1 x 6 of them; see
///     <see cref="TacticsIsometricProjection" /> for what a unit is on screen).
/// </summary>
/// <param name="RegionIndex">The <c>&lt;region&gt;</c> the record sits in; see <see cref="TacticsMissionWorld.RegionIndexOf" />.</param>
/// <param name="TileIndex">1-based index into <see cref="TacticsMissionWorld.TilePaths" />; 0 is never stored.</param>
/// <param name="Flags">The u16 at +2 (v8 reads it whole). Meaning open; 0 on 47% of retail instances, then 512/1024/768/1536.</param>
/// <param name="X">Position (<c>this+4</c>).</param>
/// <param name="Y">Height (<c>this+8</c>); 127 on 53% of retail instances, 128 on 11%.</param>
/// <param name="Z">Position (<c>this+0xC</c>).</param>
/// <param name="X2">Far corner (<c>this+0x10</c>) — equal to X + the tile header's bounding box on 2,733,005/2,733,005 retail instances.</param>
/// <param name="Y2">Far corner (<c>this+0x14</c>).</param>
/// <param name="Z2">Far corner (<c>this+0x18</c>).</param>
/// <param name="RectLeft">Image rect relative to the projected origin (<c>this+0x1C</c>) — equal to -foot_position on 2,732,997 retail instances.</param>
/// <param name="RectTop">Image rect top (<c>this+0x20</c>).</param>
/// <param name="RectRight">Image rect right (<c>this+0x24</c>).</param>
/// <param name="RectBottom">Image rect bottom (<c>this+0x28</c>).</param>
/// <param name="Unknown2C">u16 at +0x2C (read on v7+; 0 on 82% of retail).</param>
/// <param name="Unknown2E">u16 at +0x2E.</param>
/// <param name="Unknown30">u16 at +0x30 (read on v4+; 0 on 79%).</param>
/// <param name="Unknown32">u16 at +0x32 (read on v6+; 0 on 99.8%).</param>
/// <param name="InstanceId">u32 at +0x34 (read on v5+); unique per instance within a mission on the retail data.</param>
internal readonly record struct TacticsTileInstance(
    int RegionIndex,
    ushort TileIndex,
    ushort Flags,
    int X,
    int Y,
    int Z,
    int X2,
    int Y2,
    int Z2,
    int RectLeft,
    int RectTop,
    int RectRight,
    int RectBottom,
    ushort Unknown2C,
    ushort Unknown2E,
    ushort Unknown30,
    ushort Unknown32,
    uint InstanceId)
{
    /// <summary>The record's stride in a <c>&lt;region&gt;</c> v8.</summary>
    public const int RecordLength = 56;

    /// <summary>The image's width from its stored rect.</summary>
    public int ImageWidth => RectRight - RectLeft;

    /// <summary>The image's height from its stored rect.</summary>
    public int ImageHeight => RectBottom - RectTop;
}

/// <summary>
///     One entity of a mission's <c>&lt;entity_file&gt;</c>: the slot it occupies, the two u16s the
///     slot table carries before its class index (<c>FUN_004e52d0</c> reads them into the table
///     entry at +4/+6), its class name and its self-describing <c>&lt;esh&gt;</c> bag.
/// </summary>
internal sealed class TacticsWorldEntity
{
    /// <summary>The property carrying a placed entity's transform.</summary>
    public const string FramePropertyName = "frame";

    /// <summary>
    ///     Entity positions are stored in QUARTER tile units — the world's <c>DAT_008be084</c>
    ///     is 1/4 (<c>0x6e6880</c>: 1.0 / 4.0) and a placed actor's <c>frame</c> y reads 32 where the
    ///     floor tile under it sits at 128. Measured: 3,521 of 3,543 retail actors stand on a floor
    ///     tile once multiplied out (and 2,157 with the horizontal axes swapped, which is what makes
    ///     the mapping a measurement rather than a choice).
    /// </summary>
    public const float UnitsPerFrameUnit = 4f;

    /// <summary>Builds one entity.</summary>
    public TacticsWorldEntity(int slot, ushort slotA, ushort slotB, string className, IReadOnlyList<TacticsProperty> properties)
    {
        Slot = slot;
        SlotA = slotA;
        SlotB = slotB;
        ClassName = className;
        Properties = properties;
    }

    /// <summary>1-based slot in the entity table (slot 0 is skipped by the game's loop).</summary>
    public int Slot { get; }

    /// <summary>The u16 stored at the slot's +4. 0 on every retail entity measured.</summary>
    public ushort SlotA { get; }

    /// <summary>The u16 stored at the slot's +6. Meaning open.</summary>
    public ushort SlotB { get; }

    /// <summary>The class name from the file's own class table, e.g. <c>Actor</c>, <c>Light</c>, <c>Weapon</c>.</summary>
    public string ClassName { get; }

    /// <summary>The self-describing property bag — <see cref="TacticsPropertyBag" />'s shape.</summary>
    public IReadOnlyList<TacticsProperty> Properties { get; }

    /// <summary>
    ///     Whether the entity is PLACED in the world (carries a <c>frame</c>) as opposed to sitting
    ///     in another entity's inventory (weapons, ammo, armour) or being a controller/AI record.
    /// </summary>
    public bool IsPlaced => TryGetFrame(out _);

    /// <summary>The <c>Display Name</c> property, or null.</summary>
    public string? DisplayName => Text("Display Name");

    /// <summary>The <c>Sprite</c> property (a <c>sprites/...</c> path), or null.</summary>
    public string? SpritePath => Text("Sprite");

    /// <summary>
    ///     The entity's position in TILE UNITS (the <c>frame</c> translation times
    ///     <see cref="UnitsPerFrameUnit" />), or null when the entity is not placed.
    /// </summary>
    public Vector3? Position
    {
        get
        {
            if (!TryGetFrame(out var frame))
            {
                return null;
            }

            return new Vector3(frame.M41, frame.M42, frame.M43) * UnitsPerFrameUnit;
        }
    }

    /// <summary>
    ///     The 48-byte <c>frame</c> property as twelve floats: a 3x3 rotation (rows) followed by
    ///     the translation, in the file's own quarter units. The rotation is the identity or a
    ///     quarter turn about y on every retail entity measured.
    /// </summary>
    public bool TryGetFrame(out Matrix4x4 frame)
    {
        frame = default;
        foreach (var property in Properties)
        {
            if (!string.Equals(property.Name, FramePropertyName, StringComparison.Ordinal) || property.Value.Length != 48)
            {
                continue;
            }

            var span = property.Value.Span;
            var f = new float[12];
            for (var i = 0; i < 12; i++)
            {
                f[i] = BinaryPrimitives.ReadSingleLittleEndian(span[(i * 4)..]);
            }

            frame = new Matrix4x4(
                f[0], f[1], f[2], 0,
                f[3], f[4], f[5], 0,
                f[6], f[7], f[8], 0,
                f[9], f[10], f[11], 1);
            return true;
        }

        return false;
    }

    private string? Text(string name)
    {
        foreach (var property in Properties)
        {
            if (string.Equals(property.Name, name, StringComparison.Ordinal))
            {
                return TacticsCursor.PropertyText(property);
            }
        }

        return null;
    }
}

/// <summary>A <c>&lt;world_zone&gt;</c> v2: a named box in the entity's quarter units (<c>FUN_004fff80</c>).</summary>
/// <param name="Name">The zone's name — <c>Guard_House</c>, <c>outdoors</c>, <c>Start Point</c>.</param>
/// <param name="Colour">The three floats before the name; (1, 1, 0) on interiors and a hue on the outdoors/roof zones.</param>
/// <param name="Minimum">The box's low corner (the reader swaps each axis so min &lt;= max).</param>
/// <param name="Maximum">The box's high corner.</param>
/// <param name="Flag">The trailing byte a v2 zone carries. 1 on 405 and 0 on 283 retail zones.</param>
internal readonly record struct TacticsWorldZone(string Name, Vector3 Colour, Vector3 Minimum, Vector3 Maximum, byte Flag);

/// <summary>A <c>&lt;Trigger&gt;</c> v4: its name and the class names of its conditions and actions (<c>FUN_00679830</c>).</summary>
internal sealed record TacticsWorldTrigger(string Name, IReadOnlyList<string> Conditions, IReadOnlyList<string> Actions);

/// <summary>
///     The inflated interior of a Fallout Tactics mission — everything after the container
///     <see cref="TacticsMissionFile" /> already checks. Original RE 2026-09-08 off BOS.exe's own
///     loader; every Tactics reference is GPL, so nothing is ported.
///     <para>
///         ⚑ <b>128 of 128 shipped missions walk to their last byte</b> (103 archived + 25 loose,
///         counted once), every section landing on the next section's tag, with
///         <see cref="TacticsCursor.RequireEnd" /> as the final check. The save's own world
///         (version 70) has no <c>&lt;mph&gt;</c> and no grid and is NOT a mission world; it is
///         refused here rather than half-read.
///     </para>
///     <para>
///         The world reader is <c>FUN_004e8900</c> (version &gt;= 50 path — retail ships 68 and 69).
///         Sections in file order, each with the function that reads it:
///         <list type="number">
///             <item>
///                 <c>&lt;mph&gt;</c> v8 (<c>FUN_004fdac0</c>): u32-counted team names, team ids, the
///                 player squad, three u32 arrays, two u32, the speech file, three strings, an
///                 optional <c>&lt;zar&gt;</c> minimap, three bytes.
///             </item>
///             <item>
///                 <c>&lt;mapManager&gt;</c> v50 (<c>FUN_006ed930</c>): nine floats (two RGB colours
///                 and a light direction), six extent dwords, a u32-counted TILE TABLE of paths
///                 whose entry 0 is the empty "no tile", then one v10 <c>&lt;tile&gt;</c> header per
///                 path from 1 (<see cref="TacticsTileFile.ReadHeader" /> — the record the backlog
///                 once called "four unexplained u32s").
///             </item>
///             <item>
///                 The instance total, six bounds dwords and the region count, then one
///                 <c>&lt;region&gt;</c> v8 per region (<c>FUN_00740350</c>): a u32 count of 56-byte
///                 <see cref="TacticsTileInstance" /> records. ⚑ The region count is DERIVED from
///                 the bounds exactly as <c>FUN_006e7510</c> derives it — <c>(x0 = b0 &gt;&gt; 8,
///                 nx = (b3 + 255) &gt;&gt; 8 - x0)</c>, same in z — and a region is 256 units on
///                 a side; the reader refuses a count that disagrees. ⚑ Which region an instance is
///                 in is a pure function of its position: <see cref="RegionIndexOf" /> holds on
///                 2,733,005 of 2,733,005 retail instances.
///             </item>
///             <item>
///                 Two tile-group lists (<c>FUN_006d7a70</c>, version &gt;= 17): each entry a name
///                 and two u32-counted u32 arrays; the second list's entry 0 is implicit.
///             </item>
///             <item>
///                 <c>&lt;entity_file&gt;</c> v3 (<c>FUN_004e52d0</c>): u32-counted class names,
///                 then u16 slot capacity, u16 used count, u16 unknown, then for slots
///                 1..capacity-1: u16, u16, u16 class index — 0xFFFF for an empty slot, else an
///                 <c>&lt;esh&gt;</c> bag (<c>FUN_005e8ed0</c> → <c>FUN_005f33b0</c>). The used count
///                 must equal the bags read.
///             </item>
///             <item>
///                 u32-counted <c>&lt;world_zone&gt;</c> v2 (<c>FUN_004fff80</c>): 3 floats, name,
///                 6 floats, and — on version &gt; 1 — a byte. ⚠⚠ <b>Ghidra DROPPED that byte</b>
///                 ("Removing unreachable block (ram,0x0050008e)"); the retail record lengths
///                 (67/69/67/59/64/59 on mission01's seven zones) and the disassembly at
///                 <c>0x500087</c> (<c>cmp [esp+0x14], 1; jle</c>; <c>call 0x6f1cd0</c>) both carry it.
///                 The same warning hides the v&gt;1 u32 of <c>&lt;Team&gt;</c> (<c>0x4dddb4</c>),
///                 the v&gt;1 u32 of <c>&lt;mnob&gt;</c> (<c>0x4fe3c2</c>) and the third string per
///                 row of a v2 <c>&lt;speech_node&gt;</c> (<c>0x4ff1a4</c>). ⛔ A version-gated read
///                 this dump calls unreachable is a read; check the disassembly.
///             </item>
///             <item>
///                 u32-counted <c>&lt;Player&gt;</c> v8 (<c>FUN_004dee80</c>) with five
///                 <c>&lt;Group Header&gt;</c> — a SPACE, not an underscore — and a
///                 <c>&lt;PInfoH&gt;</c> each; nine <c>&lt;Team&gt;</c> v2 (<c>FUN_004ddcb0</c>);
///                 the bracketless <c>timerTableHeader</c> v2 (<c>FUN_00501430</c>) and
///                 <c>varTableHeader</c> (<c>FUN_005008b0</c>); 81 raw bytes; a
///                 <c>&lt;speechList&gt;</c> (or <c>&lt;speachList&gt;</c>, both spellings are
///                 accepted by <c>FUN_004fb880</c>); 81 more raw bytes; u32-counted
///                 <c>&lt;mnob&gt;</c> objectives; <c>&lt;TriggerManager&gt;</c> with
///                 <c>&lt;Trigger&gt;</c> v4 whose conditions and actions are each a class name
///                 plus an <c>&lt;esh&gt;</c> bag (<c>FUN_00678a80</c>); a byte and a u32;
///                 u32-counted <c>&lt;ambientSound&gt;</c> (<c>FUN_004dd0b0</c>); and a
///                 version-gated tail.
///             </item>
///         </list>
///     </para>
/// </summary>
internal sealed class TacticsMissionWorld
{
    /// <summary>The only <c>&lt;mapManager&gt;</c> version shipped (128/128).</summary>
    public const string MapManagerVersion = "50";

    /// <summary>The only <c>&lt;region&gt;</c> version shipped (128/128; the reader below 8 is a different record).</summary>
    public const string RegionVersion = "8";

    /// <summary>The only <c>&lt;entity_file&gt;</c> version shipped in a mission (the &gt; 3 path is a different walk).</summary>
    public const string EntityFileVersion = "3";

    /// <summary>A region is 256 tile units on a side (<c>FUN_006e7510</c>: <c>&gt;&gt; 8</c>).</summary>
    public const int RegionSize = 256;

    /// <summary>The raw block between the variable table and the speech list, and again after it.</summary>
    public const int AlignmentBlockLength = 0x51;

    /// <summary>Retail worlds carry exactly nine <c>&lt;Team&gt;</c> chunks after the players.</summary>
    public const int WorldTeamCount = 9;

    private const int MaximumCount = 1 << 20;

    private TacticsMissionWorld(string name)
    {
        Name = name;
    }

    /// <summary>Source name, for messages.</summary>
    public string Name { get; }

    /// <summary>The container's two-digit version (68 or 69 on retail), which gates the tail.</summary>
    public int ContainerVersion { get; private set; }

    /// <summary>The <c>&lt;mph&gt;</c> version — 8 on 128/128.</summary>
    public int HeaderVersion { get; private set; }

    /// <summary>Team names from the <c>&lt;mph&gt;</c> head, e.g. <c>BOS</c>, <c>Tribals (T)</c>.</summary>
    public IReadOnlyList<string> Teams { get; private set; } = [];

    /// <summary>The parallel team ids — real data, 1..n on 99 of 103 archived missions only.</summary>
    public IReadOnlyList<uint> TeamIds { get; private set; } = [];

    /// <summary>The player squad names (one on every retail mission).</summary>
    public IReadOnlyList<string> Squads { get; private set; } = [];

    /// <summary>The speech file the head names (<c>locale/...</c>), or empty.</summary>
    public string SpeechFile { get; private set; } = string.Empty;

    /// <summary>The three strings after the speech file.</summary>
    public IReadOnlyList<string> HeaderStrings { get; private set; } = [];

    /// <summary>Whether the head carries a minimap <c>&lt;zar&gt;</c>.</summary>
    public bool HasMinimap { get; private set; }

    /// <summary>The nine floats at the head of the map manager: two RGB triples and a unit light direction.</summary>
    public IReadOnlyList<float> Lighting { get; private set; } = [];

    /// <summary>The six extent dwords <c>(w, 128, w', h, 128, h')</c> — camera bounds in tile units, 128 on dwords 1 and 4 (128/128).</summary>
    public IReadOnlyList<uint> Extents { get; private set; } = [];

    /// <summary>The tile table's paths, <c>tiles/...</c> relative to the <c>core</c> mount; entry 0 is empty ("no tile").</summary>
    public IReadOnlyList<string> TilePaths { get; private set; } = [];

    /// <summary>The tile table's v10 headers, parallel to <see cref="TilePaths" />; entry 0 is the default (no header is stored for it).</summary>
    public IReadOnlyList<TacticsTileHeader> TileHeaders { get; private set; } = [];

    /// <summary>The six bounds dwords before the regions: (minX, minY, minZ, maxX, maxY, maxZ) in tile units.</summary>
    public IReadOnlyList<uint> Bounds { get; private set; } = [];

    /// <summary>First region column, from bounds[0] &gt;&gt; 8.</summary>
    public int RegionOriginX { get; private set; }

    /// <summary>First region row, from bounds[2] &gt;&gt; 8.</summary>
    public int RegionOriginZ { get; private set; }

    /// <summary>Regions along x — 10 on 109 of 128 retail missions, 4 on mission01.</summary>
    public int RegionsX { get; private set; }

    /// <summary>Regions along z.</summary>
    public int RegionsZ { get; private set; }

    /// <summary>Every placed tile, in file order (region by region).</summary>
    public IReadOnlyList<TacticsTileInstance> Instances { get; private set; } = [];

    /// <summary>The two tile-group lists' names.</summary>
    public IReadOnlyList<string> TileGroupNames { get; private set; } = [];

    /// <summary>The entity class table.</summary>
    public IReadOnlyList<string> EntityClasses { get; private set; } = [];

    /// <summary>The slot capacity (2000 on retail).</summary>
    public int EntitySlotCapacity { get; private set; }

    /// <summary>The third u16 of the entity header; meaning open (207 on mission01).</summary>
    public ushort EntityHeaderUnknown { get; private set; }

    /// <summary>Every entity, in slot order.</summary>
    public IReadOnlyList<TacticsWorldEntity> Entities { get; private set; } = [];

    /// <summary>The named zones.</summary>
    public IReadOnlyList<TacticsWorldZone> Zones { get; private set; } = [];

    /// <summary>The <c>&lt;Player&gt;</c> names.</summary>
    public IReadOnlyList<string> Players { get; private set; } = [];

    /// <summary>The nine <c>&lt;Team&gt;</c> chunks (name, flag, tail) — the roster <see cref="TacticsMissionFile.ScanTeams" /> finds by scanning.</summary>
    public IReadOnlyList<TacticsWorldTeam> WorldTeams { get; private set; } = [];

    /// <summary>Timer names from the timer table.</summary>
    public IReadOnlyList<string> TimerNames { get; private set; } = [];

    /// <summary>Variable names from the variable table.</summary>
    public IReadOnlyList<string> VariableNames { get; private set; } = [];

    /// <summary>Variable values, parallel to <see cref="VariableNames" /> when the counts agree.</summary>
    public IReadOnlyList<string> VariableValues { get; private set; } = [];

    /// <summary>Speech nodes in the speech list.</summary>
    public int SpeechNodeCount { get; private set; }

    /// <summary>Minimap objective (<c>&lt;mnob&gt;</c>) names.</summary>
    public IReadOnlyList<string> Objectives { get; private set; } = [];

    /// <summary>The triggers.</summary>
    public IReadOnlyList<TacticsWorldTrigger> Triggers { get; private set; } = [];

    /// <summary>Ambient sound names.</summary>
    public IReadOnlyList<string> AmbientSounds { get; private set; } = [];

    /// <summary>The entities that carry a <c>frame</c>, i.e. sit in the world.</summary>
    public IEnumerable<TacticsWorldEntity> PlacedEntities => Entities.Where(e => e.IsPlaced);

    /// <summary>Parses the inflated world of a mission, throwing when it does not walk exactly.</summary>
    public static TacticsMissionWorld Parse(TacticsMissionFile mission)
    {
        ArgumentNullException.ThrowIfNull(mission);
        if (!TryParse(mission, out var world, out var error))
        {
            throw new InvalidDataException(error);
        }

        return world;
    }

    /// <summary>Parses the inflated world of a mission, reporting why rather than throwing.</summary>
    public static bool TryParse(TacticsMissionFile mission, out TacticsMissionWorld world, out string error)
    {
        ArgumentNullException.ThrowIfNull(mission);
        return TryParse(mission.World, mission.Name, mission.Version, out world, out error);
    }

    /// <summary>Parses an inflated world payload directly.</summary>
    public static bool TryParse(ReadOnlyMemory<byte> payload, string name, string containerVersion,
        out TacticsMissionWorld world, out string error)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(containerVersion);

        world = null!;
        if (!int.TryParse(containerVersion, NumberStyles.None, CultureInfo.InvariantCulture, out var version))
        {
            error = $"{name}: container version '{containerVersion}' is not a number.";
            return false;
        }

        if (!TacticsTagChunk.Is(payload.Span, "mph"))
        {
            error = $"{name}: the world does not open with <mph> (a save's world, version 70, has no grid).";
            return false;
        }

        try
        {
            var result = new TacticsMissionWorld(name) { ContainerVersion = version };
            result.Walk(new TacticsCursor(payload, name));
            world = result;
            error = string.Empty;
            return true;
        }
        catch (InvalidDataException e)
        {
            error = e.Message;
            return false;
        }
    }

    /// <summary>
    ///     The region an instance at (<paramref name="x" />, <paramref name="z" />) belongs to:
    ///     x-major over the region grid, exactly as the game files it (2,733,005/2,733,005).
    /// </summary>
    public int RegionIndexOf(int x, int z)
    {
        return (x >> 8) - RegionOriginX + ((z >> 8) - RegionOriginZ) * RegionsX;
    }

    private void Walk(TacticsCursor r)
    {
        ReadHead(r);
        ReadMapManager(r);
        ReadRegions(r);
        ReadTileGroups(r);
        ReadEntities(r);
        ReadZones(r);
        ReadPlayers(r);
        ReadWorldTeams(r);
        ReadTimerTable(r);
        ReadVariableTable(r);
        r.Bytes(AlignmentBlockLength, "alignment block");
        ReadSpeechList(r);
        if (ContainerVersion < 0x3a)
        {
            r.String();
        }

        r.Bytes(AlignmentBlockLength, "second alignment block");
        ReadObjectives(r);
        if (ContainerVersion < 0x34)
        {
            r.U8();
        }

        ReadTriggers(r);
        r.U8();
        r.U32();
        if (ContainerVersion is > 0x36 and < 0x39)
        {
            r.U32();
        }

        ReadAmbientSounds(r);
        ReadTail(r);
        r.RequireEnd("the mission world");
    }

    /// <summary><c>FUN_004fdac0</c>.</summary>
    private void ReadHead(TacticsCursor r)
    {
        var chunk = r.Tag("mph");
        HeaderVersion = ParseVersion(r, chunk);
        Teams = Strings(r, "team");
        TeamIds = U32s(r, "team id");
        Squads = Strings(r, "squad");
        U32s(r, "head array 3");
        U32s(r, "head array 4");
        U32s(r, "head array 5");
        if (HeaderVersion > 1)
        {
            r.U32();
        }

        if (HeaderVersion > 2)
        {
            r.U32();
        }

        if (HeaderVersion > 3)
        {
            SpeechFile = r.String();
        }

        if (HeaderVersion > 4)
        {
            HeaderStrings = [r.String(), r.String(), r.String()];
        }

        if (HeaderVersion > 5)
        {
            HasMinimap = r.U8() != 0;
            if (HasMinimap)
            {
                TacticsZarImage.Read(r);
            }
        }

        if (HeaderVersion > 6)
        {
            r.U8();
            r.U8();
        }

        if (HeaderVersion > 7)
        {
            r.U8();
        }
    }

    /// <summary><c>FUN_006ed930</c>, version 50 branch.</summary>
    private void ReadMapManager(TacticsCursor r)
    {
        var at = r.Position;
        var chunk = r.Tag("mapManager");
        if (!string.Equals(chunk.Version, MapManagerVersion, StringComparison.Ordinal))
        {
            throw r.Fail(at, $"<mapManager> version '{chunk.Version}' is not {MapManagerVersion}");
        }

        var lighting = new float[9];
        for (var i = 0; i < lighting.Length; i++)
        {
            lighting[i] = r.F32();
        }

        Lighting = lighting;
        var extents = new uint[6];
        for (var i = 0; i < extents.Length; i++)
        {
            extents[i] = r.U32();
        }

        Extents = extents;
        var paths = Strings(r, "tile path");
        if (paths.Count == 0)
        {
            throw r.Fail(at, "the tile table is empty (entry 0 is always the empty 'no tile')");
        }

        var headers = new TacticsTileHeader[paths.Count];
        for (var i = 1; i < paths.Count; i++)
        {
            headers[i] = TacticsTileFile.ReadHeader(r);
        }

        TilePaths = paths;
        TileHeaders = headers;
    }

    /// <summary>The instance total, bounds, region count (<c>FUN_006e7510</c>) and the regions (<c>FUN_00740350</c>).</summary>
    private void ReadRegions(TacticsCursor r)
    {
        var at = r.Position;
        var total = r.Count(MaximumCount * 8, "tile instance");
        var bounds = new uint[6];
        for (var i = 0; i < bounds.Length; i++)
        {
            bounds[i] = r.U32();
        }

        Bounds = bounds;
        RegionOriginX = (int)((bounds[0] >> 8) & 0xFFFF);
        RegionOriginZ = (int)((bounds[2] >> 8) & 0xFFFF);
        RegionsX = (int)((bounds[3] + 0xFF) >> 8) - RegionOriginX;
        RegionsZ = (int)((bounds[5] + 0xFF) >> 8) - RegionOriginZ;
        var regionCount = r.Count(1 << 16, "region");
        if (RegionsX <= 0 || RegionsZ <= 0 || regionCount != RegionsX * RegionsZ)
        {
            throw r.Fail(at,
                $"{regionCount} regions but the bounds derive {RegionsX}x{RegionsZ} (FUN_006e7510)");
        }

        var instances = new List<TacticsTileInstance>(total);
        for (var region = 0; region < regionCount; region++)
        {
            var regionAt = r.Position;
            var chunk = r.Tag("region");
            if (!string.Equals(chunk.Version, RegionVersion, StringComparison.Ordinal))
            {
                throw r.Fail(regionAt, $"<region> version '{chunk.Version}' is not {RegionVersion}");
            }

            var count = r.Count(MaximumCount, "region tile");
            var records = r.Bytes(count * TacticsTileInstance.RecordLength, "region tiles").Span;
            for (var i = 0; i < count; i++)
            {
                var record = records.Slice(i * TacticsTileInstance.RecordLength, TacticsTileInstance.RecordLength);
                var tileIndex = BinaryPrimitives.ReadUInt16LittleEndian(record);
                if (tileIndex == 0 || tileIndex >= TilePaths.Count)
                {
                    throw r.Fail(regionAt, $"region {region} tile {i} names tile {tileIndex} of a {TilePaths.Count}-entry table");
                }

                instances.Add(new TacticsTileInstance(
                    region,
                    tileIndex,
                    BinaryPrimitives.ReadUInt16LittleEndian(record[2..]),
                    BinaryPrimitives.ReadInt32LittleEndian(record[4..]),
                    BinaryPrimitives.ReadInt32LittleEndian(record[8..]),
                    BinaryPrimitives.ReadInt32LittleEndian(record[12..]),
                    BinaryPrimitives.ReadInt32LittleEndian(record[16..]),
                    BinaryPrimitives.ReadInt32LittleEndian(record[20..]),
                    BinaryPrimitives.ReadInt32LittleEndian(record[24..]),
                    BinaryPrimitives.ReadInt32LittleEndian(record[28..]),
                    BinaryPrimitives.ReadInt32LittleEndian(record[32..]),
                    BinaryPrimitives.ReadInt32LittleEndian(record[36..]),
                    BinaryPrimitives.ReadInt32LittleEndian(record[40..]),
                    BinaryPrimitives.ReadUInt16LittleEndian(record[44..]),
                    BinaryPrimitives.ReadUInt16LittleEndian(record[46..]),
                    BinaryPrimitives.ReadUInt16LittleEndian(record[48..]),
                    BinaryPrimitives.ReadUInt16LittleEndian(record[50..]),
                    BinaryPrimitives.ReadUInt32LittleEndian(record[52..])));
            }
        }

        if (instances.Count != total)
        {
            throw r.Fail(at, $"the regions hold {instances.Count} tiles but the header declares {total}");
        }

        Instances = instances;
    }

    /// <summary><c>FUN_006d7a70</c>, the version &gt;= 17 branch: two lists, the second skipping entry 0.</summary>
    private void ReadTileGroups(TacticsCursor r)
    {
        var names = new List<string>();
        for (var list = 0; list < 2; list++)
        {
            var count = r.Count(MaximumCount, "tile group");
            for (var i = list; i < count; i++)
            {
                names.Add(r.String());
                U32s(r, "tile group array");
                U32s(r, "tile group array");
            }
        }

        TileGroupNames = names;
    }

    /// <summary><c>FUN_004e52d0</c> (v3) with <c>FUN_005e8ed0</c> per slot and <c>FUN_005f33b0</c> per bag.</summary>
    private void ReadEntities(TacticsCursor r)
    {
        var at = r.Position;
        var chunk = r.Tag("entity_file");
        if (!string.Equals(chunk.Version, EntityFileVersion, StringComparison.Ordinal))
        {
            throw r.Fail(at, $"<entity_file> version '{chunk.Version}' is not {EntityFileVersion}");
        }

        var classes = Strings(r, "entity class");
        EntityClasses = classes;
        EntitySlotCapacity = r.U16();
        var used = r.U16();
        EntityHeaderUnknown = r.U16();

        var entities = new List<TacticsWorldEntity>(used);
        for (var slot = 1; slot < EntitySlotCapacity; slot++)
        {
            var slotA = r.U16();
            var slotB = r.U16();
            var classIndex = r.U16();
            if (classIndex == 0xFFFF)
            {
                continue;
            }

            if (classIndex >= classes.Count)
            {
                throw r.Fail(r.Position - 2, $"entity slot {slot} names class {classIndex} of {classes.Count}");
            }

            entities.Add(new TacticsWorldEntity(slot, slotA, slotB, classes[classIndex], r.Properties()));
        }

        if (entities.Count != used)
        {
            throw r.Fail(at, $"{entities.Count} entities read but the header declares {used}");
        }

        Entities = entities;
    }

    /// <summary><c>FUN_004fff80</c>, including the byte the decompile dropped.</summary>
    private void ReadZones(TacticsCursor r)
    {
        var count = r.Count(1 << 16, "zone");
        var zones = new List<TacticsWorldZone>(count);
        for (var i = 0; i < count; i++)
        {
            var chunk = r.Tag("world_zone");
            var version = ParseVersion(r, chunk);
            var colour = new Vector3(r.F32(), r.F32(), r.F32());
            var name = r.String();
            var a = new Vector3(r.F32(), r.F32(), r.F32());
            var b = new Vector3(r.F32(), r.F32(), r.F32());
            var flag = version > 1 ? r.U8() : (byte)0;
            zones.Add(new TacticsWorldZone(name, colour, Vector3.Min(a, b), Vector3.Max(a, b), flag));
        }

        Zones = zones;
    }

    /// <summary><c>FUN_004dee80</c> with <c>FUN_004de720</c> (group headers) and <c>FUN_004dd950</c> (PInfoH).</summary>
    private void ReadPlayers(TacticsCursor r)
    {
        var count = r.Count(1000, "player");
        var players = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            var chunk = r.Tag("Player");
            var version = ParseVersion(r, chunk);
            players.Add(r.String());
            r.U32();
            r.Bytes(12, "player triple");
            r.U32();
            if (version > 1)
            {
                r.U16();
                r.U16();
            }

            if (version is > 2 and < 5)
            {
                r.U16();
            }

            if (version > 3)
            {
                var groups = version > 7 ? 5 : 10;
                for (var g = 0; g < groups; g++)
                {
                    var header = r.Tag("Group Header");
                    var groupVersion = ParseVersion(r, header);
                    r.Bytes(r.Count(MaximumCount, "group header"), "group header body");
                    if (groupVersion is > 1 and < 3)
                    {
                        r.U8();
                    }
                }
            }

            if (version > 4)
            {
                r.U32();
                r.U8();
                r.Bytes(12, "player triple");
                r.Bytes(r.Count(MaximumCount, "player dword") * 4, "player dwords");
                r.Bytes(r.Count(MaximumCount, "player triple") * 12, "player triples");
                r.Bytes(r.Count(MaximumCount, "player triple") * 12, "player triples");
                r.Tag("PInfoH");
                r.Bytes(3 + 1 + 1 + 4 + 4 + 2 + 0x20, "PInfoH body");
            }

            if (version > 5)
            {
                r.Bytes(8, "player tail");
            }

            if (version > 6)
            {
                r.U32();
            }
        }

        Players = players;
    }

    /// <summary><c>FUN_004ddcb0</c>: nine chunks; the u32 after the flag is version-gated (&gt; 1) and Ghidra dropped it.</summary>
    private void ReadWorldTeams(TacticsCursor r)
    {
        var teams = new List<TacticsWorldTeam>(WorldTeamCount);
        for (var i = 0; i < WorldTeamCount; i++)
        {
            var chunk = r.Tag(TacticsMissionFile.TeamTag);
            var version = ParseVersion(r, chunk);
            var name = r.String();
            var flag = r.U8();
            var tail = version > 1 ? r.U32() : 0u;
            teams.Add(new TacticsWorldTeam(name, flag, tail));
        }

        WorldTeams = teams;
    }

    /// <summary><c>FUN_00501430</c>, version &gt;= 2 branch. The tag is BRACKETLESS.</summary>
    private void ReadTimerTable(TacticsCursor r)
    {
        var version = BracketlessTag(r, "timerTableHeader");
        if (version < 2)
        {
            throw r.Fail(r.Position, $"timerTableHeader version {version} takes the legacy branch, which retail never ships");
        }

        r.Bytes(r.Count(MaximumCount, "timer") * 8, "timers");
        TimerNames = Strings(r, "timer name");
        r.Bytes(r.Count(MaximumCount, "timer dword") * 4, "timer dwords");
        r.Bytes(r.Count(MaximumCount, "timer dword") * 4, "timer dwords");
        r.Bytes(r.Count(MaximumCount, "timer dword") * 4, "timer dwords");
    }

    /// <summary><c>FUN_005008b0</c>. The tag is BRACKETLESS.</summary>
    private void ReadVariableTable(TacticsCursor r)
    {
        BracketlessTag(r, "varTableHeader");
        VariableNames = Strings(r, "variable name");
        VariableValues = Strings(r, "variable value");
    }

    /// <summary><c>FUN_004fb880</c> + <c>FUN_004ff080</c> — the row's third string on v2 and the trailing one on v&gt;2 are the dropped blocks.</summary>
    private void ReadSpeechList(TacticsCursor r)
    {
        var at = r.Position;
        if (!TacticsTagChunk.TryRead(r.Ahead, out var list)
            || list.Tag is not ("speechList" or "speachList"))
        {
            throw r.Fail(at, "expected a <speechList> (or <speachList>) tag");
        }

        r.Position += list.BodyOffset;
        var count = r.Count(MaximumCount, "speech node");
        SpeechNodeCount = count;
        for (var i = 0; i < count; i++)
        {
            var chunk = r.Tag("speech_node");
            var version = ParseVersion(r, chunk);
            r.U8();
            r.String();
            for (var row = 0; row < 6; row++)
            {
                r.String();
                r.String();
                if (version == 2)
                {
                    r.String();
                }
            }

            if (version > 2)
            {
                r.String();
            }
        }
    }

    /// <summary><c>FUN_004fe280</c>: 8 raw, name, two u32, and the version-gated u32 Ghidra dropped.</summary>
    private void ReadObjectives(TacticsCursor r)
    {
        var count = r.Count(MaximumCount, "objective");
        var names = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            var chunk = r.Tag("mnob");
            var version = ParseVersion(r, chunk);
            r.Bytes(8, "objective head");
            names.Add(r.String());
            r.U32();
            r.U32();
            if (version > 1)
            {
                r.U32();
            }
        }

        Objectives = names;
    }

    /// <summary><c>FUN_0067a9f0</c> → <c>FUN_00679830</c> per trigger → <c>FUN_00678a80</c> per condition/action.</summary>
    private void ReadTriggers(TacticsCursor r)
    {
        r.Tag("TriggerManager");
        var count = r.Count(MaximumCount, "trigger");
        var triggers = new List<TacticsWorldTrigger>(count);
        for (var i = 0; i < count; i++)
        {
            var chunk = r.Tag("Trigger");
            var version = ParseVersion(r, chunk);
            var name = r.String();
            r.U32();
            r.U32();
            r.U8();
            if (version > 1)
            {
                r.U8();
            }

            if (version == 3)
            {
                r.String();
            }

            var conditions = ScriptObjects(r, "condition");
            var actions = ScriptObjects(r, "action");
            triggers.Add(new TacticsWorldTrigger(name, conditions, actions));
        }

        Triggers = triggers;
    }

    /// <summary>A u32-counted list of (class name + <c>&lt;esh&gt;</c> bag), as <c>FUN_00678a80</c> reads a script object.</summary>
    private static List<string> ScriptObjects(TacticsCursor r, string what)
    {
        var count = r.Count(MaximumCount, what);
        var names = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            names.Add(r.String());
            r.Properties();
        }

        return names;
    }

    /// <summary><c>FUN_004dd0b0</c>, transcribed read for read.</summary>
    private void ReadAmbientSounds(TacticsCursor r)
    {
        var count = r.Count(MaximumCount, "ambient sound");
        var names = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            var chunk = r.Tag("ambientSound");
            var version = ParseVersion(r, chunk);
            names.Add(r.String());
            r.U32();
            r.U8();
            Strings(r, "ambient sound name");
            r.U8();
            r.Bytes(12, "ambient triple");
            r.U32();
            r.U32();
            r.U32();
            r.Bytes(8, "ambient floats");
            r.U8();
            r.U32();
            r.U32();
            r.U32();
            r.U32();
            r.U8();
            r.U32();
            r.Bytes(4, "ambient float");
            r.U32();
            r.U8();
            r.String();
            if (version == 2)
            {
                r.String();
            }
            else if (version > 2)
            {
                Strings(r, "ambient sound string");
            }
        }

        AmbientSounds = names;
    }

    /// <summary>The version-gated tail of <c>FUN_004e8900</c>.</summary>
    private void ReadTail(TacticsCursor r)
    {
        if (ContainerVersion > 0x3c)
        {
            r.Bytes(4, "tail float");
            r.String();
            Strings(r, "tail string");
            if (ContainerVersion < 0x42)
            {
                r.String();
                r.String();
            }
        }

        r.Bytes(3, "tail bytes");
        if (ContainerVersion > 0x3f)
        {
            Strings(r, "tail string");
            Strings(r, "tail string");
        }

        if (ContainerVersion > 0x40)
        {
            r.Bytes(24, "tail block");
        }

        if (ContainerVersion > 0x43)
        {
            r.U8();
        }

        if (ContainerVersion > 0x44)
        {
            r.U8();
        }
    }

    /// <summary>A tag written WITHOUT angle brackets: the literal name, NUL, an ASCII version, NUL.</summary>
    private static int BracketlessTag(TacticsCursor r, string tag)
    {
        var at = r.Position;
        r.Expect(Encoding.ASCII.GetBytes(tag + "\0"), $"the bracketless '{tag}' tag");
        var span = r.Ahead;
        var end = span.IndexOf((byte)0);
        if (end is <= 0 or > 4)
        {
            throw r.Fail(at, $"'{tag}' has no version");
        }

        var version = int.Parse(Encoding.ASCII.GetString(span[..end]), NumberStyles.None, CultureInfo.InvariantCulture);
        r.Position += end + 1;
        return version;
    }

    private static int ParseVersion(TacticsCursor r, TacticsTagChunk chunk)
    {
        if (!int.TryParse(chunk.Version, NumberStyles.None, CultureInfo.InvariantCulture, out var version))
        {
            throw r.Fail(r.Position, $"<{chunk.Tag}> version '{chunk.Version}' is not a number");
        }

        return version;
    }

    private static List<string> Strings(TacticsCursor r, string what)
    {
        var count = r.Count(MaximumCount, what);
        var list = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            list.Add(r.String());
        }

        return list;
    }

    private static List<uint> U32s(TacticsCursor r, string what)
    {
        var count = r.Count(MaximumCount, what);
        var list = new List<uint>(count);
        for (var i = 0; i < count; i++)
        {
            list.Add(r.U32());
        }

        return list;
    }
}
