using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Script.Conditions;

namespace BethesdaMultitool.Core.Formats.Esm.Export.Support;

/// <summary>Identical capture diagnostics for message reports and record displays.</summary>
public static class RuntimeMessageEvidenceFormatter
{
    public static IEnumerable<string> Format(RuntimeMessageEvidence? evidence)
    {
        if (evidence == null) yield break;
        yield return $"Runtime description: {evidence.DescriptionStatus}; source file offset: " +
                     (evidence.DescriptionFileOffset is { } offset ? $"0x{offset:X8}" : "unknown");
        yield return $"Runtime button list: {evidence.ButtonListStatus}";
        foreach (var line in FormatDescriptionMappings(evidence)) yield return line;
        foreach (var button in evidence.Buttons)
        {
            yield return $"Runtime button index {button.Index}: text={button.TextStatus}; conditions={button.ConditionStatus}; " +
                         $"item VA=0x{button.ItemVirtualAddress:X8}; text VA=" +
                         (button.TextVirtualAddress is { } va ? $"0x{va:X8}" : "unknown");
        }
    }

    public static string SourceToken(MessageFieldSource source) => source switch
    {
        MessageFieldSource.StoredRecord => "stored-record",
        MessageFieldSource.RuntimeObject => "runtime-object",
        MessageFieldSource.RuntimeMappedRecord => "runtime-mapped-record",
        _ => "unavailable"
    };

    public static IEnumerable<string> FormatSelectedSources(MessageRecord message)
    {
        yield return $"Description source: {SourceToken(message.DescriptionSource)}";
        yield return $"Buttons source: {SourceToken(message.ButtonSource)}";
        if (message.StoredButtonCount is { } count) yield return $"Stored button count: {count}";
        if (message.RuntimeButtons is { } runtime)
            yield return $"Runtime object offset: 0x{runtime.ObjectFileOffset:X}";
    }

    public static IEnumerable<string> FormatAlternative(MessageRecord message, ConditionDisplayContext conditions)
    {
        foreach (var runtime in Alternatives(message))
        {
            yield return $"Runtime alternative ({runtime.Buttons.Count} buttons); object offset: 0x{runtime.ObjectFileOffset:X}:";
            foreach (var line in Format(runtime.Evidence)) yield return "  " + line;
            for (var index = 0; index < runtime.Buttons.Count; index++)
            {
                yield return $"  [{index + 1}] {runtime.Buttons[index]}";
                foreach (var line in MessageConditionFormatter.Format(runtime.GetConditions(index), conditions))
                    yield return "    " + line;
            }
        }
    }

    public static IEnumerable<RuntimeMessageButtons> Alternatives(MessageRecord message)
    {
        if (message.ButtonSource != MessageFieldSource.RuntimeObject && message.RuntimeButtons is { } runtime)
            yield return runtime;
        foreach (var additional in message.AdditionalRuntimeButtons) yield return additional;
    }

    public static IEnumerable<string> FormatDescriptionMappings(RuntimeMessageEvidence? evidence)
    {
        if (evidence == null) yield break;
        foreach (var mapping in evidence.DescriptionMappings)
        {
            yield return $"Description mapping: {mapping.Status}; base VA=0x{mapping.SegmentBaseVirtualAddress:X8}; " +
                         $"record VA=0x{mapping.RecordVirtualAddress:X8}; dump offset={mapping.RecordDumpOffset?.ToString() ?? "unknown"}; " +
                         $"calibration={mapping.CalibrationMatches} match(es), example 0x{mapping.CalibrationExampleFormId:X8}; " +
                         $"DESC VA={(mapping.DescriptionVirtualAddress is { } va ? $"0x{va:X8}" : "unknown")}; " +
                         $"DESC dump offset={mapping.DescriptionDumpOffset?.ToString() ?? "unknown"}" +
                         (mapping.Text != null ? $"; text={mapping.Text}" : "");
        }
    }
}
