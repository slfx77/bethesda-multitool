using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using BethesdaMultitool.Core.Formats.Travels.Shadowkey;
using BethesdaMultitool.Core.Imaging;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Travels;

/// <summary>
///     Synthetic vectors for the Shadowkey global packs (<c>models.idx</c> + <c>models.huge</c>,
///     <c>global.spr</c>, <c>StringTable.&lt;lang&gt;</c> and <c>products.dat</c>), shaped after the
///     retail files surveyed 2026-09-05. Everything here is little-endian, and the packs carry no
///     magic at all: the only structural evidence either format offers is that its entries tile the
///     file exactly, so the rejection cases below are what the readers are really for.
/// </summary>
public sealed class ShadowkeyPackTests
{
    private const string PackName = "models.huge";

    /// <summary>
    ///     A mesh record: <paramref name="frames" /> frames of 3 vertices, 3 UVs, 1 face, one
    ///     2x2 texture of the four given texels, and one sequence.
    /// </summary>
    private static byte[] Mesh(
        int frames = 2,
        ushort[]? texels = null,
        int textureCount = 1,
        ushort tag = ShadowkeyMesh.FormatTag,
        ushort coordinateCount = 9,
        ushort trailer = ShadowkeyMesh.HeaderTrailer,
        ushort faceVertex = 2,
        ushort faceUv = 2,
        ushort sequenceEnd = 0,
        int trailingBytes = 0)
    {
        texels ??= [0x0F00, 0x00F0, 0x000F, ShadowkeyMesh.MagentaColourKey];
        var body = new List<byte>();

        void U16(int value)
        {
            var word = new byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(word, (ushort)value);
            body.AddRange(word);
        }

        void I16(int value)
        {
            var word = new byte[2];
            BinaryPrimitives.WriteInt16LittleEndian(word, (short)value);
            body.AddRange(word);
        }

        U16(tag);
        U16(frames);
        U16(3);
        U16(3);
        U16(1);
        U16(coordinateCount);
        U16(trailer);

        for (var f = 0; f < frames; f++)
        {
            for (var v = 0; v < 3; v++)
            {
                I16(v * 10);
                I16((f * 100) + v);
                I16(-v);
            }
        }

        for (var u = 0; u < 3; u++)
        {
            U16(u * 256);
            U16(0x0180);
        }

        U16(0);
        U16(1);
        U16(faceVertex);
        U16(0);
        U16(1);
        U16(faceUv);

        U16(textureCount);
        U16(2);
        U16(2);
        for (var t = 0; t < textureCount; t++)
        {
            foreach (var texel in texels)
            {
                U16(texel);
            }
        }

        U16(1);
        U16(0);
        U16(sequenceEnd == 0 ? frames : sequenceEnd);
        U16(5);

        body.AddRange(new byte[trailingBytes]);
        return [.. body];
    }

