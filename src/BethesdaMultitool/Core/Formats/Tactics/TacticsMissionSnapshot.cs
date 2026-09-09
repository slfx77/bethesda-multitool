namespace BethesdaMultitool.Core.Formats.Tactics;

/// <summary>One briefing entry in a save world: a name (<c>Brief</c>), two u32 and the text.</summary>
internal readonly record struct TacticsSaveBriefing(string Name, uint First, uint Second, string Text);

/// <summary>
///     The mission snapshot a Fallout Tactics save archives as <c>user/$$current$$/missionNN.sav</c>
///     — a <c>&lt;saveh&gt;</c> v2 header (<see cref="TacticsSaveHeader" />) followed by a
///     <c>&lt;world&gt;</c> container of the SAME framing as a shipped <c>.mis</c>
///     (<see cref="TacticsMissionFile" />: size twice, raw zlib inflating to exactly that size).
///     Original RE 2026-09-07 on <c>Snake.sav</c>; every Tactics reference is GPL, so nothing is
///     ported.
///     <para>
///         ⚑ The container claim of the backlog ("saves share the MIS world codec") HOLDS:
///         <see cref="TacticsMissionFile.IsMission" /> is true at the container and the stream
///         inflates to exactly the declared 2,607,657 bytes, consuming exactly the archived entry —
///         ⚑ which <see cref="TacticsMissionFile.TryParse" /> now ENFORCES through the stream's
///         trailing Adler-32 rather than merely asserting here.
///         ⚠ Its version is <c>'70'</c>, which no retail mission carries (87 are 68, 16 are 69), and
///         <b>the inflated payload does NOT open with <c>&lt;mph&gt;</c></b>, so
///         <see cref="TacticsMissionFile.Teams" /> is empty on it. What it opens with instead —
///         measured to tile exactly up to the first entity — is decoded here as the world HEAD:
///     </para>
///     <para>
///         wide string: the mission this save is a delta of (<c>campaigns/missions/core/mission01.mis</c>,
///         an entry of <c>mis-core_A.bos</c> whose nine <c>&lt;Team&gt;</c> chunks equal the save's
///         by name and flag); <c>&lt;sgd&gt;</c> v5 with a 72-byte body (carried raw — single sample,
///         semantics open); a u32 count of briefings, each <c>wide name, u32, u32, wide text</c> (the
///         1,732-character text is the <c>MISSION_BRIEF</c> block of <c>loc-mis_A.bos</c>'s
///         <c>locale/missions/mission01/MIS_01_Speech.txt</c>, the very path the header's string 0
///         names — ⚠ NOT byte-for-byte: the shipped file wraps its lines and writes the paragraph
///         breaks as literal <c>\n</c> escapes, so the two agree only once escapes are expanded and
///         layout whitespace dropped, 1,431 non-blank characters matching); <c>&lt;SSG&gt;</c> v1
///         with a 20-byte body (raw); <c>&lt;entity_file&gt;</c>
///         v3 with a u32 count and that many wide entity class names (52: BaseAI, Controller,
///         Physics, ... Entity, LinkHack — the SAME 52 the shipped mission lists in ASCII); then a
///         12-byte entity-list header <c>u16, u16, u32, u16, u16</c> = (2000, 955, 1398, 62, 47)
///         against the mission's (2000, 923, 54, 62, 47) at the same place.
///     </para>
///     <para>
///         ⚠ <b>The walk stops there</b>: the first <c>&lt;esh&gt;</c> is found EXACTLY at
///         <see cref="EntitiesOffset" /> (independently located by a tag scan), and its 31
///         self-describing properties ('Weapon Item Type' = 'SMG') walk under
///         <see cref="TacticsCursor.Properties" />. Beyond it the entities follow the MIS world
///         grammar (esh 6,001, attribs 1,970, actor 71, Trigger 42, Team 9 ...) WITHOUT the tile
///         grid, <c>&lt;mapManager&gt;</c> or regions a mission carries, and WITH 68 runtime
///         <c>&lt;baseAI&gt;</c> chunks no mission has — the backlog's open MIS-interior item, not
///         tiled here. A head that does not walk (a different sgd/SSG/entity_file version, or no esh
///         where one is due) leaves <see cref="HeadError" /> set and the world bytes available.
///     </para>
/// </summary>
internal sealed class TacticsMissionSnapshot
{
    /// <summary>The <c>&lt;world&gt;</c> version the save wrote — outside the retail 68/69 set.</summary>
    public const string SaveWorldVersion = "70";

