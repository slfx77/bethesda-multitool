using System.CommandLine;
using System.IO.MemoryMappedFiles;
using FalloutXbox360Utils.CLI.Shared;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Minidump;
using FalloutXbox360Utils.Core.Utils;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI.Commands.Dialogue;

/// <summary>
///     CLI command for TESTopicInfo struct debugging from memory dumps.
/// </summary>
internal static class DialogueDebugCommand
{
    internal static Command CreateDebugCommand()
    {
        var command = new Command("debug", "Hex-dump TESTopicInfo struct bytes for offset debugging");

        var inputArg = new Argument<string>("input") { Description = "Path to memory dump (.dmp) file" };
        var countOpt = new Option<int?>("--count") { Description = "Number of records to dump (default 20)" };

        command.Arguments.Add(inputArg);
        command.Options.Add(countOpt);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var input = parseResult.GetValue(inputArg)!;
            var count = parseResult.GetValue(countOpt) ?? 20;
            await RunDebugAsync(input, count, cancellationToken);
        });

        return command;
    }

    private static async Task RunDebugAsync(string input, int count, CancellationToken cancellationToken)
    {
        if (!File.Exists(input))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] File not found: {0}", input);
            return;
        }

        AnsiConsole.MarkupLine("[blue]Loading dump for TESTopicInfo debug...[/]");

        // Use MinidumpAnalyzer directly -- this command is dump-only
        var analyzer = new MinidumpAnalyzer();
        var analysisResult = await CliProgressRunner.RunWithProgressAsync(
            "Analyzing memory dump...",
            (progress, ct) => analyzer.AnalyzeAsync(input, progress, true, false, ct),
            cancellationToken);

        if (analysisResult.EsmRecords == null)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] No ESM records found in dump.");
            return;
        }

        // Use RuntimeEditorIds directly -- entries with DialogueLine set are confirmed INFO entries
        // (the hash table walker already detected INFO FormType and read prompts in pass 2)
        var infoEntries = analysisResult.EsmRecords.RuntimeEditorIds
            .Where(e => e.TesFormOffset.HasValue && e.DialogueLine != null)
            .Take(count)
            .ToList();

        // Also collect ALL INFO entries (with or without DialogueLine) for flag distribution
        // Detect the INFO FormType from entries that have DialogueLine
        byte? infoFormType = infoEntries.Count > 0 ? infoEntries[0].FormType : null;
        var allInfoEntries = infoFormType.HasValue
            ? analysisResult.EsmRecords.RuntimeEditorIds
                .Where(e => e.TesFormOffset.HasValue && e.FormType == infoFormType.Value)
                .ToList()
            : [];

        var fileInfo = new FileInfo(input);
        using var mmf = MemoryMappedFile.CreateFromFile(input, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        using var accessor = mmf.CreateViewAccessor(0, fileInfo.Length, MemoryMappedFileAccess.Read);

        AnsiConsole.MarkupLine($"[dim]Runtime entries total: {analysisResult.EsmRecords.RuntimeEditorIds.Count}[/]");
        AnsiConsole.MarkupLine($"[dim]INFO entries (with prompt): {infoEntries.Count}[/]");
        AnsiConsole.MarkupLine(
            $"[dim]INFO entries (all, FormType 0x{infoFormType ?? 0:X2}): {allInfoEntries.Count}[/]");

        if (infoEntries.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] No INFO entries with DialogueLine found in RuntimeEditorIds.");
            return;
        }

        // TESTopicInfo struct offsets -- TESTopicInfo inherits directly from TESForm,
        // so fields shift by +4 after TESForm base, not +16 like TESBoundObject-derived types.
        var buildType = MinidumpAnalyzer.DetectBuildType(analysisResult.MinidumpInfo!) ?? "Unknown";
        var infoStructSize = 84;
        var infoIndexOff = 36;
        var infoDataOff = 39;
        var infoPromptOff = 44;
        var infoSpeakerOff = 64;
        var infoDiffOff = 72;
        var infoQuestOff = 76;

        AnsiConsole.MarkupLine($"[dim]Build type: {buildType} -> TESTopicInfo struct size: {infoStructSize}B[/]");

        // PART 1: Full struct dump for first 3 entries (field identification)
        AnsiConsole.MarkupLine($"\n[blue]Full struct dump (first 3 entries, {infoStructSize}B each):[/]");
        AnsiConsole.WriteLine();

        foreach (var entry in infoEntries.Take(3))
        {
            var offset = entry.TesFormOffset!.Value;
            if (offset + infoStructSize > fileInfo.Length)
            {
                continue;
            }

            var fullBuf = new byte[infoStructSize];
            accessor.ReadArray(offset, fullBuf, 0, infoStructSize);

            AnsiConsole.MarkupLine(
                $"[yellow]0x{entry.FormId:X8}[/] ({Markup.Escape(entry.EditorId)}) at file+0x{offset:X}");

            // Dump in rows of 16 bytes with field annotations
            for (var row = 0; row < infoStructSize; row += 16)
            {
                var rowLen = Math.Min(16, infoStructSize - row);
                var hex = string.Join(" ", Enumerable.Range(row, rowLen).Select(i => $"{fullBuf[i]:X2}"));
                var ascii = string.Concat(Enumerable.Range(row, rowLen).Select(i =>
                    fullBuf[i] >= 32 && fullBuf[i] < 127 ? (char)fullBuf[i] : '.'));

                AnsiConsole.MarkupLine($"  [dim]+{row,2:D2}[/]: {hex,-48} {Markup.Escape(ascii)}");
            }

            // Annotate known fields using build-detected offsets
            var fid = BinaryUtils.ReadUInt32BE(fullBuf, 12);
            var idx = BinaryUtils.ReadUInt16BE(fullBuf, infoIndexOff);
            var prompt = BinaryUtils.ReadUInt32BE(fullBuf, infoPromptOff);
            var speaker = BinaryUtils.ReadUInt32BE(fullBuf, infoSpeakerOff);
            var diff = BinaryUtils.ReadUInt32BE(fullBuf, infoDiffOff);
            var quest = infoQuestOff + 4 <= infoStructSize ? BinaryUtils.ReadUInt32BE(fullBuf, infoQuestOff) : 0u;

            AnsiConsole.MarkupLine($"  [dim]Fields: FormType=0x{fullBuf[4]:X2} FormID=0x{fid:X8}[/]");
            AnsiConsole.MarkupLine(
                $"  [dim]  +{infoIndexOff}: 0x{idx:X4} (iInfoIndex)  +{infoPromptOff}: 0x{prompt:X8} (cPrompt ptr)[/]");
            AnsiConsole.MarkupLine(
                $"  [dim]  +{infoSpeakerOff}: 0x{speaker:X8} (pSpeaker)  +{infoDiffOff}: 0x{diff:X8} (eDifficulty)[/]");
            AnsiConsole.MarkupLine($"  [dim]  +{infoQuestOff}: 0x{quest:X8} (pOwnerQuest)[/]");
            AnsiConsole.MarkupLine($"  [dim]  Prompt: \"{Markup.Escape(entry.DialogueLine ?? "")}\"[/]");
            AnsiConsole.WriteLine();
        }

        // PART 2: Find bytes that VARY across entries (to distinguish per-record data from constants)
        AnsiConsole.Write(new Rule($"[blue]Byte Variance Analysis ({infoStructSize}B, across all INFO entries)[/]")
            .LeftJustified());

        var varianceTracker = new HashSet<byte>[infoStructSize];
        for (var i = 0; i < infoStructSize; i++)
        {
            varianceTracker[i] = [];
        }

        var sampledCount = 0;
        foreach (var entry in allInfoEntries.Take(500))
        {
            var offset = entry.TesFormOffset!.Value;
            if (offset + infoStructSize > fileInfo.Length)
            {
                continue;
            }

            var buf = new byte[infoStructSize];
            accessor.ReadArray(offset, buf, 0, infoStructSize);
            for (var i = 0; i < infoStructSize; i++)
            {
                varianceTracker[i].Add(buf[i]);
            }

            sampledCount++;
        }

        AnsiConsole.MarkupLine($"  Sampled {sampledCount} entries. Showing byte positions by uniqueness:");
        AnsiConsole.WriteLine();

        // Group bytes by category: constant (1 value), low variance (2-5), per-record (6+)
        var constant = new List<string>();
        var lowVar = new List<string>();
        var perRecord = new List<string>();

        for (var i = 0; i < infoStructSize; i++)
        {
            var uniqueCount = varianceTracker[i].Count;
            var sampleVal = varianceTracker[i].First();

            if (uniqueCount == 1)
            {
                constant.Add($"+{i}=0x{sampleVal:X2}");
            }
            else if (uniqueCount <= 5)
            {
                lowVar.Add($"+{i}({uniqueCount} vals)");
            }
            else
            {
                perRecord.Add($"+{i}");
            }
        }

        AnsiConsole.MarkupLine($"  [dim]Constant (1 value):[/] {string.Join(", ", constant)}");
        AnsiConsole.MarkupLine($"  [yellow]Low variance (2-5 values):[/] {string.Join(", ", lowVar)}");
        AnsiConsole.MarkupLine($"  [green]Per-record (6+ values):[/] {string.Join(", ", perRecord)}");

        // PART 3: For low-variance bytes, show the actual value distribution (these are likely flags)
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[blue]Low-Variance Byte Details (likely flags/enums)[/]").LeftJustified());

        for (var i = 0; i < infoStructSize; i++)
        {
            var uniqueCount = varianceTracker[i].Count;
            if (uniqueCount >= 2 && uniqueCount <= 10)
            {
                // Count each value
                var valCounts = new Dictionary<byte, int>();
                foreach (var entry in allInfoEntries.Take(500))
                {
                    var offset = entry.TesFormOffset!.Value;
                    if (offset + infoStructSize > fileInfo.Length)
                    {
                        continue;
                    }

                    var val = accessor.ReadByte(offset + i);
                    valCounts.TryGetValue(val, out var c);
                    valCounts[val] = c + 1;
                }

                var distStr = string.Join(", ",
                    valCounts.OrderByDescending(kv => kv.Value)
                        .Select(kv => $"0x{kv.Key:X2}={kv.Value}"));
                AnsiConsole.MarkupLine($"  +{i}: {distStr}");
            }
        }

        // Flag distribution for ALL INFO entries using build-detected data offset
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule($"[blue]Flag Distribution -- ALL INFO entries (offset +{infoDataOff})[/]")
            .LeftJustified());
        ShowFlagDistribution(allInfoEntries, accessor, fileInfo.Length, infoDataOff);
    }

    private static string DecodeFlagNames(byte flags)
    {
        var names = new List<string>();
        if ((flags & 0x01) != 0)
        {
            names.Add("Goodbye");
        }

        if ((flags & 0x02) != 0)
        {
            names.Add("Random");
        }

        if ((flags & 0x04) != 0)
        {
            names.Add("RandEnd");
        }

        if ((flags & 0x10) != 0)
        {
            names.Add("SayOnce");
        }

        if ((flags & 0x80) != 0)
        {
            names.Add("Speech");
        }

        return names.Count > 0 ? string.Join("|", names) : "none";
    }

    private static void ShowFlagDistribution(
        List<RuntimeEditorIdEntry> entries,
        MemoryMappedViewAccessor accessor,
        long fileSize,
        int dataOffset)
    {
        var flagCounts = new Dictionary<byte, int>();
        var total = 0;
        foreach (var entry in entries)
        {
            var offset = entry.TesFormOffset!.Value;
            if (offset + dataOffset + 4 > fileSize)
            {
                continue;
            }

            var flags = accessor.ReadByte(offset + dataOffset + 2); // +2 to get flags within TOPIC_INFO_DATA
            flagCounts.TryGetValue(flags, out var c);
            flagCounts[flags] = c + 1;
            total++;
        }

        foreach (var (flags, cnt) in flagCounts.OrderByDescending(kv => kv.Value))
        {
            var pct = 100.0 * cnt / total;
            AnsiConsole.MarkupLine(
                $"  0x{flags:X2} ({DecodeFlagNames(flags),-30}): {cnt,5} ({pct:F1}%)");
        }
    }
}
