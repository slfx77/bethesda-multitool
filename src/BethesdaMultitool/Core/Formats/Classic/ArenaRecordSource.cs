using System.Globalization;
using BethesdaMultitool.Core.Formats.Arena;
using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;

namespace BethesdaMultitool.Core.Formats.Classic;

/// <summary>
///     Synthesizes browsable records from a TES Arena install, the way the Morrowind parser
///     synthesizes them from a TES3 plugin: everything lands in
///     <c>RecordCollection.GenericRecords</c> with <see cref="ClassicFormIdScheme" /> ids, so
///     <c>stats</c>/<c>list</c>/<c>show</c>/<c>diff</c> and the GUI Records tab work with no
///     further plumbing.
///     <para>
///         Two record types today. <c>ATPL</c> is one entry of TEMPLATE.DAT, the game's string
///         table. <c>AINF</c> is one <c>.INF</c> level-definition file, carrying its texture,
///         flat, sound and on-screen-text inventory. Both come from the same install, and the
///         loose copies in the data directory override their namesakes inside GLOBAL.BSA — which
///         is a real content decision here: three of the five loose .INF files differ from the
///         archived versions.
///     </para>
/// </summary>
internal static class ArenaRecordSource
{
    /// <summary>Domain byte for <c>AINF</c> (level definition) records.</summary>
    public const byte InfDomain = 0x01;

    /// <summary>Domain byte for <c>ATPL</c> (TEMPLATE.DAT string) records.</summary>
    public const byte TemplateDomain = 0x02;

    /// <summary>The record signature used for a parsed .INF file.</summary>
    public const string InfRecordType = "AINF";

    /// <summary>The record signature used for a TEMPLATE.DAT entry.</summary>
    public const string TemplateRecordType = "ATPL";

    /// <summary>Domain byte for <c>ALOC</c> (world-map location) records.</summary>
    public const byte LocationDomain = 0x03;

    /// <summary>Domain byte for <c>APRV</c> (province) records.</summary>
    public const byte ProvinceDomain = 0x04;

    /// <summary>The record signature used for a CITYDATA location.</summary>
    public const string LocationRecordType = "ALOC";

    /// <summary>The record signature used for a CITYDATA province.</summary>
    public const string ProvinceRecordType = "APRV";

    private const string GlobalArchiveName = "GLOBAL.BSA";
    private const string TemplateFileName = "TEMPLATE.DAT";

    /// <summary>
    ///     CITYDATA copies in preference order: the base table, then the new-character template,
    ///     then the swap slot. They share a layout; only their contents differ.
    /// </summary>
    private static readonly string[] CityDataFileNames = ["CITYDATA.00", "CITYDATA.65", "CITYDATA.64"];

    /// <summary>
    ///     Reads <paramref name="root" /> (the ARENA data directory) and appends every synthesized
    ///     record to <paramref name="records" />.
    /// </summary>
    public static void Populate(string root, RecordCollection records, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(records);

        foreach (var (name, bytes) in EnumerateInfFiles(root, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            records.GenericRecords.Add(BuildInfRecord(name, bytes));
        }

        var templatePath = Path.Combine(root, TemplateFileName);
        if (File.Exists(templatePath))
        {
            var template = ArenaTemplateDat.Parse(File.ReadAllBytes(templatePath));
            foreach (var entry in template.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                records.GenericRecords.Add(BuildTemplateRecord(entry));
            }
        }

        if (FindCityData(root) is { } cityDataPath)
        {
            var cityData = ArenaCityDataFile.Parse(File.ReadAllBytes(cityDataPath), Path.GetFileName(cityDataPath));
            foreach (var province in cityData.Provinces)
            {
                cancellationToken.ThrowIfCancellationRequested();
                records.GenericRecords.Add(BuildProvinceRecord(province));

                // Unnamed slots are the random-dungeon placeholders the game fills in during play;
                // emitting them would be 126 empty records saying nothing.
                foreach (var location in province.NamedLocations)
                {
                    records.GenericRecords.Add(BuildLocationRecord(province, location));
                }
            }
        }
    }

    /// <summary>The CITYDATA copy to read, in preference order, or null when none is present.</summary>
    public static string? FindCityData(string root)
    {
        ArgumentNullException.ThrowIfNull(root);

        return CityDataFileNames
            .Select(name => Path.Combine(root, name))
            .FirstOrDefault(File.Exists);
    }

    /// <summary>Builds the record form of one province.</summary>
    public static GenericEsmRecord BuildProvinceRecord(ArenaProvince province)
    {
        ArgumentNullException.ThrowIfNull(province);

        var named = province.NamedLocations.ToList();
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["ProvinceIndex"] = province.Index,
            ["MapX"] = province.GlobalX,
            ["MapY"] = province.GlobalY,
            ["MapWidth"] = province.GlobalWidth,
            ["MapHeight"] = province.GlobalHeight,
            ["NamedLocations"] = named.Count
        };

        foreach (var group in named.GroupBy(l => l.Kind).OrderBy(g => g.Key))
        {
            fields[group.Key.ToString()] = group.Count();
        }

        return new GenericEsmRecord
        {
            FormId = ClassicFormIdScheme.Compose(ProvinceDomain, (uint)province.Index + 1),
            RecordType = ProvinceRecordType,
            EditorId = ToEditorId(province.Name),
            FullName = province.Name,
            Fields = fields
        };
    }

