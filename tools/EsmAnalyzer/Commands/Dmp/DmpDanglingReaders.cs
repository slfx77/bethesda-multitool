using System.Globalization;
using System.Text.Json;

namespace EsmAnalyzer.Commands.Dmp;

/// <summary>
///     Input readers for the attribute-dangling pipeline: parses the authority JSON into a
///     light <see cref="AuthorityView" />, and the sweep / positions CSVs into row records.
/// </summary>
internal static class DmpDanglingReaders
{
    // ---------------- Authority loading (light: just what we need) ----------------

    public static AuthorityView LoadAuthority(string path)
    {
        var view = new AuthorityView();
        using var stream = File.OpenRead(path);
        using var doc = JsonDocument.Parse(stream);

        if (doc.RootElement.TryGetProperty("worldspaces", out var wsEl) && wsEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in wsEl.EnumerateObject())
            {
                if (DmpDanglingParsing.TryParseHexUInt(p.Name, out var fid) &&
                    p.Value.ValueKind == JsonValueKind.String)
                {
                    view.WorldspaceNames[fid] = p.Value.GetString() ?? "";
                }
            }
        }

        if (doc.RootElement.TryGetProperty("cells", out var cellsEl) && cellsEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in cellsEl.EnumerateObject())
            {
                if (!DmpDanglingParsing.TryParseHexUInt(p.Name, out var cellFid) ||
                    p.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                uint? ws = null;
                int? gx = null, gy = null;
                var isInterior = false;
                var edid = "";

                if (p.Value.TryGetProperty("worldspace", out var wsField) &&
                    wsField.ValueKind == JsonValueKind.String &&
                    DmpDanglingParsing.TryParseHexUInt(wsField.GetString(), out var wsFid))
                {
                    ws = wsFid;
                }

                if (p.Value.TryGetProperty("is_interior", out var iiField) &&
                    iiField.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    isInterior = iiField.GetBoolean();
                }

                if (p.Value.TryGetProperty("grid_x", out var gxField) && gxField.ValueKind == JsonValueKind.Number)
                {
                    gx = gxField.GetInt32();
                }

                if (p.Value.TryGetProperty("grid_y", out var gyField) && gyField.ValueKind == JsonValueKind.Number)
                {
                    gy = gyField.GetInt32();
                }

                if (p.Value.TryGetProperty("editor_id", out var edEl) && edEl.ValueKind == JsonValueKind.String)
                {
                    edid = edEl.GetString() ?? "";
                }

                var info = new CellInfo(cellFid, ws, gx, gy, isInterior, edid);
                view.Cells[cellFid] = info;

                if (!isInterior && ws.HasValue && gx.HasValue && gy.HasValue)
                {
                    if (!view.CellsByGrid.TryGetValue((gx.Value, gy.Value), out var list))
                    {
                        list = [];
                        view.CellsByGrid[(gx.Value, gy.Value)] = list;
                    }

                    list.Add(info);

                    view.WorldspaceCellCounts.TryGetValue(ws.Value, out var c);
                    view.WorldspaceCellCounts[ws.Value] = c + 1;
                }
            }
        }

