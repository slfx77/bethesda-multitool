using BethesdaMultitool.Core.Formats.Nif.Rendering.Particles;

namespace BethesdaMultitool.Tests.Helpers;

/// <summary>How the sweep domain for an emitter's authored activity was derived.</summary>
internal enum ParticleSweepMode
{
    /// <summary>No authored keys at all — the rate is time-invariant, one sample is conclusive.</summary>
    Constant,
    SequenceLoop,
    SequenceReverse,
    ControllerLoop,
    ControllerReverse,

    /// <summary>A finite Clamp window: not periodic, but bounded and fully sweepable.</summary>
    Clamped,

    /// <summary>
    ///     Neither clock supplies a usable window (<c>Stop &lt;= Start</c> or non-finite), so
    ///     <c>ParticleControllerTiming.Map</c> passes raw time through. The domain is taken from the
    ///     authored key span instead.
    /// </summary>
    Identity
}

internal readonly record struct ParticleSweepPlan(
    ParticleSweepMode Mode,
    float DomainStart,
    float DomainEnd,
    float Step,
    int SampleCount);

internal readonly record struct ParticleActivityProfile(
    ParticleSweepPlan Plan,
    float BakeWindowSeconds,
    float MaxRate,
    float? FirstActiveTime,
    float DutyFraction,
    float BestSnapshot);

/// <summary>
///     Measures when a particle emitter is actually authored to emit, using the SAME clock the
///     renderer uses.
///     <para>
///         Why this exists: the 2026-08-10 FO3 census judged emitters by <c>Sample(0f)</c> and
///         <c>Sample(2.5f)</c> — two POSITIVE instants. <see cref="NifParticleBaker" /> integrates
///         <c>birthRate·dt</c> over midpoint samples of a window that runs BACKWARDS from the
///         snapshot (<c>simulationStart = snapshot − totalSteps·dt</c>), and the static viewer always
///         bakes at snapshot 0 — so every time the renderer evaluates is negative and neither
///         censused instant is ever sampled. Under <c>Cycle.Loop</c> those negatives wrap into the
///         loop's trailing segment, whose position depends on the emitter's lifespan; that is why
///         <c>fxbubblestall01</c> and its near-twin <c>fxbubblesshort01</c> — which author the same
///         pattern at different loop lengths — received opposite verdicts.
///     </para>
///     <para>
///         Everything here mirrors the baker's arithmetic rather than re-deriving it, so the two
///         cannot drift; <c>ParticleActivityWindowTests</c> pins that correspondence.
///     </para>
/// </summary>
internal static class ParticleActivityWindow
{
    private const int MaxSweepSamples = 4096;

    /// <summary>Mirrors <c>NifParticleBaker</c>: dt floor 1/240, lifespan floor dt, settle margin.</summary>
    internal static (float Dt, int TotalSteps) ResolveBakeGrid(
        ParticleSystemDefinition def, ParticleBakeOptions? options = null)
    {
        var opt = options ?? ParticleBakeOptions.Default;
        var dt = MathF.Max(opt.TimeStep, 1f / 240f);
        var lifeSpan = def.Emitter?.LifeSpan ?? 0f;
        var avgLifespan = MathF.Max(lifeSpan, dt);
        var totalSteps = (int)MathF.Ceiling((avgLifespan + opt.SettleMarginSeconds) / dt);
        return (dt, Math.Max(1, totalSteps));
    }

    /// <summary>The exact midpoint sample times the baker evaluates for a given snapshot.</summary>
    internal static IEnumerable<float> WindowSampleTimes(float snapshot, float dt, int totalSteps)
    {
        var simulationStart = snapshot - totalSteps * dt;
        for (var step = 0; step < totalSteps; step++)
        {
            yield return simulationStart + (step + 0.5f) * dt;
        }
    }

    /// <summary>Integrated birth mass the baker would see at <paramref name="snapshot" />.</summary>
    internal static float WindowIntegral(
        ParticleRateControllerDefinition rate, float snapshot, float dt, int totalSteps)
    {
        var total = 0f;
        foreach (var t in WindowSampleTimes(snapshot, dt, totalSteps))
        {
            total += rate.Sample(t) * dt;
        }

        return total;
    }

    /// <summary>
    ///     Period of one clock in its own INPUT units, or null when it is not periodic (Clamp, or the
    ///     Identity sentinel). Reverse ping-pongs, so its period is twice the window.
    /// </summary>
    private static float? PeriodOf(ParticleControllerTiming timing)
    {
        if (!float.IsFinite(timing.StartTime) || !float.IsFinite(timing.StopTime) ||
            timing.StopTime <= timing.StartTime)
        {
            return null;
        }

        if (timing.Cycle == ParticleControllerCycle.Clamp) return null;

        var length = timing.StopTime - timing.StartTime;
        var local = timing.Cycle == ParticleControllerCycle.Reverse ? length * 2f : length;
        var frequency = float.IsFinite(timing.Frequency) && !timing.Frequency.Equals(0f)
            ? MathF.Abs(timing.Frequency)
            : 1f;
        return local / frequency;
    }

