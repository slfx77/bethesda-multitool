using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Conversion.Schema;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;
using BethesdaMultitool.Core.Formats.Esm.Records;
using BethesdaMultitool.Core.Formats.Esm.Runtime;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Parsing;

/// <summary>
///     Sentinel coverage for the classic FO3/FNV 196-byte WATR visual-data layout. In particular,
///     these tests keep WATER001's fog/refraction inputs tied to their authored offsets and prevent
///     the schema's <c>UnderWater</c> spelling from drifting away from the typed appearance model.
/// </summary>
public sealed class ClassicWaterDataParsingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FalloutNvDnam196_ParsesClassicSurfaceOffsetsAliasesAndFnam(bool bigEndian)
    {
        var dnam = new byte[196];
        WriteFloat(dnam, 16, 116.25f, bigEndian);  // SunPower
        WriteFloat(dnam, 20, 120.25f, bigEndian);  // ReflectivityAmount
        WriteFloat(dnam, 24, 124.25f, bigEndian);  // FresnelAmount
        WriteFloat(dnam, 32, 132.25f, bigEndian);  // FogNear
        WriteFloat(dnam, 36, 136.25f, bigEndian);  // FogFar
        WriteUInt32(dnam, 40, 0xAA_33_22_11u, bigEndian); // ShallowColor
        WriteUInt32(dnam, 44, 0xBB_66_55_44u, bigEndian); // DeepColor
        WriteUInt32(dnam, 48, 0xCC_99_88_77u, bigEndian); // ReflectionColor
        WriteFloat(dnam, 96, 196.25f, bigEndian);  // NoiseScale
        WriteFloat(dnam, 100, 100.25f, bigEndian); // Layer 1 WindDir
        WriteFloat(dnam, 104, 104.25f, bigEndian); // Layer 2 WindDir
        WriteFloat(dnam, 108, 108.25f, bigEndian); // Layer 3 WindDir
        WriteFloat(dnam, 112, 112.25f, bigEndian); // Layer 1 WindSpeed
        WriteFloat(dnam, 116, 116.50f, bigEndian); // Layer 2 WindSpeed
        WriteFloat(dnam, 120, 120.75f, bigEndian); // Layer 3 WindSpeed
        WriteFloat(dnam, 124, 124.50f, bigEndian); // DepthFalloffStart
        WriteFloat(dnam, 128, 128.50f, bigEndian); // DepthFalloffEnd
        WriteFloat(dnam, 132, 0.625f, bigEndian);  // AboveWaterFogAmount
        WriteFloat(dnam, 136, 136.50f, bigEndian); // NormalsUVScale
        WriteFloat(dnam, 140, 0.875f, bigEndian);  // UnderWaterFogAmount
        WriteFloat(dnam, 144, 144.50f, bigEndian); // UnderWaterFogNear
        WriteFloat(dnam, 148, 148.50f, bigEndian); // UnderWaterFogFar
        WriteFloat(dnam, 152, 2.75f, bigEndian);   // DistortionAmount
        WriteFloat(dnam, 156, 156.50f, bigEndian); // Shininess
        WriteFloat(dnam, 172, 172.25f, bigEndian); // Layer 1 UVScale
        WriteFloat(dnam, 176, 176.25f, bigEndian); // Layer 2 UVScale
        WriteFloat(dnam, 180, 180.25f, bigEndian); // Layer 3 UVScale
        WriteFloat(dnam, 184, 0.184f, bigEndian);  // Layer 1 AmpScale
        WriteFloat(dnam, 188, 0.188f, bigEndian);  // Layer 2 AmpScale
        WriteFloat(dnam, 192, 0.192f, bigEndian);  // Layer 3 AmpScale

        var water = ParseWater(bigEndian,
            ("FNAM", new byte[] { 0x03 }), // WATR U8: bit 0 Causes Damage, bit 1 Reflective.
            ("DNAM", dnam));

        Assert.Equal(new byte[] { 0x03 }, water.WaterFlags);
        Assert.NotNull(water.VisualProperties);
        Assert.Equal(0.625f, water.VisualProperties!["AboveWaterFogAmount"]);
        Assert.Equal(0.875f, water.VisualProperties["UnderWaterFogAmount"]);
        Assert.Equal(144.50f, water.VisualProperties["UnderWaterFogNear"]);
        Assert.Equal(148.50f, water.VisualProperties["UnderWaterFogFar"]);
        Assert.Equal(2.75f, water.VisualProperties["DistortionAmount"]);
        Assert.DoesNotContain("UnderwaterFogAmount", water.VisualProperties.Keys);

        var appearance = Assert.IsType<WaterAppearance>(WaterAppearance.FromWaterRecord(water));
        Assert.True(appearance.CausesDamage);
        Assert.Equal((R: (byte)0x11, G: (byte)0x22, B: (byte)0x33), appearance.Shallow);
        Assert.Equal((R: (byte)0x44, G: (byte)0x55, B: (byte)0x66), appearance.Deep);
        Assert.Equal((R: (byte)0x77, G: (byte)0x88, B: (byte)0x99), appearance.Reflection);

        var surface = appearance.Surface;
        Assert.Equal(116.25f, surface.SunPower);
        Assert.Equal(120.25f, surface.ReflectivityAmount);
        Assert.Equal(124.25f, surface.FresnelAmount);
        Assert.Equal(132.25f, surface.FogNear);
        Assert.Equal(136.25f, surface.FogFar);
        Assert.Equal(196.25f, surface.NoiseScale);
        Assert.Equal(124.50f, surface.DepthFalloffStart);
        Assert.Equal(128.50f, surface.DepthFalloffEnd);
        Assert.Equal(0.625f, surface.AboveWaterFogAmount);
        Assert.Equal(136.50f, surface.NormalsUvScale);
        Assert.Equal(0.875f, surface.UnderwaterFogAmount);
        Assert.Equal(144.50f, surface.UnderwaterFogNear);
        Assert.Equal(148.50f, surface.UnderwaterFogFar);
        Assert.Equal(2.75f, surface.RefractionDistortionAmount);
        Assert.Equal(156.50f, surface.Shininess);
        Assert.Equal(new WaterNoiseLayer(172.25f, 100.25f, 112.25f, 0.184f), surface.Layer1);
        Assert.Equal(new WaterNoiseLayer(176.25f, 104.25f, 116.50f, 0.188f), surface.Layer2);
        Assert.Equal(new WaterNoiseLayer(180.25f, 108.25f, 120.75f, 0.192f), surface.Layer3);
        Assert.True(surface.HasAuthoredNoiseLayers);
        Assert.True(surface.HasAuthoredClassicRefractionInputs);
    }

    [Fact]
    public void UnderwaterFogAliases_AcceptEitherSpellingWithCreationAuthoritative()
    {
        var classicProperties = CompleteRefractionProperties();
        classicProperties["UnderWaterFogAmount"] = 0.35f;
        classicProperties["UnderWaterFogNear"] = 12f;
        classicProperties["UnderWaterFogFar"] = 345f;
        var classic = AppearanceWith(classicProperties).Surface;
        Assert.Equal(0.35f, classic.UnderwaterFogAmount);
        Assert.Equal(12f, classic.UnderwaterFogNear);
        Assert.Equal(345f, classic.UnderwaterFogFar);
        Assert.True(classic.HasAuthoredClassicRefractionInputs);

        var creationProperties = CompleteRefractionProperties();
        creationProperties["UnderwaterFogAmount"] = 0.65f;
        creationProperties["UnderwaterFogNear"] = 21f;
        creationProperties["UnderwaterFogFar"] = 543f;
        var creation = AppearanceWith(creationProperties).Surface;
        Assert.Equal(0.65f, creation.UnderwaterFogAmount);
        Assert.Equal(21f, creation.UnderwaterFogNear);
        Assert.Equal(543f, creation.UnderwaterFogFar);
        Assert.True(creation.HasAuthoredClassicRefractionInputs);

        var bothProperties = CompleteRefractionProperties();
        bothProperties["UnderwaterFogAmount"] = 0.75f;
        bothProperties["UnderwaterFogNear"] = 31f;
        bothProperties["UnderwaterFogFar"] = 654f;
        bothProperties["UnderWaterFogAmount"] = 0.25f;
        bothProperties["UnderWaterFogNear"] = 13f;
        bothProperties["UnderWaterFogFar"] = 456f;
        var both = AppearanceWith(bothProperties).Surface;
        Assert.Equal(0.75f, both.UnderwaterFogAmount);
        Assert.Equal(31f, both.UnderwaterFogNear);
        Assert.Equal(654f, both.UnderwaterFogFar);
        Assert.True(both.HasAuthoredClassicRefractionInputs);
    }

    [Fact]
    public void Water001Inputs_MissingOrMalformedValuesUseDefaultsWithoutHidingBadPrimaryAlias()
    {
        Assert.Equal(0.75f, WaterSurfaceParams.Default.AboveWaterFogAmount);
        Assert.Equal(1f, WaterSurfaceParams.Default.UnderwaterFogAmount);
        Assert.Equal(-2500f, WaterSurfaceParams.Default.UnderwaterFogNear);
        Assert.Equal(5500f, WaterSurfaceParams.Default.UnderwaterFogFar);
        Assert.Equal(600f, WaterSurfaceParams.Default.RefractionDistortionAmount);
        Assert.False(WaterSurfaceParams.Default.HasAuthoredClassicRefractionInputs);

        var missing = AppearanceWith(new Dictionary<string, object?>()).Surface;
        Assert.Equal(WaterSurfaceParams.Default.AboveWaterFogAmount, missing.AboveWaterFogAmount);
        Assert.Equal(WaterSurfaceParams.Default.UnderwaterFogAmount, missing.UnderwaterFogAmount);
        Assert.Equal(WaterSurfaceParams.Default.UnderwaterFogNear, missing.UnderwaterFogNear);
        Assert.Equal(WaterSurfaceParams.Default.UnderwaterFogFar, missing.UnderwaterFogFar);
        Assert.Equal(
            WaterSurfaceParams.Default.RefractionDistortionAmount,
            missing.RefractionDistortionAmount);
        Assert.False(missing.HasAuthoredClassicRefractionInputs);

        var malformedProperties = CompleteRefractionProperties();
        malformedProperties["AboveWaterFogAmount"] = float.PositiveInfinity;
        malformedProperties["DistortionAmount"] = "not-a-number";
        // A present Creation spelling is authoritative. These valid classic aliases must not
        // conceal the malformed primary data by being selected as a second fallback source.
        malformedProperties["UnderwaterFogAmount"] = float.NaN;
        malformedProperties["UnderWaterFogAmount"] = 0.9f;
        malformedProperties["UnderwaterFogNear"] = "bad";
        malformedProperties["UnderWaterFogNear"] = 90f;
        malformedProperties["UnderwaterFogFar"] = double.NegativeInfinity;
        malformedProperties["UnderWaterFogFar"] = 900f;
        var malformed = AppearanceWith(malformedProperties).Surface;
        Assert.Equal(WaterSurfaceParams.Default.AboveWaterFogAmount, malformed.AboveWaterFogAmount);
        Assert.Equal(WaterSurfaceParams.Default.UnderwaterFogAmount, malformed.UnderwaterFogAmount);
        Assert.Equal(WaterSurfaceParams.Default.UnderwaterFogNear, malformed.UnderwaterFogNear);
        Assert.Equal(WaterSurfaceParams.Default.UnderwaterFogFar, malformed.UnderwaterFogFar);
        Assert.Equal(
            WaterSurfaceParams.Default.RefractionDistortionAmount,
            malformed.RefractionDistortionAmount);
        Assert.False(malformed.HasAuthoredClassicRefractionInputs);

        var malformedDuplicateProperties = CompleteRefractionProperties();
        malformedDuplicateProperties["UnderwaterFogAmount"] = 0.8f;
        malformedDuplicateProperties["UnderwaterFogNear"] = 80f;
        malformedDuplicateProperties["UnderwaterFogFar"] = 800f;
        malformedDuplicateProperties["UnderWaterFogNear"] = float.NaN;
        var malformedDuplicate = AppearanceWith(malformedDuplicateProperties).Surface;
        Assert.Equal(80f, malformedDuplicate.UnderwaterFogNear);
        Assert.False(malformedDuplicate.HasAuthoredClassicRefractionInputs);
    }

    [Fact]
    public void WatrFnamSchema_IsU8AndDoesNotClaimPwatRenderCategoryBits()
    {
        // Generated FO3/FNV definitions put Refracts (bit 9), Refracts Land (bit 11), and Depth
        // (bit 28), plus their actor/dynamic/dead-body categories, on PWAT/DNAM's U32 placement
        // flags. WATR/FNAM is independently evidenced as this one-byte behavior field. A retail
        // FalloutNV.esm census found 78/78 WATR FNAM payloads were one byte (44 x 0, 34 x 0x02),
        // while all 29 PWAT records carried the separate 8-byte DNAM placement tuple.
        var schema = Assert.IsType<SubrecordSchema>(
            SubrecordSchemaRegistry.GetSchema("FNAM", "WATR", 1));
        var flags = Assert.Single(schema.Fields);
        Assert.Equal("Flags", flags.Name);
        Assert.Equal(SubrecordFieldType.UInt8, flags.Type);
        Assert.Null(SubrecordSchemaRegistry.GetSchema("FNAM", "WATR", 4));
    }

    private static WaterAppearance AppearanceWith(Dictionary<string, object?> properties)
    {
        properties["ShallowColor"] = 0x00_30_20_10u;
        return Assert.IsType<WaterAppearance>(WaterAppearance.FromVisualProperties(properties, null));
    }

    private static Dictionary<string, object?> CompleteRefractionProperties() => new()
    {
        ["FogNear"] = 100f,
        ["FogFar"] = 10_000f,
        ["DepthFalloffStart"] = 0f,
        ["DepthFalloffEnd"] = 0.01f,
        ["AboveWaterFogAmount"] = 0.5f,
        ["DistortionAmount"] = 250f,
    };

    private static WaterRecord ParseWater(
        bool bigEndian,
        params (string Signature, byte[] Data)[] subrecords)
    {
        var data = BuildSubrecords(bigEndian, subrecords);
        const int headerSize = 24;
        var file = new byte[headerSize + data.Length];
        data.CopyTo(file, headerSize);
        var record = new DetectedMainRecord("WATR", (uint)data.Length, 0, 0x0100_1234, 0, bigEndian)
        {
            HeaderSize = headerSize,
        };
        var scan = new EsmRecordScanResult
        {
            Game = BethesdaGame.FalloutNewVegas,
            MainRecords = [record],
        };
        var context = new RecordParserContext(
            scan,
            formIdCorrelations: null,
            accessor: new ByteArrayMemoryAccessor(file),
            fileSize: file.Length,
            minidumpInfo: null);

        return Assert.Single(new MiscEnvironmentHandler(context).ParseWater());
    }

    private static byte[] BuildSubrecords(
        bool bigEndian,
        IEnumerable<(string Signature, byte[] Data)> subrecords)
    {
        var result = new List<byte>();
        foreach (var (signature, data) in subrecords)
        {
            var signatureBytes = Encoding.ASCII.GetBytes(signature);
            if (bigEndian)
            {
                Array.Reverse(signatureBytes);
            }

            result.AddRange(signatureBytes);
            var length = new byte[2];
            if (bigEndian)
            {
                BinaryPrimitives.WriteUInt16BigEndian(length, checked((ushort)data.Length));
            }
            else
            {
                BinaryPrimitives.WriteUInt16LittleEndian(length, checked((ushort)data.Length));
            }

            result.AddRange(length);
            result.AddRange(data);
        }

        return [.. result];
    }

    private static void WriteFloat(byte[] destination, int offset, float value, bool bigEndian)
    {
        if (bigEndian)
        {
            BinaryPrimitives.WriteSingleBigEndian(destination.AsSpan(offset, sizeof(float)), value);
        }
        else
        {
            BinaryPrimitives.WriteSingleLittleEndian(destination.AsSpan(offset, sizeof(float)), value);
        }
    }

    private static void WriteUInt32(byte[] destination, int offset, uint value, bool bigEndian)
    {
        if (bigEndian)
        {
            BinaryPrimitives.WriteUInt32BigEndian(destination.AsSpan(offset, sizeof(uint)), value);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(destination.AsSpan(offset, sizeof(uint)), value);
        }
    }
}
