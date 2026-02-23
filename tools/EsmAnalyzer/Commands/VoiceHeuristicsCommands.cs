using System.CommandLine;
using FalloutXbox360Utils.Core.Formats.Esm;
using Spectre.Console;
using static EsmAnalyzer.Commands.VoiceFileMatcher;

namespace EsmAnalyzer.Commands;

/// <summary>
///     Cross-reference BSA voice files against ESM records to diagnose matching quality.
/// </summary>
public static class VoiceHeuristicsCommands
{
    public static Command CreateVoiceHeuristicsCommand()
    {
        var command = new Command("voice-heuristics",
            "Cross-reference BSA voice files against ESM records");

        var dirArg = new Argument<string>("data-dir")
        {
            Description = "Path to the Data directory containing BSA and ESM files"
        };

        var limitOption = new Option<int>("-l", "--limit")
        {
            Description = "Number of sample mismatches to display",
            DefaultValueFactory = _ => 20
        };

        var esmOption = new Option<string?>("--esm")
        {
            Description = "Override ESM path (use a different ESM than the one in data-dir)"
        };

        command.Arguments.Add(dirArg);
        command.Options.Add(limitOption);
        command.Options.Add(esmOption);

        command.SetAction(parseResult =>
            Run(parseResult.GetValue(dirArg)!, parseResult.GetValue(limitOption),
                parseResult.GetValue(esmOption)));

        return command;
    }

