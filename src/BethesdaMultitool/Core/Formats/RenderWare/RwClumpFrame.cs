using System.Numerics;

namespace BethesdaMultitool.Core.Formats.RenderWare;

/// <summary>An authored local transform and parent reference, with the associated plugin bodies.</summary>
internal sealed record RwClumpFrame(
    Matrix4x4 LocalTransform, int ParentIndex, uint Flags, IReadOnlyList<RwClumpChunk> Extensions);