    /// <summary>Builds the record form of one named world-map location.</summary>
    public static GenericEsmRecord BuildLocationRecord(ArenaProvince province, ArenaLocation location)
    {
        ArgumentNullException.ThrowIfNull(province);
        ArgumentNullException.ThrowIfNull(location);

        var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Province"] = province.Name,
            ["Kind"] = location.Kind.ToString(),
            ["Slot"] = location.Slot,
            ["MapX"] = location.X,
            ["MapY"] = location.Y
        };

        if (location.Kind is ArenaLocationKind.StaffDungeon or ArenaLocationKind.StaffMapDungeon
            or ArenaLocationKind.RandomDungeon)
        {
            // Visibility is only meaningful for dungeons; settlements are always drawn.
            fields["Visible"] = location.IsVisible;
        }

        return new GenericEsmRecord
        {
            FormId = ClassicFormIdScheme.Compose(LocationDomain, LocationIndex(province.Index, location.Slot)),
            RecordType = LocationRecordType,
            EditorId = ToEditorId($"{province.Name}_{location.Name}"),
            FullName = location.Name,
            Fields = fields
        };
    }

    /// <summary>
    ///     A location's identity is its province and slot, both authored and positional, so the
    ///     stable index is composed rather than hashed.
    /// </summary>
    private static uint LocationIndex(int provinceIndex, int slot)
    {
        return ((uint)provinceIndex << 8) | (uint)slot;
    }

    /// <summary>Turns a display name into an editor-id-shaped token.</summary>
    private static string ToEditorId(string name)
    {
        var builder = new System.Text.StringBuilder(name.Length);
        foreach (var character in name)
        {
            builder.Append(char.IsLetterOrDigit(character) ? character : '_');
        }

        return builder.ToString();
    }

    /// <summary>
    ///     Every .INF in the install, keyed by upper-case name, loose copies winning over archived
    ///     ones. Archived copies are XOR-encrypted and loose copies are not — residency decides,
    ///     never content.
    /// </summary>
    public static IReadOnlyList<(string Name, byte[] PlainBytes)> EnumerateInfFiles(
        string root,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(root);

        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        var archivePath = Path.Combine(root, GlobalArchiveName);
        if (File.Exists(archivePath))
        {
            using var archive = ArchiveReader.Open(archivePath);
            foreach (var entry in archive.ListFiles())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!entry.Name.EndsWith(".INF", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var raw = archive.ReadFile(entry.FullPath);
                if (raw is not null)
                {
                    result[entry.Name] = ArenaInfFile.Decrypt(raw);
                }
            }
        }

        if (Directory.Exists(root))
        {
            foreach (var path in Directory.EnumerateFiles(root, "*.INF"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                result[Path.GetFileName(path)] = File.ReadAllBytes(path);
            }
        }

        return [.. result.Select(kvp => (kvp.Key.ToUpperInvariant(), kvp.Value)).OrderBy(x => x.Item1, StringComparer.Ordinal)];
    }

    /// <summary>Parses one already-decrypted .INF into its record form.</summary>
    public static GenericEsmRecord BuildInfRecord(string name, byte[] plainBytes)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(plainBytes);

        var inf = ArenaInfFile.ParseText(System.Text.Encoding.Latin1.GetString(plainBytes), name);
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["FloorTextures"] = inf.Floors.Count,
            ["WallTextures"] = inf.Walls.Count,
            ["Flats"] = inf.Flats.Count,
            ["Sounds"] = inf.Sounds.Count,
            ["Texts"] = inf.Texts.Count
        };

        if (inf.FlatsNoShow)
        {
            fields["FlatsNoShow"] = true;
        }

        if (inf.Ceiling is { } ceiling)
        {
            fields["CeilingHeight"] = ceiling.Height;
            fields["CeilingBoxScale"] = ceiling.BoxScale;
            fields["OutdoorDungeon"] = ceiling.OutdoorDungeon;
        }

        var menuIds = inf.Walls.Where(w => w.MenuId is not null).Select(w => w.MenuId!.Value).Distinct().Order().ToList();
        if (menuIds.Count > 0)
        {
            fields["MenuIds"] = string.Join(", ", menuIds);
        }

        if (inf.Sounds.Count > 0)
        {
            fields["SoundFiles"] = string.Join(", ", inf.Sounds.Select(s => $"{s.Id}={s.FileName}"));
        }

        var keys = inf.Texts.Where(t => t.KeyId is not null).Select(t => $"{t.Id}:+{t.KeyId}").ToList();
        if (keys.Count > 0)
        {
            fields["DoorKeys"] = string.Join(", ", keys);
        }

        var riddles = inf.Texts.Count(t => t.Riddle is not null);
        if (riddles > 0)
        {
            fields["Riddles"] = riddles;
        }

        foreach (var text in inf.Texts.Where(t => !string.IsNullOrWhiteSpace(t.Text)))
        {
            fields[$"Text{text.Id:D3}"] = OneLine(text.Text!);
        }

        return new GenericEsmRecord
        {
            FormId = ClassicFormIdScheme.Compose(InfDomain, StableNameIndex(name)),
            RecordType = InfRecordType,
            EditorId = Path.GetFileNameWithoutExtension(name).ToUpperInvariant(),
            FullName = FirstText(inf),
            Fields = fields
        };
    }

    /// <summary>Builds the record form of one TEMPLATE.DAT entry.</summary>
    public static GenericEsmRecord BuildTemplateRecord(ArenaTemplateDatEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Key"] = entry.Key,
            ["Values"] = entry.Values.Count
        };

        if (entry.HasLetter)
        {
            fields["Variant"] = entry.Letter.ToString();
        }

        if (entry.Copy > 0)
        {
            fields["TilesetCopy"] = entry.Copy;
        }

        for (var i = 0; i < entry.Values.Count; i++)
        {
            fields[$"Value{i:D2}"] = OneLine(entry.Values[i]);
        }

        var editorId = entry.Copy > 0
            ? $"{entry.DisplayKey}#{entry.Copy.ToString(CultureInfo.InvariantCulture)}"
            : entry.DisplayKey;

        return new GenericEsmRecord
        {
            FormId = ClassicFormIdScheme.Compose(TemplateDomain, TemplateIndex(entry)),
            RecordType = TemplateRecordType,
            EditorId = editorId,
            FullName = entry.Values.Count > 0 ? Summarize(entry.Values[0]) : null,
            Fields = fields
        };
    }

    /// <summary>
    ///     A TEMPLATE.DAT entry's identity is fully numeric and authored, so its stable index is
    ///     composed rather than hashed: <c>key</c> in the high bits, then the tileset copy, then
    ///     the letter variant. Keys reach 1501 and copies reach 3 in the retail file, so this
    ///     stays well inside 24 bits and is identical across installs.
    /// </summary>
    private static uint TemplateIndex(ArenaTemplateDatEntry entry)
    {
        var letterSlot = entry.HasLetter
            ? (uint)(char.ToLowerInvariant(entry.Letter) - 'a' + 1)
            : 0u;
        if (letterSlot > 26)
        {
            letterSlot = 27;
        }

        return ((uint)entry.Key << 8) | ((uint)Math.Min(entry.Copy, 3) << 5) | letterSlot;
    }

    /// <summary>
    ///     An .INF file's identity is its name, so its stable index is a 24-bit FNV-1a hash of the
    ///     upper-cased name — stable across installs and independent of enumeration order, which is
    ///     what makes <c>diff</c> between two installs compare like with like.
    /// </summary>
    private static uint StableNameIndex(string name)
    {
        const uint offsetBasis = 2166136261;
        const uint prime = 16777619;

        var hash = offsetBasis;
        foreach (var character in name.ToUpperInvariant())
        {
            hash = (hash ^ character) * prime;
        }

        // Fold the top byte into the low 24 bits rather than discarding it, then keep the result
        // clear of 0 so no record can land on the domain's base id.
        var folded = ((hash >> 24) ^ hash) & ClassicFormIdScheme.MaxIndex;
        return folded == 0 ? 1 : folded;
    }

    private static string? FirstText(ArenaInfFile inf)
    {
        var text = inf.Texts.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.Text))?.Text;
        return text is null ? null : Summarize(text);
    }

    private static string OneLine(string value)
    {
        return value.Replace('\n', ' ').Replace('\r', ' ').Trim();
    }

    /// <summary>Trims a string to a display-friendly length for the FULL-name column.</summary>
    private static string Summarize(string value)
    {
        const int maxLength = 60;
        var line = OneLine(value);
        return line.Length <= maxLength ? line : string.Concat(line.AsSpan(0, maxLength - 1), "…");
    }
}
