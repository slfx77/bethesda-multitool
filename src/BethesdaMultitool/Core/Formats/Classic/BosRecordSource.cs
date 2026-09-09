using BethesdaMultitool.Core.Formats.BrotherhoodOfSteel;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Vfs;

namespace BethesdaMultitool.Core.Formats.Classic;

/// <summary>
///     Synthesizes records for Fallout: Brotherhood of Steel from the disc's <c>.DDF</c> record
///     stores and <c>.SDB</c> string databases. Original RE 2026-09-06; needs no <c>.CLP</c>, whose
///     payload encoding is still open.
///     <para>
///         Three record types, mirroring the shape the data actually has:
///         <list type="bullet">
///             <item>
///                 <c>BOSD</c> — one per definition in <c>DATA\ALL.DDF</c>, the MASTER table. This
///                 is the game's object catalogue: actors, weapons, items, traps, lights, AI
///                 behaviours (see <see cref="BosDataFile.DescribeType" />).
///             </item>
///             <item><c>BOSL</c> — one per level file, carrying its per-class census.</item>
///             <item>
///                 <c>BOST</c> — one per authored string that names no record. ⚑ Those 3,933 are
///                 the game's DIALOGUE and UI prose ("Locked", "Chapter 1 - Cyrus", whole spoken
///                 lines): median 43 characters, 2,075 of them over 40.
///             </item>
///         </list>
///     </para>
///     <para>
///         ⚑ Why the master and not every record: measured over the 54 per-level files,
///         <b>
///             40,365
///             of their 40,763 records are BYTE-IDENTICAL to the master's copy
///         </b>
///         (99.0%), and every
///         per-level hash is present in the master (0 absent). Emitting all 43,006 would be the
///         same 2,243 definitions repeated. The 1% that genuinely differ — 295 actors, 54 traps, 36
///         level records, 13 world objects — are counted per level on the <c>BOSL</c> record rather
///         than dropped silently.
///     </para>
///     <para>
///         ⚑⚑ <b>A record's name is PROVEN, not assumed (2026-09-08).</b> The SDB key function is
///         <see cref="BosNameHash" /> (<c>default.xbe</c> <c>0x00014BE0</c>), so a string can be
///         tested against the key it sits under: when <c>BosNameHash.Compute(text) == key</c> the
///         string IS the record's internal name, and when it is not it is a DISPLAY string filed
///         under that name's key. The two are visibly the two halves of one record — key
///         <c>0x00CE132C</c> holds the name <c>creature_radscorpion_glowing</c> and the display
///         text <c>Glowing Radscorpion</c>; <c>0x048F845F</c> holds <c>cain_armor_power_torso</c>
///         and <c>Power Armor</c>. The proven name becomes the EditorID (previously a synthetic
///         <c>BOS_&lt;type&gt;_&lt;index&gt;</c>) and the display string the FullName.
///     </para>
///     <para>
///         ⚑ Measured over the 2,243 master records on BOTH discs. <b>PS2</b>: 1,524 named before,
///         now 1,035 proven names + 551 display-only = <b>1,586</b>. <b>Xbox</b>: 1,920 before, now
///         1,775 proven + 396 display-only = <b>2,171</b>, leaving just <b>72</b> records unnamed.
///         (⛔ Those two splits were published as 613 and 398 — neither of which adds up to the
///         total beside it. Re-measured 2026-09-08 over all 61 Xbox <c>.SDB</c> plus
///         <c>all.ddf</c>: 396, and 551 for the PS2.)
///     </para>
///     <para>
///         ⚑ <b>The Xbox jump is that <c>deftexte.sdb</c> EXISTS</b>, not that it is read first.
///         Control, run over the affected population — the same algorithm three ways on the Xbox
///         tree: with the file removed entirely, 1,585 named / 1,035 proven / 658 unnamed, i.e.
///         essentially the PS2's figures; with it present but in plain path order, 2,158 / 85
///         unnamed; with it read first and authoritative, 2,171 / 72. The table is a 2,020-entry
///         default-text index the PS2 disc does not ship, covering 2,017 of the 2,242 distinct
///         master keys.
///     </para>
///     <para>
///         ⚠ Where two databases give a key different display text and <c>deftexte.sdb</c> has not
///         settled it, the display name is left NULL rather than a coin-flip: an unnamed record is
///         honest, a wrongly-named one is not. A PROVEN name needs no such rule — a second string
///         hashing to the same key would be a collision, and there is none on either disc.
///     </para>
/// </summary>
internal static class BosRecordSource
{
    /// <summary>Domain byte for the master object definitions.</summary>
    public const byte DefinitionDomain = 0x60;

