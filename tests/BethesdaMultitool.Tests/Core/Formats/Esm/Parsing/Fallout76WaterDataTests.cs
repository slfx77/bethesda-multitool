using System.Buffers.Binary;
using System.Text;
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
///     Pins FO76's distinct 148-byte WATR DNAM. The fixture is the exact DNAM from current retail
///     <c>SeventySix.esm</c>, WATR 0x0082F8B9 (<c>Burn_ExtToxicAbraxoWaterBasin</c>), rather than a
///     shortened synthetic FO4 payload. FO76 stores per-channel opacity and a float base color at
///     the front, then field-grouped three-layer normal animation data at byte 84.
/// </summary>
public sealed class Fallout76WaterDataTests
{
    internal const uint RetailFormId = 0x0082F8B9;
    internal const string RetailEditorId = "Burn_ExtToxicAbraxoWaterBasin";
    internal const string RetailDnamHex =
        "FFFFAB4200000000000000000000803F8BD3AB3DDAFAA93C32924D3C33338B40" +
        "B76631000000803F003013C60000354403E78C3D0000803F2B186D3FA4707D3F" +
        "CDCCCC3D9A99593FCDCC4C3FEC51783FCDCC4C3DF8536F438BECA54372687942" +
        "7DAEB63C8A8EE43C4C37093D423EC83E2B65D93E2B87D63E0080A74300005F43" +
        "FFFFDF420000804500008045000080450000803F";

    [Fact]
    public void ReadFallout76WaterData_DecodesExactRetailLayoutWithoutFo4SpecularAliases()
    {
        var bytes = Convert.FromHexString(RetailDnamHex);

        var props = MiscEnvironmentHandler.ReadFallout76WaterData(bytes, false);
        var visual = Assert.IsType<Fallout76WaterVisualData>(props["Fallout76VisualData"]);

        Assert.Equal(148, bytes.Length);
        Assert.Equal(85.9999924f, visual.DepthAmount);
        Assert.Equal((R: 0f, G: 0f, B: 1f), visual.ChannelOpacity);
        Assert.Equal(0.08389958f, visual.BaseColor.R, 7);
        Assert.Equal(0.02074950f, visual.BaseColor.G, 7);
        Assert.Equal(0.01254706f, visual.BaseColor.B, 7);
        Assert.Equal(4.35f, visual.Unknown28, 5);
        Assert.Equal((R: (byte)183, G: (byte)102, B: (byte)49), visual.UnderwaterColor);
        Assert.Equal(1f, visual.UnderwaterFogAmount);
        Assert.Equal(-9420f, visual.UnderwaterFogNear);
        Assert.Equal(724f, visual.UnderwaterFogFar);
        Assert.Equal(0.068799995f, visual.NormalMagnitude);
        Assert.Equal(1f, visual.ShallowNormalFalloff);
        Assert.Equal(0.926150024f, visual.DeepNormalFalloff);
        Assert.Equal(0.99f, visual.ReflectivityCandidate, 5);
        Assert.Equal(0.1f, visual.FresnelCandidate, 5);
        Assert.Equal(0.85f, visual.SurfaceEffectFalloff, 5);
        Assert.Equal(0.8f, visual.DisplacementForce, 5);
        Assert.Equal(0.97f, visual.DisplacementVelocity, 5);
        Assert.Equal(0.05f, visual.DisplacementFalloff, 5);

        AssertLayer(visual.Layer1, 335f, 239.328003f, 0.022299999f, 0.391099989f, 4096f);
        AssertLayer(visual.Layer2, 223f, 331.847992f, 0.027899999f, 0.424599975f, 4096f);
        AssertLayer(visual.Layer3, 111.999992f, 62.351997f, 0.033500001f, 0.419f, 4096f);
        Assert.Equal(1f, visual.Unknown144);

        // The current renderer's byte-color bridge uses the one authored base color for both depth
        // endpoints; the exact float base and channel-opacity vectors remain on the typed payload.
        Assert.Equal(0x00_03_05_15u, Assert.IsType<uint>(props["ShallowColor"]));
        Assert.Equal(0x00_03_05_15u, Assert.IsType<uint>(props["DeepColor"]));
        Assert.Equal(0x00_31_66_B7u, Assert.IsType<uint>(props["UnderwaterColor"]));
        Assert.DoesNotContain("ReflectionColor", props.Keys);
        Assert.DoesNotContain("SunPower", props.Keys);
        Assert.DoesNotContain("SunSpecularMagnitude", props.Keys);
        Assert.DoesNotContain("SunSparklePower", props.Keys);
    }

