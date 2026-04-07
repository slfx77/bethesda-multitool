using System.Numerics;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Memory;
using SharpGLTF.Scenes;
using SharpGLTF.Schema2;
using AlphaMode = SharpGLTF.Materials.AlphaMode;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Export;

internal static class NpcGlbWriter
{
    internal static void Write(
        NpcExportScene scene,
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
        NpcExportScene scene,
        NifTextureResolver textureResolver)
    {
        using var ms = new MemoryStream();
        BuildGltfScene(scene, textureResolver).WriteGLB(ms);
        return ms.ToArray();
    }

    private static ModelRoot BuildGltfScene(
        NpcExportScene scene,
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

                var nodeIndex = meshPart.NodeIndex ?? NpcExportScene.RootNodeIndex;
                sceneBuilder.AddRigidMesh(rigidMesh, nodeBuilders[nodeIndex]);
            }
        }

        return sceneBuilder.ToGltf2();
    }

    private static void NormalizeWinding(RenderableSubmesh submesh)
    {
        if (submesh.Normals == null || submesh.TriangleCount == 0)
        {
            return;
        }

        GltfNormalDiagnostic.FixWindingOrder(submesh);
    }

    private static Dictionary<int, NodeBuilder> BuildNodeBuilders(NpcExportScene scene)
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

    private static HashSet<int> CollectUsedNodeIndices(NpcExportScene scene)
    {
        var used = new HashSet<int> { NpcExportScene.RootNodeIndex };

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
                AddNodeAndAncestors(scene, meshPart.NodeIndex ?? NpcExportScene.RootNodeIndex, used);
            }
        }

        return used;
    }

    private static void AddNodeAndAncestors(
        NpcExportScene scene,
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

    private static MeshBuilder<VertexPositionNormalTangent, VertexColor1Texture1, VertexEmpty> BuildRigidMesh(
        NpcExportMeshPart meshPart,
        NifTextureResolver textureResolver,
        Dictionary<MaterialCacheKey, MaterialBuilder> materialCache)
    {
        var mesh = new MeshBuilder<VertexPositionNormalTangent, VertexColor1Texture1, VertexEmpty>(meshPart.Name);
        var material = GetOrCreateMaterial(meshPart.Submesh, textureResolver, materialCache);
        var primitive = mesh.UsePrimitive(material);
        var tangents = NpcGlbTangentBuilder.BuildTangents(meshPart.Submesh);

        for (var index = 0; index + 2 < meshPart.Submesh.Triangles.Length; index += 3)
        {
            primitive.AddTriangle(
                CreateRigidVertex(meshPart.Submesh, tangents, meshPart.Submesh.Triangles[index]),
                CreateRigidVertex(meshPart.Submesh, tangents, meshPart.Submesh.Triangles[index + 1]),
                CreateRigidVertex(meshPart.Submesh, tangents, meshPart.Submesh.Triangles[index + 2]));
        }

        return mesh;
    }

    private static MeshBuilder<VertexPositionNormalTangent, VertexColor1Texture1, VertexJoints4> BuildSkinnedMesh(
        NpcExportMeshPart meshPart,
        NifTextureResolver textureResolver,
        Dictionary<MaterialCacheKey, MaterialBuilder> materialCache)
    {
        var mesh = new MeshBuilder<VertexPositionNormalTangent, VertexColor1Texture1, VertexJoints4>(meshPart.Name);
        var material = GetOrCreateMaterial(meshPart.Submesh, textureResolver, materialCache);
        var primitive = mesh.UsePrimitive(material);
        var skin = meshPart.Skin!;
        var tangents = NpcGlbTangentBuilder.BuildTangents(meshPart.Submesh);

        for (var index = 0; index + 2 < meshPart.Submesh.Triangles.Length; index += 3)
        {
            primitive.AddTriangle(
                CreateSkinnedVertex(meshPart.Submesh, tangents, skin, meshPart.Submesh.Triangles[index]),
                CreateSkinnedVertex(meshPart.Submesh, tangents, skin, meshPart.Submesh.Triangles[index + 1]),
                CreateSkinnedVertex(meshPart.Submesh, tangents, skin, meshPart.Submesh.Triangles[index + 2]));
        }

        return mesh;
    }

    private static (VertexPositionNormalTangent Geometry, VertexColor1Texture1 Material) CreateRigidVertex(
        RenderableSubmesh submesh,
        Vector4[]? tangents,
        int vertexIndex)
    {
        return (
            new VertexPositionNormalTangent(
                ReadPosition(submesh, vertexIndex),
                ReadNormal(submesh, vertexIndex),
                ReadTangent(submesh, tangents, vertexIndex)),
            new VertexColor1Texture1(ReadVertexColor(submesh, vertexIndex), ReadUv(submesh, vertexIndex)));
    }

    private static VertexBuilder<VertexPositionNormalTangent, VertexColor1Texture1, VertexJoints4> CreateSkinnedVertex(
        RenderableSubmesh submesh,
        Vector4[]? tangents,
        NpcExportSkinBinding skin,
        int vertexIndex)
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
            new VertexColor1Texture1(ReadVertexColor(submesh, vertexIndex), ReadUv(submesh, vertexIndex)),
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

    private static Vector4 ReadVertexColor(RenderableSubmesh submesh, int vertexIndex)
    {
        return NpcGlbTintColorEncoder.BuildVertexColor(submesh, vertexIndex);
    }

    private static MaterialBuilder GetOrCreateMaterial(
        RenderableSubmesh submesh,
        NifTextureResolver textureResolver,
        Dictionary<MaterialCacheKey, MaterialBuilder> materialCache)
    {
        var diffuseTexture = !string.IsNullOrWhiteSpace(submesh.DiffuseTexturePath)
            ? textureResolver.GetTexture(submesh.DiffuseTexturePath!)
            : null;
        diffuseTexture = NpcGlbTintColorEncoder.BakeDiffuseTexture(submesh, diffuseTexture);
        var preparedAlpha = NpcGlbAlphaTexturePacker.Prepare(submesh, diffuseTexture);
        var packedNormal = NpcGlbNormalMapPacker.ResolvePacked(textureResolver, submesh.NormalMapTexturePath);
        var normalTexture = packedNormal.Texture;
        var shaderMetadata = submesh.ShaderMetadata;
        var glowTexture = !string.IsNullOrWhiteSpace(shaderMetadata?.GlowMapPath)
            ? textureResolver.GetTexture(shaderMetadata.GlowMapPath)
            : null;
        var emissiveTexture = NpcGlbMaterialChannelDecider.ShouldExportGlowAsEmissive(submesh, shaderMetadata)
            ? glowTexture
            : null;
        var heightTexture = !string.IsNullOrWhiteSpace(shaderMetadata?.HeightMapPath)
            ? textureResolver.GetTexture(shaderMetadata.HeightMapPath)
            : null;
        var environmentMaskTexture = !string.IsNullOrWhiteSpace(shaderMetadata?.EnvironmentMaskPath)
            ? textureResolver.GetTexture(shaderMetadata.EnvironmentMaskPath)
            : null;
        var baseColor = NpcGlbTintColorEncoder.BuildBaseColor(submesh, preparedAlpha.Texture != null);
        var hasEnvironmentMapping = NpcGlbMaterialTuning.HasEnvironmentMapping(submesh);
        var materialProfile = NpcGlbMaterialTuning.Derive(submesh, normalTexture, packedNormal.HasGlossAlpha);
        var key = new MaterialCacheKey(
            submesh.DiffuseTexturePath,
            submesh.NormalMapTexturePath,
            emissiveTexture != null ? shaderMetadata?.GlowMapPath : null,
            shaderMetadata?.HeightMapPath,
            shaderMetadata?.EnvironmentMaskPath,
            submesh.IsEmissive,
            submesh.UseVertexColors,
            submesh.IsDoubleSided,
            preparedAlpha.RenderMode,
            preparedAlpha.AlphaThreshold,
            submesh.AlphaTestFunction,
            preparedAlpha.HasTextureTransform,
            materialProfile.RoughnessFactor,
            materialProfile.SpecularFactor,
            baseColor);

        if (materialCache.TryGetValue(key, out var material))
        {
            return material;
        }

        material = new MaterialBuilder(submesh.ShapeName ?? "material");
        if (submesh.IsEmissive)
        {
            material.WithUnlitShader();
        }
        else
        {
            material.WithMetallicRoughnessShader();
            material.WithMetallicRoughness(
                materialProfile.MetallicFactor,
                materialProfile.RoughnessFactor);
        }

        material.WithDoubleSide(submesh.IsDoubleSided);
        if (preparedAlpha.Texture != null)
        {
            var image = ImageBuilder.From(
                new MemoryImage(NpcGlbTextureEncoder.EncodePng(preparedAlpha.Texture)),
                preparedAlpha.HasTextureTransform
                    ? BuildDerivedTextureName(submesh.DiffuseTexturePath, "baseColor.alpha")
                    : BuildTextureName(submesh.DiffuseTexturePath, "baseColor.png"));
            material.WithBaseColor(image, baseColor);
        }
        else
        {
            material.WithBaseColor(baseColor);
        }

        if (!submesh.IsEmissive && normalTexture != null)
        {
            var image = ImageBuilder.From(
                new MemoryImage(NpcGlbTextureEncoder.EncodePng(normalTexture)),
                BuildDerivedTextureName(submesh.NormalMapTexturePath, "normal"));
            material.WithNormal(image);

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
        else if (!submesh.IsEmissive && environmentMaskTexture != null && hasEnvironmentMapping)
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

        if (!submesh.IsEmissive && emissiveTexture != null)
        {
            var emissiveImage = ImageBuilder.From(
                new MemoryImage(NpcGlbTextureEncoder.EncodePng(emissiveTexture)),
                BuildDerivedTextureName(shaderMetadata?.GlowMapPath, "emissive"));
            material.WithEmissive(emissiveImage, Vector3.One);
        }

        if (!submesh.IsEmissive && heightTexture != null)
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
                material.WithAlpha(AlphaMode.MASK, preparedAlpha.AlphaThreshold / 255f);
                break;
        }

        materialCache[key] = material;
        return material;
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

    private readonly record struct MaterialCacheKey(
        string? DiffusePath,
        string? NormalPath,
        string? GlowPath,
        string? HeightPath,
        string? EnvironmentMaskPath,
        bool IsEmissive,
        bool UseVertexColors,
        bool IsDoubleSided,
        NifAlphaRenderMode AlphaMode,
        byte AlphaThreshold,
        byte AlphaFunction,
        bool HasPreparedAlphaTexture,
        float RoughnessFactor,
        float SpecularFactor,
        Vector4 BaseColor);
}
