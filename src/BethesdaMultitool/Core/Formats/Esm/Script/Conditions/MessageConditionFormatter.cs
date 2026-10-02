using System.Globalization;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;

namespace BethesdaMultitool.Core.Formats.Esm.Script.Conditions;

/// <summary>Plain-text condition details shared by MESG inspection and text reports.</summary>
internal static class MessageConditionFormatter
{
    internal static IEnumerable<string> Format(
        IReadOnlyList<DialogueCondition> conditions, ConditionDisplayContext context)
    {
        yield return $"Conditions ({conditions.Count.ToString(CultureInfo.InvariantCulture)}):";
        var descriptions = ConditionDescriber.DescribeAll(conditions, context);
        foreach (var description in descriptions)
        {
            yield return $"  {description.Index.ToString(CultureInfo.InvariantCulture)}: " +
                         ConditionTextFormatter.FormatLine(description);
        }

        if (ConditionTextFormatter.FormatLogicSummary(descriptions) is { } grouping)
        {
            yield return $"  Grouping: {grouping}";
        }

        if (context.GameAssumed && descriptions.Count > 0)
        {
            yield return $"  Game: {context.Game} assumed (not detected from the input)";
        }
    }
}
