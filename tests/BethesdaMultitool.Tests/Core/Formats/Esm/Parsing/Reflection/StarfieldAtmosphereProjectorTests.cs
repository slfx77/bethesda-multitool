using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.Reflection;
using BethesdaMultitool.Core.Formats.Esm.Parsing.Reflection;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Parsing.Reflection;

public sealed class StarfieldAtmosphereProjectorTests
{
    private const string RootType = "BGSAtmosphere";
    private const string SettingsType = "BGSAtmosphere::AtmosphereSettings";
    private const string OverridesType = "BGSAtmosphere::OverrideSettings";
    private const string MiscType = "BGSAtmosphere::MiscSettings";
    private const uint TypeRef = 0xFFFFFF05;
    private const uint TypeUInt32 = 0xFFFFFF0D;
    private const uint TypeUInt64 = 0xFFFFFF0F;

    [Fact]
    public void Decode_FullObject_PreservesThreeExplicitNullReferences()
    {
        Assert.True(StarfieldAtmosphereDecoder.TryDecode(
            BuildFullStream(0, 0, 0), StarfieldAtmospherePayloadKind.FullObject,
            out var patch, out var error), error);

        Assert.NotNull(patch);
        Assert.True(patch.ParentFormId.HasValue);
        Assert.Equal(0u, patch.ParentFormId.Value);
        Assert.True(patch.SunPresetOverrideFormId.HasValue);
        Assert.Equal(0u, patch.SunPresetOverrideFormId.Value);
        Assert.True(patch.ClimateOverrideFormId.HasValue);
        Assert.Equal(0u, patch.ClimateOverrideFormId.Value);
    }

    [Fact]
    public void Decode_Diff_ProjectsEarthParentAndClimateWhileSunRemainsAbsent()
    {
        Assert.True(StarfieldAtmosphereDecoder.TryDecode(
            BuildDiffStream(parent: 0x0020CDD3, climate: 0x00064D14),
            StarfieldAtmospherePayloadKind.Diff,
            out var patch, out var error), error);

        Assert.NotNull(patch);
        Assert.Equal(0x0020CDD3u, patch.ParentFormId);
        Assert.Null(patch.SunPresetOverrideFormId);
        Assert.Equal(0x00064D14u, patch.ClimateOverrideFormId);
    }

    [Fact]
    public void Decode_Diff_DistinguishesExplicitZeroFromAbsentMember()
    {
        Assert.True(StarfieldAtmosphereDecoder.TryDecode(
            BuildDiffStream(sun: 0), StarfieldAtmospherePayloadKind.Diff,
            out var patch, out var error), error);

        Assert.NotNull(patch);
        Assert.Null(patch.ParentFormId);
        Assert.True(patch.SunPresetOverrideFormId.HasValue);
        Assert.Equal(0u, patch.SunPresetOverrideFormId.Value);
        Assert.Null(patch.ClimateOverrideFormId);
    }

    [Fact]
    public void Decode_EmptyDiff_LeavesEveryStructuralMemberAbsent()
    {
        Assert.True(StarfieldAtmosphereDecoder.TryDecode(
            BuildDiffStream(), StarfieldAtmospherePayloadKind.Diff,
            out var patch, out var error), error);

        Assert.NotNull(patch);
        Assert.Null(patch.ParentFormId);
        Assert.Null(patch.SunPresetOverrideFormId);
        Assert.Null(patch.ClimateOverrideFormId);
    }

