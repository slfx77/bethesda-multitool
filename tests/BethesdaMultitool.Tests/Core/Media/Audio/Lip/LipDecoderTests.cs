using System.Buffers.Binary;
using System.Text.Json;
using BethesdaMultitool.CLI.Commands.Audio;
using BethesdaMultitool.Core.Formats.Lip;
using BethesdaMultitool.Core.Media.Audio.Lip;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Media.Audio.Lip;

/// <summary>Exercises byte-level LIP framing, bounds and inspection without proprietary fixtures.</summary>
public sealed class LipDecoderTests
{
    /// <summary>Checks a seventeen-byte zero run that ends inside a float, independent of the fixture encoder.</summary>
    [Fact]
    public void ZeroRunsMayEndInsideFloatValues()
    {
        byte[] file = [1, 0, 0, 0, 156, 0, 0, 0, 1, 0, 0, 0,
            1, 0, 3, 0, 255, 255, 255, 255, 0, 17, 0, 176, 217, 59, 0, 112, 0];
        var timeline = LipDecoder.Decode(file, TestContext.Current.CancellationToken);
        Assert.Equal(0x3BD9B000, BitConverter.SingleToInt32Bits(timeline.GetValue(0, 4)));
        Assert.Equal(0, timeline.GetValue(0, 5));
    }

