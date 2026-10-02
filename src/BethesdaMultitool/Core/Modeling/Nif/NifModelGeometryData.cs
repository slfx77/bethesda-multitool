using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Formats.Nif.Decoding;
using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     The typed streams of one NiTriShapeData or NiTriStripsData block (plan section 3, "Primitive fields"), read once
///     from its decoded value tree and shared by every geometry block that references it. Nothing is normalized,
///     clamped, flipped or recomputed: positions, authored normals, UV sets and colors keep their stored bits.
/// </summary>
/// <remarks>
///     <para>
///         Rules. Normals absent: <c>(0, 0, 0)</c> with <see cref="SceneNormalMode.Flat" /> and Flat provenance (Shared
///         requires the two to agree). UV set 0 fills <see cref="SceneVertex.TexCoord" /> with no V flip (NIF, the
///         document and DDS are all top-left origin); later sets become additional coordinate sets, which cannot occur
///         at BS 14-34 because the BS202 "BS Data Flags" carries a single has-UV bit. Colors: finite Color4 values are
///         kept raw in <see cref="SceneVertex.Color" /> and in the primary attribute <see cref="VertexColorAttribute" />
///         (Float32 x4, sRGB Assumed per RE-11); a block holding any non-finite color keeps its exact bits in the
///         non-primary <see cref="RawVertexColorAttribute" /> (UInt32 x4, because Shared rejects non-finite Float32
///         attributes), neutral portable colors and a diagnostic. Tangents (<see cref="TangentMappingRule" />): glTF
///         defines TANGENT.xyz as the direction of increasing U and cross(N, TANGENT.xyz) * w as the bitangent, which
///         with glTF's top-left UV origin must run up the image (along -dP/dv). The stored array that runs along +dP/du
///         is the one nif.xml calls "Bitangents"; its "Tangents" runs along +dP/dv (measured 2026-09-28,
///         <see cref="TangentMappingEvidence" />). So <see cref="SceneTangents" /> xyz is the stored "Bitangents", raw,
///         and w = sign(dot(cross(N, Bitangents), -Tangents)) per vertex, +1 where that is zero or not finite (counted,
///         Assumed); both stored arrays stay in native state under their nif.xml names. Triangles: see
///         <see cref="NifStripTriangulator" />. Texture coordinates
///         holding NaN or infinity (measured 2026-09-24: 5 blocks in 4 FNV and 7 blocks in 5 FO3 retail files, e.g.
///         nv_hooverdam_aagun.nif with 152 of 208 components, nv_thetops_pool.nif with 2 of 766) keep the set's exact
///         bits in a <see cref="RawTexCoordAttribute" /> UInt32 x2 attribute and a diagnostic; only the non-finite
///         components read as 0 in the portable coordinates, per component, so the finite vertices of the same set keep
///         their texturing (unlike colors, which are neutralized as a whole block, a bad coordinate poisons only its own
///         vertex).
///     </para>
///     <para>
///         Console packed form (slice 10): a data block whose Additional Data names a BSPackedAdditionalGeometryData
///         stores no inline streams; <see cref="NifPackedGeometryReader" /> decodes the packed block and builds this
///         type through <see cref="FromPacked" />, with the vertices in the packed order (the identity for static
///         layouts, the concatenated NiSkinPartition vertex maps for skinned ones, stated by <see cref="PointIndices" />),
///         the same color, texture-coordinate and tangent rules applied to the decoded channels (the packed Bitangent
///         channel, the lower-offset frame stream, is the one that runs along +dP/du and fills the xyz, exactly as the PC
///         "Bitangents" array it reproduces), and <see cref="Packed" /> carrying the decoded channels for the skin reader.
///         <see cref="Read" /> never decodes a packed block itself.
///     </para>
///     <para>
///         Corrupt input throws <see cref="InvalidDataException" /> naming the block, field, element and offset: a
///         triangle or strip index at or beyond Num Vertices, or a non-finite position or normal (neither occurs in any
///         retail PC file: 0 of 14,880 FNV and 10,988 FO3 meshes).
///     </para>
/// </remarks>
internal sealed class NifModelGeometryData
{
    /// <summary>The primary vertex-color attribute name (finite colors).</summary>
    public const string VertexColorAttribute = "nif.vertexColor";

    /// <summary>The non-primary attribute that keeps a non-finite color array's exact bits.</summary>
    public const string RawVertexColorAttribute = "nif.vertexColor.raw";

    /// <summary>The semantic declared for <see cref="VertexColorAttribute" />.</summary>
    public const string VertexColorSemantic = "bmt.nif.vertex-color";

    /// <summary>The semantic declared for <see cref="RawVertexColorAttribute" />.</summary>
    public const string RawVertexColorSemantic = "bmt.nif.vertex-color-float-bits";

    /// <summary>The evidence recorded for the Assumed sRGB color space.</summary>
    public const string ColorSpaceEvidence =
        "RE-11 (open): Gamebryo multiplies vertex colors in gamma space (inferred, not yet established from the " +
        "FNV shader disassembly); NIF Color4 vertex colors are assumed sRGB.";

    /// <summary>Diagnostic code for a color array holding a non-finite value.</summary>
    public const string NonFiniteColorDiagnostic = "bmt.nif.non-finite-vertex-colors";
    /// <summary>
    ///     The attribute name prefix that keeps a non-finite texture-coordinate set's exact bits; the set index follows
    ///     (<c>nif.texCoord0.raw</c>).
    /// </summary>
    public const string RawTexCoordAttribute = "nif.texCoord";
    /// <summary>The semantic declared for a <see cref="RawTexCoordAttribute" /> attribute.</summary>
    public const string RawTexCoordSemantic = "bmt.nif.texture-coordinate-float-bits";
    /// <summary>Diagnostic code for a texture-coordinate set holding a non-finite value.</summary>
    public const string NonFiniteTexCoordDiagnostic = "bmt.nif.non-finite-texture-coordinates";

    /// <summary>Diagnostic code for a tangent array holding a non-finite value.</summary>
    public const string NonFiniteTangentDiagnostic = "bmt.nif.non-finite-tangents";

