using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Character;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Plugin;

/// <summary>
///     v16 tests covering the trivial + small encoder batch:
///     EYES, HAIR, REPU, AVIF, MUSC, MESG, NOTE, FLST, and LVLI/LVLN/LVLC (one encoder).
/// </summary>
public class V16EncoderTests
{
    // ====================================================================================
    // EyesEncoder
    // ====================================================================================

    [Fact]
    public void EyesEncoder_EncodeNew_CanonicalOrder()
    {
        var eyes = new EyesRecord
        {
            FormId = 0x1100,
            EditorId = "BlueEyes",
            FullName = "Blue",
            TexturePath = "characters/eyes/blue.dds",
            Flags = 0x01
        };

        var encoded = EyesEncoder.EncodeNew(eyes);
        var sigs = encoded.Subrecords.Select(s => s.Signature).ToList();

        Assert.Equal(["EDID", "FULL", "ICON", "DATA"], sigs);
        var data = Assert.Single(encoded.Subrecords, s => s.Signature == "DATA");
        Assert.Single(data.Bytes);
        Assert.Equal(0x01, data.Bytes[0]);
    }

    [Fact]
    public void EyesEncoder_EncodeNew_OmitsOptionalsWhenNull()
    {
        var eyes = new EyesRecord { FormId = 0x1100, EditorId = "E", Flags = 0 };
        var encoded = EyesEncoder.EncodeNew(eyes);
        var sigs = encoded.Subrecords.Select(s => s.Signature).ToList();
        Assert.Equal(["EDID", "DATA"], sigs);
    }

    // ====================================================================================
    // HairEncoder
    // ====================================================================================

    [Fact]
    public void HairEncoder_EncodeNew_CanonicalOrder()
    {
        var hair = new HairRecord
        {
            FormId = 0x1200,
            EditorId = "BrownHair",
            FullName = "Brown",
            ModelPath = "characters/hair/brown.nif",
            TexturePath = "characters/hair/brown.dds",
            Flags = 0x01
        };

        var encoded = HairEncoder.EncodeNew(hair);
        var sigs = encoded.Subrecords.Select(s => s.Signature).ToList();

        Assert.Equal(["EDID", "FULL", "MODL", "ICON", "DATA"], sigs);
    }

    // ====================================================================================
    // RepuEncoder
    // ====================================================================================

