using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;

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

    /// <summary>Concrete source block type, including schema-backed aliases such as BSStripParticleSystem.</summary>
    public string SourceTypeName { get; init; } = "NiParticleSystem";

    /// <summary>The inherited on-disk geometry layout used to locate the shared NiPSys tail.</summary>
    public ParticleSystemSourceLayout SourceLayout { get; init; }

    /// <summary>True when the system simulates in world space (NiParticleSystem.World Space).</summary>
    public bool WorldSpace { get; init; }

    /// <summary>Max live particles (NiPSysData capacity / Num Vertices). The bake is capped to this.</summary>
    public int Capacity { get; init; }

    /// <summary>The exact deterministic seed used by the static and live particle baker.</summary>
    public uint DeterministicSeed =>
        unchecked((uint)(BlockIndex * 2654435761u) ^ 0x9E3779B9u);

    /// <summary>
    ///     Authored atlas rectangles from <c>NiParticlesData.Subtexture Offsets</c>. Each vector is
    ///     <c>(uOffset, vOffset, uScale, vScale)</c>; an empty list means the full texture.
    /// </summary>
    public IReadOnlyList<Vector4> SubtextureOffsets { get; set; } = [];

    /// <summary>Authored sprite width/height ratio. One is the legacy FO3/FNV default.</summary>
    public float AspectRatio { get; set; } = 1f;

    /// <summary>The emitter (one per system in practice). Null if no recognised emitter modifier was found.</summary>
    public ParticleEmitterDefinition? Emitter { get; set; }

    /// <summary>Modifiers in execution order (the NiParticleSystem Modifiers[] array order).</summary>
    public List<ParticleModifierDefinition> Modifiers { get; } = [];

    /// <summary>
    ///     Ordered simulator features retained by the parser but not yet executed by the deterministic baker.
    ///     This is exposed beside the live definition so unsupported modern steps are observable rather than
    ///     silently approximated.
    /// </summary>
    public List<string> UnsupportedSimulatorSteps { get; } = [];

    /// <summary>Compact diagnostic value suitable for renderer/capture telemetry.</summary>
    public string SupportTelemetry => UnsupportedSimulatorSteps.Count == 0
        ? $"{SourceTypeName}:{SourceLayout}:supported"
        : $"{SourceTypeName}:{SourceLayout}:partial[{string.Join(",", UnsupportedSimulatorSteps)}]";

    /// <summary>Diffuse texture path resolved from the system's shader property (the particle sprite).</summary>
    public string? DiffuseTexturePath { get; set; }

    /// <summary>
    ///     Concrete shader property attached to the particle system. Lighting is selected from this property,
    ///     not inferred from the blend equation: standard-alpha dust can still use the retail NoLighting path.
    /// </summary>
    public string? ShaderPropertyType { get; set; }

    /// <summary>True only for shader families whose retail contract bypasses scene lighting.</summary>
    public bool UsesUnlitShader => ShaderPropertyType is
        "BSShaderNoLightingProperty" or "BSEffectShaderProperty";

    /// <summary>NiAlphaProperty source blend factor (bits 1-4); default 6 = SRC_ALPHA.</summary>
    public byte SrcBlendMode { get; set; } = 6;

    /// <summary>NiAlphaProperty dest blend factor (bits 5-8); default 1 = ONE (additive — typical glow).</summary>
    public byte DstBlendMode { get; set; } = 1;

    /// <summary>True when the system has a NiAlphaProperty with blend enabled (it nearly always does).</summary>
    public bool HasAlphaBlend { get; set; } = true;

    /// <summary>
    ///     Manager-driven material opacity for the particle system. FNV SandDust02's PCloud01
    ///     authors a 0.05 maximum here; dropping this controller rendered that layer at 1.0.
    /// </summary>
    public NifMaterialAlphaController? MaterialAlphaController { get; set; }
}

internal enum ParticleSystemSourceLayout
{
    LegacyNiGeometry,
    SkyrimNiGeometry,
    BsGeometry,
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

/// <summary>NiPSysMeshEmitter initial-velocity selection.</summary>
internal enum ParticleVelocityType
{
    UseNormals = 0,
    UseRandom = 1,
    UseDirection = 2,
}

/// <summary>NiPSysMeshEmitter surface element used to select the spawn point.</summary>
internal enum ParticleEmitFrom
{
    Vertices = 0,
    FaceCenter = 1,
    EdgeCenter = 2,
    FaceSurface = 3,
    EdgeSurface = 4,
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

