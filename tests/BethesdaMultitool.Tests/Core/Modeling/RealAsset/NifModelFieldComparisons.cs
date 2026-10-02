using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Modeling.Nif;
using Slfx77.Multitool.Core.Models;
using static BethesdaMultitool.Tests.Core.Modeling.RealAsset.NifModelOracleSupport;
using static BethesdaMultitool.Tests.Core.Modeling.RealAsset.NifModelProbeVocabulary;

namespace BethesdaMultitool.Tests.Core.Modeling.RealAsset;

/// <summary>
///     Hop A1's comparisons, one per field family, between the reader's typed state (and the native rows that locate
///     it by block) and the probe's expectation record. Each returns every mismatch rather than the first, so a test can
///     run the same comparison on a mutated expectation as its control and assert that the mutation is what surfaces.
/// </summary>
/// <remarks>
///     Every comparison is keyed by block index: the reader's native rows name the block each typed value came from
///     (<c>bmt.nif.block</c> rows by index; a material row's <c>material.block</c>, <c>alpha.block</c>, ...; a primitive
///     row's <c>dataBlock</c>, <c>skin.skinInstance</c>, <c>morph.dataBlock</c>), and the expectation records the probe's
///     values under the same index. Where the reader typed a block the probe could not parse (a type outside the probe's
///     parser table), the element is noted, not failed. Typed values are compared on the document (materials, samplers,
///     layers, primitives, skins, layer sets), the native rows only locate them and carry the raw fields the document
///     does not (shader flag words, node flags).
/// </remarks>
internal static class NifModelFieldComparisons
{
    /// <summary>
    ///     Nodes: for every placed block, the probe's own name (Latin-1 header string; none and unresolved both null)
    ///     equals the block row's name text, its hidden bit equals the row's, its rotation check agrees with the TRS
    ///     rule (probe error above 1e-3 must be a matrix node; below 1e-6 must not be), the determinant signs agree, the
    ///     stored scale is bit-equal, an NiBillboardNode's mode equals the row's billboard mode, and a hidden block owns
    ///     the default-off <c>hidden:{block}</c> layer set over exactly its occurrences.
    /// </summary>
    public static NifModelFieldComparison Nodes(ModelDocument document, JsonObject expectation)
    {
        var mismatches = new List<string>();
        var notes = new List<string>();
        var compared = 0;
        var probeNodes = expectation["avObjects"]!.AsObject();
        foreach (var (index, row) in BlockRows(document).OrderBy(pair => pair.Key))
        {
            if (row["node"] is not JsonObject node)
            {
                continue;
            }

            var type = Text(row["type"]);
            if (!probeNodes.TryGetPropertyValue(Key(index), out var value) || value is not JsonObject probe)
            {
                notes.Add($"block {index} ({type}): the probe has no parser for it");
                continue;
            }

            var where = $"block {index} ({type})";
            var readerName = row["name"] is JsonObject name ? Text(name["text"]) : null;
            if (!string.Equals(readerName, Text(probe["name"]), StringComparison.Ordinal))
            {
                mismatches.Add($"{where}: name '{readerName}' vs probe '{Text(probe["name"])}'.");
            }

            var hidden = Bool(node["hidden"]);
            if (hidden != Bool(probe["hidden"]))
            {
                mismatches.Add($"{where}: hidden {hidden} vs probe {Bool(probe["hidden"])}.");
            }

            var transform = node["transform"]!.AsObject();
            var kind = Text(transform["kind"]);
            var rotationError = Double(probe["rotErr"]);
            var matrix = string.Equals(kind, "Matrix", StringComparison.Ordinal);
            if (rotationError > ProbeRotationTolerance && !matrix)
            {
                mismatches.Add($"{where}: the probe's rotation error {rotationError:R} exceeds 1e-3 but the reader chose {kind}.");
            }
            else if (rotationError < 1e-6 && matrix)
            {
                mismatches.Add($"{where}: the probe's rotation error {rotationError:R} is below 1e-6 but the reader chose a matrix node.");
            }

            if (transform["determinant"] is JsonValue determinant && determinant.TryGetValue<double>(out var det))
            {
                if (det < 0 != Double(probe["rotDet"]) < 0)
                {
                    mismatches.Add($"{where}: determinant sign {det:R} vs probe {Double(probe["rotDet"]):R}.");
                }
            }
            else
            {
                mismatches.Add($"{where}: the reader's determinant is not a finite number.");
            }

            if (!TryFloat(transform["scale"], out var scale) || !SameBits(Float(probe["scale"]), scale))
            {
                mismatches.Add($"{where}: scale {transform["scale"]} vs probe {probe["scale"]}.");
            }

            if (probe["billboardMode"] is { } billboardMode)
            {
                if (node["billboard"] is not JsonObject billboard)
                {
                    mismatches.Add($"{where}: the probe reads billboard mode {billboardMode} but the row has no billboard facts.");
                }
                else if (Long(billboard["mode"]) != Long(billboardMode))
                {
                    mismatches.Add($"{where}: billboard mode {billboard["mode"]} vs probe {billboardMode}.");
                }
            }

            if (hidden)
            {
                CompareHiddenLayer(document, index, node, where, mismatches);
            }

            compared++;
        }

        return new NifModelFieldComparison(mismatches, compared, notes);
    }

    /// <summary>
    ///     Materials: for every material row, the NiMaterialProperty it typed (alpha as the base color's W, glossiness,
    ///     specular, emissive, Emit Mult above BS 21, and below BS 26 ambient and the diffuse base color; all bit-equal),
    ///     the NiAlphaProperty (blend enabled, the source and destination terms for color and alpha as
    ///     <see cref="NifModelProbeVocabulary.BlendTerm" /> assigns the probe's names, Add, clamped; alpha test enabled,
    ///     its compare function and raw threshold; the draw order authored under No Sorter, else back to front), the
    ///     NiStencilProperty (draw mode, compare, reference, masks, the three operations, enabled), the NiZBufferProperty
    ///     (test, write, compare) and the NiVertexColorProperty (source and lighting modes).
    /// </summary>
    public static NifModelFieldComparison Materials(ModelDocument document, JsonObject expectation)
    {
        var mismatches = new List<string>();
        var notes = new List<string>();
        var compared = 0;
        foreach (var (index, payload) in IndexedRows(document, NifModelMaterialReader.MaterialKind))
        {
            var material = document.Materials[index];
            var state = material.RenderState;
            if (TypedFacts(payload["material"]) is { } materialFacts)
            {
                CompareMaterialProperty(material, materialFacts, expectation, mismatches);
                compared++;
            }

            if (TypedFacts(payload["alpha"]) is { } alphaFacts)
            {
                CompareAlphaProperty(state, alphaFacts, expectation, mismatches);
                compared++;
            }

            if (TypedFacts(payload["stencil"]) is { } stencilFacts)
            {
                CompareStencilProperty(state, stencilFacts, expectation, mismatches);
                compared++;
            }

            if (TypedFacts(payload["zBuffer"]) is { } depthFacts)
            {
                CompareDepthProperty(state, depthFacts, expectation, mismatches);
                compared++;
            }

            if (TypedFacts(payload["vertexColor"]) is { } colorFacts)
            {
                CompareVertexColorProperty(state, colorFacts, expectation, mismatches);
                compared++;
            }
        }

        return new NifModelFieldComparison(mismatches, compared, notes);
    }

