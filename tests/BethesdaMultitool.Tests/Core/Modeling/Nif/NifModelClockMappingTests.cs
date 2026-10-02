using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;
using BethesdaMultitool.Core.Modeling.Nif;
using Slfx77.Multitool.Core.Models;
using Xunit;
using static BethesdaMultitool.Tests.Core.Modeling.Nif.NifModelAnimationTestSupport;

namespace BethesdaMultitool.Tests.Core.Modeling.Nif;

/// <summary>
///     RE-22 (<see cref="NifModelClockMapping" />), through Shared's public <see cref="SceneAnimationClock.Map" />. The
///     oracle is the engine's clock restated from RE-22's answer (NiTimeController::ComputeScaledTime: s = f t + p; LOOP
///     fmod(s - lo, L) + lo; REVERSE fmod(s, 2L) anchored at 0; clamp to [lo, hi]; play backwards hi - (s - lo)), sampled
///     away from exact wrap instants and compared within RE-22's 2e-6 s. Controls: the phase dropped, Reverse anchored at
///     lo, play backwards ignored, REVERSE kept on a sequence, and the free-running rule applied to a sequence-driven
///     controller.
/// </summary>
public sealed class NifModelClockMappingTests
{
    private const ushort Active = 0x08;
    private const ushort Backwards = 0x10;
    private const ushort ManagerControlled = 0x20;
    private const uint FltMaxBits = 0x7F7FFFFF;
    private const uint NegativeFltMaxBits = 0xFF7FFFFF;

    private static readonly float[] Times = [0f, 0.1f, 0.37f, 0.8f, 1.3f, 1.7f, 2.9f, 3.33f, 4.4f, 5.55f, 6.6f, 9.1f];

    [Fact]
    public void Loop_KeepsThePhase()
    {
        var header = Controller(Active, 1f, 0.25f, 0f, 1f);

        var clock = Clock(NifModelClockMapping.MapController(header, false, true));

        Assert.Equal((1f, 0.25f, 0f, 1f, SceneAnimationCycle.Loop),
            (clock.Frequency, clock.PhaseSeconds, clock.StartSeconds, clock.StopSeconds, clock.Cycle));
        AssertMatchesEngine(clock, header);
        Assert.Equal(0.75f, clock.Map(0.5f));

        // Control: the phase dropped gives 0.5 at t = 0.5, not the engine's 0.75.
        var phaseDropped = new SceneAnimationClock(1f, 0f, 0f, 1f, SceneAnimationCycle.Loop);
        Assert.NotEqual(Engine(header, 0.5f), phaseDropped.Map(0.5f));
    }

    /// <summary>
    ///     The engine's REVERSE takes fmod(s, 2L) from 0, not from lo, so Shared's Reverse needs phase + lo. With lo = 1,
    ///     hi = 3 and phase 0.5, t = 1 maps to 2.5; anchored at lo it would map to 1.5.
    /// </summary>
    [Fact]
    public void Reverse_UsesPhasePlusLo()
    {
        var header = Controller(Active | (1 << 1), 1f, 0.5f, 1f, 3f);

        var clock = Clock(NifModelClockMapping.MapController(header, false, true));

        Assert.Equal((SceneAnimationCycle.Reverse, 1.5f), (clock.Cycle, clock.PhaseSeconds));
        AssertMatchesEngine(clock, header);
        Assert.Equal(2.5f, clock.Map(1f));

        var anchoredAtLo = new SceneAnimationClock(1f, 0.5f, 1f, 3f, SceneAnimationCycle.Reverse);
        Assert.Equal(1.5f, anchoredAtLo.Map(1f));
    }

    /// <summary>
    ///     Play backwards: Loop and Clamp use -frequency and lo + hi - phase, Reverse uses phase + hi. The control ignores
    ///     bit 4 and misses the engine.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void PlayBackwards_FollowsTheEngine(int cycle)
    {
        var header = Controller((ushort)(Active | Backwards | (cycle << 1)), 1f, 0.3f, 0.5f, 2.5f);

        var clock = Clock(NifModelClockMapping.MapController(header, false, true));

        AssertMatchesEngine(clock, header);
        var forwards = Clock(NifModelClockMapping.MapController(
            header with { Flags = (ushort)(header.Flags & ~Backwards) }, false, true));
        Assert.Contains(Times, t => MathF.Abs(forwards.Map(t) - Engine(header, t)) > 0.1f);
    }

