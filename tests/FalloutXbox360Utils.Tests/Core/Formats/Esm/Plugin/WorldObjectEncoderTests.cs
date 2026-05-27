using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Item;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.World;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Plugin;

/// <summary>
///     Tests for the world-object encoders: ACTI, DOOR, LIGH, STAT, CONT, FURN, TERM.
///     Covers byte layouts against PDB schemas, fopdoc canonical ordering, CONT/LIGH
///     trailing-optional subrecords (SNAM/QNAM/RNAM, ICON/FNAM/SNAM/SCRI), CNTO COED
///     ownership extras, TERM embedded result-script bytecode, and TERM menu-item CTDA
///     conditions (with CIS1/CIS2 string parameters).
/// </summary>
public class WorldObjectEncoderTests
{
    // ====================================================================================
    // STAT — Static
    // ====================================================================================

    [Fact]
    public void StatEncoder_EncodeNew_MinimalEmitsEdidOnly()
    {
        var stat = new StaticRecord { FormId = 0x100, EditorId = "MyStat" };

        var encoded = StatEncoder.EncodeNew(stat);

        Assert.Single(encoded.Subrecords);
        Assert.Equal("EDID", encoded.Subrecords[0].Signature);
        // Missing model path surfaces as a warning.
        Assert.Single(encoded.Warnings);
    }

    [Fact]
    public void StatEncoder_EncodeNew_FullEmitsEdidObndModlInCanonicalOrder()
    {
        var stat = new StaticRecord
        {
            FormId = 0x100,
            EditorId = "MyStat",
            ModelPath = "meshes/foo.nif",
            Bounds = new ObjectBounds { X1 = -1, Y1 = -2, Z1 = -3, X2 = 1, Y2 = 2, Z2 = 3 }
        };

        var encoded = StatEncoder.EncodeNew(stat);

        Assert.Equal(["EDID", "OBND", "MODL"], encoded.Subrecords.Select(s => s.Signature));
        Assert.Empty(encoded.Warnings);
    }

    // ====================================================================================
    // FURN — Furniture
    // ====================================================================================

    [Fact]
    public void FurnEncoder_EncodeNew_AlwaysEmitsMnam()
    {
        var furn = new FurnitureRecord { FormId = 0x200, EditorId = "Bench", MarkerFlags = 0xDEADBEEF };

        var encoded = FurnEncoder.EncodeNew(furn);

        var mnam = Assert.Single(encoded.Subrecords, s => s.Signature == "MNAM");
        Assert.Equal(4, mnam.Bytes.Length);
        Assert.Equal(0xDEADBEEF, BinaryPrimitives.ReadUInt32LittleEndian(mnam.Bytes));
    }

    [Fact]
    public void FurnEncoder_EncodeNew_CanonicalOrder()
    {
        var furn = new FurnitureRecord
        {
            FormId = 0x200,
            EditorId = "F",
            Bounds = new ObjectBounds(),
            FullName = "Bench",
            ModelPath = "m.nif",
            Script = 0x123,
            MarkerFlags = 1
        };

        var encoded = FurnEncoder.EncodeNew(furn);

        Assert.Equal(
            ["EDID", "OBND", "FULL", "MODL", "SCRI", "MNAM"],
            encoded.Subrecords.Select(s => s.Signature));
    }

    // ====================================================================================
    // ACTI — Activator
    // ====================================================================================

    [Fact]
    public void ActiEncoder_EncodeNew_AllOptionalSubrecordsEmittedInCanonicalOrder()
    {
        var acti = new ActivatorRecord
        {
            FormId = 0x300,
            EditorId = "Switch",
            Bounds = new ObjectBounds(),
            FullName = "Power Switch",
            ModelPath = "switch.nif",
            Script = 0x100,
            ActivationSoundFormId = 0x200,
            RadioStationFormId = 0x300,
            WaterTypeFormId = 0x400
        };

        var encoded = ActiEncoder.EncodeNew(acti);

        Assert.Equal(
            ["EDID", "OBND", "FULL", "MODL", "SCRI", "SNAM", "RNAM", "WNAM"],
            encoded.Subrecords.Select(s => s.Signature));
    }

