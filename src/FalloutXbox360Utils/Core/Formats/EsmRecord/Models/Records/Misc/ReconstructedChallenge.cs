namespace FalloutXbox360Utils.Core.Formats.EsmRecord.Models;

/// <summary>
///     Reconstructed Challenge (CHAL) from memory dump.
///     FNV-specific achievement-like goals.
/// </summary>
public record ReconstructedChallenge
{
    public uint FormId { get; init; }
    public string? EditorId { get; init; }
    public string? FullName { get; init; }
    public string? Description { get; init; }

    /// <summary>Icon path from ICON subrecord.</summary>
    public string? Icon { get; init; }

    /// <summary>Challenge type from DATA.</summary>
    public uint ChallengeType { get; init; }

    /// <summary>Threshold value to complete the challenge.</summary>
    public uint Threshold { get; init; }

    /// <summary>Challenge flags from DATA.</summary>
    public uint Flags { get; init; }

    /// <summary>Interval between rewards from DATA.</summary>
    public uint Interval { get; init; }

    /// <summary>Associated value1 from DATA (depends on type).</summary>
    public uint Value1 { get; init; }

    /// <summary>Associated value2 from DATA (depends on type).</summary>
    public uint Value2 { get; init; }

    /// <summary>Associated value3 from DATA (form reference).</summary>
    public uint Value3 { get; init; }

    /// <summary>Script FormID from SCRI subrecord.</summary>
    public uint Script { get; init; }

    public long Offset { get; init; }
    public bool IsBigEndian { get; init; }

    public string TypeName => ChallengeType switch
    {
        0 => "KillFromList",
        1 => "KillFormID",
        2 => "KillInCategory",
        3 => "HitEnemy",
        4 => "DiscoverMapMarker",
        5 => "UseItem",
        6 => "AcquireItem",
        7 => "UseSkill",
        8 => "DoDamage",
        9 => "UseItemOnItem",
        10 => "AcquireFormID",
        11 => "MiscStat",
        _ => $"Unknown({ChallengeType})"
    };
}