    /// <summary>Domain byte for the level records.</summary>
    public const byte LevelDomain = 0x61;

    /// <summary>Domain byte for the authored text records.</summary>
    public const byte TextDomain = 0x62;

    /// <summary>Record type for one master object definition.</summary>
    public const string DefinitionRecordType = "BOSD";

    /// <summary>Record type for one level.</summary>
    public const string LevelRecordType = "BOSL";

    /// <summary>Record type for one authored string — dialogue and UI text.</summary>
    public const string TextRecordType = "BOST";

    /// <summary>The master table every per-level file draws its records from, on the PS2 disc.</summary>
    public const string MasterPath = @"DATA\ALL.DDF";

    /// <summary>
    ///     The same master table on the Xbox disc, which puts the whole data tree under
    ///     <c>resx\</c> (levels under <c>resx\c1</c>…<c>resx\c4</c>) instead of <c>DATA\</c>.
    ///     Everything else here already finds its inputs by ENUMERATING the mount for
    ///     <c>.DDF</c>/<c>.SDB</c>, so the master is the only path this source spells out and the
    ///     only thing the second layout needs.
    /// </summary>
    public const string XboxMasterPath = @"resx\all.ddf";

    /// <summary>
    ///     The Xbox-only default-text database. It is read BEFORE every other <c>.SDB</c> and its
    ///     display text is authoritative, because it is the one table authored as a name index
    ///     rather than as one level's strings: 2,020 entries covering 2,017 of the 2,242 distinct
    ///     master keys.
    ///     <para>
    ///         ⚑ <b>What the PRECEDENCE is worth, measured rather than asserted</b> (2026-09-08,
    ///         same algorithm, same 61 databases, only the order changed): reading it first and
    ///         letting it settle a key gives 2,171 named / 72 unnamed and clears NO master key's
    ///         display text; plain path order gives 2,158 / 85 and clears 13. So the rule is worth
    ///         <b>13 records</b> — worth having, and no more than that.
    ///         ⛔ The rationale published here on 2026-09-08 — "the 379 keys whose level databases
    ///         disagree would be cleared and 72 unnamed records would become several hundred" — is
    ///         REFUTED on both halves. 379 is not reproducible anywhere in this corpus (the master
    ///         keys contested between the level databases number 2), and the several-hundred jump
    ///         belongs to the file's EXISTENCE, not its position: delete <c>deftexte.sdb</c> and
    ///         the tree names 1,585 with 658 unnamed.
    ///     </para>
    /// </summary>
    public const string DefaultTextDatabase = "deftexte.sdb";

    /// <summary>Master-table locations probed, in disc order (PS2 first — it is the older release).</summary>
    private static readonly string[] MasterPaths = [MasterPath, XboxMasterPath];

