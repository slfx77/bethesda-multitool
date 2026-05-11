using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Plugin;

/// <summary>
///     v9 tests covering the encoder-side parity-completion work:
///     - CONT trailing subrecords (SNAM/QNAM/RNAM)
///     - LIGH optional subrecords (ICON/FNAM/SNAM/SCRI)
///     - CNTO COED ownership extras
///     - TERM embedded result-script bytecode
/// </summary>
public class V9SubrecordEncoderTests
{
    // ====================================================================================
    // CONT trailing subrecords (SNAM/QNAM/RNAM)
    // ====================================================================================

    [Fact]
    public void ContEncoder_EncodeNew_EmitsSnamQnamRnamAfterData()
    {
        var cont = new ContainerRecord
        {
            FormId = 0x100,
            EditorId = "Crate",
            OpenSoundFormId = 0x111,
            OpenSoundLoopFormId = 0x222,
            CloseSoundFormId = 0x333
        };

        var encoded = ContEncoder.EncodeNew(cont);

        var sigs = encoded.Subrecords.Select(s => s.Signature).ToList();
        var dataIdx = sigs.IndexOf("DATA");
        var snamIdx = sigs.IndexOf("SNAM");
        var qnamIdx = sigs.IndexOf("QNAM");
        var rnamIdx = sigs.IndexOf("RNAM");

        Assert.True(dataIdx < snamIdx);
        Assert.True(snamIdx < qnamIdx);
        Assert.True(qnamIdx < rnamIdx);

        var snam = Assert.Single(encoded.Subrecords, s => s.Signature == "SNAM");
        Assert.Equal(0x111u, BinaryPrimitives.ReadUInt32LittleEndian(snam.Bytes));
        var qnam = Assert.Single(encoded.Subrecords, s => s.Signature == "QNAM");
        Assert.Equal(0x222u, BinaryPrimitives.ReadUInt32LittleEndian(qnam.Bytes));
        var rnam = Assert.Single(encoded.Subrecords, s => s.Signature == "RNAM");
        Assert.Equal(0x333u, BinaryPrimitives.ReadUInt32LittleEndian(rnam.Bytes));
    }

    [Fact]
    public void ContEncoder_EncodeNew_OmitsTrailingSubrecordsWhenNull()
    {
        var cont = new ContainerRecord { FormId = 0x100, EditorId = "Crate" };

        var encoded = ContEncoder.EncodeNew(cont);

        Assert.DoesNotContain(encoded.Subrecords, s => s.Signature == "SNAM");
        Assert.DoesNotContain(encoded.Subrecords, s => s.Signature == "QNAM");
        Assert.DoesNotContain(encoded.Subrecords, s => s.Signature == "RNAM");
    }

    // ====================================================================================
    // LIGH optional subrecords (ICON/FNAM/SNAM/SCRI)
    // ====================================================================================

    [Fact]
    public void LighEncoder_EncodeNew_EmitsAllOptionalSubrecordsInCanonicalOrder()
    {
        var ligh = new LightRecord
        {
            FormId = 0x500,
            EditorId = "Lamp",
            ModelPath = "lamp.nif",
            FullName = "Lamp",
            IconPath = "icons/lamp.dds",
            Script = 0x111,
            Fade = 0.75f,
            SoundFormId = 0x222
        };

        var encoded = LighEncoder.EncodeNew(ligh);

        Assert.Equal(
            ["EDID", "MODL", "SCRI", "FULL", "ICON", "DATA", "FNAM", "SNAM"],
            encoded.Subrecords.Select(s => s.Signature));
    }

    [Fact]
    public void LighEncoder_EncodeNew_FnamIs4ByteFloat()
    {
        var ligh = new LightRecord { FormId = 0x500, EditorId = "L", Fade = 0.5f };

        var encoded = LighEncoder.EncodeNew(ligh);

        var fnam = Assert.Single(encoded.Subrecords, s => s.Signature == "FNAM");
        Assert.Equal(4, fnam.Bytes.Length);
        Assert.Equal(0.5f, BinaryPrimitives.ReadSingleLittleEndian(fnam.Bytes));
    }

