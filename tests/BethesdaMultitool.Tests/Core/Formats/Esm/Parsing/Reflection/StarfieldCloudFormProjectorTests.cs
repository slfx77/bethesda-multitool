using BethesdaMultitool.Core.Formats.Esm.Models.Reflection;
using BethesdaMultitool.Core.Formats.Esm.Parsing.Reflection;
using Xunit;
using static BethesdaMultitool.Tests.Core.Formats.Esm.Parsing.Reflection.StarfieldReflectionTestStreamBuilder;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Parsing.Reflection;

public sealed class StarfieldCloudFormProjectorTests
{
    private const string RootType = "BGSCloudForm";
    private const string ShadowType = "BGSCloudForm::ShadowParams";
    private const string LayerType = "BGSCloudForm::CloudLayer";
    private const string PlaneType = "BGSCloudForm::CloudPlane";
    private const string TintType = "XMCOLOR";

    private const uint TypeUInt8 = 0xFFFFFF09;

    [Fact]
    public void Project_FullRetailShape_PreservesVisualFieldsAndXmcColorOrder()
    {
        var ok = StarfieldCloudFormProjector.TryProject(
            BuildFullObject(), out var definition, out var error);

        Assert.True(ok, error);
        Assert.NotNull(definition);
        Assert.False(definition.Shadows.Enabled);
        Assert.Equal(string.Empty, definition.Shadows.OpacityTexture);
        Assert.Equal(0f, definition.Shadows.Strength);
        Assert.Equal(0u, definition.CloudCardSequenceFormId);

        var layer = Assert.Single(definition.Layers);
        Assert.Equal("CloudLayer_A", layer.Name);
        Assert.Equal(@"Data\Textures\Sky\Clouds\layer_color.dds", layer.ColorTexture);
        Assert.Equal(string.Empty, layer.ThicknessTexture);
        Assert.Equal(12.5f, layer.ElevationKm);
        Assert.Equal(4u, layer.Tiling);
        Assert.Equal(2u, layer.VerticalTiling);
        Assert.Equal(0.75f, layer.WindScale);
        Assert.Equal(0f, layer.Density);
        Assert.Equal(0.625f, layer.Coverage);
        Assert.Equal((byte)1, layer.Tint.R);
        Assert.Equal((byte)2, layer.Tint.G);
        Assert.Equal((byte)3, layer.Tint.B);
        Assert.Equal((byte)4, layer.Tint.A);

        var plane = Assert.Single(definition.Planes);
        Assert.Equal(@"Data\Textures\Sky\Clouds\plane_opacity.dds", plane.OpacityTexture);
        Assert.Equal(3.25f, plane.ElevationKm);
        Assert.Equal(8f, plane.TilingPerKm);
        Assert.Equal(0f, plane.WindScale);
        Assert.Equal(0.25f, plane.Density);
        Assert.Equal(1f, plane.Coverage);
        Assert.Equal((byte)250, plane.Tint.R);
        Assert.Equal((byte)128, plane.Tint.G);
        Assert.Equal((byte)64, plane.Tint.B);
        Assert.Equal((byte)0, plane.Tint.A);
    }

    [Fact]
    public void Project_RejectsSparseObjectAndUnknownExactSchemaField()
    {
        var sparse = Object(RootType,
            ("pCloudCardSequence", Reference(0)));

        Assert.False(StarfieldCloudFormProjector.TryProject(
            sparse, out var sparseDefinition, out var sparseError));
        Assert.Null(sparseDefinition);
        Assert.Contains("requires exactly 4", sparseError, StringComparison.Ordinal);

        var fields = BuildFullObject().Fields.ToDictionary(
            item => item.Key, item => item.Value, StringComparer.Ordinal);
        fields.Remove("Planes");
        fields.Add("CloudPlanes", List(PlaneType, BuildPlane()));
        var unknown = new BethesdaReflectionObject(RootType, fields);

        Assert.False(StarfieldCloudFormProjector.TryProject(
            unknown, out var unknownDefinition, out var unknownError));
        Assert.Null(unknownDefinition);
        Assert.Contains("unknown field 'CloudPlanes'", unknownError, StringComparison.Ordinal);
    }

