namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Export;

/// <summary>
///     The pure routing rule for NIF GLB export: the writer decision for a preference and an eligibility
///     result.
/// </summary>
/// <remarks>
///     A call site's preference comes from <see cref="NifGlbExportDefaults" /> alone. There is deliberately no
///     command-line option or environment override: moving a call site to the normalized writer is a code
///     change that the owner then tests (standing owner ruling against flags that gate a feature decision).
/// </remarks>
internal static class NifGlbExportRouting
{
    /// <summary>The reason recorded when the native writer was asked for.</summary>
    public const string NativeRequestedReason = "The native GLB writer was requested.";

    /// <summary>Chooses the writer for a preference and an eligibility result.</summary>
    /// <param name="preference">What the caller asked for.</param>
    /// <param name="eligibility">
    ///     Whether the input can take the normalized route. Ignored for <see cref="NifGlbWriterPreference.Native" />,
    ///     whose callers never compute it.
    /// </param>
    /// <returns>
    ///     Native: the native route at stage <see cref="NifGlbDeclineStage.Requested" />. Auto: the normalized route
    ///     when eligible, else the native route carrying the declining stage and reason. Normalized: the normalized
    ///     route when eligible, else a refused decision carrying the declining stage and reason.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">The preference is not defined.</exception>
    /// <exception cref="ArgumentNullException">The eligibility is null.</exception>
    public static NifGlbExportDecision Decide(NifGlbWriterPreference preference, NifGlbEligibility eligibility)
    {
        ArgumentNullException.ThrowIfNull(eligibility);
        if (!Enum.IsDefined(preference))
        {
            throw new ArgumentOutOfRangeException(nameof(preference), preference, "Unknown GLB writer preference.");
        }

        if (preference == NifGlbWriterPreference.Native)
        {
            return new NifGlbExportDecision(NifGlbExportRoute.Native, preference, NifGlbDeclineStage.Requested,
                NativeRequestedReason, false);
        }

        if (eligibility.IsEligible)
        {
            return new NifGlbExportDecision(NifGlbExportRoute.Normalized, preference, NifGlbDeclineStage.None,
                null, false);
        }

        return preference == NifGlbWriterPreference.Auto
            ? new NifGlbExportDecision(NifGlbExportRoute.Native, preference, eligibility.Stage,
                eligibility.Reason, false)
            : new NifGlbExportDecision(NifGlbExportRoute.Normalized, preference, eligibility.Stage,
                eligibility.Reason, true);
    }
}
