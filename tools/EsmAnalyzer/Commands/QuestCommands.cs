using System.Buffers.Binary;
using System.CommandLine;
using System.Globalization;
using System.Text;
using EsmAnalyzer.Helpers;
using Spectre.Console;

namespace EsmAnalyzer.Commands;

/// <summary>
///     Commands for comparing quest link data (SCRI/QOBJ/QSTA) between two ESM files.
/// </summary>
public static class QuestCommands
{
    public static Command CreateCompareQuestLinksCommand()
    {
        var command = new Command("compare-quest-links",
            "Compare QUST link data (SCRI/QOBJ/QSTA) between two ESM files");

        var leftArg = new Argument<string>("left") { Description = "Path to the left ESM file" };
        var rightArg = new Argument<string>("right") { Description = "Path to the right ESM file" };
        var limitOption = new Option<int>("-l", "--limit")
        {
            Description = "Maximum number of differences to show (0 = unlimited)",
            DefaultValueFactory = _ => 50
        };
        var showAllOption = new Option<bool>("--all")
        {
            Description = "Show all quests, not just differences"
        };
        var formIdOption = new Option<string?>("--formid")
        {
            Description = "Quest FormID to inspect (hex, e.g., 0x00080664)"
        };

        command.Arguments.Add(leftArg);
        command.Arguments.Add(rightArg);
        command.Options.Add(limitOption);
        command.Options.Add(showAllOption);
        command.Options.Add(formIdOption);

        command.SetAction(parseResult => CompareQuestLinks(
            parseResult.GetValue(leftArg)!,
            parseResult.GetValue(rightArg)!,
            parseResult.GetValue(limitOption),
            parseResult.GetValue(showAllOption),
            parseResult.GetValue(formIdOption)));

        return command;
    }

    private static int CompareQuestLinks(string leftPath, string rightPath, int limit, bool showAll, string? formIdText)
    {
        var left = EsmFileLoader.Load(leftPath);
        var right = EsmFileLoader.Load(rightPath);
        if (left == null || right == null) return 1;

        var leftQuests = LoadQuestLinks(left);
        var rightQuests = LoadQuestLinks(right);

        uint? filterFormId = null;
        if (!string.IsNullOrWhiteSpace(formIdText))
        {
            if (!TryParseFormId(formIdText, out var parsedFormId))
            {
                AnsiConsole.MarkupLine($"[red]ERROR:[/] Invalid FormID: {formIdText}");
                return 1;
            }

            filterFormId = parsedFormId;
        }

        var allKeys = leftQuests.Keys.Union(rightQuests.Keys)
            .Where(id => filterFormId == null || id == filterFormId.Value)
            .OrderBy(x => x)
            .ToList();

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("FormID")
            .AddColumn("EDID")
            .AddColumn("SCRI (L)")
            .AddColumn("SCRI (R)")
            .AddColumn("QOBJ (L)")
            .AddColumn("QOBJ (R)")
            .AddColumn("QSTA (L)")
            .AddColumn("QSTA (R)")
            .AddColumn("Status");

        var shown = 0;
        var diffs = 0;

        foreach (var formId in allKeys)
        {
            var hasLeft = leftQuests.TryGetValue(formId, out var leftQuest);
            var hasRight = rightQuests.TryGetValue(formId, out var rightQuest);

            if (!hasLeft || !hasRight)
            {
                diffs++;
                if (!showAll && limit > 0 && shown >= limit) continue;

                table.AddRow(
                    $"0x{formId:X8}",
                    leftQuest?.Edid ?? rightQuest?.Edid ?? "—",
                    hasLeft ? leftQuest!.ScriDisplay : "—",
                    hasRight ? rightQuest!.ScriDisplay : "—",
                    hasLeft ? leftQuest!.QobjDisplay : "—",
                    hasRight ? rightQuest!.QobjDisplay : "—",
                    hasLeft ? leftQuest!.QstaDisplay : "—",
                    hasRight ? rightQuest!.QstaDisplay : "—",
                    hasLeft ? "Missing right" : "Missing left");

                shown++;
                continue;
            }

            var diffFields = leftQuest!.GetDiffFields(rightQuest!);
            var matches = diffFields.Count == 0;
            if (matches && !showAll) continue;

            if (!matches) diffs++;
            if (limit > 0 && shown >= limit) continue;

            table.AddRow(
                $"0x{formId:X8}",
                leftQuest!.Edid ?? rightQuest!.Edid ?? "—",
                leftQuest!.ScriDisplay,
                rightQuest!.ScriDisplay,
                leftQuest!.QobjDisplay,
                rightQuest!.QobjDisplay,
                leftQuest!.QstaDisplay,
                rightQuest!.QstaDisplay,
                matches ? "MATCH" : $"DIFF ({string.Join(",", diffFields)})");

            shown++;
        }

        AnsiConsole.MarkupLine("[cyan]Quest link comparison[/]");
        AnsiConsole.MarkupLine($"Left:  {Path.GetFileName(leftPath)}");
        AnsiConsole.MarkupLine($"Right: {Path.GetFileName(rightPath)}");
        AnsiConsole.MarkupLine($"Differences: {diffs:N0}");
        AnsiConsole.Write(table);

        return diffs == 0 ? 0 : 1;
    }

