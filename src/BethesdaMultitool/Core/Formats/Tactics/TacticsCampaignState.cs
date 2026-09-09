namespace BethesdaMultitool.Core.Formats.Tactics;

/// <summary>A campaign variable: <c>CVAR_M01_COMPLETE</c> = <c>FALSE</c>.</summary>
internal readonly record struct TacticsCampaignVariable(string Name, string Value);

/// <summary>A row of the campaign's stock table: which mission offers how many of an entity (45 rows are -1000).</summary>
internal readonly record struct TacticsCampaignStock(string MissionId, string EntityPath, int Quantity);

/// <summary>A recruit row: a mission id, an <c>entities/recruits/*.ent</c> path and an action (<c>add</c> on 66/66).</summary>
internal readonly record struct TacticsCampaignRecruit(string MissionId, string EntityPath, string Action);

/// <summary>
///     A <c>&lt;random_force&gt;</c> v2 record: a named encounter force. <paramref name="Low" /> and
///     <paramref name="High" /> satisfy Low &lt;= High on 109/109 (1..8 and 1..12) but whether they
///     are a spawn range is NOT established; the two per-entity u32 arrays (1..17 and 10/50/100)
///     each repeat the entity count and are carried raw.
/// </summary>
internal sealed record TacticsRandomForce(
    string Name,
    string Kind,
    uint Low,
    uint High,
    byte Flag,
    IReadOnlyList<string> EntityPaths,
    IReadOnlyList<uint> FirstValues,
    IReadOnlyList<uint> SecondValues,
    string Zone);

/// <summary>
///     The campaign state a Fallout Tactics save archives as <c>user/$$current$$/save.cam</c> — a
///     <c>&lt;campaign&gt;</c> v21 record that <b>tiles to its last byte</b> (104,378/104,378 on the
///     fixture, every count satisfied). Original RE 2026-09-07 on <c>Snake.sav</c>; every Tactics
///     reference is GPL, so nothing is ported. ⚠ Distinct from the shipped <c>campaigns/bos.cam</c>,
///     a <c>&lt;campaign&gt;</c> v19 that carries <c>&lt;campaign_tile&gt;</c> chunks where this
///     carries a zeroed grid — the two share a tag, a version family and the (65, 34) grid size,
///     not a layout.
///     <para>
///         Layout after the tag (little-endian; WSTR = wide string, ASTR = ASCII, see
///         <see cref="TacticsCursor" />): u8; WSTR source campaign (<c>campaigns/bos.cam</c>); u32
///         grid width, u32 height (65 x 34 — equal to retail <c>bos.cam</c>'s own first two dwords,
///         an oracle from outside the file); width x height u32 cells (all zero); WSTR current
///         mission file (<c>user/$$current$$/mission01.sav</c> — the sibling entry of the archive);
///         four u32 (20, 0, 0, 0); the literal <c>varTableHeader NUL '1' NUL</c> (⚠ a BRACKETLESS
///         tag <see cref="TacticsTagChunk" /> rejects, matched literally); u32 n + n WSTR variable
///         names; u32 n + n WSTR values (95: 94 <c>FALSE</c>, <c>CVAR_M10_MUTANTLAB</c> =
///         <c>SALVAGED</c>); four floats (1431, 1393, 4322, 867); u32 + that many <c>&lt;esh&gt;</c>
///         location bags (27, <c>Name</c> mission_name_01..21, 00, B01..B05, with <c>Loc x</c>/
///         <c>Loc y</c>/<c>Radius</c>/<c>MissionFile</c>/<c>State</c>); u32 + special-encounter bags
///         (30, mission_name_Z01..Z27 and T01..T04); u32 + <c>&lt;random_force&gt;</c> v2 records
///         (109); u32 + WSTR random mission paths (42, <c>missionY01..Y42.mis</c>); u32 + WSTR
///         prefab paths (5); u32 + stock rows (232: WSTR, WSTR, i32); two zero u32; u32 + recruit
///         rows (66: three WSTR); 31 zero bytes (⚠ their field split is undeterminable — all zero);
///         an empty WSTR; six (WSTR, u32) slots, all empty and 0xCFCFCFCF (an uninitialised-fill
///         pattern; a squad roster is a reading, not a proof); float 1.0; u8; u32 count + WSTR
///         location ids (1: <c>mission_name_01</c>); twelve u32 (10, 0, 0, 1, 70, 0 ...); u32 + WSTR
///         movie names (4); u32 + WSTR movie paths (4, ending <c>movie\secondary.bik</c>) — and the
///         walk lands on EOF.
///     </para>
///     <para>
///         ⛔ Two readings that FAILED on the way, so nobody repeats them: a random force is NOT
///         "entity list then a WSTR" (record 2 then reads u32 0xA where a flag is due — TWO
///         count-prefixed u32 arrays follow the list, each repeating the count on 109/109); and
///         the recruit table is TRIPLES of WSTR, not singles (reading it flat mis-parses everything
///         after it). Both were caught by the wide-string flag assert firing on misalignment.
///     </para>
/// </summary>
internal sealed class TacticsCampaignState
{
    /// <summary>The tag.</summary>
    public const string Tag = "campaign";

