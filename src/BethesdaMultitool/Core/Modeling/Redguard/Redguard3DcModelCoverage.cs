using System.Buffers.Binary;
using System.Globalization;
using BethesdaMultitool.Core.Formats.Xngine;
using BethesdaMultitool.Core.Modeling.Xngine;
using Slfx77.Multitool.Core.Assets;
using Slfx77.Multitool.Core.Models.Sources;

namespace BethesdaMultitool.Core.Modeling.Redguard;

/// <summary>
///     The Redguard <c>.3DC</c> source census and its classifications (cut-1c plan section 4, "Coverage"). The element
///     list comes from the file's own header counts (+8 planes, +16 frames), its frame-table width and the tiling of its
///     declared byte areas, independently of what the reader builds: <c>header</c>, <c>frame-block</c> (the six-dword
///     preamble), <c>frame-table</c>, <c>frame-table:fourth-dwords</c> on a four-dword table, one <c>plane:{k}</c> per
///     plane, per frame <c>frame:{i}:points</c>, <c>frame:{i}:normals</c> and <c>frame:{i}:plane-data</c>, and
///     <c>unaccounted</c> when the tiling leaves the one declared region. No element is Dropped.
/// </summary>
/// <remarks>
///     Classifications: the header, the frame table (its three offsets per frame) and every frame's point block are
///     Typed (the keyframe is the base geometry, later blocks the morph targets; when a point reaches no vertex, every
///     point block's classification says so and names the <c>bmt.redguard.3dc.unreferenced-points</c> row that keeps
///     their stored values, <see cref="UnreferencedPointsReason" />, slice-6 review finding 2); a plane is Typed
///     unless it yields no triangle (none on retail), then NativeOnly with the <c>.3D</c> reason; the preamble, the
///     fourth frame-table dword, every normal and plane-data block and the unaccounted region are NativeOnly, carried
///     by the <c>bmt.redguard.3dc.*</c> rows. The largest retail file (228 frames, 881 planes at most) stays far below
///     <see cref="ModelSourceCoverage.MaximumElements" />.
/// </remarks>
internal static class Redguard3DcModelCoverage
{
    /// <summary>The header element.</summary>
    public const string HeaderElement = "header";

    /// <summary>The frame-block (preamble) element.</summary>
    public const string FrameBlockElement = "frame-block";

    /// <summary>The frame-table element.</summary>
    public const string FrameTableElement = "frame-table";

    /// <summary>The fourth frame-table dwords of a four-dword table.</summary>
    public const string FourthDwordsElement = "frame-table:fourth-dwords";

    /// <summary>The unaccounted region.</summary>
    public const string UnaccountedElement = "unaccounted";

    /// <summary>The element kind of the header.</summary>
    public const string HeaderKind = "redguard.3dc.header";

    /// <summary>The element kind of the preamble.</summary>
    public const string FrameBlockKind = "redguard.3dc.frame-block";

    /// <summary>The element kind of the frame table.</summary>
    public const string FrameTableKind = "redguard.3dc.frame-table";

    /// <summary>The element kind of the fourth frame-table dwords.</summary>
    public const string FourthDwordsKind = "redguard.3dc.frame-table-fourth-dwords";

    /// <summary>The element kind of a plane.</summary>
    public const string PlaneKind = "redguard.3dc.plane";

    /// <summary>The element kind of a frame's point block.</summary>
    public const string FramePointsKind = "redguard.3dc.frame-points";

    /// <summary>The element kind of a frame's normal block.</summary>
    public const string FrameNormalsKind = "redguard.3dc.frame-normals";

    /// <summary>The element kind of a frame's plane-data block.</summary>
    public const string FramePlaneDataKind = "redguard.3dc.frame-plane-data";

    /// <summary>The element kind of the unaccounted region.</summary>
    public const string UnaccountedKind = "redguard.3dc.unaccounted";

    /// <summary>The reason of the preamble.</summary>
    public const string FrameBlockReason =
        "frame-block preamble dwords 1, 3, 4 and 5 undecoded (dword 0 is the frame-table offset, dword 2 the " +
        "unaccounted length); kept in bmt.redguard.3dc.preamble";

    /// <summary>The reason of the fourth frame-table dwords.</summary>
    public const string FourthDwordsReason =
        "the fourth frame-table dword is undecoded (on the 2,218 retail wide records it equals no block offset); kept " +
        "in bmt.redguard.3dc.frames";

