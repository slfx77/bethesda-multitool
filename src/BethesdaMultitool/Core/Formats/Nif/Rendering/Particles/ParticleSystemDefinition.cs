using System.Numerics;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Particles;

/// <summary>
///     The parsed, engine-faithful description of one <c>NiParticleSystem</c> (FO3/FNV) — enough for the
///     particle baker (<c>NifParticleBaker</c>) to simulate a deterministic steady-state cloud and for the
///     extractor to emit camera-facing quads. Field semantics are grounded in the decompiled engine spec
///     (tools/GhidraProject/particles_formula_spec.md). The authored per-particle arrays in NiPSysData are
///     NOT carried: the engine fills them at runtime by simulation, so the baker re-simulates from scratch.
/// </summary>
internal sealed class ParticleSystemDefinition
{
    /// <summary>Block index of the source NiParticleSystem.</summary>
    public int BlockIndex { get; init; }

    /// <summary>True when the system simulates in world space (NiParticleSystem.World Space).</summary>
    public bool WorldSpace { get; init; }

    /// <summary>Max live particles (NiPSysData capacity / Num Vertices). The bake is capped to this.</summary>
    public int Capacity { get; init; }

    /// <summary>The emitter (one per system in practice). Null if no recognised emitter modifier was found.</summary>
    public ParticleEmitterDefinition? Emitter { get; set; }

    /// <summary>Modifiers in execution order (the NiParticleSystem Modifiers[] array order).</summary>
    public List<ParticleModifierDefinition> Modifiers { get; } = [];

    /// <summary>Diffuse texture path resolved from the system's shader property (the particle sprite).</summary>
    public string? DiffuseTexturePath { get; set; }

    /// <summary>NiAlphaProperty source blend factor (bits 1-4); default 6 = SRC_ALPHA.</summary>
    public byte SrcBlendMode { get; set; } = 6;

    /// <summary>NiAlphaProperty dest blend factor (bits 5-8); default 1 = ONE (additive — typical glow).</summary>
    public byte DstBlendMode { get; set; } = 1;

    /// <summary>True when the system has a NiAlphaProperty with blend enabled (it nearly always does).</summary>
    public bool HasAlphaBlend { get; set; } = true;
}

/// <summary>Emitter volume shape (NiPSysEmitter subtype).</summary>
internal enum ParticleEmitterShape
{
    Point,
    Box,
    Sphere,
    Cylinder,
    Mesh,
}

/// <summary>
///     The emitter modifier (NiPSysEmitter family): initial spawn distribution + per-particle initial
///     velocity / lifespan / color / size. See particles_formula_spec.md "Emit".
/// </summary>
internal sealed class ParticleEmitterDefinition : ParticleModifierDefinition
{
    public ParticleEmitterShape Shape { get; init; }

    public float Speed { get; init; }
    public float SpeedVariation { get; init; }
    public float Declination { get; init; }
    public float DeclinationVariation { get; init; }
    public float PlanarAngle { get; init; }
    public float PlanarAngleVariation { get; init; }
    public Vector4 InitialColor { get; init; } = Vector4.One;
    public float InitialRadius { get; init; } = 1f;
    public float RadiusVariation { get; init; }
    public float LifeSpan { get; init; }
    public float LifeSpanVariation { get; init; }

    /// <summary>Declination reference axis. Declination=0 emits along this; the emitter-object transform then
    /// orients it to world. Defaults to +Z (the convention for volume emitters, which carry no explicit axis) —
    /// NOT +X, or the fountain jet shoots sideways. Mesh emitters override it with their authored Emission Axis.</summary>
    public Vector3 EmissionAxis { get; init; } = Vector3.UnitZ;

    // Volume params (only the relevant ones for the shape are populated).
    public float Width { get; init; }
    public float Height { get; init; }
    public float Depth { get; init; }
    public float Radius { get; init; }

    /// <summary>Local transform of the emitter object (volume emitters), relative to the system node.</summary>
    public Matrix4x4 EmitterObjectTransform { get; init; } = Matrix4x4.Identity;

    /// <summary>For mesh emitters: block indices of the emitter-volume meshes (NiPSysMeshEmitter.Emitter Meshes).
    /// These are suppressed from rendering and used to derive the spawn volume (MVP: their AABB as a box).</summary>
    public IReadOnlyList<int> EmitterMeshIndices { get; init; } = [];

    /// <summary>For mesh emitters: the emitter-volume AABB (system-local), computed from the emitter mesh
    /// geometry at extraction time (Phase 3). The baker spawns uniformly within this box. Zero ⇒ point emit.</summary>
    public Vector3 MeshBoundsMin { get; set; }
    public Vector3 MeshBoundsMax { get; set; }