    private static int Run(string dataDir, int limit, string? esmOverride)
    {
        if (!Directory.Exists(dataDir))
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] Directory not found: {dataDir}");
            return 1;
        }

        // ── Step 1: Find files ──────────────────────────────────────
        var bsaPaths = Directory.GetFiles(dataDir, "*.bsa")
            .Where(f =>
            {
                var name = Path.GetFileName(f);
                return name.Contains("Voice", StringComparison.OrdinalIgnoreCase);
            })
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var esmPath = esmOverride ?? FindEsm(dataDir);

        if (bsaPaths.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]ERROR:[/] No Fallout - Voices*.bsa files found");
            return 1;
        }

        if (esmPath == null)
        {
            AnsiConsole.MarkupLine("[red]ERROR:[/] No .esm file found");
            return 1;
        }

        AnsiConsole.MarkupLine($"[blue]Data directory:[/] {dataDir}");
        AnsiConsole.MarkupLine($"[blue]ESM:[/] {Path.GetFileName(esmPath)}");
        AnsiConsole.MarkupLine($"[blue]BSAs:[/] {string.Join(", ", bsaPaths.Select(Path.GetFileName))}");
        AnsiConsole.WriteLine();

        // ── Step 2: Parse voice files from BSAs ─────────────────────
        var voiceFiles = ExtractVoiceFiles(bsaPaths);

        var uniqueFormIds = voiceFiles.Select(v => v.FormId).ToHashSet();
        var uniqueTopics = voiceFiles.Select(v => v.TopicEditorId)
            .Where(t => t.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // ── Step 3: Parse ESM ───────────────────────────────────────
        AnsiConsole.MarkupLine("[blue]Parsing ESM...[/]");
        var esmData = File.ReadAllBytes(esmPath);
        var records = EsmParser.EnumerateRecords(esmData);

        var infoEntries = new Dictionary<uint, InfoData>();
        var dialEntries = new Dictionary<string, DialData>(StringComparer.OrdinalIgnoreCase);
        var npcFormIds = new HashSet<uint>();
        var questFormIds = new HashSet<uint>();
        var questEdidToName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var vtypEditorIds = new Dictionary<uint, string>();
        var npcVoiceTypes = new Dictionary<uint, uint>();
        var npcNames = new Dictionary<uint, string>();

        foreach (var record in records)
        {
            switch (record.Header.Signature)
            {
                case "INFO":
                {
                    // Xbox 360 splits each INFO into two records with the same FormID:
                    //   Base:     DATA, QSTI, ANAM, CTDA, PNAM (conditions, speaker, quest)
                    //   Response: TRDT, NAM1 (subtitle text)
                    // Merge both halves by preserving non-null fields from either record.
                    var hasNam1 = record.Subrecords.Any(s => s.Signature == "NAM1");
                    var anamFormId = record.Subrecords
                        .FirstOrDefault(s => s.Signature == "ANAM" && s.Data.Length >= 4)
                        ?.DataAsFormId;
                    var qstiFormId = record.Subrecords
                        .FirstOrDefault(s => s.Signature == "QSTI" && s.Data.Length >= 4)
                        ?.DataAsFormId;

                    var fid = record.Header.FormId;
                    if (infoEntries.TryGetValue(fid, out var existing))
                    {
                        infoEntries[fid] = new InfoData(
                            existing.HasNam1 || hasNam1,
                            existing.AnamFormId ?? anamFormId,
                            existing.QstiFormId ?? qstiFormId);
                    }
                    else
                    {
                        infoEntries[fid] = new InfoData(hasNam1, anamFormId, qstiFormId);
                    }

                    break;
                }
                case "DIAL":
                {
                    var editorId = record.EditorId;
                    if (editorId != null)
                    {
                        var qstiFormId = record.Subrecords
                            .FirstOrDefault(s => s.Signature == "QSTI" && s.Data.Length >= 4)
                            ?.DataAsFormId;
                        var tnamFormId = record.Subrecords
                            .FirstOrDefault(s => s.Signature == "TNAM" && s.Data.Length >= 4)
                            ?.DataAsFormId;

                        dialEntries[editorId] = new DialData(qstiFormId, tnamFormId);
                    }

                    break;
                }
                case "NPC_":
                case "CREA":
                {
                    npcFormIds.Add(record.Header.FormId);
                    var fullName = record.Subrecords
                        .FirstOrDefault(s => s.Signature == "FULL")
                        ?.DataAsString;
                    var editorId = record.EditorId;
                    var fallback = record.Header.Signature == "CREA"
                        ? $"CREA_{record.Header.FormId:X8}"
                        : $"NPC_{record.Header.FormId:X8}";
                    npcNames[record.Header.FormId] = fullName ?? editorId ?? fallback;

                    var vtckFormId = record.Subrecords
                        .FirstOrDefault(s => s.Signature == "VTCK" && s.Data.Length >= 4)
                        ?.DataAsFormId;
                    if (vtckFormId.HasValue)
                    {
                        npcVoiceTypes[record.Header.FormId] = vtckFormId.Value;
                    }

                    break;
                }
                case "QUST":
                {
                    questFormIds.Add(record.Header.FormId);
                    var editorId = record.EditorId;
                    if (editorId != null)
                    {
                        var fullName = record.Subrecords
                            .FirstOrDefault(s => s.Signature == "FULL")
                            ?.DataAsString;
                        questEdidToName[editorId] = fullName ?? editorId;
                    }

                    break;
                }
                case "VTYP":
                {
                    var editorId = record.EditorId;
                    if (editorId != null)
                    {
                        vtypEditorIds[record.Header.FormId] = editorId;
                    }

                    break;
                }
            }
        }

        // ── Step 4: Compute metrics ─────────────────────────────────
        AnsiConsole.WriteLine();

        VoiceResultRenderer.RenderEsmIndex(infoEntries, dialEntries, npcFormIds,
            questFormIds, vtypEditorIds, npcVoiceTypes);

        // Build voicetype reverse map: lowercase VTYP EDID -> list of NPC names
        var voiceTypeToNpcs = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (npcFid, vtypFid) in npcVoiceTypes)
        {
            if (!vtypEditorIds.TryGetValue(vtypFid, out var vtypEdid))
            {
                continue;
            }

            var name = npcNames.GetValueOrDefault(npcFid, $"NPC_{npcFid:X8}");
            if (!voiceTypeToNpcs.TryGetValue(vtypEdid, out var list))
            {
                list = [];
                voiceTypeToNpcs[vtypEdid] = list;
            }

            list.Add(name);
        }

        var uniqueVoiceTypes = voiceTypeToNpcs.Where(kv => kv.Value.Count == 1).ToList();
        var sharedVoiceTypes = voiceTypeToNpcs.Where(kv => kv.Value.Count > 1).ToList();

        VoiceResultRenderer.RenderVoiceFilesSummary(voiceFiles, uniqueFormIds, uniqueTopics);

        var results = ComputeMatches(voiceFiles, uniqueFormIds, uniqueTopics,
            infoEntries, dialEntries, npcFormIds, questFormIds,
            questEdidToName, voiceTypeToNpcs);

        VoiceResultRenderer.RenderMatchResults(results, voiceFiles.Count, uniqueTopics,
            uniqueVoiceTypes, sharedVoiceTypes);

        // ── Step 5: Sample mismatches ───────────────────────────────
        VoiceResultRenderer.RenderUnmatchedSamples(results, limit);

        // ── Step 6: Diagnostic tables ───────────────────────────────
        if (limit > 0)
        {
            VoiceResultRenderer.RenderDiagnostics(voiceFiles, uniqueTopics, dialEntries,
                infoEntries, npcFormIds, questFormIds, questEdidToName,
                voiceTypeToNpcs, uniqueVoiceTypes, sharedVoiceTypes, limit);
        }

        return 0;
    }
}
