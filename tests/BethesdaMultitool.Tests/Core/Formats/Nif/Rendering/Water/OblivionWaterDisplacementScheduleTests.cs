using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Water;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Water;

public sealed class OblivionWaterDisplacementScheduleTests
{
    [Fact]
    public void ConstructorAccumulatorMakesOneRainStepWithZeroDt()
    {
        var state = OblivionWaterSimulationTestData.State;
        var plan = Plan(state, OblivionWaterSimulationTestData.Rain());
        Assert.Equal("RainEvolution,Normal", Stages(plan));
        AssertPass(plan.Passes[0], 1, 4);
        AssertPass(plan.Passes[1], 4, 6);
        Assert.Equal(new OblivionWaterSimulationResource(4), plan.Next.RainHeight);
        Assert.Equal(new OblivionWaterSimulationResource(1), plan.Next.ScratchB);
        Assert.Equal(.025f, plan.Next.EvolutionSeconds);
        Assert.Equal(0f, plan.Next.EventSeconds);
        Assert.Equal(new OblivionWaterSimulationResource(2), plan.Next.WadingHeight);
        Assert.Equal(new OblivionWaterSimulationResource(3), plan.Next.ScratchA);
        Assert.Equal(OblivionWaterSimulationOutput.OrdinaryNormal, plan.OutputRoute);
        Assert.Equal(1, plan.Next.LastInvocation);
        Assert.Equal(OblivionWaterSimulationTestData.State, state);
    }

    [Fact]
    public void ExactRainIntervalDoesNotEvolveButStillPublishesNormal()
    {
        var state = OblivionWaterSimulationTestData.State with { EvolutionSeconds = .025f };
        var plan = Plan(state, OblivionWaterSimulationTestData.Rain());
        Assert.Equal("Normal", Stages(plan));
        AssertPass(plan.Passes[0], 1, 6);
        Assert.Equal(state.RainHeight, plan.Next.RainHeight);
        Assert.Equal(.025f, plan.Next.EvolutionSeconds);
    }

    [Fact]
    public void EveryRainStepSwapsAndNoSmoothingPassIsInserted()
    {
        var state = OblivionWaterSimulationTestData.State with { EvolutionSeconds = .08f };
        var plan = Plan(state, OblivionWaterSimulationTestData.Rain());
        Assert.Equal("RainEvolution,RainEvolution,RainEvolution,Normal", Stages(plan));
        AssertPass(plan.Passes[0], 1, 4);
        AssertPass(plan.Passes[1], 4, 1);
        AssertPass(plan.Passes[2], 1, 4);
        Assert.DoesNotContain(plan.Passes, pass => pass.ProgramIndex == 4);
        Assert.All(plan.Passes, pass => Assert.Equal(OblivionWaterSimulationAddress.Wrap, pass.Address));
    }

    [Fact]
    public void FirstWadingStepUsesRecenteredAWithoutSwapping()
    {
        var plan = Plan(OblivionWaterSimulationTestData.State, OblivionWaterSimulationTestData.Wading());
        Assert.Equal("Recenter,WadingEvolution,Normal", Stages(plan));
        AssertPass(plan.Passes[0], 2, 3);
        AssertPass(plan.Passes[1], 3, 2);
        AssertPass(plan.Passes[2], 2, 7);
        Assert.Equal(new OblivionWaterSimulationResource(2), plan.Next.WadingHeight);
        Assert.Equal(new Vector3(6, 7, 8), plan.Passes[1].Controls);
        Assert.Equal(9f, plan.Passes[2].Dampener);
        Assert.All(plan.Passes, pass => Assert.Equal(OblivionWaterSimulationAddress.Clamp, pass.Address));
        Assert.Equal(OblivionWaterSimulationOutput.WadingNormal, plan.OutputRoute);
    }