    /// <summary>Steady-state birth rate (particles/sec). Resolved from the emitter controller, else estimated.</summary>
    public float BirthRate { get; set; }
}

/// <summary>The recognised per-tick modifier kinds the baker simulates.</summary>
internal enum ParticleModifierKind
{
    Emitter,
    AgeDeath,
    GrowFade,
    Color,
    Bomb,
    Gravity,
    Drag,
    Position,
    Rotation,
    Spawn,
    BoundUpdate,
    Other,
}

/// <summary>Base for a parsed NiPSysModifier. Concrete kinds carry their own typed params.</summary>
internal class ParticleModifierDefinition
{
    public ParticleModifierKind Kind { get; init; }
    public bool Active { get; init; } = true;
    public int BlockIndex { get; init; }
}

/// <summary>NiPSysGrowFadeModifier: size ramps baseScale→1→baseScale over grow/fade times.</summary>
internal sealed class GrowFadeModifierDefinition : ParticleModifierDefinition
{
    public float GrowTime { get; init; }
    public ushort GrowGeneration { get; init; }
    public float FadeTime { get; init; }
    public ushort FadeGeneration { get; init; }
    public float BaseScale { get; init; } = 1f;
}

/// <summary>NiPSysBombModifier: the vortex/blast force (whirlwind). Force applied along the symmetry dir.</summary>
internal sealed class BombModifierDefinition : ParticleModifierDefinition
{
    public Vector3 BombAxis { get; init; } = Vector3.UnitX;
    public float Range { get; init; }       // "Decay" field = range over which the force decays
    public float DeltaV { get; init; }      // velocity delta scale
    public int DecayType { get; init; }     // 0 none, 1 linear, 2 exponential
    public int SymmetryType { get; init; }  // 0 spherical, 1 cylindrical, 2 planar
    public Matrix4x4 BombObjectTransform { get; init; } = Matrix4x4.Identity;
    public bool HasBombObject { get; init; }
}

/// <summary>NiPSysGravityModifier: directional/spherical gravity applied to velocity.</summary>
internal sealed class GravityModifierDefinition : ParticleModifierDefinition
{
    public Vector3 GravityAxis { get; init; } = Vector3.UnitX;
    public float Decay { get; init; }
    public float Strength { get; init; } = 1f;
    public int ForceType { get; init; }     // 0 planar, 1 spherical
    public Matrix4x4 GravityObjectTransform { get; init; } = Matrix4x4.Identity;
    public bool HasGravityObject { get; init; }
}

/// <summary>
///     NiPSysDragModifier — anisotropic drag, grounded in the decompiled <c>NiPSysDragModifier::Update</c>.
///     The engine damps ONLY the velocity component along the (drag-object-transformed) drag axis, and only
///     for particles within <see cref="Range" /> of the drag object (linearly fading to zero at
///     <see cref="RangeFalloff" />). Crucially the engine NO-OPS the whole modifier when there is no drag
///     object, so <see cref="HasDragObject" /> gates it. Per tick: <c>f = Percentage · dt/(1/30)</c>;
///     <c>v -= min(f,1) · dot(v, axisHat) · axisHat</c>.
/// </summary>
internal sealed class DragModifierDefinition : ParticleModifierDefinition
{
    public Vector3 DragAxis { get; init; } = Vector3.UnitX;
    public float Percentage { get; init; } = 0.05f; // linear drag coefficient (nif.xml default)
    public float Range { get; init; }
    public float RangeFalloff { get; init; }

    /// <summary>The engine no-ops drag without a drag object (the reference frame for the axis + range).</summary>
    public bool HasDragObject { get; init; }

    /// <summary>Drag object local transform (system-relative): orients the drag axis + locates the range origin.</summary>
    public Matrix4x4 DragObjectTransform { get; init; } = Matrix4x4.Identity;
}

/// <summary>
///     NiPSysSpawnModifier — when a particle dies, spawn child copies, grounded in the decompiled
///     <c>NiPSysSpawnModifier::SpawnParticles</c>: gated by <c>spawnGeneration &lt; NumSpawnGenerations</c> and a
///     <c>rand ≤ PercentageSpawned</c> roll; count = <c>MinToSpawn + round(rand·(MaxToSpawn-MinToSpawn))</c>
///     (min 1). Children inherit the parent's death position + (chaos-perturbed) velocity and a new lifespan.
///     This is what turns a sparse jet into a full spray (the fountain's splash). Field order per nif.xml.
/// </summary>
internal sealed class SpawnModifierDefinition : ParticleModifierDefinition
{
    public int NumSpawnGenerations { get; init; }
    public float PercentageSpawned { get; init; } = 1f;
    public int MinToSpawn { get; init; } = 1;
    public int MaxToSpawn { get; init; } = 1;
    public float SpawnSpeedVariation { get; init; }
    public float SpawnDirVariation { get; init; }
    public float LifeSpan { get; init; }
    public float LifeSpanVariation { get; init; }
}

