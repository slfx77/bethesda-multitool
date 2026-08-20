using System.Numerics;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Particles;

/// <summary>One baked particle (system-local), ready to expand into a camera-facing quad.</summary>
internal readonly record struct BakedParticle(
    Vector3 Position,
    float Size,
    Vector4 Color,
    Vector4 UvRect = default,
    float Rotation = 0f,
    float AspectRatio = 1f,
    int Generation = 0);

/// <summary>Tunables for the deterministic particle bake.</summary>
internal sealed class ParticleBakeOptions
{
    /// <summary>Fixed simulation step (seconds). Smaller = smoother trajectories, more cost.</summary>
    public float TimeStep { get; init; } = 1f / 30f;

    /// <summary>Hard cap on live particles (bounds vertex blow-up). The steady-state target is also clamped here.</summary>
    public int MaxParticles { get; init; } = 2048;

    /// <summary>Extra settle time beyond one full lifespan, so the age distribution is uniform at snapshot.</summary>
    public float SettleMarginSeconds { get; init; } = 0.5f;

    /// <summary>
    ///     Renderer/controller clock at the requested snapshot. The fixed-step bake reconstructs the preceding
    ///     lifespan of rate history, so changing this value advances pulsed/animated emitters deterministically.
    /// </summary>
    public float SnapshotTimeSeconds { get; init; }

    public static ParticleBakeOptions Default { get; } = new();
}

