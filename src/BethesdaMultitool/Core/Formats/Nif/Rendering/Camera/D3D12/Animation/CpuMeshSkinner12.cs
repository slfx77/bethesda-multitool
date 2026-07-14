#if WINDOWS_GUI
using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using Vortice.Direct3D12;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Camera.D3D12.Animation;

/// <summary>
///     Per-frame CPU skinner for keyframe-animated placed meshes (Morrowind banners). Pose is
///     evaluated ONCE per MESH off the shared animation clock — every placement of one NIF plays in
///     sync, matching the engine's global-timer animation — and each skinned submesh is re-skinned
///     into a ring-buffer vertex allocation exposed as
///     <see cref="CachedSubmesh12.AnimatedVertexBufferView" />. The draw loop binds
///     <see cref="CachedSubmesh12.EffectiveVertexBufferView" />, so both the main draw and the
///     shadow replay pick up the pose with no draw-path changes; the batching/culling/freeze
///     machinery is untouched (the static rest-pose VB remains the fallback).
///     <para>
///         Invariant: every registered mesh's overrides are CLEARED or REFRESHED each
///         <see cref="Tick" /> — a ring-buffer VBV is only valid for the frame it was written, so a
///         stale override must never survive.
///     </para>
/// </summary>
internal sealed class CpuMeshSkinner12
{
    // Budget gates (env-tunable). Overflow → the mesh simply draws its static rest pose.
    private static readonly int MaxAnimatedMeshes = ReadEnvInt("FALLOUT_VIEWER_ANIM_MAX_MESHES", 32);
    private static readonly int MaxAnimatedVerticesPerFrame = ReadEnvInt("FALLOUT_VIEWER_ANIM_MAX_VERTS", 65_536);
    private static readonly float AnimateRadius = ReadEnvInt("FALLOUT_VIEWER_ANIM_RADIUS", 8_192);

    // A registered mesh survives missing this many RESOLVE PASSES before it drops out. Staleness is
    // counted in resolve passes — the frames where Register COULD have re-sighted it — never in
    // wall-clock frames: a settled scene freezes its batches and skips the resolve loop entirely
    // (ReferenceRenderer12 batch reuse), so a frame-based TTL expired live meshes while the camera
    // was still. Symptom: banners animated only while the camera moved, "pulsing" once per batch
    // rebuild and snapping back to rest whenever the scene settled.
    private const int ResolvePassGrace = 2;

    private sealed class Entry
    {
        public required CachedNifMesh12 Mesh;

        /// <summary>Nearest instance distance² from the LAST COMPLETE sighting — drives the radius
        /// gate and nearest-first priority. Never decays in place: it is replaced wholesale from
        /// <see cref="PendingDistanceSq" /> when a resolve pass re-sights the mesh, so it stays
        /// valid across arbitrarily long frozen (batch-reuse) streaks where Register cannot run.</summary>
        public float NearestDistanceSq;

        /// <summary>Min-accumulator for the CURRENT resolve pass's Register calls; consumed (and
        /// reset) by the same frame's Tick. Register runs BEFORE Tick within a frame, so decaying
        /// <see cref="NearestDistanceSq" /> directly in Tick erased the refresh it had just
        /// received — every frozen streak then saw MaxValue and the radius gate stopped playback
        /// the moment the camera parked (the "only animates while the camera moves" report).</summary>
        public float PendingDistanceSq = float.MaxValue;

        public long LastSeenResolvePass;
        public Matrix4x4[] BoneWorlds = [];
        public Matrix4x4[] SkinMatrices = [];
    }

    private readonly Dictionary<CachedNifMesh12, Entry> _entries = new();
    private readonly List<Entry> _tickScratch = new();
    private long _resolvePassCounter;

    /// <summary>True when any submesh carries a fresh animated VBV this frame — the host ORs this
    /// into its shadow-refresh condition (same contract as the wind-leaves flag).</summary>
    public bool AnyAnimatedThisFrame { get; private set; }

