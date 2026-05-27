using System.Globalization;
using System.Text.Json;

namespace FalloutXbox360Utils;

/// <summary>
///     Visibility threshold for the dangling-REFR overlay. Each level expands the
///     previous: <c>High</c> shows only confidently-attributed clusters (HIGH+STRONG —
///     named cell pick); <c>Medium</c> also includes MEDIUM (sole unnamed candidate);
///     <c>Low</c> includes LOW (multiple unnamed candidates, picks largest worldspace).
/// </summary>
public enum DanglingRefThreshold
{
    None,
    High,
    Medium,
    Low
}

/// <summary>
///     Per-grid heuristically-attributed dangling REFR clusters, loaded from the
///     <c>dangling_refs</c> section of <c>cell_worldspace_authority.json</c>.
/// </summary>
public sealed class DanglingRefAttributions
{
    private static readonly string[] AuthorityCandidatePaths =
    [
        Path.Combine(AppContext.BaseDirectory, "data", "cell_worldspace_authority.json"),
        Path.Combine(Directory.GetCurrentDirectory(), "data", "cell_worldspace_authority.json")
    ];

    public Dictionary<(int Gx, int Gy), GridAttribution> Grid { get; } = [];

    /// <summary>
    ///     Per-REFR positions from <c>dangling_refs.positions</c>. Each entry is one unique
    ///     TESObjectREFR (deduped by FormID across all swept dumps) with world coordinates
    ///     and its attributed cell.
    /// </summary>
    public List<DanglingRefPosition> Positions { get; } = [];

    /// <summary>
    ///     Returns the attribution for the given grid if its confidence passes
    ///     <paramref name="threshold" />; otherwise <c>null</c>.
    /// </summary>
    public GridAttribution? Lookup(int gx, int gy, DanglingRefThreshold threshold)
    {
        if (threshold == DanglingRefThreshold.None)
        {
            return null;
        }

        if (!Grid.TryGetValue((gx, gy), out var attribution))
        {
            return null;
        }

        return PassesThreshold(attribution.Confidence, threshold) ? attribution : null;
    }

    public static bool PassesThreshold(string confidence, DanglingRefThreshold threshold)
    {
        return threshold switch
        {
            DanglingRefThreshold.None => false,
            // "ESM" = authoritative ref-to-cell from a Sample ESM — always the highest tier.
            DanglingRefThreshold.High => confidence is "ESM" or "HIGH" or "STRONG",
            DanglingRefThreshold.Medium => confidence is "ESM" or "HIGH" or "STRONG" or "MEDIUM",
            // "Low" also includes "CUT" (no cell at grid in any worldspace) — these are genuinely
            // off-map / cut content. Show them so they're at least discoverable.
            DanglingRefThreshold.Low => true,
            _ => false
        };
    }

    /// <summary>
    ///     Attempts to load the <c>dangling_refs</c> section from the authority JSON
    ///     at the default path. Returns an empty (but valid) instance if no file is
    ///     present or the section is missing — callers can always use <see cref="Lookup" />.
    /// </summary>
    public static DanglingRefAttributions LoadDefault()
    {
        var path = AuthorityCandidatePaths.FirstOrDefault(File.Exists);
        return path is null ? new DanglingRefAttributions() : LoadFromFile(path);
    }

    public static DanglingRefAttributions LoadFromFile(string path)
    {
        var result = new DanglingRefAttributions();
        try
        {
            using var stream = File.OpenRead(path);
            using var doc = JsonDocument.Parse(stream);

            if (!doc.RootElement.TryGetProperty("dangling_refs", out var dangEl) ||
                dangEl.ValueKind != JsonValueKind.Object)
            {
                return result;
            }

            if (!dangEl.TryGetProperty("grid_attributions", out var gridEl) ||
                gridEl.ValueKind != JsonValueKind.Object)
            {
                return result;
            }

            foreach (var prop in gridEl.EnumerateObject())
            {
                if (!TryParseGridKey(prop.Name, out var gx, out var gy) ||
                    prop.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var attr = ReadAttribution(prop.Value, gx, gy);
                if (attr is not null)
                {
                    result.Grid[(gx, gy)] = attr;
                }
            }

            if (dangEl.TryGetProperty("positions", out var positionsEl) &&
                positionsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in positionsEl.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var pos = ReadPosition(entry);
                    if (pos is not null)
                    {
                        result.Positions.Add(pos);
                    }
                }
            }
        }
        catch
        {
            // Authority is optional — never block startup on a malformed file.
        }

        return result;
    }

