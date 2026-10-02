using System.Globalization;
using System.Text.Json.Nodes;
using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     The decoded channels of one BSPackedAdditionalGeometryData whose layout <see cref="NifPackedGeometryDecoder" />
///     recognized: every array is in the packed vertex order (index i is packed vertex i), element-major, with the
///     values exactly as the encoding gives them (halves widened exactly, floats copied, color bytes divided by 255,
///     the weight sentinel applied, bone index bytes reversed). Nothing is normalized, welded or reordered here.
/// </summary>
internal sealed class NifPackedGeometryStreams
{
    /// <summary>Creates the decoded channels.</summary>
    public NifPackedGeometryStreams(
        int blockIndex,
        NifPackedGeometryLayout layout,
        NifPackedPlatformSelection platform,
        int vertexCount,
        int payloadOffset,
        float[] positions,
        float[] normals,
        float[] texCoords,
        float[] bitangents,
        float[] tangents,
        float[]? colors,
        float[]? weights,
        byte[]? boneIndices,
        int tangentFrameNonFinite,
        int texCoordNonFinite,
        int weightNonFinite,
        int sentinelWeights,
        int slot3NonZero,
        int slot3Residual,
        int colorOrderSensitiveVertices,
        IReadOnlyList<NifPackedHalfCensus> fourthHalves,
        JsonObject structure)
    {
        BlockIndex = blockIndex;
        Layout = layout;
        Platform = platform;
        VertexCount = vertexCount;
        PayloadOffset = payloadOffset;
        Positions = positions;
        Normals = normals;
        TexCoords = texCoords;
        Bitangents = bitangents;
        Tangents = tangents;
        Colors = colors;
        Weights = weights;
        BoneIndices = boneIndices;
        TangentFrameNonFinite = tangentFrameNonFinite;
        TexCoordNonFinite = texCoordNonFinite;
        WeightNonFinite = weightNonFinite;
        SentinelWeights = sentinelWeights;
        Slot3NonZero = slot3NonZero;
        Slot3Residual = slot3Residual;
        ColorOrderSensitiveVertices = colorOrderSensitiveVertices;
        FourthHalves = fourthHalves;
        Structure = structure;
    }

    /// <summary>The packed block's index.</summary>
    public int BlockIndex { get; }

    /// <summary>The recognized layout.</summary>
    public NifPackedGeometryLayout Layout { get; }

    /// <summary>The platform the color byte order was decoded for.</summary>
    public NifPackedPlatformSelection Platform { get; }

    /// <summary>The packed block's Num Vertices (the number of packed vertices).</summary>
    public int VertexCount { get; }

    /// <summary>The absolute file offset of the first packed vertex.</summary>
    public int PayloadOffset { get; }

    /// <summary>Positions, three per vertex.</summary>
    public float[] Positions { get; }

    /// <summary>Normals, three per vertex.</summary>
    public float[] Normals { get; }

    /// <summary>UV set 0, two per vertex.</summary>
    public float[] TexCoords { get; }

    /// <summary>
    ///     Bitangents (the PC Bitangents array), three per vertex: the stream that runs along +dP/du, so the typed
    ///     tangent xyz (<see cref="NifModelGeometryData.TangentMappingRule" />).
    /// </summary>
    public float[] Bitangents { get; }

    /// <summary>
    ///     Tangents (the PC Tangents array), three per vertex: the stream that runs along +dP/dv, used only for the
    ///     handedness.
    /// </summary>
    public float[] Tangents { get; }

    /// <summary>Vertex colors as R, G, B, A in 0..1 (byte / 255), four per vertex; null for a layout without them.</summary>
    public float[]? Colors { get; }

    /// <summary>Bone weights for partition slots 0..3, the sentinel read as 0; null for a static layout.</summary>
    public float[]? Weights { get; }

    /// <summary>Bone indices for partition slots 0..3 (into the owning partition's bone list); null for a static layout.</summary>
    public byte[]? BoneIndices { get; }

    /// <summary>
    ///     The decoded slot-3 weight half of every packed vertex before the sentinel rule (a stored 1.0 sentinel is 1.0
    ///     here, where <see cref="Weights" /> reads it as 0), one per vertex; null for a static layout. The X360 engine
    ///     never reads it (<see cref="NifPackedEngineLanes" />), and the influences' facts count the vertices where it is
    ///     not the engine's derived weight.
    /// </summary>
    public float[]? StoredSlot3Weights { get; init; }

    /// <summary>Tangent or bitangent components that are NaN or infinite (the tangent basis is then not typed).</summary>
    public int TangentFrameNonFinite { get; }

    /// <summary>
    ///     UV components that are NaN or infinite; each reads as 0 in the portable coordinates while the channel's
    ///     exact bits go to a raw attribute, as for inline streams.
    /// </summary>
    public int TexCoordNonFinite { get; }

    /// <summary>Weight halves that are NaN or infinite (the skin is then not typed).</summary>
    public int WeightNonFinite { get; }

    /// <summary>Vertices whose slot-3 weight was the 1.0 sentinel and was read as 0.</summary>
    public int SentinelWeights { get; }

    /// <summary>
    ///     Vertices whose slot-3 weight is non-zero, not the sentinel and above the residual values; where PC uses four
    ///     slots this is the real fourth weight.
    /// </summary>
    public int Slot3NonZero { get; }

