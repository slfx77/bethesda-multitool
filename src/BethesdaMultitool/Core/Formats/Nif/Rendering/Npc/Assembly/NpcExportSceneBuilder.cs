using System.Numerics;
using BethesdaMultitool.CLI.Rendering.Npc;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Esm.Plugin.AssetPacking;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Export;
using BethesdaMultitool.Core.Formats.Nif.Rendering.FaceGen;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Geometry;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Composition;
using BethesdaMultitool.Core.Formats.Nif.Rendering.NpcAssembly;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Assembly;

/// <summary>
///     Orchestrates NPC export scene construction. Body assembly lives in
///     <see cref="NpcExportBodyAssembler" /> and head assembly in
///     <see cref="NpcExportHeadAssembler" />.
/// </summary>
internal static class NpcExportSceneBuilder
{
    private static readonly Logger Log = Logger.Instance;

    internal static GlbScene? Build(
        NpcAppearance npc,
        MeshArchiveSet meshArchives,
        NifTextureResolver textureResolver,
        Dictionary<string, EgmParser?> egmCache,
        Dictionary<string, EgtParser?> egtCache,
        NpcExportSettings settings)
    {
        var compositionCaches = new NpcCompositionCaches(
            egmCache,
            egtCache,
            new Dictionary<string, NpcCompositionCaches.CachedNpcSkeletonPlan?>(
                StringComparer.OrdinalIgnoreCase));
        var plan = NpcCompositionPlanner.CreatePlan(
            npc,
            meshArchives,
            textureResolver,
            compositionCaches,
            NpcCompositionOptions.From(settings));
        return NpcCompositionExportAdapter.BuildNpc(plan, meshArchives, textureResolver, compositionCaches);
    }

    internal static Dictionary<string, int> AddNodes(
        GlbScene scene,
        IEnumerable<NifExportExtractor.ExtractedNode> nodes,
        GlbNodeKind kind)
    {
        // Share the raw-NIF parent-first builder. NPC skeletons can also store a child before its
        // parent block, and their node identity must survive into the native viewer for animation.
        // Its name index also rejects duplicate names instead of whichever duplicate appeared first.
        return NifExportSceneBuilder.AddNodes(scene, nodes, kind).ByName;
    }

    internal static void AddSkinnedNif(
        GlbScene scene,
        string nifPath,
        MeshArchiveSet meshArchives,
        Dictionary<string, int> nodeIndicesByBoneName,
        Action<RenderableSubmesh>? mutateSubmesh = null,
        string? filterShapeName = null,
        float[]? preSkinMorphDeltas = null,
        Func<string?, bool>? excludeShape = null)
    {
        var extracted = LoadExtractedNif(
            nifPath,
            meshArchives,
            filterShapeName,
            preSkinMorphDeltas);
        if (extracted == null)
        {
            return;
        }

        foreach (var part in extracted.MeshParts)
        {
            if (excludeShape?.Invoke(part.Name) == true)
            {
                continue;
            }

            mutateSubmesh?.Invoke(part.Submesh);
            if (part.Skin != null)
            {
                AddSkinnedPart(scene, part, nodeIndicesByBoneName, nifPath);
            }
            else
            {
                AddExtractedRigidPart(scene, part, part.ShapeWorldTransform, nifPath);
            }
        }
    }

    internal static void AddSkinnedPart(
        GlbScene scene,
        NifExportExtractor.ExtractedMeshPart part,
        Dictionary<string, int> nodeIndicesByBoneName,
        string sourceNifPath)
    {
        var jointNodeIndices = new int[part.Skin!.BoneNames.Length];
        for (var index = 0; index < part.Skin.BoneNames.Length; index++)
        {
            if (!nodeIndicesByBoneName.TryGetValue(part.Skin.BoneNames[index], out var jointNodeIndex))
            {
                Log.Warn("Skipping skinned mesh '{0}': missing joint '{1}'", part.Name, part.Skin.BoneNames[index]);
                return;
            }

            jointNodeIndices[index] = jointNodeIndex;
        }

        var submesh = CloneSubmesh(part.Submesh);
        submesh.SourceNifPath = sourceNifPath;
        submesh.BindPosePositions = part.Submesh.BindPosePositions is { } extractedBindPose
            ? (float[])extractedBindPose.Clone()
            : NifGeometryTransformUtils.TransformPositions(
                (float[])part.Submesh.Positions.Clone(),
                part.ShapeWorldTransform);

        scene.MeshParts.Add(new GlbMeshPart
        {
            Name = part.Name,
            Submesh = submesh,
            Skin = new GlbSkinBinding
            {
                JointNodeIndices = jointNodeIndices,
                InverseBindMatrices = part.Skin.InverseBindMatrices,
                PerVertexInfluences = part.Skin.PerVertexInfluences
            }
        });
    }

    internal static void AddExtractedRigidPart(
        GlbScene scene,
        NifExportExtractor.ExtractedMeshPart part,
        Matrix4x4 worldTransform,
        string label)
    {
        var rigidSubmesh = CloneSubmesh(part.Submesh);
        NpcRenderHelpers.TransformSubmesh(rigidSubmesh, worldTransform);
        AddRigidSubmesh(scene, label, rigidSubmesh);
    }

    internal static void AddRigidModel(GlbScene scene, string label, NifRenderableModel model)
    {
        foreach (var submesh in model.Submeshes)
        {
            AddRigidSubmesh(scene, label, CloneSubmesh(submesh));
        }
    }

    internal static void AddRigidSubmesh(GlbScene scene, string label, RenderableSubmesh submesh)
    {
        var nodeIndex = scene.AddNode(
            $"{Path.GetFileNameWithoutExtension(label)}_{scene.MeshParts.Count}",
            GlbScene.RootNodeIndex,
            Matrix4x4.Identity,
            Matrix4x4.Identity,
            GlbNodeKind.Attachment);
        scene.MeshParts.Add(new GlbMeshPart
        {
            Name = Path.GetFileNameWithoutExtension(label),
            NodeIndex = nodeIndex,
            Submesh = submesh
        });
    }

    internal static NifExportExtractor.ExtractedScene? LoadExtractedNif(
        string nifPath,
        MeshArchiveSet meshArchives,
        string? filterShapeName = null,
        float[]? preSkinMorphDeltas = null)
    {
        var raw = NpcMeshHelpers.LoadNifRawFromBsa(nifPath, meshArchives);
        return raw == null
            ? null
            : NifExportExtractor.Extract(raw.Value.Data, raw.Value.Info, filterShapeName: filterShapeName,
                preSkinMorphDeltas: preSkinMorphDeltas);
    }

    internal static RenderableSubmesh CloneSubmesh(RenderableSubmesh submesh)
    {
        return RenderableSubmeshCloner.DeepClone(submesh);
    }

    /// <summary>
    ///     Working state while building an NPC's GLB skeleton: the scene, bone transforms, optional pose deltas, and
    ///     bone-name-to-node-index map.
    /// </summary>
    internal sealed record SkeletonContext(
        GlbScene Scene,
        Dictionary<string, Matrix4x4> BoneTransforms,
        Dictionary<string, Matrix4x4>? PoseDeltas,
        Dictionary<string, int> NodeIndicesByBoneName);
}