    /// <summary>Bytes of the <c>&lt;sgd&gt;</c> v5 body.</summary>
    public const int SgdBodyLength = 72;

    /// <summary>Bytes of the <c>&lt;SSG&gt;</c> v1 body.</summary>
    public const int SsgBodyLength = 20;

    // ⚠ Deferred on purpose: the scan sweeps the WHOLE inflated world (2,607,657 B on the fixture)
    // and most callers only want the header, the head or the raw bytes.
    private readonly Lazy<IReadOnlyList<TacticsWorldTeam>> _teams;

    private TacticsMissionSnapshot(TacticsSaveHeader header, TacticsMissionFile world)
    {
        Header = header;
        World = world;
        _teams = new Lazy<IReadOnlyList<TacticsWorldTeam>>(() => TacticsMissionFile.ScanTeams(world.World));
        MissionPath = string.Empty;
        SgdVersion = string.Empty;
        SgdBody = ReadOnlyMemory<byte>.Empty;
        Briefings = [];
        SsgBody = ReadOnlyMemory<byte>.Empty;
        EntityClassNames = [];
        EntityListHeader = [];
        HeadError = string.Empty;
    }

    /// <summary>The snapshot's own <c>&lt;saveh&gt;</c> — string 0 names the speech file.</summary>
    public TacticsSaveHeader Header { get; }

    /// <summary>The inflated world container; <see cref="TacticsMissionFile.Version" /> is "70".</summary>
    public TacticsMissionFile World { get; }

    /// <summary>
    ///     The world's <c>&lt;Team&gt;</c> chunks — the roster that PAIRS THE SAVE WITH ITS MISSION:
    ///     the nine here are the nine of <c>campaigns/missions/core/mission01.mis</c>, same names in
    ///     the same order with the same flags, the save's written wide and the mission's ASCII.
    ///     ⚠ Found by <see cref="TacticsMissionFile.ScanTeams" />, a scan of the undecoded entity
    ///     section, not by a walk. <see cref="TacticsMissionFile.Teams" /> stays empty here: that is
    ///     the <c>&lt;mph&gt;</c> roster, which a save world does not carry.
    ///     ⚠ Scanned on FIRST READ, not at parse: the sweep covers the whole inflated world.
    /// </summary>
    public IReadOnlyList<TacticsWorldTeam> Teams => _teams.Value;

    /// <summary>The shipped mission this snapshot is a delta of, e.g. <c>campaigns/missions/core/mission01.mis</c>.</summary>
    public string MissionPath { get; private set; }

    /// <summary>The <c>&lt;sgd&gt;</c> version read ("5" on the fixture).</summary>
    public string SgdVersion { get; private set; }

    /// <summary>The 72-byte <c>&lt;sgd&gt;</c> body, raw.</summary>
    public ReadOnlyMemory<byte> SgdBody { get; private set; }

    /// <summary>The briefing entries — one, named <c>Brief</c>, on the fixture.</summary>
    public IReadOnlyList<TacticsSaveBriefing> Briefings { get; private set; }

    /// <summary>The 20-byte <c>&lt;SSG&gt;</c> body, raw.</summary>
    public ReadOnlyMemory<byte> SsgBody { get; private set; }

    /// <summary>The entity class names from <c>&lt;entity_file&gt;</c>.</summary>
    public IReadOnlyList<string> EntityClassNames { get; private set; }

    /// <summary>The five values of the 12-byte entity-list header (u16, u16, u32, u16, u16).</summary>
    public IReadOnlyList<uint> EntityListHeader { get; private set; }

    /// <summary>
    ///     Offset in <see cref="TacticsMissionFile.World" /> of the first entity's <c>&lt;esh&gt;</c>; -1 when the head
    ///     did not walk.
    /// </summary>
    public int EntitiesOffset { get; private set; } = -1;