    /// <summary>The reason of a wide file's normal block.</summary>
    public const string WideNormalsReason =
        "authored per-pose plane normals (int32 triples, lengths up to about 258: 69,656 of 1,030,183 retail " +
        "plane-frames exceed 256); Flat shading derives the directions from " +
        "each pose instead (they agree at cos >= 0.9999 on 997,906 of 1,030,183 retail plane-frames; 15 point the " +
        "opposite way); kept in bmt.redguard.3dc.frames";

    /// <summary>The reason of a narrow file's normal block.</summary>
    public const string NarrowNormalsReason =
        "4-byte per-plane frame records undecoded (no tried decoding matches); kept in bmt.redguard.3dc.frames";

    /// <summary>The reason of a frame's plane-data block.</summary>
    public const string PlaneDataReason =
        "per-frame plane-data records (24 bytes per plane wide, 12 narrow) undecoded; kept in bmt.redguard.3dc.frames";

    /// <summary>The reason of the unaccounted region.</summary>
    public const string UnaccountedReason =
        "the one region the frame block declares (preamble dword 2) and the tiling leaves, undecoded; kept in " +
        "bmt.redguard.3dc.unaccounted";

    /// <summary>
    ///     The reason every Typed point block carries when <paramref name="count" /> points reach no vertex (slice-6 review
    ///     finding 2): their stored values are in no vertex or morph target, only in the unreferenced-points row.
    /// </summary>
    public static string UnreferencedPointsReason(int count)
    {
        return string.Create(CultureInfo.InvariantCulture,
            $"typed for the points a primitive vertex names (the keyframe block as vertex positions, a later block as " +
            $"morph targets); the {count} point(s) no vertex names (no plane corner, or only planes that yield no " +
            $"triangle) are kept with their stored values in {Redguard3DcModelNativeState.UnreferencedPointsKind}");
    }

    /// <summary>The elements besides the planes and the frames: header, preamble, table, fourth dwords, unaccounted.</summary>
    public const int FixedElements = 5;

    /// <summary>The elements per frame: points, normals, plane data.</summary>
    public const int ElementsPerFrame = 3;

    /// <summary>The element identity of frame <paramref name="frame" />'s point block.</summary>
    public static string FramePointsElement(int frame)
    {
        return string.Create(CultureInfo.InvariantCulture, $"frame:{frame}:points");
    }

    /// <summary>The element identity of frame <paramref name="frame" />'s normal block.</summary>
    public static string FrameNormalsElement(int frame)
    {
        return string.Create(CultureInfo.InvariantCulture, $"frame:{frame}:normals");
    }

    /// <summary>The element identity of frame <paramref name="frame" />'s plane-data block.</summary>
    public static string FramePlaneDataElement(int frame)
    {
        return string.Create(CultureInfo.InvariantCulture, $"frame:{frame}:plane-data");
    }

    /// <summary>
    ///     The independent census of a file: <paramref name="bytes" /> is the whole file (its header supplies the plane
    ///     and frame counts, its frame table the record width), <paramref name="tiling" /> the tiling of its declared
    ///     areas, whose one gap is the unaccounted region.
    /// </summary>
    public static IReadOnlyList<ModelSourceElement> Census(ReadOnlySpan<byte> bytes, ByteAreaTiling tiling)
    {
        ArgumentNullException.ThrowIfNull(tiling);
        var planeCount = Math.Max(BinaryPrimitives.ReadInt32LittleEndian(bytes[8..]), 0);
        var frameCount = Math.Max(BinaryPrimitives.ReadInt32LittleEndian(bytes[16..]), 0);
        var frameBlock = BinaryPrimitives.ReadInt32LittleEndian(bytes[20..]);
        var table = BinaryPrimitives.ReadInt32LittleEndian(bytes[frameBlock..]);
        var planeList = BinaryPrimitives.ReadInt32LittleEndian(bytes[60..]);
        var recordDwords = frameCount == 0 ? 0 : (planeList - table) / (4 * frameCount);
        var elements = new List<ModelSourceElement>(FixedElements + planeCount + ElementsPerFrame * frameCount)
        {
            new(HeaderElement, HeaderKind),
            new(FrameBlockElement, FrameBlockKind),
            new(FrameTableElement, FrameTableKind)
        };
        if (recordDwords == 4)
        {
            elements.Add(new ModelSourceElement(FourthDwordsElement, FourthDwordsKind));
        }

        for (var k = 0; k < planeCount; k++)
        {
            elements.Add(new ModelSourceElement(XnGineModelCoverage.PlaneElement(k), PlaneKind));
        }

        for (var frame = 0; frame < frameCount; frame++)
        {
            elements.Add(new ModelSourceElement(FramePointsElement(frame), FramePointsKind));
            elements.Add(new ModelSourceElement(FrameNormalsElement(frame), FrameNormalsKind));
            elements.Add(new ModelSourceElement(FramePlaneDataElement(frame), FramePlaneDataKind));
        }

        if (tiling.Gaps.Count > 0)
        {
            elements.Add(new ModelSourceElement(UnaccountedElement, UnaccountedKind));
        }

        return elements;
    }

