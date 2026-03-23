using System.Numerics;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Geometry;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Skinning;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Export;

internal static class NifExportExtractor
{
    internal static ExtractedScene Extract(
        byte[] data,
        NifInfo nif,
        Dictionary<string, NifAnimationParser.AnimPoseOverride>? animOverrides = null,
        string? filterShapeName = null,
        float[]? preSkinMorphDeltas = null)
    {
        var nodeChildren = new Dictionary<int, List<int>>();
        var shapeDataMap = new Dictionary<int, int>();
        var shapePropertyMap = new Dictionary<int, List<int>>();
        var shapeSkinInstanceMap = new Dictionary<int, int>();
        var worldTransforms = new Dictionary<int, Matrix4x4>();

        NifSceneGraphWalker.ClassifyBlocks(
            data,
            nif,
            nodeChildren,
            shapeDataMap,
            shapePropertyMap,
            shapeSkinInstanceMap);

        ApplyShapeFilter(data, nif, shapeDataMap, shapePropertyMap, shapeSkinInstanceMap, filterShapeName);
        NifSceneGraphWalker.ComputeWorldTransforms(data, nif, nodeChildren, worldTransforms, animOverrides);

        var nodes = ExtractNodes(data, nif, nodeChildren, worldTransforms);
        var meshParts = ExtractMeshParts(
            data,
            nif,
            shapeDataMap,
            shapePropertyMap,
            shapeSkinInstanceMap,
            worldTransforms,
            preSkinMorphDeltas);

        return new ExtractedScene
        {
            Nodes = nodes,
            MeshParts = meshParts,
            NamedNodeWorldTransforms = BuildNamedNodeWorldTransforms(nodes)
        };
    }

    private static void ApplyShapeFilter(
        byte[] data,
        NifInfo nif,
        Dictionary<int, int> shapeDataMap,
        Dictionary<int, List<int>> shapePropertyMap,
        Dictionary<int, int> shapeSkinInstanceMap,
        string? filterShapeName)
    {
        if (filterShapeName == null)
        {
            return;
        }

        var shapeNames = shapeDataMap.Keys
            .Select(index => (index, Name: NifBlockParsers.ReadBlockName(data, nif.Blocks[index], nif) ?? string.Empty))
            .ToList();

        static bool MatchesFilter(string filter, string name)
        {
            return filter.Equals("Hat", StringComparison.OrdinalIgnoreCase)
                ? name.Contains("Hat", StringComparison.OrdinalIgnoreCase) &&
                  !name.Contains("NoHat", StringComparison.OrdinalIgnoreCase)
                : name.Contains(filter, StringComparison.OrdinalIgnoreCase);
        }

        if (!shapeNames.Any(entry => MatchesFilter(filterShapeName, entry.Name)))
        {
            return;
        }

        foreach (var entry in shapeNames.Where(entry => !MatchesFilter(filterShapeName, entry.Name)))
        {
            shapeDataMap.Remove(entry.index);
            shapePropertyMap.Remove(entry.index);
            shapeSkinInstanceMap.Remove(entry.index);
        }
    }

