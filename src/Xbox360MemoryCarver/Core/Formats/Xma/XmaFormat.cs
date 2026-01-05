using System.Diagnostics;
using System.Text;
using Xbox360MemoryCarver.Core.Converters;
using Xbox360MemoryCarver.Core.Utils;

namespace Xbox360MemoryCarver.Core.Formats.Xma;

/// <summary>
///     Xbox Media Audio (XMA) format module.
///     Handles parsing, repair, XMA1→XMA2 conversion, and XMA→WAV decoding.
/// </summary>
public sealed class XmaFormat : FileFormatBase, IFileRepairer, IFileConverter
{
    private static readonly ushort[] XmaFormatCodes = [0x0165, 0x0166];
    private int _convertedCount;
    private int _failedCount;
    private bool _initialized;
    private bool _verbose;
    private string? _ffmpegPath;

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
        var baseName = "Xbox Media Audio";

        if (metadata?.TryGetValue("embeddedPath", out var path) == true && path is string pathStr)
        {
            var fileName = Path.GetFileName(pathStr);
            if (!string.IsNullOrEmpty(fileName)) baseName = $"XMA ({fileName})";
        }

        // Add quality indicator based on usable percentage (audio before first corruption)
        if (metadata?.TryGetValue("usablePercent", out var usable) == true && usable is int usablePct && usablePct < 80)
        {
            return $"{baseName} [~{usablePct}% playable]";
        }

