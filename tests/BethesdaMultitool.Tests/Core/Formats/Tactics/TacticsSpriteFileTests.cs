using System.IO.Compression;
using BethesdaMultitool.Core.Formats.Tactics;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Tactics;

/// <summary>
///     Synthetic vectors for the <c>.spr</c> reader. Every fixture is spelled out byte by byte and
///     every expectation is a literal computed by hand from the layout in
///     <see cref="TacticsSpriteFile" />.
///     <para>
///         The two claims worth guarding are the ones that fail SILENTLY on real data: an image
///         block is raw on version 1 and a <c>u32</c> inflated size + zlib stream on version 2
///         (retail ships 1,586 of the former), and the two versions walk the (frame, direction,
///         layer) triple in OPPOSITE nesting — frame outer for v1, layer outer for v2. Both fixtures
///         below encode the SAME eight slots in the two orders and assert the reader lands them in
///         the same places; get the order wrong and the layer widths swap.
///     </para>
/// </summary>
public sealed class TacticsSpriteFileTests
{
    private const int Frames = 1;
    private const int Directions = 2;

    /// <summary>A palette-less <c>&lt;zar&gt;</c> of <paramref name="width" /> x 1, all opaque index 0.</summary>
    private static byte[] LayerZar(int width)
    {
        return TacticsSyntheticBytes.Concat(
            TacticsSyntheticBytes.Tag("zar", "4"),
            TacticsSyntheticBytes.I32(width),
            TacticsSyntheticBytes.I32(1),
            [0],
            TacticsSyntheticBytes.U32((uint)(1 + width)),
            [(byte)((width << 2) | 1)],
            new byte[width]);
    }

    private static byte[] Slot(int offsetX, int offsetY, int width)
    {
        return TacticsSyntheticBytes.Concat(
            [1],
            TacticsSyntheticBytes.I32(offsetX),
            TacticsSyntheticBytes.I32(offsetY),
            LayerZar(width));
    }

    private static byte[] Absent()
    {
        return [0];
    }

    private static byte[] FourPalettes()
    {
        var parts = new List<byte[]>();
        for (var k = 0; k < 4; k++)
        {
            parts.Add(TacticsSyntheticBytes.U32(2));
            parts.Add([(byte)(10 * k), 0, 0, 0, 0, (byte)(20 * k), 0, 0]);
        }

        return TacticsSyntheticBytes.Concat([.. parts]);
    }

    /// <summary>
    ///     The eight slots of one 1-frame, 2-direction animation. Present: (f0,d0,base) width 3,
    ///     (f0,d0,hair) width 5, (f0,d1,tcol) width 7. The other five are absent.
    /// </summary>
    private static byte[] SlotFor(int frame, int direction, int layer)
    {
        if (frame != 0)
        {
            return Absent();
        }

        return (direction, layer) switch
        {
            (0, 0) => Slot(1, 2, 3),
            (0, 2) => Slot(4, 5, 5),
            (1, 3) => Slot(6, 7, 7),
            _ => Absent()
        };
    }

    private static byte[] BodyInVersionOrder(int version)
    {
        var parts = new List<byte[]> { FourPalettes() };
        if (version == 1)
        {
            for (var a = 0; a < Frames; a++)
            {
                for (var b = 0; b < Directions; b++)
                {
                    for (var k = 0; k < TacticsSpriteFile.LayerCount; k++)
                    {
                        parts.Add(SlotFor(a, b, k));
                    }
                }
            }
        }
        else
        {
            for (var k = 0; k < TacticsSpriteFile.LayerCount; k++)
            {
                for (var b = 0; b < Directions; b++)
                {
                    for (var a = 0; a < Frames; a++)
                    {
                        parts.Add(SlotFor(a, b, k));
                    }
                }
            }
        }

        return TacticsSyntheticBytes.Concat([.. parts]);
    }

