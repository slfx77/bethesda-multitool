using Xbox360MemoryCarver.Core.Utils;

namespace Xbox360MemoryCarver.Core.Converters;

/// <summary>
///     Utility for repairing corrupted XMA files and adding seek tables.
/// </summary>
public static class XmaRepairUtil
{
    /// <summary>
    ///     Default sample rate for XMA.
    /// </summary>
    private const int DefaultSampleRate = 44100;

    /// <summary>
    ///     XMA packet size in bytes.
    /// </summary>
    private const int XmaPacketSize = 2048;

    /// <summary>
    ///     Default XMA2 format parameters for repair/conversion.
    /// </summary>
    private static readonly byte[] DefaultXma2FmtChunk =
    [
        // fmt chunk header
        0x66, 0x6D, 0x74, 0x20, // "fmt "
        0x34, 0x00, 0x00, 0x00, // chunk size: 52 bytes (XMA2WAVEFORMATEX)
        // WAVEFORMATEX base
        0x66, 0x01,             // wFormatTag: 0x0166 (XMA2)
        0x01, 0x00,             // nChannels: 1 (mono, will be updated)
        0x44, 0xAC, 0x00, 0x00, // nSamplesPerSec: 44100
        0x00, 0x00, 0x01, 0x00, // nAvgBytesPerSec: 65536
        0x00, 0x08,             // nBlockAlign: 2048
        0x10, 0x00,             // wBitsPerSample: 16
        0x22, 0x00,             // cbSize: 34 (extra bytes for XMA2)
        // XMA2 extension (34 bytes)
        0x01, 0x00,             // NumStreams: 1
        0x00, 0x00, 0x00, 0x00, // ChannelMask: 0
        0x00, 0x00, 0x01, 0x00, // SamplesEncoded (placeholder)
        0x00, 0x20, 0x00, 0x00, // BytesPerBlock: 8192
        0x00, 0x00, 0x00, 0x00, // PlayBegin
        0x00, 0x00, 0x00, 0x00, // PlayLength
        0x00, 0x00, 0x00, 0x00, // LoopBegin
        0x00, 0x00, 0x00, 0x00, // LoopLength
        0x00,                   // LoopCount
        0x04,                   // EncoderVersion
        0x00, 0x10              // BlockCount (placeholder)
    ];

    /// <summary>
    ///     Attempt to repair/enhance an XMA file.
    /// </summary>
    /// <param name="data">The original XMA data.</param>
    /// <param name="metadata">Parser metadata with repair info.</param>
    /// <returns>Repaired/enhanced XMA data, or original if not needed/possible.</returns>
    public static byte[] TryRepair(byte[] data, IDictionary<string, object>? metadata)
    {
        var needsRepair = metadata?.TryGetValue("needsRepair", out var repair) == true && repair is true;
        var needsSeek = metadata?.TryGetValue("hasSeekChunk", out var hasSeek) == true && hasSeek is false;
        var isXma1 = metadata?.TryGetValue("formatTag", out var fmt) == true && fmt is ushort tag && tag == 0x0165;

        if (!needsRepair && !needsSeek && !isXma1)
        {
            return data;
        }

        try
        {
            if (needsRepair)
            {
                return RepairCorruptedXma(data);
            }

            if (isXma1 || needsSeek)
            {
                return AddSeekTable(data, isXma1);
            }

            return data;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[XmaRepair] Failed: {ex.Message}");
            return data;
        }
    }

    /// <summary>
    ///     Repair a corrupted XMA file by finding data chunk and creating valid structure.
    /// </summary>
    private static byte[] RepairCorruptedXma(byte[] data)
    {
        var dataChunkOffset = FindChunk(data, "data"u8);
        if (dataChunkOffset < 0)
        {
            Console.WriteLine("[XmaRepair] No data chunk found, cannot repair");
            return data;
        }

        var dataChunkSize = BinaryUtils.ReadUInt32LE(data.AsSpan(), dataChunkOffset + 4);
        var dataStart = dataChunkOffset + 8;
        var dataEnd = Math.Min(dataStart + (int)dataChunkSize, data.Length);
        var actualDataSize = dataEnd - dataStart;

        if (actualDataSize <= 0)
        {
            Console.WriteLine("[XmaRepair] Data chunk is empty, cannot repair");
            return data;
        }

        // Generate seek table
        var seekTable = GenerateSeekTable(actualDataSize);

        // Build repaired file: RIFF + WAVE + fmt + seek + data
        return BuildXmaFile(data.AsSpan(dataStart, actualDataSize), seekTable, 1, DefaultSampleRate);
    }

