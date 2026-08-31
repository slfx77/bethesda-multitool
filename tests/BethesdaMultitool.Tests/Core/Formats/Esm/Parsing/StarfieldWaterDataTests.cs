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
///     Synthetic contract tests for Starfield's xEdit-defined 152-byte little-endian WATR DNAM
///     and its adjacent scalar/vector/CUR3 subrecords. No retail fixture or rendering projection is
///     used: every byte boundary is authored explicitly by the test.
/// </summary>
public sealed class StarfieldWaterDataTests
{
    [Fact]
    public void TryReadStarfieldWaterDnam_DecodesEveryExactOffset()
    {
        var dnam = BuildDnam();

        var visual = MiscEnvironmentHandler.TryReadStarfieldWaterDnam(dnam, false);

        Assert.NotNull(visual);
        Assert.Equal(ValueAt(0), visual.DepthAmount);
        Assert.Equal((ValueAt(4), ValueAt(8), ValueAt(12)), visual.AbsorptionRanges);
        Assert.Equal(ValueAt(16), visual.PhytoplanktonConcentration);
        Assert.Equal(ValueAt(20), visual.SedimentConcentration);
        Assert.Equal(ValueAt(24), visual.YellowMatterConcentration);
        Assert.Equal(ValueAt(28), visual.Oceanness);
        Assert.Equal((R: (byte)0x11, G: (byte)0x22, B: (byte)0x33, A: (byte)0x44),
            visual.UnderwaterColor);
        Assert.Equal(ValueAt(36), visual.UnderwaterFogAmount);
        Assert.Equal(ValueAt(40), visual.UnderwaterFogNear);
        Assert.Equal(ValueAt(44), visual.UnderwaterFogFar);
        Assert.Equal(ValueAt(48), visual.NormalMagnitude);
        Assert.Equal(ValueAt(52), visual.ShallowNormalFalloff);
        Assert.Equal(ValueAt(56), visual.DeepNormalFalloff);
        Assert.Equal(ValueAt(60), visual.SurfaceEffectFalloff);
        Assert.Equal(ValueAt(64), visual.DisplacementForce);
        Assert.Equal(ValueAt(68), visual.DisplacementVelocity);
        Assert.Equal(ValueAt(72), visual.DisplacementFalloff);
        Assert.Equal(ValueAt(76), visual.DisplacementDampener);
        Assert.Equal(ValueAt(80), visual.DisplacementStartingSize);
        AssertLayer(visual.Layer1, 84, 96, 108, 120, 132);
        AssertLayer(visual.Layer2, 88, 100, 112, 124, 136);
        AssertLayer(visual.Layer3, 92, 104, 116, 128, 140);
        Assert.Equal(ValueAt(144), visual.FlowmapScale);
        Assert.Equal(ValueAt(148), visual.Roughness);
    }

    [Theory]
    [InlineData(151)]
    [InlineData(153)]
    public void TryReadStarfieldWaterDnam_RejectsWrongSize(int length)
    {
        var source = BuildDnam();
        var candidate = new byte[length];
        source.AsSpan(0, Math.Min(source.Length, candidate.Length)).CopyTo(candidate);

        Assert.Null(MiscEnvironmentHandler.TryReadStarfieldWaterDnam(candidate, false));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(12)]
    [InlineData(36)]
    [InlineData(84)]
    [InlineData(148)]
    public void TryReadStarfieldWaterDnam_RejectsNonFiniteScalar(int offset)
    {
        var dnam = BuildDnam();
        BinaryPrimitives.WriteSingleLittleEndian(dnam.AsSpan(offset, sizeof(float)), float.NaN);

        Assert.Null(MiscEnvironmentHandler.TryReadStarfieldWaterDnam(dnam, false));
    }

    [Fact]
    public void TryReadStarfieldWaterDnam_RejectsBigEndianDispatch()
    {
        Assert.Null(MiscEnvironmentHandler.TryReadStarfieldWaterDnam(BuildDnam(), true));
    }

    [Fact]
    public void ParseWater_BigEndianRecordNeverProducesStarfieldTypedData()
    {
        var encoded = EncodeSubrecords(BuildCompleteSubrecords());

        var water = ParseStarfieldWaterData(encoded, isBigEndian: true);

        Assert.True(water.IsBigEndian);
        Assert.Null(water.VisualProperties);
    }