    /// <summary>
    ///     Reads the disc's record stores and appends a record per master definition and per level.
    /// </summary>
    public static void Populate(
        IGameFileSystem install,
        RecordCollection records,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(install);
        ArgumentNullException.ThrowIfNull(records);

        var dataFiles = install.EnumerateFiles()
            .Where(e => e.Path.EndsWith(".DDF", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (dataFiles.Count == 0)
        {
            return;
        }

        var names = ReadNames(install, cancellationToken);
        var master = ReadMaster(install, cancellationToken);

        // Per-level pass first: it both emits the level records and counts how many levels use each
        // definition, which is the reference count the definition records carry.
        var references = new Dictionary<uint, int>();
        foreach (var entry in dataFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Path.EndsWith(@"\ALL.DDF", StringComparison.OrdinalIgnoreCase) ||
                entry.Path.Equals("ALL.DDF", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var bytes = install.TryReadAllBytes(entry.Path);
            if (bytes is null || !BosDataFile.TryParse(bytes, entry.Path, out var level, out _))
            {
                continue;
            }

            foreach (var hash in level.Records.Select(r => r.Hash).Distinct())
            {
                references[hash] = references.GetValueOrDefault(hash) + 1;
            }

            records.GenericRecords.Add(BuildLevel(entry.Path, level, bytes, master, names));
        }

        if (master is null)
        {
            return;
        }

        var defined = new HashSet<uint>(master.Value.File.Records.Select(r => r.Hash));
        foreach (var record in master.Value.File.Records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            records.GenericRecords.Add(BuildDefinition(record, names, references.GetValueOrDefault(record.Hash)));
        }

        // ⚑ A string that names NO record is authored TEXT — the game's dialogue and UI prose. 3,933
        // of the 5,520, median 43 characters and 2,075 of them over 40, so most are sentences.
        // ⚑ Testing against the MASTER's hashes is testing against every record there is: no
        // per-level hash is absent from it (measured over all 40,763).
        var authored = new SortedDictionary<uint, string?>();
        foreach (var (hash, value) in names.Proven)
        {
            authored[hash] = value;
        }

        foreach (var (hash, value) in names.Display)
        {
            // The display string is the visible text, so it wins over the internal name here.
            if (value is not null || !authored.ContainsKey(hash))
            {
                authored[hash] = value;
            }
        }

        foreach (var (hash, value) in authored)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (value is not null && !defined.Contains(hash))
            {
                records.GenericRecords.Add(BuildText(hash, value));
            }
        }
    }

    /// <summary>The record form of one authored string.</summary>
    private static GenericEsmRecord BuildText(uint hash, string value)
    {
        // ⚑ The index is the game's OWN hash, truncated — source identity, not enumeration order.
        // Measured 2026-09-06: the low 24 bits are unique across all 5,520 union hashes (ZERO
        // collisions), so nothing is lost by the truncation on this corpus. The retail test pins
        // that, because a future revision with more strings could break it.
        return new GenericEsmRecord
        {
            FormId = ClassicFormIdScheme.Compose(TextDomain, hash & ClassicFormIdScheme.MaxIndex),
            RecordType = TextRecordType,
            EditorId = ClassicRecordNaming.ToEditorId($"BOSTEXT_{hash:X8}"),
            FullName = ClassicRecordNaming.Summarize(value),
            Fields = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["Hash"] = $"0x{hash:X8}",
                ["Length"] = value.Length,
                ["Text"] = ClassicRecordNaming.OneLine(value)
            }
        };
    }

    /// <summary>The master table and its bytes, or null when the disc does not carry one.</summary>
    private static (BosDataFile File, byte[] Bytes)? ReadMaster(
        IGameFileSystem install, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var path in MasterPaths)
        {
            var bytes = install.TryReadAllBytes(path);
            if (bytes is not null && BosDataFile.TryParse(bytes, path, out var master, out _))
            {
                return (master, bytes);
            }
        }

        return null;
    }

