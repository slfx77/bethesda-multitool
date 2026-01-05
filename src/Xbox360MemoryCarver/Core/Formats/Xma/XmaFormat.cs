using System.Text;
using Xbox360MemoryCarver.Core.Utils;

namespace Xbox360MemoryCarver.Core.Formats.Xma;

/// <summary>
///     Xbox Media Audio (XMA) format module.
///     Handles parsing, repair, and XMA1→XMA2 conversion.
/// </summary>
public sealed class XmaFormat : FileFormatBase, IFileRepairer
{
    private static readonly ushort[] XmaFormatCodes = [0x0165, 0x0166];

    public override string FormatId => "xma";
    public override string DisplayName => "XMA";
    public override string Extension => ".xma";
    public override FileCategory Category => FileCategory.Audio;
    public override string OutputFolder => "audio";
    public override int MinSize => 44;
    public override int MaxSize => 100 * 1024 * 1024;
    public override int DisplayPriority => 1;

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

            // Validate the reported size is reasonable
            if (reportedFileSize < 44 || reportedFileSize > 100 * 1024 * 1024) return null;

            // Check if the reported size extends past another file signature
            var boundarySize = ValidateAndAdjustSize(data, offset, reportedFileSize);

            // Parse chunks and look for XMA indicators
            return ParseXmaChunks(data, offset, reportedFileSize, boundarySize);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[XmaFormat] Exception at offset {offset}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    public override string GetDisplayDescription(string signatureId,
        IReadOnlyDictionary<string, object>? metadata = null)
    {
        if (metadata?.TryGetValue("embeddedPath", out var path) == true && path is string pathStr)
        {
            var fileName = Path.GetFileName(pathStr);
            if (!string.IsNullOrEmpty(fileName)) return $"XMA ({fileName})";
        }

        return "Xbox Media Audio (RIFF/XMA)";
    }

    #region IFileRepairer

    public bool NeedsRepair(IReadOnlyDictionary<string, object>? metadata)
    {
        if (metadata == null) return false;

        var needsRepair = metadata.TryGetValue("needsRepair", out var repair) && repair is true;
        var needsSeek = metadata.TryGetValue("hasSeekChunk", out var hasSeek) && hasSeek is false;
        var isXma1 = metadata.TryGetValue("formatTag", out var fmt) && fmt is ushort tag && tag == 0x0165;

        return needsRepair || needsSeek || isXma1;
    }

    public byte[] Repair(byte[] data, IReadOnlyDictionary<string, object>? metadata)
    {
        var needsRepair = metadata?.TryGetValue("needsRepair", out var repair) == true && repair is true;
        var needsSeek = metadata?.TryGetValue("hasSeekChunk", out var hasSeek) == true && hasSeek is false;
        var isXma1 = metadata?.TryGetValue("formatTag", out var fmt) == true && fmt is ushort tag && tag == 0x0165;

        if (!needsRepair && !needsSeek && !isXma1) return data;

        try
        {
            if (needsRepair) return RepairCorruptedXma(data);

            if (isXma1 || needsSeek) return AddSeekTable(data, isXma1);

            return data;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[XmaFormat] Repair failed: {ex.Message}");
            return data;
        }
    }

    #endregion

    #region Parsing Implementation

    private static int ValidateAndAdjustSize(ReadOnlySpan<byte> data, int offset, int reportedSize)
    {
        if (offset >= data.Length) return reportedSize;

        const int minSize = 44;
        var availableData = data.Length - offset;
        if (availableData < minSize) return Math.Min(reportedSize, availableData);

        var maxScan = Math.Min(availableData, reportedSize);

        // Find next signature within the reported size
        var boundaryOffset = SignatureBoundaryScanner.FindNextSignatureWithRiffValidation(
            data, offset, minSize, maxScan, "RIFF"u8);

        if (boundaryOffset > 0 && boundaryOffset < reportedSize) return boundaryOffset;

        return reportedSize;
    }