    /// <summary>
    ///     Classifies every element of <paramref name="census" />: a plane by <paramref name="planes" /> (one entry per
    ///     plane), a normal block by the frame width (<paramref name="wide" />), a point block with
    ///     <see cref="UnreferencedPointsReason" /> when <paramref name="unreferencedPoints" /> is positive, everything else
    ///     by its kind (see the type remarks).
    /// </summary>
    /// <exception cref="InvalidDataException">The census names a plane the triangulation does not describe.</exception>
    public static IReadOnlyList<ModelSourceClassification> Classify(IReadOnlyList<ModelSourceElement> census,
        IReadOnlyList<XnGineTriangulatedPlane> planes, bool wide, int unreferencedPoints)
    {
        ArgumentNullException.ThrowIfNull(census);
        ArgumentNullException.ThrowIfNull(planes);
        var rows = new List<ModelSourceClassification>(census.Count);
        var ordinal = 0;
        var pointsReason = unreferencedPoints > 0 ? UnreferencedPointsReason(unreferencedPoints) : null;
        foreach (var element in census)
        {
            rows.Add(element.Kind switch
            {
                HeaderKind or FrameTableKind => new ModelSourceClassification(element.Identity,
                    ModelSourceCoverageKind.Typed),
                FramePointsKind => new ModelSourceClassification(element.Identity, ModelSourceCoverageKind.Typed,
                    pointsReason),
                FrameBlockKind => NativeOnly(element, FrameBlockReason),
                FourthDwordsKind => NativeOnly(element, FourthDwordsReason),
                FrameNormalsKind => NativeOnly(element, wide ? WideNormalsReason : NarrowNormalsReason),
                FramePlaneDataKind => NativeOnly(element, PlaneDataReason),
                PlaneKind => ClassifyPlane(element, ordinal < planes.Count ? planes[ordinal] : null),
                _ => NativeOnly(element, UnaccountedReason)
            });
            if (element.Kind == PlaneKind)
            {
                ordinal++;
            }
        }

        return rows;
    }

    /// <summary>
    ///     Builds the coverage of one file: its census and classifications (<paramref name="unreferencedPoints" />, the
    ///     count of points no primitive vertex names, only shapes the point blocks' reason).
    /// </summary>
    public static ModelSourceCoverage Build(AssetReference source, ReadOnlySpan<byte> bytes, ByteAreaTiling tiling,
        IReadOnlyList<XnGineTriangulatedPlane> planes, bool wide, int unreferencedPoints,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        var census = Census(bytes, tiling);
        var evidence = string.Create(CultureInfo.InvariantCulture,
            $"Redguard .3DC census: the 64-byte header, the frame-block preamble, the frame table (and its fourth dwords " +
            $"on a four-dword table), one element per plane of the header's plane count ({planes.Count}), three blocks " +
            $"per frame of the header's frame count, and the one region the tiling of the {tiling.Length}-byte file " +
            $"leaves ({tiling.Gaps.Count} range(s))");
        return new ModelSourceCoverage(source, evidence, census, Classify(census, planes, wide, unreferencedPoints),
            cancellationToken);
    }

    /// <summary>A NativeOnly classification with its reason.</summary>
    private static ModelSourceClassification NativeOnly(ModelSourceElement element, string reason)
    {
        return new ModelSourceClassification(element.Identity, ModelSourceCoverageKind.NativeOnly, reason);
    }

    /// <summary>A plane's classification: Typed when it reaches the geometry, NativeOnly with its reason otherwise.</summary>
    private static ModelSourceClassification ClassifyPlane(ModelSourceElement element, XnGineTriangulatedPlane? plane)
    {
        if (plane is null)
        {
            throw new InvalidDataException($"The census names {element.Identity}, which the triangulation does not describe.");
        }

        return plane.IsOmitted
            ? NativeOnly(element, XnGineModelCoverage.ReasonFor(plane.Omission))
            : new ModelSourceClassification(element.Identity, ModelSourceCoverageKind.Typed);
    }
}
