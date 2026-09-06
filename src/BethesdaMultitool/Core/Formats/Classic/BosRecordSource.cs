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
///         Two record types, mirroring the shape the data actually has:
///         <list type="bullet">
///             <item>
///                 <c>BOSD</c> — one per definition in <c>DATA\ALL.DDF</c>, the MASTER table. This
///                 is the game's object catalogue: actors, weapons, items, traps, lights, AI
///                 behaviours (see <see cref="BosDataFile.DescribeType" />).
///             </item>
///             <item><c>BOSL</c> — one per level file, carrying its per-class census.</item>
///         </list>
///     </para>
///     <para>
///         ⚑ Why the master and not every record: measured over the 54 per-level files, <b>40,365
///         of their 40,763 records are BYTE-IDENTICAL to the master's copy</b> (99.0%), and every
///         per-level hash is present in the master (0 absent). Emitting all 43,006 would be the
///         same 2,243 definitions repeated. The 1% that genuinely differ — 295 actors, 54 traps, 36
///         level records, 13 world objects — are counted per level on the <c>BOSL</c> record rather
///         than dropped silently.
///     </para>
///     <para>
///         ⚠ Names come from the SDBs, and a level's own database is authoritative for its own
///         records (7,383/7,383 resolve). The master has no database of its own, so its names come
///         from the union of all 56 — and <b>115 hashes carry different text in different
///         databases</b>. Where the union disagrees the name is left NULL rather than a coin-flip:
///         an unnamed record is honest, a wrongly-named one is not.
///     </para>
/// </summary>
internal static class BosRecordSource
{
    /// <summary>Domain byte for the master object definitions.</summary>
    public const byte DefinitionDomain = 0x60;

    /// <summary>Domain byte for the level records.</summary>
    public const byte LevelDomain = 0x61;

    /// <summary>Record type for one master object definition.</summary>
    public const string DefinitionRecordType = "BOSD";

    /// <summary>Record type for one level.</summary>
    public const string LevelRecordType = "BOSL";

    /// <summary>The master table every per-level file draws its records from.</summary>
    public const string MasterPath = @"DATA\ALL.DDF";

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

        foreach (var record in master.Value.File.Records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            records.GenericRecords.Add(BuildDefinition(record, names, references.GetValueOrDefault(record.Hash)));
        }
    }

    /// <summary>The master table and its bytes, or null when the disc does not carry one.</summary>
    private static (BosDataFile File, byte[] Bytes)? ReadMaster(
        IGameFileSystem install, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = install.TryReadAllBytes(MasterPath);
        return bytes is not null && BosDataFile.TryParse(bytes, MasterPath, out var master, out _)
            ? (master, bytes)
            : null;
    }

    /// <summary>
    ///     Hash to name over every <c>.SDB</c> on the disc. A hash whose text DISAGREES between
    ///     databases is mapped to null and stays that way, so an ambiguous name is reported as
    ///     absent rather than resolved arbitrarily. Measured 2026-09-06: 5,520 distinct hashes,
    ///     115 of them ambiguous.
    /// </summary>
    private static Dictionary<uint, string?> ReadNames(IGameFileSystem install, CancellationToken cancellationToken)
    {
        var names = new Dictionary<uint, string?>();
        foreach (var entry in install.EnumerateFiles()
                     .Where(e => e.Path.EndsWith(".SDB", StringComparison.OrdinalIgnoreCase)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = install.TryReadAllBytes(entry.Path);
            if (bytes is null || !BosStringDatabase.TryParse(bytes, entry.Path, out var database, out _))
            {
                continue;
            }

            foreach (var text in database.Entries)
            {
                if (names.TryGetValue(text.Hash, out var existing))
                {
                    if (!string.Equals(existing, text.Value, StringComparison.Ordinal))
                    {
                        names[text.Hash] = null;
                    }
                }
                else
                {
                    names[text.Hash] = text.Value;
                }
            }
        }

        return names;
    }

    /// <summary>The record form of one master definition.</summary>
    private static GenericEsmRecord BuildDefinition(
        BosDataRecord record, IReadOnlyDictionary<uint, string?> names, int levels)
    {
        names.TryGetValue(record.Hash, out var name);
        var described = BosDataFile.DescribeType(record.Type);

        var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Class"] = described ?? $"Type {record.Type}",
            ["TypeCode"] = record.Type,
            ["Hash"] = $"0x{record.Hash:X8}",
            ["Size"] = record.Size,
            ["LevelsUsing"] = levels
        };

        // The length the payload declares agrees with the directory on 9 of the 13 classes and not
        // on the other four, so it is surfaced only where it disagrees — that is the interesting
        // case, and printing a redundant equal value on 39,855 records would bury it.
        if (record.DeclaredLength != (uint)record.Size)
        {
            fields["DeclaredLength"] = record.DeclaredLength;
        }

        return new GenericEsmRecord
        {
            FormId = ClassicFormIdScheme.Compose(DefinitionDomain, (uint)record.Index),
            RecordType = DefinitionRecordType,
            EditorId = ClassicRecordNaming.ToEditorId($"BOS_{record.Type:D2}_{record.Index:D5}"),
            FullName = name is null ? null : ClassicRecordNaming.Summarize(name),
            Fields = fields
        };
    }

    /// <summary>The record form of one level file.</summary>
    private static GenericEsmRecord BuildLevel(
        string path,
        BosDataFile level,
        byte[] bytes,
        (BosDataFile File, byte[] Bytes)? master,
        IReadOnlyDictionary<uint, string?> names)
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
            .Select(r => names.GetValueOrDefault(r.Hash))
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