    [Fact]
    public void SecondWadingStepSwapsAndChangesPublishedCallerReference()
    {
        var state = OblivionWaterSimulationTestData.State with { EvolutionSeconds = .08f };
        var plan = Plan(state, OblivionWaterSimulationTestData.Wading());
        Assert.Equal("Recenter,WadingEvolution,WadingEvolution,Normal", Stages(plan));
        AssertPass(plan.Passes[1], 3, 2);
        AssertPass(plan.Passes[2], 2, 3);
        AssertPass(plan.Passes[3], 3, 7);
        Assert.Equal(new OblivionWaterSimulationResource(3), plan.Next.WadingHeight);
        Assert.Equal(new OblivionWaterSimulationResource(2), plan.Next.ScratchA);
    }

    [Fact]
    public void ZeroStepWadingStampRemainsInScratchAndNormalUsesOldCallerState()
    {
        var state = OblivionWaterSimulationTestData.State with { EvolutionSeconds = .033f };
        var stamp = new OblivionWaterRecordedWadingStamp(new Vector4(1, 0, 0, .25f), new Vector4(0, 1, 0, -.25f));
        var invocation = OblivionWaterSimulationTestData.Wading() with
        {
            WadingStamp = stamp,
            RecenterOffset = new Vector2(.125f, -.25f)
        };
        var plan = Plan(state, invocation);
        Assert.Equal("Recenter,WadingStamp,Normal", Stages(plan));
        AssertPass(plan.Passes[0], 2, 3);
        Assert.Equal(invocation.RecenterOffset, plan.Passes[0].Offset);
        Assert.Null(plan.Passes[1].Input0);
        Assert.Equal(new OblivionWaterSimulationResource(3), plan.Passes[1].Output);
        Assert.Equal(stamp, plan.Passes[1].WadingStamp);
        AssertPass(plan.Passes[2], 2, 7);
        Assert.Equal(state.WadingHeight, plan.Next.WadingHeight);
        Assert.Equal(.033f, plan.Next.EvolutionSeconds);
    }

    [Fact]
    public void InteriorRainBlendsFftHeightWithBThenAssignsFinalInputWithoutSwappingMixedTarget()
    {
        var state = OblivionWaterSimulationTestData.State;
        var plan = Plan(state, OblivionWaterSimulationTestData.Rain(amount: .5f));
        Assert.Equal("FftAbsoluteHeight,RainEvolution,MixedHeight,Normal", Stages(plan));
        AssertPass(plan.Passes[0], 8, 9);
        Assert.True(plan.Passes[0].UsesHeightMapProgram);
        AssertPass(plan.Passes[1], 1, 4);
        AssertPass(plan.Passes[2], 9, 5);
        Assert.Equal(new OblivionWaterSimulationResource(1), plan.Passes[2].Input1);
        Assert.Equal(.5f, plan.Passes[2].BlendAmount);
        Assert.Equal(4f, plan.Passes[2].Dampener);
        AssertPass(plan.Passes[3], 5, 6);
        Assert.Equal(new OblivionWaterSimulationResource(4), plan.Next.RainHeight);
        Assert.Equal(new OblivionWaterSimulationResource(5), plan.Next.FinalInput);
        Assert.Equal(state.MixedHeight, plan.Next.MixedHeight);
        Assert.True(plan.Next.MixedHeightResident);
        Assert.True(plan.AcquireMixedHeight);
        Assert.True(plan.AcquireFftIntermediate);
    }

    [Fact]
    public void ZeroStepInteriorBlendReadsExistingBNotCurrentT()
    {
        var state = OblivionWaterSimulationTestData.State with { EvolutionSeconds = .025f };
        var plan = Plan(state, OblivionWaterSimulationTestData.Rain(amount: .5f));
        Assert.Equal("FftAbsoluteHeight,MixedHeight,Normal", Stages(plan));
        Assert.Equal(new OblivionWaterSimulationResource(4), plan.Passes[1].Input1);
        Assert.Equal(new OblivionWaterSimulationResource(1), plan.Next.RainHeight);
        Assert.Equal(new OblivionWaterSimulationResource(5), plan.Next.FinalInput);
    }

