using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.Reflection;
using BethesdaMultitool.Core.Formats.Esm.Parsing.Reflection;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Parsing.Reflection;

public sealed class StarfieldWeatherSettingsProjectorTests
{
    private const uint TypeRef = 0xFFFFFF05;
    private const uint TypeUInt32 = 0xFFFFFF0D;

    [Fact]
    public void Project_FullObject_ExtractsProvenWeatherFieldsAndAllTenColors()
    {
        var ok = StarfieldWeatherSettingsProjector.TryProject(
            BuildFullObject(), StarfieldWeatherSettingsPayloadKind.FullObject,
            out var patch, out var error);

        Assert.True(ok, error);
        Assert.NotNull(patch);
        Assert.True(patch.ParentFormId.HasValue);
        Assert.Equal(0u, patch.ParentFormId.Value);
        Assert.Equal(0x00001001u, patch.DisplayNameKeywordFormId);
        Assert.Equal(3u, patch.WeatherChoice?.Weight);
        Assert.Equal(0x00002001u, patch.ImageSpaceFormId);
        Assert.Equal(0x00002002u, patch.ImageSpaceNightFormId);
        Assert.Equal(0x00002003u, patch.VolumetricLightingFormId);
        Assert.Equal(0x00002004u, patch.CloudsFormId);
        Assert.Equal(0x00002005u, patch.PrecipitationEffectFormId);
        Assert.Equal(0x00002006u, patch.OptionalPhotoModeEffectFormId);
        Assert.Equal(0x00002007u, patch.LensFlareFormId);
        Assert.Equal(0.75f, patch.LensFlareCloudOcclusionStrength);
        Assert.Equal(0x00002008u, patch.WindForceFormId);
        Assert.Equal("Set", patch.WindDirectionRange?.Operation);
        Assert.Equal(15f, patch.WindDirectionRange?.Value);
        Assert.Equal(0.25f, patch.WindDirectionRange?.BlendAmount);
        Assert.Equal(1.5f, patch.WindTurbulence?.Value);
        Assert.True(patch.WindDirectionOverrideEnabled);
        Assert.Equal(90f, patch.WindDirectionOverrideValue?.Value);
        Assert.Equal(0.125f, patch.TransDelta);
        Assert.Equal(2f, patch.VolatilityMultiplier?.Value);
        Assert.Equal(0.5f, patch.VisibilityMultiplier?.Value);

        var colors = Assert.IsType<StarfieldWeatherColorSettingsPatch>(patch.Colors);
        StarfieldBlendableColorPatch?[] allColors =
        [
            colors.EffectLighting,
            colors.FogFar,
            colors.FogFarHigh,
            colors.FogNear,
            colors.FogNearHigh,
            colors.Sun,
            colors.SunGlare,
            colors.Sunlight,
            colors.MoonGlare,
            colors.Moonlight
        ];
        Assert.All(allColors, color =>
        {
            Assert.NotNull(color);
            Assert.Equal("Set", color.Operation);
            Assert.Equal(0.5f, color.BlendAmount);
            Assert.Equal(0.1f, color.Value?.X);
            Assert.Equal(0.2f, color.Value?.Y);
            Assert.Equal(0.3f, color.Value?.Z);
            Assert.Equal(1f, color.Value?.W);
        });
    }

    [Fact]
    public void Project_Diff_PreservesExplicitZeroFalseAndNestedPartialColor()
    {
        var reflected = Object("BGSWeatherSettingsForm",
            ("pParent", Reference(0)),
            ("WeatherChoice", ObjectValue(Object("BGSWeatherSettingsForm::WeatherChoiceSettings",
                ("Weight", new BethesdaReflectionUnsignedValue(0))))),
            ("Colors", ObjectValue(Object("BGSWeatherSettingsForm::ColorSettings",
                ("Sunlight", ObjectValue(Object("BSBlendable::ColorValue",
                    ("Value", ObjectValue(Object("XMFLOAT4",
                        ("z", new BethesdaReflectionFloatValue(0))))))))))),
            ("WindDirectionOverrideEnabled", new BethesdaReflectionBoolValue(false)),
            ("TransDelta", new BethesdaReflectionFloatValue(0)),
            ("VisibilityMultiplier", ObjectValue(Object("BSBlendable::FloatValue",
                ("Value", new BethesdaReflectionFloatValue(0))))));

        var ok = StarfieldWeatherSettingsProjector.TryProject(
            reflected, StarfieldWeatherSettingsPayloadKind.Diff, out var patch, out var error);

        Assert.True(ok, error);
        Assert.NotNull(patch);
        Assert.True(patch.ParentFormId.HasValue);
        Assert.Equal(0u, patch.ParentFormId.Value);
        Assert.True(patch.WeatherChoice?.Weight.HasValue);
        Assert.Equal(0u, patch.WeatherChoice!.Weight!.Value);
        Assert.Null(patch.ImageSpaceFormId);
        Assert.True(patch.WindDirectionOverrideEnabled.HasValue);
        Assert.False(patch.WindDirectionOverrideEnabled.Value);
        Assert.True(patch.TransDelta.HasValue);
        Assert.Equal(0f, patch.TransDelta.Value);
        Assert.True(patch.VisibilityMultiplier?.Value.HasValue);
        Assert.Equal(0f, patch.VisibilityMultiplier!.Value!.Value);
        Assert.Null(patch.VisibilityMultiplier.BlendAmount);

        var sunlight = Assert.IsType<StarfieldBlendableColorPatch>(patch.Colors?.Sunlight);
        Assert.Null(sunlight.Operation);
        Assert.Null(sunlight.BlendAmount);
        Assert.Null(sunlight.Value?.X);
        Assert.Null(sunlight.Value?.Y);
        Assert.True(sunlight.Value?.Z.HasValue);
        Assert.Equal(0f, sunlight.Value!.Z!.Value);
        Assert.Null(sunlight.Value.W);
        Assert.Null(patch.Colors?.FogFar);
    }

