using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Script.Conditions;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Script;

public sealed class DialogueConditionReferencePolicyTests
{
    [Theory]
    [InlineData(BethesdaGame.FalloutNewVegas, 0x0001, 2, true)]
    [InlineData(BethesdaGame.FalloutNewVegas, 0x006A, 2, false)]
    [InlineData(BethesdaGame.FalloutNewVegas, 0x011D, 2, false)]
    [InlineData(BethesdaGame.Fallout3, 0x006A, 2, true)]
    [InlineData(BethesdaGame.Fallout4, 0x011D, 2, true)]
    [InlineData(BethesdaGame.Starfield, 0x0001, 2, true)]
    [InlineData(BethesdaGame.FalloutNewVegas, 0x0001, 4, false)]
    [InlineData(BethesdaGame.Unknown, 0x0001, 2, false)]
    public void IsSemanticReferenceSlot_MatchesGameAwareXEditPolicy(
        BethesdaGame game,
        int functionIndex,
        uint runOn,
        bool expected)
    {
        Assert.Equal(expected, DialogueConditionReferencePolicy.IsSemanticReferenceSlot(
            (ushort)functionIndex,
            runOn,
            game));
    }

    [Fact]
    public void TryGetSemanticReference_RequiresNonzeroValue()
    {
        var condition = new DialogueCondition { FunctionIndex = 1, RunOn = 2, Reference = 0 };

        Assert.False(DialogueConditionReferencePolicy.TryGetSemanticReference(
            condition,
            BethesdaGame.FalloutNewVegas,
            out var reference));
        Assert.Equal(0u, reference);
    }
}