using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using BethesdaMultitool.Core.Formats.Redguard;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Redguard;

/// <summary>
///     Synthetic vectors for <see cref="RedguardRtxFile" />, shaped after the retail
///     <c>ENGLISH.RTX</c> measured 2026-09-05. The index is written in REVERSE file order
///     throughout, because retail's index is in no meaningful order and the reader must not
///     depend on it.
/// </summary>
public sealed class RedguardRtxFileTests : IDisposable
{
    private readonly List<string> _tempFiles = [];

    public void Dispose()
    {
        foreach (var path in _tempFiles)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // Temp cleanup only.
            }
        }
    }

    private string WriteTemp(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"redguard-rtx-{Guid.NewGuid():N}.rtx");
        File.WriteAllBytes(path, bytes);
        _tempFiles.Add(path);
        return path;
    }

    private static byte[] Payload(string text, byte[]? pcm = null, int sampleRate = 22050, bool sixteenBit = true)
    {
        var bytes = new List<byte>();
        bytes.AddRange(BitConverter.GetBytes((ushort)0)); // placeholder for the BE kind word
        var kind = (ushort)(pcm is null ? 0 : RedguardRtxFile.VoicedKind);
        BinaryPrimitives.WriteUInt16BigEndian(CollectionsMarshal.AsSpan(bytes), kind);
        bytes.AddRange(BitConverter.GetBytes((uint)text.Length));
        bytes.AddRange(Encoding.ASCII.GetBytes(text));
        if (pcm is not null)
        {
            var header = new byte[RedguardPcmHeader.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(header, sixteenBit ? 1u : 0u);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(RedguardPcmHeader.DepthFlagOffset),
                sixteenBit ? 1u : 0u);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(RedguardPcmHeader.SampleRateOffset),
                (uint)sampleRate);
            header[12] = 100;
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(18), 0xFFFFFFFF);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(RedguardPcmHeader.LengthOffset), (uint)pcm.Length);
            bytes.AddRange(header);
            bytes.AddRange(pcm);
        }

        return [.. bytes];
    }

    private static byte[] Build(params (string Tag, byte[] Payload)[] records)
    {
        var file = new List<byte>();
        var index = new List<(string Tag, uint Offset, uint Length)>();
        foreach (var (tag, payload) in records)
        {
            file.AddRange(Encoding.ASCII.GetBytes(tag));
            var length = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(length, (uint)payload.Length);
            file.AddRange(length);
            index.Add((tag, (uint)file.Count, (uint)payload.Length));
            file.AddRange(payload);
        }

        file.AddRange("END "u8.ToArray());
        var indexOffset = (uint)file.Count;
        foreach (var (tag, offset, length) in Enumerable.Reverse(index))
        {
            file.AddRange(Encoding.ASCII.GetBytes(tag));
            file.AddRange(BitConverter.GetBytes(offset));
            file.AddRange(BitConverter.GetBytes(length));
        }

        file.AddRange("RNAV"u8.ToArray());
        file.AddRange(BitConverter.GetBytes(indexOffset));
        file.AddRange(BitConverter.GetBytes((uint)index.Count));
        return [.. file];
    }

    [Fact]
    public void Open_ReadsTextAndVoiceInFileOrderWhateverTheIndexOrder()
    {
        using var file = RedguardRtxFile.Open(WriteTemp(Build(
            ("#bon", Payload("BOATMAN BONE SOUND", [1, 0, 2, 0])),
            ("xtor", Payload("EXAMINE TORCH")),
            ("zbza", Payload("GET BACK IN YOUR JAR.", new byte[441], 11025, false)))));

        Assert.Equal(["#bon", "xtor", "zbza"], file.Entries.Select(e => e.Tag));
        Assert.Equal([0, 1, 2], file.Entries.Select(e => e.Index));

        var bone = file.Entries[0];
        Assert.Equal(("BOATMAN BONE SOUND", true, 22050, 16),
            (bone.Text, bone.IsVoiced, bone.Sound!.Value.SampleRate, bone.Sound.Value.BitsPerSample));
        Assert.Equal([1, 0, 2, 0], file.ReadSamples(bone));

        Assert.Equal(("EXAMINE TORCH", false), (file.Entries[1].Text, file.Entries[1].IsVoiced));
        Assert.Throws<InvalidOperationException>(() => file.ReadSamples(file.Entries[1]));

        var jar = file.Find("zbza")!;
        Assert.Equal((11025, 8, 441),
            (jar.Sound!.Value.SampleRate, jar.Sound.Value.BitsPerSample, jar.Sound.Value.ByteLength));
        Assert.Equal(441 / 11025.0, jar.Sound.Value.DurationSeconds, 6);
        Assert.Null(file.Find("none"));
    }

    [Fact]
    public void Open_RecordLengthsAreBigEndianAndIndexOffsetsLittleEndian()
    {
        var bytes = Build(("#bon", Payload("X")));

        // Record header at 0: tag then BE length 7 (2 + 4 + 1).
        Assert.Equal(7u, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(4)));

        // Index entry: tag, LE offset 8, LE length 7.
        var indexOffset = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(bytes.Length - 8));
        Assert.Equal(8u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan((int)indexOffset + 4)));
        Assert.Equal(7u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan((int)indexOffset + 8)));
    }

    [Fact]
    public void Open_TrailerThatDoesNotAccountForTheFile_Throws()
    {
        var bytes = Build(("#bon", Payload("X")));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(bytes.Length - 4), 2); // claims two entries

        Assert.Throws<InvalidDataException>(() => RedguardRtxFile.Open(WriteTemp(bytes)));
    }

    [Fact]
    public void Open_IndexEntryDisagreeingWithItsRecordHeader_Throws()
    {
        var bytes = Build(("#bon", Payload("XY")), ("xtor", Payload("Z")));
        var indexOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(bytes.Length - 8));

        // Point the first index entry one byte late.
        var offsetAt = indexOffset + 4;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offsetAt),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offsetAt)) + 1);

        Assert.Throws<InvalidDataException>(() => RedguardRtxFile.Open(WriteTemp(bytes)));
    }

    [Fact]
    public void Open_MissingEndBeforeTheIndex_Throws()
    {
        var bytes = Build(("#bon", Payload("X")));
        var indexOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(bytes.Length - 8));
        bytes[indexOffset - 4] = (byte)'X';

        Assert.Throws<InvalidDataException>(() => RedguardRtxFile.Open(WriteTemp(bytes)));
    }

    [Fact]
    public void Open_VoicedRecordWhosePartsDoNotSumToItsLength_Throws()
    {
        var bytes = Build(("#bon", Payload("X", [1, 0, 2, 0])));

        // Shrink the PCM byte count in the sound header: the record now has 2 unexplained bytes.
        var pcmLengthAt = 8 + 2 + 4 + 1 + RedguardPcmHeader.LengthOffset;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(pcmLengthAt), 2);

        Assert.Throws<InvalidDataException>(() => RedguardRtxFile.Open(WriteTemp(bytes)));
    }

    [Fact]
    public void IsRtxFile_NeedsTheTrailer()
    {
        Assert.True(RedguardRtxFile.IsRtxFile(WriteTemp(Build(("#bon", Payload("X"))))));
        Assert.False(RedguardRtxFile.IsRtxFile(WriteTemp(Encoding.ASCII.GetBytes(new string('x', 64)))));
    }
}