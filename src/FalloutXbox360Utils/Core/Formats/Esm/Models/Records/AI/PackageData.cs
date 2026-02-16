namespace FalloutXbox360Utils.Core.Formats.Esm.Models;

/// <summary>
///     Package data from PKDT subrecord (12 bytes).
///     Contains package type, general flags, Fallout behavior flags, and type-specific flags.
/// </summary>
public record PackageData
{
    /// <summary>Package type (ePROCEDURE_TYPE enum value).</summary>
    public byte Type { get; init; }

    /// <summary>
    ///     Combined general flags (iPackFlags uint32).
    ///     Reconstructed from PKDT Flags1 (byte[0]) and Flags2 (uint16[1-2]):
    ///     GeneralFlags = (uint)Flags1 | ((uint)Flags2 &lt;&lt; 8)
    /// </summary>
    public uint GeneralFlags { get; init; }

    /// <summary>Fallout-specific behavior flags (iFOBehaviorFlags uint16).</summary>
    public ushort FalloutBehaviorFlags { get; init; }

    /// <summary>Type-specific flags (iPackageSpecificFlags uint16).</summary>
    public ushort TypeSpecificFlags { get; init; }

    /// <summary>Human-readable package type name.</summary>
    public string TypeName => Type switch
    {
        0 => "Find",
        1 => "Follow",
        2 => "Escort",
        3 => "Eat",
        4 => "Sleep",
        5 => "Wander",
        6 => "Travel",
        7 => "Accompany",
        8 => "Use Item At",
        9 => "Ambush",
        10 => "Flee Not Combat",
        12 => "Sandbox",
        13 => "Patrol",
        14 => "Guard",
        15 => "Dialogue",
        16 => "Use Weapon",
        _ => $"Unknown ({Type})"
    };
}
