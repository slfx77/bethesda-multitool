using System.Globalization;
using System.Text.Json;

namespace EsmAnalyzer.Commands;

/// <summary>
///     Stream-rewrites the authority JSON for the attribute-dangling pipeline: preserves every
///     existing top-level section verbatim, bumps schema_version to 3, and emits a fresh
///     `dangling_refs` section from the attribution result.
/// </summary>
internal static class DmpDanglingWriter
{
    public static void RewriteAuthority(string inPath, string outPath, AttributionResult attribution)
    {
        // Read input fully into memory and parse before opening the output stream — when
        // inPath == outPath the read handle must be released before File.Create can succeed.
        var inputBytes = File.ReadAllBytes(inPath);
        using var doc = JsonDocument.Parse(inputBytes);

        using var outStream = File.Create(outPath);
        using var w = new Utf8JsonWriter(outStream, new JsonWriterOptions { Indented = true });

        w.WriteStartObject();
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            switch (prop.Name)
            {
                case "schema_version":
                    w.WriteNumber("schema_version", 3);
                    break;
                case "dangling_refs":
                    // Skip prior dangling_refs — we re-emit fresh below.
                    break;
                default:
                    // Preserve verbatim: cells, references, conflicts, sources, worldspaces,
                    // generated_at (the underlying authority data's timestamp), etc.
                    prop.WriteTo(w);
                    break;
            }
        }

        WriteDanglingRefs(w, attribution);

