using System.CommandLine;
using System.Globalization;
using FalloutXbox360Utils.Core.Formats.Bsa;
using FalloutXbox360Utils.Core.Formats.Esm;
using Spectre.Console;

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
                if (pathParts.Length < 4)
                {
                    continue;
                }

                var voiceType = pathParts[3];

                foreach (var file in folder.Files)
                {
                    if (file.Name == null)
                    {
                        continue;
                    }

                    var ext = Path.GetExtension(file.Name).TrimStart('.').ToLowerInvariant();
                    if (ext is not ("xma" or "wav" or "mp3" or "ogg"))
                    {
                        continue;
                    }

                    if (TryParseVoiceFileName(file.Name, out var formId, out _, out var topicEditorId))
                    {
                        voiceFiles.Add(new VoiceFile(formId, topicEditorId, voiceType, file.Name));
                    }
                }
            }
        }

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
        var vtypEditorIds = new Dictionary<uint, string>();       // VTYP FormID → EditorID
        var npcVoiceTypes = new Dictionary<uint, uint>();          // NPC FormID → VTYP FormID
        var npcNames = new Dictionary<uint, string>();             // NPC FormID → display name

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
                        // Merge: keep non-null from either half
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

        // ESM Index table
        var esmTable = new Table().Border(TableBorder.Rounded).Title("[bold]ESM Index[/]");
        esmTable.AddColumn("Record Type");
        esmTable.AddColumn(new TableColumn("Count").RightAligned());
        esmTable.AddRow("INFO records", $"{infoEntries.Count:N0}");
        esmTable.AddRow("DIAL topics", $"{dialEntries.Count:N0}");
        esmTable.AddRow("NPC_ records", $"{npcFormIds.Count:N0}");
        esmTable.AddRow("QUST records", $"{questFormIds.Count:N0}");
        esmTable.AddRow("VTYP records", $"{vtypEditorIds.Count:N0}");
        esmTable.AddRow("NPCs with VTCK", $"{npcVoiceTypes.Count:N0}");
        AnsiConsole.Write(esmTable);
        AnsiConsole.WriteLine();

        // Build voicetype reverse map: lowercase VTYP EDID → list of NPC names
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

        // Voice Files table
        var uniqueVoiceTypeNames = voiceFiles.Select(v => v.VoiceType)
            .Where(t => t.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var voiceTable = new Table().Border(TableBorder.Rounded).Title("[bold]Voice Files[/]");
        voiceTable.AddColumn("Metric");
        voiceTable.AddColumn(new TableColumn("Count").RightAligned());
        voiceTable.AddRow("Total voice files", $"{voiceFiles.Count:N0}");
        voiceTable.AddRow("Unique FormIDs", $"{uniqueFormIds.Count:N0}");
        voiceTable.AddRow("Unique TopicEditorIds", $"{uniqueTopics.Count:N0}");
        voiceTable.AddRow("Unique VoiceType folders", $"{uniqueVoiceTypeNames.Count:N0}");
        AnsiConsole.Write(voiceTable);
        AnsiConsole.WriteLine();

        // FormID → INFO matching
        var formIdMatched = 0;
        var formIdWithNam1 = 0;
        var formIdWithAnam = 0;
        var formIdWithQsti = 0;
        var unmatchedFormIds = new List<(uint FormId, string TopicEditorId)>();

        foreach (var vf in voiceFiles)
        {
            if (infoEntries.TryGetValue(vf.FormId, out var info))
            {
                formIdMatched++;
                if (info.HasNam1) formIdWithNam1++;
                if (info.AnamFormId.HasValue) formIdWithAnam++;
                if (info.QstiFormId.HasValue) formIdWithQsti++;
            }
            else
            {
                unmatchedFormIds.Add((vf.FormId, vf.TopicEditorId));
            }
        }

        // TopicEditorId → DIAL matching (case-sensitive vs case-insensitive)
        var topicsCaseSensitive = 0;
        var topicsCaseInsensitive = 0;
        var topicsWithQsti = 0;
        var topicsWithTnam = 0;
        var unmatchedTopics = new List<string>();

        // Use a case-sensitive set for the case-sensitive check
        var dialEdidsCaseSensitive = new HashSet<string>(dialEntries.Keys, StringComparer.Ordinal);

        foreach (var topic in uniqueTopics)
        {
            if (dialEdidsCaseSensitive.Contains(topic))
            {
                topicsCaseSensitive++;
            }

            if (dialEntries.TryGetValue(topic, out var dial))
            {
                topicsCaseInsensitive++;
                if (dial.QstiFormId.HasValue) topicsWithQsti++;
                if (dial.TnamFormId.HasValue) topicsWithTnam++;
            }
            else
            {
                unmatchedTopics.Add(topic);
            }
        }

        // VoiceType → unique NPC matching
        var vtypMatchedUnique = 0;
        var vtypMatchedShared = 0;
        var vtypUnmatched = 0;

        foreach (var vf in voiceFiles)
        {
            if (vf.VoiceType.Length > 0 && voiceTypeToNpcs.TryGetValue(vf.VoiceType, out var npcs))
            {
                if (npcs.Count == 1)
                {
                    vtypMatchedUnique++;
                }
                else
                {
                    vtypMatchedShared++;
                }
            }
            else if (vf.VoiceType.Length > 0)
            {
                vtypUnmatched++;
            }
        }

        // Combined enrichment: simulate what Enrich() would produce
        var enrichedSubtitle = 0;
        var enrichedSpeaker = 0;
        var enrichedSpeakerViaVtyp = 0;
        var enrichedQuest = 0;
        var enrichedQuestViaPrefix = 0;

        foreach (var vf in voiceFiles)
        {
            var hasSubtitle = false;
            var hasSpeaker = false;
            var hasQuest = false;

            // Primary: INFO lookup
            if (infoEntries.TryGetValue(vf.FormId, out var info))
            {
                if (info.HasNam1) hasSubtitle = true;
                if (info.AnamFormId.HasValue && npcFormIds.Contains(info.AnamFormId.Value)) hasSpeaker = true;
                if (info.QstiFormId.HasValue && questFormIds.Contains(info.QstiFormId.Value)) hasQuest = true;
            }

            // Fallback 1: DIAL topic lookup (case-insensitive)
            if (vf.TopicEditorId.Length > 0 && dialEntries.TryGetValue(vf.TopicEditorId, out var dial))
            {
                if (!hasSpeaker && dial.TnamFormId.HasValue && npcFormIds.Contains(dial.TnamFormId.Value))
                {
                    hasSpeaker = true;
                }

                if (!hasQuest && dial.QstiFormId.HasValue && questFormIds.Contains(dial.QstiFormId.Value))
                {
                    hasQuest = true;
                }
            }

            // Fallback 2: VoiceType → unique NPC speaker
            if (!hasSpeaker && vf.VoiceType.Length > 0 &&
                voiceTypeToNpcs.TryGetValue(vf.VoiceType, out var vtypNpcs) && vtypNpcs.Count == 1)
            {
                hasSpeaker = true;
                enrichedSpeakerViaVtyp++;
            }

            // Fallback 3: Quest EDID prefix from filename (e.g., "vms19_greeting" → quest "VMS19")
            if (!hasQuest && vf.TopicEditorId.Length > 0)
            {
                var usIdx = vf.TopicEditorId.IndexOf('_');
                if (usIdx > 0 && questEdidToName.ContainsKey(vf.TopicEditorId[..usIdx]))
                {
                    hasQuest = true;
                    enrichedQuestViaPrefix++;
                }
            }

            if (hasSubtitle) enrichedSubtitle++;
            if (hasSpeaker) enrichedSpeaker++;
            if (hasQuest) enrichedQuest++;
        }

        // Matching table
        var total = voiceFiles.Count;
        var matchTable = new Table().Border(TableBorder.Rounded).Title("[bold]Matching Results[/]");
        matchTable.AddColumn("Metric");
        matchTable.AddColumn(new TableColumn("Result").RightAligned());

        matchTable.AddRow("[bold]FormID → INFO match[/]", Frac(formIdMatched, total));
        matchTable.AddRow("  with NAM1 (subtitle)", $"{formIdWithNam1:N0}");
        matchTable.AddRow("  with ANAM (speaker ref)", $"{formIdWithAnam:N0}");
        matchTable.AddRow("  with QSTI (quest ref)", $"{formIdWithQsti:N0}");
        matchTable.AddRow("", "");

        var topicTotal = uniqueTopics.Count;
        matchTable.AddRow("[bold]TopicEditorId → DIAL match[/]", "");
        matchTable.AddRow("  [red]Case-sensitive[/]", Frac(topicsCaseSensitive, topicTotal));
        matchTable.AddRow("  [green]Case-insensitive[/]", Frac(topicsCaseInsensitive, topicTotal));
        matchTable.AddRow("  with QSTI (quest ref)", $"{topicsWithQsti:N0}");
        matchTable.AddRow("  with TNAM (speaker ref)", $"{topicsWithTnam:N0}");
        matchTable.AddRow("", "");

        matchTable.AddRow("[bold]VoiceType folder → VTYP match[/]", "");
        matchTable.AddRow("  [green]Unique NPC (1:1)[/]", Frac(vtypMatchedUnique, total));
        matchTable.AddRow("  Shared (multiple NPCs)", Frac(vtypMatchedShared, total));
        matchTable.AddRow("  Unmatched", $"{vtypUnmatched:N0}");
        matchTable.AddRow($"  Unique voice types: {uniqueVoiceTypes.Count}", "");
        matchTable.AddRow($"  Shared voice types: {sharedVoiceTypes.Count}", "");
        matchTable.AddRow("", "");

        matchTable.AddRow("[bold]Combined enrichment[/]", "");
        matchTable.AddRow("  Subtitle (NAM1)", Frac(enrichedSubtitle, total));
        matchTable.AddRow("  Speaker (ANAM/TNAM/VTYP→NPC_)", Frac(enrichedSpeaker, total));
        matchTable.AddRow("    via VTYP unique speaker", $"+{enrichedSpeakerViaVtyp:N0}");
        matchTable.AddRow("  Quest (QSTI/filename prefix→QUST)", Frac(enrichedQuest, total));
        matchTable.AddRow("    via filename prefix", $"+{enrichedQuestViaPrefix:N0}");

        AnsiConsole.Write(matchTable);
        AnsiConsole.WriteLine();

        // ── Step 5: Sample mismatches ───────────────────────────────
        if (limit > 0 && unmatchedFormIds.Count > 0)
        {
            var fmTable = new Table().Border(TableBorder.Rounded)
                .Title($"[bold]Unmatched FormIDs[/] (first {Math.Min(limit, unmatchedFormIds.Count)})");
            fmTable.AddColumn("FormID");
            fmTable.AddColumn("TopicEditorId");

            foreach (var (fid, tid) in unmatchedFormIds.Distinct().Take(limit))
            {
                fmTable.AddRow($"{fid:X8}", tid);
            }

            AnsiConsole.Write(fmTable);
            AnsiConsole.MarkupLine($"[dim]Total unmatched: {unmatchedFormIds.Distinct().Count():N0}[/]");
            AnsiConsole.WriteLine();
        }

        if (limit > 0 && unmatchedTopics.Count > 0)
        {
            var tmTable = new Table().Border(TableBorder.Rounded)
                .Title($"[bold]Unmatched TopicEditorIds[/] (first {Math.Min(limit, unmatchedTopics.Count)})");
            tmTable.AddColumn("TopicEditorId (from BSA filename)");

            foreach (var topic in unmatchedTopics.Take(limit))
            {
                tmTable.AddRow(topic);
            }

            AnsiConsole.Write(tmTable);
            AnsiConsole.MarkupLine($"[dim]Total unmatched: {unmatchedTopics.Count:N0}[/]");
            AnsiConsole.WriteLine();
        }

        if (unmatchedFormIds.Count == 0 && unmatchedTopics.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]All voice files matched![/]");
        }

        // ── Step 6: Diagnostic — sample DIAL EDIDs vs filename TopicEditorIds ──
        if (limit > 0)
        {
            var diagTable = new Table().Border(TableBorder.Rounded)
                .Title("[bold]Sample DIAL EDIDs from ESM[/]");
            diagTable.AddColumn("DIAL EDID");
            diagTable.AddColumn("Has QSTI");
            diagTable.AddColumn("Has TNAM");

            foreach (var (edid, data) in dialEntries.Take(limit))
            {
                diagTable.AddRow(
                    edid,
                    data.QstiFormId.HasValue ? "Yes" : "No",
                    data.TnamFormId.HasValue ? "Yes" : "No");
            }

            AnsiConsole.Write(diagTable);
            AnsiConsole.WriteLine();

            var topicSample = new Table().Border(TableBorder.Rounded)
                .Title("[bold]Sample TopicEditorIds from BSA filenames[/]");
            topicSample.AddColumn("TopicEditorId");
            topicSample.AddColumn("Sample Filename");

            foreach (var topic in uniqueTopics.Take(limit))
            {
                var sample = voiceFiles.First(v =>
                    v.TopicEditorId.Equals(topic, StringComparison.OrdinalIgnoreCase));
                topicSample.AddRow(topic, sample.FileName);
            }

            AnsiConsole.Write(topicSample);
            AnsiConsole.WriteLine();

            // Unique voice types (1:1 NPC mapping)
            var vtypTable = new Table().Border(TableBorder.Rounded)
                .Title($"[bold]Unique Voice Types (1:1 NPC)[/] (first {Math.Min(limit, uniqueVoiceTypes.Count)})");
            vtypTable.AddColumn("VTYP EDID");
            vtypTable.AddColumn("NPC Name");
            vtypTable.AddColumn(new TableColumn("Voice Files").RightAligned());

            foreach (var (edid, npcs) in uniqueVoiceTypes
                         .OrderByDescending(kv =>
                             voiceFiles.Count(v => v.VoiceType.Equals(kv.Key, StringComparison.OrdinalIgnoreCase)))
                         .Take(limit))
            {
                var fileCount = voiceFiles.Count(v =>
                    v.VoiceType.Equals(edid, StringComparison.OrdinalIgnoreCase));
                vtypTable.AddRow(edid, npcs[0], $"{fileCount:N0}");
            }

            AnsiConsole.Write(vtypTable);
            AnsiConsole.MarkupLine($"[dim]Total unique voice types: {uniqueVoiceTypes.Count}[/]");
            AnsiConsole.WriteLine();

            // Top shared voice types
            if (sharedVoiceTypes.Count > 0)
            {
                var sharedTable = new Table().Border(TableBorder.Rounded)
                    .Title($"[bold]Top Shared Voice Types[/] (first {Math.Min(limit, sharedVoiceTypes.Count)})");
                sharedTable.AddColumn("VTYP EDID");
                sharedTable.AddColumn(new TableColumn("NPCs").RightAligned());
                sharedTable.AddColumn(new TableColumn("Voice Files").RightAligned());

                foreach (var (edid, npcs) in sharedVoiceTypes
                             .OrderByDescending(kv => kv.Value.Count)
                             .Take(limit))
                {
                    var fileCount = voiceFiles.Count(v =>
                        v.VoiceType.Equals(edid, StringComparison.OrdinalIgnoreCase));
                    sharedTable.AddRow(edid, $"{npcs.Count:N0}", $"{fileCount:N0}");
                }

                AnsiConsole.Write(sharedTable);
            }

            AnsiConsole.WriteLine();

            // Files with neither speaker NOR quest — show full filenames
            var noMatchFiles = new List<(uint FormId, string FileName, string VoiceType, string Missing)>();
            foreach (var vf in voiceFiles)
            {
                var hasSpeaker = false;
                var hasQuest = false;

                if (infoEntries.TryGetValue(vf.FormId, out var inf))
                {
                    if (inf.AnamFormId.HasValue && npcFormIds.Contains(inf.AnamFormId.Value))
                    {
                        hasSpeaker = true;
                    }

                    if (inf.QstiFormId.HasValue && questFormIds.Contains(inf.QstiFormId.Value))
                    {
                        hasQuest = true;
                    }
                }

                // VTYP fallback for speaker
                if (!hasSpeaker && vf.VoiceType.Length > 0 &&
                    voiceTypeToNpcs.TryGetValue(vf.VoiceType, out var vtNpcs) && vtNpcs.Count == 1)
                {
                    hasSpeaker = true;
                }

                // Quest EDID prefix fallback from filename
                if (!hasQuest && vf.TopicEditorId.Length > 0)
                {
                    var usIdx = vf.TopicEditorId.IndexOf('_');
                    if (usIdx > 0)
                    {
                        var prefix = vf.TopicEditorId[..usIdx];
                        if (questEdidToName.ContainsKey(prefix))
                        {
                            hasQuest = true;
                        }
                    }
                }

                if (!hasSpeaker || !hasQuest)
                {
                    var missing = (!hasSpeaker && !hasQuest) ? "speaker+quest"
                        : !hasSpeaker ? "speaker"
                        : "quest";
                    noMatchFiles.Add((vf.FormId, vf.FileName, vf.VoiceType, missing));
                }
            }

            // Summary counts
            var noSpeakerCount = noMatchFiles.Count(x => x.Missing.Contains("speaker"));
            var noQuestCount = noMatchFiles.Count(x => x.Missing.Contains("quest"));
            var noBothCount = noMatchFiles.Count(x => x.Missing == "speaker+quest");
            AnsiConsole.MarkupLine(
                $"[bold]Unmatched files:[/] {noSpeakerCount:N0} missing speaker, {noQuestCount:N0} missing quest, {noBothCount:N0} missing both");
            AnsiConsole.WriteLine();

            // Show files missing both first, then speaker-only, then quest-only
            var noMatchTable = new Table().Border(TableBorder.Rounded)
                .Title($"[bold]Files Without Speaker or Quest[/] (first {Math.Min(limit, noMatchFiles.Count)} of {noMatchFiles.Count:N0})");
            noMatchTable.AddColumn("FormID");
            noMatchTable.AddColumn("Filename");
            noMatchTable.AddColumn("VoiceType");
            noMatchTable.AddColumn("Missing");

            foreach (var ns in noMatchFiles
                         .OrderByDescending(x => x.Missing == "speaker+quest")
                         .ThenByDescending(x => x.Missing == "quest")
                         .Take(limit))
            {
                var vtInfo = voiceTypeToNpcs.TryGetValue(ns.VoiceType, out var vtList)
                    ? $"{ns.VoiceType} ({vtList.Count} NPCs)"
                    : ns.VoiceType;
                noMatchTable.AddRow($"{ns.FormId:X8}", ns.FileName, vtInfo, ns.Missing);
            }

            AnsiConsole.Write(noMatchTable);
        }

        return 0;
    }

    private static string Frac(int n, int total)
    {
        if (total == 0) return "N/A";
        return $"{n:N0} / {total:N0} ({100.0 * n / total:F1}%)";
    }

    private static string? FindEsm(string dataDir)
    {
        var esmPath = Path.Combine(dataDir, "FalloutNV.esm");
        if (File.Exists(esmPath)) return esmPath;

        var esmFiles = Directory.GetFiles(dataDir, "*.esm");
        return esmFiles.Length > 0 ? esmFiles[0] : null;
    }

    /// <summary>
    ///     Parse voice filename: {topicEditorId}_{formId:8hex}_{index}.{ext}
    ///     Inlined from FalloutAudioTranscriber.Models.VoiceFileNameParser (can't reference WinUI project).
    /// </summary>
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

    private readonly record struct VoiceFile(uint FormId, string TopicEditorId, string VoiceType, string FileName);

    private readonly record struct InfoData(bool HasNam1, uint? AnamFormId, uint? QstiFormId);

    private readonly record struct DialData(uint? QstiFormId, uint? TnamFormId);
}
