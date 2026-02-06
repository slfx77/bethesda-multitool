namespace FalloutXbox360Utils.Core;

/// <summary>
///     Detects file types by examining magic bytes at the start of a file.
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
            return AnalysisFileType.Unknown;
        }

        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            Span<byte> header = stackalloc byte[4];
            if (fs.Read(header) < 4)
            {
                return AnalysisFileType.Unknown;
            }

            return DetectFromMagic(header);
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

        // Xbox 360 ESM (big-endian): "4SET" (0x34 0x53 0x45 0x54)
        if (header[0] == '4' && header[1] == 'S' && header[2] == 'E' && header[3] == 'T')
        {
            return AnalysisFileType.EsmFile;
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
               filePath.EndsWith(".esp", StringComparison.OrdinalIgnoreCase);
    }
}
