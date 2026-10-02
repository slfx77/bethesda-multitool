using System.Globalization;
using System.Text;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     The six BSPackedAdditionalGeometryData layouts FNV ships on the X360 and PS3 (20.2.0.7, BS 34, big-endian), as
///     data: an id, the vertex stride and the ordered channels with their (Type, Unit Size) pair, byte offset, semantic
///     and encoding. Measured 2026-09-24 over the retail console Meshes archives (TestOutput/packed-layout-census-20260924:
///     exactly these six ordered (Type, Unit Size) lists on both consoles) and settled stream by stream against the PC
///     arrays of the same files (TestOutput/packed-semantics-20260924, the authoritative table; where plan section 3's
///     "Packed-stream layout table" disagreed, the measurement wins: the fourth half of a half4 stream is a constant 1.0
///     and not the bitangent sign, and a slot-3 weight of exactly 1.0 beside three weights summing to 1 is a sentinel
///     read as 0, not "kept as authored").
/// </summary>
/// <remarks>
///     <para>
///         A file's layout is recognized by <see cref="Match" /> from its ordered (Type, Unit Size, Block Offset) list and
///         its stride; anything else is unknown and stays native state (<see cref="NifModelCoverage.PackedLayoutUnknownReason" />).
///         Nothing is inferred from a channel's vector length. Type 16 is four halves (8 bytes), 14 two halves (4), 28
///         four bytes (4) and 3 three floats (12).
///     </para>
///     <para>
///         The measured shader index of each layout (126, 124, 253, 255, 52, 54) is recorded as a fact and never used as
///         a key.
///     </para>
///     <para>
///         One rule is carried over rather than measured: L6's vertex color byte order (<see cref="L6ColorInference" />).
///         Every retail L6 vertex is white opaque, so no byte order is separable there; the order is L1's and L4's, the
///         same stream type in the same file family. <see cref="ColorByteOrderInferred" /> says so, and the decoded
///         color's provenance is then Assumed even under a declared platform.
///     </para>
/// </remarks>
internal sealed class NifPackedGeometryLayout
{
    /// <summary>The evidence text of L6's carried-over color byte order (measurement open question 2).</summary>
    public const string L6ColorInference =
        "byte order carried over from L1/L4 (same stream type, same file family); every retail L6 vertex is " +
        "0xFFFFFFFF, so no order is separable (TestOutput/packed-semantics-20260924, open question 2)";

    private const uint Half4Type = 16;
    private const uint Half2Type = 14;
    private const uint FourByteType = 28;
    private const uint Float3Type = 3;

    private NifPackedGeometryLayout(string id, string description, int stride, int measuredShaderIndex,
        IReadOnlyList<NifPackedStream> streams)
    {
        Id = id;
        Description = description;
        Stride = stride;
        MeasuredShaderIndex = measuredShaderIndex;
        Streams = streams;
    }

    /// <summary>The six measured layouts, L1 to L6.</summary>
    public static IReadOnlyList<NifPackedGeometryLayout> Known { get; } =
    [
        new NifPackedGeometryLayout("L1", "static with vertex colors", 40, 126,
        [
            HalfVector3(NifPackedStreamKind.Position, 0),
            HalfVector3(NifPackedStreamKind.Normal, 8),
            Color(16),
            TexCoord(20),
            HalfVector3(NifPackedStreamKind.Bitangent, 24),
            HalfVector3(NifPackedStreamKind.Tangent, 32)
        ]),
        new NifPackedGeometryLayout("L2", "static", 36, 124,
        [
            HalfVector3(NifPackedStreamKind.Position, 0),
            HalfVector3(NifPackedStreamKind.Normal, 8),
            TexCoord(16),
            HalfVector3(NifPackedStreamKind.Bitangent, 20),
            HalfVector3(NifPackedStreamKind.Tangent, 28)
        ]),
        new NifPackedGeometryLayout("L3", "skinned", 48, 253,
        [
            HalfVector3(NifPackedStreamKind.Position, 0),
            Weights(8),
            BoneIndices(16),
            HalfVector3(NifPackedStreamKind.Normal, 20),
            TexCoord(28),
            HalfVector3(NifPackedStreamKind.Bitangent, 32),
            HalfVector3(NifPackedStreamKind.Tangent, 40)
        ]),
        new NifPackedGeometryLayout("L4", "skinned with vertex colors", 52, 255,
        [
            HalfVector3(NifPackedStreamKind.Position, 0),
            Weights(8),
            BoneIndices(16),
            HalfVector3(NifPackedStreamKind.Normal, 20),
            Color(28),
            TexCoord(32),
            HalfVector3(NifPackedStreamKind.Bitangent, 36),
            HalfVector3(NifPackedStreamKind.Tangent, 44)
        ]),
        new NifPackedGeometryLayout("L5", "interface (float normals and tangent frame)", 48, 52,
        [
            HalfVector3(NifPackedStreamKind.Position, 0),
            FloatVector3(NifPackedStreamKind.Normal, 8),
            TexCoord(20),
            FloatVector3(NifPackedStreamKind.Bitangent, 24),
            FloatVector3(NifPackedStreamKind.Tangent, 36)
        ]),
        new NifPackedGeometryLayout("L6", "interface with vertex colors", 52, 54,
        [
            HalfVector3(NifPackedStreamKind.Position, 0),
            FloatVector3(NifPackedStreamKind.Normal, 8),
            Color(20, L6ColorInference),
            TexCoord(24),
            FloatVector3(NifPackedStreamKind.Bitangent, 28),
            FloatVector3(NifPackedStreamKind.Tangent, 40)
        ])
    ];

