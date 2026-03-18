namespace FalloutXbox360Utils.Core.Formats.SaveGame;

/// <summary>
///     A Global Data entry from the save file body.
///     Structure: Type (uint32) + DataLength (uint32) + Data (byte[]).
/// </summary>
public sealed class GlobalDataEntry
{
    /// <summary>Global data type identifier.</summary>
    public uint Type { get; init; }

    /// <summary>Raw data bytes.</summary>
    public byte[] Data { get; init; } = [];

    /// <summary>Human-readable type name.</summary>
    public string TypeName => Type switch
    {
        0 => "Misc Stats",
        1 => "TES (Player Location)",
        2 => "Haggle",
        3 => "Global Variables",
        4 => "Combat",
        5 => "Combat Groups",
        6 => "Unknown 6",
        7 => "Image Space",
        8 => "Sky",
        9 => "Sound/Detection",
        10 => "Sound/Radio",
        11 => "Sound",
        1000 => "NVSE Plugin Data",
        _ => $"Unknown ({Type})"
    };
}
