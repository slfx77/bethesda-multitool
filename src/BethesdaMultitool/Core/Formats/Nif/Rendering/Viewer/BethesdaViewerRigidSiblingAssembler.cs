using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Export;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Viewer;

/// <summary>
///     Attaches an exact rigid model-family sibling to one unique joint in an already-rigged scene.
///     The operation is deliberately transactional: a missing/ambiguous anchor, a skinned sibling,
///     or a malformed hierarchy returns the original scene without changing its external-skeleton
///     skin bindings or raw Bethesda submeshes/material state.
/// </summary>
internal static class BethesdaViewerRigidSiblingAssembler
{
    internal const long MaximumPayloadBytes = 16L * 1024L * 1024L;
    internal const string HeadTargetNodeName = "Bip01 Head";

    /// <summary>
    ///     Resolves only the two authored classic creature conventions: <c>*body.nif -&gt; *head.nif</c>
    ///     and a family-named body <c>family\family.nif -&gt; family\head.nif</c> (retail Oblivion
    ///     Minotaur). No directory enumeration, fuzzy name matching, or cross-family fallback is
    ///     permitted.
    /// </summary>
    internal static bool TryResolveHeadSibling(
        string? modelPath,
        out string siblingPath,
        out string targetNodeName)
    {
        siblingPath = string.Empty;
        targetNodeName = string.Empty;
        if (!TryNormalizeVirtualPath(modelPath, out var normalizedModelPath))
        {
            return false;
        }

        var separator = normalizedModelPath.LastIndexOf('\\');
        var fileName = normalizedModelPath[(separator + 1)..];
        if (!fileName.EndsWith(".nif", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var stem = fileName[..^4];
        string siblingFileName;
        if (stem.EndsWith("body", StringComparison.OrdinalIgnoreCase))
        {
            siblingFileName = stem[..^4] + "head.nif";
        }
        else
        {
            // Retail Oblivion's Minotaur family names its body minotaur.nif and keeps the rigid
            // head in head.nif. Generalize only that exact structural convention: the model stem
            // must equal its immediate family-directory name. This remains deterministic and
            // cannot leak a head across sibling creature families.
            if (separator < 0)
            {
                return false;
            }

            var directory = normalizedModelPath[..separator];
            var directorySeparator = directory.LastIndexOf('\\');
            var familyName = directory[(directorySeparator + 1)..];
            if (familyName.Length == 0 ||
                !stem.Equals(familyName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            siblingFileName = "head.nif";
        }

        siblingPath = separator < 0
            ? siblingFileName
            : normalizedModelPath[..(separator + 1)] + siblingFileName;
        targetNodeName = HeadTargetNodeName;
        return true;
    }

    internal static bool TryAttach(
        GlbScene hostRiggedScene,
        GlbScene rigidSiblingScene,
        string siblingLabel,
        string targetNodeName,
        out GlbScene result,
        out string diagnostic)
    {
        ArgumentNullException.ThrowIfNull(hostRiggedScene);
        ArgumentNullException.ThrowIfNull(rigidSiblingScene);

        result = hostRiggedScene;
        diagnostic = string.Empty;
        if (rigidSiblingScene.MeshParts.Count == 0)
        {
            diagnostic = "The rigid sibling contains no renderable mesh parts.";
            return false;
        }

        if (rigidSiblingScene.MeshParts.Any(static part => part.Skin is not null))
        {
            diagnostic = "The sibling contains a skin binding and is not a rigid attachment.";
            return false;
        }

        if (CountSkeletonNodes(hostRiggedScene, targetNodeName, out var hostTargetNodeIndex) != 1)
        {
            diagnostic = $"The rigged scene does not contain one unique skeleton joint '{targetNodeName}'.";
            return false;
        }

        var siblingAnchorCount = CountSkeletonNodes(
            rigidSiblingScene,
            targetNodeName,
            out var siblingTargetNodeIndex);
        if (siblingAnchorCount > 1)
        {
            diagnostic = $"The rigid sibling contains {siblingAnchorCount} anchors named '{targetNodeName}'.";
            return false;
        }

        // Oblivion creature head NIFs do not repeat the skeleton joint they attach to. Retail
        // bearhead.nif, for example, is one authored BearHead root plus a rigid NiTriStrips child.
        // In that convention the exact *body -> *head path supplies the target semantics, while the
        // sibling's one renderable root branch supplies transforms that must be retained. Seed the
        // synthetic scene root at the canonical joint so recursive mapping appends that authored
        // branch below it. Multiple independent renderable roots remain ambiguous and fail closed.
        var usesAuthoredRootBranch = siblingAnchorCount == 0;
        var authoredRootBranchIndex = -1;
        if (usesAuthoredRootBranch &&
            !TryFindUniqueRenderableRootBranch(
                rigidSiblingScene,
                out authoredRootBranchIndex,
                out diagnostic))
        {
            return false;
        }

        if (!TryCloneHost(hostRiggedScene, out var candidate, out diagnostic))
        {
            return false;
        }

        var mappedSiblingNodes = usesAuthoredRootBranch
            ? new Dictionary<int, int>
            {
                [GlbScene.RootNodeIndex] = hostTargetNodeIndex
            }
            : new Dictionary<int, int>
            {
                [siblingTargetNodeIndex] = hostTargetNodeIndex
            };
        var nodesBeingMapped = new HashSet<int>();
        string? mappingDiagnostic = null;

        bool TryMapSiblingNode(int siblingNodeIndex, out int candidateNodeIndex)
        {
            if (mappedSiblingNodes.TryGetValue(siblingNodeIndex, out candidateNodeIndex))
            {
                return true;
            }

            if ((uint)siblingNodeIndex >= (uint)rigidSiblingScene.Nodes.Count)
            {
                mappingDiagnostic = $"A sibling mesh part addresses missing node {siblingNodeIndex}.";
                candidateNodeIndex = -1;
                return false;
            }

            if (!nodesBeingMapped.Add(siblingNodeIndex))
            {
                mappingDiagnostic = $"The rigid sibling contains a parent cycle at node {siblingNodeIndex}.";
                candidateNodeIndex = -1;
                return false;
            }

            try
            {
                var siblingNode = rigidSiblingScene.Nodes[siblingNodeIndex];
                if (siblingNode.ParentIndex is not int siblingParentIndex)
                {
                    mappingDiagnostic =
                        $"Sibling node {siblingNodeIndex} is not a descendant of '{targetNodeName}'.";
                    candidateNodeIndex = -1;
                    return false;
                }

                if (!TryMapSiblingNode(siblingParentIndex, out var candidateParentIndex))
                {
                    candidateNodeIndex = -1;
                    return false;
                }

                if (!IsFiniteDecomposable(siblingNode.LocalTransform))
                {
                    mappingDiagnostic = $"Sibling node {siblingNodeIndex} has an invalid local transform.";
                    candidateNodeIndex = -1;
                    return false;
                }

                var worldTransform =
                    siblingNode.LocalTransform * candidate.Nodes[candidateParentIndex].WorldTransform;
                if (!IsFinite(worldTransform))
                {
                    mappingDiagnostic = $"Sibling node {siblingNodeIndex} produces a non-finite world transform.";
                    candidateNodeIndex = -1;
                    return false;
                }

                candidateNodeIndex = candidate.AddNode(
                    siblingNode.Name,
                    candidateParentIndex,
                    siblingNode.LocalTransform,
                    worldTransform,
                    GlbNodeKind.Attachment,
                    // The sibling is a separate NIF. Its names and block indices must not compete
                    // with canonical KF targets or model-NIF source identities.
                    null,
                    null);
                mappedSiblingNodes.Add(siblingNodeIndex, candidateNodeIndex);
                return true;
            }
            finally
            {
                nodesBeingMapped.Remove(siblingNodeIndex);
            }
        }

        var prefix = GetFileName(siblingLabel);
        for (var partIndex = 0; partIndex < rigidSiblingScene.MeshParts.Count; partIndex++)
        {
            var part = rigidSiblingScene.MeshParts[partIndex];
            if (part.NodeIndex is not int siblingNodeIndex ||
                !TryMapSiblingNode(siblingNodeIndex, out var candidateNodeIndex))
            {
                diagnostic = mappingDiagnostic ??
                             $"Rigid sibling mesh part '{part.Name}' has no valid attachment node.";
                return false;
            }

            candidate.MeshParts.Add(new GlbMeshPart
            {
                Name = $"{prefix}::{part.Name}",
                NodeIndex = candidateNodeIndex,
                // Preserve the raw renderable/material contract. This assembler owns hierarchy only.
                Submesh = part.Submesh,
                Skin = null
            });
        }

        result = candidate;
        var attachmentBasis = usesAuthoredRootBranch
            ? $"through authored root '{rigidSiblingScene.Nodes[authoredRootBranchIndex].Name}'"
            : "through its matching skeleton anchor";
        diagnostic =
            $"Attached {rigidSiblingScene.MeshParts.Count:N0} rigid part(s) from '{prefix}' " +
            $"to the unique '{targetNodeName}' joint {attachmentBasis}.";
        return true;
    }

    private static bool TryFindUniqueRenderableRootBranch(
        GlbScene scene,
        out int rootBranchIndex,
        out string diagnostic)
    {
        rootBranchIndex = -1;
        diagnostic = string.Empty;
        foreach (var part in scene.MeshParts)
        {
            if (part.NodeIndex is not int nodeIndex || nodeIndex == GlbScene.RootNodeIndex)
            {
                diagnostic = $"Rigid sibling mesh part '{part.Name}' has no authored attachment branch.";
                return false;
            }

            var visited = new HashSet<int>();
            while (true)
            {
                if ((uint)nodeIndex >= (uint)scene.Nodes.Count || !visited.Add(nodeIndex))
                {
                    diagnostic = $"Rigid sibling mesh part '{part.Name}' has a malformed parent chain.";
                    return false;
                }

                var node = scene.Nodes[nodeIndex];
                if (node.ParentIndex is not int parentIndex)
                {
                    diagnostic = $"Rigid sibling mesh part '{part.Name}' is not below the scene root.";
                    return false;
                }

                if (parentIndex == GlbScene.RootNodeIndex)
                {
                    if (rootBranchIndex >= 0 && rootBranchIndex != nodeIndex)
                    {
                        diagnostic = "The anchorless rigid sibling has multiple renderable root branches.";
                        return false;
                    }

                    rootBranchIndex = nodeIndex;
                    break;
                }

                nodeIndex = parentIndex;
            }
        }

        if (rootBranchIndex < 0 || scene.Nodes[rootBranchIndex].Kind != GlbNodeKind.Skeleton)
        {
            diagnostic = "The anchorless rigid sibling has no unique authored NIF root branch.";
            return false;
        }

        return true;
    }

    private static bool TryCloneHost(
        GlbScene source,
        out GlbScene candidate,
        out string diagnostic)
    {
        candidate = new GlbScene();
        candidate.Nodes.Clear();
        diagnostic = string.Empty;
        if (source.Nodes.Count == 0)
        {
            diagnostic = "The rigged scene contains no root node.";
            return false;
        }

        for (var nodeIndex = 0; nodeIndex < source.Nodes.Count; nodeIndex++)
        {
            var node = source.Nodes[nodeIndex];
            if ((nodeIndex == GlbScene.RootNodeIndex && node.ParentIndex is not null) ||
                (nodeIndex != GlbScene.RootNodeIndex &&
                 (node.ParentIndex is not int parentIndex || parentIndex < 0 || parentIndex >= nodeIndex)) ||
                !IsFiniteDecomposable(node.LocalTransform) ||
                !IsFinite(node.WorldTransform))
            {
                diagnostic = $"The rigged scene has an invalid parent-first node at index {nodeIndex}.";
                return false;
            }

            candidate.AddNode(
                node.Name,
                node.ParentIndex,
                node.LocalTransform,
                node.WorldTransform,
                node.Kind,
                node.LookupName,
                node.SourceBlockIndex);
        }

        foreach (var part in source.MeshParts)
        {
            if (part.NodeIndex is int nodeIndex && (uint)nodeIndex >= (uint)source.Nodes.Count)
            {
                diagnostic = $"Host mesh part '{part.Name}' addresses missing node {nodeIndex}.";
                return false;
            }

            candidate.MeshParts.Add(new GlbMeshPart
            {
                Name = part.Name,
                NodeIndex = part.NodeIndex,
                Submesh = part.Submesh,
                Skin = part.Skin
            });
        }

        return true;
    }

    private static int CountSkeletonNodes(
        GlbScene scene,
        string nodeName,
        out int nodeIndex)
    {
        nodeIndex = -1;
        var matchCount = 0;
        for (var index = 0; index < scene.Nodes.Count; index++)
        {
            var node = scene.Nodes[index];
            if (node.Kind != GlbNodeKind.Skeleton ||
                !string.Equals(node.LookupName, nodeName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            nodeIndex = index;
            matchCount++;
        }

        return matchCount;
    }

    private static bool TryNormalizeVirtualPath(string? path, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(path) ||
            path.Length > 4096 ||
            path[0] is '\\' or '/')
        {
            return false;
        }

        var segments = path.Replace('/', '\\')
            .Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length is 0 or > 256 ||
            segments.Any(static segment =>
                segment is "." or ".." || segment.Contains(':') || segment.Contains('\0')))
        {
            return false;
        }

        normalized = string.Join('\\', segments);
        return normalized.Length <= 4096;
    }

    private static string GetFileName(string label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return "rigid-sibling.nif";
        }

        var separator = Math.Max(label.LastIndexOf('\\'), label.LastIndexOf('/'));
        return separator < 0 ? label : label[(separator + 1)..];
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
