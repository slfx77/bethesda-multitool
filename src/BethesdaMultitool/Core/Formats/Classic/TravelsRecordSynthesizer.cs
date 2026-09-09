using System.Collections.Immutable;
using System.Globalization;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Travels;
using BethesdaMultitool.Core.Formats.Travels.Dawnstar;
using BethesdaMultitool.Core.Vfs;

namespace BethesdaMultitool.Core.Formats.Classic;

/// <summary>
///     Turns a mounted TES Travels J2ME install (Stormhold 2003, Dawnstar 2004) into browsable
///     records. The two games ship the SAME nine data tables through the readers in
///     <c>Core/Formats/Travels/</c>, differing only in where the bytes live — Stormhold keeps them
///     loose in the MIDlet JAR, Dawnstar packs all but <c>npcstrings.dat</c> into
///     <c>datfiles.lmp</c> — so one synthesizer serves both and each game's record source supplies
///     only its signature prefix, its domain and its lump name.
///     <para>
///         <b>Identity.</b> These tables carry no ids beyond their row ordinals — and the ordinals
///         ARE the engine's foreign keys: a monster's id is its ordinal + 1, an item's type index
///         is a 1-based position in the type list, every <c>droppeditemsin</c> cell is a 1-based
///         item id and <c>geomin</c> row <c>i</c> is dungeon <c>i + 1</c>. So a row ordinal is
///         source identity here, not enumeration order, and a diff that shows rows shifting
///         between two builds is telling the truth about the game. Tables are kept apart inside
///         one domain by a composite index, <c>(tableId &lt;&lt; 16) | rowOrdinal</c>;
///         <c>npcstrings.dat</c> lines key as <c>group &lt;&lt; 8 | line</c> in the next domain up,
///         because that is a different numbering scheme and must not share a space with the table
///         ids. Nothing here is hashed, so a duplicate FormID can only be a bug — and
///         <see cref="Populate" /> throws on one rather than renumbering.
///     </para>
///     <para>
///         Retail census this produces (measured 2026-09-05). Stormhold: 7 classes, 6 races,
///         14 skills, 109 items, 25 spells, 41 monsters, 5 sprite families, 43 loot rows,
///         37 dungeons and 153 NPC strings = 440 records. Dawnstar: the same character tables,
///         101 items, 25 spells, 42 monsters, 5 sprite families, 43 loot rows, 37 dungeons
///         (unnamed — it ships no <c>dungnamesin.dat</c>), 35 help lines and 167 NPC strings = 482.
///     </para>
///     <para>
///         <b>Trap (measured, not assumed).</b> Mounting the Dawnstar JAR does NOT surface the
///         lump's members: that JAR holds 20 flat entries of which the tables are exactly one,
///         <c>datfiles.lmp</c>, so <c>install.TryReadAllBytes("charin.dat")</c> returns null there.
///         Only an unpacked install layers the lump — its profile lists <c>datfiles.lmp</c> in
///         <c>ClassicArchiveGlobs</c>, which the mount opens through <c>LmpArchiveBackend</c>.
///         This synthesizer therefore tries the mounted path FIRST and falls back to opening the
///         lump itself, so both shapes of install yield the same records.
///     </para>
/// </summary>
internal static class TravelsRecordSynthesizer
{
    /// <summary>Signature body for a <c>charin.dat</c> class row.</summary>
    public const string ClassCode = "CLS";

    /// <summary>Signature body for a <c>charin.dat</c> race name.</summary>
    public const string RaceCode = "RCE";

    /// <summary>Signature body for a <c>charin.dat</c> skill.</summary>
    public const string SkillCode = "SKL";

    /// <summary>Signature body for an <c>itemsin.dat</c> row.</summary>
    public const string ItemCode = "ITM";

    /// <summary>Signature body for a <c>spellsin.dat</c> row.</summary>
    public const string SpellCode = "SPL";

    /// <summary>Signature body for a <c>monstersin.dat</c> row.</summary>
    public const string MonsterCode = "MON";