/// <summary>A color key (NiColorData): RGBA at a normalized life fraction.</summary>
internal readonly record struct ParticleColorKey(float Time, Vector4 Color);

/// <summary>
///     NiPSysColorModifier (NiColorData keys) / BSPSysSimpleColorModifier (3-color gradient + fade
///     envelope). Colour is sampled at <c>age/lifespan</c>; the fade-in/out envelope multiplies alpha so
///     particles fade in at birth and out at death (the key fix for additive over-glow).
/// </summary>
internal sealed class ColorModifierDefinition : ParticleModifierDefinition
{
    // BSPSysSimpleColorModifier (FO3/FNV) — see nif.xml. Defaults match nif.xml.
    public bool IsSimpleColor { get; init; }
    public float FadeInPercent { get; init; } = 0.1f;
    public float FadeOutPercent { get; init; } = 0.9f;
    public float Color1StartPercent { get; init; }
    public float Color1EndPercent { get; init; }
    public float Color2StartPercent { get; init; } = 1f;
    public float Color2EndPercent { get; init; }
    public Vector4 Color0 { get; init; } = Vector4.One;
    public Vector4 Color1 { get; init; } = Vector4.One;
    public Vector4 Color2 { get; init; } = Vector4.One;

    // NiPSysColorModifier (NiColorData) keys, sorted by Time in [0,1].
    public ParticleColorKey[] Keys { get; init; } = [];

    /// <summary>Sample the modifier's RGBA at normalized life fraction <paramref name="t" /> (0=birth, 1=death),
    /// with the fade-in/out envelope applied to alpha. Falls back to <paramref name="initial" /> when empty.</summary>
    public Vector4 Sample(float t, Vector4 initial)
    {
        t = Math.Clamp(t, 0f, 1f);
        Vector4 color;
        if (IsSimpleColor)
        {
            color = SampleGradient(t);
        }
        else if (Keys.Length > 0)
        {
            color = SampleKeys(t);
        }
        else
        {
            color = initial;
        }

        // Apply the fade envelope to ALL channels: alpha for alpha-blended particles, AND rgb so additive
        // (One/One) glow particles dim toward black at birth/death instead of staying full-bright (the
        // additive over-glow fix). A glow that fades to black is the standard look.
        var fade = FadeEnvelope(t);
        return new Vector4(color.X * fade, color.Y * fade, color.Z * fade, color.W * fade);
    }

    private Vector4 SampleGradient(float t)
    {
        // Color1 is the BASE colour, held for most of life. Color0 blends IN over the start window
        // [Color1Start, Color1End] and Color2 blends OUT over the end window [Color2Start, Color2End] —
        // but ONLY when those windows are non-degenerate. FXDust authors all percents = 0 with Color0/2
        // black and Color1 the real dust colour: with degenerate windows that must resolve to Color1, not
        // fall through to a black Color2 (which made the particles invisible).
        if (Color1EndPercent > Color1StartPercent && t < Color1EndPercent)
        {
            return t <= Color1StartPercent
                ? Color0
                : Vector4.Lerp(Color0, Color1, (t - Color1StartPercent) / (Color1EndPercent - Color1StartPercent));
        }

        if (Color2EndPercent > Color2StartPercent && t > Color2StartPercent)
        {
            return t >= Color2EndPercent
                ? Color2
                : Vector4.Lerp(Color1, Color2, (t - Color2StartPercent) / (Color2EndPercent - Color2StartPercent));
        }

        return Color1;
    }

    private Vector4 SampleKeys(float t)
    {
        if (Keys.Length == 1 || t <= Keys[0].Time) return Keys[0].Color;
        for (var i = 1; i < Keys.Length; i++)
        {
            if (t <= Keys[i].Time)
            {
                var span = Keys[i].Time - Keys[i - 1].Time;
                var f = span > 1e-6f ? (t - Keys[i - 1].Time) / span : 0f;
                return Vector4.Lerp(Keys[i - 1].Color, Keys[i].Color, f);
            }
        }

        return Keys[^1].Color;
    }

    private float FadeEnvelope(float t)
    {
        var fade = 1f;
        if (FadeInPercent > 1e-4f && t < FadeInPercent)
        {
            fade = t / FadeInPercent;
        }

        if (FadeOutPercent < 1f - 1e-4f && t > FadeOutPercent)
        {
            fade = MathF.Min(fade, (1f - t) / (1f - FadeOutPercent));
        }

        return Math.Clamp(fade, 0f, 1f);
    }
}
