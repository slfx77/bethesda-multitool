using System.Numerics;
using BethesdaMultitool.Core.Formats.Esm.Plugin.AssetPacking;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Composition;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Export;

/// <summary>Builds a <see cref="GlbScene" /> from a single NIF model for export to GLB.</summary>
internal static class NifExportSceneBuilder
{
    internal static GlbScene? Build(byte[] data, NifInfo nif, string sourceLabel)
    {
        var extracted = NifExportExtractor.Extract(data, nif);
        if (extracted.MeshParts.Count == 0)
        {
            return null;
        }

        var scene = new GlbScene();
        var (nodeIndicesByName, nodeIndicesByBlock) = AddNodes(scene, extracted.Nodes);

        foreach (var part in extracted.MeshParts)
        {
            if (part.Skin != null && TryCreateSkinBinding(
                    part.Skin,
                    nodeIndicesByName,
                    nodeIndicesByBlock,
                    out var skinBinding))
            {
                scene.MeshParts.Add(new GlbMeshPart
                {
                    Name = part.Name,
                    Submesh = RenderableSubmeshCloner.DeepClone(part.Submesh),
                    Skin = skinBinding
                });
                continue;
            }

            var rigidSubmesh = RenderableSubmeshCloner.DeepClone(part.Submesh);
            var parentNodeIndex = part.ParentNodeBlockIndex is int parentBlockIndex &&
                                  nodeIndicesByBlock.TryGetValue(parentBlockIndex, out var resolvedParent)
                ? resolvedParent
                : GlbScene.RootNodeIndex;
            var nodeIndex = scene.AddNode(
                $"{part.Name}_{part.Submesh.SourceBlockIndex}",
                parentNodeIndex,
                part.ShapeLocalTransform,
                part.ShapeWorldTransform,
                GlbNodeKind.Attachment,
                part.Name,
                part.Submesh.SourceBlockIndex);
            scene.MeshParts.Add(new GlbMeshPart
            {
                Name = part.Name,
                NodeIndex = nodeIndex,
                Submesh = rigidSubmesh
            });
        }

        return scene;
    }

    /// <summary>
    ///     Builds a rigid GLB scene from the renderer's fully extracted model. This is the standalone
    ///     preview/export bridge for modern self-contained shapes: Fallout 4/76 <c>BSTriShape</c>
    ///     variants and Starfield <c>BSGeometry</c> do not have a separate <c>NiTriShapeData</c>
    ///     block, so the hierarchy-preserving legacy exporter above cannot extract their geometry.
    ///     <para>
    ///         <see cref="NifGeometryExtractor" /> has already applied the source graph transforms and
    ///         resolved modern material metadata. Attach each resulting submesh to the identity root;
    ///         this intentionally exports a static preview rather than claiming to reconstruct a
    ///         skinned/animated modern scene graph.
    ///     </para>
    /// </summary>
    internal static GlbScene? BuildRenderableModel(NifRenderableModel model, string sourceLabel)
    {
        if (!model.HasGeometry)
        {
            return null;
        }

        var scene = new GlbScene();
        var sourceName = Path.GetFileNameWithoutExtension(sourceLabel);
        for (var index = 0; index < model.Submeshes.Count; index++)
        {
            var submesh = model.Submeshes[index];
            if (submesh.VertexCount == 0 || submesh.TriangleCount == 0)
            {
                continue;
            }

            var name = string.IsNullOrWhiteSpace(submesh.ShapeName)
                ? $"{sourceName}_{index}"
                : submesh.ShapeName!;
            scene.MeshParts.Add(new GlbMeshPart
            {
                Name = name,
                NodeIndex = GlbScene.RootNodeIndex,
                Submesh = submesh
            });
        }

        return scene.MeshParts.Count == 0 ? null : scene;
    }

    /// <summary>
    ///     Applies the modern renderer extractor's resolved material state to hierarchy/skin-preserving
    ///     mesh parts. Geometry and binding stay untouched; matching is anchored to the originating NIF
    ///     shape block so duplicate shape names cannot cross-wire materials.
    /// </summary>
    internal static void ApplyModernMaterialState(GlbScene scene, NifRenderableModel modernModel)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(modernModel);

