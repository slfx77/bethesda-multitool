using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Export;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Viewer;

/// <summary>
///     Replaces the flattened bone stubs carried by a classic skinned model with the hierarchy and
///     rest transforms from its canonical external <c>skeleton.nif</c>. The operation is
///     transactional: a missing/ambiguous joint, malformed hierarchy, or invalid skin returns the
///     original scene without changing any node, mesh, material, or binding array.
/// </summary>
internal static class BethesdaViewerExternalSkeletonRigAdapter
{
    internal static bool TryApply(
        GlbScene source,
        byte[] skeletonData,
        out GlbScene result,
        out string diagnostic)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(skeletonData);

        result = source;
        diagnostic = string.Empty;
        try
        {
            var nif = NifParser.Parse(skeletonData);
            if (nif is null || nif.Blocks.Count == 0)
            {
                diagnostic = "The canonical skeleton is not a readable NIF.";
                return false;
            }

            var extracted = NifExportExtractor.Extract(skeletonData, nif);
            return TryApply(source, extracted.Nodes, out result, out diagnostic);
        }
        catch (Exception exception) when (exception is InvalidDataException or
                                               ArgumentException or
                                               OverflowException or
                                               IndexOutOfRangeException or
                                               EndOfStreamException)
        {
            diagnostic = $"The canonical skeleton could not be decoded ({exception.GetType().Name}).";
            return false;
        }
    }

    /// <summary>
    ///     Testable hierarchy boundary after the skeleton NIF has been decoded. External source block
    ///     indices deliberately do not cross into the model scene: embedded-model animation uses
    ///     model block identity, while standalone KF animation binds the canonical rig by name.
    /// </summary>
    internal static bool TryApply(
        GlbScene source,
        IReadOnlyList<NifExportExtractor.ExtractedNode> skeletonNodes,
        out GlbScene result,
        out string diagnostic)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(skeletonNodes);

        result = source;
        diagnostic = string.Empty;

        if (!TryOrderSkeletonNodes(skeletonNodes, out var orderedSkeletonNodes, out diagnostic))
        {
            return false;
        }

        var skinnedPartCount = source.MeshParts.Count(static part => part.Skin is not null);
        if (skinnedPartCount == 0)
        {
            diagnostic = "The model scene has no retained skin binding to rebind.";
            return false;
        }

        var candidate = new GlbScene();
        var skeletonSceneIndexByBlock = new Dictionary<int, int>();
        var skeletonSceneIndicesByName = new Dictionary<string, List<int>>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var node in orderedSkeletonNodes)
        {
            if (!IsFiniteDecomposable(node.LocalTransform))
            {
                diagnostic = $"Canonical skeleton node {node.BlockIndex} has an invalid rest transform.";
                return false;
            }

            var parentSceneIndex = node.ParentBlockIndex is int parentBlockIndex
                ? skeletonSceneIndexByBlock[parentBlockIndex]
                : GlbScene.RootNodeIndex;
            var worldTransform = node.LocalTransform * candidate.Nodes[parentSceneIndex].WorldTransform;
            if (!IsFinite(worldTransform))
            {
                diagnostic = $"Canonical skeleton node {node.BlockIndex} produces a non-finite world transform.";
                return false;
            }

            var sceneIndex = candidate.AddNode(
                $"{node.Name}_{node.BlockIndex}",
                parentSceneIndex,
                node.LocalTransform,
                worldTransform,
                GlbNodeKind.Skeleton,
                node.LookupName,
                // A skeleton.nif block number is not an identity in the selected model NIF.
                sourceBlockIndex: null);
            skeletonSceneIndexByBlock.Add(node.BlockIndex, sceneIndex);
            if (!string.IsNullOrWhiteSpace(node.LookupName))
            {
                if (!skeletonSceneIndicesByName.TryGetValue(node.LookupName, out var matches))
                {
                    matches = [];
                    skeletonSceneIndicesByName.Add(node.LookupName, matches);
                }

                matches.Add(sceneIndex);
            }
        }

        if (!TryBuildSkinRebindings(
                source,
                skeletonSceneIndicesByName,
                out var reboundSkins,
                out var reboundJointCount,
                out diagnostic))
        {
            return false;
        }

        var retainedSourceNodes = CollectRetainedSourceNodes(source, skeletonSceneIndicesByName);
        var retainedNameCounts = retainedSourceNodes
            .Select(index => source.Nodes[index].LookupName)
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .GroupBy(static name => name!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var remappedSourceNodes = new Dictionary<int, int>
        {
            [GlbScene.RootNodeIndex] = GlbScene.RootNodeIndex
        };
        var sourceNodesBeingMapped = new HashSet<int>();
        string? sourceNodeMappingDiagnostic = null;

        bool TryMapSourceNode(int sourceNodeIndex, out int candidateNodeIndex)
        {
            if (remappedSourceNodes.TryGetValue(sourceNodeIndex, out candidateNodeIndex))
            {
                return true;
            }

            if ((uint)sourceNodeIndex >= (uint)source.Nodes.Count)
            {
                sourceNodeMappingDiagnostic = $"A mesh part addresses missing model node {sourceNodeIndex}.";
                candidateNodeIndex = -1;
                return false;
            }

            if (!sourceNodesBeingMapped.Add(sourceNodeIndex))
            {
                sourceNodeMappingDiagnostic =
                    $"The selected model contains a parent cycle at node {sourceNodeIndex}.";
                candidateNodeIndex = -1;
                return false;
            }

            var sourceNode = source.Nodes[sourceNodeIndex];
            if (!string.IsNullOrWhiteSpace(sourceNode.LookupName) &&
                skeletonSceneIndicesByName.TryGetValue(sourceNode.LookupName, out var skeletonMatches) &&
                skeletonMatches.Count == 1)
            {
                candidateNodeIndex = skeletonMatches[0];
                remappedSourceNodes.Add(sourceNodeIndex, candidateNodeIndex);
                sourceNodesBeingMapped.Remove(sourceNodeIndex);
                return true;
            }

            if (!IsFiniteDecomposable(sourceNode.LocalTransform))
            {
                sourceNodeMappingDiagnostic =
                    $"Model attachment node {sourceNodeIndex} has an invalid local transform.";
                candidateNodeIndex = -1;
                return false;
            }

            if (sourceNode.ParentIndex is int sourceParentIndex)
            {
                if (!TryMapSourceNode(sourceParentIndex, out var mappedParent))
                {
                    candidateNodeIndex = -1;
                    return false;
                }

                candidateNodeIndex = AppendRetainedSourceNode(
                    sourceNode,
                    mappedParent,
                    candidate,
                    retainedNameCounts);
            }
            else
            {
                candidateNodeIndex = AppendRetainedSourceNode(
                    sourceNode,
                    GlbScene.RootNodeIndex,
                    candidate,
                    retainedNameCounts);
            }

            remappedSourceNodes.Add(sourceNodeIndex, candidateNodeIndex);
            sourceNodesBeingMapped.Remove(sourceNodeIndex);
            return true;
        }

        for (var partIndex = 0; partIndex < source.MeshParts.Count; partIndex++)
        {
            var part = source.MeshParts[partIndex];
            int? nodeIndex = null;
            if (part.NodeIndex is int sourceNodeIndex)
            {
                if (!TryMapSourceNode(sourceNodeIndex, out var mappedNodeIndex))
                {
                    diagnostic = sourceNodeMappingDiagnostic ??
                                 $"Mesh part '{part.Name}' could not be remapped to the canonical rig.";
                    return false;
                }

                nodeIndex = mappedNodeIndex;
            }

            candidate.MeshParts.Add(new GlbMeshPart
            {
                Name = part.Name,
                NodeIndex = nodeIndex,
                // Retain the native RenderableSubmesh instance. The rig adapter owns no material,
                // texture, vertex, controller, tint, or shader-state conversion.
                Submesh = part.Submesh,
                Skin = reboundSkins[partIndex]
            });
        }

        result = candidate;
        diagnostic =
            $"Applied {orderedSkeletonNodes.Count:N0} canonical skeleton node(s) and rebound " +
            $"{skinnedPartCount:N0} skinned part(s) across {reboundJointCount:N0} joint slot(s).";
        return true;
    }

    private static bool TryBuildSkinRebindings(
        GlbScene source,
        IReadOnlyDictionary<string, List<int>> skeletonSceneIndicesByName,
        out GlbSkinBinding?[] reboundSkins,
        out int reboundJointCount,
        out string diagnostic)
    {
        reboundSkins = new GlbSkinBinding?[source.MeshParts.Count];
        reboundJointCount = 0;
        diagnostic = string.Empty;

        for (var partIndex = 0; partIndex < source.MeshParts.Count; partIndex++)
        {
            var part = source.MeshParts[partIndex];
            if (part.Skin is not { } skin)
            {
                continue;
            }

            if (skin.JointNodeIndices.Length == 0 ||
                skin.JointNodeIndices.Length != skin.InverseBindMatrices.Length ||
                part.Submesh.Positions.Length % 3 != 0 ||
                skin.PerVertexInfluences.Length != part.Submesh.Positions.Length / 3)
            {
                diagnostic = $"Skinned part '{part.Name}' has inconsistent binding dimensions.";
                return false;
            }

            if (skin.InverseBindMatrices.Any(static matrix => !IsFinite(matrix)))
            {
                diagnostic = $"Skinned part '{part.Name}' has a non-finite inverse-bind matrix.";
                return false;
            }

            var jointIndices = new int[skin.JointNodeIndices.Length];
            var sourceJointNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var jointIndex = 0; jointIndex < skin.JointNodeIndices.Length; jointIndex++)
            {
                var sourceNodeIndex = skin.JointNodeIndices[jointIndex];
                if ((uint)sourceNodeIndex >= (uint)source.Nodes.Count ||
                    string.IsNullOrWhiteSpace(source.Nodes[sourceNodeIndex].LookupName))
                {
                    diagnostic =
                        $"Skinned part '{part.Name}' joint {jointIndex} has no uniquely named model node.";
                    return false;
                }

                var jointName = source.Nodes[sourceNodeIndex].LookupName!;
                if (!sourceJointNames.Add(jointName))
                {
                    diagnostic = $"Skinned part '{part.Name}' repeats joint name '{jointName}'.";
                    return false;
                }

                if (!skeletonSceneIndicesByName.TryGetValue(jointName, out var matches))
                {
                    diagnostic = $"Canonical skeleton is missing joint '{jointName}'.";
                    return false;
                }

                if (matches.Count != 1)
                {
                    diagnostic = $"Canonical skeleton joint '{jointName}' is ambiguous ({matches.Count} matches).";
                    return false;
                }

                jointIndices[jointIndex] = matches[0];
            }

            for (var vertexIndex = 0; vertexIndex < skin.PerVertexInfluences.Length; vertexIndex++)
            {
                foreach (var influence in skin.PerVertexInfluences[vertexIndex])
                {
                    if ((uint)influence.BoneIdx >= (uint)jointIndices.Length ||
                        !float.IsFinite(influence.Weight) ||
                        influence.Weight < 0f)
                    {
                        diagnostic =
                            $"Skinned part '{part.Name}' vertex {vertexIndex} has an invalid influence.";
                        return false;
                    }
                }
            }

            reboundSkins[partIndex] = new GlbSkinBinding
            {
                JointNodeIndices = jointIndices,
                InverseBindMatrices = skin.InverseBindMatrices,
                PerVertexInfluences = skin.PerVertexInfluences
            };
            reboundJointCount += jointIndices.Length;
        }

        return true;
    }

    private static HashSet<int> CollectRetainedSourceNodes(
        GlbScene source,
        IReadOnlyDictionary<string, List<int>> skeletonSceneIndicesByName)
    {
        var retained = new HashSet<int>();
        foreach (var part in source.MeshParts)
        {
            if (part.NodeIndex is not int nodeIndex)
            {
                continue;
            }

            while ((uint)nodeIndex < (uint)source.Nodes.Count &&
                   nodeIndex != GlbScene.RootNodeIndex &&
                   retained.Add(nodeIndex))
            {
                var node = source.Nodes[nodeIndex];
                if (!string.IsNullOrWhiteSpace(node.LookupName) &&
                    skeletonSceneIndicesByName.TryGetValue(node.LookupName, out var matches) &&
                    matches.Count == 1)
                {
                    retained.Remove(nodeIndex);
                    break;
                }

                if (node.ParentIndex is not int parentIndex || parentIndex == nodeIndex)
                {
                    break;
                }

                nodeIndex = parentIndex;
            }
        }

        return retained;
    }

    private static int AppendRetainedSourceNode(
        GlbNode sourceNode,
        int parentIndex,
        GlbScene candidate,
        IReadOnlyDictionary<string, int> retainedNameCounts)
    {
        var lookupName = sourceNode.LookupName;
        if (!string.IsNullOrWhiteSpace(lookupName) &&
            retainedNameCounts.GetValueOrDefault(lookupName) != 1)
        {
            // Keep duplicate model-only controls renderable, but do not expose an arbitrary one as
            // a KF name target after the canonical rig has been installed.
            lookupName = null;
        }

        var worldTransform = sourceNode.LocalTransform * candidate.Nodes[parentIndex].WorldTransform;
        return candidate.AddNode(
            sourceNode.Name,
            parentIndex,
            sourceNode.LocalTransform,
            worldTransform,
            GlbNodeKind.Attachment,
            lookupName,
            sourceNode.SourceBlockIndex);
    }

    private static bool TryOrderSkeletonNodes(
        IReadOnlyList<NifExportExtractor.ExtractedNode> skeletonNodes,
        out List<NifExportExtractor.ExtractedNode> ordered,
        out string diagnostic)
    {
        ordered = [];
        diagnostic = string.Empty;
        if (skeletonNodes.Count == 0)
        {
            diagnostic = "The canonical skeleton contains no scene nodes.";
            return false;
        }

        var byBlock = new Dictionary<int, NifExportExtractor.ExtractedNode>();
        foreach (var node in skeletonNodes)
        {
            if (node.BlockIndex < 0 || !byBlock.TryAdd(node.BlockIndex, node))
            {
                diagnostic = $"The canonical skeleton repeats invalid block identity {node.BlockIndex}.";
                return false;
            }
        }

        foreach (var node in skeletonNodes)
        {
            if (node.ParentBlockIndex is int parentBlockIndex && !byBlock.ContainsKey(parentBlockIndex))
            {
                diagnostic =
                    $"Canonical skeleton node {node.BlockIndex} references missing parent {parentBlockIndex}.";
                return false;
            }
        }

        var pending = new List<NifExportExtractor.ExtractedNode>(skeletonNodes);
        var emitted = new HashSet<int>();
        while (pending.Count > 0)
        {
            var madeProgress = false;
            for (var index = 0; index < pending.Count;)
            {
                var node = pending[index];
                if (node.ParentBlockIndex is int parentBlockIndex && !emitted.Contains(parentBlockIndex))
                {
                    index++;
                    continue;
                }

                ordered.Add(node);
                emitted.Add(node.BlockIndex);
                pending.RemoveAt(index);
                madeProgress = true;
            }

            if (!madeProgress)
            {
                diagnostic = "The canonical skeleton hierarchy contains a parent cycle.";
                ordered.Clear();
                return false;
            }
        }

        return true;
    }

    private static bool IsFiniteDecomposable(Matrix4x4 matrix)
    {
        return IsFinite(matrix) &&
               Matrix4x4.Decompose(matrix, out var scale, out var rotation, out var translation) &&
               IsFinite(scale) &&
               IsFinite(rotation) &&
               IsFinite(translation);
    }

    private static bool IsFinite(Matrix4x4 matrix)
    {
        return float.IsFinite(matrix.M11) && float.IsFinite(matrix.M12) &&
               float.IsFinite(matrix.M13) && float.IsFinite(matrix.M14) &&
               float.IsFinite(matrix.M21) && float.IsFinite(matrix.M22) &&
               float.IsFinite(matrix.M23) && float.IsFinite(matrix.M24) &&
               float.IsFinite(matrix.M31) && float.IsFinite(matrix.M32) &&
               float.IsFinite(matrix.M33) && float.IsFinite(matrix.M34) &&
               float.IsFinite(matrix.M41) && float.IsFinite(matrix.M42) &&
               float.IsFinite(matrix.M43) && float.IsFinite(matrix.M44);
    }

    private static bool IsFinite(Vector3 value)
    {
        return float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    }

    private static bool IsFinite(Quaternion value)
    {
        return float.IsFinite(value.X) && float.IsFinite(value.Y) &&
               float.IsFinite(value.Z) && float.IsFinite(value.W);
    }
}
