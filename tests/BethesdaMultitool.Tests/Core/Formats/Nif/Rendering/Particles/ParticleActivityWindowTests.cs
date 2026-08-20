using BethesdaMultitool.Core.Formats.Nif.Rendering.Particles;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Particles;

/// <summary>
///     Pins <see cref="ParticleActivityWindow" /> against <see cref="NifParticleBaker" />.
///     <para>
///         The 2026-08-10 FO3 emitter census judged 202 emitters by <c>Sample(0f)</c> and
///         <c>Sample(2.5f)</c> and got 36 of its 74 "gated-to-zero" verdicts wrong, because the baker
///         never evaluates either instant: it integrates over a window running BACKWARDS from the
///         snapshot, and the static viewer pins the snapshot at 0. These tests exist so the
///         replacement instrument cannot drift away from the baker the same way — every claim it
///         makes about "would this emit?" is checked against an actual bake.
///     </para>
/// </summary>
public sealed class ParticleActivityWindowTests
{
    private static readonly float[] SiblingLifeSpans = [0.25f, 0.5f, 1f, 2f, 3f, 4f, 6f, 8f];

    /// <summary>The shape the census cares about: a gated emitter with a constant authored rate.</summary>
    private static ParticleSystemDefinition MakeSystem(
        ParticleRateControllerDefinition rate, float lifeSpan = 2f)
    {
        return new ParticleSystemDefinition
        {
            Emitter = new ParticleEmitterDefinition
            {
                LifeSpan = lifeSpan,
                BirthRate = 50f,
                BirthRateController = rate
            }
        };
    }

    private static ParticleRateControllerDefinition ConstantRate(
        float value,
        ParticleControllerTiming? controller = null,
        bool? gate = null,
        params ParticleBoolKey[] gateKeys)
    {
        return new ParticleRateControllerDefinition
        {
            ConstantValue = value,
            ControllerTiming = controller ?? ParticleControllerTiming.Identity,
            EmitterActiveConstant = gate,
            EmitterActiveKeys = gateKeys
        };
    }

    [Fact]
    public void ResolveBakeGrid_MirrorsBakerFloorsAndSettleMargin()
    {
        // Lifespan below the dt floor still gets at least one step, and the settle margin is included.
        var (dt, steps) = ParticleActivityWindow.ResolveBakeGrid(MakeSystem(ConstantRate(10f), 0f));

        Assert.Equal(MathF.Max(ParticleBakeOptions.Default.TimeStep, 1f / 240f), dt, 6);
        Assert.True(steps >= 1);
        Assert.Equal(
            (int)MathF.Ceiling((dt + ParticleBakeOptions.Default.SettleMarginSeconds) / dt), steps);
    }

    [Fact]
    public void WindowSampleTimes_RunBackwardsFromSnapshot_AndAreAllNegativeAtSnapshotZero()
    {
        // This is the whole reason the original census was wrong: at the shipped snapshot of 0,
        // EVERY time the renderer evaluates is negative, so no positive probe can ever be conclusive.
        var system = MakeSystem(ConstantRate(10f));
        var (dt, steps) = ParticleActivityWindow.ResolveBakeGrid(system);

        var times = ParticleActivityWindow.WindowSampleTimes(0f, dt, steps).ToArray();

        Assert.Equal(steps, times.Length);
        Assert.All(times, t => Assert.True(t < 0f, $"expected a negative sample time, got {t}"));
        Assert.True(times[0] < times[^1], "window must advance forward in time toward the snapshot");
        Assert.Equal(-(steps * dt) + 0.5f * dt, times[0], 5);
    }

    [Fact]
    public void WindowIntegral_AgreesWithBaker_OnWhetherAnythingIsBorn()
    {
        // The anti-drift pin the plan calls for: Bake(...).Count > 0 <=> WindowIntegral > 0.
        // Cases span gated-off, gated-on, keyed, and phase-offset emitters.
        var loop = new ParticleControllerTiming(1f, 0f, 0f, 4f, ParticleControllerCycle.Loop);
        ParticleRateControllerDefinition[] cases =
        [
            ConstantRate(0f),
            ConstantRate(25f),
            ConstantRate(25f, gate: false),
            ConstantRate(25f, gate: true),
            ConstantRate(
                25f, loop, null,
                new ParticleBoolKey(0f, false), new ParticleBoolKey(2f, true)),
            new()
            {
                ControllerTiming = loop,
                Keys = [new ParticleRateKey(0f, 0f), new ParticleRateKey(2f, 80f)]
            }
        ];

        foreach (var rate in cases)
        {
            var system = MakeSystem(rate);
            var (dt, steps) = ParticleActivityWindow.ResolveBakeGrid(system);

            var integral = ParticleActivityWindow.WindowIntegral(rate, 0f, dt, steps);
            var baked = NifParticleBaker.Bake(system).Count;

            Assert.True(
                integral > 0f == baked > 0,
                $"instrument and baker disagree: integral={integral}, baked={baked}");
        }
    }

