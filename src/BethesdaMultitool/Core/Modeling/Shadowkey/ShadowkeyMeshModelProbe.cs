using System.Buffers.Binary;
using System.Globalization;
using BethesdaMultitool.Core.Formats.Travels.Shadowkey;
using Slfx77.Multitool.Core.Models.Sources;

namespace BethesdaMultitool.Core.Modeling.Shadowkey;

/// <summary>
///     The bounded content probe behind <see cref="ShadowkeyMeshModelReader.Probe" /> (cut-2 plan section 5.1, decision
///     D11): a mesh record has no magic, so recognition is a structural walk applied only to bytes inside the 64 KiB
///     prefix, in stream order; the file name is never consulted.
/// </summary>
/// <remarks>
///     <para>
///         The walk: fewer than 14 bytes, a format tag other than 7, a trailer other than 1 or a coordinate count other
///         than 3 x vertices is NotAModel; so is a zero frame, vertex, UV or face count, and sections that cannot fit the
///         declared length (<c>14 + 6FV + 4U + 12F + 8</c>). Every face inside the prefix must index a vertex and a UV in
///         range. The texture header must declare at least one skin of sides 1 to <see cref="MaximumTextureSide" />; with a
///         declared length the bytes after the texels, less the sequence count, must be a positive multiple of 6. The
///         sequence table must hold at least one sequence, end exactly at the declared length, and give every sequence
///         <c>start &lt; end &lt;= frames</c> and a non-zero rate; a complete prefix must end exactly there.
///     </para>
///     <para>
///         When the prefix runs out first: a complete candidate is NotAModel (it is a truncated record); an incomplete
///         one is Supported and Tentative, and the read decides. A complete walk is Supported and Confirmed. The evidence
///         text is exactly the independent probe's (<c>probe_mesh</c> in <c>tools/scripts/gate2/shadowkey_probe.py</c>),
///         which the Bucket-B oracle compares on every cover row and decline control. Measured on the retail tree:
///         205 slots Confirmed, 21 Tentative (larger than 64 KiB), 11 NotAModel (empty); all 1,919 files of the
///         application directory NotAModel. The probe claims no NIF (ASCII header), no XnGine <c>.3D</c> (a <c>v2.x</c>
///         tag) and no Starfield <c>.mesh</c> (a first u16 of 0 to 2), since each fails the tag.
///     </para>
/// </remarks>
internal static class ShadowkeyMeshModelProbe
{
    /// <summary>The largest texture side the probe admits (retail sides are 8 to 128).</summary>
    public const int MaximumTextureSide = 1024;

    /// <summary>Probes one bounded candidate (see the type remarks).</summary>
    public static ModelProbeResult Probe(ModelSourceCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return Probe(candidate.Content, candidate.Length, candidate.IsComplete);
    }

