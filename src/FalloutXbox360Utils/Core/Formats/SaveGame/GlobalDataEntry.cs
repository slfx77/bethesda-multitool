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

/// <summary>
///     Parsed player location from Global Data Type 1 (TES).
/// </summary>
public sealed class PlayerLocation
{
    /// <summary>Worldspace RefID.</summary>
    public SaveRefId WorldspaceRefId { get; init; }

    /// <summary>Grid coordinate X.</summary>
    public int CoordX { get; init; }

    /// <summary>Grid coordinate Y.</summary>
    public int CoordY { get; init; }

    /// <summary>Cell RefID.</summary>
    public SaveRefId CellRefId { get; init; }

    /// <summary>Player world X position.</summary>
    public float PosX { get; init; }

    /// <summary>Player world Y position.</summary>
    public float PosY { get; init; }

    /// <summary>Player world Z position.</summary>
    public float PosZ { get; init; }
}

/// <summary>
///     A global variable entry from Global Data Type 3.
/// </summary>
public readonly record struct GlobalVariable(SaveRefId RefId, float Value);
