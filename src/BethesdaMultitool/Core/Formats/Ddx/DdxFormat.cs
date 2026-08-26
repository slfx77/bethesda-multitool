using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Ddx;

/// <summary>
///     Xbox 360 DDX texture format module (3XDO and 3XDR variants).
///     Supports conversion to DDS format.
/// </summary>
public sealed class DdxFormat : FileFormatBase, IFileConverter
{
    private const string SignatureId3Xdo = "ddx_3xdo";
    private const string SignatureId3Xdr = "ddx_3xdr";

    /// <summary>Fixed DDX header size; the XMemCompress payload starts here.</summary>
    private const int DdxHeaderSize = 0x44;

    /// <summary>
    ///     BE32 total uncompressed payload size. The format doc used to call 0x3C-0x43
    ///     "padding / reserved"; measured over the 26,122 on-disk <c>.ddx</c> of the July 2010
    ///     build this dword is an upper bound on the sum of every stream's declared
    ///     uncompressed bytes (26,122/26,122), and is exactly that sum for every two-stream file.
    /// </summary>
    private const int DdxUncompressedSizeOffset = 0x3C;

    /// <summary>
    ///     BE32 length of the FIRST XMemCompress stream (the second half of the old "padding").
    ///     Matches the walked stream-1 length for 19,841/19,841 two-stream files; single-stream
    ///     files often leave it zero, so it corroborates the walk rather than replacing it.
    /// </summary>
    private const int DdxFirstStreamLengthOffset = 0x40;

    /// <summary>
    ///     Anything larger than this is not a plausible uncompressed Xbox 360 texture surface
    ///     (a 4096x4096 A8R8G8B8 mip 0 is 64 MB), so a header dword above it is treated as junk.
    /// </summary>
    private const int MaxPlausibleUncompressedSize = 64 * 1024 * 1024;

    // ----------------------------------------------------------------------------------
    // XMemCompress chunk framing. Mirrored from the shipped decoder's constants —
    // src/DDXConv/DDXConv/Compression/LzxDecompressor.cs:33-35 is the source of truth
    // (that file is a git submodule, so the values are duplicated here rather than shared).
    // ----------------------------------------------------------------------------------

    /// <summary>Leading byte of a stream's final ("terminal") chunk header.</summary>
    private const byte LzxStreamTerminator = 0xFF;

    /// <summary>Uncompressed size implied by a continuation chunk (32 KB).</summary>
    private const int LzxDefaultUncompressedChunkSize = 0x8000;

    /// <summary>A chunk whose framed total exceeds this is a decoder error, not a chunk.</summary>
    private const int LzxMaxTotalChunkSize = 0x980A;

    private int _convertedCount;
    private DdxConverter? _converter;
    private int _failedCount;

    public override string FormatId => "ddx";
    public override string DisplayName => "DDX";
    public override string Extension => ".ddx";
    public override FileCategory Category => FileCategory.Texture;
    public override string OutputFolder => "ddx";
    public override int MinSize => 68;
    public override int MaxSize => 50 * 1024 * 1024;

    /// <summary>
    ///     Wide window for boundary scanning so large textures find their true next-file
    ///     token (the carver clamps at the containing memory region / EOF).
    /// </summary>
    public override int ParseWindowSize => 8 * 1024 * 1024;

    public override IReadOnlyList<FormatSignature> Signatures { get; } =
    [
        new()
        {
            Id = SignatureId3Xdo,
            MagicBytes = "3XDO"u8.ToArray(),
            Description = "Xbox 360 DDX texture (3XDO format)"
        },
        new()
        {
            Id = SignatureId3Xdr,
            MagicBytes = "3XDR"u8.ToArray(),
            Description = "Xbox 360 DDX texture (3XDR engine-tiled format)"
        }
    ];

    public override ParseResult? Parse(ReadOnlySpan<byte> data, int offset = 0)
    {
        const int minHeaderSize = 68;
        if (data.Length < offset + minHeaderSize)
        {
            return null;
        }

        var magic = data.Slice(offset, 4);
        var is3Xdo = magic.SequenceEqual("3XDO"u8);
        var is3Xdr = magic.SequenceEqual("3XDR"u8);

        if (!is3Xdo && !is3Xdr)
        {
            return null;
        }

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
        {
            return signatureId == SignatureId3Xdr ? $"DDX 3XDR ({dims})" : $"DDX 3XDO ({dims})";
        }

        return signatureId == SignatureId3Xdr ? "DDX (3XDR)" : "DDX (3XDO)";
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
        _converter = new DdxConverter(verbose, saveAtlas);
        return true;
    }

