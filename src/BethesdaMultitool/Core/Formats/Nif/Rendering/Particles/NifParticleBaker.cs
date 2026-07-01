using System.Numerics;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Particles;

/// <summary>One baked particle (system-local), ready to expand into a camera-facing quad.</summary>
internal readonly record struct BakedParticle(Vector3 Position, float Size, Vector4 Color);

/// <summary>Tunables for the deterministic particle bake.</summary>
internal sealed class ParticleBakeOptions
{
    /// <summary>Fixed simulation step (seconds). Smaller = smoother trajectories, more cost.</summary>
    public float TimeStep { get; init; } = 1f / 30f;

    /// <summary>Hard cap on live particles (bounds vertex blow-up). The steady-state target is also clamped here.</summary>
    public int MaxParticles { get; init; } = 2048;

    /// <summary>Extra settle time beyond one full lifespan, so the age distribution is uniform at snapshot.</summary>
    public float SettleMarginSeconds { get; init; } = 0.5f;

    public static ParticleBakeOptions Default { get; } = new();
}

/// <summary>
///     Deterministically bakes a <see cref="ParticleSystemDefinition" /> to a steady-state cloud of
///     <see cref="BakedParticle" /> by simulating the emitter + modifier chain at a fixed step with a seeded
///     RNG (no wall-clock, no Math.Random — reproducible). Formulas follow the decompiled engine spec
///     (tools/GhidraProject/particles_formula_spec.md): emit (speed·dir(declination,planar), lifespan±var,
///     volume position), then per tick AgeDeath → GrowFade → Color → Bomb-vortex → Gravity → Position.
///     The viewer is static, so we run for one full lifespan (+ margin) to reach a uniform-age cloud and
///     snapshot it; live animation is deferred.
/// </summary>
internal static class NifParticleBaker
{
    public static IReadOnlyList<BakedParticle> Bake(ParticleSystemDefinition def, ParticleBakeOptions? options = null)
    {
        var opt = options ?? ParticleBakeOptions.Default;
        var emitter = def.Emitter;
        if (emitter is null)
        {
            return [];
        }

        var dt = MathF.Max(opt.TimeStep, 1f / 240f);
        var avgLifespan = MathF.Max(emitter.LifeSpan, dt);

        // Density = birth rate × lifespan. Prefer the AUTHORED birth rate (from the emitter controller) — the
        // capacity is only the buffer max, and filling to it stacks dust/smoke to opaque. Fall back to
        // capacity/lifespan when the rate can't be resolved. Either way the live count is hard-capped below.
        var capacityTarget = Math.Clamp(def.Capacity > 0 ? def.Capacity : 64, 1, opt.MaxParticles);
        var birthRate = emitter.BirthRate > 0f ? emitter.BirthRate : capacityTarget / avgLifespan; // particles/sec

        var simSeconds = avgLifespan + opt.SettleMarginSeconds;
        var totalSteps = (int)MathF.Ceiling(simSeconds / dt);

        var hasAgeDeath = def.Modifiers.Any(m => m.Kind == ParticleModifierKind.AgeDeath && m.Active);
        var grow = def.Modifiers.OfType<GrowFadeModifierDefinition>().FirstOrDefault(m => m.Active);
        var color = def.Modifiers.OfType<ColorModifierDefinition>().FirstOrDefault(m => m.Active);
        var bombs = def.Modifiers.OfType<BombModifierDefinition>().Where(m => m.Active).ToList();
        var gravities = def.Modifiers.OfType<GravityModifierDefinition>().Where(m => m.Active).ToList();
        var drags = def.Modifiers.OfType<DragModifierDefinition>().Where(m => m.Active)
            .Select(PrepareDrag).Where(d => d.Active).ToList();
        var spawn = def.Modifiers.OfType<SpawnModifierDefinition>().FirstOrDefault(m => m.Active);
        var hasPosition = def.Modifiers.Any(m => m.Kind == ParticleModifierKind.Position && m.Active);

        var rng = new DeterministicRng(unchecked((uint)(def.BlockIndex * 2654435761u) ^ 0x9E3779B9u));
        var live = new List<Particle>(capacityTarget + 16);
        var spawnAccumulator = 0f;

        for (var step = 0; step < totalSteps; step++)
        {
            // AgeDeath: age all, remove the dead (only when an AgeDeath modifier is present, else immortal —
            // capped by the spawn budget below so it can't run away). A NiPSysSpawnModifier bursts child
            // particles at each death point (the fountain's splash spray) — appended after the sweep so they
            // aren't re-aged this tick.
            if (hasAgeDeath)
            {
                List<Particle>? spawned = null;
                for (var i = live.Count - 1; i >= 0; i--)
                {
                    var p = live[i];
                    p.Age += dt;
                    if (p.Age > p.LifeSpan)
                    {
                        if (spawn is not null && p.SpawnGeneration < spawn.NumSpawnGenerations)
                        {
                            SpawnChildren(spawn, emitter, p, ref rng, ref spawned,
                                opt.MaxParticles - live.Count - (spawned?.Count ?? 0));
                        }

                        live.RemoveAt(i);
                    }
                    else
                    {
                        live[i] = p;
                    }
                }

                if (spawned is not null)
                {
                    live.AddRange(spawned);
                }
            }
            else
            {
                for (var i = 0; i < live.Count; i++)
                {
                    var p = live[i];
                    p.Age += dt;
                    live[i] = p;
                }
            }

            // Emit: spawn birthRate·dt new particles (fractional accumulator), capped by free budget.
            spawnAccumulator += birthRate * dt;
            var toSpawn = (int)spawnAccumulator;
            spawnAccumulator -= toSpawn;
            var freeBudget = opt.MaxParticles - live.Count;
            toSpawn = Math.Clamp(toSpawn, 0, Math.Max(0, freeBudget));
            for (var s = 0; s < toSpawn; s++)
            {
                live.Add(Spawn(emitter, ref rng));
            }

            // Modifier chain (per the formula spec). Operate on every live particle.
            for (var i = 0; i < live.Count; i++)
            {
                var p = live[i];

                // GrowFade → size; else size = initial radius.
                p.Size = grow is null
                    ? p.BaseSize
                    : GrowFadeSize(grow, p.Age, p.LifeSpan, p.BaseSize);

                // Color modifier: sample RGBA at age/lifespan (gradient + fade-in/out envelope). The
                // envelope dims birth/death particles, taming additive over-glow. No modifier ⇒ keep the
                // emitter InitialColor set at Spawn.
                if (color is not null)
                {
                    var lifeFrac = p.LifeSpan > 1e-4f ? p.Age / p.LifeSpan : 0f;
                    p.Color = color.Sample(lifeFrac, emitter.InitialColor);
                }

                // Bomb vortex + Gravity → velocity (forces simulate in the system's local frame, balanced with
                // the authored lifespan for a clean ballistic arc).
                foreach (var bomb in bombs)
                {
                    p.Velocity += BombForce(bomb, p.Position) * dt;
                }

                foreach (var gravity in gravities)
                {
                    p.Velocity += GravityForce(gravity, p.Position) * dt;
                }

                // Anisotropic drag (engine-accurate): damps only the velocity component along the drag axis,
                // range-gated around the drag object. The frame-scale (dt/(1/30)) is baked into the delta — it
                // is NOT a force·dt, so it isn't multiplied by dt again, and it isn't accel-scaled (it's a
                // fraction of velocity, already frame-consistent).
                foreach (var drag in drags)
                {
                    p.Velocity += DragDelta(drag, p.Position, p.Velocity, dt);
                }

                // Position integration.
                if (hasPosition)
                {
                    p.Position += p.Velocity * dt;
                }

                live[i] = p;
            }
        }

        var baked = new List<BakedParticle>(live.Count);
        foreach (var p in live)
        {
            if (p.Size > 1e-4f)
            {
                baked.Add(new BakedParticle(p.Position, p.Size, p.Color));
            }
        }

        return baked;
    }