    private static ParseResult? ParseXmaChunks(ReadOnlySpan<byte> data, int offset, int reportedSize, int boundarySize)
    {
        var searchOffset = offset + 12;
        var maxSearchOffset = Math.Min(offset + boundarySize, data.Length);

        string? embeddedPath = null;
        int? dataChunkOffset = null;
        int? dataChunkSize = null;
        ushort? formatTag = null;
        var needsRepair = false;
        var hasSeekChunk = false;

        while (searchOffset < maxSearchOffset - 8)
        {
            var chunkId = data.Slice(searchOffset, 4);
            var chunkSize = BinaryUtils.ReadUInt32LE(data, searchOffset + 4);

            if (chunkSize > int.MaxValue - 16) break;

            if (chunkId.SequenceEqual("fmt "u8))
            {
                if (searchOffset + 10 <= data.Length)
                {
                    formatTag = (ushort)(BinaryUtils.ReadUInt32LE(data, searchOffset + 8) & 0xFFFF);

                    var fmtDataStart = searchOffset + 12;
                    var fmtDataEnd = Math.Min(searchOffset + 256, data.Length);

                    if (fmtDataEnd > fmtDataStart)
                    {
                        var path = TryExtractPath(data.Slice(fmtDataStart, fmtDataEnd - fmtDataStart));
                        if (path != null)
                        {
                            embeddedPath = path;
                            needsRepair = true;
                        }
                    }
                }
            }
            else if (chunkId.SequenceEqual("XMA2"u8))
            {
                formatTag ??= 0x0166;
            }
            else if (chunkId.SequenceEqual("data"u8))
            {
                dataChunkOffset = searchOffset;
                dataChunkSize = (int)chunkSize;
            }
            else if (chunkId.SequenceEqual("seek"u8))
            {
                hasSeekChunk = true;
            }

            var nextOffset = searchOffset + 8 + (int)((chunkSize + 1) & ~1u);
            if (nextOffset <= searchOffset) break;
            searchOffset = nextOffset;
        }

        if (formatTag == null || !XmaFormatCodes.Contains(formatTag.Value))
            return null;

        int actualSize;
        if (dataChunkOffset.HasValue && dataChunkSize.HasValue)
        {
            var dataEnd = dataChunkOffset.Value + 8 + dataChunkSize.Value;
            actualSize = dataEnd - offset;

            if (reportedSize > actualSize + 100) needsRepair = true;
        }
        else
        {
            actualSize = boundarySize;
        }

        var metadata = new Dictionary<string, object>
        {
            ["isXma"] = true,
            ["formatTag"] = formatTag.Value,
            ["hasSeekChunk"] = hasSeekChunk
        };

        if (embeddedPath != null)
        {
            metadata["embeddedPath"] = embeddedPath;
            metadata["safeName"] = SanitizeFilename(embeddedPath);
        }

        if (needsRepair)
        {
            metadata["needsRepair"] = true;
            metadata["reportedSize"] = reportedSize;
        }

        string? fileName = null;
        if (metadata.TryGetValue("safeName", out var safeName) && safeName is string safeNameStr)
            fileName = safeNameStr + ".xma";

        return new ParseResult
        {
            Format = "XMA",
            EstimatedSize = actualSize,
            FileName = fileName,
            Metadata = metadata
        };
    }

    private static string? TryExtractPath(ReadOnlySpan<byte> data)
    {
        var pathIndicators = new[] { "sound\\", "music\\", "fx\\", ".xma", ".wav" };
        var str = Encoding.ASCII.GetString(data.ToArray());

        foreach (var indicator in pathIndicators)
        {
            var idx = str.IndexOf(indicator, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                var start = idx;
                while (start > 0 && IsPrintablePathChar(str[start - 1])) start--;

                var end = idx + indicator.Length;
                while (end < str.Length && IsPrintablePathChar(str[end])) end++;

                var path = str.Substring(start, end - start).Trim('\0', ' ');
                if (path.Length > 5) return path;
            }
        }

        return null;
    }

    private static bool IsPrintablePathChar(char c)
    {
        return c >= 0x20 && c < 0x7F && c != '"' && c != '<' && c != '>' && c != '|';
    }

