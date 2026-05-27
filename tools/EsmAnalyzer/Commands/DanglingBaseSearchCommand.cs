using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Semantic;
using Spectre.Console;

namespace EsmAnalyzer.Commands;

/// <summary>
///     Resolves every dangling-REFR position's <c>base_form_id</c> against one or more
///     Sample ESMs and prints those whose base record's editor ID / display name matches
///     a regex pattern. Catches cut content whose REFR FormID isn't in any ESM (so the
///     attribute-dangling enrichment missed it) but whose BASE record survives in a
///     shipped ESM — e.g. a TheStripWorld REFR placing a `UL*` STAT.
/// </summary>
internal static class DanglingBaseSearchCommand
{
    public static Command CreateDanglingBaseSearchCommand()
    {
        var command = new Command("dangling-base-search",
            "Search dangling REFRs by their base record's editor ID / display name in supplied ESMs.");

        var authOpt = new Option<string?>("--authority")
        {
            Description = "Path to cell_worldspace_authority.json (default: data/cell_worldspace_authority.json)"
        };
        var esmOpt = new Option<string[]>("--esm")
        {
            Description = "ESM/ESP to load (resolver source). Repeat for multiple. " +
                          "Earlier ESMs win on conflict.",
            Required = true,
            AllowMultipleArgumentsPerToken = true
        };
        var patternOpt = new Option<string>("--pattern")
        {
            Description = "Regex (case-insensitive by default) to match against base editor ID + display name",
            Required = true
        };
        var caseSensitiveOpt = new Option<bool>("--case-sensitive")
        {
            Description = "Disable the default case-insensitive flag"
        };

        command.Options.Add(authOpt);
        command.Options.Add(esmOpt);
        command.Options.Add(patternOpt);
        command.Options.Add(caseSensitiveOpt);

        command.SetAction(async (parseResult, ct) =>
        {
            var auth = parseResult.GetValue(authOpt) ?? Path.Combine("data", "cell_worldspace_authority.json");
            var esms = parseResult.GetValue(esmOpt)!;
            var pattern = parseResult.GetValue(patternOpt)!;
            var caseSensitive = parseResult.GetValue(caseSensitiveOpt);
            await RunAsync(auth, esms, pattern, caseSensitive, ct);
        });

        return command;
    }