    public bool CanConvert(string signatureId, IReadOnlyDictionary<string, object>? metadata)
    {
        // Both 3XDO (Morton-swizzled) and 3XDR (macro-block tiled) are supported
        return signatureId == SignatureId3Xdo || signatureId == SignatureId3Xdr;
    }

    public async Task<ConversionResult> ConvertAsync(byte[] data,
        IReadOnlyDictionary<string, object>? metadata = null)
    {
        if (_converter == null)
        {
            return new ConversionResult { Success = false, Notes = "Converter not initialized" };
        }

        // Both 3XDO and 3XDR formats are now supported
        var result = await _converter.ConvertFromMemoryWithResultAsync(data);

        if (result.Success)
        {
            Interlocked.Increment(ref _convertedCount);
        }
        else
        {
            Interlocked.Increment(ref _failedCount);
        }

        return result;
    }

    #endregion

    #region Parsing Implementation

    private static ParseResult? ParseDdxHeader(ReadOnlySpan<byte> data, int offset, bool is3Xdo)
    {
        // Upper bound replaces the old priority-byte heuristic as the anti-garbage guard: every
        // file in the July corpus reports version 4, so an unbounded field let an all-0xFF region
        // (version 65535) through to the later checks.
        const int maxKnownVersion = 16;
        var version = BinaryUtils.ReadUInt16LE(data, offset + 7);
        if (version < 3 || version > maxKnownVersion || !ValidateDdxHeader(data, offset))
        {
            return null;
        }

        var formatDword = BinaryUtils.ReadUInt32BE(data, offset + 0x28);
        var formatByte = (int)(formatDword & 0xFF);
        var sizeDword = BinaryUtils.ReadUInt32BE(data, offset + 0x2C);
        var width = (int)(sizeDword & 0x1FFF) + 1;
        var height = (int)((sizeDword >> 13) & 0x1FFF) + 1;
        var flagsDword = BinaryUtils.ReadUInt32BE(data, offset + 0x24);
        var isTiled = ((flagsDword >> 22) & 0x1) != 0;

        // Validate dimensions are reasonable (non-zero, power-of-two friendly, max 4096)
        if (width == 0 || height == 0 || width > 4096 || height > 4096)
        {
            return null;
        }

        // Validate GPU format is a known Xbox 360 texture format - reject unknown formats
        // This significantly reduces false positives from random data matching "3XDO"/"3XDR" magic
        if (!TextureFormats.Xbox360GpuTextureFormats.TryGetValue(formatByte, out var formatName))
        {
            return null;
        }

        var uncompressedSize = ReadUncompressedSize(data, offset, width, height, (uint)formatByte);
        var mipCount = CountMipLevelsFromDataSize(width, height, (uint)formatByte, uncompressedSize);
        var extent = DetermineDdxExtent(data, offset, uncompressedSize);
        var texturePath = TexturePathExtractor.FindPrecedingPath(data, offset, ".ddx");

        var metadata = new Dictionary<string, object>
        {
            ["version"] = (int)version,
            ["gpuFormat"] = formatByte,
            ["isTiled"] = isTiled,
            ["dataOffset"] = DdxHeaderSize,
            ["uncompressedSize"] = uncompressedSize,
            ["width"] = width,
            ["height"] = height,
            ["mipCount"] = mipCount,
            ["formatName"] = formatName,
            ["dimensions"] = $"{width}x{height}",
            ["lzxStreamCount"] = extent.StreamCount,
            ["headerStreamLengthAgrees"] = extent.HeaderStreamLengthAgrees
        };

        if (extent.FallbackReason != null)
        {
            metadata["boundaryFallback"] = true;
            metadata["boundaryFallbackReason"] = extent.FallbackReason;
        }

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
            {
                metadata["safeName"] = TexturePathExtractor.SanitizeFilename(safeFileName);
            }
        }