        var modernByBlock = modernModel.Submeshes
            .Where(static submesh => submesh.SourceBlockIndex >= 0)
            .GroupBy(static submesh => submesh.SourceBlockIndex)
            .ToDictionary(static group => group.Key, static group => group.ToArray());

        for (var index = 0; index < scene.MeshParts.Count; index++)
        {
            var part = scene.MeshParts[index];
            var geometry = part.Submesh;
            if (!modernByBlock.TryGetValue(geometry.SourceBlockIndex, out var candidates))
            {
                continue;
            }

            var materialSource = candidates.FirstOrDefault(candidate =>
                                     string.Equals(
                                         candidate.ShapeName,
                                         geometry.ShapeName,
                                         StringComparison.OrdinalIgnoreCase) &&
                                     candidate.VertexCount == geometry.VertexCount &&
                                     candidate.TriangleCount == geometry.TriangleCount)
                                 ?? candidates.FirstOrDefault(candidate =>
                                     candidate.VertexCount == geometry.VertexCount &&
                                     candidate.TriangleCount == geometry.TriangleCount);
            if (materialSource is null)
            {
                continue;
            }

            scene.MeshParts[index] = new GlbMeshPart
            {
                Name = part.Name,
                NodeIndex = part.NodeIndex,
                Skin = part.Skin,
                Submesh = RenderableSubmeshCloner.CloneGeometryWithRenderState(geometry, materialSource)
            };
        }
    }

    /// <summary>
    ///     Builds a creature scene from skeleton (MODL) + body meshes (NIFZ).
    ///     Loads skeleton first, applies idle animation if available, then loads
    ///     all NIFZ body meshes bound to the skeleton bones.
    /// </summary>
    internal static GlbScene? BuildCreature(
        string skeletonPath,
        string[] bodyModelPaths,
        MeshArchiveSet meshArchives,
        bool bindPose = false,
        string? idleAnimationPath = null,
        string? weaponMeshPath = null)
    {
        var plan = CreatureCompositionPlanner.CreatePlan(
            skeletonPath,
            bodyModelPaths,
            meshArchives,
            new CreatureCompositionOptions
            {
                IncludeWeapon = weaponMeshPath != null,
                BindPose = bindPose
            },
            idleAnimationPath,
            weaponMeshPath);
        return plan == null ? null : NpcCompositionExportAdapter.BuildCreature(plan, meshArchives);
    }

    internal static (Dictionary<string, int> ByName, Dictionary<int, int> ByBlock) AddNodes(
        GlbScene scene,
        IEnumerable<NifExportExtractor.ExtractedNode> nodes,
        GlbNodeKind kind = GlbNodeKind.Skeleton)
    {
        var nodeList = nodes.ToList();
        var pendingNodes = new List<NifExportExtractor.ExtractedNode>(nodeList);
        var knownBlocks = nodeList.Select(static node => node.BlockIndex).ToHashSet();
        var orderedNodes = new List<NifExportExtractor.ExtractedNode>(nodeList.Count);
        var emittedBlocks = new HashSet<int>();

        // NIF block order is not a hierarchy order: a child may legally precede its parent block.
        // The viewer pose evaluator deliberately uses a compact parent-first graph, so append every
        // resolvable parent before its descendants. Missing parents are rooted. A malformed cycle
        // cannot be represented; break it deterministically at its first source node and preserve
        // as much of the remaining hierarchy as possible.
        while (pendingNodes.Count > 0)
        {
            var madeProgress = false;
            for (var index = 0; index < pendingNodes.Count;)
            {
                var candidate = pendingNodes[index];
                if (candidate.ParentBlockIndex is int parentBlockIndex &&
                    knownBlocks.Contains(parentBlockIndex) &&
                    !emittedBlocks.Contains(parentBlockIndex))
                {
                    index++;
                    continue;
                }

                orderedNodes.Add(candidate);
                emittedBlocks.Add(candidate.BlockIndex);
                pendingNodes.RemoveAt(index);
                madeProgress = true;
            }

            if (!madeProgress)
            {
                var cycleRoot = pendingNodes[0];
                orderedNodes.Add(cycleRoot);
                emittedBlocks.Add(cycleRoot.BlockIndex);
                pendingNodes.RemoveAt(0);
            }
        }

        var blockToSceneNode = new Dictionary<int, int>();
        var nodeIndicesByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var ambiguousNodeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var node in orderedNodes)
        {
            var parentSceneNode = node.ParentBlockIndex is int parentBlockIndex &&
                                  blockToSceneNode.TryGetValue(parentBlockIndex, out var existingParent)
                ? existingParent
                : GlbScene.RootNodeIndex;

            var sceneNodeIndex = scene.AddNode(
                $"{node.Name}_{node.BlockIndex}",
                parentSceneNode,
                node.LocalTransform,
                node.WorldTransform,
                kind,
                node.LookupName,
                node.BlockIndex);
            blockToSceneNode[node.BlockIndex] = sceneNodeIndex;

            if (!string.IsNullOrWhiteSpace(node.LookupName) &&
                !nodeIndicesByName.TryAdd(node.LookupName, sceneNodeIndex))
            {
                ambiguousNodeNames.Add(node.LookupName);
            }
        }

        foreach (var ambiguousName in ambiguousNodeNames)
        {
            nodeIndicesByName.Remove(ambiguousName);
        }

        return (nodeIndicesByName, blockToSceneNode);
    }

    internal static bool TryCreateSkinBinding(
        NifExportExtractor.ExtractedSkinBinding skin,
        Dictionary<string, int> nodeIndicesByName,
        Dictionary<int, int> nodeIndicesByBlock,
        out GlbSkinBinding? binding)
    {
        var jointNodeIndices = new int[skin.BoneNames.Length];
        for (var index = 0; index < skin.BoneNames.Length; index++)
        {
            var sourceBlockIndex = index < skin.BoneBlockIndices.Length
                ? skin.BoneBlockIndices[index]
                : -1;
            int jointNodeIndex;
            var resolved = sourceBlockIndex >= 0
                ? nodeIndicesByBlock.TryGetValue(sourceBlockIndex, out jointNodeIndex)
                : nodeIndicesByName.TryGetValue(skin.BoneNames[index], out jointNodeIndex);
            if (!resolved)
            {
                binding = null;
                return false;
            }

            jointNodeIndices[index] = jointNodeIndex;
        }

        binding = new GlbSkinBinding
        {
            JointNodeIndices = jointNodeIndices,
            InverseBindMatrices = skin.InverseBindMatrices,
            PerVertexInfluences = skin.PerVertexInfluences
        };
        return true;
    }

    private static void ApplyWorldTransform(RenderableSubmesh submesh, Matrix4x4 transform)
    {
        for (var index = 0; index < submesh.Positions.Length; index += 3)
        {
            var transformed = Vector3.Transform(
                new Vector3(
                    submesh.Positions[index],
                    submesh.Positions[index + 1],
                    submesh.Positions[index + 2]),
                transform);
            submesh.Positions[index] = transformed.X;
            submesh.Positions[index + 1] = transformed.Y;
            submesh.Positions[index + 2] = transformed.Z;
        }

        if (submesh.Normals == null)
        {
            return;
        }

        var normalMatrix = Matrix4x4.Transpose(Matrix4x4.Invert(transform, out var inverse)
            ? inverse
            : Matrix4x4.Identity);

        for (var index = 0; index < submesh.Normals.Length; index += 3)
        {
            var transformed = Vector3.TransformNormal(
                new Vector3(
                    submesh.Normals[index],
                    submesh.Normals[index + 1],
                    submesh.Normals[index + 2]),
                normalMatrix);
            if (transformed.LengthSquared() > 0.0001f)
            {
                transformed = Vector3.Normalize(transformed);
            }

            submesh.Normals[index] = transformed.X;
            submesh.Normals[index + 1] = transformed.Y;
            submesh.Normals[index + 2] = transformed.Z;
        }
    }
}
