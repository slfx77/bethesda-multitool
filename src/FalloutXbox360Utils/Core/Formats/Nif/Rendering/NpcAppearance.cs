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

    // Phase 4: Hair mesh
    public string? HairNifPath { get; init; }

    // Phase 5: Eye meshes and texture
    public string? LeftEyeNifPath { get; init; }
    public string? RightEyeNifPath { get; init; }
    public string? EyeTexturePath { get; init; }

    // Phase 6: Head parts (eyebrows, beards, teeth, etc.)
    public List<string>? HeadPartNifPaths { get; init; }
}
