using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Enums;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Plugin;

/// <summary>
///     v4 tests for the simple-type new-record encoders (GMST/GLOB/MISC/KEYM/ALCH/BOOK/AMMO).
///     Verifies EDID + DATA always emitted, optional fields emitted when present, fopdoc
///     canonical order.
/// </summary>
public class NewTopLevelEncoderTests
{
    [Fact]
    public void GmstEncoder_EncodeNew_FloatGmst_EmitsEdidAndDataAsFloat()
    {
        var gmst = new GameSettingRecord
        {
            FormId = 0x800,
            EditorId = "fNewSetting",
            ValueType = GameSettingType.Float,
            FloatValue = 3.14f
        };

        var encoded = GmstEncoder.EncodeNew(gmst);

        var sigs = encoded.Subrecords.Select(s => s.Signature).ToList();
        Assert.Equal(["EDID", "DATA"], sigs);

        var data = encoded.Subrecords[1];
        Assert.Equal(3.14f, BinaryPrimitives.ReadSingleLittleEndian(data.Bytes));
    }

    [Fact]
    public void GmstEncoder_EncodeNew_StringGmst_EmitsEdidOnlyWithWarning()
    {
        var gmst = new GameSettingRecord
        {
            FormId = 0x800,
            EditorId = "sNewSetting",
            ValueType = GameSettingType.String,
            StringValue = "hello"
        };

        var encoded = GmstEncoder.EncodeNew(gmst);

        Assert.Equal(["EDID"], encoded.Subrecords.Select(s => s.Signature));
        Assert.Contains(encoded.Warnings, w => w.Contains("string"));
    }

    [Fact]
    public void GlobEncoder_EncodeNew_EmitsEdidFnamFltvInOrder()
    {
        var glob = new GlobalRecord
        {
            FormId = 0x800,
            EditorId = "NewGlobal",
            ValueType = 'f',
            Value = 42.5f
        };

        var encoded = GlobEncoder.EncodeNew(glob);

        var sigs = encoded.Subrecords.Select(s => s.Signature).ToList();
        Assert.Equal(["EDID", "FNAM", "FLTV"], sigs);
        Assert.Equal((byte)'f', encoded.Subrecords[1].Bytes[0]);
        Assert.Equal(42.5f, BinaryPrimitives.ReadSingleLittleEndian(encoded.Subrecords[2].Bytes));
    }

    [Fact]
    public void MiscEncoder_EncodeNew_EmitsCanonicalOrder()
    {
        var misc = new MiscItemRecord
        {
            FormId = 0x800,
            EditorId = "NewMisc",
            FullName = "New Item",
            ModelPath = "Items/Misc/NewItem.NIF",
            Bounds = new ObjectBounds { X1 = -5, Y1 = -5, Z1 = 0, X2 = 5, Y2 = 5, Z2 = 10 },
            Value = 25,
            Weight = 1.5f
        };

        var encoded = MiscEncoder.EncodeNew(misc);

        var sigs = encoded.Subrecords.Select(s => s.Signature).ToList();
        Assert.Equal(["EDID", "OBND", "FULL", "MODL", "DATA"], sigs);
    }

    [Fact]
    public void MiscEncoder_EncodeNew_NoModel_WarnsAboutMissingModel()
    {
        var misc = new MiscItemRecord { FormId = 0x800, EditorId = "NoModel", Value = 5, Weight = 1.0f };
        var encoded = MiscEncoder.EncodeNew(misc);
        Assert.Contains(encoded.Warnings, w => w.Contains("model"));
    }

    [Fact]
    public void KeymEncoder_EncodeNew_OmitsObndWhenAbsent()
    {
        var key = new KeyRecord { FormId = 0x800, EditorId = "NewKey", Value = 0, Weight = 0.1f };
        var encoded = KeymEncoder.EncodeNew(key);

        var sigs = encoded.Subrecords.Select(s => s.Signature).ToList();
        Assert.DoesNotContain("OBND", sigs);
        Assert.Contains("EDID", sigs);
        Assert.Contains("DATA", sigs);
    }

    [Fact]
    public void AlchEncoder_EncodeNew_EmitsDataWeight()
    {
        var alch = new ConsumableRecord { FormId = 0x800, EditorId = "Stim", Weight = 0.25f };
        var encoded = AlchEncoder.EncodeNew(alch);

        var data = Assert.Single(encoded.Subrecords, s => s.Signature == "DATA");
        Assert.Equal(0.25f, BinaryPrimitives.ReadSingleLittleEndian(data.Bytes));
    }

    [Fact]
    public void BookEncoder_EncodeNew_EmitsDescAndEnamWhenPresent()
    {
        var book = new BookRecord
        {
            FormId = 0x800,
            EditorId = "NewBook",
            FullName = "Book Title",
            Text = "Once upon a time...",
            Value = 5,
            Weight = 0.5f,
            EnchantmentFormId = 0x1234
        };

        var encoded = BookEncoder.EncodeNew(book);

        var sigs = encoded.Subrecords.Select(s => s.Signature).ToList();
        Assert.Contains("DESC", sigs);
        Assert.Contains("ENAM", sigs);
        // DESC must come before DATA per fopdoc.
        Assert.True(sigs.IndexOf("DESC") < sigs.IndexOf("DATA"));
        // ENAM comes after DATA per fopdoc.
        Assert.True(sigs.IndexOf("DATA") < sigs.IndexOf("ENAM"));
    }

    [Fact]
    public void AmmoEncoder_EncodeNew_DataIsThirteenBytes()
    {
        var ammo = new AmmoRecord
        {
            FormId = 0x800,
            EditorId = "NewAmmo",
            Speed = 5000f,
            Flags = 0x01,
            Value = 1u,
            ClipRounds = 6
        };

        var encoded = AmmoEncoder.EncodeNew(ammo);

        var data = Assert.Single(encoded.Subrecords, s => s.Signature == "DATA");
        Assert.Equal(13, data.Bytes.Length);
    }

    [Fact]
    public void AmmoEncoder_EncodeNew_ProjectileData_EmitsWarning()
    {
        var ammo = new AmmoRecord
        {
            FormId = 0x800,
            EditorId = "NewAmmo",
            ProjectileFormId = 0xABC
        };

        var encoded = AmmoEncoder.EncodeNew(ammo);
        Assert.Contains(encoded.Warnings, w => w.Contains("DAT2"));
    }

    [Fact]
    public void AllNewRecordEncoders_HandleMissingEditorIdWithWarning()
    {
        var gmst = GmstEncoder.EncodeNew(new GameSettingRecord { FormId = 1, EditorId = null });
        Assert.Contains(gmst.Warnings, w => w.Contains("EditorId"));

        var glob = GlobEncoder.EncodeNew(new GlobalRecord { FormId = 1, EditorId = null });
        Assert.Contains(glob.Warnings, w => w.Contains("EditorId"));

        var misc = MiscEncoder.EncodeNew(new MiscItemRecord { FormId = 1, EditorId = null });
        Assert.Contains(misc.Warnings, w => w.Contains("EditorId"));
    }
}
