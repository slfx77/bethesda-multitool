using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using BethesdaMultitool.CLI.Rendering.Sprite;
using BethesdaMultitool.Core.Formats.Fallout;
using BethesdaMultitool.Core.Imaging;
using Xunit;

namespace BethesdaMultitool.Tests.Core.AssetBrowse;

/// <summary>
///     Covers <c>SpriteRenderPipeline.DecodeBytes</c> — the route the asset browser takes, where the
///     bytes are already in hand and any companion palette is resolved through a caller-supplied
///     lookup rather than the filesystem.
///     <para>
///         The point of the seam is that the browser and the CLI cannot drift: both run the same
///         decoders and the same palette PRECEDENCE, and only the lookup differs. These tests pin
///         the precedence, because that is the part that silently produces a wrong-but-plausible
///         picture when it breaks.
///     </para>
/// </summary>
public sealed class SpriteDecodeBytesTests
{
    /// <summary>A one-frame FRM whose single pixel run is the palette index given.</summary>
    private static byte[] Frm(byte index, int width = 2, int height = 2)
    {
        var header = new byte[FalloutFrmFile.HeaderLength];
        BinaryPrimitives.WriteUInt32BigEndian(header, FalloutFrmFile.RetailVersion);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(8), 1);

        var pixels = new byte[width * height];
        Array.Fill(pixels, index);
        var frame = new byte[FalloutFrmFile.FrameHeaderLength];
        BinaryPrimitives.WriteUInt16BigEndian(frame, (ushort)width);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2), (ushort)height);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(4), (uint)pixels.Length);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(FalloutFrmFile.FrameAreaSizePosition), (uint)(frame.Length + pixels.Length));
        return [.. header, .. frame, .. pixels];
    }

    /// <summary>A 768-byte 6-bit palette where entry <paramref name="index" /> is a known colour.</summary>
    private static byte[] Palette768(int index, byte r, byte g, byte b)
    {
        var rgb = new byte[BethesdaMultitool.Core.Imaging.Palette.RgbByteCount];
        rgb[index * 3] = r;
        rgb[(index * 3) + 1] = g;
        rgb[(index * 3) + 2] = b;
        return rgb;
    }

    [Fact]
    public void DecodeBytes_ResolvesThePaletteThroughTheLookup()
    {
        var asked = new List<string>();
        var frames = SpriteRenderPipeline.DecodeBytes(
            Frm(7), "SOMEART.FRM",
            name =>
            {
                asked.Add(name);
                return name.Equals(FalloutPalette.FileName, StringComparison.OrdinalIgnoreCase)
                    ? Palette768(7, 63, 0, 0)
                    : null;
            });

        var frame = Assert.Single(frames.Frames);
        Assert.Equal((2, 2), (frame.Texture.Width, frame.Texture.Height));

        // 63 promotes to a full 255 red; the browser must not render 6-bit data as 8-bit.
        Assert.Equal(255, frame.Texture.Pixels[0]);
        Assert.Equal(0, frame.Texture.Pixels[1]);
        Assert.Equal(0, frame.Texture.Pixels[2]);
        Assert.Equal(FalloutPalette.FileName, frames.PaletteSource);
    }

    [Fact]
    public void DecodeBytes_AsksForTheImagesOwnPaletteBeforeTheGlobalOne()
    {
        var asked = new List<string>();
        SpriteRenderPipeline.DecodeBytes(
            Frm(7), "DEATH.FRM",
            name =>
            {
                asked.Add(name);
                return name.Equals("DEATH.PAL", StringComparison.OrdinalIgnoreCase) ? Palette768(7, 0, 63, 0) : null;
            });

        // ⚠ This ordering is the whole reason Fallout's ending slides render correctly: asking for
        // COLOR.PAL first would find a real file and produce a plausible, wrong picture.
        Assert.Equal("DEATH.PAL", asked[0]);
    }

    [Fact]
    public void DecodeBytes_PrefersTheImagesOwnPaletteWhenBothExist()
    {
        var frames = SpriteRenderPipeline.DecodeBytes(
            Frm(7), "DEATH.FRM",
            name => name.Equals("DEATH.PAL", StringComparison.OrdinalIgnoreCase)
                ? Palette768(7, 0, 63, 0)
                : Palette768(7, 63, 0, 0));

        // Green is the slide's own palette; red is the global one. Green must win.
        var pixels = Assert.Single(frames.Frames).Texture.Pixels;
        Assert.Equal((byte)0, pixels[0]);
        Assert.Equal((byte)255, pixels[1]);
        Assert.Equal("DEATH.PAL", frames.PaletteSource);
    }

    [Fact]
    public void DecodeBytes_WhenNoCandidateResolves_Throws()
    {
        var error = Assert.Throws<FileNotFoundException>(
            () => SpriteRenderPipeline.DecodeBytes(Frm(7), "SOMEART.FRM", _ => null));

        Assert.Contains(FalloutPalette.FileName, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DecodeBytes_RejectsNullArguments()
    {
        Assert.Throws<ArgumentNullException>(() => SpriteRenderPipeline.DecodeBytes(null!, "A.FRM", _ => null));
        Assert.Throws<ArgumentNullException>(() => SpriteRenderPipeline.DecodeBytes([1], null!, _ => null));
        Assert.Throws<ArgumentNullException>(() => SpriteRenderPipeline.DecodeBytes([1], "A.FRM", null!));
    }
}
