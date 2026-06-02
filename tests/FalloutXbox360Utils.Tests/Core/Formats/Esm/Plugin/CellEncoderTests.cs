using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.World;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Plugin;

public class CellEncoderTests
{
    [Fact]
    public void Encode_AlwaysEmitsEdidAndData()
    {
        var cell = new CellRecord { FormId = 0x42, EditorId = "TestCell" };

        var encoded = new CellEncoder().Encode(cell);

        Assert.Contains(encoded.Subrecords, s => s.Signature == "EDID");
        Assert.Contains(encoded.Subrecords, s => s.Signature == "DATA");
    }

    [Fact]
    public void Encode_InteriorCell_DataHasInteriorBit()
    {
        // v4 respects the model's IsInterior (computed from Flags bit 0) instead of v3's
        // hardcoded "force bit 0 on". Caller is responsible for setting Flags correctly.
        var cell = new CellRecord { FormId = 0x42, EditorId = "I", Flags = 0x01 };

        var encoded = new CellEncoder().Encode(cell);

        var data = Assert.Single(encoded.Subrecords, s => s.Signature == "DATA");
        Assert.Single(data.Bytes);
        Assert.Equal(0x01, data.Bytes[0] & 0x01);
    }

    [Fact]
    public void Encode_PreservesOtherCellFlagsAlongsideInteriorBit()
    {
        // Interior cell with public-place bit also set.
        var cell = new CellRecord { FormId = 0x42, EditorId = "I", Flags = 0x21 };
        var encoded = new CellEncoder().Encode(cell);
        var data = Assert.Single(encoded.Subrecords, s => s.Signature == "DATA");

        Assert.Equal(0x21, data.Bytes[0]); // interior + public-place
    }

    [Fact]
    public void Encode_OmitsXclwWhenWaterHeightUnset()
    {
        var cell = new CellRecord { FormId = 0x42, EditorId = "I", WaterHeight = null };
        var encoded = new CellEncoder().Encode(cell);
        Assert.DoesNotContain(encoded.Subrecords, s => s.Signature == "XCLW");
    }

    [Fact]
    public void Encode_EmitsXclwWhenWaterHeightSet()
    {
        var cell = new CellRecord { FormId = 0x42, EditorId = "I", Flags = 0x03, WaterHeight = -42.5f };
        var encoded = new CellEncoder().Encode(cell);
        var xclw = Assert.Single(encoded.Subrecords, s => s.Signature == "XCLW");
        Assert.Equal(-42.5f, BinaryPrimitives.ReadSingleLittleEndian(xclw.Bytes));
    }

    [Fact]
    public void Encode_OmitsXclwWhenWaterFlagIsClear()
    {
        var cell = new CellRecord { FormId = 0x42, EditorId = "Dry", Flags = 0x01, WaterHeight = 2048f };

        var encoded = new CellEncoder().Encode(cell);

        Assert.DoesNotContain(encoded.Subrecords, s => s.Signature == "XCLW");
        Assert.Contains(encoded.Warnings, w => w.Contains("no water flag"));
    }

    [Fact]
    public void Encode_ClearsWaterFlagWhenXclwIsSuppressed()
    {
        var cell = new CellRecord { FormId = 0x42, EditorId = "DryExterior", Flags = 0x02, WaterHeight = 2048f };

        var encoded = new CellEncoder().Encode(cell);

        var data = Assert.Single(encoded.Subrecords, s => s.Signature == "DATA");
        Assert.Equal(0, data.Bytes[0] & 0x02);
        Assert.DoesNotContain(encoded.Subrecords, s => s.Signature == "XCLW");
    }

    [Fact]
    public void Encode_OmitsExteriorXclwWithoutTerrainEvidence()
    {
        var cell = new CellRecord { FormId = 0x42, EditorId = "Exterior", Flags = 0x02, WaterHeight = 2048f };

        var encoded = new CellEncoder().Encode(cell);

        Assert.DoesNotContain(encoded.Subrecords, s => s.Signature == "XCLW");
    }