    /// <summary>Checks source ordering, signed and above-one weights, a partial-float zero run and native timing.</summary>
    [Fact]
    public void DecodeRetainsExactWeightsAndTrackIdentity()
    {
        var body = CreateBody(2, -5);
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(8), 1.25f);
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(8 + 30 * 4), -0.125f);
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(8 + 33 * 4), BitConverter.Int32BitsToSingle(0x3BD9B000));
        var file = Encode(body);
        var timeline = LipDecoder.Decode(file, TestContext.Current.CancellationToken);
        Assert.Equal(2, timeline.FrameCount);
        Assert.Equal(-5, timeline.StartingFrame);
        Assert.Equal(1.25f, timeline.GetValue(0, 0));
        Assert.Equal(-0.125f, timeline.GetValue(0, 30));
        Assert.Equal(0x3BD9B000, BitConverter.SingleToInt32Bits(timeline.GetValue(1, 0)));
        Assert.Equal("Aah", LipTimeline.Tracks[0].Name);
        Assert.Equal("W", LipTimeline.Tracks[15].Name);
        Assert.Equal("BlinkLeft", LipTimeline.Tracks[16].Name);
        Assert.Equal("HeadYaw", LipTimeline.Tracks[32].Name);
        Assert.Equal(1d / 30, timeline.GetRelativeTimeSeconds(1));
        Assert.Equal(file.Length, timeline.EncodedSize);
        Assert.Throws<ArgumentOutOfRangeException>(() => timeline.GetValue(-1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => timeline.GetValue(0, 33));
        Assert.Throws<ArgumentOutOfRangeException>(() => timeline.GetRelativeTimeSeconds(2));
    }

    /// <summary>Rejects headers for variants that have not been validated against this codec and layout.</summary>
    [Theory]
    [InlineData(2, 1)]
    [InlineData(1, 0)]
    [InlineData(1, 3)]
    [InlineData(1, 13)]
    public void RejectsUnsupportedRevisionOrFlags(uint revision, uint flags)
    {
        var data = Encode(CreateBody(1, 0));
        BinaryPrimitives.WriteUInt32LittleEndian(data, revision);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), flags);
        Assert.Throws<NotSupportedException>(() => LipDecoder.Decode(data, TestContext.Current.CancellationToken));
    }

    /// <summary>Detects every truncation of a valid file, zero runs and surplus payload bytes.</summary>
    [Fact]
    public void RejectsTruncationTrailingBytesAndZeroRuns()
    {
        var data = Encode(CreateBody(1, -1));
        for (var size = 0; size < data.Length; size++)
        {
            var truncated = data[..size];
            Assert.Throws<InvalidDataException>(() => LipDecoder.Decode(truncated, TestContext.Current.CancellationToken));
        }
        Assert.Throws<InvalidDataException>(() => LipDecoder.Decode([.. data, 42], TestContext.Current.CancellationToken));
        Assert.Throws<InvalidDataException>(() => LipDecoder.Decode([.. data[..12], 0, 0, 0], TestContext.Current.CancellationToken));
        Assert.Throws<InvalidDataException>(() => LipDecoder.Decode([.. data[..12], 0, 255, 255], TestContext.Current.CancellationToken));
    }

    /// <summary>Checks declared allocation bounds, row shape and non-finite sample rejection.</summary>
    [Fact]
    public void RejectsInvalidSizeCountAndNonFiniteSamples()
    {
        var data = Encode(CreateBody(1, 0));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), uint.MaxValue);
        Assert.Throws<InvalidDataException>(() => LipDecoder.Decode(data, TestContext.Current.CancellationToken));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 23);
        Assert.Throws<InvalidDataException>(() => LipDecoder.Decode(data, TestContext.Current.CancellationToken));
        var body = CreateBody(1, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(body, uint.MaxValue);
        Assert.Throws<InvalidDataException>(() => LipDecoder.Decode(Encode(body), TestContext.Current.CancellationToken));
        body = CreateBody(1, 0);
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(8), float.NaN);
        Assert.Throws<InvalidDataException>(() => LipDecoder.Decode(Encode(body), TestContext.Current.CancellationToken));
    }

    /// <summary>Ensures cancellation prevents decode and JSON output rather than returning partial success.</summary>
    [Fact]
    public void CancellationIsObserved()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var file = Encode(CreateBody(1, 0));
        Assert.Throws<OperationCanceledException>(() => LipDecoder.Decode(file, cancellation.Token));
        using var output = new MemoryStream();
        Assert.Throws<OperationCanceledException>(() => LipCommand.WriteJson(output, LipDecoder.Decode(file, TestContext.Current.CancellationToken), true, cancellation.Token));
    }

    /// <summary>Validates machine-readable inspection and caller-owned output stream lifetime.</summary>
    [Fact]
    public void JsonInspectionIncludesSamplesAndExplicitTimeOrigin()
    {
        using var output = new MemoryStream();
        LipCommand.WriteJson(output, LipDecoder.Decode(Encode(CreateBody(2, 4)), TestContext.Current.CancellationToken), true, TestContext.Current.CancellationToken);
        Assert.True(output.CanWrite);
        using var document = JsonDocument.Parse(output.ToArray());
        Assert.Equal(33, document.RootElement.GetProperty("tracks").GetArrayLength());
        Assert.Equal(2, document.RootElement.GetProperty("samples").GetArrayLength());
        Assert.Contains("audio alignment not applied", document.RootElement.GetProperty("timeOrigin").GetString());
    }

    /// <summary>Prevents regression to treating the expanded header size as the encoded file length.</summary>
    [Fact]
    public void FormatUsesValidatedEncodedLength()
    {
        var file = Encode(CreateBody(2, 0));
        var format = new LipFormat();
        Assert.Equal(file.Length, format.Parse(file)!.EstimatedSize);
        Assert.Null(format.Parse(file, -1));
        Assert.Null(format.Parse([.. file, 7]));
        Assert.False(format.EnableSignatureScanning);
    }

    /// <summary>Creates a synthetic decoded body with the authoritative row shape.</summary>
    private static byte[] CreateBody(int frames, int start)
    {
        var result = new byte[8 + frames * 132];
        BinaryPrimitives.WriteInt32LittleEndian(result, frames);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(4), start);
        return result;
    }

    /// <summary>Encodes independent synthetic fixtures by grouping bytes, without float-specific marker assumptions.</summary>
    private static byte[] Encode(byte[] body)
    {
        var output = new List<byte>(body.Length + 12);
        var header = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 1);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), body.Length + 16);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), 1);
        output.AddRange(header);
        for (var position = 0; position < body.Length;)
        {
            if (body[position] != 0) { output.Add(body[position++]); continue; }
            var end = position;
            while (end < body.Length && body[end] == 0 && end - position < ushort.MaxValue) end++;
            var count = end - position;
            output.Add(0);
            output.Add((byte)count);
            output.Add((byte)(count >> 8));
            position = end;
        }
        return output.ToArray();
    }
}
