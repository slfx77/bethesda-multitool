using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Animation;

public class NifMaterialAlphaControllerTests
{
    private const string RetailSandDust02 =
        @"TestOutput\fnv-opacity-audit\meshes\effects\nv\sanddust\sanddust02.nif";

    [Fact]
    public void RetailSandDust02_ZeroAlphaStormSheetsResolveToControllerTargets()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var path = SampleFileFixture.FindSamplePath(RetailSandDust02);
        Assert.SkipUnless(path is not null,
            "Extracted FNV SandDust02 NIF not present (dev-machine-only asset).");
        var data = File.ReadAllBytes(path!);
        var nif = NifParser.Parse(data);
        Assert.NotNull(nif);

        var tracks = NifMaterialAlphaControllerCollector.Collect(data, nif);

        Assert.Equal(3, tracks.Count);
        var storm03 = Assert.Single(tracks, track => track.TargetName == "sStorm03:0");
        var storm04 = Assert.Single(tracks, track => track.TargetName == "sStorm04:0");
        var particles = Assert.Single(tracks, track => track.TargetName == "PCloud01");
        AssertCurve(storm03.Keys,
            [(0f, 0f), (2.3333f, 0.5f), (3f, 0.5f), (5.3333f, 0f), (45f, 0f)]);
        AssertCurve(storm04.Keys,
            [(0f, 0f), (2.3333f, 0.25f), (3f, 0.35f), (5.3333f, 0f), (45f, 0f)]);
        AssertCurve(particles.Keys,
            [(0f, 0f), (0.6667f, 0.05f), (4f, 0.05f), (6.3333f, 0f), (45f, 0f)]);

        // Exact retail target graph + clocks. The outer Idle sequence loops for 45 seconds;
        // each alpha controller clamps at the end of its short visible pulse.
        Assert.Equal(51, storm03.MaterialPropertyRef);
        Assert.Equal(58, storm04.MaterialPropertyRef);
        Assert.Equal(69, particles.MaterialPropertyRef);
        Assert.Equal(NifCycleType.Loop, storm03.SequenceClock.Cycle);
        Assert.Equal(45f, storm03.SequenceClock.StopTime);
        Assert.Equal(NifCycleType.Clamp, storm03.ControllerClock.Cycle);
        Assert.Equal(5.3333f, storm03.ControllerClock.StopTime, 3);
        Assert.Equal(NifCycleType.Clamp, storm04.ControllerClock.Cycle);
        Assert.Equal(5.3333f, storm04.ControllerClock.StopTime, 3);
        Assert.Equal(NifCycleType.Clamp, particles.ControllerClock.Cycle);
        Assert.Equal(6.3333f, particles.ControllerClock.StopTime, 3);

        // The property-ref binding reaches both ordinary storm sheets and the NiParticleSystem
        // cloud; parsing the curves without attaching them would leave the visible bug unchanged.
        var model = NifGeometryExtractor.Extract(
            data, nif, treatRootsAsIdentity: true, collectBillboards: true);
        Assert.NotNull(model);
        var boundTargets = model.Submeshes
            .Where(static submesh => submesh.MaterialAlphaController is not null)
            .Select(static submesh =>
                $"{submesh.ShapeName}|{submesh.MaterialAlphaController!.TargetName}|" +
                submesh.MaterialAlphaController.MaterialPropertyRef)
            .OrderBy(static target => target, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            ["ParticleCloud|PCloud01|69", "sStorm03:0|sStorm03:0|51", "sStorm04:0|sStorm04:0|58"],
            boundTargets);

