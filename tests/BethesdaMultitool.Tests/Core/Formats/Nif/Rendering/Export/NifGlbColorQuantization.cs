namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Export;

/// <summary>How an encoder turned a floating-point color component into a normalized integer.</summary>
/// <remarks>
///     The parity oracle turns every decoded normalized integer back into the exact interval of floating-point
///     inputs its encoder maps to that integer. Both rules were read from the encoders themselves rather than
///     assumed; the native rule is also pinned by <c>NifGlbParityOracleTests</c> against the real toolkit.
/// </remarks>
internal enum NifGlbColorQuantization
{
    /// <summary>
    ///     Discards the fraction. SharpGLTF.Core 1.0.6 writes the native writer's normalized unsigned bytes through
    ///     <c>FloatingArrays._SetNormalizedU8</c>, which casts <c>value * 255f</c> to a byte, so a decoded byte
    ///     <c>n</c> stands for any input in <c>[n / 255, (n + 1) / 255)</c>.
    /// </summary>
    Truncate = 0,

    /// <summary>
    ///     Rounds to the nearest integer, as the shared <c>SceneGltfBuilder.WriteColor</c> does with
    ///     <c>MathF.Round</c>, so a decoded byte <c>n</c> stands for any input in <c>[(n - 0.5) / 255, (n + 0.5) / 255]</c>.
    /// </summary>
    RoundToNearest = 1
}
