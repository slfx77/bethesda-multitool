using System.CommandLine;
using System.Text;
using Spectre.Console;
using TextureAnalyzer.Parsers;
using XCompression;

namespace TextureAnalyzer.Commands;

/// <summary>
///     Commands for converting DDX to DDS format.
/// </summary>
internal static class ConvertCommands
{
    /// <summary>
    ///     Create the "convert" command for converting DDX to DDS.
    /// </summary>
    public static Command CreateConvertCommand()
    {
        var command = new Command("convert", "Convert a DDX file to DDS format (experimental 3XDR support)");
        var fileArg = new Argument<string>("file") { Description = "DDX file path" };
        var outputOpt = new Option<string?>("-o", "--output") { Description = "Output DDS file path" };
        var noSwizzleOpt = new Option<bool>("--no-swizzle") { Description = "Skip Morton untiling (for 3XDR testing)" };

        command.Arguments.Add(fileArg);
        command.Options.Add(outputOpt);
        command.Options.Add(noSwizzleOpt);

        command.SetAction(parseResult => Convert(
            parseResult.GetValue(fileArg)!,
            parseResult.GetValue(outputOpt),
            parseResult.GetValue(noSwizzleOpt)));

        return command;
    }

    private static void Convert(string path, string? outputPath, bool noSwizzle)
    {
        if (!File.Exists(path))
        {
            AnsiConsole.MarkupLine($"[red]File not found:[/] {path}");
            return;
        }

        var data = File.ReadAllBytes(path);
        var ddx = TextureParser.ParseDdx(data);

        if (ddx == null)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Not a valid DDX file");
            return;
        }

        // Default output path
        outputPath ??= Path.ChangeExtension(path, ".dds");

        AnsiConsole.MarkupLine($"Converting [cyan]{ddx.Magic}[/] file: {Path.GetFileName(path)}");
        AnsiConsole.MarkupLine($"  Dimensions: {ddx.Width} x {ddx.Height}");
        AnsiConsole.MarkupLine($"  Format: {ddx.FormatName}");

        // Extract and decompress
        var compressedData = new byte[ddx.DataSize];
        Array.Copy(data, 0x44, compressedData, 0, ddx.DataSize);

