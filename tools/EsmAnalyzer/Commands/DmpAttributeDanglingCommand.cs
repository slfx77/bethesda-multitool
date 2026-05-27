using System.CommandLine;
using System.Globalization;
using System.Text;
using System.Text.Json;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Formats.Esm.Enums;
using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using FalloutXbox360Utils.Core.Semantic;
using Spectre.Console;

namespace EsmAnalyzer.Commands;

/// <summary>
///     Reads a dangling-REFR sweep CSV (produced by `dmp sweep-refrs`) and applies
///     a grid-attribution heuristic against the cell_worldspace_authority.json:
///     for each (gridX, gridY) carrying dangling REFRs, find candidate cells (any
///     worldspace), rank by (has editor_id desc, owning-worldspace cell count desc),
///     and pick the winner.
///
///     Emits a new `dangling_refs` section into the authority JSON (schema_version
///     bumps 2 -&gt; 3). All other top-level sections are preserved verbatim.
/// </summary>
internal static class DmpAttributeDanglingCommand
{
    public static Command CreateAttributeDanglingCommand()
    {
        var command = new Command(
            "attribute-dangling",
            "Apply grid-attribution heuristic to a sweep-refrs CSV and inject a dangling_refs section " +
            "into cell_worldspace_authority.json (schema_version 2 -> 3).");

        var sweepOpt = new Option<string>("--sweep")
        {
            Description = "Path to dangling_refr_sweep_all.csv (from `dmp sweep-refrs`)",
            Required = true
        };
        var authOpt = new Option<string?>("--authority")
        {
            Description = "Path to existing authority JSON (default: data/cell_worldspace_authority.json)"
        };
        var outOpt = new Option<string?>("-o", "--output")
        {
            Description = "Output JSON path (default: overwrite input)"
        };
        var minRefsOpt = new Option<int>("--min-refs")
        {
            Description = "Skip grid attributions with fewer than this many NULL-parent REFRs",
            DefaultValueFactory = _ => 1
        };
        var excludeNearOriginOpt = new Option<int>("--exclude-near-origin")
        {
            Description = "Skip grids with (gx² + gy²) below this threshold (filters interior/uninitialized noise)",
            DefaultValueFactory = _ => 25
        };
        var positionsOpt = new Option<string?>("--positions")
        {
            Description =
                "Optional path to per-REFR positions CSV (from `dmp sweep-refrs --per-refr-out`). " +
                "When supplied, the dangling_refs section also includes a `positions` array " +
                "with FormID, world position, scale, and attributed cell per REFR."
        };
        var esmOpt = new Option<string[]?>("--esm")
        {
            Description =
                "Path to a Sample ESM/ESP used to enrich positions with editor IDs, base names, " +
                "model paths, and map-marker metadata. Repeat to ingest multiple builds; later " +
                "ESMs only fill gaps left by earlier ones.",
            AllowMultipleArgumentsPerToken = true
        };

        command.Options.Add(sweepOpt);
        command.Options.Add(authOpt);
        command.Options.Add(outOpt);
        command.Options.Add(minRefsOpt);
        command.Options.Add(excludeNearOriginOpt);
        command.Options.Add(positionsOpt);
        command.Options.Add(esmOpt);

        command.SetAction(async (parseResult, ct) =>
        {
            var sweep = parseResult.GetValue(sweepOpt)!;
            var authority = parseResult.GetValue(authOpt)
                            ?? Path.Combine("data", "cell_worldspace_authority.json");
            var output = parseResult.GetValue(outOpt) ?? authority;
            var minRefs = parseResult.GetValue(minRefsOpt);
            var nearOrigin = parseResult.GetValue(excludeNearOriginOpt);
            var positions = parseResult.GetValue(positionsOpt);
            var esms = parseResult.GetValue(esmOpt) ?? [];
            await RunAsync(sweep, authority, output, minRefs, nearOrigin, positions, esms, ct);
        });

        return command;
    }