    /// <summary>
    ///     Registers (or refreshes) an animated mesh sighted by the resolve pass. Cheap and
    ///     idempotent — call on every sighting; distance keeps the nearest instance for the
    ///     radius/budget priority.
    /// </summary>
    public void Register(CachedNifMesh12 mesh, float nearestDistanceSq)
    {
        if (mesh.Animation is null)
        {
            return;
        }

        if (_entries.TryGetValue(mesh, out var entry))
        {
            entry.LastSeenResolvePass = _resolvePassCounter;
            entry.PendingDistanceSq = MathF.Min(entry.PendingDistanceSq, nearestDistanceSq);
            return;
        }

        var boneCount = mesh.Animation.Bones.Length;
        var maxSkinBones = 0;
        foreach (var sub in mesh.Submeshes)
        {
            if (sub.Skin is { } skin)
            {
                maxSkinBones = Math.Max(maxSkinBones, skin.InverseBindPoses.Length);
            }
        }

        if (maxSkinBones == 0)
        {
            return; // rig without skinnable submeshes — nothing to pose
        }

        _entries[mesh] = new Entry
        {
            Mesh = mesh,
            NearestDistanceSq = nearestDistanceSq,
            LastSeenResolvePass = _resolvePassCounter,
            BoneWorlds = new Matrix4x4[boneCount],
            SkinMatrices = new Matrix4x4[maxSkinBones],
        };
    }

    /// <summary>
    ///     Per-frame driver: re-poses every in-budget registered mesh into fresh ring allocations
    ///     and clears every other override. Render-thread only, before the reference draw pass.
    ///     <paramref name="resolveRan" /> = this frame ran the mesh-resolve loop (i.e. Register had
    ///     a chance to re-sight live meshes). On batch-REUSE frames it must be false so entries
    ///     neither expire nor decay while the frozen scene gives them no way to re-register.
    /// </summary>
    public void Tick(
        int frameIndex, GpuRingBuffer12 ring, double clockSeconds, Vector3 cameraPos, bool enabled,
        bool resolveRan)
    {
        if (resolveRan)
        {
            _resolvePassCounter++;
        }

        AnyAnimatedThisFrame = false;

        if (_entries.Count == 0)
        {
            return;
        }

        // Snapshot + prune: entries that missed ResolvePassGrace consecutive resolve passes fell
        // out of the streamed set (or were evicted); their overrides were cleared on their last
        // ticked frame. Counted in resolve passes, NOT frames — see ResolvePassGrace.
        _tickScratch.Clear();
        List<CachedNifMesh12>? expired = null;
        foreach (var entry in _entries.Values)
        {
            // Rule: clear-or-refresh EVERY frame — never a stale ring address.
            foreach (var sub in entry.Mesh.Submeshes)
            {
                sub.AnimatedVertexBufferView = null;
            }

            if (resolveRan)
            {
                // Consume this pass's sighting (Register ran before Tick within the frame): a
                // sighted mesh gets its fresh nearest distance — allowed to GROW as the camera
                // walks away; an un-sighted one keeps its last-known distance and is handled by
                // the resolve-pass expiry below.
                if (entry.PendingDistanceSq < float.MaxValue)
                {
                    entry.NearestDistanceSq = entry.PendingDistanceSq;
                }

                entry.PendingDistanceSq = float.MaxValue;
            }

            if (_resolvePassCounter - entry.LastSeenResolvePass > ResolvePassGrace)
            {
                (expired ??= []).Add(entry.Mesh);
                continue;
            }

            _tickScratch.Add(entry);
        }

        if (expired is not null)
        {
            foreach (var mesh in expired)
            {
                _entries.Remove(mesh);
            }
        }

        if (!enabled || _tickScratch.Count == 0)
        {
            return;
        }

        // Nearest-first priority under the budget gates.
        _tickScratch.Sort(static (a, b) => a.NearestDistanceSq.CompareTo(b.NearestDistanceSq));

        var meshBudget = MaxAnimatedMeshes;
        var vertexBudget = MaxAnimatedVerticesPerFrame;
        var radiusSq = AnimateRadius * AnimateRadius;
        foreach (var entry in _tickScratch)
        {
            if (meshBudget <= 0 || vertexBudget <= 0 || entry.NearestDistanceSq > radiusSq)
            {
                break; // sorted nearest-first: everything after is farther / over budget
            }

            if (SkinMesh(entry, frameIndex, ring, clockSeconds, ref vertexBudget))
            {
                meshBudget--;
                AnyAnimatedThisFrame = true;
            }
        }
    }

