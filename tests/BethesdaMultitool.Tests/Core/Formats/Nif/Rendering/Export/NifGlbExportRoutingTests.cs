using BethesdaMultitool.Core.Formats.Nif.Rendering.Export;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Export;

/// <summary>Pins the pure NIF GLB routing rule: the full decision table.</summary>
public sealed class NifGlbExportRoutingTests
{
    private const string Probe = "probe reason";

    /// <summary>
    ///     Every preference against every eligibility stage. Expected values are literals, not recomputed: Native
    ///     ignores eligibility, Auto falls back to Native carrying the stage, Normalized refuses.
    /// </summary>
    [Theory]
    [InlineData(NifGlbWriterPreference.Native, NifGlbDeclineStage.None,
        NifGlbExportRoute.Native, NifGlbDeclineStage.Requested, NifGlbExportRouting.NativeRequestedReason, false)]
    [InlineData(NifGlbWriterPreference.Native, NifGlbDeclineStage.FamilyNotAdmitted,
        NifGlbExportRoute.Native, NifGlbDeclineStage.Requested, NifGlbExportRouting.NativeRequestedReason, false)]
    [InlineData(NifGlbWriterPreference.Native, NifGlbDeclineStage.Scope,
        NifGlbExportRoute.Native, NifGlbDeclineStage.Requested, NifGlbExportRouting.NativeRequestedReason, false)]
    [InlineData(NifGlbWriterPreference.Native, NifGlbDeclineStage.Adapter,
        NifGlbExportRoute.Native, NifGlbDeclineStage.Requested, NifGlbExportRouting.NativeRequestedReason, false)]
    [InlineData(NifGlbWriterPreference.Native, NifGlbDeclineStage.SharedBuild,
        NifGlbExportRoute.Native, NifGlbDeclineStage.Requested, NifGlbExportRouting.NativeRequestedReason, false)]
    [InlineData(NifGlbWriterPreference.Native, NifGlbDeclineStage.SharedValidation,
        NifGlbExportRoute.Native, NifGlbDeclineStage.Requested, NifGlbExportRouting.NativeRequestedReason, false)]
    [InlineData(NifGlbWriterPreference.Auto, NifGlbDeclineStage.None,
        NifGlbExportRoute.Normalized, NifGlbDeclineStage.None, null, false)]
    [InlineData(NifGlbWriterPreference.Auto, NifGlbDeclineStage.FamilyNotAdmitted,
        NifGlbExportRoute.Native, NifGlbDeclineStage.FamilyNotAdmitted, Probe, false)]
    [InlineData(NifGlbWriterPreference.Auto, NifGlbDeclineStage.Scope,
        NifGlbExportRoute.Native, NifGlbDeclineStage.Scope, Probe, false)]
    [InlineData(NifGlbWriterPreference.Auto, NifGlbDeclineStage.Adapter,
        NifGlbExportRoute.Native, NifGlbDeclineStage.Adapter, Probe, false)]
    [InlineData(NifGlbWriterPreference.Auto, NifGlbDeclineStage.SharedBuild,
        NifGlbExportRoute.Native, NifGlbDeclineStage.SharedBuild, Probe, false)]
    [InlineData(NifGlbWriterPreference.Auto, NifGlbDeclineStage.SharedValidation,
        NifGlbExportRoute.Native, NifGlbDeclineStage.SharedValidation, Probe, false)]
    [InlineData(NifGlbWriterPreference.Normalized, NifGlbDeclineStage.None,
        NifGlbExportRoute.Normalized, NifGlbDeclineStage.None, null, false)]
    [InlineData(NifGlbWriterPreference.Normalized, NifGlbDeclineStage.FamilyNotAdmitted,
        NifGlbExportRoute.Normalized, NifGlbDeclineStage.FamilyNotAdmitted, Probe, true)]
    [InlineData(NifGlbWriterPreference.Normalized, NifGlbDeclineStage.Scope,
        NifGlbExportRoute.Normalized, NifGlbDeclineStage.Scope, Probe, true)]
    [InlineData(NifGlbWriterPreference.Normalized, NifGlbDeclineStage.Adapter,
        NifGlbExportRoute.Normalized, NifGlbDeclineStage.Adapter, Probe, true)]
    [InlineData(NifGlbWriterPreference.Normalized, NifGlbDeclineStage.SharedBuild,
        NifGlbExportRoute.Normalized, NifGlbDeclineStage.SharedBuild, Probe, true)]
    [InlineData(NifGlbWriterPreference.Normalized, NifGlbDeclineStage.SharedValidation,
        NifGlbExportRoute.Normalized, NifGlbDeclineStage.SharedValidation, Probe, true)]
    internal void Decide_FollowsTheFullTruthTable(
        NifGlbWriterPreference preference,
        NifGlbDeclineStage eligibilityStage,
        NifGlbExportRoute expectedRoute,
        NifGlbDeclineStage expectedStage,
        string? expectedReason,
        bool expectedRefused)
    {
        var eligibility = eligibilityStage == NifGlbDeclineStage.None
            ? NifGlbEligibility.Eligible
            : NifGlbEligibility.Declined(eligibilityStage, Probe);

        var decision = NifGlbExportRouting.Decide(preference, eligibility);

        Assert.Equal(expectedRoute, decision.Route);
        Assert.Equal(preference, decision.Preference);
        Assert.Equal(expectedStage, decision.Stage);
        Assert.Equal(expectedReason, decision.Reason);
        Assert.Equal(expectedRefused, decision.Refused);
        Assert.Equal(expectedRoute == NifGlbExportRoute.Normalized && !expectedRefused, decision.IsNormalized);
    }

