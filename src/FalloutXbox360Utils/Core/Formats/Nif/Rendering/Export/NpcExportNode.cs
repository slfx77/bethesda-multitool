using System.Numerics;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Export;

internal sealed class NpcExportNode
{
    public required string Name { get; init; }

    public string? LookupName { get; init; }

    public int? ParentIndex { get; init; }

    public required Matrix4x4 LocalTransform { get; init; }

    public required Matrix4x4 WorldTransform { get; init; }

    public required NpcExportNodeKind Kind { get; init; }
}