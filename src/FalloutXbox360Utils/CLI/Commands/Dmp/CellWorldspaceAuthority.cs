using System.Globalization;
using System.Text;
using System.Text.Json;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Parsing.Handlers;
using FalloutXbox360Utils.Core.Formats.Esm.Records;

namespace FalloutXbox360Utils.Core.Formats.Esm;

internal sealed class CellWorldspaceAuthorityBuilder
{
    public Dictionary<uint, uint> CellToWorldspace { get; } = [];
    public Dictionary<uint, HashSet<uint>> Conflicts { get; } = [];
    public Dictionary<uint, string> WorldspaceNames { get; } = [];
    public List<CellWorldspaceAuthoritySource> Sources { get; } = [];

    public bool TryAddOrFlag(uint cellFormId, uint worldspaceFormId, string sourceLabel)
    {
        if (cellFormId == 0 || worldspaceFormId == 0)
        {
            return false;
        }

        if (!CellToWorldspace.TryGetValue(cellFormId, out var existing))
        {
            CellToWorldspace[cellFormId] = worldspaceFormId;
            return true;
        }

        if (existing != worldspaceFormId)
        {
            if (!Conflicts.TryGetValue(cellFormId, out var set))
            {
                set = [existing];
                Conflicts[cellFormId] = set;
            }

            set.Add(worldspaceFormId);
        }

        return false;
    }

    public void AddWorldspaceName(uint worldspaceFormId, string? editorId)
    {
        if (!string.IsNullOrEmpty(editorId))
        {
            WorldspaceNames.TryAdd(worldspaceFormId, editorId);
        }
    }

    public void AddSource(string type, string path, int addedCells, int observedCells)
    {
        Sources.Add(new CellWorldspaceAuthoritySource(type, path, addedCells, observedCells));
    }
}

internal sealed record CellWorldspaceAuthoritySource(
    string Type,
    string Path,
    int AddedCells,
    int ObservedCells);

internal sealed record CellWorldspaceAuthorityLoadResult(
    Dictionary<uint, uint>? Cells,
    Dictionary<uint, string>? WorldspaceNames,
    string? Path,
    string? Warning);

internal static class CellWorldspaceAuthorityJson
{
    public static CellWorldspaceAuthorityLoadResult Load(string? explicitPath)
    {
        var path = ResolveInputPath(explicitPath);
        if (path is null)
        {
            return new CellWorldspaceAuthorityLoadResult(null, null, null, null);
        }

        if (!File.Exists(path))
        {
            return new CellWorldspaceAuthorityLoadResult(
                null,
                null,
                path,
                $"--cell-authority not found, skipping: {path}");
        }

        try
        {
            using var stream = File.OpenRead(path);
            using var doc = JsonDocument.Parse(stream);
            if (!doc.RootElement.TryGetProperty("cells", out var cellsEl) ||
                cellsEl.ValueKind != JsonValueKind.Object)
            {
                return new CellWorldspaceAuthorityLoadResult(
                    null,
                    null,
                    path,
                    $"Authority JSON has no `cells` object, skipping: {path}");
            }

            var map = new Dictionary<uint, uint>(cellsEl.EnumerateObject().Count());
            foreach (var entry in cellsEl.EnumerateObject())
            {
                if (TryParseHexUInt(entry.Name, out var cell) &&
                    entry.Value.ValueKind == JsonValueKind.String &&
                    TryParseHexUInt(entry.Value.GetString(), out var wrld))
                {
                    map[cell] = wrld;
                }
            }

            var worldspaceNames = new Dictionary<uint, string>();
            if (doc.RootElement.TryGetProperty("worldspaces", out var worldspacesEl) &&
                worldspacesEl.ValueKind == JsonValueKind.Object)
            {
                foreach (var entry in worldspacesEl.EnumerateObject())
                {
                    if (TryParseHexUInt(entry.Name, out var wrld) &&
                        entry.Value.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(entry.Value.GetString()))
                    {
                        worldspaceNames[wrld] = entry.Value.GetString()!;
                    }
                }
            }

            return new CellWorldspaceAuthorityLoadResult(map, worldspaceNames, path, null);
        }
        catch (Exception ex)
        {
            return new CellWorldspaceAuthorityLoadResult(
                null,
                null,
                path,
                $"Failed to load --cell-authority ({ex.Message}); skipping.");
        }
    }

