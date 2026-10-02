using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Slfx77.Multitool.Core.Models.Sources;

namespace BethesdaMultitool.Core.Modeling.Xngine;

/// <summary>
///     The bounded content probe behind <see cref="XnGineModelReader.Probe" /> (cut-1c plan section 6.1): recognition by
///     content alone, so that exactly one reader recognizes each XnGine file. The file extension is never consulted.
/// </summary>
/// <remarks>
///     <list type="bullet">
///         <item>
///             Supported: a tag among <see cref="XnGineContentFacts.MeshTags" />, a 64-byte header whose lists fit, and
///             a plane walk that succeeds with 8-byte or 10-byte plane headers (<see cref="XnGineContentFacts" />, the
///             census's acceptance rule). Confirmed when the candidate is complete (the helper saw EOF within the 64 KiB
///             budget, never judged by the declared length, which is the stored size of an LZSS entry); Tentative
///             otherwise (40 retail static meshes exceed the prefix: 4 ARCH3D records, cover file 451 among them, 10
///             3D.BSA and 4 3D.BS6 entries by their decoded size, and 22 ROB segments, cover file MENU.ROB MENUA001
///             among them), a walk that runs past the prefix counting as not yet failed.
///         </item>
///         <item>
///             NotAModel when the <c>.3DC</c> shape test holds (<see cref="SatisfiesAnimatedShape" />): 147 of 147
///             <c>.3DC</c> files satisfy it and no static mesh does (MENU.ROB's four +16 = 9 segments fail it and stay
///             here), so a <c>.3DC</c> goes to the <c>bmt.redguard.3dc</c> reader alone. Reading one as a <c>.3D</c>
///             would return the wrong points without any error, because its header offsets are frame 1's.
///         </item>
///         <item>
///             Unsupported for the Redguard 3dfx tags <see cref="FxartTags" /> with
///             <see cref="XnGineModelFormatMetadata.FxartUnsupportedReason" /> (plan decision D8), Tentative because the
///             tag alone decides.
///         </item>
///         <item>NotAModel for everything else: another tag (the 3D.BSA strays), an empty ROB segment, a failed walk.</item>
///     </list>
///     The evidence is the variant with the header counts (<see cref="XnGineModelFormatMetadata.Describe" />), for
///     example <c>XnGine v2.7 mesh, 8-byte plane headers, 48 points, 34 planes</c>, with a note when the record extends
///     past the prefix.
/// </remarks>
internal static class XnGineModelProbe
{
    /// <summary>The note a Tentative evidence carries when the record extends past the probe prefix.</summary>
    public const string IncompleteNote = "; the record extends past the 64 KiB probe prefix";

    /// <summary>The frame-block preamble the shape test requires inside the prefix (six dwords).</summary>
    private const int FrameBlockPreambleBytes = 24;

    /// <summary>The tags of Redguard's 3dfx (fxart) meshes, a later cut (plan decision D8).</summary>
    public static IReadOnlyList<string> FxartTags { get; } = ["v4.0", "v5.0"];

    /// <summary>Probes one bounded candidate (see the type remarks).</summary>
    public static ModelProbeResult Probe(ModelSourceCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var content = candidate.Content;
        if (content.Length < XnGineContentFacts.HeaderLength)
        {
            return ModelProbeResult.NotAModel;
        }

        var tag = Tag(content);
        if (FxartTags.Contains(tag, StringComparer.Ordinal))
        {
            return new ModelProbeResult(ModelProbeKind.Unsupported, ModelProbeConfidence.Tentative,
                new ModelProbeEvidence(0, XnGineContentFacts.HeaderLength,
                    string.Create(CultureInfo.InvariantCulture, $"Redguard 3dfx mesh tag {tag}")),
                XnGineModelFormatMetadata.FxartUnsupportedReason);
        }

        if (!XnGineContentFacts.MeshTags.Contains(tag, StringComparer.Ordinal) || SatisfiesAnimatedShape(content))
        {
            return ModelProbeResult.NotAModel;
        }

        var facts = XnGineContentFacts.Measure(content, candidate.IsComplete);
        if (facts.DaggerfallWalk == XnGineLayoutWalk.Fails && facts.BattlespireWalk == XnGineLayoutWalk.Fails)
        {
            return ModelProbeResult.NotAModel;
        }

        var headerLength = EvidenceHeaderLength(facts);
        var description = XnGineModelFormatMetadata.Describe(tag, headerLength, facts.PointCount, facts.PlaneCount) +
                          (candidate.IsComplete ? string.Empty : IncompleteNote);
        return new ModelProbeResult(ModelProbeKind.Supported,
            candidate.IsComplete ? ModelProbeConfidence.Confirmed : ModelProbeConfidence.Tentative,
            new ModelProbeEvidence(0, content.Length, description));
    }

    /// <summary>
    ///     The plan's <c>.3DC</c> shape test S over a probe prefix (or a whole record): header +16 (the frame count) is
    ///     positive; the +20 frame block is positive and its six-dword preamble lies inside the bytes; the frame table
    ///     (block dword 0) starts at or before the plane list (+60), which lies inside the bytes; and the table span is a
    ///     whole number of 3- or 4-dword records per frame. True on 147 of 147 retail <c>.3DC</c> files and on no static
    ///     mesh (slice-5 receipt <c>measure_slice5.json</c>; the oracle is <c>rg3dc_probe.shape_test</c>).
    /// </summary>
    public static bool SatisfiesAnimatedShape(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < XnGineContentFacts.HeaderLength)
        {
            return false;
        }

        var frames = BinaryPrimitives.ReadInt32LittleEndian(bytes[16..]);
        var block = BinaryPrimitives.ReadInt32LittleEndian(bytes[20..]);
        var planeList = BinaryPrimitives.ReadInt32LittleEndian(bytes[60..]);
        if (frames <= 0 || block <= 0 || (long)block + FrameBlockPreambleBytes > bytes.Length)
        {
            return false;
        }

        var table = BinaryPrimitives.ReadInt32LittleEndian(bytes[block..]);
        if (table < 0 || table > planeList || planeList > bytes.Length)
        {
            return false;
        }

        var span = (long)planeList - table;
        var perFrame = 4L * frames;
        return span > 0 && span % perFrame == 0 && span / perFrame is 3 or 4;
    }

    /// <summary>The record's tag: its first four bytes up to a NUL, as ASCII (the <c>.3DC</c> probe reads it the same way).</summary>
    public static string Tag(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 4)
        {
            throw new ArgumentException("A tag is the first four bytes of a record.", nameof(bytes));
        }

        var head = bytes[..4];
        var end = head.IndexOf((byte)0);
        return Encoding.ASCII.GetString(end < 0 ? head : head[..end]);
    }

    /// <summary>
    ///     The plane-header size the evidence names: a layout that walks (8 bytes when both do, as the reader then reads
    ///     it), else the one whose walk ran past the prefix.
    /// </summary>
    private static int EvidenceHeaderLength(XnGineContentFacts facts)
    {
        if (facts.WalksWithDaggerfallLayout)
        {
            return XnGineContentFacts.DaggerfallPlaneHeaderLength;
        }

        if (facts.WalksWithBattlespireLayout)
        {
            return XnGineContentFacts.BattlespirePlaneHeaderLength;
        }

        return facts.DaggerfallWalk == XnGineLayoutWalk.Incomplete
            ? XnGineContentFacts.DaggerfallPlaneHeaderLength
            : XnGineContentFacts.BattlespirePlaneHeaderLength;
    }
}
