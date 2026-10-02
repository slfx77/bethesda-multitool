using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Slfx77.Multitool.Core.Models.Sources;

namespace BethesdaMultitool.Core.Modeling.Starfield;

/// <summary>
///     The bounded content probe behind <see cref="StarfieldMeshModelReader.Probe" /> (cut-2 plan section 4.1, decision
///     D7). A <c>.mesh</c> has no magic, so recognition is a structural walk over the 64 KiB prefix; each check applies
///     only to bytes inside the prefix, in stream order, and the file extension is never consulted.
/// </summary>
/// <remarks>
///     <para>
///         The walk: fewer than 8 bytes or a version above 2 is NotAModel; the index count must be a multiple of 3 and
///         <c>8 + 2 x count + </c><see cref="MinimumBytesAfterIndices" /> (the smallest tail-less layout;
///         <see cref="MinimumBytesAfterIndicesVersion0" /> for version 0) must not exceed the declared length; the maximum
///         of the indices inside the prefix is kept; the scale must be finite and positive, <c>weightsPerVertex</c> at most <see cref="MaximumWeightsPerVertex" />,
///         the vertex count 1 to <see cref="MaximumVertices" /> and above the kept maximum index, and <c>6 x vertices</c>
///         must fit the declared length; each of UV0, UV1, colors, normals and tangents counts 0 or the vertex count (the
///         first normal's W is read when inside the prefix); the weight count is vertices x <c>weightsPerVertex</c>; for
///         version 1 and 2 at most <see cref="MaximumLods" /> LOD lists, each a multiple of 3 with every index inside the
///         prefix below the vertex count. A complete stream that ends here has no meshlet tail: Supported and Confirmed
///         when the first normal's W is 0, Unsupported and Tentative (truncated) when it is 1, because on every retail
///         file normal W is 1 exactly when the tail follows. Otherwise the cull count must equal the meshlet count and a
///         complete stream must end exactly after the cull records (Supported, Confirmed); bytes after them are
///         NotAModel.
///     </para>
///     <para>
///         When the prefix runs out: a complete stream is NotAModel before the vertex count validated and Unsupported
///         (truncated) after; an incomplete prefix is Supported and Tentative, and the read decides. The mirror of this
///         rule is <c>probe()</c> in <c>tools/scripts/gate2/starfield_mesh_probe.py</c>, run with the bounds 8 and 8;
///         the Bucket-B oracle compares the two on every cover file and decline control. Over the retail census every
///         <c>.mesh</c> is Supported and all 180,350 non-mesh controls are NotAModel (plan section 0.2); the probe claims no
///         NIF (an ASCII header, so a first dword far above 2) and no XnGine <c>.3D</c> (a <c>v2.x</c> tag).
///     </para>
///     <para>
///         Completeness is the candidate's <see cref="ModelSourceCandidate.IsComplete" /> alone, as Shared's helper sets
///         it (EOF seen within the budget); the declared length bounds the index and vertex counts only.
///     </para>
/// </remarks>
internal static class StarfieldMeshModelProbe
{
    /// <summary>The most weights per vertex the probe accepts (the retail maximum is 8).</summary>
    public const int MaximumWeightsPerVertex = 8;

    /// <summary>The most LOD lists the probe accepts (the retail maximum is 3).</summary>
    public const int MaximumLods = 8;

    /// <summary>The most vertices a 16-bit index list can address.</summary>
    public const int MaximumVertices = 65536;

    /// <summary>The highest container version.</summary>
    public const uint MaximumVersion = 2;

    /// <summary>
    ///     The bytes the smallest valid version 1 or 2 stream holds after its index list: the scale, weightsPerVertex and
    ///     vertex count dwords, one 6-byte vertex, the five stream counts, the weight count and the LOD count, with no
    ///     meshlet tail (ten dwords and one vertex). A stream with the tail is 8 bytes longer.
    /// </summary>
    public const int MinimumBytesAfterIndices = 46;

    /// <summary>The same bound for version 0, which has no LOD count (nine dwords and one vertex).</summary>
    public const int MinimumBytesAfterIndicesVersion0 = 42;

    /// <summary>The reason an Unsupported result carries for a stream that ends inside the layout.</summary>
    public const string TruncatedReason =
        "Starfield .mesh truncated: the stream ends inside the layout after the vertex count validated";

    /// <summary>The reason an Unsupported result carries for a stream cut at the meshlet tail boundary.</summary>
    public const string TailBoundaryReason =
        "Starfield .mesh truncated: the stream ends where the meshlet tail starts while the first normal's W is 1, " +
        "which on every retail file means the meshlet and cull tail follows";

