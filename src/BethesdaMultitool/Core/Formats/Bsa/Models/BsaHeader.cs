using BethesdaMultitool.Core.Formats.Bsa.Parsing;

namespace BethesdaMultitool.Core.Formats.Bsa.Models;

/// <summary>
///     BSA archive header structure (36 bytes).
/// </summary>
public record BsaHeader
{
    /// <summary>Magic bytes "BSA\0".</summary>
    public required string FileId { get; init; }

    /// <summary>Version: 104 (0x68) for FO3/FNV/Skyrim, 105 (0x69) for SSE.</summary>
    public required uint Version { get; init; }

    /// <summary>Offset to folder records (always 36 for v104).</summary>
    public required uint FolderRecordOffset { get; init; }

    /// <summary>Archive flags - bit 7 indicates Xbox 360 origin (NOT byte order).</summary>
    public required BsaArchiveFlags ArchiveFlags { get; init; }

    /// <summary>Total number of folders in archive.</summary>
    public required uint FolderCount { get; init; }

    /// <summary>Total number of files in archive.</summary>
    public required uint FileCount { get; init; }

    /// <summary>Total length of all folder names.</summary>
    public required uint TotalFolderNameLength { get; init; }

    /// <summary>Total length of all file names.</summary>
    public required uint TotalFileNameLength { get; init; }

    /// <summary>Content type flags.</summary>
    public required BsaFileFlags FileFlags { get; init; }

    /// <summary>
    ///     True for the legacy Morrowind archive format (no "BSA\0" magic; <see cref="Version" /> is the
    ///     sentinel <c>0x100</c>). Morrowind archives are uncompressed with full-path file names and are
    ///     parsed by <see cref="MorrowindBsaParser" />. Lets display/extraction distinguish them from the
    ///     numerically-overlapping v103-105 versions.
    /// </summary>
    public bool IsMorrowind { get; init; }

    /// <summary>Whether this archive originated from Xbox 360 (flag only, data is still little-endian).</summary>
    public bool IsXbox360 => ArchiveFlags.HasFlag(BsaArchiveFlags.Xbox360Archive);

    /// <summary>Whether files are compressed by default.</summary>
    public bool DefaultCompressed => ArchiveFlags.HasFlag(BsaArchiveFlags.CompressedArchive);

    /// <summary>
    ///     Whether file names are embedded in file data blocks. The "Embed File Names" flag bit
    ///     (0x100) was introduced in v104 (FO3/FNV/Skyrim); Oblivion v103 archives sometimes set the
    ///     same bit with no such meaning (e.g. Oblivion - Meshes.bsa has flags 0x787), so honoring it
    ///     there would mis-offset every extracted file. Gate it to v104+.
    /// </summary>
    public bool EmbedFileNames => Version >= 104 && ArchiveFlags.HasFlag(BsaArchiveFlags.EmbedFileNames);

    /// <summary>
    ///     Whether compressed entries use the XMem/LZX codec instead of zlib. Not supported by the
    ///     extractor: no such archive exists in the sample corpus (all 87 are zlib, including every
    ///     Xbox 360 build), so the two parameters a decoder needs — the LZX window size and whether
    ///     an entry is one XMemCompress stream or several — cannot be validated against real data.
    ///     Detected only so extraction can fail with a named error rather than a confusing zlib one.
    ///     <para>
    ///         ⚠ Gated to v104+ for exactly the reason <see cref="EmbedFileNames" /> above is: the
    ///         0x200 bit carries no such meaning in Oblivion's v103 archives, and they set it —
    ///         <c>Oblivion - Meshes.bsa</c> has flags <c>0x787</c>, which includes 0x200. Without
    ///         the gate, every compressed Oblivion entry threw "uses the XMem/LZX codec" and the
    ///         whole Oblivion mesh corpus became unreadable. Caught by the Bucket-B sweep
    ///         (<c>NifTextureEffectRetailTests</c>, <c>OblivionHavokCollisionIntegrationTests</c>,
    ///         <c>OblivionNifBrowserTextureIntegrationTests</c>), which is why that gate exists.
    ///     </para>
    /// </summary>
    public bool UsesXMemCodec => Version >= 104 && ArchiveFlags.HasFlag(BsaArchiveFlags.XMemCodec);
}
