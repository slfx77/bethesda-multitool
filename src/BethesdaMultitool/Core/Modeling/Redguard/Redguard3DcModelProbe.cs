using System.Buffers.Binary;
using BethesdaMultitool.Core.Modeling.Xngine;
using Slfx77.Multitool.Core.Models.Sources;

namespace BethesdaMultitool.Core.Modeling.Redguard;

/// <summary>
///     The bounded content probe behind <see cref="Redguard3DcModelReader.Probe" /> (cut-1c plan section 6.1): a
///     Redguard <c>.3DC</c> frame stack is recognized by content alone, and exactly where the <c>.3D</c> probe
///     (<see cref="XnGineModelProbe" />) answers NotAModel, so exactly one reader recognizes each file. The file extension
///     is never consulted.
/// </summary>
/// <remarks>
///     <list type="bullet">
///         <item>
///             NotAModel unless the record has a mesh tag (<see cref="XnGineContentFacts.MeshTags" />) and satisfies the
///             <c>.3DC</c> shape test (<see cref="XnGineModelProbe.SatisfiesAnimatedShape" />, the same test the
///             <c>.3D</c> probe declines on; 147 of 147 retail <c>.3DC</c> files and no static mesh satisfy it), and
///             unless its plane list walks with 8-byte plane headers whose corners address the header's points. A 3dfx
///             tag is NotAModel here because the <c>.3D</c> probe already declines it as Unsupported.
///         </item>
///         <item>
///             Unsupported for a <c>v2.5</c> tag (<see cref="Redguard3DcModelFormatMetadata.V25UnsupportedReason" />),
///             decided once the mesh tag and the shape test hold and BEFORE the plane walk (slice-6 review finding 1): a
///             v2.5 record stores its corners untripled, as point index x 4, which the v2.6/v2.7 byte-offset walk
///             refuses from the second corner on, so walking first left a genuine v2.5 stack recognized by no reader.
///         </item>
///         <item>
///             Supported and Confirmed when the candidate is complete (the helper saw EOF inside the 64 KiB budget) and
///             its blocks tile the file exactly (<see cref="Redguard3DcModelReader.TryParseStack" />): 26 retail files.
///             Tentative when the candidate is not complete (121 retail files; every retail plane list ends by byte
///             32,848, inside the prefix, slice-6 receipt <c>measure_slice6.json</c>), and Tentative with the failure in
///             the evidence when a complete file declares a block outside itself
///             (<see cref="Redguard3DcModelReader.DeclaredSizeFailure" />, checked before the parse, slice-6 review
///             finding 4) or does not tile, so the read reports why it refuses it.
///         </item>
///     </list>
///     The evidence is the variant with the header counts (<see cref="Redguard3DcModelFormatMetadata.Describe" />).
/// </remarks>
internal static class Redguard3DcModelProbe
{
    /// <summary>The note a Tentative evidence carries when the file extends past the probe prefix.</summary>
    public const string IncompleteNote = "; the file extends past the 64 KiB probe prefix";

    /// <summary>
    ///     The note prefix a Tentative evidence carries when a complete file does not tile, a declared block outside the
    ///     file included.
    /// </summary>
    public const string NotTilingNote = "; the complete file does not tile, so the read refuses it: ";

    /// <summary>How a plane walk over a probe prefix ended.</summary>
    private enum Walk
    {
        /// <summary>Every plane header and corner fits and every corner addresses a point.</summary>
        Walks,

        /// <summary>A corner addresses no point, or the walk runs past the end of a complete record.</summary>
        Fails,

        /// <summary>The walk ran past the end of an incomplete prefix.</summary>
        Incomplete
    }