    [Fact]
    public void LighEncoder_EncodeNew_OmitsOptionalsWhenNull()
    {
        var ligh = new LightRecord { FormId = 0x500, EditorId = "L" };

        var encoded = LighEncoder.EncodeNew(ligh);

        Assert.DoesNotContain(encoded.Subrecords, s => s.Signature == "ICON");
        Assert.DoesNotContain(encoded.Subrecords, s => s.Signature == "FNAM");
        Assert.DoesNotContain(encoded.Subrecords, s => s.Signature == "SNAM");
        Assert.DoesNotContain(encoded.Subrecords, s => s.Signature == "SCRI");
    }

    // ====================================================================================
    // CNTO COED ownership extras
    // ====================================================================================

    [Fact]
    public void ContEncoder_EncodeNew_OwnedCntoEmitsTrailingCoed()
    {
        var cont = new ContainerRecord
        {
            FormId = 0x100,
            EditorId = "Crate",
            Contents =
            [
                new InventoryItem(0x1111, 3)
                {
                    OwnerFormId = 0xAAAA,
                    GlobalOrRank = 5,
                    ItemCondition = 0.75f
                }
            ]
        };

        var encoded = ContEncoder.EncodeNew(cont);

        var sigs = encoded.Subrecords.Select(s => s.Signature).ToList();
        var cntoIdx = sigs.IndexOf("CNTO");
        var coedIdx = sigs.IndexOf("COED");

        // COED immediately follows CNTO.
        Assert.Equal(cntoIdx + 1, coedIdx);

        var coed = Assert.Single(encoded.Subrecords, s => s.Signature == "COED");
        Assert.Equal(12, coed.Bytes.Length);
        Assert.Equal(0xAAAAu, BinaryPrimitives.ReadUInt32LittleEndian(coed.Bytes.AsSpan(0, 4)));
        Assert.Equal(5u, BinaryPrimitives.ReadUInt32LittleEndian(coed.Bytes.AsSpan(4, 4)));
        Assert.Equal(0.75f, BinaryPrimitives.ReadSingleLittleEndian(coed.Bytes.AsSpan(8, 4)));
    }

    [Fact]
    public void ContEncoder_EncodeNew_UnownedCntoSkipsCoed()
    {
        var cont = new ContainerRecord
        {
            FormId = 0x100,
            EditorId = "Crate",
            Contents = [new InventoryItem(0x1111, 1)]
        };

        var encoded = ContEncoder.EncodeNew(cont);

        Assert.Contains(encoded.Subrecords, s => s.Signature == "CNTO");
        Assert.DoesNotContain(encoded.Subrecords, s => s.Signature == "COED");
    }

    [Fact]
    public void ContEncoder_EncodeNew_MixedOwnedAndUnownedItemsInterleaveCoedCorrectly()
    {
        var cont = new ContainerRecord
        {
            FormId = 0x100,
            EditorId = "Crate",
            Contents =
            [
                new InventoryItem(0x1, 1), // unowned
                new InventoryItem(0x2, 1) { OwnerFormId = 0xAAAA }, // owned
                new InventoryItem(0x3, 1) // unowned
            ]
        };

        var encoded = ContEncoder.EncodeNew(cont);

        // Expect: CNTO(item1), CNTO(item2), COED, CNTO(item3) — only the middle item carries COED.
        var sigs = encoded.Subrecords
            .Where(s => s.Signature is "CNTO" or "COED")
            .Select(s => s.Signature)
            .ToList();
        Assert.Equal(["CNTO", "CNTO", "COED", "CNTO"], sigs);
    }

    // ====================================================================================
    // TERM embedded result-script bytecode
    // ====================================================================================