    [Fact]
    public void ParseWater_StarfieldRetainsFlagsVectorsAndAllSevenCurveReferences()
    {
        var water = ParseStarfieldWater(BuildCompleteSubrecords());

        Assert.Equal(new byte[] { 0x1D }, water.WaterFlags);
        var properties = Assert.IsType<Dictionary<string, object?>>(water.VisualProperties);
        Assert.Single(properties);
        var visual = Assert.IsType<StarfieldWaterVisualData>(properties["StarfieldVisualData"]);
        Assert.Equal(ValueAt(0), visual.Dnam.DepthAmount);
        Assert.Equal(ValueAt(148), visual.Dnam.Roughness);
        Assert.Equal(
            StarfieldWaterFlags.Dangerous |
            StarfieldWaterFlags.DirectionalSound |
            StarfieldWaterFlags.EnableFlowmap |
            StarfieldWaterFlags.BlendNormals,
            visual.Flags);
        var gnam = Assert.IsType<StarfieldWaterUnusedGnam>(visual.Gnam);
        Assert.Equal(0x7FC0_0000u, gnam.Word0);
        Assert.Equal(0xFFFF_FFFFu, gnam.Word1);
        Assert.Equal(0x0123_4567u, gnam.Word2);
        Assert.Equal((X: 4f, Y: 5f, Z: 6f), visual.LinearVelocity!.Value);
        Assert.Equal((X: 7f, Y: 8f, Z: 9f), visual.AngularVelocity!.Value);
        Assert.Equal(0x0100_0001u, visual.RiverAbsorptionCurveFormId!.Value);
        Assert.Equal(0x0100_0002u, visual.OceanAbsorptionCurveFormId!.Value);
        Assert.Equal(0x0100_0003u, visual.RiverScatteringCurveFormId!.Value);
        Assert.Equal(0x0100_0004u, visual.OceanScatteringCurveFormId!.Value);
        Assert.Equal(0x0100_0005u, visual.PhytoplanktonCurveFormId!.Value);
        Assert.Equal(0x0100_0006u, visual.SedimentCurveFormId!.Value);
        Assert.Equal(0x0100_0007u, visual.YellowMatterCurveFormId!.Value);

        // Starfield DNAM has no shallow/deep surface colors. Ingestion must not invent them or
        // change the current flat-fallback rendering policy.
        Assert.Null(WaterAppearance.FromWaterRecord(water));

        // When an independent producer supplies compatibility colors, the typed Starfield payload
        // survives WaterAppearance dispatch without being used to manufacture those colors.
        var compatibilityProperties = new Dictionary<string, object?>(properties)
        {
            ["ShallowColor"] = 0x00_33_22_11u,
            ["DeepColor"] = 0x00_66_55_44u
        };
        var appearance = WaterAppearance.FromVisualProperties(compatibilityProperties, null);
        Assert.NotNull(appearance);
        Assert.Same(visual, appearance.StarfieldVisualData);
    }

    [Fact]
    public void ParseWater_PresentMalformedVelocityVectorFailsClosed()
    {
        var subrecords = BuildCompleteSubrecords();
        var malformed = Vector(1f, float.PositiveInfinity, 3f);
        subrecords[4] = ("NAM0", malformed);

        var water = ParseStarfieldWater(subrecords);

        Assert.Null(water.VisualProperties);
        Assert.Null(WaterAppearance.FromWaterRecord(water));
    }

    [Theory]
    [InlineData(3, 11)] // GNAM opaque payload
    [InlineData(4, 11)] // NAM0 linear velocity
    [InlineData(5, 13)] // NAM1 angular velocity
    public void ParseWater_WrongSizedAdjacentPayloadFailsClosed(int subrecordIndex, int length)
    {
        var subrecords = BuildCompleteSubrecords();
        var signature = subrecords[subrecordIndex].Signature;
        subrecords[subrecordIndex] = (signature, new byte[length]);

        var water = ParseStarfieldWater(subrecords);

        Assert.Null(water.VisualProperties);
    }

    [Fact]
    public void ParseWater_WrongSizedDnamFailsClosedWithoutClassicFallthrough()
    {
        var subrecords = BuildCompleteSubrecords();
        subrecords[2] = ("DNAM", BuildDnam()[..151]);

        var water = ParseStarfieldWater(subrecords);

        Assert.Null(water.VisualProperties);
    }

    [Fact]
    public void ParseWater_WrongSizedFlagsFailClosedForTypedEnvelope()
    {
        var subrecords = BuildCompleteSubrecords();
        subrecords[1] = ("FNAM", [(byte)0x1D, (byte)0x80]);

        var water = ParseStarfieldWater(subrecords);

        Assert.Null(water.VisualProperties);
        Assert.Equal(new byte[] { 0x1D, 0x80 }, water.WaterFlags);
    }

    [Fact]
    public void ParseWater_MissingRequiredFlagsFailClosedForTypedEnvelope()
    {
        var subrecords = BuildCompleteSubrecords()
            .Where(subrecord => subrecord.Signature != "FNAM")
            .ToArray();

        var water = ParseStarfieldWater(subrecords);

        Assert.Null(water.VisualProperties);
    }

    [Theory]
    [InlineData(1)] // required FNAM
    [InlineData(2)] // required DNAM
    [InlineData(6)] // ENAM curve reference
    public void ParseWater_DuplicateTypedFieldFailsClosed(int duplicateIndex)
    {
        var subrecords = BuildCompleteSubrecords();
        var duplicate = subrecords[duplicateIndex];
        subrecords = subrecords.Append(duplicate).ToArray();

        var water = ParseStarfieldWater(subrecords);

        Assert.Null(water.VisualProperties);
    }

