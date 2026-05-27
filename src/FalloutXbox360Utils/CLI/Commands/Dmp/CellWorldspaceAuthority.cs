using System.Globalization;
using System.Text;
using System.Text.Json;
using FalloutXbox360Utils.Core.Formats.Esm.Analysis;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using FalloutXbox360Utils.Core.Formats.Esm.Parsing.Handlers;
using FalloutXbox360Utils.Core.Formats.Esm.Records;

namespace FalloutXbox360Utils.Core.Formats.Esm;

internal sealed class CellWorldspaceAuthorityBuilder
{
    public Dictionary<uint, CellAuthorityMetadata> Cells { get; } = [];
    public Dictionary<uint, uint> ReferenceParents { get; } = [];
    public List<CellReferenceParentWindow> ReferenceParentWindows { get; } = [];
    public Dictionary<uint, HashSet<uint>> Conflicts { get; } = [];
    public Dictionary<uint, HashSet<uint>> ReferenceConflicts { get; } = [];
    public Dictionary<uint, string> WorldspaceNames { get; } = [];
    public List<CellWorldspaceAuthoritySource> Sources { get; } = [];

    public Dictionary<uint, uint> CellToWorldspace => Cells
        .Where(kv => kv.Value.WorldspaceFormId is > 0)
        .ToDictionary(kv => kv.Key, kv => kv.Value.WorldspaceFormId!.Value);

    public bool TryAddOrFlag(uint cellFormId, uint worldspaceFormId, string sourceLabel)
    {
        if (cellFormId == 0 || worldspaceFormId == 0)
        {
            return false;
        }

        if (!Cells.TryGetValue(cellFormId, out var existing))
        {
            Cells[cellFormId] = new CellAuthorityMetadata { WorldspaceFormId = worldspaceFormId };
            return true;
        }

        if (existing.WorldspaceFormId is null or 0u)
        {
            Cells[cellFormId] = existing with { WorldspaceFormId = worldspaceFormId };
            return false;
        }

        if (existing.WorldspaceFormId.Value != worldspaceFormId)
        {
            if (!Conflicts.TryGetValue(cellFormId, out var set))
            {
                set = [existing.WorldspaceFormId.Value];
                Conflicts[cellFormId] = set;
            }

            set.Add(worldspaceFormId);
        }

        return false;
    }

    public bool TryAddReferenceParent(uint referenceFormId, uint cellFormId, string sourceLabel)
    {
        if (referenceFormId == 0 || cellFormId == 0)
        {
            return false;
        }

        if (!ReferenceParents.TryGetValue(referenceFormId, out var existing))
        {
            ReferenceParents[referenceFormId] = cellFormId;
            return true;
        }

        if (existing != cellFormId)
        {
            if (!ReferenceConflicts.TryGetValue(referenceFormId, out var set))
            {
                set = [existing];
                ReferenceConflicts[referenceFormId] = set;
            }

            set.Add(cellFormId);
        }

        return false;
    }

