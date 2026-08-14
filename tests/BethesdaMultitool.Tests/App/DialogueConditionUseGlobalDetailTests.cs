using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.App;

public sealed class DialogueConditionUseGlobalDetailTests
{
    [Fact]
    public void BuildRecordDetailRows_AddsGlobalComparisonAsNavigableConditionReference()
    {
        var source = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "App", "Helpers", "Dialogue",
            "DialogueRecordDetailBuilder.cs");

        Assert.Contains("if (cond.ComparisonGlobalFormId != 0)", source, StringComparison.Ordinal);
        Assert.Contains("refs.Add(cond.ComparisonGlobalFormId);", source, StringComparison.Ordinal);
    }
}