    /// <summary>
    ///     Splits every <c>.SDB</c> string on the disc into the two kinds of row the databases
    ///     actually hold, by testing each string against the key it sits under with
    ///     <see cref="BosNameHash" />.
    ///     <list type="bullet">
    ///         <item>
    ///             <b>Proven</b> — <c>BosNameHash.Compute(text) == key</c>, so the string IS the
    ///             record's internal name. 8,758 of the Xbox disc's 14,829 entries and 7,113 of the
    ///             PS2's 12,134 qualify. No key ever gets two different proven names on either
    ///             disc, so first-wins needs no ambiguity rule.
    ///         </item>
    ///         <item>
    ///             <b>Display</b> — everything else: the UI text filed under that name's key
    ///             ("Glowing Radscorpion" under <c>creature_radscorpion_glowing</c>). Here a
    ///             disagreement between databases DOES happen, and clears the entry to null.
    ///         </item>
    ///     </list>
    ///     <see cref="DefaultTextDatabase" /> is read first and its display text is never cleared.
    /// </summary>
    private static (Dictionary<uint, string?> Proven, Dictionary<uint, string?> Display) ReadNames(
        IGameFileSystem install, CancellationToken cancellationToken)
    {
        var proven = new Dictionary<uint, string?>();
        var display = new Dictionary<uint, string?>();
        var settled = new HashSet<uint>();

        // Ordered so the rules below are deterministic rather than dependent on enumeration order,
        // with the default-text table ahead of every level database.
        var databases = install.EnumerateFiles()
            .Where(e => e.Path.EndsWith(".SDB", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => Path.GetFileName(e.Path).Equals(DefaultTextDatabase, StringComparison.OrdinalIgnoreCase)
                ? 0
                : 1)
            .ThenBy(e => e.Path, StringComparer.OrdinalIgnoreCase);

        foreach (var entry in databases)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = install.TryReadAllBytes(entry.Path);
            if (bytes is null || !BosStringDatabase.TryParse(bytes, entry.Path, out var database, out _))
            {
                continue;
            }

            var authoritative = Path.GetFileName(entry.Path)
                .Equals(DefaultTextDatabase, StringComparison.OrdinalIgnoreCase);

            foreach (var text in database.Entries)
            {
                if (BosNameHash.Names(text.Value, text.Hash))
                {
                    // A second string hashing to the same key would be a collision; there is none
                    // on either disc, so the first sighting stands.
                    proven.TryAdd(text.Hash, text.Value);
                    continue;
                }

                if (settled.Contains(text.Hash))
                {
                    continue;
                }

                if (display.TryGetValue(text.Hash, out var existing))
                {
                    if (!string.Equals(existing, text.Value, StringComparison.Ordinal))
                    {
                        display[text.Hash] = null;
                    }
                }
                else
                {
                    display[text.Hash] = text.Value;
                }

                if (authoritative)
                {
                    settled.Add(text.Hash);
                }
            }
        }

        return (proven, display);
    }

    /// <summary>
    ///     The best text for a key: the display string when the disc carries one, otherwise the
    ///     proven internal name, otherwise nothing.
    /// </summary>
    private static string? BestText(
        (Dictionary<uint, string?> Proven, Dictionary<uint, string?> Display) names, uint hash)
    {
        return names.Display.GetValueOrDefault(hash) ?? names.Proven.GetValueOrDefault(hash);
    }

    /// <summary>The record form of one master definition.</summary>
    private static GenericEsmRecord BuildDefinition(
        BosDataRecord record,
        (Dictionary<uint, string?> Proven, Dictionary<uint, string?> Display) names,
        int levels)
    {
        var proven = names.Proven.GetValueOrDefault(record.Hash);
        var display = names.Display.GetValueOrDefault(record.Hash);
        var described = BosDataFile.DescribeType(record.Type);

        var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Class"] = described ?? $"Type {record.Type}",
            ["TypeCode"] = record.Type,
            ["Hash"] = $"0x{record.Hash:X8}",
            ["Size"] = record.Size,
            ["LevelsUsing"] = levels
        };