    /// <summary>The version a save writes ("21"); the shipped <c>bos.cam</c> is "19" with a different body.</summary>
    public const string SaveVersion = "21";

    /// <summary>The fill pattern of an unset roster slot.</summary>
    public const uint UnsetSlot = 0xCFCFCFCF;

    private TacticsCampaignState(
        byte flag, string sourceCampaign, int gridWidth, int gridHeight, IReadOnlyList<uint> gridCells,
        string currentMissionFile, IReadOnlyList<uint> headerValues, IReadOnlyList<TacticsCampaignVariable> variables,
        IReadOnlyList<float> floats, IReadOnlyList<IReadOnlyList<TacticsProperty>> locations,
        IReadOnlyList<IReadOnlyList<TacticsProperty>> specialEncounters, IReadOnlyList<TacticsRandomForce> randomForces,
        IReadOnlyList<string> randomMissionPaths, IReadOnlyList<string> prefabPaths,
        IReadOnlyList<TacticsCampaignStock> stock,
        IReadOnlyList<TacticsCampaignRecruit> recruits, IReadOnlyList<(string Name, uint Value)> rosterSlots,
        float tailFloat,
        byte tailFlag, IReadOnlyList<string> currentLocationIds, IReadOnlyList<uint> tailValues,
        IReadOnlyList<string> movieNames, IReadOnlyList<string> moviePaths, int length)
    {
        Flag = flag;
        SourceCampaign = sourceCampaign;
        GridWidth = gridWidth;
        GridHeight = gridHeight;
        GridCells = gridCells;
        CurrentMissionFile = currentMissionFile;
        HeaderValues = headerValues;
        Variables = variables;
        Floats = floats;
        Locations = locations;
        SpecialEncounters = specialEncounters;
        RandomForces = randomForces;
        RandomMissionPaths = randomMissionPaths;
        PrefabPaths = prefabPaths;
        Stock = stock;
        Recruits = recruits;
        RosterSlots = rosterSlots;
        TailFloat = tailFloat;
        TailFlag = tailFlag;
        CurrentLocationIds = currentLocationIds;
        TailValues = tailValues;
        MovieNames = movieNames;
        MoviePaths = moviePaths;
        Length = length;
    }

    /// <summary>The bracketless tag that opens the variable table, NULs included: <c>varTableHeader NUL 1 NUL</c>.</summary>
    public static ReadOnlySpan<byte> VariableTableHeader => "varTableHeader\u00001\u0000"u8;

    public byte Flag { get; }

    /// <summary>The shipped campaign this state was started from, e.g. <c>campaigns/bos.cam</c>.</summary>
    public string SourceCampaign { get; }

    public int GridWidth { get; }

    public int GridHeight { get; }

    /// <summary>Width x height u32 cells — all zero on the fixture.</summary>
    public IReadOnlyList<uint> GridCells { get; }

    /// <summary>The archived snapshot the campaign is in, e.g. <c>user/$$current$$/mission01.sav</c>.</summary>
    public string CurrentMissionFile { get; }

    /// <summary>Four u32 after the current mission — (20, 0, 0, 0); meaning open.</summary>
    public IReadOnlyList<uint> HeaderValues { get; }

    /// <summary>The campaign variables, name and value paired by index.</summary>
    public IReadOnlyList<TacticsCampaignVariable> Variables { get; }

    /// <summary>Four floats after the variables — (1431, 1393, 4322, 867); meaning open.</summary>
    public IReadOnlyList<float> Floats { get; }

    /// <summary>The world-map location bags (27).</summary>
    public IReadOnlyList<IReadOnlyList<TacticsProperty>> Locations { get; }

    /// <summary>The special-encounter bags (30).</summary>
    public IReadOnlyList<IReadOnlyList<TacticsProperty>> SpecialEncounters { get; }

    public IReadOnlyList<TacticsRandomForce> RandomForces { get; }

    public IReadOnlyList<string> RandomMissionPaths { get; }

    public IReadOnlyList<string> PrefabPaths { get; }