    /// <summary>
    ///     Shaders: for every material row that typed a BSShader*Property, the shader type, both flag words and the
    ///     environment-map scale (bit-equal) equal the probe's; the material is unlit exactly for
    ///     BSShaderNoLightingProperty; every layer the shader or its texture set produced samples with the wrapping the
    ///     probe's Texture Clamp Mode names (repeat both ways when the property stores none); each non-empty texture-set
    ///     slot 0-5 is either a layer of the slot's role whose image is named by the probe's string or a recorded
    ///     omission, an empty slot has no layer, the environment layer's constant is the environment-map scale, and a
    ///     File Name shader binds its file as base color (or records why not).
    /// </summary>
    public static NifModelFieldComparison Shaders(ModelDocument document, JsonObject expectation)
    {
        var mismatches = new List<string>();
        var notes = new List<string>();
        var compared = 0;
        var probeShaders = expectation["shaders"]!.AsObject();
        var probeSets = expectation["textureSets"]!.AsObject();
        foreach (var (index, payload) in IndexedRows(document, NifModelMaterialReader.MaterialKind))
        {
            if (TypedFacts(payload["shade"]) is not { } shade || shade["shaderType"] is null)
            {
                continue;
            }

            var block = Int(shade["block"]);
            var where = $"material {index} shader block {block}";
            if (!probeShaders.TryGetPropertyValue(Key(block), out var value) || value is not JsonObject probe)
            {
                mismatches.Add($"{where}: the probe did not parse this shader block.");
                continue;
            }

            var material = document.Materials[index];
            var type = Text(shade["type"]);
            Expect(mismatches, where, "type", Text(probe["type"]), type);
            Expect(mismatches, where, "shaderType", Long(probe["shaderType"]), Long(shade["shaderType"]));
            Expect(mismatches, where, "shaderFlags1", Long(probe["flags1"]), Long(shade["shaderFlags1"]));
            Expect(mismatches, where, "shaderFlags2", Long(probe["flags2"]), Long(shade["shaderFlags2"]));
            if (!TryFloat(shade["environmentMapScale"], out var scale) || !SameBits(Float(probe["envMapScale"]), scale))
            {
                mismatches.Add($"{where}: environment map scale {shade["environmentMapScale"]} vs probe {probe["envMapScale"]}.");
            }

            if (probe["textureClampMode"] is { } clampMode)
            {
                Expect(mismatches, where, "textureClampMode", Long(clampMode), Long(shade["textureClampMode"]));
            }

            var unlit = string.Equals(type, "BSShaderNoLightingProperty", StringComparison.Ordinal);
            Expect(mismatches, where, "unlit", unlit, material.Unlit);

            var layers = payload["layers"]!.AsArray().OfType<JsonObject>().ToList();
            var omitted = payload["omittedLayers"]!.AsArray().OfType<JsonObject>().Select(o => Text(o["source"]))
                .ToList();
            (SceneTextureWrap U, SceneTextureWrap V) expectedWrap =
                probe["textureClampMode"] is { } mode && Long(mode) is >= 0 and <= 3
                    ? Clamp(Long(mode))
                    : (SceneTextureWrap.Repeat, SceneTextureWrap.Repeat);
            var textureSet = probe["textureSet"] is { } set ? Int(set) : -1;
            foreach (var layer in layers.Where(l => Int(l["block"]) == block || (textureSet >= 0 && Int(l["block"]) == textureSet)))
            {
                var sampler = document.Samplers[Int(layer["sampler"])];
                if (sampler.WrapU != expectedWrap.U || sampler.WrapV != expectedWrap.V)
                {
                    mismatches.Add($"{where}: layer '{Text(layer["source"])}' samples ({sampler.WrapU}, {sampler.WrapV}); the probe's clamp mode gives {expectedWrap}.");
                }
            }

            if (textureSet >= 0)
            {
                if (!probeSets.TryGetPropertyValue(Key(textureSet), out var setValue) || setValue is not JsonArray strings)
                {
                    mismatches.Add($"{where}: the probe did not parse texture set block {textureSet}.");
                }
                else
                {
                    CompareTextureSet(document, material, strings, layers, omitted, textureSet, Float(probe["envMapScale"]),
                        where, mismatches);
                }
            }

            if (probe["fileName"] is { } fileName && !string.IsNullOrWhiteSpace(Text(fileName)))
            {
                var layer = layers.FirstOrDefault(l => string.Equals(Text(l["source"]), "shader File Name", StringComparison.Ordinal));
                if (layer is null)
                {
                    if (!omitted.Contains("shader File Name"))
                    {
                        mismatches.Add($"{where}: the probe reads File Name '{Text(fileName)}' but the reader neither bound nor recorded omitting it.");
                    }
                }
                else
                {
                    ExpectImageName(mismatches, where + " File Name", Text(fileName) ?? "", document, Int(layer["image"]));
                    Expect(mismatches, where, "File Name role", nameof(SceneTextureLayerRole.BaseColor), Text(layer["role"]));
                }
            }

            compared++;
        }

        return new NifModelFieldComparison(mismatches, compared, notes);
    }

