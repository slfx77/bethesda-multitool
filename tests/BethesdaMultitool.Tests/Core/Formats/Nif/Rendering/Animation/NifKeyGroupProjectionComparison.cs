using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Animation;

/// <summary>
///     One renderer key-group read run through both <see cref="NifKeyGroupReaderLegacyReference" /> and the projection in
///     <see cref="NifKeyGroupReader" />: what the legacy reader returned, and both results as bit-exact signatures that
///     must be equal (see <see cref="NifKeyGroupProjectionSignatures" />).
/// </summary>
/// <param name="Read">The legacy reader's return value.</param>
/// <param name="Position">The legacy reader's cursor after the call.</param>
/// <param name="Interpolation">The legacy reader's interpolation out value.</param>
/// <param name="KeyCount">The number of keys the legacy reader returned.</param>
/// <param name="Euler">True when the legacy reader returned Euler axis keys.</param>
/// <param name="Legacy">The legacy result's signature.</param>
/// <param name="View">The projection's signature.</param>
/// <param name="EngineEulerReference">
///     True when the reference is not the legacy reader's own rotation read but the engine's Euler walk composed from the
///     legacy float-group reader (an XYZ-Euler block on which the two walks differ by design; see
///     <see cref="NifKeyGroupProjectionSignatures.Quat" />). The other members then describe that composed reference.
/// </param>
internal readonly record struct NifKeyGroupProjectionComparison(
    bool Read,
    int Position,
    NifKeyInterpolation Interpolation,
    int KeyCount,
    bool Euler,
    string Legacy,
    string View,
    bool EngineEulerReference = false)
{
    /// <summary>True when the projection returned exactly what the legacy reader returned.</summary>
    public bool Matches => string.Equals(Legacy, View, StringComparison.Ordinal);
}
