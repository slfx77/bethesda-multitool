using System.Text;
using BethesdaMultitool.Core.Formats.Battlespire;
using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Xngine.Mesh;

namespace BethesdaMultitool.Core.Formats.Classic;

/// <summary>
///     Synthesizes browsable records from a Battlespire install: one <c>BMSH</c> per mesh in
///     <c>3D.BSA</c> and per loose <c>.3D</c> file, one <c>BTXT</c> per entry of <c>TXT.BSA</c>
///     (whose 254 records are plain tab-separated text), and one <c>BLVL</c> per level in
///     <c>BS6.BSA</c>.
/// </summary>
internal static class BattlespireRecordSource
{
    /// <summary>Domain byte for <c>BMSH</c> (mesh) records.</summary>
    public const byte MeshDomain = 0x20;

    /// <summary>Domain byte for <c>BTXT</c> (text) records.</summary>
    public const byte TextDomain = 0x21;

    /// <summary>The record signature used for a mesh.</summary>
    public const string MeshRecordType = "BMSH";

    /// <summary>The record signature used for a text entry.</summary>
    public const string TextRecordType = "BTXT";

    /// <summary>Domain byte for <c>BLVL</c> (level) records.</summary>
    public const byte LevelDomain = 0x22;

    /// <summary>The record signature used for a level.</summary>
    public const string LevelRecordType = "BLVL";

    private const string MeshArchiveName = "3D.BSA";
    private const string TextArchiveName = "TXT.BSA";
    private const string LevelArchiveName = "BS6.BSA";

    /// <summary>Reads <paramref name="dataRoot" /> (GAMEDATA) and appends every synthesized record.</summary>
    public static void Populate(string dataRoot, RecordCollection records,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataRoot);
        ArgumentNullException.ThrowIfNull(records);