    private static Dictionary<uint, QuestLinkInfo> LoadQuestLinks(EsmFileLoadResult file)
    {
        var quests = EsmHelpers.ScanForRecordType(file.Data, file.IsBigEndian, "QUST");
        var recordIndex = EsmHelpers.ScanAllRecords(file.Data, file.IsBigEndian)
            .GroupBy(r => r.FormId)
            .ToDictionary(g => g.Key, g => g.First());

        var map = new Dictionary<uint, QuestLinkInfo>();
        foreach (var quest in quests)
        {
            var data = EsmHelpers.GetRecordData(file.Data, quest, file.IsBigEndian);
            var subs = EsmHelpers.ParseSubrecords(data, file.IsBigEndian);

            var edid = TryDecodeString(subs.FirstOrDefault(s => s.Signature == "EDID")?.Data);
            var scriSub = subs.FirstOrDefault(s => s.Signature == "SCRI");
            var scri = scriSub != null ? ReadUInt32(scriSub.Data, file.IsBigEndian) : (uint?)null;

            var qobj = subs.Where(s => s.Signature == "QOBJ")
                .Select(s => ReadUInt32(s.Data, file.IsBigEndian))
                .ToList();

            var qstaTargets = subs.Where(s => s.Signature == "QSTA")
                .Select(s => s.Data.Length >= 4 ? ReadUInt32(s.Data, file.IsBigEndian) : 0u)
                .ToList();

            var scriDisplay = BuildScriDisplay(scri, recordIndex, file);
            var qobjDisplay = qobj.Count == 0
                ? "—"
                : string.Join(",", qobj.Select(v => v.ToString(CultureInfo.InvariantCulture)));
            var qstaDisplay = qstaTargets.Count == 0 ? "—" : $"{qstaTargets.Count}";

            map[quest.FormId] = new QuestLinkInfo(
                quest.FormId,
                edid,
                scri,
                qobj,
                qstaTargets,
                scriDisplay,
                qobjDisplay,
                qstaDisplay);
        }

        return map;
    }

    private static string BuildScriDisplay(uint? scri, Dictionary<uint, AnalyzerRecordInfo> index,
        EsmFileLoadResult file)
    {
        if (!scri.HasValue) return "—";
        if (!index.TryGetValue(scri.Value, out var record)) return $"0x{scri.Value:X8} (missing)";

        string? edid = null;
        try
        {
            var data = EsmHelpers.GetRecordData(file.Data, record, file.IsBigEndian);
            var subs = EsmHelpers.ParseSubrecords(data, file.IsBigEndian);
            edid = TryDecodeString(subs.FirstOrDefault(s => s.Signature == "EDID")?.Data);
        }
        catch
        {
            edid = null;
        }

        return edid == null
            ? $"0x{scri.Value:X8}"
            : $"0x{scri.Value:X8} ({edid})";
    }

    private static uint ReadUInt32(byte[] data, bool bigEndian)
    {
        if (data.Length < 4) return 0;
        return bigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(0, 4))
            : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0, 4));
    }

    private static bool TryParseFormId(string text, out uint formId)
    {
        formId = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            text = text[2..];

        return uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out formId);
    }

    private static string? TryDecodeString(byte[]? data)
    {
        if (data == null || data.Length == 0) return null;

        var nullIdx = Array.IndexOf(data, (byte)0);
        var len = nullIdx >= 0 ? nullIdx : data.Length;
        if (len <= 0) return null;

        var str = Encoding.UTF8.GetString(data, 0, len);
        return str.All(c => !char.IsControl(c) || c is '\r' or '\n' or '\t') ? str : null;
    }

    private sealed record QuestLinkInfo(
        uint FormId,
        string? Edid,
        uint? Scri,
        List<uint> Qobj,
        List<uint> QstaTargets,
        string ScriDisplay,
        string QobjDisplay,
        string QstaDisplay)
    {
        public List<string> GetDiffFields(QuestLinkInfo other)
        {
            var diffs = new List<string>();
            if (!Nullable.Equals(Scri, other.Scri)) diffs.Add("SCRI");
            if (!Qobj.SequenceEqual(other.Qobj)) diffs.Add("QOBJ");
            if (!QstaTargets.SequenceEqual(other.QstaTargets)) diffs.Add("QSTA");
            return diffs;
        }
    }
}
