using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Parsing;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin;

/// <summary>One retail INFO and its authoritative type-7 Topic Children parent.</summary>
internal sealed record MasterDialogueInfo(
    ParsedMainRecord Record,
    uint ParentDialFormId,
    int FileOrder,
    IReadOnlySet<uint> ExactSpeakerFormIds);

/// <summary>
///     Raw-order index of retail DIAL/INFO records. INFO parentage is structural in TES4:
///     the parent DIAL is the label on the enclosing type-7 GRUP, not a field carried by
///     INFO and not necessarily the topic attribution recovered from a runtime dump.
/// </summary>
internal sealed class MasterDialogueIndex
{
    private const uint GreetingDialFormId = 0x000000C8;
    private readonly Dictionary<uint, MasterDialogueInfo> _infosByFormId;
    private readonly HashSet<uint> _exactGreetingSpeakers;

    private MasterDialogueIndex(Dictionary<uint, MasterDialogueInfo> infosByFormId)
    {
        _infosByFormId = infosByFormId;
        _exactGreetingSpeakers = infosByFormId.Values
            .Where(info => info.ParentDialFormId == GreetingDialFormId)
            .SelectMany(info => info.ExactSpeakerFormIds)
            .Where(static speaker => speaker != 0)
            .ToHashSet();
    }

    public IReadOnlyCollection<MasterDialogueInfo> Infos => _infosByFormId.Values;

    public bool TryGetInfo(uint formId, out MasterDialogueInfo info) =>
        _infosByFormId.TryGetValue(formId, out info!);

    public bool HasExactGreetingCoverage(uint speakerFormId) =>
        speakerFormId != 0 && _exactGreetingSpeakers.Contains(speakerFormId);

    /// <summary>Build from the parsed master stream and its raw GRUP headers.</summary>
    public static MasterDialogueIndex Build(
        IReadOnlyList<ParsedMainRecord> records,
        IReadOnlyList<GrupHeaderInfo> grupHeaders)
    {
        var topicGroups = grupHeaders
            .Where(static group => group.GroupType == 7 && group.Label.Length >= 4)
            .ToList();

        var events = new List<(long Offset, GrupHeaderInfo? Group, ParsedMainRecord? Record)>(
            topicGroups.Count + records.Count);
        events.AddRange(topicGroups.Select(group =>
            (group.Offset, (GrupHeaderInfo?)group, (ParsedMainRecord?)null)));
        events.AddRange(records
            .Where(static record => record.Header.Signature == "INFO")
            .Select(record => (record.Offset, (GrupHeaderInfo?)null, (ParsedMainRecord?)record)));
        events.Sort(static (left, right) =>
        {
            var byOffset = left.Offset.CompareTo(right.Offset);
            if (byOffset != 0)
            {
                return byOffset;
            }

            return left.Group is not null ? -1 : 1;
        });

        var stack = new Stack<GrupHeaderInfo>();
        var infos = new Dictionary<uint, MasterDialogueInfo>();
        var order = 0;
        foreach (var current in events)
        {
            while (stack.Count > 0
                   && current.Offset >= stack.Peek().Offset + stack.Peek().GroupSize)
            {
                stack.Pop();
            }

            if (current.Group is not null)
            {
                stack.Push(current.Group);
                continue;
            }

            var record = current.Record!;
            var parentGroup = stack.FirstOrDefault(group =>
                current.Offset >= group.Offset + GrupHeaderInfo.HeaderSize
                && current.Offset < group.Offset + group.GroupSize);
            if (parentGroup is null)
            {
                continue;
            }

            var parentDial = BinaryPrimitives.ReadUInt32LittleEndian(parentGroup.Label);
            infos.TryAdd(record.Header.FormId,
                new MasterDialogueInfo(record, parentDial, order++, ExtractExactSpeakers(record)));
        }

        // Synthetic/unit fixtures and unusually stripped masters may omit GRUP metadata.
        // Fill only missing entries through the legacy offset-order relationship; raw GRUP
        // ancestry remains authoritative whenever it is available.
        AddOrderFallback(records, infos, ref order);
        return new MasterDialogueIndex(infos);
    }

    /// <summary>Fallback used by focused callers that only have parsed records.</summary>
    public static MasterDialogueIndex BuildFromRecordOrder(IEnumerable<ParsedMainRecord> records)
    {
        var materialized = records.ToList();
        var infos = new Dictionary<uint, MasterDialogueInfo>();
        var order = 0;
        AddOrderFallback(materialized, infos, ref order);
        return new MasterDialogueIndex(infos);
    }

    private static void AddOrderFallback(
        IEnumerable<ParsedMainRecord> records,
        Dictionary<uint, MasterDialogueInfo> infos,
        ref int order)
    {
        uint currentDial = 0;
        foreach (var record in records
                     .Where(static record => record.Header.Signature is "DIAL" or "INFO")
                     .OrderBy(static record => record.Offset))
        {
            if (record.Header.Signature == "DIAL")
            {
                currentDial = record.Header.FormId;
                continue;
            }

            if (currentDial == 0 || infos.ContainsKey(record.Header.FormId))
            {
                continue;
            }

            infos.Add(record.Header.FormId,
                new MasterDialogueInfo(record, currentDial, order++, ExtractExactSpeakers(record)));
        }
    }

    private static HashSet<uint> ExtractExactSpeakers(ParsedMainRecord info)
    {
        var speakers = new HashSet<uint>();
        foreach (var subrecord in info.Subrecords)
        {
            if (subrecord.Signature == "ANAM" && subrecord.Data.Length >= 4)
            {
                var speaker = BinaryPrimitives.ReadUInt32LittleEndian(subrecord.Data);
                if (speaker != 0)
                {
                    speakers.Add(speaker);
                }

                continue;
            }

            if (subrecord.Signature != "CTDA" || subrecord.Data.Length < 16)
            {
                continue;
            }

            if (!DialogueSpeakerBinding.IsPositiveSubjectGetIsId(subrecord.Data))
            {
                continue;
            }

            var parameter = BinaryPrimitives.ReadUInt32LittleEndian(subrecord.Data.AsSpan(12, 4));
            if (parameter != 0)
            {
                speakers.Add(parameter);
            }
        }

        return speakers;
    }
}
