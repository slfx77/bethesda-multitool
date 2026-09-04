using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.NpcAssembly;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Composition;

/// <summary>Identifies how the effective head diffuse was produced.</summary>
internal enum NpcHeadTextureSource
{
    /// <summary>The unmodified race/base head diffuse; no FaceGen texture delta was applied.</summary>
    BaseDiffuse,

    /// <summary>Oblivion's shipped per-NPC <c>_0.dds</c> FaceGenMap0 delta was composited.</summary>
    AuthoredMap0,

    /// <summary>The delta was regenerated from the base head EGT and FGTS coefficients.</summary>
    GeneratedEgt
}

/// <summary>Identifies the retail FaceGenMap1 contribution baked into the effective head albedo.</summary>
internal enum NpcFaceGenMap1Source
{
    /// <summary>No Map1 term was applied because the game is not Oblivion or the base texture was unavailable.</summary>
    None,

    /// <summary>
    ///     The source-proven <c>BSFaceGenManager::DefaultDetailModFaceGenTexture</c> procedural tile
    ///     was used because stock Oblivion does not ship the optional age/sex detail textures.
    /// </summary>
    DefaultDetailModFaceGenTexture
}

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

    public NpcHeadTextureSource EffectiveHeadTextureSource { get; init; }

    public string? EffectiveEarTexturePath { get; init; }

    public string? HairFilter { get; init; }

    public Dictionary<string, Matrix4x4>? AttachmentBoneTransforms { get; init; }

    public Matrix4x4? BonelessAttachmentTransform { get; init; }

    public IReadOnlyList<string> RaceFacePartPaths { get; init; } = [];

    public string? HairNifPath { get; init; }

    public IReadOnlyList<string> HeadPartNifPaths { get; init; } = [];

    public IReadOnlyList<string> EyeNifPaths { get; init; } = [];

    public IReadOnlyList<EquippedItem> HeadEquipment { get; init; } = [];
}
