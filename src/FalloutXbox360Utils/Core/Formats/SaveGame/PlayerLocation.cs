namespace FalloutXbox360Utils.Core.Formats.SaveGame;

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