    public static Dictionary<uint, uint>? Merge(
        IReadOnlyDictionary<uint, uint>? pcEsm,
        IReadOnlyDictionary<uint, uint>? authority)
    {
        if (pcEsm is null && authority is null)
        {
            return null;
        }

        var merged = new Dictionary<uint, uint>(
            (pcEsm?.Count ?? 0) + (authority?.Count ?? 0));
        if (authority is not null)
        {
            foreach (var (k, v) in authority)
            {
                merged[k] = v;
            }
        }

        if (pcEsm is not null)
        {
            foreach (var (k, v) in pcEsm)
            {
                merged[k] = v;
            }
        }

        return merged;
    }

    public static async Task WriteAsync(
        string path,
        IReadOnlyDictionary<uint, uint> cellToWorldspace,
        IReadOnlyDictionary<uint, HashSet<uint>> conflicts,
        IReadOnlyDictionary<uint, string> worldspaceNames,
        IEnumerable<CellWorldspaceAuthoritySource> sources,
        CancellationToken ct)
    {
        // Hand-rolled emit via Utf8JsonWriter — the project disables reflection-based
        // serialization (likely AOT-readiness), so JsonSerializer.Serialize on anonymous /
        // dictionary types throws. Utf8JsonWriter is the supported escape hatch.
        await using var stream = File.Create(path);
        await using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

        writer.WriteStartObject();
        writer.WriteNumber("schema_version", 1);
        writer.WriteString(
            "generated_at",
            DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));

        writer.WriteStartArray("sources");
        foreach (var src in sources)
        {
            writer.WriteStartObject();
            writer.WriteString("type", src.Type);
            writer.WriteString("path", src.Path.Replace('\\', '/'));
            writer.WriteNumber("added", src.AddedCells);
            writer.WriteNumber("observed", src.ObservedCells);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        writer.WriteStartObject("worldspaces");
        foreach (var (ws, name) in worldspaceNames.OrderBy(kv => kv.Key))
        {
            writer.WriteString($"0x{ws:X8}", name);
        }
        writer.WriteEndObject();

        writer.WriteStartObject("cells");
        foreach (var (cell, wrld) in cellToWorldspace.OrderBy(kv => kv.Key))
        {
            writer.WriteString($"0x{cell:X8}", $"0x{wrld:X8}");
        }
        writer.WriteEndObject();

        writer.WriteStartObject("conflicts");
        foreach (var (cell, set) in conflicts.OrderBy(kv => kv.Key))
        {
            writer.WriteStartArray($"0x{cell:X8}");
            foreach (var ws in set.OrderBy(x => x))
            {
                writer.WriteStringValue($"0x{ws:X8}");
            }
            writer.WriteEndArray();
        }
        writer.WriteEndObject();

        writer.WriteEndObject();
        await writer.FlushAsync(ct);
    }

    internal static bool TryParseHexUInt(string? s, out uint value)
    {
        value = 0;
        if (string.IsNullOrEmpty(s))
        {
            return false;
        }

        var span = s.AsSpan();
        if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            span = span[2..];
        }

        return uint.TryParse(span, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }

    private static string? ResolveInputPath(string? explicitPath)
    {
        if (!string.IsNullOrEmpty(explicitPath))
        {
            return explicitPath;
        }

        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "data", "cell_worldspace_authority.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "data", "cell_worldspace_authority.json")
        ];
        return candidates.FirstOrDefault(File.Exists);
    }
}

internal sealed record CellWorldspaceAuthorityApplyResult(
    int Applied,
    int Added,
    int Overrode,
    int SynthesizedWorldspaces,
    int TerrainCellsAttached = 0);

