// Copyright (c) 2026 FalloutXbox360Utils Contributors
// Licensed under the MIT License.

namespace FalloutXbox360Utils.Core.Formats.Bsa.Ba2;

/// <summary>
///     BA2 archive content type (the 4-char tag in the header after the version). Ported from the
///     community Sharp.BSA.BA2 reference (BSA_Browser).
/// </summary>
public enum Ba2HeaderType
{
    /// <summary>Unrecognized tag.</summary>
    Unknown = 0,

    /// <summary>General files — flat entries, optionally zlib/LZ4 compressed (`GNRL`).</summary>
    General,

    /// <summary>Textures — chunked DDS surfaces, a DDS header is synthesized on extract (`DX10`).</summary>
    Texture,

    /// <summary>PlayStation GNF textures (`GNMF`). Parsed but not extractable here (PC FO4/FO76 don't use it).</summary>
    Gnmf
}

/// <summary>Compression codec used by the archive's compressed entries.</summary>
public enum Ba2CompressionFormat
{
    /// <summary>zlib/DEFLATE (FO4 and most FO76 archives).</summary>
    Zip,

    /// <summary>Raw LZ4 block (FO76 version-3 archives flagged with compression == 1).</summary>
    Lz4
}