        var decompressed = DecompressAllChunks(compressedData, ddx);
        if (decompressed == null || decompressed.Length == 0)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Failed to decompress texture data");
            return;
        }

        AnsiConsole.MarkupLine($"  Decompressed: {decompressed.Length:N0} bytes");

        // Determine conversion path
        byte[] textureData;
        int mipLevels = 1;

        if (ddx.Is3XDR)
        {
            // 3XDR: Data is already linear, but still needs byte swapping (Xbox 360 is big-endian)
            AnsiConsole.MarkupLine("  [cyan]3XDR mode:[/] Data is linear (no untiling, byte swap only)");

            // Check if we have exactly mip0 or more
            var mip0Size = ddx.CalculateMip0Size();
            if (decompressed.Length == mip0Size)
            {
                textureData = SwapDxtEndianness(decompressed);
                AnsiConsole.MarkupLine("  Contains: Mip0 only");
            }
            else if (decompressed.Length > mip0Size)
            {
                // Has mips - count them
                textureData = SwapDxtEndianness(decompressed);
                mipLevels = CountMipLevels(ddx.Width, ddx.Height, decompressed.Length, ddx.BlockSize);
                AnsiConsole.MarkupLine($"  Contains: {mipLevels} mip level(s)");
            }
            else
            {
                AnsiConsole.MarkupLine($"[yellow]Warning:[/] Decompressed size ({decompressed.Length}) < mip0 ({mip0Size})");
                textureData = SwapDxtEndianness(decompressed);
            }
        }
        else
        {
            // 3XDO: Need to untile Morton swizzled data
            AnsiConsole.MarkupLine("  [yellow]3XDO mode:[/] Morton untiling required");

            var mip0Size = ddx.CalculateMip0Size();

            if (decompressed.Length == mip0Size * 2)
            {
                // Two chunks: main + atlas
                AnsiConsole.MarkupLine("  Contains: Main + Atlas (will extract main only for now)");

                // For simplicity, just untile the second half (main surface)
                var mainData = new byte[mip0Size];
                Array.Copy(decompressed, mip0Size, mainData, 0, mip0Size);

                if (!noSwizzle)
                {
                    textureData = UnswizzleDxt(mainData, ddx.Width, ddx.Height, ddx.BlockSize);
                }
                else
                {
                    textureData = mainData;
                }
            }
            else
            {
                if (!noSwizzle)
                {
                    textureData = UnswizzleDxt(decompressed, ddx.Width, ddx.Height, ddx.BlockSize);
                }
                else
                {
                    textureData = decompressed;
                }
            }
        }

        // Write DDS file
        WriteDds(outputPath, ddx, textureData, mipLevels);
        AnsiConsole.MarkupLine($"[green]Saved:[/] {outputPath}");
    }

    private static byte[]? DecompressAllChunks(byte[] compressedData, DdxInfo ddx)
    {
        var chunks = new List<byte[]>();
        var totalConsumed = 0;
        var estimatedSize = (uint)ddx.CalculateMip0Size();

        while (totalConsumed < compressedData.Length)
        {
            var remaining = compressedData.Length - totalConsumed;
            if (remaining < 10) break;

            try
            {
                var chunkData = new byte[remaining];
                Array.Copy(compressedData, totalConsumed, chunkData, 0, remaining);

                var decompressed = DecompressXMemCompress(chunkData, estimatedSize, out var consumed);

                if (decompressed == null || decompressed.Length == 0 || consumed == 0)
                    break;

                chunks.Add(decompressed);
                totalConsumed += consumed;
                estimatedSize = (uint)decompressed.Length;
            }
            catch
            {
                break;
            }
        }

        if (chunks.Count == 0) return null;

        var totalSize = chunks.Sum(c => c.Length);
        var result = new byte[totalSize];
        var offset = 0;

        foreach (var chunk in chunks)
        {
            Array.Copy(chunk, 0, result, offset, chunk.Length);
            offset += chunk.Length;
        }

        return result;
    }

    private static byte[]? DecompressXMemCompress(byte[] compressedData, uint estimatedSize, out int bytesConsumed)
    {
        bytesConsumed = 0;
        var sizesToTry = new[] { estimatedSize, estimatedSize * 2, estimatedSize * 4, 0x100000u };

        foreach (var size in sizesToTry)
        {
            try
            {
                using var context = new DecompressionContext();
                var decompressedData = new byte[size];
                var inputCount = compressedData.Length;
                var outputCount = (int)size;

                var result = context.Decompress(compressedData, 0, ref inputCount, decompressedData, 0, ref outputCount);

                if (result == ErrorCode.None && outputCount > 0)
                {
                    bytesConsumed = inputCount;
                    if (outputCount < decompressedData.Length)
                        Array.Resize(ref decompressedData, outputCount);
                    return decompressedData;
                }
            }
            catch { continue; }
        }

        return null;
    }

    private static int CountMipLevels(int width, int height, int dataSize, int blockSize)
    {
        var levels = 0;
        var total = 0;
        var w = width;
        var h = height;

        while (w >= 4 && h >= 4 && total < dataSize)
        {
            var blocksW = Math.Max(1, (w + 3) / 4);
            var blocksH = Math.Max(1, (h + 3) / 4);
            var mipSize = blocksW * blocksH * blockSize;

            if (total + mipSize > dataSize) break;

            total += mipSize;
            levels++;
            w /= 2;
            h /= 2;
        }

        return Math.Max(1, levels);
    }

    /// <summary>
    ///     Swap 16-bit words for Xbox 360 big-endian DXT data.
    ///     Used for 3XDR which is linear (no Morton untiling needed).
    /// </summary>
    private static byte[] SwapDxtEndianness(byte[] src)
    {
        var dst = new byte[src.Length];

        // Swap every 16-bit word (Xbox 360 DXT uses big-endian 16-bit values)
        for (var i = 0; i < src.Length - 1; i += 2)
        {
            dst[i] = src[i + 1];
            dst[i + 1] = src[i];
        }

        // Handle odd byte at end if present
        if ((src.Length & 1) == 1)
        {
            dst[src.Length - 1] = src[src.Length - 1];
        }

        return dst;
    }

    /// <summary>
    ///     Simple Morton (Z-order) untiling for DXT blocks.
    /// </summary>
    private static byte[] UnswizzleDxt(byte[] src, int width, int height, int blockSize)
    {
        var blocksX = Math.Max(1, (width + 3) / 4);
        var blocksY = Math.Max(1, (height + 3) / 4);
        var totalBlocks = blocksX * blocksY;
        var dst = new byte[totalBlocks * blockSize];

        // Find power-of-2 dimensions for Morton encoding
        var log2BPP = blockSize == 8 ? 3 : 4; // 8 or 16 bytes per block

        for (var y = 0; y < blocksY; y++)
        {
            for (var x = 0; x < blocksX; x++)
            {
                // Calculate Morton index
                var mortonIdx = GetMortonIndex((uint)x, (uint)y);

                // Linear destination index
                var linearIdx = y * blocksX + x;

                if (mortonIdx < totalBlocks && linearIdx < totalBlocks)
                {
                    var srcOffset = (int)(mortonIdx * blockSize);
                    var dstOffset = linearIdx * blockSize;

                    if (srcOffset + blockSize <= src.Length && dstOffset + blockSize <= dst.Length)
                    {
                        // Copy and swap 16-bit words (Xbox 360 big-endian DXT)
                        for (var i = 0; i < blockSize; i += 2)
                        {
                            dst[dstOffset + i] = src[srcOffset + i + 1];
                            dst[dstOffset + i + 1] = src[srcOffset + i];
                        }
                    }
                }
            }
        }

        return dst;
    }

    /// <summary>
    ///     Calculate Morton (Z-order) index from x,y coordinates.
    /// </summary>
    private static uint GetMortonIndex(uint x, uint y)
    {
        uint result = 0;
        for (var i = 0; i < 16; i++)
        {
            result |= ((x >> i) & 1) << (2 * i);
            result |= ((y >> i) & 1) << (2 * i + 1);
        }
        return result;
    }

    /// <summary>
    ///     Write a DDS file with the given texture data.
    /// </summary>
    private static void WriteDds(string path, DdxInfo ddx, byte[] data, int mipLevels)
    {
        using var writer = new BinaryWriter(File.Create(path));

        // DDS magic
        writer.Write(Encoding.ASCII.GetBytes("DDS "));

        // DDS_HEADER (124 bytes)
        writer.Write(124u);  // dwSize

        // dwFlags: DDSD_CAPS | DDSD_HEIGHT | DDSD_WIDTH | DDSD_PIXELFORMAT | DDSD_LINEARSIZE
        uint flags = 0x1 | 0x2 | 0x4 | 0x1000 | 0x80000;
        if (mipLevels > 1) flags |= 0x20000; // DDSD_MIPMAPCOUNT
        writer.Write(flags);

        writer.Write((uint)ddx.Height);  // dwHeight
        writer.Write((uint)ddx.Width);   // dwWidth
        writer.Write((uint)data.Length); // dwPitchOrLinearSize
        writer.Write(0u);                // dwDepth
        writer.Write((uint)mipLevels);   // dwMipMapCount

        // dwReserved1[11]
        for (var i = 0; i < 11; i++) writer.Write(0u);

        // DDS_PIXELFORMAT (32 bytes)
        writer.Write(32u); // dwSize
        writer.Write(0x4u); // dwFlags = DDPF_FOURCC
        writer.Write(Encoding.ASCII.GetBytes(ddx.ExpectedFourCC)); // dwFourCC
        writer.Write(0u); // dwRGBBitCount
        writer.Write(0u); // dwRBitMask
        writer.Write(0u); // dwGBitMask
        writer.Write(0u); // dwBBitMask
        writer.Write(0u); // dwABitMask

        // dwCaps
        uint caps = 0x1000; // DDSCAPS_TEXTURE
        if (mipLevels > 1) caps |= 0x8 | 0x400000; // DDSCAPS_COMPLEX | DDSCAPS_MIPMAP
        writer.Write(caps);

        writer.Write(0u); // dwCaps2
        writer.Write(0u); // dwCaps3
        writer.Write(0u); // dwCaps4
        writer.Write(0u); // dwReserved2

        // Texture data
        writer.Write(data);
    }
}