    /// <summary>Probes one bounded candidate (see the type remarks).</summary>
    public static ModelProbeResult Probe(ModelSourceCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var content = candidate.Content;
        if (content.Length < XnGineContentFacts.HeaderLength)
        {
            return ModelProbeResult.NotAModel;
        }

        var tag = XnGineModelProbe.Tag(content);
        if (!XnGineContentFacts.MeshTags.Contains(tag, StringComparer.Ordinal) ||
            !XnGineModelProbe.SatisfiesAnimatedShape(content))
        {
            return ModelProbeResult.NotAModel;
        }

        var points = BinaryPrimitives.ReadInt32LittleEndian(content[4..]);
        var planes = BinaryPrimitives.ReadInt32LittleEndian(content[8..]);
        var frames = BinaryPrimitives.ReadInt32LittleEndian(content[16..]);
        var table = BinaryPrimitives.ReadInt32LittleEndian(content[BinaryPrimitives.ReadInt32LittleEndian(content[20..])..]);
        var planeList = BinaryPrimitives.ReadInt32LittleEndian(content[60..]);
        var recordDwords = (int)(((long)planeList - table) / (4L * frames));
        var description = Redguard3DcModelFormatMetadata.Describe(tag, recordDwords, points, planes, frames);

        // The tag is decided before the walk: a v2.5 record's corners are point index x 4, which the walk refuses.
        if (!Redguard3DcModelFormatMetadata.Tags.Contains(tag, StringComparer.Ordinal))
        {
            return new ModelProbeResult(ModelProbeKind.Unsupported, ModelProbeConfidence.Tentative,
                new ModelProbeEvidence(0, content.Length, description),
                Redguard3DcModelFormatMetadata.V25UnsupportedReason);
        }

        var walk = WalkPlanes(content, candidate.IsComplete);
        if (walk == Walk.Fails)
        {
            return ModelProbeResult.NotAModel;
        }

        if (!candidate.IsComplete)
        {
            return new ModelProbeResult(ModelProbeKind.Supported, ModelProbeConfidence.Tentative,
                new ModelProbeEvidence(0, content.Length, description + IncompleteNote));
        }

        if (Redguard3DcModelReader.DeclaredSizeFailure(content) is { } failure)
        {
            return new ModelProbeResult(ModelProbeKind.Supported, ModelProbeConfidence.Tentative,
                new ModelProbeEvidence(0, content.Length, description + NotTilingNote + failure));
        }

        return Redguard3DcModelReader.TryParseStack(content.ToArray(), "probe", out _, out var error)
            ? new ModelProbeResult(ModelProbeKind.Supported, ModelProbeConfidence.Confirmed,
                new ModelProbeEvidence(0, content.Length, description))
            : new ModelProbeResult(ModelProbeKind.Supported, ModelProbeConfidence.Tentative,
                new ModelProbeEvidence(0, content.Length, description + NotTilingNote + error));
    }

    /// <summary>
    ///     The plane walk with 8-byte plane headers from header +60: every header and corner fits, and every corner's
    ///     offset addresses one of the header's points (a multiple of 12 below the point count; v2.6 and v2.7 store
    ///     byte offsets). Running past the bytes fails a complete record and leaves an incomplete prefix undecided.
    /// </summary>
    private static Walk WalkPlanes(ReadOnlySpan<byte> bytes, bool isComplete)
    {
        const int planeHeaderLength = XnGineContentFacts.DaggerfallPlaneHeaderLength;
        var pastEnd = isComplete ? Walk.Fails : Walk.Incomplete;
        var pointCount = BinaryPrimitives.ReadInt32LittleEndian(bytes[4..]);
        var planeCount = BinaryPrimitives.ReadInt32LittleEndian(bytes[8..]);
        long position = BinaryPrimitives.ReadInt32LittleEndian(bytes[60..]);
        if (pointCount <= 0 || planeCount <= 0 || position < 0)
        {
            return Walk.Fails;
        }

        for (var k = 0; k < planeCount; k++)
        {
            if (position + planeHeaderLength > bytes.Length)
            {
                return pastEnd;
            }

            int corners = bytes[(int)position];
            position += planeHeaderLength;
            for (var q = 0; q < corners; q++)
            {
                if (position + XnGineContentFacts.PlanePointLength > bytes.Length)
                {
                    return pastEnd;
                }

                var offset = BinaryPrimitives.ReadInt32LittleEndian(bytes[(int)position..]);
                position += XnGineContentFacts.PlanePointLength;
                if (offset < 0 || offset % XnGineContentFacts.PointLength != 0 ||
                    offset / XnGineContentFacts.PointLength >= pointCount)
                {
                    return Walk.Fails;
                }
            }
        }

        return Walk.Walks;
    }
}
