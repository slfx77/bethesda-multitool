using System.Numerics;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Dds;
using BethesdaMultitool.Core.Formats.Nif;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Skinning;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Inspection;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Materials;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Viewer;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12.Viewer;

/// <summary>Snapshot of one native scene node; indices remain identical to the source scene.</summary>
internal sealed record DecodedBethesdaViewerNode12(
    string Name,
    string? LookupName,
    int? SourceBlockIndex,
    int? ParentIndex,
    Matrix4x4 LocalTransform,
    Matrix4x4 WorldTransform,
    BethesdaViewerNodeRole Role);

/// <summary>
///     Joint-indexed skin retained beside the decoded GPU payload. It deliberately does not coerce
///     the viewer node graph into <see cref="NifSubmeshSkin" />, whose indices address a
///     different placed-mesh animation rig.
/// </summary>
internal sealed record DecodedBethesdaViewerSkinBinding12(
    int[] JointNodeIndices,
    Matrix4x4[] InverseBindMatrices,
    (int BoneIdx, float Weight)[][] PerVertexInfluences);

/// <summary>
///     Bethesda semantics that the existing placed-reference <see cref="DecodedSubmesh12" /> does
///     not need. Keeping these explicit and viewer-only avoids changing the persistent cache schema
///     while retaining NPC appearance, sky, inspection, and animation-ready seam state.
/// </summary>
internal sealed record DecodedBethesdaViewerSubmeshSemantics12(
    string? ShapeName,
    NifShaderTextureMetadata? ShaderMetadata,
    bool UseVertexColors,
    bool UseVertexAlphaForOpacity,
    NifAlphaRenderMode NativeAlphaRenderMode,
    (float R, float G, float B)? MaterialDiffuse,
    bool IsEyeEnvmap,
    float EyeEnvironmentMapScale,
    int RenderOrder,
    (float R, float G, float B)? TintColor,
    bool IsFaceGen,
    (float R, float G, float B) SubsurfaceColor,
    (float R, float G, float B)? AnimatedEmissiveColor,
    (float R, float G, float B)? EmissiveColor,
    float[]? BindPosePositions,
    string? SourceNifPath,
    SkyObjectType? SkyType,
    byte[]? AuthoredSkyVertexColors,
    bool IsFarLodFallback);

/// <summary>One stable scene mesh-part index and its GPU-ready/reference-native payloads.</summary>
internal sealed record DecodedBethesdaViewerMeshPart12(
    string Name,
    int? NodeIndex,
    DecodedSubmesh12 Submesh,
    DecodedBethesdaViewerSubmeshSemantics12 NativeSemantics,
    DecodedBethesdaViewerSkinBinding12? Skin);

/// <summary>Animation-time cross-mesh seam addresses, deep-copied from the scene.</summary>
internal sealed record DecodedBethesdaViewerBoundaryStitchGroup12(
    BethesdaViewerMeshVertexIndex[] Vertices);

/// <summary>
///     Pure CPU result consumed by the future native D3D12 viewport materializer. <see cref="Mesh" />
///     is the existing decoded-mesh payload in stable mesh-part order; the parallel scene collections
///     retain node, skin, animation, resource, and Bethesda-only material semantics that a placed
///     world reference does not carry.
/// </summary>
internal sealed record DecodedBethesdaViewerScene12(
    int ContractVersion,
    string SourceLabel,
    BethesdaViewerScenePurpose Purpose,
    BethesdaGame Game,
    BethesdaViewerBounds? Bounds,
    IReadOnlyList<string> TextureSourcePaths,
    IReadOnlyDictionary<string, DecodedTexture> GeneratedTextures,
    IReadOnlyList<DecodedBethesdaViewerNode12> Nodes,
    IReadOnlyList<DecodedBethesdaViewerMeshPart12> MeshParts,
    IReadOnlyList<BethesdaViewerAnimationClip> AnimationClips,
    IReadOnlyList<DecodedBethesdaViewerBoundaryStitchGroup12> BoundaryStitchGroups,
    DecodedNifMesh12 Mesh);

