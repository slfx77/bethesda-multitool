namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Particles;

/// <summary>NiTimeController / NiControllerSequence cycle behavior.</summary>
internal enum ParticleControllerCycle : byte
{
    Loop = 0,
    Reverse = 1,
    Clamp = 2,
}

/// <summary>Authored NiFloatData interpolation basis.</summary>
internal enum ParticleRateInterpolation : uint
{
    Linear = 1,
    Quadratic = 2,
    Tbc = 3,
    Constant = 5,
}

/// <summary>
///     One controller clock. <c>Map</c> applies the recovered Gamebryo time transform
///     <c>u = t*Frequency + Phase</c>, followed by the authored loop/reverse/clamp policy.
/// </summary>
internal readonly record struct ParticleControllerTiming(
    float Frequency,
    float Phase,
    float StartTime,
    float StopTime,
    ParticleControllerCycle Cycle)
{
    internal static ParticleControllerTiming Identity { get; } =
        new(1f, 0f, float.PositiveInfinity, float.NegativeInfinity, ParticleControllerCycle.Clamp);

    internal float Map(float timeSeconds)
    {
        var frequency = float.IsFinite(Frequency) ? Frequency : 1f;
        var phase = float.IsFinite(Phase) ? Phase : 0f;
        var localTime = timeSeconds * frequency + phase;

        // Invalid/sentinel ranges mean that this clock supplies only frequency/phase. This is also the
        // identity timing used by synthetic definitions and direct pose-value interpolators.
        if (!float.IsFinite(StartTime) || !float.IsFinite(StopTime) || StopTime <= StartTime)
        {
            return float.IsFinite(localTime) ? localTime : 0f;
        }

        if (!float.IsFinite(localTime))
        {
            return localTime > 0f ? StopTime : StartTime;
        }

        var length = StopTime - StartTime;
        return Cycle switch
        {
            ParticleControllerCycle.Loop => StartTime + PositiveModulo(localTime - StartTime, length),
            ParticleControllerCycle.Reverse => MapReverse(localTime, length),
            _ => Math.Clamp(localTime, StartTime, StopTime),
        };
    }

    private float MapReverse(float localTime, float length)
    {
        var offset = PositiveModulo(localTime - StartTime, length * 2f);
        return offset <= length
            ? StartTime + offset
            : StopTime - (offset - length);
    }

    private static float PositiveModulo(float value, float modulus)
    {
        var result = value % modulus;
        return result < 0f ? result + modulus : result;
    }
}

/// <summary>
///     One lossless NiFloatData rate key. Quadratic forward/backward tangents and TBC coefficients are
///     retained even while those two uncommon bases use the explicitly-labelled linear fallback below.
/// </summary>
internal readonly record struct ParticleRateKey(
    float Time,
    float Value,
    float Forward = 0f,
    float Backward = 0f,
    float Tension = 0f,
    float Bias = 0f,
    float Continuity = 0f);

/// <summary>
///     Live, deterministic birth-rate contract for the supported legacy NiPSys emitter family. A managed
///     particle controller has two clocks: NiControllerSequence maps renderer time first, then the target
///     NiTimeController applies its per-emitter frequency/phase and cycle window before the float curve is
///     sampled. This preserves the authored phase offsets used to desynchronise emitters in one effect.
/// </summary>
internal sealed class ParticleRateControllerDefinition
{
    public bool IsActive { get; init; } = true;

    /// <summary>Optional outer NiControllerSequence clock for manager-controlled interpolators.</summary>
    public ParticleControllerTiming? SequenceTiming { get; init; }

    /// <summary>Inner NiPSysEmitterCtlr clock, including its authored per-emitter phase.</summary>
    public ParticleControllerTiming ControllerTiming { get; init; } = ParticleControllerTiming.Identity;

    public ParticleRateInterpolation Interpolation { get; init; } = ParticleRateInterpolation.Linear;
    public IReadOnlyList<ParticleRateKey> Keys { get; init; } = [];

    /// <summary>NiFloatInterpolator pose value when no NiFloatData curve is attached.</summary>
    public float? ConstantValue { get; init; }

    /// <summary>
    ///     True when the authored coefficients are retained but the evaluator uses linear interpolation.
    ///     NiBezFloatKey/NiTCBFloatKey binary-oracle recovery can replace this fallback without a parser or
    ///     renderer contract change.
    /// </summary>
    public bool UsesInterpolationFallback =>
        Interpolation is ParticleRateInterpolation.Quadratic or ParticleRateInterpolation.Tbc;

    internal float Sample(float rendererTimeSeconds)
    {
        if (!IsActive)
        {
            return 0f;
        }

        var localTime = float.IsFinite(rendererTimeSeconds) ? rendererTimeSeconds : 0f;
        if (SequenceTiming is { } sequenceTiming)
        {
            localTime = sequenceTiming.Map(localTime);
        }

        localTime = ControllerTiming.Map(localTime);
        var value = Keys.Count > 0
            ? SampleKeys(localTime)
            : ConstantValue.GetValueOrDefault();
        return float.IsFinite(value) ? MathF.Max(0f, value) : 0f;
    }

    private float SampleKeys(float time)
    {
        if (Keys.Count == 1 || time <= Keys[0].Time)
        {
            return Keys[0].Value;
        }

        for (var i = 1; i < Keys.Count; i++)
        {
            var next = Keys[i];
            if (time >= next.Time)
            {
                continue;
            }

            var previous = Keys[i - 1];
            if (Interpolation == ParticleRateInterpolation.Constant)
            {
                return previous.Value;
            }

            var span = next.Time - previous.Time;
            if (span <= 1e-6f)
            {
                return next.Value;
            }

            // LINEAR is exact. QUADRATIC/TBC intentionally use this deterministic, bounded stand-in while
            // preserving their authored auxiliary fields above; this is safer than the old peak-rate hold.
            var fraction = Math.Clamp((time - previous.Time) / span, 0f, 1f);
            return float.Lerp(previous.Value, next.Value, fraction);
        }

        return Keys[^1].Value;
    }
}