    [Fact]
    public void ActiEncoder_EncodeNew_OptionalsOmittedWhenNull()
    {
        var acti = new ActivatorRecord { FormId = 0x300, EditorId = "Switch" };

        var encoded = ActiEncoder.EncodeNew(acti);

        Assert.Single(encoded.Subrecords);
        Assert.Equal("EDID", encoded.Subrecords[0].Signature);
    }

    // ====================================================================================
    // DOOR — Door
    // ====================================================================================

    [Fact]
    public void DoorEncoder_EncodeNew_FnamIsSingleByte()
    {
        var door = new DoorRecord { FormId = 0x400, EditorId = "Door", Flags = 0x05 };

        var encoded = DoorEncoder.EncodeNew(door);

        var fnam = Assert.Single(encoded.Subrecords, s => s.Signature == "FNAM");
        var fnamByte = Assert.Single(fnam.Bytes);
        Assert.Equal(0x05, fnamByte);
    }

    [Fact]
    public void DoorEncoder_EncodeNew_CanonicalOrder()
    {
        var door = new DoorRecord
        {
            FormId = 0x400,
            EditorId = "Door",
            Bounds = new ObjectBounds(),
            FullName = "Wooden Door",
            ModelPath = "door.nif",
            Script = 0x100,
            OpenSoundFormId = 0x200,
            CloseSoundFormId = 0x300,
            LoopSoundFormId = 0x400,
            Flags = 0x01
        };

        var encoded = DoorEncoder.EncodeNew(door);

        Assert.Equal(
            ["EDID", "OBND", "FULL", "MODL", "SCRI", "SNAM", "ANAM", "BNAM", "FNAM"],
            encoded.Subrecords.Select(s => s.Signature));
    }

    // ====================================================================================
    // LIGH — Light
    // ====================================================================================

    [Fact]
    public void LighEncoder_EncodeNew_DataIs32BytesRoundTrip()
    {
        var ligh = new LightRecord
        {
            FormId = 0x500,
            EditorId = "Lamp",
            Duration = 600,
            Radius = 256,
            Color = 0xFFAABBCC,
            Flags = 0x0001,
            FalloffExponent = 1.5f,
            Fov = 90.0f,
            Value = 25,
            Weight = 1.2f
        };

        var encoded = LighEncoder.EncodeNew(ligh);

        var data = Assert.Single(encoded.Subrecords, s => s.Signature == "DATA");
        Assert.Equal(32, data.Bytes.Length);

        Assert.Equal(600, BinaryPrimitives.ReadInt32LittleEndian(data.Bytes.AsSpan(0, 4)));
        Assert.Equal(256u, BinaryPrimitives.ReadUInt32LittleEndian(data.Bytes.AsSpan(4, 4)));
        Assert.Equal(0xFFAABBCCu, BinaryPrimitives.ReadUInt32LittleEndian(data.Bytes.AsSpan(8, 4)));
        Assert.Equal(0x0001u, BinaryPrimitives.ReadUInt32LittleEndian(data.Bytes.AsSpan(12, 4)));
        Assert.Equal(1.5f, BinaryPrimitives.ReadSingleLittleEndian(data.Bytes.AsSpan(16, 4)));
        Assert.Equal(90.0f, BinaryPrimitives.ReadSingleLittleEndian(data.Bytes.AsSpan(20, 4)));
        Assert.Equal(25, BinaryPrimitives.ReadInt32LittleEndian(data.Bytes.AsSpan(24, 4)));
        Assert.Equal(1.2f, BinaryPrimitives.ReadSingleLittleEndian(data.Bytes.AsSpan(28, 4)));
    }

    [Fact]
    public void LighEncoder_EncodeNew_CanonicalOrderEdidObndModlFullData()
    {
        var ligh = new LightRecord
        {
            FormId = 0x500,
            EditorId = "Lamp",
            Bounds = new ObjectBounds(),
            ModelPath = "lamp.nif",
            FullName = "Lamp"
        };

        var encoded = LighEncoder.EncodeNew(ligh);

        Assert.Equal(
            ["EDID", "OBND", "MODL", "FULL", "DATA"],
            encoded.Subrecords.Select(s => s.Signature));
    }