    private static Particle Spawn(ParticleEmitterDefinition e, ref DeterministicRng rng)
    {
        var speed = e.Speed + e.SpeedVariation * (rng.NextFloat() - 0.5f);
        var decl = e.Declination + e.DeclinationVariation * rng.NextFloat();
        var planar = e.PlanarAngle + e.PlanarAngleVariation * rng.NextFloat();
        var lifeSpan = MathF.Max(0.01f, e.LifeSpan + e.LifeSpanVariation * (rng.NextFloat() - 0.5f));
        var radius = MathF.Max(0f, e.InitialRadius + e.RadiusVariation * rng.NextFloat());

        // Direction: spherical (decl = polar from emission axis, planar = azimuth around it), then aligned
        // so local +Z maps to the emission axis.
        var sinDecl = MathF.Sin(decl);
        var local = new Vector3(sinDecl * MathF.Cos(planar), sinDecl * MathF.Sin(planar), MathF.Cos(decl));
        var dir = AlignToAxis(local, e.EmissionAxis);
        var velocity = Vector3.TransformNormal(dir, e.EmitterObjectTransform) * speed;

        var localPos = SamplePosition(e, ref rng);
        var position = Vector3.Transform(localPos, e.EmitterObjectTransform);

        return new Particle
        {
            Position = position,
            Velocity = velocity,
            Age = 0f,
            LifeSpan = lifeSpan,
            BaseSize = radius,
            Size = radius,
            Color = e.InitialColor,
            SpawnGeneration = 0,
        };
    }