    /// <summary>
    ///     NiTexturingProperty: for every material row that typed one, the apply mode, texture count and each present map's
    ///     source block, flags, UV set, filter, clamp, transform presence and transform values (bit-equal) equal the
    ///     probe's, the map counts agree, and each map with an external NiSourceTexture is a layer named by that
    ///     texture's File Name (sampling with the probe's clamp and filter on the probe's UV set) or a recorded omission.
    /// </summary>
    public static NifModelFieldComparison Texturing(ModelDocument document, JsonObject expectation)
    {
        var mismatches = new List<string>();
        var notes = new List<string>();
        var compared = 0;
        var probeTexturing = expectation["texturing"]!.AsObject();
        var probeSources = expectation["sourceTextures"]!.AsObject();
        foreach (var (index, payload) in IndexedRows(document, NifModelMaterialReader.MaterialKind))
        {
            if (TypedFacts(payload["texturing"]) is not { } facts)
            {
                continue;
            }

            var block = Int(facts["block"]);
            var where = $"material {index} NiTexturingProperty block {block}";
            if (!probeTexturing.TryGetPropertyValue(Key(block), out var value) || value is not JsonObject probe)
            {
                mismatches.Add($"{where}: the probe did not parse this block.");
                continue;
            }

            Expect(mismatches, where, "applyMode", ApplyMode(Text(probe["applyMode"])!), Int(facts["applyMode"]));
            Expect(mismatches, where, "textureCount", Int(probe["textureCount"]), Int(facts["textureCount"]));
            var maps = facts["maps"]!.AsArray().OfType<JsonObject>().ToList();
            var probeMaps = probe["maps"]!.AsObject();
            Expect(mismatches, where, "map count", probeMaps.Count, maps.Count);
            var layers = payload["layers"]!.AsArray().OfType<JsonObject>().ToList();
            var omitted = payload["omittedLayers"]!.AsArray().OfType<JsonObject>().Select(o => Text(o["source"]))
                .ToList();
            foreach (var (probeSlot, probeMapValue) in probeMaps)
            {
                var slot = TexturingSlot(probeSlot);
                var probeMap = probeMapValue!.AsObject();
                var map = maps.FirstOrDefault(m => string.Equals(Text(m["slot"]), slot, StringComparison.Ordinal));
                var mapWhere = $"{where} {slot} map";
                if (map is null)
                {
                    mismatches.Add($"{mapWhere}: the probe reads it but the reader lists no such map.");
                    continue;
                }

                Expect(mismatches, mapWhere, "source", Int(probeMap["source"]), Int(map["source"]));
                Expect(mismatches, mapWhere, "flags", Int(probeMap["flags"]), Int(map["flags"]));
                Expect(mismatches, mapWhere, "uvSet", Int(probeMap["uvSet"]), Int(map["uvSet"]));
                Expect(mismatches, mapWhere, "hasTransform", Bool(probeMap["hasTransform"]), Bool(map["hasTextureTransform"]));
                if (Bool(probeMap["hasTransform"]) && map["transform"] is JsonObject transform)
                {
                    ExpectPair(mismatches, mapWhere, "translation", probeMap["translation"], transform["translation"]);
                    ExpectPair(mismatches, mapWhere, "scale", probeMap["scale"], transform["scale"]);
                    ExpectPair(mismatches, mapWhere, "center", probeMap["center"], transform["center"]);
                    if (!TryFloat(transform["rotation"], out var rotation) || !SameBits(Float(probeMap["rotation"]), rotation))
                    {
                        mismatches.Add($"{mapWhere}: rotation {transform["rotation"]} vs probe {probeMap["rotation"]}.");
                    }

                    Expect(mismatches, mapWhere, "method", Long(probeMap["method"]), Long(transform["method"]));
                }

                CompareTexturingLayer(document, probeSources, probeMap, slot, layers, omitted, mapWhere, mismatches);
            }

            compared++;
        }

        return new NifModelFieldComparison(mismatches, compared, notes);
    }

    /// <summary>
    ///     Geometry streams: for every primitive, the probe's NiTri*Data facts for its data block give the vertex count,
    ///     the UV set count (set 0 in the vertices, the rest as additional coordinates), whether normals are authored,
    ///     whether vertex colors are stored (a color attribute is then present), whether tangents are stored, the
    ///     triangle form, and the stored triangle count (lists) or strip count (strips). A data block whose Additional
    ///     Data names a BSPackedAdditionalGeometryData block the probe parsed (console files) is compared through
    ///     <see cref="ComparePackedGeometry" /> instead: its Has flags are 0 on every retail packed block (measured
    ///     2026-09-24 over the 443 packed data blocks of the cover manifest), so channel presence comes from the
    ///     probe's stream table matched against the measured layouts (<see cref="NifModelProbePackedLayouts" />).
    /// </summary>
    public static NifModelFieldComparison Geometry(ModelDocument document, JsonObject expectation)
    {
        var mismatches = new List<string>();
        var notes = new List<string>();
        var compared = 0;
        var probeData = expectation["geometryData"]!.AsObject();
        var probePacked = expectation["packed"] as JsonObject;
        foreach (var (geometryBlock, (primitive, payload)) in PrimitivesByGeometryBlock(document).OrderBy(p => p.Key))
        {
            var dataBlock = Int(payload["dataBlock"]);
            var where = $"geometry block {geometryBlock} data block {dataBlock}";
            if (!probeData.TryGetPropertyValue(Key(dataBlock), out var value) || value is not JsonObject probe)
            {
                mismatches.Add($"{where}: the probe did not parse the data block.");
                continue;
            }

            var additional = probe["additionalData"] is null ? -1 : Int(probe["additionalData"]);
            if (additional >= 0 && probePacked is not null &&
                probePacked.TryGetPropertyValue(Key(additional), out var packedValue) && packedValue is JsonObject packedProbe)
            {
                ComparePackedGeometry(primitive, payload, probe, packedProbe, expectation, additional, where, mismatches);
                compared++;
                continue;
            }

            if (payload["packed"] is JsonObject)
            {
                mismatches.Add($"{where}: the reader typed packed streams but the probe parsed no packed block for it (Additional Data {additional}).");
                continue;
            }

            Expect(mismatches, where, "numVertices", Int(probe["numVertices"]), primitive.Vertices.Count);
            var uvSets = Int(probe["uvSets"]);
            Expect(mismatches, where, "uvSets (row)", uvSets, Int(payload["uvSets"]));
            Expect(mismatches, where, "additional UV sets", Math.Max(0, uvSets - 1), primitive.AdditionalTextureCoordinates.Count);
            var authored = primitive.NormalProvenance?.Kind == SceneNormalProvenanceKind.Authored;
            Expect(mismatches, where, "normals authored", Int(probe["hasNormals"]) != 0, authored);
            Expect(mismatches, where, "normals (row)", Int(probe["hasNormals"]) != 0,
                string.Equals(Text(payload["normals"]), "authored", StringComparison.Ordinal));
            var colorsStored = Int(probe["hasVertexColors"]) != 0;
            var colorAttribute = primitive.Attributes.Any(a =>
                a.Name is NifModelGeometryData.VertexColorAttribute or NifModelGeometryData.RawVertexColorAttribute);
            Expect(mismatches, where, "vertex colors (attribute)", colorsStored, colorAttribute);
            Expect(mismatches, where, "vertex colors (row)", colorsStored,
                !string.Equals(Text(payload["vertexColors"]!["state"]), "absent", StringComparison.Ordinal));
            // nif.xml stores the Tangents arrays only with Has Normals; the probe's flag is the raw BS Data Flags bit.
            Expect(mismatches, where, "tangents stored", Bool(probe["hasTangents"]) && Int(probe["hasNormals"]) != 0,
                payload["tangents"] is JsonObject);
            CompareStaticTriangles(payload, probe, where, mismatches);
            compared++;
        }

        return new NifModelFieldComparison(mismatches, compared, notes);
    }

