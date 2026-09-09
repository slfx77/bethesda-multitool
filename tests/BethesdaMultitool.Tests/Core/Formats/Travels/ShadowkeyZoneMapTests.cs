using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using BethesdaMultitool.Core.Formats.Travels.Shadowkey;
using BethesdaMultitool.Core.Imaging;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Travels;

/// <summary>
///     Synthetic vectors for the Shadowkey (N-Gage) per-zone compressed families — the
///     <see cref="ShadowkeyCompressedFile" /> envelope and the <c>.zmp</c>, <c>.zcp</c>,
///     <c>.ztx</c>, <c>.zsk</c>, <c>.zlu</c> and <c>.zfg</c> readers built on it. Everything is
///     little-endian (Symbian/ARM), the opposite of the DOS-era readers next door, so the
///     byte-order cases carry weight.
///     <para>
///         None of these formats has a magic number: the whole identification is arithmetic —
///         a declared inflated length that must match, and a header whose counts must tile the
///         payload exactly. The rejection tests below are therefore the format's real contract.
///     </para>
/// </summary>
public sealed class ShadowkeyZoneMapTests
{
    /// <summary>Wraps a payload the way every compressed Shadowkey file is wrapped.</summary>
    private static byte[] Envelope(byte[] payload, uint? declaredLength = null)
    {
        using var compressed = new MemoryStream();
        using (var deflater = new ZLibStream(compressed, CompressionLevel.Optimal, true))
        {
            deflater.Write(payload, 0, payload.Length);
        }

        var body = compressed.ToArray();
        var file = new byte[ShadowkeyCompressedFile.HeaderLength + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(file, declaredLength ?? (uint)payload.Length);
        body.CopyTo(file, ShadowkeyCompressedFile.HeaderLength);
        return file;
    }

    /// <summary>
    ///     A fixed-width string field written the way the retail struct dumps carry it: the text,
    ///     a NUL, then 0xCD heap fill and the stale tail of a longer earlier string.
    /// </summary>
    private static void WriteDirtyField(Span<byte> field, string text, string staleTail)
    {
        field.Fill(0xCD);
        var written = Encoding.Latin1.GetBytes(text, field);
        field[written] = 0;
        Encoding.Latin1.GetBytes(staleTail, field[(written + 1)..]);
    }

    private static byte[] ZoneMapPayload(int width, int height, Action<Span<byte>, int> writeCell)
    {
        var payload = new byte[ShadowkeyZoneMap.HeaderLength + width * height * ShadowkeyZoneMap.CellLength];
        WriteDirtyField(payload.AsSpan(0, ShadowkeyZoneMap.NameFieldLength), "crypt1", "azraled");
        WriteDirtyField(
            payload.AsSpan(ShadowkeyZoneMap.NameFieldLength, ShadowkeyZoneMap.AuthorFieldLength),
            "No Auth",
            "Level Pimp");
        WriteDirtyField(
            payload.AsSpan(
                ShadowkeyZoneMap.NameFieldLength + ShadowkeyZoneMap.AuthorFieldLength,
                ShadowkeyZoneMap.DescriptionFieldLength),
            "Nondescript",
            "Shadowkey Level Two");
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(128), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(130), (ushort)height);
        for (var i = 0; i < width * height; i++)
        {
            writeCell(
                payload.AsSpan(
                    ShadowkeyZoneMap.HeaderLength + i * ShadowkeyZoneMap.CellLength,
                    ShadowkeyZoneMap.CellLength),
                i);
        }

        return payload;
    }

    /// <summary>A 2x2 grid: cell 1 blocked, distinct shades, orientations and prototype indices.</summary>
    private static byte[] TwoByTwoMapPayload()
    {
        return ZoneMapPayload(2, 2, (cell, i) =>
        {
            cell[0] = i == 1 ? (byte)0x06 : (byte)0x04;
            cell[1] = 0x04;

            // Low 6 bits clear, orientation in bits 6-7, shade in the high byte.
            BinaryPrimitives.WriteUInt16LittleEndian(cell[2..], (ushort)(((i * 9) << 8) | (i << 6)));
            BinaryPrimitives.WriteUInt16LittleEndian(cell[4..], (ushort)(3 - i));
        });
    }

