using System.Numerics;
using FalloutXbox360Utils.CLI;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Animation;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Composition;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Assets;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Export;

internal static class NifExportSceneBuilder
{
    private static readonly Logger Log = Logger.Instance;

    internal static NpcExportScene? Build(byte[] data, NifInfo nif, string sourceLabel)
    {
        var extracted = NifExportExtractor.Extract(data, nif);
        if (extracted.MeshParts.Count == 0)
        {
            return null;
        }

        var scene = new NpcExportScene();
        var nodeIndicesByName = AddNodes(scene, extracted.Nodes);

        foreach (var part in extracted.MeshParts)
        {
            if (part.Skin != null && TryCreateSkinBinding(part.Skin, nodeIndicesByName, out var skinBinding))
            {
                scene.MeshParts.Add(new NpcExportMeshPart
                {
                    Name = part.Name,
                    Submesh = CloneSubmesh(part.Submesh),
                    Skin = skinBinding
                });
                continue;
            }

            var rigidSubmesh = CloneSubmesh(part.Submesh);
            ApplyWorldTransform(rigidSubmesh, part.ShapeWorldTransform);
            var nodeIndex = scene.AddNode(
                $"{Path.GetFileNameWithoutExtension(sourceLabel)}_{scene.MeshParts.Count}",
                NpcExportScene.RootNodeIndex,
                Matrix4x4.Identity,
                Matrix4x4.Identity,
                NpcExportNodeKind.Attachment);
            scene.MeshParts.Add(new NpcExportMeshPart
            {
                Name = part.Name,
                NodeIndex = nodeIndex,
                Submesh = rigidSubmesh
            });
        }

        return scene;
    }

    /// <summary>
    ///     Builds a creature scene from skeleton (MODL) + body meshes (NIFZ).
    ///     Loads skeleton first, applies idle animation if available, then loads
    ///     all NIFZ body meshes bound to the skeleton bones.
    /// </summary>
    internal static NpcExportScene? BuildCreature(
        string skeletonPath,
        string[] bodyModelPaths,
        NpcMeshArchiveSet meshArchives,
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

    private static Dictionary<string, int> AddNodes(
        NpcExportScene scene,
        IEnumerable<NifExportExtractor.ExtractedNode> nodes)
    {
        var nodeList = nodes.ToList();
        var blockToSceneNode = new Dictionary<int, int>();
        var nodeIndicesByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var node in nodeList)
        {
            var parentSceneNode = node.ParentBlockIndex is int parentBlockIndex &&
                                  blockToSceneNode.TryGetValue(parentBlockIndex, out var existingParent)
                ? existingParent
                : NpcExportScene.RootNodeIndex;

            var sceneNodeIndex = scene.AddNode(
                $"{node.Name}_{node.BlockIndex}",
                parentSceneNode,
                node.LocalTransform,
                node.WorldTransform,
                NpcExportNodeKind.Skeleton,
                node.LookupName);
            blockToSceneNode[node.BlockIndex] = sceneNodeIndex;

            if (!string.IsNullOrWhiteSpace(node.LookupName) && !nodeIndicesByName.ContainsKey(node.LookupName))
            {
                nodeIndicesByName.Add(node.LookupName, sceneNodeIndex);
            }
        }

        return nodeIndicesByName;
    }

    private static bool TryCreateSkinBinding(
        NifExportExtractor.ExtractedSkinBinding skin,
        Dictionary<string, int> nodeIndicesByName,
        out NpcExportSkinBinding? binding)
    {
        var jointNodeIndices = new int[skin.BoneNames.Length];
        for (var index = 0; index < skin.BoneNames.Length; index++)
        {
            if (!nodeIndicesByName.TryGetValue(skin.BoneNames[index], out var jointNodeIndex))
            {
                binding = null;
                return false;
            }

            jointNodeIndices[index] = jointNodeIndex;
        }

        binding = new NpcExportSkinBinding
        {
            JointNodeIndices = jointNodeIndices,
            InverseBindMatrices = skin.InverseBindMatrices,
            PerVertexInfluences = skin.PerVertexInfluences
        };
        return true;
    }

    private static RenderableSubmesh CloneSubmesh(RenderableSubmesh source)
    {
        return new RenderableSubmesh
        {
            ShapeName = source.ShapeName,
            Positions = (float[])source.Positions.Clone(),
            Triangles = (ushort[])source.Triangles.Clone(),
            Normals = source.Normals != null ? (float[])source.Normals.Clone() : null,
            UVs = source.UVs != null ? (float[])source.UVs.Clone() : null,
            VertexColors = source.VertexColors != null ? (byte[])source.VertexColors.Clone() : null,
            Tangents = source.Tangents != null ? (float[])source.Tangents.Clone() : null,
            Bitangents = source.Bitangents != null ? (float[])source.Bitangents.Clone() : null,
            DiffuseTexturePath = source.DiffuseTexturePath,
            NormalMapTexturePath = source.NormalMapTexturePath,
            ShaderMetadata = source.ShaderMetadata,
            IsEmissive = source.IsEmissive,
            UseVertexColors = source.UseVertexColors,
            IsDoubleSided = source.IsDoubleSided,
            HasAlphaBlend = source.HasAlphaBlend,
            HasAlphaTest = source.HasAlphaTest,
            AlphaTestThreshold = source.AlphaTestThreshold,
            AlphaTestFunction = source.AlphaTestFunction,
            SrcBlendMode = source.SrcBlendMode,
            DstBlendMode = source.DstBlendMode,
            MaterialAlpha = source.MaterialAlpha,
            MaterialGlossiness = source.MaterialGlossiness,
            IsEyeEnvmap = source.IsEyeEnvmap,
            EnvMapScale = source.EnvMapScale,
            RenderOrder = source.RenderOrder,
            TintColor = source.TintColor
        };
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