    /// <summary>
    ///     The triangle facts of a data block that keeps its own index buffer (inline streams, and the static packed
    ///     layouts, whose console index buffers equal the PC ones): the form, Num Triangles, and the strip count or
    ///     the stored list count.
    /// </summary>
    private static void CompareStaticTriangles(JsonObject payload, JsonObject probe, string where, List<string> mismatches)
    {
        var triangles = payload["triangles"]!.AsObject();
        Expect(mismatches, where, "triangle form", Text(probe["form"]), Text(triangles["form"]));
        Expect(mismatches, where, "numTriangles", Int(probe["numTriangles"]), Int(triangles["numTriangles"]));
        if (string.Equals(Text(probe["form"]), "strips", StringComparison.Ordinal))
        {
            Expect(mismatches, where, "numStrips", Int(probe["numStrips"]), Int(triangles["numStrips"]));
        }
        else if (Int(probe["hasTriangles"]) != 0)
        {
            Expect(mismatches, where, "stored triangles", Int(probe["numTriangles"]), Int(triangles["stored"]));
        }
    }

    /// <summary>
    ///     A console data block whose streams live in a BSPackedAdditionalGeometryData block: the probe's stream table
    ///     (type, unit size, offset per stream and the stride) must match one measured layout
    ///     (<see cref="NifModelProbePackedLayouts" />), the reader must have typed that layout with the same stream
    ///     table, packed block index, stride and packed vertex count, and the primitive's channels must be the layout's:
    ///     one UV set, authored normals, a color attribute exactly when the layout carries colors, and typed tangents
    ///     unless the reader counted non-finite tangent components. The data block's Num Vertices is the shape vertex
    ///     count (the point count of a skinned primitive, whose vertices are the concatenated partition maps). Skinned
    ///     layouts take their triangles from the NiSkinPartition: the partition count and per-partition vertex counts
    ///     equal the probe's skin facts, and a list partition's stored count equals the probe's; static layouts keep the
    ///     data block's own index buffer, compared as for inline streams.
    /// </summary>
    private static void ComparePackedGeometry(ScenePrimitive primitive, JsonObject payload, JsonObject probe,
        JsonObject packedProbe, JsonObject expectation, int packedBlock, string where, List<string> mismatches)
    {
        var layout = NifModelProbePackedLayouts.Match(packedProbe);
        if (layout is null)
        {
            mismatches.Add($"{where}: the probe's packed stream table ({NifModelProbePackedLayouts.KeyOf(packedProbe)}) " +
                           $"matches no measured layout, but the reader typed a primitive for it.");
            return;
        }

        if (payload["packed"] is not JsonObject packed)
        {
            mismatches.Add($"{where}: the probe parsed packed block {packedBlock} ({layout.Id}) but the reader typed inline streams.");
            return;
        }

        Expect(mismatches, where, "packed layout", layout.Id, Text(packed["layout"]));
        Expect(mismatches, where, "packed block", packedBlock, Int(packed["packedBlock"]));
        Expect(mismatches, where, "packed stride", layout.Stride, Int(packed["stride"]));
        Expect(mismatches, where, "packed vertices", Int(packedProbe["numVertices"]), Int(packed["packedVertices"]));
        Expect(mismatches, where, "packed vertices (primitive)", Int(packedProbe["numVertices"]), primitive.Vertices.Count);
        var probeStreams = packedProbe["streams"]!.AsArray().OfType<JsonObject>().ToList();
        var readerStreams = packed["streams"]!.AsArray().OfType<JsonObject>().ToList();
        Expect(mismatches, where, "packed stream count", probeStreams.Count, readerStreams.Count);
        for (var s = 0; s < Math.Min(probeStreams.Count, readerStreams.Count); s++)
        {
            Expect(mismatches, where, $"stream {s} type", Long(probeStreams[s]["type"]), Long(readerStreams[s]["type"]));
            Expect(mismatches, where, $"stream {s} unit size", Long(probeStreams[s]["unitSize"]), Long(readerStreams[s]["unitSize"]));
            Expect(mismatches, where, $"stream {s} offset", Long(probeStreams[s]["blockOffset"]), Long(readerStreams[s]["offset"]));
        }

        var shapeVertices = primitive.PointIndices?.PointCount ?? primitive.Vertices.Count;
        Expect(mismatches, where, "numVertices (shape)", Int(probe["numVertices"]), shapeVertices);
        Expect(mismatches, where, "skinned vertex order", layout.IsSkinned, primitive.PointIndices is not null);
        Expect(mismatches, where, "uvSets (row)", 1, Int(payload["uvSets"]));
        Expect(mismatches, where, "additional UV sets", 0, primitive.AdditionalTextureCoordinates.Count);
        Expect(mismatches, where, "normals authored", true,
            primitive.NormalProvenance?.Kind == SceneNormalProvenanceKind.Authored);
        Expect(mismatches, where, "normals (row)", "authored (packed channel)", Text(payload["normals"]));
        var colorAttribute = primitive.Attributes.Any(a =>
            a.Name is NifModelGeometryData.VertexColorAttribute or NifModelGeometryData.RawVertexColorAttribute);
        Expect(mismatches, where, "vertex colors (attribute)", layout.HasVertexColors, colorAttribute);
        Expect(mismatches, where, "vertex colors (row)", layout.HasVertexColors,
            !string.Equals(Text(payload["vertexColors"]!["state"]), "absent", StringComparison.Ordinal));
        if (payload["tangents"] is not JsonObject tangents)
        {
            mismatches.Add($"{where}: layout {layout.Id} carries a tangent frame but the row has no tangent facts.");
        }
        else
        {
            var nonFinite = packed["tangentFrameNonFiniteComponents"] is { } count ? Int(count) : 0;
            Expect(mismatches, where, "tangents typed", nonFinite == 0, Bool(tangents["typed"]));
            Expect(mismatches, where, "tangents (primitive)", nonFinite == 0, primitive.Tangents is not null);
        }

        if (!layout.IsSkinned)
        {
            CompareStaticTriangles(payload, probe, where, mismatches);
            return;
        }

        var triangles = payload["triangles"]!.AsObject();
        Expect(mismatches, where, "triangle form", "partitions", Text(triangles["form"]));
        if (payload["skin"] is not JsonObject skin || skin["skinInstance"] is null)
        {
            mismatches.Add($"{where}: layout {layout.Id} is skinned but the row names no skin instance.");
            return;
        }

        var skinInstance = Key(Int(skin["skinInstance"]));
        if (!expectation["skins"]!.AsObject().TryGetPropertyValue(skinInstance, out var skinValue) ||
            skinValue is not JsonObject probeSkin || probeSkin["partitionVertices"] is not JsonArray probeVertices)
        {
            mismatches.Add($"{where}: the probe did not parse skin instance {skinInstance} or its partition.");
            return;
        }

        var partitions = triangles["partitions"]!.AsArray().OfType<JsonObject>().ToList();
        Expect(mismatches, where, "partitions", probeVertices.Count, partitions.Count);
        var probeTriangles = probeSkin["partitionTriangles"]!.AsArray();
        for (var k = 0; k < Math.Min(probeVertices.Count, partitions.Count); k++)
        {
            Expect(mismatches, where, $"partition {k} vertices", Int(probeVertices[k]), Int(partitions[k]["numVertices"]));
            if (string.Equals(Text(partitions[k]["form"]), "list", StringComparison.Ordinal))
            {
                Expect(mismatches, where, $"partition {k} stored triangles", Int(probeTriangles[k]), Int(partitions[k]["stored"]));
            }
        }
    }

