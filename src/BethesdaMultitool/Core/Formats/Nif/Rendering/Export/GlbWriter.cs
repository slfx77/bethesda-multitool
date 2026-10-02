using System.Numerics;
using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Formats.Nif.Materials;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Memory;
using SharpGLTF.Scenes;
using SharpGLTF.Schema2;
using AlphaMode = SharpGLTF.Materials.AlphaMode;
using TextureMipMapFilter = SharpGLTF.Schema2.TextureMipMapFilter;
using TextureInterpolationFilter = SharpGLTF.Schema2.TextureInterpolationFilter;
using TextureWrapMode = SharpGLTF.Schema2.TextureWrapMode;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Export;

/// <summary>Serializes an assembled <see cref="GlbScene" /> (nodes, meshes, skins, materials) to a GLB file via SharpGLTF.</summary>
internal static class GlbWriter
{
    internal static void Write(
        GlbScene scene,
        NifTextureResolver textureResolver,
        string outputPath)
    {
        ArgumentNullException.ThrowIfNull(outputPath);

        var outputDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        BuildGltfScene(scene, textureResolver).SaveGLB(outputPath);
    }

    internal static byte[] WriteToBytes(
        GlbScene scene,
        NifTextureResolver textureResolver)
    {
        using var ms = new MemoryStream();
        BuildGltfScene(scene, textureResolver).WriteGLB(ms);
        return ms.ToArray();
    }

    private static ModelRoot BuildGltfScene(
        GlbScene scene,
        NifTextureResolver textureResolver)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(textureResolver);

        using var assetReads = textureResolver.AssetSelection?.CaptureReads();
        var sceneBuilder = new SceneBuilder();
        var nodeBuilders = BuildNodeBuilders(scene);
        var materialCache = new NifGlbMaterialCache();
        var uses = BethesdaMultitool.Core.Formats.Nif.Rendering.Scene.SceneAssetUses.WithMeshes(
            scene.AssetUses, scene.MeshParts.Select(part => part.Submesh));

        foreach (var meshPart in scene.MeshParts)
        {
            using var componentReads = textureResolver.AssetSelection?.CaptureReads();
            try
            {
                if (meshPart.Submesh.TriangleCount == 0 || meshPart.Submesh.VertexCount == 0)
                {
                    continue;
                }

                // Match the world renderer's treatment of database-confirmed no-albedo helper geometry
                // before a glTF material can turn its absent base layer into an opaque white surface.
                // Specialized CE2 routes are retained: water/effect/ring/scattering geometry can be
                // legitimately albedo-free and must reach its dedicated or best-effort export route.
                if (ShouldSkipStarfieldNoDrawSubmesh(meshPart.Submesh, textureResolver))
                {
                    continue;
                }

                NormalizeWinding(meshPart.Submesh);

                if (meshPart.Skin != null)
                {
                    var skinnedMesh = BuildSkinnedMesh(meshPart, textureResolver, materialCache);
                    if (skinnedMesh.IsEmpty)
                    {
                        continue;
                    }

                    var joints = meshPart.Skin.JointNodeIndices
                        .Select((jointNodeIndex, jointIndex) => (
                            nodeBuilders[jointNodeIndex],
                            GltfCoordinateAdapter.ConvertMatrix(meshPart.Skin.InverseBindMatrices[jointIndex])))
                        .ToArray();
                    sceneBuilder.AddSkinnedMesh(skinnedMesh, joints);
                }
                else
                {
                    var rigidMesh = BuildRigidMesh(meshPart, textureResolver, materialCache);
                    if (rigidMesh.IsEmpty)
                    {
                        continue;
                    }

                    var nodeIndex = meshPart.NodeIndex ?? GlbScene.RootNodeIndex;
                    sceneBuilder.AddRigidMesh(rigidMesh, nodeBuilders[nodeIndex]);
                }
            }
            finally
            {
                uses = BethesdaMultitool.Core.Formats.Nif.Rendering.Scene.SceneAssetUses.WithObservedMaterialReads(
                    uses, meshPart.Submesh, componentReads?.Receipts ?? []);
            }
        }

