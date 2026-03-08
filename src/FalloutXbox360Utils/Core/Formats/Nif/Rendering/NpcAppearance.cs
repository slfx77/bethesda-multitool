using FalloutXbox360Utils.Core.Formats.Esm.Enums;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering;

/// <summary>
///     Resolved visual appearance of an NPC: mesh paths and FaceGen data needed to render.
///     Built incrementally — fields added as rendering phases are implemented.
/// </summary>
internal sealed class NpcAppearance
{
    public uint NpcFormId { get; init; }
    public string? EditorId { get; init; }
    public string? FullName { get; init; }
    public bool IsFemale { get; init; }

    // Phase 1: Head mesh
    public string? BaseHeadNifPath { get; init; }
    public string? HeadDiffuseOverride { get; init; }
    public string? FaceGenNifPath { get; init; }

    // Phase 2: EGM morph coefficients (merged NPC + race base)
    public float[]? FaceGenSymmetricCoeffs { get; init; }
    public float[]? FaceGenAsymmetricCoeffs { get; init; }

    // Phase 3: EGT texture morph coefficients (merged NPC + race base)
    public float[]? FaceGenTextureCoeffs { get; init; }

    // Phase 4: Hair mesh and texture
    public string? HairNifPath { get; init; }
    public string? HairTexturePath { get; init; }

    // Phase 5: Eye meshes and texture
    public string? LeftEyeNifPath { get; init; }
    public string? RightEyeNifPath { get; init; }
    public string? EyeTexturePath { get; init; }

    // Phase 6: Head parts (eyebrows, beards, teeth, etc.)
    public List<string>? HeadPartNifPaths { get; init; }

    // Hair color tint (packed 0x00BBGGRR from HCLR subrecord)
    public uint? HairColor { get; init; }

    // Phase 7: Equipment (resolved from NPC_ CNTO inventory → ARMO biped models)
    public List<EquippedItem>? EquippedItems { get; init; }

    // Phase 10: Weapon (resolved from NPC_ CNTO inventory → WEAP)
    public EquippedWeapon? EquippedWeapon { get; init; }

    // Phase 8: Body meshes (from RACE body parts section, after NAM1)
    public string? UpperBodyNifPath { get; init; }
    public string? LeftHandNifPath { get; init; }
    public string? RightHandNifPath { get; init; }
    public string? BodyTexturePath { get; init; }
    public string? HandTexturePath { get; init; }
    public string? SkeletonNifPath { get; init; }

    // Phase 9: Body EGT paths (for body/hand skin tinting via FaceGen texture morphs)
    public string? BodyEgtPath { get; init; }
    public string? LeftHandEgtPath { get; init; }
    public string? RightHandEgtPath { get; init; }
}

/// <summary>
///     A single piece of equipment resolved from NPC_ CNTO → ARMO.
/// </summary>
internal sealed class EquippedItem
{
    public uint BipedFlags { get; init; }
    public string MeshPath { get; init; } = "";
}

/// <summary>
///     A weapon resolved from NPC_ CNTO inventory → WEAP.
/// </summary>
internal sealed class EquippedWeapon
{
    public WeaponType WeaponType { get; init; }
    public string MeshPath { get; init; } = "";
}
