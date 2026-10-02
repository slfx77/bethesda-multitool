using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Export;

/// <summary>
///     Snapshots an assembled <see cref="GlbScene" /> as a shared <see cref="ModelDocument" />, or reports the
///     Bethesda capability that keeps <see cref="GlbWriter" /> responsible for it.
/// </summary>
/// <remarks>
///     <para>
///         This sits at the <see cref="GlbWriter" /> boundary rather than in front of it, so it observes the same
///         prepared geometry the native writer does. It reuses the writer's winding, normal and no-draw rules
///         instead of restating them, which is what makes the neutral and native paths provably agree rather than
///         agree by inspection.
///     </para>
///     <para>
///         <b>Everything crosses the Z-up to Y-up basis.</b> Positions, normals, tangent directions, node local
///         transforms and every inverse bind matrix pass through <see cref="GltfCoordinateAdapter" />. An earlier
///         attempt adapted the writer's <i>input</i> and skipped this, which is a silent 90-degree rotation on
///         every export; that attempt is kept under <c>docs/failed-attempts/</c>.
///     </para>
///     <para>
///         Per-vertex skin influences are carried in full. The native writer caps them at four through its packed
///         joint vertex; <see cref="SceneSkinInfluences" /> has no such limit, so this path is strictly more
///         faithful on that axis and must not be written down to match the cap.
///     </para>
///     <para>
///         Every state <see cref="SceneValidation" /> rejects is declined here first, with its own reason, using
///         the validator's own tests on the values it would inspect. A caller choosing between the writers
///         therefore receives a decline rather than an <see cref="InvalidDataException" /> for any source the
///         native writer still accepts; the validator keeps running afterwards as the backstop.
///     </para>
/// </remarks>
internal static class NifNeutralSceneAdapter
{
    /// <summary>Copies an eligible scene or reports the Bethesda capability that requires the existing writer.</summary>
    /// <param name="source">The assembled scene graph, already prepared by the exporter.</param>
    /// <param name="textureResolver">Resolves the same source textures and material policy used by the native writer.</param>
    /// <param name="name">The document label, normally the source stem.</param>
    /// <param name="scene">The neutral snapshot when this returns true.</param>
    /// <param name="unsupportedReason">The declining capability when this returns false.</param>
    /// <param name="cancellationToken">Cancels a large graph walk.</param>
    /// <returns>True when the scene was snapshotted; false when the native writer retains it.</returns>
    internal static bool TryAdapt(
        GlbScene source,
        NifTextureResolver textureResolver,
        string name,
        [NotNullWhen(true)] out ModelDocument? scene,
        out string? unsupportedReason,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(textureResolver);
        ArgumentNullException.ThrowIfNull(name);
        cancellationToken.ThrowIfCancellationRequested();

        scene = null;
        var drawable = Drawable(source, textureResolver);
        unsupportedReason = UnsupportedReason(source, drawable, cancellationToken);
        if (unsupportedReason is not null) return false;

        var preparedCache = new Dictionary<NifMaterialCacheKey, NifPreparedMaterial>();
        var preparedBySubmesh = new Dictionary<RenderableSubmesh, NifPreparedMaterial>(ReferenceEqualityComparer.Instance);
        foreach (var (_, part) in drawable)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var projection = StarfieldGlbVertexLerpProjection.Resolve(part.Submesh);
            if (projection.IsUnsupported || projection.RequiresViewerShader)
            {
                unsupportedReason = "A specialized vertex-color projection retains the native material writer.";
                return false;
            }
            var prepared = NifMaterialPreparation.Prepare(part.Submesh, textureResolver, preparedCache, projection);
            cancellationToken.ThrowIfCancellationRequested();
            if (PreparedMaterialReason(prepared) is { } reason)
            {
                unsupportedReason = reason;
                return false;
            }
            preparedBySubmesh[part.Submesh] = prepared;
        }

        // Rigid geometry retains its existing owning-node grouping. The legacy writer adds each skin
        // independently from its palette and inverse binds; the optional mesh-part NodeIndex is not
        // a skin transform. Skinned occurrences therefore receive separate neutral placements below.
        var partsByNode = new SortedDictionary<int, List<GlbMeshPart>>();
        foreach (var (_, part) in drawable)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (part.Skin is not null) continue;
            if (!partsByNode.TryGetValue(part.NodeIndex!.Value, out var list))
            {
                list = [];
                partsByNode[part.NodeIndex.Value] = list;
            }

