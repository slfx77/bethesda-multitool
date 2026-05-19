using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Conversion.Schema;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.AI;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.AI;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.World;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Plugin;

/// <summary>
///     Tests for encoders that re-serialize parser-deficient subrecord types through
///     SchemaDictionarySerializer (CSTY/LGTM/WATR) plus narrow typed-field emission
///     (WTHR), and SchemaDictionarySerializer itself.
///     NAVI is intentionally unencoded — its model lacks vertex/triangle/portal arrays.
/// </summary>
public class SchemaDrivenEncoderTests
{
    // ====================================================================================
    // SchemaDictionarySerializer
    // ====================================================================================

    [Fact]
    public void SchemaDictionarySerializer_Serialize_WritesFieldsPerSchemaOrder()
    {
        // Use the LGTM DATA schema (40 bytes) — known layout: AmbientColor (u32) + DirectionalColor (u32) + ...
        var schema = SubrecordSchemaRegistry.GetSchema("DATA", "LGTM", 40);
        Assert.NotNull(schema);

        var values = new Dictionary<string, object?>
        {
            ["AmbientColor"] = 0xABCDEFu,
            ["DirectionalColor"] = 0x12345678u
        };

        var bytes = SchemaDictionarySerializer.Serialize(schema!, values);

        Assert.Equal(40, bytes.Length);
        Assert.Equal(0xABCDEFu, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0, 4)));
        Assert.Equal(0x12345678u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4)));
    }

    [Fact]
    public void SchemaDictionarySerializer_Serialize_ZeroFillsMissingFields()
    {
        var schema = SubrecordSchemaRegistry.GetSchema("DATA", "LGTM", 40);
        var bytes = SchemaDictionarySerializer.Serialize(schema!, new Dictionary<string, object?>());

        Assert.Equal(40, bytes.Length);
        Assert.All(bytes, b => Assert.Equal(0, b));
    }

    // ====================================================================================
    // CstyEncoder
    // ====================================================================================

    [Fact]
    public void CstyEncoder_EncodeNew_EmitsCstdCsadCssdWhenDictsPresent()
    {
        var csty = new CombatStyleRecord
        {
            FormId = 0x3500,
            EditorId = "AggressiveStyle",
            StyleData = new Dictionary<string, object?>
            {
                ["DodgeChance"] = (byte)25,
                ["LRChance"] = (byte)50
            },
            AdvancedData = new Dictionary<string, object?>
            {
                ["Value0"] = 1.0f,
                ["Value5"] = 5.0f
            },
            SimpleData = new Dictionary<string, object?>
            {
                ["CoverSearchRadius"] = 100.0f,
                ["TakeCoverChance"] = 0.5f
            }
        };

        var encoded = CstyEncoder.EncodeNew(csty);
        var sigs = encoded.Subrecords.Select(s => s.Signature).ToList();
        Assert.Equal(["EDID", "CSTD", "CSAD", "CSSD"], sigs);

        var cstd = Assert.Single(encoded.Subrecords, s => s.Signature == "CSTD").Bytes;
        Assert.Equal(92, cstd.Length);
        Assert.Equal(25, cstd[0]);
        Assert.Equal(50, cstd[1]);

        var csad = Assert.Single(encoded.Subrecords, s => s.Signature == "CSAD").Bytes;
        Assert.Equal(84, csad.Length);
        Assert.Equal(1.0f, BinaryPrimitives.ReadSingleLittleEndian(csad.AsSpan(0, 4)));
        Assert.Equal(5.0f, BinaryPrimitives.ReadSingleLittleEndian(csad.AsSpan(20, 4)));

        var cssd = Assert.Single(encoded.Subrecords, s => s.Signature == "CSSD").Bytes;
        Assert.Equal(64, cssd.Length);
    }

    [Fact]
    public void CstyEncoder_EncodeNew_OmitsCstdWhenDictNull()
    {
        var csty = new CombatStyleRecord { FormId = 0x3500, EditorId = "Minimal" };
        var encoded = CstyEncoder.EncodeNew(csty);
        var sigs = encoded.Subrecords.Select(s => s.Signature).ToList();
        Assert.Equal(["EDID"], sigs);
    }

    // ====================================================================================
    // LgtmEncoder
    // ====================================================================================

    [Fact]
    public void LgtmEncoder_EncodeNew_DataIs40Bytes()
    {
        var lgtm = new LightingTemplateRecord
        {
            FormId = 0x3600,
            EditorId = "InteriorDefault",
            LightingData = new Dictionary<string, object?>
            {
                ["AmbientColor"] = 0x808080u,
                ["DirectionalColor"] = 0xFFFFFFu
            }
        };

        var encoded = LgtmEncoder.EncodeNew(lgtm);
        var data = Assert.Single(encoded.Subrecords, s => s.Signature == "DATA").Bytes;
        Assert.Equal(40, data.Length);
        Assert.Equal(0x808080u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0, 4)));
    }

    [Fact]
    public void LgtmEncoder_EncodeNew_NullDictOmitsDataSubrecord()
    {
        var lgtm = new LightingTemplateRecord { FormId = 0x3600, EditorId = "Empty" };
        var encoded = LgtmEncoder.EncodeNew(lgtm);
        Assert.DoesNotContain(encoded.Subrecords, s => s.Signature == "DATA");
    }

    // ====================================================================================
    // WatrEncoder
    // ====================================================================================

    [Fact]
    public void WatrEncoder_EncodeNew_CanonicalOrderWithAllFields()
    {
        var watr = new WaterRecord
        {
            FormId = 0x3700,
            EditorId = "Lake",
            FullName = "Lake",
            NoiseTexture = "water/noise.dds",
            Opacity = 200,
            WaterFlags = [0x01, 0x02],
            SoundFormId = 0xABC,
            Damage = 5,
            VisualProperties = new Dictionary<string, object?> { ["SunPower"] = 50.0f },
            RelatedWater = new Dictionary<string, object?> { ["Daytime"] = 0x111u }
        };

        var encoded = WatrEncoder.EncodeNew(watr);
        var sigs = encoded.Subrecords.Select(s => s.Signature).ToList();
        Assert.Equal(
            ["EDID", "FULL", "NNAM", "ANAM", "FNAM", "SNAM", "DATA", "DNAM", "GNAM"],
            sigs);

        var anam = Assert.Single(encoded.Subrecords, s => s.Signature == "ANAM").Bytes;
        Assert.Equal(200, anam[0]);

        var data = Assert.Single(encoded.Subrecords, s => s.Signature == "DATA").Bytes;
        Assert.Equal(2, data.Length);
        Assert.Equal((ushort)5, BinaryPrimitives.ReadUInt16LittleEndian(data));

        var dnam = Assert.Single(encoded.Subrecords, s => s.Signature == "DNAM").Bytes;
        Assert.Equal(196, dnam.Length);

        var gnam = Assert.Single(encoded.Subrecords, s => s.Signature == "GNAM").Bytes;
        Assert.Equal(12, gnam.Length);
        Assert.Equal(0x111u, BinaryPrimitives.ReadUInt32LittleEndian(gnam.AsSpan(0, 4)));
    }

    // ====================================================================================
    // WthrEncoder
    // ====================================================================================

    [Fact]
    public void WthrEncoder_EncodeNew_EmitsTypedFieldsOnly()
    {
        var wthr = new WeatherRecord
        {
            FormId = 0x3800,
            EditorId = "SunnyDay",
            ImageSpaceModifier = 0x100,
            Sounds =
            [
                new WeatherSound { SoundFormId = 0x200, Type = 1 },
                new WeatherSound { SoundFormId = 0x300, Type = 2 }
            ]
        };

        var encoded = WthrEncoder.EncodeNew(wthr);
        var sigs = encoded.Subrecords.Select(s => s.Signature).ToList();
        Assert.Equal(["EDID", "INAM", "SNAM", "SNAM"], sigs);

        var snams = encoded.Subrecords.Where(s => s.Signature == "SNAM").ToList();
        Assert.Equal(8, snams[0].Bytes.Length);
        Assert.Equal(0x200u, BinaryPrimitives.ReadUInt32LittleEndian(snams[0].Bytes.AsSpan(0, 4)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(snams[0].Bytes.AsSpan(4, 4)));
    }

    [Fact]
    public void WthrEncoder_EncodeNew_AlwaysWarnsAboutVisualSubrecords()
    {
        var wthr = new WeatherRecord { FormId = 0x3800, EditorId = "Any" };
        var encoded = WthrEncoder.EncodeNew(wthr);
        Assert.Contains(encoded.Warnings, w => w.Contains("visual subrecords"));
    }

    // ====================================================================================
    // Cross-encoder warning check
    // ====================================================================================

    [Fact]
    public void SchemaDrivenEncoders_EmitWarningWhenEditorIdMissing()
    {
        Assert.Contains(CstyEncoder.EncodeNew(new CombatStyleRecord { FormId = 1 }).Warnings,
            w => w.Contains("has no EditorId"));
        Assert.Contains(LgtmEncoder.EncodeNew(new LightingTemplateRecord { FormId = 1 }).Warnings,
            w => w.Contains("has no EditorId"));
        Assert.Contains(WatrEncoder.EncodeNew(new WaterRecord { FormId = 1 }).Warnings,
            w => w.Contains("has no EditorId"));
        Assert.Contains(WthrEncoder.EncodeNew(new WeatherRecord { FormId = 1 }).Warnings,
            w => w.Contains("has no EditorId"));
    }
}