    /// <summary>
    ///     Vertices whose slot-3 weight is exactly 2^-24 or 2^-23, the values the 2026-09-24 measurement records on
    ///     vertices whose PC partition uses at most three slots (X360: 1,745 L3 + 125 L4); carried raw into the
    ///     influences, not zeroed; mechanism (the exporter's float32 residual rounded to half) supported, not proven.
    /// </summary>
    public int Slot3Residual { get; }

    /// <summary>
    ///     Vertices whose color bytes 1..3 are not all equal: the only vertices on which the X360 (A,R,G,B) and PS3
    ///     (A,G,B,R) byte orders decode to different colors. Zero for a layout without colors.
    /// </summary>
    public int ColorOrderSensitiveVertices { get; }

    /// <summary>The fourth-half census of every half4 channel, in layout order.</summary>
    public IReadOnlyList<NifPackedHalfCensus> FourthHalves { get; }

    /// <summary>The stream-table and data-block facts the decoder checked (owned; callers clone).</summary>
    public JsonObject Structure { get; }

    /// <summary>
    ///     The provenance of the decoded color byte order: Assumed when the platform was assumed (the file carries no
    ///     discriminator) or when the layout's color order is carried over rather than measured
    ///     (<see cref="NifPackedGeometryLayout.ColorByteOrderInferred" />, L6), else ReverseEngineered.
    /// </summary>
    public SceneValueProvenance ColorByteOrderProvenance => Platform.IsAssumed || Layout.ColorByteOrderInferred
        ? SceneValueProvenance.Assumed
        : SceneValueProvenance.ReverseEngineered;

    /// <summary>The evidence text of a carried-over color byte order, or null when the order was measured.</summary>
    public string? ColorByteOrderEvidence => Layout.Stream(NifPackedStreamKind.VertexColor)?.Inference;

    /// <summary>The native facts of the decoded block, built fresh on each call.</summary>
    public JsonObject CreateFacts()
    {
        var platform = new JsonObject
        {
            ["value"] = Platform.OptionValue,
            ["source"] = Platform.Source,
            ["assumed"] = Platform.IsAssumed,
            ["colorByteOrder"] = Layout.HasVertexColors ? Platform.ColorByteOrder : null,
            ["colorByteOrderProvenance"] = Layout.HasVertexColors ? ColorByteOrderProvenance.ToString() : null
        };
        if (ColorByteOrderEvidence is { } evidence)
        {
            platform["colorByteOrderEvidence"] = evidence;
        }

        if (Layout.HasVertexColors)
        {
            platform["colorOrderSensitiveVertices"] = ColorOrderSensitiveVertices;
        }

        var facts = new JsonObject
        {
            ["packedBlock"] = BlockIndex,
            ["layout"] = Layout.Id,
            ["layoutDescription"] = Layout.Description,
            ["layoutKey"] = Layout.Key,
            ["stride"] = Layout.Stride,
            ["packedVertices"] = VertexCount,
            ["byteOrder"] = "big-endian",
            ["layoutProvenance"] = "measured 2026-09-24 against the PC arrays of the same files " +
                                   "(TestOutput/packed-semantics-20260924)",
            ["platform"] = platform,
            ["streams"] = new JsonArray(Layout.Streams.Select(stream => (JsonNode?)new JsonObject
            {
                ["kind"] = stream.Kind.ToString(),
                ["type"] = stream.Type,
                ["unitSize"] = stream.UnitSize,
                ["offset"] = stream.Offset,
                ["encoding"] = stream.Encoding.ToString()
            }).ToArray()),
            ["fourthHalves"] = new JsonArray(FourthHalves.Select(census => (JsonNode?)census.ToJson()).ToArray()),
            ["structure"] = Structure.DeepClone()
        };
        if (Layout.IsSkinned)
        {
            facts["weights"] = new JsonObject
            {
                ["slots"] = 4,
                ["sentinelSlot3ReadAsZero"] = SentinelWeights,
                ["slot3NonZero"] = Slot3NonZero,
                ["slot3Residual"] = Slot3Residual,
                ["nonFiniteHalves"] = WeightNonFinite,
                ["rule"] = "four halves = partition slots 0..3, stored slot for slot; a slot-3 value of exactly 1.0 " +
                           "whose siblings sum to 1 within 2e-3 is a sentinel and reads as 0; no renormalization; " +
                           "slot-3 halves of 2^-24 / 2^-23 are the exporter's residual on <= 3-slot vertices (X360 " +
                           "1,745 L3 + 125 L4) and are counted separately, not zeroed; this is the decoded channel, " +
                           "whose slot 3 the X360 skin influences never read (the engine derives the fourth weight, " +
                           "NifPackedEngineLanes)",
                ["boneIndexRule"] = "slot k = byte[3 - k] of the big-endian uint32; indexes the owning partition's " +
                                    "bone list"
            };
        }

        if (TangentFrameNonFinite > 0)
        {
            facts["tangentFrameNonFiniteComponents"] = TangentFrameNonFinite;
        }

        if (TexCoordNonFinite > 0)
        {
            facts["texCoordNonFiniteComponents"] = TexCoordNonFinite;
        }

        facts["precision"] = string.Create(CultureInfo.InvariantCulture,
            $"half channels are exact widenings of the stored binary16 (one half ulp of the PC value, up to 8 above " +
            $"8,192); float3 channels are exact copies");
        return facts;
    }
}
