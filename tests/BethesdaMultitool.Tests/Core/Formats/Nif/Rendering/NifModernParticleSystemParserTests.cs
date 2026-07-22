using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Particles;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

/// <summary>
///     Pins the two Bethesda 20.2 NiParticleSystem inheritance layouts independently of the production
///     parser. Synthetic records cover both byte orders; installed retail fixtures prove the same cursors
///     against Skyrim LE (BS 83) and Fallout 4 (BS 130).
/// </summary>
public sealed class NifModernParticleSystemParserTests
{
    private const string SkyrimFixture =
        @"TestOutput\particle_modern\skyrim\meshes\effects\fxdustbursttiny.nif";
    private const string Fallout4Fixture =
        @"TestOutput\particle_modern\fo4\Meshes\Effects\MPSSmokeDustDrift.nif";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Parse_SkyrimNiGeometryLayout_RetainsOrderedChainInBothByteOrders(bool bigEndian)
    {
        var (data, nif) = BuildFixture(83, bigEndian, capacity: 30);

        var definition = Assert.IsType<ParticleSystemDefinition>(
            NifParticleSystemParser.Parse(data, nif, 0));

        Assert.Equal(ParticleSystemSourceLayout.SkyrimNiGeometry, definition.SourceLayout);
        Assert.Equal(30, definition.Capacity);
        Assert.True(definition.WorldSpace);
        Assert.Equal(1.5f, definition.AspectRatio, 4);
        Assert.Equal([2, 3], definition.Modifiers.Select(modifier => modifier.BlockIndex));
        AssertCylinderEmitter(definition);
        Assert.Contains("BSPSysScaleModifier", definition.UnsupportedSimulatorSteps[0]);
        Assert.Contains("partial", definition.SupportTelemetry);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Parse_Fallout4BsGeometryLayout_RetainsOrderedChainInBothByteOrders(bool bigEndian)
    {
        var (data, nif) = BuildFixture(130, bigEndian, capacity: 90);

        var definition = Assert.IsType<ParticleSystemDefinition>(
            NifParticleSystemParser.Parse(data, nif, 0));

        Assert.Equal(ParticleSystemSourceLayout.BsGeometry, definition.SourceLayout);
        Assert.Equal(90, definition.Capacity);
        Assert.True(definition.WorldSpace);
        Assert.Equal([2, 3], definition.Modifiers.Select(modifier => modifier.BlockIndex));
        AssertCylinderEmitter(definition);
        Assert.Equal("BSPSysScaleModifier", definition.Modifiers[1].SourceTypeName);
    }

    [Fact]
    public void Parse_InstalledSkyrimFixture_NormalizesBothRetailSystems()
    {
        BucketBTestGuard.SkipUnlessEnabled();

        var path = SampleFileFixture.FindSamplePath(SkyrimFixture);
        Assert.SkipWhen(path is null, "installed Skyrim particle fixture not available");

        var (data, nif) = Load(path!);
        var systems = ParseSystems(data, nif);

        Assert.Equal(2, systems.Length);
        Assert.Equal([30, 6], systems.Select(system => system.Capacity));
        Assert.All(systems, system =>
        {
            Assert.Equal(ParticleSystemSourceLayout.SkyrimNiGeometry, system.SourceLayout);
            Assert.IsType<ParticleEmitterDefinition>(system.Emitter);
            Assert.False(string.IsNullOrWhiteSpace(system.DiffuseTexturePath));
            Assert.Contains(system.Modifiers, modifier => modifier.SourceTypeName == "BSPSysScaleModifier");
            Assert.Contains(system.UnsupportedSimulatorSteps, step => step.Contains("BSPSysLODModifier"));
        });
        Assert.Equal(14, systems[0].Modifiers.Count);
        Assert.Equal(12, systems[1].Modifiers.Count);
    }

    [Fact]
    public void Parse_InstalledFallout4Fixture_NormalizesRetailSystem()
    {
        BucketBTestGuard.SkipUnlessEnabled();

        var path = SampleFileFixture.FindSamplePath(Fallout4Fixture);
        Assert.SkipWhen(path is null, "installed Fallout 4 particle fixture not available");

        var (data, nif) = Load(path!);
        var definition = Assert.Single(ParseSystems(data, nif));

        Assert.Equal(ParticleSystemSourceLayout.BsGeometry, definition.SourceLayout);
        Assert.Equal(90, definition.Capacity);
        Assert.Equal(14, definition.Modifiers.Count);
        Assert.False(string.IsNullOrWhiteSpace(definition.DiffuseTexturePath));
        Assert.Equal(ParticleEmitterShape.Cylinder,
            Assert.IsType<ParticleEmitterDefinition>(definition.Emitter).Shape);
        Assert.Contains(definition.UnsupportedSimulatorSteps, step => step.Contains("BSPSysScaleModifier"));
        Assert.Contains(definition.UnsupportedSimulatorSteps, step => step.Contains("BSWindModifier"));
    }

    [Fact]
    public void IsParticleSystem_AcceptsOnlySchemaBackedBethesdaAlias()
    {
        Assert.True(NifParticleSystemParser.IsParticleSystem("BSStripParticleSystem"));
        Assert.False(NifParticleSystemParser.IsParticleSystem("BSParticleSystem"));
        Assert.False(NifParticleSystemParser.IsParticleSystem("NiPSParticleSystem"));
    }

    private static void AssertCylinderEmitter(ParticleSystemDefinition definition)
    {
        var emitter = Assert.IsType<ParticleEmitterDefinition>(definition.Emitter);
        Assert.Equal(ParticleEmitterShape.Cylinder, emitter.Shape);
        Assert.Equal(3f, emitter.Speed, 4);
        Assert.Equal(2f, emitter.LifeSpan, 4);
        Assert.Equal(4f, emitter.Radius, 4);
        Assert.Equal(8f, emitter.Height, 4);
    }

    private static ParticleSystemDefinition[] ParseSystems(byte[] data, NifInfo nif) =>
        nif.Blocks.Select((block, index) => (block, index))
            .Where(item => NifParticleSystemParser.IsParticleSystem(item.block.TypeName))
            .Select(item => NifParticleSystemParser.Parse(data, nif, item.index))
            .OfType<ParticleSystemDefinition>()
            .ToArray();

    private static (byte[] Data, NifInfo Nif) Load(string path)
    {
        var data = File.ReadAllBytes(path);
        return (data, Assert.IsType<NifInfo>(NifParser.Parse(data)));
    }

    private static (byte[] Data, NifInfo Nif) BuildFixture(
        uint bsVersion, bool bigEndian, ushort capacity)
    {
        var blocks = new (string Type, byte[] Payload)[]
        {
            ("NiParticleSystem", BuildSystem(bsVersion, bigEndian)),
            ("NiPSysData", BuildData(capacity, bigEndian)),
            ("NiPSysCylinderEmitter", BuildCylinderEmitter(bigEndian)),
            ("BSPSysScaleModifier", BuildModifierBase(bigEndian)),
            ("BSEffectShaderProperty", []),
            ("NiAlphaProperty", []),
        };

        var nif = new NifInfo
        {
            BinaryVersion = 0x14020007,
            UserVersion = 12,
            BsVersion = bsVersion,
            IsBigEndian = bigEndian,
            BlockCount = blocks.Length,
        };
        using var stream = new MemoryStream();
        for (var index = 0; index < blocks.Length; index++)
        {
            var offset = checked((int)stream.Length);
            stream.Write(blocks[index].Payload);
            nif.Blocks.Add(new BlockInfo
            {
                Index = index,
                TypeName = blocks[index].Type,
                DataOffset = offset,
                Size = blocks[index].Payload.Length,
            });
        }

        return (stream.ToArray(), nif);
    }

    private static byte[] BuildSystem(uint bsVersion, bool be)
    {
        using var stream = new MemoryStream();
        WriteI32(stream, 0, be); // Name
        WriteU32(stream, 0, be); // Extra data count
        WriteI32(stream, -1, be); // Controller
        WriteU32(stream, bsVersion < 100 ? 0x0008000Eu : 0x0000000Eu, be);
        WriteZeros(stream, 12 + 36);
        WriteF32(stream, 1f, be); // Scale
        WriteI32(stream, -1, be); // Collision

        if (bsVersion < 100)
        {
            WriteI32(stream, 1, be); // Data
            WriteI32(stream, -1, be); // Skin
            WriteMaterialData(stream, be);
            WriteI32(stream, 4, be); // Shader
            WriteI32(stream, 5, be); // Alpha
        }
        else
        {
            WriteZeros(stream, 16); // Bounding sphere
            WriteI32(stream, -1, be); // Skin
            WriteI32(stream, 4, be); // Shader
            WriteI32(stream, 5, be); // Alpha
            WriteU64(stream, 0x0840200002000031UL, be); // Vertex descriptor
        }

        WriteU16(stream, 10, be);
        WriteU16(stream, 20, be);
        WriteU16(stream, 30, be);
        WriteU16(stream, 40, be);
        if (bsVersion >= 100)
        {
            WriteI32(stream, 1, be); // Data moved behind the distance bands
        }

        stream.WriteByte(1); // World space
        WriteU32(stream, 2, be);
        WriteI32(stream, 2, be);
        WriteI32(stream, 3, be);
        return stream.ToArray();
    }

    private static byte[] BuildData(ushort capacity, bool be)
    {
        var data = new byte[55];
        WriteU16(data.AsSpan(4), capacity, be); // Group ID precedes BS Max Vertices
        data[8] = 1; // Has Vertices (runtime storage; no serialized array in Bethesda 20.2)
        WriteF32(data.AsSpan(51), 1.5f, be); // Aspect ratio after empty Subtexture Offsets
        return data;
    }

    private static byte[] BuildCylinderEmitter(bool be)
    {
        var data = new byte[81];
        WriteU32(data.AsSpan(4), 1000, be); // Modifier order
        WriteI32(data.AsSpan(8), 0, be); // Target system
        data[12] = 1; // Active
        WriteF32(data.AsSpan(13), 3f, be); // Speed
        WriteF32(data.AsSpan(13 + 24), 1f, be); // Initial color R
        WriteF32(data.AsSpan(13 + 28), 1f, be);
        WriteF32(data.AsSpan(13 + 32), 1f, be);
        WriteF32(data.AsSpan(13 + 36), 1f, be);
        WriteF32(data.AsSpan(13 + 40), 1f, be); // Initial radius
        WriteF32(data.AsSpan(13 + 48), 2f, be); // Lifespan
        WriteI32(data.AsSpan(13 + 56), -1, be); // Emitter object
        WriteF32(data.AsSpan(13 + 60), 4f, be); // Cylinder radius
        WriteF32(data.AsSpan(13 + 64), 8f, be); // Cylinder height
        return data;
    }

    private static byte[] BuildModifierBase(bool be)
    {
        var data = new byte[17];
        WriteU32(data.AsSpan(4), 3000, be);
        WriteI32(data.AsSpan(8), 0, be);
        data[12] = 1;
        WriteU32(data.AsSpan(13), 0, be); // BSPSysScaleModifier.Num Scales
        return data;
    }

    private static void WriteMaterialData(Stream stream, bool be)
    {
        WriteU32(stream, 0, be);
        WriteI32(stream, -1, be);
        stream.WriteByte(0);
    }

    private static void WriteZeros(Stream stream, int count) => stream.Write(new byte[count]);

    private static void WriteI32(Stream stream, int value, bool be)
    {
        Span<byte> bytes = stackalloc byte[4];
        WriteI32(bytes, value, be);
        stream.Write(bytes);
    }

    private static void WriteI32(Span<byte> bytes, int value, bool be)
    {
        if (be) BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        else BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
    }

    private static void WriteU16(Stream stream, ushort value, bool be)
    {
        Span<byte> bytes = stackalloc byte[2];
        WriteU16(bytes, value, be);
        stream.Write(bytes);
    }

    private static void WriteU16(Span<byte> bytes, ushort value, bool be)
    {
        if (be) BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
        else BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
    }

    private static void WriteU32(Stream stream, uint value, bool be)
    {
        Span<byte> bytes = stackalloc byte[4];
        WriteU32(bytes, value, be);
        stream.Write(bytes);
    }

    private static void WriteU32(Span<byte> bytes, uint value, bool be)
    {
        if (be) BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        else BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
    }

    private static void WriteU64(Stream stream, ulong value, bool be)
    {
        Span<byte> bytes = stackalloc byte[8];
        if (be) BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
        else BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteF32(Stream stream, float value, bool be)
    {
        Span<byte> bytes = stackalloc byte[4];
        WriteF32(bytes, value, be);
        stream.Write(bytes);
    }

    private static void WriteF32(Span<byte> bytes, float value, bool be) =>
        WriteI32(bytes, BitConverter.SingleToInt32Bits(value), be);
}