    /// <summary>The evidence fragment of a stream without the meshlet tail.</summary>
    public const string NoTailNote = "no meshlet tail";

    /// <summary>The evidence fragment of a stream with the meshlet tail.</summary>
    public const string TailNote = "meshlet tail";

    /// <summary>The note a Tentative evidence carries when the stream extends past the probe prefix.</summary>
    public const string IncompleteNote = "the stream extends past the 64 KiB probe prefix";

    /// <summary>Probes one bounded candidate (see the type remarks).</summary>
    public static ModelProbeResult Probe(ModelSourceCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var walk = new Walk(candidate.Content, candidate.IsComplete, candidate.Length);
        return walk.Run();
    }

    /// <summary>How far the walk validated, which decides a complete stream's class when the prefix runs out.</summary>
    private enum Stage
    {
        /// <summary>The index count or the indices were being read.</summary>
        BeforeIndices,

        /// <summary>The indices fit; the scale, weights per vertex or vertex count were being read.</summary>
        BeforeVertexCount,

        /// <summary>The vertex count validated.</summary>
        AfterVertexCount
    }

    /// <summary>The walk's state over one prefix.</summary>
    private ref struct Walk
    {
        private readonly ReadOnlySpan<byte> _content;
        private readonly bool _complete;
        private readonly long? _declared;
        private int _pos;
        private Stage _stage;
        private uint _version;
        private uint _indexCount;
        private uint _vertices;
        private uint? _normalW;

        public Walk(ReadOnlySpan<byte> content, bool complete, long? declared)
        {
            _content = content;
            _complete = complete;
            _declared = declared;
            _pos = 0;
            _stage = Stage.BeforeIndices;
            _version = 0;
            _indexCount = 0;
            _vertices = 0;
            _normalW = null;
        }

        public ModelProbeResult Run()
        {
            if (_content.Length < 8)
            {
                return ModelProbeResult.NotAModel;
            }

            _version = ReadU32();
            if (_version > MaximumVersion)
            {
                return ModelProbeResult.NotAModel;
            }

            _indexCount = ReadU32();
            var after = _version == 0 ? MinimumBytesAfterIndicesVersion0 : MinimumBytesAfterIndices;
            if (_indexCount % 3 != 0 || (_declared is { } declared && 8 + 2L * _indexCount + after > declared))
            {
                return ModelProbeResult.NotAModel;
            }

            var available = (_content.Length - _pos) / 2;
            var inPrefix = (int)Math.Min(_indexCount, (uint)available);
            var indexMax = MaximumIndex(_pos, inPrefix);
            if (available < _indexCount)
            {
                return OutOfPrefix();
            }

            _pos += (int)(2 * _indexCount);
            _stage = Stage.BeforeVertexCount;
            if (!TryReadU32(out var scaleBits))
            {
                return OutOfPrefix();
            }

            var scale = BitConverter.UInt32BitsToSingle(scaleBits);
            if (!float.IsFinite(scale) || scale <= 0f)
            {
                return ModelProbeResult.NotAModel;
            }

            if (!TryReadU32(out var weightsPerVertex))
            {
                return OutOfPrefix();
            }

            if (weightsPerVertex > MaximumWeightsPerVertex)
            {
                return ModelProbeResult.NotAModel;
            }

            if (!TryReadU32(out var vertices))
            {
                return OutOfPrefix();
            }

            _vertices = vertices;

            if (_vertices == 0 || _vertices > MaximumVertices || indexMax >= _vertices ||
                (_declared is { } length && _pos + 6L * _vertices > length))
            {
                return ModelProbeResult.NotAModel;
            }

            _stage = Stage.AfterVertexCount;
            if (!TrySkip(6L * _vertices))
            {
                return OutOfPrefix();
            }

            for (var stream = 0; stream < 5; stream++)
            {
                if (!TryReadU32(out var count))
                {
                    return OutOfPrefix();
                }

                if (count != 0 && count != _vertices)
                {
                    return ModelProbeResult.NotAModel;
                }

                // Stream 3 is the normals: the first one's 2-bit W says whether the meshlet tail follows.
                if (stream == 3 && count != 0 && _pos + 4 <= _content.Length)
                {
                    _normalW = BinaryPrimitives.ReadUInt32LittleEndian(_content[_pos..]) >> 30;
                }

                if (!TrySkip(4L * count))
                {
                    return OutOfPrefix();
                }
            }

            if (!TryReadU32(out var weightCount))
            {
                return OutOfPrefix();
            }

            if (weightCount != (long)_vertices * weightsPerVertex)
            {
                return ModelProbeResult.NotAModel;
            }

            if (!TrySkip(4L * weightCount))
            {
                return OutOfPrefix();
            }

            if (_version != 0)
            {
                if (!TryReadU32(out var lods))
                {
                    return OutOfPrefix();
                }

                if (lods > MaximumLods)
                {
                    return ModelProbeResult.NotAModel;
                }

                for (var lod = 0u; lod < lods; lod++)
                {
                    if (!TryReadU32(out var lodCount))
                    {
                        return OutOfPrefix();
                    }

                    if (lodCount % 3 != 0)
                    {
                        return ModelProbeResult.NotAModel;
                    }

                    var have = (_content.Length - _pos) / 2;
                    if (MaximumIndex(_pos, (int)Math.Min(lodCount, (uint)have)) >= _vertices)
                    {
                        return ModelProbeResult.NotAModel;
                    }

                    if (!TrySkip(2L * lodCount))
                    {
                        return OutOfPrefix();
                    }
                }
            }

            if (_complete && _pos == _content.Length)
            {
                return _normalW == 1
                    ? new ModelProbeResult(ModelProbeKind.Unsupported, ModelProbeConfidence.Tentative,
                        Evidence(_pos, null), TailBoundaryReason)
                    : new ModelProbeResult(ModelProbeKind.Supported, ModelProbeConfidence.Confirmed,
                        Evidence(_pos, NoTailNote));
            }

            if (!TryReadU32(out var meshlets))
            {
                return OutOfPrefix();
            }

            if (!TrySkip(16L * meshlets))
            {
                return OutOfPrefix();
            }

            if (!TryReadU32(out var cull))
            {
                return OutOfPrefix();
            }

            if (cull != meshlets)
            {
                return ModelProbeResult.NotAModel;
            }

            if (!TrySkip(24L * cull))
            {
                return OutOfPrefix();
            }

            if (!_complete)
            {
                return new ModelProbeResult(ModelProbeKind.Supported, ModelProbeConfidence.Tentative,
                    Evidence(_pos, IncompleteNote));
            }

            return _pos == _content.Length
                ? new ModelProbeResult(ModelProbeKind.Supported, ModelProbeConfidence.Confirmed,
                    Evidence(_pos, TailNote))
                : ModelProbeResult.NotAModel;
        }

        /// <summary>The verdict when the prefix runs out (see the type remarks).</summary>
        private readonly ModelProbeResult OutOfPrefix()
        {
            var examined = _content.Length;
            if (!_complete)
            {
                return new ModelProbeResult(ModelProbeKind.Supported, ModelProbeConfidence.Tentative,
                    Evidence(examined, IncompleteNote));
            }

            return _stage == Stage.AfterVertexCount
                ? new ModelProbeResult(ModelProbeKind.Unsupported, ModelProbeConfidence.Tentative,
                    Evidence(examined, null), TruncatedReason)
                : ModelProbeResult.NotAModel;
        }

        /// <summary>The evidence for the first <paramref name="examined" /> bytes, with the facts the walk reached.</summary>
        private readonly ModelProbeEvidence Evidence(int examined, string? note)
        {
            var text = new StringBuilder();
            text.Append(CultureInfo.InvariantCulture, $"Starfield .mesh v{_version}");
            if (_stage == Stage.AfterVertexCount)
            {
                text.Append(CultureInfo.InvariantCulture, $", {_vertices} vertices");
            }

            text.Append(CultureInfo.InvariantCulture, $", {_indexCount} indices");
            if (note is not null)
            {
                text.Append(", ").Append(note);
            }

            return new ModelProbeEvidence(0, Math.Max(examined, 8), text.ToString());
        }

        /// <summary>The largest of <paramref name="count" /> u16 values at <paramref name="at" /> (-1 when none).</summary>
        private readonly int MaximumIndex(int at, int count)
        {
            var max = -1;
            for (var i = 0; i < count; i++)
            {
                max = Math.Max(max, BinaryPrimitives.ReadUInt16LittleEndian(_content[(at + i * 2)..]));
            }

            return max;
        }

        private uint ReadU32()
        {
            var value = BinaryPrimitives.ReadUInt32LittleEndian(_content[_pos..]);
            _pos += 4;
            return value;
        }

        private bool TryReadU32(out uint value)
        {
            if (_pos + 4 > _content.Length)
            {
                _pos = _content.Length;
                value = 0;
                return false;
            }

            value = ReadU32();
            return true;
        }

        private bool TrySkip(long bytes)
        {
            if (_pos + bytes > _content.Length)
            {
                _pos = _content.Length;
                return false;
            }

            _pos += (int)bytes;
            return true;
        }
    }
}
