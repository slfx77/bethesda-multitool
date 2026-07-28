using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Particles;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Particles;

/// <summary>
///     Independent vectors for FX-08. Expected values are hand-calculated from the authored clocks/keys;
///     no production sampler is used to manufacture an expected result.
/// </summary>
public sealed class ParticleRateControllerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Parser_PreservesQuadraticRateKeysInBothEndianModes(bool bigEndian)
    {
        var bytes = new byte[8 + 2 * 16];
        WriteUInt32(bytes, 0, 2, bigEndian);
        WriteUInt32(bytes, 4, (uint)ParticleRateInterpolation.Quadratic, bigEndian);
        WriteFloat(bytes, 8, 0f, bigEndian);
        WriteFloat(bytes, 12, 2f, bigEndian);
        WriteFloat(bytes, 16, 3f, bigEndian);
        WriteFloat(bytes, 20, 4f, bigEndian);
        WriteFloat(bytes, 24, 1f, bigEndian);
        WriteFloat(bytes, 28, 10f, bigEndian);
        WriteFloat(bytes, 32, 11f, bigEndian);
        WriteFloat(bytes, 36, 12f, bigEndian);

        var success = NifParticleSystemParser.TryReadRateKeys(
            bytes, 0, bytes.Length, bigEndian, out var interpolation, out var keys);

        Assert.True(success);
        Assert.Equal(ParticleRateInterpolation.Quadratic, interpolation);
        Assert.Equal(2, keys.Count);
        Assert.Equal(new ParticleRateKey(0f, 2f, 3f, 4f), keys[0]);
        Assert.Equal(new ParticleRateKey(1f, 10f, 11f, 12f), keys[1]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Parser_RejectsUnsupportedInterpolationWithoutInventingARate(bool bigEndian)
    {
        var bytes = new byte[16];
        WriteUInt32(bytes, 0, 1, bigEndian);
        WriteUInt32(bytes, 4, 99, bigEndian);
        WriteFloat(bytes, 8, 0f, bigEndian);
        WriteFloat(bytes, 12, 500f, bigEndian);

        var success = NifParticleSystemParser.TryReadRateKeys(
            bytes, 0, bytes.Length, bigEndian, out _, out var keys);

        Assert.False(success);
        Assert.Empty(keys);
    }

    [Fact]
    public void Sample_InterpolatesLinearKeysAfterSequenceAndControllerClocks()
    {
        var controller = new ParticleRateControllerDefinition
        {
            // Wall 3.5 -> sequence 3.5 -> controller phase +1 = 4.5 -> loop 0.5.
            SequenceTiming = new ParticleControllerTiming(
                1f, 0f, 0f, 4f, ParticleControllerCycle.Loop),
            ControllerTiming = new ParticleControllerTiming(
                1f, 1f, 0f, 4f, ParticleControllerCycle.Loop),
            Interpolation = ParticleRateInterpolation.Linear,
            Keys = [new ParticleRateKey(0f, 0f), new ParticleRateKey(4f, 40f)]
        };

        Assert.Equal(5f, controller.Sample(3.5f), 5);
    }

    [Fact]
    public void Sample_LoopWrapsWhileClampHoldsFinalRate()
    {
        ParticleRateKey[] keys = [new(1f, 10f), new(3f, 30f)];
        var looping = new ParticleRateControllerDefinition
        {
            ControllerTiming = new ParticleControllerTiming(
                1f, 0f, 1f, 3f, ParticleControllerCycle.Loop),
            Keys = keys
        };
        var clamped = new ParticleRateControllerDefinition
        {
            ControllerTiming = new ParticleControllerTiming(
                1f, 0f, 1f, 3f, ParticleControllerCycle.Clamp),
            Keys = keys
        };

        // 3.5 wraps to 1.5 for a two-second loop: 25% from 10 to 30 = 15.
        Assert.Equal(15f, looping.Sample(3.5f), 5);
        Assert.Equal(30f, clamped.Sample(3.5f), 5);
    }

    [Fact]
    public void Bake_AdvancingPulsedControllerChangesLiveCountWithoutExceedingCapacity()
    {
        var def = PulsedSystem(3);
        var quiet = NifParticleBaker.Bake(def, new ParticleBakeOptions
        {
            TimeStep = 0.25f,
            SettleMarginSeconds = 0f,
            SnapshotTimeSeconds = 0.5f,
            MaxParticles = 100
        });
        var active = NifParticleBaker.Bake(def, new ParticleBakeOptions
        {
            TimeStep = 0.25f,
            SettleMarginSeconds = 0f,
            SnapshotTimeSeconds = 2f,
            MaxParticles = 100
        });

        // Quiet replay window [-0.5, 0.5] is clamped to the zero-rate section. The active replay window
        // [1, 2] integrates the ramp and immediately reaches, but never exceeds, the authored capacity.
        Assert.Empty(quiet);
        Assert.Equal(3, active.Count);
    }

    [Fact]
    public void Bake_TimeSampledControllerIsDeterministicForSameSeedAndTimestamp()
    {
        var def = PulsedSystem(19);
        var options = new ParticleBakeOptions
        {
            TimeStep = 1f / 30f,
            SettleMarginSeconds = 0.25f,
            SnapshotTimeSeconds = 1.75f,
            MaxParticles = 2048
        };

        var first = NifParticleBaker.Bake(def, options);
        var second = NifParticleBaker.Bake(def, options);

        Assert.NotEmpty(first);
        Assert.Equal(first.Count, second.Count);
        for (var i = 0; i < first.Count; i++)
        {
            Assert.Equal(first[i], second[i]);
        }
    }

    private static ParticleSystemDefinition PulsedSystem(int capacity)
    {
        var emitter = new ParticleEmitterDefinition
        {
            Kind = ParticleModifierKind.Emitter,
            Shape = ParticleEmitterShape.Box,
            Width = 2f,
            Height = 2f,
            Depth = 2f,
            LifeSpan = 1f,
            InitialRadius = 1f,
            BirthRateController = new ParticleRateControllerDefinition
            {
                ControllerTiming = new ParticleControllerTiming(
                    1f, 0f, 0f, 2f, ParticleControllerCycle.Clamp),
                Interpolation = ParticleRateInterpolation.Linear,
                Keys =
                [
                    new ParticleRateKey(0f, 0f),
                    new ParticleRateKey(1f, 0f),
                    new ParticleRateKey(2f, 100f)
                ]
            }
        };
        var def = new ParticleSystemDefinition
        {
            BlockIndex = 0x23,
            Capacity = capacity,
            Emitter = emitter
        };
        def.Modifiers.Add(emitter);
        def.Modifiers.Add(new ParticleModifierDefinition { Kind = ParticleModifierKind.AgeDeath });
        return def;
    }

    private static void WriteUInt32(byte[] data, int offset, uint value, bool bigEndian)
    {
        if (bigEndian)
        {
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offset, 4), value);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset, 4), value);
        }
    }

    private static void WriteFloat(byte[] data, int offset, float value, bool bigEndian)
    {
        var bits = BitConverter.SingleToInt32Bits(value);
        if (bigEndian)
        {
            BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(offset, 4), bits);
        }
        else
        {
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset, 4), bits);
        }
    }
}