    [Fact]
    public void ReadFallout76WaterData_FeedsTypedAppearanceAndAllThreeAuthoredNormalLayers()
    {
        var props = MiscEnvironmentHandler.ReadFallout76WaterData(
            Convert.FromHexString(RetailDnamHex), false);
        string[] normals =
        [
            @"data\Textures\Water\DefaultWaterTile_n.DDS",
            @"data\Textures\Water\DefaultWater_n.DDS",
            @"data\Textures\Water\DefaultWater_n.DDS"
        ];

        var appearance = WaterAppearance.FromVisualProperties(props, normals[0], normals);

        Assert.NotNull(appearance);
        Assert.Equal((R: (byte)21, G: (byte)5, B: (byte)3), appearance.Shallow);
        Assert.Equal(appearance.Shallow, appearance.Deep);
        Assert.Equal(appearance.Shallow, appearance.Reflection);
        Assert.Equal((R: (byte)183, G: (byte)102, B: (byte)49), appearance.Underwater);
        Assert.Equal(normals[0], appearance.NoiseTexture);
        Assert.Equal(normals, appearance.NormalTextures);
        Assert.NotNull(appearance.Fallout76VisualData);
        Assert.Equal((R: 0f, G: 0f, B: 1f), appearance.Fallout76VisualData.Value.ChannelOpacity);

        var surface = appearance.Surface;
        Assert.Equal(85.9999924f, surface.DepthAmount);
        Assert.Equal(0.99f, surface.ReflectivityAmount, 5);
        Assert.Equal(0.1f, surface.FresnelAmount, 5);
        Assert.Equal(0.85f, surface.SurfaceEffectFalloff, 5);
        Assert.Equal(0.8f, surface.DisplacementForce, 5);
        Assert.Equal(0.97f, surface.DisplacementVelocity, 5);
        Assert.Equal(0.05f, surface.DisplacementFalloff, 5);
        Assert.Equal(2f / 3f, surface.ShallowAlpha, 5);
        Assert.Equal(23f / 24f, surface.DeepAlpha, 5);
        Assert.Equal(1f, surface.AlphaDeepRange);
        Assert.Equal(0.068799995f, surface.NormalMagnitude);
        Assert.Equal(335f, surface.Layer1.UvScale);
        Assert.Equal(331.847992f, surface.Layer2.WindDirDegrees);
        Assert.Equal(0.033500001f, surface.Layer3.WindSpeed);
        Assert.Equal(4096f, surface.Layer3.Falloff);
        Assert.True(surface.HasAuthoredNoiseLayers);
        Assert.Equal(0f, surface.SunSpecularMagnitude);
    }

    [Fact]
    public void FromVisualProperties_TypedBlackFo76BaseColorRemainsAValidAppearance()
    {
        var bytes = Convert.FromHexString(RetailDnamHex);
        bytes.AsSpan(16, 3 * sizeof(float)).Clear();
        var props = MiscEnvironmentHandler.ReadFallout76WaterData(bytes, false);

        var appearance = WaterAppearance.FromVisualProperties(props, null);

        Assert.NotNull(appearance);
        Assert.Equal((R: (byte)0, G: (byte)0, B: (byte)0), appearance.Shallow);
        Assert.Equal(appearance.Shallow, appearance.Deep);
        Assert.Equal((R: 0f, G: 0f, B: 0f), appearance.Fallout76VisualData!.Value.BaseColor);
    }

    [Fact]
    public void ParseWater_ExactFo76DnamDispatchesToTheTypedModernLayout()
    {
        var water = ParseFallout76Water(Convert.FromHexString(RetailDnamHex));

        Assert.NotNull(water.VisualProperties);
        var visual = Assert.IsType<Fallout76WaterVisualData>(
            water.VisualProperties["Fallout76VisualData"]);
        Assert.Equal(85.9999924f, visual.DepthAmount);
        Assert.Equal((R: 0f, G: 0f, B: 1f), visual.ChannelOpacity);
        Assert.Equal(335f, visual.Layer1.UvScale);
        Assert.NotNull(WaterAppearance.FromWaterRecord(water));
    }