    [Fact]
    public void TermEncoder_EncodeNew_EmbeddedScriptEmitsSchrScdaSctx()
    {
        var term = new TerminalRecord
        {
            FormId = 0x700,
            EditorId = "Terminal",
            MenuItems =
            [
                new TerminalMenuItem
                {
                    Text = "Detonate",
                    CompiledData = [0x10, 0x20, 0x30, 0x40],
                    SourceText = "Begin OnActivate\n  doStuff\nEnd"
                }
            ]
        };

        var encoded = TermEncoder.EncodeNew(term);

        var schr = Assert.Single(encoded.Subrecords, s => s.Signature == "SCHR");
        Assert.Equal(20, schr.Bytes.Length);
        // CompiledSize at offset 8 should equal SCDA length.
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32LittleEndian(schr.Bytes.AsSpan(8, 4)));
        // IsCompiled at offset 18 should be 1 since we have bytecode.
        Assert.Equal(1, schr.Bytes[18]);

        var scda = Assert.Single(encoded.Subrecords, s => s.Signature == "SCDA");
        Assert.Equal(new byte[] { 0x10, 0x20, 0x30, 0x40 }, scda.Bytes);

        Assert.Contains(encoded.Subrecords, s => s.Signature == "SCTX");
        // RNAM should NOT be emitted when embedded script is present.
        Assert.DoesNotContain(encoded.Subrecords, s => s.Signature == "RNAM");
    }

    [Fact]
    public void TermEncoder_EncodeNew_EmbeddedScriptScroAndScrvBranch()
    {
        var term = new TerminalRecord
        {
            FormId = 0x700,
            EditorId = "T",
            MenuItems =
            [
                new TerminalMenuItem
                {
                    Text = "X",
                    CompiledData = [0x00],
                    ReferencedObjects = [0xABCu, 0x80000001u] // SCRO + SCRV (high bit set)
                }
            ]
        };

        var encoded = TermEncoder.EncodeNew(term);

        var scro = Assert.Single(encoded.Subrecords, s => s.Signature == "SCRO");
        Assert.Equal(0xABCu, BinaryPrimitives.ReadUInt32LittleEndian(scro.Bytes));
        var scrv = Assert.Single(encoded.Subrecords, s => s.Signature == "SCRV");
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(scrv.Bytes));
    }

    [Fact]
    public void TermEncoder_EncodeNew_NextSeparatorBetweenMenuItems()
    {
        var term = new TerminalRecord
        {
            FormId = 0x700,
            EditorId = "T",
            MenuItems =
            [
                new TerminalMenuItem { Text = "A", ResultScript = 0x1 },
                new TerminalMenuItem { Text = "B", ResultScript = 0x2 },
                new TerminalMenuItem { Text = "C", ResultScript = 0x3 }
            ]
        };

        var encoded = TermEncoder.EncodeNew(term);

        // Two NEXT separators expected — one after items A and B, none after the last (C).
        var nextCount = encoded.Subrecords.Count(s => s.Signature == "NEXT");
        Assert.Equal(2, nextCount);

        var sigs = encoded.Subrecords
            .Where(s => s.Signature is "ITXT" or "RNAM" or "NEXT")
            .Select(s => s.Signature)
            .ToList();
        Assert.Equal(["ITXT", "RNAM", "NEXT", "ITXT", "RNAM", "NEXT", "ITXT", "RNAM"], sigs);
    }

    [Fact]
    public void TermEncoder_EncodeNew_EmbeddedScriptTakesPrecedenceOverRnam()
    {
        var term = new TerminalRecord
        {
            FormId = 0x700,
            EditorId = "T",
            MenuItems =
            [
                new TerminalMenuItem
                {
                    Text = "X",
                    ResultScript = 0xABC,
                    CompiledData = [0x99]
                }
            ]
        };

        var encoded = TermEncoder.EncodeNew(term);

        Assert.Contains(encoded.Subrecords, s => s.Signature == "SCHR");
        Assert.DoesNotContain(encoded.Subrecords, s => s.Signature == "RNAM");
    }
}
