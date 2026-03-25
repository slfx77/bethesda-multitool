using System.Collections.Concurrent;
using System.Text;
using DDXConv;
using FalloutXbox360Utils.Core.Formats.Ddx;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Scanning;

/// <summary>
///     Scans memory dumps for NiXenonSourceTextureData runtime objects — GPU-prepared textures.
///     These are textures that have been uploaded to the Xbox 360 GPU via the DDX→GPU pipeline.
///     The struct layout was reverse-engineered from Ghidra decompilation of
///     NiXenonSourceTextureData::Create, CreateSurf, InitializeFromD3DTexture,
///     CopyDataToSurface, and CopyDataToSurfaceLevel.
///     NiXenonSourceTextureData (0x88 = 136 bytes):
///     +0x00 vtable (ptr)
///     +0x04 m_uiRefCount (uint32 BE)
///     +0x08 NiSourceTexture back-pointer (ptr)
///     +0x0C Width (uint32 BE) — set from NiPixelData during CreateSurf
///     +0x10 Height (uint32 BE)
///     +0x14 D3DBaseTexture (0x44 = 68 bytes inline) — GPU texture descriptor
///     +0x14+0x1C = +0x30: GPU fetch constant DWORD[0..5] (24 bytes)
///     +0x14+0x34 = +0x48: BaseAddress (physical addr of texture data)
///     +0x14+0x38 = +0x4C: MipAddress
///     +0x58 DDX format info (2 bytes)
///     +0x5A GPU format code byte (0xFF = uninitialized)
///     +0x5B Quality/degradation state
///     +0x64 Intermediate buffer pointer
///     +0x68 D3DTexture pointer (GPU resource handle)
///     +0x6C Mip level count (uint32 BE)
///     +0x70 Error/reload flag (byte)
///     +0x71 IsPowerOfTwo flag (byte)
///     +0x74 Total texture data size in bytes (uint32 BE)
///     +0x7C Base mip level offset (uint32 BE)
///     GPU fetch constant (6 DWORDs at +0x30, Xenia xe_gpu_texture_fetch_t layout):
///     DWORD[3] at +0x3C: tiled(1) | pitch(9) | _(1) | DataFormat(6) | _(1) | _(1) | base_address(20)
///     DWORD[4] at +0x40: width-1(13) | height-1(13) | _(6) — for 2D textures
/// </summary>
internal sealed class RuntimeGpuTextureScanner(RuntimeMemoryContext context)
{
    private readonly RuntimeMemoryContext _context = context;
    private readonly RuntimeObjectScanner _scanner = new(context);

    private int _texturesFound;

    /// <summary>Shared counter for progress reporting.</summary>
    public int TexturesFound => _texturesFound;

    /// <summary>
    ///     Scan the entire dump for NiXenonSourceTextureData objects.
    ///     Extracts GPU texture data and applies reverse transformations (untile + endian swap)
    ///     to produce DDS-compatible pixel data.
    ///     Returns a deduplicated list of extracted textures enriched with filenames.
    /// </summary>
    public List<ExtractedTexture> ScanForGpuTextures(
        IProgress<(long Scanned, long Total)>? progress = null)
    {
        var textures = new ConcurrentBag<ExtractedTexture>();
        var dataHashes = new ConcurrentDictionary<long, byte>();
        _texturesFound = 0;
        var log = Logger.Instance;

        log.Info("GPU texture scanner: starting NiXenonSourceTextureData scan ({0:N0} bytes)",
            _context.FileSize);

        _scanner.ScanAligned(
            FastFilter,
            (chunk, offset, fileOffset) =>
            {
                if (offset + StructSize > chunk.Length)
                    return;

                var texture = ValidateAndExtract(chunk, offset, fileOffset);
                if (texture != null && dataHashes.TryAdd(texture.DataHash, 0))
                {
                    textures.Add(texture);
                    Interlocked.Increment(ref _texturesFound);
                    log.Debug(
                        "  Found GPU texture at 0x{0:X}: {1}x{2}, fmt=0x{3:X2}, {4} mips, {5:N0} bytes",
                        fileOffset, texture.Width, texture.Height,
                        chunk[offset + GpuFormatByteOffset],
                        texture.MipmapLevels, texture.DataSize);
                }
            },
            StructSize,
            progress);

        var result = textures.OrderBy(t => t.SourceOffset).ToList();

        // Resolve filenames via NiSourceTexture back-pointer at +0x08
        if (result.Count > 0)
            result = ResolveFilenames(result);

        log.Info("GPU texture scanner: found {0} unique GPU-prepared textures", result.Count);
        return result;
    }

