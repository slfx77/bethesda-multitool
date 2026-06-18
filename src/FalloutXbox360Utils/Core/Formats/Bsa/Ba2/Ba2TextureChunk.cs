// Copyright (c) 2026 FalloutXbox360Utils Contributors
// Licensed under the MIT License.

namespace FalloutXbox360Utils.Core.Formats.Bsa.Ba2;

/// <summary>
///     One mip-chunk of a DX10 texture entry. A texture's surface data is split into chunks (each
///     covering a contiguous mip range), every chunk independently compressed. Ported from
///     Sharp.BSA.BA2's BA2TextureChunk.
/// </summary>
public sealed record Ba2TextureChunk
{
    /// <summary>Absolute offset of the chunk's data in the archive.</summary>
    public required ulong Offset { get; init; }

    /// <summary>Compressed (packed) size, or 0 when the chunk is stored uncompressed.</summary>
    public required uint PackedSize { get; init; }

    /// <summary>Decompressed (full) size of the chunk's surface data.</summary>
    public required uint FullSize { get; init; }

    /// <summary>First mip level covered by this chunk.</summary>
    public required ushort StartMip { get; init; }

    /// <summary>Last mip level covered by this chunk.</summary>
    public required ushort EndMip { get; init; }

    /// <summary>Alignment padding field (unused for extraction).</summary>
    public required uint Align { get; init; }

    /// <summary>Whether the chunk is compressed (packed size present).</summary>
    public bool Compressed => PackedSize != 0;

    /// <summary>Reads one chunk record from the current reader position.</summary>
    public static Ba2TextureChunk Read(BinaryReader reader) => new()
    {
        Offset = reader.ReadUInt64(),
        PackedSize = reader.ReadUInt32(),
        FullSize = reader.ReadUInt32(),
        StartMip = reader.ReadUInt16(),
        EndMip = reader.ReadUInt16(),
        Align = reader.ReadUInt32()
    };
}