    /// <summary>The mapping of the stored tangent frame onto <see cref="SceneTangents" />, recorded in native state.</summary>
    public const string TangentMappingRule =
        "SceneTangents xyz = the stored Bitangents (inline) or the packed Bitangent channel, raw: the array that runs " +
        "along +dP/du, which glTF TANGENT requires; the stored Tangents (or the packed Tangent channel) runs along " +
        "+dP/dv and enters only the handedness; glTF's bitangent cross(N, xyz) * w runs along -dP/dv (up the image: " +
        "glTF and NIF UVs are both top-left origin and are passed through unflipped)";

    /// <summary>The handedness rule recorded in native state.</summary>
    public const string HandednessRule = "w = sign(dot(cross(N, Bitangents), -Tangents)) per vertex from the stored " +
                                         "Normals, Bitangents and Tangents (the packed Bitangent and Tangent " +
                                         "channels); +1 where the product is zero or not finite";

    /// <summary>The measurement the mapping rests on, recorded in native state.</summary>
    public const string TangentMappingEvidence =
        "measured 2026-09-28 against the UV derivatives of UV set 0 (TestOutput/nif-tangent-frame-20260928/MEASURE.md): " +
        "the stored Bitangents run along +dP/du on 99.91% of scored PC cover vertices, the packed Bitangent channel on " +
        "99.94% of console ones; the stored Tangents along +dP/du on 0.12% and 0.09%";

    /// <summary>The known class whose stored frame carries no mirroring, recorded in native state.</summary>
    public const string TangentHandednessException =
        "owner ruling pending: terrain LOD shapes (shader flags 2 bit 2) and seven FO3 SCOL files store Tangents = " +
        "cross(N, Bitangents), so w is -1 on every vertex there although the UVs mirror (the stored frame is " +
        "reproduced; the direction still runs along +dP/du)";

    private readonly NifDecodedBlock _block;
    private readonly NifFloatArrayValue? _storedTangents;
    private readonly NifFloatArrayValue? _storedBitangents;
    private readonly int _colorNonFinite;
    private readonly int _texCoordNonFinite;
    private readonly int _colorOutOfUnitRange;
    private readonly int _handednessNegative;
    private readonly int _handednessPositive;
    private readonly int _handednessUndetermined;
    private readonly JsonObject? _packedTriangleFacts;

    private NifModelGeometryData(NifDecodedBlock block, string? untypedReason, string? untypedDetail)
    {
        _block = block;
        UntypedReason = untypedReason;
        UntypedDetail = untypedDetail;
        Vertices = Array.Empty<SceneVertex>();
        AdditionalTextureCoordinates = Array.Empty<SceneTextureCoordinates>();
        Attributes = Array.Empty<SceneAttributeStream>();
        NormalProvenance = new SceneNormalProvenance(SceneNormalProvenanceKind.Flat);
        NormalMode = SceneNormalMode.Flat;
        UvSetCount = 0;
    }

    private NifModelGeometryData(NifDecodedBlock block, int vertexCount, NifFloatArrayValue positions,
        IReadOnlyList<SceneVertex> vertices, NifTriangulation triangulation, bool normalsAuthored,
        SceneTangents? tangents, NifFloatArrayValue? storedTangents, NifFloatArrayValue? storedBitangents,
        (int Positive, int Negative, int Undetermined) handedness,
        IReadOnlyList<SceneTextureCoordinates> additional, int uvSetCount, IReadOnlyList<SceneAttributeStream> attributes,
        int? primaryColor, int colorNonFinite, int colorOutOfUnitRange, bool hasTangentArrays, int texCoordNonFinite)
    {
        _block = block;
        VertexCount = vertexCount;
        Positions = positions;
        Vertices = vertices;
        Triangulation = triangulation;
        NormalsAuthored = normalsAuthored;
        NormalMode = normalsAuthored ? SceneNormalMode.Vertex : SceneNormalMode.Flat;
        NormalProvenance = new SceneNormalProvenance(normalsAuthored
            ? SceneNormalProvenanceKind.Authored
            : SceneNormalProvenanceKind.Flat);
        Tangents = tangents;
        _storedTangents = storedTangents;
        _storedBitangents = storedBitangents;
        (_handednessPositive, _handednessNegative, _handednessUndetermined) = handedness;
        AdditionalTextureCoordinates = additional;
        UvSetCount = uvSetCount;
        Attributes = attributes;
        PrimaryColorAttributeIndex = primaryColor;
        _colorNonFinite = colorNonFinite;
        _colorOutOfUnitRange = colorOutOfUnitRange;
        _texCoordNonFinite = texCoordNonFinite;
        HasTangentArrays = hasTangentArrays;
    }

    /// <summary>The packed (console) form: see <see cref="FromPacked" />.</summary>
    private NifModelGeometryData(NifDecodedBlock block, int vertexCount, NifPackedGeometryStreams packed,
        NifFloatArrayValue positions, IReadOnlyList<SceneVertex> vertices, NifTriangulation triangulation,
        ScenePointIndices? pointIndices, IReadOnlyList<NifSkinPartitionView>? partitions,
        IReadOnlyList<int>? partitionStarts, SceneTangents? tangents,
        (int Positive, int Negative, int Undetermined) handedness, IReadOnlyList<SceneAttributeStream> attributes,
        int? primaryColor, int colorOutOfUnitRange, int texCoordNonFinite, JsonObject? packedTriangleFacts)
    {
        _block = block;
        VertexCount = vertexCount;
        Packed = packed;
        _texCoordNonFinite = texCoordNonFinite;
        Positions = positions;
        Vertices = vertices;
        Triangulation = triangulation;
        PointIndices = pointIndices;
        PackedPartitions = partitions;
        PackedPartitionStarts = partitionStarts;
        NormalsAuthored = true;
        NormalMode = SceneNormalMode.Vertex;
        NormalProvenance = new SceneNormalProvenance(SceneNormalProvenanceKind.Authored);
        Tangents = tangents;
        (_handednessPositive, _handednessNegative, _handednessUndetermined) = handedness;
        AdditionalTextureCoordinates = Array.Empty<SceneTextureCoordinates>();
        UvSetCount = 1;
        Attributes = attributes;
        PrimaryColorAttributeIndex = primaryColor;
        _colorOutOfUnitRange = colorOutOfUnitRange;
        HasTangentArrays = true;
        _packedTriangleFacts = packedTriangleFacts;
    }

    /// <summary>The data block's index.</summary>
    public int BlockIndex => _block.Index;

