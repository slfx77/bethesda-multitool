// Copyright (c) 2026 FalloutXbox360Utils Contributors
// Licensed under the MIT License.

namespace FalloutXbox360Utils.Core.Formats.Bsa.Ba2;

/// <summary>
///     A single BA2 entry. BA2 has no folder tree — entries are a flat list keyed by hashed
///     directory/name plus an extension, with the human-readable path supplied by an optional name
///     table. A record is either a GNRL (general) file or a DX10 (texture) file; the
///     <see cref="Texture" /> payload is populated only for textures. Ported from Sharp.BSA.BA2's
///     BA2FileEntry / BA2TextureEntry.
/// </summary>
public sealed record Ba2FileRecord
{
    /// <summary>General vs texture entry.</summary>
    public required Ba2HeaderType Kind { get; init; }

    /// <summary>Zero-based index in the archive's file list (matches the name-table order).</summary>
    public required int Index { get; init; }

    /// <summary>Hash of the file name (without directory/extension).</summary>
    public required uint NameHash { get; init; }

    /// <summary>Four-char extension tag (trailing NULs trimmed).</summary>
    public required string Extension { get; init; }

    /// <summary>Hash of the directory portion of the path.</summary>
    public required uint DirHash { get; init; }

    /// <summary>Full virtual path from the name table, or null when the archive has none.</summary>
    public string? Name { get; set; }

    // --- GNRL fields (Kind == General) ---

    /// <summary>Entry flags (GNRL only; meaning archive-specific).</summary>
    public uint Flags { get; init; }

    /// <summary>Absolute data offset (GNRL only; textures use <see cref="Ba2TextureChunk.Offset" />).</summary>
    public ulong Offset { get; init; }

    /// <summary>Compressed size, or 0 when stored uncompressed (GNRL only).</summary>
    public uint PackedSize { get; init; }

    /// <summary>Decompressed size (GNRL only).</summary>
    public uint RealSize { get; init; }

    /// <summary>Alignment padding field (GNRL only; unused for extraction).</summary>
    public uint Align { get; init; }

    // --- DX10 payload (Kind == Texture) ---

    /// <summary>Texture surface metadata + chunks, populated only for DX10 entries.</summary>
    public Ba2TextureInfo? Texture { get; init; }

    /// <summary>Whether a GNRL entry's data is compressed (textures decide per-chunk).</summary>
    public bool Compressed => PackedSize != 0;

    /// <summary>
    ///     Best-effort virtual path: the name-table entry when present, otherwise the
    ///     hash-derived placeholder Sharp.BSA.BA2 uses (<c>{dirHash:X}_{nameHash:X}.{ext}</c>).
    /// </summary>
    public string FullPath
    {
        get
        {
            if (!string.IsNullOrEmpty(Name))
            {
                return Name;
            }

            var prefix = DirHash > 0 ? $"{DirHash:X}_" : string.Empty;
            return $"{prefix}{NameHash:X}.{Extension}";
        }
    }
}

/// <summary>DX10 texture surface metadata + its mip chunks.</summary>
public sealed record Ba2TextureInfo
{
    /// <summary>Unknown leading byte.</summary>
    public required byte Unknown { get; init; }

    /// <summary>Number of mip chunks.</summary>
    public required byte ChunkCount { get; init; }

    /// <summary>Per-chunk header length (informational).</summary>
    public required ushort ChunkHeaderLength { get; init; }

    /// <summary>Texture height in pixels.</summary>
    public required ushort Height { get; init; }

    /// <summary>Texture width in pixels.</summary>
    public required ushort Width { get; init; }

    /// <summary>Total mip count.</summary>
    public required byte MipCount { get; init; }

    /// <summary>DXGI format value (see <see cref="Ba2DxgiFormat" />).</summary>
    public required byte Format { get; init; }

    /// <summary>Non-zero when the texture is a cubemap.</summary>
    public required byte IsCubemap { get; init; }

    /// <summary>Tile mode; values other than 8 indicate an Xbox-tiled surface.</summary>
    public required byte TileMode { get; init; }

    /// <summary>The texture's mip chunks.</summary>
    public required List<Ba2TextureChunk> Chunks { get; init; }
}