        PopulateMeshes(dataRoot, records, cancellationToken);
        PopulateText(dataRoot, records, cancellationToken);
        PopulateLevels(dataRoot, records, cancellationToken);
    }

    private static void PopulateLevels(string dataRoot, RecordCollection records, CancellationToken cancellationToken)
    {
        var path = Path.Combine(dataRoot, LevelArchiveName);
        if (!File.Exists(path))
        {
            return;
        }

        using var archive = ArchiveReader.Open(path);
        var index = 0;
        foreach (var entry in archive.ListFiles())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = archive.ReadFile(entry.FullPath);
            if (bytes is null)
            {
                index++;
                continue;
            }

            try
            {
                records.GenericRecords.Add(BuildLevelRecord(index, Bs6File.Parse(bytes, entry.Name)));
            }
            catch (InvalidDataException)
            {
                // Two retail entries are not levels: a text file and a truncated record.
            }

            index++;
        }
    }

    /// <summary>Builds the record form of one level. Identity is its position in the archive.</summary>
    public static GenericEsmRecord BuildLevelRecord(int index, Bs6File level)
    {
        ArgumentNullException.ThrowIfNull(level);

        var placed = level.Objects
            .Where(o => o.MeshIndex >= 0 && o.MeshIndex < level.MeshNames.Count)
            .GroupBy(o => level.MeshNames[o.MeshIndex])
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Take(10)
            .Select(g => $"{g.Key}={g.Count()}")
            .ToList();

        var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["MeshesListed"] = level.MeshNames.Count,
            ["MeshesPlaced"] = level.Objects.Count,
            ["Lights"] = level.Lights.Count,
            ["Flats"] = level.Flats.Count,
            ["Views"] = level.ViewCount,
            ["SnapGrids"] = level.SnapCount,
            ["Radius"] = level.Radius,
            ["Water"] = level.Water,
            ["Bits"] = $"0x{level.Bits:X}"
        };

        if (level.BoundingBox is { } box)
        {
            fields["BoundsMin"] = $"({box.Min.X}, {box.Min.Y}, {box.Min.Z})";
            fields["BoundsMax"] = $"({box.Max.X}, {box.Max.Y}, {box.Max.Z})";
        }

        if (level.TextureDirectory is { Length: > 0 } directory)
        {
            fields["AuthoredIn"] = directory;
        }

        if (placed.Count > 0)
        {
            fields["MostPlaced"] = string.Join(", ", placed);
        }

        return new GenericEsmRecord
        {
            FormId = ClassicFormIdScheme.Compose(LevelDomain, (uint)index),
            RecordType = LevelRecordType,
            EditorId = ClassicRecordNaming.ToEditorId(Path.GetFileNameWithoutExtension(level.Name).ToUpperInvariant()),
            FullName = null,
            Fields = fields
        };
    }

    private static void PopulateMeshes(string dataRoot, RecordCollection records, CancellationToken cancellationToken)
    {
        var index = 0;
        var archivePath = Path.Combine(dataRoot, MeshArchiveName);
        if (File.Exists(archivePath))
        {
            using var archive = BattlespireMeshArchive.Open(archivePath);
            for (var i = 0; i < archive.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (archive.TryParse(i, out var mesh, out _))
                {
                    records.GenericRecords.Add(BuildMeshRecord(index, archive.EntryName(i), MeshArchiveName, mesh));
                }

                index++;
            }
        }

        if (!Directory.Exists(dataRoot))
        {
            return;
        }

        var loose = Directory.EnumerateFiles(dataRoot, "*.3D")
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase);
        foreach (var path in loose)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(path);
            try
            {
                records.GenericRecords.Add(BuildMeshRecord(index, name, null,
                    BattlespireMeshArchive.ParseLoose(File.ReadAllBytes(path), name)));
            }
            catch (InvalidDataException)
            {
                // A mesh this reader cannot parse is reported by the CLI, not silently recorded.
            }

            index++;
        }
    }

    private static void PopulateText(string dataRoot, RecordCollection records, CancellationToken cancellationToken)
    {
        var path = Path.Combine(dataRoot, TextArchiveName);
        if (!File.Exists(path))
        {
            return;
        }

        using var archive = ArchiveReader.Open(path);
        var index = 0;
        foreach (var entry in archive.ListFiles())
        {
            cancellationToken.ThrowIfCancellationRequested();
            records.GenericRecords.Add(BuildTextRecord(index++, entry.Name, archive.ReadFile(entry.FullPath)));
        }
    }

    /// <summary>Builds the record form of one mesh. Identity is its position in the enumeration.</summary>
    public static GenericEsmRecord BuildMeshRecord(int index, string name, string? archiveName, XnGineMesh mesh)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(mesh);

        var size = mesh.Size;
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Source"] = archiveName ?? "loose",
            ["Version"] = mesh.VersionTag,
            ["Points"] = mesh.Points.Count,
            ["Planes"] = mesh.Planes.Count,
            ["Triangles"] = mesh.Planes.Sum(p => Math.Max(0, p.Points.Count - 2)),
            ["Radius"] = mesh.RadiusUnits,
            ["SizeX"] = size.X,
            ["SizeY"] = size.Y,
            ["SizeZ"] = size.Z,
            ["Textures"] = mesh.UniqueTextures.Count
        };

        return new GenericEsmRecord
        {
            FormId = ClassicFormIdScheme.Compose(MeshDomain, (uint)index),
            RecordType = MeshRecordType,
            EditorId = ClassicRecordNaming.ToEditorId(Path.GetFileNameWithoutExtension(name).ToUpperInvariant()),
            FullName = null,
            Fields = fields
        };
    }

    /// <summary>
    ///     Builds the record form of one TXT.BSA entry. The entries are tab-indented plain text, so
    ///     the first non-empty line becomes the display name and every line is surfaced.
    /// </summary>
    public static GenericEsmRecord BuildTextRecord(int index, string name, byte[]? bytes)
    {
        ArgumentNullException.ThrowIfNull(name);

        var text = bytes is null ? string.Empty : Encoding.Latin1.GetString(bytes);
        var lines = text.Split('\n')
            .Select(line => line.Replace("\r", string.Empty).Trim())
            .Where(line => line.Length > 0)
            .ToList();

        var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Bytes"] = bytes?.Length ?? 0,
            ["Lines"] = lines.Count
        };

        // ⚑ Four of the 253 text entries are the magical-item TABLE rather than prose or a dated
        // developer log, so those get their records parsed into fields instead of only lines.
        // ⚠ The gate is the item KEYS, not the presence of tabs: nine entries are dev logs whose
        // keys are dates, and gating on tabs would surface changelog lines as game data.
        if (BattlespireItemTable.LooksLikeItemTable(text))
        {
            var items = BattlespireItemTable.Parse(text);
            fields["Items"] = items.Count;
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                fields[$"Item{i:D3}"] = item.Name;

                // ⚠ 13 of the 457 retail records carry NO id — the whole of ITEML2 and SITEML2 —
                // so the id is emitted only when present rather than defaulted to 0 or -1.
                if (item.Id is { } id)
                {
                    fields[$"Item{i:D3}Id"] = id;
                }
            }
        }

        for (var i = 0; i < lines.Count; i++)
        {
            fields[$"Line{i:D3}"] = lines[i];
        }

        return new GenericEsmRecord
        {
            FormId = ClassicFormIdScheme.Compose(TextDomain, (uint)index),
            RecordType = TextRecordType,
            EditorId = ClassicRecordNaming.ToEditorId(name.ToUpperInvariant()),
            FullName = lines.Count > 0 ? ClassicRecordNaming.Summarize(lines[0]) : null,
            Fields = fields
        };
    }
}