    /// <summary>
    ///     NiPSysSpawnModifier::SpawnParticles: a dying particle, if it passes the <c>PercentageSpawned</c> roll,
    ///     bursts <c>MinToSpawn + round(rand·(MaxToSpawn-MinToSpawn))</c> children (≥1) at its death position.
    ///     Each child inherits the parent's velocity scaled by the speed variation and scattered by the dir
    ///     variation (the chaos that fans a splash out), with a fresh spawn lifespan and an incremented
    ///     generation so the cascade terminates at <c>NumSpawnGenerations</c>. Capped by <paramref name="budget" />.
    /// </summary>
    private static void SpawnChildren(
        SpawnModifierDefinition s, ParticleEmitterDefinition e, Particle parent, ref DeterministicRng rng,
        ref List<Particle>? sink, int budget)
    {
        if (budget <= 0 || rng.NextFloat() > s.PercentageSpawned)
        {
            return;
        }

        var span = Math.Max(0, s.MaxToSpawn - s.MinToSpawn);
        var count = s.MinToSpawn + (int)MathF.Round(rng.NextFloat() * span);
        count = Math.Clamp(count < 1 ? 1 : count, 1, budget);

        var parentSpeed = parent.Velocity.Length();
        for (var i = 0; i < count; i++)
        {
            var speedFactor = 1f + s.SpawnSpeedVariation * (2f * rng.NextFloat() - 1f);
            var childVel = parent.Velocity * speedFactor;
            if (s.SpawnDirVariation > 0f && parentSpeed > 1e-4f)
            {
                childVel += RandomUnitVector(ref rng) * (s.SpawnDirVariation * parentSpeed);
            }

            // The spawn modifier authors the child's lifespan; fall back to the parent's when it's unset (0).
            var life = s.LifeSpan > 1e-3f
                ? MathF.Max(0.01f, s.LifeSpan + s.LifeSpanVariation * (rng.NextFloat() - 0.5f))
                : parent.LifeSpan;

            (sink ??= []).Add(new Particle
            {
                Position = parent.Position,
                Velocity = childVel,
                Age = 0f,
                LifeSpan = life,
                BaseSize = e.InitialRadius,
                Size = e.InitialRadius,
                Color = e.InitialColor,
                SpawnGeneration = parent.SpawnGeneration + 1,
            });
        }
    }

    private static Vector3 SamplePosition(ParticleEmitterDefinition e, ref DeterministicRng rng)
    {
        switch (e.Shape)
        {
            case ParticleEmitterShape.Box:
                return new Vector3(
                    e.Width * (rng.NextFloat() - 0.5f),
                    e.Height * (rng.NextFloat() - 0.5f),
                    e.Depth * (rng.NextFloat() - 0.5f));

            case ParticleEmitterShape.Sphere:
            {
                // Uniform-ish in volume: random direction × radius × cbrt(rand).
                var d = RandomUnitVector(ref rng);
                return d * (e.Radius * MathF.Cbrt(rng.NextFloat()));
            }

            case ParticleEmitterShape.Cylinder:
            {
                var r = e.Radius * rng.NextFloat(); // engine uses linear (not area-uniform) radius
                var theta = rng.NextFloat() * (MathF.PI * 2f);
                return new Vector3(r * MathF.Cos(theta), r * MathF.Sin(theta), (rng.NextFloat() - 0.5f) * e.Height);
            }

            case ParticleEmitterShape.Mesh:
            {
                var min = e.MeshBoundsMin;
                var max = e.MeshBoundsMax;
                if (max.X <= min.X && max.Y <= min.Y && max.Z <= min.Z)
                {
                    return Vector3.Zero; // no bounds yet ⇒ point emit
                }

                return new Vector3(
                    Lerp(min.X, max.X, rng.NextFloat()),
                    Lerp(min.Y, max.Y, rng.NextFloat()),
                    Lerp(min.Z, max.Z, rng.NextFloat()));
            }

            default:
                return Vector3.Zero;
        }
    }

