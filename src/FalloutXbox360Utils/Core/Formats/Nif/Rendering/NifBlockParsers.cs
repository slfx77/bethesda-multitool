using System.Numerics;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Geometry;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Parsing;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering;

/// <summary>
///     Stable façade over low-level NIF block parsing helpers used by the renderer.
/// </summary>
internal static class NifBlockParsers
{
    internal static bool SkipNiObjectNET(byte[] data, ref int pos, int end, bool be)
        => NifBinaryCursor.SkipNiObjectNET(data, ref pos, end, be);

    internal static Matrix4x4 ParseNiAVObjectTransform(
        byte[] data,
        BlockInfo block,
        uint bsVersion,
        bool be) => NifObjectBlockReader.ParseNiAVObjectTransform(data, block, bsVersion, be);

    internal static string? ReadBlockName(byte[] data, BlockInfo block, NifInfo nif)
        => NifObjectBlockReader.ReadBlockName(data, block, nif);

    internal static bool IsGoreShape(string? name)
        => name != null &&
           (name.Contains("gore", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("dismember", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("decap", StringComparison.OrdinalIgnoreCase));

    internal static bool IsEditorHelperShape(string? name)
        => name != null &&
           name.Contains("EditorMarker", StringComparison.OrdinalIgnoreCase);

    internal static int ParseShapeSkinInstanceRef(
        byte[] data,
        BlockInfo block,
        uint bsVersion,
        bool be)
        => NifSceneGraphBlockReader.ParseShapeSkinInstanceRef(data, block, bsVersion, be);

    internal static int[]? ParseDismemberPartitions(byte[] data, BlockInfo block, bool be)
        => NifSceneGraphBlockReader.ParseDismemberPartitions(data, block, be);

    internal static bool IsDismemberGoreShape(int[]? bodyParts)
        => NifSceneGraphBlockReader.IsDismemberGoreShape(bodyParts);

    internal static int ParseGeometryAdditionalDataRef(
        byte[] data,
        BlockInfo block,
        uint bsVersion,
        bool be)
        => NifSceneGraphBlockReader.ParseGeometryAdditionalDataRef(data, block, bsVersion, be);

    internal static int ReadVertexCount(byte[] data, BlockInfo block, bool be)
        => NifSceneGraphBlockReader.ReadVertexCount(data, block, be);

    internal static List<int>? ParseNodeChildren(
        byte[] data,
        BlockInfo block,
        uint bsVersion,
        bool be)
        => NifSceneGraphBlockReader.ParseNodeChildren(data, block, bsVersion, be);

    internal static int ParseShapeDataRef(byte[] data, BlockInfo block, uint bsVersion, bool be)
        => NifSceneGraphBlockReader.ParseShapeDataRef(data, block, bsVersion, be);

    internal static List<int>? ParseShapePropertyRefs(
        byte[] data,
        BlockInfo block,
        uint bsVersion,
        bool be)
        => NifSceneGraphBlockReader.ParseShapePropertyRefs(data, block, bsVersion, be);

    internal static RenderableSubmesh? ExtractSubmesh(
        byte[] data,
        NifInfo nif,
        int shapeIndex,
        int dataIndex,
        Dictionary<int, Matrix4x4> worldTransforms,
        string? shapeName = null,
        NifShaderTextureMetadata? shaderMetadata = null,
        string? diffuseTexturePath = null,
        string? normalMapTexturePath = null,
        bool isEmissive = false,
        ((int BoneIdx, float Weight)[][] PerVertexInfluences, Matrix4x4[] BoneSkinMatrices)? skinning = null,
        bool useVertexColors = true,
        bool isDoubleSided = false,
        bool hasAlphaBlend = false,
        bool hasAlphaTest = false,
        byte alphaTestThreshold = 128,
        byte alphaTestFunction = 4,
        bool isEyeEnvmap = false,
        float envMapScale = 0f,
        byte srcBlendMode = 6,
        byte dstBlendMode = 7,
        float materialAlpha = 1f,
        bool useDualQuaternionSkinning = false)
        => NifSubmeshExtractor.ExtractSubmesh(
            data,
            nif,
            shapeIndex,
            dataIndex,
            worldTransforms,
            shapeName,
            shaderMetadata,
            diffuseTexturePath,
            normalMapTexturePath,
            isEmissive,
            skinning,
            useVertexColors,
            isDoubleSided,
            hasAlphaBlend,
            hasAlphaTest,
            alphaTestThreshold,
            alphaTestFunction,
            isEyeEnvmap,
            envMapScale,
            srcBlendMode,
            dstBlendMode,
            materialAlpha,
            useDualQuaternionSkinning);

    internal static RenderableSubmesh? ExtractTriShapeData(
        byte[] data,
        BlockInfo block,
        bool be,
        uint bsVersion,
        Matrix4x4 transform,
        ((int BoneIdx, float Weight)[][] PerVertexInfluences, Matrix4x4[] BoneSkinMatrices)? skinning = null,
        bool useDualQuaternionSkinning = false)
        => NifSubmeshExtractor.ExtractTriShapeData(
            data,
            block,
            be,
            bsVersion,
            transform,
            skinning,
            useDualQuaternionSkinning);

    internal static RenderableSubmesh? ExtractTriStripsData(
        byte[] data,
        BlockInfo block,
        bool be,
        uint bsVersion,
        Matrix4x4 transform,
        ((int BoneIdx, float Weight)[][] PerVertexInfluences, Matrix4x4[] BoneSkinMatrices)? skinning = null,
        bool useDualQuaternionSkinning = false)
        => NifSubmeshExtractor.ExtractTriStripsData(
            data,
            block,
            be,
            bsVersion,
            transform,
            skinning,
            useDualQuaternionSkinning);

    internal static float[] ReadVertexPositions(byte[] data, int offset, int numVerts, bool be)
        => NifGeometryDataReader.ReadVertexPositions(data, offset, numVerts, be);

    internal static float[] ReadUVs(byte[] data, int offset, int numVerts, bool be)
        => NifGeometryDataReader.ReadUvs(data, offset, numVerts, be);

    internal static bool ReadIsDoubleSided(byte[] data, NifInfo nif, List<int> propertyRefs)
        => NifRenderPropertyReader.ReadIsDoubleSided(data, nif, propertyRefs);

    internal static void ReadAlphaProperty(
        byte[] data,
        NifInfo nif,
        List<int> propertyRefs,
        out bool hasAlphaBlend,
        out bool hasAlphaTest,
        out byte alphaTestThreshold,
        out byte alphaTestFunction,
        out byte srcBlendMode,
        out byte dstBlendMode)
    {
        var alphaInfo = NifRenderPropertyReader.ReadAlphaProperty(data, nif, propertyRefs);
        hasAlphaBlend = alphaInfo.HasAlphaBlend;
        hasAlphaTest = alphaInfo.HasAlphaTest;
        alphaTestThreshold = alphaInfo.AlphaTestThreshold;
        alphaTestFunction = alphaInfo.AlphaTestFunction;
        srcBlendMode = alphaInfo.SrcBlendMode;
        dstBlendMode = alphaInfo.DstBlendMode;
    }

    internal static float ReadMaterialAlpha(byte[] data, NifInfo nif, List<int> propertyRefs)
        => NifRenderPropertyReader.ReadMaterialAlpha(data, nif, propertyRefs);

    internal static byte[] ReadVertexColors(byte[] data, int offset, int numVerts, bool be)
        => NifGeometryDataReader.ReadVertexColors(data, offset, numVerts, be);

    internal static float[] TransformPositions(float[] positions, Matrix4x4 transform)
        => NifGeometryTransformUtils.TransformPositions(positions, transform);

    internal static float[] TransformNormals(float[] normals, Matrix4x4 transform)
        => NifGeometryTransformUtils.TransformNormals(normals, transform);

    public static float[] RecomputeSmoothNormals(float[] positions, ushort[] triangles)
        => NifGeometryTransformUtils.RecomputeSmoothNormals(positions, triangles);
}