        return new ParseResult
        {
            Format = is3Xdo ? "3XDO" : "3XDR",
            EstimatedSize = extent.EstimatedSize,
            FileName = fileName,
            Metadata = metadata
        };
    }

    /// <summary>
    ///     Structural plausibility of a DDX header. Deliberately does NOT test the priority byte at
    ///     0x04: that byte is a small enum (measured over the 26,123-file July corpus: values 0-6)
    ///     whose <b>0xFF is a legitimate value carried by 1,847 real textures</b> (7.1%, e.g. every
    ///     <c>dlc03/interface/**</c>), all of which pass every other check. Rejecting it was a
    ///     carver false-negative that silently discarded those textures.
    ///     <para>
    ///         The all-0xFF garbage region that check was presumably guarding against is already
    ///         rejected downstream, and more precisely: 0xFF is not a member of
    ///         <see cref="TextureFormats.Xbox360GpuTextureFormats" />, the version bound below is
    ///         explicit, and the chunk-framing walk will not walk garbage to a clean terminator.
    ///     </para>
    /// </summary>
    private static bool ValidateDdxHeader(ReadOnlySpan<byte> data, int offset)
    {
        return data[offset + 0x24] >= 0x80;
    }

    /// <summary>
    ///     Total uncompressed payload size. Prefers the header's own BE32 @0x3C (see
    ///     <see cref="DdxUncompressedSizeOffset" />); when that is zero or implausible it falls
    ///     back to the same expression <c>DdxParser</c> uses for its decompress hint —
    ///     tiled mip 0 plus the sequential tiled mip chain (pinned by
    ///     <c>DDXConv.Tests/DecompressBufferHintTests.cs</c>).
    /// </summary>
    private static int ReadUncompressedSize(ReadOnlySpan<byte> data, int offset, int width, int height,
        uint gpuFormat)
    {
        var declared = BinaryUtils.ReadUInt32BE(data, offset + DdxUncompressedSizeOffset);
        if (declared is > 0 and <= MaxPlausibleUncompressedSize)
        {
            return (int)declared;
        }

        return DDXConv.TextureUtilities.CalculateTiledMipSize(width, height, gpuFormat)
               + ComputeSequentialTiledMipTotal(width, height, gpuFormat);
    }

    /// <summary>
    ///     Local copy of <c>DDXConv.TextureUtilities.ComputeSequentialTiledMipTotal</c>, which is
    ///     <c>internal</c> to the DDXConv submodule (visible only to <c>DDXConv.Tests</c>) and so
    ///     cannot be called from here. The arithmetic is reproduced verbatim; the submodule copy
    ///     stays the source of truth and is pinned by <c>DecompressBufferHintTests</c>.
    /// </summary>
    private static int ComputeSequentialTiledMipTotal(int baseWidth, int baseHeight, uint format)
    {
        var totalLevels = (int)DDXConv.TextureUtilities.CalculateMipLevels((uint)baseWidth, (uint)baseHeight);
        var total = 0;

        // Non-block formats have no 4x4 block grid, so their chain is the linear total.
        if (format is 0x06 or 0x04)
        {
            for (var level = 1; level < totalLevels; level++)
            {
                total += DDXConv.TextureUtilities.CalculateMipSize(
                    Math.Max(1, baseWidth >> level), Math.Max(1, baseHeight >> level), format);
            }

            return total;
        }

        for (var level = 1; level < totalLevels; level++)
        {
            var mipW = Math.Max(4, baseWidth >> level);
            var mipH = Math.Max(4, baseHeight >> level);
            total += DDXConv.TextureUtilities.CalculateTiledMipSize(mipW, mipH, format);

            // Packed tail: one tile-aligned surface shared by every remaining level.
            if (Math.Min(mipW, mipH) <= 16)
            {
                break;
            }
        }

        return total;
    }

    /// <summary>
    ///     How many whole mip levels fit in <paramref name="dataLength" /> bytes. Mirrors
    ///     <c>DdxMipAtlasUnpacker.CountMipLevelsFromDataSize</c> (also <c>internal</c> to the
    ///     submodule). This replaces the old <c>(formatDword &gt;&gt; 16) &amp; 0xF</c> read:
    ///     those bits belong to the fetch constant's <c>base_address</c> field, which is zero in
    ///     every DDX file, so that expression reported 1 mip for 24,268/24,268 measured files.
    /// </summary>
    private static int CountMipLevelsFromDataSize(int width, int height, uint format, int dataLength)
    {
        var levels = 0;
        var w = (uint)width;
        var h = (uint)height;
        var consumed = 0L;

        while (true)
        {
            var mipSize = (long)DDXConv.TextureUtilities.CalculateMipSize(w, h, format);
            if (consumed + mipSize > dataLength)
            {
                break;
            }

            consumed += mipSize;
            levels++;

            if (w == 1 && h == 1)
            {
                break;
            }

            w = Math.Max(1, w / 2);
            h = Math.Max(1, h / 2);
        }

        return Math.Max(1, levels);
    }

    /// <summary>
    ///     Walks ONE XMemCompress stream's chunk framing — size prefixes only, no LZX decode, no
    ///     Huffman tables, no window allocation — reporting the span of the chunks that framed
    ///     cleanly, the sum of their declared uncompressed sizes, and whether the stream's own
    ///     terminator was reached.
    /// </summary>
    /// <remarks>
    ///     Framing (see the constants above and their DDXConv source): a continuation chunk is a
    ///     BE16 compressed size and spans <c>size + 2</c>; a terminal chunk starts with 0xFF, then
    ///     BE16 uncompressed size and BE16 compressed size, and spans <c>compressed + 10</c>.
    ///     A stream ends at its terminal chunk.
    ///     <para>
    ///         A zero compressed size is rejected outright: without that guard an all-zero buffer
    ///         "walks" two bytes at a time to the end of the window and reports success.
    ///     </para>
    ///     <para>
    ///         The walk stops — cleanly, keeping what it has — once another chunk would push the
    ///         declared uncompressed total past <paramref name="uncompressedBudget" />. That is
    ///         what stops a walk over unrelated heap bytes from running away.
    ///     </para>
    /// </remarks>
    private static LzxStreamWalk WalkLzxStream(ReadOnlySpan<byte> data, int start, long uncompressedBudget)
    {
        if (start < 0 || start >= data.Length)
        {
            return new LzxStreamWalk(0, 0, false);
        }

        var pos = start;
        long declaredUncompressed = 0;

        while (pos < data.Length)
        {
            var terminal = data[pos] == LzxStreamTerminator;
            int compressedSize;
            int chunkUncompressedSize;
            int totalChunkSize;

            if (terminal)
            {
                if (pos + 5 > data.Length)
                {
                    break;
                }

                chunkUncompressedSize = (data[pos + 1] << 8) | data[pos + 2];
                compressedSize = (data[pos + 3] << 8) | data[pos + 4];
                totalChunkSize = compressedSize + 10;
            }
            else
            {
                if (pos + 2 > data.Length)
                {
                    break;
                }

                compressedSize = (data[pos] << 8) | data[pos + 1];
                chunkUncompressedSize = LzxDefaultUncompressedChunkSize;
                totalChunkSize = compressedSize + 2;
            }

            if (compressedSize == 0 || totalChunkSize > LzxMaxTotalChunkSize ||
                pos + totalChunkSize > data.Length ||
                declaredUncompressed + chunkUncompressedSize > uncompressedBudget)
            {
                break;
            }

            pos += totalChunkSize;
            declaredUncompressed += chunkUncompressedSize;

            if (terminal)
            {
                return new LzxStreamWalk(pos - start, declaredUncompressed, true);
            }
        }

        return new LzxStreamWalk(pos - start, declaredUncompressed, false);
    }

    /// <summary>
    ///     Exact carve extent for one DDX: walk the compressed payload's own chunk framing rather
    ///     than guessing a compression ratio.
    /// </summary>
    /// <remarks>
    ///     A memory dump has no EOF, so the walk needs a stop rule. Stream 1 must walk cleanly to
    ///     its own terminator; anything less is a structural failure and drops to the fallbacks
    ///     below. A second stream is then considered only when stream 1's declared uncompressed
    ///     total falls short of the header's BE32 @0x3C total — true for every real two-stream
    ///     file in the July 2010 corpus (19,841/19,841) and for no real single-stream file.
    ///     Stream 2 is admitted for as many chunks as frame cleanly within the remaining
    ///     uncompressed budget, terminator or not: a dump's copy of a texture buffer is routinely
    ///     overwritten part-way through its tail (measured on <c>rugsmall01.ddx</c> in
    ///     <c>Fallout_Debug.xex.dmp</c>, whose stream 2 dies at chunk 2), and dropping the whole
    ///     stream over that loses recoverable mips permanently — the ruling here is to prefer
    ///     over-carving, which only costs padding bytes. A third stream is never attempted; the
    ///     corpus contains none.
    ///     <para>
    ///         Finally, the bytes that are NOT proven by a stream terminator — an admitted but
    ///         unterminated stream-2 prefix — are capped at the first next-file token, so a walk
    ///         over unrelated heap data can never swallow the following file. The token scan is
    ///         deliberately *not* applied to terminator-proven bytes: a 4-byte magic inside LZX
    ///         output is a far weaker signal than the framing itself, and over the 26,122-file
    ///         July 2010 corpus three files (<c>1stpersondisplacergloves_m</c> "XEX2",
    ///         <c>nvmcc_terminal_int_floor02</c> PNG, <c>explosionsparks03</c> a header that
    ///         passes <see cref="IsValidNextDdxHeader" />) carry such a false token inside their
    ///         own payload and would be truncated by up to 97% — the exact under-carve this item
    ///         exists to stop.
    ///     </para>
    /// </remarks>
    private static DdxExtent DetermineDdxExtent(ReadOnlySpan<byte> data, int offset, int uncompressedSize)
    {
        var declaredTotalUncompressed = BinaryUtils.ReadUInt32BE(data, offset + DdxUncompressedSizeOffset);
        var declaredFirstStreamLength = BinaryUtils.ReadUInt32BE(data, offset + DdxFirstStreamLengthOffset);

        var first = WalkLzxStream(data, offset + DdxHeaderSize, long.MaxValue);
        if (first.Terminated)
        {
            var walked = DdxHeaderSize + first.Consumed;
            var terminatorProven = walked;
            var streamCount = 1;

            if (first.DeclaredUncompressed < declaredTotalUncompressed)
            {
                var second = WalkLzxStream(data, offset + walked,
                    declaredTotalUncompressed - first.DeclaredUncompressed);
                if (second.Consumed > 0)
                {
                    walked += second.Consumed;
                    streamCount = 2;
                    if (second.Terminated)
                    {
                        terminatorProven = walked;
                    }
                }
            }

            if (terminatorProven < walked)
            {
                var token = FindNextFileToken(data, offset, walked, terminatorProven);
                if (token.HasValue)
                {
                    walked = token.Value;
                }
            }

            return new DdxExtent(walked, null, streamCount,
                declaredFirstStreamLength == (uint)first.Consumed);
        }

        var scanLimit = Math.Min(data.Length - offset,
            Math.Min(DdxHeaderSize + uncompressedSize * 2 + 512, 10 * 1024 * 1024));
        var fallbackToken = FindNextFileToken(data, offset, scanLimit);
        if (fallbackToken.HasValue)
        {
            return new DdxExtent(fallbackToken.Value,
                "LZX chunk framing walk failed; size = next-file boundary token", 0, false);
        }

        // Empirically tuned last-resort fallback (do not change the 0.7 factor without
        // re-baselining) — reached only when the payload framing is unreadable AND no next-file
        // token is in range.
        return new DdxExtent(DdxHeaderSize + Math.Max(100, uncompressedSize * 7 / 10),
            "LZX chunk framing walk failed and no boundary token found; size = header + 0.7*uncompressed",
            0, false);
    }

    /// <summary>
    ///     Offset (relative to <paramref name="offset" />) of the first following file signature
    ///     in <c>[minScanStart, maxSize)</c>, or <c>null</c> when there is none.
    /// </summary>
    private static int? FindNextFileToken(ReadOnlySpan<byte> data, int offset, int maxSize, int minScanStart = 0)
    {
        minScanStart = Math.Max(minScanStart, DdxHeaderSize + 64);
        maxSize = Math.Min(maxSize, data.Length - offset);

        for (var i = offset + minScanStart; i < offset + maxSize - 4; i++)
        {
            var slice = data.Slice(i, 4);

            // Check for DDX signatures with validation
            if ((slice.SequenceEqual("3XDO"u8) || slice.SequenceEqual("3XDR"u8)) && IsValidNextDdxHeader(data, i))
            {
                return i - offset;
            }

            // Check for RIFF with validation
            if (slice.SequenceEqual("RIFF"u8) && SignatureBoundaryScanner.IsValidRiffHeader(data, i))
            {
                return i - offset;
            }

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
            {
                return i - offset;
            }

            // Check for NIF (Gamebryo)
            if (i + 20 <= data.Length && data.Slice(i, 20).SequenceEqual("Gamebryo File Format"u8))
            {
                return i - offset;
            }
        }

        return null;
    }

    private static bool IsValidNextDdxHeader(ReadOnlySpan<byte> data, int position)
    {
        if (position + 68 > data.Length)
        {
            return false;
        }

        var version = BinaryUtils.ReadUInt16LE(data, position + 7);
        return version >= 3 && data[position + 0x04] != 0xFF && data[position + 0x24] >= 0x80;
    }

    /// <summary>
    ///     Outcome of <see cref="DetermineDdxExtent" />. <see cref="FallbackReason" /> is
    ///     <c>null</c> when the size came from the exact framing walk.
    /// </summary>
    private readonly record struct DdxExtent(
        int EstimatedSize,
        string? FallbackReason,
        int StreamCount,
        bool HeaderStreamLengthAgrees);

    /// <summary>
    ///     Result of <see cref="WalkLzxStream" />: the span of the chunks that framed cleanly, the
    ///     uncompressed bytes they declare, and whether the stream's 0xFF terminator was reached.
    /// </summary>
    private readonly record struct LzxStreamWalk(int Consumed, long DeclaredUncompressed, bool Terminated);

    #endregion
}
