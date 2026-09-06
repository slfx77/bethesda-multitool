using BethesdaMultitool.Core.Analysis;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.FileFormat;

/// <summary>
///     Detects file types by examining magic bytes at the start of a file, or — for the classic
///     pre-plugin-era games, whose analyzable unit is an install rather than a single file — by
///     recognizing an install directory.
/// </summary>
public static class FileTypeDetector
{
    /// <summary>
    ///     Detects the file type by reading the first 4 bytes of the file.
    /// </summary>
    /// <param name="filePath">Path to the file to analyze.</param>
    /// <returns>The detected file type.</returns>
    public static AnalysisFileType Detect(string filePath)
    {
        if (!File.Exists(filePath))
        {
            // A classic game "file" can be a whole install directory — those games have no single
            // plugin to point at, so the install root is the analyzable unit.
            return Directory.Exists(filePath) && ClassicGameLocator.DetectFromDirectory(filePath) is not null
                ? AnalysisFileType.ClassicGameData
                : AnalysisFileType.Unknown;
        }

        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            Span<byte> header = stackalloc byte[4];
            if (fs.Read(header) < 4)
            {
                return AnalysisFileType.Unknown;
            }

            var result = DetectFromMagic(header);

            // STFS-wrapped save files (.fxs) start with "CON " which is too generic,
            // so fall back to extension-based detection for save files.
            if (result == AnalysisFileType.Unknown &&
                (filePath.EndsWith(".fxs", StringComparison.OrdinalIgnoreCase) ||
                 filePath.EndsWith(".fos", StringComparison.OrdinalIgnoreCase)))
            {
                return AnalysisFileType.SaveFile;
            }

            // A J2ME JAR (TES Travels) IS a classic install: one PKZIP whose entry names carry a
            // profile's install markers. Checked before the location probe because the JAR can sit
            // anywhere (a Sample folder, a download directory) with no install around it.
            if (result == AnalysisFileType.Unknown && ClassicGameLocator.DetectFromArchive(filePath) is not null)
            {
                return AnalysisFileType.ClassicGameData;
            }

            // A console disc image is an install the same way: Fallout: Brotherhood of Steel ships
            // as one PS2 ISO. Gated on the ISO9660 signature inside the probe, so an ordinary file
            // never pays for a mount.
            if (result == AnalysisFileType.Unknown && ClassicSourceProbe.TryDetectDiscImage(filePath) is not null)
            {
                return AnalysisFileType.ClassicGameData;
            }

            // Classic-era formats mostly have weak or no magic (Fallout DAT1 has none), so the last
            // resort is location: a declared artifact inside a detected classic install.
            if (result == AnalysisFileType.Unknown && ClassicSourceProbe.TryDetect(filePath) is not null)
            {
                return AnalysisFileType.ClassicGameData;
            }

            return result;
        }
        catch
        {
            return AnalysisFileType.Unknown;
        }
    }

    /// <summary>
    ///     Detects the file type from a 4-byte magic header.
    /// </summary>
    public static AnalysisFileType DetectFromMagic(ReadOnlySpan<byte> header)
    {
        if (header.Length < 4)
        {
            return AnalysisFileType.Unknown;
        }

        // Windows minidump: "MDMP" (0x4D 0x44 0x4D 0x50)
        if (header[0] == 'M' && header[1] == 'D' && header[2] == 'M' && header[3] == 'P')
        {
            return AnalysisFileType.Minidump;
        }

        // PC ESM (little-endian): "TES4" (0x54 0x45 0x53 0x34)
        if (header[0] == 'T' && header[1] == 'E' && header[2] == 'S' && header[3] == '4')
        {
            return AnalysisFileType.EsmFile;
        }

        // Morrowind ESM/ESP (TES3, little-endian): "TES3" (0x54 0x45 0x53 0x33).
        if (header[0] == 'T' && header[1] == 'E' && header[2] == 'S' && header[3] == '3')
        {
            return AnalysisFileType.EsmFile;
        }

        // Xbox 360 ESM (big-endian): "4SET" (0x34 0x53 0x45 0x54)
        if (header[0] == '4' && header[1] == 'S' && header[2] == 'E' && header[3] == 'T')
        {
            return AnalysisFileType.EsmFile;
        }

        // Raw FO3SAVEGAME: "FO3S" (first 4 bytes of "FO3SAVEGAME" magic)
        if (header[0] == 'F' && header[1] == 'O' && header[2] == '3' && header[3] == 'S')
        {
            return AnalysisFileType.SaveFile;
        }

        return AnalysisFileType.Unknown;
    }

    /// <summary>
    ///     Checks if a file path has a supported extension for analysis.
    /// </summary>
    public static bool IsSupportedExtension(string filePath)
    {
        return filePath.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase) ||
               filePath.EndsWith(".esm", StringComparison.OrdinalIgnoreCase) ||
               filePath.EndsWith(".esp", StringComparison.OrdinalIgnoreCase) ||
               filePath.EndsWith(".fxs", StringComparison.OrdinalIgnoreCase) ||
               filePath.EndsWith(".fos", StringComparison.OrdinalIgnoreCase) ||
               // A J2ME Travels title IS its JAR: one PKZIP that constitutes the whole install.
               filePath.EndsWith(".jar", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Whether <paramref name="path" /> names something the analysis pipeline can open: a
    ///     supported single file, or a DIRECTORY — the classic pre-plugin-era games ship no single
    ///     plugin, so an install folder is a legitimate source.
    ///     <para>
    ///         Deliberately cheap, because this runs on every keystroke in the path box: it checks
    ///         existence and extension only and does NOT probe install markers. A directory that
    ///         holds no recognizable game is reported by the analysis run itself.
    ///     </para>
    /// </summary>
    public static bool IsSupportedSource(string? path)
    {
        return !string.IsNullOrEmpty(path) &&
               (Directory.Exists(path) || (File.Exists(path) && IsSupportedExtension(path)));
    }
}