    private static async Task<int> RunAsync(string sweepCsv, string authorityIn, string authorityOut, int minRefs,
        int nearOriginCutoff, string? positionsCsv, string[] esmPaths, CancellationToken ct)
    {
        if (!File.Exists(sweepCsv))
        {
            AnsiConsole.MarkupLine($"[red]Sweep CSV not found:[/] {Markup.Escape(sweepCsv)}");
            return 1;
        }

        if (!File.Exists(authorityIn))
        {
            AnsiConsole.MarkupLine($"[red]Authority JSON not found:[/] {Markup.Escape(authorityIn)}");
            return 1;
        }

        AnsiConsole.MarkupLine($"[blue]Loading authority:[/] {Markup.Escape(authorityIn)}");
        var auth = LoadAuthority(authorityIn);
        AnsiConsole.MarkupLine(
            $"  worldspaces: [cyan]{auth.WorldspaceNames.Count}[/]  cells: [cyan]{auth.Cells.Count:N0}[/]  " +
            $"grids-with-cells: [cyan]{auth.CellsByGrid.Count:N0}[/]");

        AnsiConsole.MarkupLine($"[blue]Reading sweep:[/] {Markup.Escape(sweepCsv)}");
        var sweepRows = LoadSweep(sweepCsv, minRefs, nearOriginCutoff);
        AnsiConsole.MarkupLine($"  (dump, cell) buckets after filter: [cyan]{sweepRows.Count:N0}[/]");

        AnsiConsole.MarkupLine($"[blue]Applying heuristic...[/]");
        var attributions = Attribute(auth, sweepRows);

        if (!string.IsNullOrEmpty(positionsCsv) && File.Exists(positionsCsv))
        {
            AnsiConsole.MarkupLine($"[blue]Reading positions:[/] {Markup.Escape(positionsCsv)}");
            var positions = LoadPositions(positionsCsv);
            AnsiConsole.MarkupLine($"  unique REFRs: [cyan]{positions.Count:N0}[/]");

            var enrichment = await BuildEnrichmentIndexAsync(esmPaths, ct);
            AttributePositions(auth, attributions, positions, enrichment);

            var byPosConfidence = attributions.Positions.GroupBy(p => p.Confidence)
                .ToDictionary(g => g.Key, g => g.Count());
            AnsiConsole.MarkupLine($"  attributed positions: [green]{attributions.Positions.Count:N0}[/]");
            foreach (var k in new[] { "ESM", "HIGH", "STRONG", "MEDIUM", "LOW", "CUT" })
            {
                var n = byPosConfidence.GetValueOrDefault(k, 0);
                if (n > 0)
                {
                    AnsiConsole.MarkupLine($"    {k,-8}: [cyan]{n:N0}[/]");
                }
            }

            if (enrichment.Count > 0)
            {
                var enrichedCount = attributions.Positions.Count(p => p.HasEnrichment);
                AnsiConsole.MarkupLine($"  enriched from ESMs: [green]{enrichedCount:N0}[/]");
            }
        }
        else if (!string.IsNullOrEmpty(positionsCsv))
        {
            AnsiConsole.MarkupLine($"[yellow]Positions CSV not found:[/] {Markup.Escape(positionsCsv)} (skipping)");
        }

        AnsiConsole.MarkupLine($"  grid attributions: [green]{attributions.GridAttributions.Count:N0}[/]");
        AnsiConsole.MarkupLine($"  cut regions     : [yellow]{attributions.CutRegions.Count:N0}[/]");

        var byConfidence = attributions.GridAttributions
            .GroupBy(kv => kv.Value.Confidence)
            .ToDictionary(g => g.Key, g => g.Sum(kv => kv.Value.RefCount));
        var byWs = attributions.GridAttributions
            .GroupBy(kv => kv.Value.WorldspaceFormId)
            .ToDictionary(g => g.Key, g => g.Sum(kv => kv.Value.RefCount));

        var total = attributions.GridAttributions.Sum(kv => kv.Value.RefCount);
        var totalCut = attributions.CutRegions.Sum(c => c.RefCount);
        AnsiConsole.MarkupLine($"  total attributed REFRs: [green]{total:N0}[/]  cut: [yellow]{totalCut:N0}[/]");

        AnsiConsole.WriteLine();
        var conf = new Table().Border(TableBorder.Rounded).AddColumn("Confidence").AddColumn("REFRs", c => c.RightAligned());
        foreach (var k in new[] { "HIGH", "STRONG", "MEDIUM", "LOW" })
        {
            conf.AddRow(k, byConfidence.GetValueOrDefault(k, 0).ToString("N0", CultureInfo.InvariantCulture));
        }
        AnsiConsole.Write(conf);

        AnsiConsole.WriteLine();
        var ws = new Table().Border(TableBorder.Rounded).AddColumn("Worldspace").AddColumn("REFRs", c => c.RightAligned());
        foreach (var (id, n) in byWs.OrderByDescending(kv => kv.Value).Take(10))
        {
            var name = auth.WorldspaceNames.GetValueOrDefault(id, "?");
            ws.AddRow($"0x{id:X8} {Markup.Escape(name)}", n.ToString("N0", CultureInfo.InvariantCulture));
        }
        AnsiConsole.Write(ws);

        AnsiConsole.MarkupLine($"\n[blue]Writing:[/] {Markup.Escape(authorityOut)}");
        RewriteAuthority(authorityIn, authorityOut, attributions);
        AnsiConsole.MarkupLine($"[green]Done.[/] schema_version bumped to 3.");
        return 0;
    }