    /// <summary>Signature body for a <c>droppeditemsin.dat</c> row.</summary>
    public const string DropCode = "DRP";

    /// <summary>Signature body for a <c>geomin.dat</c> dungeon row.</summary>
    public const string DungeonCode = "GEO";

    /// <summary>Signature body for a <c>helptext.dat</c> line (Dawnstar only).</summary>
    public const string HelpCode = "HLP";

    /// <summary>Signature body for a <c>monsterfilenamesin.dat</c> sprite family.</summary>
    public const string SpriteCode = "SET";

    /// <summary>Signature body for an <c>npcstrings.dat</c> line.</summary>
    public const string StringCode = "NPC";

    private const int ClassTableId = 0x01;
    private const int RaceTableId = 0x02;
    private const int SkillTableId = 0x03;
    private const int ItemTableId = 0x04;
    private const int SpellTableId = 0x05;
    private const int MonsterTableId = 0x06;
    private const int DropTableId = 0x07;
    private const int DungeonTableId = 0x08;
    private const int HelpTableId = 0x09;
    private const int SpriteTableId = 0x0A;

    private const string CharacterFile = "charin.dat";
    private const string ItemFile = "itemsin.dat";
    private const string SpellFile = "spellsin.dat";
    private const string MonsterFile = "monstersin.dat";
    private const string SpriteFile = "monsterfilenamesin.dat";
    private const string DropFile = "droppeditemsin.dat";
    private const string DungeonFile = "geomin.dat";
    private const string DungeonNameFile = "dungnamesin.dat";
    private const string HelpFile = "helptext.dat";
    private const string StringFile = "npcstrings.dat";

    /// <summary>Lines a single <c>npcstrings.dat</c> group may hold before its key would alias.</summary>
    private const int MaxStringsPerGroup = 0x100;

    /// <summary>Loot columns whose meaning was measured; another width falls back to Column&lt;n&gt;.</summary>
    private static readonly string[] DropColumnNames =
        ["MagicItem", "ScrollItem", "ScrollSpell", "ArmourItem", "WeaponItem"];

    /// <summary>
    ///     Reads every table the install ships and appends one record per row. A table that is
    ///     absent leaves the collection short — the analyzer runs on whatever an install actually
    ///     holds, and Stormhold and Dawnstar each ship one table the other does not.
    /// </summary>
    /// <param name="install">The mounted install: the JAR, or the directory it was unpacked into.</param>
    /// <param name="records">Collection the synthesized records are appended to.</param>
    /// <param name="signaturePrefix">
    ///     One letter naming the game — <c>S</c> for Stormhold, <c>D</c> for Dawnstar — prepended
    ///     to each three-letter code above to form the four-character record signature.
    /// </param>
    /// <param name="firstDomain">
    ///     The game's first reserved <see cref="ClassicFormIdScheme" /> domain, which takes the
    ///     table rows; <c>npcstrings.dat</c> lines land in <c>firstDomain + 1</c>.
    /// </param>
    /// <param name="lumpName">
    ///     The <c>.lmp</c> holding the tables when the game packs them (Dawnstar's
    ///     <c>datfiles.lmp</c>), or null when they are all loose (Stormhold).
    /// </param>
    /// <param name="cancellationToken">Cancellation for the per-row loops.</param>
    public static void Populate(
        IGameFileSystem install,
        RecordCollection records,
        string signaturePrefix,
        byte firstDomain,
        string? lumpName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(install);
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(signaturePrefix);

        var lump = OpenLump(install, lumpName);
        var built = new List<GenericEsmRecord>();
        var context = new TravelsRecordContext(signaturePrefix, firstDomain, built);

        var characters = ReadTable(install, lump, CharacterFile) is { } charin
            ? TravelsCharacterTable.Parse(charin.Span, CharacterFile)
            : null;
        var items = ReadTable(install, lump, ItemFile) is { } itemsin
            ? TravelsItemTable.Parse(itemsin.Span, ItemFile)
            : null;

        AddCharacterRecords(context, characters, cancellationToken);
        AddItemRecords(context, items, cancellationToken);

        if (ReadTable(install, lump, SpellFile) is { } spellsin)
        {
            AddSpellRecords(context, TravelsSpellTable.Parse(spellsin.Span, SpellFile), characters, cancellationToken);
        }

        if (ReadTable(install, lump, MonsterFile) is { } monstersin)
        {
            AddMonsterRecords(context, TravelsMonsterTable.Parse(monstersin.Span, MonsterFile), cancellationToken);
        }

        if (ReadTable(install, lump, SpriteFile) is { } spritesin)
        {
            AddSpriteRecords(context, TravelsMonsterSpriteTable.Parse(spritesin.Span, SpriteFile), cancellationToken);
        }

        if (ReadTable(install, lump, DropFile) is { } dropsin)
        {
            AddDropRecords(context, TravelsDropTable.Parse(dropsin.Span, DropFile), items, cancellationToken);
        }

        AddDungeonRecords(context, install, lump, cancellationToken);

        if (ReadTable(install, lump, HelpFile) is { } helptext)
        {
            AddHelpRecords(context, TravelsHelpTextTable.Parse(helptext.Span, HelpFile), cancellationToken);
        }

        if (ReadTable(install, lump, StringFile) is { } npcstrings)
        {
            AddStringRecords(context, TravelsNpcStringTable.Parse(npcstrings.Span, StringFile), cancellationToken);
        }

        AssertUniqueFormIds(built, signaturePrefix);
        records.GenericRecords.AddRange(built);
    }