        return baseName;
    }

    #region IFileRepairer

    public bool NeedsRepair(IReadOnlyDictionary<string, object>? metadata)
    {
        if (metadata == null) return false;

        // Don't repair corrupted files - we'll convert them to WAV instead
        // which preserves all recoverable audio
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

        // Don't do truncation repair here - corrupted files will be converted to WAV
        if (!needsRepair && !needsSeek && !isXma1) return data;

        try
        {
            // Only do structural repairs (XMA1→XMA2, add seek table)
            // Corrupted files are handled by WAV conversion which preserves all recoverable audio
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

    #region IFileConverter - XMA to WAV decoding

    public string TargetExtension => ".wav";
    public string TargetFolder => "audio_wav";
    public bool IsInitialized => _initialized;
    public int ConvertedCount => _convertedCount;
    public int FailedCount => _failedCount;

    public bool Initialize(bool verbose = false, Dictionary<string, object>? options = null)
    {
        _verbose = verbose;

        // Look for FFmpeg in PATH or common locations
        _ffmpegPath = FindFfmpeg();

        if (_ffmpegPath == null)
        {
            if (verbose)
            {
                Console.WriteLine("[XmaFormat] FFmpeg not found - XMA to WAV conversion disabled");
                Console.WriteLine("[XmaFormat] Install FFmpeg and add to PATH for XMA→WAV conversion");
            }
            return false;
        }

        if (verbose)
        {
            Console.WriteLine($"[XmaFormat] FFmpeg found at: {_ffmpegPath}");
        }

        _initialized = true;
        return true;
    }

    public bool CanConvert(string signatureId, IReadOnlyDictionary<string, object>? metadata)
    {
        // Convert corrupted files to WAV - this extracts all recoverable audio
        if (metadata?.TryGetValue("likelyCorrupted", out var corrupt) == true && corrupt is true)
        {
            return true;
        }

        // Also convert any XMA that user wants as WAV
        return metadata?.TryGetValue("convertToWav", out var convert) == true && convert is true;
    }

    public async Task<DdxConversionResult> ConvertAsync(byte[] data,
        IReadOnlyDictionary<string, object>? metadata = null)
    {
        if (!_initialized || _ffmpegPath == null)
        {
            return new DdxConversionResult { Success = false, Notes = "FFmpeg not available" };
        }

        try
        {
            var result = await DecodeXmaToWavAsync(data);

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
            return new DdxConversionResult { Success = false, Notes = $"Exception: {ex.Message}" };
        }
    }

    /// <summary>
    ///     Decodes XMA audio to WAV using FFmpeg.
    ///     Extracts whatever audio is recoverable before corruption.
    /// </summary>
    private async Task<DdxConversionResult> DecodeXmaToWavAsync(byte[] xmaData)
    {
        // Create temp files for input and output
        var tempDir = Path.GetTempPath();
        var inputPath = Path.Combine(tempDir, $"xma_decode_{Guid.NewGuid():N}.xma");
        var outputPath = Path.Combine(tempDir, $"xma_decode_{Guid.NewGuid():N}.wav");

        try
        {
            // Write XMA data to temp file
            await File.WriteAllBytesAsync(inputPath, xmaData);

            // Run FFmpeg to decode XMA to WAV
            // -y: overwrite output
            // -hide_banner: reduce noise
            // -loglevel error: only show errors
            // -i: input file
            // -c:a pcm_s16le: output as 16-bit PCM WAV
            var startInfo = new ProcessStartInfo
            {
                FileName = _ffmpegPath,
                Arguments = $"-y -hide_banner -loglevel error -i \"{inputPath}\" -c:a pcm_s16le \"{outputPath}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = startInfo };
            process.Start();

            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0 || !File.Exists(outputPath))
            {
                // FFmpeg failed completely
                if (_verbose && !string.IsNullOrEmpty(stderr))
                {
                    Console.WriteLine($"[XmaFormat] FFmpeg error: {stderr.Trim()}");
                }
                return new DdxConversionResult { Success = false, Notes = "FFmpeg decode failed" };
            }

            // Read the output WAV
            var wavData = await File.ReadAllBytesAsync(outputPath);

            // Check if we got any audio (WAV header is 44 bytes minimum)
            if (wavData.Length <= 44)
            {
                return new DdxConversionResult { Success = false, Notes = "No audio decoded" };
            }

            if (_verbose)
            {
                var duration = EstimateWavDuration(wavData);
                Console.WriteLine($"[XmaFormat] Decoded {xmaData.Length} bytes XMA → {wavData.Length} bytes WAV ({duration:F2}s)");
            }

            return new DdxConversionResult
            {
                Success = true,
                DdsData = wavData, // Reusing DdsData field for WAV output
                Notes = "Decoded to WAV"
            };
        }
        finally
        {
            // Clean up temp files - ignore deletion failures (file may be locked or already deleted)
            try { if (File.Exists(inputPath)) File.Delete(inputPath); } catch { /* Cleanup failures are non-critical */ }
            try { if (File.Exists(outputPath)) File.Delete(outputPath); } catch { /* Cleanup failures are non-critical */ }
        }
    }

    private static string? FindFfmpeg()
    {
        // Check if ffmpeg is in PATH
        var pathDirs = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];

        foreach (var dir in pathDirs)
        {
            var ffmpegPath = Path.Combine(dir, "ffmpeg.exe");
            if (File.Exists(ffmpegPath)) return ffmpegPath;

            // Also check without .exe for cross-platform
            ffmpegPath = Path.Combine(dir, "ffmpeg");
            if (File.Exists(ffmpegPath)) return ffmpegPath;
        }

        // Check common Windows locations using environment-based paths where possible
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var systemDrive = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";

        var commonPaths = new[]
        {
            Path.Combine(systemDrive, "ffmpeg", "bin", "ffmpeg.exe"),
            Path.Combine(programFiles, "ffmpeg", "bin", "ffmpeg.exe"),
            Path.Combine(programFilesX86, "ffmpeg", "bin", "ffmpeg.exe"),
            Path.Combine(localAppData, "ffmpeg", "bin", "ffmpeg.exe")
        };

        return commonPaths.FirstOrDefault(File.Exists);
    }

    private static double EstimateWavDuration(byte[] wavData)
    {
        if (wavData.Length < 44) return 0;

        // WAV header: bytes 28-31 = byte rate
        var byteRate = BitConverter.ToInt32(wavData, 28);
        if (byteRate <= 0) return 0;

        // Data size is file size minus header (approximately)
        var dataSize = wavData.Length - 44;
        return (double)dataSize / byteRate;
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

        // Estimate data quality by checking for corruption patterns
        var (totalQuality, usablePercent) = dataChunkOffset.HasValue && dataChunkSize.HasValue
            ? EstimateDataQuality(data, dataChunkOffset.Value + 8, dataChunkSize.Value)
            : (100, 100);

        var metadata = new Dictionary<string, object>
        {
            ["isXma"] = true,
            ["formatTag"] = formatTag.Value,
            ["hasSeekChunk"] = hasSeekChunk,
            ["qualityEstimate"] = totalQuality,
            ["usablePercent"] = usablePercent
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

        // Flag potentially corrupted files (low usable audio)
        if (usablePercent < 50)
        {
            metadata["likelyCorrupted"] = true;
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

    /// <summary>
    ///     Estimates data quality by detecting corruption patterns.
    ///     Memory dumps often contain partially overwritten audio buffers.
    /// </summary>
    /// <param name="data">The data buffer.</param>
    /// <param name="dataStart">Start offset of the audio data.</param>
    /// <param name="dataSize">Size of the audio data.</param>
    /// <returns>
    ///     Tuple of (totalQuality, usablePercent) where:
    ///     - totalQuality: percentage of data that isn't corrupted (0-100)
    ///     - usablePercent: percentage of data before first major corruption (0-100)
    /// </returns>
    private static (int TotalQuality, int UsablePercent) EstimateDataQuality(ReadOnlySpan<byte> data, int dataStart,
        int dataSize)
    {
        if (dataSize <= 0 || dataStart >= data.Length)
            return (100, 100);

        var corruptBytes = 0;
        var endOffset = Math.Min(dataStart + dataSize, data.Length);

        // Track first corruption location
        var firstCorruptionOffset = -1;

        // Scan for 8+ consecutive 0xFF or 0x00 bytes (common corruption patterns)
        var runLength = 0;
        byte? runByte = null;
        var runStart = dataStart;

        for (var i = dataStart; i < endOffset; i++)
        {
            var b = data[i];

            if (b == 0xFF || b == 0x00)
            {
                if (runByte == b)
                {
                    runLength++;
                }
                else
                {
                    // End of previous run - count it if it was long enough
                    if (runLength >= 8)
                    {
                        corruptBytes += runLength;
                        if (firstCorruptionOffset < 0)
                            firstCorruptionOffset = runStart;
                    }

                    runByte = b;
                    runLength = 1;
                    runStart = i;
                }
            }
            else
            {
                // End of any potential run
                if (runLength >= 8)
                {
                    corruptBytes += runLength;
                    if (firstCorruptionOffset < 0)
                        firstCorruptionOffset = runStart;
                }

                runByte = null;
                runLength = 0;
            }
        }

        // Handle final run
        if (runLength >= 8)
        {
            corruptBytes += runLength;
            if (firstCorruptionOffset < 0)
                firstCorruptionOffset = runStart;
        }

        var totalQuality = dataSize > 0 ? Math.Max(0, 100 - (corruptBytes * 100 / dataSize)) : 100;

        // Calculate usable percent (clean data before first corruption)
        int usablePercent;
        if (firstCorruptionOffset < 0)
        {
            usablePercent = 100;
        }
        else
        {
            var cleanBytes = firstCorruptionOffset - dataStart;
            usablePercent = dataSize > 0 ? Math.Max(0, cleanBytes * 100 / dataSize) : 100;
        }

        return (totalQuality, usablePercent);
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
