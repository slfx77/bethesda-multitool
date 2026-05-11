using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Plugin;

/// <summary>
///     v7 tests for the world-object encoders: ACTI, DOOR, LIGH, STAT, CONT, FURN, TERM.
///     Each test verifies byte layouts against PDB schemas and fopdoc canonical ordering.
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
}