    [Fact]
    public void Project_RejectsMalformedListTypeAndElementObject()
    {
        var wrongListType = ReplaceField(
            BuildFullObject(), "Layers", List(PlaneType, BuildPlane()));

        Assert.False(StarfieldCloudFormProjector.TryProject(
            wrongListType, out var wrongListDefinition, out var wrongListError));
        Assert.Null(wrongListDefinition);
        Assert.Contains($"List<{LayerType}>", wrongListError, StringComparison.Ordinal);

        var wrongElement = ReplaceField(
            BuildFullObject(), "Layers", List(LayerType, BuildPlane()));

        Assert.False(StarfieldCloudFormProjector.TryProject(
            wrongElement, out var wrongElementDefinition, out var wrongElementError));
        Assert.Null(wrongElementDefinition);
        Assert.Contains("Layers[0]", wrongElementError, StringComparison.Ordinal);
    }

    [Fact]
    public void Project_RejectsNonFiniteFloatAndOutOfRangeXmcColor()
    {
        var invalidLayer = ReplaceField(
            BuildLayer(), "Density", new BethesdaReflectionFloatValue(double.NaN));
        var invalidFloatRoot = ReplaceField(
            BuildFullObject(), "Layers", List(LayerType, invalidLayer));

        Assert.False(StarfieldCloudFormProjector.TryProject(
            invalidFloatRoot, out var floatDefinition, out var floatError));
        Assert.Null(floatDefinition);
        Assert.Contains("finite Float", floatError, StringComparison.Ordinal);

        var invalidTint = ReplaceField(
            BuildTint(1, 2, 3, 4), "r", new BethesdaReflectionUnsignedValue(256));
        var invalidTintLayer = ReplaceField(
            BuildLayer(), "Tint", new BethesdaReflectionObjectValue(invalidTint));
        var invalidTintRoot = ReplaceField(
            BuildFullObject(), "Layers", List(LayerType, invalidTintLayer));

        Assert.False(StarfieldCloudFormProjector.TryProject(
            invalidTintRoot, out var tintDefinition, out var tintError));
        Assert.Null(tintDefinition);
        Assert.Contains("UInt8", tintError, StringComparison.Ordinal);
    }

    [Fact]
    public void Decode_FullObject_UsesGenericReaderAndPreservesExplicitDefaults()
    {
        var stream = BuildCloudFormStream();

        Assert.True(StarfieldCloudFormDecoder.TryDecode(
            stream, out var definition, out var error), error);
        Assert.NotNull(definition);
        Assert.False(definition.Shadows.Enabled);
        Assert.Equal(string.Empty, definition.Shadows.OpacityTexture);
        Assert.Equal(0f, definition.Shadows.TilingPerKm);
        Assert.Empty(definition.Layers);
        Assert.Empty(definition.Planes);
        Assert.Equal(0u, definition.CloudCardSequenceFormId);
    }

    [Fact]
    public void Decode_RejectsDiffAndMissingOutOfLineList()
    {
        var diff = BuildCloudFormStream(objectChunk: "DIFF");

        Assert.False(StarfieldCloudFormDecoder.TryDecode(
            diff, out var diffDefinition, out var diffError));
        Assert.Null(diffDefinition);
        Assert.Contains("unexpected", diffError, StringComparison.OrdinalIgnoreCase);

        var missingList = BuildCloudFormStream(includePlaneList: false);

        Assert.False(StarfieldCloudFormDecoder.TryDecode(
            missingList, out var listDefinition, out var listError));
        Assert.Null(listDefinition);
        Assert.Contains("LIST", listError, StringComparison.Ordinal);
    }