    /// <summary>The data block's type (NiTriShapeData or NiTriStripsData).</summary>
    public string BlockType => _block.Type;

    /// <summary>Null when the block yields a primitive; otherwise its NativeOnly coverage reason.</summary>
    public string? UntypedReason { get; }

    /// <summary>A short explanation of why the block yields no primitive (null when it does).</summary>
    public string? UntypedDetail { get; }

    /// <summary>True when the block yields a primitive.</summary>
    public bool IsDrawable => UntypedReason is null;

    /// <summary>The stored Num Vertices (the shape vertex domain; the primitive may hold more packed vertices).</summary>
    public int VertexCount { get; }

    /// <summary>
    ///     The positions exactly as decoded, one per primitive vertex (null when the block is not drawable): the stored
    ///     Vertices for inline streams, the exactly widened halves in packed order for the packed form.
    /// </summary>
    public NifFloatArrayValue? Positions { get; }

    /// <summary>The document vertices in stored order (packed order for the packed form).</summary>
    public IReadOnlyList<SceneVertex> Vertices { get; }

    /// <summary>The kept triangles and drop counts, in primitive vertex indices (null when the block is not drawable).</summary>
    public NifTriangulation? Triangulation { get; }

    /// <summary>True when the block stores normals.</summary>
    public bool NormalsAuthored { get; }

    /// <summary>Vertex for authored normals, Flat otherwise.</summary>
    public SceneNormalMode NormalMode { get; }

    /// <summary>Authored or Flat, agreeing with <see cref="NormalMode" />.</summary>
    public SceneNormalProvenance NormalProvenance { get; }

    /// <summary>
    ///     The typed tangent basis as glTF defines it (<see cref="TangentMappingRule" />), or null when none is stored (or
    ///     a stored tangent-frame value is not finite).
    /// </summary>
    public SceneTangents? Tangents { get; }

    /// <summary>True when the block stores Tangents and Bitangents arrays (or the packed layout carries them).</summary>
    public bool HasTangentArrays { get; }

    /// <summary>UV sets 1 and later.</summary>
    public IReadOnlyList<SceneTextureCoordinates> AdditionalTextureCoordinates { get; }

    /// <summary>The stored UV set count (UV set 0 is in <see cref="SceneVertex.TexCoord" />).</summary>
    public int UvSetCount { get; }

    /// <summary>The source attribute streams (the vertex-color stream when colors are stored).</summary>
    public IReadOnlyList<SceneAttributeStream> Attributes { get; }

    /// <summary>The primary color attribute (0) for finite colors; null otherwise.</summary>
    public int? PrimaryColorAttributeIndex { get; }

    /// <summary>The decoded packed channels for the console form; null for inline streams.</summary>
    public NifPackedGeometryStreams? Packed { get; }

    /// <summary>
    ///     For the packed skinned form, the source point of every primitive vertex (the concatenated partition vertex
    ///     maps over a point domain of Num Vertices); null for the identity domain.
    /// </summary>
    public ScenePointIndices? PointIndices { get; }

    /// <summary>For the packed skinned form, the checked partitions whose vertex maps give the packed order; else null.</summary>
    public IReadOnlyList<NifSkinPartitionView>? PackedPartitions { get; }

    /// <summary>For the packed skinned form, the first primitive vertex of each partition; else null.</summary>
    public IReadOnlyList<int>? PackedPartitionStarts { get; }

