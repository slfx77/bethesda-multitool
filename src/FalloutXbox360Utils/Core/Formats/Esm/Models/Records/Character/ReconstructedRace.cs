namespace FalloutXbox360Utils.Core.Formats.Esm.Models;

/// <summary>
///     Fully reconstructed Race from ESM or memory dump.
///     Aggregates data from RACE main record header, DATA (36 bytes), and related subrecords.
/// </summary>
public record ReconstructedRace
{
    /// <summary>FormID of the race record.</summary>
    public uint FormId { get; init; }

    /// <summary>Editor ID.</summary>
    public string? EditorId { get; init; }

    /// <summary>Display name.</summary>
    public string? FullName { get; init; }

    /// <summary>Race description (DESC subrecord).</summary>
    public string? Description { get; init; }

    // DATA subrecord (36 bytes) - S.P.E.C.I.A.L. bonuses (first 7 bytes of SkillBoosts area)
    /// <summary>Strength modifier.</summary>
    public sbyte Strength { get; init; }

    /// <summary>Perception modifier.</summary>
    public sbyte Perception { get; init; }

    /// <summary>Endurance modifier.</summary>
    public sbyte Endurance { get; init; }

    /// <summary>Charisma modifier.</summary>
    public sbyte Charisma { get; init; }

    /// <summary>Intelligence modifier.</summary>
    public sbyte Intelligence { get; init; }

    /// <summary>Agility modifier.</summary>
    public sbyte Agility { get; init; }

    /// <summary>Luck modifier.</summary>
    public sbyte Luck { get; init; }

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

    /// <summary>Default hair color (index or FormID depending on game).</summary>
    public uint? DefaultHairColorFormId { get; init; }

    /// <summary>Available hair style FormIDs.</summary>
    public List<uint> HairStyleFormIds { get; init; } = [];

    /// <summary>Available eye color FormIDs.</summary>
    public List<uint> EyeColorFormIds { get; init; } = [];

    // FaceGen data
    /// <summary>FaceGen main clamp value.</summary>
    public float FaceGenMainClamp { get; init; }

    /// <summary>FaceGen face clamp value.</summary>
    public float FaceGenFaceClamp { get; init; }

    /// <summary>Offset in the dump where this record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the record was detected as big-endian (Xbox 360).</summary>
    public bool IsBigEndian { get; init; }
}
