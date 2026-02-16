using System.Buffers.Binary;

namespace FalloutXbox360Utils.Core.VersionTracking.Extraction;

/// <summary>
///     Reads PE header TimeDateStamp from .exe files.
///     Works with standard PE files (both x86 and Xbox 360 executables).
/// </summary>
public static class PeTimestampReader
{
    /// <summary>
    ///     Reads the TimeDateStamp from a PE file's COFF header.
    ///     PE format: DOS header at offset 0, e_lfanew at 0x3C points to PE signature,
    ///     followed by COFF header with TimeDateStamp at offset +8.
    /// </summary>
    /// <returns>The timestamp as a DateTimeOffset, or null if the file is not a valid PE.</returns>
    public static DateTimeOffset? ReadBuildDate(string exePath)
    {
        var timestamp = ReadTimestamp(exePath);
        if (timestamp == null || timestamp == 0)
        {
            return null;
        }

        return DateTimeOffset.FromUnixTimeSeconds(timestamp.Value);
    }

    /// <summary>
    ///     Reads the raw TimeDateStamp uint from a PE file.
    /// </summary>
    public static uint? ReadTimestamp(string exePath)
    {
        try
        {
            using var fs = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (fs.Length < 0x40)
            {
                return null;
            }

            var buffer = new byte[4];

            // Check DOS magic: 'MZ'
            fs.Position = 0;
            if (fs.ReadByte() != 'M' || fs.ReadByte() != 'Z')
            {
                return null;
            }

            // Read e_lfanew (offset to PE signature) at 0x3C
            fs.Position = 0x3C;
            if (fs.Read(buffer, 0, 4) != 4)
            {
                return null;
            }

            var peOffset = BinaryPrimitives.ReadInt32LittleEndian(buffer);
            if (peOffset <= 0 || peOffset + 8 > fs.Length)
            {
                return null;
            }

            // Check PE signature: 'PE\0\0'
            fs.Position = peOffset;
            if (fs.ReadByte() != 'P' || fs.ReadByte() != 'E' ||
                fs.ReadByte() != 0 || fs.ReadByte() != 0)
            {
                return null;
            }

            // COFF header: Machine(2) + NumberOfSections(2) + TimeDateStamp(4)
            // Skip Machine and NumberOfSections
            fs.Position = peOffset + 4 + 4; // PE sig(4) + Machine(2) + NumSections(2)
            if (fs.Read(buffer, 0, 4) != 4)
            {
                return null;
            }

            return BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
