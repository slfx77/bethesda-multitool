namespace FalloutXbox360Utils.Repack;

/// <summary>
///     Configuration options for the Xbox 360 to PC repacker.
/// </summary>
public sealed class RepackerOptions
{
    /// <summary>
    ///     Source folder containing the Xbox 360 FalloutNV installation.
    /// </summary>
    public required string SourceFolder { get; init; }

    /// <summary>
    ///     Output folder for the converted PC files.
    /// </summary>
    public required string OutputFolder { get; init; }

    /// <summary>
    ///     Whether to process Video folder (copy BIK files).
    /// </summary>
    public bool ProcessVideo { get; init; } = true;

    /// <summary>
    ///     Whether to process Music folder (XMA to MP3).
    /// </summary>
    public bool ProcessMusic { get; init; } = true;

    /// <summary>
    ///     Whether to process BSA files (extract, convert, repack).
    /// </summary>
    public bool ProcessBsa { get; init; } = true;

    /// <summary>
    ///     Specific BSA files to process. If null or empty, all BSAs are processed.
    ///     Contains just the BSA filenames (not full paths).
    /// </summary>
    public HashSet<string>? SelectedBsaFiles { get; init; }

    /// <summary>
    ///     Whether to generate a hybrid INI file for PC compatibility.
    /// </summary>
    public bool ProcessIni { get; init; } = true;

    /// <summary>
    ///     Whether to process ESM files (endian conversion).
    /// </summary>
    public bool ProcessEsm { get; init; } = true;

    /// <summary>
    ///     Whether to process ESP files (endian conversion).
    /// </summary>
    public bool ProcessEsp { get; init; } = true;

    /// <summary>
    ///     Number of concurrent FFmpeg processes for audio conversion.
    /// </summary>
    public int MaxConcurrentAudioConversions { get; init; } = 4;

    /// <summary>
    ///     Whether to use verbose logging during conversion.
    /// </summary>
    public bool Verbose { get; init; }

    /// <summary>
    ///     Whether to update the user's Fallout.ini in My Games\FalloutNV with the converted BSA list.
    ///     A backup will be created before modification.
    /// </summary>
    public bool UpdateUserIni { get; init; }
}
