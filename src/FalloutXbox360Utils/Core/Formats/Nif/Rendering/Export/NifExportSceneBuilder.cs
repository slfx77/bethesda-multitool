using System.Numerics;
using FalloutXbox360Utils.CLI;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Animation;
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
        var skeletonNifPath = skeletonPath.StartsWith("meshes\\", StringComparison.OrdinalIgnoreCase)
            ? skeletonPath
            : "meshes\\" + skeletonPath;

        var skeletonRaw = NpcMeshHelpers.LoadNifRawFromBsa(skeletonNifPath, meshArchives);
        if (skeletonRaw == null)
        {
            return null;
        }

        // Load idle animation: KFFZ from ESM → locomotion/mtidle.kf → skeleton embedded
        Dictionary<string, NifAnimationParser.AnimPoseOverride>? animOverrides = null;
        if (!bindPose)
        {
            (byte[] Data, NifInfo Info)? idleRaw = null;

            // 1. Try ESM-defined animation (KFFZ)
            if (idleAnimationPath != null)
            {
                var kfPath = idleAnimationPath.StartsWith("meshes\\", StringComparison.OrdinalIgnoreCase)
                    ? idleAnimationPath
                    : "meshes\\" + idleAnimationPath;
                idleRaw = NpcMeshHelpers.LoadNifRawFromBsa(kfPath, meshArchives, skipConversion: true);
                if (idleRaw != null)
                {
                    Log.Debug("Creature idle animation from KFFZ: {0}", kfPath);
                }
            }

            // 2. Try locomotion/mtidle.kf in skeleton directory
            if (idleRaw == null)
            {
                var skeletonDir = skeletonNifPath.Replace(
                    "skeleton.nif", "", StringComparison.OrdinalIgnoreCase);
                idleRaw = NpcMeshHelpers.LoadNifRawFromBsa(
                    skeletonDir + "locomotion\\mtidle.kf", meshArchives, skipConversion: true);
            }

            // 3. Parse from KF file or fall back to skeleton embedded node controllers
            if (idleRaw != null)
            {
                animOverrides = NifAnimationParser.ParseIdlePoseOverrides(idleRaw.Value.Data, idleRaw.Value.Info);
            }
            else
            {
                // Try NiControllerSequence blocks first, then per-node NiTransformControllers
                animOverrides = NifAnimationParser.ParseIdlePoseOverrides(
                    skeletonRaw.Value.Data, skeletonRaw.Value.Info);
                if (animOverrides == null || animOverrides.Count == 0)
                {
                    animOverrides = NifNodeControllerPoseReader.Parse(
                        skeletonRaw.Value.Data, skeletonRaw.Value.Info);
                }
            }
        }

        // Extract skeleton hierarchy with animation applied
        var extractedSkeleton = NifExportExtractor.Extract(
            skeletonRaw.Value.Data, skeletonRaw.Value.Info, animOverrides);
        var scene = new NpcExportScene();
        var nodeIndicesByName = AddNodes(scene, extractedSkeleton.Nodes);

        // Build bone world transforms for attaching rigid meshes to skeleton bones
        var boneWorldTransforms = extractedSkeleton.NamedNodeWorldTransforms;

        Log.Debug("Creature skeleton: {0} named bones from '{1}'",
            nodeIndicesByName.Count, Path.GetFileName(skeletonNifPath));

        // Resolve body mesh paths relative to skeleton directory
        var skeletonDirectory = Path.GetDirectoryName(skeletonNifPath);

        foreach (var bodyPath in bodyModelPaths)
        {
            string fullBodyPath;
            if (bodyPath.Contains('\\') || bodyPath.Contains('/'))
            {
                fullBodyPath = bodyPath.StartsWith("meshes\\", StringComparison.OrdinalIgnoreCase)
                    ? bodyPath
                    : "meshes\\" + bodyPath;
            }
            else if (!string.IsNullOrEmpty(skeletonDirectory))
            {
                fullBodyPath = Path.Combine(skeletonDirectory, bodyPath);
            }
            else
            {
                fullBodyPath = "meshes\\" + bodyPath;
            }

            var bodyRaw = NpcMeshHelpers.LoadNifRawFromBsa(fullBodyPath, meshArchives);
            if (bodyRaw == null)
            {
                continue;
            }

            var bodyExtracted = NifExportExtractor.Extract(bodyRaw.Value.Data, bodyRaw.Value.Info);
            foreach (var part in bodyExtracted.MeshParts)
            {
                if (part.Skin != null && TryCreateSkinBinding(part.Skin, nodeIndicesByName, out var skinBinding))
                {
                    Log.Debug("  Skinned: '{0}' ({1} bones)", part.Name, part.Skin.BoneNames.Length);
                    scene.MeshParts.Add(new NpcExportMeshPart
                    {
                        Name = part.Name,
                        Submesh = CloneSubmesh(part.Submesh),
                        Skin = skinBinding
                    });
                }
                else
                {
                    if (part.Skin != null)
                    {
                        var missingBones = part.Skin.BoneNames
                            .Where(b => !nodeIndicesByName.ContainsKey(b))
                            .ToArray();
                        Log.Warn("Creature skin binding failed for '{0}': missing bones [{1}]",
                            part.Name, string.Join(", ", missingBones));
                    }
                    else
                    {
                        Log.Debug("  Rigid: '{0}'", part.Name);
                    }

                    // For rigid meshes in creature body NIFs, try to find a skeleton
                    // bone to attach to. Eye meshes etc. are authored in head-local
                    // space, so apply translation only (not rotation) from the bone.
                    var attachmentTransform = part.ShapeWorldTransform;
                    if (attachmentTransform.Translation.LengthSquared() < 0.01f &&
                        boneWorldTransforms != null)
                    {
                        if (boneWorldTransforms.TryGetValue("Bip01 Head", out var headTransform))
                        {
                            attachmentTransform = Matrix4x4.CreateTranslation(headTransform.Translation);
                            Log.Debug("  Rigid mesh '{0}' attached to 'Bip01 Head'", part.Name);
                        }
                    }

                    var rigidSubmesh = CloneSubmesh(part.Submesh);
                    ApplyWorldTransform(rigidSubmesh, attachmentTransform);
                    var nodeIndex = scene.AddNode(
                        $"{Path.GetFileNameWithoutExtension(fullBodyPath)}_{scene.MeshParts.Count}",
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
            }
        }

        // Attach weapon if provided and skeleton has a "Weapon" bone
        if (weaponMeshPath != null && boneWorldTransforms != null &&
            boneWorldTransforms.TryGetValue("Weapon", out var weaponBoneTransform))
        {
            var weaponNifPath = weaponMeshPath.StartsWith("meshes\\", StringComparison.OrdinalIgnoreCase)
                ? weaponMeshPath
                : "meshes\\" + weaponMeshPath;
            var weaponRaw = NpcMeshHelpers.LoadNifRawFromBsa(weaponNifPath, meshArchives);
            if (weaponRaw != null)
            {
                var weaponExtracted = NifExportExtractor.Extract(weaponRaw.Value.Data, weaponRaw.Value.Info);
                foreach (var part in weaponExtracted.MeshParts)
                {
                    var weaponSubmesh = CloneSubmesh(part.Submesh);
                    ApplyWorldTransform(weaponSubmesh, weaponBoneTransform * part.ShapeWorldTransform);
                    var nodeIndex = scene.AddNode(
                        $"Weapon_{scene.MeshParts.Count}",
                        NpcExportScene.RootNodeIndex,
                        Matrix4x4.Identity,
                        Matrix4x4.Identity,
                        NpcExportNodeKind.Attachment);
                    scene.MeshParts.Add(new NpcExportMeshPart
                    {
                        Name = part.Name,
                        NodeIndex = nodeIndex,
                        Submesh = weaponSubmesh
                    });
                }

                Log.Debug("  Weapon attached: '{0}' ({1} parts)", weaponNifPath, weaponExtracted.MeshParts.Count);
            }
        }

        return scene.MeshParts.Count > 0 ? scene : null;
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