    /// <summary>
    ///     Add a seek table to an XMA file (and convert XMA1 to XMA2 if needed).
    /// </summary>
    private static byte[] AddSeekTable(byte[] data, bool convertToXma2)
    {
        // Find existing chunks
        var fmtOffset = FindChunk(data, "fmt "u8);
        var dataOffset = FindChunk(data, "data"u8);

        if (fmtOffset < 0 || dataOffset < 0)
        {
            Console.WriteLine("[XmaRepair] Missing fmt or data chunk");
            return data;
        }

        // Read format tag to determine XMA1 vs XMA2 layout
        var formatTag = data.Length > fmtOffset + 10 ? BinaryUtils.ReadUInt16LE(data.AsSpan(), fmtOffset + 8) : (ushort)0;

        int channels;
        int sampleRate;

        if (formatTag == 0x0165 || convertToXma2)
        {
            // XMA1WAVEFORMAT structure (different from WAVEFORMATEX):
            //   Offset 0-1: wFormatTag (0x0165)
            //   Offset 2-3: wBitsPerSample
            //   Offset 4-5: EncodeOptions
            //   Offset 6-7: LargestSkip
            //   Offset 8-9: NumStreams
            //   Offset 10: LoopCount
            //   Offset 11: Version
            //   XmaStreamData[0] at offset 12:
            //     Offset 0-3: PseudoBytesPerSec
            //     Offset 4-7: SampleRate
            //     Offset 8-11: LoopStart
            //     Offset 12-15: LoopEnd
            //     Offset 16: SubframeData
            //     Offset 17: Channels (single byte!)
            //     Offset 18-19: ChannelMask
            sampleRate = data.Length > fmtOffset + 24
                ? (int)BinaryUtils.ReadUInt32LE(data.AsSpan(), fmtOffset + 24)  // fmt+16 = XmaStreamData.SampleRate
                : DefaultSampleRate;
            channels = data.Length > fmtOffset + 37
                ? data[fmtOffset + 37]  // fmt+29 = XmaStreamData.Channels (single byte)
                : 1;

            Console.WriteLine($"[XmaRepair] XMA1 detected: {channels} channels, {sampleRate} Hz");
        }
        else
        {
            // XMA2/WAVEFORMATEX structure:
            //   Offset 0-1: wFormatTag
            //   Offset 2-3: nChannels
            //   Offset 4-7: nSamplesPerSec
            channels = data.Length > fmtOffset + 10 ? BinaryUtils.ReadUInt16LE(data.AsSpan(), fmtOffset + 10) : 1;
            sampleRate = data.Length > fmtOffset + 12
                ? (int)BinaryUtils.ReadUInt32LE(data.AsSpan(), fmtOffset + 12)
                : DefaultSampleRate;
        }

        // Sanity check values
        if (channels < 1 || channels > 8)
        {
            Console.WriteLine($"[XmaRepair] Invalid channel count {channels}, using 1");
            channels = 1;
        }
        if (sampleRate < 8000 || sampleRate > 96000)
        {
            Console.WriteLine($"[XmaRepair] Invalid sample rate {sampleRate}, using 44100");
            sampleRate = DefaultSampleRate;
        }

        // Get data chunk
        var dataSize = BinaryUtils.ReadUInt32LE(data.AsSpan(), dataOffset + 4);
        var dataStart = dataOffset + 8;
        var actualDataSize = Math.Min((int)dataSize, data.Length - dataStart);

        if (actualDataSize <= 0)
        {
            return data;
        }

        // Generate seek table
        var seekTable = GenerateSeekTable(actualDataSize);

        // Build new file with seek table
        var result = BuildXmaFile(data.AsSpan(dataStart, actualDataSize), seekTable, channels, sampleRate);

        Console.WriteLine($"[XmaRepair] Added seek table: {data.Length} -> {result.Length} bytes, {channels} ch, {sampleRate} Hz ({seekTable.Length / 4} entries)");
        return result;
    }