    private static byte[] ModelIndex(params (uint Offset, uint Size)[] entries)
    {
        var bytes = new byte[4 + (entries.Length * 8)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)entries.Length);
        for (var i = 0; i < entries.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4 + (i * 8)), entries[i].Offset);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8 + (i * 8)), entries[i].Size);
        }

        return bytes;
    }

    [Fact]
    public void ModelPack_WhoseIndexTilesThePack_ExposesEveryEntry()
    {
        var first = Mesh();
        var second = Mesh(frames: 1);
        var pack = (byte[])[.. first, .. second];
        var index = ModelIndex(
            (0u, (uint)first.Length),
            ((uint)first.Length, 0u),
            ((uint)first.Length, (uint)second.Length));
        var modelsTxt = "0 2 64 128 rat.bin\r\n1 0 0 0 NULL.bin\r\n2 2 256 256 door.bin\r\n";

        var models = ShadowkeyModelPack.Parse(index, pack, modelsTxt, PackName);

        Assert.Equal(3, models.Count);
        Assert.Equal("rat.bin", models.Entries[0].FileName);
        Assert.Equal(2, models.Entries[0].Flag);
        Assert.Equal(64, models.Entries[0].Width);
        Assert.Equal(128, models.Entries[0].Height);
        Assert.True(models.Entries[1].IsEmpty);
        Assert.Equal(first.Length, models.GetEntryBytes(0).Length);
        Assert.Equal(0, models.GetEntryBytes(1).Length);
        Assert.Equal(second, models.GetEntryBytes(2).ToArray());

        Assert.Null(models.GetMesh(1));
        var mesh = models.GetMesh(0);
        Assert.NotNull(mesh);
        Assert.Equal(2, mesh!.FrameCount);
        Assert.Same(mesh, models.GetMesh(0));
    }

    [Fact]
    public void ModelPack_WhoseIndexLeavesAGap_Throws()
    {
        var mesh = Mesh();
        var pack = (byte[])[.. mesh, .. mesh];
        var index = ModelIndex((0u, (uint)mesh.Length), ((uint)mesh.Length + 1, (uint)mesh.Length));

        var error = Assert.Throws<InvalidDataException>(
            () => ShadowkeyModelPack.Parse(index, pack, null, PackName));

        Assert.Contains("tile to", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ModelPack_WhoseEntriesStopShortOfThePack_Throws()
    {
        var mesh = Mesh();
        var pack = (byte[])[.. mesh, 0, 0];
        var index = ModelIndex((0u, (uint)mesh.Length));

        var error = Assert.Throws<InvalidDataException>(
            () => ShadowkeyModelPack.Parse(index, pack, null, PackName));

        Assert.Contains($"but the pack is {pack.Length} bytes", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ModelPack_WhoseEntryRunsPastThePack_Throws()
    {
        var mesh = Mesh();
        var index = ModelIndex((0u, (uint)mesh.Length + 8));

        Assert.Throws<InvalidDataException>(() => ShadowkeyModelPack.Parse(index, mesh, null, PackName));
    }

    [Fact]
    public void ModelPack_WhoseIndexLengthDisagreesWithItsCount_Throws()
    {
        var index = ModelIndex((0u, 0u));
        var truncated = index[..^2];

        var error = Assert.Throws<InvalidDataException>(
            () => ShadowkeyModelPack.Parse(truncated, [], null, PackName));

        Assert.Contains("declares 1 entries", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ModelPack_WithATooShortIndex_Throws()
    {
        Assert.Throws<InvalidDataException>(() => ShadowkeyModelPack.Parse([1, 2], [], null, PackName));
    }

    [Theory]
    [InlineData("1 0 0 0 rat.bin\r\n", "expected the slot index 0")]
    [InlineData("0 0 0 rat.bin\r\n", "has 4 fields")]
    [InlineData("0 x 0 0 rat.bin\r\n", "non-numeric")]
    [InlineData("0 0 0 0 rat.bin\r\n1 0 0 0 door.bin\r\n", "but the index declares 1 entries")]
    public void ModelPack_WithAMalformedModelsTxt_Throws(string modelsTxt, string expected)
    {
        var index = ModelIndex((0u, 0u));

        var error = Assert.Throws<InvalidDataException>(
            () => ShadowkeyModelPack.Parse(index, [], modelsTxt, PackName));

        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ModelPack_IndexerRejectsASlotOutsideThePack()
    {
        var models = ShadowkeyModelPack.Parse(ModelIndex((0u, 0u)), [], null, PackName);

        Assert.Throws<ArgumentOutOfRangeException>(() => models.GetEntryBytes(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => models.GetMesh(-1));
    }

    [Fact]
    public void Mesh_WithTwoFramesAndOneTexture_DecodesEverySection()
    {
        var mesh = ShadowkeyMesh.Parse(Mesh(), "rat.bin");

        Assert.Equal(2, mesh.FrameCount);
        Assert.Equal(3, mesh.VertexCount);
        Assert.Equal(6, mesh.Vertices.Count);

        // Frame-major: frame 1 vertex 0 sits at 1 * VertexCount, and carries frame 1's Y.
        Assert.Equal(new ShadowkeyVertex(0, 0, 0), mesh.Vertices[0]);
        Assert.Equal(new ShadowkeyVertex(0, 100, 0), mesh.Vertices[3]);
        Assert.Equal(new ShadowkeyVertex(20, 102, -2), mesh.Vertices[5]);

        // UVs are 8.8 fixed point in texels, so 0x0100 is texel 1.0 and 0x0180 is 1.5.
        Assert.Equal(1f, mesh.Uvs[1].TexelU);
        Assert.Equal(1.5f, mesh.Uvs[1].TexelV);

        Assert.Equal(new ShadowkeyFace(0, 1, 2, 0, 1, 2), Assert.Single(mesh.Faces));
        Assert.Equal(2, mesh.Textures.Width);
        Assert.Equal(2, mesh.Textures.Height);
        Assert.Equal(4, Assert.Single(mesh.Textures.Skins).Length);

        var sequence = Assert.Single(mesh.Sequences);
        Assert.Equal(new ShadowkeySequence(0, 2, 5), sequence);
        Assert.Equal(2, sequence.Length);
    }

    [Fact]
    public void Mesh_DecodeSkin_ExpandsFourFourFourTexelsAndKeysMagenta()
    {
        var mesh = ShadowkeyMesh.Parse(Mesh(), "rat.bin");

        var image = mesh.DecodeSkin(0);

        Assert.Equal(2, image.Bitmap.Width);
        Assert.Equal([0, 1, 2, 3], image.Bitmap.Indices);
        Assert.Equal(((byte)255, (byte)0, (byte)0, (byte)255), image.Palette.GetEntry(0));
        Assert.Equal(((byte)0, (byte)255, (byte)0, (byte)255), image.Palette.GetEntry(1));
        Assert.Equal(((byte)0, (byte)0, (byte)255, (byte)255), image.Palette.GetEntry(2));

        // 0x0F0F is the magenta colour key: the colour survives, the alpha does not.
        Assert.Equal(((byte)255, (byte)0, (byte)255, (byte)0), image.Palette.GetEntry(3));

        var opaque = mesh.DecodeSkin(0, magentaIsTransparent: false);
        Assert.Equal(((byte)255, (byte)0, (byte)255, (byte)255), opaque.Palette.GetEntry(3));

        var texture = image.ToDecodedTexture();
        Assert.Equal(2, texture.Width);
    }

    [Fact]
    public void Mesh_DecodeSkin_RepeatsNibblesSoMidRangeChannelsStayEven()
    {
        // 0x0842 -> R 8, G 4, B 2 -> 136, 68, 34 (v * 17), not v << 4.
        var mesh = ShadowkeyMesh.Parse(Mesh(texels: [0x0842, 0x0842, 0x0842, 0x0842]), "rat.bin");

        var image = mesh.DecodeSkin(0);

        Assert.Equal([0, 0, 0, 0], image.Bitmap.Indices);
        Assert.Equal(((byte)136, (byte)68, (byte)34, (byte)255), image.Palette.GetEntry(0));
    }

    [Fact]
    public void Mesh_WithSeveralTextures_ExposesThemAsAlternativeSkins()
    {
        var mesh = ShadowkeyMesh.Parse(Mesh(textureCount: 3), "male_long_tunic.bin");

        Assert.Equal(3, mesh.Textures.Skins.Count);
        Assert.Throws<ArgumentOutOfRangeException>(() => mesh.DecodeSkin(3));
    }

    [Fact]
    public void Mesh_ToUnrolledTriangles_UnrollsOneFrameIntoCorners()
    {
        var mesh = ShadowkeyMesh.Parse(Mesh(), "rat.bin");

        var triangles = mesh.ToUnrolledTriangles(1);

        Assert.Equal(3, triangles.Positions.Length);
        Assert.Equal([0, 1, 2], triangles.Indices);
        Assert.Equal(100f, triangles.Positions[0].Y);
        Assert.Equal(20f, triangles.Positions[2].X);
        Assert.Equal(2f, triangles.TexelUvs[2].X);

        var positions = mesh.FramePositions(1);
        Assert.Equal(3, positions.Length);
        Assert.Equal(new Vector3(0, 100, 0), positions[0]);
        Assert.Throws<ArgumentOutOfRangeException>(() => mesh.ToUnrolledTriangles(2));
    }

    [Theory]
    [InlineData(6, 9, 1, 2, 2, 0, 0, "expected the format tag")]
    [InlineData(7, 8, 1, 2, 2, 0, 0, "expected 3 x 3 vertices")]
    [InlineData(7, 9, 2, 2, 2, 0, 0, "header word 6")]
    [InlineData(7, 9, 1, 3, 2, 0, 0, "indexes vertex 3 of 3")]
    [InlineData(7, 9, 1, 2, 3, 0, 0, "indexes UV 3 of 3")]
    [InlineData(7, 9, 1, 2, 2, 3, 0, "over 2 frames")]
    [InlineData(7, 9, 1, 2, 2, 0, 4, "but the entry is")]
    public void Mesh_WithABrokenSection_Throws(
        ushort tag, ushort coordinateCount, ushort trailer, ushort faceVertex, ushort faceUv, ushort sequenceEnd, int trailingBytes, string expected)
    {
        var bytes = Mesh(
            tag: tag,
            coordinateCount: coordinateCount,
            trailer: trailer,
            faceVertex: faceVertex,
            faceUv: faceUv,
            sequenceEnd: sequenceEnd,
            trailingBytes: trailingBytes);

        var error = Assert.Throws<InvalidDataException>(() => ShadowkeyMesh.Parse(bytes, "rat.bin"));

        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Mesh_ThatIsTruncated_Throws()
    {
        var bytes = Mesh();

        Assert.Throws<InvalidDataException>(() => ShadowkeyMesh.Parse(bytes.AsSpan(0, 10), "rat.bin"));
        Assert.Throws<InvalidDataException>(() => ShadowkeyMesh.Parse(bytes.AsSpan(0, 40), "rat.bin"));
    }

    [Fact]
    public void Mesh_WithNoFrames_Throws()
    {
        var bytes = Mesh();
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), 0);

        var error = Assert.Throws<InvalidDataException>(() => ShadowkeyMesh.Parse(bytes, "rat.bin"));

        Assert.Contains("frame count at byte 2", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Builds one sprite blob: width, height, palette, then the given row spans.</summary>
    private static byte[] Sprite(int width, int height, ushort[] palette, IEnumerable<(int X0, int X1, byte[] Indices)> rows)
    {
        var bytes = new List<byte>();

        void U16(int value)
        {
            var word = new byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(word, (ushort)value);
            bytes.AddRange(word);
        }

        U16(width);
        U16(height);
        for (var i = 0; i < Palette.EntryCount; i++)
        {
            U16(i < palette.Length ? palette[i] : ShadowkeySprite.PaletteFiller);
        }

        foreach (var (x0, x1, indices) in rows)
        {
            U16(x0);
            U16(x1);
            bytes.AddRange(indices);
        }

        return [.. bytes];
    }

    private static byte[] SpritePack(params byte[][] blobs)
    {
        var bytes = new List<byte>();
        foreach (var blob in blobs)
        {
            var word = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(word, (uint)blob.Length);
            bytes.AddRange(word);
        }

        foreach (var blob in blobs)
        {
            bytes.AddRange(blob);
        }

        return [.. bytes];
    }

    private static byte[] FourColourSprite()
    {
        return Sprite(
            4,
            3,
            [0x0F00, 0x00F0, ShadowkeyMesh.MagentaColourKey],
            [
                (1, 3, [1, 2]),
                (ShadowkeySpriteRow.EmptyMarker, ShadowkeySpriteRow.EmptyMarker, []),
                (0, 4, [0, 1, 2, 0]),
            ]);
    }

    [Fact]
    public void SpritePack_SolvesTheEntryCountByTilingAndSkipsReservedSlots()
    {
        var blob = FourColourSprite();
        var pack = SpritePack(blob, []);

        var sprites = ShadowkeySpritePack.Parse(pack, "global.spr");

        Assert.Equal(2, sprites.Count);
        Assert.Equal([(uint)blob.Length, 0u], sprites.Sizes);
        Assert.Equal(blob, sprites.GetEntryBytes(0).ToArray());
        Assert.Null(sprites.GetSprite(1));

        var sprite = sprites.GetSprite(0);
        Assert.NotNull(sprite);
        Assert.Same(sprite, sprites.GetSprite(0));
        Assert.Equal(4, sprite!.Width);
        Assert.Equal(3, sprite.Height);
        Assert.Equal(3, sprite.UsedPaletteLength);
        Assert.Equal(6, sprite.SpanPixelCount);
        Assert.Equal(1, sprite.EmptyRowCount);
        Assert.True(sprite.Rows[1].IsEmpty);
        Assert.Equal([1, 2], sprite.Rows[0].Indices.ToArray());
    }

    [Fact]
    public void Sprite_DecodesRowSpansWithEverythingElseTransparent()
    {
        var sprites = ShadowkeySpritePack.Parse(SpritePack(FourColourSprite()), "global.spr");
        var sprite = sprites.GetSprite(0)!;

        var image = sprite.ToImage();

        // Index 3 is the first slot no pixel uses, so it can carry the transparency.
        Assert.Equal(3, (int)image.TransparentIndex);
        Assert.False(image.TransparentIndexAliasesPixels);
        Assert.Equal(
            [
                3, 1, 2, 3,
                3, 3, 3, 3,
                0, 1, 2, 0,
            ],
            image.Bitmap.Indices);
        Assert.Equal(0, (int)image.Palette.GetEntry(3).A);
        Assert.Equal(((byte)255, (byte)0, (byte)0, (byte)255), image.Palette.GetEntry(0));

        // Palette slot 2 is the magenta key and defaults to transparent, colour intact.
        Assert.Equal(((byte)255, (byte)0, (byte)255, (byte)0), image.Palette.GetEntry(2));
        Assert.Equal(255, (int)sprite.BuildPalette(3, magentaIsTransparent: false).GetEntry(2).A);
    }

    [Fact]
    public void Sprite_ThatUsesEveryPaletteSlot_ReportsTheTransparentIndexAliasesItsPixels()
    {
        var indices = new byte[Palette.EntryCount];
        for (var i = 0; i < indices.Length; i++)
        {
            indices[i] = (byte)i;
        }

        var blob = Sprite(
            Palette.EntryCount,
            2,
            [.. Enumerable.Repeat((ushort)0x0123, Palette.EntryCount)],
            [
                (0, Palette.EntryCount, indices),
                (ShadowkeySpriteRow.EmptyMarker, ShadowkeySpriteRow.EmptyMarker, []),
            ]);

        var sprite = ShadowkeySpritePack.Parse(SpritePack(blob), "global.spr").GetSprite(0)!;

        Assert.Null(sprite.FindUnusedIndex());
        var image = sprite.ToImage();
        Assert.Equal(255, (int)image.TransparentIndex);
        Assert.True(image.TransparentIndexAliasesPixels);
        Assert.Equal(255, (int)image.Bitmap.Indices[Palette.EntryCount]);
    }

    [Fact]
    public void SpritePack_ThatTilesAtNoEntryCount_Throws()
    {
        var bytes = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 1000);

        var error = Assert.Throws<InvalidDataException>(() => ShadowkeySpritePack.Parse(bytes, "global.spr"));

        Assert.Contains("no entry count", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The tiling total <c>4 * n + sum(size[0..n-1])</c> grows by at least 4 per candidate, so
    ///     at most one entry count can ever hit the file length: a pack padded with reserved slots
    ///     resolves to the LONGER table, and the shorter one misses by exactly the 4 bytes of the
    ///     size word it left out. The reader still carries an ambiguity guard, but no byte sequence
    ///     can reach it — which is the honest reason there is no "ambiguous" fixture here.
    /// </summary>
    [Fact]
    public void SpritePack_TilingSolutionIsUniqueEvenWithReservedSlots()
    {
        var blob = FourColourSprite();
        var pack = SpritePack(blob, [], [], []);

        var sprites = ShadowkeySpritePack.Parse(pack, "global.spr");

        Assert.Equal(4, sprites.Count);
        Assert.Equal(4 + blob.Length, SpritePack(blob).Length);
        Assert.Equal(16 + blob.Length, pack.Length);
    }

    [Fact]
    public void SpritePack_TooShortForASizeWord_Throws()
    {
        Assert.Throws<InvalidDataException>(() => ShadowkeySpritePack.Parse([1, 2, 3], "global.spr"));
    }

    [Fact]
    public void SpritePack_RejectsASlotOutsideThePack()
    {
        var sprites = ShadowkeySpritePack.Parse(SpritePack(FourColourSprite()), "global.spr");

        Assert.Throws<ArgumentOutOfRangeException>(() => sprites.GetSprite(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => sprites.GetEntryBytes(-1));
    }

    [Fact]
    public void Sprite_TooShortForItsPalette_Throws()
    {
        var error = Assert.Throws<InvalidDataException>(
            () => ShadowkeySpritePack.ParseSprite(new byte[100], 0, "global.spr[0]"));

        Assert.Contains("too short for the 516-byte header", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Sprite_WhoseSpanLeavesTheRow_Throws()
    {
        var blob = Sprite(4, 1, [0x0F00], [(2, 6, [0, 0, 0, 0])]);

        var error = Assert.Throws<InvalidDataException>(
            () => ShadowkeySpritePack.ParseSprite(blob, 0, "global.spr[0]"));

        Assert.Contains("spans [2, 6) of a 4-pixel row", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Sprite_WithBytesLeftOverAfterItsRows_Throws()
    {
        var blob = (byte[])[.. Sprite(4, 1, [0x0F00], [(0, 2, [0, 0])]), 0, 0];

        var error = Assert.Throws<InvalidDataException>(
            () => ShadowkeySpritePack.ParseSprite(blob, 0, "global.spr[0]"));

        Assert.Contains("rows end at byte", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Sprite_WhoseRowsRunPastTheBlob_Throws()
    {
        var blob = Sprite(4, 3, [0x0F00], [(0, 2, [0, 0])]);

        Assert.Throws<InvalidDataException>(() => ShadowkeySpritePack.ParseSprite(blob, 0, "global.spr[0]"));
    }

    private static byte[] StringTable(params string[] strings)
    {
        var bytes = new List<byte>();
        var count = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(count, (uint)strings.Length);
        bytes.AddRange(count);

        foreach (var text in strings)
        {
            var length = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(length, (uint)(text.Length + 1));
            bytes.AddRange(length);
            bytes.AddRange(Encoding.Unicode.GetBytes(text));
            bytes.AddRange([0, 0]);
        }

        return [.. bytes];
    }

    [Fact]
    public void StringTable_ReadsUtf16LittleEndianTextAndTheLanguageFromTheName()
    {
        var table = ShadowkeyStringTable.Parse(
            StringTable("YOU ARE DEAD.", "Épée", string.Empty), "StringTable.FRE");

        Assert.Equal("fre", table.Language);
        Assert.Equal(["YOU ARE DEAD.", "Épée", string.Empty], table.Strings);
    }

    [Fact]
    public void StringTable_WithoutATerminator_Throws()
    {
        var bytes = StringTable("Hi");
        bytes[^1] = 0x21;

        var error = Assert.Throws<InvalidDataException>(() => ShadowkeyStringTable.Parse(bytes, "StringTable.eng"));

        Assert.Contains("expected the U+0000 terminator", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void StringTable_WithAZeroLengthEntry_Throws()
    {
        var bytes = StringTable("Hi");
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 0);

        var error = Assert.Throws<InvalidDataException>(() => ShadowkeyStringTable.Parse(bytes, "StringTable.eng"));

        Assert.Contains("declares 0 code units", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void StringTable_WithBytesLeftOver_Throws()
    {
        var bytes = (byte[])[.. StringTable("Hi"), 0, 0];

        var error = Assert.Throws<InvalidDataException>(() => ShadowkeyStringTable.Parse(bytes, "StringTable.eng"));

        Assert.Contains("entries end at byte", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void StringTable_DeclaringMoreEntriesThanTheFileHolds_Throws()
    {
        var bytes = StringTable("Hi");
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 4082);

        var error = Assert.Throws<InvalidDataException>(() => ShadowkeyStringTable.Parse(bytes, "StringTable.eng"));

        Assert.Contains("declares 4082 entries", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void StringTable_ThatIsTooShortForItsHeader_Throws()
    {
        Assert.Throws<InvalidDataException>(() => ShadowkeyStringTable.Parse([1, 2], "StringTable.eng"));
    }

    private static byte[] ProductTable(params (ushort Id, ushort Type, uint Cost, ushort NameId, byte[] Flags)[] products)
    {
        var bytes = new byte[ShadowkeyProductTable.HeaderLength
            + (products.Length * ShadowkeyProductTable.RecordLength)];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, ShadowkeyProductTable.RetailFormatTag);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), (ushort)products.Length);

        for (var i = 0; i < products.Length; i++)
        {
            var record = bytes.AsSpan(
                ShadowkeyProductTable.HeaderLength + (i * ShadowkeyProductTable.RecordLength),
                ShadowkeyProductTable.RecordLength);
            BinaryPrimitives.WriteUInt16LittleEndian(record, products[i].Id);
            BinaryPrimitives.WriteUInt16LittleEndian(record[2..], products[i].Type);
            BinaryPrimitives.WriteUInt32LittleEndian(record[4..], products[i].Cost);
            BinaryPrimitives.WriteUInt16LittleEndian(record[8..], 42);
            BinaryPrimitives.WriteUInt16LittleEndian(record[10..], (ushort)(products[i].NameId + 1));
            record[12] = 3;
            BinaryPrimitives.WriteUInt16LittleEndian(record[13..], products[i].NameId);
            BinaryPrimitives.WriteUInt16LittleEndian(record[15..], ShadowkeyProductTable.ClassFlagCount);
            products[i].Flags.CopyTo(record[17..]);
        }

        return bytes;
    }

    [Fact]
    public void ProductTable_ReadsTheUnalignedRecordAndItsClassFlags()
    {
        var bytes = ProductTable(
            (50, 1, 108_779, 2772, [0, 0, 1, 0, 1, 0, 1, 1, 0]),
            (4905, 3, 30, 208, [1, 1, 1, 1, 1, 1, 1, 1, 1]));

        var table = ShadowkeyProductTable.Parse(bytes, "products.dat", stringCount: 4082);

        Assert.Equal(ShadowkeyProductTable.RetailFormatTag, table.FormatTag);
        Assert.Equal(2, table.Products.Count);

        var weapon = table.Products[0];
        Assert.Equal(50, (int)weapon.Id);
        Assert.Equal(ShadowkeyProductType.Weapon, weapon.Type);
        Assert.Equal(108_779u, weapon.Cost);
        Assert.Equal(42, (int)weapon.Rating);
        Assert.Equal(2773, (int)weapon.DescriptionStringId);
        Assert.Equal(2772, (int)weapon.NameStringId);
        Assert.Equal(3, (int)weapon.ArmorSlot);
        Assert.Equal(
            ShadowkeyClasses.Battlemage | ShadowkeyClasses.Nightblade | ShadowkeyClasses.Spellsword | ShadowkeyClasses.Sorcerer,
            weapon.UsableBy);
        Assert.Equal(ShadowkeyClasses.All, table.Products[1].UsableBy);
        Assert.Equal(ShadowkeyProductType.Armor, table.Products[1].Type);
    }

    [Fact]
    public void ProductTable_WhoseLengthDisagreesWithItsCount_Throws()
    {
        var bytes = ProductTable((50, 1, 10, 100, [1, 0, 0, 0, 0, 0, 0, 0, 0]));

        var error = Assert.Throws<InvalidDataException>(
            () => ShadowkeyProductTable.Parse([.. bytes, 0], "products.dat"));

        Assert.Contains("declares 1 records", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductTable_WithAnUnexpectedClassFlagCount_Throws()
    {
        var bytes = ProductTable((50, 1, 10, 100, [1, 0, 0, 0, 0, 0, 0, 0, 0]));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(ShadowkeyProductTable.HeaderLength + 15), 8);

        var error = Assert.Throws<InvalidDataException>(() => ShadowkeyProductTable.Parse(bytes, "products.dat"));

        Assert.Contains("declares 8 class flags", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductTable_WithANonBooleanClassFlag_Throws()
    {
        var bytes = ProductTable((50, 1, 10, 100, [2, 0, 0, 0, 0, 0, 0, 0, 0]));

        var error = Assert.Throws<InvalidDataException>(() => ShadowkeyProductTable.Parse(bytes, "products.dat"));

        Assert.Contains("class flag 0", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductTable_NamingAStringPastTheTable_ThrowsOnlyWhenTheCountIsGiven()
    {
        var bytes = ProductTable((50, 1, 10, 5000, [1, 0, 0, 0, 0, 0, 0, 0, 0]));

        Assert.Equal(5000, (int)ShadowkeyProductTable.Parse(bytes, "products.dat").Products[0].NameStringId);

        var error = Assert.Throws<InvalidDataException>(
            () => ShadowkeyProductTable.Parse(bytes, "products.dat", stringCount: 4082));

        Assert.Contains("past the 4082-entry string table", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductTable_ThatIsTooShortForItsHeader_Throws()
    {
        Assert.Throws<InvalidDataException>(() => ShadowkeyProductTable.Parse([1, 2], "products.dat"));
    }
}