    [Fact]
    public void LighEncoder_EncodeNew_MinimalStillEmitsData()
    {
        var ligh = new LightRecord { FormId = 0x500, EditorId = "Lamp" };

        var encoded = LighEncoder.EncodeNew(ligh);

        Assert.Equal(["EDID", "DATA"], encoded.Subrecords.Select(s => s.Signature));
    }

    // ====================================================================================
    // CONT — Container
    // ====================================================================================

    [Fact]
    public void ContEncoder_EncodeNew_DataIs5BytesFlagsAndWeight()
    {
        var cont = new ContainerRecord
        {
            FormId = 0x600,
            EditorId = "Crate",
            Flags = 0x02,
            Weight = 5.5f
        };

        var encoded = ContEncoder.EncodeNew(cont);

        var data = Assert.Single(encoded.Subrecords, s => s.Signature == "DATA");
        Assert.Equal(5, data.Bytes.Length);
        Assert.Equal(0x02, data.Bytes[0]);
        Assert.Equal(5.5f, BinaryPrimitives.ReadSingleLittleEndian(data.Bytes.AsSpan(1, 4)));
    }

    [Fact]
    public void ContEncoder_EncodeNew_EmitsOneCntoPerInventoryItem()
    {
        var cont = new ContainerRecord
        {
            FormId = 0x600,
            EditorId = "Crate",
            Contents =
            [
                new InventoryItem(0x1111, 3),
                new InventoryItem(0x2222, 7)
            ]
        };

        var encoded = ContEncoder.EncodeNew(cont);

        var cntos = encoded.Subrecords.Where(s => s.Signature == "CNTO").ToList();
        Assert.Equal(2, cntos.Count);

        Assert.Equal(8, cntos[0].Bytes.Length);
        Assert.Equal(0x1111u, BinaryPrimitives.ReadUInt32LittleEndian(cntos[0].Bytes.AsSpan(0, 4)));
        Assert.Equal(3, BinaryPrimitives.ReadInt32LittleEndian(cntos[0].Bytes.AsSpan(4, 4)));

        Assert.Equal(0x2222u, BinaryPrimitives.ReadUInt32LittleEndian(cntos[1].Bytes.AsSpan(0, 4)));
        Assert.Equal(7, BinaryPrimitives.ReadInt32LittleEndian(cntos[1].Bytes.AsSpan(4, 4)));
    }

    [Fact]
    public void ContEncoder_EncodeNew_CanonicalOrderCntoBeforeData()
    {
        var cont = new ContainerRecord
        {
            FormId = 0x600,
            EditorId = "Crate",
            FullName = "Wooden Crate",
            ModelPath = "crate.nif",
            Script = 0x100,
            Contents = [new InventoryItem(0x1, 1)],
            Flags = 0,
            Weight = 0
        };

        var encoded = ContEncoder.EncodeNew(cont);

        Assert.Equal(
            ["EDID", "FULL", "MODL", "SCRI", "CNTO", "DATA"],
            encoded.Subrecords.Select(s => s.Signature));
    }

    // ====================================================================================
    // TERM — Terminal
    // ====================================================================================

    [Fact]
    public void TermEncoder_EncodeNew_DnamIs4BytesPerPdb()
    {
        var term = new TerminalRecord
        {
            FormId = 0x700,
            EditorId = "Terminal",
            Difficulty = 3,
            Flags = 0x05
        };

        var encoded = TermEncoder.EncodeNew(term);

        var dnam = Assert.Single(encoded.Subrecords, s => s.Signature == "DNAM");
        Assert.Equal(4, dnam.Bytes.Length);
        Assert.Equal(3, dnam.Bytes[0]); // Difficulty
        Assert.Equal(0x05, dnam.Bytes[1]); // Flags
        Assert.Equal(0, dnam.Bytes[2]); // ServerType (unset in model)
        Assert.Equal(0, dnam.Bytes[3]); // Unused
    }

