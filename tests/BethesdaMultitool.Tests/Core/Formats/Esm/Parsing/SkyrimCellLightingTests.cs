using System.Buffers.Binary;
using System.Numerics;
using BethesdaMultitool.Core.Formats.Esm.Conversion.Schema;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.World;
using BethesdaMultitool.Core.Formats.Esm.Records;
using BethesdaMultitool.Core.Formats.Esm.Runtime;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Atmosphere;
using BethesdaMultitool.Core.Games;
using Xunit;
using static BethesdaMultitool.Tests.Helpers.EsmTestRecordBuilder;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Parsing;

/// <summary>
///     Pins the modern Skyrim CELL/LGTM lighting shape to bytes measured directly from retail
///     Skyrim.esm. These are deliberately tiny synthetic records: the layout and typed-handler
///     contracts stay in the ordinary test bucket while the existing cached retail-master test
///     proves the same values end to end when Bucket B is enabled.
/// </summary>
public sealed class SkyrimCellLightingTests
{
    private const uint DragonsreachCellFormId = 0x000165A3;
    private const uint DragonsreachTemplateFormId = 0x0006175D;

    // Skyrim.esm CELL 0x000165A3 at file offset 0x021D1F87; XCLL begins at offset 51 in its
    // 223-byte decompressed body.
    private static readonly byte[] DragonsreachXcll = Convert.FromHexString(
        "5B50400000000000000000000000000000E0C445000000000000000000000000" +
        "00C0DA450000803F6450430051503D005B5340005B4C40002B261D008A796300" +
        "000000000000803F000000000000803F000000000000000006000000");

    // Skyrim.esm LGTM 0x0006175D DATA payload (92 bytes), file offset 0x0EA0E91D.
    private static readonly byte[] DragonsreachTemplateData = Convert.FromHexString(
        "3935310040494A0058534700000000430000FA44B4000000000000000000803F" +
        "0000FA440000803F000000000000000000000000000000000000000000000000" +
        "5853470000000000585347000000803F000000000000000000000000");

    [Fact]
    public void SkyrimNinetyTwoByteSchemas_DecodeRetailFieldsWithoutDroppingTheTail()
    {
        var xcllSchema = SubrecordSchemaRegistry.GetSchema("XCLL", "CELL", DragonsreachXcll.Length);
        var lgtmSchema = SubrecordSchemaRegistry.GetSchema("DATA", "LGTM", DragonsreachTemplateData.Length);

        Assert.NotNull(xcllSchema);
        Assert.Equal(92, xcllSchema.ExpectedSize);
        Assert.NotNull(lgtmSchema);
        Assert.Equal(92, lgtmSchema.ExpectedSize);
        // The two legacy forms remain independently registered and length-exact.
        Assert.NotNull(SubrecordSchemaRegistry.GetSchema("XCLL", "CELL", 36));
        Assert.NotNull(SubrecordSchemaRegistry.GetSchema("XCLL", "CELL", 40));

        var xcll = SubrecordSchemaView.Read("XCLL", "CELL", DragonsreachXcll, false);
        Assert.Equal(0x0040505Bu, xcll.UInt32("AmbientColor"));
        Assert.Equal(6300f, xcll.Float("FogFar"));
        Assert.Equal(7000f, xcll.Float("FogClipDistance"));
        Assert.Equal(0x00435064u, xcll.UInt32("DirectionalAmbientPositiveX"));
        Assert.Equal(0x0063798Au, xcll.UInt32("DirectionalAmbientNegativeZ"));
        Assert.Equal(1f, xcll.Float("DirectionalAmbientFresnelPower"));
        Assert.Equal(1f, xcll.Float("FogMax"));
        Assert.Equal(6u, xcll.UInt32("Inherits"));

        var template = SubrecordSchemaView.Read("DATA", "LGTM", DragonsreachTemplateData, false);
        Assert.Equal(0x00313539u, template.UInt32("AmbientColor"));
        Assert.Equal(0x004A4940u, template.UInt32("DirectionalColor"));
        Assert.Equal(0x00475358u, template.UInt32("FogColor"));
        Assert.Equal(128f, template.Float("FogNear"));
        Assert.Equal(2000f, template.Float("FogFar"));
        Assert.Equal(0x00475358u, template.UInt32("FogColorFar"));
        Assert.Equal(1f, template.Float("FogMax"));
    }

    [Fact]
    public void SkyrimCellHandler_RetainsRetailXcllAndUsesItsEmbeddedInheritanceWord()
    {
        var bytes = BuildRecordBytes(
            DragonsreachCellFormId,
            "CELL",
            false,
            ("EDID", "WhiterunDragonsreach\0"u8.ToArray()),
            ("DATA", new byte[] { 0xA1, 0x00 }),
            ("XCLL", DragonsreachXcll),
            ("LTMP", UInt32(DragonsreachTemplateFormId)),
            // Skyrim does not author this classic field here. If a malformed record has both,
            // the reviewed embedded XCLL word remains authoritative.
            ("LNAM", UInt32(0xFFFFFFFFu)));
        var cell = ParseCell(bytes);

        Assert.NotNull(cell.LightingData);
        Assert.Equal(0x0040505Bu, Assert.IsType<uint>(cell.LightingData["AmbientColor"]));
        Assert.Equal(6300f, Assert.IsType<float>(cell.LightingData["FogFar"]));
        Assert.Equal(6u, cell.LightingTemplateInheritanceFlags);
        Assert.Equal(DragonsreachTemplateFormId, cell.LightingTemplateFormId);
    }

