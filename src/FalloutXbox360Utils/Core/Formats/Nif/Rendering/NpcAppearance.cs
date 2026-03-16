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
    public string? RenderVariantLabel { get; init; }

    // Phase 1: Head mesh
    public string? BaseHeadNifPath { get; init; }
    public string? HeadDiffuseOverride { get; init; }
    public string? FaceGenNifPath { get; init; }

    // Phase 2: EGM morph coefficients (merged NPC + race base)
    public float[]? FaceGenSymmetricCoeffs { get; init; }
    public float[]? FaceGenAsymmetricCoeffs { get; init; }

    // Phase 3: EGT texture morph coefficients for the current render/export path.
    public float[]? FaceGenTextureCoeffs { get; init; }
    public float[]? NpcFaceGenTextureCoeffs { get; init; }
    public float[]? RaceFaceGenTextureCoeffs { get; init; }

    // Phase 4: Hair mesh and texture
    public string? HairNifPath { get; init; }
    public string? HairTexturePath { get; init; }

    // Phase 5: Eye meshes and texture
    public string? LeftEyeNifPath { get; init; }
    public string? RightEyeNifPath { get; init; }
    public string? EyeTexturePath { get; init; }

    // Phase 6: Race face parts (mouth, teeth, tongue)
    public string? MouthNifPath { get; init; }
    public string? LowerTeethNifPath { get; init; }
    public string? UpperTeethNifPath { get; init; }
    public string? TongueNifPath { get; init; }

    // Phase 7: Head parts (eyebrows, beards, NPC HDPT attachments, etc.)
    public List<string>? HeadPartNifPaths { get; init; }

    // Hair color tint (packed 0x00BBGGRR from HCLR subrecord)
    public uint? HairColor { get; init; }

    // Phase 8: Equipment (resolved from NPC_ CNTO inventory → ARMO biped models)
    public List<EquippedItem>? EquippedItems { get; init; }

    // Phase 11: Weapon (resolved from packages + inventory, rendered in holster or equipped space by class)
    public WeaponVisual? WeaponVisual { get; init; }

    // Phase 9: Body meshes (from RACE body parts section, after NAM1)
    public string? UpperBodyNifPath { get; init; }
    public string? LeftHandNifPath { get; init; }
    public string? RightHandNifPath { get; init; }
    public string? BodyTexturePath { get; init; }
    public string? HandTexturePath { get; init; }
    public string? SkeletonNifPath { get; init; }

    // Phase 10: Body EGT paths (for body/hand skin tinting via FaceGen texture morphs)
    public string? BodyEgtPath { get; init; }
    public string? LeftHandEgtPath { get; init; }
    public string? RightHandEgtPath { get; init; }

    internal NpcAppearance CloneWithTextureVariant(
        float[]? textureCoeffs,
        string? renderVariantLabel)
    {
        return new NpcAppearance
        {
            NpcFormId = NpcFormId,
            EditorId = EditorId,
            FullName = FullName,
            IsFemale = IsFemale,
            RenderVariantLabel = renderVariantLabel,
            BaseHeadNifPath = BaseHeadNifPath,
            HeadDiffuseOverride = HeadDiffuseOverride,
            FaceGenNifPath = FaceGenNifPath,
            FaceGenSymmetricCoeffs = FaceGenSymmetricCoeffs,
            FaceGenAsymmetricCoeffs = FaceGenAsymmetricCoeffs,
            FaceGenTextureCoeffs = textureCoeffs,
            NpcFaceGenTextureCoeffs = NpcFaceGenTextureCoeffs,
            RaceFaceGenTextureCoeffs = RaceFaceGenTextureCoeffs,
            HairNifPath = HairNifPath,
            HairTexturePath = HairTexturePath,
            LeftEyeNifPath = LeftEyeNifPath,
            RightEyeNifPath = RightEyeNifPath,
            EyeTexturePath = EyeTexturePath,
            MouthNifPath = MouthNifPath,
            LowerTeethNifPath = LowerTeethNifPath,
            UpperTeethNifPath = UpperTeethNifPath,
            TongueNifPath = TongueNifPath,
            HeadPartNifPaths = HeadPartNifPaths,
            HairColor = HairColor,
            EquippedItems = EquippedItems,
            WeaponVisual = WeaponVisual,
            UpperBodyNifPath = UpperBodyNifPath,
            LeftHandNifPath = LeftHandNifPath,
            RightHandNifPath = RightHandNifPath,
            BodyTexturePath = BodyTexturePath,
            HandTexturePath = HandTexturePath,
            SkeletonNifPath = SkeletonNifPath,
            BodyEgtPath = BodyEgtPath,
            LeftHandEgtPath = LeftHandEgtPath,
            RightHandEgtPath = RightHandEgtPath
        };
    }
}