    private static List<ExtractedNode> ExtractNodes(
        byte[] data,
        NifInfo nif,
        Dictionary<int, List<int>> nodeChildren,
        Dictionary<int, Matrix4x4> worldTransforms)
    {
        var childToParent = new Dictionary<int, int>();
        foreach (var (parentIndex, children) in nodeChildren)
        {
            foreach (var childIndex in children)
            {
                childToParent[childIndex] = parentIndex;
            }
        }

        var nodes = new List<ExtractedNode>();
        foreach (var (blockIndex, worldTransform) in worldTransforms.OrderBy(entry => entry.Key))
        {
            if (blockIndex < 0 || blockIndex >= nif.Blocks.Count)
            {
                continue;
            }

            var block = nif.Blocks[blockIndex];
            if (!NifSceneGraphWalker.NodeTypes.Contains(block.TypeName))
            {
                continue;
            }

            var lookupName = NifBlockParsers.ReadBlockName(data, block, nif);
            var displayName = string.IsNullOrWhiteSpace(lookupName)
                ? $"Node_{blockIndex}"
                : lookupName;

            // Derive local transform from the world transform hierarchy so that animation
            // overrides (idle pose) are reflected in the exported nodes.  Reading the local
            // transform directly from the NIF block would give the bind-pose value, causing
            // the GLB skeleton to ignore idle animation.
            Matrix4x4 localTransform;
            if (childToParent.TryGetValue(blockIndex, out var parentIndex) &&
                worldTransforms.TryGetValue(parentIndex, out var parentWorld) &&
                Matrix4x4.Invert(parentWorld, out var invParentWorld))
            {
                localTransform = worldTransform * invParentWorld;
            }
            else
            {
                localTransform = worldTransform;
            }

            nodes.Add(new ExtractedNode(
                blockIndex,
                displayName,
                lookupName,
                childToParent.ContainsKey(blockIndex) ? parentIndex : null,
                localTransform,
                worldTransform));
        }

        return nodes;
    }

    private static List<ExtractedMeshPart> ExtractMeshParts(
        byte[] data,
        NifInfo nif,
        Dictionary<int, int> shapeDataMap,
        Dictionary<int, List<int>> shapePropertyMap,
        Dictionary<int, int> shapeSkinInstanceMap,
        Dictionary<int, Matrix4x4> worldTransforms,
        float[]? preSkinMorphDeltas)
    {
        var meshParts = new List<ExtractedMeshPart>();

        foreach (var (shapeIndex, dataIndex) in shapeDataMap)
        {
            var shapeBlock = nif.Blocks[shapeIndex];
            var dataBlock = nif.Blocks[dataIndex];
            var shapeName = NifBlockParsers.ReadBlockName(data, shapeBlock, nif) ?? $"Shape_{shapeIndex}";
            var properties = ResolveShapeProperties(data, nif, shapePropertyMap, shapeIndex);
            var skin = TryExtractSkinBinding(data, nif, shapeSkinInstanceMap, shapeIndex, dataIndex);

            var shapeMorphDeltas = skin != null ? preSkinMorphDeltas : null;
            preSkinMorphDeltas = skin != null ? null : preSkinMorphDeltas;
            var submesh = ExtractRawSubmesh(
                data,
                nif,
                dataBlock,
                shapeName,
                properties,
                shapeMorphDeltas);
            if (submesh == null)
            {
                continue;
            }

            meshParts.Add(new ExtractedMeshPart
            {
                Name = shapeName,
                Submesh = submesh,
                ShapeWorldTransform = worldTransforms.TryGetValue(shapeIndex, out var shapeWorld)
                    ? shapeWorld
                    : Matrix4x4.Identity,
                Skin = skin
            });
        }

        return meshParts;
    }

    private static ExtractedSkinBinding? TryExtractSkinBinding(
        byte[] data,
        NifInfo nif,
        Dictionary<int, int> shapeSkinInstanceMap,
        int shapeIndex,
        int dataIndex)
    {
        if (!shapeSkinInstanceMap.TryGetValue(shapeIndex, out var skinInstanceIndex))
        {
            return null;
        }

        var skinInstance = NifSkinBlockParser.ParseNiSkinInstance(data, nif.Blocks[skinInstanceIndex], nif.IsBigEndian);
        if (skinInstance == null ||
            skinInstance.DataRef < 0 ||
            skinInstance.DataRef >= nif.Blocks.Count ||
            nif.Blocks[skinInstance.DataRef].TypeName != "NiSkinData")
        {
            return null;
        }

        var skinData = NifSkinBlockParser.ParseNiSkinData(data, nif.Blocks[skinInstance.DataRef], nif.IsBigEndian);
        if (skinData == null || skinData.Bones.Length == 0)
        {
            return null;
        }

        var numVertices = NifBlockParsers.ReadVertexCount(data, nif.Blocks[dataIndex], nif.IsBigEndian);
        if (numVertices <= 0)
        {
            return null;
        }

        var influences = skinData.HasVertexWeights
            ? NifSkinInfluenceBuilder.BuildPerVertexInfluences(skinData, numVertices)
            : NifSkinInfluenceBuilder.BuildPerVertexInfluencesFromPartitions(
                data,
                nif,
                skinInstance,
                numVertices,
                out _);
        if (influences == null)
        {
            return null;
        }

        var boneNames = new string[skinData.Bones.Length];
        for (var i = 0; i < boneNames.Length; i++)
        {
            boneNames[i] = i < skinInstance.BoneRefs.Length &&
                           skinInstance.BoneRefs[i] >= 0 &&
                           skinInstance.BoneRefs[i] < nif.Blocks.Count
                ? NifBlockParsers.ReadBlockName(data, nif.Blocks[skinInstance.BoneRefs[i]], nif) ?? $"Bone_{i}"
                : $"Bone_{i}";
        }

        return new ExtractedSkinBinding
        {
            BoneNames = boneNames,
            InverseBindMatrices = skinData.Bones.Select(bone => bone.InverseBindPose).ToArray(),
            PerVertexInfluences = influences
        };
    }

