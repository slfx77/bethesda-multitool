using System.Numerics;
using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Formats.Dds;
using BethesdaMultitool.Core.Formats.Nif.Materials;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Materials;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Water;
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

        var sceneBuilder = new SceneBuilder();
        var nodeBuilders = BuildNodeBuilders(scene);
        var materialCache = new Dictionary<MaterialCacheKey, MaterialBuilder>();

        foreach (var meshPart in scene.MeshParts)
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

        return sceneBuilder.ToGltf2();
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

    private static void NormalizeWinding(RenderableSubmesh submesh)
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
                ? parentBuilder.CreateNode(node.Name)
                : new NodeBuilder(node.Name);
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
        Dictionary<MaterialCacheKey, MaterialBuilder> materialCache)
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
        Dictionary<MaterialCacheKey, MaterialBuilder> materialCache)
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

    private static Vector3 ReadNormal(RenderableSubmesh submesh, int vertexIndex)
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

    private static Vector4 ReadTangent(
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

    private static Vector4 ReadVertexColor(
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

    private static MaterialBuilder GetOrCreateMaterial(
        RenderableSubmesh submesh,
        NifTextureResolver textureResolver,
        Dictionary<MaterialCacheKey, MaterialBuilder> materialCache,
        StarfieldGlbVertexLerpProjectionResult vertexLerpProjection)
    {
        var isStarfieldWater = string.Equals(
            submesh.DiffuseTexturePath,
            RenderableSubmesh.WaterSurfaceTexturePath,
            StringComparison.Ordinal);
        var authoredSkyPreview = AuthoredSkyGlbPreviewProjection.AppliesTo(submesh);
        var diffuseTexture = !authoredSkyPreview &&
                             !isStarfieldWater &&
                             !string.IsNullOrWhiteSpace(submesh.DiffuseTexturePath)
            ? textureResolver.GetTexture(submesh.DiffuseTexturePath!)
            : null;
        diffuseTexture = NpcGlbTintColorEncoder.BakeDiffuseTexture(submesh, diffuseTexture);
        if (vertexLerpProjection.OmitDiffuseTexture)
        {
            // With weight one CE2 returns interpolated vertex RGB exactly; retaining the albedo
            // image would make core glTF multiply it back in and change the authored result.
            diffuseTexture = null;
        }

        var starfieldColor = ResolveStarfieldColor(
            submesh,
            textureResolver,
            vertexLerpProjection);
        diffuseTexture = StarfieldGlbColorLerpBaker.BakeDiffuseTexture(diffuseTexture, starfieldColor);
        // Preserve whether Lerp was actually baked into authored RGB. AlphaSettings may synthesize
        // a white base texture later; that texture still needs the no-albedo Lerp factor rather than
        // being mistaken for already-baked colour.
        var starfieldLerpBakedIntoTexture = diffuseTexture is not null;
        var (starfieldAlpha, starfieldMaterialPath) = ResolveStarfieldAlpha(submesh, textureResolver);
        var starfieldEffectPolicy = !isStarfieldWater && starfieldMaterialPath is not null
            ? textureResolver.ResolveStarfieldEffectPolicy(starfieldMaterialPath)
            : default;
        var hasStaticStarfieldEffectAlpha =
            starfieldEffectPolicy.TryResolveStaticGlassAlphaBlend(out var starfieldEffectAlpha);
        var effectOpacityTexture = hasStaticStarfieldEffectAlpha &&
                                   starfieldEffectAlpha.OpacitySlot.TexturePath is { Length: > 0 } effectOpacityPath
            ? textureResolver.GetTexture(effectOpacityPath)
            : null;
        var effectAlphaBake = hasStaticStarfieldEffectAlpha
            ? StarfieldGlbOpacityBaker.BakeEffectAlpha(
                diffuseTexture,
                effectOpacityTexture,
                starfieldEffectAlpha)
            : new StarfieldGlbEffectAlphaBakeResult(diffuseTexture, false, 1f);
        hasStaticStarfieldEffectAlpha &= effectAlphaBake.Applied;

        var opacityTexture = !hasStaticStarfieldEffectAlpha &&
                             starfieldAlpha.IsLayer0OpacityCutout &&
                             starfieldMaterialPath is not null
            ? textureResolver.GetTexture(
                MaterialTexturePathResolver.BuildStarfieldOpacityMapRequest(starfieldMaterialPath))
            : null;
        var opacityBake = !hasStaticStarfieldEffectAlpha && starfieldAlpha.IsLayer0OpacityCutout
            ? StarfieldGlbOpacityBaker.Bake(diffuseTexture, opacityTexture)
            : new StarfieldGlbOpacityBakeResult(diffuseTexture, false);
        diffuseTexture = hasStaticStarfieldEffectAlpha
            ? effectAlphaBake.Texture
            : opacityBake.Texture;
        NpcGlbAlphaTexturePacker.PreparedAlphaTexture preparedAlpha;
        if (isStarfieldWater || authoredSkyPreview)
        {
            // KHR_materials_transmission models optical transparency; core alpha is geometric
            // coverage. A water sheet fully covers its triangles, so keep alpha OPAQUE/one instead
            // of multiplying the physical transmission by the old half-alpha visibility fallback.
            preparedAlpha = new NpcGlbAlphaTexturePacker.PreparedAlphaTexture(
                null,
                NifAlphaRenderMode.Opaque,
                0,
                false);
        }
        else if (hasStaticStarfieldEffectAlpha)
        {
            preparedAlpha = new NpcGlbAlphaTexturePacker.PreparedAlphaTexture(
                diffuseTexture,
                NifAlphaRenderMode.Blend,
                0,
                false);
        }
        else if (starfieldAlpha.IsLayer0OpacityCutout)
        {
            // PreparedAlphaTexture's byte threshold is the legacy NIF lane. Starfield keeps its
            // authored float below so LINEAR-filtered opacity is not quantized at the silhouette.
            preparedAlpha = new NpcGlbAlphaTexturePacker.PreparedAlphaTexture(
                diffuseTexture,
                opacityBake.Applied ? NifAlphaRenderMode.Cutout : NifAlphaRenderMode.Opaque,
                0,
                false);
        }
        else
        {
            preparedAlpha = NpcGlbAlphaTexturePacker.Prepare(submesh, diffuseTexture);
        }
        // A standalone Starfield water NIF has no WATR record from which to select authored noise
        // layers. Reuse only the shipped primary global normal already named by the source-backed
        // World Viewer approximation; portable GLB viewers receive it statically and the embedded
        // viewer scrolls it under an explicit approximation marker.
        var normalTexturePath = authoredSkyPreview
            ? null
            : isStarfieldWater
            ? StarfieldWaterMaterialRoute.MeshViewerPrimaryNormalTexturePath
            : submesh.NormalMapTexturePath;
        var packedNormal = NpcGlbNormalMapPacker.ResolvePacked(textureResolver, normalTexturePath);
        var normalTexture = packedNormal.Texture;
        var shaderMetadata = submesh.ShaderMetadata;
        var starfieldOrmPolicy = !isStarfieldWater && starfieldMaterialPath is not null
            ? textureResolver.ResolveStarfieldOrmPolicy(starfieldMaterialPath)
            : default;
        var starfieldOrmState = default(StarfieldMaterialOrmState);
        var hasStaticStarfieldOrm = !isStarfieldWater &&
                                    !submesh.IsEmissive &&
                                    starfieldOrmPolicy.TryResolveStaticLayer0Orm(out starfieldOrmState);
        var starfieldOrm = hasStaticStarfieldOrm
            ? StarfieldGlbOrmPacker.Pack(
                starfieldOrmState,
                LoadStarfieldSlotTexture(textureResolver, starfieldOrmState.RoughnessSlot),
                LoadStarfieldSlotTexture(textureResolver, starfieldOrmState.MetalnessSlot),
                LoadStarfieldSlotTexture(textureResolver, starfieldOrmState.AmbientOcclusionSlot))
            : default;
        var bgsmEmissiveFactor = Vector3.One;
        var bgsmEmissiveStrength = 1f;
        var hasActiveBgsmEmission = !isStarfieldWater && TryEncodeGltfEmission(
            submesh.BgsmEmissionColor,
            out bgsmEmissiveFactor,
            out bgsmEmissiveStrength);
        var bgsmGlowTexture = hasActiveBgsmEmission &&
                              !string.IsNullOrWhiteSpace(submesh.BgsmGlowMapTexturePath)
            ? textureResolver.GetTexture(submesh.BgsmGlowMapTexturePath)
            : null;
        var hasExternalRegularBgsm = !isStarfieldWater && HasExternalRegularBgsmMaterial(submesh);
        var glowTexture = !isStarfieldWater &&
                          !string.IsNullOrWhiteSpace(shaderMetadata?.GlowMapPath)
            ? textureResolver.GetTexture(shaderMetadata.GlowMapPath)
            : null;
        var inlineEmissiveTexture = !isStarfieldWater &&
                                    !hasExternalRegularBgsm &&
                                    NpcGlbMaterialChannelDecider.ShouldExportGlowAsEmissive(
                                        submesh,
                                        shaderMetadata)
            ? glowTexture
            : null;
        // A regular BGSM owns glow enablement as well as its texture and colour/scale. Even an
        // inactive or malformed external material must not resurrect a stale inline slot-2 map.
        var emissiveTexture = hasActiveBgsmEmission ? bgsmGlowTexture : inlineEmissiveTexture;
        var emissiveTexturePath = hasActiveBgsmEmission
            ? submesh.BgsmGlowMapTexturePath
            : inlineEmissiveTexture != null ? shaderMetadata?.GlowMapPath : null;
        var emissiveFactor = hasActiveBgsmEmission ? bgsmEmissiveFactor : Vector3.One;
        var emissiveStrength = hasActiveBgsmEmission ? bgsmEmissiveStrength : 1f;
        var heightTexture = !isStarfieldWater &&
                            !string.IsNullOrWhiteSpace(shaderMetadata?.HeightMapPath)
            ? textureResolver.GetTexture(shaderMetadata.HeightMapPath)
            : null;
        var environmentMaskTexture = !isStarfieldWater &&
                                     !string.IsNullOrWhiteSpace(shaderMetadata?.EnvironmentMaskPath)
            ? textureResolver.GetTexture(shaderMetadata.EnvironmentMaskPath)
            : null;
        var baseColor = authoredSkyPreview || isStarfieldWater
            ? Vector4.One
            : StarfieldGlbColorLerpBaker.BuildBaseColor(
                NpcGlbTintColorEncoder.BuildBaseColor(submesh, preparedAlpha.Texture != null),
                starfieldLerpBakedIntoTexture,
                starfieldColor);
        if (hasStaticStarfieldEffectAlpha)
        {
            // CE2 EffectSettings owns this alpha; inline NIF material alpha is not an additional
            // multiplier on the reference shader's Effect route.
            baseColor.W = effectAlphaBake.AlphaFactor;
        }

        var hasEnvironmentMapping = !isStarfieldWater &&
                                    NpcGlbMaterialTuning.HasEnvironmentMapping(submesh);
        var materialProfile = isStarfieldWater
            ? default
            : NpcGlbMaterialTuning.Derive(submesh, normalTexture, packedNormal.HasGlossAlpha);
        // Selecting the strict CE2 ORM lane is a one-way decision. If an authored image is missing
        // or dimensions disagree, emit neutral CE2 constructor factors and no ORM image; never fall
        // through to the legacy NPC normal-alpha/environment/height heuristics, which interpret
        // unrelated Starfield channels as plausible-looking gloss/specular/AO.
        var metallicFactor = authoredSkyPreview
            ? 0f
            : isStarfieldWater
            ? 0f
            : hasStaticStarfieldOrm
            ? starfieldOrm.Applied ? starfieldOrm.MetallicFactor : 0f
            : materialProfile.MetallicFactor;
        var roughnessFactor = authoredSkyPreview
            ? 1f
            : isStarfieldWater
            ? StarfieldWaterMaterialRoute.MeshViewerRoughness
            : hasStaticStarfieldOrm
            ? starfieldOrm.Applied ? starfieldOrm.RoughnessFactor : 0f
            : materialProfile.RoughnessFactor;
        var (clampTextureU, clampTextureV) = isStarfieldWater ||
                                             hasStaticStarfieldOrm ||
                                             hasStaticStarfieldEffectAlpha
            ? (false, false)
            : ResolveTextureAddressing(submesh, textureResolver);
        var alphaCutoff = opacityBake.Applied && starfieldAlpha.IsLayer0OpacityCutout
            ? ToGltfGreaterCutoff(starfieldAlpha.AlphaTestThreshold)
            : preparedAlpha.AlphaThreshold / 255f;
        var key = new MaterialCacheKey(
            submesh.DiffuseTexturePath,
            normalTexturePath,
            emissiveTexturePath,
            hasActiveBgsmEmission,
            emissiveFactor,
            emissiveStrength,
            isStarfieldWater ? null : shaderMetadata?.HeightMapPath,
            isStarfieldWater ? null : shaderMetadata?.EnvironmentMaskPath,
            submesh.IsEmissive && !isStarfieldWater,
            isStarfieldWater,
            authoredSkyPreview,
            submesh.UseVertexColors,
            submesh.IsDoubleSided || isStarfieldWater,
            preparedAlpha.RenderMode,
            preparedAlpha.AlphaThreshold,
            isStarfieldWater ? (byte)0 : submesh.AlphaTestFunction,
            preparedAlpha.HasTextureTransform,
            clampTextureU,
            clampTextureV,
            metallicFactor,
            roughnessFactor,
            materialProfile.SpecularFactor,
            starfieldColor,
            isStarfieldWater ? null : starfieldMaterialPath,
            starfieldAlpha,
            opacityBake.Applied,
            starfieldEffectPolicy,
            hasStaticStarfieldEffectAlpha,
            starfieldOrmPolicy,
            hasStaticStarfieldOrm,
            starfieldOrm.Applied,
            starfieldOrm.Texture is not null,
            starfieldOrm.HasAmbientOcclusion,
            vertexLerpProjection,
            baseColor);

        if (materialCache.TryGetValue(key, out var material))
        {
            return material;
        }

        var materialName = submesh.ShapeName ?? "material";
        if (vertexLerpProjection.IsUnsupported)
        {
            // Keep malformed source data visible in the artifact. It must never receive the viewer
            // marker because the shader hook cannot recover a missing or incomplete RGBA stream.
            materialName += " [CE2 vertex Lerp omitted: missing or incomplete RGBA stream]";
        }
        else if (vertexLerpProjection.RequiresViewerShader)
        {
            materialName += " [CE2 varying vertex Lerp: exact in embedded Mesh Viewer; portable base fallback]";
        }

        if (starfieldEffectPolicy.IsResolved &&
            starfieldEffectPolicy.HasEffectSettings &&
            starfieldEffectPolicy.IsGlass &&
            !hasStaticStarfieldEffectAlpha)
        {
            materialName += " [CE2 glass alpha omitted: unsupported effect composition or missing opacity]";
        }

        if (authoredSkyPreview)
        {
            materialName += AuthoredSkyGlbPreviewProjection.NameSuffix;
        }

        material = new MaterialBuilder(materialName);
        var materialExtras = new JsonObject();
        if (vertexLerpProjection.RequiresViewerShader)
        {
            materialExtras[StarfieldGlbVertexLerpProjection.ViewerMaterialExtrasKey] = true;
        }

        if (isStarfieldWater)
        {
            materialName += " [CE2 water: global-normal physical preview (approx.)]";
            material.Name = materialName;
            materialExtras[StarfieldWaterMaterialRoute.MeshViewerMaterialExtrasKey] = true;
        }

        if (materialExtras.Count > 0)
        {
            material.Extras = materialExtras;
        }

        if ((!isStarfieldWater && submesh.IsEmissive) || authoredSkyPreview)
        {
            material.WithUnlitShader();
        }
        else
        {
            material.WithMetallicRoughnessShader();
            material.WithMetallicRoughness(
                metallicFactor,
                roughnessFactor);
        }

        if (isStarfieldWater)
        {
            // Standard glTF physical channels make the portable artifact reflective/transmissive
            // instead of an opaque white slab. These are deliberately neutral water optics, not a
            // claim that CE2's Water DXIL, CUR3, or material constants have been recovered.
            material.IndexOfRefraction = StarfieldWaterMaterialRoute.MeshViewerIndexOfRefraction;
            material.WithTransmission(null, StarfieldWaterMaterialRoute.MeshViewerTransmission);
            material.WithClearCoat(null, StarfieldWaterMaterialRoute.MeshViewerClearCoat);
            material.WithClearCoatRoughness(
                null,
                StarfieldWaterMaterialRoute.MeshViewerClearCoatRoughness);
        }

        // Generic alpha-blended materials stay single-sided to avoid unsorted back faces over front
        // faces. Authored sky domes and thin water sheets must remain visible from either side.
        material.WithDoubleSide(
            authoredSkyPreview || isStarfieldWater ||
            (submesh.IsDoubleSided &&
             preparedAlpha.RenderMode != NifAlphaRenderMode.Blend &&
             preparedAlpha.RenderMode != NifAlphaRenderMode.AlphaToCoverage));
        if (preparedAlpha.Texture != null)
        {
            var imageName = BuildBaseColorTextureName(
                submesh.DiffuseTexturePath,
                preparedAlpha.HasTextureTransform,
                starfieldColor.IsConstantLerp,
                opacityBake.Applied || hasStaticStarfieldEffectAlpha);
            var image = ImageBuilder.From(
                new MemoryImage(NpcGlbTextureEncoder.EncodePng(preparedAlpha.Texture)),
                imageName);
            material.WithBaseColor(image, baseColor);
        }
        else
        {
            material.WithBaseColor(baseColor);
        }

        if ((isStarfieldWater || !submesh.IsEmissive) && normalTexture != null)
        {
            var image = ImageBuilder.From(
                new MemoryImage(NpcGlbTextureEncoder.EncodePng(normalTexture)),
                BuildDerivedTextureName(normalTexturePath, "normal"));
            material.WithNormal(image);

            if (!isStarfieldWater && !hasStaticStarfieldOrm)
            {
                var metallicRoughnessTexture = NpcGlbMaterialTexturePacker.BuildMetallicRoughnessTexture(
                    normalTexture,
                    packedNormal.HasGlossAlpha,
                    environmentMaskTexture,
                    hasEnvironmentMapping);
                if (metallicRoughnessTexture != null)
                {
                    var metallicRoughnessImage = ImageBuilder.From(
                        new MemoryImage(NpcGlbTextureEncoder.EncodePng(metallicRoughnessTexture)),
                        BuildDerivedTextureName(submesh.NormalMapTexturePath, "metallicRoughness"));
                    material.WithMetallicRoughness(
                        metallicRoughnessImage,
                        materialProfile.MetallicFactor,
                        materialProfile.RoughnessFactor);
                }
            }

            if (!isStarfieldWater && !hasStaticStarfieldOrm)
            {
                var specularFactorTexture = NpcGlbMaterialTexturePacker.BuildSpecularFactorTexture(
                    normalTexture,
                    packedNormal.HasGlossAlpha,
                    environmentMaskTexture,
                    hasEnvironmentMapping);
                if (specularFactorTexture != null)
                {
                    var specularFactorImage = ImageBuilder.From(
                        new MemoryImage(NpcGlbTextureEncoder.EncodePng(specularFactorTexture)),
                        BuildDerivedTextureName(submesh.NormalMapTexturePath, "specular"));
                    material.WithSpecularFactor(specularFactorImage, materialProfile.SpecularFactor);
                }
            }
        }
        else if (!isStarfieldWater &&
                 !submesh.IsEmissive &&
                 environmentMaskTexture != null &&
                 hasEnvironmentMapping)
        {
            if (!hasStaticStarfieldOrm)
            {
                var metallicRoughnessTexture = NpcGlbMaterialTexturePacker.BuildMetallicRoughnessTexture(
                    null,
                    false,
                    environmentMaskTexture,
                    hasEnvironmentMapping);
                if (metallicRoughnessTexture != null)
                {
                    var metallicRoughnessImage = ImageBuilder.From(
                        new MemoryImage(NpcGlbTextureEncoder.EncodePng(metallicRoughnessTexture)),
                        BuildDerivedTextureName(shaderMetadata?.EnvironmentMaskPath, "metallicRoughness"));
                    material.WithMetallicRoughness(
                        metallicRoughnessImage,
                        materialProfile.MetallicFactor,
                        materialProfile.RoughnessFactor);
                }
            }

            if (!hasStaticStarfieldOrm)
            {
                var specularFactorTexture = NpcGlbMaterialTexturePacker.BuildSpecularFactorTexture(
                    null,
                    false,
                    environmentMaskTexture,
                    hasEnvironmentMapping);
                if (specularFactorTexture != null)
                {
                    var specularFactorImage = ImageBuilder.From(
                        new MemoryImage(NpcGlbTextureEncoder.EncodePng(specularFactorTexture)),
                        BuildDerivedTextureName(shaderMetadata?.EnvironmentMaskPath, "specular"));
                    material.WithSpecularFactor(specularFactorImage, materialProfile.SpecularFactor);
                }
            }
        }

        if (!isStarfieldWater &&
            !submesh.IsEmissive &&
            starfieldOrm.Applied &&
            starfieldOrm.Texture is { } ormTexture)
        {
            var ormImage = ImageBuilder.From(
                new MemoryImage(NpcGlbTextureEncoder.EncodePng(ormTexture)),
                BuildDerivedTextureName(starfieldMaterialPath, "starfieldOrm"));
            material.WithMetallicRoughness(
                ormImage,
                starfieldOrm.MetallicFactor,
                starfieldOrm.RoughnessFactor);
            if (starfieldOrm.HasAmbientOcclusion)
            {
                // glTF reads occlusion from R and metallic-roughness from B/G, so one packed image
                // can back both texture slots without changing CE2's individual red-channel values.
                material.WithOcclusion(ormImage, 1f);
            }
        }

        if (!isStarfieldWater && !submesh.IsEmissive && emissiveTexture != null)
        {
            var emissiveImage = ImageBuilder.From(
                new MemoryImage(NpcGlbTextureEncoder.EncodePng(emissiveTexture)),
                BuildDerivedTextureName(emissiveTexturePath, "emissive"));
            material.WithEmissive(emissiveImage, emissiveFactor, emissiveStrength);
        }
        else if (!isStarfieldWater && !submesh.IsEmissive && hasActiveBgsmEmission)
        {
            // BGSM can carry a lit constant emission with no glow map. An authored map that cannot
            // be resolved follows the renderer's white-texture fallback and therefore reaches the
            // same constant term. Keep both in glTF emissive, never the legacy unlit route.
            material.WithEmissive(emissiveFactor, emissiveStrength);
        }

        if (!isStarfieldWater &&
            !submesh.IsEmissive &&
            heightTexture != null &&
            !hasStaticStarfieldOrm)
        {
            var occlusionTexture = NpcGlbMaterialTexturePacker.BuildOcclusionTexture(heightTexture);
            if (occlusionTexture != null)
            {
                var occlusionImage = ImageBuilder.From(
                    new MemoryImage(NpcGlbTextureEncoder.EncodePng(occlusionTexture)),
                    BuildDerivedTextureName(shaderMetadata?.HeightMapPath, "occlusion"));
                material.WithOcclusion(occlusionImage, 0.35f);
            }
        }

        switch (preparedAlpha.RenderMode)
        {
            case NifAlphaRenderMode.Blend:
                material.WithAlpha(AlphaMode.BLEND);
                break;
            case NifAlphaRenderMode.Cutout:
                material.WithAlpha(AlphaMode.MASK, alphaCutoff);
                break;
            case NifAlphaRenderMode.AlphaToCoverage:
                // glTF has no native A2C; map to pure BLEND. The earlier two-primitive
                // MASK depth-prepass + BLEND color pass approximation gave correct
                // depth-write occlusion at hair-card intersections but produced a hard
                // visible boundary between the MASK opaque core and the BLEND soft halo.
                // Pure BLEND is the cleaner trade-off — soft strand-aligned silhouettes
                // matching the rasterizer's appearance, at the cost of z-fighting where
                // hair cards genuinely intersect. The
                // rasterizer's A2C stochastic dither stays in place for the WinUI
                // viewer; this is the GLB-export branch only.
                material.WithAlpha(AlphaMode.BLEND);
                break;
        }

        ConfigureSamplers(material, clampTextureU, clampTextureV);

        materialCache[key] = material;
        return material;
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

    private static (bool ClampU, bool ClampV) ResolveTextureAddressing(
        RenderableSubmesh submesh,
        NifTextureResolver textureResolver)
    {
        var materialPath = submesh.ShaderMetadata?.MaterialPath;
        if (string.IsNullOrWhiteSpace(materialPath) &&
            submesh.DiffuseTexturePath is { } diffuse &&
            (diffuse.EndsWith(".bgsm", StringComparison.OrdinalIgnoreCase) ||
             diffuse.EndsWith(".bgem", StringComparison.OrdinalIgnoreCase)))
        {
            materialPath = diffuse;
        }

        return !string.IsNullOrWhiteSpace(materialPath) &&
               textureResolver.TryGetMaterial(materialPath) is { } material
            ? (!material.TileU, !material.TileV)
            : (submesh.ClampTextureU, submesh.ClampTextureV);
    }

    private static DecodedTexture? LoadStarfieldSlotTexture(
        NifTextureResolver textureResolver,
        StarfieldMaterialSlot slot)
    {
        return slot.TexturePath is { Length: > 0 } path
            ? textureResolver.GetTexture(path)
            : null;
    }

    private static StarfieldMaterialColorRenderState ResolveStarfieldColor(
        RenderableSubmesh submesh,
        NifTextureResolver textureResolver,
        StarfieldGlbVertexLerpProjectionResult vertexLerpProjection)
    {
        // The water sentinel has already left the ordinary CE2 material route. Do not resurrect a
        // layer tint from ShaderMetadata.MaterialPath in specialized/older export scenes that did
        // not carry the extractor's cleared state.
        if (string.Equals(
                submesh.DiffuseTexturePath,
                RenderableSubmesh.WaterSurfaceTexturePath,
                StringComparison.Ordinal))
        {
            return default;
        }

        if (vertexLerpProjection.IsUniformTextureBake)
        {
            return StarfieldGlbColorLerpBaker.Normalize(vertexLerpProjection.ConstantLerpState);
        }

        var carriedState = StarfieldGlbColorLerpBaker.Normalize(submesh.StarfieldMaterialColor);
        if (carriedState.IsConstantLerp)
        {
            return carriedState;
        }

        // Current extraction carries its resolved state on the submesh. Retain a direct constant-
        // Lerp fallback for older/specialized export scenes that preserve only the material path.
        // Vertex Lerp requires the carried external RGBA stream and is projected above instead.
        var materialPath = submesh.ShaderMetadata?.MaterialPath;
        if (string.IsNullOrWhiteSpace(materialPath) ||
            !MaterialTexturePathResolver.IsStarfieldMaterialPath(materialPath))
        {
            materialPath = submesh.DiffuseTexturePath;
        }

        if (string.IsNullOrWhiteSpace(materialPath) ||
            !MaterialTexturePathResolver.IsStarfieldMaterialPath(materialPath))
        {
            return default;
        }

        var policy = textureResolver.ResolveStarfieldBaseColorPolicy(materialPath);
        return policy.TryResolveConstantLerp(out var linearTint)
            ? StarfieldGlbColorLerpBaker.Normalize(
                new StarfieldMaterialColorRenderState(
                    StarfieldMaterialColorRenderMode.ConstantLerp,
                    linearTint))
            : default;
    }

    private static (
        StarfieldMaterialAlphaRenderState State,
        string? MaterialPath) ResolveStarfieldAlpha(
        RenderableSubmesh submesh,
        NifTextureResolver textureResolver)
    {
        // Water's explicit physical-preview coverage/no-cutout state is authoritative. Re-resolving
        // the .mat here could otherwise replace it with an ordinary CE2 opacity cutout.
        if (string.Equals(
                submesh.DiffuseTexturePath,
                RenderableSubmesh.WaterSurfaceTexturePath,
                StringComparison.Ordinal))
        {
            return default;
        }

        var materialPath = submesh.ShaderMetadata?.MaterialPath;
        if (string.IsNullOrWhiteSpace(materialPath) ||
            !MaterialTexturePathResolver.IsStarfieldMaterialPath(materialPath))
        {
            materialPath = submesh.DiffuseTexturePath;
        }

        if (string.IsNullOrWhiteSpace(materialPath) ||
            !MaterialTexturePathResolver.IsStarfieldMaterialPath(materialPath))
        {
            return default;
        }

        var carried = submesh.StarfieldMaterialAlpha;
        if (carried.IsLayer0OpacityCutout &&
            float.IsFinite(carried.AlphaTestThreshold) &&
            carried.AlphaTestThreshold is > 0f and < 1f)
        {
            return (carried, materialPath);
        }

        var resolved = textureResolver.ResolveStarfieldAlphaPolicy(materialPath).ResolveRenderState();
        return (resolved, materialPath);
    }

    internal static float ToGltfGreaterCutoff(float threshold)
    {
        // CE2 keeps alpha strictly GREATER than the threshold; glTF MASK keeps alpha >= cutoff.
        // The PNG's UNORM8 texels are LINEAR-filtered by glTF, so sampled coverage is continuous;
        // quantizing the authored threshold to a byte shifts silhouettes between adjacent texels.
        // The next float makes >= exactly equivalent to > for the shader's float-domain predicate.
        return MathF.BitIncrement(threshold);
    }

    private static string BuildBaseColorTextureName(
        string? texturePath,
        bool hasAlphaTransform,
        bool hasStarfieldLerp,
        bool hasStarfieldOpacity)
    {
        if (hasStarfieldOpacity)
        {
            return BuildDerivedTextureName(
                texturePath,
                hasStarfieldLerp
                    ? "baseColor.starfieldLerp.opacity"
                    : "baseColor.starfieldOpacity");
        }

        if (hasStarfieldLerp)
        {
            return BuildDerivedTextureName(
                texturePath,
                hasAlphaTransform
                    ? "baseColor.starfieldLerp.alpha"
                    : "baseColor.starfieldLerp");
        }

        return hasAlphaTransform
            ? BuildDerivedTextureName(texturePath, "baseColor.alpha")
            : BuildTextureName(texturePath, "baseColor.png");
    }

    private static string BuildTextureName(string? texturePath, string fallbackFileName)
    {
        if (string.IsNullOrWhiteSpace(texturePath))
        {
            return fallbackFileName;
        }

        var fileName = Path.GetFileNameWithoutExtension(texturePath);
        return string.IsNullOrWhiteSpace(fileName)
            ? fallbackFileName
            : fileName + ".png";
    }

    private static string BuildDerivedTextureName(string? texturePath, string suffix)
    {
        if (string.IsNullOrWhiteSpace(texturePath))
        {
            return suffix + ".png";
        }

        var fileName = Path.GetFileNameWithoutExtension(texturePath);
        return string.IsNullOrWhiteSpace(fileName)
            ? suffix + ".png"
            : fileName + "." + suffix + ".png";
    }

    internal static bool HasExternalRegularBgsmMaterial(RenderableSubmesh submesh)
    {
        ArgumentNullException.ThrowIfNull(submesh);

        var materialPath = submesh.ShaderMetadata?.MaterialPath;
        if (string.IsNullOrWhiteSpace(materialPath) &&
            submesh.DiffuseTexturePath?.EndsWith(".bgsm", StringComparison.OrdinalIgnoreCase) == true)
        {
            materialPath = submesh.DiffuseTexturePath;
        }

        return materialPath?.EndsWith(".bgsm", StringComparison.OrdinalIgnoreCase) == true;
    }

    /// <summary>
    ///     Encodes an effective linear emissive RGB into glTF's bounded emissive factor plus
    ///     <c>KHR_materials_emissive_strength</c>. Their product represents the input within normal
    ///     floating-point rounding for finite, non-negative authored values; malformed or inactive
    ///     state fails closed.
    /// </summary>
    internal static bool TryEncodeGltfEmission(
        Vector3 effectiveEmission,
        out Vector3 emissiveFactor,
        out float emissiveStrength)
    {
        emissiveFactor = Vector3.Zero;
        emissiveStrength = 1f;

        if (!float.IsFinite(effectiveEmission.X) ||
            !float.IsFinite(effectiveEmission.Y) ||
            !float.IsFinite(effectiveEmission.Z) ||
            effectiveEmission.X < 0f ||
            effectiveEmission.Y < 0f ||
            effectiveEmission.Z < 0f)
        {
            return false;
        }

        var peak = MathF.Max(effectiveEmission.X, MathF.Max(effectiveEmission.Y, effectiveEmission.Z));
        if (!(peak > 0f))
        {
            return false;
        }

        emissiveStrength = MathF.Max(1f, peak);
        emissiveFactor = effectiveEmission / emissiveStrength;
        return true;
    }

    private readonly record struct MaterialCacheKey(
        string? DiffusePath,
        string? NormalPath,
        string? EmissivePath,
        bool HasActiveBgsmEmission,
        Vector3 EmissiveFactor,
        float EmissiveStrength,
        string? HeightPath,
        string? EnvironmentMaskPath,
        bool IsEmissive,
        bool IsStarfieldWater,
        bool IsAuthoredSkyPreview,
        bool UseVertexColors,
        bool IsDoubleSided,
        NifAlphaRenderMode AlphaMode,
        byte AlphaThreshold,
        byte AlphaFunction,
        bool HasPreparedAlphaTexture,
        bool ClampTextureU,
        bool ClampTextureV,
        float MetallicFactor,
        float RoughnessFactor,
        float SpecularFactor,
        StarfieldMaterialColorRenderState StarfieldColor,
        string? StarfieldMaterialPath,
        StarfieldMaterialAlphaRenderState StarfieldAlpha,
        bool HasStarfieldOpacityTexture,
        StarfieldMaterialEffectPolicy StarfieldEffectPolicy,
        bool HasStarfieldEffectAlpha,
        StarfieldMaterialOrmPolicy StarfieldOrmPolicy,
        bool HasStaticStarfieldOrmPolicy,
        bool HasStarfieldOrm,
        bool HasStarfieldOrmTexture,
        bool HasStarfieldAmbientOcclusion,
        StarfieldGlbVertexLerpProjectionResult StarfieldVertexLerpProjection,
        Vector4 BaseColor);
}