    public IReadOnlyList<TacticsCampaignStock> Stock { get; }

    public IReadOnlyList<TacticsCampaignRecruit> Recruits { get; }

    /// <summary>Six (name, u32) slots — all empty / <see cref="UnsetSlot" /> on the fixture.</summary>
    public IReadOnlyList<(string Name, uint Value)> RosterSlots { get; }

    public float TailFloat { get; }

    public byte TailFlag { get; }

    /// <summary>Location ids after the tail flag — <c>mission_name_01</c> alone on the fixture.</summary>
    public IReadOnlyList<string> CurrentLocationIds { get; }

    /// <summary>Twelve u32 before the movie lists — (10, 0, 0, 1, 70, 0, 0, 0, 0, 0, 0, 0); meaning open.</summary>
    public IReadOnlyList<uint> TailValues { get; }

    public IReadOnlyList<string> MovieNames { get; }

    public IReadOnlyList<string> MoviePaths { get; }

    /// <summary>Bytes the record occupies — equal to the buffer length, since the walk must end there.</summary>
    public int Length { get; }

    /// <summary>The <c>Name</c> property of each location bag, in order.</summary>
    public IEnumerable<string> LocationNames => Locations.Select(NameOf);

    /// <summary>The <c>Name</c> property of each special-encounter bag, in order.</summary>
    public IEnumerable<string> SpecialEncounterNames => SpecialEncounters.Select(NameOf);

    /// <summary>Content probe: the framing with the <c>campaign</c> tag.</summary>
    public static bool IsCampaign(ReadOnlySpan<byte> bytes)
    {
        return TacticsTagChunk.Is(bytes, Tag);
    }

    /// <summary>Parses a whole <c>save.cam</c>, throwing unless the walk lands exactly on its last byte.</summary>
    public static TacticsCampaignState Parse(ReadOnlyMemory<byte> bytes, string name)
    {
        var cursor = new TacticsCursor(bytes, name);
        var chunk = cursor.Tag(Tag);
        if (!string.Equals(chunk.Version, SaveVersion, StringComparison.Ordinal))
        {
            throw cursor.Fail(0,
                $"<{Tag}> version '{chunk.Version}' is not the save's '{SaveVersion}' (the shipped bos.cam is '19' and lays out differently)");
        }

        var flag = cursor.U8();
        var source = cursor.WideString();
        var width = cursor.Count(1024, "grid width");
        var height = cursor.Count(1024, "grid height");
        var cells = new uint[width * height];
        for (var i = 0; i < cells.Length; i++)
        {
            cells[i] = cursor.U32();
        }

        var currentMission = cursor.WideString();
        var headerValues = ReadU32s(cursor, 4);

        cursor.Expect(VariableTableHeader, "the 'varTableHeader' literal");
        var names = ReadWideStrings(cursor, 4096, "variable name");
        var values = ReadWideStrings(cursor, 4096, "variable value");
        if (values.Count != names.Count)
        {
            throw cursor.Fail(cursor.Position, $"{names.Count} variable names but {values.Count} values");
        }

        var variables = new TacticsCampaignVariable[names.Count];
        for (var i = 0; i < variables.Length; i++)
        {
            variables[i] = new TacticsCampaignVariable(names[i], values[i]);
        }

        var floats = new float[4];
        for (var i = 0; i < floats.Length; i++)
        {
            floats[i] = cursor.F32();
        }

        var locations = ReadBags(cursor, "location");
        var specials = ReadBags(cursor, "special encounter");

        var forceCount = cursor.Count(4096, "random force");
        var forces = new List<TacticsRandomForce>(forceCount);
        for (var i = 0; i < forceCount; i++)
        {
            forces.Add(ReadRandomForce(cursor));
        }

        var randomMissions = ReadWideStrings(cursor, 4096, "random mission path");
        var prefabs = ReadWideStrings(cursor, 4096, "prefab path");

        var stockCount = cursor.Count(65536, "stock row");
        var stock = new List<TacticsCampaignStock>(stockCount);
        for (var i = 0; i < stockCount; i++)
        {
            var missionId = cursor.WideString();
            var entityPath = cursor.WideString();
            stock.Add(new TacticsCampaignStock(missionId, entityPath, cursor.I32()));
        }

        cursor.Zeros(8, "the two u32 after the stock table");

        var recruitCount = cursor.Count(65536, "recruit row");
        var recruits = new List<TacticsCampaignRecruit>(recruitCount);
        for (var i = 0; i < recruitCount; i++)
        {
            var missionId = cursor.WideString();
            var entityPath = cursor.WideString();
            recruits.Add(new TacticsCampaignRecruit(missionId, entityPath, cursor.WideString()));
        }

        cursor.Zeros(31, "the 31 bytes after the recruit table");
        var empty = cursor.WideString();
        if (empty.Length != 0)
        {
            throw cursor.Fail(cursor.Position,
                $"expected an empty wide string before the roster slots, read '{empty}'");
        }

        var slots = new (string Name, uint Value)[6];
        for (var i = 0; i < slots.Length; i++)
        {
            var slotName = cursor.WideString();
            slots[i] = (slotName, cursor.U32());
        }

        var tailFloat = cursor.F32();
        var tailFlag = cursor.U8();
        var locationIds = ReadWideStrings(cursor, 64, "current location id");
        var tailValues = ReadU32s(cursor, 12);
        var movieNames = ReadWideStrings(cursor, 4096, "movie name");
        var moviePaths = ReadWideStrings(cursor, 4096, "movie path");

        cursor.RequireEnd($"<{Tag}> v{SaveVersion}");

        return new TacticsCampaignState(
            flag, source, width, height, cells, currentMission, headerValues, variables, floats,
            locations, specials, forces, randomMissions, prefabs, stock, recruits, slots, tailFloat,
            tailFlag, locationIds, tailValues, movieNames, moviePaths, cursor.Position);
    }

