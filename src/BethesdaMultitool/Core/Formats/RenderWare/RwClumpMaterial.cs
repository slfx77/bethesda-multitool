namespace BethesdaMultitool.Core.Formats.RenderWare;

/// <summary>Raw material fields; in particular the serialized unused word is not assumed to be zero.</summary>
internal sealed record RwClumpMaterial(
    uint Flags, uint ColorRgba, uint Unused, uint Textured,
    float Ambient, float Specular, float Diffuse, RwClumpTexture? Texture,
    IReadOnlyList<RwClumpChunk> Extensions);
