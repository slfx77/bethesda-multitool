using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Geometry;
using BethesdaMultitool.Core.Formats.Nif.Parser;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering;

/// <summary>
///     Stable façade over low-level NIF block parsing helpers used by the renderer.
/// </summary>
internal static class NifBlockParsers
{
    internal static bool SkipNiObjectNET(byte[] data, ref int pos, int end, bool be,
        bool hasInlineStrings = false)
    {
        return NifBinaryCursor.SkipNiObjectNET(data, ref pos, end, be, hasInlineStrings);
    }

    internal static Matrix4x4 ParseNiAVObjectTransform(
        byte[] data,
        BlockInfo block,
        uint bsVersion,
        uint binaryVersion,
        bool be,
        bool hasInlineStrings = false)
    {
        return NifObjectBlockReader.ParseNiAVObjectTransform(data, block, bsVersion, binaryVersion, be, hasInlineStrings);
    }

    internal static string? ReadBlockName(byte[] data, BlockInfo block, NifInfo nif)
    {
        return NifObjectBlockReader.ReadBlockName(data, block, nif);
    }

    /// <summary>
    ///     True when a geometry/node block carries the NiAVObject <c>Hidden</c> flag (bit 0 = APP_CULLED) —
    ///     the engine culls it, so the renderer must too (e.g. NV_FencePickCleanGate's hidden C_gatepost
    ///     proxy posts that otherwise render as untextured white "duplicate" posts).
    /// </summary>
    internal static bool IsHiddenShape(byte[] data, BlockInfo block, NifInfo nif)
    {
        return NifObjectBlockReader.IsHidden(data, block, nif.BsVersion, nif.BinaryVersion, nif.IsBigEndian, nif.HasInlineStrings);
    }

    internal static string? ReadParentNodeExtraData(byte[] data, BlockInfo block, NifInfo nif)
    {
        return NifObjectBlockReader.ReadParentNodeExtraData(data, block, nif);
    }

    internal static string? ReadAttachmentBoneExtraData(byte[] data, BlockInfo block, NifInfo nif)
    {
        return NifObjectBlockReader.ReadAttachmentBoneExtraData(data, block, nif);
    }

