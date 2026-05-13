using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Plugin;

/// <summary>
///     v8 tests for MODT (texture hash) + ICON/MICO (paperdoll/inventory icon) emission
///     across every encoder that produces MODL. MODT is an opaque byte-array subrecord;
///     ICON/MICO are null-terminated paths. None of these are emitted when the underlying
///     model field is null/empty/zero-length.
/// </summary>
public class TextureHashIconEncoderTests
{
    private static readonly byte[] SampleModt = [0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02];

    // ====================================================================================
    // World object encoders — MODT only (no ICON/MICO)
    // ====================================================================================

    [Fact]
    public void StatEncoder_EncodeNew_EmitsModtAfterModlWhenPresent()
    {
        var stat = new StaticRecord
        {
            FormId = 0x100,
            EditorId = "S",
            ModelPath = "m.nif",
            TextureHashData = SampleModt
        };

        var encoded = StatEncoder.EncodeNew(stat);

        Assert.Equal(["EDID", "MODL", "MODT"], encoded.Subrecords.Select(s => s.Signature));
        var modt = Assert.Single(encoded.Subrecords, s => s.Signature == "MODT");
        Assert.Equal(SampleModt, modt.Bytes);
    }

    [Fact]
    public void StatEncoder_EncodeNew_OmitsModtWhenNull()
    {
        var stat = new StaticRecord
        {
            FormId = 0x100,
            EditorId = "S",
            ModelPath = "m.nif",
            TextureHashData = null
        };

        var encoded = StatEncoder.EncodeNew(stat);

        Assert.DoesNotContain(encoded.Subrecords, s => s.Signature == "MODT");
    }

    [Fact]
    public void StatEncoder_EncodeNew_OmitsModtWhenEmpty()
    {
        var stat = new StaticRecord
        {
            FormId = 0x100,
            EditorId = "S",
            ModelPath = "m.nif",
            TextureHashData = []
        };

        var encoded = StatEncoder.EncodeNew(stat);

        Assert.DoesNotContain(encoded.Subrecords, s => s.Signature == "MODT");
    }

    [Fact]
    public void ActiEncoder_EncodeNew_EmitsModtBetweenModlAndSubsequentSubrecords()
    {
        var acti = new ActivatorRecord
        {
            FormId = 0x300,
            EditorId = "A",
            ModelPath = "a.nif",
            Script = 0x1,
            TextureHashData = SampleModt
        };

        var encoded = ActiEncoder.EncodeNew(acti);

        Assert.Equal(["EDID", "MODL", "MODT", "SCRI"], encoded.Subrecords.Select(s => s.Signature));
    }

    [Fact]
    public void DoorEncoder_EncodeNew_EmitsModt()
    {
        var door = new DoorRecord
        {
            FormId = 0x400,
            EditorId = "D",
            ModelPath = "d.nif",
            TextureHashData = SampleModt,
            Flags = 0x01
        };

        var encoded = DoorEncoder.EncodeNew(door);

        Assert.Contains(encoded.Subrecords, s => s.Signature == "MODT");
    }

    [Fact]
    public void LighEncoder_EncodeNew_EmitsModtBetweenModlAndFullAndData()
    {
        var ligh = new LightRecord
        {
            FormId = 0x500,
            EditorId = "L",
            ModelPath = "lamp.nif",
            FullName = "Lamp",
            TextureHashData = SampleModt
        };

        var encoded = LighEncoder.EncodeNew(ligh);

        Assert.Equal(["EDID", "MODL", "MODT", "FULL", "DATA"], encoded.Subrecords.Select(s => s.Signature));
    }

    [Fact]
    public void FurnEncoder_EncodeNew_EmitsModt()
    {
        var furn = new FurnitureRecord
        {
            FormId = 0x200,
            EditorId = "F",
            ModelPath = "f.nif",
            TextureHashData = SampleModt,
            MarkerFlags = 0
        };

        var encoded = FurnEncoder.EncodeNew(furn);

        Assert.Contains(encoded.Subrecords, s => s.Signature == "MODT");
    }

    [Fact]
    public void ContEncoder_EncodeNew_EmitsModt()
    {
        var cont = new ContainerRecord
        {
            FormId = 0x600,
            EditorId = "C",
            ModelPath = "c.nif",
            TextureHashData = SampleModt
        };

        var encoded = ContEncoder.EncodeNew(cont);

        Assert.Contains(encoded.Subrecords, s => s.Signature == "MODT");
    }

    // ====================================================================================
    // Inventory item encoders — MODT + ICON + MICO
    // ====================================================================================

    [Fact]
    public void MiscEncoder_EncodeNew_EmitsModtIconMicoInCanonicalOrder()
    {
        var misc = new MiscItemRecord
        {
            FormId = 0x100,
            EditorId = "M",
            ModelPath = "m.nif",
            TextureHashData = SampleModt,
            IconPath = "icons/m.dds",
            MessageIconPath = "icons/m_mini.dds"
        };

        var encoded = MiscEncoder.EncodeNew(misc);

        Assert.Equal(
            ["EDID", "MODL", "MODT", "ICON", "MICO", "DATA"],
            encoded.Subrecords.Select(s => s.Signature));
    }

    [Fact]
    public void MiscEncoder_EncodeNew_IconWithoutMicoEmitsOnlyIcon()
    {
        var misc = new MiscItemRecord
        {
            FormId = 0x100,
            EditorId = "M",
            ModelPath = "m.nif",
            IconPath = "icons/m.dds"
        };

        var encoded = MiscEncoder.EncodeNew(misc);

        Assert.Contains(encoded.Subrecords, s => s.Signature == "ICON");
        Assert.DoesNotContain(encoded.Subrecords, s => s.Signature == "MICO");
    }

