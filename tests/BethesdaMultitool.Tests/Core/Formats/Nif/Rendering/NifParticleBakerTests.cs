using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Particles;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

/// <summary>
///     Covers <see cref="NifParticleBaker" />: deterministic output, particle counts within range, spawn
///     positions inside the emitter volume, and that a Bomb-vortex modifier pushes particles outward. Uses
///     synthetic <see cref="ParticleSystemDefinition" />s so it runs in CI (no NIF fixture needed).
/// </summary>
public sealed class NifParticleBakerTests
{
    private static ParticleSystemDefinition BoxSystem(params ParticleModifierDefinition[] extraModifiers)
    {
        var emitter = new ParticleEmitterDefinition
        {
            Kind = ParticleModifierKind.Emitter,
            Shape = ParticleEmitterShape.Box,
            Width = 20f, Height = 10f, Depth = 6f,
            Speed = 5f, SpeedVariation = 1f,
            LifeSpan = 2f, LifeSpanVariation = 0.5f,
            InitialRadius = 4f,
            InitialColor = new Vector4(1f, 0.5f, 0.2f, 1f),
            EmissionAxis = Vector3.UnitZ,
        };
        var def = new ParticleSystemDefinition { BlockIndex = 7, WorldSpace = false, Capacity = 200 };
        def.Modifiers.Add(emitter);
        def.Emitter = emitter;
        def.Modifiers.Add(new ParticleModifierDefinition { Kind = ParticleModifierKind.AgeDeath });
        def.Modifiers.AddRange(extraModifiers);
        def.Modifiers.Add(new ParticleModifierDefinition { Kind = ParticleModifierKind.Position });
        return def;
    }

    [Fact]
    public void Bake_IsDeterministic_SameDefinitionSameOutput()
    {
        var a = NifParticleBaker.Bake(BoxSystem());
        var b = NifParticleBaker.Bake(BoxSystem());

        Assert.Equal(a.Count, b.Count);
        for (var i = 0; i < a.Count; i++)
        {
            Assert.Equal(a[i].Position, b[i].Position);
            Assert.Equal(a[i].Size, b[i].Size);
            Assert.Equal(a[i].Color, b[i].Color);
        }
    }

    [Fact]
    public void Bake_ProducesParticlesWithinCapAndSizeAndColor()
    {
        var grow = new GrowFadeModifierDefinition
        {
            Kind = ParticleModifierKind.GrowFade, GrowTime = 0.3f, FadeTime = 0.5f, BaseScale = 0f,
        };
        var baked = NifParticleBaker.Bake(BoxSystem(grow));

        Assert.NotEmpty(baked);
        Assert.True(baked.Count <= ParticleBakeOptions.Default.MaxParticles);
        Assert.All(baked, p =>
        {
            Assert.True(p.Size > 0f);
            Assert.Equal(new Vector4(1f, 0.5f, 0.2f, 1f), p.Color); // emitter InitialColor fallback
        });
    }

    // A mesh emitter with no drift (Speed 0, no Bomb/Gravity, no Position modifier) so the baked positions ARE
    // the spawn distribution — isolates the mesh-volume spawn that NifParticleSystemExtractor.ResolveMeshEmitterBounds
    // feeds via MeshBoundsMin/Max.
    private static ParticleSystemDefinition MeshSystem(Vector3 min, Vector3 max)
    {
        var emitter = new ParticleEmitterDefinition
        {
            Kind = ParticleModifierKind.Emitter,
            Shape = ParticleEmitterShape.Mesh,
            Speed = 0f,
            LifeSpan = 2f,
            InitialRadius = 1f,
            MeshBoundsMin = min,
            MeshBoundsMax = max,
        };
        var def = new ParticleSystemDefinition { BlockIndex = 9, WorldSpace = false, Capacity = 400 };
        def.Modifiers.Add(emitter);
        def.Emitter = emitter;
        def.Modifiers.Add(new ParticleModifierDefinition { Kind = ParticleModifierKind.AgeDeath });
        return def;
    }

    [Fact]
    public void Bake_MeshEmitterWithBounds_SpawnsAcrossColumnVolume()
    {
        // A tall column along X (bounds [-50..50] in X, thin in Y/Z) — e.g. an NV whirlwind dust column.
        var min = new Vector3(-50f, -3f, -3f);
        var max = new Vector3(50f, 3f, 3f);
        var baked = NifParticleBaker.Bake(MeshSystem(min, max));
        Assert.NotEmpty(baked);

        var xSpan = baked.Max(p => p.Position.X) - baked.Min(p => p.Position.X);
        var ySpan = baked.Max(p => p.Position.Y) - baked.Min(p => p.Position.Y);
        var zSpan = baked.Max(p => p.Position.Z) - baked.Min(p => p.Position.Z);

        Assert.True(xSpan > 80f, $"particles should fill the column's long axis, got X span {xSpan:F1}");
        Assert.True(ySpan < 12f && zSpan < 12f, $"column stays thin off-axis (Y {ySpan:F1}, Z {zSpan:F1})");
        Assert.All(baked, p =>
        {
            Assert.InRange(p.Position.X, min.X - 0.01f, max.X + 0.01f);
            Assert.InRange(p.Position.Y, min.Y - 0.01f, max.Y + 0.01f);
            Assert.InRange(p.Position.Z, min.Z - 0.01f, max.Z + 0.01f);
        });
    }

    [Fact]
    public void Bake_MeshEmitterWithoutBounds_CollapsesToPoint()
    {
        // Zero bounds ⇒ the baker falls back to point emission (the pre-fix behaviour). With no drift every
        // particle sits at the origin, so the cloud has no spread — proving the AABB is what creates the volume.
        var baked = NifParticleBaker.Bake(MeshSystem(Vector3.Zero, Vector3.Zero));
        Assert.NotEmpty(baked);
        Assert.All(baked, p => Assert.True(p.Position.Length() < 0.01f, $"expected point emit, got {p.Position}"));
    }

    [Fact]
    public void Bake_NoEmitter_ReturnsEmpty()
    {
        var def = new ParticleSystemDefinition { BlockIndex = 1, Capacity = 100 };
        Assert.Empty(NifParticleBaker.Bake(def));
    }

    [Fact]
    public void Bake_BombVortex_PushesParticlesOutwardBeyondEmitterVolume()
    {
        // A spherical bomb with a strong positive DeltaV should fling particles well past the 20×10×6
        // emitter box (max corner radius ~11.7). Compare against the same system without the bomb.
        var bomb = new BombModifierDefinition
        {
            Kind = ParticleModifierKind.Bomb,
            DeltaV = 200f,
            DecayType = 0,        // no decay → constant outward push
            SymmetryType = 0,     // spherical
            BombAxis = Vector3.UnitZ,
        };

        var withoutBomb = NifParticleBaker.Bake(BoxSystem());
        var withBomb = NifParticleBaker.Bake(BoxSystem(bomb));

        var maxRadiusNoBomb = withoutBomb.Max(p => p.Position.Length());
        var maxRadiusBomb = withBomb.Max(p => p.Position.Length());

        Assert.True(maxRadiusBomb > maxRadiusNoBomb * 2f,
            $"bomb should disperse particles ({maxRadiusBomb:F0} vs {maxRadiusNoBomb:F0})");
    }
}
