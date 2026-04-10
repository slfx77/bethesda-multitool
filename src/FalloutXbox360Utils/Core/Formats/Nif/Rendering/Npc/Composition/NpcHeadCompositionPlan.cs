using System.Numerics;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Composition;

internal sealed class NpcHeadCompositionPlan
{
    public string? BaseHeadNifPath { get; init; }

    public string? FaceGenNifPath { get; init; }

    public float[]? HeadPreSkinMorphDeltas { get; init; }

    public string? EffectiveHeadTexturePath { get; init; }

    public bool EffectiveHeadTextureUsesEgtMorph { get; init; }

    public string? HairFilter { get; init; }

    public Dictionary<string, Matrix4x4>? AttachmentBoneTransforms { get; init; }

    public Matrix4x4? BonelessAttachmentTransform { get; init; }

    public IReadOnlyList<string> RaceFacePartPaths { get; init; } = [];

    public string? HairNifPath { get; init; }

    public IReadOnlyList<string> HeadPartNifPaths { get; init; } = [];

    public IReadOnlyList<string> EyeNifPaths { get; init; } = [];

    public IReadOnlyList<EquippedItem> HeadEquipment { get; init; } = [];
}
