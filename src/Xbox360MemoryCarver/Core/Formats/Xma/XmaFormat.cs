using Xbox360MemoryCarver.Core.Converters;
using Xbox360MemoryCarver.Core.Utils;

namespace Xbox360MemoryCarver.Core.Formats.Xma;

/// <summary>
///     Xbox Media Audio (XMA) format module.
///     Handles parsing, repair, XMA1→XMA2 conversion, and XMA→WAV decoding.
/// </summary>
public sealed class XmaFormat : FileFormatBase, IFileRepairer, IFileConverter
{
    private int _convertedCount;
    private int _failedCount;
    private XmaWavConverter? _wavConverter;

    public override string FormatId => "xma";
    public override string DisplayName => "XMA";
    public override string Extension => ".xma";
    public override FileCategory Category => FileCategory.Audio;
    public override string OutputFolder => "audio";
    public override int MinSize => 44;
    public override int MaxSize => 100 * 1024 * 1024;

    public override IReadOnlyList<FormatSignature> Signatures { get; } =
    [
        new()
        {
            Id = "xma",
            MagicBytes = "RIFF"u8.ToArray(),
            Description = "Xbox Media Audio (RIFF/XMA)"
        }
    ];

    public override ParseResult? Parse(ReadOnlySpan<byte> data, int offset = 0)
    {
        if (data.Length < offset + 12) return null;
        if (!data.Slice(offset, 4).SequenceEqual("RIFF"u8)) return null;

        try
        {
            var riffSize = BinaryUtils.ReadUInt32LE(data, offset + 4);
            var reportedFileSize = (int)(riffSize + 8);
            var formatType = data.Slice(offset + 8, 4);

            if (!formatType.SequenceEqual("WAVE"u8)) return null;
            if (reportedFileSize < 44 || reportedFileSize > 100 * 1024 * 1024) return null;

            var boundarySize = ValidateAndAdjustSize(data, offset, reportedFileSize);
            return XmaParser.ParseXmaChunks(data, offset, reportedFileSize, boundarySize);
        }
        catch
        {
            return null;
        }
    }

    public override string GetDisplayDescription(string signatureId,
        IReadOnlyDictionary<string, object>? metadata = null)
    {
        var baseName = "Xbox Media Audio";

        if (metadata?.TryGetValue("embeddedPath", out var path) == true && path is string pathStr)
        {
            var fileName = Path.GetFileName(pathStr);
            if (!string.IsNullOrEmpty(fileName)) baseName = $"XMA ({fileName})";
        }

        if (metadata?.TryGetValue("usablePercent", out var usable) == true && usable is int usablePct && usablePct < 80)
            return $"{baseName} [~{usablePct}% playable]";

        return baseName;
    }

    #region Private Helpers

    private static int ValidateAndAdjustSize(ReadOnlySpan<byte> data, int offset, int reportedSize)
    {
        if (offset >= data.Length) return reportedSize;

        const int minSize = 44;
        var availableData = data.Length - offset;
        if (availableData < minSize) return Math.Min(reportedSize, availableData);

        var maxScan = Math.Min(availableData, reportedSize);

        var boundaryOffset = SignatureBoundaryScanner.FindNextSignatureWithRiffValidation(
            data, offset, minSize, maxScan, "RIFF"u8);

        if (boundaryOffset > 0 && boundaryOffset < reportedSize) return boundaryOffset;

        return reportedSize;
    }

    #endregion

    #region IFileRepairer

    public bool NeedsRepair(IReadOnlyDictionary<string, object>? metadata)
    {
        if (metadata == null) return false;

        var needsRepair = metadata.TryGetValue("needsRepair", out var repair) && repair is true;
        var needsSeek = metadata.TryGetValue("hasSeekChunk", out var hasSeek) && hasSeek is false;
        var isXma1 = metadata.TryGetValue("formatTag", out var fmt) && fmt is int tag && tag == 0x0165;

        return needsRepair || needsSeek || isXma1;
    }

    public byte[] Repair(byte[] data, IReadOnlyDictionary<string, object>? metadata)
    {
        var needsRepair = metadata?.TryGetValue("needsRepair", out var repair) == true && repair is true;
        var needsSeek = metadata?.TryGetValue("hasSeekChunk", out var hasSeek) == true && hasSeek is false;
        var isXma1 = metadata?.TryGetValue("formatTag", out var fmt) == true && fmt is int tag && tag == 0x0165;

        if (!needsRepair && !needsSeek && !isXma1) return data;

        try
        {
            if (isXma1 || needsSeek) return XmaRepairer.AddSeekTable(data, isXma1);
            return data;
        }
        catch
        {
            return data;
        }
    }

    #endregion

    #region IFileConverter

    public string TargetExtension => ".wav";
    public string TargetFolder => "audio_wav";
    public bool IsInitialized => _wavConverter?.IsAvailable ?? false;
    public int ConvertedCount => _convertedCount;
    public int FailedCount => _failedCount;

    public bool Initialize(bool verbose = false, Dictionary<string, object>? options = null)
    {
        _wavConverter = new XmaWavConverter();
        return _wavConverter.IsAvailable;
    }

    public bool CanConvert(string signatureId, IReadOnlyDictionary<string, object>? metadata)
    {
        if (metadata?.TryGetValue("likelyCorrupted", out var corrupt) == true && corrupt is true) return true;
        return metadata?.TryGetValue("convertToWav", out var convert) == true && convert is true;
    }

    public async Task<DdxConversionResult> ConvertAsync(byte[] data,
        IReadOnlyDictionary<string, object>? metadata = null)
    {
        if (_wavConverter == null || !_wavConverter.IsAvailable)
            return new DdxConversionResult { Success = false, Notes = "FFmpeg not available" };

        try
        {
            var result = await _wavConverter.ConvertAsync(data);

            if (result.Success)
                Interlocked.Increment(ref _convertedCount);
            else
                Interlocked.Increment(ref _failedCount);

            return result;
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _failedCount);
            return new DdxConversionResult { Success = false, Notes = $"Exception: {ex.Message}" };
        }
    }

    #endregion
}