    public bool AddOrUpdateCell(uint cellFormId, CellAuthorityMetadata metadata, string sourceLabel)
    {
        if (cellFormId == 0)
        {
            return false;
        }

        var added = !Cells.TryGetValue(cellFormId, out var existing);
        existing ??= new CellAuthorityMetadata();

        if (metadata.WorldspaceFormId is > 0)
        {
            TryAddOrFlag(cellFormId, metadata.WorldspaceFormId.Value, sourceLabel);
            existing = Cells[cellFormId];
        }

        Cells[cellFormId] = existing.MergeMissing(metadata);
        return added;
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

public sealed record CellAuthorityMetadata
{
    public uint? WorldspaceFormId { get; init; }
    public bool? IsInterior { get; init; }
    public int? GridX { get; init; }
    public int? GridY { get; init; }
    public string? EditorId { get; init; }
    public string? FullName { get; init; }

    public static CellAuthorityMetadata FromCell(CellRecord cell, uint? worldspaceFormId = null)
    {
        return new CellAuthorityMetadata
        {
            WorldspaceFormId = cell.IsInterior ? null : worldspaceFormId ?? cell.WorldspaceFormId,
            IsInterior = cell.IsInterior,
            GridX = cell.IsInterior ? null : cell.GridX,
            GridY = cell.IsInterior ? null : cell.GridY,
            EditorId = cell.EditorId,
            FullName = cell.FullName
        };
    }

    public CellAuthorityMetadata MergeMissing(CellAuthorityMetadata other)
    {
        return this with
        {
            WorldspaceFormId = WorldspaceFormId is > 0 ? WorldspaceFormId : other.WorldspaceFormId,
            IsInterior = IsInterior ?? other.IsInterior,
            GridX = GridX ?? other.GridX,
            GridY = GridY ?? other.GridY,
            EditorId = string.IsNullOrWhiteSpace(EditorId) ? other.EditorId : EditorId,
            FullName = string.IsNullOrWhiteSpace(FullName) ? other.FullName : FullName
        };
    }
}

public sealed record CellReferenceParentWindow
{
    public uint CellFormId { get; init; }
    public uint? AnchorReferenceFormId { get; init; }
    public long? CenterOffset { get; init; }
    public long? MinOffset { get; init; }
    public long? MaxOffset { get; init; }
    public int RadiusBeforeBytes { get; init; } = 0x400;
    public int RadiusAfterBytes { get; init; } = 0x400;
    public string? Label { get; init; }

    internal bool TryResolveRange(Func<uint, long?> resolveAnchorOffset, out long minOffset, out long maxOffset)
    {
        minOffset = 0;
        maxOffset = 0;

        if (MinOffset.HasValue && MaxOffset.HasValue)
        {
            minOffset = Math.Min(MinOffset.Value, MaxOffset.Value);
            maxOffset = Math.Max(MinOffset.Value, MaxOffset.Value);
            return maxOffset > 0;
        }

        var center = CenterOffset;
        if (!center.HasValue && AnchorReferenceFormId is { } anchorReferenceFormId)
        {
            center = resolveAnchorOffset(anchorReferenceFormId);
        }

        if (center is not > 0)
        {
            return false;
        }

        var before = Math.Max(0, RadiusBeforeBytes);
        var after = Math.Max(0, RadiusAfterBytes);
        minOffset = center.Value > before ? center.Value - before : 0;
        maxOffset = center.Value > long.MaxValue - after ? long.MaxValue : center.Value + after;
        return true;
    }
}

internal sealed record CellWorldspaceAuthorityLoadResult(
    Dictionary<uint, CellAuthorityMetadata>? Cells,
    Dictionary<uint, uint>? RefToCell,
    List<CellReferenceParentWindow>? RefWindows,
    Dictionary<uint, string>? WorldspaceNames,
    string? Path,
    string? Warning)
{
    public Dictionary<uint, uint>? CellToWorldspace => Cells?
        .Where(kv => kv.Value.WorldspaceFormId is > 0)
        .ToDictionary(kv => kv.Key, kv => kv.Value.WorldspaceFormId!.Value);
}

internal static class CellWorldspaceAuthorityJson
{
    public static CellWorldspaceAuthorityLoadResult Load(string? explicitPath)
    {
        var path = ResolveInputPath(explicitPath);
        if (path is null)
        {
            return new CellWorldspaceAuthorityLoadResult(null, null, null, null, null, null);
        }

        if (!File.Exists(path))
        {
            return new CellWorldspaceAuthorityLoadResult(
                null,
                null,
                null,
                null,
                path,
                $"--cell-authority not found, skipping: {path}");
        }

        try
        {
            using var stream = File.OpenRead(path);
            using var doc = JsonDocument.Parse(stream);
            Dictionary<uint, CellAuthorityMetadata>? map = null;
            if (doc.RootElement.TryGetProperty("cells", out var cellsEl) &&
                cellsEl.ValueKind == JsonValueKind.Object)
            {
                map = new Dictionary<uint, CellAuthorityMetadata>(cellsEl.EnumerateObject().Count());
                foreach (var entry in cellsEl.EnumerateObject())
                {
                    if (!TryParseHexUInt(entry.Name, out var cell) ||
                        entry.Value.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var metadata = ReadCellMetadata(entry.Value);
                    if (metadata is not null)
                    {
                        map[cell] = metadata;
                    }
                }
            }

            var refToCell = new Dictionary<uint, uint>();
            if (doc.RootElement.TryGetProperty("references", out var referencesEl) &&
                referencesEl.ValueKind == JsonValueKind.Object)
            {
                foreach (var entry in referencesEl.EnumerateObject())
                {
                    if (TryParseHexUInt(entry.Name, out var referenceFormId) &&
                        entry.Value.ValueKind == JsonValueKind.String &&
                        TryParseHexUInt(entry.Value.GetString(), out var cellFormId))
                    {
                        refToCell[referenceFormId] = cellFormId;
                    }
                }
            }

            var refWindows = ReadReferenceWindows(doc.RootElement);

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

            if ((map is null || map.Count == 0) &&
                refToCell.Count == 0 &&
                (refWindows is null || refWindows.Count == 0) &&
                worldspaceNames.Count == 0)
            {
                return new CellWorldspaceAuthorityLoadResult(
                    null,
                    null,
                    null,
                    null,
                    path,
                    $"Authority JSON has no recognized authority sections, skipping: {path}");
            }

            return new CellWorldspaceAuthorityLoadResult(
                map,
                refToCell.Count > 0 ? refToCell : null,
                refWindows,
                worldspaceNames.Count > 0 ? worldspaceNames : null,
                path,
                null);
        }
        catch (Exception ex)
        {
            return new CellWorldspaceAuthorityLoadResult(
                null,
                null,
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

    public static Dictionary<uint, CellAuthorityMetadata>? MergeMetadata(
        IReadOnlyDictionary<uint, CellAuthorityMetadata>? preferred,
        IReadOnlyDictionary<uint, CellAuthorityMetadata>? fallback)
    {
        if (preferred is null && fallback is null)
        {
            return null;
        }

        var merged = new Dictionary<uint, CellAuthorityMetadata>(
            (preferred?.Count ?? 0) + (fallback?.Count ?? 0));
        if (fallback is not null)
        {
            foreach (var (k, v) in fallback)
            {
                merged[k] = v;
            }
        }

        if (preferred is not null)
        {
            foreach (var (k, v) in preferred)
            {
                merged[k] = merged.TryGetValue(k, out var existing)
                    ? v.MergeMissing(existing)
                    : v;
            }
        }

        return merged;
    }

    public static async Task WriteAsync(
        string path,
        IReadOnlyDictionary<uint, CellAuthorityMetadata> cells,
        IReadOnlyDictionary<uint, uint> referenceParents,
        IReadOnlyList<CellReferenceParentWindow> referenceWindows,
        IReadOnlyDictionary<uint, HashSet<uint>> conflicts,
        IReadOnlyDictionary<uint, HashSet<uint>> referenceConflicts,
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
        writer.WriteNumber("schema_version", 2);
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
        foreach (var (cell, metadata) in cells.OrderBy(kv => kv.Key))
        {
            writer.WriteStartObject($"0x{cell:X8}");
            if (metadata.WorldspaceFormId is > 0)
            {
                writer.WriteString("worldspace", $"0x{metadata.WorldspaceFormId.Value:X8}");
            }

            if (metadata.IsInterior.HasValue)
            {
                writer.WriteBoolean("is_interior", metadata.IsInterior.Value);
            }

            if (metadata.GridX.HasValue && metadata.GridY.HasValue)
            {
                writer.WriteNumber("grid_x", metadata.GridX.Value);
                writer.WriteNumber("grid_y", metadata.GridY.Value);
            }

            if (!string.IsNullOrWhiteSpace(metadata.EditorId))
            {
                writer.WriteString("editor_id", metadata.EditorId);
            }

            if (!string.IsNullOrWhiteSpace(metadata.FullName))
            {
                writer.WriteString("full_name", metadata.FullName);
            }

            writer.WriteEndObject();
        }
        writer.WriteEndObject();

        writer.WriteStartObject("references");
        foreach (var (reference, cell) in referenceParents.OrderBy(kv => kv.Key))
        {
            writer.WriteString($"0x{reference:X8}", $"0x{cell:X8}");
        }
        writer.WriteEndObject();

        writer.WriteStartArray("reference_windows");
        foreach (var window in referenceWindows.OrderBy(w => w.CellFormId)
                     .ThenBy(w => w.AnchorReferenceFormId ?? 0)
                     .ThenBy(w => w.CenterOffset ?? w.MinOffset ?? 0))
        {
            writer.WriteStartObject();
            writer.WriteString("cell", $"0x{window.CellFormId:X8}");
            if (window.AnchorReferenceFormId is { } anchorReferenceFormId)
            {
                writer.WriteString("anchor_reference", $"0x{anchorReferenceFormId:X8}");
            }

            if (window.CenterOffset is { } centerOffset)
            {
                writer.WriteString("center_offset", $"0x{centerOffset:X}");
            }

            if (window.MinOffset is { } minOffset)
            {
                writer.WriteString("min_offset", $"0x{minOffset:X}");
            }

            if (window.MaxOffset is { } maxOffset)
            {
                writer.WriteString("max_offset", $"0x{maxOffset:X}");
            }

            if (window.RadiusBeforeBytes == window.RadiusAfterBytes)
            {
                writer.WriteString("radius", $"0x{window.RadiusBeforeBytes:X}");
            }
            else
            {
                writer.WriteString("radius_before", $"0x{window.RadiusBeforeBytes:X}");
                writer.WriteString("radius_after", $"0x{window.RadiusAfterBytes:X}");
            }

            if (!string.IsNullOrWhiteSpace(window.Label))
            {
                writer.WriteString("label", window.Label);
            }

            writer.WriteEndObject();
        }
        writer.WriteEndArray();

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

        writer.WriteStartObject("reference_conflicts");
        foreach (var (reference, set) in referenceConflicts.OrderBy(kv => kv.Key))
        {
            writer.WriteStartArray($"0x{reference:X8}");
            foreach (var cell in set.OrderBy(x => x))
            {
                writer.WriteStringValue($"0x{cell:X8}");
            }
            writer.WriteEndArray();
        }
        writer.WriteEndObject();

        writer.WriteEndObject();
        await writer.FlushAsync(ct);
    }

    private static List<CellReferenceParentWindow>? ReadReferenceWindows(JsonElement root)
    {
        if (!root.TryGetProperty("reference_windows", out var windowsEl) ||
            windowsEl.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var windows = new List<CellReferenceParentWindow>();
        foreach (var entry in windowsEl.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object ||
                !TryReadHexUInt(entry, "cell", out var cellFormId))
            {
                continue;
            }

            uint? anchorReferenceFormId = null;
            if (TryReadHexUInt(entry, "anchor_reference", out var anchor) ||
                TryReadHexUInt(entry, "reference", out anchor))
            {
                anchorReferenceFormId = anchor;
            }

            long? centerOffset = null;
            if (TryReadLong(entry, "center_offset", out var center) ||
                TryReadLong(entry, "offset", out center))
            {
                centerOffset = center;
            }

            long? minOffset = null;
            if (TryReadLong(entry, "min_offset", out var min))
            {
                minOffset = min;
            }

            long? maxOffset = null;
            if (TryReadLong(entry, "max_offset", out var max))
            {
                maxOffset = max;
            }

            var radius = 0x400;
            if (TryReadLong(entry, "radius", out var radiusValue) ||
                TryReadLong(entry, "window", out radiusValue) ||
                TryReadLong(entry, "window_bytes", out radiusValue))
            {
                radius = ClampRadius(radiusValue);
            }

            var radiusBefore = radius;
            if (TryReadLong(entry, "radius_before", out var radiusBeforeValue) ||
                TryReadLong(entry, "before", out radiusBeforeValue))
            {
                radiusBefore = ClampRadius(radiusBeforeValue);
            }

            var radiusAfter = radius;
            if (TryReadLong(entry, "radius_after", out var radiusAfterValue) ||
                TryReadLong(entry, "after", out radiusAfterValue))
            {
                radiusAfter = ClampRadius(radiusAfterValue);
            }

            if (!minOffset.HasValue && !maxOffset.HasValue && !centerOffset.HasValue && !anchorReferenceFormId.HasValue)
            {
                continue;
            }

            var label = entry.TryGetProperty("label", out var labelEl) &&
                        labelEl.ValueKind == JsonValueKind.String
                ? labelEl.GetString()
                : null;

            windows.Add(new CellReferenceParentWindow
            {
                CellFormId = cellFormId,
                AnchorReferenceFormId = anchorReferenceFormId,
                CenterOffset = centerOffset,
                MinOffset = minOffset,
                MaxOffset = maxOffset,
                RadiusBeforeBytes = radiusBefore,
                RadiusAfterBytes = radiusAfter,
                Label = label
            });
        }

        return windows.Count > 0 ? windows : null;
    }

    private static CellAuthorityMetadata? ReadCellMetadata(JsonElement element)
    {
        uint? worldspace = null;
        if (element.TryGetProperty("worldspace", out var wsEl) &&
            wsEl.ValueKind == JsonValueKind.String &&
            TryParseHexUInt(wsEl.GetString(), out var wsFid))
        {
            worldspace = wsFid;
        }

        bool? isInterior = null;
        if (element.TryGetProperty("is_interior", out var interiorEl) &&
            interiorEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            isInterior = interiorEl.GetBoolean();
        }

        int? gridX = null;
        if (element.TryGetProperty("grid_x", out var gridXEl) &&
            gridXEl.TryGetInt32(out var gx))
        {
            gridX = gx;
        }

        int? gridY = null;
        if (element.TryGetProperty("grid_y", out var gridYEl) &&
            gridYEl.TryGetInt32(out var gy))
        {
            gridY = gy;
        }

        var editorId = element.TryGetProperty("editor_id", out var editorEl) &&
                       editorEl.ValueKind == JsonValueKind.String
            ? editorEl.GetString()
            : null;
        var fullName = element.TryGetProperty("full_name", out var fullEl) &&
                       fullEl.ValueKind == JsonValueKind.String
            ? fullEl.GetString()
            : null;

        if (worldspace is null && isInterior is null && gridX is null && gridY is null &&
            string.IsNullOrWhiteSpace(editorId) && string.IsNullOrWhiteSpace(fullName))
        {
            return null;
        }

        return new CellAuthorityMetadata
        {
            WorldspaceFormId = worldspace,
            IsInterior = isInterior,
            GridX = gridX,
            GridY = gridY,
            EditorId = editorId,
            FullName = fullName
        };
    }

    private static bool TryReadHexUInt(JsonElement element, string propertyName, out uint value)
    {
        value = 0;
        return element.TryGetProperty(propertyName, out var valueEl) &&
               valueEl.ValueKind == JsonValueKind.String &&
               TryParseHexUInt(valueEl.GetString(), out value);
    }

    private static bool TryReadLong(JsonElement element, string propertyName, out long value)
    {
        value = 0;
        if (!element.TryGetProperty(propertyName, out var valueEl))
        {
            return false;
        }

        if (valueEl.ValueKind == JsonValueKind.Number)
        {
            return valueEl.TryGetInt64(out value);
        }

        if (valueEl.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var s = valueEl.GetString();
        if (string.IsNullOrWhiteSpace(s))
        {
            return false;
        }

        var span = s.AsSpan().Trim();
        var style = NumberStyles.Integer;
        if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            span = span[2..];
            style = NumberStyles.HexNumber;
        }

        return long.TryParse(span, style, CultureInfo.InvariantCulture, out value);
    }

    private static int ClampRadius(long value)
    {
        if (value <= 0)
        {
            return 0;
        }

        return value > int.MaxValue ? int.MaxValue : (int)value;
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
    int TerrainCellsAttached = 0,
    int ReferencesReattached = 0,
    int ReferenceCellsCreated = 0,
    int ReferenceWindowsApplied = 0,
    int ReferenceWindowAmbiguousMatches = 0);

internal static class CellWorldspaceAuthorityApplier
{
    private const string SourceAuthorityOffsetCluster = "AuthorityOffsetCluster";
    private const string SourceAuthorityRefWindow = "AuthorityRefWindow";
    private const int AuthorityOffsetClusterGapBytes = 0x1000;
    private const int AuthorityOffsetClusterWindowBytes = 0x20000;
    private const int AuthorityOffsetClusterNearestBandBytes = 0x4000;
    private const int AuthorityOffsetClusterGridExpansion = 2;
    private const int AuthorityOffsetClusterMinPlacements = 2;
    private const string SourceOffsetCluster = "OffsetCluster";
    private const string SourceVirtual = "Virtual";

    private readonly record struct ResolvedReferenceWindow(
        uint CellFormId,
        long MinOffset,
        long MaxOffset,
        string? Label);

    /// <summary>
    ///     Applies authoritative CELL metadata to the semantic record model and rebuilds
    ///     worldspace child lists so reports and map views see the same ownership that
    ///     DMP to ESP conversion uses.
    /// </summary>
    public static CellWorldspaceAuthorityApplyResult Apply(
        RecordCollection records,
        IReadOnlyDictionary<uint, uint>? authority,
        IReadOnlyDictionary<uint, string>? worldspaceNames = null,
        EsmRecordScanResult? scanResult = null,
        IReadOnlyDictionary<uint, CellAuthorityMetadata>? cellMetadata = null,
        IReadOnlyDictionary<uint, uint>? refToCell = null,
        IReadOnlyList<CellReferenceParentWindow>? refWindows = null)
    {
        cellMetadata ??= authority?.ToDictionary(
            kv => kv.Key,
            kv => new CellAuthorityMetadata { WorldspaceFormId = kv.Value });
        var hasCellMetadata = cellMetadata is { Count: > 0 };
        var hasReferenceParents = refToCell is { Count: > 0 };
        var hasReferenceWindows = refWindows is { Count: > 0 };
        if ((!hasCellMetadata && !hasReferenceParents && !hasReferenceWindows) || records.Cells.Count == 0)
        {
            return new CellWorldspaceAuthorityApplyResult(0, 0, 0, 0);
        }

        var applied = 0;
        var overrode = 0;
        var added = 0;
        var matched = 0;

        for (var i = 0; hasCellMetadata && i < records.Cells.Count; i++)
        {
            var cell = records.Cells[i];
            if (!cellMetadata!.TryGetValue(cell.FormId, out var metadata))
            {
                continue;
            }

            matched++;
            var wsFid = metadata.IsInterior == true ? null : metadata.WorldspaceFormId;
            if (scanResult is not null && wsFid is > 0)
            {
                scanResult.CellToWorldspaceMap[cell.FormId] = wsFid.Value;
            }

            var flags = cell.Flags;
            if (metadata.IsInterior == true)
            {
                flags |= 0x01;
            }
            else if (metadata.IsInterior == false || wsFid is > 0)
            {
                flags = (byte)(flags & ~0x01);
            }

            if ((metadata.IsInterior == true && cell.WorldspaceFormId is > 0) ||
                (wsFid is > 0 && cell.WorldspaceFormId is { } prior && prior != 0u && prior != wsFid.Value))
            {
                overrode++;
            }
            else if (wsFid is > 0 && (cell.WorldspaceFormId is null || cell.WorldspaceFormId == 0u))
            {
                added++;
            }

            var gridX = metadata.IsInterior == true ? null : metadata.GridX ?? cell.GridX;
            var gridY = metadata.IsInterior == true ? null : metadata.GridY ?? cell.GridY;
            var updated = cell with
            {
                Flags = flags,
                GridX = gridX,
                GridY = gridY,
                WorldspaceFormId = metadata.IsInterior == true ? null : wsFid ?? cell.WorldspaceFormId,
                WorldspaceAssignmentSource = wsFid is > 0 ? "Authority" : cell.WorldspaceAssignmentSource,
                EditorId = string.IsNullOrWhiteSpace(cell.EditorId) ? metadata.EditorId : cell.EditorId,
                FullName = string.IsNullOrWhiteSpace(cell.FullName) ? metadata.FullName : cell.FullName
            };

            if (updated != cell)
            {
                records.Cells[i] = updated;
                applied++;
            }
        }

        var terrainBefore = CountCellsWithTerrain(records);
        var referenceMove = ReattachUnresolvedReferences(
            records,
            refToCell,
            cellMetadata,
            authority,
            scanResult);
        var windowMove = ReattachWindowedUnresolvedReferences(
            records,
            refWindows,
            cellMetadata,
            authority,
            scanResult);
        var offsetMove = ReattachOffsetClusteredUnresolvedReferences(records, scanResult);

        if (scanResult is not null &&
            (matched > 0 || referenceMove.Moved > 0 || windowMove.Moved > 0 || offsetMove.Moved > 0))
        {
            EsmLandEnricher.EnrichLandRecordsWithCellWorldspaces(scanResult, records.Cells);
            CellRecordHandler.AttachTerrainDataFromLandRecords(records.Cells, scanResult);
        }

        var synthesized = RebuildWorldspaceCellLists(records, worldspaceNames);
        var terrainAttached = Math.Max(0, CountCellsWithTerrain(records) - terrainBefore);
        return new CellWorldspaceAuthorityApplyResult(
            applied,
            added,
            overrode,
            synthesized,
            terrainAttached,
            referenceMove.Moved + windowMove.Moved + offsetMove.Moved,
            referenceMove.CreatedCells + windowMove.CreatedCells + offsetMove.CreatedCells,
            windowMove.AppliedWindows,
            windowMove.AmbiguousMatches);
    }

    private static (int Moved, int CreatedCells) ReattachUnresolvedReferences(
        RecordCollection records,
        IReadOnlyDictionary<uint, uint>? refToCell,
        IReadOnlyDictionary<uint, CellAuthorityMetadata>? cellMetadata,
        IReadOnlyDictionary<uint, uint>? authority,
        EsmRecordScanResult? scanResult)
    {
        if (refToCell is not { Count: > 0 })
        {
            return (0, 0);
        }

        var cellIndexByFormId = BuildCellIndex(records.Cells);
        var moved = 0;
        var createdCells = 0;
        var originalCellCount = records.Cells.Count;

        for (var i = 0; i < originalCellCount; i++)
        {
            var source = records.Cells[i];
            if (!CanReattachAuthorityMappedReferencesFrom(source) || source.PlacedObjects.Count == 0)
            {
                continue;
            }

            var kept = new List<PlacedReference>(source.PlacedObjects.Count);
            var sourceChanged = false;

            foreach (var placed in source.PlacedObjects)
            {
                if (!refToCell.TryGetValue(placed.FormId, out var targetCellFormId) ||
                    targetCellFormId == 0 ||
                    targetCellFormId == source.FormId ||
                    !TryGetOrCreateAuthorityCell(
                        records,
                        cellIndexByFormId,
                        targetCellFormId,
                        cellMetadata,
                        authority,
                        scanResult,
                        out var targetIndex,
                        out var created))
                {
                    kept.Add(placed);
                    continue;
                }

                if (created)
                {
                    createdCells++;
                }

                var target = records.Cells[targetIndex];
                if (!target.PlacedObjects.Any(p => p.FormId == placed.FormId))
                {
                    target.PlacedObjects.Add(placed with { AssignmentSource = "AuthorityRefParent" });
                }

                MoveScanResultRefLink(scanResult, source.FormId, targetCellFormId, placed.FormId);
                moved++;
                sourceChanged = true;
            }

            if (sourceChanged)
            {
                records.Cells[i] = source with { PlacedObjects = kept };
            }
        }

        if (moved == 0)
        {
            return (0, 0);
        }

        records.Cells.RemoveAll(c => c.PlacedObjects.Count == 0 && CanRemoveEmptyAuthorityReferenceSource(c));
        CellRecordHandler.ResolveDoorLinks(records.Cells);
        return (moved, createdCells);
    }

    private static bool CanReattachAuthorityMappedReferencesFrom(CellRecord cell)
    {
        if (cell.IsUnresolvedBucket)
        {
            return true;
        }

        if (!cell.IsVirtual || cell.IsPersistentCell)
        {
            return false;
        }

        return cell.PlacedObjects.Any(placed =>
            placed.AssignmentSource is SourceOffsetCluster or SourceVirtual or SourceAuthorityOffsetCluster or
                SourceAuthorityRefWindow);
    }

    private static bool CanRemoveEmptyAuthorityReferenceSource(CellRecord cell)
    {
        if (cell.IsUnresolvedBucket)
        {
            return true;
        }

        return cell.IsVirtual &&
               !cell.IsPersistentCell &&
               cell.WorldspaceAssignmentSource is SourceOffsetCluster or SourceVirtual or SourceAuthorityOffsetCluster or
                   SourceAuthorityRefWindow;
    }

    private static (int Moved, int CreatedCells, int AppliedWindows, int AmbiguousMatches)
        ReattachWindowedUnresolvedReferences(
            RecordCollection records,
            IReadOnlyList<CellReferenceParentWindow>? refWindows,
            IReadOnlyDictionary<uint, CellAuthorityMetadata>? cellMetadata,
            IReadOnlyDictionary<uint, uint>? authority,
            EsmRecordScanResult? scanResult)
    {
        if (refWindows is not { Count: > 0 })
        {
            return (0, 0, 0, 0);
        }

        var cellIndexByFormId = BuildCellIndex(records.Cells);
        var createdCells = 0;
        var targetIndexByCellFormId = new Dictionary<uint, int>();
        var resolvedWindows = new List<ResolvedReferenceWindow>();
        foreach (var window in refWindows)
        {
            if (window.CellFormId == 0 ||
                !window.TryResolveRange(
                    anchorReferenceFormId => TryFindAnchorOffset(records, window.CellFormId, anchorReferenceFormId),
                    out var minOffset,
                    out var maxOffset))
            {
                continue;
            }

            resolvedWindows.Add(new ResolvedReferenceWindow(
                window.CellFormId,
                minOffset,
                maxOffset,
                window.Label));
        }

        if (resolvedWindows.Count == 0)
        {
            return (0, createdCells, 0, 0);
        }

        var moved = 0;
        var ambiguousMatches = 0;
        for (var i = 0; i < records.Cells.Count; i++)
        {
            var source = records.Cells[i];
            if (!CanReattachAuthorityMappedReferencesFrom(source) || source.PlacedObjects.Count == 0)
            {
                continue;
            }

            var kept = new List<PlacedReference>(source.PlacedObjects.Count);
            var sourceChanged = false;
            foreach (var placed in source.PlacedObjects)
            {
                if (placed.Offset <= 0)
                {
                    kept.Add(placed);
                    continue;
                }

                if (!TrySelectWindowForOffset(resolvedWindows, placed.Offset, out var window, out var ambiguous))
                {
                    if (ambiguous)
                    {
                        ambiguousMatches++;
                    }

                    kept.Add(placed);
                    continue;
                }

                if (window.CellFormId == source.FormId)
                {
                    kept.Add(placed);
                    continue;
                }

                if (!targetIndexByCellFormId.TryGetValue(window.CellFormId, out var targetIndex))
                {
                    if (!TryGetOrCreateAuthorityCell(
                            records,
                            cellIndexByFormId,
                            window.CellFormId,
                            cellMetadata,
                            authority,
                            scanResult,
                            out targetIndex,
                            out var created))
                    {
                        kept.Add(placed);
                        continue;
                    }

                    if (created)
                    {
                        createdCells++;
                    }

                    targetIndexByCellFormId[window.CellFormId] = targetIndex;
                }

                var target = records.Cells[targetIndex];
                if (!target.PlacedObjects.Any(p => p.FormId == placed.FormId))
                {
                    target.PlacedObjects.Add(placed with { AssignmentSource = SourceAuthorityRefWindow });
                }

                MoveScanResultRefLink(scanResult, source.FormId, window.CellFormId, placed.FormId);
                moved++;
                sourceChanged = true;
            }

            if (sourceChanged)
            {
                records.Cells[i] = source with { PlacedObjects = kept };
            }
        }

        if (moved == 0)
        {
            return (0, createdCells, resolvedWindows.Count, ambiguousMatches);
        }

        records.Cells.RemoveAll(c => c.PlacedObjects.Count == 0 && CanRemoveEmptyAuthorityReferenceSource(c));
        CellRecordHandler.ResolveDoorLinks(records.Cells);
        return (moved, createdCells, resolvedWindows.Count, ambiguousMatches);
    }

    private static long? TryFindAnchorOffset(
        RecordCollection records,
        uint targetCellFormId,
        uint anchorReferenceFormId)
    {
        var targetOffsets = records.Cells
            .Where(cell => cell.FormId == targetCellFormId)
            .SelectMany(cell => cell.PlacedObjects)
            .Where(placed => placed.FormId == anchorReferenceFormId && placed.Offset > 0)
            .Select(placed => placed.Offset)
            .Distinct()
            .ToList();
        if (targetOffsets.Count == 1)
        {
            return targetOffsets[0];
        }

        var offsets = records.Cells
            .SelectMany(cell => cell.PlacedObjects)
            .Where(placed => placed.FormId == anchorReferenceFormId && placed.Offset > 0)
            .Select(placed => placed.Offset)
            .Distinct()
            .ToList();
        return offsets.Count == 1 ? offsets[0] : null;
    }

    private static bool TrySelectWindowForOffset(
        IReadOnlyList<ResolvedReferenceWindow> windows,
        long offset,
        out ResolvedReferenceWindow window,
        out bool ambiguous)
    {
        window = default;
        ambiguous = false;
        var matched = windows
            .Where(candidate => offset >= candidate.MinOffset && offset <= candidate.MaxOffset)
            .GroupBy(candidate => candidate.CellFormId)
            .ToList();
        if (matched.Count == 0)
        {
            return false;
        }

        if (matched.Count > 1)
        {
            ambiguous = true;
            return false;
        }

        window = matched[0].First();
        return true;
    }

    private static (int Moved, int CreatedCells) ReattachOffsetClusteredUnresolvedReferences(
        RecordCollection records,
        EsmRecordScanResult? scanResult)
    {
        var offsetAnchors = records.Cells
            .Where(cell => !cell.IsInterior &&
                           !cell.IsVirtual &&
                           !cell.IsUnresolvedBucket &&
                           cell.WorldspaceFormId is > 0 &&
                           cell.GridX.HasValue &&
                           cell.GridY.HasValue &&
                           cell.Offset > 0)
            .OrderBy(cell => cell.Offset)
            .ToList();

        if (offsetAnchors.Count == 0)
        {
            return (0, 0);
        }

        var cellIndexByFormId = BuildCellIndex(records.Cells);
        var moved = 0;
        var createdCells = 0;
        var nextVirtualFormId = NextAvailableSyntheticCellFormId(records, 0xFE900001u);

        for (var i = 0; i < records.Cells.Count; i++)
        {
            var source = records.Cells[i];
            if (!source.IsUnresolvedBucket || source.PlacedObjects.Count == 0)
            {
                continue;
            }

            var movedFormIds = new HashSet<uint>();
            foreach (var cluster in BuildPlacedOffsetClusters(source.PlacedObjects))
            {
                if (!TryInferAuthorityOffsetClusterWorldspace(cluster, offsetAnchors, out var worldspaceFormId))
                {
                    continue;
                }

                foreach (var placed in cluster)
                {
                    var (gx, gy) = CellUtils.WorldToCellCoordinates(placed.X, placed.Y);
                    var targetIndex = GetOrCreateAuthorityOffsetVirtualCell(
                        records,
                        cellIndexByFormId,
                        worldspaceFormId,
                        gx,
                        gy,
                        placed.IsBigEndian,
                        ref nextVirtualFormId,
                        out var created);
                    if (created)
                    {
                        createdCells++;
                    }

                    var target = records.Cells[targetIndex];
                    if (!target.PlacedObjects.Any(p => p.FormId == placed.FormId))
                    {
                        target.PlacedObjects.Add(placed with { AssignmentSource = SourceAuthorityOffsetCluster });
                    }

                    MoveScanResultRefLink(scanResult, source.FormId, target.FormId, placed.FormId);
                    movedFormIds.Add(placed.FormId);
                    moved++;
                }
            }

            if (movedFormIds.Count > 0)
            {
                records.Cells[i] = source with
                {
                    PlacedObjects = source.PlacedObjects
                        .Where(placed => !movedFormIds.Contains(placed.FormId))
                        .ToList()
                };
            }
        }

        if (moved == 0)
        {
            return (0, 0);
        }

        records.Cells.RemoveAll(c => c.IsUnresolvedBucket && c.PlacedObjects.Count == 0);
        CellRecordHandler.ResolveDoorLinks(records.Cells);
        return (moved, createdCells);
    }

    private static IEnumerable<List<PlacedReference>> BuildPlacedOffsetClusters(
        IReadOnlyList<PlacedReference> placedReferences)
    {
        List<PlacedReference>? cluster = null;
        long lastOffset = 0;
        foreach (var placed in placedReferences
                     .Where(placed => placed.Offset > 0)
                     .OrderBy(placed => placed.Offset))
        {
            if (cluster == null || placed.Offset - lastOffset > AuthorityOffsetClusterGapBytes)
            {
                if (cluster is { Count: > 0 })
                {
                    yield return cluster;
                }

                cluster = [];
            }

            cluster.Add(placed);
            lastOffset = placed.Offset;
        }

        if (cluster is { Count: > 0 })
        {
            yield return cluster;
        }
    }

    private static bool TryInferAuthorityOffsetClusterWorldspace(
        List<PlacedReference> cluster,
        List<CellRecord> offsetAnchors,
        out uint worldspaceFormId)
    {
        worldspaceFormId = 0;
        if (cluster.Count < AuthorityOffsetClusterMinPlacements)
        {
            return false;
        }

        var minOffset = cluster.Min(placed => placed.Offset);
        var maxOffset = cluster.Max(placed => placed.Offset);
        var nearbyAnchors = offsetAnchors
            .Select(cell => new
            {
                Cell = cell,
                Distance = DistanceToOffsetRange(cell.Offset, minOffset, maxOffset)
            })
            .Where(anchor => anchor.Distance <= AuthorityOffsetClusterWindowBytes)
            .ToList();

        if (nearbyAnchors.Count == 0)
        {
            return false;
        }

        var minDistance = nearbyAnchors.Min(anchor => anchor.Distance);
        var selectedAnchors = nearbyAnchors
            .Where(anchor => anchor.Distance <= minDistance + AuthorityOffsetClusterNearestBandBytes)
            .Select(anchor => anchor.Cell)
            .ToList();

        var worldspaces = selectedAnchors
            .Select(cell => cell.WorldspaceFormId!.Value)
            .Distinct()
            .ToList();
        if (worldspaces.Count != 1)
        {
            return false;
        }

        var anchorMinX = selectedAnchors.Min(cell => cell.GridX!.Value) - AuthorityOffsetClusterGridExpansion;
        var anchorMaxX = selectedAnchors.Max(cell => cell.GridX!.Value) + AuthorityOffsetClusterGridExpansion;
        var anchorMinY = selectedAnchors.Min(cell => cell.GridY!.Value) - AuthorityOffsetClusterGridExpansion;
        var anchorMaxY = selectedAnchors.Max(cell => cell.GridY!.Value) + AuthorityOffsetClusterGridExpansion;

        foreach (var placed in cluster)
        {
            var (gx, gy) = CellUtils.WorldToCellCoordinates(placed.X, placed.Y);
            if (gx < anchorMinX || gx > anchorMaxX || gy < anchorMinY || gy > anchorMaxY)
            {
                return false;
            }
        }

        worldspaceFormId = worldspaces[0];
        return true;
    }

    private static long DistanceToOffsetRange(long offset, long minOffset, long maxOffset)
    {
        if (offset < minOffset)
        {
            return minOffset - offset;
        }

        return offset > maxOffset ? offset - maxOffset : 0;
    }

    private static int GetOrCreateAuthorityOffsetVirtualCell(
        RecordCollection records,
        Dictionary<uint, int> cellIndexByFormId,
        uint worldspaceFormId,
        int gridX,
        int gridY,
        bool isBigEndian,
        ref uint nextVirtualFormId,
        out bool created)
    {
        for (var i = 0; i < records.Cells.Count; i++)
        {
            var existingCell = records.Cells[i];
            if (!existingCell.IsUnresolvedBucket &&
                existingCell.WorldspaceFormId == worldspaceFormId &&
                existingCell.GridX == gridX &&
                existingCell.GridY == gridY &&
                !existingCell.IsPersistentCell)
            {
                created = false;
                return i;
            }
        }

        while (cellIndexByFormId.ContainsKey(nextVirtualFormId))
        {
            nextVirtualFormId++;
        }

        var cellFormId = nextVirtualFormId++;
        var newCell = new CellRecord
        {
            FormId = cellFormId,
            GridX = gridX,
            GridY = gridY,
            WorldspaceFormId = worldspaceFormId,
            WorldspaceAssignmentSource = SourceAuthorityOffsetCluster,
            EditorId = $"[Virtual {gridX},{gridY}]",
            PlacedObjects = [],
            IsVirtual = true,
            IsBigEndian = isBigEndian
        };

        records.Cells.Add(newCell);
        var index = records.Cells.Count - 1;
        cellIndexByFormId[cellFormId] = index;
        created = true;
        return index;
    }

    private static uint NextAvailableSyntheticCellFormId(RecordCollection records, uint start)
    {
        var used = records.Cells.Select(cell => cell.FormId).ToHashSet();
        while (used.Contains(start))
        {
            start++;
        }

        return start;
    }

    private static Dictionary<uint, int> BuildCellIndex(List<CellRecord> cells)
    {
        var index = new Dictionary<uint, int>();
        for (var i = 0; i < cells.Count; i++)
        {
            var cell = cells[i];
            if (cell.FormId == 0)
            {
                continue;
            }

            if (!index.TryGetValue(cell.FormId, out var existingIndex) ||
                cells[existingIndex].IsUnresolvedBucket)
            {
                index[cell.FormId] = i;
            }
        }

        return index;
    }

    private static bool TryGetOrCreateAuthorityCell(
        RecordCollection records,
        Dictionary<uint, int> cellIndexByFormId,
        uint cellFormId,
        IReadOnlyDictionary<uint, CellAuthorityMetadata>? cellMetadata,
        IReadOnlyDictionary<uint, uint>? authority,
        EsmRecordScanResult? scanResult,
        out int index,
        out bool created)
    {
        if (cellIndexByFormId.TryGetValue(cellFormId, out index))
        {
            created = false;
            return true;
        }

        CellAuthorityMetadata? metadata = null;
        cellMetadata?.TryGetValue(cellFormId, out metadata);
        uint? worldspaceFormId = metadata?.WorldspaceFormId;
        if ((worldspaceFormId is null or 0u) &&
            authority is not null &&
            authority.TryGetValue(cellFormId, out var authorityWorldspaceFormId))
        {
            worldspaceFormId = authorityWorldspaceFormId;
        }

        if (metadata is null && worldspaceFormId is not > 0)
        {
            created = false;
            return false;
        }

        var isInterior = metadata?.IsInterior ?? (worldspaceFormId is not > 0u);
        var cell = new CellRecord
        {
            FormId = cellFormId,
            Flags = (byte)(isInterior ? 0x01 : 0),
            GridX = isInterior ? null : metadata?.GridX,
            GridY = isInterior ? null : metadata?.GridY,
            WorldspaceFormId = isInterior ? null : worldspaceFormId,
            WorldspaceAssignmentSource = !isInterior && worldspaceFormId is > 0 ? "AuthorityRefParent" : null,
            EditorId = metadata?.EditorId,
            FullName = metadata?.FullName
        };

        records.Cells.Add(cell);
        index = records.Cells.Count - 1;
        cellIndexByFormId[cellFormId] = index;
        created = true;

        if (scanResult is not null && !isInterior && worldspaceFormId is > 0)
        {
            scanResult.CellToWorldspaceMap[cellFormId] = worldspaceFormId.Value;
        }

        return true;
    }

    private static void MoveScanResultRefLink(
        EsmRecordScanResult? scanResult,
        uint sourceCellFormId,
        uint targetCellFormId,
        uint referenceFormId)
    {
        if (scanResult is null || referenceFormId == 0)
        {
            return;
        }

        if (scanResult.CellToRefrMap.TryGetValue(sourceCellFormId, out var sourceRefs))
        {
            sourceRefs.Remove(referenceFormId);
        }

        if (!scanResult.CellToRefrMap.TryGetValue(targetCellFormId, out var targetRefs))
        {
            targetRefs = [];
            scanResult.CellToRefrMap[targetCellFormId] = targetRefs;
        }

        if (!targetRefs.Contains(referenceFormId))
        {
            targetRefs.Add(referenceFormId);
        }
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