        w.WriteEndObject();
        w.Flush();
    }

    private static void WriteDanglingRefs(Utf8JsonWriter w, AttributionResult attr)
    {
        w.WriteStartObject("dangling_refs");
        w.WriteString("attributed_at",
            DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
        w.WriteString("source", "dmp sweep-refrs + grid-attribution heuristic");

        // Stats
        w.WriteStartObject("stats");
        var totalAttributed = attr.GridAttributions.Sum(kv => kv.Value.RefCount);
        var totalCut = attr.CutRegions.Sum(c => c.RefCount);
        w.WriteNumber("total_attributed", totalAttributed);
        w.WriteNumber("total_cut", totalCut);
        w.WriteNumber("grid_count", attr.GridAttributions.Count);
        w.WriteNumber("cut_grid_count", attr.CutRegions.Count);
        w.WriteStartObject("by_confidence");
        var byConf = attr.GridAttributions.GroupBy(kv => kv.Value.Confidence)
            .ToDictionary(g => g.Key, g => g.Sum(kv => kv.Value.RefCount));
        foreach (var k in new[] { "HIGH", "STRONG", "MEDIUM", "LOW" })
        {
            w.WriteNumber(k, byConf.GetValueOrDefault(k, 0));
        }
        w.WriteEndObject();
        w.WriteEndObject();

        // Grid attributions
        w.WriteStartObject("grid_attributions");
        foreach (var kv in attr.GridAttributions.OrderBy(kv => kv.Key.Gx).ThenBy(kv => kv.Key.Gy))
        {
            var (gx, gy) = kv.Key;
            var g = kv.Value;
            w.WriteStartObject($"({gx},{gy})");
            if (g.WorldspaceFormId != 0)
            {
                w.WriteString("worldspace", $"0x{g.WorldspaceFormId:X8}");
                if (!string.IsNullOrEmpty(g.WorldspaceEditorId))
                {
                    w.WriteString("worldspace_editor_id", g.WorldspaceEditorId);
                }
            }
            w.WriteString("cell", $"0x{g.CellFormId:X8}");
            if (!string.IsNullOrEmpty(g.CellEditorId))
            {
                w.WriteString("cell_editor_id", g.CellEditorId);
            }
            w.WriteNumber("grid_x", gx);
            w.WriteNumber("grid_y", gy);
            w.WriteString("confidence", g.Confidence);
            w.WriteNumber("candidates", g.CandidateCount);
            w.WriteNumber("ref_count", g.RefCount);
            w.WriteNumber("evidence_dump_count", g.EvidenceDumps.Count);
            if (g.SampleFormIds.Count > 0)
            {
                w.WriteStartArray("sample_form_ids");
                foreach (var f in g.SampleFormIds)
                {
                    w.WriteStringValue($"0x{f:X8}");
                }
                w.WriteEndArray();
            }
            w.WriteEndObject();
        }
        w.WriteEndObject();

        // Cut regions (no cell at grid in any worldspace)
        w.WriteStartArray("cut_regions");
        foreach (var c in attr.CutRegions.OrderBy(c => c.Gx).ThenBy(c => c.Gy))
        {
            w.WriteStartObject();
            w.WriteNumber("grid_x", c.Gx);
            w.WriteNumber("grid_y", c.Gy);
            w.WriteNumber("ref_count", c.RefCount);
            w.WriteNumber("evidence_dump_count", c.EvidenceDumps.Count);
            if (c.SampleFormIds.Count > 0)
            {
                w.WriteStartArray("sample_form_ids");
                foreach (var f in c.SampleFormIds)
                {
                    w.WriteStringValue($"0x{f:X8}");
                }
                w.WriteEndArray();
            }
            w.WriteEndObject();
        }
        w.WriteEndArray();

        // Per-REFR positions (when --positions was supplied)
        if (attr.Positions.Count > 0)
        {
            w.WriteStartArray("positions");
            foreach (var p in attr.Positions.OrderBy(p => p.FormId))
            {
                w.WriteStartObject();
                w.WriteString("form_id", $"0x{p.FormId:X8}");
                w.WriteNumber("x", p.X);
                w.WriteNumber("y", p.Y);
                w.WriteNumber("z", p.Z);
                if (Math.Abs(p.Scale - 1.0f) > 0.001f)
                {
                    w.WriteNumber("scale", p.Scale);
                }
                w.WriteNumber("grid_x", p.GridX);
                w.WriteNumber("grid_y", p.GridY);
                if (p.WorldspaceFormId.HasValue && p.WorldspaceFormId.Value != 0)
                {
                    w.WriteString("worldspace", $"0x{p.WorldspaceFormId.Value:X8}");
                }
                if (p.CellFormId != 0)
                {
                    w.WriteString("cell", $"0x{p.CellFormId:X8}");
                }
                if (!string.IsNullOrEmpty(p.CellEditorId))
                {
                    w.WriteString("cell_editor_id", p.CellEditorId);
                }
                if (p.BaseFormId != 0)
                {
                    w.WriteString("base_form_id", $"0x{p.BaseFormId:X8}");
                }
                if (p.BaseFormType != 0)
                {
                    w.WriteString("base_form_type", $"0x{p.BaseFormType:X2}");
                }
                w.WriteString("confidence", p.Confidence);
                if (p.FoundInDumps > 1)
                {
                    w.WriteNumber("found_in_dumps", p.FoundInDumps);
                }
                if (!string.IsNullOrEmpty(p.EditorId))
                {
                    w.WriteString("editor_id", p.EditorId);
                }
                if (!string.IsNullOrEmpty(p.BaseEditorId))
                {
                    w.WriteString("base_editor_id", p.BaseEditorId);
                }
                if (!string.IsNullOrEmpty(p.BaseFullName))
                {
                    w.WriteString("base_full_name", p.BaseFullName);
                }
                if (!string.IsNullOrEmpty(p.ModelPath))
                {
                    w.WriteString("model_path", p.ModelPath);
                }
                if (!string.IsNullOrEmpty(p.RecordType) && p.RecordType != "REFR")
                {
                    w.WriteString("record_type", p.RecordType);
                }
                if (p.IsMapMarker)
                {
                    w.WriteBoolean("is_map_marker", true);
                    if (!string.IsNullOrEmpty(p.MarkerName))
                    {
                        w.WriteString("marker_name", p.MarkerName);
                    }
                    if (p.MarkerType.HasValue)
                    {
                        w.WriteNumber("marker_type", p.MarkerType.Value);
                    }
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }

        w.WriteEndObject();
    }
}