    /// <summary>Declination reference axis. Recovered FNV math treats declination as elevation, so π/2 emits
    /// along this axis and zero emits in its perpendicular plane; the emitter-object transform then orients it
    /// to world. Defaults to +Z for volume emitters. Mesh emitters override it with their authored Emission Axis.</summary>
    public Vector3 EmissionAxis { get; init; } = Vector3.UnitZ;

    public ParticleVelocityType VelocityType { get; init; } = ParticleVelocityType.UseDirection;
    public ParticleEmitFrom EmitFrom { get; init; } = ParticleEmitFrom.Vertices;

    // Volume params (only the relevant ones for the shape are populated).
    public float Width { get; init; }
    public float Height { get; init; }
    public float Depth { get; init; }
    public float Radius { get; init; }

    /// <summary>
    ///     Transform of the emitter object (volume emitters) relative to the system node. The parser seeds it
    ///     with the emitter object's own LOCAL transform (correct only when the object is a direct child of
    ///     the system); the extractor re-derives it as <c>emitterWorld · inverse(systemWorld)</c> from the
    ///     scene-graph walk when the emitter object resolves — the same frame fix-up mesh emitters use.
    /// </summary>
    public Matrix4x4 EmitterObjectTransform { get; set; } = Matrix4x4.Identity;

    /// <summary>Block index of the volume emitter's Emitter Object node (-1 when absent), so the extractor
    /// can resolve its WORLD transform from the scene-graph walk instead of trusting the raw local read.</summary>
    public int EmitterObjectIndex { get; init; } = -1;

    /// <summary>For mesh emitters: block indices of the authored emission meshes
    /// (<c>NiPSysMeshEmitter.Emitter Meshes</c>). These shapes are suppressed from ordinary rendering.</summary>
    public IReadOnlyList<int> EmitterMeshIndices { get; init; } = [];

    /// <summary>Compatibility projection of the emitter geometry bounds in particle-system local space.</summary>
    public Vector3 MeshBoundsMin { get; set; }
    public Vector3 MeshBoundsMax { get; set; }

    /// <summary>Emitter mesh geometry expressed in particle-system local space.</summary>
    public IReadOnlyList<Vector3> MeshVertices { get; set; } = [];
    public IReadOnlyList<Vector3> MeshNormals { get; set; } = [];
    public IReadOnlyList<int> MeshTriangles { get; set; } = [];

    /// <summary>
    ///     Static birth-rate fallback (particles/sec) for definitions without an authored live controller.
    ///     Parsed NiPSysEmitterCtlr data lives in <see cref="BirthRateController" /> instead.
    /// </summary>
    public float BirthRate { get; set; }

    /// <summary>
    ///     Authored, time-sampled NiPSysEmitterCtlr birth-rate curve. Null means the controller graph was
    ///     absent or could not be decoded safely, in which case the baker retains its bounded static fallback.
    /// </summary>
    public ParticleRateControllerDefinition? BirthRateController { get; set; }
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
    Subtexture,
    Other,
}

/// <summary>Base for a parsed NiPSysModifier. Concrete kinds carry their own typed params.</summary>
internal class ParticleModifierDefinition
{
    public ParticleModifierKind Kind { get; init; }
    public bool Active { get; init; } = true;
    public int BlockIndex { get; init; }
    public string SourceTypeName { get; set; } = "NiPSysModifier";
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

/// <summary>NiPSysRotationModifier authored initial angle and angular velocity.</summary>
internal sealed class RotationModifierDefinition : ParticleModifierDefinition
{
    public float RotationSpeed { get; init; }
    public float RotationSpeedVariation { get; init; }
    public float RotationAngle { get; init; }
    public float RotationAngleVariation { get; init; }
    public bool RandomSpeedSign { get; init; }
}

/// <summary>BSPSysSubTexModifier atlas-frame controller.</summary>
internal sealed class SubtextureModifierDefinition : ParticleModifierDefinition
{
    public float StartFrame { get; init; }
    public float StartFrameFudge { get; init; }
    public float EndFrame { get; init; }
    public float LoopStartFrame { get; init; }
    public float LoopStartFrameFudge { get; init; }
    public float FrameCount { get; init; }
    public float FrameCountFudge { get; init; }