    // ---------------- Authority loading (light: just what we need) ----------------

    private sealed class AuthorityView
    {
        public Dictionary<uint, string> WorldspaceNames { get; } = [];
        public Dictionary<uint, CellInfo> Cells { get; } = [];
        public Dictionary<(int, int), List<CellInfo>> CellsByGrid { get; } = [];
        public Dictionary<uint, int> WorldspaceCellCounts { get; } = [];

        /// <summary>
        ///     ESM-derived ref→cell map (from `references` section). Authoritative when present —
        ///     means the FormID exists as a child REFR of that CELL in at least one Sample ESM.
        /// </summary>
        public Dictionary<uint, uint> RefToCell { get; } = [];
    }

    private sealed record CellInfo(uint FormId, uint? WorldspaceFormId, int? GridX, int? GridY, bool IsInterior, string EditorId);

    private static AuthorityView LoadAuthority(string path)
    {
        var view = new AuthorityView();
        using var stream = File.OpenRead(path);
        using var doc = JsonDocument.Parse(stream);

        if (doc.RootElement.TryGetProperty("worldspaces", out var wsEl) && wsEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in wsEl.EnumerateObject())
            {
                if (TryParseHexUInt(p.Name, out var fid) && p.Value.ValueKind == JsonValueKind.String)
                {
                    view.WorldspaceNames[fid] = p.Value.GetString() ?? "";
                }
            }
        }

        if (doc.RootElement.TryGetProperty("cells", out var cellsEl) && cellsEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in cellsEl.EnumerateObject())
            {
                if (!TryParseHexUInt(p.Name, out var cellFid) || p.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                uint? ws = null;
                int? gx = null, gy = null;
                var isInterior = false;
                var edid = "";

                if (p.Value.TryGetProperty("worldspace", out var wsField) &&
                    wsField.ValueKind == JsonValueKind.String &&
                    TryParseHexUInt(wsField.GetString(), out var wsFid))
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
                if (TryParseHexUInt(p.Name, out var refFid) &&
                    p.Value.ValueKind == JsonValueKind.String &&
                    TryParseHexUInt(p.Value.GetString(), out var cellFid))
                {
                    view.RefToCell[refFid] = cellFid;
                }
            }
        }

        return view;
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

    // ---------------- Sweep CSV loading ----------------

    private sealed record SweepRow(
        string Dump,
        int GridX,
        int GridY,
        int TotalRefrs,
        int NullParentRefrs,
        int DistinctFormIds,
        string SampleFormIds);

