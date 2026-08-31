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
        var nodeIndicesByName = AddNodes(scene, extracted.Nodes);

        foreach (var part in extracted.MeshParts)
        {
            if (part.Skin != null && TryCreateSkinBinding(part.Skin, nodeIndicesByName, out var skinBinding))
            {
                scene.MeshParts.Add(new GlbMeshPart
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
                GlbScene.RootNodeIndex,
                Matrix4x4.Identity,
                Matrix4x4.Identity,
                GlbNodeKind.Attachment);
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
                Submesh = CloneGeometryWithMaterialState(geometry, materialSource)
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

    private static Dictionary<string, int> AddNodes(
        GlbScene scene,
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
                : GlbScene.RootNodeIndex;

            var sceneNodeIndex = scene.AddNode(
                $"{node.Name}_{node.BlockIndex}",
                parentSceneNode,
                node.LocalTransform,
                node.WorldTransform,
                GlbNodeKind.Skeleton,
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
        out GlbSkinBinding? binding)
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

        binding = new GlbSkinBinding
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
            StarfieldMaterialColor = source.StarfieldMaterialColor,
            StarfieldMaterialAlpha = source.StarfieldMaterialAlpha,
            DiffuseTexturePath = source.DiffuseTexturePath,
            BgsmGlowMapTexturePath = source.BgsmGlowMapTexturePath,
            BgsmEmissionColor = source.BgsmEmissionColor,
            ClampTextureU = source.ClampTextureU,
            ClampTextureV = source.ClampTextureV,
            NormalMapTexturePath = source.NormalMapTexturePath,
            EffectTint = source.EffectTint,
            EffectFalloff = source.EffectFalloff,
            ShaderMetadata = source.ShaderMetadata,
            IsEmissive = source.IsEmissive,
            UseVertexColors = source.UseVertexColors,
            UseVertexAlphaForOpacity = source.UseVertexAlphaForOpacity,
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
            TintColor = source.TintColor,
            SourceBlockIndex = source.SourceBlockIndex,
            SourceNifPath = source.SourceNifPath
        };
    }

    private static RenderableSubmesh CloneGeometryWithMaterialState(
        RenderableSubmesh geometry,
        RenderableSubmesh material)
    {
        return new RenderableSubmesh
        {
            ShapeName = geometry.ShapeName,
            Positions = geometry.Positions,
            Triangles = geometry.Triangles,
            Normals = geometry.Normals,
            UVs = geometry.UVs,
            VertexColors = geometry.VertexColors,
            Tangents = geometry.Tangents,
            Bitangents = geometry.Bitangents,
            BindPosePositions = geometry.BindPosePositions,
            LocalBounds = geometry.LocalBounds,
            SourceBlockIndex = geometry.SourceBlockIndex,
            SourceNifPath = material.SourceNifPath ?? geometry.SourceNifPath,

            StarfieldMaterialColor = material.StarfieldMaterialColor,
            StarfieldMaterialAlpha = material.StarfieldMaterialAlpha,
            DiffuseTexturePath = material.DiffuseTexturePath,
            BgsmGlowMapTexturePath = material.BgsmGlowMapTexturePath,
            BgsmEmissionColor = material.BgsmEmissionColor,
            NormalMapTexturePath = material.NormalMapTexturePath,
            SpecularMapTexturePath = material.SpecularMapTexturePath,
            GradientMapTexturePath = material.GradientMapTexturePath,
            GradientMapV = material.GradientMapV,
            EnvironmentMapTexturePath = material.EnvironmentMapTexturePath,
            EnvironmentMapScale = material.EnvironmentMapScale,
            EnvironmentMapSmoothness = material.EnvironmentMapSmoothness,
            ClassicEnvironmentMapTexturePath = material.ClassicEnvironmentMapTexturePath,
            ClassicEnvironmentMaskTexturePath = material.ClassicEnvironmentMaskTexturePath,
            ClassicEnvironmentMapScale = material.ClassicEnvironmentMapScale,
            ClassicEnvironmentMapUsesWindowReflection = material.ClassicEnvironmentMapUsesWindowReflection,
            ClassicEnvironmentMapIsSphereMap = material.ClassicEnvironmentMapIsSphereMap,
            ClassicParallaxHeightMapTexturePath = material.ClassicParallaxHeightMapTexturePath,
            ClampTextureU = material.ClampTextureU,
            ClampTextureV = material.ClampTextureV,
            EffectTint = material.EffectTint,
            EffectFalloff = material.EffectFalloff,
            ShaderMetadata = material.ShaderMetadata,
            IsEmissive = material.IsEmissive,
            UseVertexColors = material.UseVertexColors,
            UseVertexAlphaForOpacity = material.UseVertexAlphaForOpacity,
            IsDoubleSided = material.IsDoubleSided,
            HasAlphaBlend = material.HasAlphaBlend,
            HasAlphaTest = material.HasAlphaTest,
            AlphaTestThreshold = material.AlphaTestThreshold,
            AlphaTestFunction = material.AlphaTestFunction,
            SrcBlendMode = material.SrcBlendMode,
            DstBlendMode = material.DstBlendMode,
            MaterialAlpha = material.MaterialAlpha,
            MaterialGlossiness = material.MaterialGlossiness,
            SpecularColor = material.SpecularColor,
            MaterialDiffuse = material.MaterialDiffuse,
            IsEyeEnvmap = material.IsEyeEnvmap,
            EnvMapScale = material.EnvMapScale,
            RenderOrder = material.RenderOrder,
            TintColor = material.TintColor,
            IsFaceGen = material.IsFaceGen
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