    /// <summary>
    ///     Opens the game's table lump when it ships one and the mount did not already flatten it.
    ///     A lump that fails to parse is a real defect and its <see cref="InvalidDataException" />
    ///     propagates; a lump that is simply absent is not (Stormhold has none, and an unpacked
    ///     Dawnstar install serves the members directly).
    /// </summary>
    private static DawnstarLumpArchive? OpenLump(IGameFileSystem install, string? lumpName)
    {
        if (lumpName is null)
        {
            return null;
        }

        var bytes = install.TryReadAllBytes(lumpName);
        return bytes is null ? null : DawnstarLumpArchive.Parse(bytes, lumpName);
    }

    /// <summary>
    ///     The bytes of one table: the copy the mount serves when it has one, else the lump member
    ///     of that name, else null.
    /// </summary>
    private static ReadOnlyMemory<byte>? ReadTable(IGameFileSystem install, DawnstarLumpArchive? lump, string name)
    {
        if (install.TryReadAllBytes(name) is { } served)
        {
            return served;
        }

        if (lump is not null && lump.TryGetEntry(name, out var entry))
        {
            return lump.Read(entry);
        }

        return null;
    }

    private static void AddCharacterRecords(
        TravelsRecordContext context, TravelsCharacterTable? characters, CancellationToken cancellationToken)
    {
        if (characters is null)
        {
            return;
        }

        foreach (var row in characters.Classes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            context.Add(BuildClassRecord(context, characters, row));
        }

        for (var i = 0; i < characters.RaceNames.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            context.Add(BuildRaceRecord(context, characters, i));
        }

        for (var i = 0; i < characters.SkillNames.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            context.Add(BuildSkillRecord(context, characters, i));
        }
    }

    /// <summary>
    ///     One playable class. Identity is the class ordinal, which the row also carries in its
    ///     column 0 and which the save game stores.
    /// </summary>
    private static GenericEsmRecord BuildClassRecord(
        TravelsRecordContext context, TravelsCharacterTable characters, TravelsClassRow row)
    {
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Index"] = row.Index,
            ["DefaultRaceIndex"] = (int)row.DefaultRaceIndex,
            ["DefaultRace"] = NameAt(characters.RaceNames, row.DefaultRaceIndex),
            ["MagickaMultiplier"] = (int)row.MagickaMultiplier,
            ["Field11"] = (int)row.Field11,
            ["Field12"] = (int)row.Field12
        };

        for (var a = 0; a < row.AttributeBases.Length; a++)
        {
            fields["Attribute_" + AttributeName(characters, a)] = (int)row.AttributeBases[a];
        }