    [Fact]
    public void Profile_BestSnapshot_ActuallyMaximisesWhatTheBakerProduces()
    {
        // A phase-offset gate that is shut at the shipped snapshot but open later in the loop:
        // "pulses-invisible" in census terms. bestSnapshot must be a snapshot that really bakes.
        var loop = new ParticleControllerTiming(1f, 0f, 0f, 10f, ParticleControllerCycle.Loop);
        var rate = ConstantRate(
            60f, loop, null,
            new ParticleBoolKey(0f, false),
            new ParticleBoolKey(6f, true),
            new ParticleBoolKey(8f, false));
        var system = MakeSystem(rate);

        var profile = ParticleActivityWindow.Profile(system);
        var atShipped = NifParticleBaker.Bake(system).Count;
        var atBest = NifParticleBaker.Bake(
            system, new ParticleBakeOptions { SnapshotTimeSeconds = profile.BestSnapshot }).Count;

        Assert.True(profile.DutyFraction is > 0f and < 1f, $"duty {profile.DutyFraction} should be partial");
        Assert.True(atBest > 0, "bestSnapshot must be a snapshot at which the baker actually emits");
        Assert.True(
            atBest >= atShipped,
            $"bestSnapshot ({atBest}) must not bake less than the shipped snapshot ({atShipped}) — the census "
            + "reports 'pulses-invisible' on exactly this comparison");
    }

    [Fact]
    public void Profile_IdentityTiming_PinsKeyZeroForever_RegressionGuard()
    {
        // Identity timing (Stop <= Start) passes raw time through, so the backwards window only ever
        // sees negative times and the step track returns key 0 no matter what follows it. 13 FO3 and
        // 38 FNV emitters sit on this path ("identity-pinned-key0"). Documented, not endorsed: if the
        // default snapshot ever moves off 0, this test fails and the census must be re-run.
        Assert.Equal(0f, ParticleBakeOptions.Default.SnapshotTimeSeconds);

        var rate = ConstantRate(
            60f, null, null,
            new ParticleBoolKey(0f, false), new ParticleBoolKey(1f, true));
        var system = MakeSystem(rate);

        Assert.Empty(NifParticleBaker.Bake(system));
        Assert.True(
            NifParticleBaker.Bake(
                system, new ParticleBakeOptions { SnapshotTimeSeconds = 5f }).Count > 0,
            "a later snapshot reaches the authored 'true' key");
    }

    [Fact]
    public void Profile_SiblingsDifferingOnlyInLifespan_CanDisagreeAtTheShippedSnapshot()
    {
        // fxbubblestall01 vs fxbubblesshort01: identical authored gate, different emitter lifespans,
        // opposite verdicts in the old census. The lifespan sets the window length, which sets where
        // the backwards window lands inside the loop — so this is a snapshot artifact, not an asset
        // difference, and any verdict that ignores lifespan is measuring the wrong thing.
        var loop = new ParticleControllerTiming(1f, 0f, 0f, 8f, ParticleControllerCycle.Loop);
        // The ON segment sits mid-loop, so the backwards window from snapshot 0 wraps into the loop's
        // trailing OFF segment: a short-lived emitter's window never reaches back far enough to see
        // the ON keys, while a long-lived one's does. Nothing but LifeSpan differs between the two.
        var rate = ConstantRate(
            60f, loop, null,
            new ParticleBoolKey(0f, false),
            new ParticleBoolKey(2f, true),
            new ParticleBoolKey(4f, false));

        var byLifespan = SiblingLifeSpans
            .Select(life => (life, emits: NifParticleBaker.Bake(MakeSystem(rate, life)).Count > 0))
            .ToArray();

        Assert.Contains(byLifespan, v => v.emits);
        Assert.Contains(byLifespan, v => !v.emits);
    }
}