        // Exact end-to-end acceptance oracle. The two sheet materials initialize their stored alpha
        // to zero. Retail NiAlphaController::Update replaces that field with the interpolator result;
        // multiplying the two values would make both sheets permanently invisible while the particle
        // cloud alone could still make an aggregate image-difference gate pass.
        var controlledSubmeshes = model.Submeshes
            .Where(static submesh =>
                submesh.MaterialAlphaController is not null && submesh.ShapeName is not null)
            .ToDictionary(static submesh => submesh.ShapeName!, StringComparer.Ordinal);
        var storm03Submesh = controlledSubmeshes["sStorm03:0"];
        var storm04Submesh = controlledSubmeshes["sStorm04:0"];
        var particleSubmesh = controlledSubmeshes["ParticleCloud"];
        Assert.Equal(0f, storm03Submesh.MaterialAlpha);
        Assert.Equal(0f, storm04Submesh.MaterialAlpha);
        Assert.Equal(1f, particleSubmesh.MaterialAlpha);
        Assert.Equal(0.5f, storm03Submesh.MaterialAlphaController!.ResolveTargetAlpha(
            2.3333f, animationsEnabled: true), 3);
        Assert.Equal(0.25f, storm04Submesh.MaterialAlphaController!.ResolveTargetAlpha(
            2.3333f, animationsEnabled: true), 3);
        Assert.Equal(0.05f, particleSubmesh.MaterialAlphaController!.ResolveTargetAlpha(
            2.3333f, animationsEnabled: true), 3);
        Assert.All(controlledSubmeshes.Values, static submesh =>
        {
            Assert.True(submesh.HasAlphaBlend);
            Assert.Equal((byte)6, submesh.SrcBlendMode);
            Assert.Equal((byte)7, submesh.DstBlendMode);
        });
    }

    [Fact]
    public void SandDust02Idle_CollectsAndSamplesAllThreeRetailAlphaCurves()
    {
        var fixture = BuildSandDust02Fixture();

        var tracks = NifMaterialAlphaControllerCollector.Collect(fixture.Data, fixture.Nif);

        Assert.Equal(3, tracks.Count);
        var storm03 = Assert.Single(tracks, track => track.TargetName == "sStorm03:0");
        var storm04 = Assert.Single(tracks, track => track.TargetName == "sStorm04:0");
        var particles = Assert.Single(tracks, track => track.TargetName == "PCloud01");

        Assert.Equal(fixture.MaterialRefs[0], storm03.MaterialPropertyRef);
        Assert.Equal(fixture.MaterialRefs[1], storm04.MaterialPropertyRef);
        Assert.Equal(fixture.MaterialRefs[2], particles.MaterialPropertyRef);
        Assert.Equal(5, storm03.Keys.Length);
        Assert.Equal(5, storm04.Keys.Length);
        Assert.Equal(5, particles.Keys.Length);

        // Exact maxima recovered from effects\NV\SandDust\SandDust02.NIF.
        Assert.Equal(0.5f, storm03.Sample(2.3333f), 3);
        Assert.Equal(0.25f, storm04.Sample(2.3333f), 3);
        Assert.Equal(0.35f, storm04.Sample(3f), 3);
        Assert.Equal(0.05f, particles.Sample(2f), 3);

        // The 45-second Idle sequence starts transparent and loops to that same authored frame.
        Assert.Equal(0f, storm03.Sample(0f));
        Assert.Equal(0f, particles.Sample(0f));
        Assert.Equal(0f, storm03.Sample(45f));
        Assert.InRange(storm03.Sample(46f), 0.20f, 0.23f);
        Assert.Equal(0.5f, storm03.ResolveTargetAlpha(2.3333f, animationsEnabled: true), 3);
        Assert.Equal(0f, storm03.ResolveTargetAlpha(2.3333f, animationsEnabled: false));
    }

    [Fact]
    public void BsXWithoutAnimatedBit_VetoesControllerManagerPlayback()
    {
        var fixture = BuildSandDust02Fixture();
        WriteUInt32(fixture.Data, fixture.BsxFlagsOffset, 0u);

        var tracks = NifMaterialAlphaControllerCollector.Collect(fixture.Data, fixture.Nif);

        Assert.Empty(tracks);
    }

    [Fact]
    public void ConstantInterpolation_HoldsPriorKeyAndBoundsOpacity()
    {
        var clock = new NifAlphaControllerClock(
            1f, 0f, 0f, 10f, NifCycleType.Clamp);
        var track = new NifMaterialAlphaController(
            0,
            "Dust",
            NifKeyInterpolation.Constant,
            [new NifFloatKey(0f, -1f), new NifFloatKey(2f, 2f)],
            null,
            clock,
            clock);

        Assert.Equal(0f, track.Sample(0.5f));
        Assert.Equal(1f, track.Sample(2f));
    }

    private static Fixture BuildSandDust02Fixture()
    {
        var data = new byte[1024];
        var nif = new NifInfo
        {
            BinaryVersion = NifVersions.Gamebryo202007,
            BsVersion = 34,
        };
        nif.Strings.AddRange([
            "Idle",
            "sStorm03:0",
            "sStorm04:0",
            "PCloud01",
            "NiMaterialProperty",
            "NiAlphaController",
        ]);

        var nextOffset = 0;
        int AddBlock(string type, int size)
        {
            var index = nif.Blocks.Count;
            nif.Blocks.Add(new BlockInfo
            {
                Index = index,
                TypeName = type,
                DataOffset = nextOffset,
                Size = size,
            });
            nextOffset += size;
            return index;
        }

        var materialRefs = new[]
        {
            AddBlock("NiMaterialProperty", 4),
            AddBlock("NiMaterialProperty", 4),
            AddBlock("NiMaterialProperty", 4),
        };
        var controllerRefs = new[]
        {
            AddBlock("NiAlphaController", NifTimeControllerHeader.HeaderSize),
            AddBlock("NiAlphaController", NifTimeControllerHeader.HeaderSize),
            AddBlock("NiAlphaController", NifTimeControllerHeader.HeaderSize),
        };
        var interpolatorRefs = new[]
        {
            AddBlock("NiFloatInterpolator", 8),
            AddBlock("NiFloatInterpolator", 8),
            AddBlock("NiFloatInterpolator", 8),
        };
        var floatDataRefs = new[]
        {
            AddBlock("NiFloatData", 48),
            AddBlock("NiFloatData", 48),
            AddBlock("NiFloatData", 48),
        };
        const int controlledBlockStride = 29;
        const int sequenceTailSize = 32;
        var sequenceRef = AddBlock(
            "NiControllerSequence", 12 + 3 * controlledBlockStride + sequenceTailSize);
        var bsxRef = AddBlock("BSXFlags", 8);
        nif.BlockCount = nif.Blocks.Count;

        for (var i = 0; i < controllerRefs.Length; i++)
        {
            var offset = nif.Blocks[controllerRefs[i]].DataOffset;
            WriteInt32(data, offset, -1); // next controller
            WriteUInt16(data, offset + 4, 0x0008); // active + loop
            WriteSingle(data, offset + 6, 1f);
            WriteSingle(data, offset + 10, 0f);
            WriteSingle(data, offset + 14, 0f);
            WriteSingle(data, offset + 18, 45f);
            WriteInt32(data, offset + 22, materialRefs[i]);

            var interpolatorOffset = nif.Blocks[interpolatorRefs[i]].DataOffset;
            WriteSingle(data, interpolatorOffset, float.MaxValue);
            WriteInt32(data, interpolatorOffset + 4, floatDataRefs[i]);
        }

        WriteFloatKeys(data, nif.Blocks[floatDataRefs[0]].DataOffset,
        [
            (0f, 0f),
            (2.3333f, 0.5f),
            (3f, 0.5f),
            (5.3333f, 0f),
            (45f, 0f),
        ]);
        WriteFloatKeys(data, nif.Blocks[floatDataRefs[1]].DataOffset,
        [
            (0f, 0f),
            (2.3333f, 0.25f),
            (3f, 0.35f),
            (5.3333f, 0f),
            (45f, 0f),
        ]);
        WriteFloatKeys(data, nif.Blocks[floatDataRefs[2]].DataOffset,
        [
            (0f, 0f),
            (0.6667f, 0.05f),
            (4f, 0.05f),
            (6.3333f, 0f),
            (45f, 0f),
        ]);

        var sequenceOffset = nif.Blocks[sequenceRef].DataOffset;
        WriteInt32(data, sequenceOffset, 0); // Idle string
        WriteUInt32(data, sequenceOffset + 4, 3);
        WriteUInt32(data, sequenceOffset + 8, 3); // array grow-by
        for (var i = 0; i < 3; i++)
        {
            var controlled = sequenceOffset + 12 + i * controlledBlockStride;
            WriteInt32(data, controlled, interpolatorRefs[i]);
            WriteInt32(data, controlled + 4, controllerRefs[i]);
            data[controlled + 8] = 0;
            WriteInt32(data, controlled + 9, 1 + i); // target name
            WriteInt32(data, controlled + 13, 4); // NiMaterialProperty
            WriteInt32(data, controlled + 17, 5); // NiAlphaController
            WriteInt32(data, controlled + 21, -1);
            WriteInt32(data, controlled + 25, -1);
        }

        var tail = sequenceOffset + 12 + 3 * controlledBlockStride;
        WriteSingle(data, tail, 1f); // weight
        WriteInt32(data, tail + 4, -1); // text keys
        WriteInt32(data, tail + 8, 0); // cycle loop
        WriteSingle(data, tail + 12, 1f);
        WriteSingle(data, tail + 16, 0f);
        WriteSingle(data, tail + 20, 45f);
        WriteInt32(data, tail + 24, -1); // manager
        WriteInt32(data, tail + 28, -1); // accum root

        var bsxOffset = nif.Blocks[bsxRef].DataOffset;
        WriteInt32(data, bsxOffset, -1); // name
        WriteUInt32(data, bsxOffset + 4, 1u); // Animated

        return new Fixture(data, nif, materialRefs, bsxOffset + 4);
    }

    private static void WriteFloatKeys(byte[] data, int offset, (float Time, float Value)[] keys)
    {
        WriteUInt32(data, offset, (uint)keys.Length);
        WriteUInt32(data, offset + 4, (uint)NifKeyInterpolation.Linear);
        for (var i = 0; i < keys.Length; i++)
        {
            WriteSingle(data, offset + 8 + i * 8, keys[i].Time);
            WriteSingle(data, offset + 12 + i * 8, keys[i].Value);
        }
    }

    private static void WriteInt32(byte[] data, int offset, int value) =>
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset, 4), value);

    private static void WriteUInt32(byte[] data, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset, 4), value);

    private static void WriteUInt16(byte[] data, int offset, ushort value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset, 2), value);

    private static void WriteSingle(byte[] data, int offset, float value) =>
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(offset, 4), value);

    private static void AssertCurve(NifFloatKey[] actual, (float Time, float Value)[] expected)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].Time, actual[i].Time, 3);
            Assert.Equal(expected[i].Value, actual[i].Value, 4);
        }
    }

    private sealed record Fixture(
        byte[] Data,
        NifInfo Nif,
        int[] MaterialRefs,
        int BsxFlagsOffset);
}