    private static DanglingRefPosition? ReadPosition(JsonElement el)
    {
        if (!el.TryGetProperty("form_id", out var fidEl) ||
            fidEl.ValueKind != JsonValueKind.String ||
            !TryParseHexUInt(fidEl.GetString(), out var fid))
        {
            return null;
        }

        if (!TryGetFloat(el, "x", out var x) ||
            !TryGetFloat(el, "y", out var y) ||
            !TryGetFloat(el, "z", out var z))
        {
            return null;
        }

        var scale = 1.0f;
        if (TryGetFloat(el, "scale", out var s))
        {
            scale = s;
        }

        var gx = el.TryGetProperty("grid_x", out var gxEl) && gxEl.ValueKind == JsonValueKind.Number
            ? gxEl.GetInt32()
            : (int)Math.Floor(x / 4096f);
        var gy = el.TryGetProperty("grid_y", out var gyEl) && gyEl.ValueKind == JsonValueKind.Number
            ? gyEl.GetInt32()
            : (int)Math.Floor(y / 4096f);

        uint? ws = null;
        if (el.TryGetProperty("worldspace", out var wsEl) &&
            wsEl.ValueKind == JsonValueKind.String &&
            TryParseHexUInt(wsEl.GetString(), out var wsFid))
        {
            ws = wsFid;
        }

        uint cell = 0;
        if (el.TryGetProperty("cell", out var cellEl) &&
            cellEl.ValueKind == JsonValueKind.String &&
            TryParseHexUInt(cellEl.GetString(), out var cellFid))
        {
            cell = cellFid;
        }

        var cellEdid = el.TryGetProperty("cell_editor_id", out var ceEl) &&
                       ceEl.ValueKind == JsonValueKind.String
            ? ceEl.GetString() ?? string.Empty
            : string.Empty;

        uint baseFid = 0;
        if (el.TryGetProperty("base_form_id", out var bfEl) &&
            bfEl.ValueKind == JsonValueKind.String)
        {
            TryParseHexUInt(bfEl.GetString(), out baseFid);
        }

        byte baseFormType = 0;
        if (el.TryGetProperty("base_form_type", out var btEl) &&
            btEl.ValueKind == JsonValueKind.String)
        {
            var typeStr = btEl.GetString();
            if (!string.IsNullOrEmpty(typeStr))
            {
                var span = typeStr.AsSpan();
                if (span.Length > 2 && span[0] == '0' && (span[1] == 'x' || span[1] == 'X'))
                {
                    span = span[2..];
                }
                byte.TryParse(span, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out baseFormType);
            }
        }

        var confidence = el.TryGetProperty("confidence", out var cfEl) &&
                         cfEl.ValueKind == JsonValueKind.String
            ? cfEl.GetString() ?? string.Empty
            : string.Empty;

        if (string.IsNullOrEmpty(confidence))
        {
            return null;
        }

        var found = el.TryGetProperty("found_in_dumps", out var fdEl) && fdEl.ValueKind == JsonValueKind.Number
            ? fdEl.GetInt32()
            : 1;

        var editorId = TryReadString(el, "editor_id");
        var baseEditorId = TryReadString(el, "base_editor_id");
        var baseFullName = TryReadString(el, "base_full_name");
        var modelPath = TryReadString(el, "model_path");
        var recordType = TryReadString(el, "record_type") ?? "REFR";
        var isMapMarker = el.TryGetProperty("is_map_marker", out var mmEl) &&
                          mmEl.ValueKind is JsonValueKind.True;
        var markerName = TryReadString(el, "marker_name");
        ushort? markerType = null;
        if (el.TryGetProperty("marker_type", out var mtEl) && mtEl.ValueKind == JsonValueKind.Number &&
            mtEl.TryGetUInt16(out var mt))
        {
            markerType = mt;
        }

        return new DanglingRefPosition
        {
            FormId = fid,
            X = x, Y = y, Z = z,
            Scale = scale,
            GridX = gx,
            GridY = gy,
            WorldspaceFormId = ws,
            CellFormId = cell,
            CellEditorId = cellEdid,
            BaseFormId = baseFid,
            BaseFormType = baseFormType,
            Confidence = confidence,
            FoundInDumps = found,
            EditorId = editorId,
            BaseEditorId = baseEditorId,
            BaseFullName = baseFullName,
            ModelPath = modelPath,
            RecordType = recordType,
            IsMapMarker = isMapMarker,
            MarkerName = markerName,
            MarkerType = markerType
        };
    }