    internal int SampleFrame(float age, float seed, int atlasCount)
    {
        if (atlasCount <= 1)
        {
            return 0;
        }

        // Bethesda's per-particle fudge can push an authored edge frame slightly past the atlas
        // (retail Skyrim fire reaches 15.992 for a 16-frame sheet and 63.190 for a 64-frame sheet).
        // Normalize the sampled window before using it as Math.Clamp bounds; otherwise start >
        // atlasCount - 1 throws and rejects the entire placed effect during mesh decode.
        var lastFrame = atlasCount - 1f;
        var start = Math.Clamp(StartFrame + StartFrameFudge * seed, 0f, lastFrame);
        var end = EndFrame >= start
            ? Math.Clamp(EndFrame, start, lastFrame)
            : lastFrame;
        var rate = MathF.Max(0f, FrameCount + FrameCountFudge * (seed - 0.5f));
        var frame = start + age * rate;
        if (frame > end)
        {
            var loopStart = Math.Clamp(LoopStartFrame + LoopStartFrameFudge * seed, start, end);
            var loopLength = MathF.Max(1f, end - loopStart + 1f);
            frame = loopStart + (frame - loopStart) % loopLength;
        }

        return Math.Clamp((int)MathF.Floor(frame), 0, atlasCount - 1);
    }
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
    public float Turbulence { get; init; }
    public float TurbulenceScale { get; init; } = 1f;
    public bool WorldAligned { get; init; }
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

    /// <summary>Sample the modifier's RGBA at normalized life fraction <paramref name="t" /> (0=birth, 1=death).
    /// Falls back to <paramref name="initial" /> when empty.</summary>
    public Vector4 Sample(float t, Vector4 initial)
    {
        t = Math.Clamp(t, 0f, 1f);
        if (IsSimpleColor)
        {
            return SampleSimpleColor(t);
        }

        if (Keys.Length > 0)
        {
            return SampleKeys(t);
        }

        return initial;
    }

    private Vector4 SampleSimpleColor(float t)
    {
        // FalloutNV MemDebug BSPSysSimpleColorModifier::Update (PDB 0004:00797F38) evaluates RGB and alpha
        // independently. The NIF serializes each RGB transition's End value before its Start value; retail
        // SandDust02 therefore authors Color0 -> Color1 over 0.2 -> 0.4 and Color1 -> Color2 over 0.6 -> 0.8.
        // Treating those pairs as conventional Start/End values skips the authored RGB transitions.
        // Interpolating RGBA together and then applying a generic fade envelope can also double-fade
        // endpoint alpha for other authored configurations, so retain the engine's split branches.
        var rgb = SampleSimpleColorRgb(t);
        var alpha = SampleSimpleColorAlpha(t);
        return new Vector4(rgb.X, rgb.Y, rgb.Z, alpha);
    }

    private Vector4 SampleSimpleColorRgb(float t)
    {
        var color1Span = Color1StartPercent - Color1EndPercent;
        if (color1Span != 0f && t < Color1StartPercent)
        {
            return t < Color1EndPercent
                ? Color0
                : Vector4.Lerp(Color0, Color1, (t - Color1EndPercent) / color1Span);
        }

        var color2Span = Color2StartPercent - Color2EndPercent;
        if (color2Span == 0f || t <= Color2EndPercent)
        {
            return Color1;
        }

        return t <= Color2StartPercent
            ? Vector4.Lerp(Color1, Color2, (t - Color2EndPercent) / color2Span)
            : Color2;
    }

    private float SampleSimpleColorAlpha(float t)
    {
        if (FadeOutPercent == 0f || t <= FadeOutPercent)
        {
            return FadeInPercent == 0f || t >= FadeInPercent
                ? Color1.W
                : Color0.W + ((Color1.W - Color0.W) * (t / FadeInPercent));
        }

        return Color1.W + ((Color2.W - Color1.W) *
                           ((t - FadeOutPercent) / (1f - FadeOutPercent)));
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

}