    private static async Task RunAsync(string authorityPath, string[] esmPaths, string pattern, bool caseSensitive, CancellationToken ct)
    {
        if (!File.Exists(authorityPath))
        {
            AnsiConsole.MarkupLine($"[red]Authority JSON not found:[/] {Markup.Escape(authorityPath)}");
            return;
        }

        Regex regex;
        try
        {
            regex = new Regex(pattern, caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
        }
        catch (ArgumentException ex)
        {
            AnsiConsole.MarkupLine($"[red]Invalid regex:[/] {Markup.Escape(ex.Message)}");
            return;
        }

        // Build a merged FormID -> (editor_id, display_name) map across all ESMs.
        var nameByFid = new Dictionary<uint, (string? Edid, string? Display)>();
        foreach (var esmPath in esmPaths)
        {
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(esmPath))
            {
                AnsiConsole.MarkupLine($"[yellow]ESM not found, skipping:[/] {Markup.Escape(esmPath)}");
                continue;
            }

            AnsiConsole.MarkupLine($"[blue]Loading[/] {Markup.Escape(esmPath)}");
            using var loaded = await SemanticFileLoader.LoadAsync(
                esmPath, new SemanticFileLoadOptions { FileType = AnalysisFileType.EsmFile }, ct);

            // Iterate every FormID known to the resolver and back-fill our merged map.
            // Earlier ESMs win — only fill gaps from later ESMs.
            HarvestNames(loaded.Resolver, nameByFid);
        }

        AnsiConsole.MarkupLine($"[blue]Merged name index:[/] {nameByFid.Count:N0} FormIDs");

        // Read dangling positions
        using var stream = File.OpenRead(authorityPath);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        if (!doc.RootElement.TryGetProperty("dangling_refs", out var dEl) ||
            !dEl.TryGetProperty("positions", out var pEl) ||
            pEl.ValueKind != JsonValueKind.Array)
        {
            AnsiConsole.MarkupLine("[red]authority JSON has no dangling_refs.positions array[/]");
            return;
        }

        var totalScanned = 0;
        var matches = new List<MatchRow>();
        foreach (var entry in pEl.EnumerateArray())
        {
            totalScanned++;
            if (!entry.TryGetProperty("base_form_id", out var bfEl) ||
                bfEl.ValueKind != JsonValueKind.String ||
                !TryParseHex(bfEl.GetString(), out var baseFid) ||
                baseFid == 0)
            {
                continue;
            }

            if (!nameByFid.TryGetValue(baseFid, out var nm))
            {
                continue;
            }

            var haystack = $"{nm.Edid ?? string.Empty}\n{nm.Display ?? string.Empty}";
            if (!regex.IsMatch(haystack))
            {
                continue;
            }

            var fid = entry.TryGetProperty("form_id", out var fEl) && fEl.ValueKind == JsonValueKind.String
                ? TryParseHex(fEl.GetString(), out var f) ? f : 0u
                : 0u;
            var x = entry.TryGetProperty("x", out var xEl) ? (float)xEl.GetDouble() : 0f;
            var y = entry.TryGetProperty("y", out var yEl) ? (float)yEl.GetDouble() : 0f;
            var z = entry.TryGetProperty("z", out var zEl) ? (float)zEl.GetDouble() : 0f;
            var gx = entry.TryGetProperty("grid_x", out var gxEl) ? gxEl.GetInt32() : 0;
            var gy = entry.TryGetProperty("grid_y", out var gyEl) ? gyEl.GetInt32() : 0;
            var conf = entry.TryGetProperty("confidence", out var cEl) ? cEl.GetString() ?? "" : "";
            var cellEdid = entry.TryGetProperty("cell_editor_id", out var ceEl) ? ceEl.GetString() ?? "" : "";
            var foundIn = entry.TryGetProperty("found_in_dumps", out var fdEl) ? fdEl.GetInt32() : 1;

            matches.Add(new MatchRow(fid, baseFid, nm.Edid, nm.Display, x, y, z, gx, gy, conf, cellEdid, foundIn));
        }

        AnsiConsole.MarkupLine($"\n[green]{matches.Count:N0} match(es)[/] across {totalScanned:N0} positions scanned.");
        foreach (var m in matches.OrderBy(m => m.BaseEdid, StringComparer.OrdinalIgnoreCase).ThenBy(m => m.RefFid))
        {
            AnsiConsole.WriteLine(
                $"  REFR 0x{m.RefFid:X8}  base 0x{m.BaseFid:X8}  " +
                $"edid={m.BaseEdid ?? "(none)"}  name={m.BaseDisplay ?? "(none)"}  " +
                $"pos=({m.X:F0},{m.Y:F0},{m.Z:F0})  grid=({m.Gx},{m.Gy})  " +
                $"[{m.Confidence}]  dumps={m.FoundInDumps}  " +
                (string.IsNullOrEmpty(m.CellEdid) ? "" : $"attributed→{m.CellEdid}"));
        }
    }

    private sealed record MatchRow(
        uint RefFid, uint BaseFid, string? BaseEdid, string? BaseDisplay,
        float X, float Y, float Z, int Gx, int Gy,
        string Confidence, string CellEdid, int FoundInDumps);

    private static void HarvestNames(FormIdResolver resolver, Dictionary<uint, (string? Edid, string? Display)> target)
    {
        foreach (var kvp in resolver.EditorIds)
        {
            if (target.ContainsKey(kvp.Key))
            {
                continue;
            }
            target[kvp.Key] = (kvp.Value, resolver.GetDisplayName(kvp.Key));
        }
        foreach (var kvp in resolver.DisplayNames)
        {
            if (target.ContainsKey(kvp.Key))
            {
                continue;
            }
            target[kvp.Key] = (null, kvp.Value);
        }
    }

    private static bool TryParseHex(string? s, out uint value)
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