    /// <summary>
    ///     Generate a seek table for XMA data.
    ///     Each entry is a big-endian uint32 representing cumulative samples decoded at that point.
    /// </summary>
    private static byte[] GenerateSeekTable(int dataSize)
    {
        // Calculate number of XMA packets
        var numPackets = (dataSize + XmaPacketSize - 1) / XmaPacketSize;

        // One seek entry per packet (at minimum), but typically one per 2048 samples
        // For simplicity, we'll create one entry per packet
        var numEntries = Math.Max(1, numPackets);

        // Estimate samples per packet (rough approximation)
        // XMA typically decodes to about 512 samples per subframe, 8 subframes per packet
        const int samplesPerPacket = 512 * 8;

        var seekTable = new byte[numEntries * 4];
        for (var i = 0; i < numEntries; i++)
        {
            var cumulativeSamples = (uint)((i + 1) * samplesPerPacket);
            // Write as big-endian
            seekTable[i * 4] = (byte)(cumulativeSamples >> 24);
            seekTable[i * 4 + 1] = (byte)(cumulativeSamples >> 16);
            seekTable[i * 4 + 2] = (byte)(cumulativeSamples >> 8);
            seekTable[i * 4 + 3] = (byte)cumulativeSamples;
        }

        return seekTable;
    }

    /// <summary>
    ///     Build a complete XMA2 file with fmt, seek, and data chunks.
    /// </summary>
    private static byte[] BuildXmaFile(ReadOnlySpan<byte> audioData, byte[] seekTable, int channels, int sampleRate)
    {
        var fmtChunk = (byte[])DefaultXma2FmtChunk.Clone();

        // Update channels
        fmtChunk[10] = (byte)channels;
        fmtChunk[11] = (byte)(channels >> 8);

        // Update sample rate
        fmtChunk[12] = (byte)sampleRate;
        fmtChunk[13] = (byte)(sampleRate >> 8);
        fmtChunk[14] = (byte)(sampleRate >> 16);
        fmtChunk[15] = (byte)(sampleRate >> 24);

        // Calculate total RIFF size
        var seekChunkSize = 8 + seekTable.Length; // "seek" + size + data
        var dataChunkSize = 8 + audioData.Length; // "data" + size + data  
        var totalSize = 4 + fmtChunk.Length + seekChunkSize + dataChunkSize; // WAVE + chunks

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        // RIFF header
        bw.Write("RIFF"u8);
        bw.Write(totalSize);
        bw.Write("WAVE"u8);

        // fmt chunk
        bw.Write(fmtChunk);

        // seek chunk
        bw.Write("seek"u8);
        bw.Write(seekTable.Length);
        bw.Write(seekTable);

        // data chunk
        bw.Write("data"u8);
        bw.Write(audioData.Length);
        bw.Write(audioData);

        return ms.ToArray();
    }

    /// <summary>
    ///     Find a RIFF chunk by its 4-character ID.
    ///     Uses linear scan since corrupted files may have invalid chunk sizes.
    /// </summary>
    private static int FindChunk(byte[] data, ReadOnlySpan<byte> chunkId)
    {
        // First, try proper RIFF chunk navigation (works for valid files)
        var offset = 12; // Start after RIFF header
        while (offset < data.Length - 8)
        {
            if (data.AsSpan(offset, 4).SequenceEqual(chunkId))
            {
                return offset;
            }

            var chunkSize = BinaryUtils.ReadUInt32LE(data.AsSpan(), offset + 4);

            // If chunk size is reasonable, navigate to next chunk
            if (chunkSize > 0 && chunkSize < (uint)(data.Length - offset - 8))
            {
                // Word-align
                offset += 8 + (int)((chunkSize + 1) & ~1u);
                continue;
            }

            // Chunk size is invalid - fall back to linear scan
            break;
        }

        // Linear scan fallback for corrupted files
        for (var i = 12; i < data.Length - 8; i++)
        {
            if (data.AsSpan(i, 4).SequenceEqual(chunkId))
            {
                // Validate that the size looks reasonable
                var size = BinaryUtils.ReadUInt32LE(data.AsSpan(), i + 4);
                if (size > 0 && size <= (uint)(data.Length - i - 8))
                {
                    return i;
                }
                // Size might still be corrupted but at least we found the marker
                // Accept if it's at least plausible
                if (size <= 100_000_000) // 100 MB max
                {
                    return i;
                }
            }
        }

        return -1;
    }
}
