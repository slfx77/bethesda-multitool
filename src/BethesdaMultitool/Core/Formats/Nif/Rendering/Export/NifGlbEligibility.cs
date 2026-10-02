namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Export;

/// <summary>Whether an input can take the normalized GLB route, and the first stage that declined it if not.</summary>
internal sealed record NifGlbEligibility
{
    /// <summary>Creates an eligibility result, rejecting stage and reason combinations that cannot occur.</summary>
    /// <param name="stage">
    ///     <see cref="NifGlbDeclineStage.None" /> for an eligible input, else the declining stage. Never
    ///     <see cref="NifGlbDeclineStage.Requested" />, which only a decision can carry.
    /// </param>
    /// <param name="reason">Null for an eligible input; otherwise the non-blank decline reason.</param>
    /// <exception cref="ArgumentOutOfRangeException">The stage is undefined or is <see cref="NifGlbDeclineStage.Requested" />.</exception>
    /// <exception cref="ArgumentException">The reason is present for an eligible input or blank for a declined one.</exception>
    public NifGlbEligibility(NifGlbDeclineStage stage, string? reason)
    {
        if (!Enum.IsDefined(stage) || stage == NifGlbDeclineStage.Requested)
        {
            throw new ArgumentOutOfRangeException(nameof(stage), stage,
                "An eligibility result carries None or a declining stage.");
        }

        if (stage == NifGlbDeclineStage.None && reason is not null)
        {
            throw new ArgumentException("An eligible input carries no decline reason.", nameof(reason));
        }

        if (stage != NifGlbDeclineStage.None && string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A declined input requires a reason.", nameof(reason));
        }

        Stage = stage;
        Reason = reason;
    }

    /// <summary>The shared eligible result.</summary>
    public static NifGlbEligibility Eligible { get; } = new(NifGlbDeclineStage.None, null);

    /// <summary><see cref="NifGlbDeclineStage.None" /> when eligible, else the first stage that declined.</summary>
    public NifGlbDeclineStage Stage { get; }

    /// <summary>The decline reason, or null when eligible.</summary>
    public string? Reason { get; }

    /// <summary>Whether the normalized route can be taken.</summary>
    public bool IsEligible => Stage == NifGlbDeclineStage.None;

    /// <summary>Creates a declined result for <paramref name="stage" />.</summary>
    /// <param name="stage">The declining stage.</param>
    /// <param name="reason">The non-blank reason.</param>
    /// <returns>The declined result.</returns>
    public static NifGlbEligibility Declined(NifGlbDeclineStage stage, string reason)
    {
        if (stage == NifGlbDeclineStage.None)
        {
            throw new ArgumentOutOfRangeException(nameof(stage), stage, "A declined result needs a declining stage.");
        }

        return new NifGlbEligibility(stage, reason);
    }
}
