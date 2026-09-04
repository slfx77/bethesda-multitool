namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Materials;

/// <summary>
///     Defines the default tangent-space normal-map strength shared by the preview renderers.
/// </summary>
internal static class NifNormalMapStrengthPolicy
{
    /// <summary>
    ///     The recovered FNV SLS pixel shader consumes the decoded tangent-space normal without
    ///     attenuation. Full strength also matches the native reference renderer and glTF's neutral
    ///     normal-texture scale. Diagnostic callers may still override the sprite renderer at runtime.
    /// </summary>
    internal const float GenericDefault = 1f;
}
