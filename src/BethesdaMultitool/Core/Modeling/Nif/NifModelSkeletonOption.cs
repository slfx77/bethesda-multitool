namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     Resolves the explicit skeleton of a <c>.kf</c> read from its app options
///     (<see cref="BethesdaModelRegistration.SkeletonOption" />, set by the shell from <c>--skeleton</c>; cut-1b slice 10).
///     The value names a file: it is handed to the skeleton lookup exactly as given and wins over the nearest-ancestor
///     walk-up (<see cref="NifModelSkeletonResolver" />). It never switches behavior: a <c>.nif</c> read ignores it, and a
///     <c>.kf</c> read without it walks up.
/// </summary>
internal static class NifModelSkeletonOption
{
    /// <summary>The explicit skeleton path, or null when the option is absent, empty or whitespace.</summary>
    /// <param name="appOptions">The read's app options.</param>
    /// <returns>The path exactly as given, or null.</returns>
    public static string? Resolve(IReadOnlyDictionary<string, string> appOptions)
    {
        ArgumentNullException.ThrowIfNull(appOptions);
        return appOptions.TryGetValue(BethesdaModelRegistration.SkeletonOption, out var value) &&
               !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
    }
}
