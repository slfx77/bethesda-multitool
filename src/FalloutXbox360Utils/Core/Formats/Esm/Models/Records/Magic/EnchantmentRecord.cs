namespace FalloutXbox360Utils.Core.Formats.Esm.Models;

/// <summary>
///     Reconstructed Enchantment / Object Effect (ENCH) from memory dump.
/// </summary>
public record EnchantmentRecord
{
    public uint FormId { get; init; }
    public string? EditorId { get; init; }
    public string? FullName { get; init; }

    /// <summary>Enchantment type from ENIT: 0=Scroll, 2=Weapon, 3=Apparel.</summary>
    public uint EnchantType { get; init; }

    /// <summary>Charge amount from ENIT.</summary>
    public uint ChargeAmount { get; init; }

    /// <summary>Enchantment cost from ENIT.</summary>
    public uint EnchantCost { get; init; }

    /// <summary>Flags from ENIT.</summary>
    public byte Flags { get; init; }

    /// <summary>Effects applied by this enchantment (EFID + EFIT pairs).</summary>
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
