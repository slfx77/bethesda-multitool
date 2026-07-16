using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Particles;

/// <summary>Opt-in and deterministic-clock policy for the temporary legacy live-particle path.</summary>
internal static class ParticleLiveSettings
{
    internal const string EnvironmentVariable = "FALLOUT_VIEWER_LIVE_PARTICLES";
    internal const int UpdatesPerSecond = 30;

    /// <summary>
    ///     Live rebaking is deliberately opt-in until the cross-game capture/performance matrix passes.
    ///     Unset keeps the existing static particle-cloud geometry and persistent decoded-mesh cache.
    /// </summary>
    internal static bool Enabled { get; } =
        Environment.GetEnvironmentVariable(EnvironmentVariable) == "1";

    /// <summary>
    ///     Quantize the renderer clock to a fixed 30 Hz controller tick. This makes repeated captures at
    ///     the same frame time byte-deterministic and avoids rebuilding a different cloud for sub-tick jitter.
    /// </summary>
    internal static float QuantizeFrameTime(double elapsedSeconds)
    {
        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds <= 0d)
        {
            return 0f;
        }

        var ticks = Math.Floor(elapsedSeconds * UpdatesPerSecond);
        var result = (float)(ticks / UpdatesPerSecond);
        return float.IsFinite(result) ? result : 0f;
    }

    /// <summary>
    ///     The persistent mesh payload intentionally contains only the static fallback geometry, not the
    ///     mutable parser/modifier graph. An opted-in particle-bearing decode must therefore use source bytes.
    /// </summary>
    internal static bool UsePersistentDecodedMeshCache(
        bool liveParticlesEnabled,
        bool isNegative,
        bool containsParticleSource) =>
        !liveParticlesEnabled || (!isNegative && !containsParticleSource);
}

/// <summary>
///     CPU-side source retained for one legacy particle submesh. The definition already contains resolved
///     emitter mesh geometry; <see cref="SystemWorld" /> places each rebaked cloud in NIF model space.
/// </summary>
internal sealed record ParticleRuntimeDefinition(
    ParticleSystemDefinition Definition,
    Matrix4x4 SystemWorld,
    ParticleBakeOptions? BakeOptions = null);

/// <summary>One immutable CPU geometry snapshot ready for a transient GPU upload.</summary>
internal sealed record ParticleGeometrySnapshot(
    GpuMeshUploader.GpuVertex[] Vertices,
    ushort[] Indices,
    Vector3[] Centers,
    bool IsValid = true)
{
    internal static ParticleGeometrySnapshot Empty { get; } = new([], [], []);
    internal static ParticleGeometrySnapshot Invalid { get; } = new([], [], [], IsValid: false);
}

/// <summary>
///     Pure live-particle frame builder. It replays the authored controller window at a requested fixed time,
///     then uses the same billboard encoding as the static extractor. GPU ownership remains in D3D12 code.
/// </summary>
internal static class ParticleLiveSnapshotBuilder
{
    internal static ParticleGeometrySnapshot Build(
        ParticleRuntimeDefinition runtime,
        float snapshotTimeSeconds)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        var template = runtime.BakeOptions ?? ParticleBakeOptions.Default;
        var options = new ParticleBakeOptions
        {
            TimeStep = template.TimeStep,
            MaxParticles = template.MaxParticles,
            SettleMarginSeconds = template.SettleMarginSeconds,
            SnapshotTimeSeconds = float.IsFinite(snapshotTimeSeconds) ? snapshotTimeSeconds : 0f,
        };
        var baked = NifParticleBaker.Bake(runtime.Definition, options);
        if (baked.Count == 0 ||
            NifParticleSystemExtractor.BuildSubmesh(
                baked, runtime.SystemWorld, runtime.Definition) is not { } submesh)
        {
            return baked.Count == 0 ? ParticleGeometrySnapshot.Empty : ParticleGeometrySnapshot.Invalid;
        }

        var vertices = GpuMeshUploader.BuildVertices(submesh);
        if (vertices.Length == 0 || vertices.Length % 4 != 0 ||
            submesh.Triangles.Length != vertices.Length / 4 * ParticleDepthSort.IndicesPerQuad)
        {
            return ParticleGeometrySnapshot.Invalid;
        }

        var centers = new Vector3[vertices.Length / 4];
        for (var i = 0; i < centers.Length; i++)
        {
            centers[i] = vertices[i * 4].Tangent;
        }

        return new ParticleGeometrySnapshot(vertices, submesh.Triangles, centers);
    }
}