    /// <summary>
    ///     Skins: for every primitive whose skin the reader typed, the instance type, data and partition links, the bone
    ///     count (the document skin's joints and inverse binds), NiSkinData's Has Vertex Weights, the partition count and,
    ///     for a typed dismember instance, the per-face body parts: every face value is one of the probe's body parts and
    ///     every partition with triangles contributes its body part (list partitions; strip partitions are not counted).
    /// </summary>
    public static NifModelFieldComparison Skins(ModelDocument document, JsonObject expectation)
    {
        var mismatches = new List<string>();
        var notes = new List<string>();
        var compared = 0;
        var probeSkins = expectation["skins"]!.AsObject();
        foreach (var (geometryBlock, (primitive, payload)) in PrimitivesByGeometryBlock(document).OrderBy(p => p.Key))
        {
            if (payload["skin"] is not JsonObject skin || !Bool(skin["typed"]))
            {
                continue;
            }

            var block = Int(skin["skinInstance"]);
            var where = $"geometry block {geometryBlock} skin instance {block}";
            if (!probeSkins.TryGetPropertyValue(Key(block), out var value) || value is not JsonObject probe)
            {
                mismatches.Add($"{where}: the probe did not parse the skin instance.");
                continue;
            }

            Expect(mismatches, where, "type", Text(probe["type"]), Text(skin["skinInstanceType"]));
            Expect(mismatches, where, "skinData", Int(probe["data"]), Int(skin["skinData"]));
            Expect(mismatches, where, "skinPartition", Int(probe["skinPartition"]), Int(skin["skinPartition"]));
            if (probe["hasVertexWeights"] is { } weights)
            {
                Expect(mismatches, where, "hasVertexWeights", Long(weights), Long(skin["hasVertexWeights"]));
            }

            var skinIndex = Int(skin["skins"]!.AsArray()[0]);
            var typed = document.Skins[skinIndex];
            Expect(mismatches, where, "joints", Int(probe["bones"]), typed.JointNodeIndices.Count);
            Expect(mismatches, where, "inverse binds", Int(probe["bones"]), typed.InverseBindMatrices.Count);
            if (probe["partitions"] is { } partitions && skin["partitions"] is JsonObject partitionFacts)
            {
                Expect(mismatches, where, "partitions", Int(partitions), Int(partitionFacts["count"]));
            }

            if (probe["bodyParts"] is JsonArray bodyParts && skin["dismember"] is JsonObject dismember &&
                Bool(dismember["typed"]))
            {
                CompareBodyParts(primitive, probe, bodyParts, where, mismatches);
            }

            compared++;
        }

        return new NifModelFieldComparison(mismatches, compared, notes);
    }

    /// <summary>
    ///     Morphs: for every primitive with a morpher, the NiMorphData's morph count, vertex count and Relative Targets
    ///     equal the probe's, the controller links that data block, and when typed the primitive carries morph count
    ///     minus one targets named by the probe's frame names 1..n.
    /// </summary>
    public static NifModelFieldComparison Morphs(ModelDocument document, JsonObject expectation)
    {
        var mismatches = new List<string>();
        var notes = new List<string>();
        var compared = 0;
        var probeMorphs = expectation["morphData"]!.AsObject();
        var probeMorphers = expectation["morphers"]!.AsObject();
        foreach (var (geometryBlock, (primitive, payload)) in PrimitivesByGeometryBlock(document).OrderBy(p => p.Key))
        {
            if (payload["morph"] is not JsonObject morph || morph["dataBlock"] is null || morph["numMorphs"] is null)
            {
                continue;
            }

            var dataBlock = Int(morph["dataBlock"]);
            var where = $"geometry block {geometryBlock} morph data {dataBlock}";
            if (!probeMorphs.TryGetPropertyValue(Key(dataBlock), out var value) || value is not JsonObject probe)
            {
                mismatches.Add($"{where}: the probe did not parse the NiMorphData.");
                continue;
            }

            Expect(mismatches, where, "numMorphs", Int(probe["numMorphs"]), Int(morph["numMorphs"]));
            Expect(mismatches, where, "numVertices", Int(probe["numVertices"]), Int(morph["numVertices"]));
            Expect(mismatches, where, "relativeTargets", Int(probe["relativeTargets"]), Int(morph["relativeTargets"]));
            var controller = Key(Int(morph["controllerBlock"]));
            if (!probeMorphers.TryGetPropertyValue(controller, out var morpherValue) || morpherValue is not JsonObject morpher)
            {
                mismatches.Add($"{where}: the probe did not parse morpher controller {controller}.");
            }
            else
            {
                Expect(mismatches, where, "controller data link", dataBlock, Int(morpher["data"]));
            }

            if (Bool(morph["typed"]))
            {
                var names = probe["frameNames"]!.AsArray().Select(n => Text(n) ?? "").ToList();
                Expect(mismatches, where, "targets", Math.Max(0, Int(probe["numMorphs"]) - 1), primitive.MorphTargets.Count);
                for (var t = 0; t < Math.Min(primitive.MorphTargets.Count, Math.Max(0, names.Count - 1)); t++)
                {
                    Expect(mismatches, $"{where} target {t}", "name", names[t + 1], primitive.MorphTargets[t].Name);
                }
            }

            compared++;
        }

        return new NifModelFieldComparison(mismatches, compared, notes);
    }

