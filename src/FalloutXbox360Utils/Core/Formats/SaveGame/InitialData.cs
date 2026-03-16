namespace FalloutXbox360Utils.Core.Formats.SaveGame;

/// <summary>
///     Initial data parsed from a reference-type changed form.
///     Contains position and cell information.
/// </summary>
public sealed class InitialData
{
    /// <summary>Initial data type (4, 5, or 6).</summary>
    public int DataType { get; init; }

    /// <summary>Cell or worldspace RefID.</summary>
    public SaveRefId CellRefId { get; init; }

    /// <summary>World position X.</summary>
    public float PosX { get; init; }

    /// <summary>World position Y.</summary>
    public float PosY { get; init; }

    /// <summary>World position Z.</summary>
    public float PosZ { get; init; }

    /// <summary>Rotation X (radians).</summary>
    public float RotX { get; init; }

    /// <summary>Rotation Y (radians).</summary>
    public float RotY { get; init; }

    /// <summary>Rotation Z (radians).</summary>
    public float RotZ { get; init; }

    /// <summary>New cell RefID (for type 6 = cell changed).</summary>
    public SaveRefId? NewCellRefId { get; init; }

    /// <summary>New cell grid coordinate X (for type 6).</summary>
    public short? NewCoordX { get; init; }

    /// <summary>New cell grid coordinate Y (for type 6).</summary>
    public short? NewCoordY { get; init; }

    /// <summary>Base form RefID (for type 5 = created forms).</summary>
    public SaveRefId? BaseFormRefId { get; init; }
}