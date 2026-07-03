using System.Numerics;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Nif.GeometryAnalysis;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Geometry;

/// <summary>
///     Extracts renderable submeshes from NiTriShapeData and NiTriStripsData blocks.
/// </summary>
internal static class NifSubmeshExtractor
{
    private static readonly Logger Log = Logger.Instance;

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
        float[]? preSkinMorphDeltas = null)
    {
        var dataBlock = nif.Blocks[dataIndex];
        worldTransforms.TryGetValue(shapeIndex, out var transform);
        if (transform == default)
        {
            transform = Matrix4x4.Identity;
        }

        // Morrowind (NIF 4.0.0.2) NiTri*Data uses a distinct NiGeometryData layout (no Group ID,
        // 32-bit booleans, an always-present Bounding Sphere, and the legacy Data Flags + Has UV
        // before the UV sets), so it takes a dedicated reader rather than the FNV/Oblivion one.
        var submesh = NifVersions.IsLegacyNetImmerse(nif.BinaryVersion)
                      && dataBlock.TypeName is "NiTriShapeData" or "NiTriStripsData"
            ? ExtractMorrowindGeometryData(
                data,
                dataBlock,
                nif.IsBigEndian,
                transform,
                skinning,
                useDualQuaternionSkinning,
                preSkinMorphDeltas,
                shapeName)
            : dataBlock.TypeName switch
            {
                "NiTriShapeData" => ExtractTriShapeData(
                    data,
                    dataBlock,
                    nif.IsBigEndian,
                    nif.BsVersion,
                    nif.BinaryVersion,
                    transform,
                    skinning,
                    useDualQuaternionSkinning,
                    preSkinMorphDeltas,
                    shapeName),
                "NiTriStripsData" => ExtractTriStripsData(
                    data,
                    dataBlock,
                    nif.IsBigEndian,
                    nif.BsVersion,
                    nif.BinaryVersion,
                    transform,
                    skinning,
                    useDualQuaternionSkinning,
                    preSkinMorphDeltas,
                    shapeName),
                // BSTriShape and its variants are self-contained (the shape block IS its own data block,
                // so dataIndex == shapeIndex). Skyrim SE / Fallout 4 / Fallout 76 geometry.
                "BSTriShape" or "BSSubIndexTriShape" or "BSMeshLODTriShape" or "BSDynamicTriShape"
                    => ExtractBsTriShape(data, dataBlock, nif.IsBigEndian, nif.BsVersion, nif.BinaryVersion, transform, shapeName),
                _ => null
            };

        if (submesh == null)
        {
            return null;
        }

        return new RenderableSubmesh
        {
            ShapeName = shapeName,
            Positions = submesh.Positions,
            Triangles = submesh.Triangles,
            Normals = submesh.Normals,
            UVs = submesh.UVs,
            VertexColors = submesh.VertexColors,
            Tangents = submesh.Tangents,
            Bitangents = submesh.Bitangents,
            BindPosePositions = submesh.BindPosePositions,
            ShaderMetadata = shaderMetadata,
            DiffuseTexturePath = diffuseTexturePath,
            NormalMapTexturePath = normalMapTexturePath,
            IsEmissive = isEmissive,
            UseVertexColors = useVertexColors,
            IsDoubleSided = isDoubleSided,
            HasAlphaBlend = hasAlphaBlend,
            HasAlphaTest = hasAlphaTest,
            AlphaTestThreshold = alphaTestThreshold,
            AlphaTestFunction = alphaTestFunction,
            IsEyeEnvmap = isEyeEnvmap,
            EnvMapScale = envMapScale,
            SrcBlendMode = srcBlendMode,
            DstBlendMode = dstBlendMode,
            MaterialAlpha = materialAlpha,
            MaterialGlossiness = materialGlossiness,
            SpecularColor = specularColor
        };
    }

    internal static RenderableSubmesh? ExtractTriShapeData(
        byte[] data,
        BlockInfo block,
        bool be,
        uint bsVersion,
        uint binaryVersion,
        Matrix4x4 transform,
        ((int BoneIdx, float Weight)[][] PerVertexInfluences, Matrix4x4[] BoneSkinMatrices)? skinning = null,
        bool useDualQuaternionSkinning = false,
        float[]? preSkinMorphDeltas = null,
        string? shapeName = null)
    {
        var pos = block.DataOffset;
        var end = block.DataOffset + block.Size;

        // NiGeometryData layout is keyed deterministically on the NIF version, never the Bethesda stream
        // version. The "modern" base — a Data Flags ushort, an always-present Bounding Sphere, flag-driven
        // UV and a trailing Consistency Flags — is present since NIF 10.0.1.0: that covers Oblivion's
        // oldest 10.0.1.0 / 10.0.1.2 meshes, its 10.1.0.10x / 10.2.0.0 architecture, 20.0.0.4/5, and FNV
        // 20.2.0.7. The Keep/Compress Flags pair is narrower (since 10.1.0.0), so 10.0.1.x has the base
        // WITHOUT it. The trailing Additional Data ref exists only since 20.0.0.4.
        var modernGeom = NifVersions.HasModernGeometryBase(binaryVersion);
        var hasKeepCompressFlags = NifVersions.HasGeometryKeepFlags(binaryVersion);
        var hasAdditionalData = binaryVersion >= 0x14000004;

        // NiGeometryData.Group ID (int) is present only since NIF 10.1.0.114. Oblivion 20.0.0.x and
        // 10.2.0.0 have it, but the older 10.1.0.106 fort-ruins architecture does NOT — skipping it
        // unconditionally would consume the first 4 bytes of Num Vertices and yield no geometry.
        if (NifVersions.HasGeometryGroupId(binaryVersion))
        {
            pos += 4;
        }
        if (pos + 2 > end)
        {
            return null;
        }

        var numVerts = BinaryUtils.ReadUInt16(data, pos, be);
        pos += 2;
        if (numVerts == 0)
        {
            return null;
        }

        if (hasKeepCompressFlags)
        {
            pos += 2; // Keep Flags + Compress Flags (since 10.1.0.0; absent on 10.0.1.x)
        }

        if (pos + 1 > end)
        {
            return null;
        }

        float[]? positions = null;
        if (data[pos++] != 0)
        {
            positions = NifGeometryDataReader.ReadVertexPositions(data, pos, numVerts, be);
            pos += numVerts * 12;
        }

        ushort bsVectorFlags = 0;
        if (modernGeom)
        {
            if (pos + 2 > end)
            {
                return null;
            }

            bsVectorFlags = BinaryUtils.ReadUInt16(data, pos, be);
            pos += 2;

            // Skyrim and later (BS stream > 34) insert a 4-byte Material CRC into NiGeometryData
            // after the BS Data Flags, before the normals. FNV (BS 34) does not.
            if (bsVersion > 34)
            {
                pos += 4;
            }
        }

        if (pos + 1 > end)
        {
            return null;
        }

        float[]? normals = null;
        float[]? tangents = null;
        float[]? bitangents = null;
        if (data[pos++] != 0)
        {
            normals = NifGeometryDataReader.ReadVertexPositions(data, pos, numVerts, be);
            pos += numVerts * 12;

            if (modernGeom)
            {
                // nif.xml NiGeometryData order is Normals → Tangents → Bitangents → Bounding Sphere, so the
                // tangents/bitangents sit RIGHT AFTER the normals, BEFORE the 16-byte bounding sphere. The old
                // code skipped the sphere FIRST, shifting every tangent 16 bytes (~1.33 verts) into the array
                // → a garbage/degenerate per-vertex tangent basis. That made the normal-map TBN swim and
                // produced triangulation-following bump banding on flat panels (e.g. billboards). Read them
                // first; skip the sphere after. The total advance is unchanged (24N + 16) so the bounding
                // sphere and downstream vertex colors/UVs stay aligned — which is why this stayed hidden.
                if ((bsVectorFlags & 0x1000) != 0)
                {
                    if (pos + numVerts * 24 <= end)
                    {
                        tangents = NifGeometryDataReader.ReadVertexPositions(data, pos, numVerts, be);
                        bitangents = NifGeometryDataReader.ReadVertexPositions(
                            data,
                            pos + numVerts * 12,
                            numVerts,
                            be);
                    }

                    pos += numVerts * 24;
                }

                pos += 16; // bounding sphere (Center + Radius) comes AFTER tangents/bitangents
            }
        }
        else if (modernGeom)
        {
            pos += 16;
        }

        byte[]? vertexColors = null;
        if (pos + 1 > end)
        {
            return null;
        }

        if (data[pos++] != 0)
        {
            if (pos + numVerts * 16 <= end)
            {
                vertexColors = NifGeometryDataReader.ReadVertexColors(data, pos, numVerts, be);
            }

            pos += numVerts * 16;
        }

        float[]? uvs = null;
        if (modernGeom)
        {
            var numUvSets = (bsVectorFlags & 0x0001) != 0 ? 1 : 0;
            if (numUvSets > 0 && pos + numVerts * 8 <= end)
            {
                uvs = NifGeometryDataReader.ReadUvs(data, pos, numVerts, be);
            }

            pos += numVerts * numUvSets * 8;
        }
        else
        {
            if (pos + 4 > end)
            {
                return null;
            }

            var numUvSets = BinaryUtils.ReadUInt16(data, pos, be) & 0x3F;
            pos += 4;
            if (numUvSets > 0 && pos + numVerts * 8 <= end)
            {
                uvs = NifGeometryDataReader.ReadUvs(data, pos, numVerts, be);
            }

            pos += numVerts * numUvSets * 8;
        }

        pos += 2; // Consistency Flags
        if (hasAdditionalData)
        {
            pos += 4; // Additional Data ref (since 20.0.0.4)
        }

        if (pos + 2 > end)
        {
            return null;
        }

        var numTriangles = BinaryUtils.ReadUInt16(data, pos, be);
        pos += 2;
        if (pos + 4 > end)
        {
            return null;
        }

        pos += 4; // Num Triangle Points

        // "Has Triangles" bool exists only since 10.1.0.0; at or below 10.0.1.2 (Oblivion 10.0.1.x) the
        // triangle list follows unconditionally, so reading the bool would eat the first triangle index.
        if (NifVersions.HasShapeTriangleFlag(binaryVersion) && (pos + 1 > end || data[pos++] == 0))
        {
            return null;
        }

        if (numTriangles == 0 || positions == null)
        {
            return null;
        }

        if (pos + numTriangles * 6 > end)
        {
            return null;
        }

        var triangles = new ushort[numTriangles * 3];
        for (var i = 0; i < triangles.Length; i++)
        {
            triangles[i] = BinaryUtils.ReadUInt16(data, pos, be);
            pos += 2;
        }

        // Apply EGM morph deltas in bind-pose space BEFORE skinning transforms vertices
        if (preSkinMorphDeltas != null)
        {
            var count = Math.Min(positions.Length, preSkinMorphDeltas.Length);
            for (var i = 0; i < count; i++)
                positions[i] += preSkinMorphDeltas[i];
        }

        // Capture bind-pose world positions before skinning for boundary vertex stitching
        var bindPosePositions = skinning.HasValue
            ? NifGeometryTransformUtils.TransformPositions(positions, transform)
            : null;

        var transformed = ApplySkinningOrTransform(
            positions,
            normals,
            tangents,
            bitangents,
            transform,
            skinning,
            useDualQuaternionSkinning,
            shapeName);

        return new RenderableSubmesh
        {
            Positions = transformed.Positions,
            Triangles = triangles,
            Normals = transformed.Normals
                      ?? NifGeometryTransformUtils.RecomputeSmoothNormals(transformed.Positions, triangles),
            UVs = uvs,
            VertexColors = vertexColors,
            Tangents = transformed.Tangents,
            Bitangents = transformed.Bitangents,
            BindPosePositions = bindPosePositions
        };
    }

    /// <summary>
    ///     Extract geometry from a Morrowind (NIF 4.0.0.2) NiTriShapeData / NiTriStripsData block.
    ///     The NiGeometryData base differs from FNV/Oblivion: there is no Group ID, booleans are
    ///     32-bit, the Bounding Sphere is always present (right after the normals), and the legacy
    ///     Data Flags (ushort, low 6 bits = UV-set count) + Has UV (32-bit bool) precede the UV sets.
    ///     Triangle data is either a triangle list (NiTriShapeData) or triangle strips
    ///     (NiTriStripsData: Num Strips + Strip Lengths + jagged Points). Field offsets validated
    ///     byte-for-byte against the schema measure walk; gated via NifVersions.IsLegacyNetImmerse.
    /// </summary>
    private static RenderableSubmesh? ExtractMorrowindGeometryData(
        byte[] data,
        BlockInfo block,
        bool be,
        Matrix4x4 transform,
        ((int BoneIdx, float Weight)[][] PerVertexInfluences, Matrix4x4[] BoneSkinMatrices)? skinning,
        bool useDualQuaternionSkinning,
        float[]? preSkinMorphDeltas,
        string? shapeName)
    {
        var pos = block.DataOffset;
        var end = block.DataOffset + block.Size;
        var isStrips = block.TypeName == "NiTriStripsData";

        if (pos + 2 > end)
        {
            return null;
        }

        var numVerts = BinaryUtils.ReadUInt16(data, pos, be);
        pos += 2;
        if (numVerts == 0)
        {
            return null;
        }

        // Has Vertices (32-bit bool) + Vertices (Vector3 × numVerts).
        if (pos + 4 > end)
        {
            return null;
        }

        var hasVertices = BinaryUtils.ReadUInt32(data, pos, be) != 0;
        pos += 4;
        float[]? positions = null;
        if (hasVertices)
        {
            if (pos + numVerts * 12 > end)
            {
                return null;
            }

            positions = NifGeometryDataReader.ReadVertexPositions(data, pos, numVerts, be);
            pos += numVerts * 12;
        }

        // Has Normals (32-bit bool) + Normals (Vector3 × numVerts). No tangents/bitangents (< 10.1.0.0).
        if (pos + 4 > end)
        {
            return null;
        }

        var hasNormals = BinaryUtils.ReadUInt32(data, pos, be) != 0;
        pos += 4;
        float[]? normals = null;
        if (hasNormals)
        {
            if (pos + numVerts * 12 > end)
            {
                return null;
            }

            normals = NifGeometryDataReader.ReadVertexPositions(data, pos, numVerts, be);
            pos += numVerts * 12;
        }

        // Bounding Sphere (NiBound: Vector3 center + float radius) — always present.
        pos += 16;

        // Has Vertex Colors (32-bit bool) + Vertex Colors (Color4 × numVerts).
        if (pos + 4 > end)
        {
            return null;
        }

        var hasColors = BinaryUtils.ReadUInt32(data, pos, be) != 0;
        pos += 4;
        byte[]? vertexColors = null;
        if (hasColors)
        {
            if (pos + numVerts * 16 > end)
            {
                return null;
            }

            vertexColors = NifGeometryDataReader.ReadVertexColors(data, pos, numVerts, be);
            pos += numVerts * 16;
        }

        // Data Flags (ushort, low 6 bits = UV-set count) + Has UV (32-bit bool) + UV Sets.
        if (pos + 6 > end)
        {
            return null;
        }

        var numUvSets = BinaryUtils.ReadUInt16(data, pos, be) & 0x3F;
        pos += 2;
        pos += 4; // Has UV (32-bit bool)
        float[]? uvs = null;
        if (numUvSets > 0)
        {
            if (pos + numVerts * 8 > end)
            {
                return null;
            }

            uvs = NifGeometryDataReader.ReadUvs(data, pos, numVerts, be);
        }

        pos += numUvSets * numVerts * 8;

        // NiTriBasedGeomData: Num Triangles.
        if (pos + 2 > end)
        {
            return null;
        }

        var numTriangles = BinaryUtils.ReadUInt16(data, pos, be);
        pos += 2;

        var triangles = isStrips
            ? ReadMorrowindStripTriangles(data, ref pos, end, be)
            : ReadMorrowindListTriangles(data, ref pos, end, be, numTriangles);

        if (triangles == null || triangles.Length == 0 || positions == null)
        {
            return null;
        }

        if (preSkinMorphDeltas != null)
        {
            var count = Math.Min(positions.Length, preSkinMorphDeltas.Length);
            for (var i = 0; i < count; i++)
            {
                positions[i] += preSkinMorphDeltas[i];
            }
        }

        var bindPosePositions = skinning.HasValue
            ? NifGeometryTransformUtils.TransformPositions(positions, transform)
            : null;

        var transformed = ApplySkinningOrTransform(
            positions,
            normals,
            null,
            null,
            transform,
            skinning,
            useDualQuaternionSkinning,
            shapeName);

        return new RenderableSubmesh
        {
            ShapeName = shapeName,
            Positions = transformed.Positions,
            Triangles = triangles,
            Normals = transformed.Normals
                      ?? NifGeometryTransformUtils.RecomputeSmoothNormals(transformed.Positions, triangles),
            UVs = uvs,
            VertexColors = vertexColors,
            Tangents = transformed.Tangents,
            Bitangents = transformed.Bitangents,
            BindPosePositions = bindPosePositions
        };
    }

    // NiTriShapeData triangle list (Morrowind): Num Triangle Points (uint) + Triangle[] (3 × ushort).
    private static ushort[]? ReadMorrowindListTriangles(byte[] data, ref int pos, int end, bool be, int numTriangles)
    {
        pos += 4; // Num Triangle Points (= numTriangles * 3); recomputable, skip
        if (numTriangles == 0 || pos + numTriangles * 6 > end)
        {
            return null;
        }

        var triangles = new ushort[numTriangles * 3];
        for (var i = 0; i < triangles.Length; i++)
        {
            triangles[i] = BinaryUtils.ReadUInt16(data, pos, be);
            pos += 2;
        }

        return triangles;
    }

    // NiTriStripsData strips (Morrowind, < 10.0.1.3 so no Has Points flag): Num Strips (ushort) +
    // Strip Lengths (ushort × Num Strips) + Points (ushort, sum of strip lengths). Each strip is
    // unwound into a triangle list with standard winding-flip on odd triangles; degenerates dropped.
    private static ushort[]? ReadMorrowindStripTriangles(byte[] data, ref int pos, int end, bool be)
    {
        if (pos + 2 > end)
        {
            return null;
        }

        int numStrips = BinaryUtils.ReadUInt16(data, pos, be);
        pos += 2;
        if (numStrips is <= 0 or > 10000 || pos + numStrips * 2 > end)
        {
            return null;
        }

        var stripLengths = new int[numStrips];
        for (var i = 0; i < numStrips; i++)
        {
            stripLengths[i] = BinaryUtils.ReadUInt16(data, pos, be);
            pos += 2;
        }

        var triangles = new List<ushort>();
        for (var s = 0; s < numStrips; s++)
        {
            var len = stripLengths[s];
            if (len < 0 || pos + len * 2 > end)
            {
                return null;
            }

            var strip = new ushort[len];
            for (var i = 0; i < len; i++)
            {
                strip[i] = BinaryUtils.ReadUInt16(data, pos, be);
                pos += 2;
            }

            for (var i = 0; i + 2 < len; i++)
            {
                var a = strip[i];
                var b = strip[i + 1];
                var c = strip[i + 2];
                if (a == b || b == c || a == c)
                {
                    continue; // degenerate (strip restart)
                }

                if ((i & 1) == 0)
                {
                    triangles.Add(a);
                    triangles.Add(b);
                    triangles.Add(c);
                }
                else
                {
                    triangles.Add(a);
                    triangles.Add(c);
                    triangles.Add(b);
                }
            }
        }

        return triangles.Count > 0 ? triangles.ToArray() : null;
    }

    internal static RenderableSubmesh? ExtractTriStripsData(
        byte[] data,
        BlockInfo block,
        bool be,
        uint bsVersion,
        uint binaryVersion,
        Matrix4x4 transform,
        ((int BoneIdx, float Weight)[][] PerVertexInfluences, Matrix4x4[] BoneSkinMatrices)? skinning = null,
        bool useDualQuaternionSkinning = false,
        float[]? preSkinMorphDeltas = null,
        string? shapeName = null)
    {
        var triangles = NifTriStripExtractor.ExtractTrianglesFromTriStripsData(data, block, be, binaryVersion);
        if (triangles == null || triangles.Length == 0)
        {
            return null;
        }

        // NiGeometryData.Group ID (int) only since NIF 10.1.0.114 (see ExtractTriShapeData) — absent on
        // Oblivion's 10.1.0.106 architecture, so skip it only when present.
        var pos = block.DataOffset + (NifVersions.HasGeometryGroupId(binaryVersion) ? 4 : 0);
        var end = block.DataOffset + block.Size;
        // Modern NiGeometryData base (Data Flags + Bounding Sphere + flag-driven UV) — present since NIF
        // 10.0.1.0; the Keep/Compress Flags pair is narrower (since 10.1.0.0). Keyed on the NIF version
        // rather than the Bethesda stream version (see ExtractTriShapeData).
        var modernGeom = NifVersions.HasModernGeometryBase(binaryVersion);
        var hasKeepCompressFlags = NifVersions.HasGeometryKeepFlags(binaryVersion);
        if (pos + 2 > end)
        {
            return null;
        }

        var numVerts = BinaryUtils.ReadUInt16(data, pos, be);
        pos += 2;
        if (numVerts == 0)
        {
            return null;
        }

        // The Keep Flags + Compress Flags pair sits right after Num Vertices, since NIF 10.1.0.0. Oblivion
        // 10.1.0.10x / 10.2.0.0 / 20.0.0.x and FO3/FNV all have it; Oblivion's oldest 10.0.1.x and
        // Morrowind 4.0.0.2 do not. (Skyrim+, bsVersion > 34, also adds a Material CRC and uses BSTriShape,
        // not NiTri*Data.)
        if (hasKeepCompressFlags)
        {
            pos += 2;
        }

        if (pos + 1 > end || data[pos++] == 0)
        {
            return null;
        }

        var positions = NifGeometryDataReader.ReadVertexPositions(data, pos, numVerts, be);
        pos += numVerts * 12;

        ushort bsVectorFlags = 0;
        if (modernGeom)
        {
            if (pos + 2 > end)
            {
                return null;
            }

            bsVectorFlags = BinaryUtils.ReadUInt16(data, pos, be);
            pos += 2;

            // Skyrim and later (BS stream > 34) insert a 4-byte Material CRC into NiGeometryData
            // after the BS Data Flags, before the normals. FNV (BS 34) does not.
            if (bsVersion > 34)
            {
                pos += 4;
            }
        }

        if (pos + 1 > end)
        {
            return null;
        }

        float[]? normals = null;
        float[]? tangents = null;
        float[]? bitangents = null;
        if (data[pos++] != 0)
        {
            normals = NifGeometryDataReader.ReadVertexPositions(data, pos, numVerts, be);
            pos += numVerts * 12;

            if (modernGeom)
            {
                // nif.xml NiGeometryData order is Normals → Tangents → Bitangents → Bounding Sphere, so the
                // tangents/bitangents sit RIGHT AFTER the normals, BEFORE the 16-byte bounding sphere. The old
                // code skipped the sphere FIRST, shifting every tangent 16 bytes (~1.33 verts) into the array
                // → a garbage/degenerate per-vertex tangent basis. That made the normal-map TBN swim and
                // produced triangulation-following bump banding on flat panels (e.g. billboards). Read them
                // first; skip the sphere after. The total advance is unchanged (24N + 16) so the bounding
                // sphere and downstream vertex colors/UVs stay aligned — which is why this stayed hidden.
                if ((bsVectorFlags & 0x1000) != 0)
                {
                    if (pos + numVerts * 24 <= end)
                    {
                        tangents = NifGeometryDataReader.ReadVertexPositions(data, pos, numVerts, be);
                        bitangents = NifGeometryDataReader.ReadVertexPositions(
                            data,
                            pos + numVerts * 12,
                            numVerts,
                            be);
                    }

                    pos += numVerts * 24;
                }

                pos += 16; // bounding sphere (Center + Radius) comes AFTER tangents/bitangents
            }
        }
        else if (modernGeom)
        {
            pos += 16;
        }

        byte[]? vertexColors = null;
        if (pos + 1 <= end && data[pos++] != 0)
        {
            if (pos + numVerts * 16 <= end)
            {
                vertexColors = NifGeometryDataReader.ReadVertexColors(data, pos, numVerts, be);
            }

            pos += numVerts * 16;
        }

        float[]? uvs = null;
        if (modernGeom)
        {
            var numUvSets = (bsVectorFlags & 0x0001) != 0 ? 1 : 0;
            if (numUvSets > 0 && pos + numVerts * 8 <= end)
            {
                uvs = NifGeometryDataReader.ReadUvs(data, pos, numVerts, be);
            }
        }
        else if (pos + 4 <= end)
        {
            var numUvSets = BinaryUtils.ReadUInt16(data, pos, be) & 0x3F;
            pos += 4;
            if (numUvSets > 0 && pos + numVerts * 8 <= end)
            {
                uvs = NifGeometryDataReader.ReadUvs(data, pos, numVerts, be);
            }
        }

        // Apply EGM morph deltas in bind-pose space BEFORE skinning transforms vertices
        if (preSkinMorphDeltas != null)
        {
            var count = Math.Min(positions.Length, preSkinMorphDeltas.Length);
            for (var i = 0; i < count; i++)
                positions[i] += preSkinMorphDeltas[i];
        }

        // Capture bind-pose world positions before skinning for boundary vertex stitching
        var bindPosePositions = skinning.HasValue
            ? NifGeometryTransformUtils.TransformPositions(positions, transform)
            : null;

        var transformed = ApplySkinningOrTransform(
            positions,
            normals,
            tangents,
            bitangents,
            transform,
            skinning,
            useDualQuaternionSkinning,
            shapeName);

        return new RenderableSubmesh
        {
            Positions = transformed.Positions,
            Triangles = triangles,
            Normals = transformed.Normals
                      ?? NifGeometryTransformUtils.RecomputeSmoothNormals(transformed.Positions, triangles),
            UVs = uvs,
            VertexColors = vertexColors,
            Tangents = transformed.Tangents,
            Bitangents = transformed.Bitangents,
            BindPosePositions = bindPosePositions
        };
    }

    /// <summary>
    ///     Extract geometry from a self-contained BSTriShape block (Skyrim SE / Fallout 4 /
    ///     Fallout 76). The interleaved vertex buffer is decoded per the BSVertexDesc bitfield, which
    ///     gives both the per-attribute presence flags (high 12 bits) and each attribute's byte offset
    ///     within the vertex. Positions are full float3 in Skyrim SE (and FO4 with the Full_Precision
    ///     flag) and half3 otherwise. Static path only: applies the node world transform, no skinning.
    /// </summary>
    internal static RenderableSubmesh? ExtractBsTriShape(
        byte[] data,
        BlockInfo block,
        bool be,
        uint bsVersion,
        uint binaryVersion,
        Matrix4x4 transform,
        string? shapeName = null)
    {
        var parsed = NifSceneGraphBlockReader.ParseBsTriShape(data, block, bsVersion, binaryVersion, be);
        if (parsed is not { } info || info.NumVertices == 0 || info.NumTriangles == 0)
        {
            return null;
        }

        var vertexSize = (int)(info.VertexDesc & 0xF) * 4;

        // Vertex Attributes occupy the high 12 bits of the descriptor (see nif.xml BSVertexDesc).
        var attributes = (info.VertexDesc >> 44) & 0xFFF;
        var hasNormal = (attributes & 0x8) != 0;
        var hasTangent = (attributes & 0x10) != 0;
        var hasUv = (attributes & 0x2) != 0;
        var hasColor = (attributes & 0x20) != 0;

        // Skyrim SE stores full-precision float3 positions; Fallout 4 uses half3 unless the
        // Full_Precision attribute (0x400) is set.
        var fullPrecisionPos = bsVersion < 130 || (attributes & 0x400) != 0;

        // Per-attribute byte offsets within the vertex (descriptor nibbles, in 4-byte units).
        var uvOffset = (int)((info.VertexDesc >> 8) & 0xF) * 4;
        var normalOffset = (int)((info.VertexDesc >> 16) & 0xF) * 4;
        var tangentOffset = (int)((info.VertexDesc >> 20) & 0xF) * 4;
        var colorOffset = (int)((info.VertexDesc >> 24) & 0xF) * 4;

        var positions = new float[info.NumVertices * 3];
        var normals = hasNormal ? new float[info.NumVertices * 3] : null;
        // The bitangent is stored split: X rides after the position (half/float), Y in the normal's
        // 4th byte, Z in the tangent's 4th byte — so reconstructing it needs both attributes present.
        // Without tangents+bitangents the decoder's hasBump gate drops the normal map (no TBN basis).
        var tangents = hasTangent ? new float[info.NumVertices * 3] : null;
        var bitangents = hasTangent && hasNormal ? new float[info.NumVertices * 3] : null;
        var uvs = hasUv ? new float[info.NumVertices * 2] : null;
        var colors = hasColor ? new byte[info.NumVertices * 4] : null;

        for (var v = 0; v < info.NumVertices; v++)
        {
            var vbase = info.VertexBufferOffset + v * vertexSize;

            if (fullPrecisionPos)
            {
                positions[v * 3] = BinaryUtils.ReadFloat(data, vbase, be);
                positions[v * 3 + 1] = BinaryUtils.ReadFloat(data, vbase + 4, be);
                positions[v * 3 + 2] = BinaryUtils.ReadFloat(data, vbase + 8, be);
            }
            else
            {
                positions[v * 3] = BinaryUtils.HalfToFloat(BinaryUtils.ReadUInt16(data, vbase, be));
                positions[v * 3 + 1] = BinaryUtils.HalfToFloat(BinaryUtils.ReadUInt16(data, vbase + 2, be));
                positions[v * 3 + 2] = BinaryUtils.HalfToFloat(BinaryUtils.ReadUInt16(data, vbase + 4, be));
            }

            if (normals != null)
            {
                // ByteVector3, [0,255] mapped to [-1,1].
                var n = vbase + normalOffset;
                normals[v * 3] = data[n] / 127.5f - 1f;
                normals[v * 3 + 1] = data[n + 1] / 127.5f - 1f;
                normals[v * 3 + 2] = data[n + 2] / 127.5f - 1f;
            }

            if (tangents != null)
            {
                var t = vbase + tangentOffset;
                tangents[v * 3] = data[t] / 127.5f - 1f;
                tangents[v * 3 + 1] = data[t + 1] / 127.5f - 1f;
                tangents[v * 3 + 2] = data[t + 2] / 127.5f - 1f;
                if (bitangents != null)
                {
                    bitangents[v * 3] = fullPrecisionPos
                        ? BinaryUtils.ReadFloat(data, vbase + 12, be)
                        : BinaryUtils.HalfToFloat(BinaryUtils.ReadUInt16(data, vbase + 6, be));
                    bitangents[v * 3 + 1] = data[vbase + normalOffset + 3] / 127.5f - 1f;
                    bitangents[v * 3 + 2] = data[t + 3] / 127.5f - 1f;
                }
            }

            if (uvs != null)
            {
                var u = vbase + uvOffset;
                uvs[v * 2] = BinaryUtils.HalfToFloat(BinaryUtils.ReadUInt16(data, u, be));
                uvs[v * 2 + 1] = BinaryUtils.HalfToFloat(BinaryUtils.ReadUInt16(data, u + 2, be));
            }

            if (colors != null)
            {
                // ByteColor4 — already RGBA bytes, matching RenderableSubmesh.VertexColors.
                var c = vbase + colorOffset;
                colors[v * 4] = data[c];
                colors[v * 4 + 1] = data[c + 1];
                colors[v * 4 + 2] = data[c + 2];
                colors[v * 4 + 3] = data[c + 3];
            }
        }

        // BSMeshLODTriShape partitions its triangle buffer into up to three consecutive LOD slices
        // (LOD0/LOD1/LOD2 sizes trail the block); each slice is a COMPLETE standalone representation
        // the engine picks by distance, not an additive refinement. Drawing the whole buffer renders
        // the full-detail mesh AND its simplified LOD copies on top of each other (z-fighting
        // duplicates). Render the first non-empty slice — the highest-detail representation
        // (TreeElmFree01: LOD0=1191 full tree; VineHanging05 authors everything in LOD2).
        var firstTriangle = 0;
        var triangleCount = info.NumTriangles;
        if (block.TypeName == "BSMeshLODTriShape" && block.Size >= 12)
        {
            var lodOffset = block.DataOffset + block.Size - 12;
            var lod0 = (int)BinaryUtils.ReadUInt32(data, lodOffset, be);
            var lod1 = (int)BinaryUtils.ReadUInt32(data, lodOffset + 4, be);
            var lod2 = (int)BinaryUtils.ReadUInt32(data, lodOffset + 8, be);
            if (lod0 > 0 || lod1 > 0 || lod2 > 0)
            {
                if (lod0 > 0)
                {
                    (firstTriangle, triangleCount) = (0, lod0);
                }
                else if (lod1 > 0)
                {
                    (firstTriangle, triangleCount) = (lod0, lod1);
                }
                else
                {
                    (firstTriangle, triangleCount) = (lod0 + lod1, lod2);
                }

                // A malformed partition (sizes exceeding the buffer) falls back to the full buffer.
                if (firstTriangle + triangleCount > info.NumTriangles)
                {
                    (firstTriangle, triangleCount) = (0, info.NumTriangles);
                }
            }
        }

        var triangles = new ushort[triangleCount * 3];
        for (var i = 0; i < triangles.Length; i++)
        {
            triangles[i] = BinaryUtils.ReadUInt16(data, info.TriangleBufferOffset + (firstTriangle * 3 + i) * 2, be);
        }

        var transformed =
            ApplySkinningOrTransform(positions, normals, tangents, bitangents, transform, null, false, shapeName);

        return new RenderableSubmesh
        {
            Positions = transformed.Positions,
            Triangles = triangles,
            Normals = transformed.Normals
                      ?? NifGeometryTransformUtils.RecomputeSmoothNormals(transformed.Positions, triangles),
            UVs = uvs,
            VertexColors = colors,
            Tangents = transformed.Tangents,
            Bitangents = transformed.Bitangents,
            BindPosePositions = null
        };
    }

    private static (
        float[] Positions,
        float[]? Normals,
        float[]? Tangents,
        float[]? Bitangents) ApplySkinningOrTransform(
            float[] positions,
            float[]? normals,
            float[]? tangents,
            float[]? bitangents,
            Matrix4x4 transform,
            ((int BoneIdx, float Weight)[][] PerVertexInfluences, Matrix4x4[] BoneSkinMatrices)? skinning,
            bool useDualQuaternionSkinning,
            string? shapeName)
    {
        if (!skinning.HasValue)
        {
            return (
                NifGeometryTransformUtils.TransformPositions(positions, transform),
                normals != null
                    ? NifGeometryTransformUtils.TransformNormals(normals, transform)
                    : null,
                tangents != null
                    ? NifGeometryTransformUtils.TransformNormals(tangents, transform)
                    : null,
                bitangents != null
                    ? NifGeometryTransformUtils.TransformNormals(bitangents, transform)
                    : null);
        }

        var (perVertexInfluences, boneSkinMatrices) = skinning.Value;
        if (useDualQuaternionSkinning)
        {
            var compatibility = NifSkinningExtractor.AnalyzeDualQuaternionCompatibility(
                boneSkinMatrices);
            if (!compatibility.CanUse)
            {
                Log.Debug(
                    "Submesh '{0}': using linear skinning instead of DQS; matrix {1} is non-rigid (scale={2:F3}/{3:F3}/{4:F3}, axisDot={5:F3}, det={6:F3})",
                    shapeName ?? "(unnamed)",
                    compatibility.MatrixIndex,
                    compatibility.ScaleX,
                    compatibility.ScaleY,
                    compatibility.ScaleZ,
                    compatibility.MaxAxisDot,
                    compatibility.Determinant);
            }
            else
            {
                return (
                    NifSkinningExtractor.ApplySkinningPositionsDQS(
                        positions,
                        perVertexInfluences,
                        boneSkinMatrices),
                    normals != null
                        ? NifSkinningExtractor.ApplySkinningNormalsDQS(
                            normals,
                            perVertexInfluences,
                            boneSkinMatrices)
                        : null,
                    tangents != null
                        ? NifSkinningExtractor.ApplySkinningNormalsDQS(
                            tangents,
                            perVertexInfluences,
                            boneSkinMatrices)
                        : null,
                    bitangents != null
                        ? NifSkinningExtractor.ApplySkinningNormalsDQS(
                            bitangents,
                            perVertexInfluences,
                            boneSkinMatrices)
                        : null);
            }
        }

        return (
            NifSkinningExtractor.ApplySkinningPositions(
                positions,
                perVertexInfluences,
                boneSkinMatrices),
            normals != null
                ? NifSkinningExtractor.ApplySkinningNormals(
                    normals,
                    perVertexInfluences,
                    boneSkinMatrices)
                : null,
            tangents != null
                ? NifSkinningExtractor.ApplySkinningNormals(
                    tangents,
                    perVertexInfluences,
                    boneSkinMatrices)
                : null,
            bitangents != null
                ? NifSkinningExtractor.ApplySkinningNormals(
                    bitangents,
                    perVertexInfluences,
                    boneSkinMatrices)
                : null);
    }
}
