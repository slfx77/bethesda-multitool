using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using BethesdaMultitool.Core.Formats.Redguard;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Redguard;

/// <summary>
///     Synthetic vectors for <see cref="RedguardWldFile" />, shaped after the four fixed-size
///     retail terrains measured 2026-09-05.
/// </summary>
public sealed class RedguardWldFileTests
{
    private static byte[] Build(Action<byte[]>? mutate = null)
    {
        var bytes = new byte[RedguardWldFile.FileLength];
        uint[] header = [16, 2, 2, 0, 160, 1, 22, RedguardWldFile.FileLength - 16];
        for (var i = 0; i < header.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4 * i), header[i]);
        }

        "TULO"u8.CopyTo(bytes.AsSpan(RedguardWldFile.FileLength - RedguardWldFile.TrailerLength));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(RedguardWldFile.FileLength - 12), 0x43C028);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(RedguardWldFile.FileLength - 8), 0xFFFFFFFF);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(RedguardWldFile.FileLength - 4), 0x43735);

        // Layer 1: a horizontal gradient (smooth); layer 2: alternating 10/200 (categorical).
        var layer1 = RedguardWldFile.HeaderLength + RedguardWldFile.LayerLength;
        var layer2 = layer1 + RedguardWldFile.LayerLength;
        for (var y = 0; y < RedguardWldFile.Height; y++)
        {
            for (var x = 0; x < RedguardWldFile.Width; x++)
            {
                bytes[layer1 + y * RedguardWldFile.Width + x] = (byte)(1 + x);
                bytes[layer2 + y * RedguardWldFile.Width + x] = (byte)(x % 2 == 0 ? 10 : 200);
            }
        }

        mutate?.Invoke(bytes);
        return bytes;
    }

    [Fact]
    public void Parse_ReadsHeaderEightLayersAndTrailer()
    {
        var wld = RedguardWldFile.Parse(Build(), "ISLAND.WLD");

        Assert.Equal([16u, 2u, 2u, 0u, 160u, 1u, 22u, 263416u], wld.Header);
        Assert.Equal(8, wld.Layers.Count);
        Assert.All(wld.Layers, layer => Assert.Equal((128, 256), (layer.Width, layer.Height)));
        Assert.Equal([0x43C028u, 0xFFFFFFFFu, 0x43735u], wld.Trailer);

        // Layer 1 row 0 is the gradient; layer 0 is untouched zeros.
        Assert.Equal(1, wld.Layers[1].Indices[0]);
        Assert.Equal(128, wld.Layers[1].Indices[127]);
        Assert.All(wld.Layers[0].Indices, b => Assert.Equal(0, b));
    }

    [Fact]
    public void MeanStep_SeparatesSmoothFromCategoricalLayers()
    {
        var wld = RedguardWldFile.Parse(Build(), "ISLAND.WLD");

        Assert.Equal(1.0, RedguardWldFile.MeanStep(wld.Layers[1]), 6);
        Assert.Equal(190.0, RedguardWldFile.MeanStep(wld.Layers[2]), 6);
        Assert.Equal(0.0, RedguardWldFile.MeanStep(wld.Layers[0]), 6);
    }

    [Fact]
    public void Parse_WrongLength_Throws()
    {
        Assert.Throws<InvalidDataException>(() => RedguardWldFile.Parse(Build().Take(1000).ToArray(), "BAD.WLD"));
    }

    [Fact]
    public void Parse_HeaderLengthWordThatDoesNotMatchTheFile_Throws()
    {
        var bytes = Build(b => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(28), 1));

        Assert.Throws<InvalidDataException>(() => RedguardWldFile.Parse(bytes, "BAD.WLD"));
    }

    [Fact]
    public void Parse_MissingTrailerTag_Throws()
    {
        var bytes = Build(b => b[RedguardWldFile.FileLength - RedguardWldFile.TrailerLength] = (byte)'X');

        Assert.Throws<InvalidDataException>(() => RedguardWldFile.Parse(bytes, "BAD.WLD"));
    }

    [Fact]
    public void IsWldFile_NeedsTheFixedLengthAndTheTrailer()
    {
        Assert.True(RedguardWldFile.IsWldFile(Build()));
        Assert.False(RedguardWldFile.IsWldFile(Build().Take(RedguardWldFile.FileLength - 1).ToArray()));
        Assert.False(RedguardWldFile.IsWldFile(Build(b => b[RedguardWldFile.FileLength - 16] = 0)));
    }
}