    [Fact]
    public void RepeatedInteriorBlendReusesMixedAllocationWithoutSwappingItWithCallerState()
    {
        var first = Plan(OblivionWaterSimulationTestData.State, OblivionWaterSimulationTestData.Rain(amount: .5f));
        var second = Plan(first.Next, OblivionWaterSimulationTestData.Rain(2, .25f));
        Assert.Equal("FftAbsoluteHeight,MixedHeight,Normal", Stages(second));
        Assert.False(second.AcquireMixedHeight);
        Assert.False(second.AcquireFftIntermediate);
        Assert.Equal(new OblivionWaterSimulationResource(5), second.Next.MixedHeight);
        Assert.Equal(new OblivionWaterSimulationResource(4), second.Next.RainHeight);
        Assert.Equal(new OblivionWaterSimulationResource(1), second.Passes[1].Input1);
        AssertPass(second.Passes[2], 5, 6);
    }

    [Fact]
    public void RetainedCallerFftResourceCannotBeSilentlyReplaced()
    {
        var first = Plan(OblivionWaterSimulationTestData.State, OblivionWaterSimulationTestData.Rain(amount: .5f));
        Assert.Throws<ArgumentException>(() => Plan(first.Next,
            OblivionWaterSimulationTestData.Rain(2, .5f) with
            {
                FftIntermediate = new OblivionWaterSimulationResource(10)
            }));
    }

    [Fact]
    public void ZeroBlendRainRunsOnlyFftNormalAndPreservesEffectHistoryAndResources()
    {
        var interior = Plan(OblivionWaterSimulationTestData.State, OblivionWaterSimulationTestData.Rain(amount: .5f));
        var invocation = OblivionWaterSimulationTestData.Rain(2, 0f) with { DeltaSeconds = 10f, RainRate = 100 };
        var plan = Plan(interior.Next, invocation);
        Assert.Equal("FftNormal", Stages(plan));
        AssertPass(plan.Passes[0], 8, 6);
        Assert.True(plan.Passes[0].UsesHeightMapProgram);
        Assert.False(plan.InvokedDisplacement);
        Assert.Equal(interior.Next with { LastInvocation = 2 }, plan.Next);
        Assert.False(plan.ReleaseMixedHeight);
        Assert.False(plan.ReleaseFftIntermediate);
    }

    [Fact]
    public void FullRainBlendReleasesIntermediatesAndRetainsExternalAlias()
    {
        var interior = Plan(OblivionWaterSimulationTestData.State, OblivionWaterSimulationTestData.Rain(amount: .5f));
        var invocation = OblivionWaterSimulationTestData.Rain(2) with { RawFftHeight = null, FftIntermediate = null };
        var plan = Plan(interior.Next, invocation);
        Assert.Equal("Normal", Stages(plan));
        AssertPass(plan.Passes[0], 4, 6);
        Assert.True(plan.ReleaseMixedHeight);
        Assert.True(plan.ReleaseFftIntermediate);
        Assert.False(plan.Next.MixedHeightResident);
        Assert.Null(plan.Next.FftIntermediate);
        Assert.Equal(new OblivionWaterSimulationResource(9), plan.Next.ExternalHeight);
        Assert.Equal(new OblivionWaterSimulationResource(4), plan.Next.FinalInput);
    }

    [Fact]
    public void WadingAtGlobalFullRainBlendAlsoReleasesMixedTargetButNotRainOwnerFftIntermediate()
    {
        var interior = Plan(OblivionWaterSimulationTestData.State, OblivionWaterSimulationTestData.Rain(amount: .5f));
        var invocation = OblivionWaterSimulationTestData.Wading(2) with { RainBlendAmount = 1f };
        var plan = Plan(interior.Next, invocation);
        Assert.Equal("Recenter,Normal", Stages(plan));
        Assert.True(plan.ReleaseMixedHeight);
        Assert.False(plan.ReleaseFftIntermediate);
        Assert.Equal(new OblivionWaterSimulationResource(9), plan.Next.FftIntermediate);
        Assert.Equal(interior.Next.RainHeight, plan.Next.RainHeight);
    }

