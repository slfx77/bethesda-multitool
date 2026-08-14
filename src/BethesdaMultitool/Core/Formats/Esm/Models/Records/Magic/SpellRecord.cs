using BethesdaMultitool.Core.Formats.Esm.Enums;

namespace BethesdaMultitool.Core.Formats.Esm.Models.Records.Magic;

/// <summary>
///     Parsed Spell record.
///     Aggregates data from SPEL main record header, SPIT (16 bytes), and repeated
///     EFID/EFIT/CTDA effect groups.
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

    /// <summary>
    ///     Historical name for the raw SPIT word at bytes 4..7. Fallout New Vegas xEdit labels
    ///     this storage unused; it remains modeled so captured bytes are not silently discarded.
    /// </summary>
    public uint Cost { get; init; }

    /// <summary>
    ///     Historical name for the raw SPIT word at bytes 8..11. Fallout New Vegas xEdit labels
    ///     this storage unused; it remains modeled so captured bytes are not silently discarded.
    /// </summary>
    public uint Level { get; init; }

    /// <summary>Spell flags.</summary>
    public byte Flags { get; init; }

    /// <summary>Effects and their conditions (EFID + EFIT + CTDA* groups).</summary>
    public List<EnchantmentEffect> Effects { get; init; } = [];

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
