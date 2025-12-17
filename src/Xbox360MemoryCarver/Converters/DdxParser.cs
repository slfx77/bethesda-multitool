using Xbox360MemoryCarver.Compression;

namespace Xbox360MemoryCarver.Converters;

/// <summary>
/// DDX (3XDO/3XDR) to DDS converter with Xbox 360 tiling/untile support.
/// Ported from kran27/DDXConv for 64-bit managed implementation.
/// </summary>
public class DdxParser
{
    private readonly bool _verbose;
    private ConversionOptions _options = new();

    private const uint Magic3Xdo = 0x4F445833; // "3XDO"
    private const uint Magic3Xdr = 0x52445833; // "3XDR"

    public DdxParser(bool verbose = false)
    {
        _verbose = verbose;
    }

    /// <summary>
    /// Check if data appears to be a valid DDX file.
    /// </summary>
    public static bool IsDdxFile(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4) return false;
        uint magic = BitConverter.ToUInt32(data);
        return magic == Magic3Xdo || magic == Magic3Xdr;
    }

    /// <summary>
    /// Check if DDX data uses the 3XDR format.
    /// </summary>
    public static bool Is3XdrFormat(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4) return false;
        return BitConverter.ToUInt32(data) == Magic3Xdr;
    }

    /// <summary>
    /// Convert a DDX file to DDS format.
    /// </summary>
    public void ConvertDdxToDds(string inputPath, string outputPath, ConversionOptions options)
    {
        using var reader = new BinaryReader(File.OpenRead(inputPath));
        var magic = reader.ReadUInt32();

        if (magic == Magic3Xdr)
            throw new NotSupportedException("3XDR format files do not yet convert properly.");
        if (magic != Magic3Xdo)
            throw new InvalidDataException($"Unknown DDX magic: 0x{magic:X8}.");

        ConvertDdxToDdsCore(reader, outputPath, options);
    }

    /// <summary>
    /// Convert DDX data from memory to DDS format.
    /// </summary>
    public byte[]? ConvertDdxToDdsMemory(byte[] ddxData, ConversionOptions options)
    {
        using var ms = new MemoryStream(ddxData);
        using var reader = new BinaryReader(ms);

        var magic = reader.ReadUInt32();
        if (magic == Magic3Xdr)
            throw new NotSupportedException("3XDR format files do not yet convert properly.");
        if (magic != Magic3Xdo)
            throw new InvalidDataException($"Unknown DDX magic: 0x{magic:X8}.");

        return ConvertDdxToDdsMemoryCore(reader, options);
    }

    private void ConvertDdxToDdsCore(BinaryReader reader, string outputPath, ConversionOptions options)
    {
        _options = options;

        // Skip priority bytes
        reader.ReadByte(); // priorityL
        reader.ReadByte(); // priorityC
        reader.ReadByte(); // priorityH

        var version = reader.ReadUInt16();
        if (version < 3)
            throw new NotSupportedException($"DDX version {version} is not supported. Need version >= 3");

        // Go back 1 byte to read texture header starting at 0x08
        reader.BaseStream.Seek(-1, SeekOrigin.Current);
        var textureHeader = reader.ReadBytes(52);
        reader.ReadBytes(8); // Skip to 0x44

        var texture = ParseD3DTextureHeader(textureHeader, out var width, out var height);
        Log($"Dimensions from D3D texture header: {width}x{height}");

        // Read remaining data
        var remainingBytes = (int)(reader.BaseStream.Length - reader.BaseStream.Position);
        var compressedData = reader.ReadBytes(remainingBytes);

        // Process texture data
        var linearData = ProcessTextureData(compressedData, texture, width, height, outputPath);

        // Write output DDS
        WriteDdsFile(outputPath, texture, linearData);
    }

    private byte[]? ConvertDdxToDdsMemoryCore(BinaryReader reader, ConversionOptions options)
    {
        _options = options;

        // Skip priority bytes
        reader.ReadByte();
        reader.ReadByte();
        reader.ReadByte();

        var version = reader.ReadUInt16();
        if (version < 3) return null;

        reader.BaseStream.Seek(-1, SeekOrigin.Current);
        var textureHeader = reader.ReadBytes(52);
        reader.ReadBytes(8);

        var texture = ParseD3DTextureHeader(textureHeader, out var width, out var height);

        var remainingBytes = (int)(reader.BaseStream.Length - reader.BaseStream.Position);
        var compressedData = reader.ReadBytes(remainingBytes);

        var linearData = ProcessTextureData(compressedData, texture, width, height, null);

        return WriteDdsToMemory(texture, linearData);
    }

    private byte[] ProcessTextureData(byte[] rawData, D3DTextureInfo texture, ushort width, ushort height, string? outputPath)
    {
        var mainSurfaceSize = (uint)CalculateMipSize(width, height, texture.ActualFormat);
        var atlasSize = mainSurfaceSize;

        // Detect if data is compressed
        bool isCompressed = rawData.Length != mainSurfaceSize && rawData.Length != mainSurfaceSize * 2;

        if (isCompressed)
        {
            Log($"Attempting decompression: input={rawData.Length}, expected={mainSurfaceSize}");

            if (TryDecompressAllChunks(rawData, atlasSize, out var decompressed))
            {
                rawData = decompressed;
                Log($"Decompressed to {decompressed.Length} bytes");
            }
            else
            {
                Log("Decompression failed, using raw data");
            }

            if (_options.SaveRaw && outputPath != null)
            {
                var rawPath = outputPath.Replace(".dds", "_raw.bin");
                File.WriteAllBytes(rawPath, rawData);
            }
        }

        // Detect chunk layout
        bool isTwoChunk = false;
        uint chunk1Size = 0, chunk2Size = 0;

        if (rawData.Length == atlasSize * 2)
        {
            isTwoChunk = true;
            chunk1Size = atlasSize;
            chunk2Size = atlasSize;
        }
        else if (rawData.Length > mainSurfaceSize)
        {
            isTwoChunk = true;
            chunk1Size = (uint)(rawData.Length - mainSurfaceSize);
            chunk2Size = mainSurfaceSize;
            Log($"Two-chunk format: atlas={chunk1Size} + main={chunk2Size}");
        }

        if (isTwoChunk)
        {
            return ProcessTwoChunkFormat(rawData, texture, width, height, chunk1Size, chunk2Size, outputPath);
        }
        else
        {
            return ProcessSingleChunkFormat(rawData, texture, width, height);
        }
    }

    private byte[] ProcessTwoChunkFormat(byte[] data, D3DTextureInfo texture, int width, int height,
        uint chunk1Size, uint chunk2Size, string? outputPath)
    {
        var chunk1 = new byte[chunk1Size];
        var chunk2 = new byte[chunk2Size];
        Array.Copy(data, 0, chunk1, 0, chunk1Size);
        Array.Copy(data, chunk1Size, chunk2, 0, chunk2Size);

        // Determine atlas dimensions
        int blockSize = GetBlockSize(texture.ActualFormat);
        int atlasWidth, atlasHeight;

        if (width <= 256 && height <= 256)
        {
            atlasWidth = width;
            atlasHeight = height;
        }
        else
        {
            var chunk1Blocks = (int)chunk1Size / blockSize;
            var widthBlocksBase = Math.Max(1, width / 4);
            var chosenWidthBlocks = widthBlocksBase;

            for (var wb = widthBlocksBase; wb <= 128; wb++)
            {
                if (chunk1Blocks % wb != 0) continue;
                var hb = chunk1Blocks / wb;
                var candidateW = wb * 4;
                var candidateH = hb * 4;
                if (candidateW >= width && candidateH >= height && candidateW <= 2048 && candidateH <= 2048)
                {
                    chosenWidthBlocks = wb;
                    break;
                }
            }

            atlasWidth = chosenWidthBlocks * 4;
            atlasHeight = chunk1Blocks / chosenWidthBlocks * 4;
        }

        Log($"Untiling chunk1 ({chunk1Size} bytes) as atlas {atlasWidth}x{atlasHeight}");
        Log($"Untiling chunk2 ({chunk2Size} bytes) as main {width}x{height}");

        var untiledAtlas = _options.NoUntileAtlas ? chunk1 : UnswizzleDxtTexture(chunk1, atlasWidth, atlasHeight, texture.ActualFormat);
        var untiledMain = UnswizzleDxtTexture(chunk2, width, height, texture.ActualFormat);

        if (_options.SaveAtlas && outputPath != null)
        {
            var atlasPath = outputPath.Replace(".dds", "_atlas.dds");
            var atlasTexture = new D3DTextureInfo
            {
                Width = (uint)atlasWidth,
                Height = (uint)atlasHeight,
                Format = texture.Format,
                ActualFormat = texture.ActualFormat,
                DataFormat = texture.DataFormat,
                MipLevels = 1
            };
            WriteDdsFile(atlasPath, atlasTexture, untiledAtlas);
        }

        // Extract mips from atlas
        var mips = UnpackMipAtlas(untiledAtlas, atlasWidth, atlasHeight, texture.ActualFormat, width, height);

        var result = new byte[untiledMain.Length + mips.Length];
        Array.Copy(untiledMain, 0, result, 0, untiledMain.Length);
        Array.Copy(mips, 0, result, untiledMain.Length, mips.Length);

        return result;
    }

    private byte[] ProcessSingleChunkFormat(byte[] data, D3DTextureInfo texture, int width, int height)
    {
        var mainSurfaceSize = CalculateMipSize(width, height, texture.ActualFormat);

        if (data.Length <= mainSurfaceSize)
        {
            texture.MipLevels = 1;
            return UnswizzleDxtTexture(data, width, height, texture.ActualFormat);
        }

        // Has extra data - try to process as main + sequential mips
        var mainTiled = new byte[mainSurfaceSize];
        Array.Copy(data, 0, mainTiled, 0, mainSurfaceSize);
        var mainUntiled = UnswizzleDxtTexture(mainTiled, width, height, texture.ActualFormat);

        var remaining = data.Length - mainSurfaceSize;
        var remainingData = new byte[remaining];
        Array.Copy(data, mainSurfaceSize, remainingData, 0, remaining);

        var mipDataList = new List<byte[]> { mainUntiled };
        var processedMipData = 0;
        var mipWidth = width / 2;
        var mipHeight = height / 2;
        var mipLevels = 1;

        while (mipWidth >= 4 && mipHeight >= 4 && processedMipData < remaining)
        {
            var mipSize = CalculateMipSize(mipWidth, mipHeight, texture.ActualFormat);
            if (processedMipData + mipSize > remaining) break;

            var mipTiled = new byte[mipSize];
            Array.Copy(remainingData, processedMipData, mipTiled, 0, mipSize);
            var mipUntiled = UnswizzleDxtTexture(mipTiled, mipWidth, mipHeight, texture.ActualFormat);
            mipDataList.Add(mipUntiled);

            processedMipData += mipSize;
            mipLevels++;
            mipWidth /= 2;
            mipHeight /= 2;
        }

        texture.MipLevels = (uint)mipLevels;

        var totalSize = mipDataList.Sum(m => m.Length);
        var result = new byte[totalSize];
        var offset = 0;
        foreach (var mip in mipDataList)
        {
            Array.Copy(mip, 0, result, offset, mip.Length);
            offset += mip.Length;
        }

        return result;
    }

    private bool TryDecompressAllChunks(byte[] compressedData, uint chunkSize, out byte[] decompressed)
    {
        decompressed = Array.Empty<byte>();

        try
        {
            var chunks = new List<byte[]>();
            var totalConsumed = 0;

            // First chunk
            var firstChunk = XCompression.DecompressWithBytesConsumed(compressedData, (int)chunkSize, out var consumed);
            if (firstChunk == null) return false;

            chunks.Add(firstChunk);
            totalConsumed = consumed > 0 ? consumed : compressedData.Length;

            // Additional chunks
            while (totalConsumed < compressedData.Length)
            {
                var remaining = compressedData.Length - totalConsumed;
                if (remaining < 10) break;

                var slice = new byte[remaining];
                Array.Copy(compressedData, totalConsumed, slice, 0, remaining);

                var chunk = XCompression.DecompressWithBytesConsumed(slice, (int)chunkSize, out consumed);
                if (chunk == null || consumed == 0) break;

                chunks.Add(chunk);
                totalConsumed += consumed;
            }

            // Combine chunks
            var totalLength = chunks.Sum(c => c.Length);
            decompressed = new byte[totalLength];
            var offset = 0;
            foreach (var chunk in chunks)
            {
                Array.Copy(chunk, 0, decompressed, offset, chunk.Length);
                offset += chunk.Length;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    #region Header Parsing

    private D3DTextureInfo ParseD3DTextureHeader(byte[] header, out ushort width, out ushort height)
    {
        // Dimensions stored big-endian at dword5 (offset 36)
        var dword5Bytes = new byte[4];
        Array.Copy(header, 36, dword5Bytes, 0, 4);
        Array.Reverse(dword5Bytes);
        var dword5 = BitConverter.ToUInt32(dword5Bytes, 0);

        width = (ushort)((dword5 & 0x1FFF) + 1);
        height = (ushort)(((dword5 >> 13) & 0x1FFF) + 1);

        Log($"Parsed from Format dword_5: 0x{dword5:X8} -> {width}x{height}");

        var info = new D3DTextureInfo
        {
            Width = width,
            Height = height
        };

        // Read format dwords (little-endian)
        var formatDwords = new uint[6];
        for (var i = 0; i < 6; i++)
            formatDwords[i] = BitConverter.ToUInt32(header, 16 + i * 4);

        var dword0 = formatDwords[0];
        var dword3 = formatDwords[3];
        var dword4 = formatDwords[4];

        info.DataFormat = dword3 & 0xFF;
        var actualFormat = (dword4 >> 24) & 0xFF;

        Log($"Format detection: DataFormat=0x{info.DataFormat:X2}, ActualFormat=0x{actualFormat:X2}");

        info.Endian = (dword0 >> 26) & 0x3;
        info.Tiled = ((dword0 >> 19) & 1) != 0;
        info.ActualFormat = actualFormat != 0 ? actualFormat : info.DataFormat;
        info.Format = GetDxgiFormat(info.ActualFormat);
        info.MipLevels = CalculateMipLevels(info.Width, info.Height);
        info.MainDataSize = CalculateMainDataSize(info.Width, info.Height, info.ActualFormat, info.MipLevels);

        return info;
    }

    #endregion

    #region Format Helpers

    private static uint GetDxgiFormat(uint gpuFormat)
    {
        return gpuFormat switch
        {
            0x52 => 0x31545844, // DXT1
            0x53 => 0x33545844, // DXT3
            0x54 => 0x35545844, // DXT5
            0x71 => 0x32495441, // ATI2 (BC5)
            0x7B => 0x31495441, // ATI1 (BC4)
            0x82 => 0x31545844, // DXT1 variant
            0x86 => 0x31545844, // DXT1 variant
            0x88 => 0x35545844, // DXT5 variant
            0x12 => 0x31545844, // GPUTEXTUREFORMAT_DXT1
            0x13 => 0x33545844, // GPUTEXTUREFORMAT_DXT2/3
            0x14 => 0x35545844, // GPUTEXTUREFORMAT_DXT4/5
            0x06 => 0x18280046, // A8R8G8B8
            0x04 => 0x28280044, // R5G6B5
            _ => 0x31545844
        };
    }

    private static int GetBlockSize(uint format)
    {
        return format switch
        {
            0x52 or 0x7B or 0x82 or 0x86 or 0x12 => 8,  // DXT1, BC4
            _ => 16  // DXT3, DXT5, BC5
        };
    }

    private static uint CalculateMipLevels(uint width, uint height)
    {
        uint levels = 1;
        var w = width;
        var h = height;
        while (w > 1 || h > 1)
        {
            w = Math.Max(1, w / 2);
            h = Math.Max(1, h / 2);
            levels++;
        }
        return levels;
    }

    private static uint CalculateMainDataSize(uint width, uint height, uint format, uint mipLevels)
    {
        uint totalSize = 0;
        var w = width;
        var h = height;
        for (var i = 0; i < mipLevels; i++)
        {
            totalSize += CalculateMipSize(w, h, format);
            w = Math.Max(1, w / 2);
            h = Math.Max(1, h / 2);
        }
        return totalSize;
    }

    private static uint CalculateMipSize(uint width, uint height, uint format)
    {
        return format switch
        {
            0x52 or 0x7B or 0x82 or 0x86 or 0x12 =>
                Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4) * 8,
            0x53 or 0x54 or 0x71 or 0x88 or 0x13 or 0x14 =>
                Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4) * 16,
            0x06 => width * height * 4,
            0x04 => width * height * 2,
            _ => width * height * 4
        };
    }

    private static int CalculateMipSize(int width, int height, uint format) =>
        (int)CalculateMipSize((uint)width, (uint)height, format);

    #endregion

    #region Unswizzle

    private byte[] UnswizzleDxtTexture(byte[] src, int width, int height, uint format)
    {
        int blockSize = GetBlockSize(format);
        var blocksWide = width / 4;
        var blocksHigh = height / 4;
        var dst = new byte[src.Length];

        // log2 of bytes per pixel for DXT blocks
        var log2Bpp = (uint)(blockSize / 4 + ((blockSize / 2) >> (blockSize / 4)));

        for (var y = 0; y < blocksHigh; y++)
        {
            var inputRowOffset = TiledOffset2DRow((uint)y, (uint)blocksWide, log2Bpp);

            for (var x = 0; x < blocksWide; x++)
            {
                var inputOffset = TiledOffset2DColumn((uint)x, (uint)y, log2Bpp, inputRowOffset);
                inputOffset >>= (int)log2Bpp;

                var srcOffset = (int)(inputOffset * blockSize);
                var dstOffset = (y * blocksWide + x) * blockSize;

                if (srcOffset + blockSize <= src.Length && dstOffset + blockSize <= dst.Length)
                {
                    if (!_options.SkipEndianSwap)
                    {
                        // Xbox 360 is big-endian, swap bytes in 16-bit words
                        for (var i = 0; i < blockSize; i += 2)
                        {
                            dst[dstOffset + i] = src[srcOffset + i + 1];
                            dst[dstOffset + i + 1] = src[srcOffset + i];
                        }
                    }
                    else
                    {
                        Array.Copy(src, srcOffset, dst, dstOffset, blockSize);
                    }
                }
            }
        }

        return dst;
    }

    // Xbox 360 tiling functions from Xenia emulator
    private static uint TiledOffset2DRow(uint y, uint width, uint log2Bpp)
    {
        var macro = (y / 32 * (width / 32)) << (int)(log2Bpp + 7);
        var micro = (y & 6) << 2 << (int)log2Bpp;
        return macro + ((micro & ~0xFu) << 1) + (micro & 0xF) +
               ((y & 8) << (int)(3 + log2Bpp)) + ((y & 1) << 4);
    }

    private static uint TiledOffset2DColumn(uint x, uint y, uint log2Bpp, uint baseOffset)
    {
        var macro = (x / 32) << (int)(log2Bpp + 7);
        var micro = (x & 7) << (int)log2Bpp;
        var offset = baseOffset + macro + ((micro & ~0xFu) << 1) + (micro & 0xF);
        return ((offset & ~0x1FFu) << 3) + ((offset & 0x1C0) << 2) + (offset & 0x3F) +
               ((y & 16) << 7) + (((((y & 8) >> 2) + (x >> 3)) & 3) << 6);
    }

    #endregion

    #region Mip Atlas

    private byte[] UnpackMipAtlas(byte[] atlasData, int atlasWidth, int atlasHeight, uint format, int mainWidth, int mainHeight)
    {
        int blockSize = GetBlockSize(format);
        var atlasWidthInBlocks = atlasWidth / 4;

        // Calculate total mip data size (excluding main surface)
        var mipCount = CalculateMipLevels((uint)mainWidth, (uint)mainHeight);
        var mainSize = CalculateMipSize(mainWidth, mainHeight, format);
        var totalMipSize = (int)(CalculateMainDataSize((uint)mainWidth, (uint)mainHeight, format, mipCount) - mainSize);

        if (totalMipSize <= 0)
            return Array.Empty<byte>();

        var output = new byte[totalMipSize];
        var outputOffset = 0;

        // Default mip positions for common atlas sizes
        var mipPositions = GetMipPositions(atlasWidth, atlasHeight, mainWidth, mainHeight);

        foreach (var (mipX, mipY, mipW, mipH) in mipPositions)
        {
            var mipWidthBlocks = mipW;
            var mipHeightBlocks = mipH;

            for (var by = 0; by < mipHeightBlocks; by++)
            {
                for (var bx = 0; bx < mipWidthBlocks; bx++)
                {
                    var srcBlockX = mipX + bx;
                    var srcBlockY = mipY + by;
                    var srcOffset = (srcBlockY * atlasWidthInBlocks + srcBlockX) * blockSize;

                    if (srcOffset + blockSize <= atlasData.Length && outputOffset + blockSize <= output.Length)
                    {
                        Array.Copy(atlasData, srcOffset, output, outputOffset, blockSize);
                    }
                    outputOffset += blockSize;
                }
            }

            if (outputOffset >= totalMipSize)
                break;
        }

        // Trim to actual size
        if (outputOffset < output.Length)
        {
            var trimmed = new byte[outputOffset];
            Array.Copy(output, 0, trimmed, 0, outputOffset);
            return trimmed;
        }

        return output;
    }

    private static (int x, int y, int w, int h)[] GetMipPositions(int atlasWidth, int atlasHeight, int mainWidth, int mainHeight)
    {
        // Standard mip layouts for common texture sizes
        if (atlasWidth == 256 && atlasHeight == 192)
        {
            // 128x128 texture
            return new[]
            {
                (0, 0, 16, 16),   // Mip 0: 64x64
                (32, 0, 8, 8),    // Mip 1: 32x32
                (4, 32, 4, 4),    // Mip 2: 16x16
                (2, 32, 2, 2),    // Mip 3: 8x8
                (1, 32, 1, 1),    // Mip 4: 4x4
            };
        }

        if (atlasWidth == 256 && atlasHeight == 256)
        {
            // 128x128 texture
            return new[]
            {
                (32, 0, 16, 16),  // Mip 1: 64x64
                (0, 32, 8, 8),    // Mip 2: 32x32
                (36, 32, 4, 4),   // Mip 3: 16x16
                (34, 32, 2, 2),   // Mip 4: 8x8
                (33, 32, 1, 1),   // Mip 5: 4x4
            };
        }

        // Dynamic layout: pack mips starting from half-size
        var positions = new List<(int, int, int, int)>();
        int mipW = mainWidth / 2, mipH = mainHeight / 2;
        int curX = 0, curY = 0, rowH = 0;
        int atlasWBlocks = atlasWidth / 4, atlasHBlocks = atlasHeight / 4;

        while (mipW >= 4 && mipH >= 4)
        {
            int mbW = Math.Max(1, (mipW + 3) / 4);
            int mbH = Math.Max(1, (mipH + 3) / 4);

            if (curX + mbW > atlasWBlocks)
            {
                curX = 0;
                curY += rowH;
                rowH = 0;
            }

            if (curY + mbH > atlasHBlocks) break;

            positions.Add((curX, curY, mbW, mbH));
            curX += mbW;
            rowH = Math.Max(rowH, mbH);

            mipW /= 2;
            mipH /= 2;
        }

        return positions.ToArray();
    }

    #endregion

    #region DDS Writing

    private void WriteDdsFile(string outputPath, D3DTextureInfo texture, byte[] data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
        using var writer = new BinaryWriter(File.Create(outputPath));
        WriteDdsHeader(writer, texture);
        writer.Write(data);
    }

    private static byte[]? WriteDdsToMemory(D3DTextureInfo texture, byte[] data)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        WriteDdsHeader(writer, texture);
        writer.Write(data);
        return ms.ToArray();
    }

    private static void WriteDdsHeader(BinaryWriter writer, D3DTextureInfo texture)
    {
        writer.Write(0x20534444); // "DDS "
        writer.Write(124);        // Header size

        uint flags = 0x1 | 0x2 | 0x4 | 0x1000; // CAPS | HEIGHT | WIDTH | PIXELFORMAT
        if (texture.MipLevels > 1)
            flags |= 0x20000; // MIPMAPCOUNT

        writer.Write(flags);
        writer.Write(texture.Height);
        writer.Write(texture.Width);
        writer.Write(CalculatePitch(texture.Width, texture.ActualFormat));
        writer.Write(0); // Depth
        writer.Write(texture.MipLevels);

        // Reserved
        for (var i = 0; i < 9; i++)
            writer.Write(0);
        writer.Write(0x4E41524B); // "KRAN" branding
        writer.Write(0);

        // Pixel format
        writer.Write(32);         // Size
        writer.Write(0x4);        // FOURCC flag
        writer.Write(texture.Format);
        writer.Write(0);          // RGB bit count
        writer.Write(0);          // R mask
        writer.Write(0);          // G mask
        writer.Write(0);          // B mask
        writer.Write(0);          // A mask

        // Caps
        uint caps = 0x1000;
        if (texture.MipLevels > 1)
            caps |= 0x400000 | 0x8;
        writer.Write(caps);
        writer.Write(0); // Caps2
        writer.Write(0); // Caps3
        writer.Write(0); // Caps4
        writer.Write(0); // Reserved
    }

    private static uint CalculatePitch(uint width, uint format)
    {
        return format switch
        {
            0x12 => Math.Max(1, (width + 3) / 4) * 8,
            0x13 or 0x14 => Math.Max(1, (width + 3) / 4) * 16,
            0x06 => width * 4,
            0x04 => width * 2,
            _ => width * 4
        };
    }

    #endregion

    private void Log(string message)
    {
        if (_verbose || _options.Verbose)
            Console.WriteLine(message);
    }
}
