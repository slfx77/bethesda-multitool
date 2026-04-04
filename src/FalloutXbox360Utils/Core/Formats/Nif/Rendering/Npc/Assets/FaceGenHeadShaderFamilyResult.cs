namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Assets;

internal readonly record struct FaceGenHeadShaderFamilyResult(
    string DiffuseTexturePath,
    string? NormalMapTexturePath,
    string? SubsurfaceTexturePath,
    (float R, float G, float B) SubsurfaceColor);
