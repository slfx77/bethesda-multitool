namespace BethesdaMultitool.Core.Formats.Esm.Models.Records.Magic;

/// <summary>
///     Parsed Enchantment / Object Effect (ENCH) record.
/// </summary>
public record EnchantmentRecord
{
    public uint FormId { get; init; }
    public string? EditorId { get; init; }
    public string? FullName { get; init; }

    /// <summary>Enchantment type from ENIT: 0=Scroll, 2=Weapon, 3=Apparel.</summary>
    public uint EnchantType { get; init; }

    /// <summary>
    ///     Historical name for the raw word at ENIT bytes 4..7. Fallout New Vegas xEdit labels
    ///     this storage unused; it remains modeled so captured bytes are not silently discarded.
    /// </summary>
    public uint ChargeAmount { get; init; }

    /// <summary>
    ///     Historical name for the raw word at ENIT bytes 8..11. Fallout New Vegas xEdit labels
    ///     this storage unused; it remains modeled so captured bytes are not silently discarded.
    /// </summary>
    public uint EnchantCost { get; init; }

    /// <summary>Flags from ENIT.</summary>
    public byte Flags { get; init; }

    /// <summary>Effects and their conditions (EFID + EFIT + CTDA* groups).</summary>
    public List<EnchantmentEffect> Effects { get; init; } = [];

    public long Offset { get; init; }
    public bool IsBigEndian { get; init; }

    public string TypeName => EnchantType switch
    {
        0 => "Scroll",
        2 => "Weapon",
        3 => "Apparel",
        _ => $"Unknown({EnchantType})"
    };
}