    [Fact]
    public void DoubleSentinel_HasNoClock_AndBlocksACurve()
    {
        var header = Controller(Active, 1f, 0f, BitConverter.UInt32BitsToSingle(FltMaxBits),
            BitConverter.UInt32BitsToSingle(NegativeFltMaxBits));

        var curveless = NifModelClockMapping.MapController(header, false, false);
        var withCurve = NifModelClockMapping.MapController(header, false, true);

        Assert.Equal(NifModelClockResult.NoClock, curveless);
        Assert.Equal(NifModelCurveBlock.SentinelClockWithCurve, withCurve.Block);

        // Control: the sentinel range is reversed, so it cannot be a Shared clock at all.
        Assert.Throws<ArgumentOutOfRangeException>(() => new SceneAnimationClock(
            header.Frequency, header.Phase, header.StartTime, header.StopTime, SceneAnimationCycle.Clamp));
    }

    [Theory]
    [InlineData(FltMaxBits, 0x40000000u, 2)]
    [InlineData(0x3F800000u, NegativeFltMaxBits, 2)]
    [InlineData(0x40000000u, 0x3F800000u, 2)]
    [InlineData(0x00000000u, 0x3F800000u, 3)]
    public void OneSidedSentinel_ReversedRange_AndCycle3_AreDegenerate(uint startBits, uint stopBits, int cycle)
    {
        var header = Controller((ushort)(Active | (cycle << 1)), 1f, 0f, BitConverter.UInt32BitsToSingle(startBits),
            BitConverter.UInt32BitsToSingle(stopBits));

        Assert.Equal(NifModelCurveBlock.DegenerateClock, NifModelClockMapping.MapController(header, false, true).Block);

        // Control: an ordinary range with a defined cycle maps.
        var ordinary = header with { StartTime = 0f, StopTime = 1f, Flags = Active | (2 << 1) };
        Assert.False(NifModelClockMapping.MapController(ordinary, false, true).IsBlocked);
    }

    [Fact]
    public void InactiveFreeRunningController_IsBlocked()
    {
        var inactive = Controller(0, 1f, 0f, 0f, 1f);

        Assert.Equal(NifModelCurveBlock.InactiveController, NifModelClockMapping.MapController(inactive, false, true).Block);

        // Control: the active bit set gives a clock.
        Assert.NotNull(NifModelClockMapping.MapController(inactive with { Flags = Active }, false, true).Clock);
    }

    /// <summary>
    ///     A sequence-driven controller (flags 0x20, or referenced by a controlled block) gets no track clock and is not
    ///     gated on its own active bit, even when its own clock is inactive and phased; the free-running rule on the same
    ///     header blocks it.
    /// </summary>
    [Fact]
    public void SequenceDrivenControllers_GetNoTrackClock()
    {
        var managed = Controller(ManagerControlled, 2f, 0.7f, 0f, 1f);
        var referenced = Controller(0, 2f, 0.7f, 0f, 1f);

        Assert.Equal(NifModelClockResult.NoClock, NifModelClockMapping.MapController(managed, false, true));
        Assert.Equal(NifModelClockResult.NoClock, NifModelClockMapping.MapController(referenced, true, true));
        Assert.True(NifModelClockMapping.IsSequenceDriven(managed, false));

        Assert.Equal(NifModelCurveBlock.InactiveController, NifModelClockMapping.MapFreeRunning(managed, true).Block);
        Assert.Equal(NifModelCurveBlock.InactiveController, NifModelClockMapping.MapController(referenced, false, true).Block);
    }