    [Fact]
    public void ParseWater_PhysicallyTruncatedSubrecordTailFailsClosed()
    {
        var encoded = EncodeSubrecords(BuildCompleteSubrecords());
        // UNAM still declares four bytes, but the record ends after three of them.
        var truncated = encoded[..^1];

        var water = ParseStarfieldWaterData(truncated);

        Assert.Null(water.VisualProperties);
    }

    [Fact]
    public void ParseWater_TruncatedCurveReferenceFailsClosed()
    {
        var subrecords = BuildCompleteSubrecords();
        subrecords[6] = ("ENAM", new byte[3]);

        var water = ParseStarfieldWater(subrecords);

        Assert.Null(water.VisualProperties);
    }

    private static (string Signature, byte[] Payload)[] BuildCompleteSubrecords()
    {
        return
        [
            ("ANAM", [(byte)80]),
            ("FNAM", [(byte)0x1D]),
            ("DNAM", BuildDnam()),
            ("GNAM", Gnam(0x7FC0_0000u, 0xFFFF_FFFFu, 0x0123_4567u)),
            ("NAM0", Vector(4f, 5f, 6f)),
            ("NAM1", Vector(7f, 8f, 9f)),
            ("ENAM", FormId(0x0100_0001u)),
            ("HNAM", FormId(0x0100_0002u)),
            ("JNAM", FormId(0x0100_0003u)),
            ("LNAM", FormId(0x0100_0004u)),
            ("MNAM", FormId(0x0100_0005u)),
            ("QNAM", FormId(0x0100_0006u)),
            ("UNAM", FormId(0x0100_0007u))
        ];
    }

    private static byte[] BuildDnam()
    {
        var dnam = new byte[152];
        for (var offset = 0; offset < dnam.Length; offset += sizeof(float))
        {
            if (offset == 32) continue;
            BinaryPrimitives.WriteSingleLittleEndian(dnam.AsSpan(offset, sizeof(float)), ValueAt(offset));
        }

        dnam[32] = 0x11;
        dnam[33] = 0x22;
        dnam[34] = 0x33;
        dnam[35] = 0x44;
        return dnam;
    }

    private static float ValueAt(int offset) => offset + 0.25f;

    private static byte[] Vector(float x, float y, float z)
    {
        var vector = new byte[12];
        BinaryPrimitives.WriteSingleLittleEndian(vector.AsSpan(0, 4), x);
        BinaryPrimitives.WriteSingleLittleEndian(vector.AsSpan(4, 4), y);
        BinaryPrimitives.WriteSingleLittleEndian(vector.AsSpan(8, 4), z);
        return vector;
    }

    private static byte[] Gnam(uint word0, uint word1, uint word2)
    {
        var gnam = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(gnam.AsSpan(0, 4), word0);
        BinaryPrimitives.WriteUInt32LittleEndian(gnam.AsSpan(4, 4), word1);
        BinaryPrimitives.WriteUInt32LittleEndian(gnam.AsSpan(8, 4), word2);
        return gnam;
    }

    private static byte[] FormId(uint value)
    {
        var formId = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(formId, value);
        return formId;
    }

    private static void AssertLayer(
        StarfieldWaterNoiseLayer layer,
        int directionOffset,
        int speedOffset,
        int amplitudeOffset,
        int uvOffset,
        int falloffOffset)
    {
        Assert.Equal(ValueAt(directionOffset), layer.WindDirection);
        Assert.Equal(ValueAt(speedOffset), layer.WindSpeed);
        Assert.Equal(ValueAt(amplitudeOffset), layer.AmplitudeScale);
        Assert.Equal(ValueAt(uvOffset), layer.UvScale);
        Assert.Equal(ValueAt(falloffOffset), layer.NoiseFalloff);
    }

    private static WaterRecord ParseStarfieldWater(
        params (string Signature, byte[] Payload)[] subrecords)
    {
        return ParseStarfieldWaterData(EncodeSubrecords(subrecords));
    }

    private static byte[] EncodeSubrecords(
        params (string Signature, byte[] Payload)[] subrecords)
    {
        var dataLength = subrecords.Sum(subrecord => 6 + subrecord.Payload.Length);
        var data = new byte[dataLength];
        var cursor = 0;
        foreach (var (signature, payload) in subrecords)
        {
            Encoding.ASCII.GetBytes(signature).CopyTo(data, cursor);
            BinaryPrimitives.WriteUInt16LittleEndian(
                data.AsSpan(cursor + 4, sizeof(ushort)),
                checked((ushort)payload.Length));
            payload.CopyTo(data, cursor + 6);
            cursor += 6 + payload.Length;
        }

        return data;
    }

    private static WaterRecord ParseStarfieldWaterData(byte[] data, bool isBigEndian = false)
    {
        const int headerSize = 24;
        var file = new byte[headerSize + data.Length];
        data.CopyTo(file, headerSize);
        var record = new DetectedMainRecord("WATR", (uint)data.Length, 0, 0x0100_1234, 0, isBigEndian)
        {
            HeaderSize = headerSize
        };
        var scan = new EsmRecordScanResult
        {
            Game = BethesdaGame.Starfield,
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
