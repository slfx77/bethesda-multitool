namespace BethesdaMultitool.Core.Formats.RenderWare;

/// <summary>A draw occurrence referencing a frame and geometry, without flattening either.</summary>
internal sealed record RwClumpAtomic(
    int FrameIndex, int GeometryIndex, uint Flags, uint Unused, IReadOnlyList<RwClumpChunk> Extensions);