    [Fact]
    public void WadingAddsToSharedEventAndEvolutionClocksConsumedByNextRainInvocation()
    {
        var initial = OblivionWaterSimulationTestData.State with { EvolutionSeconds = 0f };
        var first = Plan(initial, OblivionWaterSimulationTestData.Wading() with { DeltaSeconds = .015625f });
        Assert.Equal("Recenter,Normal", Stages(first));
        Assert.Equal(.015625f, first.Next.EvolutionSeconds);
        Assert.InRange(first.Next.EventSeconds, .115624f, .115626f);
        var invocation = OblivionWaterSimulationTestData.Rain(2) with
        {
            DeltaSeconds = .015625f, RainRate = 8,
            RainSamples = [new OblivionWaterRecordedRainSample(0, 32767)]
        };
        var second = Plan(first.Next, invocation);
        Assert.Equal("RainStamp,RainEvolution,Normal", Stages(second));
        Assert.Equal(new Vector2(-1, 1), second.Passes[0].Offset);
        Assert.Equal(5f, second.Passes[0].StampScale);
        Assert.Equal(0f, second.Next.EventSeconds);
        Assert.InRange(second.Next.EvolutionSeconds, .006249f, .006251f);
        Assert.Equal(0, OblivionWaterSimulationMath.RainEventCount(8, .115625f));
    }

    [Fact]
    public void SubunitRainEventDebtIsDiscardedEvenWithNoStamp()
    {
        var first = Plan(OblivionWaterSimulationTestData.State,
            OblivionWaterSimulationTestData.Rain() with { RainRate = 9 });
        Assert.DoesNotContain(first.Passes, pass => pass.Stage == OblivionWaterSimulationStage.RainStamp);
        Assert.Equal(0f, first.Next.EventSeconds);
        var second = Plan(first.Next,
            OblivionWaterSimulationTestData.Rain(2) with { RainRate = 9, DeltaSeconds = .1f });
        Assert.DoesNotContain(second.Passes, pass => pass.Stage == OblivionWaterSimulationStage.RainStamp);
        Assert.Equal(0f, second.Next.EventSeconds);
    }

    [Fact]
    public void AbsentFinalTargetSkipsOnlyNormalPass()
    {
        var plan = Plan(OblivionWaterSimulationTestData.State,
            OblivionWaterSimulationTestData.Rain() with { NormalOutput = null });
        Assert.Equal("RainEvolution", Stages(plan));
        Assert.Null(plan.PublishedNormal);
        Assert.Equal(new OblivionWaterSimulationResource(4), plan.Next.RainHeight);
    }

    [Fact]
    public void RecordedEventsMustMatchExactlyAndNeverChooseAnImplicitSeed()
    {
        var state = OblivionWaterSimulationTestData.State;
        var invocation = OblivionWaterSimulationTestData.Rain() with { RainRate = 10 };
        Assert.Throws<ArgumentException>(() => Plan(state, invocation));
        var tooMany = invocation with
        {
            RainSamples = [new OblivionWaterRecordedRainSample(0, 0), new OblivionWaterRecordedRainSample(1, 1)]
        };
        Assert.Throws<ArgumentException>(() => Plan(state, tooMany));
        var invalidRandom = invocation with { RainSamples = [new OblivionWaterRecordedRainSample(-1, 0)] };
        Assert.Throws<ArgumentOutOfRangeException>(() => Plan(state, invalidRandom));
        Assert.Equal(OblivionWaterSimulationTestData.State, state);
    }

