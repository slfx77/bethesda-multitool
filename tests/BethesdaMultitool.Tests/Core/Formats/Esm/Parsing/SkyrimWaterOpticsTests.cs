using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.RecordModel;
using BethesdaMultitool.Core.Formats.Esm.RecordModel.Decoding;
using BethesdaMultitool.Core.Formats.Esm.RecordModel.Generated;
using BethesdaMultitool.Core.Formats.Esm.RecordModel.Schema;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Water;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Parsing;

public sealed class SkyrimWaterOpticsTests
{
    private static readonly string[] ExpectedDepthNames = ["Reflections", "Refraction", "Normals", "Specular Lighting"];
    private static readonly float[] ExpectedDepthValues = [.9f, .5f, .1f, .2f];
    private static readonly float[] NonFiniteValues = [float.NaN, float.PositiveInfinity, float.NegativeInfinity];

    [Fact]
    public void ExactDefaultWater_PreservesAuthoredControlsThroughRecordAppearanceAndUpload()
    {
        var water = SkyrimWaterOpticsTestData.ParseDefaultWater();
        var appearance = Assert.IsType<WaterAppearance>(WaterAppearance.FromWaterRecord(water));
        var source = Assert.IsType<SkyrimWaterOptics>(appearance.SkyrimOptics);
        var projected = SkyrimWaterOpticsConstants.Project(BethesdaGame.Skyrim, true, source);

        Assert.Equal(0x00000018u, water.FormId);
        Assert.Equal("DefaultWater", water.EditorId);
        Assert.Equal((R: (byte)37, G: (byte)52, B: (byte)37), appearance.Shallow);
        Assert.Equal((R: (byte)5, G: (byte)16, B: (byte)5), appearance.Deep);
        Assert.Equal((0.9f, 0.5f, 0.1f, 0.2f), source.DepthControl);
        Assert.Equal((0f, 110f, 0.93f),
            (source.AboveWaterFogNear, source.AboveWaterFogFar, source.AboveWaterFogAmount));
        Assert.Equal(new Vector4(.9f, .5f, .1f, .2f), projected.DepthControl);
        Assert.Equal(new Vector4(110f, 110f, 3.72f, 1f), projected.Fog);
        Assert.Equal(-500f, appearance.Surface.UnderwaterFogNear);
        Assert.Equal(1600f, appearance.Surface.UnderwaterFogFar);
        Assert.Equal(3, appearance.NormalTextures!.Count);
    }