    [Fact]
    public void KeymEncoder_EncodeNew_EmitsModtIconMico()
    {
        var key = new KeyRecord
        {
            FormId = 0x100,
            EditorId = "K",
            ModelPath = "k.nif",
            TextureHashData = SampleModt,
            IconPath = "icons/k.dds",
            MessageIconPath = "icons/k_mini.dds"
        };

        var encoded = KeymEncoder.EncodeNew(key);

        Assert.Contains(encoded.Subrecords, s => s.Signature == "MODT");
        Assert.Contains(encoded.Subrecords, s => s.Signature == "ICON");
        Assert.Contains(encoded.Subrecords, s => s.Signature == "MICO");
    }

    [Fact]
    public void ArmoEncoder_EncodeNew_EmitsBmdtBeforeModlModtIconMico()
    {
        // fopdoc canonical for FNV ARMO is EDID, OBND?, FULL?, BMDT, MODL?, MODT?, ICON?, MICO?
        // — BMDT before MODL. The earlier "MODL-first" ordering tripped the runtime's
        // post-model BMDT_ID size table (max=4), truncating GeneralFlags at load.
        var armo = new ArmorRecord
        {
            FormId = 0x100,
            EditorId = "A",
            ModelPath = "a.nif",
            TextureHashData = SampleModt,
            IconPath = "icons/a.dds",
            MessageIconPath = "icons/a_mini.dds"
        };

        var encoded = ArmoEncoder.EncodeNew(armo);

        var sigs = encoded.Subrecords.Select(s => s.Signature).ToList();
        var modlIdx = sigs.IndexOf("MODL");
        var modtIdx = sigs.IndexOf("MODT");
        var iconIdx = sigs.IndexOf("ICON");
        var micoIdx = sigs.IndexOf("MICO");
        var bmdtIdx = sigs.IndexOf("BMDT");

        Assert.True(bmdtIdx < modlIdx);
        Assert.True(modlIdx < modtIdx);
        Assert.True(modtIdx < iconIdx);
        Assert.True(iconIdx < micoIdx);
    }

    [Fact]
    public void AmmoEncoder_EncodeNew_EmitsModtIconMico()
    {
        var ammo = new AmmoRecord
        {
            FormId = 0x100,
            EditorId = "A",
            ModelPath = "a.nif",
            TextureHashData = SampleModt,
            IconPath = "icons/a.dds",
            MessageIconPath = "icons/a_mini.dds"
        };

        var encoded = AmmoEncoder.EncodeNew(ammo);

        Assert.Contains(encoded.Subrecords, s => s.Signature == "MODT");
        Assert.Contains(encoded.Subrecords, s => s.Signature == "ICON");
        Assert.Contains(encoded.Subrecords, s => s.Signature == "MICO");
    }

    [Fact]
    public void AlchEncoder_EncodeNew_EmitsModtIconMico()
    {
        var alch = new ConsumableRecord
        {
            FormId = 0x100,
            EditorId = "A",
            ModelPath = "a.nif",
            TextureHashData = SampleModt,
            IconPath = "icons/a.dds",
            MessageIconPath = "icons/a_mini.dds"
        };

        var encoded = AlchEncoder.EncodeNew(alch);

        Assert.Contains(encoded.Subrecords, s => s.Signature == "MODT");
        Assert.Contains(encoded.Subrecords, s => s.Signature == "ICON");
        Assert.Contains(encoded.Subrecords, s => s.Signature == "MICO");
    }

    [Fact]
    public void BookEncoder_EncodeNew_EmitsModtIconMicoBeforeDesc()
    {
        var book = new BookRecord
        {
            FormId = 0x100,
            EditorId = "B",
            ModelPath = "b.nif",
            TextureHashData = SampleModt,
            IconPath = "icons/b.dds",
            MessageIconPath = "icons/b_mini.dds",
            Text = "Story..."
        };

        var encoded = BookEncoder.EncodeNew(book);

        var sigs = encoded.Subrecords.Select(s => s.Signature).ToList();
        var modlIdx = sigs.IndexOf("MODL");
        var modtIdx = sigs.IndexOf("MODT");
        var iconIdx = sigs.IndexOf("ICON");
        var micoIdx = sigs.IndexOf("MICO");
        var descIdx = sigs.IndexOf("DESC");

        Assert.True(modlIdx < modtIdx);
        Assert.True(modtIdx < iconIdx);
        Assert.True(iconIdx < micoIdx);
        Assert.True(micoIdx < descIdx);
    }

    // ====================================================================================
    // MODT byte passthrough
    // ====================================================================================

    [Fact]
    public void MiscEncoder_EncodeNew_ModtBytesAreVerbatim()
    {
        var bytes = new byte[64];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(i ^ 0xA5);
        }

        var misc = new MiscItemRecord
        {
            FormId = 0x100,
            EditorId = "M",
            ModelPath = "m.nif",
            TextureHashData = bytes
        };

        var encoded = MiscEncoder.EncodeNew(misc);
        var modt = Assert.Single(encoded.Subrecords, s => s.Signature == "MODT");

        Assert.Equal(bytes.Length, modt.Bytes.Length);
        Assert.Equal(bytes, modt.Bytes);
    }
}