    [Fact]
    public void Project_FullObject_RejectsMissingRequiredNestedField()
    {
        var reflected = Object(RootType,
            ("Settings", ObjectValue(Object(SettingsType,
                ("pParent", Reference(0)),
                ("Overrides", ObjectValue(Object(OverridesType,
                    ("pSunPresetOverride", Reference(0))))),
                ("Misc", ObjectValue(Object(MiscType)))))));

        Assert.False(StarfieldAtmosphereProjector.TryProject(
            reflected, StarfieldAtmospherePayloadKind.FullObject,
            out var patch, out var error));
        Assert.Null(patch);
        Assert.Contains("pClimateOverride", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Decode_FullObject_RejectsWrongReferenceValueType()
    {
        Assert.False(StarfieldAtmosphereDecoder.TryDecode(
            BuildFullStream(
                0, 0, 0,
                sunReferenceEncoding: Concat(U32(TypeUInt64), U64(1))),
            StarfieldAtmospherePayloadKind.FullObject,
            out var patch, out var error));

        Assert.Null(patch);
        Assert.Contains("Ref<UInt32>", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Decode_RejectsSelfDescribedWrongNestedClassName()
    {
        const string falseSettingsType = "BGSAtmosphere::AtmosphereSettingsLookalike";

        Assert.False(StarfieldAtmosphereDecoder.TryDecode(
            BuildFullStream(0, 0, 0, settingsType: falseSettingsType),
            StarfieldAtmospherePayloadKind.FullObject,
            out var patch, out var error));

        Assert.Null(patch);
        Assert.Contains(SettingsType, error, StringComparison.Ordinal);
        Assert.Contains("exact type", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Project_Diff_RejectsStructuralLeafAtWrongPath()
    {
        var reflected = Object(RootType,
            ("pParent", Reference(0x0020CDD3)));

        Assert.False(StarfieldAtmosphereProjector.TryProject(
            reflected, StarfieldAtmospherePayloadKind.Diff,
            out var patch, out var error));
        Assert.Null(patch);
        Assert.Contains("invalid path", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Settings", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Project_Diff_RejectsClimateReferenceUnderOverrides()
    {
        var reflected = Object(RootType,
            ("Settings", ObjectValue(Object(SettingsType,
                ("Overrides", ObjectValue(Object(OverridesType,
                    ("pClimateOverride", Reference(0x00064D14)))))))));

        Assert.False(StarfieldAtmosphereProjector.TryProject(
            reflected, StarfieldAtmospherePayloadKind.Diff,
            out var patch, out var error));
        Assert.Null(patch);
        Assert.Contains("invalid path", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Settings.Misc", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Decode_RejectsWrongReflectedRootClass()
    {
        Assert.False(StarfieldAtmosphereDecoder.TryDecode(
            BuildFullStream(0, 0, 0, rootType: "BGSAtmosphereLookalike"),
            StarfieldAtmospherePayloadKind.FullObject,
            out var patch, out var error));

        Assert.Null(patch);
        Assert.Contains("expected class", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Decode_RejectsUnknownPayloadKindBeforeReadingStream()
    {
        Assert.False(StarfieldAtmosphereDecoder.TryDecode(
            [], StarfieldAtmospherePayloadKind.Unknown,
            out var patch, out var error));

        Assert.Null(patch);
        Assert.Contains("payload kind", error, StringComparison.OrdinalIgnoreCase);
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

    private static byte[] BuildFullStream(
        uint parent,
        uint sun,
        uint climate,
        string rootType = RootType,
        string settingsType = SettingsType,
        byte[]? sunReferenceEncoding = null)
    {
        var schema = BuildSchema(rootType, settingsType);
        var body = Concat(
            U32(schema.Offsets[rootType]),
            Ref(parent),
            sunReferenceEncoding ?? Ref(sun),
            Ref(climate));
        var chunks = new List<byte[]>(schema.ClassChunks)
        {
            Chunk("OBJT", body)
        };
        return ReflectionStream(schema.StringTable, chunks);
    }

    private static byte[] BuildDiffStream(
        uint? parent = null,
        uint? sun = null,
        uint? climate = null)
    {
        var schema = BuildSchema(RootType, SettingsType);
        var body = new List<byte>();
        body.AddRange(U32(schema.Offsets[RootType]));

        if (parent.HasValue || sun.HasValue || climate.HasValue)
        {
            body.AddRange(U16(0)); // BGSAtmosphere.Settings
            if (parent.HasValue)
            {
                body.AddRange(U16(0)); // AtmosphereSettings.pParent
                body.AddRange(Ref(parent.Value));
            }

            if (sun.HasValue)
            {
                body.AddRange(U16(1)); // AtmosphereSettings.Overrides
                body.AddRange(U16(0)); // OverrideSettings.pSunPresetOverride
                body.AddRange(Ref(sun.Value));
                body.AddRange(U16(ushort.MaxValue));
            }

            if (climate.HasValue)
            {
                body.AddRange(U16(2)); // AtmosphereSettings.Misc
                body.AddRange(U16(0)); // MiscSettings.pClimateOverride
                body.AddRange(Ref(climate.Value));
                body.AddRange(U16(ushort.MaxValue));
            }

            body.AddRange(U16(ushort.MaxValue));
        }

        body.AddRange(U16(ushort.MaxValue));
        var chunks = new List<byte[]>(schema.ClassChunks)
        {
            Chunk("DIFF", [.. body])
        };
        return ReflectionStream(schema.StringTable, chunks);
    }

    private static ReflectionSchema BuildSchema(string rootType, string settingsType)
    {
        string[] names =
        [
            rootType,
            settingsType,
            OverridesType,
            MiscType,
            "Settings",
            "pParent",
            "Overrides",
            "Misc",
            "pSunPresetOverride",
            "pClimateOverride"
        ];
        var offsets = new Dictionary<string, uint>(StringComparer.Ordinal);
        var strings = new List<byte>();
        foreach (var name in names.Distinct(StringComparer.Ordinal))
        {
            offsets[name] = checked((uint)strings.Count);
            strings.AddRange(Encoding.ASCII.GetBytes(name));
            strings.Add(0);
        }

        var chunks = new List<byte[]>
        {
            Chunk("TYPE", U32(4)),
            ClassChunk(offsets, OverridesType, ("pSunPresetOverride", TypeRef)),
            ClassChunk(offsets, MiscType, ("pClimateOverride", TypeRef)),
            ClassChunk(offsets, settingsType,
                ("pParent", TypeRef),
                ("Overrides", offsets[OverridesType]),
                ("Misc", offsets[MiscType])),
            ClassChunk(offsets, rootType, ("Settings", offsets[settingsType]))
        };
        return new ReflectionSchema(offsets, [.. strings], chunks);
    }

    private static byte[] ClassChunk(
        IReadOnlyDictionary<string, uint> offsets,
        string className,
        params (string Name, uint Type)[] fields)
    {
        var body = new List<byte>();
        body.AddRange(U32(offsets[className]));
        body.AddRange(U32(0));
        body.AddRange(U16(0));
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

    private static byte[] Chunk(string signature, byte[] body)
    {
        return Concat(Encoding.ASCII.GetBytes(signature), U32((uint)body.Length), body);
    }

    private static byte[] Ref(uint value) => Concat(U32(TypeUInt32), U32(value));

    private static byte[] U32(uint value) => BitConverter.GetBytes(value);

    private static byte[] U64(ulong value) => BitConverter.GetBytes(value);

    private static byte[] U16(ushort value) => BitConverter.GetBytes(value);

    private static byte[] Concat(params byte[][] parts)
    {
        var bytes = new List<byte>();
        foreach (var part in parts)
        {
            bytes.AddRange(part);
        }

        return [.. bytes];
    }

    private sealed record ReflectionSchema(
        IReadOnlyDictionary<string, uint> Offsets,
        byte[] StringTable,
        IReadOnlyList<byte[]> ClassChunks);
}
