using System.Globalization;
using System.Text.Json;

namespace BethesdaMultitool.CLI.Commands.Dmp;

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