    private bool SkinMesh(Entry entry, int frameIndex, GpuRingBuffer12 ring, double clockSeconds, ref int vertexBudget)
    {
        var animation = entry.Mesh.Animation!;
        NifAnimationPoseEvaluator.EvaluateBoneWorlds(animation, (float)clockSeconds, entry.BoneWorlds);

        var any = false;
        foreach (var sub in entry.Mesh.Submeshes)
        {
            if (sub.Skin is not { } skin || sub.RestPoseVertices is not { } template)
            {
                continue;
            }

            var vertexCount = Math.Min(skin.VertexCount, template.Length);
            if (vertexCount == 0 || vertexCount > vertexBudget)
            {
                continue; // over budget → this submesh keeps the static rest-pose VB
            }

            var byteCount = (uint)(vertexCount * GpuMeshUploader.GpuVertexSize);
            if (!ring.TryAllocate(frameIndex, byteCount, out var alloc, alignment: 16))
            {
                continue; // ring exhausted → static fallback, retry next frame
            }

            NifAnimationPoseEvaluator.ComputeSkinMatrices(
                skin.InverseBindPoses, skin.SkinBoneToAnimBone, entry.BoneWorlds,
                entry.SkinMatrices.AsSpan(0, skin.InverseBindPoses.Length));

            unsafe
            {
                var dst = new Span<GpuMeshUploader.GpuVertex>((void*)alloc.CpuPtr, vertexCount);
                template.AsSpan(0, vertexCount).CopyTo(dst);
                SkinInto(dst, skin, entry.SkinMatrices);
            }

            sub.AnimatedVertexBufferView = new VertexBufferView
            {
                BufferLocation = alloc.GpuAddress,
                SizeInBytes = byteCount,
                StrideInBytes = (uint)GpuMeshUploader.GpuVertexSize,
            };
            vertexBudget -= vertexCount;
            any = true;
        }

        return any;
    }

    /// <summary>4-influence LBS into the template copy: positions transformed, normals
    /// direction-transformed + renormalized; uv/color/tangents keep their rest values (TES3 cloth
    /// carries no bump maps — tangent re-skinning isn't worth the cost until an asset needs it).</summary>
    private static void SkinInto(
        Span<GpuMeshUploader.GpuVertex> vertices,
        Skinning.NifSubmeshSkin skin,
        Matrix4x4[] skinMatrices)
    {
        for (var v = 0; v < vertices.Length; v++)
        {
            var basePos = new Vector3(
                skin.BasePositions[v * 3],
                skin.BasePositions[(v * 3) + 1],
                skin.BasePositions[(v * 3) + 2]);

            var position = Vector3.Zero;
            var normal = Vector3.Zero;
            var totalWeight = 0f;
            for (var k = 0; k < 4; k++)
            {
                var weight = skin.BoneWeights[(v * 4) + k];
                if (weight <= 0f)
                {
                    continue;
                }

                var bone = skin.BoneIndices[(v * 4) + k];
                if (bone >= skinMatrices.Length)
                {
                    continue;
                }

                position += Vector3.Transform(basePos, skinMatrices[bone]) * weight;
                totalWeight += weight;

                if (skin.BaseNormals is { } normals)
                {
                    var baseNormal = new Vector3(
                        normals[v * 3], normals[(v * 3) + 1], normals[(v * 3) + 2]);
                    normal += Vector3.TransformNormal(baseNormal, skinMatrices[bone]) * weight;
                }
            }

            if (totalWeight <= 0f)
            {
                continue; // unweighted vertex keeps its rest-pose template values
            }

            vertices[v].Position = position;
            if (normal != Vector3.Zero)
            {
                vertices[v].Normal = Vector3.Normalize(normal);
            }
        }
    }

    private static int ReadEnvInt(string name, int fallback)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return int.TryParse(raw, out var value) && value > 0 ? value : fallback;
    }
}
#endif