    /// <summary>The layout id, L1 to L6.</summary>
    public string Id { get; }

    /// <summary>A short description of what the layout serves.</summary>
    public string Description { get; }

    /// <summary>The bytes per vertex (every channel's Stride, and the data block's Total Size).</summary>
    public int Stride { get; }

    /// <summary>The Shader Index every retail block of this layout carries (a recorded fact, not a key).</summary>
    public int MeasuredShaderIndex { get; }

    /// <summary>The channels in stored order.</summary>
    public IReadOnlyList<NifPackedStream> Streams { get; }

    /// <summary>True when the layout carries bone weights and indices (L3, L4).</summary>
    public bool IsSkinned => Stream(NifPackedStreamKind.BoneWeights) is not null;

    /// <summary>True when the layout carries a vertex color channel (L1, L4, L6).</summary>
    public bool HasVertexColors => Stream(NifPackedStreamKind.VertexColor) is not null;

    /// <summary>
    ///     True when the color channel's byte order was carried over from another layout instead of measured on this
    ///     one (L6 only; false for L1 and L4, whose orders were measured on rows with R != B, and for every layout
    ///     without colors).
    /// </summary>
    public bool ColorByteOrderInferred => Stream(NifPackedStreamKind.VertexColor)?.Inference is not null;

    /// <summary>
    ///     The layout key as text: the ordered (Type, Unit Size) pairs and the stride, for example
    ///     <c>(16,8)(16,8)(28,4)(14,4)(16,8)(16,8) stride 40</c>.
    /// </summary>
    public string Key => KeyOf(Streams.Select(s => (s.Type, s.UnitSize, (uint)s.Offset)).ToList(), (uint)Stride);

    /// <summary>
    ///     The layout whose ordered channels declare exactly these (Type, Unit Size, Block Offset) triples and whose
    ///     stride equals <paramref name="stride" />, or null when the file's table matches none of the six.
    /// </summary>
    public static NifPackedGeometryLayout? Match(IReadOnlyList<(uint Type, uint UnitSize, uint Offset)> streams,
        uint stride)
    {
        ArgumentNullException.ThrowIfNull(streams);
        foreach (var layout in Known)
        {
            if (layout.Matches(streams, stride))
            {
                return layout;
            }
        }

        return null;
    }

    /// <summary>The key text of a file's stream table, in the same spelling as <see cref="Key" />.</summary>
    public static string KeyOf(IReadOnlyList<(uint Type, uint UnitSize, uint Offset)> streams, uint stride)
    {
        ArgumentNullException.ThrowIfNull(streams);
        var text = new StringBuilder();
        foreach (var (type, unitSize, _) in streams)
        {
            text.Append(CultureInfo.InvariantCulture, $"({type},{unitSize})");
        }

        text.Append(CultureInfo.InvariantCulture, $" stride {stride}");
        return text.ToString();
    }

    /// <summary>The channel carrying <paramref name="kind" />, or null when the layout has none.</summary>
    public NifPackedStream? Stream(NifPackedStreamKind kind)
    {
        foreach (var stream in Streams)
        {
            if (stream.Kind == kind)
            {
                return stream;
            }
        }

        return null;
    }

    private bool Matches(IReadOnlyList<(uint Type, uint UnitSize, uint Offset)> streams, uint stride)
    {
        if (stride != (uint)Stride || streams.Count != Streams.Count)
        {
            return false;
        }

        for (var i = 0; i < streams.Count; i++)
        {
            var expected = Streams[i];
            var (type, unitSize, offset) = streams[i];
            if (type != expected.Type || unitSize != expected.UnitSize || offset != (uint)expected.Offset)
            {
                return false;
            }
        }

        return true;
    }

    private static NifPackedStream HalfVector3(NifPackedStreamKind kind, int offset)
    {
        return new NifPackedStream(kind, Half4Type, 8, offset, NifPackedStreamEncoding.HalfVector3);
    }

    private static NifPackedStream FloatVector3(NifPackedStreamKind kind, int offset)
    {
        return new NifPackedStream(kind, Float3Type, 12, offset, NifPackedStreamEncoding.FloatVector3);
    }

    private static NifPackedStream TexCoord(int offset)
    {
        return new NifPackedStream(NifPackedStreamKind.TexCoord, Half2Type, 4, offset,
            NifPackedStreamEncoding.HalfVector2);
    }

    private static NifPackedStream Color(int offset, string? inference = null)
    {
        return new NifPackedStream(NifPackedStreamKind.VertexColor, FourByteType, 4, offset,
            NifPackedStreamEncoding.D3DColor, inference);
    }

    private static NifPackedStream Weights(int offset)
    {
        return new NifPackedStream(NifPackedStreamKind.BoneWeights, Half4Type, 8, offset,
            NifPackedStreamEncoding.HalfWeights4);
    }

    private static NifPackedStream BoneIndices(int offset)
    {
        return new NifPackedStream(NifPackedStreamKind.BoneIndices, FourByteType, 4, offset,
            NifPackedStreamEncoding.UByte4Reversed);
    }
}
