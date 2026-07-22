using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Plugin;

public sealed class PluginBuilderDialogueSanitationOrderTests
{
    [Fact]
    public void CapturedInfoDeduplicationPrecedesQuestVariableSanitationAndRunsOnce()
    {
        var source = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Esm", "Plugin", "Pipeline",
            "PluginBuilder.cs");
        const string deduplicate =
            "DialogueCombinePlanner.DeduplicateInPlace(dmpRecords.Dialogues)";
        const string sanitize = "SanitizeQuestVariableConditions(";

        var deduplicateIndex = source.IndexOf(deduplicate, StringComparison.Ordinal);
        var sanitizeIndex = source.IndexOf(sanitize, StringComparison.Ordinal);

        Assert.True(deduplicateIndex >= 0, "PluginBuilder must deduplicate captured INFO records.");
        Assert.True(sanitizeIndex > deduplicateIndex,
            "Duplicate INFO records must be removed before quest-variable sanitation can reserve locals.");
        Assert.Equal(1, source.Split(deduplicate, StringSplitOptions.None).Length - 1);
    }
}