    private static List<SweepRow> LoadSweep(string path, int minRefs, int nearOriginCutoff)
    {
        var rows = new List<SweepRow>();
        using var reader = new StreamReader(path);
        var header = reader.ReadLine();
        if (header == null)
        {
            return rows;
        }

        var cols = SplitCsv(header);
        int IdxOf(string n) => Array.IndexOf(cols, n);
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
            var parts = SplitCsv(line);
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

    private static string[] SplitCsv(string line)
    {
        var parts = new List<string>();
        var cur = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (c == ',' && !inQuotes)
            {
                parts.Add(cur.ToString());
                cur.Clear();
            }
            else
            {
                cur.Append(c);
            }
        }
        parts.Add(cur.ToString());
        return parts.ToArray();
    }

    // ---------------- Attribution heuristic ----------------

    private sealed class AttributionResult
    {
        public Dictionary<(int Gx, int Gy), GridAttribution> GridAttributions { get; } = [];
        public List<CutRegion> CutRegions { get; } = [];

        /// <summary>Per-REFR position records, populated when --positions is supplied.</summary>
        public List<PositionAttribution> Positions { get; } = [];
    }

    private sealed class PositionAttribution
    {
        public uint FormId { get; init; }
        public float X { get; init; }
        public float Y { get; init; }
        public float Z { get; init; }
        public float Scale { get; init; }
        public int GridX { get; init; }
        public int GridY { get; init; }
        public int FoundInDumps { get; init; }
        public uint? WorldspaceFormId { get; init; }
        public uint CellFormId { get; init; }
        public string CellEditorId { get; init; } = "";
        public uint BaseFormId { get; init; }
        public byte BaseFormType { get; init; }

        /// <summary>
        ///     Attribution source / strength. "ESM" = exact ref-to-cell from a Sample ESM
        ///     (authoritative). "HIGH"/"STRONG"/"MEDIUM"/"LOW" = grid-heuristic fallback.
        ///     "CUT" = no cell at this grid in any worldspace.
        /// </summary>
        public string Confidence { get; init; } = "";

        // -- ESM enrichment (populated when --esm matched this FormID in a Sample ESM) --

        /// <summary>REFR's own EDID subrecord (uncommon — most REFRs have no name).</summary>
        public string? EditorId { get; init; }

        /// <summary>Base record's EDID (e.g. "Mr_HouseNV").</summary>
        public string? BaseEditorId { get; init; }

        /// <summary>Base record's FULL name (e.g. "Mr. House").</summary>
        public string? BaseFullName { get; init; }

        /// <summary>Base record's model path (NIF).</summary>
        public string? ModelPath { get; init; }

        /// <summary>"REFR" / "ACHR" / "ACRE" per the ESM record type.</summary>
        public string? RecordType { get; init; }

        /// <summary>True if the ESM REFR carries an XMRK subrecord.</summary>
        public bool IsMapMarker { get; init; }

        /// <summary>Map marker display name (FULL on the REFR for XMRK records).</summary>
        public string? MarkerName { get; init; }

        /// <summary>Map marker type byte (1=City … 14=Vault).</summary>
        public ushort? MarkerType { get; init; }

        /// <summary>True if any of the enrichment fields were populated from an ESM.</summary>
        public bool HasEnrichment =>
            EditorId is not null || BaseEditorId is not null || BaseFullName is not null ||
            ModelPath is not null || RecordType is not null || IsMapMarker ||
            MarkerName is not null || MarkerType.HasValue;
    }

    /// <summary>
    ///     ESM-side enrichment for a placed reference, indexed by REFR FormID. Built once per
    ///     attribute-dangling run by walking every Sample ESM's parsed <see cref="PlacedReference" />
    ///     records. Later ESMs only fill gaps left by earlier ones, so the first ESM listed wins
    ///     when the same FormID appears in multiple builds.
    /// </summary>
    private sealed record ReferenceEnrichment(
        string? EditorId,
        uint BaseFormId,
        string? BaseEditorId,
        string? BaseFullName,
        string? ModelPath,
        string RecordType,
        bool IsMapMarker,
        string? MarkerName,
        ushort? MarkerType);

