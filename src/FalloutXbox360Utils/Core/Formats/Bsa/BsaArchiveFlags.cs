namespace FalloutXbox360Utils.Core.Formats.Bsa;

/// <summary>
///     BSA archive flags.
/// </summary>
[Flags]
public enum BsaArchiveFlags : uint
{
    None = 0,

    /// <summary>Include directory names in archive.</summary>
    IncludeDirectoryNames = 0x0001,

    /// <summary>Include file names in archive.</summary>
    IncludeFileNames = 0x0002,

    /// <summary>Files are compressed by default.</summary>
    CompressedArchive = 0x0004,

    /// <summary>Retain directory names (?).</summary>
    RetainDirectoryNames = 0x0008,

    /// <summary>Retain file names (?).</summary>
    RetainFileNames = 0x0010,

    /// <summary>Retain file name offsets (?).</summary>
    RetainFileNameOffsets = 0x0020,

    /// <summary>Xbox 360 archive - all numbers are big-endian.</summary>
    Xbox360Archive = 0x0040,

    /// <summary>Retain strings during startup (?).</summary>
    RetainStringsDuringStartup = 0x0080,

    /// <summary>Embed file names in file data blocks.</summary>
    EmbedFileNames = 0x0100,

    /// <summary>XMem codec compression (Xbox 360).</summary>
    XMemCodec = 0x0200
}
