namespace FalloutXbox360Utils.Core.Formats.EsmRecord.Models;

/// <summary>
///     Fully reconstructed Race from memory dump.
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

    // DATA subrecord (36 bytes) - S.P.E.C.I.A.L. bonuses
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

    // Height data
    /// <summary>Male height multiplier.</summary>
    public float MaleHeight { get; init; }

    /// <summary>Female height multiplier.</summary>
    public float FemaleHeight { get; init; }

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

    /// <summary>Offset in the dump where this record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the record was detected as big-endian (Xbox 360).</summary>
    public bool IsBigEndian { get; init; }
}
