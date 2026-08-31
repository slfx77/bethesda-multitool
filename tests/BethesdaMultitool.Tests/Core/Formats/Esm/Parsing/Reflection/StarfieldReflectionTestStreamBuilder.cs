using BethesdaMultitool.Core.Formats.Esm.Parsing;
using System.Text;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Parsing.Reflection;

/// <summary>
///     Builds structurally valid standalone Starfield reflection streams shared by projector and
///     full <c>RecordParser</c> tests. Keeping the binary schema literals here prevents handler-boundary
///     coverage from drifting away from the decoder fixtures it is intended to exercise.
/// </summary>
internal static class StarfieldReflectionTestStreamBuilder
{
    private const uint TypeString = 0xFFFFFF02;
    private const uint TypeList = 0xFFFFFF03;
    private const uint TypeRef = 0xFFFFFF05;
    private const uint TypeUInt8 = 0xFFFFFF09;
    private const uint TypeUInt32 = 0xFFFFFF0D;
    private const uint TypeBool = 0xFFFFFF10;
    private const uint TypeFloat = 0xFFFFFF11;
    private const uint TypeDouble = 0xFFFFFF12;

    internal static byte[] BuildValidVolumetricLightingStream(
        float[]? values = null,
        bool omitScatteringVolumeFar = false,
        bool scatteringVolumeNearIsUInt32 = false,
        string rootType = "BGSVolumetricLighting",
        bool scatteringVolumeFarIsDouble = false,
        bool appendListSideChunk = false)
    {
        values ??= Enumerable.Range(1, 32).Select(value => (float)value).ToArray();
        if (values.Length != 32)
        {
            throw new ArgumentException("VOLI test streams require exactly 32 float values.", nameof(values));
        }

        var schema = BuildVolumetricLightingSchema(
            rootType,
            omitScatteringVolumeFar,
            scatteringVolumeNearIsUInt32,
            scatteringVolumeFarIsDouble);
        var serializedValues = omitScatteringVolumeFar
            ? values.Where((_, index) => index != 1)
            : values;
        var body = new List<byte>(sizeof(uint) + (32 * sizeof(float)));
        body.AddRange(U32(schema.Offsets[rootType]));
        foreach (var value in serializedValues)
        {
            body.AddRange(F32(value));
        }

        var chunks = new List<byte[]>(schema.Chunks)
        {
            Chunk("OBJT", [.. body])
        };
        if (appendListSideChunk)
        {
            chunks.Add(Chunk("LIST", Concat(U32(TypeFloat), U32(0))));
        }

        return ReflectionStream(schema.StringTable, chunks);
    }

    internal static byte[] BuildVolumetricLightingDiffStream()
    {
        var schema = BuildVolumetricLightingSchema("BGSVolumetricLighting", false, false, false);
        var chunks = new List<byte[]>(schema.Chunks)
        {
            Chunk("DIFF", Concat(
                U32(schema.Offsets["BGSVolumetricLighting"]), U16(ushort.MaxValue)))
        };
        return ReflectionStream(schema.StringTable, chunks);
    }

    internal static byte[] BuildCloudFormStream(
        string objectChunk = "OBJT",
        bool includePlaneList = true,
        uint layerTilingType = TypeUInt32,
        uint layerListCount = 0,
        int shadowOpacityTextureLength = 0)
    {
        if (shadowOpacityTextureLength is < 0 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(shadowOpacityTextureLength),
                "Reflected strings use a UInt16 byte length.");
        }

        const string rootType = "BGSCloudForm";
        const string shadowType = "BGSCloudForm::ShadowParams";
        const string layerType = "BGSCloudForm::CloudLayer";
        const string planeType = "BGSCloudForm::CloudPlane";
        const string tintType = "XMCOLOR";

        var strings = new List<byte>();
        var tokens = new Dictionary<string, uint>(StringComparer.Ordinal);
        uint Token(string value)
        {
            if (tokens.TryGetValue(value, out var existing))
            {
                return existing;
            }

            var token = checked((uint)strings.Count);
            strings.AddRange(Encoding.ASCII.GetBytes(value));
            strings.Add(0);
            tokens.Add(value, token);
            return token;
        }