/// <summary>
///     Deterministically bakes a <see cref="ParticleSystemDefinition" /> to a steady-state cloud of
///     <see cref="BakedParticle" /> by simulating the emitter + modifier chain at a fixed step with a seeded
///     RNG (no wall-clock, no Math.Random — reproducible). Formulas follow the decompiled engine spec
///     (tools/GhidraProject/particles_formula_spec.md): emit (speed·dir(declination,planar), lifespan±var,
///     volume position), then per tick AgeDeath → GrowFade → Color → Bomb-vortex → Gravity → Position.
///     Each snapshot replays one full lifespan (+ margin) ending at the requested controller time. The current
///     extractor still uploads one static snapshot, but the rate contract and snapshot clock can be advanced by
///     a future live GPU-particle owner without reparsing the NIF.
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

        // Density = integral(birthRate(t)) over the living age window. An authored controller is authoritative
        // even when it samples to zero; only a missing/unreadable controller uses the bounded static fallback.
        var hardLimit = Math.Max(1, opt.MaxParticles);
        var capacityTarget = Math.Clamp(def.Capacity > 0 ? def.Capacity : 64, 1, hardLimit);
        var liveLimit = Math.Min(capacityTarget, hardLimit);
        var fallbackBirthRate = emitter.BirthRate > 0f && float.IsFinite(emitter.BirthRate)
            ? emitter.BirthRate
            : capacityTarget / avgLifespan;

        var simSeconds = avgLifespan + opt.SettleMarginSeconds;
        var totalSteps = (int)MathF.Ceiling(simSeconds / dt);
        var snapshotTime = float.IsFinite(opt.SnapshotTimeSeconds) ? opt.SnapshotTimeSeconds : 0f;
        var simulationStart = snapshotTime - totalSteps * dt;

        var hasAgeDeath = def.Modifiers.Any(m => m.Kind == ParticleModifierKind.AgeDeath && m.Active);
        var spawnModifiers = def.Modifiers.OfType<SpawnModifierDefinition>().Where(m => m.Active).ToArray();
        // Key by the modifier instance rather than block index. A Modifiers[] array may legally reference the
        // same block more than once; block-index keys made that authored multiplicity throw in ToDictionary.
        var preparedDrags = def.Modifiers.OfType<DragModifierDefinition>()
            .Where(m => m.Active).ToDictionary(m => m, PrepareDrag);
        var rotationModifiers = def.Modifiers.OfType<RotationModifierDefinition>().Where(m => m.Active).ToArray();
        var subtexture = def.Modifiers.OfType<SubtextureModifierDefinition>().FirstOrDefault(m => m.Active);

        var rng = new DeterministicRng(def.DeterministicSeed);
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
                        // Release the dying particle's authored-capacity slot before spawning its children.
                        // Otherwise a system at capacity rejected every death-spawn despite the slot becoming
                        // free in this same update.
                        live.RemoveAt(i);
                        foreach (var spawn in spawnModifiers)
                        {
                            if (p.SpawnGeneration < spawn.NumSpawnGenerations)
                            {
                                SpawnChildren(spawn, emitter, p, ref rng, ref spawned,
                                    liveLimit - live.Count - (spawned?.Count ?? 0));
                            }
                        }
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

            // Emit: midpoint-sample the authored rate over this fixed step. Midpoint integration avoids a
            // one-tick pulse bias while keeping replay deterministic at any requested renderer timestamp.
            var rateSampleTime = simulationStart + (step + 0.5f) * dt;
            var birthRate = emitter.BirthRateController?.Sample(rateSampleTime) ?? fallbackBirthRate;
            spawnAccumulator += birthRate * dt;
            var toSpawn = (int)spawnAccumulator;
            spawnAccumulator -= toSpawn;
            var freeBudget = liveLimit - live.Count;
            toSpawn = Math.Clamp(toSpawn, 0, Math.Max(0, freeBudget));
            for (var s = 0; s < toSpawn; s++)
            {
                live.Add(Spawn(emitter, rotationModifiers, ref rng));
            }

            // Modifier chain (per the formula spec). Operate on every live particle.
            for (var i = 0; i < live.Count; i++)
            {
                var p = live[i];

                // The engine executes Modifiers[] in authored array order. Re-grouping by type changed
                // multiplicity and made Position run after forces which were authored later.
                foreach (var modifier in def.Modifiers)
                {
                    if (!modifier.Active)
                    {
                        continue;
                    }

                    switch (modifier)
                    {
                        case GrowFadeModifierDefinition grow:
                            p.Size = GrowFadeSize(grow, p.Age, p.LifeSpan, p.BaseSize, p.SpawnGeneration);
                            break;
                        case ColorModifierDefinition color:
                            var lifeFrac = p.LifeSpan > 1e-4f ? p.Age / p.LifeSpan : 0f;
                            p.Color = color.Sample(lifeFrac, emitter.InitialColor);
                            break;
                        case BombModifierDefinition bomb:
                            p.Velocity += BombForce(bomb, p.Position) * dt;
                            break;
                        case GravityModifierDefinition gravity:
#pragma warning disable S1244 // authored zero Turbulence disables the term; exact comparison intended
                            var turbulenceSample = gravity.Turbulence != 0f
#pragma warning restore S1244
                                ? new Vector3(rng.NextSignedFloat(), rng.NextSignedFloat(), rng.NextSignedFloat())
                                : Vector3.Zero;
                            p.Velocity += GravityForce(gravity, p.Position, turbulenceSample) * dt;
                            break;
                        case DragModifierDefinition drag when preparedDrags.TryGetValue(drag, out var prepared):
                            p.Velocity += DragDelta(prepared, p.Position, p.Velocity, dt);
                            break;
                        case RotationModifierDefinition:
                            // Initial angle/speed were accumulated once per authored modifier at spawn.
                            // Advancing the accumulated speed here would do it once PER modifier and turn
                            // two modifiers into 2 * (speed0 + speed1) instead of speed0 + speed1.
                            break;
                        default:
                            if (modifier.Kind == ParticleModifierKind.Position)
                            {
                                p.Position += p.Velocity * dt;
                            }

                            break;
                    }
                }

                // Rotation does not feed any other supported modifier, so the additive authored rotation
                // modifiers can be integrated once after the ordered chain without changing ordering.
                if (rotationModifiers.Length > 0)
                {
                    p.Rotation += p.RotationSpeed * dt;
                }

                live[i] = p;
            }
        }

        var baked = new List<BakedParticle>(live.Count);
        foreach (var p in live)
        {
            if (p.Size > 1e-4f)
            {
                var atlasCount = def.SubtextureOffsets.Count;
                var frame = atlasCount > 0
                    ? subtexture?.SampleFrame(p.Age, p.AtlasSeed, atlasCount)
                      ?? Math.Clamp((int)(p.AtlasSeed * atlasCount), 0, atlasCount - 1)
                    : 0;
                var uvRect = atlasCount > 0 ? def.SubtextureOffsets[frame] : new Vector4(0f, 0f, 1f, 1f);
                baked.Add(new BakedParticle(
                    p.Position, p.Size, p.Color, uvRect, p.Rotation,
                    def.AspectRatio > 1e-4f ? def.AspectRatio : 1f, p.SpawnGeneration));
            }
        }

        return baked;
    }

    private static Particle Spawn(
        ParticleEmitterDefinition e,
        IReadOnlyList<RotationModifierDefinition> rotationModifiersForSpawn,
        ref DeterministicRng rng)
    {
        var speed = e.Speed + e.SpeedVariation * (rng.NextFloat() - 0.5f);
        var decl = e.Declination + e.DeclinationVariation * rng.NextFloat();
        var planar = e.PlanarAngle + e.PlanarAngleVariation * rng.NextFloat();
        var lifeSpan = MathF.Max(0.01f, e.LifeSpan + e.LifeSpanVariation * (rng.NextFloat() - 0.5f));
        var radius = MathF.Max(0f, e.InitialRadius + e.RadiusVariation * rng.NextFloat());

        var sample = SamplePosition(e, ref rng);
        var dir = e.Shape == ParticleEmitterShape.Mesh
            ? e.VelocityType switch
            {
                ParticleVelocityType.UseNormals when sample.Normal.LengthSquared() > 1e-8f =>
                    Vector3.Normalize(sample.Normal),
                ParticleVelocityType.UseRandom => RandomUnitVector(ref rng),
                _ => ComputeEmissionDirection(decl, planar, e.EmissionAxis)
            }
            : ComputeEmissionDirection(decl, planar, e.EmissionAxis);
        var velocity = Vector3.TransformNormal(dir, e.EmitterObjectTransform) * speed;

        var localPos = sample.Position;
        var position = Vector3.Transform(localPos, e.EmitterObjectTransform);

        var rotationSpeed = 0f;
        var rotationAngle = 0f;
        foreach (var rotation in rotationModifiersForSpawn)
        {
            var variedSpeed = rotation.RotationSpeed
                              + rotation.RotationSpeedVariation * (rng.NextFloat() * 2f - 1f);
            if (rotation.RandomSpeedSign && rng.NextFloat() < 0.5f)
            {
                variedSpeed = -variedSpeed;
            }

            rotationSpeed += variedSpeed;
            rotationAngle += rotation.RotationAngle
                             + rotation.RotationAngleVariation * (rng.NextFloat() * 2f - 1f);
        }

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
            Rotation = rotationAngle,
            RotationSpeed = rotationSpeed,
            AtlasSeed = rng.NextFloat()
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
                Rotation = parent.Rotation,
                RotationSpeed = parent.RotationSpeed,
                AtlasSeed = rng.NextFloat()
            });
        }
    }

    private static MeshSample SamplePosition(ParticleEmitterDefinition e, ref DeterministicRng rng)
    {
        switch (e.Shape)
        {
            case ParticleEmitterShape.Box:
                // Recovered NiPSysBoxEmitter field-to-axis convention: Depth=X, Height=Y, Width=Z.
                return new MeshSample(new Vector3(
                    e.Depth * (rng.NextFloat() - 0.5f),
                    e.Height * (rng.NextFloat() - 0.5f),
                    e.Width * (rng.NextFloat() - 0.5f)), Vector3.Zero);

            case ParticleEmitterShape.Sphere:
            {
                // Uniform-ish in volume: random direction × radius × cbrt(rand).
                var d = RandomUnitVector(ref rng);
                return new MeshSample(d * (e.Radius * MathF.Cbrt(rng.NextFloat())), d);
            }

            case ParticleEmitterShape.Cylinder:
            {
                var r = e.Radius * rng.NextFloat(); // engine uses linear (not area-uniform) radius
                var theta = rng.NextFloat() * (MathF.PI * 2f);
                return new MeshSample(
                    new Vector3(r * MathF.Cos(theta), r * MathF.Sin(theta),
                        (rng.NextFloat() - 0.5f) * e.Height),
                    Vector3.Zero);
            }

            case ParticleEmitterShape.Mesh:
                return SampleMeshPosition(e, ref rng);

            default:
                return new MeshSample(Vector3.Zero, Vector3.Zero);
        }
    }

    private static MeshSample SampleMeshPosition(ParticleEmitterDefinition e, ref DeterministicRng rng)
    {
        var vertices = e.MeshVertices;
        var triangles = e.MeshTriangles;
        if (vertices.Count == 0)
        {
            return new MeshSample(Vector3.Zero, Vector3.Zero);
        }

        if (e.EmitFrom == ParticleEmitFrom.Vertices || triangles.Count < 3)
        {
            var index = Math.Min((int)(rng.NextFloat() * vertices.Count), vertices.Count - 1);
            return new MeshSample(vertices[index], VertexNormal(e, index));
        }

        if (e.EmitFrom is ParticleEmitFrom.EdgeCenter or ParticleEmitFrom.EdgeSurface)
        {
            var edges = BuildEdges(triangles, vertices.Count);
            if (edges.Count == 0)
            {
                return new MeshSample(vertices[0], VertexNormal(e, 0));
            }

            var edge = PickWeightedEdge(edges, vertices, ref rng);
            var t = e.EmitFrom == ParticleEmitFrom.EdgeCenter ? 0.5f : rng.NextFloat();
            var pos = Vector3.Lerp(vertices[edge.A], vertices[edge.B], t);
            var normal = Vector3.Lerp(VertexNormal(e, edge.A), VertexNormal(e, edge.B), t);
            return new MeshSample(pos, NormalizeOrZero(normal));
        }

        var tri = PickAreaWeightedTriangle(triangles, vertices, ref rng);
        var a = vertices[tri.A];
        var b = vertices[tri.B];
        var c = vertices[tri.C];
        if (e.EmitFrom == ParticleEmitFrom.FaceCenter)
        {
            return new MeshSample((a + b + c) / 3f, TriangleNormal(e, tri));
        }

        // Uniform barycentric face sampling: sqrt(r1) removes the vertex-density bias.
        var root = MathF.Sqrt(rng.NextFloat());
        var w0 = 1f - root;
        var w1 = root * (1f - rng.NextFloat());
        var w2 = 1f - w0 - w1;
        var point = a * w0 + b * w1 + c * w2;
        var interpolated = VertexNormal(e, tri.A) * w0
                           + VertexNormal(e, tri.B) * w1
                           + VertexNormal(e, tri.C) * w2;
        return new MeshSample(point,
            interpolated.LengthSquared() > 1e-8f ? Vector3.Normalize(interpolated) : TriangleNormal(e, tri));
    }

    private static Triangle PickAreaWeightedTriangle(
        IReadOnlyList<int> indices, IReadOnlyList<Vector3> vertices, ref DeterministicRng rng)
    {
        var total = 0f;
        for (var i = 0; i + 2 < indices.Count; i += 3)
        {
            if (!ValidTriangle(indices, vertices.Count, i)) continue;
            total += TriangleArea(vertices[indices[i]], vertices[indices[i + 1]], vertices[indices[i + 2]]);
        }

        var target = rng.NextFloat() * total;
        Triangle fallback = default;
        for (var i = 0; i + 2 < indices.Count; i += 3)
        {
            if (!ValidTriangle(indices, vertices.Count, i)) continue;
            fallback = new Triangle(indices[i], indices[i + 1], indices[i + 2]);
            target -= TriangleArea(vertices[fallback.A], vertices[fallback.B], vertices[fallback.C]);
            if (target <= 0f) return fallback;
        }

        return fallback;
    }

    private static List<Edge> BuildEdges(IReadOnlyList<int> indices, int vertexCount)
    {
        var seen = new HashSet<(int, int)>();
        var result = new List<Edge>();
        for (var i = 0; i + 2 < indices.Count; i += 3)
        {
            if (!ValidTriangle(indices, vertexCount, i)) continue;
            Add(indices[i], indices[i + 1]);
            Add(indices[i + 1], indices[i + 2]);
            Add(indices[i + 2], indices[i]);
        }

        return result;

        void Add(int a, int b)
        {
            var key = a < b ? (a, b) : (b, a);
            if (seen.Add(key)) result.Add(new Edge(key.Item1, key.Item2));
        }
    }

    private static Edge PickWeightedEdge(
        IReadOnlyList<Edge> edges, IReadOnlyList<Vector3> vertices, ref DeterministicRng rng)
    {
        var total = edges.Sum(edge => Vector3.Distance(vertices[edge.A], vertices[edge.B]));
        var target = rng.NextFloat() * total;
        foreach (var edge in edges)
        {
            target -= Vector3.Distance(vertices[edge.A], vertices[edge.B]);
            if (target <= 0f) return edge;
        }

        return edges[^1];
    }

    private static Vector3 VertexNormal(ParticleEmitterDefinition e, int index)
    {
        return index >= 0 && index < e.MeshNormals.Count ? e.MeshNormals[index] : Vector3.Zero;
    }

    private static Vector3 TriangleNormal(ParticleEmitterDefinition e, Triangle tri)
    {
        var n = Vector3.Cross(e.MeshVertices[tri.B] - e.MeshVertices[tri.A],
            e.MeshVertices[tri.C] - e.MeshVertices[tri.A]);
        return NormalizeOrZero(n);
    }

    private static bool ValidTriangle(IReadOnlyList<int> indices, int vertexCount, int offset)
    {
        return indices[offset] >= 0 && indices[offset] < vertexCount
                                    && indices[offset + 1] >= 0 && indices[offset + 1] < vertexCount
                                    && indices[offset + 2] >= 0 && indices[offset + 2] < vertexCount;
    }

    private static float TriangleArea(Vector3 a, Vector3 b, Vector3 c)
    {
        return Vector3.Cross(b - a, c - a).Length() * 0.5f;
    }

    private static Vector3 NormalizeOrZero(Vector3 value)
    {
        return value.LengthSquared() > 1e-8f ? Vector3.Normalize(value) : Vector3.Zero;
    }

    internal static Vector3 ComputeEmissionDirection(float declination, float planarAngle, Vector3 axis)
    {
        // Recovered FNV convention: declination is elevation from the XY plane, not polar angle from +Z.
        var cosDecl = MathF.Cos(declination);
        var local = new Vector3(
            cosDecl * MathF.Cos(planarAngle),
            cosDecl * MathF.Sin(planarAngle),
            MathF.Sin(declination));
        return AlignToAxis(local, axis);
    }

    /// <summary>GrowFade: size ramps baseScale·radius → radius → baseScale·radius over grow/fade windows.</summary>
    private static float GrowFadeSize(
        GrowFadeModifierDefinition g, float age, float lifeSpan, float radius, int generation)
    {
        var growF = generation == g.GrowGeneration && g.GrowTime > 0f && age < g.GrowTime
            ? age / g.GrowTime
            : 1f;
        var remaining = lifeSpan - age;
        var fadeF = generation == g.FadeGeneration && g.FadeTime > 0f && remaining < g.FadeTime
            ? remaining / g.FadeTime
            : 1f;
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
            2 => b.Range > 0f ? MathF.Exp(-dist / b.Range) : 1f, // exponential
            _ => 1f // none
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

    /// <summary>
    ///     Recovered <c>NiPSysGravityModifier::Update</c> acceleration. The binary multiplies authored
    ///     Strength by 1.6, exponentially attenuates planar force by perpendicular distance and
    ///     spherical force by radius, then adds a signed random turbulence vector scaled by
    ///     <c>Turbulence * TurbulenceScale * 500</c>. A missing gravity object is a complete no-op.
    /// </summary>
    internal static Vector3 GravityForce(
        GravityModifierDefinition g, Vector3 pos, Vector3 signedTurbulenceSample)
    {
        if (!g.HasGravityObject)
        {
            return Vector3.Zero;
        }

        var gravityPosition = g.GravityObjectTransform.Translation;
        var localAxis = g.GravityAxis.LengthSquared() > 1e-6f ? Vector3.Normalize(g.GravityAxis) : Vector3.UnitZ;
        var axis = localAxis;
        var transformedAxis = Vector3.TransformNormal(localAxis, g.GravityObjectTransform);
        if (transformedAxis.LengthSquared() > 1e-6f)
        {
            // ResolveObjectTransform already normalizes the gravity object's frame into system-local
            // coordinates. WorldAligned is retained losslessly; both runtime branches reduce to this
            // same system-local axis for a static bake.
            axis = Vector3.Normalize(transformedAxis);
        }

        Vector3 direction;
        float distance;
        if (g.ForceType == 1)
        {
            // Spherical gravity is ATTRACTIVE in the recovered update: object position - particle.
            var towardObject = gravityPosition - pos;
            distance = towardObject.Length();
            direction = distance > 1e-6f ? towardObject / distance : Vector3.Zero;
        }
        else
        {
            direction = axis;
            // Planar decay uses exp(-Decay * abs(dot(axis, objectPos - particlePos))).
            distance = MathF.Abs(Vector3.Dot(axis, gravityPosition - pos));
        }

#pragma warning disable S1244 // authored zero Decay/Turbulence disables the term; exact comparison intended
        var attenuation = g.Decay != 0f ? MathF.Exp(-g.Decay * distance) : 1f;
        var force = direction * (g.Strength * 1.6f * attenuation);
        if (g.Turbulence != 0f)
#pragma warning restore S1244
        {
            force += signedTurbulenceSample * (g.Turbulence * g.TurbulenceScale * 500f);
        }

        return force;
    }

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

    /// <summary>
    ///     Drag axis (system-local, normalized) + range origin + coefficients, precomputed once per
    ///     modifier. <see cref="Active" /> is false when the engine would no-op the modifier (no drag object,
    ///     non-positive percentage, or a degenerate axis).
    /// </summary>
    private readonly record struct PreparedDrag(
        bool Active,
        Vector3 AxisHat,
        Vector3 ObjectPos,
        float Range,
        float Falloff,
        float Percentage);

    private struct Particle
    {
        public Vector3 Position;
        public Vector3 Velocity;
        public float Age;
        public float LifeSpan;
        public float BaseSize;
        public float Size;
        public Vector4 Color;

        /// <summary>
        ///     How many NiPSysSpawnModifier generations deep this particle is (0 = emitted directly).
        ///     Spawning stops once this reaches the modifier's NumSpawnGenerations.
        /// </summary>
        public int SpawnGeneration;

        public float Rotation;
        public float RotationSpeed;
        public float AtlasSeed;
    }

    private readonly record struct MeshSample(Vector3 Position, Vector3 Normal);

    private readonly record struct Triangle(int A, int B, int C);

    private readonly record struct Edge(int A, int B);

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

        /// <summary>Symmetric unit random matching the engine helper used by gravity turbulence.</summary>
        public float NextSignedFloat()
        {
            return NextFloat() * 2f - 1f;
        }
    }
}
