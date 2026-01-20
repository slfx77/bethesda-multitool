using Xbox360MemoryCarver.Core.Converters;
using Xbox360MemoryCarver.Core.Utils;

namespace Xbox360MemoryCarver.Core.Formats.Xma;

/// <summary>
///     Xbox Media Audio (XMA) format module.
///     Handles parsing, repair, XMA1 -> XMA2 conversion, and XMA -> WAV/OGG decoding.
/// </summary>
public sealed class XmaFormat : FileFormatBase, IFileRepairer, IFileConverter
{
    /// <summary>
    ///     Output format for XMA conversion.
    /// </summary>
    public enum OutputFormat
    {
        /// <summary>WAV (PCM) - lossless, larger files.</summary>
        Wav,

        /// <summary>OGG Vorbis - compressed, PC game compatible.</summary>
        Ogg
    }

    private int _convertedCount;
    private int _failedCount;
    private XmaOggConverter? _oggConverter;
    private OutputFormat _outputFormat = OutputFormat.Wav;
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
        if (data.Length < offset + 12)
        {
            return null;
        }

        if (!data.Slice(offset, 4).SequenceEqual("RIFF"u8))
        {
            return null;
        }

        try
        {
            var riffSize = BinaryUtils.ReadUInt32LE(data, offset + 4);
            var reportedFileSize = (int)(riffSize + 8);
            var formatType = data.Slice(offset + 8, 4);

            if (!formatType.SequenceEqual("WAVE"u8))
            {
                return null;
            }

            if (reportedFileSize < 44 || reportedFileSize > 100 * 1024 * 1024)
            {
                return null;
            }

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
            if (!string.IsNullOrEmpty(fileName))
            {
                baseName = $"XMA ({fileName})";
            }
        }

        if (metadata?.TryGetValue("usablePercent", out var usable) == true && usable is int usablePct and < 80)
        {
            return $"{baseName} [~{usablePct}% playable]";
        }

        return baseName;
    }

    #region Private Helpers

    private static int ValidateAndAdjustSize(ReadOnlySpan<byte> data, int offset, int reportedSize)
    {
        if (offset >= data.Length)
        {
            return reportedSize;
        }

        const int minSize = 44;
        var availableData = data.Length - offset;
        if (availableData < minSize)
        {
            return Math.Min(reportedSize, availableData);
        }

        var maxScan = Math.Min(availableData, reportedSize);

        var boundaryOffset = SignatureBoundaryScanner.FindNextSignatureWithRiffValidation(
            data, offset, minSize, maxScan, "RIFF"u8);

        if (boundaryOffset > 0 && boundaryOffset < reportedSize)
        {
            return boundaryOffset;
        }

        return reportedSize;
    }

    #endregion

    #region IFileRepairer

    public bool NeedsRepair(IReadOnlyDictionary<string, object>? metadata)
    {
        if (metadata == null)
        {
            return false;
        }

        var needsRepair = metadata.TryGetValue("needsRepair", out var repair) && repair is true;
        var needsSeek = metadata.TryGetValue("hasSeekChunk", out var hasSeek) && hasSeek is false;
        var isXma1 = metadata.TryGetValue("formatTag", out var fmt) && fmt is 0x0165;

        return needsRepair || needsSeek || isXma1;
    }

    public byte[] Repair(byte[] data, IReadOnlyDictionary<string, object>? metadata)
    {
        var needsRepair = metadata?.TryGetValue("needsRepair", out var repair) == true && repair is true;
        var needsSeek = metadata?.TryGetValue("hasSeekChunk", out var hasSeek) == true && hasSeek is false;
        var isXma1 = metadata?.TryGetValue("formatTag", out var fmt) == true && fmt is 0x0165;

        if (!needsRepair && !needsSeek && !isXma1)
        {
            return data;
        }

        try
        {
            if (isXma1 || needsSeek)
            {
                return XmaRepairer.AddSeekTable(data, isXma1);
            }

            return data;
        }
        catch
        {
            return data;
        }
    }

    #endregion

    #region IFileConverter

    public string TargetExtension => _outputFormat == OutputFormat.Ogg ? ".ogg" : ".wav";
    public string TargetFolder => _outputFormat == OutputFormat.Ogg ? "audio_ogg" : "audio_wav";

    public bool IsInitialized =>
        (_outputFormat == OutputFormat.Ogg ? _oggConverter?.IsAvailable : _wavConverter?.IsAvailable) ?? false;

    public int ConvertedCount => _convertedCount;
    public int FailedCount => _failedCount;

    /// <summary>
    ///     Initialize the XMA converter.
    ///     Supports options: "outputFormat" = "ogg" or "wav" (default: "wav")
    /// </summary>
    public bool Initialize(bool verbose = false, Dictionary<string, object>? options = null)
    {
        // Check for output format option
        if (options?.TryGetValue("outputFormat", out var formatValue) == true)
        {
            if (formatValue is OutputFormat format)
            {
                _outputFormat = format;
            }
            else if (formatValue is string formatStr)
            {
                _outputFormat = formatStr.Equals("ogg", StringComparison.OrdinalIgnoreCase)
                    ? OutputFormat.Ogg
                    : OutputFormat.Wav;
            }
        }

        if (_outputFormat == OutputFormat.Ogg)
        {
            _oggConverter = new XmaOggConverter();
            return _oggConverter.IsAvailable;
        }

        _wavConverter = new XmaWavConverter();
        return _wavConverter.IsAvailable;
    }

    public bool CanConvert(string signatureId, IReadOnlyDictionary<string, object>? metadata)
    {
        if (metadata?.TryGetValue("likelyCorrupted", out var corrupt) == true && corrupt is true)
        {
            return true;
        }

        return metadata?.TryGetValue("convertToWav", out var convert) == true && convert is true;
    }

    public async Task<ConversionResult> ConvertAsync(byte[] data,
        IReadOnlyDictionary<string, object>? metadata = null)
    {
        // Allow per-call override of output format
        var useOgg = _outputFormat == OutputFormat.Ogg;
        if (metadata?.TryGetValue("outputFormat", out var formatValue) == true)
        {
            if (formatValue is OutputFormat format)
            {
                useOgg = format == OutputFormat.Ogg;
            }
            else if (formatValue is string formatStr)
            {
                useOgg = formatStr.Equals("ogg", StringComparison.OrdinalIgnoreCase);
            }
        }

        try
        {
            ConversionResult result;

            if (useOgg)
            {
                _oggConverter ??= new XmaOggConverter();
                if (!_oggConverter.IsAvailable)
                {
                    return new ConversionResult { Success = false, Notes = "FFmpeg not available for OGG conversion" };
                }

                result = await _oggConverter.ConvertAsync(data);
            }
            else
            {
                _wavConverter ??= new XmaWavConverter();
                if (!_wavConverter.IsAvailable)
                {
                    return new ConversionResult { Success = false, Notes = "FFmpeg not available for WAV conversion" };
                }

                result = await _wavConverter.ConvertAsync(data);
            }

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
        catch (Exception ex)
        {
            Interlocked.Increment(ref _failedCount);
            return new ConversionResult { Success = false, Notes = $"Exception: {ex.Message}" };
        }
    }

    #endregion
}
