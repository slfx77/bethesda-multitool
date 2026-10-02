namespace BethesdaMultitool.Core.Formats.RenderWare;

/// <summary>An unresolved texture/mask name pair and its unmodified sampler word.</summary>
internal sealed record RwClumpTexture(
    uint Sampler, string Name, string MaskName, IReadOnlyList<RwClumpChunk> Extensions);