        string[] names =
        [
            rootType, shadowType, layerType, planeType, tintType,
            "Shadows", "Layers", "Planes", "pCloudCardSequence",
            "Enabled", "OpacityTexture", "TilingPerKm", "ElevationKm", "Strength", "WindScale",
            "r", "g", "b", "a",
            "Name", "ColorTexture", "ThicknessTexture", "NormalTexture", "HeightKm", "DistanceKm",
            "Thickness", "TextureShadowOffset", "TextureShadowStrength", "NormalShadowStrength",
            "Tiling", "VerticalTiling", "TopBlendDistanceKm", "TopBlendStartKm",
            "BottomBlendDistanceKm", "BottomBlendStartKm", "Density", "Coverage", "AlphaAdd",
            "AlphaMultiply", "Tint", "FadeStartKm", "FadeDistanceKm"
        ];
        foreach (var name in names)
        {
            Token(name);
        }

        byte[] Class(string name, ushort flags, params (string Name, uint Type)[] fields)
        {
            var body = new List<byte>();
            body.AddRange(U32(Token(name)));
            body.AddRange(U32(0));
            body.AddRange(U16(flags));
            body.AddRange(U16(checked((ushort)fields.Length)));
            foreach (var field in fields)
            {
                body.AddRange(U32(Token(field.Name)));
                body.AddRange(U32(field.Type));
                body.AddRange(U32(0));
            }

            return Chunk("CLAS", [.. body]);
        }

        var shadowOpacityTexture = Enumerable.Repeat(
            (byte)'x', shadowOpacityTextureLength).ToArray();
        byte[][] chunks =
        [
            Chunk("TYPE", U32(5)),
            Class(shadowType, 0,
                ("Enabled", TypeBool), ("OpacityTexture", TypeString), ("TilingPerKm", TypeFloat),
                ("ElevationKm", TypeFloat), ("Strength", TypeFloat), ("WindScale", TypeFloat)),
            Class(tintType, 8,
                ("r", TypeUInt8), ("g", TypeUInt8), ("b", TypeUInt8), ("a", TypeUInt8)),
            Class(rootType, 0,
                ("Shadows", Token(shadowType)), ("Layers", TypeList), ("Planes", TypeList),
                ("pCloudCardSequence", TypeRef)),
            Class(layerType, 0,
                ("Name", TypeString), ("ColorTexture", TypeString), ("ThicknessTexture", TypeString),
                ("NormalTexture", TypeString), ("OpacityTexture", TypeString),
                ("ElevationKm", TypeFloat), ("HeightKm", TypeFloat), ("DistanceKm", TypeFloat),
                ("Thickness", TypeFloat), ("TextureShadowOffset", TypeFloat),
                ("TextureShadowStrength", TypeFloat), ("NormalShadowStrength", TypeFloat),
                ("Tiling", layerTilingType), ("VerticalTiling", TypeUInt32),
                ("TopBlendDistanceKm", TypeFloat), ("TopBlendStartKm", TypeFloat),
                ("BottomBlendDistanceKm", TypeFloat), ("BottomBlendStartKm", TypeFloat),
                ("WindScale", TypeFloat), ("Density", TypeFloat), ("Coverage", TypeFloat),
                ("AlphaAdd", TypeFloat), ("AlphaMultiply", TypeFloat), ("Tint", Token(tintType))),
            Class(planeType, 0,
                ("Name", TypeString), ("ColorTexture", TypeString), ("ThicknessTexture", TypeString),
                ("NormalTexture", TypeString), ("OpacityTexture", TypeString),
                ("ElevationKm", TypeFloat), ("FadeStartKm", TypeFloat), ("FadeDistanceKm", TypeFloat),
                ("Thickness", TypeFloat), ("TextureShadowOffset", TypeFloat),
                ("TextureShadowStrength", TypeFloat), ("NormalShadowStrength", TypeFloat),
                ("TilingPerKm", TypeFloat), ("WindScale", TypeFloat), ("Density", TypeFloat),
                ("Coverage", TypeFloat), ("AlphaAdd", TypeFloat), ("AlphaMultiply", TypeFloat),
                ("Tint", Token(tintType))),
            Chunk(objectChunk, Concat(
                U32(Token(rootType)),
                [0], U16(checked((ushort)shadowOpacityTexture.Length)), shadowOpacityTexture,
                F32(0), F32(0), F32(0), F32(0),
                U32(TypeUInt32), U32(0))),
            Chunk("LIST", Concat(U32(Token(layerType)), U32(layerListCount)))
        ];

