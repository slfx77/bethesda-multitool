using System.Numerics;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Export;

/// <summary>One node in a GLB scene graph: its name, parent, local/world transforms, and role.</summary>
internal sealed class GlbNode
{
    public required string Name { get; init; }

    public string? LookupName { get; init; }

    /// <summary>Originating NIF object block, when this node has a one-to-one source identity.</summary>
    public int? SourceBlockIndex { get; init; }

    public int? ParentIndex { get; init; }

    public required Matrix4x4 LocalTransform { get; init; }

    public required Matrix4x4 WorldTransform { get; init; }

    public required GlbNodeKind Kind { get; init; }
}