    private static byte[] Deflate(byte[] body)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, true))
        {
            zlib.Write(body, 0, body.Length);
        }

        return output.ToArray();
    }

    private static byte[] HeaderAndAnimation(int imageBlockOffset)
    {
        return TacticsSyntheticBytes.Concat(
            TacticsSyntheticBytes.Tag("sprite", "4"),
            [2, 1, 2],
            TacticsSyntheticBytes.I32(10),
            TacticsSyntheticBytes.I32(-20),
            [3, 0],
            [100],
            TacticsSyntheticBytes.U32(1),
            TacticsSyntheticBytes.U32(2),
            [0x00, 0x00, 0xFC, 0xFF],
            TacticsSyntheticBytes.F32(0f),
            TacticsSyntheticBytes.F32(0.14f),
            TacticsSyntheticBytes.Ascii("default"),
            [0, 0],
            TacticsSyntheticBytes.U32(1),
            TacticsSyntheticBytes.Tag("spranim", "1"),
            TacticsSyntheticBytes.U32((uint)imageBlockOffset),
            TacticsSyntheticBytes.Ascii("Stand"),
            TacticsSyntheticBytes.U32(Frames),
            TacticsSyntheticBytes.U32(Directions),
            TacticsSyntheticBytes.I32(0),
            TacticsSyntheticBytes.I32(0),
            TacticsSyntheticBytes.I32(16),
            TacticsSyntheticBytes.I32(8),
            TacticsSyntheticBytes.I32(-4),
            TacticsSyntheticBytes.I32(-2),
            TacticsSyntheticBytes.I32(20),
            TacticsSyntheticBytes.I32(10));
    }

    private static byte[] Sprite(int blockVersion)
    {
        // The header's length is fixed, so build it once with a placeholder to learn the offset,
        // then again with the real one — exactly the two-pass shape the writer FUN_0070acc0 uses.
        var offset = HeaderAndAnimation(0).Length;
        var body = BodyInVersionOrder(blockVersion);
        var block = blockVersion == 1
            ? TacticsSyntheticBytes.Concat(TacticsSyntheticBytes.Tag("spranim_img", "1"), body)
            : TacticsSyntheticBytes.Concat(
                TacticsSyntheticBytes.Tag("spranim_img", "2"),
                TacticsSyntheticBytes.U32((uint)body.Length),
                Deflate(body));

        return TacticsSyntheticBytes.Concat(HeaderAndAnimation(offset), block);
    }

    [Fact]
    public void TheHeaderReadsEveryNamedFieldAndTheSequence()
    {
        var sprite = TacticsSpriteFile.Parse(Sprite(1), "s.spr");

        Assert.Equal(2, sprite.BoundingBoxX);
        Assert.Equal(1, sprite.BoundingBoxY);
        Assert.Equal(2, sprite.BoundingBoxZ);
        Assert.Equal(10, sprite.FootPositionX);
        Assert.Equal(-20, sprite.FootPositionY);
        Assert.Equal(TacticsSpriteMaterial.MetalThick, sprite.Material);
        Assert.Equal(TacticsSpriteFile.HeaderLiteral, sprite.HeaderLiteralValue);

        var sequence = Assert.Single(sprite.Sequences);
        Assert.Equal("default", sequence.Name);
        Assert.Equal(0, sequence.AnimationIndex);

        // 0x0000 then 0xFFFC: a frame index and the "Anim Repeat" event. Read as u16 the second
        // would be 65,532.
        Assert.Equal(new short[] { 0, TacticsSpriteFile.AnimRepeatEvent }, sequence.Entries);
        Assert.Equal(new[] { 0f, 0.14f }, sequence.Values);
    }

    [Fact]
    public void TheAnimationHeaderCarriesItsAxesAndScreenExtents()
    {
        var sprite = TacticsSpriteFile.Parse(Sprite(1), "s.spr");
        var animation = Assert.Single(sprite.Animations);

        Assert.Equal("Stand", animation.Name);
        Assert.Equal(Frames, animation.FrameCount);
        Assert.Equal(Directions, animation.DirectionCount);
        Assert.Equal(sprite.HeaderLength, animation.ImageBlockOffset);

        // Rects are frame-major: (0,0) then (0,1). Width is x1 - x0 with no inclusive +1.
        Assert.Equal(new TacticsSpriteRect(0, 0, 16, 8), animation.Rect(0, 0));
        Assert.Equal(new TacticsSpriteRect(-4, -2, 20, 10), animation.Rect(0, 1));
        Assert.Equal(16, animation.Rect(0, 0).Width);
        Assert.Equal(24, animation.Rect(0, 1).Width);
        Assert.Equal(12, animation.Rect(0, 1).Height);
    }

    /// <summary>
    ///     The discriminating test: the same eight slots written in the two nesting orders must
    ///     read back identically. If the reader used one order for both, the v2 fixture would put
    ///     the width-5 image in (direction 1, layer 0) instead of (direction 0, layer 2).
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void BothBlockVersionsLandTheSameSlotsInTheSamePlaces(int blockVersion)
    {
        var sprite = TacticsSpriteFile.Parse(Sprite(blockVersion), $"v{blockVersion}.spr");
        var block = sprite.ReadImageBlock(0);

        Assert.Equal(blockVersion, block.Version);
        Assert.Equal(Frames * Directions * TacticsSpriteFile.LayerCount, block.SlotCount);

        var baseLayer = block.Layer(0, 0, 0, Directions);
        Assert.NotNull(baseLayer);
        Assert.Equal(3, baseLayer.Image.Width);
        Assert.Equal(1, baseLayer.OffsetX);
        Assert.Equal(2, baseLayer.OffsetY);

        var hair = block.Layer(0, 0, 2, Directions);
        Assert.NotNull(hair);
        Assert.Equal(5, hair.Image.Width);
        Assert.Equal(4, hair.OffsetX);

        var tcol = block.Layer(0, 1, 3, Directions);
        Assert.NotNull(tcol);
        Assert.Equal(7, tcol.Image.Width);

        Assert.Null(block.Layer(0, 0, 1, Directions));
        Assert.Null(block.Layer(0, 1, 0, Directions));
        Assert.True(block.HasFrame(0, 0, Directions));
        Assert.True(block.HasFrame(0, 1, Directions));
    }

    [Fact]
    public void EachLayerKeepsItsOwnPalette()
    {
        var block = TacticsSpriteFile.Parse(Sprite(2), "p.spr").ReadImageBlock(0);

        // Stored B,G,R,x — entry 0 of layer k is (10k, 0, 0), which is BLUE 10k after the swap.
        for (var k = 0; k < TacticsSpriteFile.LayerCount; k++)
        {
            Assert.Equal(8, block.PaletteBgrx(k).Length);
            var entry = block.PaletteFor(k).GetEntry(0);
            Assert.Equal(0, entry.R);
            Assert.Equal(0, entry.G);
            Assert.Equal((byte)(10 * k), entry.B);
        }
    }

    [Fact]
    public void ComposingAFrameUsesTheRectSizeAndTheLayerOffsets()
    {
        var sprite = TacticsSpriteFile.Parse(Sprite(1), "c.spr");
        var animation = sprite.Animations[0];
        var block = sprite.ReadImageBlock(0);

        var texture = block.ComposeFrame(animation, 0, 0);
        Assert.NotNull(texture);
        Assert.Equal(16, texture.Width);
        Assert.Equal(8, texture.Height);

        // The base layer's three pixels sit at (1,2) and are palette 0 of layer 0 — pure black,
        // opaque. The hair layer's five sit at (4,5) and are palette 0 of layer 2 — blue 20.
        Assert.Equal(255, texture.Pixels[(2 * 16 + 1) * 4 + 3]);
        Assert.Equal(0, texture.Pixels[(2 * 16 + 0) * 4 + 3]);
        Assert.Equal(20, texture.Pixels[(5 * 16 + 4) * 4 + 2]);
    }

    [Fact]
    public void AVersionTwoBlockWithoutItsInflatedSizePrefixIsRefused()
    {
        // Drop the four prefix bytes and the zlib stream starts one dword early: this is exactly
        // the mistake that makes 0 of 108 retail v2 files inflate.
        var good = Sprite(2);
        var sprite = TacticsSpriteFile.Parse(good, "v2.spr");
        var at = sprite.Animations[0].ImageBlockOffset + TacticsSyntheticBytes.Tag("spranim_img", "2").Length;
        var broken = new byte[good.Length - 4];
        Array.Copy(good, broken, at);
        Array.Copy(good, at + 4, broken, at, good.Length - at - 4);

        var damaged = TacticsSpriteFile.Parse(broken, "v2.spr");
        Assert.ThrowsAny<InvalidDataException>(() => damaged.ReadImageBlock(0));
    }

    [Fact]
    public void ABlockVersionOtherThanOneOrTwoIsRefused()
    {
        var bytes = Sprite(1);
        var at = TacticsSpriteFile.Parse(bytes, "x.spr").Animations[0].ImageBlockOffset;

        // '<spranim_img>' NUL then the version digit.
        bytes[at + "<spranim_img>".Length + 1] = (byte)'3';
        var sprite = TacticsSpriteFile.Parse(bytes, "x.spr");
        var error = Assert.Throws<InvalidDataException>(() => sprite.ReadImageBlock(0));
        Assert.Contains("is not 1 or 2", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ASpriteVersionOtherThanFourIsRefused()
    {
        var bytes = Sprite(1);
        bytes["<sprite>".Length + 1] = (byte)'3';
        Assert.Throws<InvalidDataException>(() => TacticsSpriteFile.Parse(bytes, "old.spr"));
        Assert.False(TacticsSpriteFile.TryParse(bytes, "old.spr", out _, out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void ADegenerateScreenExtentIsRefusedWithTheGamesMessage()
    {
        // One animation whose only rect has x1 = -1 against x0 = 0.
        var bytes = TacticsSyntheticBytes.Concat(
            TacticsSyntheticBytes.Tag("sprite", "4"),
            [2, 1, 2],
            TacticsSyntheticBytes.I32(0),
            TacticsSyntheticBytes.I32(0),
            [3, 0],
            [100],
            TacticsSyntheticBytes.U32(0),
            TacticsSyntheticBytes.U32(1),
            TacticsSyntheticBytes.Tag("spranim", "1"),
            TacticsSyntheticBytes.U32(0),
            TacticsSyntheticBytes.Ascii("Bad"),
            TacticsSyntheticBytes.U32(1),
            TacticsSyntheticBytes.U32(1),
            TacticsSyntheticBytes.I32(0),
            TacticsSyntheticBytes.I32(0),
            TacticsSyntheticBytes.I32(-1),
            TacticsSyntheticBytes.I32(4));

        var error = Assert.Throws<InvalidDataException>(() => TacticsSpriteFile.Parse(bytes, "r.spr"));
        Assert.Contains(TacticsSpriteFile.BadExtentsMessage, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void IsSpriteRecognisesTheFramingAndRejectsEverythingElse()
    {
        Assert.True(TacticsSpriteFile.IsSprite(Sprite(1)));
        Assert.False(TacticsSpriteFile.IsSprite(TacticsSyntheticBytes.Tag("spranim", "1")));
        Assert.False(TacticsSpriteFile.IsSprite([0x50, 0x4B, 0x03, 0x04]));
    }

    /// <summary>
    ///     The parameter counts the editor's player <c>FUN_0044e9a0</c> consumes. Expected values
    ///     are read off that switch, not off the method under test.
    /// </summary>
    [Theory]
    [InlineData(-43, 3)]
    [InlineData(-5, 1)]
    [InlineData(-3, 1)]
    [InlineData(-2, 1)]
    [InlineData(-4, 0)]
    [InlineData(-6, 0)]
    [InlineData(-1, 0)]
    [InlineData(-40, 0)]
    [InlineData(-41, 0)]
    [InlineData(-42, 0)]
    [InlineData(-44, 0)]
    [InlineData(-45, 0)]
    [InlineData(0, 0)]
    [InlineData(43, 0)]
    public void EventParameterCountsFollowThePlayersSwitch(int entry, int expected)
    {
        Assert.Equal(expected, TacticsSpriteSequence.ParameterCount((short)entry));
    }

    /// <summary>
    ///     The codes the editor's event dialog binds, in the order it creates the buttons. The
    ///     first ELEVEN constants were each read out of the callback that button registers
    ///     (a single <c>FUN_0070b7c0(sequence, position, literal)</c> call).
    ///     <para>
    ///         ⛔ The twelfth, <see cref="TacticsSpriteEvent.SpecialKey" /> = -1, is INFERRED and is
    ///         asserted here only to pin this reader's own naming. Its callback
    ///         <c>0x0044ab30</c> prompts "Type in Special Key List", splits on "," and appends the
    ///         AUTHOR-TYPED value of each element (parsed by <c>0x007a31ba</c>) — it pushes no
    ///         constant, and no negative literal appears anywhere in the function. Nothing
    ///         behavioural rests on the value: no retail sequence contains -1.
    ///     </para>
    /// </summary>
    [Fact]
    public void TheEventCodesAreTheOnesTheEditorsButtonsAppend()
    {
        Assert.Equal(-40, (int)TacticsSpriteEvent.LeftFootstep);
        Assert.Equal(-41, (int)TacticsSpriteEvent.RightFootstep);
        Assert.Equal(-44, (int)TacticsSpriteEvent.SoundStart);
        Assert.Equal(-42, (int)TacticsSpriteEvent.WeaponFire);
        Assert.Equal(-43, (int)TacticsSpriteEvent.WeaponRelease);
        Assert.Equal(-4, (int)TacticsSpriteEvent.AnimRepeat);
        Assert.Equal(-5, (int)TacticsSpriteEvent.AnimGoto);
        Assert.Equal(-2, (int)TacticsSpriteEvent.AnimDelay);
        Assert.Equal(-3, (int)TacticsSpriteEvent.AnimRate);
        Assert.Equal(-6, (int)TacticsSpriteEvent.StartOverlay);
        Assert.Equal(-45, (int)TacticsSpriteEvent.Pickup);
        // ⛔ Inferred, not measured — see the summary. This pins our naming, nothing about the game.
        Assert.Equal(-1, (int)TacticsSpriteEvent.SpecialKey);
    }

    [Fact]
    public void TheFourLayersAreNamedInTheGamesOrder()
    {
        Assert.Equal(new[] { "base", "skin", "hair", "tcol" }, TacticsSpriteFile.LayerNames);
    }
}