    [Fact]
    public void RegisteredSchemaRetainsAllFourDepthLeavesBeyondUnnamedFloats()
    {
        var schema = Assert.Single(EsmSchemas.ForGame(BethesdaGame.Skyrim)!, item => item.Signature == "WATR");
        var decoded = Assert.Single(SchemaRecordDecoder.Decode(schema,
            [new RawSubrecord("DNAM", SkyrimWaterOpticsTestData.DefaultDnam())],
            game: BethesdaGame.Skyrim));
        var depth = Assert.Single(decoded.Children, node => node.Label == "Depth Properties");

        Assert.Equal(ExpectedDepthNames,
            depth.Children.Select(node => node.Label));
        Assert.Equal(ExpectedDepthValues,
            depth.Children.Select(node => Assert.IsType<float>(node.RawValue)));
        Assert.Contains(decoded.Children, node => node.Label == "Unknown float +28" && !node.IsRaw);
        Assert.Contains(decoded.Children, node => node.Label == "Unknown float +136" && !node.IsRaw);

        var original = Assert.Single(SkyrimSchema.Records, item => item.Signature == "WATR");
        Assert.NotSame(original, schema);
        var unaffected = Assert.Single(SkyrimSchema.Records, item => item.Signature == "WEAP");
        Assert.Same(unaffected, EsmSchemas.IndexForGame(BethesdaGame.Skyrim)!["WEAP"]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void SameCountRegeneratedSchemaWithDifferentIdentityOrOffsetsIsNotReinterpreted(int mutation)
    {
        var original = Assert.Single(SkyrimSchema.Records, item => item.Signature == "WATR");
        var dnam = Assert.IsType<StructDef>(Assert.Single(original.Members, member => member.Signature == "DNAM"));
        var members = dnam.Members.ToArray();
        switch (mutation)
        {
            case 0:
                members[8] = members[8] with { Name = "Different near plane" };
                break;
            case 1:
                members[21] = members[21] with { Name = "Different fog amount" };
                break;
            case 2:
                members[12] = new UnusedDef(2); // Same member count, subsequent fields move by one byte.
                break;
            default:
                var depth = Assert.IsType<StructDef>(members[34]);
                var lanes = depth.Members.ToArray();
                lanes[2] = lanes[2] with { Name = "Different depth lane" };
                members[34] = depth with { Members = lanes };
                break;
        }

        var changedDnam = dnam with { Members = members };
        var changedRecord = original with
        {
            Members = original.Members.Select(member => ReferenceEquals(member, dnam) ? changedDnam : member).ToArray()
        };
        var result = Assert.Single(SkyrimWaterSchema.CompleteKnownFloatWidths([changedRecord]));
        Assert.Same(changedDnam, Assert.Single(result.Members, member => member.Signature == "DNAM"));
    }

    [Theory]
    [InlineData(52)]
    [InlineData(227)]
    [InlineData(229)]
    [InlineData(232)]
    public void WrongLengthDoesNotOptIntoPcSkyrimShader(int length)
    {
        var data = new byte[length];
        SkyrimWaterOpticsTestData.DefaultDnam().AsSpan(0, Math.Min(length, 228)).CopyTo(data);
        Assert.Null(SkyrimWaterOptics.TryRead(data, false));
    }

    [Fact]
    public void BigEndianDoesNotSilentlyOptIntoPcOracle()
    {
        Assert.Null(SkyrimWaterOptics.TryRead(SkyrimWaterOpticsTestData.DefaultDnam(), true));
    }

    [Theory]
    [InlineData(32)]
    [InlineData(36)]
    [InlineData(132)]
    [InlineData(208)]
    [InlineData(212)]
    [InlineData(216)]
    [InlineData(220)]
    public void EveryConsumedNonFiniteLaneRejectsTypedProjection(int offset)
    {
        foreach (var value in NonFiniteValues)
        {
            var data = SkyrimWaterOpticsTestData.DefaultDnam();
            BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(offset, 4), value);
            Assert.Null(SkyrimWaterOptics.TryRead(data, false));
        }
    }

    [Fact]
    public void FiniteAuthoredDepthValuesAreRetainedWithoutAnInventedRangeClamp()
    {
        var source = new SkyrimWaterOptics(-20f, 80f, -2f, (-2f, 3f, .25f, 0f));
        var projected = SkyrimWaterOpticsConstants.Project(BethesdaGame.Skyrim, true, source);
        Assert.Equal(new Vector4(-2f, 3f, .25f, 0f), projected.DepthControl);
        Assert.Equal(new Vector4(80f, 100f, 1f, 1f), projected.Fog);
    }

    [Fact]
    public void EqualFogRangeUsesRetailEpsilonButSmallNonzeroRangeIsNotRetuned()
    {
        var source = new SkyrimWaterOptics(0f, 0f, .1f, (1f, 1f, 1f, 1f));
        var equal = SkyrimWaterOpticsConstants.Project(BethesdaGame.Skyrim, true, source);
        var tiny = SkyrimWaterOpticsConstants.Project(BethesdaGame.Skyrim, true,
            source with { AboveWaterFogFar = 1e-6f });
        Assert.Equal(new Vector4(1f, 1e-5f, 1f, 1f), equal.Fog);
        Assert.Equal(1e-6f, tiny.Fog.Y);
    }

    [Fact]
    public void ProjectionRejectsMissingSourceWrongGameMissingSnapshotAndArithmeticOverflow()
    {
        var source = Assert.IsType<SkyrimWaterOptics>(SkyrimWaterOptics.TryRead(
            SkyrimWaterOpticsTestData.DefaultDnam(), false));
        Assert.False(SkyrimWaterOpticsConstants.Project(BethesdaGame.Skyrim, true, null).IsEnabled);
        Assert.False(SkyrimWaterOpticsConstants.Project(BethesdaGame.FalloutNewVegas, true, source).IsEnabled);
        Assert.False(SkyrimWaterOpticsConstants.Project(BethesdaGame.Skyrim, false, source).IsEnabled);
        Assert.False(SkyrimWaterOpticsConstants.Project(BethesdaGame.Skyrim, true,
            source with { AboveWaterFogAmount = float.MaxValue }).IsEnabled);
        Assert.False(SkyrimWaterOpticsConstants.Project(BethesdaGame.Skyrim, true,
            source with { AboveWaterFogNear = -float.MaxValue, AboveWaterFogFar = float.MaxValue }).IsEnabled);
        Assert.Equal(32, Marshal.SizeOf<SkyrimWaterOpticsConstants>());
        Assert.Equal(SkyrimWaterOpticsConstants.ByteSize, Marshal.SizeOf<SkyrimWaterOpticsConstants>());
    }

    [Fact]
    public void UnderwaterDistancesCannotChangeTheAboveWaterOpticalConstants()
    {
        var original = SkyrimWaterOpticsTestData.DefaultDnam();
        var changed = original.ToArray();
        BinaryPrimitives.WriteSingleLittleEndian(changed.AsSpan(144, 4), 12345f);
        BinaryPrimitives.WriteSingleLittleEndian(changed.AsSpan(148, 4), 98765f);
        Assert.Equal(SkyrimWaterOptics.TryRead(original, false), SkyrimWaterOptics.TryRead(changed, false));
    }

    [Fact]
    public void RetailProjectionExposesTheDimensionalCounterexampleAndDistinctDepthLanes()
    {
        var source = Assert.IsType<SkyrimWaterOptics>(SkyrimWaterOptics.TryRead(
            SkyrimWaterOpticsTestData.DefaultDnam(), false));
        var constants = SkyrimWaterOpticsConstants.Project(BethesdaGame.Skyrim, true, source);
        // Raw shader oracle: (slant,slant,vertical,vertical), normalized once by the above-water far.
        var fractions = new Vector4(1f, 1f, .5f, .5f);
        var factors = Vector4.One + constants.DepthControl * (fractions - Vector4.One);
        Assert.Equal(new Vector4(1f, 1f, .95f, .9f), factors);
        var fullBody = 1f - MathF.Pow(0f / constants.Fog.Y, constants.Fog.Z);
        Assert.Equal(1f, fullBody);
        var halfBody = 1f - MathF.Pow(.5f * constants.Fog.X / constants.Fog.Y, constants.Fog.Z);
        // DNAM+132 bytes7B146E3F -> .93f; independent 1 - 2^(-4 * .93f).
        Assert.Equal(.9241128f, halfBody, 6);

        // The former world-unit underwater planes used as normalized thresholds stay near .238.
        var oldRipple = ((110f / 110f) - (-500f)) / (1600f - (-500f));
        Assert.InRange(oldRipple, .2385f, .2386f);
        Assert.InRange(oldRipple * .75f, .1789f, .1790f);
    }
}
