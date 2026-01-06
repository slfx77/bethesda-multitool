using Xbox360MemoryCarver.Core.Utils;

namespace Xbox360MemoryCarver.Core.Formats.Xma;

/// <summary>
///     XMA file repair and XMA1→XMA2 conversion.
/// </summary>
internal static class XmaRepairer
{
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

    public static byte[] AddSeekTable(byte[] data, bool convertToXma2)
    {
        var fmtOffset = FindChunk(data, "fmt "u8);
        var dataOffset = FindChunk(data, "data"u8);

        if (fmtOffset < 0 || dataOffset < 0)
        {
            return data;
        }

        var (channels, sampleRate) = ExtractAudioParams(data, fmtOffset, convertToXma2);
        var dataSize = BinaryUtils.ReadUInt32LE(data.AsSpan(), dataOffset + 4);
        var dataStart = dataOffset + 8;
        var actualDataSize = Math.Min((int)dataSize, data.Length - dataStart);

        if (actualDataSize <= 0) return data;

        var seekTable = GenerateSeekTable(actualDataSize);
        var result = BuildXmaFile(data.AsSpan(dataStart, actualDataSize), seekTable, channels, sampleRate);

        return result;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Sonar", "S3776:Cognitive Complexity", Justification = "Audio parameter extraction requires multiple format-specific branches")]
    private static (int channels, int sampleRate) ExtractAudioParams(byte[] data, int fmtOffset, bool convertToXma2)
    {
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
            channels = data.Length > fmtOffset + 37 ? data[fmtOffset + 37] : 1;
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

        return (channels, sampleRate);
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

        // Fallback: linear scan
        for (var i = 12; i < data.Length - 8; i++)
            if (data.AsSpan(i, 4).SequenceEqual(chunkId))
            {
                var size = BinaryUtils.ReadUInt32LE(data.AsSpan(), i + 4);
                if (size <= 100_000_000) return i;
            }

        return -1;
    }
}