    [Fact]
    public void SkyrimLightingTemplateHandler_RetainsRetailNinetyTwoByteData()
    {
        var bytes = BuildRecordBytes(
            DragonsreachTemplateFormId,
            "LGTM",
            false,
            ("EDID", "LightingTemplateDragonsreach\0"u8.ToArray()),
            ("DATA", DragonsreachTemplateData));
        var record = new DetectedMainRecord(
            "LGTM", (uint)(bytes.Length - 24), 0, DragonsreachTemplateFormId, 0, false);
        var context = Context(bytes, record);

        var template = Assert.Single(new MiscGameSystemHandler(context).ParseLightingTemplates());

        Assert.NotNull(template.LightingData);
        Assert.Equal(0x004A4940u, Assert.IsType<uint>(template.LightingData["DirectionalColor"]));
        Assert.Equal(0x00475358u, Assert.IsType<uint>(template.LightingData["FogColorFar"]));
        Assert.Equal(2000f, Assert.IsType<float>(template.LightingData["FogFar"]));
    }

    [Fact]
    public void SkyrimSchemaBridge_RetainsTypedLightingTemplateForInteriorResolution()
    {
        var bytes = BuildRecordBytes(
            DragonsreachTemplateFormId,
            "LGTM",
            false,
            ("EDID", "LightingTemplateDragonsreach\0"u8.ToArray()),
            ("DATA", DragonsreachTemplateData));
        var record = new DetectedMainRecord(
            "LGTM", (uint)(bytes.Length - 24), 0, DragonsreachTemplateFormId, 0, false);

        var parsed = new RecordParser(Context(bytes, record)).ParseAll();
        var template = Assert.Single(parsed.LightingTemplates);

        Assert.Equal(DragonsreachTemplateFormId, template.FormId);
        Assert.NotNull(template.LightingData);
        Assert.Equal(0x00475358u, Assert.IsType<uint>(template.LightingData["FogColorFar"]));
    }

    [Fact]
    public void RetailCellAndTemplate_ResolveInheritedFogAndDirectionalAmbientCube()
    {
        var cell = SubrecordSchemaView.Read("XCLL", "CELL", DragonsreachXcll, false).Raw;
        var template = SubrecordSchemaView.Read("DATA", "LGTM", DragonsreachTemplateData, false).Raw;

        var resolved = AtmosphereState.ResolveInterior(cell, template, 6u);

        AssertVector(new Vector3(91f, 80f, 64f) / 255f, resolved.AmbientColor);
        AssertVector(new Vector3(88f, 83f, 71f) / 255f, resolved.FogColor);
        AssertVector(new Vector3(88f, 83f, 71f) / 255f, resolved.FogFarColor);
        Assert.Equal(0f, resolved.FogNear);
        Assert.Equal(6300f, resolved.FogFar);
        Assert.Equal(1f, resolved.FogMaxOpacity);
        Assert.NotNull(resolved.DirectionalAmbient);
        AssertVector(
            new Vector3(100f, 80f, 67f) / 255f,
            resolved.DirectionalAmbient.Value.PositiveX);
        AssertVector(
            new Vector3(138f, 121f, 99f) / 255f,
            resolved.DirectionalAmbient.Value.NegativeZ);
    }

    [Fact]
    public void CellEncoder_RoundTripsSkyrimXcllWithoutFabricatingClassicLnam()
    {
        var values = SubrecordSchemaView.Read("XCLL", "CELL", DragonsreachXcll, false).Raw;
        var encoded = new CellEncoder().Encode(new CellRecord
        {
            FormId = DragonsreachCellFormId,
            EditorId = "WhiterunDragonsreach",
            Flags = 0xA1,
            LightingTemplateFormId = DragonsreachTemplateFormId,
            LightingTemplateInheritanceFlags = 6,
            LightingData = values
        });

        var xcll = Assert.Single(encoded.Subrecords, subrecord => subrecord.Signature == "XCLL");
        Assert.Equal(DragonsreachXcll, xcll.Bytes);
        Assert.DoesNotContain(encoded.Subrecords, subrecord => subrecord.Signature == "LNAM");
    }

    [Fact]
    public void LightingTemplateEncoder_RoundTripsSkyrimDataIncludingOpaqueExtension()
    {
        var values = SubrecordSchemaView.Read("DATA", "LGTM", DragonsreachTemplateData, false).Raw;
        var encoded = LgtmEncoder.EncodeNew(new LightingTemplateRecord
        {
            FormId = DragonsreachTemplateFormId,
            EditorId = "LightingTemplateDragonsreach",
            LightingData = values
        });

        var data = Assert.Single(encoded.Subrecords, subrecord => subrecord.Signature == "DATA");
        Assert.Equal(DragonsreachTemplateData, data.Bytes);
    }

    private static CellRecord ParseCell(byte[] bytes)
    {
        var record = new DetectedMainRecord(
            "CELL", (uint)(bytes.Length - 24), 0, DragonsreachCellFormId, 0, false);
        return Assert.Single(new CellRecordHandler(Context(bytes, record)).ParseCells());
    }

    private static RecordParserContext Context(byte[] bytes, DetectedMainRecord record)
    {
        return new RecordParserContext(
            new EsmRecordScanResult
            {
                Game = BethesdaGame.Skyrim,
                MainRecords = [record]
            },
            null,
            new ByteArrayMemoryAccessor(bytes),
            bytes.Length,
            null);
    }

    private static byte[] UInt32(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return bytes;
    }

    private static void AssertVector(Vector3 expected, Vector3 actual)
    {
        Assert.Equal(expected.X, actual.X, 5);
        Assert.Equal(expected.Y, actual.Y, 5);
        Assert.Equal(expected.Z, actual.Z, 5);
    }
}