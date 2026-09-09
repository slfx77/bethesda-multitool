using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Water;

internal enum OblivionWaterSimulationMode
{
    Rain,
    Wading
}

internal enum OblivionWaterSimulationAddress
{
    None,
    Wrap,
    Clamp
}

internal enum OblivionWaterSimulationOutput
{
    OrdinaryNormal,
    WadingNormal
}

internal enum OblivionWaterSimulationStage
{
    WadingStamp,
    RainStamp,
    WadingEvolution,
    RainEvolution,
    Normal,
    MixedHeight,
    Recenter,
    FftNormal,
    FftAbsoluteHeight
}

/// <summary>Opaque replay resource identity, not a GPU descriptor or an allocation policy.</summary>
internal readonly record struct OblivionWaterSimulationResource(int Value);

internal readonly record struct OblivionWaterRecordedRainSample(int RandomX, int RandomY);

internal readonly record struct OblivionWaterRecordedWadingStamp(Vector4 Row0, Vector4 Row1);

/// <summary>
///     One cached selector20 effect and its two explicitly identified callers. Caller heights and
///     scratch fields exchange resource references. FinalInput/ExternalHeight are aliases only.
///     Residency flags describe source-owned acquire/release requirements, not GPU allocation.
/// </summary>
internal sealed record OblivionWaterDisplacementState(
    long LastInvocation,
    float EvolutionSeconds,
    float EventSeconds,
    OblivionWaterSimulationResource RainHeight,
    OblivionWaterSimulationResource WadingHeight,
    OblivionWaterSimulationResource ScratchA,
    OblivionWaterSimulationResource ScratchB,
    OblivionWaterSimulationResource MixedHeight,
    OblivionWaterSimulationResource? FinalInput = null,
    OblivionWaterSimulationResource? ExternalHeight = null,
    bool MixedHeightResident = false,
    OblivionWaterSimulationResource? FftIntermediate = null)
{
    internal static OblivionWaterDisplacementState Create(
        OblivionWaterSimulationResource rain, OblivionWaterSimulationResource wading,
        OblivionWaterSimulationResource scratchA, OblivionWaterSimulationResource scratchB,
        OblivionWaterSimulationResource mixed)
    {
        return new OblivionWaterDisplacementState(0, 0.05f, 0.1f, rain, wading, scratchA, scratchB, mixed);
    }
}

/// <summary>
///     Explicit ordered replay input. No camera, weather, random seed or elapsed-clock producer is
///     inferred. Wading rows are the recorded phase0 translation matrix, not synthesized actors.
/// </summary>
internal sealed record OblivionWaterDisplacementInvocation(
    long Order,
    OblivionWaterSimulationMode Mode,
    float DeltaSeconds,
    float RainBlendAmount,
    int RainRate,
    ImmutableArray<OblivionWaterRecordedRainSample> RainSamples,
    OblivionWaterRecordedWadingStamp? WadingStamp,
    Vector2 RecenterOffset,
    OblivionWaterSimulationResource? RawFftHeight,
    OblivionWaterSimulationResource? FftIntermediate,
    OblivionWaterSimulationResource? NormalOutput);

internal sealed record OblivionWaterSimulationPass(
    OblivionWaterSimulationStage Stage,
    OblivionWaterSimulationResource? Input0,
    OblivionWaterSimulationResource? Input1,
    OblivionWaterSimulationResource Output,
    OblivionWaterSimulationAddress Address,
    Vector3 Controls = default,
    float Dampener = 0f,
    float BlendAmount = 0f,
    Vector2 Offset = default,
    float StampScale = 0f,
    OblivionWaterRecordedWadingStamp? WadingStamp = null)
{
    internal bool UsesHeightMapProgram => Stage is OblivionWaterSimulationStage.FftNormal or
        OblivionWaterSimulationStage.FftAbsoluteHeight;

    internal int ProgramIndex => Stage switch
    {
        OblivionWaterSimulationStage.WadingStamp => 0,
        OblivionWaterSimulationStage.RainStamp => 1,
        OblivionWaterSimulationStage.WadingEvolution => 2,
        OblivionWaterSimulationStage.RainEvolution => 3,
        OblivionWaterSimulationStage.Normal or OblivionWaterSimulationStage.FftNormal => 5,
        OblivionWaterSimulationStage.MixedHeight or OblivionWaterSimulationStage.FftAbsoluteHeight => 6,
        OblivionWaterSimulationStage.Recenter => 7,
        _ => throw new InvalidOperationException("Unrecognized simulation stage.")
    };
}