    private static string? TryReadString(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        var s = v.GetString();
        return string.IsNullOrEmpty(s) ? null : s;
    }

    private static bool TryGetFloat(JsonElement el, string name, out float value)
    {
        value = 0;
        if (!el.TryGetProperty(name, out var f) || f.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        try
        {
            value = f.GetSingle();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static GridAttribution? ReadAttribution(JsonElement el, int gx, int gy)
    {
        uint? worldspace = null;
        if (el.TryGetProperty("worldspace", out var wsEl) &&
            wsEl.ValueKind == JsonValueKind.String &&
            TryParseHexUInt(wsEl.GetString(), out var ws))
        {
            worldspace = ws;
        }

        var worldspaceEditorId = el.TryGetProperty("worldspace_editor_id", out var wsEdEl)
            ? wsEdEl.GetString() ?? string.Empty
            : string.Empty;

        uint cellFormId = 0;
        if (el.TryGetProperty("cell", out var cellEl) &&
            cellEl.ValueKind == JsonValueKind.String &&
            TryParseHexUInt(cellEl.GetString(), out var cell))
        {
            cellFormId = cell;
        }

        var cellEditorId = el.TryGetProperty("cell_editor_id", out var ceEl)
            ? ceEl.GetString() ?? string.Empty
            : string.Empty;

        var confidence = el.TryGetProperty("confidence", out var cfEl)
            ? cfEl.GetString() ?? string.Empty
            : string.Empty;

        var candidates = el.TryGetProperty("candidates", out var caEl) && caEl.ValueKind == JsonValueKind.Number
            ? caEl.GetInt32()
            : 0;

        var refCount = el.TryGetProperty("ref_count", out var rcEl) && rcEl.ValueKind == JsonValueKind.Number
            ? rcEl.GetInt32()
            : 0;

        var evidenceDumpCount = el.TryGetProperty("evidence_dump_count", out var edEl) && edEl.ValueKind == JsonValueKind.Number
            ? edEl.GetInt32()
            : 0;

        var samples = new List<uint>();
        if (el.TryGetProperty("sample_form_ids", out var sampEl) && sampEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in sampEl.EnumerateArray())
            {
                if (s.ValueKind == JsonValueKind.String && TryParseHexUInt(s.GetString(), out var f))
                {
                    samples.Add(f);
                }
            }
        }

        if (string.IsNullOrEmpty(confidence))
        {
            return null;
        }

        return new GridAttribution
        {
            GridX = gx,
            GridY = gy,
            WorldspaceFormId = worldspace,
            WorldspaceEditorId = worldspaceEditorId,
            CellFormId = cellFormId,
            CellEditorId = cellEditorId,
            Confidence = confidence,
            CandidateCount = candidates,
            RefCount = refCount,
            EvidenceDumpCount = evidenceDumpCount,
            SampleFormIds = samples
        };
    }

    private static bool TryParseGridKey(string key, out int gx, out int gy)
    {
        gx = gy = 0;
        if (string.IsNullOrEmpty(key) || key[0] != '(' || key[^1] != ')')
        {
            return false;
        }

        var inner = key.AsSpan(1, key.Length - 2);
        var comma = inner.IndexOf(',');
        if (comma <= 0 || comma >= inner.Length - 1)
        {
            return false;
        }

        return int.TryParse(inner[..comma], NumberStyles.Integer, CultureInfo.InvariantCulture, out gx) &&
               int.TryParse(inner[(comma + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out gy);
    }

    private static bool TryParseHexUInt(string? s, out uint value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(s))
        {
            return false;
        }

        var span = s.AsSpan();
        if (span.Length > 2 && span[0] == '0' && (span[1] == 'x' || span[1] == 'X'))
        {
            span = span[2..];
        }

        return uint.TryParse(span, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }
}

public sealed class GridAttribution
{
    public required int GridX { get; init; }
    public required int GridY { get; init; }
    public uint? WorldspaceFormId { get; init; }
    public string WorldspaceEditorId { get; init; } = string.Empty;
    public uint CellFormId { get; init; }
    public string CellEditorId { get; init; } = string.Empty;
    public string Confidence { get; init; } = string.Empty;
    public int CandidateCount { get; init; }
    public int RefCount { get; init; }
    public int EvidenceDumpCount { get; init; }
    public IReadOnlyList<uint> SampleFormIds { get; init; } = [];
}

/// <summary>
///     Per-REFR position record. <see cref="Confidence" /> is one of:
///     <c>ESM</c> (authoritative — FormID exists as child REFR of <see cref="CellFormId" /> in a Sample ESM),
///     <c>HIGH/STRONG/MEDIUM/LOW</c> (grid-heuristic fallback), or
///     <c>CUT</c> (no cell at this grid in any worldspace).
/// </summary>
public sealed class DanglingRefPosition
{
    public required uint FormId { get; init; }
    public required float X { get; init; }
    public required float Y { get; init; }
    public required float Z { get; init; }
    public float Scale { get; init; } = 1.0f;
    public required int GridX { get; init; }
    public required int GridY { get; init; }
    public uint? WorldspaceFormId { get; init; }
    public uint CellFormId { get; init; }
    public string CellEditorId { get; init; } = string.Empty;
    public uint BaseFormId { get; init; }
    public byte BaseFormType { get; init; }
    public required string Confidence { get; init; }
    public int FoundInDumps { get; init; } = 1;

    // -- ESM-side enrichment (populated when `dmp attribute-dangling --esm ...` matched
    // this FormID in a Sample ESM). Optional — older authority JSONs lack these fields. --

    /// <summary>REFR's own EDID, if any (most REFRs have none).</summary>
    public string? EditorId { get; init; }

    /// <summary>Base record's EDID, e.g. "MrHouseTerminalNV".</summary>
    public string? BaseEditorId { get; init; }

    /// <summary>Base record's FULL name, e.g. "Mr. House".</summary>
    public string? BaseFullName { get; init; }

    /// <summary>Base record's MODL path (NIF mesh).</summary>
    public string? ModelPath { get; init; }

    /// <summary>"REFR" / "ACHR" / "ACRE" — defaults to "REFR" when not enriched.</summary>
    public string RecordType { get; init; } = "REFR";

    /// <summary>True when the ESM REFR carries an XMRK subrecord (map marker).</summary>
    public bool IsMapMarker { get; init; }

    /// <summary>Map marker display name (FULL on XMRK REFRs).</summary>
    public string? MarkerName { get; init; }

    /// <summary>Map marker type byte (1=City … 14=Vault), or null when not a marker.</summary>
    public ushort? MarkerType { get; init; }
}
