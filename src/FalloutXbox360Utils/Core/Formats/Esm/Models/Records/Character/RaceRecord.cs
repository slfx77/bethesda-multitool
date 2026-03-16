namespace FalloutXbox360Utils.Core.Formats.Esm.Models;

/// <summary>
///     Parsed Race record.
///     Aggregates data from RACE main record header, DATA (36 bytes), and related subrecords.
/// </summary>
public record RaceRecord
{
    /// <summary>FormID of the race record.</summary>
    public uint FormId { get; init; }

    /// <summary>Editor ID.</summary>
    public string? EditorId { get; init; }

    /// <summary>Display name.</summary>
    public string? FullName { get; init; }

    /// <summary>Race description (DESC subrecord).</summary>
    public string? Description { get; init; }

    // DATA subrecord (36 bytes) - Skill Boosts (7 pairs of AV code + boost value)
    /// <summary>Skill boosts from DATA subrecord (7 pairs of Skill AV code + Boost value).</summary>
    public List<(int SkillIndex, sbyte Boost)> SkillBoosts { get; init; } = [];

    // Height/Weight data from DATA subrecord
    /// <summary>Male height multiplier.</summary>
    public float MaleHeight { get; init; }

    /// <summary>Female height multiplier.</summary>
    public float FemaleHeight { get; init; }

    /// <summary>Male weight multiplier.</summary>
    public float MaleWeight { get; init; } = 1.0f;

    /// <summary>Female weight multiplier.</summary>
    public float FemaleWeight { get; init; } = 1.0f;

    /// <summary>Race flags from DATA subrecord.</summary>
    public uint DataFlags { get; init; }

    /// <summary>Whether this race is playable.</summary>
    public bool IsPlayable => (DataFlags & 0x01) != 0;

    /// <summary>Whether this race is a child race.</summary>
    public bool IsChild => (DataFlags & 0x04) != 0;

    // Related races (ONAM, YNAM)
    /// <summary>Older race FormID (for aging).</summary>
    public uint? OlderRaceFormId { get; init; }

    /// <summary>Younger race FormID (for aging).</summary>
    public uint? YoungerRaceFormId { get; init; }

    // Voice types (VTCK)
    /// <summary>Male voice type FormID.</summary>
    public uint? MaleVoiceFormId { get; init; }

    /// <summary>Female voice type FormID.</summary>
    public uint? FemaleVoiceFormId { get; init; }

    /// <summary>Racial abilities (SPLO subrecords).</summary>
    public List<uint> AbilityFormIds { get; init; } = [];

    // Hair/Eyes from DNAM, CNAM, HNAM, ENAM
    /// <summary>Default male hair FormID.</summary>
    public uint? DefaultHairMaleFormId { get; init; }

    /// <summary>Default female hair FormID.</summary>
    public uint? DefaultHairFemaleFormId { get; init; }

    /// <summary>Default hair color index (CNAM subrecord, not a FormID).</summary>
    public byte? DefaultHairColor { get; init; }

    /// <summary>Available hair style FormIDs.</summary>
    public List<uint> HairStyleFormIds { get; init; } = [];

    /// <summary>Available eye color FormIDs.</summary>
    public List<uint> EyeColorFormIds { get; init; } = [];

    // FaceGen data
    /// <summary>FaceGen main clamp value.</summary>
    public float FaceGenMainClamp { get; init; }

    /// <summary>FaceGen face clamp value.</summary>
    public float FaceGenFaceClamp { get; init; }

    // FaceGen base morph coefficients (per-race default face, from MNAM/FNAM sections)
    /// <summary>Male FaceGen geometry-symmetric base coefficients (FGGS after MNAM, 50 floats).</summary>
    public float[]? MaleFaceGenGeometrySymmetric { get; init; }

    /// <summary>Male FaceGen geometry-asymmetric base coefficients (FGGA after MNAM, 30 floats).</summary>
    public float[]? MaleFaceGenGeometryAsymmetric { get; init; }

