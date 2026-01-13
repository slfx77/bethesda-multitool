using Xbox360MemoryCarver.Core.Converters;
using Xbox360MemoryCarver.Core.Utils;

namespace Xbox360MemoryCarver.Core.Formats.Ddx;

/// <summary>
///     Xbox 360 DDX texture format module (3XDO and 3XDR variants).
///     Supports conversion to DDS format.
/// </summary>
public sealed class DdxFormat : FileFormatBase, IFileConverter
{
    private int _convertedCount;
    private DdxSubprocessConverter? _converter;
    private int _failedCount;

    public override string FormatId => "ddx";
    public override string DisplayName => "DDX";
    public override string Extension => ".ddx";
    public override FileCategory Category => FileCategory.Texture;
    public override string OutputFolder => "ddx";
    public override int MinSize => 68;
    public override int MaxSize => 50 * 1024 * 1024;

    public override IReadOnlyList<FormatSignature> Signatures { get; } =
    [
        new()
        {
            Id = "ddx_3xdo",
            MagicBytes = "3XDO"u8.ToArray(),
            Description = "Xbox 360 DDX texture (3XDO format)"
        },
        new()
        {
            Id = "ddx_3xdr",
            MagicBytes = "3XDR"u8.ToArray(),
            Description = "Xbox 360 DDX texture (3XDR engine-tiled format)"
        }
    ];

