using System.CommandLine;
using System.Globalization;
using System.IO.MemoryMappedFiles;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using Spectre.Console;

namespace EsmAnalyzer.Commands;

/// <summary>
///     Commands for validating FaceGen slider computation against GECK reference values.
/// </summary>
public static class FaceGenCommands
{
    public static Command CreateFaceGenCommand()
    {
        var command = new Command("facegen", "Compute FaceGen slider values for an NPC (with race-base merging)");

        var fileArg = new Argument<string>("file") { Description = "Path to ESM file" };
        var formIdArg = new Argument<string>("formid")
            { Description = "NPC FormID (hex, e.g. 0x00101C9B)" };
        var compareOpt = new Option<string?>("-c", "--compare")
            { Description = "Path to GECK reference file for comparison" };

        command.Arguments.Add(fileArg);
        command.Arguments.Add(formIdArg);
        command.Options.Add(compareOpt);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var file = parseResult.GetValue(fileArg)!;
            var formIdStr = parseResult.GetValue(formIdArg)!;
            var compareFile = parseResult.GetValue(compareOpt);
            await RunFaceGenAsync(file, formIdStr, compareFile, cancellationToken);
        });

        return command;
    }

    private static async Task RunFaceGenAsync(
        string input,
        string formIdStr,
        string? compareFile,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(input))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] File not found: {0}", input);
            return;
        }

        // Parse FormID
        var cleanFormId = formIdStr.Replace("0x", "", StringComparison.OrdinalIgnoreCase);
        if (!uint.TryParse(cleanFormId, NumberStyles.HexNumber, null, out var targetFormId))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Invalid FormID: {0}", formIdStr);
            return;
        }

        // Phase 1: Analyze ESM
        AnsiConsole.MarkupLine("[blue]Analyzing ESM...[/]");
        var result = await EsmFileAnalyzer.AnalyzeAsync(input, cancellationToken: cancellationToken);

        if (result.EsmRecords == null)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Failed to parse ESM records");
            return;
        }

        // Phase 2: Full semantic reconstruction
        RecordCollection semanticResult;
        AnsiConsole.MarkupLine("[blue]Reconstructing records...[/]");
        using (var mmf = MemoryMappedFile.CreateFromFile(input, FileMode.Open, null, 0,
                   MemoryMappedFileAccess.Read))
        using (var accessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read))
        {
            var parser = new RecordParser(result.EsmRecords, result.FormIdMap, accessor, result.FileSize);
            semanticResult = parser.ReconstructAll();
        }

        // Phase 3: Find NPC
        var npc = semanticResult.Npcs.FirstOrDefault(n => n.FormId == targetFormId);
        if (npc == null)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] NPC with FormID 0x{0:X8} not found", targetFormId);
            AnsiConsole.MarkupLine("[dim]Found {0} NPCs total[/]", semanticResult.Npcs.Count);
            return;
        }

        // Phase 4: Find race
        RaceRecord? race = null;
        if (npc.Race.HasValue)
        {
            race = semanticResult.Races.FirstOrDefault(r => r.FormId == npc.Race.Value);
        }

        var isFemale = npc.Stats != null && (npc.Stats.Flags & 1) == 1;
        var gender = isFemale ? "Female" : "Male";

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]NPC: {0} — {1}[/]", npc.EditorId ?? "(unknown)", npc.FullName ?? "(unnamed)");
        AnsiConsole.MarkupLine("FormID: 0x{0:X8}  Gender: {1}", npc.FormId, gender);

        if (race != null)
        {
            AnsiConsole.MarkupLine("Race: {0} (0x{1:X8})", race.EditorId ?? "(unknown)", race.FormId);
            var raceBase = isFemale ? race.FemaleFaceGenGeometrySymmetric : race.MaleFaceGenGeometrySymmetric;
            AnsiConsole.MarkupLine("Race FGGS ({0}): {1}", gender,
                raceBase != null ? $"{raceBase.Length} floats" : "[red]NOT FOUND[/]");
        }
        else
        {
            AnsiConsole.MarkupLine("[yellow]Race: not found (no race-base merging)[/]");
        }

        AnsiConsole.WriteLine();

        // Load GECK reference if provided
        Dictionary<string, float>? geckRef = null;
        if (compareFile != null && File.Exists(compareFile))
        {
            geckRef = ParseGeckReference(await File.ReadAllTextAsync(compareFile, cancellationToken));
            AnsiConsole.MarkupLine("[blue]Loaded {0} GECK reference values from {1}[/]", geckRef.Count, compareFile);
            AnsiConsole.WriteLine();
        }

        // Phase 5: Compute FaceGen values
        var raceFggs = isFemale ? race?.FemaleFaceGenGeometrySymmetric : race?.MaleFaceGenGeometrySymmetric;
        var raceFgga = isFemale ? race?.FemaleFaceGenGeometryAsymmetric : race?.MaleFaceGenGeometryAsymmetric;
        var raceFgts = isFemale ? race?.FemaleFaceGenTextureSymmetric : race?.MaleFaceGenTextureSymmetric;

        PrintFaceGenSection("Geometry-Symmetric", npc.FaceGenGeometrySymmetric, raceFggs,
            fggs => FaceGenControls.ComputeGeometrySymmetric(fggs, raceFggs),
            fggs => FaceGenControls.ComputeGeometrySymmetric(fggs), geckRef);

        PrintFaceGenSection("Geometry-Asymmetric", npc.FaceGenGeometryAsymmetric, raceFgga,
            fgga => FaceGenControls.ComputeGeometryAsymmetric(fgga, raceFgga),
            fgga => FaceGenControls.ComputeGeometryAsymmetric(fgga), geckRef);

        PrintFaceGenSection("Texture-Symmetric", npc.FaceGenTextureSymmetric, raceFgts,
            fgts => FaceGenControls.ComputeTextureSymmetric(fgts, raceFgts),
            fgts => FaceGenControls.ComputeTextureSymmetric(fgts), geckRef);

        // Summary stats if GECK reference provided
        if (geckRef != null)
        {
            PrintComparisonSummary(npc, raceFggs, raceFgga, raceFgts, geckRef);
        }
    }

    private static void PrintFaceGenSection(
        string label,
        float[]? npcData,
        float[]? raceBase,
        Func<float[], (string Name, float Value)[]> computeMerged,
        Func<float[], (string Name, float Value)[]> computeNpcOnly,
        Dictionary<string, float>? geckRef)
    {
        if (npcData == null || npcData.Length == 0)
        {
            AnsiConsole.MarkupLine("[dim]{0}: no data[/]", label);
            return;
        }

        var merged = computeMerged(npcData);
        var npcOnly = computeNpcOnly(npcData);

        Console.WriteLine($"{label} ({merged.Length} controls)");
        Console.WriteLine(new string('-', 90));

        if (geckRef != null)
        {
            Console.WriteLine($"  {"Control",-45} {"Merged",8} {"NPC-Only",8} {"GECK",8} {"Error",8}");
        }
        else
        {
            Console.WriteLine($"  {"Control",-45} {"Merged",8} {"NPC-Only",8}");
        }

        Console.WriteLine($"  {new string('-', 45)} {new string('-', 8)} {new string('-', 8)}" +
                           (geckRef != null ? $" {new string('-', 8)} {new string('-', 8)}" : ""));

        for (var i = 0; i < merged.Length; i++)
        {
            var name = merged[i].Name;
            var mergedVal = merged[i].Value;
            var npcOnlyVal = npcOnly[i].Value;

            if (Math.Abs(mergedVal) < 0.01f && Math.Abs(npcOnlyVal) < 0.01f)
            {
                continue;
            }

            if (geckRef != null && geckRef.TryGetValue(name, out var geckVal))
            {
                var error = Math.Abs(mergedVal - geckVal);
                var marker = error < 0.02f ? " " : error < 0.1f ? "~" : "!";
                Console.WriteLine($"  {name,-45} {mergedVal,8:F4} {npcOnlyVal,8:F4} {geckVal,8:F4} {error,7:F4}{marker}");
            }
            else
            {
                Console.WriteLine($"  {name,-45} {mergedVal,8:F4} {npcOnlyVal,8:F4}");
            }
        }

        Console.WriteLine();
    }

    private static void PrintComparisonSummary(
        NpcRecord npc,
        float[]? raceFggs,
        float[]? raceFgga,
        float[]? raceFgts,
        Dictionary<string, float> geckRef)
    {
        var mergedErrors = new List<float>();
        var npcOnlyErrors = new List<float>();

        void CollectErrors(float[]? data, float[]? raceBase,
            Func<float[], (string, float)[]> computeMerged,
            Func<float[], (string, float)[]> computeNpcOnly)
        {
            if (data == null) return;
            var merged = computeMerged(data);
            var npcOnly = computeNpcOnly(data);
            for (var i = 0; i < merged.Length; i++)
            {
                if (geckRef.TryGetValue(merged[i].Item1, out var geckVal))
                {
                    mergedErrors.Add(Math.Abs(merged[i].Item2 - geckVal));
                    npcOnlyErrors.Add(Math.Abs(npcOnly[i].Item2 - geckVal));
                }
            }
        }

        CollectErrors(npc.FaceGenGeometrySymmetric, raceFggs,
            f => FaceGenControls.ComputeGeometrySymmetric(f, raceFggs),
            f => FaceGenControls.ComputeGeometrySymmetric(f));
        CollectErrors(npc.FaceGenGeometryAsymmetric, raceFgga,
            f => FaceGenControls.ComputeGeometryAsymmetric(f, raceFgga),
            f => FaceGenControls.ComputeGeometryAsymmetric(f));
        CollectErrors(npc.FaceGenTextureSymmetric, raceFgts,
            f => FaceGenControls.ComputeTextureSymmetric(f, raceFgts),
            f => FaceGenControls.ComputeTextureSymmetric(f));

        if (mergedErrors.Count == 0) return;

        AnsiConsole.MarkupLine("[bold underline]Comparison Summary[/]");
        AnsiConsole.MarkupLine("  Matched controls:  {0}/{1}", mergedErrors.Count, geckRef.Count);
        AnsiConsole.MarkupLine("  [green]Merged (with race base):[/]");
        AnsiConsole.MarkupLine("    Mean error:    {0:F4}", mergedErrors.Average());
        AnsiConsole.MarkupLine("    Max error:     {0:F4}", mergedErrors.Max());
        AnsiConsole.MarkupLine("    Within 0.02:   {0}/{1}",
            mergedErrors.Count(e => e < 0.02f), mergedErrors.Count);
        AnsiConsole.MarkupLine("  [dim]NPC-only (no race base):[/]");
        AnsiConsole.MarkupLine("    Mean error:    {0:F4}", npcOnlyErrors.Average());
        AnsiConsole.MarkupLine("    Max error:     {0:F4}", npcOnlyErrors.Max());
        AnsiConsole.MarkupLine("    Within 0.02:   {0}/{1}",
            npcOnlyErrors.Count(e => e < 0.02f), npcOnlyErrors.Count);
    }

    /// <summary>
    ///     Parse GECK FaceGen reference file (format: "Control Name: value" per line).
    /// </summary>
    private static Dictionary<string, float> ParseGeckReference(string content)
    {
        var result = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in content.Split('\n'))
        {
            var trimmed = line.Trim();
            var colonIdx = trimmed.LastIndexOf(':');
            if (colonIdx <= 0) continue;

            var name = trimmed[..colonIdx].Trim();
            var valueStr = trimmed[(colonIdx + 1)..].Trim();

            if (float.TryParse(valueStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                // Handle duplicate control names (e.g., "Nose - sellion shallow / deep" appears twice)
                // by appending " (2)" to match the naming in FaceGenControls
                if (result.ContainsKey(name))
                {
                    result[$"{name} (2)"] = value;
                }
                else
                {
                    result[name] = value;
                }
            }
        }

        return result;
    }
}