    /// <summary>Empty when the head walked to the first entity; otherwise why it stopped.</summary>
    public string HeadError { get; private set; }

    /// <summary>True when every head field above was read and an <c>&lt;esh&gt;</c> sits at <see cref="EntitiesOffset" />.</summary>
    public bool HeadWalked => HeadError.Length == 0;

    /// <summary>Content probe: a <c>&lt;saveh&gt;</c> header.</summary>
    public static bool IsSnapshot(ReadOnlySpan<byte> bytes)
    {
        return TacticsSaveHeader.IsSaveHeader(bytes);
    }

    /// <summary>Parses a whole snapshot file, throwing when the container does not tile.</summary>
    public static TacticsMissionSnapshot Parse(ReadOnlyMemory<byte> bytes, string name)
    {
        var cursor = new TacticsCursor(bytes, name);
        var header = TacticsSaveHeader.Read(cursor);

        if (!TacticsMissionFile.TryParse(cursor.Ahead, name, out var world, out var error))
        {
            throw cursor.Fail(cursor.Position, error);
        }

        var snapshot = new TacticsMissionSnapshot(header, world);
        snapshot.WalkHead(name);
        return snapshot;
    }

    /// <summary>Parses a whole snapshot file, reporting why rather than throwing.</summary>
    public static bool TryParse(ReadOnlyMemory<byte> bytes, string name, out TacticsMissionSnapshot snapshot,
        out string error)
    {
        try
        {
            snapshot = Parse(bytes, name);
            error = string.Empty;
            return true;
        }
        catch (InvalidDataException e)
        {
            snapshot = null!;
            error = e.Message;
            return false;
        }
    }

    private void WalkHead(string name)
    {
        var cursor = new TacticsCursor(World.World, name + " world");
        try
        {
            MissionPath = cursor.WideString();

            var sgd = cursor.Tag("sgd");
            SgdVersion = sgd.Version;
            if (!string.Equals(sgd.Version, "5", StringComparison.Ordinal))
            {
                throw cursor.Fail(cursor.Position,
                    $"<sgd> version '{sgd.Version}' is not the measured '5'; its body length is unknown");
            }

            SgdBody = cursor.Bytes(SgdBodyLength, "the <sgd> body");

            var briefingCount = cursor.Count(64, "briefing");
            var briefings = new List<TacticsSaveBriefing>(briefingCount);
            for (var i = 0; i < briefingCount; i++)
            {
                var briefingName = cursor.WideString();
                var first = cursor.U32();
                var second = cursor.U32();
                briefings.Add(new TacticsSaveBriefing(briefingName, first, second, cursor.WideString()));
            }

            Briefings = briefings;

            var ssg = cursor.Tag("SSG");
            if (!string.Equals(ssg.Version, "1", StringComparison.Ordinal))
            {
                throw cursor.Fail(cursor.Position,
                    $"<SSG> version '{ssg.Version}' is not the measured '1'; its body length is unknown");
            }

            SsgBody = cursor.Bytes(SsgBodyLength, "the <SSG> body");

            var entityFile = cursor.Tag("entity_file");
            if (!string.Equals(entityFile.Version, "3", StringComparison.Ordinal))
            {
                throw cursor.Fail(cursor.Position,
                    $"<entity_file> version '{entityFile.Version}' is not the measured '3'");
            }

            var classCount = cursor.Count(1024, "entity class");
            var classNames = new string[classCount];
            for (var i = 0; i < classNames.Length; i++)
            {
                classNames[i] = cursor.WideString();
            }

            EntityClassNames = classNames;
            EntityListHeader = [cursor.U16(), cursor.U16(), cursor.U32(), cursor.U16(), cursor.U16()];

            // The alignment check: the first entity's <esh> must start exactly here.
            if (!TacticsTagChunk.Is(cursor.Ahead, "esh"))
            {
                throw cursor.Fail(cursor.Position, "no <esh> where the first entity is due");
            }

            EntitiesOffset = cursor.Position;
            HeadError = string.Empty;
        }
        catch (InvalidDataException e)
        {
            HeadError = e.Message;
        }
    }
}