    private static string SanitizeFilename(string path)
    {
        var filename = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrEmpty(filename)) filename = path.Replace('\\', '_').Replace('/', '_');

        foreach (var c in Path.GetInvalidFileNameChars()) filename = filename.Replace(c, '_');

        return filename;
    }

    #endregion

    #region Repair Implementation

    private const int DefaultSampleRate = 44100;
    private const int XmaPacketSize = 2048;

    private static readonly byte[] DefaultXma2FmtChunk =
    [
        0x66, 0x6D, 0x74, 0x20, // "fmt "
        0x34, 0x00, 0x00, 0x00, // chunk size: 52 bytes
        0x66, 0x01, // wFormatTag: 0x0166 (XMA2)
        0x01, 0x00, // nChannels: 1
        0x44, 0xAC, 0x00, 0x00, // nSamplesPerSec: 44100
        0x00, 0x00, 0x01, 0x00, // nAvgBytesPerSec: 65536
        0x00, 0x08, // nBlockAlign: 2048
        0x10, 0x00, // wBitsPerSample: 16
        0x22, 0x00, // cbSize: 34
        0x01, 0x00, // NumStreams: 1
        0x00, 0x00, 0x00, 0x00, // ChannelMask: 0
        0x00, 0x00, 0x01, 0x00, // SamplesEncoded
        0x00, 0x20, 0x00, 0x00, // BytesPerBlock: 8192
        0x00, 0x00, 0x00, 0x00, // PlayBegin
        0x00, 0x00, 0x00, 0x00, // PlayLength
        0x00, 0x00, 0x00, 0x00, // LoopBegin
        0x00, 0x00, 0x00, 0x00, // LoopLength
        0x00, // LoopCount
        0x04, // EncoderVersion
        0x00, 0x10 // BlockCount
    ];

    private static byte[] RepairCorruptedXma(byte[] data)
    {
        var dataChunkOffset = FindChunk(data, "data"u8);
        if (dataChunkOffset < 0)
        {
            Console.WriteLine("[XmaFormat] No data chunk found, cannot repair");
            return data;
        }

        var dataChunkSize = BinaryUtils.ReadUInt32LE(data.AsSpan(), dataChunkOffset + 4);
        var dataStart = dataChunkOffset + 8;
        var dataEnd = Math.Min(dataStart + (int)dataChunkSize, data.Length);
        var actualDataSize = dataEnd - dataStart;

        if (actualDataSize <= 0)
        {
            Console.WriteLine("[XmaFormat] Data chunk is empty, cannot repair");
            return data;
        }

        var seekTable = GenerateSeekTable(actualDataSize);
        return BuildXmaFile(data.AsSpan(dataStart, actualDataSize), seekTable, 1, DefaultSampleRate);
    }

    private static byte[] AddSeekTable(byte[] data, bool convertToXma2)
    {
        var fmtOffset = FindChunk(data, "fmt "u8);
        var dataOffset = FindChunk(data, "data"u8);

        if (fmtOffset < 0 || dataOffset < 0)
        {
            Console.WriteLine("[XmaFormat] Missing fmt or data chunk");
            return data;
        }

        var formatTag = data.Length > fmtOffset + 10
            ? BinaryUtils.ReadUInt16LE(data.AsSpan(), fmtOffset + 8)
            : (ushort)0;

        int channels;
        int sampleRate;

        if (formatTag == 0x0165 || convertToXma2)
        {
            // XMA1WAVEFORMAT structure
            sampleRate = data.Length > fmtOffset + 24
                ? (int)BinaryUtils.ReadUInt32LE(data.AsSpan(), fmtOffset + 24)
                : DefaultSampleRate;
            channels = data.Length > fmtOffset + 37
                ? data[fmtOffset + 37]
                : 1;

            Console.WriteLine($"[XmaFormat] XMA1 detected: {channels} channels, {sampleRate} Hz");
        }
        else
        {
            // XMA2/WAVEFORMATEX structure
            channels = data.Length > fmtOffset + 10 ? BinaryUtils.ReadUInt16LE(data.AsSpan(), fmtOffset + 10) : 1;
            sampleRate = data.Length > fmtOffset + 12
                ? (int)BinaryUtils.ReadUInt32LE(data.AsSpan(), fmtOffset + 12)
                : DefaultSampleRate;
        }

        if (channels < 1 || channels > 8) channels = 1;
        if (sampleRate < 8000 || sampleRate > 96000) sampleRate = DefaultSampleRate;

        var dataSize = BinaryUtils.ReadUInt32LE(data.AsSpan(), dataOffset + 4);
        var dataStart = dataOffset + 8;
        var actualDataSize = Math.Min((int)dataSize, data.Length - dataStart);

        if (actualDataSize <= 0) return data;

        var seekTable = GenerateSeekTable(actualDataSize);
        var result = BuildXmaFile(data.AsSpan(dataStart, actualDataSize), seekTable, channels, sampleRate);

        Console.WriteLine(
            $"[XmaFormat] Added seek table: {data.Length} -> {result.Length} bytes, {channels} ch, {sampleRate} Hz");
        return result;
    }

    private static byte[] GenerateSeekTable(int dataSize)
    {
        var numPackets = (dataSize + XmaPacketSize - 1) / XmaPacketSize;
        var numEntries = Math.Max(1, numPackets);
        const int samplesPerPacket = 512 * 8;

        var seekTable = new byte[numEntries * 4];
        for (var i = 0; i < numEntries; i++)
        {
            var cumulativeSamples = (uint)((i + 1) * samplesPerPacket);
            seekTable[i * 4] = (byte)(cumulativeSamples >> 24);
            seekTable[i * 4 + 1] = (byte)(cumulativeSamples >> 16);
            seekTable[i * 4 + 2] = (byte)(cumulativeSamples >> 8);
            seekTable[i * 4 + 3] = (byte)cumulativeSamples;
        }

        return seekTable;
    }

    private static byte[] BuildXmaFile(ReadOnlySpan<byte> audioData, byte[] seekTable, int channels, int sampleRate)
    {
        var fmtChunk = (byte[])DefaultXma2FmtChunk.Clone();
        fmtChunk[10] = (byte)channels;
        fmtChunk[11] = (byte)(channels >> 8);
        fmtChunk[12] = (byte)sampleRate;
        fmtChunk[13] = (byte)(sampleRate >> 8);
        fmtChunk[14] = (byte)(sampleRate >> 16);
        fmtChunk[15] = (byte)(sampleRate >> 24);

        var seekChunkSize = 8 + seekTable.Length;
        var dataChunkSize = 8 + audioData.Length;
        var totalSize = 4 + fmtChunk.Length + seekChunkSize + dataChunkSize;

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        bw.Write("RIFF"u8);
        bw.Write(totalSize);
        bw.Write("WAVE"u8);
        bw.Write(fmtChunk);
        bw.Write("seek"u8);
        bw.Write(seekTable.Length);
        bw.Write(seekTable);
        bw.Write("data"u8);
        bw.Write(audioData.Length);
        bw.Write(audioData);

        return ms.ToArray();
    }

    private static int FindChunk(byte[] data, ReadOnlySpan<byte> chunkId)
    {
        var offset = 12;
        while (offset < data.Length - 8)
        {
            if (data.AsSpan(offset, 4).SequenceEqual(chunkId)) return offset;

            var chunkSize = BinaryUtils.ReadUInt32LE(data.AsSpan(), offset + 4);
            if (chunkSize > 0 && chunkSize < (uint)(data.Length - offset - 8))
            {
                offset += 8 + (int)((chunkSize + 1) & ~1u);
                continue;
            }

            break;
        }

        for (var i = 12; i < data.Length - 8; i++)
            if (data.AsSpan(i, 4).SequenceEqual(chunkId))
            {
                var size = BinaryUtils.ReadUInt32LE(data.AsSpan(), i + 4);
                if (size <= 100_000_000) return i;
            }

        return -1;
    }

    #endregion
}
