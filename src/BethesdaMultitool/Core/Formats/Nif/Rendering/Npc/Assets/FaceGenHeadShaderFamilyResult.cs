namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Assets;

/// <summary>
///     The resolved FaceGen head texture set: diffuse, optional normal/subsurface map paths, and subsurface tint
///     color.
/// </summary>
internal readonly record struct FaceGenHeadShaderFamilyResult(
    string DiffuseTexturePath,
    string? NormalMapTexturePath,
    string? SubsurfaceTexturePath,
    (float R, float G, float B) SubsurfaceColor);