    /// <summary>GrowFade: size ramps baseScale·radius → radius → baseScale·radius over grow/fade windows.</summary>
    private static float GrowFadeSize(GrowFadeModifierDefinition g, float age, float lifeSpan, float radius)
    {
        var growF = g.GrowTime > 0f && age < g.GrowTime ? age / g.GrowTime : 1f;
        var remaining = lifeSpan - age;
        var fadeF = g.FadeTime > 0f && remaining < g.FadeTime ? remaining / g.FadeTime : 1f;
        var ramp = g.BaseScale + MathF.Min(growF, fadeF) * (1f - g.BaseScale);
        return MathF.Max(1e-4f, radius * ramp);
    }

    /// <summary>Bomb vortex/blast force at a particle position (system-local). See spec "Bomb".</summary>
    private static Vector3 BombForce(BombModifierDefinition b, Vector3 pos)
    {
        var bombPos = b.HasBombObject ? b.BombObjectTransform.Translation : Vector3.Zero;
        var r = pos - bombPos;
        var dist = r.Length();
        if (b.DecayType != 0 && dist > b.Range && b.Range > 0f)
        {
            return Vector3.Zero;
        }

        var decay = b.DecayType switch
        {
            1 => b.Range > 0f ? MathF.Max(0f, (b.Range - dist) / b.Range) : 1f, // linear
            2 => b.Range > 0f ? MathF.Exp(-dist / b.Range) : 1f,                // exponential
            _ => 1f,                                                            // none
        };

        var axis = b.BombAxis.LengthSquared() > 1e-6f ? Vector3.Normalize(b.BombAxis) : Vector3.UnitZ;
        Vector3 dir;
        switch (b.SymmetryType)
        {
            case 1: // cylindrical: radial component perpendicular to the axis
                var radial = r - axis * Vector3.Dot(r, axis);
                dir = radial.LengthSquared() > 1e-6f ? Vector3.Normalize(radial) : Vector3.Zero;
                break;
            case 2: // planar: along ±axis
                dir = Vector3.Dot(r, axis) >= 0f ? axis : -axis;
                break;
            default: // spherical: outward from the bomb point
                dir = dist > 1e-6f ? r / dist : Vector3.Zero;
                break;
        }

        return dir * (decay * b.DeltaV);
    }

    /// <summary>Gravity force (planar/spherical). MVP ignores the decay/turbulence attenuation.</summary>
    private static Vector3 GravityForce(GravityModifierDefinition g, Vector3 pos)
    {
        if (g.ForceType == 1) // spherical: toward/away from the gravity object
        {
            var gravPos = g.HasGravityObject ? g.GravityObjectTransform.Translation : Vector3.Zero;
            var d = pos - gravPos;
            var len = d.Length();
            return len > 1e-6f ? d / len * g.Strength : Vector3.Zero;
        }

        // planar: constant directional force along the gravity axis. The axis is in the gravity OBJECT's local
        // frame (nif.xml: "the local direction of the force"), so transform it by that object's transform —
        // mirroring how emission velocity is transformed by the emitter object. This is load-bearing: the
        // fountain's authored axis is +Z (up), but its gravity object orients it to world -Z (down), so without
        // this the jet accelerates UP into the sky instead of arcing back down.
        var localAxis = g.GravityAxis.LengthSquared() > 1e-6f ? Vector3.Normalize(g.GravityAxis) : Vector3.UnitZ;
        var axis = localAxis;
        if (g.HasGravityObject)
        {
            var world = Vector3.TransformNormal(localAxis, g.GravityObjectTransform);
            if (world.LengthSquared() > 1e-6f)
            {
                axis = Vector3.Normalize(world);
            }
        }

        return axis * g.Strength;
    }

    /// <summary>Drag axis (system-local, normalized) + range origin + coefficients, precomputed once per
    /// modifier. <see cref="Active" /> is false when the engine would no-op the modifier (no drag object,
    /// non-positive percentage, or a degenerate axis).</summary>
    private readonly record struct PreparedDrag(
        bool Active, Vector3 AxisHat, Vector3 ObjectPos, float Range, float Falloff, float Percentage);

