using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Export;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Viewer;

/// <summary>
///     Transitional bridge for scene assemblers that currently produce <see cref="GlbScene" />.
///     The bridge copies the legacy scene subset without material conversion or geometry baking;
///     GLB serialization remains an explicit downstream export operation.
/// </summary>
internal static class BethesdaViewerSceneGlbAdapter
{
    /// <summary>
    ///     Wraps an existing raw-NIF or assembled-NPC GLB scene in the native viewer contract. Node
    ///     and joint indices retain their exact order, and each <see cref="RenderableSubmesh" />
    ///     instance is retained so no Bethesda material semantics are projected away.
    /// </summary>
    internal static BethesdaViewerScene FromGlbScene(
        GlbScene source,
        string sourceLabel,
        BethesdaViewerScenePurpose purpose,
        BethesdaViewerBounds? bounds = null,
        BethesdaGame game = BethesdaGame.Unknown,
        IReadOnlyList<string>? textureSourcePaths = null)
    {
        ArgumentNullException.ThrowIfNull(source);

        var target = new BethesdaViewerScene(
            sourceLabel,
            purpose,
            bounds ?? CalculateBounds(source),
            game,
            textureSourcePaths);
        target.Nodes.Clear();

        foreach (var node in source.Nodes)
        {
            target.AddNode(
                node.Name,
                node.ParentIndex,
                node.LocalTransform,
                node.WorldTransform,
                ToViewerRole(node.Kind),
                node.LookupName,
                node.SourceBlockIndex);
        }

        foreach (var meshPart in source.MeshParts)
        {
            target.MeshParts.Add(new BethesdaViewerMeshPart
            {
                Name = meshPart.Name,
                NodeIndex = meshPart.NodeIndex,
                Submesh = meshPart.Submesh,
                Skin = meshPart.Skin == null
                    ? null
                    : new BethesdaViewerSkinBinding
                    {
                        JointNodeIndices = meshPart.Skin.JointNodeIndices,
                        InverseBindMatrices = meshPart.Skin.InverseBindMatrices,
                        PerVertexInfluences = meshPart.Skin.PerVertexInfluences
                    }
            });
        }

        return target;
    }

    /// <summary>
    ///     Projects the static node/mesh/skin subset back to the legacy export DTO. Native animation
    ///     clips stay on <paramref name="source" /> because the current GLB DTO has no animation
    ///     representation; its existing fields are otherwise copied without loss or material baking.
    /// </summary>
    internal static GlbScene ToGlbScene(BethesdaViewerScene source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.ContractVersion != BethesdaViewerScene.CurrentContractVersion)
        {
            throw new NotSupportedException(
                $"Bethesda viewer scene contract v{source.ContractVersion} is not supported.");
        }

        var target = new GlbScene();
        target.Nodes.Clear();

        foreach (var node in source.Nodes)
        {
            target.AddNode(
                node.Name,
                node.ParentIndex,
                node.LocalTransform,
                node.WorldTransform,
                ToGlbKind(node.Role),
                node.LookupName,
                node.SourceBlockIndex);
        }

        foreach (var meshPart in source.MeshParts)
        {
            target.MeshParts.Add(new GlbMeshPart
            {
                Name = meshPart.Name,
                NodeIndex = meshPart.NodeIndex,
                Submesh = meshPart.Submesh,
                Skin = meshPart.Skin == null
                    ? null
                    : new GlbSkinBinding
                    {
                        JointNodeIndices = meshPart.Skin.JointNodeIndices,
                        InverseBindMatrices = meshPart.Skin.InverseBindMatrices,
                        PerVertexInfluences = meshPart.Skin.PerVertexInfluences
                    }
            });
        }

        return target;
    }

    private static BethesdaViewerNodeRole ToViewerRole(GlbNodeKind kind)
    {
        return kind switch
        {
            GlbNodeKind.Root => BethesdaViewerNodeRole.Root,
            GlbNodeKind.Skeleton => BethesdaViewerNodeRole.Skeleton,
            GlbNodeKind.Attachment => BethesdaViewerNodeRole.Attachment,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown GLB node role.")
        };
    }

    private static GlbNodeKind ToGlbKind(BethesdaViewerNodeRole role)
    {
        return role switch
        {
            BethesdaViewerNodeRole.Root => GlbNodeKind.Root,
            BethesdaViewerNodeRole.Skeleton => GlbNodeKind.Skeleton,
            BethesdaViewerNodeRole.Attachment => GlbNodeKind.Attachment,
            _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown viewer node role.")
        };
    }

    private static BethesdaViewerBounds? CalculateBounds(GlbScene source)
    {
        var minimum = new Vector3(float.MaxValue);
        var maximum = new Vector3(float.MinValue);
        var foundPoint = false;

        foreach (var meshPart in source.MeshParts)
        {
            var positions = meshPart.Submesh.Positions;
            var worldTransform = ResolveWorldTransform(source, meshPart.NodeIndex);
            for (var index = 0; index + 2 < positions.Length; index += 3)
            {
                var point = Vector3.Transform(
                    new Vector3(positions[index], positions[index + 1], positions[index + 2]),
                    worldTransform);
                if (!float.IsFinite(point.X) ||
                    !float.IsFinite(point.Y) ||
                    !float.IsFinite(point.Z))
                {
                    continue;
                }

                minimum = Vector3.Min(minimum, point);
                maximum = Vector3.Max(maximum, point);
                foundPoint = true;
            }
        }

        return foundPoint ? new BethesdaViewerBounds(minimum, maximum) : null;
    }

    private static Matrix4x4 ResolveWorldTransform(GlbScene source, int? nodeIndex)
    {
        var resolvedIndex = nodeIndex ?? GlbScene.RootNodeIndex;
        return resolvedIndex >= 0 && resolvedIndex < source.Nodes.Count
            ? source.Nodes[resolvedIndex].WorldTransform
            : Matrix4x4.Identity;
    }
}