    [Fact]
    public void RepuEncoder_EncodeNew_DataLayout()
    {
        var repu = new ReputationRecord
        {
            FormId = 0x1300,
            EditorId = "NCRRep",
            FullName = "NCR Reputation",
            PositiveValue = 100.0f,
            NegativeValue = -50.0f
        };

        var encoded = RepuEncoder.EncodeNew(repu);
        var data = Assert.Single(encoded.Subrecords, s => s.Signature == "DATA").Bytes;

        Assert.Equal(8, data.Length);
        Assert.Equal(100.0f, BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(0, 4)));
        Assert.Equal(-50.0f, BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(4, 4)));
    }

    [Fact]
    public void RepuEncoder_EncodeNew_OmitsFullWhenNull()
    {
        var repu = new ReputationRecord { FormId = 0x1300, EditorId = "R" };
        var encoded = RepuEncoder.EncodeNew(repu);
        var sigs = encoded.Subrecords.Select(s => s.Signature).ToList();
        Assert.Equal(["EDID", "DATA"], sigs);
    }

    // ====================================================================================
    // AvifEncoder
    // ====================================================================================

    [Fact]
    public void AvifEncoder_EncodeNew_CanonicalOrder()
    {
        var avif = new ActorValueInfoRecord
        {
            FormId = 0x1400,
            EditorId = "Strength",
            FullName = "Strength",
            Description = "Raw physical power.",
            Icon = "icons/special/strength.dds",
            Abbreviation = "STR"
        };

        var encoded = AvifEncoder.EncodeNew(avif);
        var sigs = encoded.Subrecords.Select(s => s.Signature).ToList();

        Assert.Equal(["EDID", "FULL", "DESC", "ICON", "ANAM"], sigs);
    }

    // ====================================================================================
    // MuscEncoder
    // ====================================================================================

    [Fact]
    public void MuscEncoder_EncodeNew_FnamAndAnam()
    {
        var musc = new MusicTypeRecord
        {
            FormId = 0x1500,
            EditorId = "MusCombat",
            FileName = "music/special/dnbattle.mp3",
            Attenuation = -12.0f
        };

        var encoded = MuscEncoder.EncodeNew(musc);
        var sigs = encoded.Subrecords.Select(s => s.Signature).ToList();

        Assert.Equal(["EDID", "FNAM", "ANAM"], sigs);
        var anam = Assert.Single(encoded.Subrecords, s => s.Signature == "ANAM").Bytes;
        Assert.Equal(-12.0f, BinaryPrimitives.ReadSingleLittleEndian(anam));
    }

    // ====================================================================================
    // MesgEncoder
    // ====================================================================================

    [Fact]
    public void MesgEncoder_EncodeNew_AllFieldsCanonicalOrder()
    {
        var mesg = new MessageRecord
        {
            FormId = 0x1600,
            EditorId = "MsgPickLock",
            FullName = "Pick Lock?",
            Description = "Attempt to pick this lock?",
            Icon = "icons/lockpick.dds",
            QuestFormId = 0xABCD,
            Flags = 0x03, // MessageBox + AutoDisplay
            DisplayTime = 5,
            Buttons = ["Yes", "No"]
        };

        var encoded = MesgEncoder.EncodeNew(mesg);
        var sigs = encoded.Subrecords.Select(s => s.Signature).ToList();

        Assert.Equal(["EDID", "FULL", "DESC", "ICON", "QNAM", "DNAM", "TNAM", "ITXT", "ITXT"], sigs);
    }

    [Fact]
    public void MesgEncoder_EncodeNew_OmitsQnamWhenZero()
    {
        var mesg = new MessageRecord { FormId = 0x1600, EditorId = "M", QuestFormId = 0 };
        var encoded = MesgEncoder.EncodeNew(mesg);
        Assert.DoesNotContain(encoded.Subrecords, s => s.Signature == "QNAM");
    }

    // ====================================================================================
    // NoteEncoder
    // ====================================================================================

    [Fact]
    public void NoteEncoder_EncodeNew_TextNoteEmitsTnamAsString()
    {
        var note = new NoteRecord
        {
            FormId = 0x1700,
            EditorId = "NoteEx",
            FullName = "Sample Note",
            NoteType = 1, // Text
            Text = "Hello, Wasteland."
        };

        var encoded = NoteEncoder.EncodeNew(note);
        var tnam = Assert.Single(encoded.Subrecords, s => s.Signature == "TNAM").Bytes;
        // Null-terminated Latin-1 string body.
        Assert.Equal(note.Text.Length + 1, tnam.Length);
        Assert.Equal(0, tnam[^1]);
    }

    [Fact]
    public void NoteEncoder_EncodeNew_VoiceNoteEmitsTnamAsFormId()
    {
        var note = new NoteRecord
        {
            FormId = 0x1700,
            EditorId = "V",
            NoteType = 3, // Voice
            TopicFormId = 0xDEADBEEF
        };

        var encoded = NoteEncoder.EncodeNew(note);
        var tnam = Assert.Single(encoded.Subrecords, s => s.Signature == "TNAM").Bytes;
        Assert.Equal(4, tnam.Length);
        Assert.Equal(0xDEADBEEFu, BinaryPrimitives.ReadUInt32LittleEndian(tnam));
    }

    [Fact]
    public void NoteEncoder_EncodeNew_CanonicalOrder()
    {
        var note = new NoteRecord
        {
            FormId = 0x1700,
            EditorId = "F",
            FullName = "Full Note",
            ModelPath = "note.nif",
            IconPath = "icons/note.dds",
            TexturePath = "icons/note_mico.dds",
            NoteType = 1,
            Text = "Body",
            SoundFormId = 0x300,
            ObjectFormId = 0x400
        };

        var encoded = NoteEncoder.EncodeNew(note);
        var sigs = encoded.Subrecords.Select(s => s.Signature).ToList();

        Assert.Equal(["EDID", "FULL", "MODL", "ICON", "MICO", "DATA", "TNAM", "SNAM", "ONAM"], sigs);
    }

    // ====================================================================================
    // FlstEncoder
    // ====================================================================================

    [Fact]
    public void FlstEncoder_EncodeNew_EmitsEachFormIdAsLnam()
    {
        var flst = new FormListRecord
        {
            FormId = 0x1800,
            EditorId = "MyList",
            FormIds = [0x101u, 0x202u, 0x303u]
        };

        var encoded = FlstEncoder.EncodeNew(flst);
        var sigs = encoded.Subrecords.Select(s => s.Signature).ToList();

        Assert.Equal(["EDID", "LNAM", "LNAM", "LNAM"], sigs);

        var lnams = encoded.Subrecords.Where(s => s.Signature == "LNAM").ToList();
        Assert.Equal(0x101u, BinaryPrimitives.ReadUInt32LittleEndian(lnams[0].Bytes));
        Assert.Equal(0x202u, BinaryPrimitives.ReadUInt32LittleEndian(lnams[1].Bytes));
        Assert.Equal(0x303u, BinaryPrimitives.ReadUInt32LittleEndian(lnams[2].Bytes));
    }

    [Fact]
    public void FlstEncoder_EncodeNew_EmptyListEmitsOnlyEdid()
    {
        var flst = new FormListRecord { FormId = 0x1800, EditorId = "E" };
        var encoded = FlstEncoder.EncodeNew(flst);
        var sigs = encoded.Subrecords.Select(s => s.Signature).ToList();
        Assert.Equal(["EDID"], sigs);
    }

    // ====================================================================================
    // LvliEncoder (handles LVLI/LVLN/LVLC)
    // ====================================================================================

    [Fact]
    public void LvliEncoder_EncodeNew_LvloLayout()
    {
        var lvli = new LeveledListRecord
        {
            FormId = 0x1900,
            EditorId = "LL1",
            ListType = "LVLI",
            ChanceNone = 25,
            Flags = 0x01,
            GlobalFormId = 0xAA,
            Entries =
            [
                new LeveledEntry(Level: 10, FormId: 0x111, Count: 1),
                new LeveledEntry(Level: 20, FormId: 0x222, Count: 3)
            ]
        };

        var encoded = LvliEncoder.EncodeNew(lvli);
        var sigs = encoded.Subrecords.Select(s => s.Signature).ToList();

        Assert.Equal(["EDID", "LVLD", "LVLF", "LVLG", "LVLO", "LVLO"], sigs);

        var lvlos = encoded.Subrecords.Where(s => s.Signature == "LVLO").ToList();
        Assert.Equal(12, lvlos[0].Bytes.Length);
        Assert.Equal(10, BinaryPrimitives.ReadUInt16LittleEndian(lvlos[0].Bytes.AsSpan(0, 2)));
        Assert.Equal(0x111u, BinaryPrimitives.ReadUInt32LittleEndian(lvlos[0].Bytes.AsSpan(4, 4)));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(lvlos[0].Bytes.AsSpan(8, 2)));
    }

    [Fact]
    public void LvliEncoder_EncodeNew_OmitsOptionalsWhenDefault()
    {
        var lvli = new LeveledListRecord
        {
            FormId = 0x1900,
            EditorId = "E",
            ChanceNone = 0,
            Flags = 0,
            GlobalFormId = null,
            Entries = []
        };

        var encoded = LvliEncoder.EncodeNew(lvli);
        var sigs = encoded.Subrecords.Select(s => s.Signature).ToList();
        Assert.Equal(["EDID"], sigs);
    }

    [Fact]
    public void LvliEncoder_EncodeNew_HandlesAllThreeListTypes()
    {
        foreach (var listType in new[] { "LVLI", "LVLN", "LVLC" })
        {
            var lvl = new LeveledListRecord
            {
                FormId = 0x1900,
                EditorId = "T",
                ListType = listType,
                Entries = [new LeveledEntry(Level: 1, FormId: 0x1, Count: 1)]
            };

            var encoded = LvliEncoder.EncodeNew(lvl);
            Assert.Contains(encoded.Subrecords, s => s.Signature == "EDID");
            Assert.Contains(encoded.Subrecords, s => s.Signature == "LVLO");
        }
    }

    // ====================================================================================
    // Cross-encoder warning check
    // ====================================================================================

    [Fact]
    public void AllEncoders_EmitWarningWhenEditorIdMissing()
    {
        Assert.Contains(EyesEncoder.EncodeNew(new EyesRecord { FormId = 1 }).Warnings,
            w => w.Contains("has no EditorId"));
        Assert.Contains(HairEncoder.EncodeNew(new HairRecord { FormId = 1 }).Warnings,
            w => w.Contains("has no EditorId"));
        Assert.Contains(RepuEncoder.EncodeNew(new ReputationRecord { FormId = 1 }).Warnings,
            w => w.Contains("has no EditorId"));
        Assert.Contains(AvifEncoder.EncodeNew(new ActorValueInfoRecord { FormId = 1 }).Warnings,
            w => w.Contains("has no EditorId"));
        Assert.Contains(MuscEncoder.EncodeNew(new MusicTypeRecord { FormId = 1 }).Warnings,
            w => w.Contains("has no EditorId"));
        Assert.Contains(MesgEncoder.EncodeNew(new MessageRecord { FormId = 1 }).Warnings,
            w => w.Contains("has no EditorId"));
        Assert.Contains(NoteEncoder.EncodeNew(new NoteRecord { FormId = 1 }).Warnings,
            w => w.Contains("has no EditorId"));
        Assert.Contains(FlstEncoder.EncodeNew(new FormListRecord { FormId = 1 }).Warnings,
            w => w.Contains("has no EditorId"));
        Assert.Contains(LvliEncoder.EncodeNew(new LeveledListRecord { FormId = 1 }).Warnings,
            w => w.Contains("has no EditorId"));
    }
}
