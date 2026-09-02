// Ported from OpenTESArena (MIT License), https://github.com/afritz1/OpenTESArena
//   OpenTESArena/src/Assets/RMDFile.cpp / RMDFile.h and Compression::decodeRLEWords. License
//   texts are collected centrally in THIRD_PARTY_LICENSES.

using System.Buffers.Binary;
using BethesdaMultitool.Core.Compression;

namespace BethesdaMultitool.Core.Formats.Arena;

/// <summary>
///     An Arena <c>.RMD</c> wilderness chunk: a fixed 64x64 block carrying the same three voxel
///     layers a <c>.MIF</c> level does (floors, walls, upper storeys), used to tile the overworld.
///     <para>
///         The first word is the uncompressed length in words. Zero means the file is stored raw
///         and must be exactly 24,576 bytes — those are RMD #001-#004, the uncompressed city
///         quarters the game shows while you are out in the wilderness. Any other value means the
///         rest of the file is word-oriented RLE.
///     </para>
///     <para>
///         Note the overlap in the raw case: the length word is NOT a header there, it is already
///         the first floor voxel. The scheme works only because that voxel is empty (id 0) in the
///         chunks stored this way, so layer data is read from offset 0 rather than from offset 2.
///     </para>
/// </summary>
internal sealed class ArenaRmdFile
{
    /// <summary>Width of a wilderness chunk in voxels.</summary>
    public const int Width = 64;

    /// <summary>Depth of a wilderness chunk in voxels.</summary>
    public const int Depth = 64;

    /// <summary>Voxels in one layer.</summary>
    public const int VoxelsPerLayer = Width * Depth;

    /// <summary>Bytes in one layer — two per voxel.</summary>
    public const int BytesPerLayer = VoxelsPerLayer * 2;

    /// <summary>Exact byte length of an uncompressed .RMD (three layers).</summary>
    public const int UncompressedFileLength = BytesPerLayer * 3;

    private ArenaRmdFile(ushort[] floor, ushort[] map1, ushort[] map2, bool wasCompressed)
    {
        Floor = floor;
        Map1 = map1;
        Map2 = map2;
        WasCompressed = wasCompressed;
    }

    /// <summary>Floor voxel ids, row-major (<c>index = x + (z * Width)</c>).</summary>
    public ushort[] Floor { get; }

    /// <summary>Wall/ground-storey voxel ids, row-major.</summary>
    public ushort[] Map1 { get; }

    /// <summary>Upper-storey voxel ids, row-major.</summary>
    public ushort[] Map2 { get; }

    /// <summary>False for the four raw city-quarter chunks, true for every other .RMD.</summary>
    public bool WasCompressed { get; }

    /// <summary>Parses a .RMD chunk.</summary>
    public static ArenaRmdFile Parse(ReadOnlySpan<byte> bytes, string name)
    {
        if (bytes.Length < 2)
        {
            throw new InvalidDataException($"'{name}' is too small to be an Arena .RMD ({bytes.Length} bytes).");
        }

        var uncompressedWords = BinaryPrimitives.ReadUInt16LittleEndian(bytes);
        if (uncompressedWords == 0)
        {
            if (bytes.Length != UncompressedFileLength)
            {
                throw new InvalidDataException(
                    $"'{name}' declares itself uncompressed, so it must be exactly " +
                    $"{UncompressedFileLength} bytes; got {bytes.Length}.");
            }

            return new ArenaRmdFile(
                ReadLayer(bytes[..BytesPerLayer]),
                ReadLayer(bytes.Slice(BytesPerLayer, BytesPerLayer)),
                ReadLayer(bytes.Slice(BytesPerLayer * 2, BytesPerLayer)),
                wasCompressed: false);
        }

        var decompressed = RleCodec.DecompressWords(bytes[2..], uncompressedWords);
        if (decompressed.Length < UncompressedFileLength)
        {
            throw new InvalidDataException(
                $"'{name}' decompressed to {decompressed.Length} bytes; a .RMD holds " +
                $"{UncompressedFileLength} (three {Width}x{Depth} layers).");
        }

        return new ArenaRmdFile(
            ReadLayer(decompressed.AsSpan(0, BytesPerLayer)),
            ReadLayer(decompressed.AsSpan(BytesPerLayer, BytesPerLayer)),
            ReadLayer(decompressed.AsSpan(BytesPerLayer * 2, BytesPerLayer)),
            wasCompressed: true);
    }

    private static ushort[] ReadLayer(ReadOnlySpan<byte> bytes)
    {
        var layer = new ushort[VoxelsPerLayer];
        for (var i = 0; i < layer.Length; i++)
        {
            layer[i] = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(i * 2)..]);
        }

        return layer;
    }
}
