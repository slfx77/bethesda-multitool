using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Animation;

public sealed class NifTes3SequenceStreamReaderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LinkedNamesFollowControllerOrderAcrossPhysicalBlockOrder(bool reversePhysicalOrder)
    {
        var data = CreateSequence(reversePhysicalOrder);
        var nif = Assert.IsType<NifInfo>(NifParser.Parse(data));
        var clip = Assert.Single(NifTes3SequenceStreamReader.ReadAll(data, nif));

        Assert.Equal("External TES3 Controller Cycle", clip.Name);
        Assert.Equal(NifCycleType.Reverse, clip.Cycle);
        Assert.Equal(2f, clip.StartTime);
        Assert.Equal(6f, clip.StopTime);
        Assert.Equal(2, clip.Tracks.Length);
        Assert.Equal(reversePhysicalOrder ? "Rock_B" : "Rock_A", clip.Tracks[0].NodeName);
        Assert.Equal(reversePhysicalOrder ? "Rock_A" : "Rock_B", clip.Tracks[1].NodeName);
        Assert.DoesNotContain(clip.Tracks, static track => track.NodeName == "Body");
        Assert.All(clip.Tracks, static track =>
        {
            Assert.Empty(track.RotationKeys);
            Assert.Empty(track.ScaleKeys);
            Assert.False(track.HasEulerRotation);
            Assert.All(track.TranslationKeys, static key => Assert.True(key.HasQuadraticTangents));
        });
        Assert.Equal(2, clip.TextKeys.Length);
    }

    [Fact]
    public void ReturnedClipOwnsItsNamesKeysAndTangents()
    {
        var data = CreateSequence(false);
        var nif = Assert.IsType<NifInfo>(NifParser.Parse(data));
        var clip = Assert.Single(NifTes3SequenceStreamReader.ReadAll(data, nif));
        var value = clip.Tracks[0].TranslationKeys[0];
        Array.Clear(data);

        Assert.Equal("Rock_A", clip.Tracks[0].NodeName);
        Assert.Equal(value, clip.Tracks[0].TranslationKeys[0]);
        Assert.True(value.HasQuadraticTangents);
        Assert.Equal(20f, value.Backward.X);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("endian")]
    [InlineData("root")]
    [InlineData("extra-loop")]
    [InlineData("controller-loop")]
    [InlineData("short-name")]
    [InlineData("unequal-chains")]
    [InlineData("duplicate-name")]
    [InlineData("target")]
    [InlineData("inactive")]
    [InlineData("zero-frequency")]
    [InlineData("phase")]
    [InlineData("mismatched-stop")]
    [InlineData("unknown-controller")]
    [InlineData("tbc")]
    [InlineData("truncated-text-key")]
    [InlineData("trailing-key-data")]
    public void MalformedOrUnsupportedGraphDoesNotPublishAnApproximateClip(string mutation)
    {
        var data = CreateSequence(false);
        var nif = Assert.IsType<NifInfo>(NifParser.Parse(data));
        var extra = nif.Blocks[3].DataOffset;
        var controller = nif.Blocks[6].DataOffset;
        switch (mutation)
        {
            case "version": nif.BinaryVersion = 0x14020007; break;
            case "endian": nif.IsBigEndian = true; break;
            case "root": WriteInt(data, data.Length - 4, 1); break;
            case "extra-loop": WriteInt(data, nif.Blocks[4].DataOffset, 3); break;
            case "controller-loop": WriteInt(data, nif.Blocks[7].DataOffset, 6); break;
            case "short-name": WriteInt(data, extra + 8, 512); break;
            case "unequal-chains": WriteInt(data, nif.Blocks[2].DataOffset, -1); break;
            case "duplicate-name": Encoding.ASCII.GetBytes("rock_a").CopyTo(data, nif.Blocks[4].DataOffset + 12); break;
            case "target": WriteInt(data, controller + 22, 0); break;
            case "inactive": BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(controller + 4), 2); break;
            case "zero-frequency": WriteFloat(data, controller + 6, 0f); break;
            case "phase": WriteFloat(data, controller + 10, .5f); break;
            case "mismatched-stop": WriteFloat(data, nif.Blocks[7].DataOffset + 18, 8f); break;
            case "unknown-controller": nif.Blocks[6].TypeName = "NiAlphaController"; break;
            case "tbc": WriteInt(data, nif.Blocks[9].DataOffset + 8, 3); break;
            case "truncated-text-key": WriteInt(data, nif.Blocks[1].DataOffset + 16, 512); break;
            case "trailing-key-data": nif.Blocks[9].Size++; break;
            default: throw new InvalidOperationException("Unknown fixture mutation.");
        }

        Assert.Empty(NifTes3SequenceStreamReader.ReadAll(data, nif));
    }

    [Fact]
    public void AuthoredRotationDoesNotSlipIntoTheTranslationOnlyRoute()
    {
        var data = CreateSequence(false, true);
        var nif = Assert.IsType<NifInfo>(NifParser.Parse(data));
        Assert.Equal(11, nif.Blocks.Count);
        Assert.Empty(NifTes3SequenceStreamReader.ReadAll(data, nif));
    }

    [Fact]
    public void NoReverseGroupProducesNoControllerCycle()
    {
        var data = CreateSequence(false);
        var nif = Assert.IsType<NifInfo>(NifParser.Parse(data));
        foreach (var block in nif.Blocks.Where(static block => block.TypeName == "NiKeyframeController"))
        {
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(block.DataOffset + 4), 12);
        }

        Assert.Empty(NifTes3SequenceStreamReader.ReadAll(data, nif));
    }

    private static byte[] CreateSequence(bool reversePhysicalOrder, bool addRotation = false)
    {
        var blocks = new List<(string Type, byte[] Data)>();
        blocks.Add(("NiSequenceStreamHelper", Payload(writer =>
        {
            WriteName(writer, string.Empty);
            writer.Write(1);
            writer.Write(5);
        })));
        blocks.Add(("NiTextKeyExtraData", Payload(writer =>
        {
            writer.Write(2);
            writer.Write(0);
            writer.Write(2);
            writer.Write(2f);
            WriteName(writer, "Idle: Start");
            writer.Write(6f);
            WriteName(writer, "Idle: Stop");
        })));
        string[] names = ["Body", "Rock_A", "Rock_B"];
        for (var index = 0; index < names.Length; index++)
        {
            var next = NextReference(index, 2, reversePhysicalOrder);
            var name = names[index];
            blocks.Add(("NiStringExtraData", Payload(writer =>
            {
                writer.Write(next);
                writer.Write(4 + name.Length);
                WriteName(writer, name);
            })));
        }

        for (var index = 0; index < names.Length; index++)
        {
            var next = NextReference(index, 5, reversePhysicalOrder);
            blocks.Add(("NiKeyframeController", Payload(writer =>
            {
                writer.Write(next);
                writer.Write((ushort)(index == 0 ? 12 : 10));
                writer.Write(1f);
                writer.Write(0f);
                writer.Write(2f);
                writer.Write(6f);
                writer.Write(-1);
                writer.Write(8 + index);
            })));
        }

        for (var index = 0; index < names.Length; index++)
        {
            blocks.Add(("NiKeyframeData", Payload(writer =>
            {
                WriteOptionalRotation(writer, addRotation && index == 1);
                writer.Write(2);
                writer.Write(2);
                WriteTranslationKey(writer, 2f, index * 10f, 0f, 20f);
                WriteTranslationKey(writer, 6f, index * 10f + 10f, 0f, 0f);
                writer.Write(0);
            })));
        }

        return Payload(writer =>
        {
            writer.Write(Encoding.ASCII.GetBytes("NetImmerse File Format, Version 4.0.0.2\n"));
            writer.Write(0x04000002u);
            writer.Write(blocks.Count);
            foreach (var block in blocks)
            {
                WriteName(writer, block.Type);
                writer.Write(block.Data);
            }

            writer.Write(1);
            writer.Write(0);
        });
    }

    private static int NextReference(int index, int first, bool reverseOrder)
    {
        int offset;
        if (reverseOrder)
        {
            offset = index switch { 0 => 2, 2 => 1, _ => -1 };
        }
        else
        {
            offset = index < 2 ? index + 1 : -1;
        }

        return offset < 0 ? -1 : first + offset;
    }

    private static void WriteOptionalRotation(BinaryWriter writer, bool rotation)
    {
        writer.Write(rotation ? 1 : 0);
        if (!rotation) return;
        writer.Write(1);
        writer.Write(2f);
        writer.Write(1f);
        writer.Write(0f);
        writer.Write(0f);
        writer.Write(0f);
    }

    private static void WriteTranslationKey(BinaryWriter writer, float time, float value, float forward, float backward)
    {
        writer.Write(time);
        writer.Write(value);
        writer.Write(0f);
        writer.Write(0f);
        writer.Write(forward);
        writer.Write(0f);
        writer.Write(0f);
        writer.Write(backward);
        writer.Write(0f);
        writer.Write(0f);
    }

    private static void WriteName(BinaryWriter writer, string name)
    {
        var bytes = Encoding.ASCII.GetBytes(name);
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static byte[] Payload(Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, true);
        write(writer);
        writer.Flush();
        return stream.ToArray();
    }

    private static void WriteInt(byte[] data, int offset, int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset), value);
    }

    private static void WriteFloat(byte[] data, int offset, float value)
    {
        WriteInt(data, offset, BitConverter.SingleToInt32Bits(value));
    }
}