    /// <summary>An undefined preference or a missing eligibility is an argument error, not a routing answer.</summary>
    [Fact]
    public void Decide_RejectsAnUndefinedPreferenceAndANullEligibility()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            NifGlbExportRouting.Decide((NifGlbWriterPreference)99, NifGlbEligibility.Eligible));
        Assert.Throws<ArgumentNullException>(() =>
            NifGlbExportRouting.Decide(NifGlbWriterPreference.Auto, null!));
    }

    /// <summary>Eligibility carries None or a declining stage with a reason, never Requested and never a reasonless decline.</summary>
    [Fact]
    public void Eligibility_RejectsCombinationsThatCannotOccur()
    {
        // Requested belongs to a decision, never to an input's eligibility.
        Assert.Throws<ArgumentOutOfRangeException>(() => new NifGlbEligibility(NifGlbDeclineStage.Requested, Probe));
        Assert.Throws<ArgumentOutOfRangeException>(() => new NifGlbEligibility((NifGlbDeclineStage)42, Probe));
        Assert.Throws<ArgumentException>(() => new NifGlbEligibility(NifGlbDeclineStage.None, Probe));
        Assert.Throws<ArgumentException>(() => new NifGlbEligibility(NifGlbDeclineStage.Adapter, " "));
        Assert.Throws<ArgumentException>(() => new NifGlbEligibility(NifGlbDeclineStage.Adapter, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => NifGlbEligibility.Declined(NifGlbDeclineStage.None, Probe));

        Assert.True(NifGlbEligibility.Eligible.IsEligible);
        Assert.Null(NifGlbEligibility.Eligible.Reason);
        var declined = NifGlbEligibility.Declined(NifGlbDeclineStage.Scope, Probe);
        Assert.False(declined.IsEligible);
        Assert.Equal(NifGlbDeclineStage.Scope, declined.Stage);
        Assert.Equal(Probe, declined.Reason);
    }

    /// <summary>A refused decision never counts as the normalized route, even though its route names it.</summary>
    [Fact]
    public void Decision_IsNormalizedOnlyWhenNotRefused()
    {
        var refused = new NifGlbExportDecision(NifGlbExportRoute.Normalized, NifGlbWriterPreference.Normalized,
            NifGlbDeclineStage.Adapter, Probe, true);
        var taken = new NifGlbExportDecision(NifGlbExportRoute.Normalized, NifGlbWriterPreference.Auto,
            NifGlbDeclineStage.None, null, false);

        Assert.False(refused.IsNormalized);
        Assert.True(taken.IsNormalized);
    }
}