    [Fact]
    public void TermEncoder_EncodeNew_PreservesTypedServerAndMenuActionFields()
    {
        var term = new TerminalRecord
        {
            FormId = 0x700,
            EditorId = "Terminal",
            Difficulty = 2,
            Flags = 0x04,
            ServerType = 0x03,
            MenuItems =
            [
                new TerminalMenuItem
                {
                    Text = "Login",
                    ActionType = 0x07,
                    ResultScript = 0xABC
                }
            ]
        };

        var encoded = TermEncoder.EncodeNew(term);

        var dnam = Assert.Single(encoded.Subrecords, s => s.Signature == "DNAM");
        Assert.Equal([0x02, 0x04, 0x03, 0x00], dnam.Bytes);

        var anam = Assert.Single(encoded.Subrecords, s => s.Signature == "ANAM");
        Assert.Equal([0x07], anam.Bytes);

        var sigs = encoded.Subrecords
            .Where(s => s.Signature is "ITXT" or "ANAM" or "RNAM")
            .Select(s => s.Signature)
            .ToList();
        Assert.Equal(["ITXT", "ANAM", "RNAM"], sigs);
    }

    [Fact]
    public void TermEncoder_EncodeNew_MenuItemEmitsItxtAndRnamPair()
    {
        var term = new TerminalRecord
        {
            FormId = 0x700,
            EditorId = "Terminal",
            MenuItems =
            [
                new TerminalMenuItem { Text = "Login", ResultScript = 0xABC },
                new TerminalMenuItem { Text = "Logout", SubTerminal = 0xDEF }
            ]
        };

        var encoded = TermEncoder.EncodeNew(term);

        var itxts = encoded.Subrecords.Where(s => s.Signature == "ITXT").ToList();
        var rnams = encoded.Subrecords.Where(s => s.Signature == "RNAM").ToList();

        Assert.Equal(2, itxts.Count);
        Assert.Equal(2, rnams.Count);

        Assert.Equal(0xABCu, BinaryPrimitives.ReadUInt32LittleEndian(rnams[0].Bytes));
        Assert.Equal(0xDEFu, BinaryPrimitives.ReadUInt32LittleEndian(rnams[1].Bytes));
    }

    [Fact]
    public void TermEncoder_EncodeNew_MenuItemWithoutLinkWarns()
    {
        var term = new TerminalRecord
        {
            FormId = 0x700,
            EditorId = "Terminal",
            MenuItems =
            [
                new TerminalMenuItem { Text = "Detonate" } // no ResultScript or SubTerminal
            ]
        };

        var encoded = TermEncoder.EncodeNew(term);

        // ITXT still emitted, but no RNAM, and one warning surfaces.
        Assert.Contains(encoded.Subrecords, s => s.Signature == "ITXT");
        Assert.DoesNotContain(encoded.Subrecords, s => s.Signature == "RNAM");
        Assert.Single(encoded.Warnings);
        Assert.Contains("has neither embedded script bytecode nor an external link", encoded.Warnings[0]);
    }

    [Fact]
    public void TermEncoder_EncodeNew_CanonicalOrder()
    {
        var term = new TerminalRecord
        {
            FormId = 0x700,
            EditorId = "Terminal",
            FullName = "Mainframe",
            HeaderText = "Welcome",
            Difficulty = 2,
            Flags = 1
        };

        var encoded = TermEncoder.EncodeNew(term);

        Assert.Equal(
            ["EDID", "FULL", "DESC", "DNAM"],
            encoded.Subrecords.Select(s => s.Signature));
    }

    // ====================================================================================
    // CONT/LIGH/TERM parity-completion — trailing subrecords, optional fields,
    // CNTO COED ownership extras, and TERM embedded result-script bytecode
    // ====================================================================================

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

    // ====================================================================================
    // TERM menu-item CTDA conditions (+ CIS1/CIS2 string parameters)
    // Conditions emit between ITXT and the result-script block per fopdoc.
    // ====================================================================================