    private static PreparedDrag PrepareDrag(DragModifierDefinition d)
    {
        // The engine transforms the drag axis into the drag-object frame and normalizes it; it no-ops the whole
        // modifier when there is no drag object or the percentage isn't positive.
        var worldAxis = Vector3.TransformNormal(d.DragAxis, d.DragObjectTransform);
        if (!d.HasDragObject || d.Percentage <= 0f || worldAxis.LengthSquared() < 1e-8f)
        {
            return new PreparedDrag(false, Vector3.UnitZ, Vector3.Zero, 0f, 0f, 0f);
        }

        return new PreparedDrag(true, Vector3.Normalize(worldAxis), d.DragObjectTransform.Translation,
            d.Range, d.RangeFalloff, d.Percentage);
    }

    /// <summary>
    ///     Engine-accurate <c>NiPSysDragModifier::Update</c> delta. Damps ONLY the velocity component along the
    ///     drag axis, scaled by the drag percentage and the frame-step (dt/(1/30)). Particles beyond
    ///     <c>Range</c> get a linearly-fading drag down to zero at <c>RangeFalloff</c>; beyond that, none. The
    ///     fraction is clamped so it never removes more than the whole axis component (the engine's clamp). The
    ///     result is the velocity delta to ADD (already includes the frame-step, so callers don't multiply by dt).
    /// </summary>
    private static Vector3 DragDelta(PreparedDrag d, Vector3 position, Vector3 velocity, float dt)
    {
        if (!d.Active)
        {
            return Vector3.Zero;
        }

        var dist = (position - d.ObjectPos).Length();
        float effPct;
        if (dist <= d.Range)
        {
            effPct = d.Percentage;
        }
        else if (dist < d.Falloff && d.Falloff > d.Range)
        {
            effPct = (1f - (dist - d.Range) / (d.Falloff - d.Range)) * d.Percentage;
        }
        else
        {
            return Vector3.Zero; // beyond the falloff radius the engine applies no drag
        }

        var component = Vector3.Dot(velocity, d.AxisHat);
        var frac = MathF.Min(1f, effPct * (dt / (1f / 30f)));
        return -frac * component * d.AxisHat;
    }

    /// <summary>Rotate <paramref name="local" /> (defined with +Z = up) so +Z maps to <paramref name="axis" />.</summary>
    private static Vector3 AlignToAxis(Vector3 local, Vector3 axis)
    {
        if (axis.LengthSquared() < 1e-6f)
        {
            return local;
        }

        var w = Vector3.Normalize(axis);
        if (MathF.Abs(w.Z - 1f) < 1e-5f)
        {
            return local; // already +Z
        }

        if (MathF.Abs(w.Z + 1f) < 1e-5f)
        {
            return new Vector3(local.X, local.Y, -local.Z); // flipped
        }

        // Build an orthonormal basis (u, v, w) with w = axis.
        var reference = MathF.Abs(w.Z) < 0.99f ? Vector3.UnitZ : Vector3.UnitX;
        var u = Vector3.Normalize(Vector3.Cross(reference, w));
        var v = Vector3.Cross(w, u);
        return u * local.X + v * local.Y + w * local.Z;
    }

    private static Vector3 RandomUnitVector(ref DeterministicRng rng)
    {
        var z = rng.NextFloat() * 2f - 1f;
        var theta = rng.NextFloat() * (MathF.PI * 2f);
        var r = MathF.Sqrt(MathF.Max(0f, 1f - z * z));
        return new Vector3(r * MathF.Cos(theta), r * MathF.Sin(theta), z);
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private struct Particle
    {
        public Vector3 Position;
        public Vector3 Velocity;
        public float Age;
        public float LifeSpan;
        public float BaseSize;
        public float Size;
        public Vector4 Color;

        /// <summary>How many NiPSysSpawnModifier generations deep this particle is (0 = emitted directly).
        /// Spawning stops once this reaches the modifier's NumSpawnGenerations.</summary>
        public int SpawnGeneration;
    }

    /// <summary>Small deterministic xorshift32 PRNG — reproducible across runs/platforms (no Math.Random).</summary>
    private struct DeterministicRng(uint seed)
    {
        private uint _state = seed == 0 ? 0x1234_5678u : seed;

        public float NextFloat()
        {
            _state ^= _state << 13;
            _state ^= _state >> 17;
            _state ^= _state << 5;
            return (_state & 0x00FF_FFFF) / (float)0x0100_0000; // [0,1)
        }
    }
}
