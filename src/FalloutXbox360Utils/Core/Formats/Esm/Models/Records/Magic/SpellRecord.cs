using FalloutXbox360Utils.Core.Formats.Esm.Enums;

namespace FalloutXbox360Utils.Core.Formats.Esm.Models;

/// <summary>
///     Fully reconstructed Spell from memory dump.
///     Aggregates data from SPEL main record header, SPIT (16 bytes), EFID subrecords.
/// </summary>
public record SpellRecord
{
    /// <summary>FormID of the spell record.</summary>
    public uint FormId { get; init; }

    /// <summary>Editor ID.</summary>
    public string? EditorId { get; init; }

    /// <summary>Display name.</summary>
    public string? FullName { get; init; }

    // SPIT subrecord (16 bytes)
    /// <summary>Spell type classification.</summary>
    public SpellType Type { get; init; }

    /// <summary>Spell cost.</summary>
    public uint Cost { get; init; }

    /// <summary>Spell level.</summary>
    public uint Level { get; init; }

    /// <summary>Spell flags.</summary>
    public byte Flags { get; init; }

    /// <summary>Effect FormIDs (EFID subrecords).</summary>
    public List<uint> EffectFormIds { get; init; } = [];

    /// <summary>Offset in the dump where this record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the record was detected as big-endian (Xbox 360).</summary>
    public bool IsBigEndian { get; init; }

    /// <summary>Human-readable spell type name.</summary>
    public string TypeName => Type switch
    {
        SpellType.Spell => "Spell",
        SpellType.Disease => "Disease",
        SpellType.Power => "Power",
        SpellType.LesserPower => "Lesser Power",
        SpellType.Ability => "Ability",
        SpellType.Poison => "Poison",
        SpellType.Addiction => "Addiction",
        _ => $"Unknown ({(uint)Type})"
    };
}