    [Fact]
    public void InsufficientBudgetRejectsWholePlanWithoutAdvancingState()
    {
        var state = OblivionWaterSimulationTestData.State;
        Assert.Throws<InvalidOperationException>(() => OblivionWaterDisplacementSchedule.Plan(state,
            OblivionWaterSimulationTestData.Wading(), OblivionWaterSimulationTestData.Inputs, 1));
        Assert.Equal(OblivionWaterSimulationTestData.State, state);
        Assert.Equal("Recenter,WadingEvolution,Normal", Stages(Plan(state, OblivionWaterSimulationTestData.Wading())));
    }

    [Fact]
    public void MissingRawHeightRejectsPartialAndZeroRainBlendButFullBlendDoesNotNeedIt()
    {
        var state = OblivionWaterSimulationTestData.State;
        Assert.Throws<ArgumentException>(() => Plan(state,
            OblivionWaterSimulationTestData.Rain(amount: .5f) with { RawFftHeight = null }));
        Assert.Throws<ArgumentException>(() => Plan(state,
            OblivionWaterSimulationTestData.Rain(amount: 0f) with { RawFftHeight = null }));
        Assert.Equal("RainEvolution,Normal", Stages(Plan(state,
            OblivionWaterSimulationTestData.Rain() with { RawFftHeight = null, FftIntermediate = null })));
    }

    [Fact]
    public void DuplicateOrMissingInvocationOrderCannotConsumeSharedClockTwice()
    {
        var first = Plan(OblivionWaterSimulationTestData.State, OblivionWaterSimulationTestData.Rain());
        Assert.Throws<ArgumentException>(() => Plan(first.Next, OblivionWaterSimulationTestData.Rain()));
        Assert.Throws<ArgumentException>(() => Plan(first.Next, OblivionWaterSimulationTestData.Rain(3)));
    }

    [Fact]
    public void InvalidResourceAliasesAndUninitializedSampleArrayAreRejected()
    {
        var state = OblivionWaterSimulationTestData.State;
        Assert.Throws<ArgumentException>(() => Plan(state with { ScratchA = state.RainHeight },
            OblivionWaterSimulationTestData.Wading()));
        Assert.Throws<ArgumentException>(() => Plan(state,
            OblivionWaterSimulationTestData.Rain() with { NormalOutput = state.WadingHeight }));
        Assert.Throws<ArgumentException>(() => Plan(state,
            OblivionWaterSimulationTestData.Rain() with { RainSamples = default }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ImpossibleRetainedResourceIdsAreRejectedWithoutInventingSharedLifetimes(int field)
    {
        var state = OblivionWaterSimulationTestData.State;
        state = field switch
        {
            0 => state with { FinalInput = new OblivionWaterSimulationResource(-1) },
            1 => state with { ExternalHeight = new OblivionWaterSimulationResource(0) },
            2 => state with { FftIntermediate = new OblivionWaterSimulationResource(-1) },
            _ => state with { FinalInput = new OblivionWaterSimulationResource(99) }
        };
        Assert.Throws<ArgumentException>(() => Plan(state, OblivionWaterSimulationTestData.Rain()));
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(-1f)]
    public void UnsupportedRecordedTimeCannotPoisonSharedState(float seconds)
    {
        Assert.Throws<ArgumentException>(() => Plan(OblivionWaterSimulationTestData.State,
            OblivionWaterSimulationTestData.Rain() with { DeltaSeconds = seconds }));
    }

    private static OblivionWaterDisplacementPlan Plan(
        OblivionWaterDisplacementState state, OblivionWaterDisplacementInvocation invocation)
    {
        return OblivionWaterDisplacementSchedule.Plan(state, invocation, OblivionWaterSimulationTestData.Inputs, 100);
    }

    private static string Stages(OblivionWaterDisplacementPlan plan)
    {
        return string.Join(',', plan.Passes.Select(pass => pass.Stage));
    }

    private static void AssertPass(OblivionWaterSimulationPass pass, int source, int destination)
    {
        Assert.Equal(new OblivionWaterSimulationResource(source), pass.Input0);
        Assert.Equal(new OblivionWaterSimulationResource(destination), pass.Output);
    }
}