using BethesdaMultitool.Tests.Helpers;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Export;

/// <summary>One row of the production parity corpus (plan section 3.3).</summary>
/// <param name="Id">The stable identifier used by the theory row, the receipt name and the ratchet.</param>
/// <param name="Description">What the stratum covers.</param>
/// <param name="Route">How its scenes are assembled.</param>
/// <param name="SampleSize">N, the candidates taken in SHA-256 order unless the full-stratum mode is on.</param>
/// <param name="RootDescription">How the root is resolved, for skip messages and the receipt.</param>
/// <param name="ResolveRoot">Resolves the corpus root through the repository helpers, or null when absent.</param>
internal sealed record NifGlbParityStratum(
    string Id,
    string Description,
    NifGlbParityRoute Route,
    int SampleSize,
    string RootDescription,
    Func<string?> ResolveRoot)
{
    /// <summary>Resolves a supporting root (the texture data for SpeedTree), or null when the route needs none.</summary>
    internal Func<string?>? ResolveSupportRoot { get; init; }

    /// <summary>How the supporting root is resolved.</summary>
    internal string? SupportDescription { get; init; }

    /// <summary>Whether at least one inspected file must have been converted from big-endian.</summary>
    internal bool RequiresBigEndianConversion { get; init; }

    /// <summary>Whether every inspected file must have resolved production texture sources.</summary>
    internal bool RequiresTextureSources { get; init; }

    /// <summary>Relative paths that must be compared (not declined) in this stratum.</summary>
    internal IReadOnlyList<string> RequiredComparedPaths { get; init; } = [];

    /// <summary>The skip message naming the missing root; an absent modern family is a named, unadmitted gap.</summary>
    internal string MissingRootMessage =>
        Route == NifGlbParityRoute.GuiAssembly && Id.StartsWith("S7", StringComparison.Ordinal)
            ? $"Named gap: {Description} is not installed ({RootDescription}); its family stays unadmitted."
            : $"{Description} root not found ({RootDescription}). Set {RealAssetPaths.RootVariable} to a " +
              "directory containing it.";
}