    /// <summary>
    ///     Fast filter: reject >99% of offsets with minimal computation.
    ///     Checks refcount, width/height power-of-two, GPU format byte, and mip count.
    /// </summary>
    private static bool FastFilter(byte[] chunk, int offset)
    {
        if (offset + StructSize > chunk.Length)
            return false;

        // Refcount at +0x04: all Gamebryo NiObjects have refcount in [1, 10000]
        var refCount = BinaryUtils.ReadUInt32BE(chunk, offset + RefCountOffset);
        if (refCount == 0 || refCount > MaxRefCount)
            return false;

        // Width at +0x0C: must be power-of-two, 1-8192
        var width = BinaryUtils.ReadUInt32BE(chunk, offset + WidthOffset);
        if (width == 0 || width > MaxDimension || !IsPowerOfTwo(width))
            return false;

        // Height at +0x10: must be power-of-two, 1-8192
        var height = BinaryUtils.ReadUInt32BE(chunk, offset + HeightOffset);
        if (height == 0 || height > MaxDimension || !IsPowerOfTwo(height))
            return false;

        // GPU format byte at +0x5A: must be a known format code or 0xFF (uninitialized)
        var gpuFormat = chunk[offset + GpuFormatByteOffset];
        if (gpuFormat != 0xFF && !KnownGpuFormats.Contains(gpuFormat))
            return false;

        // Mip count at +0x6C: must be 1-16
        var mipCount = BinaryUtils.ReadUInt32BE(chunk, offset + MipCountOffset);
        if (mipCount == 0 || mipCount > MaxMipmapLevels)
            return false;

        return true;
    }

