// Copyright (c) 2026 BethesdaMultitool Contributors
// Licensed under the MIT License.
//
// BA2 format layout re-implemented from the public Bethesda Archive v2 format and the
// MIT-licensed fo76utils reference (libfo76utils/src/ba2file.{hpp,cpp},
// https://github.com/fo76utils/fo76utils). Not derived from any copyleft source.

namespace BethesdaMultitool.Core.Formats.Bsa.Ba2;

/// <summary>
///     BA2 archive content kind, taken from the 4-char tag that follows the version in the header.
/// </summary>
public enum Ba2HeaderType
{
    /// <summary>Tag not recognised.</summary>
    Unknown = 0,

    /// <summary>General files (<c>GNRL</c>): a flat entry list, each optionally zlib/LZ4 compressed.</summary>
    General,

    /// <summary>Textures (<c>DX10</c>): chunked DDS surfaces; a DDS header is rebuilt on extract.</summary>
    Texture,

    /// <summary>PlayStation GNF textures (<c>GNMF</c>): recognised but not extractable (PC FO4/FO76 never use it).</summary>
    Gnmf
}

/// <summary>Compression codec applied to an archive's compressed payloads.</summary>
public enum Ba2CompressionFormat
{
    /// <summary>zlib/DEFLATE — Fallout 4 and the great majority of Fallout 76 archives.</summary>
    Zip,

    /// <summary>Raw LZ4 block — the version-3 (Starfield-era) archives flagged for LZ4.</summary>
    Lz4
}