internal sealed record OblivionWaterDisplacementPlan(
    OblivionWaterDisplacementState Next,
    ImmutableArray<OblivionWaterSimulationPass> Passes,
    OblivionWaterSimulationOutput OutputRoute,
    OblivionWaterSimulationResource? PublishedNormal,
    bool InvokedDisplacement,
    bool AcquireMixedHeight,
    bool ReleaseMixedHeight,
    bool AcquireFftIntermediate,
    bool ReleaseFftIntermediate);

/// <summary>
///     CPU invocation plan for 0049D7B0/0049B930 -> cached selector20 -> 007DE8A0. This is a pure
///     plan: callers commit Next only after successful execution. A supplied draw budget rejects a
///     whole plan; it never silently drops evolution steps or advances partially executed history.
/// </summary>
internal static class OblivionWaterDisplacementSchedule
{
    internal const float RainInterval = 0.025f; // 0x3CCCCCCD
    internal const float WadingInterval = 0.033f; // 0x3D072B02

    internal static OblivionWaterDisplacementPlan Plan(
        OblivionWaterDisplacementState state,
        OblivionWaterDisplacementInvocation invocation,
        OblivionWaterSimulationInputs inputs,
        int maximumDraws)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(inputs);
        Validate(state, invocation, maximumDraws);
        var passes = ImmutableArray.CreateBuilder<OblivionWaterSimulationPass>();
        var next = state with { LastInvocation = invocation.Order };
        var rain = invocation.Mode == OblivionWaterSimulationMode.Rain;
        var amount = invocation.RainBlendAmount;
        var outputRoute =
            rain ? OblivionWaterSimulationOutput.OrdinaryNormal : OblivionWaterSimulationOutput.WadingNormal;
        var acquireFft = false;
        var releaseFft = false;

        [SuppressMessage("Major Code Smell", "S3928",
            Justification =
                "invocation is the captured parameter of the enclosing Plan API and supplies the aliased resource identities reported to its caller.")]
        void Add(OblivionWaterSimulationPass pass)
        {
            if (passes.Count >= maximumDraws)
            {
                throw new InvalidOperationException("The complete invocation exceeds the supplied draw budget.");
            }

            if (pass.Output == pass.Input0 || pass.Output == pass.Input1)
            {
                throw new ArgumentException("A sampled resource cannot also be this pass's output.",
                    nameof(invocation));
            }

            passes.Add(pass);
        }

        // 0049DAB6/0049DACF: B=0 runs HMAP005; interior B runs HMAP006. B=1 skips HMAP
        // and releases owner+0C. A zero-blend rain owner does not call 007DE8A0 at all.
        if (rain && amount < 1f)
        {
            var rawHeight = Require(invocation.RawFftHeight, "The FFT branch requires a recorded raw-height resource.");
            if (amount.Equals(0f)) // Exact caller branch, not a measured-near-zero comparison.
            {
                if (!invocation.RainSamples.IsEmpty)
                {
                    throw new ArgumentException("B=0 consumes no recorded rain samples.", nameof(invocation));
                }

                var normal = Require(invocation.NormalOutput, "HMAP005 requires its caller's normal output.");
                Add(new OblivionWaterSimulationPass(OblivionWaterSimulationStage.FftNormal, rawHeight, null, normal,
                    OblivionWaterSimulationAddress.Wrap, Dampener: 0.8f));
                return new OblivionWaterDisplacementPlan(next, passes.ToImmutable(), outputRoute, normal, false, false,
                    false, false, false);
            }

            var fft = Require(invocation.FftIntermediate, "Interior B requires the absolute FFT-height intermediate.");
            if (next.FftIntermediate is { } retained && retained != fft)
            {
                throw new ArgumentException("A retained FFT intermediate cannot silently change identity.",
                    nameof(invocation));
            }

            acquireFft = next.FftIntermediate is null;
            next = next with { FftIntermediate = fft };
            Add(new OblivionWaterSimulationPass(OblivionWaterSimulationStage.FftAbsoluteHeight, rawHeight, null, fft,
                OblivionWaterSimulationAddress.Wrap));
        }
        else if (rain && amount.Equals(1f))
        {
            releaseFft = next.FftIntermediate is not null;
            next = next with { FftIntermediate = null };
        }