            list.Add(part);
        }

        var images = new List<SceneImage>();
        var samplers = new List<SceneSampler>();
        var materials = new List<SceneMaterial>();
        var imageByContent = new Dictionary<NifPreparedImage, int>(NifPreparedImageContentComparer.Instance);
        var materialByKey = new Dictionary<NifMaterialCacheKey, int>();
        var meshes = new List<SceneMesh>(partsByNode.Count);
        var skins = new List<SceneSkin>();
        var meshIndexByNode = new Dictionary<int, int>(partsByNode.Count);
        var skinPlacements = new List<NifNeutralSkinPlacement>();

        foreach (var entry in partsByNode)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var nodeIndex = entry.Key;
            var primitives = new List<ScenePrimitive>(entry.Value.Count);
            foreach (var part in entry.Value)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var prepared = preparedBySubmesh[part.Submesh];
                var materialIndex = Material(prepared, images, samplers, materials,
                    imageByContent, materialByKey, cancellationToken);
                var primitive = Primitive(part, prepared, materialIndex, cancellationToken);
                if (PrimitiveReason(primitive, cancellationToken) is { } geometry)
                {
                    unsupportedReason = geometry;
                    return false;
                }

                primitives.Add(primitive);
            }

            meshIndexByNode[nodeIndex] = meshes.Count;
            // A node that owns one part (every part on the classic export route has its own attachment node, named
            // with a block suffix such as "Shape05:0_61") names its mesh after that part, as the native writer does.
            // A node that groups several parts keeps its own name.
            meshes.Add(new SceneMesh(
                entry.Value.Count == 1 ? MeshPartName(entry.Value[0]) : source.Nodes[nodeIndex].Name, primitives));
        }

        foreach (var (ordinal, part) in drawable)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (part.Skin is not { } binding) continue;
            var prepared = preparedBySubmesh[part.Submesh];
            var materialIndex = Material(prepared, images, samplers, materials,
                imageByContent, materialByKey, cancellationToken);
            var primitive = Primitive(part, prepared, materialIndex, cancellationToken);
            if (PrimitiveReason(primitive, cancellationToken) is { } geometry)
            {
                unsupportedReason = geometry;
                return false;
            }

            skinPlacements.Add(new NifNeutralSkinPlacement(ordinal, part, meshes.Count, skins.Count));
            meshes.Add(new SceneMesh(MeshPartName(part), [primitive]));
            skins.Add(new SceneSkin(part.Name, binding.JointNodeIndices,
                binding.InverseBindMatrices.Select(GltfCoordinateAdapter.ConvertMatrix), GlbScene.RootNodeIndex));
        }

        var nodes = Nodes(source, meshIndexByNode, skinPlacements, cancellationToken);
        scene = new ModelDocument(
            "nif",
            name,
            [new SceneDefinition(name, [GlbScene.RootNodeIndex])],
            nodes,
            meshes,
            materials,
            images,
            samplers,
            skins: skins);
        SceneValidation.Validate(scene, cancellationToken);
        return true;
    }

    /// <summary>Retains original part ordinals while applying the native writer's empty and no-draw rules.</summary>
    /// <param name="source">The unmodified source part table; repeated part objects remain separate occurrences.</param>
    /// <param name="textureResolver">The same material resolver used by the writer.</param>
    /// <returns>Drawable occurrences in source order, without cloning or renumbering their original ordinals.</returns>
    private static List<(int Ordinal, GlbMeshPart Part)> Drawable(GlbScene source, NifTextureResolver textureResolver)
    {
        var drawable = new List<(int Ordinal, GlbMeshPart Part)>(source.MeshParts.Count);
        for (var ordinal = 0; ordinal < source.MeshParts.Count; ordinal++)
        {
            var part = source.MeshParts[ordinal];
            if (part.Submesh.TriangleCount == 0 || part.Submesh.VertexCount == 0) continue;
            if (GlbWriter.ShouldSkipStarfieldNoDrawSubmesh(part.Submesh, textureResolver)) continue;
            drawable.Add((ordinal, part));
        }

        return drawable;
    }

    /// <summary>Preserves source node indices and transforms, then appends independently identified skin placements.</summary>
    /// <param name="source">The unmodified source hierarchy.</param>
    /// <param name="meshIndexByNode">Existing rigid mesh attachments by original node index.</param>
    /// <param name="skinPlacements">Independent skin occurrences in original mesh-part order.</param>
    /// <param name="cancellationToken">Cancels node and placement construction.</param>
    /// <returns>All original nodes followed by identity-transform skin nodes attached to the scene root.</returns>
    private static SceneNode[] Nodes(
        GlbScene source,
        Dictionary<int, int> meshIndexByNode,
        List<NifNeutralSkinPlacement> skinPlacements,
        CancellationToken cancellationToken)
    {
        var children = new List<int>[source.Nodes.Count];
        for (var index = 0; index < source.Nodes.Count; index++) children[index] = [];
        for (var index = 0; index < source.Nodes.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (source.Nodes[index].ParentIndex is { } parent && (uint)parent < (uint)source.Nodes.Count)
            {
                children[parent].Add(index);
            }
        }

        var nodes = new SceneNode[checked(source.Nodes.Count + skinPlacements.Count)];
        for (var index = 0; index < skinPlacements.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var nodeIndex = source.Nodes.Count + index;
            children[GlbScene.RootNodeIndex].Add(nodeIndex);
            nodes[nodeIndex] = skinPlacements[index].CreateNode();
        }
        for (var index = 0; index < source.Nodes.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            nodes[index] = new SceneNode(
                source.Nodes[index].Name,
                GltfCoordinateAdapter.ConvertMatrix(source.Nodes[index].LocalTransform),
                children[index],
                meshIndexByNode.TryGetValue(index, out var mesh) ? mesh : null);
        }

        return nodes;
    }

    /// <summary>Normalizes an owned geometry copy before crossing the basis and retaining all skin influences.</summary>
    /// <param name="part">The original mesh occurrence; its source buffers are never modified.</param>
    /// <param name="prepared">The already resolved material and vertex-color policy.</param>
    /// <param name="materialIndex">The corresponding neutral material index.</param>
    /// <param name="cancellationToken">Cancels vertex, tangent and influence traversal.</param>
    /// <returns>An independently owned primitive with the legacy winding and basis conversion.</returns>
    private static ScenePrimitive Primitive(GlbMeshPart part, NifPreparedMaterial prepared, int? materialIndex,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var submesh = RenderableSubmeshCloner.DeepClone(part.Submesh);
        GlbWriter.NormalizeWinding(submesh);
        var vertexCount = submesh.Positions.Length / 3;
        var vertices = new SceneVertex[vertexCount];
        for (var index = 0; index < vertexCount; index++)
        {
            if ((index & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
            var offset = index * 3;
            vertices[index] = new SceneVertex(
                GltfCoordinateAdapter.ConvertPosition(new Vector3(
                    submesh.Positions[offset],
                    submesh.Positions[offset + 1],
                    submesh.Positions[offset + 2])),
                GlbWriter.ReadNormal(submesh, index),
                GlbWriter.ReadVertexColor(submesh, index, prepared.Key.StarfieldVertexLerpProjection),
                TexCoord(submesh, index));
        }

        var indices = new int[submesh.Triangles.Length];
        for (var index = 0; index < submesh.Triangles.Length; index++)
        {
            indices[index] = submesh.Triangles[index];
        }

        return new ScenePrimitive(
            MeshPartName(part),
            vertices,
            indices,
            materialIndex,
            // A NIF color is read as bytes (NifGeometryDataReader), and the native writer stores normalized unsigned
            // bytes; floats would hold the same values in four times the space.
            SceneColorEncoding.UnsignedByteNormalized,
            skinInfluences: Influences(part, vertexCount, cancellationToken),
            tangents: prepared.NormalImage is not null || HasAuthoredTangents(submesh)
                ? LitTangents(submesh, vertexCount, cancellationToken)
                : null);
    }

    /// <summary>Names a part's geometry exactly as the native writer names that part's mesh.</summary>
    /// <remarks>
    ///     <see cref="GlbWriter" /> appends <see cref="AuthoredSkyGlbPreviewProjection.NameSuffix" /> to the mesh of an
    ///     authored-sky part, so the fallback palette is labeled on the geometry as well as on its material. The
    ///     native writer emits one mesh per part, so the matching place here is the part's primitive and, for a
    ///     skinned part, its one-primitive mesh. A rigid node that owns exactly one part also names its mesh with
    ///     this, suffix included, matching the native mesh name; a node grouping several parts keeps the node name.
    ///     A skin is named after its part without the suffix, as the native writer names it.
    /// </remarks>
    /// <param name="part">The original source occurrence.</param>
    /// <returns>The part name, with the authored-sky suffix where the native writer adds it.</returns>
    private static string MeshPartName(GlbMeshPart part) =>
        AuthoredSkyGlbPreviewProjection.AppliesTo(part.Submesh)
            ? part.Name + AuthoredSkyGlbPreviewProjection.NameSuffix
            : part.Name;

    /// <summary>
    ///     Declines a built primitive whose projected vertex values or tangent basis the shared validator rejects.
    /// </summary>
    /// <remarks>
    ///     This inspects the primitive after projection rather than the source arrays, because the projection is
    ///     what produces the rejected values: the basis rotation can carry a finite source position to infinity,
    ///     and an infinite source normal or tangent normalizes to zero. Each test is the validator's own, so a
    ///     primitive passes here exactly when it passes there. The vertex-color test cannot fire through today's
    ///     projections, which divide a byte by 255 or clamp; it stays so a future projection cannot reach the
    ///     validator first.
    /// </remarks>
    /// <param name="primitive">A primitive built by this adapter.</param>
    /// <param name="cancellationToken">Cancels the vertex and tangent walk.</param>
    /// <returns>The first unportable value's reason, or null when every vertex and tangent is portable.</returns>
    internal static string? PrimitiveReason(ScenePrimitive primitive, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(primitive);
        for (var index = 0; index < primitive.Vertices.Count; index++)
        {
            if ((index & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
            var vertex = primitive.Vertices[index];
            if (!IsFinite(vertex.Position))
                return "A non-finite vertex position retains the native writer.";
            if (!IsFinite(vertex.Normal) || MathF.Abs(vertex.Normal.LengthSquared() - 1f) > 0.0001f)
                return "A vertex normal that is not a finite unit vector retains the native writer.";
            if (!IsUnitColor(vertex.Color))
                return "A projected vertex color outside the unit range retains the native writer.";
            if (!float.IsFinite(vertex.TexCoord.X) || !float.IsFinite(vertex.TexCoord.Y))
                return "A non-finite texture coordinate retains the native writer.";
        }

        if (primitive.Tangents is not { } tangents) return null;
        const string tangentReason =
            "A tangent that is not a finite unit direction with unit handedness retains the native writer.";
        if (tangents.Values.Count != primitive.Vertices.Count) return tangentReason;
        for (var index = 0; index < tangents.Values.Count; index++)
        {
            if ((index & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
            var tangent = tangents.Values[index];
            var direction = new Vector3(tangent.X, tangent.Y, tangent.Z);
            if (!IsFinite(direction) || !float.IsFinite(tangent.W) ||
                MathF.Abs(direction.LengthSquared() - 1f) > 0.0001f || tangent.W is not (1f or -1f))
                return tangentReason;
        }

        return null;
    }

    /// <summary>Whether the source authored a tangent array, of any length.</summary>
    /// <remarks>
    ///     Such surfaces take <see cref="LitTangents" /> even without a normal map, so the normalized tangents equal
    ///     <see cref="GlbWriter" />'s in every case: the native builder uses a complete authored array (with its own
    ///     near-zero cutoff and cross-product fallback) and generates tangents from texture coordinates for an
    ///     incomplete one. The adapter's former separate conversion disagreed on near-zero tangents (measured by the
    ///     M2.1 parity gate on the Xbox 360 stratum) and indexed a short array without a bounds check.
    /// </remarks>
    /// <param name="submesh">The source part.</param>
    /// <returns>True when the source authored tangents.</returns>
    internal static bool HasAuthoredTangents(RenderableSubmesh submesh) => submesh.Tangents is not null;

    /// <summary>
    ///     Uses the native tangent builder and output basis for normal-mapped surfaces and for every surface with
    ///     authored tangents, so the normalized tangents equal <see cref="GlbWriter" />'s.
    /// </summary>
    private static SceneTangents LitTangents(RenderableSubmesh submesh, int vertexCount, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var built = NpcGlbTangentBuilder.BuildTangents(submesh);
        var values = new Vector4[vertexCount];
        for (var index = 0; index < vertexCount; index++)
        {
            if ((index & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
            values[index] = GlbWriter.ReadTangent(submesh, built, index);
        }
        return new SceneTangents(values);
    }

    /// <summary>Reads a UV pair, defaulting to the origin when the source authored none.</summary>
    private static Vector2 TexCoord(RenderableSubmesh submesh, int vertexIndex)
    {
        if (submesh.UVs == null) return Vector2.Zero;
        var offset = vertexIndex * 2;
        if (offset + 1 >= submesh.UVs.Length) return Vector2.Zero;
        return new Vector2(submesh.UVs[offset], submesh.UVs[offset + 1]);
    }

    /// <summary>Carries every authored influence. The native writer's four-influence cap is not reproduced here.</summary>
    private static SceneSkinInfluences? Influences(GlbMeshPart part, int vertexCount,
        CancellationToken cancellationToken)
    {
        if (part.Skin is not { } binding) return null;
        var perVertex = binding.PerVertexInfluences;
        var widest = 0;
        foreach (var influences in perVertex) widest = Math.Max(widest, influences.Length);
        if (widest == 0) return null;

        var joints = new int[vertexCount * widest];
        var weights = new float[vertexCount * widest];
        for (var vertex = 0; vertex < vertexCount; vertex++)
        {
            if ((vertex & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
            var influences = vertex < perVertex.Length ? perVertex[vertex] : [];
            for (var slot = 0; slot < influences.Length; slot++)
            {
                joints[(vertex * widest) + slot] = influences[slot].BoneIdx;
                weights[(vertex * widest) + slot] = influences[slot].Weight;
            }
        }

        return new SceneSkinInfluences(widest, joints, weights);
    }

    /// <summary>Transports the already prepared surface into shared channels using the legacy cache identity.</summary>
    private static int Material(
        NifPreparedMaterial prepared,
        List<SceneImage> images,
        List<SceneSampler> samplers,
        List<SceneMaterial> materials,
        Dictionary<NifPreparedImage, int> imageByContent,
        Dictionary<NifMaterialCacheKey, int> materialByKey,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (materialByKey.TryGetValue(prepared.Key, out var existing)) return existing;
        var samplerIndex = samplers.Count;
        if (prepared.BaseColorImage is not null || prepared.NormalImage is not null ||
            prepared.MetallicRoughnessImage is not null || prepared.SpecularImage is not null ||
            prepared.OcclusionImage is not null || prepared.EmissiveImage is not null)
        {
            samplers.Add(new SceneSampler(
                prepared.ClampU ? SceneTextureWrap.ClampToEdge : SceneTextureWrap.Repeat,
                prepared.ClampV ? SceneTextureWrap.ClampToEdge : SceneTextureWrap.Repeat,
                SceneTextureFilter.Linear, SceneTextureFilter.Linear));
        }
        var material = new SceneMaterial(prepared.Name, prepared.BaseColor,
            Binding(prepared.BaseColorImage, samplerIndex, images, imageByContent),
            prepared.AlphaMode, prepared.AlphaCutoff, prepared.DoubleSided, prepared.Unlit,
            prepared.Extras?.ToJsonString())
        {
            NormalTexture = Binding(prepared.NormalImage, samplerIndex, images, imageByContent),
            NormalScale = prepared.NormalScale,
            MetallicRoughnessTexture = Binding(prepared.MetallicRoughnessImage, samplerIndex, images, imageByContent),
            MetallicFactor = prepared.MetallicFactor,
            RoughnessFactor = prepared.RoughnessFactor,
            SpecularTexture = Binding(prepared.SpecularImage, samplerIndex, images, imageByContent),
            SpecularFactor = prepared.SpecularFactor,
            OcclusionTexture = Binding(prepared.OcclusionImage, samplerIndex, images, imageByContent),
            OcclusionStrength = prepared.OcclusionStrength,
            EmissiveTexture = Binding(prepared.EmissiveImage, samplerIndex, images, imageByContent),
            EmissiveFactor = prepared.EmissiveFactor,
            EmissiveStrength = prepared.EmissiveStrength
        };
        var index = materials.Count;
        materials.Add(material);
        materialByKey.Add(prepared.Key, index);
        return index;
    }

    /// <summary>Stores each distinct prepared image once, keyed by its exact PNG bytes.</summary>
    /// <remarks>
    ///     Equal display names never alias different bytes, and identical bytes are stored once whatever their names
    ///     (<see cref="NifPreparedImageContentComparer" />). A material that misses the preparation cache, for example
    ///     one that differs only by its glow map, re-prepares images another material already holds; keeping each
    ///     occurrence stored those bytes twice (2.2 MB of the retail neon sign's 2.24 MB growth, measured
    ///     2026-09-23), where the native writer's glTF library stores them once.
    /// </remarks>
    private static SceneTextureBinding? Binding(NifPreparedImage? image, int samplerIndex,
        List<SceneImage> images, Dictionary<NifPreparedImage, int> imageByContent)
    {
        if (image is null) return null;
        if (!imageByContent.TryGetValue(image, out var imageIndex))
        {
            imageIndex = images.Count;
            images.Add(new SceneImage(image.Name, image.Png));
            imageByContent.Add(image, imageIndex);
        }
        return new SceneTextureBinding(imageIndex, samplerIndex);
    }

    /// <summary>Declines actual prepared channels that the pinned shared material contract cannot carry.</summary>
    /// <remarks>
    ///     The checks after the Starfield glass case mirror the shared material validator on the values the
    ///     adapter copies into <see cref="SceneMaterial" />. An authored sky is forced unlit yet can still pack
    ///     occlusion, metallic-roughness or specular maps, and a non-finite authored material alpha or glossiness
    ///     reaches the base color or roughness as NaN, because preparation's clamps pass NaN through.
    /// </remarks>
    private static string? PreparedMaterialReason(NifPreparedMaterial prepared)
    {
        if (prepared.Unlit && (prepared.HasEmission || prepared.EmissiveFactor != Vector3.Zero ||
                               prepared.EmissiveStrength is not 1f))
            return "Emission on a prepared unlit surface retains the native material writer.";
        if (prepared.IndexOfRefraction is not null || prepared.Transmission is not null ||
            prepared.ClearCoat is not null || prepared.ClearCoatRoughness is not null)
            return "Water optical material channels retain the native material writer.";
        if (prepared.Extras is { Count: > 0 })
            return "A specialized material viewer extension retains the native material writer.";
        var effect = prepared.Key.StarfieldEffectPolicy;
        if (effect.IsResolved && effect.HasEffectSettings && effect.IsGlass && !prepared.Key.HasStarfieldEffectAlpha)
            return "Unrepresented Starfield effect composition retains the native material writer.";
        if (prepared.Unlit && HasLightingMaps(prepared))
            return "Lighting maps on a prepared unlit surface retain the native material writer.";
        if (!IsUnitColor(prepared.BaseColor) || !Enum.IsDefined(prepared.AlphaMode) ||
            !IsUnitFactor(prepared.AlphaCutoff))
            return "A base color or alpha cutoff outside the unit range retains the native material writer.";
        if (!HasPortableFactors(prepared))
            return "A material factor outside its portable range retains the native material writer.";
        return null;
    }

    /// <summary>Whether a prepared surface binds any map that only a lit shading model consumes.</summary>
    /// <remarks>Each prepared image maps to exactly one <see cref="SceneMaterial.HasLightingMaps" /> binding.</remarks>
    private static bool HasLightingMaps(NifPreparedMaterial prepared) =>
        prepared.NormalImage is not null || prepared.MetallicRoughnessImage is not null ||
        prepared.SpecularImage is not null || prepared.OcclusionImage is not null ||
        prepared.EmissiveImage is not null;

    /// <summary>Whether every copied scalar factor is finite and inside the range the shared validator requires.</summary>
    /// <remarks>The specular color factor is not copied; the shared default of one is always portable.</remarks>
    private static bool HasPortableFactors(NifPreparedMaterial prepared) =>
        IsUnitFactor(prepared.MetallicFactor) && IsUnitFactor(prepared.RoughnessFactor) &&
        IsUnitFactor(prepared.SpecularFactor) && IsUnitFactor(prepared.OcclusionStrength) &&
        IsUnitFactor(prepared.EmissiveFactor.X) && IsUnitFactor(prepared.EmissiveFactor.Y) &&
        IsUnitFactor(prepared.EmissiveFactor.Z) && float.IsFinite(prepared.NormalScale) &&
        float.IsFinite(prepared.EmissiveStrength) && prepared.EmissiveStrength >= 0f;

    /// <summary>
    ///     Identifies unsupported semantics before any source array is copied or discarded. Every Bethesda state the
    ///     neutral document cannot express is a decline reason here, never a silently dropped field.
    /// </summary>
    /// <param name="source">The original node and part domain.</param>
    /// <param name="drawable">Original ordinals and parts surviving the native no-draw rule.</param>
    /// <param name="cancellationToken">Cancels validation before output publication.</param>
    /// <returns>An explicit unsupported capability, or null when the adapter can preserve every drawable part.</returns>
    private static string? UnsupportedReason(GlbScene source, List<(int Ordinal, GlbMeshPart Part)> drawable,
        CancellationToken cancellationToken)
    {
        if (source.Nodes.Count == 0) return "An empty scene retains the native empty-result behavior.";

        if (drawable.Count == 0) return "Empty geometry retains the native empty-result behavior.";

        // The neutral hierarchy follows in-range parent links only, exactly as Nodes builds it. A cycle
        // among them, or a parent above the scene root, is a graph the shared validator rejects.
        if (NodeRoots(source, cancellationToken) is not { } roots ||
            roots[GlbScene.RootNodeIndex] != GlbScene.RootNodeIndex)
            return "A cyclic node hierarchy or a parented scene root retains the native writer.";
        if (NodeTransformReason(source, cancellationToken) is { } transform) return transform;

        foreach (var (_, part) in drawable)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((part.NodeIndex is { } nodeIndex && (uint)nodeIndex >= (uint)source.Nodes.Count) ||
                (part.NodeIndex is null && part.Skin is null))
                return "A mesh part with no resolved owning node retains the native writer.";

            var submesh = part.Submesh;
            if (submesh.Positions.Length % 3 != 0)
                return "A position array that is not a whole number of vertices retains the native writer.";

            var vertexCount = submesh.Positions.Length / 3;
            if (submesh.Normals is { } normals && normals.Length != vertexCount * 3)
                return "A normal array that disagrees with the position count retains the native writer.";
            if (submesh.UVs is { } uvs && uvs.Length != vertexCount * 2)
                return "A UV array that disagrees with the position count retains the native writer.";
            if (submesh.VertexColors is { } colors && colors.Length != vertexCount * 4)
                return "A vertex-color array that disagrees with the position count retains the native writer.";
            if (submesh.Tangents is { } tangents && tangents.Length != vertexCount * 3)
                return "A tangent array that disagrees with the position count retains the native writer.";
            if (submesh.Triangles.Length % 3 != 0)
                return "An index array that is not a whole number of triangles retains the native writer.";

            foreach (var index in submesh.Triangles)
            {
                if (index >= vertexCount)
                    return "An index outside the vertex range retains the native writer.";
            }

            if (part.Skin is { } binding)
            {
                if (binding.JointNodeIndices.Length != binding.InverseBindMatrices.Length)
                    return "A joint palette that disagrees with its inverse binds retains the native writer.";
                if (binding.PerVertexInfluences.Length != vertexCount)
                    return "A skin influence table that disagrees with the vertex count retains the native writer.";
                foreach (var influences in binding.PerVertexInfluences)
                {
                    foreach (var influence in influences)
                    {
                        if ((uint)influence.BoneIdx >= (uint)binding.JointNodeIndices.Length)
                            return "A skin influence outside the joint palette retains the native writer.";
                    }
                }

                if (JointPaletteReason(source, binding, roots, cancellationToken) is { } palette) return palette;
                if (SkinWeightReason(binding, cancellationToken) is { } weights) return weights;
            }

            if (VegetationReason(submesh) is { } vegetation) return vegetation;
            if (MaterialReason(submesh) is { } reason) return reason;
        }

        return null;
    }

    /// <summary>
    ///     Resolves each source node's topological root through in-range parent links, the same links
    ///     <see cref="Nodes" /> turns into child lists, or reports that those links form a cycle.
    /// </summary>
    /// <param name="source">The original node table; a parent index outside it is treated as absent.</param>
    /// <param name="cancellationToken">Cancels the walk over a large hierarchy.</param>
    /// <returns>The root index of every node, or null when any parent chain loops.</returns>
    private static int[]? NodeRoots(GlbScene source, CancellationToken cancellationToken)
    {
        const int unresolved = -1;
        const int onPath = -2;
        var count = source.Nodes.Count;
        var roots = new int[count];
        Array.Fill(roots, unresolved);
        var path = new List<int>();
        for (var start = 0; start < count; start++)
        {
            if ((start & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (roots[start] != unresolved) continue;
            path.Clear();
            var current = start;
            int root;
            while (true)
            {
                if (roots[current] >= 0)
                {
                    root = roots[current];
                    break;
                }

                // Reaching a node already on this walk means the parent links loop.
                if (roots[current] == onPath) return null;
                roots[current] = onPath;
                path.Add(current);
                if (source.Nodes[current].ParentIndex is not { } parent || (uint)parent >= (uint)count)
                {
                    root = current;
                    break;
                }

                current = parent;
            }

            foreach (var node in path) roots[node] = root;
        }

        return roots;
    }

    /// <summary>Declines a node whose converted local transform the shared validator rejects.</summary>
    /// <param name="source">The original node table.</param>
    /// <param name="cancellationToken">Cancels the walk over a large hierarchy.</param>
    /// <returns>The decline reason, or null when every converted transform is a finite affine matrix.</returns>
    private static string? NodeTransformReason(GlbScene source, CancellationToken cancellationToken)
    {
        foreach (var node in source.Nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsFiniteAffine(GltfCoordinateAdapter.ConvertMatrix(node.LocalTransform)))
                return "A node transform that is not a finite affine matrix retains the native writer.";
        }

        return null;
    }

    /// <summary>Declines a joint palette the shared skin validator rejects.</summary>
    /// <remarks>
    ///     Every neutral skin names the scene root as its skeleton root, so each joint must descend from it.
    ///     The palette and inverse-bind lengths are already known to agree. A joint is bounded by the source node
    ///     table rather than the document's: an index just past it would otherwise bind to one of the skin
    ///     placements appended after the source nodes, which the validator accepts and the native writer cannot
    ///     resolve.
    /// </remarks>
    /// <param name="source">The original node table the joints index.</param>
    /// <param name="binding">The part's palette, inverse binds and influences.</param>
    /// <param name="roots">Each node's topological root, from an acyclic hierarchy.</param>
    /// <param name="cancellationToken">Cancels the walk over a large palette.</param>
    /// <returns>The decline reason, or null when the palette is portable.</returns>
    private static string? JointPaletteReason(GlbScene source, GlbSkinBinding binding, int[] roots,
        CancellationToken cancellationToken)
    {
        if (binding.JointNodeIndices.Length == 0)
            return "An empty joint palette retains the native writer.";
        var seen = new HashSet<int>();
        for (var index = 0; index < binding.JointNodeIndices.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var joint = binding.JointNodeIndices[index];
            if ((uint)joint >= (uint)source.Nodes.Count)
                return "A joint outside the node table retains the native writer.";
            if (!seen.Add(joint))
                return "A joint palette that repeats a node retains the native writer.";
            if (!IsFiniteAffine(GltfCoordinateAdapter.ConvertMatrix(binding.InverseBindMatrices[index])))
                return "A non-finite inverse bind matrix retains the native writer.";
            if (roots[joint] != GlbScene.RootNodeIndex)
                return "A joint outside the scene root's hierarchy retains the native writer.";
        }

        return null;
    }

    /// <summary>Declines per-vertex skin weights the shared skin validator rejects.</summary>
    /// <remarks>
    ///     <see cref="Influences" /> copies each vertex's influences in order and pads the rest with zero weights,
    ///     which add nothing to a sum and are never repeats, so testing the unpadded lists is the validator's test.
    ///     A vertex with no influence sums to zero; that is also how a part with no influences at all, which
    ///     would reach the validator with no skin attributes, is declined.
    /// </remarks>
    /// <param name="binding">The part's per-vertex influences, already known to match the vertex count.</param>
    /// <param name="cancellationToken">Cancels the walk over a large mesh.</param>
    /// <returns>The decline reason, or null when every vertex's weights are portable.</returns>
    private static string? SkinWeightReason(GlbSkinBinding binding, CancellationToken cancellationToken)
    {
        var weightedJoints = new HashSet<int>();
        for (var vertex = 0; vertex < binding.PerVertexInfluences.Length; vertex++)
        {
            if ((vertex & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
            weightedJoints.Clear();
            double sum = 0;
            foreach (var (joint, weight) in binding.PerVertexInfluences[vertex])
            {
                if (!float.IsFinite(weight) || weight < 0f || (weight > 0f && !weightedJoints.Add(joint)))
                    return "A negative, non-finite or repeated skin weight retains the native writer.";
                sum += weight;
            }

            if (Math.Abs(sum - 1) > 0.0001)
                return "Skin weights that do not sum to one retain the native writer.";
        }

        return null;
    }

    /// <summary>Checks all vector components, as the shared validator does, without normalizing them.</summary>
    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    /// <summary>Checks a color against the shared validator's unit range; NaN and infinities fall outside it.</summary>
    private static bool IsUnitColor(Vector4 value) =>
        value.X is >= 0f and <= 1f && value.Y is >= 0f and <= 1f &&
        value.Z is >= 0f and <= 1f && value.W is >= 0f and <= 1f;

    /// <summary>Checks a scalar factor against the shared validator's finite unit range.</summary>
    private static bool IsUnitFactor(float value) => float.IsFinite(value) && value is >= 0f and <= 1f;

    /// <summary>Checks a converted matrix exactly as the shared validator checks node and inverse-bind transforms.</summary>
    private static bool IsFiniteAffine(Matrix4x4 matrix) =>
        IsFinite(new Vector3(matrix.M11, matrix.M12, matrix.M13)) &&
        IsFinite(new Vector3(matrix.M21, matrix.M22, matrix.M23)) &&
        IsFinite(new Vector3(matrix.M31, matrix.M32, matrix.M33)) &&
        IsFinite(new Vector3(matrix.M41, matrix.M42, matrix.M43)) &&
        matrix.M14 is 0f && matrix.M24 is 0f && matrix.M34 is 0f && matrix.M44 is 1f;

    /// <summary>
    ///     The SpeedTree vegetation semantics a neutral document cannot express.
    /// </summary>
    /// <remarks>
    ///     These are runtime behaviours rather than geometry: a billboard is oriented per frame against the camera,
    ///     wind speeds drive a vertex animation the exporter does not bake, and a LOD set is a choice made at draw
    ///     time. A neutral scene has no place to put any of them. Synthesising a still approximation so the numbers
    ///     look complete would be a placeholder, which the program's rules forbid, so each one declines instead and
    ///     the native writer keeps the shape.
    /// </remarks>
    private static string? VegetationReason(RenderableSubmesh submesh)
    {
        if (submesh.IsBillboard || submesh.IsLeafBillboard)
            return "SpeedTree billboard orientation is a per-frame runtime behavior and retains the native writer.";
        if (submesh.IsSpeedTreeBranch && submesh.SpeedTreeWindSpeeds != Vector2.One)
            return "SpeedTree wind-rig speeds drive an unbaked vertex animation and retain the native writer.";
        if (submesh.SpeedTreeLod is not null)
            return "SpeedTree level-of-detail selection is a draw-time choice and retains the native writer.";
        if (submesh.IsFarLodFallback)
            return "A far-LOD fallback shape retains the native writer.";
        return null;
    }

    /// <summary>
    ///     Source semantics that remain outside the shared material contract. The prepared-output check also
    ///     rejects specialized channels resolved from metadata, including water optics.
    /// </summary>
    private static string? MaterialReason(RenderableSubmesh submesh)
    {
        if (submesh.SpecularMapTexturePath is not null ||
            submesh.GradientMapTexturePath is not null ||
            submesh.EnvironmentMapTexturePath is not null ||
            submesh.ClassicEnvironmentMapTexturePath is not null ||
            submesh.ClassicEnvironmentMaskTexturePath is not null ||
            submesh.ClassicParallaxHeightMapTexturePath is not null)
            return "Maps beyond diffuse retain the native material writer.";
        if (EmissionReason(submesh) is { } emission) return emission;
        if (submesh.IsDecal)
            return "Decal behavior retains the native material writer.";
        if (submesh.EffectFalloff is not null)
            return "Falloff material behavior retains the native material writer.";
        if (submesh.EffectTint != (1f, 1f, 1f))
            return "Effect tint retains the native material writer.";
        if (submesh.HasAuthoredOblivionBodySkinInputs || submesh.HasAuthoredOblivionOrdinaryInputs)
            return "Authored Oblivion shader inputs retain the native material writer.";
        if (submesh.StarfieldMaterialColor != default || submesh.StarfieldMaterialAlpha != default)
            return "Starfield material render state retains the native material writer.";
        return null;
    }

    /// <summary>Retains dynamic, malformed and unlit BGSM emission states that preparation cannot transport as lit emission.</summary>
    /// <param name="submesh">The original source material state before export preparation.</param>
    /// <returns>The unsupported emission state, or null when preparation can preserve its selected channel.</returns>
    private static string? EmissionReason(RenderableSubmesh submesh)
    {
        if (submesh.UsesExternalEmittance)
            return "Emissive and external-emittance state retains the native material writer.";
        if (submesh.IsEmissive &&
            (submesh.BgsmGlowMapTexturePath is not null || submesh.BgsmEmissionColor != Vector3.Zero))
            return "BGSM emission on an unlit surface retains the native material writer.";
        if (submesh.BgsmEmissionColor != Vector3.Zero &&
            !NifMaterialPreparation.TryEncodeGltfEmission(submesh.BgsmEmissionColor, out _, out _))
            return "Malformed BGSM emission retains the native material writer.";
        return null;
    }
}
