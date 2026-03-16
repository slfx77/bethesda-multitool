using System.Numerics;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering;

namespace FalloutXbox360Utils.CLI.Rendering.Npc;

internal sealed class NpcRenderCaches
{
    public Dictionary<string, Matrix4x4>? PoseDeltas;

    public Dictionary<string, Matrix4x4>? SkeletonBones;

    public Dictionary<string, NifRenderableModel?> HeadMeshes { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, EgmParser?> EgmFiles { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, EgtParser?> EgtFiles { get; } =
        new(StringComparer.OrdinalIgnoreCase);
}