    /// <summary>
    ///     Full validation and extraction for a NiXenonSourceTextureData candidate.
    ///     Cross-validates dimensions, follows pointers, reads pixel data,
    ///     and applies reverse GPU transforms.
    /// </summary>
    private ExtractedTexture? ValidateAndExtract(byte[] chunk, int offset, long fileOffset)
    {
        var width = (int)BinaryUtils.ReadUInt32BE(chunk, offset + WidthOffset);
        var height = (int)BinaryUtils.ReadUInt32BE(chunk, offset + HeightOffset);
        var gpuFormatByte = chunk[offset + GpuFormatByteOffset];
        var mipCount = (int)BinaryUtils.ReadUInt32BE(chunk, offset + MipCountOffset);
        var totalDataSize = (int)BinaryUtils.ReadUInt32BE(chunk, offset + TotalDataSizeOffset);

        // Total data size must be positive and reasonable
        if (totalDataSize <= 0 || totalDataSize > MaxPixelDataSize)
            return null;

        // D3DTexture pointer at +0x68: must be valid
        var d3dTexturePtr = BinaryUtils.ReadUInt32BE(chunk, offset + D3DTexturePtrOffset);
        if (d3dTexturePtr == 0 || !_context.IsValidPointer(d3dTexturePtr))
            return null;

        // Cross-validate: decode dimensions from GPU fetch constant DWORD[4] at +0x40
        var fetchDword4 = BinaryUtils.ReadUInt32BE(chunk, offset + FetchConstDword4Offset);
        var fetchWidth = (int)(fetchDword4 & 0x1FFF) + 1;
        var fetchHeight = (int)((fetchDword4 >> 13) & 0x1FFF) + 1;

        if (fetchWidth != width && fetchWidth != 0 && width != 0 && width > fetchWidth)
            return null;
        if (fetchHeight != height && fetchHeight != 0 && height != 0 && height > fetchHeight)
            return null;

        // Extract GPU format from fetch constant DWORD[3] at +0x3C
        var fetchDword3 = BinaryUtils.ReadUInt32BE(chunk, offset + FetchConstDword3Offset);
        var fetchDataFormat = (fetchDword3 >> 15) & 0x3F;
        var fetchTiled = (fetchDword3 >> 31) & 1;

        // Determine the GPU format code to use
        uint gpuFormat;
        if (gpuFormatByte != 0xFF && KnownGpuFormats.Contains(gpuFormatByte))
            gpuFormat = gpuFormatByte;
        else if (TextureFormats.Xbox360GpuTextureFormats.ContainsKey((int)fetchDataFormat))
            gpuFormat = fetchDataFormat;
        else
            return null;

        // Calculate expected data size and validate against stored total
        var formatName = TextureFormats.Xbox360GpuTextureFormats.GetValueOrDefault((int)gpuFormat, "DXT1");
        var bytesPerBlock = TextureFormats.GetBytesPerBlock(formatName);
        var expectedMip0Size = CalculateCompressedMipSize(width, height, bytesPerBlock);

        if (totalDataSize < expectedMip0Size)
            return null;

        // Try to read the GPU texture data from the D3DBaseTexture addresses
        var pixelData = TryReadGpuTextureData(chunk, offset, totalDataSize);
        if (pixelData == null)
            return null;

        // Apply reverse GPU transforms
        var convertedData = ReverseGpuTransform(pixelData, width, height, gpuFormat, fetchTiled != 0);

        // Map GPU format to NiTextureFormat
        var niFormat = MapGpuFormatToNiFormat(gpuFormat);

        return new ExtractedTexture
        {
            SourceOffset = fileOffset,
            Width = width,
            Height = height,
            MipmapLevels = mipCount,
            Format = niFormat,
            BitsPerPixel = 0,
            Faces = 1,
            PixelData = convertedData,
            DataHash = ComputeDataHash(convertedData)
        };
    }

