using System.Globalization;
using System.Text;

namespace BethesdaMultitool.Core.Formats.Esm.Script.Conditions;

/// <summary>
///     Plain-text rendering of <see cref="ConditionDescription" />s for the CLI and text reports. Output carries
///     no Spectre markup and contains <c>[</c>/<c>]</c>, so a caller writing markup must escape it.
///     <para>
///         Unlike the dialogue viewer, this text always states Run On (even the default Subject) and always
///         prints a FormID as <c>EDID [0xFORMID]</c>, so a line read on its own is unambiguous.
///     </para>
/// </summary>
public static class ConditionTextFormatter
{
    /// <summary>
    ///     How <see cref="FormatLogicSummary" /> groups conditions. The grouping is the GECK's documented
    ///     convention and has not been verified against the engine's condition evaluator.
    /// </summary>
    public const string GroupingConventionNote =
        "GECK convention: conditions joined by the OR flag form a group, and groups are joined by AND; " +
        "not verified against the engine";

    /// <summary>
    ///     One condition as a single line, e.g.
    ///     <c>GetQuestVariable(VDialogueVegasNorth [0x000F2429], GhoulDealtWith [var 11]) == 2 [Run On: Subject]</c>.
    ///     A condition that is not last ends with its connector (<c> AND</c> or <c> OR</c>); a last condition that
    ///     still carries the OR flag ends with <c> [OR flag on last condition]</c>. The index is not included.
    /// </summary>
    public static string FormatLine(ConditionDescription description)
        => FormatLine(description, includeConnector: true);

    /// <summary>Formats a canonical condition value when list connectors are stored separately.</summary>
    public static string FormatLine(ConditionDescription description, bool includeConnector)
    {
        ArgumentNullException.ThrowIfNull(description);

        var qualifiers = new List<string>(4) { $"Run On: {description.RunOn}" };
        if (description.ReferenceDisplay is { } reference)
        {
            qualifiers.Add($"Ref: {reference}");
        }

        if (description.Parameter3Label is { } parameter3)
        {
            qualifiers.Add(parameter3);
        }

        qualifiers.AddRange(ConditionTypeFlags.Describe(description.TypeRaw, description.Game));

        var line = new StringBuilder(description.Expression.Length + 48);
        line.Append(description.Expression);
        line.Append(" [");
        line.AppendJoin("; ", qualifiers);
        line.Append(']');
        if (includeConnector && description.ConnectorToNext is { } connector)
        {
            line.Append(' ');
            line.Append(connector);
        }

        if (includeConnector && description.OrFlagOnLast)
        {
            line.Append(" [OR flag on last condition]");
        }

        return line.ToString();
    }

    /// <summary>Formats every condition with <see cref="FormatLine(ConditionDescription)" />, in order.</summary>
    public static IReadOnlyList<string> FormatLines(IReadOnlyList<ConditionDescription> descriptions)
    {
        ArgumentNullException.ThrowIfNull(descriptions);
        return descriptions.Select(FormatLine).ToArray();
    }

    /// <summary>
    ///     A one-line summary of how the stored connectors group the list, by condition index, e.g.
    ///     <c>(1 OR 2) AND 3</c>, followed by <see cref="GroupingConventionNote" />, and a warning when the last
    ///     condition carries the OR flag. Returns null for an empty list and for a single condition without the
    ///     OR flag, where there is nothing to group.
    /// </summary>
    public static string? FormatLogicSummary(IReadOnlyList<ConditionDescription> descriptions)
    {
        ArgumentNullException.ThrowIfNull(descriptions);
        if (descriptions.Count == 0 || (descriptions.Count == 1 && !descriptions[0].OrFlagOnLast))
        {
            return null;
        }

        var groups = descriptions
            .GroupBy(description => description.OrGroup)
            .OrderBy(group => group.Key)
            .Select(group => group.Select(description => description.Index.ToString(CultureInfo.InvariantCulture))
                .ToArray())
            .ToArray();
        var parenthesize = groups.Length > 1;
        var expression = string.Join(" AND ", groups.Select(members =>
            parenthesize && members.Length > 1
                ? $"({string.Join(" OR ", members)})"
                : string.Join(" OR ", members)));

        var summary = $"{expression} ({GroupingConventionNote})";
        var last = descriptions[^1];
        return last.OrFlagOnLast
            ? $"{summary}; warning: condition {last.Index.ToString(CultureInfo.InvariantCulture)} is last but carries the OR flag, which joins it to nothing"
            : summary;
    }

    /// <summary>
    ///     A FormID as <c>EDID [0xFORMID]</c>; <c>0xFORMID (no EditorID)</c> when the resolver has no EditorID
    ///     (which does not mean the form is absent); <c>0x00000000 (none)</c> for a null FormID.
    /// </summary>
    public static string FormatFormId(uint formId, string? editorId)
    {
        if (!string.IsNullOrEmpty(editorId))
        {
            return $"{editorId} [0x{formId:X8}]";
        }

        return formId == 0 ? "0x00000000 (none)" : $"0x{formId:X8} (no EditorID)";
    }
}