    /// <summary>Parses a whole <c>save.cam</c>, reporting why rather than throwing.</summary>
    public static bool TryParse(ReadOnlyMemory<byte> bytes, string name, out TacticsCampaignState campaign,
        out string error)
    {
        try
        {
            campaign = Parse(bytes, name);
            error = string.Empty;
            return true;
        }
        catch (InvalidDataException e)
        {
            campaign = null!;
            error = e.Message;
            return false;
        }
    }

    /// <summary>The first property named <paramref name="propertyName" /> of a bag, or null.</summary>
    public static TacticsProperty? Find(IReadOnlyList<TacticsProperty> bag, string propertyName)
    {
        ArgumentNullException.ThrowIfNull(bag);
        ArgumentNullException.ThrowIfNull(propertyName);

        foreach (var property in bag)
        {
            if (string.Equals(property.Name, propertyName, StringComparison.Ordinal))
            {
                return property;
            }
        }

        return null;
    }

    private static string NameOf(IReadOnlyList<TacticsProperty> bag)
    {
        return Find(bag, "Name") is { } property ? TacticsCursor.PropertyText(property) : string.Empty;
    }

    private static TacticsRandomForce ReadRandomForce(TacticsCursor cursor)
    {
        var start = cursor.Position;
        var chunk = cursor.Tag("random_force");
        if (!string.Equals(chunk.Version, "2", StringComparison.Ordinal))
        {
            throw cursor.Fail(start, $"<random_force> version '{chunk.Version}' is not the measured '2'");
        }

        var forceName = cursor.WideString();
        var kind = cursor.WideString();
        var low = cursor.U32();
        var high = cursor.U32();
        var flag = cursor.U8();
        var entities = ReadWideStrings(cursor, 256, "random force entity");

        // ⚠ TWO count-prefixed u32 arrays follow the entity list, each repeating its count.
        var first = ReadCountedU32s(cursor, entities.Count, "random force first array");
        var second = ReadCountedU32s(cursor, entities.Count, "random force second array");
        var zone = cursor.WideString();
        return new TacticsRandomForce(forceName, kind, low, high, flag, entities, first, second, zone);
    }

    private static List<IReadOnlyList<TacticsProperty>> ReadBags(TacticsCursor cursor, string what)
    {
        var count = cursor.Count(4096, what);
        var bags = new List<IReadOnlyList<TacticsProperty>>(count);
        for (var i = 0; i < count; i++)
        {
            bags.Add(cursor.Properties());
        }

        return bags;
    }

    private static List<string> ReadWideStrings(TacticsCursor cursor, int maximum, string what)
    {
        var count = cursor.Count(maximum, what);
        var strings = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            strings.Add(cursor.WideString());
        }

        return strings;
    }

    private static uint[] ReadU32s(TacticsCursor cursor, int count)
    {
        var values = new uint[count];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = cursor.U32();
        }

        return values;
    }

    private static uint[] ReadCountedU32s(TacticsCursor cursor, int expected, string what)
    {
        var at = cursor.Position;
        var count = cursor.Count(65536, what);
        if (count != expected)
        {
            throw cursor.Fail(at, $"{what} declares {count} values for {expected} entities");
        }

        return ReadU32s(cursor, count);
    }
}
