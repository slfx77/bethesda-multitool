namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     Resolves the console platform from the read's app options (<see cref="BethesdaModelRegistration.PlatformOption" />,
///     set by the shell from <c>--platform</c>). Accepted values are <c>x360</c> and <c>ps3</c>, matched without regard
///     to case; any other value is an error rather than a silent fallback. Without the option the platform is not
///     established and X360 stands in as Assumed, which the packed decoder reports with a diagnostic on every packed
///     color stream it decodes (the byte order is the one thing the two consoles disagree on; measured 2026-09-24,
///     TestOutput/packed-semantics-20260924).
/// </summary>
internal static class NifPackedPlatformOption
{
    /// <summary>The option value naming the Xbox 360.</summary>
    public const string X360Value = "x360";

    /// <summary>The option value naming the PlayStation 3.</summary>
    public const string Ps3Value = "ps3";

    /// <summary>The X360 memory order of a packed D3DCOLOR vertex color.</summary>
    public const string X360ColorByteOrder = "A,R,G,B";

    /// <summary>The PS3 memory order of a packed D3DCOLOR vertex color.</summary>
    public const string Ps3ColorByteOrder = "A,G,B,R";

    /// <summary>The evidence stated when no platform is established.</summary>
    public const string AssumedEvidence =
        "bmt.platform not set: X360 assumed; a big-endian FNV NIF carries no byte that says which console wrote it, " +
        "and PS3 differs only in the packed vertex-color byte order (A,G,B,R instead of A,R,G,B)";

    /// <summary>Resolves the platform from the read's app options.</summary>
    /// <exception cref="ArgumentException">The option names no console platform.</exception>
    public static NifPackedPlatformSelection Resolve(IReadOnlyDictionary<string, string> appOptions)
    {
        ArgumentNullException.ThrowIfNull(appOptions);
        if (!appOptions.TryGetValue(BethesdaModelRegistration.PlatformOption, out var value) ||
            string.IsNullOrWhiteSpace(value))
        {
            return new NifPackedPlatformSelection(NifPackedPlatform.X360, true, AssumedEvidence);
        }

        var platform = Parse(value);
        return new NifPackedPlatformSelection(platform, false,
            $"{BethesdaModelRegistration.PlatformOption}={OptionValue(platform)}");
    }

    /// <summary>Parses an option value (see the type remarks).</summary>
    /// <exception cref="ArgumentException">The value names no console platform.</exception>
    public static NifPackedPlatform Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var trimmed = value.Trim();
        if (string.Equals(trimmed, X360Value, StringComparison.OrdinalIgnoreCase))
        {
            return NifPackedPlatform.X360;
        }

        if (string.Equals(trimmed, Ps3Value, StringComparison.OrdinalIgnoreCase))
        {
            return NifPackedPlatform.Ps3;
        }

        throw new ArgumentException(
            $"The {BethesdaModelRegistration.PlatformOption} option '{value}' names no console platform (use " +
            $"{X360Value} or {Ps3Value}).", nameof(value));
    }

    /// <summary>The option value for a platform.</summary>
    public static string OptionValue(NifPackedPlatform platform)
    {
        return platform switch
        {
            NifPackedPlatform.X360 => X360Value,
            NifPackedPlatform.Ps3 => Ps3Value,
            _ => throw new ArgumentOutOfRangeException(nameof(platform), platform, "Unknown console platform.")
        };
    }
}
