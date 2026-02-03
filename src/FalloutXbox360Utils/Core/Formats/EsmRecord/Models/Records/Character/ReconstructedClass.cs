namespace FalloutXbox360Utils.Core.Formats.EsmRecord.Models;

/// <summary>
///     Reconstructed Class (CLAS) from memory dump.
///     Determines NPC skill growth and tag skills.
/// </summary>
public record ReconstructedClass
{
    public uint FormId { get; init; }
    public string? EditorId { get; init; }
    public string? FullName { get; init; }
    public string? Description { get; init; }

    /// <summary>Icon path from ICON subrecord.</summary>
    public string? Icon { get; init; }

    /// <summary>Tag skill indices (up to 4) from DATA.</summary>
    public int[] TagSkills { get; init; } = [];

    /// <summary>Class flags from DATA.</summary>
    public uint Flags { get; init; }

    /// <summary>Barter/services flags from DATA.</summary>
    public uint BarterFlags { get; init; }

    /// <summary>Training skill index from ATTR subrecord.</summary>
    public byte TrainingSkill { get; init; }

    /// <summary>Training max level from ATTR subrecord.</summary>
    public byte TrainingLevel { get; init; }

    /// <summary>SPECIAL attribute weights (7 values) from ATTR subrecord.</summary>
    public byte[] AttributeWeights { get; init; } = [];

    public long Offset { get; init; }
    public bool IsBigEndian { get; init; }

    public bool IsPlayable => (Flags & 1) != 0;
    public bool IsGuard => (Flags & 2) != 0;
}