    [Fact]
    public void Decode_RejectsWrongRetailScalarTypeEvenWhenListsAreEmpty()
    {
        var wrongScalarType = BuildCloudFormStream(layerTilingType: TypeUInt8);

        Assert.False(StarfieldCloudFormDecoder.TryDecode(
            wrongScalarType, out var definition, out var error));
        Assert.Null(definition);
        Assert.Contains("Tiling:UInt8", error, StringComparison.Ordinal);
        Assert.Contains("Tiling:UInt32", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Decode_RejectsImpossibleListCountBeforeGenericReaderAllocation()
    {
        var malformedList = BuildCloudFormStream(layerListCount: uint.MaxValue);

        Assert.False(StarfieldCloudFormDecoder.TryDecode(
            malformedList, out var definition, out var error));
        Assert.Null(definition);
        Assert.Contains("cannot fit", error, StringComparison.Ordinal);
    }

    private static BethesdaReflectionObject BuildFullObject()
    {
        return Object(RootType,
            ("Shadows", new BethesdaReflectionObjectValue(Object(ShadowType,
                ("Enabled", new BethesdaReflectionBoolValue(false)),
                ("OpacityTexture", new BethesdaReflectionStringValue(string.Empty)),
                ("TilingPerKm", F(0)),
                ("ElevationKm", F(2)),
                ("Strength", F(0)),
                ("WindScale", F(1.25))))),
            ("Layers", List(LayerType, BuildLayer())),
            ("Planes", List(PlaneType, BuildPlane())),
            ("pCloudCardSequence", Reference(0)));
    }

    private static BethesdaReflectionObject BuildLayer()
    {
        return Object(LayerType,
            ("Name", S("CloudLayer_A")),
            ("ColorTexture", S(@"Data\Textures\Sky\Clouds\layer_color.dds")),
            ("ThicknessTexture", S(string.Empty)),
            ("NormalTexture", S(@"Data\Textures\Sky\Clouds\layer_normal.dds")),
            ("OpacityTexture", S(@"Data\Textures\Sky\Clouds\layer_opacity.dds")),
            ("ElevationKm", F(12.5)),
            ("HeightKm", F(3)),
            ("DistanceKm", F(100)),
            ("Thickness", F(0.5)),
            ("TextureShadowOffset", F(0.1)),
            ("TextureShadowStrength", F(0.2)),
            ("NormalShadowStrength", F(0.3)),
            ("Tiling", U(4)),
            ("VerticalTiling", U(2)),
            ("TopBlendDistanceKm", F(8)),
            ("TopBlendStartKm", F(5)),
            ("BottomBlendDistanceKm", F(7)),
            ("BottomBlendStartKm", F(4)),
            ("WindScale", F(0.75)),
            ("Density", F(0)),
            ("Coverage", F(0.625)),
            ("AlphaAdd", F(0)),
            ("AlphaMultiply", F(1)),
            ("Tint", new BethesdaReflectionObjectValue(BuildTint(1, 2, 3, 4))));
    }

    private static BethesdaReflectionObject BuildPlane()
    {
        return Object(PlaneType,
            ("Name", S("CloudPlane_A")),
            ("ColorTexture", S(@"Data\Textures\Sky\Clouds\plane_color.dds")),
            ("ThicknessTexture", S(@"Data\Textures\Sky\Clouds\plane_thickness.dds")),
            ("NormalTexture", S(@"Data\Textures\Sky\Clouds\plane_normal.dds")),
            ("OpacityTexture", S(@"Data\Textures\Sky\Clouds\plane_opacity.dds")),
            ("ElevationKm", F(3.25)),
            ("FadeStartKm", F(20)),
            ("FadeDistanceKm", F(10)),
            ("Thickness", F(0.25)),
            ("TextureShadowOffset", F(0.4)),
            ("TextureShadowStrength", F(0.5)),
            ("NormalShadowStrength", F(0.6)),
            ("TilingPerKm", F(8)),
            ("WindScale", F(0)),
            ("Density", F(0.25)),
            ("Coverage", F(1)),
            ("AlphaAdd", F(0)),
            ("AlphaMultiply", F(1)),
            ("Tint", new BethesdaReflectionObjectValue(BuildTint(250, 128, 64, 0))));
    }

    private static BethesdaReflectionObject BuildTint(byte r, byte g, byte b, byte a)
    {
        return Object(TintType,
            ("r", U(r)),
            ("g", U(g)),
            ("b", U(b)),
            ("a", U(a)));
    }

    private static BethesdaReflectionObject ReplaceField(
        BethesdaReflectionObject source,
        string fieldName,
        BethesdaReflectionValue value)
    {
        var fields = source.Fields.ToDictionary(
            item => item.Key, item => item.Value, StringComparer.Ordinal);
        fields[fieldName] = value;
        return new BethesdaReflectionObject(source.TypeName, fields);
    }

    private static BethesdaReflectionObject Object(
        string typeName,
        params (string Name, BethesdaReflectionValue Value)[] fields)
    {
        return new BethesdaReflectionObject(
            typeName,
            fields.ToDictionary(field => field.Name, field => field.Value, StringComparer.Ordinal));
    }

    private static BethesdaReflectionListValue List(
        string elementType,
        params BethesdaReflectionObject[] values)
    {
        return new BethesdaReflectionListValue(
            elementType,
            Array.AsReadOnly(values.Select(value =>
                (BethesdaReflectionValue)new BethesdaReflectionObjectValue(value)).ToArray()));
    }

    private static BethesdaReflectionReferenceValue Reference(uint value) =>
        new("UInt32", new BethesdaReflectionUnsignedValue(value));

    private static BethesdaReflectionStringValue S(string value) => new(value);
    private static BethesdaReflectionFloatValue F(double value) => new(value);
    private static BethesdaReflectionUnsignedValue U(ulong value) => new(value);
}
