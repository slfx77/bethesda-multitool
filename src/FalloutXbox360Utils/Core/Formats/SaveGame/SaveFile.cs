namespace FalloutXbox360Utils.Core.Formats.SaveGame;

/// <summary>
///     Complete parsed representation of a FO3SAVEGAME save file.
/// </summary>
public sealed class SaveFile
{
    /// <summary>Header with metadata, screenshot info, and plugin list.</summary>
    public required SaveFileHeader Header { get; init; }

    /// <summary>Gameplay statistics.</summary>
    public required SaveStatistics Statistics { get; init; }

    /// <summary>File Location Table with section offsets and counts.</summary>
    public required FileLocationTable LocationTable { get; init; }

    /// <summary>Global Data Table 1 entries (types 0-11).</summary>
    public IReadOnlyList<GlobalDataEntry> GlobalData1 { get; init; } = [];

    /// <summary>Global Data Table 2 entries.</summary>
    public IReadOnlyList<GlobalDataEntry> GlobalData2 { get; init; } = [];

    /// <summary>All changed forms.</summary>
    public IReadOnlyList<ChangedForm> ChangedForms { get; init; } = [];

    /// <summary>FormID lookup array (used by RefIDs of type 0).</summary>
    public IReadOnlyList<uint> FormIdArray { get; init; } = [];

    /// <summary>Visited worldspace FormIDs.</summary>
    public IReadOnlyList<uint> VisitedWorldspaces { get; init; } = [];

    /// <summary>Parsed player location from Global Data Type 1, if available.</summary>
    public PlayerLocation? PlayerLocation { get; init; }

    /// <summary>Parsed global variables from Global Data Type 3, if available.</summary>
    public IReadOnlyList<GlobalVariable> GlobalVariables { get; init; } = [];

    /// <summary>Offset of the inner Savegame.dat within the STFS container (0 if not an STFS file).</summary>
    public int StfsPayloadOffset { get; init; }
}