    /// <summary>
    ///     Chooses the domain to sweep. A periodic OUTER (sequence) clock dominates, because a
    ///     composed map is periodic with the outer clock's period.
    /// </summary>
    internal static ParticleSweepPlan ResolveSweep(
        ParticleRateControllerDefinition rate, float bakeWindowSeconds)
    {
        var hasKeys = rate.Keys.Count > 0 || rate.EmitterActiveKeys.Count > 0;
        float period;
        ParticleSweepMode mode;

        if (rate.SequenceTiming is { } sequence && PeriodOf(sequence) is { } sequencePeriod)
        {
            period = sequencePeriod;
            mode = sequence.Cycle == ParticleControllerCycle.Reverse
                ? ParticleSweepMode.SequenceReverse
                : ParticleSweepMode.SequenceLoop;
        }
        else if (PeriodOf(rate.ControllerTiming) is { } controllerPeriod)
        {
            // The sequence clock (when present) scales the controller's input, so divide by its rate.
            var sequenceFrequency = rate.SequenceTiming is { } seq && float.IsFinite(seq.Frequency) &&
                                    !seq.Frequency.Equals(0f)
                ? MathF.Abs(seq.Frequency)
                : 1f;
            period = controllerPeriod / sequenceFrequency;
            mode = rate.ControllerTiming.Cycle == ParticleControllerCycle.Reverse
                ? ParticleSweepMode.ControllerReverse
                : ParticleSweepMode.ControllerLoop;
        }
        else if (!hasKeys)
        {
            // Pure constant: one sample settles it, but keep a token window so callers can sweep.
            return new ParticleSweepPlan(
                ParticleSweepMode.Constant, -bakeWindowSeconds, 0f, MathF.Max(bakeWindowSeconds, 1e-3f), 2);
        }
        else
        {
            // Identity: the raw (negative) time reaches the track, so sweep the authored key span.
            var maxKeyTime = 0f;
            foreach (var key in rate.Keys) maxKeyTime = MathF.Max(maxKeyTime, key.Time);
            foreach (var key in rate.EmitterActiveKeys) maxKeyTime = MathF.Max(maxKeyTime, key.Time);
            period = MathF.Max(maxKeyTime, bakeWindowSeconds);
            mode = ParticleSweepMode.Identity;
        }

        if (!float.IsFinite(period) || period <= 0f) period = MathF.Max(bakeWindowSeconds, 1f);

        // Sweep both signs: the bake window runs backwards, and Loop wraps negatives.
        var start = -(period + bakeWindowSeconds);
        var end = period + bakeWindowSeconds;
        var span = end - start;
        var step = MathF.Max(span / MaxSweepSamples, 1f / 120f);
        var count = Math.Max(2, (int)MathF.Ceiling(span / step) + 1);
        return new ParticleSweepPlan(mode, start, end, step, count);
    }

    /// <summary>
    ///     Profiles one system: peak rate, first authored active time, duty fraction, and the
    ///     snapshot at which the baker would see the most birth mass.
    /// </summary>
    internal static ParticleActivityProfile Profile(
        ParticleSystemDefinition def, ParticleBakeOptions? options = null)
    {
        var (dt, totalSteps) = ResolveBakeGrid(def, options);
        var bakeWindow = totalSteps * dt;
        if (def.Emitter?.BirthRateController is not { } rate)
        {
            return new ParticleActivityProfile(
                new ParticleSweepPlan(ParticleSweepMode.Constant, 0f, 0f, 1f, 0),
                bakeWindow, 0f, null, 0f, 0f);
        }

        var plan = ResolveSweep(rate, bakeWindow);
        var maxRate = 0f;
        float? firstActive = null;
        var activeSamples = 0;
        var samples = 0;
        var bestSnapshot = 0f;
        var bestIntegral = -1f;

        for (var i = 0; i < plan.SampleCount; i++)
        {
            var t = plan.DomainStart + i * plan.Step;
            if (t > plan.DomainEnd) break;
            var value = rate.Sample(t);
            samples++;
            if (value > 0f)
            {
                activeSamples++;
                maxRate = MathF.Max(maxRate, value);
                firstActive ??= t;
            }
        }

        // Coarser grid for the snapshot search: each candidate costs a whole window integral.
        var snapshotStride = MathF.Max(plan.Step, (plan.DomainEnd - plan.DomainStart) / 256f);
        for (var t = plan.DomainStart; t <= plan.DomainEnd; t += snapshotStride)
        {
            var integral = WindowIntegral(rate, t, dt, totalSteps);
            if (integral <= bestIntegral) continue;
            bestIntegral = integral;
            bestSnapshot = t;
        }

        return new ParticleActivityProfile(
            plan,
            bakeWindow,
            maxRate,
            firstActive,
            samples > 0 ? (float)activeSamples / samples : 0f,
            bestSnapshot);
    }
}