    [Fact]
    public void Encode_EmitsLightingTemplateAndInheritanceFlagsWhenSet()
    {
        var cell = new CellRecord
        {
            FormId = 0x42,
            EditorId = "I",
            LightingTemplateFormId = 0x123456,
            LightingTemplateInheritanceFlags = 0x07
        };

        var encoded = new CellEncoder().Encode(cell);

        Assert.Contains(encoded.Subrecords, s => s.Signature == "LTMP");
        Assert.Contains(encoded.Subrecords, s => s.Signature == "LNAM");
    }

    [Fact]
    public void Encode_EmitsDirectCellLightingWhenSet()
    {
        var cell = new CellRecord
        {
            FormId = 0x42,
            EditorId = "Lit",
            LightingData = new Dictionary<string, object?>
            {
                ["AmbientColor"] = 0x11223344u,
                ["DirectionalColor"] = 0x55667788u,
                ["FogColor"] = 0x99AABBCCu,
                ["FogNear"] = 128.5f,
                ["FogFar"] = 4096f,
                ["DirectionalRotationXY"] = 15,
                ["DirectionalRotationZ"] = -30,
                ["DirectionalFade"] = 0.75f,
                ["FogClipDistance"] = 8192f,
                ["FogPow"] = 1.25f
            }
        };

        var encoded = new CellEncoder().Encode(cell);
        var xcll = Assert.Single(encoded.Subrecords, s => s.Signature == "XCLL");

        Assert.Equal(40, xcll.Bytes.Length);
        Assert.Equal(0x11223344u, BinaryPrimitives.ReadUInt32LittleEndian(xcll.Bytes.AsSpan(0, 4)));
        Assert.Equal(128.5f, BinaryPrimitives.ReadSingleLittleEndian(xcll.Bytes.AsSpan(12, 4)));
        Assert.Equal(-30, BinaryPrimitives.ReadInt32LittleEndian(xcll.Bytes.AsSpan(24, 4)));
    }

    [Fact]
    public void Encode_WarnsWhenInteriorCellMissesEditorId()
    {
        // Interior cells (Flags bit 0 set) need an EDID for editor/script navigation —
        // missing one is a real problem the user should hear about.
        var cell = new CellRecord { FormId = 0x42, EditorId = null, Flags = 0x01 };

        var encoded = new CellEncoder().Encode(cell);

        Assert.Contains(encoded.Warnings, w => w.Contains("has no EditorId"));
    }

    [Fact]
    public void Encode_DoesNotWarnWhenExteriorCellMissesEditorId()
    {
        // Exterior cells are identified by XCLC grid coords, not editor names — a missing
        // EDID is the norm in vanilla content (every wilderness cell). Warning would be noise.
        var cell = new CellRecord
        {
            FormId = 0x42, EditorId = null, Flags = 0x00, GridX = 0, GridY = 0
        };

        var encoded = new CellEncoder().Encode(cell);

        Assert.DoesNotContain(encoded.Warnings, w => w.Contains("has no EditorId"));
    }

    [Fact]
    public void Encode_DoesNotEmitEdidWhenEditorIdIsNullOrEmpty()
    {
        // Worldspace persistent cells (block -1, sub-block -1 of each WRLD) have no EDID
        // in master FalloutNV.esm. Appending an empty EDID subrecord via the merge engine
        // crashes the engine's special-case handling of the persistent cell during havok
        // cell-attach — confirmed across 13 master worldspaces in the v52-xex44 crash.
        // The encoder must emit zero EDID subrecords when EditorId is null/empty so the
        // merge engine retains master's "no EDID" shape.
        var nullEdid = new CellRecord { FormId = 0x42, EditorId = null, Flags = 0x00 };
        var emptyEdid = new CellRecord { FormId = 0x43, EditorId = "", Flags = 0x00 };

        var enc1 = new CellEncoder().Encode(nullEdid);
        var enc2 = new CellEncoder().Encode(emptyEdid);

        Assert.DoesNotContain(enc1.Subrecords, s => s.Signature == "EDID");
        Assert.DoesNotContain(enc2.Subrecords, s => s.Signature == "EDID");
    }
}
