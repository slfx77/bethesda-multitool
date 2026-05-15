using System.Globalization;
using System.Text;
using System.Text.Json;

namespace FalloutXbox360Utils.CLI.Commands.Dmp;

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
    string? Path,
    string? Warning);

internal static class CellWorldspaceAuthorityJson
{
    public static CellWorldspaceAuthorityLoadResult Load(string? explicitPath)
    {
        var path = ResolveInputPath(explicitPath);
        if (path is null)
        {
            return new CellWorldspaceAuthorityLoadResult(null, null, null);
        }

        if (!File.Exists(path))
        {
            return new CellWorldspaceAuthorityLoadResult(
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

            return new CellWorldspaceAuthorityLoadResult(map, path, null);
        }
        catch (Exception ex)
        {
            return new CellWorldspaceAuthorityLoadResult(
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