    /// <summary>Male FaceGen texture-symmetric base coefficients (FGTS after MNAM, 50 floats).</summary>
    public float[]? MaleFaceGenTextureSymmetric { get; init; }

    /// <summary>Female FaceGen geometry-symmetric base coefficients (FGGS after FNAM, 50 floats).</summary>
    public float[]? FemaleFaceGenGeometrySymmetric { get; init; }

    /// <summary>Female FaceGen geometry-asymmetric base coefficients (FGGA after FNAM, 30 floats).</summary>
    public float[]? FemaleFaceGenGeometryAsymmetric { get; init; }

    /// <summary>Female FaceGen texture-symmetric base coefficients (FGTS after FNAM, 50 floats).</summary>
    public float[]? FemaleFaceGenTextureSymmetric { get; init; }

    // Body mesh paths (from body parts section after NAM1)
    /// <summary>Male head model path (NAM0 INDX 0 MODL).</summary>
    public string? MaleHeadModelPath { get; init; }

    /// <summary>Female head model path (NAM0 INDX 0 MODL).</summary>
    public string? FemaleHeadModelPath { get; init; }

    /// <summary>Male head texture path (NAM0 INDX 0 ICON).</summary>
    public string? MaleHeadTexturePath { get; init; }

    /// <summary>Female head texture path (NAM0 INDX 0 ICON).</summary>
    public string? FemaleHeadTexturePath { get; init; }

    /// <summary>Male mouth model path (NAM0 INDX 2 MODL).</summary>
    public string? MaleMouthModelPath { get; init; }

    /// <summary>Female mouth model path (NAM0 INDX 2 MODL).</summary>
    public string? FemaleMouthModelPath { get; init; }

    /// <summary>Male lower teeth model path (NAM0 INDX 3 MODL).</summary>
    public string? MaleLowerTeethModelPath { get; init; }

    /// <summary>Female lower teeth model path (NAM0 INDX 3 MODL).</summary>
    public string? FemaleLowerTeethModelPath { get; init; }

    /// <summary>Male upper teeth model path (NAM0 INDX 4 MODL).</summary>
    public string? MaleUpperTeethModelPath { get; init; }

    /// <summary>Female upper teeth model path (NAM0 INDX 4 MODL).</summary>
    public string? FemaleUpperTeethModelPath { get; init; }

    /// <summary>Male tongue model path (NAM0 INDX 5 MODL).</summary>
    public string? MaleTongueModelPath { get; init; }

    /// <summary>Female tongue model path (NAM0 INDX 5 MODL).</summary>
    public string? FemaleTongueModelPath { get; init; }

    /// <summary>Male upper body model path (NAM1 INDX 0 MODL).</summary>
    public string? MaleUpperBodyPath { get; init; }

    /// <summary>Female upper body model path (NAM1 INDX 0 MODL).</summary>
    public string? FemaleUpperBodyPath { get; init; }

    /// <summary>Male left hand model path (NAM1 INDX 1 MODL).</summary>
    public string? MaleLeftHandPath { get; init; }

    /// <summary>Female left hand model path (NAM1 INDX 1 MODL).</summary>
    public string? FemaleLeftHandPath { get; init; }

    /// <summary>Male right hand model path (NAM1 INDX 2 MODL).</summary>
    public string? MaleRightHandPath { get; init; }

    /// <summary>Female right hand model path (NAM1 INDX 2 MODL).</summary>
    public string? FemaleRightHandPath { get; init; }

    /// <summary>Male body texture path (NAM1 INDX 0 ICON).</summary>
    public string? MaleBodyTexturePath { get; init; }

    /// <summary>Female body texture path (NAM1 INDX 0 ICON).</summary>
    public string? FemaleBodyTexturePath { get; init; }

    /// <summary>Offset in the dump where this record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the record was detected as big-endian (Xbox 360).</summary>
    public bool IsBigEndian { get; init; }
}