/// <summary>
///     Converts the renderer-neutral native scene into the established D3D12 CPU payload without
///     creating devices, command lists, archive handles, or GPU resources.
/// </summary>
internal static class BethesdaViewerSceneDecoder12
{
    private static readonly Logger Log = Logger.Instance;

    internal static DecodedBethesdaViewerScene12 Decode(BethesdaViewerScene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (scene.ContractVersion != BethesdaViewerScene.CurrentContractVersion)
        {
            throw new NotSupportedException(
                $"Bethesda viewer scene contract v{scene.ContractVersion} is not supported.");
        }

        var nodes = scene.Nodes
            .Select(static node => new DecodedBethesdaViewerNode12(
                node.Name,
                node.LookupName,
                node.SourceBlockIndex,
                node.ParentIndex,
                node.LocalTransform,
                node.WorldTransform,
                node.Role))
            .ToArray();

        // Never filter empty parts here: animation morph tracks and boundary-stitch groups address
        // scene mesh-part indices, so compacting the list would silently retarget those indices.
        var parts = new DecodedBethesdaViewerMeshPart12[scene.MeshParts.Count];
        var decodedSubmeshes = new DecodedSubmesh12[scene.MeshParts.Count];
        var containsParticleSource = false;
        for (var index = 0; index < scene.MeshParts.Count; index++)
        {
            var part = scene.MeshParts[index];
            var native = part.Submesh;
            var decoded = ReferenceSubmeshDecoder12.Decode(
                native,
                new ReferenceSubmeshDecodeOptions12(
                    native.DiffuseTexturePath,
                    native.NormalMapTexturePath,
                    IncludeParticleRuntime: true));
            decodedSubmeshes[index] = decoded;
            containsParticleSource |= native.ParticleRuntime is not null;
            parts[index] = new DecodedBethesdaViewerMeshPart12(
                part.Name,
                part.NodeIndex,
                decoded,
                SnapshotNativeSemantics(scene.Purpose, native),
                SnapshotSkin(part.Skin));
        }

        var generatedTextures = new Dictionary<string, DecodedTexture>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, texture) in scene.GeneratedTextures)
        {
            // BethesdaViewerScene already owns a deep immutable snapshot. The decoded scene copies
            // dictionary ownership while intentionally sharing those read-only texture descriptors.
            generatedTextures.Add(path, texture);
        }

        var stitchGroups = scene.BoundaryStitchGroups
            .Select(static group => new DecodedBethesdaViewerBoundaryStitchGroup12(
                (BethesdaViewerMeshVertexIndex[])group.Vertices.Clone()))
            .ToArray();
        var mesh = new DecodedNifMesh12(
            decodedSubmeshes,
            ContainsParticleSource: containsParticleSource);
        var animationClips = SnapshotValidAnimationClips(
            scene.AnimationClips,
            nodes.Length,
            parts.Length,
            scene.SourceLabel);

        return new DecodedBethesdaViewerScene12(
            scene.ContractVersion,
            scene.SourceLabel,
            scene.Purpose,
            scene.Game,
            scene.Bounds,
            scene.TextureSourcePaths.ToArray(),
            generatedTextures,
            nodes,
            parts,
            animationClips,
            stitchGroups,
            mesh);
    }

    private static BethesdaViewerAnimationClip[] SnapshotValidAnimationClips(
        IEnumerable<BethesdaViewerAnimationClip> sourceClips,
        int nodeCount,
        int meshPartCount,
        string sourceLabel)
    {
        var snapshots = new List<BethesdaViewerAnimationClip>();
        foreach (var source in sourceClips)
        {
            try
            {
                if (!BethesdaViewerAnimationValidator.TryValidate(
                        source,
                        nodeCount,
                        meshPartCount,
                        out var validationError))
                {
                    Log.Warn(
                        "BethesdaViewerSceneDecoder12: animation clip in '{0}' was ignored: {1}.",
                        sourceLabel,
                        validationError);
                    continue;
                }

                snapshots.Add(SnapshotAnimationClip(source));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and
                                       not StackOverflowException and
                                       not OperationCanceledException)
            {
                Log.Warn(
                    "BethesdaViewerSceneDecoder12: malformed optional animation in '{0}' was ignored: {1}.",
                    sourceLabel,
                    ex.Message);
            }
        }

        return snapshots.ToArray();
    }

    private static BethesdaViewerAnimationClip SnapshotAnimationClip(
        BethesdaViewerAnimationClip source)
    {
        var nodeTracks = source.NodeTracks
            .Select(static track => new BethesdaViewerNodeAnimationTrack(
                track.NodeIndex,
                track.Frequency,
                track.Phase,
                track.RotationInterpolation,
                (BethesdaViewerQuaternionKey[])track.RotationKeys.Clone(),
                track.TranslationInterpolation,
                (BethesdaViewerVector3Key[])track.TranslationKeys.Clone(),
                track.ScaleInterpolation,
                (BethesdaViewerFloatKey[])track.ScaleKeys.Clone(),
                track.EulerXKeys is null
                    ? null
                    : (BethesdaViewerFloatKey[])track.EulerXKeys.Clone(),
                track.EulerYKeys is null
                    ? null
                    : (BethesdaViewerFloatKey[])track.EulerYKeys.Clone(),
                track.EulerZKeys is null
                    ? null
                    : (BethesdaViewerFloatKey[])track.EulerZKeys.Clone()))
            .ToArray();
        var morphTracks = source.MorphWeightTracks
            .Select(static track => new BethesdaViewerMorphWeightTrack(
                track.MeshPartIndex,
                (string[])track.TargetNames.Clone(),
                track.Frequency,
                track.Phase,
                track.Interpolation,
                track.Keys
                    .Select(static key => new BethesdaViewerMorphWeightKey(
                        key.Time,
                        (float[])key.Weights.Clone()))
                    .ToArray()))
            .ToArray();

        return new BethesdaViewerAnimationClip(
            source.Name,
            source.StartTime,
            source.EndTime,
            source.Loops,
            nodeTracks,
            morphTracks,
            (BethesdaViewerTextKey[])source.TextKeys.Clone());
    }

    private static DecodedBethesdaViewerSubmeshSemantics12 SnapshotNativeSemantics(
        BethesdaViewerScenePurpose purpose,
        RenderableSubmesh source)
    {
        // Shader metadata and controller/runtime objects are extracted descriptor graphs shared by
        // the established decoded payload. Mutable geometry/bind arrays remain separately owned.
        return new DecodedBethesdaViewerSubmeshSemantics12(
            source.ShapeName,
            source.ShaderMetadata,
            source.UseVertexColors,
            source.UseVertexAlphaForOpacity,
            NifAlphaClassifier.Classify(source, diffuseTexture: null).RenderMode,
            source.MaterialDiffuse,
            source.IsEyeEnvmap,
            source.EnvMapScale,
            source.RenderOrder,
            source.TintColor,
            source.IsFaceGen,
            source.SubsurfaceColor,
            source.AnimatedEmissiveColor,
            source.EmissiveColor,
            source.BindPosePositions is null ? null : (float[])source.BindPosePositions.Clone(),
            source.SourceNifPath,
            source.SkyType,
            BethesdaViewerNativeSkyPolicy.IsDedicatedRawNifLayer(purpose, source.SkyType) &&
            source.VertexColors is { } skyVertexColors
                ? (byte[])skyVertexColors.Clone()
                : null,
            source.IsFarLodFallback);
    }

    private static DecodedBethesdaViewerSkinBinding12? SnapshotSkin(BethesdaViewerSkinBinding? source)
    {
        if (source is null)
        {
            return null;
        }

        var influences = new (int BoneIdx, float Weight)[source.PerVertexInfluences.Length][];
        for (var index = 0; index < influences.Length; index++)
        {
            influences[index] = ((int BoneIdx, float Weight)[])source.PerVertexInfluences[index].Clone();
        }

        return new DecodedBethesdaViewerSkinBinding12(
            (int[])source.JointNodeIndices.Clone(),
            (Matrix4x4[])source.InverseBindMatrices.Clone(),
            influences);
    }
}
