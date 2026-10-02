using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     The resolved console platform of one read: which platform applies, whether it was assumed (the option was not
///     set) or declared by the shell, and the evidence text native state quotes.
/// </summary>
/// <param name="Platform">The platform whose color byte order applies.</param>
/// <param name="IsAssumed">True when the option was absent and the default platform stands in.</param>
/// <param name="Source">Where the selection came from, for native state (<c>bmt.platform=ps3</c> or the assumption).</param>
internal sealed record NifPackedPlatformSelection(NifPackedPlatform Platform, bool IsAssumed, string Source)
{
    /// <summary>The option value spelled for the selected platform (<c>x360</c> or <c>ps3</c>).</summary>
    public string OptionValue => NifPackedPlatformOption.OptionValue(Platform);

    /// <summary>The memory order of a packed vertex color's four bytes on this platform.</summary>
    public string ColorByteOrder => Platform == NifPackedPlatform.Ps3
        ? NifPackedPlatformOption.Ps3ColorByteOrder
        : NifPackedPlatformOption.X360ColorByteOrder;

    /// <summary>
    ///     The provenance of the color byte order: measured (ReverseEngineered) when the shell declared the platform,
    ///     Assumed when the default stands in, because the file itself carries no discriminator.
    /// </summary>
    public SceneValueProvenance ColorByteOrderProvenance => IsAssumed
        ? SceneValueProvenance.Assumed
        : SceneValueProvenance.ReverseEngineered;
}
