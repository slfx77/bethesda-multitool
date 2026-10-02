using System.Globalization;
using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Formats.Nif.Decoding;
using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     Types the geometry of every placed NiTriShape, BSSegmentedTriShape and NiTriStrips block (plan section 3,
///     "Geometry"; section 6, slice 3): one <see cref="SceneMesh" /> with one <see cref="ScenePrimitive" /> per geometry
///     block and effective material, in geometry-block order. Streams follow <see cref="NifModelGeometryData" />,
///     triangles <see cref="NifStripTriangulator" />, morphs <see cref="NifModelMorphReader" />.
/// </summary>
/// <remarks>
///     <para>
///         Materials (slice 5): the caller's material callback gives each placed occurrence its material index (null when
///         no property is effective on it). Occurrences sharing a material share one mesh; a geometry instanced under
///         parents whose properties differ gets one mesh per distinct material, each placing its own occurrences, because
///         the material index lives on the primitive. The vertex data is read once per geometry block.
///     </para>
///     <para>
///         Scope: stored (inline) streams in either byte order, which the decoder has already put in host order, and
///         (slice 10) the console packed streams of a data block whose Additional Data names a BSPackedAdditionalGeometryData,
///         decoded by <see cref="NifPackedGeometryReader" /> under the platform the caller resolved. A packed skinned
///         geometry's vertex order depends on the shape's skin partitions, so its data is read once per (data block,
///         skin instance) pair; a packed block whose layout is unknown, or whose payload does not fit its counts, keeps
///         the geometry, its data and the packed block NativeOnly with the precise reason
///         (<see cref="NifModelCoverage.PackedLayoutUnknownReason" />, <see cref="NifModelCoverage.PackedPayloadReason" />).
///         The decoded packed block is Typed and targets the mesh it fed. Data naming an NiAdditionalGeometryData (PC
///         landscape LOD) is typed from its inline streams; the additional block stays NativeOnly
///         <see cref="NifModelCoverage.AdditionalGeometryReason" />.
///     </para>
///     <para>
///         Skins (slice 7): the caller's <see cref="NifModelSkinReader" /> types the skin of each drawable geometry once;
///         every material variant's primitive carries the same influences, and a dismember skin's faces and Face-domain
///         streams (appended after the geometry's own attributes, so the primary color index is unchanged). The skin
///         reader records each occurrence's skin index for the node. A geometry that yields no primitive hands its skin
///         blocks its own NativeOnly reason. A geometry that yields no triangle or no vertex emits no primitive (Shared
///         rejects empty primitives); it and its data are NativeOnly <see cref="NifModelCoverage.EmptyGeometryReason" />
///         and its node stays placed without a mesh.
///     </para>
///     <para>
///         Corrupt input throws <see cref="InvalidDataException" />: a geometry whose Data link names a block of the
///         wrong data type, or the data errors listed on <see cref="NifModelGeometryData" />.
///     </para>
/// </remarks>
internal static class NifModelGeometryReader
{
    /// <summary>The native-state kind of the per-primitive rows.</summary>
    public const string PrimitiveKind = "bmt.nif.primitive";

    /// <summary>The payload version of <see cref="PrimitiveKind" /> rows.</summary>
    public const int PrimitivePayloadVersion = 1;

    /// <summary>The console packed-stream block type.</summary>
    public const string PackedAdditionalDataType = "BSPackedAdditionalGeometryData";

    /// <summary>The geometry block types this reader types, with the data type each must reference.</summary>
    private static readonly Dictionary<string, string> DataTypeByGeometryType = new(StringComparer.Ordinal)
    {
        ["NiTriShape"] = "NiTriShapeData",
        ["BSSegmentedTriShape"] = "NiTriShapeData",
        ["NiTriStrips"] = "NiTriStripsData"
    };

    /// <summary>True for the geometry block types this reader types (NiTriShape, BSSegmentedTriShape, NiTriStrips).</summary>
    public static bool IsGeometryType(string type)
    {
        return DataTypeByGeometryType.ContainsKey(type);
    }

    /// <summary>
    ///     The block types whose content this reader turns into typed state, which the reader therefore decodes strictly
    ///     (plan section 1: a typed block type that does not decode exactly is corrupt input). The packed block joins
    ///     them in slice 10.
    /// </summary>
    public static bool IsStrictType(string type)
    {
        return IsGeometryType(type) ||
               type is "NiTriShapeData" or "NiTriStripsData" or NifModelMorphReader.MorphDataType or
                   PackedAdditionalDataType;
    }

    /// <summary>Reads the geometry of every placed geometry block.</summary>
    /// <param name="state">The read state.</param>
    /// <param name="graph">The walked hierarchy.</param>
    /// <param name="materialFor">
    ///     The material of one placed occurrence (node index) of drawable geometry, or null for none; null when the
    ///     caller types no materials.
    /// </param>
    /// <param name="skins">The skin reader, or null when the caller types no skins.</param>
    /// <param name="platform">The console platform packed vertex colors are decoded for.</param>
    /// <param name="cancellationToken">Observed per block and per occurrence.</param>
    /// <exception cref="InvalidDataException">See the type remarks and <see cref="NifModelSkinReader.Read" />.</exception>
    public static NifModelGeometryResult Read(NifModelReadState state, NifModelNodeGraph graph,
        Func<int, NifModelGeometryData, int?>? materialFor, NifModelSkinReader? skins,
        NifPackedPlatformSelection platform, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(platform);
        var meshes = new List<SceneMesh>();
        var meshByGeometry = new Dictionary<int, int>();
        var meshByNode = new Dictionary<int, int>();
        var meshByFed = new Dictionary<int, int>();
        var dispositions = new Dictionary<int, NifModelBlockDisposition>();
        var rows = new List<NifModelPrimitiveRow>();
        var diagnostics = new NifModelDiagnosticSink();
        var dataCache = new Dictionary<(int Data, int Skin), NifModelGeometryData>();

        for (var index = 0; index < state.Blocks.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var shape = state.Blocks[index];
            if (graph.OccurrencesByBlock[index].Count == 0 ||
                !DataTypeByGeometryType.TryGetValue(shape.Type, out var expectedDataType))
            {
                continue;
            }

            var dataIndex = DataLink(state, shape, expectedDataType);
            if (dataIndex is null)
            {
                MarkUntyped(state, shape, null, null, NifModelCoverage.EmptyGeometryReason, dispositions, skins);
                continue;
            }

            var dataBlock = state.Blocks[dataIndex.Value];
            var packedIndex = PackedLink(state, dataBlock);
            var cacheKey = (dataIndex.Value, packedIndex is null ? -1 : SkinLink(shape));
            if (!dataCache.TryGetValue(cacheKey, out var data))
            {
                data = packedIndex is { } packed
                    ? NifPackedGeometryReader.Read(state, shape, dataBlock, state.Blocks[packed], platform, diagnostics,
                        cancellationToken)
                    : NifModelGeometryData.Read(state, dataIndex.Value, diagnostics, cancellationToken);
                dataCache.Add(cacheKey, data);
            }

            if (data.UntypedReason is { } reason)
            {
                MarkUntyped(state, shape, data.BlockIndex, packedIndex, reason, dispositions, skins);
                continue;
            }

            var groups = GroupByMaterial(graph.OccurrencesByBlock[index], data, materialFor, cancellationToken);
            var morph = NifModelMorphReader.Read(state, shape, data, diagnostics);
            var skin = skins?.Read(shape, data, graph.OccurrencesByBlock[index]) ?? NifModelShapeSkin.None;
            var attributes = skin.FaceAttributes.Count == 0
                ? data.Attributes
                : data.Attributes.Concat(skin.FaceAttributes).ToArray();
            var name = graph.FactsByBlock.TryGetValue(index, out var facts) ? facts.Name : "";
            for (var variant = 0; variant < groups.Count; variant++)
            {
                var (material, nodes) = groups[variant];
                var primitive = new ScenePrimitive(NifModelCoverage.Identity(data.BlockIndex), data.Vertices,
                    data.Triangulation!.Indices, material, SceneColorEncoding.FloatingPoint, morph.Targets,
                    skinInfluences: skin.Influences, tangents: data.Tangents,
                    additionalTextureCoordinates: data.AdditionalTextureCoordinates, normalMode: data.NormalMode)
                {
                    Faces = skin.Faces,
                    PointIndices = data.PointIndices,
                    Attributes = attributes,
                    PrimaryColorAttributeIndex = data.PrimaryColorAttributeIndex,
                    NormalProvenance = data.NormalProvenance
                };

                var meshIndex = meshes.Count;
                meshes.Add(new SceneMesh(name, [primitive]));
                meshByGeometry.TryAdd(index, meshIndex);
                meshByFed.TryAdd(data.BlockIndex, meshIndex);
                if (packedIndex is { } fedPacked)
                {
                    meshByFed.TryAdd(fedPacked, meshIndex);
                }

                foreach (var node in nodes)
                {
                    meshByNode[node] = meshIndex;
                }

                if (morph.TypedDataBlock is { } morphData)
                {
                    meshByFed.TryAdd(morphData, meshIndex);
                }

                rows.Add(new NifModelPrimitiveRow(meshIndex, data.BlockIndex,
                    PrimitivePayload(state, shape, data, morph, skin, nodes, material, variant, groups.Count)));
            }

            Mark(dispositions, index, NifModelBlockDisposition.Typed);
            Mark(dispositions, data.BlockIndex, NifModelBlockDisposition.Typed);
            if (packedIndex is { } typedPacked)
            {
                Mark(dispositions, typedPacked, NifModelBlockDisposition.Typed);
            }

            foreach (var (block, disposition) in morph.Dispositions)
            {
                Mark(dispositions, block, disposition);
            }
        }

        return new NifModelGeometryResult(meshes.AsReadOnly(), meshByGeometry, meshByNode, meshByFed, dispositions,
            rows.AsReadOnly(), diagnostics.ToList());
    }

    /// <summary>The occurrences of one geometry grouped by material, in first-occurrence order.</summary>
    private static List<(int? Material, List<int> Nodes)> GroupByMaterial(IReadOnlyList<int> occurrences,
        NifModelGeometryData data, Func<int, NifModelGeometryData, int?>? materialFor,
        CancellationToken cancellationToken)
    {
        var groups = new List<(int? Material, List<int> Nodes)>();
        foreach (var node in occurrences)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var material = materialFor?.Invoke(node, data);
            var group = groups.FindIndex(g => g.Material == material);
            if (group < 0)
            {
                groups.Add((material, new List<int> { node }));
            }
            else
            {
                groups[group].Nodes.Add(node);
            }
        }

        return groups;
    }

    /// <summary>
    ///     Records a decision for a block: Typed wins over NativeOnly (a data block shared by several geometries is typed
    ///     when any of them typed it), and the first NativeOnly reason is kept otherwise.
    /// </summary>
    private static void Mark(Dictionary<int, NifModelBlockDisposition> dispositions, int block,
        NifModelBlockDisposition disposition)
    {
        if (!dispositions.TryGetValue(block, out var existing) || (!existing.IsTyped && disposition.IsTyped))
        {
            dispositions[block] = disposition;
        }
    }

    /// <summary>
    ///     A geometry that yields no primitive: it, its data, its packed block, the morph data its controllers link and
    ///     its skin blocks are NativeOnly with the same reason (unless another geometry typed them).
    /// </summary>
    private static void MarkUntyped(NifModelReadState state, NifDecodedBlock shape, int? dataBlock, int? packedBlock,
        string reason, Dictionary<int, NifModelBlockDisposition> dispositions, NifModelSkinReader? skins)
    {
        skins?.MarkUntyped(shape, reason);
        var disposition = NifModelBlockDisposition.NativeOnly(reason);
        Mark(dispositions, shape.Index, disposition);
        if (dataBlock is { } data)
        {
            Mark(dispositions, data, disposition);
        }

        if (packedBlock is { } packed)
        {
            Mark(dispositions, packed, disposition);
        }

        foreach (var morphData in NifModelMorphReader.MorphDataBlocks(state, shape))
        {
            Mark(dispositions, morphData, disposition);
        }
    }

    /// <summary>The geometry's data block, or null for a null link.</summary>
    /// <exception cref="InvalidDataException">The link names a block that is not the expected data type.</exception>
    private static int? DataLink(NifModelReadState state, NifDecodedBlock shape, string expectedDataType)
    {
        if (!shape.Root.TryGet("Data", out var value) || value is not NifRefValue link)
        {
            throw new InvalidDataException(
                $"NIF block {shape.Index} ({shape.Type}) did not decode its NiGeometry Data link.");
        }

        if (link.IsNone)
        {
            return null;
        }

        var target = state.Blocks[link.Index].Type;
        return string.Equals(target, expectedDataType, StringComparison.Ordinal)
            ? link.Index
            : throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture,
                $"NIF block {shape.Index} ({shape.Type}) links block {link.Index} ({target}) as its data; a " +
                $"{shape.Type} requires {expectedDataType}."));
    }

    /// <summary>The BSPackedAdditionalGeometryData the data block's Additional Data names, or null.</summary>
    private static int? PackedLink(NifModelReadState state, NifDecodedBlock dataBlock)
    {
        return NifModelGeometryData.AdditionalDataType(state, dataBlock) is PackedAdditionalDataType
            ? NifModelGeometryData.AdditionalDataIndex(state, dataBlock)
            : null;
    }

    /// <summary>The shape's Skin Instance link as stored (-1 for none), for the packed data cache key.</summary>
    private static int SkinLink(NifDecodedBlock shape)
    {
        return shape.Root.TryGet("Skin Instance", out var value) && value is NifRefValue { IsNone: false } link
            ? link.Index
            : -1;
    }

    private static JsonObject PrimitivePayload(NifModelReadState state, NifDecodedBlock shape,
        NifModelGeometryData data, NifModelMorphResult morph, NifModelShapeSkin skin, IReadOnlyList<int> occurrences,
        int? material, int variant, int variants)
    {
        var payload = new JsonObject
        {
            ["geometryBlock"] = shape.Index,
            ["geometryType"] = shape.Type,
            ["nodes"] = NifModelNativeValues.Integers(occurrences)
        };
        foreach (var (key, value) in data.CreateFacts())
        {
            payload[key] = value?.DeepClone();
        }

        if (shape.Root.TryGet("Skin Instance", out var skinValue) && skinValue is NifRefValue skinLink)
        {
            payload["skinInstance"] = skinLink.Index;
            if (!skinLink.IsNone)
            {
                payload["skinInstanceType"] = state.Blocks[skinLink.Index].Type;
                payload["skin"] = skin.Facts?.DeepClone();
            }
        }

        if (data.Attributes.Count > 0 && data.PrimaryColorAttributeIndex is null)
        {
            payload["primaryColor"] = "none: the stored colors are not finite";
        }

        payload["morph"] = morph.Facts?.DeepClone();
        payload["material"] = material is { } index
            ? JsonValue.Create(index)
            : JsonValue.Create("none: no property is effective on these placements");
        payload["materialVariant"] = new JsonObject { ["ordinal"] = variant, ["count"] = variants };
        return payload;
    }
}
