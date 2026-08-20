using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.NpcAssembly;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Composition;

/// <summary>
///     The head portion of an NPC composition plan: the base head NIF, its head parts, FaceGen morph data, and tint
///     colors.
/// </summary>
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
