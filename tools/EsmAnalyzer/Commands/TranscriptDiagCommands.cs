using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using FalloutXbox360Utils.Core.Formats.Bsa;
using Spectre.Console;

namespace EsmAnalyzer.Commands;

/// <summary>
///     Diagnostic command to examine .fnvtranscript.json alongside BSA voice file counts.
///     Helps debug key format issues (old FormID-only keys vs new voicetype|FormID keys).
/// </summary>
public static class TranscriptDiagCommands
{
    private const string TranscriptFileName = ".fnvtranscript.json";

    public static Command CreateTranscriptDiagCommand()
    {
        var command = new Command("transcript-diag",
            "Diagnose .fnvtranscript.json key counts vs BSA voice file counts");

        var dirArg = new Argument<string>("data-dir")
        {
            Description = "Path to the Data directory containing BSAs and .fnvtranscript.json"
        };

        var limitOption = new Option<int>("-l", "--limit")
        {
            Description = "Number of sample entries to display per table",
            DefaultValueFactory = _ => 20
        };

        command.Arguments.Add(dirArg);
        command.Options.Add(limitOption);

        command.SetAction(parseResult =>
            Run(parseResult.GetValue(dirArg)!, parseResult.GetValue(limitOption)));

        return command;
    }

    private static int Run(string dataDir, int limit)
    {
        if (!Directory.Exists(dataDir))
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] Directory not found: {dataDir}");
            return 1;
        }

        AnsiConsole.MarkupLine($"[blue]Data directory:[/] {dataDir}");
        AnsiConsole.WriteLine();

        // ── Step 1: Parse voice files from BSAs ───────────────────
        var bsaPaths = Directory.GetFiles(dataDir, "*.bsa")
            .Where(f =>
            {
                var name = Path.GetFileName(f);
                return name.Contains("Voice", StringComparison.OrdinalIgnoreCase);
            })
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (bsaPaths.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]ERROR:[/] No Voice BSA files found");
            return 1;
        }

        AnsiConsole.MarkupLine($"[blue]BSAs:[/] {string.Join(", ", bsaPaths.Select(Path.GetFileName))}");

        var voiceFiles = new List<VoiceFile>();
        foreach (var bsaPath in bsaPaths)
        {
            var archive = BsaParser.Parse(bsaPath);
            foreach (var folder in archive.Folders)
            {
                if (folder.Name == null ||
                    !folder.Name.StartsWith(@"sound\voice\", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var pathParts = folder.Name.Split('\\');
                if (pathParts.Length < 4) continue;
                var voiceType = pathParts[3];

                foreach (var file in folder.Files)
                {
                    if (file.Name == null) continue;
                    var ext = Path.GetExtension(file.Name).TrimStart('.').ToLowerInvariant();
                    if (ext is not ("xma" or "wav" or "mp3" or "ogg")) continue;
                    if (TryParseVoiceFileName(file.Name, out var formId, out var responseIndex, out _))
                    {
                        voiceFiles.Add(new VoiceFile(formId, responseIndex, voiceType, file.Name));
                    }
                }
            }
        }

        // ── Step 2: Compute BSA-level statistics ──────────────────
        var totalFiles = voiceFiles.Count;
        var uniqueFormIdResp = voiceFiles.Select(v => $"{v.FormId:X8}_{v.ResponseIndex}").ToHashSet();
        var uniqueVtFormIdResp = voiceFiles.Select(v => $"{v.VoiceType}|{v.FormId:X8}_{v.ResponseIndex}").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var voiceTypeGroups = voiceFiles.GroupBy(v => v.VoiceType, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ToList();

        var bsaTable = new Table().Border(TableBorder.Rounded).Title("[bold]BSA Voice Files[/]");
        bsaTable.AddColumn("Metric");
        bsaTable.AddColumn(new TableColumn("Count").RightAligned());
        bsaTable.AddRow("Total voice files", $"{totalFiles:N0}");
        bsaTable.AddRow("Unique FormID_ResponseIndex keys", $"{uniqueFormIdResp.Count:N0}");
        bsaTable.AddRow("Unique VoiceType|FormID_ResponseIndex keys", $"{uniqueVtFormIdResp.Count:N0}");
        bsaTable.AddRow("[bold yellow]Key collision (lost files)[/]",
            $"[bold yellow]{totalFiles - uniqueFormIdResp.Count:N0}[/]");
        bsaTable.AddRow("Unique voice type folders", $"{voiceTypeGroups.Count:N0}");
        AnsiConsole.Write(bsaTable);
        AnsiConsole.WriteLine();

        // Show collision breakdown: how many FormID_ResponseIndex keys map to multiple voice types
        var formIdRespToVoiceTypes = new Dictionary<string, List<string>>();
        foreach (var vf in voiceFiles)
        {
            var key = $"{vf.FormId:X8}_{vf.ResponseIndex}";
            if (!formIdRespToVoiceTypes.TryGetValue(key, out var list))
            {
                list = [];
                formIdRespToVoiceTypes[key] = list;
            }
            list.Add(vf.VoiceType);
        }

        var collisions = formIdRespToVoiceTypes.Where(kv => kv.Value.Count > 1).ToList();
        var collisionTable = new Table().Border(TableBorder.Rounded).Title("[bold]Key Collision Analysis[/]");
        collisionTable.AddColumn("Metric");
        collisionTable.AddColumn(new TableColumn("Count").RightAligned());
        collisionTable.AddRow("FormID_ResponseIndex keys with 1 voice type", $"{formIdRespToVoiceTypes.Count(kv => kv.Value.Count == 1):N0}");
        collisionTable.AddRow("[yellow]FormID_ResponseIndex keys with 2+ voice types[/]", $"[yellow]{collisions.Count:N0}[/]");
        collisionTable.AddRow("[yellow]Total voice files collapsed by old key format[/]",
            $"[yellow]{collisions.Sum(kv => kv.Value.Count - 1):N0}[/]");

        if (collisions.Count > 0)
        {
            var maxCollision = collisions.MaxBy(kv => kv.Value.Count)!;
            collisionTable.AddRow("Max voice types per key",
                $"{maxCollision.Value.Count} ({maxCollision.Key})");
        }

        AnsiConsole.Write(collisionTable);
        AnsiConsole.WriteLine();

        // Show top colliding keys
        if (limit > 0 && collisions.Count > 0)
        {
            var topCollisions = new Table().Border(TableBorder.Rounded)
                .Title($"[bold]Top Colliding Keys[/] (first {Math.Min(limit, collisions.Count)})");
            topCollisions.AddColumn("FormID_ResponseIndex");
            topCollisions.AddColumn(new TableColumn("Voice Types").RightAligned());
            topCollisions.AddColumn("Sample Types");

            foreach (var kv in collisions.OrderByDescending(kv => kv.Value.Count).Take(limit))
            {
                var sampleTypes = string.Join(", ",
                    kv.Value.Distinct(StringComparer.OrdinalIgnoreCase).Take(5));
                if (kv.Value.Distinct(StringComparer.OrdinalIgnoreCase).Count() > 5)
                    sampleTypes += "...";
                topCollisions.AddRow(kv.Key, $"{kv.Value.Count}", sampleTypes);
            }

            AnsiConsole.Write(topCollisions);
            AnsiConsole.WriteLine();
        }

        // ── Step 3: Load and analyze .fnvtranscript.json ──────────
        var jsonPath = Path.Combine(dataDir, TranscriptFileName);
        if (!File.Exists(jsonPath))
        {
            AnsiConsole.MarkupLine($"[yellow]No {TranscriptFileName} found — skipping JSON analysis.[/]");
            return 0;
        }

        var jsonSize = new FileInfo(jsonPath).Length;
        AnsiConsole.MarkupLine($"[blue]JSON file:[/] {jsonPath} ({jsonSize / 1024:N0} KB)");
        AnsiConsole.WriteLine();

        Dictionary<string, JsonElement>? entries;
        try
        {
            var json = File.ReadAllText(jsonPath);
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Try both PascalCase and camelCase (source-generated serializer uses camelCase)
            if (!root.TryGetProperty("entries", out var entriesElem) &&
                !root.TryGetProperty("Entries", out entriesElem))
            {
                AnsiConsole.MarkupLine("[red]ERROR:[/] JSON doesn't have an 'Entries' property");
                return 1;
            }

            entries = new Dictionary<string, JsonElement>();
            foreach (var prop in entriesElem.EnumerateObject())
            {
                entries[prop.Name] = prop.Value;
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] Failed to parse JSON: {ex.Message}");
            return 1;
        }

        // Categorize keys by format
        var newFormatKeys = 0;   // voicetype|FormID_ResponseIndex
        var legacyFormIdResp = 0; // FormID_ResponseIndex (no voice type)
        var legacyFormIdOnly = 0; // FormID only (8 hex chars)
        var unknownFormat = 0;

        var sourceBreakdown = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var (key, value) in entries)
        {
            if (key.Contains('|'))
                newFormatKeys++;
            else if (key.Contains('_'))
                legacyFormIdResp++;
            else if (key.Length == 8 && uint.TryParse(key, NumberStyles.HexNumber, null, out _))
                legacyFormIdOnly++;
            else
                unknownFormat++;

            var source = "";
            if (value.TryGetProperty("source", out var srcElem) ||
                value.TryGetProperty("Source", out srcElem))
                source = srcElem.GetString() ?? "";
            if (!sourceBreakdown.TryGetValue(source, out _))
                sourceBreakdown[source] = 0;
            sourceBreakdown[source]++;
        }

        var jsonTable = new Table().Border(TableBorder.Rounded).Title("[bold].fnvtranscript.json Analysis[/]");
        jsonTable.AddColumn("Metric");
        jsonTable.AddColumn(new TableColumn("Count").RightAligned());
        jsonTable.AddRow("[bold]Total entries[/]", $"[bold]{entries.Count:N0}[/]");
        jsonTable.AddRow("", "");
        jsonTable.AddRow("[bold]Key format breakdown:[/]", "");
        jsonTable.AddRow("  New format (voicetype|FormID_Resp)", $"{newFormatKeys:N0}");
        jsonTable.AddRow("  Legacy (FormID_ResponseIndex)", $"{legacyFormIdResp:N0}");
        jsonTable.AddRow("  Legacy (FormID only)", $"{legacyFormIdOnly:N0}");
        if (unknownFormat > 0)
            jsonTable.AddRow("  Unknown format", $"{unknownFormat:N0}");
        jsonTable.AddRow("", "");
        jsonTable.AddRow("[bold]Source breakdown:[/]", "");
        foreach (var (source, count) in sourceBreakdown.OrderByDescending(kv => kv.Value))
        {
            jsonTable.AddRow($"  {(source.Length > 0 ? source : "(empty)")}", $"{count:N0}");
        }
        AnsiConsole.Write(jsonTable);
        AnsiConsole.WriteLine();

        // ── Step 4: Cross-reference BSA voice files vs JSON keys ──
        // For each BSA voice file, check if a JSON key would match it
        var matchedByNew = 0;       // matched by voicetype|FormID_Resp
        var matchedByLegacyFR = 0;  // matched by FormID_Resp (legacy)
        var matchedByLegacyF = 0;   // matched by FormID only (legacy, resp=0)
        var unmatched = 0;

        var unmatchedSamples = new List<string>();

        foreach (var vf in voiceFiles)
        {
            var newKey = $"{vf.VoiceType}|{vf.FormId:X8}_{vf.ResponseIndex}";
            var legacyFrKey = $"{vf.FormId:X8}_{vf.ResponseIndex}";
            var legacyFKey = vf.FormId.ToString("X8");

            if (entries.ContainsKey(newKey))
            {
                matchedByNew++;
            }
            else if (entries.ContainsKey(legacyFrKey))
            {
                matchedByLegacyFR++;
            }
            else if (vf.ResponseIndex == 0 && entries.ContainsKey(legacyFKey))
            {
                matchedByLegacyF++;
            }
            else
            {
                unmatched++;
                if (unmatchedSamples.Count < limit)
                    unmatchedSamples.Add($"{vf.VoiceType}|{vf.FormId:X8}_{vf.ResponseIndex} ({vf.FileName})");
            }
        }

        var xrefTable = new Table().Border(TableBorder.Rounded)
            .Title("[bold]BSA → JSON Cross-Reference[/]");
        xrefTable.AddColumn("Metric");
        xrefTable.AddColumn(new TableColumn("Count").RightAligned());
        xrefTable.AddRow("[bold]BSA voice files[/]", $"[bold]{totalFiles:N0}[/]");
        xrefTable.AddRow("[bold]JSON entries[/]", $"[bold]{entries.Count:N0}[/]");
        xrefTable.AddRow("", "");
        xrefTable.AddRow("Matched by new key (voicetype|FormID_Resp)", $"[green]{matchedByNew:N0}[/]");
        xrefTable.AddRow("Matched by legacy key (FormID_Resp)", $"[yellow]{matchedByLegacyFR:N0}[/]");
        xrefTable.AddRow("Matched by legacy key (FormID only)", $"[yellow]{matchedByLegacyF:N0}[/]");
        xrefTable.AddRow("[red]Unmatched (no JSON entry)[/]", $"[red]{unmatched:N0}[/]");
        xrefTable.AddRow("", "");
        xrefTable.AddRow("[bold]Total matched[/]",
            $"[bold]{matchedByNew + matchedByLegacyFR + matchedByLegacyF:N0}[/]");

        var expectedWhisper = sourceBreakdown.GetValueOrDefault("whisper", 0) +
                              sourceBreakdown.GetValueOrDefault("whisper-empty", 0);
        xrefTable.AddRow("", "");
        xrefTable.AddRow("[bold]Expected whisper entries (if all unique)[/]",
            $"[bold]{uniqueVtFormIdResp.Count:N0}[/]");
        xrefTable.AddRow("[bold]Actual whisper entries in JSON[/]",
            $"[bold]{expectedWhisper:N0}[/]");
        xrefTable.AddRow("[bold red]Missing whisper entries[/]",
            $"[bold red]{uniqueVtFormIdResp.Count - expectedWhisper:N0}[/]");

        AnsiConsole.Write(xrefTable);
        AnsiConsole.WriteLine();

        // ── Step 5: Show what migration would produce ─────────────
        if (legacyFormIdResp + legacyFormIdOnly > 0)
        {
            AnsiConsole.MarkupLine("[bold yellow]Legacy keys detected — migration needed![/]");
            AnsiConsole.MarkupLine("The ApplyToEntries migration should expand each legacy key into");
            AnsiConsole.MarkupLine("one key per voice type. Here's what that would look like:");
            AnsiConsole.WriteLine();

            // For each legacy key, count how many BSA voice files share that FormID_Resp
            var expansionCount = 0;
            var expansionSamples = new List<(string LegacyKey, int ExpectedKeys)>();

            foreach (var (key, _) in entries)
            {
                if (key.Contains('|')) continue; // already new format

                // Find all BSA voice files that match this legacy key
                int matchCount;
                if (key.Contains('_'))
                {
                    // FormID_ResponseIndex format
                    matchCount = formIdRespToVoiceTypes.TryGetValue(key, out var vtList) ? vtList.Count : 0;
                }
                else
                {
                    // FormID-only format (response index 0)
                    var frKey = $"{key}_0";
                    matchCount = formIdRespToVoiceTypes.TryGetValue(frKey, out var vtList) ? vtList.Count : 0;
                }

                expansionCount += matchCount;
                if (expansionSamples.Count < limit)
                    expansionSamples.Add((key, matchCount));
            }

            var migTable = new Table().Border(TableBorder.Rounded)
                .Title("[bold]Migration Projection[/]");
            migTable.AddColumn("Metric");
            migTable.AddColumn(new TableColumn("Count").RightAligned());
            migTable.AddRow("Legacy keys to expand", $"{legacyFormIdResp + legacyFormIdOnly:N0}");
            migTable.AddRow("New-format keys after expansion", $"{expansionCount:N0}");
            migTable.AddRow("Already new-format keys", $"{newFormatKeys:N0}");
            migTable.AddRow("[bold green]Total keys after migration[/]",
                $"[bold green]{newFormatKeys + expansionCount:N0}[/]");
            AnsiConsole.Write(migTable);
            AnsiConsole.WriteLine();

            if (limit > 0 && expansionSamples.Count > 0)
            {
                var expTable = new Table().Border(TableBorder.Rounded)
                    .Title($"[bold]Legacy Key Expansion Samples[/] (first {Math.Min(limit, expansionSamples.Count)})");
                expTable.AddColumn("Legacy Key");
                expTable.AddColumn(new TableColumn("→ New Keys").RightAligned());

                foreach (var (legacyKey, expected) in expansionSamples
                             .OrderByDescending(x => x.ExpectedKeys).Take(limit))
                {
                    expTable.AddRow(legacyKey, $"{expected}");
                }

                AnsiConsole.Write(expTable);
                AnsiConsole.WriteLine();
            }
        }
        else if (newFormatKeys == entries.Count && newFormatKeys > 0)
        {
            AnsiConsole.MarkupLine("[green]All JSON keys are in new format (voicetype|FormID_ResponseIndex) — no migration needed.[/]");
        }

        // ── Step 6: Voice type file distribution ──────────────────
        if (limit > 0)
        {
            var vtDistTable = new Table().Border(TableBorder.Rounded)
                .Title($"[bold]Voice Type Distribution[/] (top {Math.Min(limit, voiceTypeGroups.Count)})");
            vtDistTable.AddColumn("Voice Type");
            vtDistTable.AddColumn(new TableColumn("BSA Files").RightAligned());
            vtDistTable.AddColumn(new TableColumn("JSON Keys").RightAligned());
            vtDistTable.AddColumn("Status");

            foreach (var g in voiceTypeGroups.Take(limit))
            {
                var vtName = g.Key;
                var bsaCount = g.Count();
                var jsonCount = entries.Count(kv =>
                    kv.Key.StartsWith($"{vtName}|", StringComparison.OrdinalIgnoreCase));
                var status = jsonCount == bsaCount
                    ? "[green]OK[/]"
                    : jsonCount == 0
                        ? "[red]No JSON keys[/]"
                        : $"[yellow]{bsaCount - jsonCount:N0} missing[/]";
                vtDistTable.AddRow(vtName, $"{bsaCount:N0}", $"{jsonCount:N0}", status);
            }

            AnsiConsole.Write(vtDistTable);
            AnsiConsole.WriteLine();
        }

        // Summary
        AnsiConsole.MarkupLine("[bold]Summary:[/]");
        if (legacyFormIdResp + legacyFormIdOnly > 0)
        {
            AnsiConsole.MarkupLine($"  [yellow]JSON has {legacyFormIdResp + legacyFormIdOnly:N0} legacy keys that need migration to voicetype|FormID_Resp format[/]");
            AnsiConsole.MarkupLine($"  [yellow]This is why export shows ~{entries.Count:N0} instead of ~{totalFiles:N0}[/]");
            AnsiConsole.MarkupLine("  [bold]Fix: Run the app with the updated ApplyToEntries to trigger migration, then re-export[/]");
        }
        else if (entries.Count < totalFiles)
        {
            AnsiConsole.MarkupLine($"  [yellow]JSON has {entries.Count:N0} entries but BSA has {totalFiles:N0} voice files[/]");
            AnsiConsole.MarkupLine("  [yellow]Not all files have been transcribed yet[/]");
        }
        else
        {
            AnsiConsole.MarkupLine("  [green]Key counts look correct[/]");
        }

        return 0;
    }

    private static bool TryParseVoiceFileName(string fileName, out uint formId, out int responseIndex,
        out string topicEditorId)
    {
        formId = 0;
        responseIndex = 0;
        topicEditorId = "";

        var dotIndex = fileName.LastIndexOf('.');
        if (dotIndex < 0) return false;
        var baseName = fileName[..dotIndex];

        var lastUnderscore = baseName.LastIndexOf('_');
        if (lastUnderscore < 0) return false;
        if (!int.TryParse(baseName[(lastUnderscore + 1)..], out responseIndex)) return false;

        var formIdUnderscore = baseName.LastIndexOf('_', lastUnderscore - 1);
        if (formIdUnderscore < 0) return false;

        var formIdPart = baseName[(formIdUnderscore + 1)..lastUnderscore];
        if (formIdPart.Length != 8 ||
            !uint.TryParse(formIdPart, NumberStyles.HexNumber, null, out formId))
        {
            return false;
        }

        topicEditorId = baseName[..formIdUnderscore];
        return true;
    }

    private readonly record struct VoiceFile(uint FormId, int ResponseIndex, string VoiceType, string FileName);
}
