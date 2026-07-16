using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Particles;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

public sealed class ParticleLiveSnapshotTests
{
    [Fact]
    public void Build_AdvancesQuietPulseAndEmitsCapacityBoundedGpuTopology()
    {
        var runtime = PulsedRuntime(capacity: 3);

        var quiet = ParticleLiveSnapshotBuilder.Build(runtime, 0.5f);
        var active = ParticleLiveSnapshotBuilder.Build(runtime, 2f);

        Assert.Empty(quiet.Vertices);
        Assert.Empty(quiet.Indices);
        Assert.Empty(quiet.Centers);
        Assert.Equal(3, active.Centers.Length);
        Assert.Equal(3 * 4, active.Vertices.Length);
        Assert.Equal(3 * ParticleDepthSort.IndicesPerQuad, active.Indices.Length);
        Assert.All(active.Centers, center =>
        {
            Assert.InRange(center.X, 9f, 11f);
            Assert.InRange(center.Y, 19f, 21f);
            Assert.InRange(center.Z, 29f, 31f);
        });
    }

    [Fact]
    public void Build_SameDefinitionAndTimeProducesIdenticalUploadData()
    {
        var runtime = PulsedRuntime(capacity: 19);

        var first = ParticleLiveSnapshotBuilder.Build(runtime, 1.75f);
        var second = ParticleLiveSnapshotBuilder.Build(runtime, 1.75f);

        Assert.NotEmpty(first.Vertices);
        Assert.Equal<ushort>(first.Indices, second.Indices);
        Assert.Equal<Vector3>(first.Centers, second.Centers);
        Assert.Equal(first.Vertices.Length, second.Vertices.Length);
        for (var i = 0; i < first.Vertices.Length; i++)
        {
            Assert.Equal(first.Vertices[i].Position, second.Vertices[i].Position);
            Assert.Equal(first.Vertices[i].Normal, second.Vertices[i].Normal);
            Assert.Equal(first.Vertices[i].TexCoord, second.Vertices[i].TexCoord);
            Assert.Equal(first.Vertices[i].VertexColor, second.Vertices[i].VertexColor);
            Assert.Equal(first.Vertices[i].Tangent, second.Vertices[i].Tangent);
            Assert.Equal(first.Vertices[i].Bitangent, second.Vertices[i].Bitangent);
        }
    }

    [Fact]
    public void QuantizeFrameTime_IsStableWithinFixedControllerTick()
    {
        Assert.Equal(1f, ParticleLiveSettings.QuantizeFrameTime(1.0001));
        Assert.Equal(1f, ParticleLiveSettings.QuantizeFrameTime(1.032));
        Assert.Equal(31f / 30f, ParticleLiveSettings.QuantizeFrameTime(1.034), 6);
        Assert.Equal(0f, ParticleLiveSettings.QuantizeFrameTime(double.NaN));
        Assert.Equal(0f, ParticleLiveSettings.QuantizeFrameTime(double.PositiveInfinity));
    }

    [Fact]
    public void PersistentDecodedCache_IsKeptForStaticModeAndBypassedForLiveMode()
    {
        Assert.True(ParticleLiveSettings.UsePersistentDecodedMeshCache(
            liveParticlesEnabled: false, isNegative: true, containsParticleSource: true));
        Assert.True(ParticleLiveSettings.UsePersistentDecodedMeshCache(
            liveParticlesEnabled: true, isNegative: false, containsParticleSource: false));
        Assert.False(ParticleLiveSettings.UsePersistentDecodedMeshCache(
            liveParticlesEnabled: true, isNegative: false, containsParticleSource: true));
        Assert.False(ParticleLiveSettings.UsePersistentDecodedMeshCache(
            liveParticlesEnabled: true, isNegative: true, containsParticleSource: false));
    }

    private static ParticleRuntimeDefinition PulsedRuntime(int capacity)
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
                    new ParticleRateKey(2f, 100f),
                ],
            },
        };
        var definition = new ParticleSystemDefinition
        {
            BlockIndex = 0x23,
            Capacity = capacity,
            Emitter = emitter,
        };
        definition.Modifiers.Add(emitter);
        definition.Modifiers.Add(new ParticleModifierDefinition { Kind = ParticleModifierKind.AgeDeath });

        return new ParticleRuntimeDefinition(
            definition,
            Matrix4x4.CreateTranslation(10f, 20f, 30f),
            new ParticleBakeOptions
            {
                TimeStep = 0.25f,
                SettleMarginSeconds = 0f,
                MaxParticles = 100,
            });
    }
}