    [Fact]
    public void Project_FullObject_RejectsSparseDiffLikeObject()
    {
        var reflected = Object("BGSWeatherSettingsForm", ("pParent", Reference(0)));

        Assert.False(StarfieldWeatherSettingsProjector.TryProject(
            reflected, StarfieldWeatherSettingsPayloadKind.FullObject,
            out var patch, out var error));
        Assert.Null(patch);
        Assert.Contains("pDisplayNameKeyword", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Project_Diff_RejectsWrongKnownFieldType()
    {
        var reflected = Object("BGSWeatherSettingsForm",
            ("pClouds", new BethesdaReflectionUnsignedValue(0x1234)));

        Assert.False(StarfieldWeatherSettingsProjector.TryProject(
            reflected, StarfieldWeatherSettingsPayloadKind.Diff,
            out var patch, out var error));
        Assert.Null(patch);
        Assert.Contains("Ref<UInt32>", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Project_RejectsWrongRootClass()
    {
        var reflected = Object("BGSWeatherForm");

        Assert.False(StarfieldWeatherSettingsProjector.TryProject(
            reflected, StarfieldWeatherSettingsPayloadKind.Diff,
            out var patch, out var error));
        Assert.Null(patch);
        Assert.Contains("BGSWeatherSettingsForm", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Decode_Diff_EnforcesDiffChunkAndPreservesExplicitZeroReference()
    {
        var stream = BuildParentOnlyDiffStream("BGSWeatherSettingsForm");

        Assert.True(StarfieldWeatherSettingsDecoder.TryDecode(
            stream, StarfieldWeatherSettingsPayloadKind.Diff, out var patch, out var error), error);
        Assert.NotNull(patch);
        Assert.True(patch.ParentFormId.HasValue);
        Assert.Equal(0u, patch.ParentFormId.Value);

        Assert.False(StarfieldWeatherSettingsDecoder.TryDecode(
            stream, StarfieldWeatherSettingsPayloadKind.FullObject,
            out var wrongKindPatch, out var wrongKindError));
        Assert.Null(wrongKindPatch);
        Assert.Contains("unexpected", wrongKindError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Decode_RejectsWrongReflectedRootClass()
    {
        var stream = BuildParentOnlyDiffStream("NotWeatherSettings");

        Assert.False(StarfieldWeatherSettingsDecoder.TryDecode(
            stream, StarfieldWeatherSettingsPayloadKind.Diff, out var patch, out var error));
        Assert.Null(patch);
        Assert.Contains("expected class", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Decode_RejectsUnknownPayloadKindBeforeReadingStream()
    {
        Assert.False(StarfieldWeatherSettingsDecoder.TryDecode(
            [], StarfieldWeatherSettingsPayloadKind.Unknown, out var patch, out var error));
        Assert.Null(patch);
        Assert.Contains("payload kind", error, StringComparison.OrdinalIgnoreCase);
    }

    private static BethesdaReflectionObject BuildFullObject()
    {
        return Object("BGSWeatherSettingsForm",
            ("pParent", Reference(0)),
            ("pDisplayNameKeyword", Reference(0x00001001)),
            ("WeatherChoice", ObjectValue(Object("BGSWeatherSettingsForm::WeatherChoiceSettings",
                ("Weight", new BethesdaReflectionUnsignedValue(3))))),
            ("pImageSpace", Reference(0x00002001)),
            ("pImageSpaceNight", Reference(0x00002002)),
            ("pVolumeticLighting", Reference(0x00002003)),
            ("pClouds", Reference(0x00002004)),
            ("Colors", ObjectValue(BuildFullColors())),
            ("pPrecipitationEffect", Reference(0x00002005)),
            ("pOptionalPhotoModeEffect", Reference(0x00002006)),
            ("pLensFlare", Reference(0x00002007)),
            ("LensFlareCloudOcclusionStrength", new BethesdaReflectionFloatValue(0.75)),
            ("pWindForce", Reference(0x00002008)),
            ("WindDirectionRange", BlendableFloat(15, 0.25)),
            ("WindTurbulence", BlendableFloat(1.5, 0.5)),
            ("WindDirectionOverrideEnabled", new BethesdaReflectionBoolValue(true)),
            ("WindDirectionOverrideValue", BlendableFloat(90, 1)),
            ("TransDelta", new BethesdaReflectionFloatValue(0.125)),
            ("VolatilityMultiplier", BlendableFloat(2, 0)),
            ("VisibilityMultiplier", BlendableFloat(0.5, 0)));
    }

    private static BethesdaReflectionObject BuildFullColors()
    {
        return Object("BGSWeatherSettingsForm::ColorSettings",
            ("EffectLighting", BlendableColor()),
            ("FogFar", BlendableColor()),
            ("FogFarHigh", BlendableColor()),
            ("FogNear", BlendableColor()),
            ("FogNearHigh", BlendableColor()),
            ("Sun", BlendableColor()),
            ("SunGlare", BlendableColor()),
            ("Sunlight", BlendableColor()),
            ("MoonGlare", BlendableColor()),
            ("Moonlight", BlendableColor()));
    }

    private static BethesdaReflectionValue BlendableColor()
    {
        return ObjectValue(Object("BSBlendable::ColorValue",
            ("Op", new BethesdaReflectionStringValue("Set")),
            ("Value", ObjectValue(Object("XMFLOAT4",
                ("x", new BethesdaReflectionFloatValue(0.1)),
                ("y", new BethesdaReflectionFloatValue(0.2)),
                ("z", new BethesdaReflectionFloatValue(0.3)),
                ("w", new BethesdaReflectionFloatValue(1))))),
            ("BlendAmount", new BethesdaReflectionFloatValue(0.5))));
    }

    private static BethesdaReflectionValue BlendableFloat(double value, double blendAmount)
    {
        return ObjectValue(Object("BSBlendable::FloatValue",
            ("Op", new BethesdaReflectionStringValue("Set")),
            ("Value", new BethesdaReflectionFloatValue(value)),
            ("BlendAmount", new BethesdaReflectionFloatValue(blendAmount))));
    }

    private static BethesdaReflectionReferenceValue Reference(uint value)
    {
        return new BethesdaReflectionReferenceValue(
            "UInt32", new BethesdaReflectionUnsignedValue(value));
    }

    private static BethesdaReflectionObjectValue ObjectValue(BethesdaReflectionObject value)
    {
        return new BethesdaReflectionObjectValue(value);
    }

    private static BethesdaReflectionObject Object(
        string typeName,
        params (string Name, BethesdaReflectionValue Value)[] fields)
    {
        return new BethesdaReflectionObject(
            typeName,
            fields.ToDictionary(field => field.Name, field => field.Value, StringComparer.Ordinal));
    }

    private static byte[] BuildParentOnlyDiffStream(string rootType)
    {
        var strings = new List<byte>();
        var rootOffset = AddString(strings, rootType);
        var parentOffset = AddString(strings, "pParent");
        var type = Chunk("TYPE", U32(1));
        var clas = Chunk("CLAS", Concat(
            U32(rootOffset), U32(0), U16(0), U16(1),
            U32(parentOffset), U32(TypeRef), U16(0), U16(0)));
        var diff = Chunk("DIFF", Concat(
            U32(rootOffset), U16(0), U32(TypeUInt32), U32(0), U16(ushort.MaxValue)));
        byte[][] chunks = [type, clas, diff];
        return Concat(
            Encoding.ASCII.GetBytes("BETH"), U32(8), U32(4), U32((uint)chunks.Length + 2),
            Encoding.ASCII.GetBytes("STRT"), U32((uint)strings.Count), [.. strings],
            Concat(chunks));
    }

    private static uint AddString(List<byte> strings, string value)
    {
        var offset = checked((uint)strings.Count);
        strings.AddRange(Encoding.ASCII.GetBytes(value));
        strings.Add(0);
        return offset;
    }

    private static byte[] Chunk(string signature, byte[] body)
    {
        return Concat(Encoding.ASCII.GetBytes(signature), U32((uint)body.Length), body);
    }

    private static byte[] U32(uint value) => BitConverter.GetBytes(value);

    private static byte[] U16(ushort value) => BitConverter.GetBytes(value);

    private static byte[] Concat(params byte[][] parts)
    {
        var bytes = new List<byte>();
        foreach (var part in parts) bytes.AddRange(part);
        return [.. bytes];
    }
}