    public override ParseResult? Parse(ReadOnlySpan<byte> data, int offset = 0)
    {
        const int minHeaderSize = 68;
        if (data.Length < offset + minHeaderSize) return null;

        var magic = data.Slice(offset, 4);
        var is3Xdo = magic.SequenceEqual("3XDO"u8);
        var is3Xdr = magic.SequenceEqual("3XDR"u8);

        if (!is3Xdo && !is3Xdr) return null;

        try
        {
            return ParseDdxHeader(data, offset, is3Xdo);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DdxFormat] Exception at offset {offset}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    public override string GetDisplayDescription(string signatureId,
        IReadOnlyDictionary<string, object>? metadata = null)
    {
        if (metadata?.TryGetValue("dimensions", out var dims) == true)
            return signatureId == "ddx_3xdr" ? $"DDX 3XDR ({dims})" : $"DDX 3XDO ({dims})";
        return signatureId == "ddx_3xdr" ? "DDX (3XDR)" : "DDX (3XDO)";
    }

    #region IFileConverter

    public string TargetExtension => ".dds";
    public string TargetFolder => "textures";
    public bool IsInitialized => _converter != null;
    public int ConvertedCount => _convertedCount;
    public int FailedCount => _failedCount;

    public bool Initialize(bool verbose = false, Dictionary<string, object>? options = null)
    {
        var saveAtlas = options?.TryGetValue("saveAtlas", out var saveAtlasObj) == true && saveAtlasObj is true;

        try
        {
            _converter = new DdxSubprocessConverter(verbose, saveAtlas: saveAtlas);
            return true;
        }
        catch (FileNotFoundException ex)
        {
            Logger.Instance.Debug($"Warning: {ex.Message}");

            return false;
        }
    }

    public bool CanConvert(string signatureId, IReadOnlyDictionary<string, object>? metadata)
    {
        // Only convert 3XDO files - 3XDR (alternate tiling) not yet supported
        return signatureId == "ddx_3xdo";
    }

    public async Task<DdxConversionResult> ConvertAsync(byte[] data,
        IReadOnlyDictionary<string, object>? metadata = null)
    {
        if (_converter == null) return new DdxConversionResult { Success = false, Notes = "Converter not initialized" };

        // Check for 3XDR which isn't supported yet
        if (data.Length >= 4 && data[3] == 'R')
        {
            Interlocked.Increment(ref _failedCount);
            return new DdxConversionResult { Success = false, Notes = "3XDR format not yet supported" };
        }

        var result = await _converter.ConvertFromMemoryWithResultAsync(data);

        if (result.Success)
            Interlocked.Increment(ref _convertedCount);
        else
            Interlocked.Increment(ref _failedCount);

        return result;
    }

    #endregion

    #region Parsing Implementation

    private static ParseResult? ParseDdxHeader(ReadOnlySpan<byte> data, int offset, bool is3Xdo)
    {
        var version = BinaryUtils.ReadUInt16LE(data, offset + 7);
        if (version < 3 || !ValidateDdxHeader(data, offset)) return null;

        var formatDword = BinaryUtils.ReadUInt32BE(data, offset + 0x28);
        var formatByte = (int)(formatDword & 0xFF);
        var sizeDword = BinaryUtils.ReadUInt32BE(data, offset + 0x2C);
        var width = (int)(sizeDword & 0x1FFF) + 1;
        var height = (int)((sizeDword >> 13) & 0x1FFF) + 1;
        var mipCount = Math.Min((int)(((formatDword >> 16) & 0xF) + 1), 13);
        var flagsDword = BinaryUtils.ReadUInt32BE(data, offset + 0x24);
        var isTiled = ((flagsDword >> 22) & 0x1) != 0;

        if (width == 0 || height == 0 || width > 4096 || height > 4096) return null;

        var formatName = TextureFormats.Xbox360GpuTextureFormats.TryGetValue(formatByte, out var gpuFormatName)
            ? gpuFormatName
            : $"Unknown(0x{formatByte:X2})";
        var uncompressedSize = CalculateUncompressedSize(width, height, mipCount, formatName);
        var estimatedSize = FindDdxBoundary(data, offset, uncompressedSize);
        var texturePath = TexturePathExtractor.FindPrecedingPath(data, offset, ".ddx");

        var metadata = new Dictionary<string, object>
        {
            ["version"] = (int)version,
            ["gpuFormat"] = formatByte,
            ["isTiled"] = isTiled,
            ["dataOffset"] = 0x44,
            ["uncompressedSize"] = uncompressedSize,
            ["width"] = width,
            ["height"] = height,
            ["mipCount"] = mipCount,
            ["formatName"] = formatName,
            ["dimensions"] = $"{width}x{height}"
        };

        string? fileName = null;
        if (texturePath != null)
        {
            metadata["texturePath"] = texturePath;
            var fn = Path.GetFileName(texturePath);
            if (!string.IsNullOrEmpty(fn))
            {
                metadata["fileName"] = fn;
                fileName = fn;
            }

            var safeFileName = Path.GetFileNameWithoutExtension(texturePath);
            if (!string.IsNullOrEmpty(safeFileName))
                metadata["safeName"] = TexturePathExtractor.SanitizeFilename(safeFileName);
        }

        return new ParseResult
        {
            Format = is3Xdo ? "3XDO" : "3XDR",
            EstimatedSize = estimatedSize,
            FileName = fileName,
            Metadata = metadata
        };
    }

    private static bool ValidateDdxHeader(ReadOnlySpan<byte> data, int offset)
    {
        return data[offset + 0x04] != 0xFF && data[offset + 0x24] >= 0x80;
    }

    private static int CalculateUncompressedSize(int width, int height, int mipCount, string formatName)
    {
        var bytesPerBlock = TextureFormats.GetBytesPerBlock(formatName);
        var size = (width + 3) / 4 * ((height + 3) / 4) * bytesPerBlock;

        int mipW = width, mipH = height;
        for (var i = 1; i < mipCount; i++)
        {
            mipW = Math.Max(1, mipW / 2);
            mipH = Math.Max(1, mipH / 2);
            size += Math.Max(1, (mipW + 3) / 4) * Math.Max(1, (mipH + 3) / 4) * bytesPerBlock;
        }

        return size;
    }

    private static int FindDdxBoundary(ReadOnlySpan<byte> data, int offset, int uncompressedSize)
    {
        const int headerSize = 0x44;
        const int minScanStart = headerSize + 64;
        var maxSize = Math.Min(data.Length - offset,
            Math.Min(headerSize + uncompressedSize * 2 + 512, 10 * 1024 * 1024));

        for (var i = offset + minScanStart; i < offset + maxSize - 4; i++)
        {
            var slice = data.Slice(i, 4);

            // Check for DDX signatures with validation
            if ((slice.SequenceEqual("3XDO"u8) || slice.SequenceEqual("3XDR"u8)) && IsValidNextDdxHeader(data, i))
                return i - offset;

            // Check for RIFF with validation
            if (slice.SequenceEqual("RIFF"u8) && SignatureBoundaryScanner.IsValidRiffHeader(data, i))
                return i - offset;

            // Check for other file signatures
            if (slice.SequenceEqual("XEX2"u8) ||
                slice.SequenceEqual("XUIS"u8) ||
                slice.SequenceEqual("XUIB"u8) ||
                slice.SequenceEqual("XDBF"u8) ||
                slice.SequenceEqual("TES4"u8) ||
                slice.SequenceEqual("LIPS"u8) ||
                slice.SequenceEqual("scn "u8) ||
                slice.SequenceEqual("DDS "u8) ||
                SignatureBoundaryScanner.IsPngSignature(data, i))
                return i - offset;

            // Check for NIF (Gamebryo)
            if (i + 20 <= data.Length && data.Slice(i, 20).SequenceEqual("Gamebryo File Format"u8))
                return i - offset;
        }

        return headerSize + Math.Max(100, uncompressedSize * 7 / 10);
    }

    private static bool IsValidNextDdxHeader(ReadOnlySpan<byte> data, int position)
    {
        if (position + 68 > data.Length) return false;
        var version = BinaryUtils.ReadUInt16LE(data, position + 7);
        return version >= 3 && data[position + 0x04] != 0xFF && data[position + 0x24] >= 0x80;
    }

    #endregion
}