    /// <summary>The walk over a prefix with its declared length and completeness (see the type remarks).</summary>
    public static ModelProbeResult Probe(ReadOnlySpan<byte> prefix, long? length, bool complete)
    {
        if (prefix.Length < ShadowkeyMesh.HeaderLength)
        {
            return ModelProbeResult.NotAModel;
        }

        var tag = BinaryPrimitives.ReadUInt16LittleEndian(prefix);
        long frames = BinaryPrimitives.ReadUInt16LittleEndian(prefix[2..]);
        long vertices = BinaryPrimitives.ReadUInt16LittleEndian(prefix[4..]);
        long uvs = BinaryPrimitives.ReadUInt16LittleEndian(prefix[6..]);
        long faces = BinaryPrimitives.ReadUInt16LittleEndian(prefix[8..]);
        long coordinates = BinaryPrimitives.ReadUInt16LittleEndian(prefix[10..]);
        var trailer = BinaryPrimitives.ReadUInt16LittleEndian(prefix[12..]);
        if (tag != ShadowkeyMesh.FormatTag || trailer != ShadowkeyMesh.HeaderTrailer || coordinates != 3 * vertices)
        {
            return ModelProbeResult.NotAModel;
        }

        if (frames == 0 || vertices == 0 || uvs == 0 || faces == 0)
        {
            return ModelProbeResult.NotAModel;
        }

        var head = ShadowkeyMesh.HeaderLength + 6 * frames * vertices + 4 * uvs + 12 * faces;
        if (length is { } declared && head + 8 > declared)
        {
            return ModelProbeResult.NotAModel;
        }

        var faceOffset = head - 12 * faces;
        for (long face = 0; face < faces; face++)
        {
            var at = faceOffset + 12 * face;
            if (at + 12 > prefix.Length)
            {
                break;
            }

            var corners = prefix.Slice((int)at, 12);
            var v0 = BinaryPrimitives.ReadUInt16LittleEndian(corners);
            var v1 = BinaryPrimitives.ReadUInt16LittleEndian(corners[2..]);
            var v2 = BinaryPrimitives.ReadUInt16LittleEndian(corners[4..]);
            var t0 = BinaryPrimitives.ReadUInt16LittleEndian(corners[6..]);
            var t1 = BinaryPrimitives.ReadUInt16LittleEndian(corners[8..]);
            var t2 = BinaryPrimitives.ReadUInt16LittleEndian(corners[10..]);
            if (Math.Max(v0, Math.Max(v1, v2)) >= vertices || Math.Max(t0, Math.Max(t1, t2)) >= uvs)
            {
                return ModelProbeResult.NotAModel;
            }
        }

        var evidence = string.Create(CultureInfo.InvariantCulture,
            $"Shadowkey mesh record: {frames} frames, {vertices} vertices, {faces} faces");
        if (head + 6 > prefix.Length)
        {
            return complete ? ModelProbeResult.NotAModel : Tentative(prefix, evidence + TextureHeaderNote);
        }

        long skins = BinaryPrimitives.ReadUInt16LittleEndian(prefix[(int)head..]);
        long width = BinaryPrimitives.ReadUInt16LittleEndian(prefix[((int)head + 2)..]);
        long height = BinaryPrimitives.ReadUInt16LittleEndian(prefix[((int)head + 4)..]);
        if (skins == 0 || width is < 1 or > MaximumTextureSide || height is < 1 or > MaximumTextureSide)
        {
            return ModelProbeResult.NotAModel;
        }

        var sequenceCountAt = head + 6 + 2 * skins * width * height;
        evidence += string.Create(CultureInfo.InvariantCulture, $", {skins} skin(s) {width}x{height}");
        if (length is { } total)
        {
            var rest = total - sequenceCountAt - 2;
            if (rest < 6 || rest % 6 != 0)
            {
                return ModelProbeResult.NotAModel;
            }
        }

        if (sequenceCountAt + 2 > prefix.Length)
        {
            return complete ? ModelProbeResult.NotAModel : Tentative(prefix, evidence + SequenceCountNote);
        }

        long sequenceCount = BinaryPrimitives.ReadUInt16LittleEndian(prefix[(int)sequenceCountAt..]);
        var end = sequenceCountAt + 2 + 6 * sequenceCount;
        if (sequenceCount == 0 || (length is { } expected && end != expected))
        {
            return ModelProbeResult.NotAModel;
        }

        if (end > prefix.Length)
        {
            return complete ? ModelProbeResult.NotAModel : Tentative(prefix, evidence + SequenceTableNote);
        }

        for (long index = 0; index < sequenceCount; index++)
        {
            var at = (int)(sequenceCountAt + 2 + 6 * index);
            var start = BinaryPrimitives.ReadUInt16LittleEndian(prefix[at..]);
            var stop = BinaryPrimitives.ReadUInt16LittleEndian(prefix[(at + 2)..]);
            var rate = BinaryPrimitives.ReadUInt16LittleEndian(prefix[(at + 4)..]);
            if (!(start < stop && stop <= frames) || rate == 0)
            {
                return ModelProbeResult.NotAModel;
            }
        }

        if (complete && end != prefix.Length)
        {
            return ModelProbeResult.NotAModel;
        }

        if (!complete)
        {
            return Tentative(prefix, evidence + IncompleteNote);
        }

        return new ModelProbeResult(ModelProbeKind.Supported, ModelProbeConfidence.Confirmed,
            new ModelProbeEvidence(0, (int)end,
                evidence + string.Create(CultureInfo.InvariantCulture, $", {sequenceCount} sequence(s)")));
    }

    /// <summary>The note a Tentative evidence carries when the prefix ends before the texture header.</summary>
    public const string TextureHeaderNote = " (prefix ends before the texture header)";

    /// <summary>The note a Tentative evidence carries when the prefix ends before the sequence count.</summary>
    public const string SequenceCountNote = " (prefix ends before the sequence table)";

    /// <summary>The note a Tentative evidence carries when the prefix ends inside the sequence table.</summary>
    public const string SequenceTableNote = " (prefix ends in the sequence table)";

    /// <summary>The note a Tentative evidence carries when the whole record is inside an incomplete prefix.</summary>
    public const string IncompleteNote = " (prefix is not the whole entry)";

    private static ModelProbeResult Tentative(ReadOnlySpan<byte> prefix, string evidence)
    {
        return new ModelProbeResult(ModelProbeKind.Supported, ModelProbeConfidence.Tentative,
            new ModelProbeEvidence(0, Math.Min(prefix.Length, ModelSourceCandidate.MaximumProbeBytes), evidence));
    }
}