    private sealed class GridAttribution
    {
        public uint WorldspaceFormId { get; init; }
        public string WorldspaceEditorId { get; init; } = "";
        public uint CellFormId { get; init; }
        public string CellEditorId { get; init; } = "";
        public int CandidateCount { get; init; }
        public string Confidence { get; init; } = "";
        public int RefCount; // mutable: accumulate across dumps
        public HashSet<string> EvidenceDumps { get; } = new(StringComparer.Ordinal);
        public HashSet<uint> SampleFormIds { get; } = [];
    }

    private sealed class CutRegion
    {
        public int Gx { get; init; }
        public int Gy { get; init; }
        public int RefCount;
        public HashSet<string> EvidenceDumps { get; } = new(StringComparer.Ordinal);
        public HashSet<uint> SampleFormIds { get; } = [];
    }

    private static AttributionResult Attribute(AuthorityView auth, IEnumerable<SweepRow> rows)
    {
        var result = new AttributionResult();

        foreach (var row in rows)
        {
            var key = (row.GridX, row.GridY);
            if (auth.CellsByGrid.TryGetValue(key, out var candidates) && candidates.Count > 0)
            {
                // Rank: has editor_id desc, worldspace cell count desc, ws_id asc (stable)
                var winner = candidates
                    .OrderByDescending(c => !string.IsNullOrWhiteSpace(c.EditorId))
                    .ThenByDescending(c => c.WorldspaceFormId.HasValue
                        ? auth.WorldspaceCellCounts.GetValueOrDefault(c.WorldspaceFormId.Value, 0)
                        : 0)
                    .ThenBy(c => c.WorldspaceFormId)
                    .First();

                var hasEdid = !string.IsNullOrWhiteSpace(winner.EditorId);
                string confidence;
                if (candidates.Count == 1 && hasEdid)
                {
                    confidence = "HIGH";
                }
                else if (hasEdid)
                {
                    confidence = "STRONG";
                }
                else if (candidates.Count == 1)
                {
                    confidence = "MEDIUM";
                }
                else
                {
                    confidence = "LOW";
                }

                if (!result.GridAttributions.TryGetValue(key, out var attr))
                {
                    attr = new GridAttribution
                    {
                        WorldspaceFormId = winner.WorldspaceFormId ?? 0,
                        WorldspaceEditorId = winner.WorldspaceFormId.HasValue
                            ? auth.WorldspaceNames.GetValueOrDefault(winner.WorldspaceFormId.Value, "")
                            : "",
                        CellFormId = winner.FormId,
                        CellEditorId = winner.EditorId,
                        CandidateCount = candidates.Count,
                        Confidence = confidence
                    };
                    result.GridAttributions[key] = attr;
                }

                attr.RefCount += row.NullParentRefrs;
                attr.EvidenceDumps.Add(row.Dump);
                foreach (var f in ParseSampleFormIds(row.SampleFormIds))
                {
                    if (attr.SampleFormIds.Count < 8)
                    {
                        attr.SampleFormIds.Add(f);
                    }
                }
            }
            else
            {
                // Cut region: no cell in any worldspace at this grid
                var cut = result.CutRegions.FirstOrDefault(c => c.Gx == row.GridX && c.Gy == row.GridY);
                if (cut == null)
                {
                    cut = new CutRegion { Gx = row.GridX, Gy = row.GridY };
                    result.CutRegions.Add(cut);
                }

                cut.RefCount += row.NullParentRefrs;
                cut.EvidenceDumps.Add(row.Dump);
                foreach (var f in ParseSampleFormIds(row.SampleFormIds))
                {
                    if (cut.SampleFormIds.Count < 8)
                    {
                        cut.SampleFormIds.Add(f);
                    }
                }
            }
        }

        return result;
    }

    private static IEnumerable<uint> ParseSampleFormIds(string field)
    {
        if (string.IsNullOrWhiteSpace(field))
        {
            yield break;
        }
        foreach (var part in field.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (TryParseHexUInt(part, out var f) && f != 0)
            {
                yield return f;
            }
        }
    }

    // ---------------- Stream-rewrite the authority JSON ----------------

    private static void RewriteAuthority(string inPath, string outPath, AttributionResult attribution)
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

    // ---------------- Per-REFR positions: load + attribute ----------------