internal static class CellWorldspaceAuthorityApplier
{
    /// <summary>
    ///     Applies an authoritative Cell FormID to Worldspace FormID map to the semantic record
    ///     model and rebuilds worldspace child lists so reports and map views see the same
    ///     ownership that DMP to ESP conversion uses.
    /// </summary>
    public static CellWorldspaceAuthorityApplyResult Apply(
        RecordCollection records,
        IReadOnlyDictionary<uint, uint>? authority,
        IReadOnlyDictionary<uint, string>? worldspaceNames = null,
        EsmRecordScanResult? scanResult = null)
    {
        if (authority is null || authority.Count == 0 || records.Cells.Count == 0)
        {
            return new CellWorldspaceAuthorityApplyResult(0, 0, 0, 0);
        }

        var applied = 0;
        var overrode = 0;
        var added = 0;
        var matched = 0;

        for (var i = 0; i < records.Cells.Count; i++)
        {
            var cell = records.Cells[i];
            if (!authority.TryGetValue(cell.FormId, out var wsFid) || wsFid == 0u)
            {
                continue;
            }

            matched++;
            if (scanResult is not null)
            {
                scanResult.CellToWorldspaceMap[cell.FormId] = wsFid;
            }

            if (cell.WorldspaceFormId is { } existing && existing == wsFid &&
                string.Equals(cell.WorldspaceAssignmentSource, "Authority", StringComparison.Ordinal))
            {
                continue;
            }

            if (cell.WorldspaceFormId is { } prior && prior != 0u && prior != wsFid)
            {
                overrode++;
            }
            else if (cell.WorldspaceFormId is null || cell.WorldspaceFormId == 0u)
            {
                added++;
            }

            records.Cells[i] = cell with
            {
                WorldspaceFormId = wsFid,
                WorldspaceAssignmentSource = "Authority"
            };
            applied++;
        }

        var terrainBefore = CountCellsWithTerrain(records);
        if (scanResult is not null && matched > 0)
        {
            EsmLandEnricher.EnrichLandRecordsWithCellWorldspaces(scanResult, records.Cells);
            CellRecordHandler.AttachTerrainDataFromLandRecords(records.Cells, scanResult);
        }

        var synthesized = RebuildWorldspaceCellLists(records, worldspaceNames);
        var terrainAttached = Math.Max(0, CountCellsWithTerrain(records) - terrainBefore);
        return new CellWorldspaceAuthorityApplyResult(applied, added, overrode, synthesized, terrainAttached);
    }

    private static int CountCellsWithTerrain(RecordCollection records)
    {
        return records.Cells.Count(c =>
            c.Heightmap is not null ||
            c.LandVisualData?.HasAny == true ||
            c.RuntimeTerrainMesh is not null);
    }

    private static int RebuildWorldspaceCellLists(
        RecordCollection records,
        IReadOnlyDictionary<uint, string>? worldspaceNames)
    {
        foreach (var (worldspaceFormId, name) in worldspaceNames ?? new Dictionary<uint, string>())
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                records.FormIdToEditorId.TryAdd(worldspaceFormId, name);
            }
        }

        var cellsByWorldspace = records.Cells
            .Where(c => !c.IsInterior && c.WorldspaceFormId is > 0)
            .GroupBy(c => c.WorldspaceFormId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        var worldspaceIds = new HashSet<uint>(records.Worldspaces.Select(w => w.FormId));
        var synthesized = 0;
        foreach (var worldspaceFormId in cellsByWorldspace.Keys.OrderBy(id => id))
        {
            if (worldspaceIds.Contains(worldspaceFormId))
            {
                continue;
            }

            var name = worldspaceNames is not null &&
                       worldspaceNames.TryGetValue(worldspaceFormId, out var resolvedName)
                ? resolvedName
                : null;
            records.Worldspaces.Add(new WorldspaceRecord
            {
                FormId = worldspaceFormId,
                EditorId = string.IsNullOrWhiteSpace(name) ? null : name,
                Cells = []
            });
            worldspaceIds.Add(worldspaceFormId);
            synthesized++;
        }

        for (var i = 0; i < records.Worldspaces.Count; i++)
        {
            var ws = records.Worldspaces[i];
            cellsByWorldspace.TryGetValue(ws.FormId, out var cells);
            records.Worldspaces[i] = ws with { Cells = cells ?? [] };
        }

        return synthesized;
    }
}
