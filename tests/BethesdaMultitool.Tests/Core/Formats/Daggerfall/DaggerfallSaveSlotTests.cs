using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Daggerfall;
using BethesdaMultitool.Core.Formats.Png;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Daggerfall;

/// <summary>
///     Synthetic vectors for <see cref="DaggerfallSaveSlot" />: the eight-file slot directory, the
///     NUL conventions of SAVENAME.TXT and BIO.DAT, and the 80 x 50 thumbnail through ART_PAL.COL.
/// </summary>
public class DaggerfallSaveSlotTests
{
    private static byte[] SaveName(string text, byte residue = 0)
    {
        var bytes = new byte[DaggerfallSaveSlot.SaveNameLength];
        Encoding.ASCII.GetBytes(text).CopyTo(bytes, 0);
        bytes[^1] = residue;
        return bytes;
    }

    /// <summary>A COL palette file: u32 length, u16 magic, u16 version, 768 full-range RGB bytes.</summary>
    private static byte[] Palette(byte redOfEntryOne = 0x10, byte greenOfEntryOne = 0x20, byte blueOfEntryOne = 0x30)
    {
        var bytes = new byte[776];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 776);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), 0xB123);
        bytes[8 + 3] = redOfEntryOne;
        bytes[8 + 4] = greenOfEntryOne;
        bytes[8 + 5] = blueOfEntryOne;
        return bytes;
    }

    private static byte[] Tree()
    {
        return DaggerfallSaveTreeTests.File(
            [DaggerfallSaveTreeTests.Root(0x03, 0x00012711)],
            null);
    }

    [Fact]
    public void ReadSaveName_StopsAtTheFirstNulAndIgnoresResidue()
    {
        Assert.Equal("Hans", DaggerfallSaveSlot.ReadSaveName(SaveName("Hans"), "SAVENAME.TXT"));
        Assert.Equal("Hans", DaggerfallSaveSlot.ReadSaveName(SaveName("Hans", (byte)'X'), "SAVENAME.TXT"));
        Assert.Equal(string.Empty, DaggerfallSaveSlot.ReadSaveName(new byte[32], "SAVENAME.TXT"));
    }

    [Fact]
    public void ReadSaveName_RejectsAnyOtherLength()
    {
        Assert.Throws<InvalidDataException>(() => DaggerfallSaveSlot.ReadSaveName(new byte[31], "SAVENAME.TXT"));
        Assert.Throws<InvalidDataException>(() => DaggerfallSaveSlot.ReadSaveName(new byte[33], "SAVENAME.TXT"));
    }

    /// <summary>
    ///     BIO.DAT ends with a NUL, so a raw split leaves an empty entry; the empty line terminates
    ///     the list and neither it nor the split artefact is a line.
    /// </summary>
    [Fact]
    public void ReadBiography_SplitsOnNulsAndStopsAtTheEmptyLine()
    {
        var bytes = Encoding.ASCII.GetBytes("You have vague memories\0of a young woman\0\0");

        var lines = DaggerfallSaveSlot.ReadBiography(bytes);

        Assert.Equal(2, lines.Count);
        Assert.Equal("You have vague memories", lines[0]);
        Assert.Equal("of a young woman", lines[1]);
        Assert.Empty(DaggerfallSaveSlot.ReadBiography([]));
        Assert.Empty(DaggerfallSaveSlot.ReadBiography([0]));
    }

    [Fact]
    public void ReadImage_DemandsExactlyEightyByFiftyIndices()
    {
        var image = DaggerfallSaveSlot.ReadImage(new byte[DaggerfallSaveSlot.ImageLength], "IMAGE.RAW");

        Assert.Equal(80, image.Width);
        Assert.Equal(50, image.Height);
        Assert.Equal(4_000, image.Indices.Length);
        Assert.Throws<InvalidDataException>(() => DaggerfallSaveSlot.ReadImage(new byte[3_999], "IMAGE.RAW"));
        Assert.Throws<InvalidDataException>(() => DaggerfallSaveSlot.ReadImage(new byte[8_000], "IMAGE.RAW"));
    }

    [Fact]
    public void Load_RequiresTheTreeAndTreatsEveryOtherFileAsOptional()
    {
        var root = Path.Combine(Path.GetTempPath(), "bmt-daggerfall-save-" + Guid.NewGuid().ToString("N"));
        var slotPath = Path.Combine(root, "SAVE0");
        Directory.CreateDirectory(slotPath);
        try
        {
            Assert.False(DaggerfallSaveSlot.IsSaveSlot(slotPath));
            Assert.Throws<FileNotFoundException>(() => DaggerfallSaveSlot.Load(slotPath));

            File.WriteAllBytes(Path.Combine(slotPath, DaggerfallSaveSlot.TreeFileName), Tree());
            Assert.True(DaggerfallSaveSlot.IsSaveSlot(slotPath));

            var slot = DaggerfallSaveSlot.Load(slotPath);
            Assert.Single(slot.Tree.Records);
            Assert.Null(slot.Vars);
            Assert.Null(slot.RumorFile);
            Assert.Null(slot.SaveName);
            Assert.Null(slot.Image);
            Assert.Empty(slot.BiographyLines);
            Assert.Empty(slot.Automaps);
            Assert.Null(slot.TryLoadPalette());
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Load_ReadsEveryFileOfAFullSlot()
    {
        var root = Path.Combine(Path.GetTempPath(), "bmt-daggerfall-save-" + Guid.NewGuid().ToString("N"));
        var slotPath = Path.Combine(root, "SAVE0");
        var arena2 = Path.Combine(root, DaggerfallSaveSlot.DataDirectoryName);
        Directory.CreateDirectory(slotPath);
        Directory.CreateDirectory(arena2);
        try
        {
            var indices = new byte[DaggerfallSaveSlot.ImageLength];
            indices[0] = 1;
            var automap = new byte[DaggerfallSaveAutomapFile.FileLength];
            BinaryPrimitives.WriteUInt32LittleEndian(automap, 524_043);
            automap[4] = 0x33;

            File.WriteAllBytes(Path.Combine(slotPath, DaggerfallSaveSlot.TreeFileName), Tree());
            File.WriteAllBytes(Path.Combine(slotPath, DaggerfallSaveSlot.VarsFileName), DaggerfallSaveVarsTests.Vars());
            File.WriteAllBytes(
                Path.Combine(slotPath, DaggerfallSaveSlot.RumorFileName),
                DaggerfallSaveRumorFileTests.Rumor(208, 12, 32, 8, "Rlerki is the new Count of Shalgora."));
            File.WriteAllBytes(Path.Combine(slotPath, DaggerfallSaveSlot.NameFileName), SaveName("Hans"));
            File.WriteAllBytes(Path.Combine(slotPath, DaggerfallSaveSlot.BiographyFileName),
                Encoding.ASCII.GetBytes("One line\0\0"));
            File.WriteAllBytes(Path.Combine(slotPath, DaggerfallSaveSlot.ImageFileName), indices);
            File.WriteAllBytes(Path.Combine(slotPath, "AT50050.AMF"), automap);
            File.WriteAllBytes(Path.Combine(arena2, DaggerfallSaveSlot.PaletteFileName), Palette());

            var slot = DaggerfallSaveSlot.Load(slotPath);

            Assert.Equal("Hans", slot.SaveName);
            Assert.Equal(524_043u, slot.Vars!.GameTimeMinutes);
            Assert.Single(slot.RumorFile!.Rumors);
            Assert.Equal("One line", Assert.Single(slot.BiographyLines));
            Assert.Equal(80, slot.Image!.Width);

            var map = Assert.Single(slot.Automaps);
            Assert.Equal("AT50050.AMF", map.Name);
            Assert.Equal(50_050, map.LocationId);
            Assert.Equal(524_043u, map.TimeStamp);
            Assert.Equal(DaggerfallSaveAutomapFile.PayloadLength, map.Payload.Length);
            Assert.Equal(0x33, map.Payload.Span[0]);

            // The thumbnail resolves through ARENA2\ART_PAL.COL beside the slot's parent, and the
            // palette's entry 1 comes back out of the encoded PNG unchanged (full-range 8-bit, NOT
            // promoted from 6 bits — that would quarter every component).
            var palette = slot.TryLoadPalette();
            Assert.NotNull(palette);
            var png = slot.EncodeImagePng(palette);
            var decoded = PngImageDecoder.Decode(png);
            Assert.Equal(80, decoded.Width);
            Assert.Equal(50, decoded.Height);
            Assert.Equal(0x10, decoded.Pixels[0]);
            Assert.Equal(0x20, decoded.Pixels[1]);
            Assert.Equal(0x30, decoded.Pixels[2]);
            Assert.Equal(255, decoded.Pixels[3]);

            var pngPath = Path.Combine(root, "thumb.png");
            slot.SaveImagePng(pngPath, palette);
            Assert.True(new FileInfo(pngPath).Length > 0);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Load_RefusesAnAutomapOfTheWrongLength()
    {
        var root = Path.Combine(Path.GetTempPath(), "bmt-daggerfall-save-" + Guid.NewGuid().ToString("N"));
        var slotPath = Path.Combine(root, "SAVE0");
        Directory.CreateDirectory(slotPath);
        try
        {
            File.WriteAllBytes(Path.Combine(slotPath, DaggerfallSaveSlot.TreeFileName), Tree());
            File.WriteAllBytes(Path.Combine(slotPath, "AT50050.AMF"),
                new byte[DaggerfallSaveAutomapFile.FileLength - 1]);

            Assert.Throws<InvalidDataException>(() => DaggerfallSaveSlot.Load(slotPath));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void EncodeImagePng_ThrowsWhenTheSlotHasNoThumbnail()
    {
        var root = Path.Combine(Path.GetTempPath(), "bmt-daggerfall-save-" + Guid.NewGuid().ToString("N"));
        var slotPath = Path.Combine(root, "SAVE0");
        Directory.CreateDirectory(slotPath);
        try
        {
            File.WriteAllBytes(Path.Combine(slotPath, DaggerfallSaveSlot.TreeFileName), Tree());
            var slot = DaggerfallSaveSlot.Load(slotPath);

            Assert.Throws<InvalidOperationException>(() =>
                slot.EncodeImagePng(BethesdaMultitool.Core.Imaging.Palette.LoadDaggerfallCol(Palette())));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}