    internal static bool IsGoreShape(string? name)
    {
        return name != null &&
               (name.Contains("gore", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("dismember", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("decap", StringComparison.OrdinalIgnoreCase));
    }

    internal static bool IsEditorHelperShape(string? name)
    {
        return name != null &&
               name.Contains("EditorMarker", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     True when a NiGeometry shape in a BSShaderProperty-era NIF (FO3/FNV and later, BSVersion ≥ 34)
    ///     carries NO texture-source property — neither a <c>*ShaderProperty</c> (BSShaderPPLighting,
    ///     BSLighting, BSEffect, Water/Sky shaders, …) nor a legacy <c>NiTexturingProperty</c>. The FO3+
    ///     render pipeline can only draw geometry through a shader, so such a shape is a non-visual helper
    ///     the game never renders but the extractor would otherwise bake as an untextured white blob:
    ///     furniture-marker / boundary / capsule-collision-viz placeholders (e.g.
    ///     <c>furniture\LoungeChair_Tops.NIF</c>'s MarkerSource / ChairBoundary strips, which carry zero
    ///     properties), or a shape left with only a NiMaterialProperty and no shader (e.g.
    ///     <c>NV_McCarran-WallRubble.NIF</c>'s :2 strip). Older NIFs (Oblivion/Morrowind, BSVersion &lt; 34)
    ///     use property inheritance + NiTexturingProperty, so they are deliberately left untouched.
    /// </summary>
    internal static bool IsNonRenderableHelperShape(NifInfo nif, IReadOnlyList<int>? propertyRefs)
    {
        if (nif.BsVersion < 34) return false;
        if (propertyRefs is null || propertyRefs.Count == 0) return true; // no shader path at all

        foreach (var propRef in propertyRefs)
        {
            if (propRef < 0 || propRef >= nif.Blocks.Count) continue;
            var type = nif.Blocks[propRef].TypeName;

            // Any shader property is a texture source. The FO3/FNV names interleave the word
            // ("BSShaderPPLightingProperty", "BSShaderNoLightingProperty") so a "ShaderProperty"
            // substring would MISS them — match "Shader" + the "Property" suffix instead. Legacy
            // geometry without a shader gets its texture from NiTexturingProperty.
            if ((type.Contains("Shader", StringComparison.Ordinal) &&
                 type.EndsWith("Property", StringComparison.Ordinal)) ||
                string.Equals(type, "NiTexturingProperty", StringComparison.Ordinal))
            {
                return false; // has a texture-source property → renderable
            }
        }

        return true; // BSShader-era shape with only material/alpha/stencil → non-visual helper
    }

    /// <summary>
    ///     Pip-Boy effect shapes that FOPipboyManager drives at runtime (screen glare,
    ///     the flashlight quad, per-tab button glows) and that render as blown-out
    ///     overlays in a static render/export. The physical screen face
    ///     ("pipboyscreen", textured with Screen.dds) is kept — only the dynamic
    ///     effect quads are dropped. "PipboyLightEffect" is the FXWHITE flashlight
    ///     billboard, off by default in-game (FOPipboyManager::ShowPipboyLightEffect).
    /// </summary>
    internal static bool IsPipBoyScreenShape(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        // The render extractor names instanced submeshes "<shape>:<n>" — compare the
        // base shape name so "ScreenLit:0" matches like the export path's "ScreenLit".
        var colon = name.IndexOf(':', StringComparison.Ordinal);
        var baseName = colon >= 0 ? name.AsSpan(0, colon) : name.AsSpan();

        return baseName.Equals("ScreenLit", StringComparison.OrdinalIgnoreCase) ||
               baseName.Equals("glare", StringComparison.OrdinalIgnoreCase) ||
               baseName.Equals("PipboyLightEffect", StringComparison.OrdinalIgnoreCase) ||
               baseName.Equals("StatsGlow", StringComparison.OrdinalIgnoreCase) ||
               baseName.Equals("ItemsGlow", StringComparison.OrdinalIgnoreCase) ||
               baseName.Equals("DataGlow", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Armor/clothing meshes carry alternate left-forearm geometry that the engine
    ///     toggles when a Pip-Boy is worn. Decompile-verified against
    ///     <c>BipedAnim::AttachSkinnedObject</c> (MemDebug XEX, VA 0x822FBE40): the
    ///     engine lowercases each root child name and prefix-matches it — with
    ///     <c>abPipboy</c> set it removes <c>pipboyoff*</c> shapes (full sleeve), and
    ///     without it removes <c>pipboyon*</c> shapes (cut-away sleeve). Returns true
    ///     when <paramref name="name" /> is the variant that should NOT be drawn for
    ///     the given Pip-Boy state.
    /// </summary>
    internal static bool IsSuppressedPipBoyVariantShape(string? name, bool pipBoyVisible)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        // "PipBoyOff…" never matches the "PipBoyOn" prefix (they diverge at index 7),
        // so plain StartsWith mirrors the engine's strncmp exactly.
        return pipBoyVisible
            ? name.StartsWith("PipBoyOff", StringComparison.OrdinalIgnoreCase)
            : name.StartsWith("PipBoyOn", StringComparison.OrdinalIgnoreCase);
    }

    internal static int ParseShapeSkinInstanceRef(
        byte[] data,
        BlockInfo block,
        uint bsVersion,
        uint binaryVersion,
        bool be,
        bool hasInlineStrings = false)
    {
        return NifSceneGraphBlockReader.ParseShapeSkinInstanceRef(data, block, bsVersion, binaryVersion, be, hasInlineStrings);
    }

    internal static int[]? ParseDismemberPartitions(byte[] data, BlockInfo block, bool be)
    {
        return NifSceneGraphBlockReader.ParseDismemberPartitions(data, block, be);
    }

    internal static bool IsDismemberGoreShape(int[]? bodyParts)
    {
        return NifSceneGraphBlockReader.IsDismemberGoreShape(bodyParts);
    }

    internal static int ParseGeometryAdditionalDataRef(
        byte[] data,
        BlockInfo block,
        uint bsVersion,
        uint binaryVersion,
        bool be)
    {
        return NifSceneGraphBlockReader.ParseGeometryAdditionalDataRef(data, block, bsVersion, binaryVersion, be);
    }

    internal static int ReadVertexCount(byte[] data, BlockInfo block, bool be, uint binaryVersion)
    {
        return NifSceneGraphBlockReader.ReadVertexCount(data, block, be, binaryVersion);
    }

    internal static List<int>? ParseNodeChildren(
        byte[] data,
        BlockInfo block,
        uint bsVersion,
        uint binaryVersion,
        bool be,
        bool hasInlineStrings = false)
    {
        return NifSceneGraphBlockReader.ParseNodeChildren(data, block, bsVersion, binaryVersion, be, hasInlineStrings);
    }

    internal static int? ParseSwitchNodeActiveChildOrdinal(
        byte[] data,
        BlockInfo block,
        uint bsVersion,
        uint binaryVersion,
        bool be,
        bool hasInlineStrings = false)
    {
        return NifSceneGraphBlockReader.ParseSwitchNodeActiveChildOrdinal(
            data, block, bsVersion, binaryVersion, be, hasInlineStrings);
    }

    internal static int ParseShapeDataRef(byte[] data, BlockInfo block, uint bsVersion, uint binaryVersion,
        bool be, bool hasInlineStrings = false)
    {
        return NifSceneGraphBlockReader.ParseShapeDataRef(data, block, bsVersion, binaryVersion, be, hasInlineStrings);
    }

    internal static List<int>? ParseShapePropertyRefs(
        byte[] data,
        BlockInfo block,
        uint bsVersion,
        uint binaryVersion,
        bool be,
        bool hasInlineStrings = false)
    {
        return NifSceneGraphBlockReader.ParseShapePropertyRefs(data, block, bsVersion, binaryVersion, be, hasInlineStrings);
    }

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
        float materialGlossiness = 10f,
        (float R, float G, float B) specularColor = default,
        bool useDualQuaternionSkinning = false,
        float[]? preSkinMorphDeltas = null,
        Func<string, byte[]?>? externalMeshLoader = null,
        Action<string>? onExternalMeshDecodeFailure = null)
    {
        return NifSubmeshExtractor.ExtractSubmesh(
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
            materialGlossiness,
            specularColor,
            useDualQuaternionSkinning,
            preSkinMorphDeltas,
            externalMeshLoader,
            onExternalMeshDecodeFailure);
    }

    internal static RenderableSubmesh? ExtractTriShapeData(
        byte[] data,
        BlockInfo block,
        bool be,
        uint bsVersion,
        uint binaryVersion,
        Matrix4x4 transform,
        ((int BoneIdx, float Weight)[][] PerVertexInfluences, Matrix4x4[] BoneSkinMatrices)? skinning = null,
        bool useDualQuaternionSkinning = false)
    {
        return NifSubmeshExtractor.ExtractTriShapeData(
            data,
            block,
            be,
            bsVersion,
            binaryVersion,
            transform,
            skinning,
            useDualQuaternionSkinning);
    }

    internal static RenderableSubmesh? ExtractTriStripsData(
        byte[] data,
        BlockInfo block,
        bool be,
        uint bsVersion,
        uint binaryVersion,
        Matrix4x4 transform,
        ((int BoneIdx, float Weight)[][] PerVertexInfluences, Matrix4x4[] BoneSkinMatrices)? skinning = null,
        bool useDualQuaternionSkinning = false)
    {
        return NifSubmeshExtractor.ExtractTriStripsData(
            data,
            block,
            be,
            bsVersion,
            binaryVersion,
            transform,
            skinning,
            useDualQuaternionSkinning);
    }

    internal static float[] ReadVertexPositions(byte[] data, int offset, int numVerts, bool be)
    {
        return NifGeometryDataReader.ReadVertexPositions(data, offset, numVerts, be);
    }

    internal static float[] ReadUVs(byte[] data, int offset, int numVerts, bool be)
    {
        return NifGeometryDataReader.ReadUvs(data, offset, numVerts, be);
    }

    internal static bool ReadIsDoubleSided(byte[] data, NifInfo nif, List<int> propertyRefs)
    {
        return NifRenderPropertyReader.ReadIsDoubleSided(data, nif, propertyRefs);
    }

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

        // TES4 parallax materials (NiTexturingProperty Apply Mode HILIGHT/HILIGHT2) repurpose the
        // diffuse alpha channel as a height map, and Oblivion ships them with a blend-enabled
        // NiAlphaProperty (0x00ED) the engine does not blend with — SEIsland's rock faces rendered
        // see-through here because the mid-gray height data fed SRC_ALPHA blending. Demote blend for
        // that combination; alpha-test is left untouched (shipped parallax shapes author test=off).
        if (hasAlphaBlend &&
            dstBlendMode == 7 &&
            Textures.NifTexturingPropertyReader.ReadApplyMode(data, nif, propertyRefs) is 3 or 4)
        {
            hasAlphaBlend = false;
        }
    }

    internal static float ReadMaterialAlpha(byte[] data, NifInfo nif, List<int> propertyRefs)
    {
        return NifRenderPropertyReader.ReadMaterialAlpha(data, nif, propertyRefs);
    }

    internal static float ReadMaterialGlossiness(byte[] data, NifInfo nif, List<int> propertyRefs)
    {
        return NifRenderPropertyReader.ReadMaterialGlossiness(data, nif, propertyRefs);
    }

    internal static (float R, float G, float B) ReadMaterialSpecularColor(byte[] data, NifInfo nif,
        List<int> propertyRefs)
    {
        return NifRenderPropertyReader.ReadMaterialSpecularColor(data, nif, propertyRefs);
    }

    internal static (float R, float G, float B)? ReadMaterialDiffuse(byte[] data, NifInfo nif,
        List<int> propertyRefs)
    {
        return NifRenderPropertyReader.ReadMaterialDiffuse(data, nif, propertyRefs);
    }

    internal static (float R, float G, float B)? ReadAnimatedEmissiveColor(
        byte[] data, NifInfo nif, List<int> propertyRefs)
    {
        return NifRenderPropertyReader.ReadAnimatedEmissiveColor(data, nif, propertyRefs);
    }

    internal static (float R, float G, float B)? ReadMaterialEmissive(
        byte[] data, NifInfo nif, List<int> propertyRefs)
    {
        return NifRenderPropertyReader.ReadMaterialEmissive(data, nif, propertyRefs);
    }

    /// <summary>
    ///     Raw material emission plus its independent multiplier. Lighting30 applies the multiplier
    ///     only while HDR is active, so its renderer path cannot use <see cref="ReadMaterialEmissive" />
    ///     (which intentionally returns their product for full-bright NoLighting materials).
    /// </summary>
    internal static (float R, float G, float B, float Mult)? ReadMaterialEmissionSource(
        byte[] data, NifInfo nif, List<int> propertyRefs)
    {
        var info = NifRenderPropertyReader.ReadMaterialProperty(data, nif, propertyRefs);
        return info.HasMaterial
            ? (info.EmissiveR, info.EmissiveG, info.EmissiveB, info.EmissiveMult)
            : null;
    }

    internal static byte[] ReadVertexColors(byte[] data, int offset, int numVerts, bool be)
    {
        return NifGeometryDataReader.ReadVertexColors(data, offset, numVerts, be);
    }

    internal static float[] TransformPositions(float[] positions, Matrix4x4 transform)
    {
        return NifGeometryTransformUtils.TransformPositions(positions, transform);
    }

    internal static float[] TransformNormals(float[] normals, Matrix4x4 transform)
    {
        return NifGeometryTransformUtils.TransformNormals(normals, transform);
    }

    /// <summary>Recomputes per-vertex smooth (area-weighted) normals from positions and triangle indices.</summary>
    public static float[] RecomputeSmoothNormals(float[] positions, ushort[] triangles)
    {
        return NifGeometryTransformUtils.RecomputeSmoothNormals(positions, triangles);
    }
}
