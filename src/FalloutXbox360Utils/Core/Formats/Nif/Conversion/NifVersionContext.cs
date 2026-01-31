namespace FalloutXbox360Utils.Core.Formats.Nif.Conversion;

/// <summary>
///     Context for evaluating version expressions.
/// </summary>
public sealed record NifVersionContext
{
    /// <summary>NIF file version (e.g., 0x14020007 for 20.2.0.7)</summary>
    public uint Version { get; init; }

    /// <summary>User version (game-specific)</summary>
    public uint UserVersion { get; init; }

    /// <summary>Bethesda stream version (e.g., 34 for FO3/NV)</summary>
    public int BsVersion { get; init; }

    // Common presets
    public static NifVersionContext FalloutNV => new()
    {
        Version = 0x14020007, // 20.2.0.7
        UserVersion = 0,
        BsVersion = 34
    };

    public static NifVersionContext Fallout4 => new()
    {
        Version = 0x14020007,
        UserVersion = 12,
        BsVersion = 130
    };

    public static NifVersionContext Skyrim => new()
    {
        Version = 0x14020007,
        UserVersion = 12,
        BsVersion = 83
    };
}
