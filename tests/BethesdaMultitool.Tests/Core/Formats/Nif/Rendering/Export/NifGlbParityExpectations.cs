namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Export;

/// <summary>What the source proves about a native/normalized GLB pair, supplied to <see cref="NifGlbParityOracle" />.</summary>
/// <remarks>
///     Every member defaults to the strictest reading: no triangle is removed before pairing and every tangent is
///     compared. Source knowledge only ever relaxes a rule where it proves a difference is a known native behavior.
/// </remarks>
internal sealed record NifGlbParityExpectations
{
    /// <summary>Compares every encoded triangle and every tangent as written.</summary>
    internal static NifGlbParityExpectations Strict { get; } = new();

    /// <summary>
    ///     The exact number of source triangles with two exactly equal positions, which the native toolkit rejects.
    ///     When set, the normalized GLB must hold exactly this many such triangles and they are removed before
    ///     pairing. Null disables the accounting, so every normalized triangle must pair.
    /// </summary>
    internal int? RepeatedPositionTriangles { get; init; }

    /// <summary>
    ///     Reports, for a normalized logical mesh and primitive index, whether its source part authored tangents.
    ///     A native fallback tangent is ignored only on a primitive whose material has no normal texture and whose
    ///     part authored none. Null means authorship is unknown, so tangents are compared on every primitive.
    /// </summary>
    internal Func<int, int, bool>? SharedPrimitiveHasAuthoredTangents { get; init; }

    /// <summary>The native writer's normalized-color rule, measured from SharpGLTF.Core 1.0.6.</summary>
    internal NifGlbColorQuantization NativeColorQuantization { get; init; } = NifGlbColorQuantization.Truncate;

    /// <summary>The normalized writer's rule, used only if it is asked for a normalized integer color.</summary>
    internal NifGlbColorQuantization SharedColorQuantization { get; init; } = NifGlbColorQuantization.RoundToNearest;
}