    [Fact]
    public void TermEncoder_EncodeNew_ConditionEmittedBetweenItxtAndRnam()
    {
        var term = new TerminalRecord
        {
            FormId = 0x700,
            EditorId = "T",
            MenuItems =
            [
                new TerminalMenuItem
                {
                    Text = "Login",
                    ResultScript = 0x111,
                    Conditions =
                    [
                        new DialogueCondition
                        {
                            Type = 0x20, // ==
                            ComparisonValue = 1.0f,
                            FunctionIndex = 76, // GetIsID
                            Parameter1 = 0x1234,
                            RunOn = 0
                        }
                    ]
                }
            ]
        };

        var encoded = TermEncoder.EncodeNew(term);

        var sigs = encoded.Subrecords
            .Where(s => s.Signature is "ITXT" or "CTDA" or "RNAM")
            .Select(s => s.Signature)
            .ToList();
        Assert.Equal(["ITXT", "CTDA", "RNAM"], sigs);
    }

    [Fact]
    public void TermEncoder_EncodeNew_MultipleConditionsEmittedInOrder()
    {
        var term = new TerminalRecord
        {
            FormId = 0x700,
            EditorId = "T",
            MenuItems =
            [
                new TerminalMenuItem
                {
                    Text = "Maybe",
                    ResultScript = 0x1,
                    Conditions =
                    [
                        new DialogueCondition { Type = 0x20, FunctionIndex = 1 },
                        new DialogueCondition { Type = 0x21, FunctionIndex = 2 }, // OR-bit set
                        new DialogueCondition { Type = 0x40, FunctionIndex = 3 }  // !=
                    ]
                }
            ]
        };

        var encoded = TermEncoder.EncodeNew(term);

        var ctdas = encoded.Subrecords.Where(s => s.Signature == "CTDA").ToList();
        Assert.Equal(3, ctdas.Count);
        foreach (var ctda in ctdas)
        {
            Assert.Equal(28, ctda.Bytes.Length);
        }

        // FunctionIndex (offset 8, uint16 LE) preserves order.
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(ctdas[0].Bytes.AsSpan(8, 2)));
        Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(ctdas[1].Bytes.AsSpan(8, 2)));
        Assert.Equal(3, BinaryPrimitives.ReadUInt16LittleEndian(ctdas[2].Bytes.AsSpan(8, 2)));
    }

    [Fact]
    public void TermEncoder_EncodeNew_CtdaLayoutMatchesPdb()
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
                    ResultScript = 0x1,
                    Conditions =
                    [
                        new DialogueCondition
                        {
                            Type = 0x35,
                            ComparisonValue = 3.5f,
                            FunctionIndex = 250,
                            Parameter1 = 0xABCDEFu,
                            Parameter2 = 0x12345678u,
                            RunOn = 4, // Linked Reference
                            Reference = 0xDEADBEEFu
                        }
                    ]
                }
            ]
        };

        var encoded = TermEncoder.EncodeNew(term);
        var ctda = Assert.Single(encoded.Subrecords, s => s.Signature == "CTDA");

        Assert.Equal(0x35, ctda.Bytes[0]);
        Assert.Equal(3.5f, BinaryPrimitives.ReadSingleLittleEndian(ctda.Bytes.AsSpan(4, 4)));
        Assert.Equal(250, BinaryPrimitives.ReadUInt16LittleEndian(ctda.Bytes.AsSpan(8, 2)));
        Assert.Equal(0xABCDEFu, BinaryPrimitives.ReadUInt32LittleEndian(ctda.Bytes.AsSpan(12, 4)));
        Assert.Equal(0x12345678u, BinaryPrimitives.ReadUInt32LittleEndian(ctda.Bytes.AsSpan(16, 4)));
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32LittleEndian(ctda.Bytes.AsSpan(20, 4)));
        Assert.Equal(0xDEADBEEFu, BinaryPrimitives.ReadUInt32LittleEndian(ctda.Bytes.AsSpan(24, 4)));
    }

    [Fact]
    public void TermEncoder_EncodeNew_Cis1FollowsCtdaWhenStringParam1Set()
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
                    ResultScript = 0x1,
                    Conditions =
                    [
                        new DialogueCondition
                        {
                            Type = 0x20,
                            FunctionIndex = 449, // GetVariable
                            Parameter1String = "MyScriptVar"
                        }
                    ]
                }
            ]
        };

        var encoded = TermEncoder.EncodeNew(term);

        var sigs = encoded.Subrecords
            .Where(s => s.Signature is "CTDA" or "CIS1" or "CIS2" or "RNAM")
            .Select(s => s.Signature)
            .ToList();
        Assert.Equal(["CTDA", "CIS1", "RNAM"], sigs);

        var cis1 = Assert.Single(encoded.Subrecords, s => s.Signature == "CIS1");
        // Null-terminated Latin-1 string.
        Assert.Equal("MyScriptVar".Length + 1, cis1.Bytes.Length);
        Assert.Equal(0, cis1.Bytes[^1]);
    }

    [Fact]
    public void TermEncoder_EncodeNew_Cis1AndCis2BothEmittedWhenBothSet()
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
                    ResultScript = 0x1,
                    Conditions =
                    [
                        new DialogueCondition
                        {
                            Type = 0x20,
                            FunctionIndex = 449,
                            Parameter1String = "P1",
                            Parameter2String = "P2"
                        }
                    ]
                }
            ]
        };

        var encoded = TermEncoder.EncodeNew(term);

        var sigs = encoded.Subrecords
            .Where(s => s.Signature is "CTDA" or "CIS1" or "CIS2")
            .Select(s => s.Signature)
            .ToList();
        Assert.Equal(["CTDA", "CIS1", "CIS2"], sigs);
    }

    [Fact]
    public void TermEncoder_EncodeNew_ConditionsPrecedeEmbeddedScriptBlock()
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
                    CompiledData = [0x10],
                    Conditions = [new DialogueCondition { Type = 0x20, FunctionIndex = 1 }]
                }
            ]
        };

        var encoded = TermEncoder.EncodeNew(term);

        var sigs = encoded.Subrecords
            .Where(s => s.Signature is "ITXT" or "CTDA" or "SCHR" or "SCDA")
            .Select(s => s.Signature)
            .ToList();
        Assert.Equal(["ITXT", "CTDA", "SCHR", "SCDA"], sigs);
    }

    [Fact]
    public void TermEncoder_EncodeNew_NoConditionsLeavesItxtRnamPairIntact()
    {
        // Regression: menu items without conditions should still emit just ITXT + RNAM.
        var term = new TerminalRecord
        {
            FormId = 0x700,
            EditorId = "T",
            MenuItems = [new TerminalMenuItem { Text = "X", ResultScript = 0x1 }]
        };

        var encoded = TermEncoder.EncodeNew(term);

        Assert.DoesNotContain(encoded.Subrecords, s => s.Signature == "CTDA");
        Assert.DoesNotContain(encoded.Subrecords, s => s.Signature == "CIS1");
        Assert.DoesNotContain(encoded.Subrecords, s => s.Signature == "CIS2");
    }

    // ====================================================================================
    // TERM extra subrecords (Phase 4.2b): OBND, MODL, SCRI, SNAM, PNAM
    // ====================================================================================

    [Fact]
    public void TermEncoder_EncodeNew_EmitsObndWhenBoundsPresent()
    {
        var term = new TerminalRecord
        {
            FormId = 0x701,
            EditorId = "BoundedTerm",
            Bounds = new ObjectBounds { X1 = -10, Y1 = -20, Z1 = 0, X2 = 10, Y2 = 20, Z2 = 30 }
        };

        var encoded = TermEncoder.EncodeNew(term);

        var obnd = Assert.Single(encoded.Subrecords, s => s.Signature == "OBND").Bytes;
        Assert.Equal(12, obnd.Length);
        Assert.Equal(-10, BinaryPrimitives.ReadInt16LittleEndian(obnd.AsSpan(0, 2)));
        Assert.Equal(-20, BinaryPrimitives.ReadInt16LittleEndian(obnd.AsSpan(2, 2)));
        Assert.Equal(0, BinaryPrimitives.ReadInt16LittleEndian(obnd.AsSpan(4, 2)));
        Assert.Equal(10, BinaryPrimitives.ReadInt16LittleEndian(obnd.AsSpan(6, 2)));
        Assert.Equal(20, BinaryPrimitives.ReadInt16LittleEndian(obnd.AsSpan(8, 2)));
        Assert.Equal(30, BinaryPrimitives.ReadInt16LittleEndian(obnd.AsSpan(10, 2)));
    }

    [Fact]
    public void TermEncoder_EncodeNew_EmitsModlWhenModelPathPresent()
    {
        var term = new TerminalRecord
        {
            FormId = 0x702,
            EditorId = "ModelTerm",
            ModelPath = "Terminals\\testTerm.nif"
        };

        var encoded = TermEncoder.EncodeNew(term);

        var modl = Assert.Single(encoded.Subrecords, s => s.Signature == "MODL").Bytes;
        // Null-terminated string.
        Assert.Equal("Terminals\\testTerm.nif\0", System.Text.Encoding.ASCII.GetString(modl));
    }

    [Fact]
    public void TermEncoder_EncodeNew_EmitsScriSnamPnamFormIds()
    {
        var term = new TerminalRecord
        {
            FormId = 0x703,
            EditorId = "LinkedTerm",
            ScriptFormId = 0x00012345,
            SoundLoopFormId = 0x00023456,
            PasswordNoteFormId = 0x00034567
        };

        var encoded = TermEncoder.EncodeNew(term);

        var scri = Assert.Single(encoded.Subrecords, s => s.Signature == "SCRI").Bytes;
        Assert.Equal(0x00012345u, BinaryPrimitives.ReadUInt32LittleEndian(scri));
        var snam = Assert.Single(encoded.Subrecords, s => s.Signature == "SNAM").Bytes;
        Assert.Equal(0x00023456u, BinaryPrimitives.ReadUInt32LittleEndian(snam));
        var pnam = Assert.Single(encoded.Subrecords, s => s.Signature == "PNAM").Bytes;
        Assert.Equal(0x00034567u, BinaryPrimitives.ReadUInt32LittleEndian(pnam));
    }

    [Fact]
    public void TermEncoder_EncodeNew_FullCanonicalOrderWithAllOptionals()
    {
        // Canonical order per fopdoc: EDID, OBND, FULL, MODL, SCRI, DESC, SNAM, PNAM, DNAM, menu items.
        var term = new TerminalRecord
        {
            FormId = 0x704,
            EditorId = "FullTerm",
            Bounds = new ObjectBounds { X2 = 1, Y2 = 1, Z2 = 1 },
            FullName = "Mainframe",
            ModelPath = "term.nif",
            ScriptFormId = 0x1,
            HeaderText = "Welcome",
            SoundLoopFormId = 0x2,
            PasswordNoteFormId = 0x3,
            Difficulty = 2,
            Flags = 0x10,
            ServerType = 5
        };

        var encoded = TermEncoder.EncodeNew(term);

        Assert.Equal(
            ["EDID", "OBND", "FULL", "MODL", "SCRI", "DESC", "SNAM", "PNAM", "DNAM"],
            encoded.Subrecords.Select(s => s.Signature));
    }

    [Fact]
    public void TermEncoder_EncodeNew_DnamServerTypeRoundTrips()
    {
        // Regression: ServerType byte now flows from the model into DNAM[2] (previously hard-coded 0).
        var term = new TerminalRecord
        {
            FormId = 0x705,
            EditorId = "ServerTypeTerm",
            Difficulty = 1,
            Flags = 0x20,
            ServerType = 7
        };

        var encoded = TermEncoder.EncodeNew(term);

        var dnam = Assert.Single(encoded.Subrecords, s => s.Signature == "DNAM").Bytes;
        Assert.Equal(new byte[] { 1, 0x20, 7, 0 }, dnam);
    }

    [Fact]
    public void TermEncoder_EncodeNew_OmitsOptionalSubrecordsWhenNull()
    {
        // Bare TERM (no extras) should produce the same shape as before Phase 4.2b.
        var term = new TerminalRecord
        {
            FormId = 0x706,
            EditorId = "BareTerm",
            HeaderText = "Hi",
            Difficulty = 0
        };

        var encoded = TermEncoder.EncodeNew(term);

        Assert.Equal(["EDID", "DESC", "DNAM"], encoded.Subrecords.Select(s => s.Signature));
    }
}
