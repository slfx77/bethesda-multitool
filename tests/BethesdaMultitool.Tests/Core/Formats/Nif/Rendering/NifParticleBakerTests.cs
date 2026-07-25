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
            // Recovered declination is elevation: pi/2 points along EmissionAxis, while zero
            // lies in its perpendicular plane.
            Declination = MathF.PI / 2f,
            LifeSpan = 2f, LifeSpanVariation = 0.5f,
            InitialRadius = 4f,
            InitialColor = new Vector4(1f, 0.5f, 0.2f, 1f),
            EmissionAxis = Vector3.UnitZ
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
            Kind = ParticleModifierKind.GrowFade, GrowTime = 0.3f, FadeTime = 0.5f, BaseScale = 0f
        };
        var baked = NifParticleBaker.Bake(BoxSystem(grow));

        Assert.NotEmpty(baked);
        Assert.True(baked.Count <= 200, $"authored capacity must be the live limit, got {baked.Count}");
        Assert.All(baked, p =>
        {
            Assert.True(p.Size > 0f);
            Assert.Equal(new Vector4(1f, 0.5f, 0.2f, 1f), p.Color); // emitter InitialColor fallback
        });
    }

    private static ParticleSystemDefinition MeshSystem(
        IReadOnlyList<Vector3> vertices,
        IReadOnlyList<int> triangles,
        ParticleEmitFrom emitFrom = ParticleEmitFrom.FaceSurface,
        ParticleVelocityType velocityType = ParticleVelocityType.UseDirection,
        float speed = 0f,
        bool integrate = false)
    {
        var emitter = new ParticleEmitterDefinition
        {
            Kind = ParticleModifierKind.Emitter,
            Shape = ParticleEmitterShape.Mesh,
            Speed = speed,
            LifeSpan = 2f,
            InitialRadius = 1f,
            EmitFrom = emitFrom,
            VelocityType = velocityType,
            MeshVertices = vertices,
            MeshNormals = vertices.Select(_ => Vector3.UnitZ).ToArray(),
            MeshTriangles = triangles
        };
        var def = new ParticleSystemDefinition { BlockIndex = 9, WorldSpace = false, Capacity = 400 };
        def.Modifiers.Add(emitter);
        def.Emitter = emitter;
        def.Modifiers.Add(new ParticleModifierDefinition { Kind = ParticleModifierKind.AgeDeath });
        if (integrate) def.Modifiers.Add(new ParticleModifierDefinition { Kind = ParticleModifierKind.Position });
        return def;
    }

    [Fact]
    public void Bake_MeshFaceSurface_SpawnsOnAuthoredTrianglesNotAabbVolume()
    {
        Vector3[] vertices =
        [
            new(-50f, -3f, 0f), new(50f, -3f, 0f), new(50f, 3f, 0f), new(-50f, 3f, 0f)
        ];
        int[] triangles = [0, 1, 2, 0, 2, 3];
        var baked = NifParticleBaker.Bake(MeshSystem(vertices, triangles));
        Assert.NotEmpty(baked);

        var xSpan = baked.Max(p => p.Position.X) - baked.Min(p => p.Position.X);
        var ySpan = baked.Max(p => p.Position.Y) - baked.Min(p => p.Position.Y);
        Assert.True(xSpan > 80f, $"particles should fill the column's long axis, got X span {xSpan:F1}");
        Assert.True(ySpan > 4f, $"particles should cover the authored face width, got Y span {ySpan:F1}");
        Assert.All(baked, p =>
        {
            Assert.InRange(p.Position.X, -50.01f, 50.01f);
            Assert.InRange(p.Position.Y, -3.01f, 3.01f);
            Assert.InRange(p.Position.Z, -0.001f, 0.001f);
        });
    }

    [Fact]
    public void Bake_MeshNormals_DriveInitialVelocity()
    {
        Vector3[] vertices = [new(-1f, -1f, 0f), new(1f, -1f, 0f), new(0f, 1f, 0f)];
        var baked = NifParticleBaker.Bake(MeshSystem(
            vertices, [0, 1, 2], velocityType: ParticleVelocityType.UseNormals, speed: 10f, integrate: true));
        Assert.NotEmpty(baked);
        Assert.All(baked, p => Assert.True(p.Position.Z >= -0.001f, $"normal velocity should move +Z: {p.Position}"));
        Assert.True(baked.Max(p => p.Position.Z) > 5f);
    }

    [Fact]
    public void ComputeEmissionDirection_UsesRecoveredElevationConvention()
    {
        var horizontal = NifParticleBaker.ComputeEmissionDirection(0f, 0f, Vector3.UnitZ);
        var vertical = NifParticleBaker.ComputeEmissionDirection(MathF.PI / 2f, 0f, Vector3.UnitZ);

        Assert.True(horizontal.X > 0.999f && MathF.Abs(horizontal.Z) < 0.001f);
        Assert.True(vertical.Z > 0.999f && MathF.Abs(vertical.X) < 0.001f);
    }

    [Fact]
    public void Bake_AuthoredCapacityOverridesGlobalSafetyCap()
    {
        var def = BoxSystem();
        def = new ParticleSystemDefinition { BlockIndex = def.BlockIndex, Capacity = 19, Emitter = def.Emitter };
        def.Modifiers.Add(def.Emitter!);
        def.Modifiers.Add(new ParticleModifierDefinition { Kind = ParticleModifierKind.AgeDeath });
        var baked = NifParticleBaker.Bake(def, new ParticleBakeOptions { MaxParticles = 2048 });
        Assert.InRange(baked.Count, 1, 19);
    }

    [Fact]
    public void Bake_GrowFadeOnlyAffectsItsAuthoredGeneration()
    {
        var generationZero = NifParticleBaker.Bake(BoxSystem(new GrowFadeModifierDefinition
        {
            Kind = ParticleModifierKind.GrowFade,
            GrowTime = 10f,
            FadeTime = 10f,
            BaseScale = 0f,
            GrowGeneration = 0,
            FadeGeneration = 0
        }));
        var generationOne = NifParticleBaker.Bake(BoxSystem(new GrowFadeModifierDefinition
        {
            Kind = ParticleModifierKind.GrowFade,
            GrowTime = 10f,
            FadeTime = 10f,
            BaseScale = 0f,
            GrowGeneration = 1,
            FadeGeneration = 1
        }));

        Assert.NotEmpty(generationZero);
        Assert.NotEmpty(generationOne);
        Assert.True(generationZero.Average(p => p.Size) < generationOne.Average(p => p.Size) * 0.25f);
        Assert.All(generationOne, p => Assert.Equal(4f, p.Size, 4));
    }

    [Fact]
    public void Bake_AtlasCarriesAuthoredUvRectRotationAndAspect()
    {
        var def = BoxSystem(new RotationModifierDefinition
        {
            Kind = ParticleModifierKind.Rotation,
            RotationSpeed = 1f,
            RotationAngle = 0.25f
        });
        def.SubtextureOffsets = Enumerable.Range(0, 16)
            .Select(i => new Vector4(i % 4 * 0.25f, i / 4 * 0.25f, 0.25f, 0.25f)).ToArray();
        def.AspectRatio = 2f;

        var baked = NifParticleBaker.Bake(def);
        Assert.NotEmpty(baked);
        Assert.All(baked, p =>
        {
            Assert.Equal(0.25f, p.UvRect.Z, 4);
            Assert.Equal(0.25f, p.UvRect.W, 4);
            Assert.Equal(2f, p.AspectRatio, 4);
        });
        Assert.Contains(baked, p => MathF.Abs(p.Rotation) > 0.25f);
        Assert.True(baked.Select(p => p.UvRect).Distinct().Count() > 1);
    }

    [Fact]
    public void Bake_MultipleRotationModifiersIntegrateTheirSumOncePerTick()
    {
        const float dt = 1f / 30f;
        var emitter = new ParticleEmitterDefinition
        {
            Kind = ParticleModifierKind.Emitter,
            Shape = ParticleEmitterShape.Box,
            LifeSpan = dt,
            BirthRate = 30f,
            InitialRadius = 1f
        };
        var def = new ParticleSystemDefinition { BlockIndex = 17, Capacity = 1, Emitter = emitter };
        def.Modifiers.Add(emitter);
        def.Modifiers.Add(new ParticleModifierDefinition { Kind = ParticleModifierKind.AgeDeath });
        def.Modifiers.Add(new RotationModifierDefinition
        {
            Kind = ParticleModifierKind.Rotation,
            RotationAngle = 0.25f,
            RotationSpeed = 1f
        });
        def.Modifiers.Add(new RotationModifierDefinition
        {
            Kind = ParticleModifierKind.Rotation,
            RotationAngle = 0.5f,
            RotationSpeed = 2f
        });

        var particle = Assert.Single(NifParticleBaker.Bake(def, new ParticleBakeOptions
        {
            TimeStep = dt,
            SettleMarginSeconds = 0f,
            MaxParticles = 1
        }));

        Assert.Equal(0.75f + 3f * dt, particle.Rotation, 5);
    }

    [Fact]
    public void Bake_ExecutesForceAndPositionInAuthoredOrder()
    {
        var forceBeforePosition = BoxSystem();
        var position = forceBeforePosition.Modifiers.Single(m => m.Kind == ParticleModifierKind.Position);
        forceBeforePosition.Modifiers.Remove(position);
        var gravity = new GravityModifierDefinition
        {
            Kind = ParticleModifierKind.Gravity,
            HasGravityObject = true,
            GravityObjectTransform = Matrix4x4.Identity,
            GravityAxis = Vector3.UnitX,
            Strength = 100f,
            ForceType = 0
        };
        forceBeforePosition.Modifiers.Add(gravity);
        forceBeforePosition.Modifiers.Add(position);

        var positionBeforeForce = BoxSystem();
        position = positionBeforeForce.Modifiers.Single(m => m.Kind == ParticleModifierKind.Position);
        positionBeforeForce.Modifiers.Remove(position);
        positionBeforeForce.Modifiers.Add(position);
        positionBeforeForce.Modifiers.Add(gravity);

        var forceFirstMeanX = NifParticleBaker.Bake(forceBeforePosition).Average(p => p.Position.X);
        var positionFirstMeanX = NifParticleBaker.Bake(positionBeforeForce).Average(p => p.Position.X);

        Assert.True(forceFirstMeanX > positionFirstMeanX + 1f,
            $"force-before-position should advance farther this snapshot ({forceFirstMeanX:F2} vs {positionFirstMeanX:F2})");
    }

    [Fact]
    public void Bake_NoEmitter_ReturnsEmpty()
    {
        var def = new ParticleSystemDefinition { BlockIndex = 1, Capacity = 100 };
        Assert.Empty(NifParticleBaker.Bake(def));
    }

    [Fact]
    public void Bake_DragWithObject_DampsVelocityAlongDragAxis()
    {
        // The BoxSystem jet rises along +Z. A +Z drag (with a drag object + effectively-infinite range) should
        // damp the vertical velocity, so the cloud rises far less than the undamped jet (engine-accurate
        // anisotropic NiPSysDragModifier behaviour).
        var drag = new DragModifierDefinition
        {
            Kind = ParticleModifierKind.Drag,
            HasDragObject = true,
            DragObjectTransform = Matrix4x4.Identity,
            DragAxis = Vector3.UnitZ,
            Percentage = 0.5f,
            Range = 1e30f,
            RangeFalloff = 1e30f
        };

        // Compare the cloud centroid, not its maximum: a freshly emitted particle can begin at the
        // box's +Z face and therefore pins both maxima before either system integrates velocity.
        var meanZNoDrag = NifParticleBaker.Bake(BoxSystem()).Average(p => p.Position.Z);
        var meanZWithDrag = NifParticleBaker.Bake(BoxSystem(drag)).Average(p => p.Position.Z);

        Assert.True(meanZWithDrag < meanZNoDrag - 2f,
            $"drag along +Z should lower the jet centroid ({meanZWithDrag:F2} vs {meanZNoDrag:F2})");
    }

    [Fact]
    public void Bake_DragWithoutObject_IsNoOp()
    {
        // The engine no-ops NiPSysDragModifier entirely when there is no drag object, so the bake must match the
        // drag-free system exactly (same seed ⇒ identical particles).
        var drag = new DragModifierDefinition
        {
            Kind = ParticleModifierKind.Drag,
            HasDragObject = false, // no drag object ⇒ engine applies no drag
            DragAxis = Vector3.UnitZ,
            Percentage = 0.5f,
            Range = 1e30f,
            RangeFalloff = 1e30f
        };

        var maxZNoDrag = NifParticleBaker.Bake(BoxSystem()).Max(p => p.Position.Z);
        var maxZNoObjDrag = NifParticleBaker.Bake(BoxSystem(drag)).Max(p => p.Position.Z);

        Assert.Equal(maxZNoDrag, maxZNoObjDrag);
    }

    [Fact]
    public void Bake_SpawnWithGenerations_BurstsChildrenOnDeath()
    {
        // A spawn modifier with ≥1 generation and Min/Max=4 should multiply the cloud: every dying particle
        // bursts 4 children (the splash). Compare against the same system with no spawn modifier.
        var spawn = new SpawnModifierDefinition
        {
            Kind = ParticleModifierKind.Spawn,
            NumSpawnGenerations = 1, PercentageSpawned = 1f, MinToSpawn = 4, MaxToSpawn = 4,
            LifeSpan = 1f
        };

        var withoutSpawn = NifParticleBaker.Bake(BoxSystem());
        var withSpawn = NifParticleBaker.Bake(BoxSystem(spawn));

        Assert.True(withSpawn.Count > withoutSpawn.Count,
            $"spawn should add child particles ({withSpawn.Count} vs {withoutSpawn.Count})");
    }

    [Fact]
    public void Bake_SpawnZeroGenerations_IsNoOp()
    {
        // NumSpawnGenerations=0 (the UL fountain's case) ⇒ the gate spawnGen<0 is never true, so no children and
        // no RNG consumed — identical to the spawn-free bake.
        var spawn = new SpawnModifierDefinition
        {
            Kind = ParticleModifierKind.Spawn,
            NumSpawnGenerations = 0, PercentageSpawned = 1f, MinToSpawn = 4, MaxToSpawn = 4
        };

        var withoutSpawn = NifParticleBaker.Bake(BoxSystem());
        var withSpawn = NifParticleBaker.Bake(BoxSystem(spawn));

        Assert.Equal(withoutSpawn.Count, withSpawn.Count);
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
            DecayType = 0, // no decay → constant outward push
            SymmetryType = 0, // spherical
            BombAxis = Vector3.UnitZ
        };

        var withoutBomb = NifParticleBaker.Bake(BoxSystem());
        var withBomb = NifParticleBaker.Bake(BoxSystem(bomb));

        var maxRadiusNoBomb = withoutBomb.Max(p => p.Position.Length());
        var maxRadiusBomb = withBomb.Max(p => p.Position.Length());

        Assert.True(maxRadiusBomb > maxRadiusNoBomb * 2f,
            $"bomb should disperse particles ({maxRadiusBomb:F0} vs {maxRadiusNoBomb:F0})");
    }

    [Fact]
    public void GravityForce_SphericalAttractsTowardObjectAndUsesRecoveredStrengthScale()
    {
        var gravity = new GravityModifierDefinition
        {
            Kind = ParticleModifierKind.Gravity,
            HasGravityObject = true,
            GravityObjectTransform = Matrix4x4.Identity,
            ForceType = 1,
            Strength = 10f
        };

        var force = NifParticleBaker.GravityForce(gravity, new Vector3(4f, 0f, 0f), Vector3.Zero);

        Assert.Equal(new Vector3(-16f, 0f, 0f), force);
    }

    [Fact]
    public void GravityForce_PlanarAppliesExponentialDecayAndAuthoredTurbulence()
    {
        var gravity = new GravityModifierDefinition
        {
            Kind = ParticleModifierKind.Gravity,
            HasGravityObject = true,
            GravityObjectTransform = Matrix4x4.Identity,
            GravityAxis = Vector3.UnitX,
            ForceType = 0,
            Strength = 10f,
            Decay = 0.5f,
            Turbulence = 0.2f,
            TurbulenceScale = 2f
        };
        var turbulenceSample = new Vector3(1f, -0.5f, 0.25f);

        var force = NifParticleBaker.GravityForce(
            gravity, new Vector3(2f, 8f, -3f), turbulenceSample);

        var baseForce = 16f * MathF.Exp(-1f);
        Assert.Equal(baseForce + 200f, force.X, 4);
        Assert.Equal(-100f, force.Y, 4);
        Assert.Equal(50f, force.Z, 4);
    }

    [Fact]
    public void GravityForce_WithoutGravityObject_IsNoOp()
    {
        var gravity = new GravityModifierDefinition
        {
            Kind = ParticleModifierKind.Gravity,
            HasGravityObject = false,
            Strength = 100f,
            Turbulence = 1f
        };

        Assert.Equal(
            Vector3.Zero,
            NifParticleBaker.GravityForce(gravity, Vector3.One, Vector3.One));
    }
}