        // ⚑ Reported separately because they are different facts: Name is the identifier that
        // HASHES to the key, DisplayName is the UI text filed under it. 238 Xbox records carry
        // both (creature_radscorpion_glowing / "Glowing Radscorpion").
        if (proven is not null)
        {
            fields["Name"] = proven;
        }

        if (display is not null)
        {
            fields["DisplayName"] = display;
        }

        // The length the payload declares agrees with the directory on 9 of the 13 classes and not
        // on the other four, so it is surfaced only where it disagrees — that is the interesting
        // case, and printing a redundant equal value on 39,855 records would bury it.
        if (record.DeclaredLength != (uint)record.Size)
        {
            fields["DeclaredLength"] = record.DeclaredLength;
        }

        // ⚑ The EditorID is the engine's own internal name where the hash proves one, and only
        // falls back to the synthetic index for the records no database names.
        var displayName = display ?? proven;
        return new GenericEsmRecord
        {
            FormId = ClassicFormIdScheme.Compose(DefinitionDomain, (uint)record.Index),
            RecordType = DefinitionRecordType,
            EditorId = ClassicRecordNaming.ToEditorId(
                proven ?? $"BOS_{record.Type:D2}_{record.Index:D5}"),
            FullName = displayName is null ? null : ClassicRecordNaming.Summarize(displayName),
            Fields = fields
        };
    }

    /// <summary>The record form of one level file.</summary>
    private static GenericEsmRecord BuildLevel(
        string path,
        BosDataFile level,
        byte[] bytes,
        (BosDataFile File, byte[] Bytes)? master,
        (Dictionary<uint, string?> Proven, Dictionary<uint, string?> Display) names)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["File"] = path,
            ["Records"] = level.Records.Count,
            ["Size"] = bytes.Length
        };

        foreach (var group in level.Records
                     .GroupBy(r => r.Type)
                     .OrderBy(g => g.Key))
        {
            fields[BosDataFile.DescribeType(group.Key) ?? $"Type {group.Key}"] = group.Count();
        }

        // The level's own name is on its type-8 record; where several are present (dev levels ship
        // beside the shipped one) the first is taken and the count reported.
        var levelRecords = level.Records.Where(r => r.Type == 8).ToList();
        var named = levelRecords
            .Select(r => BestText(names, r.Hash))
            .FirstOrDefault(n => n is not null);

        if (master is { } table)
        {
            fields["OverridesMaster"] = CountOverrides(level, bytes, table.File, table.Bytes);
        }

        return new GenericEsmRecord
        {
            FormId = ClassicFormIdScheme.Compose(LevelDomain, ClassicNameHash.Of(path, 24)),
            RecordType = LevelRecordType,
            EditorId = ClassicRecordNaming.ToEditorId(stem),
            FullName = named is null ? stem : ClassicRecordNaming.Summarize(named),
            Fields = fields
        };
    }

    /// <summary>
    ///     How many of a level's records differ in BYTES from the master's copy of the same hash —
    ///     the level's genuinely local content. 398 across the whole disc.
    /// </summary>
    private static int CountOverrides(BosDataFile level, byte[] bytes, BosDataFile master, byte[] masterBytes)
    {
        var byHash = new Dictionary<uint, List<BosDataRecord>>();
        foreach (var record in master.Records)
        {
            if (!byHash.TryGetValue(record.Hash, out var list))
            {
                list = [];
                byHash[record.Hash] = list;
            }

            list.Add(record);
        }

        var overrides = 0;
        foreach (var record in level.Records)
        {
            if (!byHash.TryGetValue(record.Hash, out var candidates))
            {
                overrides++;
                continue;
            }

            var payload = bytes.AsSpan(record.Offset, record.Size);
            var matched = false;
            foreach (var candidate in candidates)
            {
                if (payload.SequenceEqual(masterBytes.AsSpan(candidate.Offset, candidate.Size)))
                {
                    matched = true;
                    break;
                }
            }

            if (!matched)
            {
                overrides++;
            }
        }

        return overrides;
    }
}