        // SharpGLTF.Toolkit 1.0.6 rejects duplicate non-null armature names while
        // registering skins. Names are presentation labels, so keep the temporary
        // builders unnamed until registration is complete, then restore every label
        // before conversion (which associates joints by NodeBuilder identity).
        foreach (var (nodeIndex, nodeBuilder) in nodeBuilders)
        {
            nodeBuilder.Name = scene.Nodes[nodeIndex].Name;
        }

        var result = sceneBuilder.ToGltf2();
        uses = BethesdaMultitool.Core.Formats.Nif.Rendering.Scene.SceneAssetUses.WithGeneratedInputs(uses, textureResolver);
        var reads = scene.AssetReadReceipts.Concat(assetReads?.Receipts ?? []).Distinct().ToArray();
        uses = BethesdaMultitool.Core.Formats.Nif.Rendering.Scene.SceneAssetUses.WithResolvedTextureReads(uses, textureResolver, reads);
        if (reads.Length > 0 || !uses.Nodes.IsEmpty)
            result.Extras = new JsonObject
            {
                ["BMT_asset_reads"] = JsonNode.Parse(BethesdaMultitool.Core.Assets.AssetSelectionJson.Serialize(reads)),
                ["BMT_asset_uses"] = JsonNode.Parse(BethesdaMultitool.Core.Assets.AssetSelectionJson.SerializeUses(uses.Bind(reads))),
                ["BMT_asset_scope"] = "composition-and-export-reads",
                ["BMT_engine_priority_verified"] = false
            };
        return result;
    }

    /// <summary>
    ///     Suppresses the same database-backed, no-albedo helper population as ReferenceMeshCache12
    ///     without applying a shape-name heuristic. Explicit non-Deferred shader routes stay in the
    ///     artifact because their lack of ordinary albedo is meaningful, not evidence of proxy/no-draw
    ///     geometry. A missing database record also stays visible as a content diagnostic.
    /// </summary>
    internal static bool ShouldSkipStarfieldNoDrawSubmesh(
        RenderableSubmesh submesh,
        NifTextureResolver textureResolver)
    {
        ArgumentNullException.ThrowIfNull(submesh);
        ArgumentNullException.ThrowIfNull(textureResolver);

        var materialPath = submesh.ShaderMetadata?.MaterialPath;
        if (string.IsNullOrWhiteSpace(materialPath) ||
            !MaterialTexturePathResolver.IsStarfieldMaterialPath(materialPath))
        {
            materialPath = submesh.DiffuseTexturePath;
        }

        if (string.IsNullOrWhiteSpace(materialPath) ||
            !MaterialTexturePathResolver.IsStarfieldMaterialPath(materialPath) ||
            !textureResolver.IsStarfieldNoDrawMaterial(materialPath))
        {
            return false;
        }

        // Null means the database could not establish a trustworthy route. Keep that geometry
        // visible as a diagnostic; only a positively resolved ordinary Deferred route authorizes
        // suppression.
        return textureResolver.ResolveStarfieldShaderRoute(materialPath) ==
               StarfieldMaterialShaderRoute.Deferred;
    }

    internal static void NormalizeWinding(RenderableSubmesh submesh)
    {
        if (submesh.Normals == null || submesh.TriangleCount == 0)
        {
            return;
        }

        GltfNormalDiagnostic.FixWindingOrder(submesh);
    }

    private static Dictionary<int, NodeBuilder> BuildNodeBuilders(GlbScene scene)
    {
        var usedNodes = CollectUsedNodeIndices(scene);
        var builders = new Dictionary<int, NodeBuilder>();
        for (var index = 0; index < scene.Nodes.Count; index++)
        {
            if (!usedNodes.Contains(index))
            {
                continue;
            }

            var node = scene.Nodes[index];
            builders[index] = node.ParentIndex is int parentIndex &&
                              builders.TryGetValue(parentIndex, out var parentBuilder)
                ? parentBuilder.CreateNode()
                : new NodeBuilder();
            builders[index].LocalMatrix = GltfCoordinateAdapter.ConvertMatrix(node.LocalTransform);
        }

        return builders;
    }

    private static HashSet<int> CollectUsedNodeIndices(GlbScene scene)
    {
        var used = new HashSet<int> { GlbScene.RootNodeIndex };

        foreach (var meshPart in scene.MeshParts)
        {
            if (meshPart.Skin != null)
            {
                foreach (var jointNodeIndex in meshPart.Skin.JointNodeIndices)
                {
                    AddNodeAndAncestors(scene, jointNodeIndex, used);
                }
            }
            else
            {
                AddNodeAndAncestors(scene, meshPart.NodeIndex ?? GlbScene.RootNodeIndex, used);
            }
        }

        return used;
    }

    private static void AddNodeAndAncestors(
        GlbScene scene,
        int nodeIndex,
        HashSet<int> used)
    {
        var current = nodeIndex;
        while (current >= 0 && used.Add(current))
        {
            var parentIndex = scene.Nodes[current].ParentIndex;
            if (!parentIndex.HasValue)
            {
                break;
            }

            current = parentIndex.Value;
        }
    }

    private static IMeshBuilder<MaterialBuilder> BuildRigidMesh(
        GlbMeshPart meshPart,
        NifTextureResolver textureResolver,
        NifGlbMaterialCache materialCache)
    {
        var vertexLerpProjection = StarfieldGlbVertexLerpProjection.Resolve(meshPart.Submesh);
        var material = GetOrCreateMaterial(
            meshPart.Submesh,
            textureResolver,
            materialCache,
            vertexLerpProjection);
        var tangents = NpcGlbTangentBuilder.BuildTangents(meshPart.Submesh);
        var meshName = AuthoredSkyGlbPreviewProjection.AppliesTo(meshPart.Submesh)
            ? meshPart.Name + AuthoredSkyGlbPreviewProjection.NameSuffix
            : meshPart.Name;

        if (vertexLerpProjection.RequiresViewerShader)
        {
            var viewerMesh =
                new MeshBuilder<VertexPositionNormalTangent, VertexColor2Texture1, VertexEmpty>(meshName);
            var viewerPrimitive = viewerMesh.UsePrimitive(material);
            for (var index = 0; index + 2 < meshPart.Submesh.Triangles.Length; index += 3)
            {
                viewerPrimitive.AddTriangle(
                    CreateRigidViewerVertex(
                        meshPart.Submesh, tangents, meshPart.Submesh.Triangles[index], vertexLerpProjection),
                    CreateRigidViewerVertex(
                        meshPart.Submesh, tangents, meshPart.Submesh.Triangles[index + 1], vertexLerpProjection),
                    CreateRigidViewerVertex(
                        meshPart.Submesh, tangents, meshPart.Submesh.Triangles[index + 2], vertexLerpProjection));
            }

            return viewerMesh;
        }

        var mesh = new MeshBuilder<VertexPositionNormalTangent, VertexColor1Texture1, VertexEmpty>(meshName);
        var primitive = mesh.UsePrimitive(material);
        for (var index = 0; index + 2 < meshPart.Submesh.Triangles.Length; index += 3)
        {
            primitive.AddTriangle(
                CreateRigidVertex(
                    meshPart.Submesh, tangents, meshPart.Submesh.Triangles[index], vertexLerpProjection),
                CreateRigidVertex(
                    meshPart.Submesh, tangents, meshPart.Submesh.Triangles[index + 1], vertexLerpProjection),
                CreateRigidVertex(
                    meshPart.Submesh, tangents, meshPart.Submesh.Triangles[index + 2], vertexLerpProjection));
        }

        return mesh;
    }

    private static IMeshBuilder<MaterialBuilder> BuildSkinnedMesh(
        GlbMeshPart meshPart,
        NifTextureResolver textureResolver,
        NifGlbMaterialCache materialCache)
    {
        var vertexLerpProjection = StarfieldGlbVertexLerpProjection.Resolve(meshPart.Submesh);
        var material = GetOrCreateMaterial(
            meshPart.Submesh,
            textureResolver,
            materialCache,
            vertexLerpProjection);
        var skin = meshPart.Skin!;
        var tangents = NpcGlbTangentBuilder.BuildTangents(meshPart.Submesh);
        var meshName = AuthoredSkyGlbPreviewProjection.AppliesTo(meshPart.Submesh)
            ? meshPart.Name + AuthoredSkyGlbPreviewProjection.NameSuffix
            : meshPart.Name;

        if (vertexLerpProjection.RequiresViewerShader)
        {
            var viewerMesh =
                new MeshBuilder<VertexPositionNormalTangent, VertexColor2Texture1, VertexJoints4>(meshName);
            var viewerPrimitive = viewerMesh.UsePrimitive(material);
            for (var index = 0; index + 2 < meshPart.Submesh.Triangles.Length; index += 3)
            {
                viewerPrimitive.AddTriangle(
                    CreateSkinnedViewerVertex(
                        meshPart.Submesh, tangents, skin, meshPart.Submesh.Triangles[index], vertexLerpProjection),
                    CreateSkinnedViewerVertex(
                        meshPart.Submesh, tangents, skin, meshPart.Submesh.Triangles[index + 1], vertexLerpProjection),
                    CreateSkinnedViewerVertex(
                        meshPart.Submesh, tangents, skin, meshPart.Submesh.Triangles[index + 2], vertexLerpProjection));
            }

            return viewerMesh;
        }

        var mesh = new MeshBuilder<VertexPositionNormalTangent, VertexColor1Texture1, VertexJoints4>(meshName);
        var primitive = mesh.UsePrimitive(material);
        for (var index = 0; index + 2 < meshPart.Submesh.Triangles.Length; index += 3)
        {
            primitive.AddTriangle(
                CreateSkinnedVertex(
                    meshPart.Submesh, tangents, skin, meshPart.Submesh.Triangles[index], vertexLerpProjection),
                CreateSkinnedVertex(
                    meshPart.Submesh, tangents, skin, meshPart.Submesh.Triangles[index + 1], vertexLerpProjection),
                CreateSkinnedVertex(
                    meshPart.Submesh, tangents, skin, meshPart.Submesh.Triangles[index + 2], vertexLerpProjection));
        }

        return mesh;
    }

    private static (VertexPositionNormalTangent Geometry, VertexColor1Texture1 Material) CreateRigidVertex(
        RenderableSubmesh submesh,
        Vector4[]? tangents,
        int vertexIndex,
        StarfieldGlbVertexLerpProjectionResult vertexLerpProjection)
    {
        return (
            new VertexPositionNormalTangent(
                ReadPosition(submesh, vertexIndex),
                ReadNormal(submesh, vertexIndex),
                ReadTangent(submesh, tangents, vertexIndex)),
            new VertexColor1Texture1(
                ReadVertexColor(submesh, vertexIndex, vertexLerpProjection),
                ReadUv(submesh, vertexIndex)));
    }

    private static (VertexPositionNormalTangent Geometry, VertexColor2Texture1 Material) CreateRigidViewerVertex(
        RenderableSubmesh submesh,
        Vector4[]? tangents,
        int vertexIndex,
        StarfieldGlbVertexLerpProjectionResult vertexLerpProjection)
    {
        return (
            new VertexPositionNormalTangent(
                ReadPosition(submesh, vertexIndex),
                ReadNormal(submesh, vertexIndex),
                ReadTangent(submesh, tangents, vertexIndex)),
            new VertexColor2Texture1(
                ReadVertexColor(submesh, vertexIndex, vertexLerpProjection),
                ReadViewerVertexLerpColor(submesh, vertexIndex, vertexLerpProjection),
                ReadUv(submesh, vertexIndex)));
    }

    private static VertexBuilder<VertexPositionNormalTangent, VertexColor1Texture1, VertexJoints4> CreateSkinnedVertex(
        RenderableSubmesh submesh,
        Vector4[]? tangents,
        GlbSkinBinding skin,
        int vertexIndex,
        StarfieldGlbVertexLerpProjectionResult vertexLerpProjection)
    {
        var bindings = skin.PerVertexInfluences[vertexIndex];
        var joints = bindings.Length > 0
            ? new VertexJoints4(bindings)
            : new VertexJoints4((0, 1f));

        return new VertexBuilder<VertexPositionNormalTangent, VertexColor1Texture1, VertexJoints4>(
            new VertexPositionNormalTangent(
                ReadPosition(submesh, vertexIndex),
                ReadNormal(submesh, vertexIndex),
                ReadTangent(submesh, tangents, vertexIndex)),
            new VertexColor1Texture1(
                ReadVertexColor(submesh, vertexIndex, vertexLerpProjection),
                ReadUv(submesh, vertexIndex)),
            joints);
    }

    private static VertexBuilder<VertexPositionNormalTangent, VertexColor2Texture1, VertexJoints4>
        CreateSkinnedViewerVertex(
            RenderableSubmesh submesh,
            Vector4[]? tangents,
            GlbSkinBinding skin,
            int vertexIndex,
            StarfieldGlbVertexLerpProjectionResult vertexLerpProjection)
    {
        var bindings = skin.PerVertexInfluences[vertexIndex];
        var joints = bindings.Length > 0
            ? new VertexJoints4(bindings)
            : new VertexJoints4((0, 1f));

        return new VertexBuilder<VertexPositionNormalTangent, VertexColor2Texture1, VertexJoints4>(
            new VertexPositionNormalTangent(
                ReadPosition(submesh, vertexIndex),
                ReadNormal(submesh, vertexIndex),
                ReadTangent(submesh, tangents, vertexIndex)),
            new VertexColor2Texture1(
                ReadVertexColor(submesh, vertexIndex, vertexLerpProjection),
                ReadViewerVertexLerpColor(submesh, vertexIndex, vertexLerpProjection),
                ReadUv(submesh, vertexIndex)),
            joints);
    }

    private static Vector3 ReadPosition(RenderableSubmesh submesh, int vertexIndex)
    {
        var offset = vertexIndex * 3;
        return GltfCoordinateAdapter.ConvertPosition(new Vector3(
            submesh.Positions[offset],
            submesh.Positions[offset + 1],
            submesh.Positions[offset + 2]));
    }

    internal static Vector3 ReadNormal(RenderableSubmesh submesh, int vertexIndex)
    {
        if (submesh.Normals == null)
        {
            return Vector3.UnitY;
        }

        var offset = vertexIndex * 3;
        var normal = new Vector3(
            submesh.Normals[offset],
            submesh.Normals[offset + 1],
            submesh.Normals[offset + 2]);
        return normal.LengthSquared() > 0.0001f
            ? GltfCoordinateAdapter.ConvertDirection(Vector3.Normalize(normal))
            : Vector3.UnitY;
    }

    private static Vector2 ReadUv(RenderableSubmesh submesh, int vertexIndex)
    {
        if (submesh.UVs == null)
        {
            return Vector2.Zero;
        }

        var offset = vertexIndex * 2;
        return new Vector2(submesh.UVs[offset], submesh.UVs[offset + 1]);
    }

    /// <summary>Applies the same tangent basis and fallback to legacy and normalized material consumers.</summary>
    internal static Vector4 ReadTangent(
        RenderableSubmesh submesh,
        Vector4[]? tangents,
        int vertexIndex)
    {
        if (tangents != null && vertexIndex >= 0 && vertexIndex < tangents.Length)
        {
            var tangent = tangents[vertexIndex];
            var direction = new Vector3(tangent.X, tangent.Y, tangent.Z);
            direction = direction.LengthSquared() > 0.0001f
                ? GltfCoordinateAdapter.ConvertDirection(Vector3.Normalize(direction))
                : Vector3.UnitX;
            return new Vector4(direction, tangent.W is 0f ? 1f : tangent.W);
        }

        var normal = ReadNormal(submesh, vertexIndex);
        var axis = MathF.Abs(normal.Y) < 0.999f
            ? Vector3.UnitY
            : Vector3.UnitX;
        var tangentDir = Vector3.Normalize(Vector3.Cross(axis, normal));
        return new Vector4(tangentDir, 1f);
    }

    /// <summary>Applies the existing material-dependent vertex-color projection for either output path.</summary>
    internal static Vector4 ReadVertexColor(
        RenderableSubmesh submesh,
        int vertexIndex,
        StarfieldGlbVertexLerpProjectionResult vertexLerpProjection)
    {
        if (AuthoredSkyGlbPreviewProjection.TryBuildVertexColor(
                submesh,
                vertexIndex,
                out var authoredSkyPreview))
        {
            return authoredSkyPreview;
        }

        if (StarfieldGlbVertexLerpProjection.TryBuildVertexColor(
                submesh,
                vertexIndex,
                vertexLerpProjection,
                out var projected))
        {
            return projected;
        }

        return NpcGlbTintColorEncoder.BuildVertexColor(submesh, vertexIndex);
    }

    private static Vector4 ReadViewerVertexLerpColor(
        RenderableSubmesh submesh,
        int vertexIndex,
        StarfieldGlbVertexLerpProjectionResult vertexLerpProjection)
    {
        if (StarfieldGlbVertexLerpProjection.TryBuildViewerVertexLerpColor(
                submesh,
                vertexIndex,
                vertexLerpProjection,
                out var color))
        {
            return color;
        }

        throw new InvalidDataException(
            $"Mesh '{submesh.ShapeName ?? "unnamed"}' was classified for exact Mesh Viewer vertex Lerp " +
            $"but vertex {vertexIndex} has no complete CE2 RGBA value.");
    }

    /// <summary>Translates a cached prepared surface without reinterpreting Bethesda material inputs.</summary>
    private static MaterialBuilder GetOrCreateMaterial(
        RenderableSubmesh submesh,
        NifTextureResolver textureResolver,
        NifGlbMaterialCache materialCache,
        StarfieldGlbVertexLerpProjectionResult vertexLerpProjection)
    {
        var prepared = NifMaterialPreparation.Prepare(submesh, textureResolver, materialCache.Prepared, vertexLerpProjection);
        if (materialCache.Materials.TryGetValue(prepared.Key, out var material)) return material;
        material = new MaterialBuilder(prepared.Name) { Extras = prepared.Extras?.DeepClone() };
        if (prepared.Unlit) material.WithUnlitShader();
        else
        {
            material.WithMetallicRoughnessShader();
            material.WithMetallicRoughness(prepared.MetallicFactor, prepared.RoughnessFactor);
        }
        material.WithDoubleSide(prepared.DoubleSided);
        var images = new Dictionary<NifPreparedImage, ImageBuilder>(ReferenceEqualityComparer.Instance);
        if (prepared.BaseColorImage is { } baseColor) material.WithBaseColor(Image(baseColor, images), prepared.BaseColor);
        else material.WithBaseColor(prepared.BaseColor);
        if (prepared.NormalImage is { } normal) material.WithNormal(Image(normal, images), prepared.NormalScale);
        if (prepared.MetallicRoughnessImage is { } orm)
            material.WithMetallicRoughness(Image(orm, images), prepared.MetallicFactor, prepared.RoughnessFactor);
        if (prepared.SpecularImage is { } specular) material.WithSpecularFactor(Image(specular, images), prepared.SpecularFactor);
        if (prepared.OcclusionImage is { } occlusion) material.WithOcclusion(Image(occlusion, images), prepared.OcclusionStrength);
        if (prepared.EmissiveImage is { } emissive)
            material.WithEmissive(Image(emissive, images), prepared.EmissiveFactor, prepared.EmissiveStrength);
        else if (prepared.HasEmission) material.WithEmissive(prepared.EmissiveFactor, prepared.EmissiveStrength);
        if (prepared.IndexOfRefraction is { } ior) material.IndexOfRefraction = ior;
        if (prepared.Transmission is { } transmission) material.WithTransmission(null, transmission);
        if (prepared.ClearCoat is { } clearCoat) material.WithClearCoat(null, clearCoat);
        if (prepared.ClearCoatRoughness is { } clearCoatRoughness) material.WithClearCoatRoughness(null, clearCoatRoughness);
        if (prepared.AlphaMode == Slfx77.Multitool.Core.Models.SceneAlphaMode.Blend) material.WithAlpha(AlphaMode.BLEND);
        else if (prepared.AlphaMode == Slfx77.Multitool.Core.Models.SceneAlphaMode.Mask)
            material.WithAlpha(AlphaMode.MASK, prepared.AlphaCutoff);
        ConfigureSamplers(material, prepared.ClampU, prepared.ClampV);
        materialCache.Materials[prepared.Key] = material;
        return material;
    }

    /// <summary>Reuses a prepared image shared by several material channels without identifying it by its label.</summary>
    private static ImageBuilder Image(NifPreparedImage prepared, Dictionary<NifPreparedImage, ImageBuilder> images)
    {
        if (!images.TryGetValue(prepared, out var image))
        {
            image = ImageBuilder.From(new MemoryImage(prepared.Png), prepared.Name);
            images.Add(prepared, image);
        }
        return image;
    }

    /// <summary>
    ///     Set authored BGSM/BGEM U/V addressing + non-mipmapped LINEAR minification on every
    ///     texture channel. Non-material NIFs retain REPEAT on both axes.
    ///     Without this, glTF viewers default to LINEAR_MIPMAP_LINEAR; on heavily-tiled
    ///     content (e.g. tree bark with V ≈ −18 → −1) the GPU's screen-space derivative
    ///     of UV crosses each integer boundary and snaps to the coarsest mip, producing
    ///     evenly-spaced dark bands along the seam. NIFSkope doesn't hit this because its
    ///     software sampler doesn't pick mip level from derivatives.
    /// </summary>
    private static void ConfigureSamplers(MaterialBuilder material, bool clampU, bool clampV)
    {
        var wrapU = clampU ? TextureWrapMode.CLAMP_TO_EDGE : TextureWrapMode.REPEAT;
        var wrapV = clampV ? TextureWrapMode.CLAMP_TO_EDGE : TextureWrapMode.REPEAT;
        foreach (var channel in material.Channels)
        {
            channel.Texture?.WithSampler(
                wrapU,
                wrapV,
                TextureMipMapFilter.LINEAR,
                TextureInterpolationFilter.LINEAR);
        }
    }

    /// <summary>Retains the existing strict-greater alpha helper entry point.</summary>
    internal static float ToGltfGreaterCutoff(float threshold) => NifMaterialPreparation.ToGltfGreaterCutoff(threshold);

    /// <summary>Retains the existing external-BGSM classification entry point.</summary>
    internal static bool HasExternalRegularBgsmMaterial(RenderableSubmesh submesh) => NifMaterialPreparation.HasExternalRegularBgsmMaterial(submesh);

    /// <summary>Retains the existing bounded factor and HDR-strength emission entry point.</summary>
    internal static bool TryEncodeGltfEmission(Vector3 effectiveEmission, out Vector3 emissiveFactor, out float emissiveStrength) =>
        NifMaterialPreparation.TryEncodeGltfEmission(effectiveEmission, out emissiveFactor, out emissiveStrength);
}