    private static byte[] PrototypeTablePayload(int count, Action<Span<byte>, int> writeRecord)
    {
        var payload = new byte[
            ShadowkeyCellPrototypes.HeaderLength + count * ShadowkeyCellPrototype.RecordLength];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, (uint)count);
        for (var i = 0; i < count; i++)
        {
            writeRecord(
                payload.AsSpan(
                    ShadowkeyCellPrototypes.HeaderLength + i * ShadowkeyCellPrototype.RecordLength,
                    ShadowkeyCellPrototype.RecordLength),
                i);
        }

        return payload;
    }

    /// <summary>Four prototypes: index 0 is a normal 4.0-high room, index 3 is a solid cell.</summary>
    private static byte[] FourPrototypePayload()
    {
        return PrototypeTablePayload(4, (record, i) =>
        {
            record[0] = 0xF8; // -8 as a signed shade.
            record[1] = 0xCD; // The heap-fill padding byte.
            BinaryPrimitives.WriteInt16LittleEndian(record[2..], 0);
            BinaryPrimitives.WriteInt16LittleEndian(record[4..], 0x0400);
            for (var corner = 0; corner < ShadowkeyCellPrototype.CornerCount; corner++)
            {
                BinaryPrimitives.WriteInt16LittleEndian(record[(6 + corner * 2)..], (short)(corner * 0x0100));

                // The last prototype is solid: its ceiling drops to meet the floor.
                BinaryPrimitives.WriteInt16LittleEndian(
                    record[(14 + corner * 2)..], i == 3 ? (short)0 : (short)0x0400);
            }

            record[22] = 5;
            record[23] = ShadowkeyCellPrototype.NoSurface;
            record[30] = 0x04;
            BinaryPrimitives.WriteUInt16LittleEndian(record[34..], 0x0123);
        });
    }

    private static byte[] TextureBankPayload(int count, int textureLength = ShadowkeyTextureBank.TextureLength)
    {
        var payload = new byte[ShadowkeyTextureBank.HeaderLength + count * textureLength];
        payload[0] = (byte)count;
        for (var i = 0; i < count * textureLength; i++)
        {
            payload[ShadowkeyTextureBank.HeaderLength + i] = (byte)(i & 0xFF);
        }

        return payload;
    }

    /// <summary>An 8-bit palette: a grey ramp with the 0xFF00FF colour key at index 6.</summary>
    private static byte[] PaletteBytes()
    {
        var rgb = new byte[Palette.RgbByteCount];
        for (var i = 0; i < Palette.EntryCount; i++)
        {
            rgb[i * 3] = (byte)i;
            rgb[i * 3 + 1] = (byte)(255 - i);
            rgb[i * 3 + 2] = 128;
        }

        rgb[6 * 3] = 0xFF;
        rgb[6 * 3 + 1] = 0x00;
        rgb[6 * 3 + 2] = 0xFF;
        return rgb;
    }

    private static byte[] SkyboxPayload(int vertexCount, int cornerCount, int faceCount, int gapLength)
    {
        var meshLength = ShadowkeySkybox.HeaderLength + vertexCount * 6 + cornerCount * 4 + faceCount * 12;
        var payload = new byte[meshLength + gapLength + ShadowkeySkybox.TextureLength + ShadowkeySkybox.FooterLength];
        BinaryPrimitives.WriteUInt16LittleEndian(payload, 7);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4), (ushort)vertexCount);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(6), (ushort)cornerCount);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(8), (ushort)faceCount);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(10), (ushort)(vertexCount * 3));
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(12), 1);

        for (var i = 0; i < vertexCount; i++)
        {
            var offset = ShadowkeySkybox.HeaderLength + i * 6;
            BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(offset), (short)(-100 - i));
            BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(offset + 2), (short)(i * 2));
            BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(offset + 4), (short)(200 + i));
        }

        var cornerOffset = ShadowkeySkybox.HeaderLength + vertexCount * 6;
        for (var i = 0; i < cornerCount; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(cornerOffset + i * 4), (ushort)(i * 256));
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(cornerOffset + i * 4 + 2), 256);
        }

        var faceOffset = cornerOffset + cornerCount * 4;
        for (var i = 0; i < faceCount; i++)
        {
            var offset = faceOffset + i * 12;
            for (var word = 0; word < 3; word++)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(
                    payload.AsSpan(offset + word * 2), (ushort)((i + word) % vertexCount));
                BinaryPrimitives.WriteUInt16LittleEndian(
                    payload.AsSpan(offset + 6 + word * 2), (ushort)((i + word) % cornerCount));
            }
        }

        var imageOffset = payload.Length - ShadowkeySkybox.FooterLength - ShadowkeySkybox.TextureLength;
        payload[imageOffset] = 0x2A;
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(payload.Length - 8), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(payload.Length - 6), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(payload.Length - 4), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(payload.Length - 2), 10);
        return payload;
    }

    private static byte[] ToBytes(ReadOnlyMemory<ushort> entries)
    {
        var bytes = new byte[entries.Length * 2];
        for (var i = 0; i < entries.Length; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2), entries.Span[i]);
        }

        return bytes;
    }

    [Fact]
    public void Inflate_ReturnsThePayloadWhenTheDeclaredLengthMatches()
    {
        var payload = TwoByTwoMapPayload();

        var inflated = ShadowkeyCompressedFile.Inflate(Envelope(payload), "crypt1.zmp");

        Assert.Equal(payload, inflated);
    }

    [Fact]
    public void Inflate_RejectsAWrongDeclaredLengthAndNamesBothNumbers()
    {
        var payload = TwoByTwoMapPayload();

        var error = Assert.Throws<InvalidDataException>(() =>
            ShadowkeyCompressedFile.Inflate(Envelope(payload, (uint)payload.Length + 1), "crypt1.zmp"));

        Assert.Contains("crypt1.zmp", error.Message, StringComparison.Ordinal);
        Assert.Contains(payload.Length.ToString(CultureInfo.InvariantCulture), error.Message, StringComparison.Ordinal);
        Assert.Contains((payload.Length + 1).ToString(CultureInfo.InvariantCulture), error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Inflate_RejectsBytesThatDoNotOpenAZlibStream()
    {
        var file = new byte[64];
        BinaryPrimitives.WriteUInt32LittleEndian(file, 32);
        file[4] = 0x1F; // gzip, not zlib
        file[5] = 0x8B;

        var error = Assert.Throws<InvalidDataException>(() => ShadowkeyCompressedFile.Inflate(file, "crypt1.zcp"));

        Assert.Contains("crypt1.zcp", error.Message, StringComparison.Ordinal);
        Assert.Contains("zlib", error.Message, StringComparison.Ordinal);
        Assert.False(ShadowkeyCompressedFile.LooksLike(file));
    }

    [Fact]
    public void Inflate_RejectsAFileTooShortToHoldTheLengthPrefix()
    {
        var error = Assert.Throws<InvalidDataException>(() => ShadowkeyCompressedFile.Inflate(new byte[4], "stub.zmp"));

        Assert.Contains("stub.zmp", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Inflate_RejectsACorruptStreamThatPassesTheHeaderCheck()
    {
        var file = Envelope(TwoByTwoMapPayload());
        file[^1] ^= 0xFF;
        file[^2] ^= 0xFF;

        var error = Assert.Throws<InvalidDataException>(() => ShadowkeyCompressedFile.Inflate(file, "crypt1.zmp"));

        Assert.Contains("crypt1.zmp", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ZoneMap_ReadsTheHeaderStringsUpToTheirFirstNul()
    {
        var map = ShadowkeyZoneMap.Parse(TwoByTwoMapPayload(), "crypt1.zmp");

        Assert.Equal("crypt1", map.ZoneName);
        Assert.Equal("No Auth", map.Author);
        Assert.Equal("Nondescript", map.Description);
    }

    [Fact]
    public void ZoneMap_ReadsCellsRowMajorWithXFastest()
    {
        var map = ShadowkeyZoneMap.Parse(TwoByTwoMapPayload(), "crypt1.zmp");

        Assert.Equal(2, map.Width);
        Assert.Equal(2, map.Height);
        Assert.Equal(4, map.Cells.Count);

        // Cell index 1 is (x=1, y=0), not (x=0, y=1).
        Assert.True(map.Cell(1, 0).IsBlocked);
        Assert.True(map.IsBlocked(1, 0));
        Assert.False(map.Cell(0, 1).IsBlocked);
        Assert.Equal(1, map.BlockedCellCount);

        Assert.Equal(1, map.Cell(0, 1).PrototypeIndex);
        Assert.Equal(3, map.MaxPrototypeIndex);
    }

    [Fact]
    public void ZoneMap_ExposesTheRawWordAlongsideBothReadingsOfIt()
    {
        var map = ShadowkeyZoneMap.Parse(TwoByTwoMapPayload(), "crypt1.zmp");
        var cell = map.Cell(0, 1); // index 2: shade 18, orientation 2

        Assert.Equal((ushort)((18 << 8) | (2 << 6)), cell.Raw);
        Assert.Equal(18, cell.Shade);
        Assert.Equal(2, cell.Orientation);
        Assert.Equal((byte)((18 << 2) | 2), cell.PackedValue);
        Assert.True(map.RawLowBitsClear);
    }

    [Fact]
    public void ZoneMap_ReportsRatherThanRejectsACellWithLowBitsSet()
    {
        var payload = TwoByTwoMapPayload();
        payload[ShadowkeyZoneMap.HeaderLength + 2] |= 0x01;

        var map = ShadowkeyZoneMap.Parse(payload, "crypt1.zmp");

        Assert.False(map.RawLowBitsClear);
        Assert.Equal(1, map.Cells[0].Raw & 0x3F);
    }

    [Fact]
    public void ZoneMap_RejectsAGridThatDoesNotTileThePayload()
    {
        var payload = TwoByTwoMapPayload();
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(128), 3);

        var error = Assert.Throws<InvalidDataException>(() => ShadowkeyZoneMap.Parse(payload, "crypt1.zmp"));

        Assert.Contains("crypt1.zmp", error.Message, StringComparison.Ordinal);
        Assert.Contains(payload.Length.ToString(CultureInfo.InvariantCulture), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ZoneMap_RejectsAZeroDimensionAndATruncatedHeader()
    {
        var payload = TwoByTwoMapPayload();
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(130), 0);

        Assert.Throws<InvalidDataException>(() => ShadowkeyZoneMap.Parse(payload, "crypt1.zmp"));
        Assert.Throws<InvalidDataException>(() =>
            ShadowkeyZoneMap.Parse(new byte[ShadowkeyZoneMap.HeaderLength - 1], "crypt1.zmp"));
    }

    [Fact]
    public void ZoneMap_RejectsCoordinatesOutsideTheGrid()
    {
        var map = ShadowkeyZoneMap.Parse(TwoByTwoMapPayload(), "crypt1.zmp");

        Assert.Throws<ArgumentOutOfRangeException>(() => map.Cell(2, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => map.Cell(0, -1));
    }

    [Fact]
    public void CellPrototypes_ReadTheThirtySixByteRecordFields()
    {
        var table = ShadowkeyCellPrototypes.Parse(FourPrototypePayload(), "crypt1.zcp");

        Assert.Equal(4, table.Count);
        var record = table.Records[0];
        Assert.Equal(-8, record.Shade);
        Assert.Equal(0xCD, record.Padding);
        Assert.Equal(0, record.ReferenceFloor);
        Assert.Equal(0x0400, record.ReferenceCeiling);
        Assert.Equal(4f, ShadowkeyCellPrototype.ToUnits(record.ReferenceCeiling));
        Assert.Equal(new short[] { 0, 0x0100, 0x0200, 0x0300 }, record.FloorCorners);
        Assert.Equal(new short[] { 0x0400, 0x0400, 0x0400, 0x0400 }, record.CeilingCorners);
        Assert.Equal(5, record.SurfaceSlots[0]);
        Assert.Equal(ShadowkeyCellPrototype.NoSurface, record.SurfaceSlots[1]);
        Assert.Equal(0x04, record.Edge[0]);
        Assert.Equal(0x0123, record.Extra);
        Assert.False(record.IsSolid);
        Assert.True(table.Records[3].IsSolid);
    }

    [Fact]
    public void CellPrototypes_RejectATableThatDoesNotTileThePayload()
    {
        var payload = FourPrototypePayload();
        BinaryPrimitives.WriteUInt32LittleEndian(payload, 5);

        var error = Assert.Throws<InvalidDataException>(() => ShadowkeyCellPrototypes.Parse(payload, "crypt1.zcp"));

        Assert.Contains("crypt1.zcp", error.Message, StringComparison.Ordinal);
        Assert.Contains("5", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CellPrototypes_RejectATruncatedCount()
    {
        Assert.Throws<InvalidDataException>(() => ShadowkeyCellPrototypes.Parse(new byte[3], "crypt1.zcp"));
    }

    [Fact]
    public void CellPrototypes_ValidateAgainstRejectsAMapIndexPastTheTable()
    {
        var map = ShadowkeyZoneMap.Parse(TwoByTwoMapPayload(), "crypt1.zmp");
        var table = ShadowkeyCellPrototypes.Parse(
            PrototypeTablePayload(2, (record, _) => record[1] = 0xCD), "crypt1.zcp");

        var error = Assert.Throws<InvalidDataException>(() => table.ValidateAgainst(map, "crypt1.zcp"));

        Assert.Contains("crypt1.zcp", error.Message, StringComparison.Ordinal);
        Assert.Contains("indexes prototype 3", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CellPrototypes_ValidateAgainstAcceptsAMapThatUsesEveryRecord()
    {
        var map = ShadowkeyZoneMap.Parse(TwoByTwoMapPayload(), "crypt1.zmp");
        var table = ShadowkeyCellPrototypes.Parse(FourPrototypePayload(), "crypt1.zcp");

        table.ValidateAgainst(map, "crypt1.zcp");

        Assert.Equal(0, table.CountUnreferencedBy(map));
        Assert.Equal(table.Count - 1, map.MaxPrototypeIndex);
    }

    [Fact]
    public void CellPrototypes_CountUnreferencedByFindsSlackInTheTable()
    {
        var map = ShadowkeyZoneMap.Parse(TwoByTwoMapPayload(), "crypt1.zmp");
        var table = ShadowkeyCellPrototypes.Parse(
            PrototypeTablePayload(6, (record, _) => record[1] = 0xCD), "crypt1.zcp");

        Assert.Equal(2, table.CountUnreferencedBy(map));
    }

    [Fact]
    public void TextureBank_ReadsCountTimes128x128Textures()
    {
        var bank = ShadowkeyTextureBank.Parse(TextureBankPayload(2), "crypt1.ztx");

        Assert.Equal(2, bank.Count);
        Assert.All(bank.Textures, texture =>
        {
            Assert.Equal(ShadowkeyTextureBank.TextureWidth, texture.Width);
            Assert.Equal(ShadowkeyTextureBank.TextureHeight, texture.Height);
        });

        // Second texture starts where the first ends: byte 16384 of the pixel run. The file stores
        // rows bottom-up, so that first file row lands on the LAST row of the bitmap...
        var lastRow = (ShadowkeyTextureBank.TextureHeight - 1) * ShadowkeyTextureBank.TextureWidth;
        Assert.Equal((byte)0, bank.Textures[1].Indices[lastRow]);
        Assert.Equal((byte)1, bank.Textures[0].Indices[lastRow + 1]);

        // ...and the file's last row (pixel-run bytes 16256..16383) becomes the bitmap's top row.
        Assert.Equal((byte)(16256 & 0xFF), bank.Textures[0].Indices[0]);
    }

    [Fact]
    public void TextureBank_RejectsAPayloadThatIsNotOnePlusCountTimes16384()
    {
        var payload = TextureBankPayload(2, ShadowkeyTextureBank.TextureLength - 1);

        var error = Assert.Throws<InvalidDataException>(() => ShadowkeyTextureBank.Parse(payload, "crypt1.ztx"));

        Assert.Contains("crypt1.ztx", error.Message, StringComparison.Ordinal);
        Assert.Contains(payload.Length.ToString(CultureInfo.InvariantCulture), error.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => ShadowkeyTextureBank.Parse(Array.Empty<byte>(), "crypt1.ztx"));
    }

    [Fact]
    public void TextureBank_DecodesThroughTheZonePaletteWithoutPromotingComponents()
    {
        var bank = ShadowkeyTextureBank.Parse(TextureBankPayload(1), "crypt1.ztx");
        var palette = ShadowkeyZonePalette.Parse(PaletteBytes(), "crypt1.pal");

        var texture = bank.Decode(0, palette);
        var pixels = texture.Pixels;

        // The file's first row is the bitmap's LAST row (bottom-up storage). Its first texel is
        // index 0 -> (0, 255, 128) verbatim; a 6-bit promotion would scale it.
        var bottomLeft = (ShadowkeyTextureBank.TextureHeight - 1) * ShadowkeyTextureBank.TextureWidth * 4;
        Assert.Equal(0, pixels[bottomLeft]);
        Assert.Equal(255, pixels[bottomLeft + 1]);
        Assert.Equal(128, pixels[bottomLeft + 2]);

        // Texel 100 of that row is index 100 -> (100, 155, 128).
        Assert.Equal(100, pixels[bottomLeft + 100 * 4]);
        Assert.Equal(155, pixels[bottomLeft + 100 * 4 + 1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => bank.Decode(1, palette));
    }

    [Fact]
    public void ZonePalette_FindsTheColourKeyAndMakesItTransparent()
    {
        var bytes = PaletteBytes();

        Assert.Equal(6, ShadowkeyZonePalette.FindColourKeyIndex(bytes));
        Assert.Equal(255, ShadowkeyZonePalette.Parse(bytes, "crypt1.pal").GetEntry(6).A);
        Assert.Equal(0, ShadowkeyZonePalette.ParseWithColourKey(bytes, "crypt1.pal").GetEntry(6).A);
    }

    [Fact]
    public void ZonePalette_RejectsAFileThatIsNot768Bytes()
    {
        var error = Assert.Throws<InvalidDataException>(() => ShadowkeyZonePalette.Parse(new byte[767], "crypt1.pal"));

        Assert.Contains("crypt1.pal", error.Message, StringComparison.Ordinal);
        Assert.Null(ShadowkeyZonePalette.FindColourKeyIndex(new byte[767]));
    }

    [Fact]
    public void Skybox_ReadsTheMeshAndLocatesTheImageFromTheFileEnd()
    {
        var sky = ShadowkeySkybox.Parse(
            SkyboxPayload(4, 5, 2, ShadowkeySkybox.OutdoorGapLength),
            "azra.zsk");

        Assert.Equal(4, sky.Vertices.Count);
        Assert.Equal(new ShadowkeySkyVertex(-101, 2, 201), sky.Vertices[1]);
        Assert.Equal(5, sky.Corners.Count);
        Assert.Equal(1f, sky.Corners[1].UnitsU);
        Assert.Equal(2, sky.Faces.Count);
        Assert.Equal(new ShadowkeySkyFace(1, 2, 3, 1, 2, 3), sky.Faces[1]);
        Assert.True(sky.IsOutdoorClass);
        Assert.Equal(ShadowkeySkybox.TextureWidth, sky.Texture.Width);
        Assert.Equal(ShadowkeySkybox.TextureHeight, sky.Texture.Height);
        Assert.Equal((byte)0x2A, sky.Texture.Indices[0]);
        Assert.True(sky.HasPaintedTexture);
        Assert.Equal(new ushort[] { 1, 0, 1, 10 }, sky.Footer);
    }

    [Fact]
    public void Skybox_AcceptsTheFourByteInteriorGap()
    {
        var sky = ShadowkeySkybox.Parse(
            SkyboxPayload(3, 3, 1, ShadowkeySkybox.InteriorGapLength),
            "crypt1.zsk");

        Assert.False(sky.IsOutdoorClass);
        Assert.Equal(ShadowkeySkybox.InteriorGapLength, sky.GapLength);
    }

    [Fact]
    public void Skybox_RejectsAGapThatIsNeitherFourNorSix()
    {
        var payload = SkyboxPayload(4, 5, 2, 5);

        var error = Assert.Throws<InvalidDataException>(() => ShadowkeySkybox.Parse(payload, "azra.zsk"));

        Assert.Contains("azra.zsk", error.Message, StringComparison.Ordinal);
        Assert.Contains("gap of 5", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Skybox_RejectsWrongHeaderConstantsAndAMiscountedCoordinateBlock()
    {
        var wrongTag = SkyboxPayload(4, 5, 2, ShadowkeySkybox.OutdoorGapLength);
        BinaryPrimitives.WriteUInt16LittleEndian(wrongTag.AsSpan(0), 8);
        Assert.Throws<InvalidDataException>(() => ShadowkeySkybox.Parse(wrongTag, "azra.zsk"));

        var wrongCoordinates = SkyboxPayload(4, 5, 2, ShadowkeySkybox.OutdoorGapLength);
        BinaryPrimitives.WriteUInt16LittleEndian(wrongCoordinates.AsSpan(10), 11);
        var error = Assert.Throws<InvalidDataException>(() => ShadowkeySkybox.Parse(wrongCoordinates, "azra.zsk"));
        Assert.Contains("11 coordinates", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Skybox_RejectsAFaceThatIndexesAVertexThatDoesNotExist()
    {
        var payload = SkyboxPayload(4, 5, 2, ShadowkeySkybox.OutdoorGapLength);
        var faceOffset = ShadowkeySkybox.HeaderLength + 4 * 6 + 5 * 4;
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(faceOffset), 4);

        var error = Assert.Throws<InvalidDataException>(() => ShadowkeySkybox.Parse(payload, "azra.zsk"));

        Assert.Contains("face vertex index 4", error.Message, StringComparison.Ordinal);
        Assert.Contains(faceOffset.ToString(CultureInfo.InvariantCulture), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LightTable_RoundTripsTheSynthesisRecipeAndPinsTheColourKey()
    {
        var palette = ShadowkeyZonePalette.Parse(PaletteBytes(), "crypt1.pal");
        var synthesized = ShadowkeyLightTable.Synthesize(palette, "crypt1.zlu");

        var parsed = ShadowkeyLightTable.Parse(ToBytes(synthesized.Entries), "crypt1.zlu");

        Assert.True(parsed.MatchesPalette(palette));
        Assert.Equal(ShadowkeyLightTable.ColourKeyEntry, parsed.Lookup(0, 0, 6));
        Assert.Equal(ShadowkeyLightTable.ColourKeyEntry, parsed.Lookup(3, 63, 6));

        // Level 0 is black, and the red bank tints green and blue to 2/5 of the palette value.
        Assert.Equal(0, parsed.Lookup(0, 0, 200));
        Assert.Equal(
            (ushort)((Math.Min(15, 200 * 63 / ShadowkeyLightTable.LightDivisor) << 8)
                     | (Math.Min(15, 55 * 2 / 5 * 63 / ShadowkeyLightTable.LightDivisor) << 4)
                     | Math.Min(15, 128 * 2 / 5 * 63 / ShadowkeyLightTable.LightDivisor)),
            parsed.Lookup(1, 63, 200));
    }

    [Fact]
    public void LightTable_RejectsAWrongSizedPayloadAndAnEntryPastTwelveBits()
    {
        var error = Assert.Throws<InvalidDataException>(() =>
            ShadowkeyLightTable.Parse(new byte[ShadowkeyLightTable.PayloadLength - 2], "crypt1.zlu"));
        Assert.Contains("crypt1.zlu", error.Message, StringComparison.Ordinal);

        var bytes = new byte[ShadowkeyLightTable.PayloadLength];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8), 0x1000);
        var overflow = Assert.Throws<InvalidDataException>(() => ShadowkeyLightTable.Parse(bytes, "crypt1.zlu"));
        Assert.Contains("byte 8", overflow.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LightTable_RejectsLookupsOutsideTheTable()
    {
        var table = ShadowkeyLightTable.Parse(new byte[ShadowkeyLightTable.PayloadLength], "crypt1.zlu");

        Assert.Throws<ArgumentOutOfRangeException>(() => table.Lookup(4, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => table.Lookup(0, 64, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => table.Lookup(0, 0, 256));
    }

    [Fact]
    public void FogTable_RecoversTheFogColourFromTheMostFoggedRow()
    {
        var synthesized = ShadowkeyFogTable.Synthesize(10, 10, 11, "GhstPass.zfg");

        var parsed = ShadowkeyFogTable.Parse(ToBytes(synthesized.Entries), "GhstPass.zfg");

        Assert.Equal(10, parsed.FogRed);
        Assert.Equal(10, parsed.FogGreen);
        Assert.Equal(11, parsed.FogBlue);
        Assert.Equal(0x0AAB, parsed.FogColour);
        Assert.True(parsed.IsLevelZeroIdentity);
        Assert.True(parsed.MatchesBlendRecipe);
        Assert.Equal(0x0123, parsed.Lookup(0, 0x0123));

        // The most-fogged row still carries 1/16 of the input, so it is not a constant.
        Assert.NotEqual(parsed.Lookup(15, 0x0000), parsed.Lookup(15, 0x0FFF));
    }

    [Fact]
    public void FogTable_ReportsRatherThanRejectsATableThatIsNotTheBlend()
    {
        var synthesized = ShadowkeyFogTable.Synthesize(0, 0, 0, "crypt1.zfg");
        var bytes = ToBytes(synthesized.Entries);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), 0x0555);

        var parsed = ShadowkeyFogTable.Parse(bytes, "crypt1.zfg");

        Assert.False(parsed.MatchesBlendRecipe);
        Assert.False(parsed.IsLevelZeroIdentity);
    }

    [Fact]
    public void FogTable_RejectsAWrongSizedPayloadAnEntryPastTwelveBitsAndABadLookup()
    {
        Assert.Throws<InvalidDataException>(() =>
            ShadowkeyFogTable.Parse(new byte[ShadowkeyFogTable.PayloadLength + 2], "crypt1.zfg"));

        var bytes = new byte[ShadowkeyFogTable.PayloadLength];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), 0xF000);
        var overflow = Assert.Throws<InvalidDataException>(() => ShadowkeyFogTable.Parse(bytes, "crypt1.zfg"));
        Assert.Contains("byte 6", overflow.Message, StringComparison.Ordinal);

        var table = ShadowkeyFogTable.Synthesize(0, 0, 0, "crypt1.zfg");
        Assert.Throws<ArgumentOutOfRangeException>(() => table.Lookup(16, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => table.Lookup(0, 4096));
        Assert.Throws<ArgumentOutOfRangeException>(() => ShadowkeyFogTable.Synthesize(16, 0, 0, "crypt1.zfg"));
    }
}