        // ESM-derived ref→cell map — authoritative when present (any of the Sample ESMs
        // ingested by `dmp build-cell-authority` recorded this ref as a child of that cell).
        if (doc.RootElement.TryGetProperty("references", out var refsEl) && refsEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in refsEl.EnumerateObject())
            {
                if (DmpDanglingParsing.TryParseHexUInt(p.Name, out var refFid) &&
                    p.Value.ValueKind == JsonValueKind.String &&
                    DmpDanglingParsing.TryParseHexUInt(p.Value.GetString(), out var cellFid))
                {
                    view.RefToCell[refFid] = cellFid;
                }
            }
        }

        return view;
    }

    // ---------------- Sweep CSV loading ----------------

    public static List<SweepRow> LoadSweep(string path, int minRefs, int nearOriginCutoff)
    {
        var rows = new List<SweepRow>();
        using var reader = new StreamReader(path);
        var header = reader.ReadLine();
        if (header == null)
        {
            return rows;
        }

        var cols = DmpDanglingParsing.SplitCsv(header);

        int IdxOf(string n)
        {
            return Array.IndexOf(cols, n);
        }

        var iDump = IdxOf("Dump");
        var iGx = IdxOf("GridX");
        var iGy = IdxOf("GridY");
        var iTotal = IdxOf("TotalRefrs");
        var iNull = IdxOf("NullParentRefrs");
        var iDist = IdxOf("DistinctFormIds");
        var iSamp = IdxOf("SampleFormIds");

        if (iDump < 0 || iGx < 0 || iGy < 0 || iTotal < 0 || iNull < 0 || iDist < 0 || iSamp < 0)
        {
            throw new InvalidDataException("sweep CSV missing required columns");
        }

        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var parts = DmpDanglingParsing.SplitCsv(line);
            if (parts.Length <= Math.Max(iSamp, iDist))
            {
                continue;
            }

            if (!int.TryParse(parts[iGx], out var gx) ||
                !int.TryParse(parts[iGy], out var gy) ||
                !int.TryParse(parts[iTotal], out var total) ||
                !int.TryParse(parts[iNull], out var nullp) ||
                !int.TryParse(parts[iDist], out var dist))
            {
                continue;
            }

            if (nullp < minRefs)
            {
                continue;
            }

            if (gx * gx + gy * gy < nearOriginCutoff)
            {
                continue;
            }

            rows.Add(new SweepRow(parts[iDump], gx, gy, total, nullp, dist, parts[iSamp]));
        }

        return rows;
    }

    // ---------------- Per-REFR positions loading ----------------

    public static List<PositionRow> LoadPositions(string path)
    {
        var rows = new List<PositionRow>();
        using var reader = new StreamReader(path);
        var header = reader.ReadLine();
        if (header == null)
        {
            return rows;
        }

        var cols = DmpDanglingParsing.SplitCsv(header);

        int IdxOf(string n)
        {
            return Array.IndexOf(cols, n);
        }

        var iFid = IdxOf("FormId");
        var iX = IdxOf("X");
        var iY = IdxOf("Y");
        var iZ = IdxOf("Z");
        var iScale = IdxOf("Scale");
        var iGx = IdxOf("GridX");
        var iGy = IdxOf("GridY");
        var iBaseFid = IdxOf("BaseFormId");
        var iBaseType = IdxOf("BaseFormType");
        var iDumps = IdxOf("FoundInDumps");
        if (iFid < 0 || iX < 0 || iY < 0 || iZ < 0 || iGx < 0 || iGy < 0)
        {
            throw new InvalidDataException("positions CSV missing required columns");
        }

        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var parts = DmpDanglingParsing.SplitCsv(line);
            if (parts.Length <= Math.Max(iY, Math.Max(iGy, iDumps < 0 ? 0 : iDumps)))
            {
                continue;
            }

            if (!DmpDanglingParsing.TryParseHexUInt(parts[iFid], out var fid))
            {
                continue;
            }

            if (!float.TryParse(parts[iX], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
                !float.TryParse(parts[iY], NumberStyles.Float, CultureInfo.InvariantCulture, out var y) ||
                !float.TryParse(parts[iZ], NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
            {
                continue;
            }

            var scale = 1.0f;
            if (iScale >= 0 && iScale < parts.Length)
            {
                float.TryParse(parts[iScale], NumberStyles.Float, CultureInfo.InvariantCulture, out scale);
                if (scale <= 0.001f || float.IsNaN(scale))
                {
                    scale = 1.0f;
                }
            }

            if (!int.TryParse(parts[iGx], out var gx) || !int.TryParse(parts[iGy], out var gy))
            {
                continue;
            }

            var foundInDumps = 1;
            if (iDumps >= 0 && iDumps < parts.Length)
            {
                foundInDumps = int.TryParse(parts[iDumps], out var dumps) ? dumps : 0;
            }

            uint baseFid = 0;
            if (iBaseFid >= 0 && iBaseFid < parts.Length &&
                DmpDanglingParsing.TryParseHexUInt(parts[iBaseFid], out var parsedBaseFid))
            {
                baseFid = parsedBaseFid;
            }

            byte baseType = 0;
            if (iBaseType >= 0 && iBaseType < parts.Length && !string.IsNullOrWhiteSpace(parts[iBaseType]))
            {
                var s = parts[iBaseType];
                if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    s = s[2..];
                }

                byte.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out baseType);
            }

            rows.Add(new PositionRow(fid, x, y, z, scale, gx, gy, baseFid, baseType, foundInDumps));
        }

        return rows;
    }
}