    private static RenderableSubmesh? ExtractRawSubmesh(
        byte[] data,
        NifInfo nif,
        BlockInfo dataBlock,
        string shapeName,
        ShapeProperties properties,
        float[]? preSkinMorphDeltas)
    {
        var raw = dataBlock.TypeName switch
        {
            "NiTriShapeData" => NifSubmeshExtractor.ExtractTriShapeData(
                data,
                dataBlock,
                nif.IsBigEndian,
                nif.BsVersion,
                Matrix4x4.Identity,
                null,
                false,
                preSkinMorphDeltas),
            "NiTriStripsData" => NifSubmeshExtractor.ExtractTriStripsData(
                data,
                dataBlock,
                nif.IsBigEndian,
                nif.BsVersion,
                Matrix4x4.Identity,
                null,
                false,
                preSkinMorphDeltas),
            _ => null
        };

        if (raw == null)
        {
            return null;
        }

        return new RenderableSubmesh
        {
            ShapeName = shapeName,
            Positions = raw.Positions,
            Triangles = raw.Triangles,
            Normals = raw.Normals,
            UVs = raw.UVs,
            VertexColors = raw.VertexColors,
            Tangents = raw.Tangents,
            Bitangents = raw.Bitangents,
            ShaderMetadata = properties.ShaderMetadata,
            DiffuseTexturePath = properties.DiffusePath,
            NormalMapTexturePath = properties.NormalMapPath,
            IsEmissive = properties.IsEmissive,
            UseVertexColors = properties.UseVertexColors,
            IsDoubleSided = properties.IsDoubleSided,
            HasAlphaBlend = properties.HasAlphaBlend,
            HasAlphaTest = properties.HasAlphaTest,
            AlphaTestThreshold = properties.AlphaTestThreshold,
            AlphaTestFunction = properties.AlphaTestFunction,
            SrcBlendMode = properties.SrcBlendMode,
            DstBlendMode = properties.DstBlendMode,
            MaterialAlpha = properties.MaterialAlpha,
            MaterialGlossiness = properties.MaterialGlossiness,
            IsEyeEnvmap = properties.IsEyeEnvmap,
            EnvMapScale = properties.EnvMapScale
        };
    }