    private sealed record PositionRow(
        uint FormId, float X, float Y, float Z, float Scale, int GridX, int GridY,
        uint BaseFormId, byte BaseFormType, int FoundInDumps);

    private static List<PositionRow> LoadPositions(string path)
    {
        var rows = new List<PositionRow>();
        using var reader = new StreamReader(path);
        var header = reader.ReadLine();
        if (header == null)
        {
            return rows;
        }

        var cols = SplitCsv(header);
        int IdxOf(string n) => Array.IndexOf(cols, n);
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
            var parts = SplitCsv(line);
            if (parts.Length <= Math.Max(iY, Math.Max(iGy, iDumps < 0 ? 0 : iDumps)))
            {
                continue;
            }

            if (!TryParseHexUInt(parts[iFid], out var fid))
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
                int.TryParse(parts[iDumps], out foundInDumps);
            }

            uint baseFid = 0;
            if (iBaseFid >= 0 && iBaseFid < parts.Length)
            {
                TryParseHexUInt(parts[iBaseFid], out baseFid);
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

    /// <summary>
    ///     For each REFR position, attribute to (worldspace, cell) with priority:
    ///     1. ESM-derived ref→cell (authoritative — FormID exists as child of CELL in a Sample ESM)
    ///     2. Grid attribution (per (gx, gy) heuristic match from earlier pass) —
    ///        SKIPPED for ACHR/ACRE base types, because actor positions in memory dumps reflect
    ///        last-known runtime state (player-visited, AI-driven, or stale-cache), not their
    ///        canonical cell. Putting Mr. House at the player's last-loaded Novac grid would
    ///        be misleading. Without an ESM match, actors fall through to CUT.
    ///     3. CUT (no cell record at this grid, OR an actor with no ESM authority)
    /// </summary>
    private static void AttributePositions(
        AuthorityView auth,
        AttributionResult attribution,
        IEnumerable<PositionRow> positions,
        IReadOnlyDictionary<uint, ReferenceEnrichment> enrichment)
    {
        foreach (var p in positions)
        {
            enrichment.TryGetValue(p.FormId, out var er);

            // 1. ESM-derived authoritative attribution
            if (auth.RefToCell.TryGetValue(p.FormId, out var esmCellFid) &&
                auth.Cells.TryGetValue(esmCellFid, out var esmCell))
            {
                attribution.Positions.Add(MakePositionAttribution(
                    p, er, esmCell.WorldspaceFormId, esmCellFid, esmCell.EditorId, "ESM"));
                continue;
            }

            // 2. Grid-attribution fallback — but NOT for actors (ACHR/ACRE).
            // BaseFormType 0x2A = NPC_, 0x2B = CREA in the runtime FormType enum (RuntimeBuildOffsets).
            // These values are stable across all post-shift builds (the +1 shift at 0x46 only
            // affects types above 0x45, so NPC_/CREA are unaffected).
            var isActor = IsActorBaseType(p.BaseFormType);
            if (!isActor && attribution.GridAttributions.TryGetValue((p.GridX, p.GridY), out var gridAttr))
            {
                attribution.Positions.Add(MakePositionAttribution(
                    p, er,
                    gridAttr.WorldspaceFormId == 0 ? null : gridAttr.WorldspaceFormId,
                    gridAttr.CellFormId, gridAttr.CellEditorId, gridAttr.Confidence));
                continue;
            }

            // 3. Cut content — no cell at this grid, OR an actor without ESM authority
            attribution.Positions.Add(MakePositionAttribution(
                p, er, null, 0, "", "CUT"));
        }
    }

    private static bool IsActorBaseType(byte baseFormType) => baseFormType is 0x2A or 0x2B;

    private static PositionAttribution MakePositionAttribution(
        PositionRow p, ReferenceEnrichment? er,
        uint? worldspace, uint cellFormId, string cellEditorId, string confidence)
    {
        return new PositionAttribution
        {
            FormId = p.FormId,
            X = p.X, Y = p.Y, Z = p.Z, Scale = p.Scale,
            GridX = p.GridX, GridY = p.GridY,
            FoundInDumps = p.FoundInDumps,
            WorldspaceFormId = worldspace,
            CellFormId = cellFormId,
            CellEditorId = cellEditorId,
            BaseFormId = p.BaseFormId,
            BaseFormType = p.BaseFormType,
            Confidence = confidence,
            EditorId = er?.EditorId,
            BaseEditorId = er?.BaseEditorId,
            BaseFullName = er?.BaseFullName,
            ModelPath = er?.ModelPath,
            RecordType = er?.RecordType,
            IsMapMarker = er?.IsMapMarker ?? false,
            MarkerName = er?.MarkerName,
            MarkerType = er?.MarkerType
        };
    }

    // ---------------- ESM enrichment ----------------

    /// <summary>
    ///     Walks each supplied Sample ESM via the semantic loader and builds a FormID→enrichment
    ///     index from every parsed <see cref="PlacedReference" /> (in each cell's PlacedObjects,
    ///     in worldspace cells, and the flat <see cref="RecordCollection.MapMarkers" /> list).
    ///     Earlier ESMs win on conflict — later builds only fill gaps.
    /// </summary>
    private static async Task<IReadOnlyDictionary<uint, ReferenceEnrichment>> BuildEnrichmentIndexAsync(
        string[] esmPaths, CancellationToken ct)
    {
        var enrich = new Dictionary<uint, ReferenceEnrichment>();
        if (esmPaths.Length == 0)
        {
            return enrich;
        }

        foreach (var esmPath in esmPaths)
        {
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(esmPath))
            {
                AnsiConsole.MarkupLine($"[yellow]ESM not found, skipping:[/] {Markup.Escape(esmPath)}");
                continue;
            }

            AnsiConsole.MarkupLine($"[blue]Loading ESM for enrichment:[/] {Markup.Escape(esmPath)}");
            try
            {
                using var loaded = await SemanticFileLoader.LoadAsync(
                    esmPath,
                    new SemanticFileLoadOptions { FileType = AnalysisFileType.EsmFile },
                    ct);

                var resolver = loaded.Resolver;
                var added = 0;

                foreach (var cell in loaded.Records.Cells)
                {
                    foreach (var pr in cell.PlacedObjects)
                    {
                        if (TryAddEnrichment(enrich, pr, resolver))
                        {
                            added++;
                        }
                    }
                }
                foreach (var ws in loaded.Records.Worldspaces)
                {
                    foreach (var cell in ws.Cells)
                    {
                        foreach (var pr in cell.PlacedObjects)
                        {
                            if (TryAddEnrichment(enrich, pr, resolver))
                            {
                                added++;
                            }
                        }
                    }
                }
                foreach (var pr in loaded.Records.MapMarkers)
                {
                    if (TryAddEnrichment(enrich, pr, resolver))
                    {
                        added++;
                    }
                }

                AnsiConsole.MarkupLine($"  +{added:N0} enrichment entries (total: {enrich.Count:N0})");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"  [red]ERROR: {Markup.Escape(ex.Message)}[/]");
            }
        }

        return enrich;
    }

    private static bool TryAddEnrichment(
        Dictionary<uint, ReferenceEnrichment> enrich,
        PlacedReference pr,
        FormIdResolver resolver)
    {
        if (pr.FormId == 0 || enrich.ContainsKey(pr.FormId))
        {
            return false;
        }

        enrich[pr.FormId] = new ReferenceEnrichment(
            EditorId: string.IsNullOrEmpty(pr.EditorId) ? null : pr.EditorId,
            BaseFormId: pr.BaseFormId,
            BaseEditorId: pr.BaseEditorId ?? resolver.GetEditorId(pr.BaseFormId),
            BaseFullName: resolver.GetDisplayName(pr.BaseFormId),
            ModelPath: string.IsNullOrEmpty(pr.ModelPath) ? null : pr.ModelPath,
            RecordType: pr.RecordType,
            IsMapMarker: pr.IsMapMarker,
            MarkerName: string.IsNullOrEmpty(pr.MarkerName) ? null : pr.MarkerName,
            MarkerType: pr.MarkerType.HasValue ? (ushort)pr.MarkerType.Value : null);
        return true;
    }
}