    /// <summary>Reads one data block with inline streams (see the type remarks for the rules).</summary>
    /// <param name="state">The read state.</param>
    /// <param name="blockIndex">A NiTriShapeData or NiTriStripsData block that decoded strictly.</param>
    /// <param name="diagnostics">Where non-finite color and tangent diagnostics go.</param>
    /// <param name="cancellationToken">Observed between streams.</param>
    /// <exception cref="InvalidDataException">An index is out of range or a required value is not finite.</exception>
    public static NifModelGeometryData Read(NifModelReadState state, int blockIndex,
        NifModelDiagnosticSink diagnostics, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(diagnostics);
        var block = state.Blocks[blockIndex];
        if (AdditionalDataType(state, block) is NifModelGeometryReader.PackedAdditionalDataType)
        {
            return Untyped(block, NifModelCoverage.PackedLayoutUnknownReason,
                "its streams live in a BSPackedAdditionalGeometryData that was not decoded");
        }

        var vertexCount = checked((int)RequireInteger(block, "Num Vertices"));
        var triangulation = string.Equals(block.Type, "NiTriStripsData", StringComparison.Ordinal)
            ? ReadStrips(block, vertexCount)
            : ReadList(block, vertexCount);
        cancellationToken.ThrowIfCancellationRequested();

        var positions = OptionalFloats(block, "Vertices", 3, vertexCount);
        if (vertexCount == 0 || positions is null)
        {
            return Untyped(block, NifModelCoverage.EmptyGeometryReason, "it stores no vertices");
        }

        if (triangulation is null || triangulation.StoredTriangles == 0)
        {
            return Untyped(block, NifModelCoverage.EmptyGeometryReason, "it stores no triangles");
        }

        if (triangulation.KeptTriangles == 0)
        {
            return Untyped(block, NifModelCoverage.EmptyGeometryReason, string.Create(
                CultureInfo.InvariantCulture,
                $"all {triangulation.StoredTriangles} stored triangle(s) repeat a vertex index"));
        }

        RequireFinite(block, positions, "Vertices", 0);
        var normals = OptionalFloats(block, "Normals", 3, vertexCount);
        if (normals is not null)
        {
            RequireFinite(block, normals, "Normals", 0);
        }

        var uvSets = UvSets(block, vertexCount);
        cancellationToken.ThrowIfCancellationRequested();

        var colors = OptionalFloats(block, "Vertex Colors", 4, vertexCount);
        var (colorNonFinite, colorOutOfRange) = colors is null ? (0, 0) : ColorCensus(colors);
        var attributes = new List<SceneAttributeStream>();
        int? primaryColor = null;
        var texCoordNonFinite = 0;
        for (var set = 0; set < uvSets.Count; set++)
        {
            var first = FirstNonFinite(uvSets[set]);
            if (first < 0)
            {
                continue;
            }

            var count = CountNonFinite(uvSets[set]);
            texCoordNonFinite += count;
            attributes.Add(new SceneAttributeStream(
                string.Create(CultureInfo.InvariantCulture, $"{RawTexCoordAttribute}{set}.raw"), RawTexCoordSemantic,
                SceneAttributeDomain.Vertex, SceneAttributeComponentType.UInt32, 2, vertexCount,
                LittleEndianBits(uvSets[set])));
            diagnostics.Add(NonFiniteTexCoordDiagnostic, string.Create(CultureInfo.InvariantCulture,
                $"Block {block.Index} ({block.Type}): {count} texture-coordinate component(s) of UV set {set} are NaN " +
                $"or infinite (first at element {first / 2}, offset " +
                $"0x{ElementOffset(block, "UV Sets", set * vertexCount * 2 + first, 4):X}). Those components read as 0 " +
                $"in the portable coordinates; the set's exact bits are kept in the '{RawTexCoordAttribute}{set}.raw' " +
                $"attribute and native state."));
        }

        if (colors is not null)
        {
            var bytes = LittleEndianBits(colors);
            if (colorNonFinite == 0)
            {
                attributes.Add(new SceneAttributeStream(VertexColorAttribute, VertexColorSemantic,
                    SceneAttributeDomain.Vertex, SceneAttributeComponentType.Float32, 4, vertexCount, bytes,
                    colorSpace: SceneColorSpace.Srgb, colorSpaceProvenance: SceneValueProvenance.Assumed,
                    colorSpaceEvidence: ColorSpaceEvidence));
                primaryColor = attributes.Count - 1;
            }
            else
            {
                attributes.Add(new SceneAttributeStream(RawVertexColorAttribute, RawVertexColorSemantic,
                    SceneAttributeDomain.Vertex, SceneAttributeComponentType.UInt32, 4, vertexCount, bytes));
                diagnostics.Add(NonFiniteColorDiagnostic, string.Create(CultureInfo.InvariantCulture,
                    $"Block {block.Index} ({block.Type}): {colorNonFinite} vertex-color component(s) are NaN or " +
                    $"infinite (first at offset 0x{ElementOffset(block, "Vertex Colors", FirstNonFinite(colors), 4):X}). " +
                    $"The portable vertex colors are neutral (1, 1, 1, 1); the exact bits are kept in the " +
                    $"'{RawVertexColorAttribute}' attribute and native state."));
            }
        }

        // nif.xml stores the two arrays under one condition (Has Normals and BS Data Flags bit 12), Tangents first. The
        // one that runs along +dP/du, and so becomes the glTF TANGENT, is "Bitangents" (TangentMappingRule).
        var storedTangents = OptionalFloats(block, "Tangents", 3, vertexCount);
        var storedBitangents = OptionalFloats(block, "Bitangents", 3, vertexCount);
        SceneTangents? tangents = null;
        (int Positive, int Negative, int Undetermined) handedness = (0, 0, 0);
        if (storedTangents is not null && storedBitangents is not null && normals is not null)
        {
            var nonFiniteArray = "Bitangents";
            var firstNonFinite = FirstNonFinite(storedBitangents);
            if (firstNonFinite < 0)
            {
                nonFiniteArray = "Tangents";
                firstNonFinite = FirstNonFinite(storedTangents);
            }

            if (firstNonFinite < 0)
            {
                (tangents, handedness) = BuildTangents(normals.ToSingleArray(), storedBitangents.ToSingleArray(),
                    storedTangents.ToSingleArray(), vertexCount);
            }
            else
            {
                diagnostics.Add(NonFiniteTangentDiagnostic, string.Create(CultureInfo.InvariantCulture,
                    $"Block {block.Index} ({block.Type}): a stored {nonFiniteArray} value is NaN or infinite (element " +
                    $"{firstNonFinite / 3}, offset 0x{ElementOffset(block, nonFiniteArray, firstNonFinite, 4):X}); the " +
                    $"primitive carries no typed tangents and the stored arrays stay in native state."));
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var vertices = new SceneVertex[vertexCount];
        var uv0 = uvSets.Count > 0 ? uvSets[0] : null;
        for (var i = 0; i < vertexCount; i++)
        {
            var normal = normals is null
                ? Vector3.Zero
                : new Vector3(normals.Get(i, 0), normals.Get(i, 1), normals.Get(i, 2));
            var color = colors is null || colorNonFinite > 0
                ? Vector4.One
                : new Vector4(colors.Get(i, 0), colors.Get(i, 1), colors.Get(i, 2), colors.Get(i, 3));
            var uv = uv0 is null ? Vector2.Zero : new Vector2(Finite(uv0.Get(i, 0)), Finite(uv0.Get(i, 1)));
            vertices[i] = new SceneVertex(
                new Vector3(positions.Get(i, 0), positions.Get(i, 1), positions.Get(i, 2)), normal, color, uv);
        }

        var additional = new List<SceneTextureCoordinates>();
        for (var set = 1; set < uvSets.Count; set++)
        {
            var values = new Vector2[vertexCount];
            for (var i = 0; i < vertexCount; i++)
            {
                values[i] = new Vector2(Finite(uvSets[set].Get(i, 0)), Finite(uvSets[set].Get(i, 1)));
            }

            additional.Add(new SceneTextureCoordinates(values, cancellationToken));
        }

        return new NifModelGeometryData(block, vertexCount, positions, vertices, triangulation, normals is not null,
            tangents, storedTangents, storedBitangents, handedness, additional.AsReadOnly(), uvSets.Count,
            attributes.AsReadOnly(), primaryColor, colorNonFinite, colorOutOfRange,
            storedTangents is not null || storedBitangents is not null, texCoordNonFinite);
    }

    /// <summary>A block that yields no primitive, with its NativeOnly reason and a short explanation.</summary>
    public static NifModelGeometryData Untyped(NifDecodedBlock block, string reason, string detail)
    {
        ArgumentNullException.ThrowIfNull(block);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);
        return new NifModelGeometryData(block, reason, detail);
    }

    /// <summary>
    ///     Builds the packed (console) form from decoded channels already gathered into the primitive vertex order by
    ///     <see cref="NifPackedGeometryReader" />: the color and tangent rules of the inline form are applied to the
    ///     decoded values (colors are bytes / 255, so they are always finite and inside the unit range; a non-finite
    ///     tangent frame leaves the basis untyped, which the caller reports).
    /// </summary>
    /// <param name="block">The data block.</param>
    /// <param name="vertexCount">The stored Num Vertices (the point domain).</param>
    /// <param name="packed">The decoded packed channels (in packed order).</param>
    /// <param name="order">Primitive vertex i is packed vertex <c>order[i]</c> (the identity, or the partition order).</param>
    /// <param name="triangulation">The triangles in primitive vertex indices.</param>
    /// <param name="pointIndices">The point identities for the partition order, or null for the identity domain.</param>
    /// <param name="partitions">The partitions of the skinned form, or null.</param>
    /// <param name="partitionStarts">The first primitive vertex of each partition, or null.</param>
    /// <param name="packedTriangleFacts">Triangle facts of the skinned form (per partition), or null for the data block's own.</param>
    public static NifModelGeometryData FromPacked(NifDecodedBlock block, int vertexCount,
        NifPackedGeometryStreams packed, IReadOnlyList<int> order, NifTriangulation triangulation,
        ScenePointIndices? pointIndices, IReadOnlyList<NifSkinPartitionView>? partitions,
        IReadOnlyList<int>? partitionStarts, JsonObject? packedTriangleFacts)
    {
        ArgumentNullException.ThrowIfNull(block);
        ArgumentNullException.ThrowIfNull(packed);
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(triangulation);
        var count = order.Count;
        var positionBits = new uint[count * 3];
        var normals = new float[count * 3];
        var tangentValues = new float[count * 3];
        var bitangentValues = new float[count * 3];
        var vertices = new SceneVertex[count];
        var colorBytes = packed.Colors is null ? null : new byte[count * 16];
        var outOfRange = 0;
        for (var i = 0; i < count; i++)
        {
            var p = order[i];
            for (var c = 0; c < 3; c++)
            {
                positionBits[i * 3 + c] = BitConverter.SingleToUInt32Bits(packed.Positions[p * 3 + c]);
                normals[i * 3 + c] = packed.Normals[p * 3 + c];
                tangentValues[i * 3 + c] = packed.Tangents[p * 3 + c];
                bitangentValues[i * 3 + c] = packed.Bitangents[p * 3 + c];
            }

            var color = Vector4.One;
            if (packed.Colors is { } colors)
            {
                color = new Vector4(colors[p * 4], colors[p * 4 + 1], colors[p * 4 + 2], colors[p * 4 + 3]);
                for (var c = 0; c < 4; c++)
                {
                    BinaryPrimitives.WriteSingleLittleEndian(colorBytes!.AsSpan(i * 16 + c * 4), colors[p * 4 + c]);
                    if (colors[p * 4 + c] is < 0f or > 1f)
                    {
                        outOfRange++;
                    }
                }
            }

            vertices[i] = new SceneVertex(
                new Vector3(packed.Positions[p * 3], packed.Positions[p * 3 + 1], packed.Positions[p * 3 + 2]),
                new Vector3(normals[i * 3], normals[i * 3 + 1], normals[i * 3 + 2]), color,
                new Vector2(Finite(packed.TexCoords[p * 2]), Finite(packed.TexCoords[p * 2 + 1])));
        }

        var attributes = new List<SceneAttributeStream>();
        int? primaryColor = null;
        if (packed.TexCoordNonFinite > 0)
        {
            // The same shape as the inline rule: the set's exact bits (here the exactly widened halves) in the raw
            // attribute, the non-finite components read as 0 in the portable coordinates (applied above).
            var uvBits = new byte[count * 8];
            for (var i = 0; i < count; i++)
            {
                var p = order[i];
                BinaryPrimitives.WriteSingleLittleEndian(uvBits.AsSpan(i * 8), packed.TexCoords[p * 2]);
                BinaryPrimitives.WriteSingleLittleEndian(uvBits.AsSpan(i * 8 + 4), packed.TexCoords[p * 2 + 1]);
            }

            attributes.Add(new SceneAttributeStream(RawTexCoordAttribute + "0.raw", RawTexCoordSemantic,
                SceneAttributeDomain.Vertex, SceneAttributeComponentType.UInt32, 2, count, uvBits));
        }

        if (colorBytes is not null)
        {
            attributes.Add(new SceneAttributeStream(VertexColorAttribute, VertexColorSemantic,
                SceneAttributeDomain.Vertex, SceneAttributeComponentType.Float32, 4, count, colorBytes,
                colorSpace: SceneColorSpace.Srgb, colorSpaceProvenance: SceneValueProvenance.Assumed,
                colorSpaceEvidence: ColorSpaceEvidence));
            primaryColor = attributes.Count - 1;
        }

        SceneTangents? tangents = null;
        (int Positive, int Negative, int Undetermined) handedness = (0, 0, 0);
        if (packed.TangentFrameNonFinite == 0)
        {
            // The packed Bitangent channel (the lower-offset frame stream, the PC Bitangents array) runs along +dP/du.
            (tangents, handedness) = BuildTangents(normals, bitangentValues, tangentValues, count);
        }

        return new NifModelGeometryData(block, vertexCount, packed, new NifFloatArrayValue("Vector3", 3, positionBits),
            vertices, triangulation, pointIndices, partitions, partitionStarts, tangents, handedness,
            attributes.AsReadOnly(), primaryColor, outOfRange, packed.TexCoordNonFinite, packedTriangleFacts);
    }

    /// <summary>
    ///     The native facts of this block for a <c>bmt.nif.primitive</c> row, built fresh on each call so several
    ///     primitives sharing the block each own their JSON.
    /// </summary>
    public JsonObject CreateFacts()
    {
        var root = _block.Root;
        var facts = new JsonObject
        {
            ["dataBlock"] = BlockIndex,
            ["dataType"] = BlockType,
            ["groupId"] = Field(root, "Group ID"),
            ["keepFlags"] = Field(root, "Keep Flags"),
            ["compressFlags"] = Field(root, "Compress Flags"),
            ["hasVertices"] = Field(root, "Has Vertices"),
            ["numVertices"] = Field(root, "Num Vertices")
        };
        if (root.Contains("BS Data Flags"))
        {
            facts["bsDataFlags"] = Field(root, "BS Data Flags");
        }

        if (root.Contains("Data Flags"))
        {
            facts["dataFlags"] = Field(root, "Data Flags");
        }

        facts["consistencyFlags"] = Field(root, "Consistency Flags");
        facts["boundingSphere"] = Field(root, "Bounding Sphere");
        facts["additionalData"] = Field(root, "Additional Data");
        if (Packed is { } packed)
        {
            AddPackedFacts(facts, packed);
            return facts;
        }

        facts["normals"] = NormalsAuthored
            ? "authored"
            : "absent: normals are (0, 0, 0) with NormalMode Flat and Flat provenance";
        facts["uvSets"] = UvSetCount;
        facts["texCoordNonFiniteComponents"] = _texCoordNonFinite;
        facts["uvVFlip"] = false;
        facts["vertexColors"] = ColorFacts(root);
        facts["triangles"] = TriangleFacts(root);
        if (root.Contains("Match Groups"))
        {
            facts["matchGroups"] = Field(root, "Match Groups");
        }

        if (HasTangentArrays)
        {
            facts["tangents"] = TangentFacts("the stored Bitangents array, raw", "Bitangents", "Tangents");
            facts["storedTangentFrame"] = new JsonObject
            {
                ["order"] = "nif.xml names in stored order: Tangents (runs along +dP/dv), then Bitangents (runs " +
                            "along +dP/du, the typed xyz)",
                ["Tangents"] = _storedTangents is null ? null : NifModelNativeValues.ToJson(_storedTangents),
                ["Bitangents"] = _storedBitangents is null ? null : NifModelNativeValues.ToJson(_storedBitangents)
            };
        }

        if (!IsDrawable)
        {
            facts["untyped"] = UntypedDetail;
        }

        return facts;
    }

    /// <summary>The packed form's facts: the decoded block, the vertex order, colors, tangents and triangles.</summary>
    private void AddPackedFacts(JsonObject facts, NifPackedGeometryStreams packed)
    {
        facts["packed"] = packed.CreateFacts();
        facts["normals"] = "authored (packed channel)";
        facts["uvSets"] = 1;
        facts["texCoordNonFiniteComponents"] = _texCoordNonFinite;
        facts["uvVFlip"] = false;
        if (packed.Colors is null)
        {
            facts["vertexColors"] = new JsonObject { ["state"] = "absent", ["portable"] = "(1, 1, 1, 1)" };
        }
        else
        {
            var colors = new JsonObject
            {
                ["state"] = "finite",
                ["source"] = "packed D3DCOLOR bytes / 255",
                ["byteOrder"] = packed.Platform.ColorByteOrder,
                ["byteOrderProvenance"] = packed.ColorByteOrderProvenance.ToString(),
                ["colorOrderSensitiveVertices"] = packed.ColorOrderSensitiveVertices,
                ["componentsOutsideUnitRange"] = _colorOutOfUnitRange,
                ["attribute"] = VertexColorAttribute,
                ["colorSpace"] = nameof(SceneColorSpace.Srgb),
                ["colorSpaceProvenance"] = nameof(SceneValueProvenance.Assumed)
            };
            if (packed.ColorByteOrderEvidence is { } evidence)
            {
                colors["colorByteOrderEvidence"] = evidence;
            }

            facts["vertexColors"] = colors;
        }

        facts["vertexOrder"] = PointIndices is { } points
            ? new JsonObject
            {
                ["form"] = "partition order",
                ["primitiveVertices"] = Vertices.Count,
                ["pointCount"] = points.PointCount,
                ["partitions"] = PackedPartitions?.Count,
                ["partitionStarts"] = PackedPartitionStarts is null
                    ? null
                    : NifModelNativeValues.Integers(PackedPartitionStarts),
                ["rule"] = "primitive vertex = the concatenated NiSkinPartition vertex maps; PointIndices names each " +
                           "vertex's shape vertex; never welded"
            }
            : new JsonObject
            {
                ["form"] = "identity",
                ["primitiveVertices"] = Vertices.Count,
                ["rule"] = "packed vertex i is shape vertex i (the console index buffers equal the PC ones)"
            };
        facts["triangles"] = _packedTriangleFacts?.DeepClone() ?? TriangleFacts(_block.Root);
        facts["tangents"] = TangentFacts("the packed Bitangent channel, exactly widened", "Bitangent channel",
            "Tangent channel");
        facts["storedTangentFrame"] = new JsonObject
        {
            ["order"] = "the Bitangent channel (the lower offset; runs along +dP/du, the typed xyz), then the " +
                        "Tangent channel (runs along +dP/dv); both keep their exact bytes in the packed block's row",
            ["bitangentOffset"] = packed.Layout.Stream(NifPackedStreamKind.Bitangent)?.Offset,
            ["tangentOffset"] = packed.Layout.Stream(NifPackedStreamKind.Tangent)?.Offset
        };
        if (!IsDrawable)
        {
            facts["untyped"] = UntypedDetail;
        }
    }

    /// <summary>
    ///     The tangent facts of a primitive row: whether the basis is typed, which stored array fills its xyz and which
    ///     enters only its handedness, the handedness counts, the rules and the evidence.
    /// </summary>
    private JsonObject TangentFacts(string source, string xyzArray, string handednessArray)
    {
        return new JsonObject
        {
            ["typed"] = Tangents is not null,
            ["source"] = source,
            ["xyz"] = xyzArray,
            ["handednessFrom"] = handednessArray,
            ["handedness"] = new JsonObject
            {
                ["positive"] = _handednessPositive,
                ["negative"] = _handednessNegative,
                ["undeterminedAsPositive"] = _handednessUndetermined
            },
            ["mapping"] = TangentMappingRule,
            ["rule"] = HandednessRule,
            ["evidence"] = TangentMappingEvidence,
            ["exception"] = TangentHandednessException,
            ["provenance"] = nameof(SceneValueProvenance.Assumed)
        };
    }

    private JsonObject ColorFacts(NifStructValue root)
    {
        if (!root.Contains("Vertex Colors"))
        {
            return new JsonObject { ["state"] = "absent", ["portable"] = "(1, 1, 1, 1)" };
        }

        return _colorNonFinite > 0
            ? new JsonObject
            {
                ["state"] = "non-finite",
                ["nonFiniteComponents"] = _colorNonFinite,
                ["portable"] = "(1, 1, 1, 1)",
                ["attribute"] = RawVertexColorAttribute,
                ["values"] = Field(root, "Vertex Colors")
            }
            : new JsonObject
            {
                ["state"] = "finite",
                ["componentsOutsideUnitRange"] = _colorOutOfUnitRange,
                ["attribute"] = VertexColorAttribute,
                ["colorSpace"] = nameof(SceneColorSpace.Srgb),
                ["colorSpaceProvenance"] = nameof(SceneValueProvenance.Assumed)
            };
    }

    private JsonObject TriangleFacts(NifStructValue root)
    {
        var isStrips = string.Equals(BlockType, "NiTriStripsData", StringComparison.Ordinal);
        var triangles = new JsonObject
        {
            ["form"] = isStrips ? "strips" : "list",
            ["numTriangles"] = Field(root, "Num Triangles")
        };
        if (isStrips)
        {
            triangles["numStrips"] = Field(root, "Num Strips");
            triangles["stripLengths"] = Field(root, "Strip Lengths");
            triangles["hasPoints"] = Field(root, "Has Points");
        }
        else
        {
            triangles["numTrianglePoints"] = Field(root, "Num Triangle Points");
            triangles["hasTriangles"] = Field(root, "Has Triangles");
        }

        if (Triangulation is { } result)
        {
            triangles["stored"] = result.StoredTriangles;
            triangles["kept"] = result.KeptTriangles;
            triangles["droppedRepeatedIndex"] = result.DroppedTriangles;
            triangles["droppedOrdinals"] = NifModelNativeValues.Integers(result.DroppedOrdinals);
            if (isStrips)
            {
                triangles["droppedPerStrip"] = NifModelNativeValues.Integers(result.DroppedPerStrip);
            }

            triangles["rule"] = isStrips
                ? "for i in 0..L-3: (p[i], p[i+1], p[i+2]); repeated index dropped; odd i emits (a, c, b); " +
                  "parity from the strip start including dropped triangles"
                : "triangles repeating a vertex index are dropped; zero-area triangles with distinct indices are kept";
        }

        return triangles;
    }

    /// <summary>
    ///     The tangent basis as glTF defines it (<see cref="TangentMappingRule" />): xyz is the stored array that runs
    ///     along +dP/du, raw (never normalized: the GLB writer declares that conversion), and
    ///     w = sign(dot(cross(N, U), -V)), so that glTF's bitangent cross(N, xyz) * w points against the stored +dP/dv
    ///     array, which is up the image for glTF's top-left UV origin; +1 where the product is zero or not finite
    ///     (undetermined), with the handedness counts. The product is evaluated in double from the stored singles.
    /// </summary>
    /// <param name="normals">Three components per vertex.</param>
    /// <param name="uDirection">
    ///     The stored array that runs along +dP/du, three components per vertex: the inline "Bitangents" or the packed
    ///     Bitangent channel.
    /// </param>
    /// <param name="vDirection">
    ///     The stored array that runs along +dP/dv, three components per vertex: the inline "Tangents" or the packed
    ///     Tangent channel.
    /// </param>
    /// <param name="vertexCount">The vertex count.</param>
    internal static (SceneTangents Tangents, (int, int, int) Handedness) BuildTangents(float[] normals,
        float[] uDirection, float[] vDirection, int vertexCount)
    {
        ArgumentNullException.ThrowIfNull(normals);
        ArgumentNullException.ThrowIfNull(uDirection);
        ArgumentNullException.ThrowIfNull(vDirection);
        var values = new Vector4[vertexCount];
        int positive = 0, negative = 0, undetermined = 0;
        for (var i = 0; i < vertexCount; i++)
        {
            float ux = uDirection[i * 3], uy = uDirection[i * 3 + 1], uz = uDirection[i * 3 + 2];
            double nx = normals[i * 3], ny = normals[i * 3 + 1], nz = normals[i * 3 + 2];
            double cx = ny * uz - nz * uy, cy = nz * ux - nx * uz, cz = nx * uy - ny * ux;
            var dot = -(cx * vDirection[i * 3] + cy * vDirection[i * 3 + 1] + cz * vDirection[i * 3 + 2]);
            var w = 1f;
            if (dot > 0)
            {
                positive++;
            }
            else if (dot < 0)
            {
                w = -1f;
                negative++;
            }
            else
            {
                undetermined++;
            }

            values[i] = new Vector4(ux, uy, uz, w);
        }

        return (new SceneTangents(values), (positive, negative, undetermined));
    }

    /// <summary>The kept triangles of a NiTriShapeData list, or null when it stores none.</summary>
    /// <exception cref="InvalidDataException">An index is not below <paramref name="vertexCount" />.</exception>
    internal static NifTriangulation? ReadList(NifDecodedBlock block, int vertexCount)
    {
        if (!block.Root.TryGet("Triangles", out var value))
        {
            return null;
        }

        if (value is not NifUInt16ArrayValue { ComponentsPerElement: 3 } triangles)
        {
            throw Shape(block, "Triangles", value);
        }

        var components = triangles.Values;
        for (var i = 0; i < components.Length; i++)
        {
            if (components[i] >= vertexCount)
            {
                throw IndexOutOfRange(block, "Triangles", i / 3, components[i], vertexCount,
                    ElementOffset(block, "Triangles", i, 2));
            }
        }

        return NifStripTriangulator.FilterList(components);
    }

    /// <summary>The kept triangles of a NiTriStripsData strip set, or null when it stores no points.</summary>
    /// <exception cref="InvalidDataException">A point is not below <paramref name="vertexCount" />.</exception>
    internal static NifTriangulation? ReadStrips(NifDecodedBlock block, int vertexCount)
    {
        if (!block.Root.TryGet("Points", out var value))
        {
            return null;
        }

        if (value is not NifArrayValue { IsRows: true } rows)
        {
            throw Shape(block, "Points", value);
        }

        var strips = new List<ushort[]>(rows.Count);
        var flat = 0;
        foreach (var row in rows.Items)
        {
            if (row is not NifUInt16ArrayValue { ComponentsPerElement: 1 } points)
            {
                throw Shape(block, "Points", row);
            }

            var copy = points.Values.ToArray();
            for (var i = 0; i < copy.Length; i++)
            {
                if (copy[i] >= vertexCount)
                {
                    throw IndexOutOfRange(block, $"Points[{strips.Count}]", i, copy[i], vertexCount,
                        ElementOffset(block, "Points", flat + i, 2));
                }
            }

            flat += copy.Length;
            strips.Add(copy);
        }

        return NifStripTriangulator.Triangulate(strips);
    }

    /// <summary>The stored Num Vertices of a data block.</summary>
    /// <exception cref="InvalidDataException">The integer did not decode.</exception>
    internal static int NumVertices(NifDecodedBlock block)
    {
        return checked((int)RequireInteger(block, "Num Vertices"));
    }

    private static List<NifFloatArrayValue> UvSets(NifDecodedBlock block, int vertexCount)
    {
        var sets = new List<NifFloatArrayValue>();
        if (!block.Root.TryGet("UV Sets", out var value))
        {
            return sets;
        }

        if (value is not NifArrayValue { IsRows: true } rows)
        {
            throw Shape(block, "UV Sets", value);
        }

        foreach (var row in rows.Items)
        {
            if (row is not NifFloatArrayValue { ComponentsPerElement: 2 } set || set.Count != vertexCount)
            {
                throw Shape(block, "UV Sets", row);
            }

            sets.Add(set);
        }

        return sets;
    }

    /// <summary>The block type the data block's Additional Data names, or null for a null or out-of-range link.</summary>
    internal static string? AdditionalDataType(NifModelReadState state, NifDecodedBlock block)
    {
        if (!block.Root.TryGet("Additional Data", out var value) || value is not NifRefValue { IsNone: false } link)
        {
            return null;
        }

        return (uint)link.Index < (uint)state.Blocks.Count ? state.Blocks[link.Index].Type : null;
    }

    /// <summary>The block index the data block's Additional Data names, or null for a null or out-of-range link.</summary>
    internal static int? AdditionalDataIndex(NifModelReadState state, NifDecodedBlock block)
    {
        if (!block.Root.TryGet("Additional Data", out var value) || value is not NifRefValue { IsNone: false } link)
        {
            return null;
        }

        return (uint)link.Index < (uint)state.Blocks.Count ? link.Index : null;
    }

    private static NifFloatArrayValue? OptionalFloats(NifDecodedBlock block, string field, int components,
        int vertexCount)
    {
        if (!block.Root.TryGet(field, out var value))
        {
            return null;
        }

        if (value is not NifFloatArrayValue array || array.ComponentsPerElement != components ||
            array.Count != vertexCount)
        {
            throw Shape(block, field, value);
        }

        return array;
    }

    private static long RequireInteger(NifDecodedBlock block, string field)
    {
        return block.Root.TryGet(field, out var value) && value is NifIntegerValue integer
            ? integer.Value
            : throw new InvalidDataException(
                $"NIF block {block.Index} ({block.Type}) did not decode the integer '{field}'.");
    }

    /// <summary>Throws for the first non-finite component; <paramref name="baseComponent" /> offsets row arrays.</summary>
    private static void RequireFinite(NifDecodedBlock block, NifFloatArrayValue values, string field,
        int baseComponent)
    {
        var index = FirstNonFinite(values);
        if (index < 0)
        {
            return;
        }

        var element = index / values.ComponentsPerElement;
        var bits = values.Bits[index];
        throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture,
            $"NIF block {block.Index} ({block.Type}), field '{field}' element {element} component " +
            $"{index % values.ComponentsPerElement} at offset 0x{ElementOffset(block, field, baseComponent + index, 4):X}: " +
            $"the value 0x{bits:X8} is not finite."));
    }

    /// <summary>A stored component, or 0 when it is NaN or infinite (the portable value of a non-finite coordinate).</summary>
    private static float Finite(float value)
    {
        return float.IsFinite(value) ? value : 0f;
    }

    /// <summary>The number of NaN or infinite components in the array.</summary>
    private static int CountNonFinite(NifFloatArrayValue values)
    {
        var count = 0;
        foreach (var bits in values.Bits)
        {
            if (!float.IsFinite(BitConverter.UInt32BitsToSingle(bits)))
            {
                count++;
            }
        }

        return count;
    }

    private static int FirstNonFinite(NifFloatArrayValue values)
    {
        var bits = values.Bits;
        for (var i = 0; i < bits.Length; i++)
        {
            if (!float.IsFinite(BitConverter.UInt32BitsToSingle(bits[i])))
            {
                return i;
            }
        }

        return -1;
    }

    private static (int NonFinite, int OutOfRange) ColorCensus(NifFloatArrayValue colors)
    {
        int nonFinite = 0, outOfRange = 0;
        foreach (var bits in colors.Bits)
        {
            var value = BitConverter.UInt32BitsToSingle(bits);
            if (!float.IsFinite(value))
            {
                nonFinite++;
            }
            else if (value is < 0f or > 1f)
            {
                outOfRange++;
            }
        }

        return (nonFinite, outOfRange);
    }

    private static byte[] LittleEndianBits(NifFloatArrayValue values)
    {
        var bits = values.Bits;
        var bytes = new byte[bits.Length * 4];
        for (var i = 0; i < bits.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), bits[i]);
        }

        return bytes;
    }

    /// <summary>The file offset of one component of an array field (its span start plus the component's position).</summary>
    private static long ElementOffset(NifDecodedBlock block, string field, int component, int componentSize)
    {
        foreach (var span in block.Spans)
        {
            if (string.Equals(span.Path, field, StringComparison.Ordinal))
            {
                return span.Offset + (long)component * componentSize;
            }
        }

        return block.Offset;
    }

    private static JsonNode? Field(NifStructValue root, string name)
    {
        return root.TryGet(name, out var value) ? NifModelNativeValues.ToJson(value) : null;
    }

    private static InvalidDataException Shape(NifDecodedBlock block, string field, NifValue value)
    {
        return new InvalidDataException(
            $"NIF block {block.Index} ({block.Type}) decoded '{field}' as {value}, not the expected array shape.");
    }

    private static InvalidDataException IndexOutOfRange(NifDecodedBlock block, string field, int element, int index,
        int vertexCount, long offset)
    {
        return new InvalidDataException(string.Create(CultureInfo.InvariantCulture,
            $"NIF block {block.Index} ({block.Type}), field '{field}' element {element} at offset 0x{offset:X}: " +
            $"vertex index {index} is not below Num Vertices ({vertexCount})."));
    }
}