    [Fact]
    public void ParseWater_UnknownFo76DnamLengthDoesNotFallThroughToClassicDecoder()
    {
        var classicSizedPayload = new byte[196];
        // These would become visible classic shallow/deep colors if the old un-gated fallback ran.
        BinaryPrimitives.WriteUInt32LittleEndian(classicSizedPayload.AsSpan(40, 4), 0x00_33_22_11u);
        BinaryPrimitives.WriteUInt32LittleEndian(classicSizedPayload.AsSpan(44, 4), 0x00_66_55_44u);

        var water = ParseFallout76Water(classicSizedPayload);

        Assert.Null(water.VisualProperties);
        Assert.Null(WaterAppearance.FromWaterRecord(water));
    }

    [Theory]
    [InlineData(108)]
    [InlineData(147)]
    [InlineData(149)]
    public void ReadFallout76WaterData_RejectsUnknownLengths(int length)
    {
        var retail = Convert.FromHexString(RetailDnamHex);
        var candidate = new byte[length];
        retail.AsSpan(0, Math.Min(retail.Length, candidate.Length)).CopyTo(candidate);

        Assert.Empty(MiscEnvironmentHandler.ReadFallout76WaterData(candidate, false));
        Assert.Null(MiscEnvironmentHandler.TryReadFallout76WaterVisualData(candidate, false));
    }

    [Fact]
    public void ReadFallout76WaterData_RejectsBigEndianSyntheticPayload()
    {
        var bytes = Convert.FromHexString(RetailDnamHex);

        Assert.Empty(MiscEnvironmentHandler.ReadFallout76WaterData(bytes, true));
        Assert.Null(MiscEnvironmentHandler.TryReadFallout76WaterVisualData(bytes, true));
    }

    [Theory]
    [InlineData(35, 1u)] // RGB8 pad/fingerprint must remain zero.
    [InlineData(16, 0x7FC00000u)] // NaN base-color channel.
    [InlineData(144, 0x40000000u)] // Terminal layout fingerprint must be exactly 1.0f.
    public void ReadFallout76WaterData_RejectsMalformedLayoutFingerprints(
        int offset,
        uint replacement)
    {
        var bytes = Convert.FromHexString(RetailDnamHex);
        if (offset == 35)
            bytes[offset] = checked((byte)replacement);
        else
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, sizeof(uint)), replacement);

        Assert.Empty(MiscEnvironmentHandler.ReadFallout76WaterData(bytes, false));
        Assert.Null(MiscEnvironmentHandler.TryReadFallout76WaterVisualData(bytes, false));
    }

    private static void AssertLayer(
        WaterNoiseLayer actual,
        float uvScale,
        float windDirection,
        float windSpeed,
        float amplitude,
        float falloff)
    {
        Assert.Equal(uvScale, actual.UvScale);
        Assert.Equal(windDirection, actual.WindDirDegrees);
        Assert.Equal(windSpeed, actual.WindSpeed);
        Assert.Equal(amplitude, actual.AmpScale);
        Assert.Equal(falloff, actual.Falloff);
    }

    private static WaterRecord ParseFallout76Water(byte[] dnam)
    {
        var data = new byte[6 + dnam.Length];
        Encoding.ASCII.GetBytes("DNAM").CopyTo(data, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4, 2), checked((ushort)dnam.Length));
        dnam.CopyTo(data, 6);

        const int headerSize = 24;
        var file = new byte[headerSize + data.Length];
        data.CopyTo(file, headerSize);
        var record = new DetectedMainRecord("WATR", (uint)data.Length, 0, 0x0100_1234, 0, false)
        {
            HeaderSize = headerSize
        };
        var scan = new EsmRecordScanResult
        {
            Game = BethesdaGame.Fallout76,
            MainRecords = [record]
        };
        var context = new RecordParserContext(
            scan,
            null,
            new ByteArrayMemoryAccessor(file),
            file.Length,
            null);

        return Assert.Single(new MiscEnvironmentHandler(context).ParseWater());
    }
}