        // 007DEB1F..007DEB39: BOTH modes add dt to BOTH shared accumulators, storing floats.
        var evolution = state.EvolutionSeconds + invocation.DeltaSeconds;
        var events = state.EventSeconds + invocation.DeltaSeconds;
        if (!float.IsFinite(evolution) || !float.IsFinite(events))
        {
            throw new ArgumentOutOfRangeException(nameof(invocation),
                "Recorded elapsed time overflowed the effect state.");
        }

        var current = rain ? next.RainHeight : next.WadingHeight;
        var scratch = rain ? next.ScratchB : next.ScratchA;
        var coefficients = rain ? inputs.Rain : inputs.Wading;
        var controls = new Vector3(coefficients.Force, coefficients.Velocity, coefficients.Falloff);
        var address = rain ? OblivionWaterSimulationAddress.Wrap : OblivionWaterSimulationAddress.Clamp;

        if (rain)
        {
            var count = OblivionWaterSimulationMath.RainEventCount(invocation.RainRate, events);
            if (invocation.RainSamples.Length != count)
            {
                throw new ArgumentException("Recorded rand pairs must exactly match the source-derived event count.",
                    nameof(invocation));
            }

            events = 0f; // 007DF073 resets even when count is zero; no fractional event debt.
            foreach (var sample in invocation.RainSamples)
            {
                var offset = new Vector2(OblivionWaterSimulationMath.RainClipOffset(sample.RandomX),
                    OblivionWaterSimulationMath.RainClipOffset(sample.RandomY));
                Add(new OblivionWaterSimulationPass(OblivionWaterSimulationStage.RainStamp, null, null, current,
                    OblivionWaterSimulationAddress.None, Offset: offset, StampScale: coefficients.StartingSize));
            }
        }
        else
        {
            Add(new OblivionWaterSimulationPass(OblivionWaterSimulationStage.Recenter, current, null, scratch,
                address, Offset: invocation.RecenterOffset));
            if (invocation.WadingStamp is { } stamp)
            {
                Add(new OblivionWaterSimulationPass(OblivionWaterSimulationStage.WadingStamp, null, null, scratch,
                    OblivionWaterSimulationAddress.None, WadingStamp: stamp));
            }
        }

        var interval = rain ? RainInterval : WadingInterval;
        var firstStep = true;
        while (evolution > interval)
        {
            // 007DF22F: only the FIRST wading step skips the swap. Rain swaps every step.
            if (rain || !firstStep)
            {
                (scratch, current) = (current, scratch);
            }

            firstStep = false;
            var remainder = evolution - interval;
            if (remainder.Equals(evolution))
            {
                throw new ArgumentOutOfRangeException(nameof(invocation),
                    "Recorded float time cannot make evolution progress.");
            }

            evolution = remainder;
            Add(new OblivionWaterSimulationPass(
                rain ? OblivionWaterSimulationStage.RainEvolution : OblivionWaterSimulationStage.WadingEvolution,
                scratch, null, current, address, controls));
        }

        next = rain
            ? next with { RainHeight = current, ScratchB = scratch }
            : next with { WadingHeight = current, ScratchA = scratch };
        next = next with { EvolutionSeconds = evolution, EventSeconds = events, FinalInput = current };
        var acquireMixed = false;
        var releaseMixed = false;
        if (rain && amount > 0f && amount < 1f)
        {
            var fft = Require(invocation.FftIntermediate, "Interior B requires the FFT intermediate.");
            acquireMixed = !next.MixedHeightResident;
            Add(new OblivionWaterSimulationPass(OblivionWaterSimulationStage.MixedHeight, fft, next.ScratchB,
                next.MixedHeight,
                OblivionWaterSimulationAddress.Wrap, Dampener: coefficients.Dampener, BlendAmount: amount));
            // 0055E2A0 at007DF40A ASSIGNS FC=10C. It leaves the mixed allocation/reference intact.
            next = next with { FinalInput = next.MixedHeight, ExternalHeight = fft, MixedHeightResident = true };
        }
        else if (amount.Equals(1f))
        {
            // 007DF411..007DF458 releases +10C at exact B=1, including a wading invocation.
            releaseMixed = next.MixedHeightResident;
            next = next with { MixedHeightResident = false };
        }