    /// <summary>
    ///     Try to read GPU texture pixel data from the D3DBaseTexture addresses.
    ///     Strategy: try BaseAddress at +0x48, then fetch constant, then D3DTexture ptr.
    /// </summary>
    private byte[]? TryReadGpuTextureData(byte[] chunk, int offset, int size)
    {
        // Strategy 1: D3DBaseTexture.BaseAddress at +0x48
        var baseAddr = BinaryUtils.ReadUInt32BE(chunk, offset + D3DBaseTexBaseAddrOffset);
        if (baseAddr != 0)
        {
            var data = TryReadAtAddress(baseAddr, size);
            if (data != null) return data;

            // Try as physical address with typical VA offsets
            if (baseAddr < 0x20000000)
            {
                data = TryReadAtAddress(baseAddr | 0x40000000, size);
                if (data != null) return data;

                data = TryReadAtAddress(baseAddr | 0xA0000000, size);
                if (data != null) return data;
            }
        }

        // Strategy 2: Extract base address from GPU fetch constant DWORD[3]
        var fetchDword3 = BinaryUtils.ReadUInt32BE(chunk, offset + FetchConstDword3Offset);
        var fetchBaseAddr = (fetchDword3 & 0xFFFFF) << 12;
        if (fetchBaseAddr != 0 && fetchBaseAddr != baseAddr)
        {
            var data = TryReadAtAddress(fetchBaseAddr, size);
            if (data != null) return data;

            if (fetchBaseAddr < 0x20000000)
            {
                data = TryReadAtAddress(fetchBaseAddr | 0x40000000, size);
                if (data != null) return data;
            }
        }

        // Strategy 3: Follow D3DTexture pointer at +0x68
        var d3dTexPtr = BinaryUtils.ReadUInt32BE(chunk, offset + D3DTexturePtrOffset);
        if (d3dTexPtr != 0 && _context.IsValidPointer(d3dTexPtr))
        {
            var d3dTexFo = _context.VaToFileOffset(d3dTexPtr);
            if (d3dTexFo != null)
            {
                var d3dTexBuf = _context.ReadBytes(d3dTexFo.Value, 0x44);
                if (d3dTexBuf is { Length: >= 0x44 })
                {
                    var d3dBaseAddr = BinaryUtils.ReadUInt32BE(d3dTexBuf, 0x34);
                    if (d3dBaseAddr != 0)
                    {
                        var data = TryReadAtAddress(d3dBaseAddr, size);
                        if (data != null) return data;

                        if (d3dBaseAddr < 0x20000000)
                        {
                            data = TryReadAtAddress(d3dBaseAddr | 0x40000000, size);
                            if (data != null) return data;
                        }
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    ///     Try to read data at a given virtual address.
    ///     Returns null if the address is invalid or data is all-zero (unmapped).
    /// </summary>
    private byte[]? TryReadAtAddress(uint va, int size)
    {
        if (!_context.IsValidPointer(va))
            return null;

        var fo = _context.VaToFileOffset(va);
        if (fo == null)
            return null;

        var data = _context.ReadBytes(fo.Value, size);
        if (data == null || data.Length < size)
            return null;

        // Quick sanity: reject all-zero data (unmapped memory)
        for (var i = 0; i < Math.Min(data.Length, 256); i++)
        {
            if (data[i] != 0)
                return data;
        }

        return null;
    }

    /// <summary>
    ///     Apply reverse GPU transforms to convert GPU-prepared texture data to linear, little-endian format.
    ///     Uses DDXConv's proven untiling algorithms.
    /// </summary>
    private static byte[] ReverseGpuTransform(byte[] data, int width, int height, uint gpuFormat, bool isTiled)
    {
        // For non-block-compressed formats (A8R8G8B8, R5G6B5), just endian swap
        if (gpuFormat is 0x06 or 0x04)
            return TextureUtilities.SwapEndian16(data);

        if (isTiled)
        {
            // Tiled: use Morton/Z-order untiling + endian swap in one pass
            return TextureUtilities.UnswizzleMortonDXT(data, width, height, gpuFormat);
        }

        // Untiled (linear): just endian swap for compressed formats
        return TextureUtilities.SwapEndian16(data);
    }

    /// <summary>Map Xbox 360 GPU format code to NiTextureFormat enum.</summary>
    private static NiTextureFormat MapGpuFormatToNiFormat(uint gpuFormat)
    {
        return gpuFormat switch
        {
            0x12 or 0x52 or 0x82 or 0x86 => NiTextureFormat.DXT1,
            0x13 or 0x53 => NiTextureFormat.DXT3,
            0x14 or 0x54 or 0x88 => NiTextureFormat.DXT5,
            0x7B => NiTextureFormat.DXT1, // ATI1/BC4
            0x71 => NiTextureFormat.DXT5, // ATI2/BC5
            0x06 => NiTextureFormat.RGBA,
            0x04 => NiTextureFormat.RGB,
            _ => NiTextureFormat.DXT1
        };
    }

    /// <summary>Resolve filenames by following NiSourceTexture back-pointer at +0x08.</summary>
    private List<ExtractedTexture> ResolveFilenames(List<ExtractedTexture> textures)
    {
        var log = Logger.Instance;
        var matchCount = 0;
        var result = new List<ExtractedTexture>(textures.Count);

        foreach (var tex in textures)
        {
            var buf = _context.ReadBytes(tex.SourceOffset, StructSize);
            if (buf is not { Length: >= StructSize })
            {
                result.Add(tex);
                continue;
            }

            var srcTexPtr = BinaryUtils.ReadUInt32BE(buf, SrcTextureBackPtrOffset);
            if (srcTexPtr == 0 || !_context.IsValidPointer(srcTexPtr))
            {
                result.Add(tex);
                continue;
            }

            var srcTexFo = _context.VaToFileOffset(srcTexPtr);
            if (srcTexFo == null)
            {
                result.Add(tex);
                continue;
            }

            var srcTexBuf = _context.ReadBytes(srcTexFo.Value, 72);
            if (srcTexBuf is not { Length: >= 64 })
            {
                result.Add(tex);
                continue;
            }

            var filenamePtr = BinaryUtils.ReadUInt32BE(srcTexBuf, 48);
            if (filenamePtr == 0 || !_context.IsValidPointer(filenamePtr))
            {
                result.Add(tex);
                continue;
            }

            var filename = ReadNullTerminatedString(filenamePtr);
            if (filename != null)
            {
                result.Add(tex with { Filename = filename });
                matchCount++;
            }
            else
            {
                result.Add(tex);
            }
        }

        if (matchCount > 0)
        {
            log.Info("GPU texture scanner: resolved {0}/{1} filenames via NiSourceTexture",
                matchCount, textures.Count);
        }

        return result;
    }

    /// <summary>Read a null-terminated ASCII string from a pointer.</summary>
    private string? ReadNullTerminatedString(uint ptr)
    {
        var fo = _context.VaToFileOffset(ptr);
        if (fo == null)
            return null;

        var buf = _context.ReadBytes(fo.Value, 256);
        if (buf == null)
            return null;

        for (var i = 0; i < buf.Length; i++)
        {
            if (buf[i] == 0)
                return i == 0 ? null : Encoding.ASCII.GetString(buf, 0, i);

            if (buf[i] < 32 || buf[i] > 126)
                return null;
        }

        return null;
    }

    private static int CalculateCompressedMipSize(int width, int height, int bytesPerBlock)
    {
        return Math.Max(1, width / 4) * Math.Max(1, height / 4) * bytesPerBlock;
    }

    private static bool IsPowerOfTwo(uint value)
    {
        return value > 0 && (value & (value - 1)) == 0;
    }

    private static long ComputeDataHash(byte[] data)
    {
        long hash = 0;
        var len = Math.Min(data.Length, 64);
        for (var i = 0; i < len; i++)
            hash = hash * 31 + data[i];

        return hash ^ (data.Length * 0x517CC1B727220A95L);
    }

    #region NiXenonSourceTextureData Field Offsets (decompilation-verified)

    private const int StructSize = 0x88; // 136 bytes (from Create allocation: li r3,0x88)
    private const int RefCountOffset = 0x04;
    private const int SrcTextureBackPtrOffset = 0x08;
    private const int WidthOffset = 0x0C;
    private const int HeightOffset = 0x10;
    private const int FetchConstDword3Offset = 0x3C;
    private const int FetchConstDword4Offset = 0x40;
    private const int D3DBaseTexBaseAddrOffset = 0x48;
    private const int GpuFormatByteOffset = 0x5A;
    private const int D3DTexturePtrOffset = 0x68;
    private const int MipCountOffset = 0x6C;
    private const int TotalDataSizeOffset = 0x74;

    #endregion

    #region Validation Thresholds

    private const int MaxRefCount = 10_000;
    private const int MaxMipmapLevels = 16;
    private const int MaxDimension = 8192;
    private const int MaxPixelDataSize = 64 * 1024 * 1024;

    private static readonly HashSet<byte> KnownGpuFormats =
    [
        0x04, 0x06, 0x12, 0x13, 0x14, 0x52, 0x53, 0x54,
        0x71, 0x7B, 0x82, 0x86, 0x88
    ];

    #endregion
}