        if (includePlaneList)
        {
            chunks = [.. chunks, Chunk("LIST", Concat(U32(Token(planeType)), U32(0)))];
        }

        return Concat(
            Encoding.ASCII.GetBytes("BETH"), U32(8), U32(4), U32((uint)chunks.Length + 2),
            Encoding.ASCII.GetBytes("STRT"), U32((uint)strings.Count), [.. strings],
            Concat(chunks));
    }

    private static VolumetricLightingSchema BuildVolumetricLightingSchema(
        string rootType,
        bool omitScatteringVolumeFar,
        bool scatteringVolumeNearIsUInt32,
        bool scatteringVolumeFarIsDouble)
    {
        const string settings = "BGSVolumetricLightingSettings";
        const string shared = "BGSVolumetricLightingSettings::ExteriorAndInteriorSettings";
        const string exterior = "BGSVolumetricLightingSettings::ExteriorSettings";
        const string thickness = "BGSVolumetricLightingSettings::FogThicknessSettings";
        const string density = "BGSVolumetricLightingSettings::FogDensitySettings";
        const string horizon = "BGSVolumetricLightingSettings::HorizonFogSettings";
        const string fogMap = "BGSVolumetricLightingSettings::FogMapSettings";
        const string distant = "BGSVolumetricLightingSettings::DistantLightingSettings";
        const string float4 = "XMFLOAT4";

        var sharedFields = new List<(string Name, uint Type)>
        {
            ("ScatteringVolumeNear", scatteringVolumeNearIsUInt32 ? TypeUInt32 : TypeFloat)
        };
        if (!omitScatteringVolumeFar)
        {
            sharedFields.Add((
                "ScatteringVolumeFar",
                scatteringVolumeFarIsDouble ? TypeDouble : TypeFloat));
        }

        sharedFields.Add(("HighFrequencyNoiseScale", TypeFloat));
        sharedFields.Add(("HighFrequencyNoiseDensityScale", TypeFloat));

        (string Class, ushort Flags, (string Name, string? NamedType, uint BuiltInType)[] Fields)[] classes =
        [
            (float4, 8,
                [("x", null, TypeFloat), ("y", null, TypeFloat),
                    ("z", null, TypeFloat), ("w", null, TypeFloat)]),
            (thickness, 0,
                [("ThicknessNoiseScale", null, TypeFloat), ("ThicknessNoiseBias", null, TypeFloat),
                    ("MinFogThickness", null, TypeFloat), ("MaxFogThickness", null, TypeFloat)]),
            (fogMap, 0,
                [("HeightAboveTerrain", null, TypeFloat), ("TerrainMatch", null, TypeFloat),
                    ("Albedo", float4, 0), ("Anisotropy", null, TypeFloat),
                    ("MinMeanFreePath", null, TypeFloat), ("MaxMeanFreePath", null, TypeFloat),
                    ("HeightFalloffExponent", null, TypeFloat), ("Span", null, TypeFloat)]),
            (density, 0,
                [("DensityNoiseScale", null, TypeFloat), ("DensityNoiseBias", null, TypeFloat),
                    ("MinFogDensity", null, TypeFloat), ("MaxFogDensity", null, TypeFloat),
                    ("DensityStartDistance", null, TypeFloat),
                    ("DensityFullDistance", null, TypeFloat),
                    ("DensityDistanceExponent", null, TypeFloat)]),
            (shared, 0, sharedFields.Select(field =>
                (field.Name, (string?)null, field.Type)).ToArray()),
            (settings, 0,
                [("ExteriorAndInterior", shared, 0), ("Exterior", exterior, 0),
                    ("DistantLighting", distant, 0)]),
            (exterior, 0,
                [("FogThickness", thickness, 0), ("FogDensity", density, 0),
                    ("HorizonFog", horizon, 0), ("FogMap", fogMap, 0)]),
            (rootType, 0, [("Settings", settings, 0)]),
            (horizon, 0,
                [("FogThickness", null, TypeFloat), ("FogDensity", null, TypeFloat),
                    ("DensityStartDistance", null, TypeFloat),
                    ("DensityFullDistance", null, TypeFloat)]),
            (distant, 0,
                [("ScatteringTransition", null, TypeFloat), ("ScatteringFar", null, TypeFloat)])
        ];

        var names = classes.Select(item => item.Class)
            .Concat(classes.SelectMany(item => item.Fields)
                .SelectMany(field => field.NamedType is null
                    ? new[] { field.Name }
                    : new[] { field.Name, field.NamedType! }))
            .Distinct(StringComparer.Ordinal);
        var offsets = new Dictionary<string, uint>(StringComparer.Ordinal);
        var strings = new List<byte>();
        foreach (var name in names)
        {
            offsets[name] = checked((uint)strings.Count);
            strings.AddRange(Encoding.ASCII.GetBytes(name));
            strings.Add(0);
        }

        var chunks = new List<byte[]> { Chunk("TYPE", U32(checked((uint)classes.Length))) };
        foreach (var item in classes)
        {
            chunks.Add(VolumetricLightingClassChunk(
                offsets,
                item.Class,
                item.Flags,
                item.Fields.Select(field =>
                    (field.Name, field.NamedType is null
                        ? field.BuiltInType
                        : offsets[field.NamedType!])).ToArray()));
        }

        return new VolumetricLightingSchema(offsets, [.. strings], chunks);
    }

    private static byte[] VolumetricLightingClassChunk(
        IReadOnlyDictionary<string, uint> offsets,
        string className,
        ushort flags,
        params (string Name, uint Type)[] fields)
    {
        var body = new List<byte>();
        body.AddRange(U32(offsets[className]));
        body.AddRange(U32(0));
        body.AddRange(U16(flags));
        body.AddRange(U16(checked((ushort)fields.Length)));
        foreach (var field in fields)
        {
            body.AddRange(U32(offsets[field.Name]));
            body.AddRange(U32(field.Type));
            body.AddRange(U16(0));
            body.AddRange(U16(0));
        }

        return Chunk("CLAS", [.. body]);
    }

    private static byte[] ReflectionStream(byte[] strings, IReadOnlyList<byte[]> chunks)
    {
        return Concat(
            Encoding.ASCII.GetBytes("BETH"), U32(8), U32(4), U32((uint)chunks.Count + 2),
            Encoding.ASCII.GetBytes("STRT"), U32((uint)strings.Length), strings,
            Concat([.. chunks]));
    }

    private static byte[] Chunk(string signature, byte[] body) =>
        Concat(Encoding.ASCII.GetBytes(signature), U32((uint)body.Length), body);

    private static byte[] U32(uint value) => BitConverter.GetBytes(value);
    private static byte[] U16(ushort value) => BitConverter.GetBytes(value);
    private static byte[] F32(float value) => BitConverter.GetBytes(value);

    private static byte[] Concat(params byte[][] parts)
    {
        var bytes = new List<byte>();
        foreach (var part in parts)
        {
            bytes.AddRange(part);
        }

        return [.. bytes];
    }

    private sealed record VolumetricLightingSchema(
        IReadOnlyDictionary<string, uint> Offsets,
        byte[] StringTable,
        IReadOnlyList<byte[]> Chunks);
}