        if (invocation.NormalOutput is { } output)
        {
            Add(new OblivionWaterSimulationPass(OblivionWaterSimulationStage.Normal, next.FinalInput, null, output,
                address,
                Dampener: coefficients.Dampener));
        }

        return new OblivionWaterDisplacementPlan(next, passes.ToImmutable(), outputRoute, invocation.NormalOutput, true,
            acquireMixed, releaseMixed, acquireFft, releaseFft);
    }

    private static OblivionWaterSimulationResource Require(OblivionWaterSimulationResource? resource, string message)
    {
        return resource ?? throw new ArgumentException(message);
    }

    private static void Validate(OblivionWaterDisplacementState state,
        OblivionWaterDisplacementInvocation invocation, int maximumDraws)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumDraws);
        if (state.LastInvocation < 0 || state.LastInvocation == long.MaxValue ||
            invocation.Order != state.LastInvocation + 1 ||
            invocation.Mode is not (OblivionWaterSimulationMode.Rain or OblivionWaterSimulationMode.Wading) ||
            !FiniteNonnegative(state.EvolutionSeconds) || !FiniteNonnegative(state.EventSeconds) ||
            !FiniteNonnegative(invocation.DeltaSeconds) || !FiniteNonnegative(invocation.RainBlendAmount) ||
            invocation.RainBlendAmount > 1f || invocation.RainRate < 0 || invocation.RainSamples.IsDefault ||
            !float.IsFinite(invocation.RecenterOffset.X) || !float.IsFinite(invocation.RecenterOffset.Y))
        {
            throw new ArgumentException("Invocation/order/time/state is outside the finite recorded cohort.",
                nameof(invocation));
        }

        if ((invocation.Mode == OblivionWaterSimulationMode.Wading && !invocation.RainSamples.IsEmpty) ||
            (invocation.Mode == OblivionWaterSimulationMode.Rain && invocation.WadingStamp is not null))
        {
            throw new ArgumentException("Recorded stamps belong to a different invocation mode.", nameof(invocation));
        }

        if (invocation.WadingStamp is { } stamp && (!Finite(stamp.Row0) || !Finite(stamp.Row1)))
        {
            throw new ArgumentException("Recorded wading matrix must be finite.", nameof(invocation));
        }

        ImmutableArray<OblivionWaterSimulationResource> histories =
            [state.RainHeight, state.WadingHeight, state.ScratchA, state.ScratchB, state.MixedHeight];
        if (histories.Any(resource => resource.Value <= 0) || histories.Distinct().Count() != histories.Length)
        {
            throw new ArgumentException("Caller and scratch allocations must have distinct positive identities.",
                nameof(state));
        }

        if ((state.FinalInput is { } final && (final.Value <= 0 || !histories.Contains(final))) ||
            state.ExternalHeight is { Value: <= 0 } || state.FftIntermediate is { Value: <= 0 })
        {
            throw new ArgumentException("A retained resource identity is invalid.", nameof(state));
        }

        // FinalInput is assigned only from caller T or mixed +10C. ExternalHeight may legitimately
        // outlive the caller's FFT intermediate: the exact B=1 release does not clear effect+108.
        // Do not demand that those two optional fields have identical residency/lifetimes.
        ImmutableArray<OblivionWaterSimulationResource?> external =
            [invocation.RawFftHeight, invocation.FftIntermediate, invocation.NormalOutput];
        var supplied = external.OfType<OblivionWaterSimulationResource>().ToImmutableArray();
        if (supplied.Any(resource => resource.Value <= 0 || histories.Contains(resource)) ||
            supplied.Distinct().Count() != supplied.Length)
        {
            throw new ArgumentException("External textures must not alias each other or simulation histories.",
                nameof(invocation));
        }
    }

    private static bool FiniteNonnegative(float value)
    {
        return float.IsFinite(value) && value >= 0f;
    }

    private static bool Finite(Vector4 value)
    {
        return float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z) && float.IsFinite(value.W);
    }
}