    private static ShapeProperties ResolveShapeProperties(
        byte[] data,
        NifInfo nif,
        Dictionary<int, List<int>> shapePropertyMap,
        int shapeIndex)
    {
        if (!shapePropertyMap.TryGetValue(shapeIndex, out var propRefs))
        {
            return ShapeProperties.Default;
        }

        var shaderMetadata = NifTextureResolver.ReadShaderMetadata(data, nif, propRefs);
        NifBlockParsers.ReadAlphaProperty(
            data,
            nif,
            propRefs,
            out var hasAlphaBlend,
            out var hasAlphaTest,
            out var alphaTestThreshold,
            out var alphaTestFunction,
            out var srcBlendMode,
            out var dstBlendMode);

        var useVertexColors = false;
        var isEyeEnvmap = false;
        var envMapScale = 0f;
        if (shaderMetadata?.ShaderFlags2 is uint shaderFlags2)
        {
            useVertexColors = (shaderFlags2 & (1u << 5)) != 0;
        }

        if (shaderMetadata?.ShaderFlags is uint shaderFlags &&
            shaderMetadata.EnvMapScale is float resolvedEnvMapScale)
        {
            isEyeEnvmap = (shaderFlags & 0x20000u) != 0;
            envMapScale = resolvedEnvMapScale;
        }

        return new ShapeProperties
        {
            ShaderMetadata = shaderMetadata,
            DiffusePath = shaderMetadata?.DiffusePath,
            NormalMapPath = shaderMetadata?.NormalMapPath,
            IsEmissive = shaderMetadata?.PropertyType == "BSShaderNoLightingProperty",
            UseVertexColors = useVertexColors,
            IsDoubleSided = NifBlockParsers.ReadIsDoubleSided(data, nif, propRefs),
            HasAlphaBlend = hasAlphaBlend,
            HasAlphaTest = hasAlphaTest,
            AlphaTestThreshold = alphaTestThreshold,
            AlphaTestFunction = alphaTestFunction,
            SrcBlendMode = srcBlendMode,
            DstBlendMode = dstBlendMode,
            MaterialAlpha = NifBlockParsers.ReadMaterialAlpha(data, nif, propRefs),
            MaterialGlossiness = NifBlockParsers.ReadMaterialGlossiness(data, nif, propRefs),
            IsEyeEnvmap = isEyeEnvmap,
            EnvMapScale = envMapScale
        };
    }

    private static Dictionary<string, Matrix4x4> BuildNamedNodeWorldTransforms(IEnumerable<ExtractedNode> nodes)
    {
        var result = new Dictionary<string, Matrix4x4>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in nodes)
        {
            if (!string.IsNullOrWhiteSpace(node.LookupName) && !result.ContainsKey(node.LookupName))
            {
                result.Add(node.LookupName, node.WorldTransform);
            }
        }

        return result;
    }

    internal sealed record ExtractedNode(
        int BlockIndex,
        string Name,
        string? LookupName,
        int? ParentBlockIndex,
        Matrix4x4 LocalTransform,
        Matrix4x4 WorldTransform);

    internal sealed class ExtractedScene
    {
        public required List<ExtractedNode> Nodes { get; init; }

        public required List<ExtractedMeshPart> MeshParts { get; init; }

        public required Dictionary<string, Matrix4x4> NamedNodeWorldTransforms { get; init; }
    }

    internal sealed class ExtractedMeshPart
    {
        public required string Name { get; init; }

        public required RenderableSubmesh Submesh { get; init; }

        public required Matrix4x4 ShapeWorldTransform { get; init; }

        public ExtractedSkinBinding? Skin { get; init; }
    }

    internal sealed class ExtractedSkinBinding
    {
        public required string[] BoneNames { get; init; }

        public required Matrix4x4[] InverseBindMatrices { get; init; }

        public required (int BoneIdx, float Weight)[][] PerVertexInfluences { get; init; }
    }

    private sealed class ShapeProperties
    {
        internal static readonly ShapeProperties Default = new();

        public NifShaderTextureMetadata? ShaderMetadata { get; init; }

        public string? DiffusePath { get; init; }

        public string? NormalMapPath { get; init; }

        public bool IsEmissive { get; init; }

        public bool UseVertexColors { get; init; }

        public bool IsDoubleSided { get; init; }

        public bool HasAlphaBlend { get; init; }

        public bool HasAlphaTest { get; init; }

        public byte AlphaTestThreshold { get; init; } = 128;

        public byte AlphaTestFunction { get; init; } = 4;

        public byte SrcBlendMode { get; init; } = 6;

        public byte DstBlendMode { get; init; } = 7;

        public float MaterialAlpha { get; init; } = 1f;

        public float MaterialGlossiness { get; init; } = 10f;

        public bool IsEyeEnvmap { get; init; }

        public float EnvMapScale { get; init; }
    }
}