    private static void CompareHiddenLayer(ModelDocument document, int index, JsonObject node, string where,
        List<string> mismatches)
    {
        var id = $"hidden:{index}";
        var set = document.LayerSets.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal));
        if (set is null)
        {
            mismatches.Add($"{where}: hidden, but the document has no layer set '{id}'.");
            return;
        }

        Expect(mismatches, where, "hidden layer source kind", NifModelLayerReader.HiddenSourceKind, set.SourceKind);
        Expect(mismatches, where, "hidden layer default", false, set.DefaultOn);
        if (node["occurrences"] is JsonArray occurrences)
        {
            var expected = occurrences.Select(o => Int(o)).Order().ToList();
            if (!expected.SequenceEqual(set.Members.Order()))
            {
                mismatches.Add($"{where}: hidden layer members [{string.Join(",", set.Members)}] vs occurrences [{string.Join(",", expected)}].");
            }
        }
    }

    private static void CompareMaterialProperty(SceneMaterial material, JsonObject facts, JsonObject expectation,
        List<string> mismatches)
    {
        var block = Int(facts["block"]);
        var where = $"material '{material.Name}' NiMaterialProperty block {block}";
        if (!expectation["materials"]!.AsObject().TryGetPropertyValue(Key(block), out var value) || value is not JsonObject probe)
        {
            mismatches.Add($"{where}: the probe did not parse this block.");
            return;
        }

        var source = material.Source!;
        if (!SameBits(Float(probe["alpha"]), material.BaseColor.W))
        {
            mismatches.Add($"{where}: base color alpha {material.BaseColor.W:R} vs probe {probe["alpha"]}.");
        }

        // Optional values the reader leaves undeclared when the block stores a non-finite number.
        ExpectOptionalFloat(mismatches, where, "glossiness", probe["glossiness"], material.Glossiness);
        ExpectOptionalColor(mismatches, where, "specular", probe["specular"], material.SpecularColor);
        ExpectOptionalColor(mismatches, where, "emissive", probe["emissive"], source.EmissiveColor);
        if (probe["emissiveMult"] is { } multiplier)
        {
            ExpectOptionalFloat(mismatches, where, "Emit Mult", multiplier, source.EmissiveMultiplier);
        }
        else if (source.EmissiveMultiplier is not null)
        {
            mismatches.Add($"{where}: the reader typed Emit Mult {source.EmissiveMultiplier} but the block stores none (BS 21 or below).");
        }

        var rgb = new Vector3(material.BaseColor.X, material.BaseColor.Y, material.BaseColor.Z);
        if (probe["diffuse"] is { } diffuse)
        {
            if (!SameBits(diffuse, rgb))
            {
                mismatches.Add($"{where}: base color {Show(rgb)} vs probe diffuse {diffuse}.");
            }

            ExpectOptionalColor(mismatches, where, "ambient", probe["ambient"], source.AmbientColor);
        }
        else if (rgb != Vector3.One)
        {
            mismatches.Add($"{where}: base color {Show(rgb)}, expected white (no Diffuse Color at BS 26 and above).");
        }
    }

    /// <summary>A finite probe number must be typed bit for bit; a non-finite one must be left undeclared.</summary>
    private static void ExpectOptionalFloat(List<string> mismatches, string where, string field, JsonNode? probe,
        float? actual)
    {
        if (!TryFloat(probe, out var expected))
        {
            if (actual is not null)
            {
                mismatches.Add($"{where}: {field} {actual} typed from a non-finite stored value {probe}.");
            }
        }
        else if (actual is not { } value || !SameBits(expected, value))
        {
            mismatches.Add($"{where}: {field} {actual} vs probe {probe}.");
        }
    }

    /// <summary>A finite probe color must be typed bit for bit; one with a non-finite component must be undeclared.</summary>
    private static void ExpectOptionalColor(List<string> mismatches, string where, string field, JsonNode? probe,
        Vector3? actual)
    {
        var triple = probe!.AsArray();
        var finite = TryFloat(triple[0], out _) && TryFloat(triple[1], out _) && TryFloat(triple[2], out _);
        if (!finite)
        {
            if (actual is not null)
            {
                mismatches.Add($"{where}: {field} {actual} typed from a non-finite stored color {probe}.");
            }
        }
        else if (actual is not { } value || !SameBits(probe, value))
        {
            mismatches.Add($"{where}: {field} {(actual is { } shown ? Show(shown) : "null")} vs probe {probe}.");
        }
    }

    private static void CompareAlphaProperty(SceneRenderState? state, JsonObject facts, JsonObject expectation,
        List<string> mismatches)
    {
        var block = Int(facts["block"]);
        var where = $"NiAlphaProperty block {block}";
        if (!expectation["alpha"]!.AsObject().TryGetPropertyValue(Key(block), out var value) || value is not JsonObject probe)
        {
            mismatches.Add($"{where}: the probe did not parse this block.");
            return;
        }

        if (state is null)
        {
            mismatches.Add($"{where}: the material has no render state.");
            return;
        }

        var source = Text(probe["src"])!;
        var destination = Text(probe["dst"])!;
        if (TryBlendTerm(source, out var sourceTerm) && TryBlendTerm(destination, out var destinationTerm))
        {
            if (state.Blend is not { } blend)
            {
                mismatches.Add($"{where}: no blend state for {source}/{destination}.");
            }
            else
            {
                Expect(mismatches, where, "blend enabled", Bool(probe["blend"]), blend.Enabled);
                foreach (var (label, equation) in new[] { ("color", blend.ColorEquation), ("alpha", blend.AlphaEquation) })
                {
                    Expect(mismatches, where, label + " source factor", sourceTerm, equation.SourceFactor);
                    Expect(mismatches, where, label + " destination factor", destinationTerm, equation.DestinationFactor);
                    Expect(mismatches, where, label + " operation", SceneBlendOperation.Add, equation.Operation);
                    Expect(mismatches, where, label + " clamp", true, equation.Clamp);
                }
            }
        }
        else if (state.Blend is not null)
        {
            mismatches.Add($"{where}: the probe names an undefined blend function ({source}/{destination}) but the reader typed a blend.");
        }

        if (state.AlphaTest is not { } test)
        {
            mismatches.Add($"{where}: no alpha test state.");
        }
        else
        {
            Expect(mismatches, where, "test enabled", Bool(probe["test"]), test.Enabled);
            Expect(mismatches, where, "test compare", TestFunction(Text(probe["testFunc"])!), test.Compare);
            Expect(mismatches, where, "raw threshold", (ulong)Int(probe["threshold"]), test.RawReference ?? ulong.MaxValue);
            Expect(mismatches, where, "threshold", Int(probe["threshold"]) / 255.0, test.Reference);
        }

        if (Bool(probe["blend"]))
        {
            var expected = Bool(probe["noSorter"]) ? SceneDrawSort.Authored : SceneDrawSort.BackToFront;
            if (state.DrawOrder is not { } order)
            {
                mismatches.Add($"{where}: blending but no draw order.");
            }
            else
            {
                Expect(mismatches, where, "draw sort", expected, order.Sort);
            }
        }
    }

    private static void CompareStencilProperty(SceneRenderState? state, JsonObject facts, JsonObject expectation,
        List<string> mismatches)
    {
        var block = Int(facts["block"]);
        var where = $"NiStencilProperty block {block}";
        if (!expectation["stencil"]!.AsObject().TryGetPropertyValue(Key(block), out var value) || value is not JsonObject probe)
        {
            mismatches.Add($"{where}: the probe did not parse this block.");
            return;
        }

        if (state?.Stencil is not { } stencil)
        {
            mismatches.Add($"{where}: no stencil state.");
            return;
        }

        Expect(mismatches, where, "draw mode", StencilDrawMode(Text(probe["drawMode"])!), stencil.DrawMode);
        var actions = new[] { Text(probe["failAction"])!, Text(probe["zFailAction"])!, Text(probe["passAction"])! };
        if (actions.All(IsStencilAction))
        {
            if (stencil.Test is not { } test)
            {
                mismatches.Add($"{where}: no stencil test although every action is defined.");
                return;
            }

            Expect(mismatches, where, "enabled", Bool(probe["enable"]), test.Enabled);
            Expect(mismatches, where, "compare", StencilTest(Text(probe["testFunc"])!), test.Compare);
            Expect(mismatches, where, "reference", (uint)Long(probe["stencilRef"]), test.Reference);
            Expect(mismatches, where, "read mask", (uint)Long(probe["stencilMask"]), test.ReadMask);
            Expect(mismatches, where, "write mask", (uint)Long(probe["stencilMask"]), test.WriteMask);
            Expect(mismatches, where, "fail", StencilAction(actions[0]), test.FailOperation);
            Expect(mismatches, where, "depth fail", StencilAction(actions[1]), test.DepthFailOperation);
            Expect(mismatches, where, "pass", StencilAction(actions[2]), test.PassOperation);
        }
        else if (stencil.Test is not null)
        {
            mismatches.Add($"{where}: an undefined stencil action but the reader typed a stencil test.");
        }
    }

    private static void CompareDepthProperty(SceneRenderState? state, JsonObject facts, JsonObject expectation,
        List<string> mismatches)
    {
        var block = Int(facts["block"]);
        var where = $"NiZBufferProperty block {block}";
        if (!expectation["zbuffer"]!.AsObject().TryGetPropertyValue(Key(block), out var value) || value is not JsonObject probe)
        {
            mismatches.Add($"{where}: the probe did not parse this block.");
            return;
        }

        if (state?.Depth is not { } depth)
        {
            mismatches.Add($"{where}: no depth state.");
            return;
        }

        Expect(mismatches, where, "test", Bool(probe["zTest"]), depth.Test);
        Expect(mismatches, where, "write", Bool(probe["zWrite"]), depth.Write);
        Expect(mismatches, where, "compare", TestFunction(Text(probe["testFunc"])!), depth.Compare);
    }

    private static void CompareVertexColorProperty(SceneRenderState? state, JsonObject facts, JsonObject expectation,
        List<string> mismatches)
    {
        var block = Int(facts["block"]);
        var where = $"NiVertexColorProperty block {block}";
        if (!expectation["vertexColor"]!.AsObject().TryGetPropertyValue(Key(block), out var value) || value is not JsonObject probe)
        {
            mismatches.Add($"{where}: the probe did not parse this block.");
            return;
        }

        var sourceName = Text(probe["sourceVertexMode"])!;
        if (!sourceName.StartsWith("SRC_", StringComparison.Ordinal))
        {
            if (state?.VertexColorUse is not null)
            {
                mismatches.Add($"{where}: an undefined source mode ({sourceName}) but the reader typed vertex-color use.");
            }

            return;
        }

        if (state?.VertexColorUse is not { } use)
        {
            mismatches.Add($"{where}: no vertex-color use.");
            return;
        }

        Expect(mismatches, where, "source", VertexColorSource(sourceName), use.Source);
        Expect(mismatches, where, "lighting", LightingMode(Text(probe["lightingMode"])!), use.Lighting);
    }

    private static void CompareTextureSet(ModelDocument document, SceneMaterial material, JsonArray strings,
        List<JsonObject> layers, List<string?> omitted, int textureSet, float environmentScale, string where,
        List<string> mismatches)
    {
        for (var slot = 0; slot < Math.Min(strings.Count, TextureSetRoles.Count); slot++)
        {
            var authored = Text(strings[slot]) ?? "";
            var origin = string.Create(CultureInfo.InvariantCulture, $"texture set slot {slot}");
            var layer = layers.FirstOrDefault(l => string.Equals(Text(l["source"]), origin, StringComparison.Ordinal));
            var slotWhere = $"{where} texture set {textureSet} slot {slot}";
            if (string.IsNullOrWhiteSpace(authored))
            {
                if (layer is not null)
                {
                    mismatches.Add($"{slotWhere}: empty in the probe but the reader bound a layer.");
                }

                continue;
            }

            if (layer is null)
            {
                // The reader omits a whole set ("texture set") when the geometry stores no UVs, else one slot.
                if (!omitted.Contains(origin) && !omitted.Contains("texture set"))
                {
                    mismatches.Add($"{slotWhere}: '{authored}' is neither bound nor recorded as omitted.");
                }

                continue;
            }

            Expect(mismatches, slotWhere, "role", TextureSetRoles[slot].ToString(), Text(layer["role"]));
            ExpectImageName(mismatches, slotWhere, authored, document, Int(layer["image"]));
            if (TextureSetRoles[slot] == SceneTextureLayerRole.Environment)
            {
                var typed = material.Layers.FirstOrDefault(l => l.Role == SceneTextureLayerRole.Environment);
                if (typed is null || !SameBits(environmentScale, typed.Constant.X))
                {
                    mismatches.Add($"{slotWhere}: environment constant {typed?.Constant} vs probe env map scale {environmentScale:R}.");
                }
            }
        }
    }

    private static void CompareTexturingLayer(ModelDocument document, JsonObject probeSources, JsonObject probeMap,
        string slot, List<JsonObject> layers, List<string?> omitted, string where, List<string> mismatches)
    {
        var source = Int(probeMap["source"]);
        if (source < 0 || string.Equals(slot, "Bump Map", StringComparison.Ordinal))
        {
            return;
        }

        if (!probeSources.TryGetPropertyValue(Key(source), out var value) || value is not JsonObject texture ||
            Int(texture["useExternal"]) == 0 || Text(texture["fileName"]) is not { } fileName)
        {
            return;
        }

        var origin = "NiTexturingProperty " + slot + " map";
        var layer = layers.FirstOrDefault(l => string.Equals(Text(l["source"]), origin, StringComparison.Ordinal));
        if (layer is null)
        {
            if (!omitted.Contains(origin))
            {
                mismatches.Add($"{where}: '{fileName}' is neither bound nor recorded as omitted.");
            }

            return;
        }

        ExpectImageName(mismatches, where, fileName, document, Int(layer["image"]));
        Expect(mismatches, where, "layer uvSet", Int(probeMap["uvSet"]), Int(layer["uvSet"]));
        var sampler = document.Samplers[Int(layer["sampler"])];
        var wrap = Clamp(Text(probeMap["clamp"])!);
        var filter = Filter(Text(probeMap["filter"])!);
        Expect(mismatches, where, "wrap U", wrap.U, sampler.WrapU);
        Expect(mismatches, where, "wrap V", wrap.V, sampler.WrapV);
        Expect(mismatches, where, "min filter", filter.Min, sampler.MinFilter);
        Expect(mismatches, where, "mag filter", filter.Mag, sampler.MagFilter);
    }

    private static void CompareBodyParts(ScenePrimitive primitive, JsonObject probe, JsonArray bodyParts, string where,
        List<string> mismatches)
    {
        var stream = primitive.Attributes.FirstOrDefault(a =>
            string.Equals(a.Name, NifModelDismemberFaces.BodyPartAttribute, StringComparison.Ordinal));
        if (stream is null)
        {
            mismatches.Add($"{where}: typed dismember but no '{NifModelDismemberFaces.BodyPartAttribute}' face stream.");
            return;
        }

        var declared = bodyParts.Select(b => (ushort)Int(b)).ToList();
        var faces = new HashSet<ushort>();
        for (var face = 0; face < stream.Count; face++)
        {
            faces.Add(BinaryPrimitives.ReadUInt16LittleEndian(stream.GetTupleBytes(face)));
        }

        foreach (var part in faces.Where(part => !declared.Contains(part)))
        {
            mismatches.Add($"{where}: face body part {part} is not among the probe's [{string.Join(",", declared)}].");
        }

        var stripped = probe["stripped"] is JsonValue strips && strips.TryGetValue<bool>(out var flag) && flag;
        if (probe["partitionTriangles"] is JsonArray triangles && !stripped)
        {
            for (var k = 0; k < Math.Min(triangles.Count, declared.Count); k++)
            {
                if (Int(triangles[k]) > 0 && !faces.Contains(declared[k]))
                {
                    mismatches.Add($"{where}: partition {k} has {Int(triangles[k])} triangles but body part {declared[k]} reaches no face.");
                }
            }
        }
    }

    /// <summary>A facts object that names its block and that the reader did not mark <c>typed: false</c>, else null.</summary>
    private static JsonObject? TypedFacts(JsonNode? facts)
    {
        if (facts is not JsonObject o || o["block"] is null)
        {
            return null;
        }

        return o["typed"] is JsonValue typed && typed.TryGetValue<bool>(out var flag) && !flag ? null : o;
    }

    private static bool TryBlendTerm(string name, out SceneBlendTerm term)
    {
        try
        {
            term = BlendTerm(name);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            term = SceneBlendTerm.Zero;
            return false;
        }
    }

    private static bool IsStencilAction(string name)
    {
        return name is "KEEP" or "ZERO" or "REPLACE" or "INCREMENT" or "DECREMENT" or "INVERT";
    }

    private static void ExpectPair(List<string> mismatches, string where, string field, JsonNode? probe, JsonNode? reader)
    {
        var expected = probe!.AsArray();
        var actual = reader!.AsArray();
        if (!TryFloat(actual[0], out var x) || !TryFloat(actual[1], out var y) ||
            !SameBits(Float(expected[0]), x) || !SameBits(Float(expected[1]), y))
        {
            mismatches.Add($"{where}: {field} {reader} vs probe {probe}.");
        }
    }

    /// <summary>
    ///     Compares a bound image's name with the probe's authored string: equal ordinally, or equal ignoring case when
    ///     the document holds no image under the exact authored spelling. The reader deduplicates texture requests by
    ///     their normalized lookup key, so a file naming one texture twice with different casing (X360
    ///     architecture/vault22/vault22entrance.nif: VMeshR01.dds and VMeshr01.dds) yields one image named by the first
    ///     spelling; a reader that bound a different file, or that kept both spellings and bound the wrong one, still
    ///     mismatches.
    /// </summary>
    private static void ExpectImageName(List<string> mismatches, string where, string authored, ModelDocument document,
        int imageIndex)
    {
        var name = document.Images[imageIndex].Name;
        if (string.Equals(authored, name, StringComparison.Ordinal))
        {
            return;
        }

        if (string.Equals(authored, name, StringComparison.OrdinalIgnoreCase) &&
            !document.Images.Any(image => string.Equals(image.Name, authored, StringComparison.Ordinal)))
        {
            return;
        }

        mismatches.Add($"{where}: image {name} vs probe {authored}.");
    }

    private static void Expect<T>(List<string> mismatches, string where, string field, T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            mismatches.Add($"{where}: {field} {actual} vs probe {expected}.");
        }
    }

    private static string Key(int block)
    {
        return block.ToString(CultureInfo.InvariantCulture);
    }
}
