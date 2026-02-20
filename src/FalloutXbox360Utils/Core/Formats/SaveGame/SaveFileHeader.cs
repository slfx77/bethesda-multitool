namespace FalloutXbox360Utils.Core.Formats.SaveGame;

/// <summary>
///     Parsed header from a FO3SAVEGAME save file.
/// </summary>
public sealed class SaveFileHeader
{
    /// <summary>Header size as declared in the file (bytes of the Header struct).</summary>
    public uint HeaderSize { get; init; }

    /// <summary>Save format version (typically 0x30 for FO3/FNV).</summary>
    public uint Version { get; init; }

    /// <summary>Screenshot width in pixels.</summary>
    public uint ScreenshotWidth { get; init; }

    /// <summary>Screenshot height in pixels.</summary>
    public uint ScreenshotHeight { get; init; }

    /// <summary>Save slot number.</summary>
    public uint SaveNumber { get; init; }

    /// <summary>Player character name (may be empty in prototype builds).</summary>
    public string PlayerName { get; init; } = "";

    /// <summary>Player karma/reputation status string (e.g. "Drifter", "Adventurer").</summary>
    public string PlayerStatus { get; init; } = "";

    /// <summary>Player character level.</summary>
    public uint PlayerLevel { get; init; }

    /// <summary>Current cell/location name (e.g. "Mojave Wasteland", "The Strip").</summary>
    public string PlayerCell { get; init; } = "";

    /// <summary>Playtime as formatted string (e.g. "001.30.41" = hours.minutes.seconds).</summary>
    public string SaveDuration { get; init; } = "";

    /// <summary>Form version byte (follows screenshot data).</summary>
    public byte FormVersion { get; init; }

    /// <summary>List of active plugin names (e.g. "Fallout3.esm").</summary>
    public IReadOnlyList<string> Plugins { get; init; } = [];

    /// <summary>Offset within the data where the screenshot RGB data begins.</summary>
    public int ScreenshotDataOffset { get; init; }

    /// <summary>Size of the screenshot data in bytes (width * height * 3).</summary>
    public int ScreenshotDataSize { get; init; }
}