        var known = 0;
        for (var s = 0; s < row.Skills.Length; s++)
        {
            var skill = row.Skills[s];
            if (skill.Rank > 0)
            {
                known++;
            }

            var skillName = NameAt(characters.SkillNames, s) ?? Ordinal(s);
            fields["Skill_" + ClassicRecordNaming.ToEditorId(skillName)] =
                string.Create(CultureInfo.InvariantCulture, $"rank {skill.Rank}, {skill.BasePercent}%");
        }

        fields["KnownSkills"] = known;

        return context.Create(
            ClassCode, ClassTableId, row.Index, "CLASS_" + row.Name, row.Name, fields);
    }

    /// <summary>One playable race — a name in the fourth list, joined with the classes defaulting to it.</summary>
    private static GenericEsmRecord BuildRaceRecord(
        TravelsRecordContext context, TravelsCharacterTable characters, int index)
    {
        var name = characters.RaceNames[index];
        var defaultFor = characters.Classes
            .Where(c => c.DefaultRaceIndex == index)
            .Select(c => c.Name)
            .ToList();

        var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Index"] = index,
            ["DefaultForClasses"] = string.Join(", ", defaultFor),
            ["DefaultForClassCount"] = defaultFor.Count
        };

        return context.Create(RaceCode, RaceTableId, index, "RACE_" + name, name, fields);
    }

    /// <summary>
    ///     One skill: its name, the attribute that governs it (an EVEN index into the attribute
    ///     label list, which alternates "&lt;Attr&gt;" with "&lt;Attr&gt; Increases"), and the
    ///     classes that start out knowing it.
    /// </summary>
    private static GenericEsmRecord BuildSkillRecord(
        TravelsRecordContext context, TravelsCharacterTable characters, int index)
    {
        var name = characters.SkillNames[index];
        var governing = index < characters.GoverningAttributes.Length
            ? characters.GoverningAttributes[index]
            : (short)-1;

        var taught = characters.Classes
            .Where(c => index < c.Skills.Length && c.Skills[index].Rank > 0)
            .Select(c => c.Name)
            .ToList();

        var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Index"] = index,
            ["GoverningAttributeIndex"] = (int)governing,
            ["GoverningAttribute"] = NameAt(characters.AttributeLabels, governing),
            ["TaughtToClasses"] = string.Join(", ", taught),
            ["TaughtToClassCount"] = taught.Count
        };

        return context.Create(SkillCode, SkillTableId, index, "SKILL_" + name, name, fields);
    }

    private static void AddItemRecords(
        TravelsRecordContext context, TravelsItemTable? items, CancellationToken cancellationToken)
    {
        if (items is null)
        {
            return;
        }

        foreach (var item in items.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            context.Add(BuildItemRecord(context, item));
        }
    }

    /// <summary>
    ///     One item. Identity is the 1-based id every other table references, stored as
    ///     <c>Id - 1</c> so the composite index is the row ordinal.
    /// </summary>
    private static GenericEsmRecord BuildItemRecord(TravelsRecordContext context, TravelsItem item)
    {
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Id"] = item.Id,
            ["Type"] = item.TypeName,
            ["TypeIndex"] = (int)item.TypeIndex,
            ["Tier"] = (int)item.Tier,
            ["Power"] = (int)item.Power,
            ["Value"] = (int)item.Value,
            ["Value35"] = (int)item.Value35,
            ["Slot"] = (int)item.Slot,
            ["Equippable"] = item.Slot >= 0
        };

        return context.Create(ItemCode, ItemTableId, item.Id - 1, "ITEM_" + item.Name, item.Name, fields);
    }

    private static void AddSpellRecords(
        TravelsRecordContext context,
        TravelsSpellTable spells,
        TravelsCharacterTable? characters,
        CancellationToken cancellationToken)
    {
        foreach (var spell in spells.Spells)
        {
            cancellationToken.ThrowIfCancellationRequested();
            context.Add(BuildSpellRecord(context, spell, characters));
        }
    }

    /// <summary>
    ///     One spell. Its school column is a 0-based index into <c>charin.dat</c>'s skill list, so
    ///     the school NAME is only available when that table was readable too.
    /// </summary>
    private static GenericEsmRecord BuildSpellRecord(
        TravelsRecordContext context, TravelsSpell spell, TravelsCharacterTable? characters)
    {
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Id"] = spell.Id,
            ["SchoolSkillIndex"] = (int)spell.SchoolSkill,
            ["School"] = characters is null ? null : NameAt(characters.SkillNames, spell.SchoolSkill),
            ["Cost"] = (int)spell.Cost,
            ["Duration"] = (int)spell.Duration,
            ["Target"] = (int)spell.Target,
            ["BaseChance"] = (int)spell.BaseChance,
            ["RankRequired"] = (int)spell.RankRequired,
            ["Description"] = spell.Description
        };

        return context.Create(SpellCode, SpellTableId, spell.Id - 1, "SPELL_" + spell.Name, spell.Name, fields);
    }

    private static void AddMonsterRecords(
        TravelsRecordContext context, TravelsMonsterTable monsters, CancellationToken cancellationToken)
    {
        foreach (var monster in monsters.Monsters)
        {
            cancellationToken.ThrowIfCancellationRequested();
            context.Add(BuildMonsterRecord(context, monster));
        }
    }

    /// <summary>
    ///     One monster. Only the four stat columns the reader is willing to name are broken out;
    ///     the rest are carried verbatim in <c>Stats</c> rather than given invented names — their
    ///     meaning is a hypothesis, and a wrong field name outlives a missing one.
    /// </summary>
    private static GenericEsmRecord BuildMonsterRecord(TravelsRecordContext context, TravelsMonster monster)
    {
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Id"] = monster.Id,
            ["Family"] = (int)monster.Family,
            ["HitPoints"] = (int)monster.HitPoints,
            ["DropChance"] = (int)monster.DropChance,
            ["LootRolls"] = (int)monster.LootRolls,
            ["Stats"] = string.Join(", ", monster.Stats.Select(stat => Ordinal(stat)))
        };

        return context.Create(
            MonsterCode, MonsterTableId, monster.Id - 1, "MONSTER_" + monster.Name, monster.Name, fields);
    }

    private static void AddSpriteRecords(
        TravelsRecordContext context, TravelsMonsterSpriteTable sprites, CancellationToken cancellationToken)
    {
        for (var family = 0; family < sprites.Families.Length; family++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            context.Add(BuildSpriteRecord(context, family, sprites.Families[family]));
        }
    }

    /// <summary>
    ///     One sprite family: the seven part resources the engine loads as a graphics chunk. Unused
    ///     parts are empty strings that keep the 5 x 7 grid square, so the record reports how many
    ///     of the seven are live rather than dropping the empties and losing the slot numbering.
    /// </summary>
    private static GenericEsmRecord BuildSpriteRecord(
        TravelsRecordContext context, int family, ImmutableArray<string> parts)
    {
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Family"] = family,
            ["PartsUsed"] = parts.Count(p => p.Length > 0)
        };

        for (var p = 0; p < parts.Length; p++)
        {
            fields["Part" + Ordinal(p)] = parts[p];
        }

        var first = parts.FirstOrDefault(p => p.Length > 0);
        var stem = string.IsNullOrEmpty(first) ? null : Path.GetFileNameWithoutExtension(first);
        var editorId = "SPRITEFAMILY" + Ordinal(family) + (stem is null ? "" : "_" + stem);

        return context.Create(SpriteCode, SpriteTableId, family, editorId, stem, fields);
    }

    private static void AddDropRecords(
        TravelsRecordContext context,
        TravelsDropTable drops,
        TravelsItemTable? items,
        CancellationToken cancellationToken)
    {
        for (var row = 0; row < drops.Rows.Length; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            context.Add(BuildDropRecord(context, row, drops.Rows[row], items));
        }
    }

    /// <summary>
    ///     One loot row — the graded band the game indexes by depth. Cells are 1-based item ids
    ///     (column 2 a 1-based spell id), so they are resolved against <c>itemsin.dat</c> when it
    ///     was readable. A table of another width falls back to numbered columns rather than
    ///     forcing the five measured names onto it.
    /// </summary>
    private static GenericEsmRecord BuildDropRecord(
        TravelsRecordContext context, int row, ImmutableArray<byte> cells, TravelsItemTable? items)
    {
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal) { ["Index"] = row };
        var named = cells.Length == DropColumnNames.Length;

        for (var c = 0; c < cells.Length; c++)
        {
            var key = named ? DropColumnNames[c] : "Column" + Ordinal(c);
            fields[key] = (int)cells[c];

            // Column 2 is a SPELL id, not an item id — resolving it against the item table would
            // name the wrong thing, so it stays a number.
            if (named && c != 2 && ItemName(items, cells[c]) is { } itemName)
            {
                fields[key + "Name"] = itemName;
            }
        }

        var fullName = named ? Join(ItemName(items, cells[4]), ItemName(items, cells[3])) : null;
        var editorId = "LOOTROW" + row.ToString("D2", CultureInfo.InvariantCulture);

        return context.Create(DropCode, DropTableId, row, editorId, fullName, fields);
    }

    /// <summary>
    ///     Reads the dungeon graph and, when the game ships one, the name pairs — two separate
    ///     files joined on the 1-based dungeon id.
    /// </summary>
    private static void AddDungeonRecords(
        TravelsRecordContext context,
        IGameFileSystem install,
        DawnstarLumpArchive? lump,
        CancellationToken cancellationToken)
    {
        if (ReadTable(install, lump, DungeonFile) is not { } geomin)
        {
            return;
        }

        var geometry = TravelsGeometryTable.Parse(geomin.Span, DungeonFile);
        var names = ReadTable(install, lump, DungeonNameFile) is { } dungnames
            ? TravelsDungeonNameTable.Parse(dungnames.Span, DungeonNameFile)
            : null;

        foreach (var dungeon in geometry.Dungeons)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = names?.Names.FirstOrDefault(n => n.Id == dungeon.Id);
            context.Add(BuildDungeonRecord(context, dungeon, name));
        }
    }

    /// <summary>
    ///     One dungeon: its four neighbour links and its two doorway direction codes, joined with
    ///     the two display lines <c>dungnamesin.dat</c> gives it. Dawnstar ships no such file, so
    ///     its dungeons are genuinely nameless here — those names are believed to live in code.
    /// </summary>
    private static GenericEsmRecord BuildDungeonRecord(
        TravelsRecordContext context, TravelsDungeonLink dungeon, TravelsDungeonName? name)
    {
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Id"] = dungeon.Id,
            ["North"] = (int)dungeon.North,
            ["East"] = (int)dungeon.East,
            ["South"] = (int)dungeon.South,
            ["West"] = (int)dungeon.West,
            ["ExitDirection"] = (int)dungeon.ExitDirection,
            ["ReturnDirection"] = (int)dungeon.ReturnDirection,

            // Row 0 (id 1) is the camp: it links to four dungeons but has no onward/return door.
            ["IsCamp"] = dungeon.Id == 1
        };

        string? fullName = null;
        if (name is not null)
        {
            fields["NameLine1"] = name.FirstLine;
            fields["NameLine2"] = name.SecondLine;
            fullName = Join(name.FirstLine, name.SecondLine);
        }

        var editorId = "DUNGEON" + dungeon.Id.ToString("D2", CultureInfo.InvariantCulture)
                                 + (fullName is null ? "" : "_" + ClassicRecordNaming.ToEditorId(fullName));

        return context.Create(DungeonCode, DungeonTableId, dungeon.Id - 1, editorId, fullName, fields);
    }

    private static void AddHelpRecords(
        TravelsRecordContext context, TravelsHelpTextTable help, CancellationToken cancellationToken)
    {
        for (var i = 0; i < help.Lines.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = help.Lines[i];
            var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["Index"] = i,
                ["Length"] = text.Length,
                ["Text"] = text
            };

            context.Add(context.Create(
                HelpCode,
                HelpTableId,
                i,
                "HELP" + i.ToString("D2", CultureInfo.InvariantCulture),
                ClassicRecordNaming.Summarize(text),
                fields));
        }
    }

    /// <summary>
    ///     One record per line of <c>npcstrings.dat</c>. The file is a run of groups — one per NPC
    ///     in code order, then the generic/system set — so a line's identity is its
    ///     <c>(group, line)</c> pair, packed as <c>group &lt;&lt; 8 | line</c>. That packing needs a
    ///     group to hold at most 256 lines, which retail satisfies with room to spare (77 is the
    ///     largest), and the guard says so out loud rather than silently aliasing two lines.
    /// </summary>
    private static void AddStringRecords(
        TravelsRecordContext context, TravelsNpcStringTable strings, CancellationToken cancellationToken)
    {
        for (var group = 0; group < strings.Groups.Length; group++)
        {
            var lines = strings.Groups[group];
            if (lines.Length > MaxStringsPerGroup)
            {
                throw new InvalidDataException(
                    $"'{StringFile}': group {group} holds {lines.Length} lines; the "
                    + $"'group << 8 | line' record key allows at most {MaxStringsPerGroup}.");
            }

            for (var line = 0; line < lines.Length; line++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                context.Add(BuildStringRecord(context, group, line, lines[line]));
            }
        }
    }

    private static GenericEsmRecord BuildStringRecord(
        TravelsRecordContext context, int group, int line, string text)
    {
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Group"] = group,
            ["Line"] = line,
            ["Length"] = text.Length,
            ["Text"] = text
        };

        return new GenericEsmRecord
        {
            FormId = ClassicFormIdScheme.Compose(context.StringDomain, (uint)((group << 8) | line)),
            RecordType = context.Signature(StringCode),
            EditorId = string.Create(CultureInfo.InvariantCulture, $"NPCSTR_G{group}_L{line:D2}"),
            FullName = ClassicRecordNaming.Summarize(text),
            Fields = fields
        };
    }

    /// <summary>
    ///     Nothing here is hashed — every index is a composite of a table id and a row ordinal — so
    ///     a duplicate FormID means a synthesizer bug, never a name collision. Renumbering would
    ///     hide it and break the diff the ids exist for, so it is fatal.
    /// </summary>
    private static void AssertUniqueFormIds(List<GenericEsmRecord> built, string signaturePrefix)
    {
        var seen = new Dictionary<uint, GenericEsmRecord>();
        foreach (var record in built)
        {
            if (seen.TryGetValue(record.FormId, out var previous))
            {
                throw new InvalidOperationException(
                    $"Travels record source '{signaturePrefix}' produced two records with FormID "
                    + $"0x{record.FormId:X8}: '{previous.EditorId}' ({previous.RecordType}) and "
                    + $"'{record.EditorId}' ({record.RecordType}).");
            }

            seen[record.FormId] = record;
        }
    }

    /// <summary>The attribute label list alternates "&lt;Attr&gt;" with "&lt;Attr&gt; Increases".</summary>
    private static string AttributeName(TravelsCharacterTable characters, int attribute)
    {
        return NameAt(characters.AttributeLabels, 2 * attribute) is { } label
            ? ClassicRecordNaming.ToEditorId(label)
            : Ordinal(attribute);
    }

    private static string? NameAt(ImmutableArray<string> names, int index)
    {
        return index >= 0 && index < names.Length ? names[index] : null;
    }

    private static string? ItemName(TravelsItemTable? items, int id)
    {
        return items is not null && id >= 1 && id <= items.Items.Length ? items.Items[id - 1].Name : null;
    }

    private static string? Join(string? first, string? second)
    {
        if (string.IsNullOrEmpty(first))
        {
            return string.IsNullOrEmpty(second) ? null : second;
        }

        return string.IsNullOrEmpty(second) ? first : first + " " + second;
    }

    private static string Ordinal(int value)
    {
        return value.ToString(CultureInfo.InvariantCulture);
    }
}
