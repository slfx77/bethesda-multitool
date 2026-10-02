using System.Buffers.Binary;

namespace BethesdaMultitool.Tests.Core.Formats.Xngine.Flic;

/// <summary>Writes a two-pixel FLC from format words with independent palette/hold/delta/loop-back expectations.</summary>
internal static class FlicMediaFixture
{
    /// <summary>Writes four display slots and one ring block, optionally prefixed by CEL authoring metadata.</summary>
    internal static byte[] Create(uint speed = 142, bool prefix = false, int declaredFrames = 4,
        ushort extraChunk = 18, bool partialPalette = false, ushort frameExtension = 0,
        byte[]? firstRun = null, byte[]? deltaBody = null, byte ringIndex = 7)
    {
        var first = Frame([Chunk(4, Palette(false, partialPalette)), Chunk(15, firstRun ?? [1, 2, 7])], frameExtension);
        var hold = Frame([Chunk(extraChunk, [])]);
        var palette = Frame([Chunk(4, Palette(true, false))]);
        var delta = Frame([Chunk(7, deltaBody ?? [1, 0, 1, 0, 0, 1, 8, 9])]);
        var ring = Frame([Chunk(4, Palette(false, false)), Chunk(15, [1, 2, ringIndex])]);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(new byte[128]);
        if (prefix)
        {
            writer.Write(16u); writer.Write((ushort)0xF100); writer.Write(new byte[10]);
        }
        foreach (var block in new[] { first, hold, palette, delta, ring })
        {
            writer.Write(block);
        }
        var bytes = stream.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)bytes.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), 0xAF12);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), (ushort)declaredFrames);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(10), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(12), 8);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), speed);
        return bytes;
    }

    /// <summary>Changes each red channel by an explicit palette rule while retaining full-range color and opaque alpha.</summary>
    private static byte[] Palette(bool changed, bool partial)
    {
        var bytes = new byte[772];
        bytes[0] = 1;
        bytes[2] = partial ? (byte)1 : (byte)0;
        for (var index = 0; index < 256; index++)
        {
            bytes[4 + index * 3] = changed ? unchecked((byte)(90 + index)) : (byte)index;
            bytes[5 + index * 3] = changed ? (byte)40 : (byte)(255 - index);
            bytes[6 + index * 3] = changed ? (byte)10 : unchecked((byte)(index * 3));
        }
        return bytes;
    }

    /// <summary>Wraps an exact encoded chunk with its six-byte framing.</summary>
    private static byte[] Chunk(ushort type, byte[] body)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((uint)(body.Length + 6)); writer.Write(type); writer.Write(body);
        return stream.ToArray();
    }

    /// <summary>Wraps one display block without a production file writer.</summary>
    private static byte[] Frame(byte[][] chunks, ushort extension = 0)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((uint)(16 + chunks.Sum(chunk => chunk.Length)));
        writer.Write((ushort)0xF1FA); writer.Write((ushort)chunks.Length);
        writer.Write(extension); writer.Write(new byte[6]);
        foreach (var chunk in chunks)
        {
            writer.Write(chunk);
        }
        return stream.ToArray();
    }
}