    /// <summary>
    ///     A sequence clock has no phase and no REVERSE branch: (frequency, 0, start, stop, Loop for cycle 0, else Clamp).
    ///     A REVERSE sequence over [0, 2] holds 2 at t = 3; kept as Reverse it would return to 1.
    /// </summary>
    [Fact]
    public void SequenceClock_HasNoPhase_AndPlaysReverseAsClamp()
    {
        var reverse = Clock(NifModelClockMapping.MapSequence(Sequence(1, 0f, 2f), true));
        var loop = Clock(NifModelClockMapping.MapSequence(Sequence(0, 0f, 2f), true));
        var clamp = Clock(NifModelClockMapping.MapSequence(Sequence(2, 0f, 2f), true));

        Assert.Equal((1f, 0f, 0f, 2f, SceneAnimationCycle.Clamp),
            (reverse.Frequency, reverse.PhaseSeconds, reverse.StartSeconds, reverse.StopSeconds, reverse.Cycle));
        Assert.Equal(SceneAnimationCycle.Loop, loop.Cycle);
        Assert.Equal(SceneAnimationCycle.Clamp, clamp.Cycle);
        Assert.Equal(2f, reverse.Map(3f));
        Assert.Equal(0.5f, loop.Map(2.5f));

        var keptReverse = new SceneAnimationClock(1f, 0f, 0f, 2f, SceneAnimationCycle.Reverse);
        Assert.Equal(1f, keptReverse.Map(3f));
    }

    [Fact]
    public void SequenceClock_IsBlockedWhenItsDriversAreInactive()
    {
        Assert.Equal(NifModelCurveBlock.InactiveManager,
            NifModelClockMapping.MapSequence(Sequence(0, 0f, 2f), false).Block);

        // Control: active drivers give the clock.
        Assert.NotNull(NifModelClockMapping.MapSequence(Sequence(0, 0f, 2f), true).Clock);
    }

    /// <summary>
    ///     The engine's controller clock, restated in Float32 from RE-22's answer for an APP_TIME controller whose clock
    ///     starts at tau = 0 (a sentinel-free header with cycle 0, 1 or 2).
    /// </summary>
    private static float Engine(NifTimeControllerHeader header, float tau)
    {
        var lo = header.StartTime;
        var hi = header.StopTime;
        var length = hi - lo;
        var s = header.Frequency * tau + header.Phase;
        switch (header.RawCycle)
        {
            case 0:
                if (length == 0f)
                {
                    s = lo;
                }
                else
                {
                    s = (s - lo) % length + lo;
                    if (s < lo)
                    {
                        s += length;
                    }
                }

                break;
            case 1:
                if (length == 0f)
                {
                    s = lo;
                }
                else
                {
                    var twice = length * 2f;
                    var r = s % twice;
                    if (r < 0f)
                    {
                        r += twice;
                    }

                    s = r <= length ? lo + r : lo + (twice - r);
                }

                break;
        }

        s = Math.Clamp(s, lo, hi);
        return header.PlayBackwards ? hi - (s - lo) : s;
    }

    /// <summary>Asserts Shared's mapping of the mapped clock equals the engine at every sample time within 2e-6 s.</summary>
    private static void AssertMatchesEngine(SceneAnimationClock clock, NifTimeControllerHeader header)
    {
        foreach (var tau in Times)
        {
            Assert.Equal(Engine(header, tau), clock.Map(tau), 2e-6f);
        }
    }

    /// <summary>A controller header with the given flags and clock.</summary>
    private static NifTimeControllerHeader Controller(int flags, float frequency, float phase, float start, float stop)
    {
        return new NifTimeControllerHeader(-1, (ushort)flags, frequency, phase, start, stop, 0);
    }

    /// <summary>A sequence view with frequency 1 and the given cycle word and range.</summary>
    private static NifControllerSequenceView Sequence(uint cycle, float start, float stop)
    {
        return new NifControllerSequenceView(0, 0, [], Bits(1f), -1, cycle, Bits(1f), Bits(start), Bits(stop), -1, -1,
            null, null, true);
    }

    /// <summary>The clock of a result that must have one.</summary>
    private static SceneAnimationClock Clock(NifModelClockResult result)
    {
        Assert.Equal(NifModelCurveBlock.None, result.Block);
        return Assert.IsType<SceneAnimationClock>(result.Clock);
    }
}
