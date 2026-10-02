namespace BethesdaMultitool.Core.Formats.RenderWare;

/// <summary>Generic geometry, its material slots, and the independently validated draw index splits.</summary>
internal sealed record RwClumpGeometry(
    RwGeometry Geometry, IReadOnlyList<RwClumpMaterial> Materials, IReadOnlyList<int> MaterialMap,
    RwBinMesh BinMesh, IReadOnlyList<RwClumpChunk> Extensions);
