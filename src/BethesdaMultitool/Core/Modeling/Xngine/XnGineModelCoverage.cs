using System.Buffers.Binary;
using System.Globalization;
using BethesdaMultitool.Core.Formats.Xngine;
using Slfx77.Multitool.Core.Assets;
using Slfx77.Multitool.Core.Models.Sources;

namespace BethesdaMultitool.Core.Modeling.Xngine;

/// <summary>
///     The XnGine <c>.3D</c> source census and its classifications (cut-1c plan section 3.3). The element list comes
///     from the record's own header counts and a byte-area tiling, independently of what the reader builds:
///     <c>header</c>, <c>points</c>, <c>normals</c>, one <c>plane:{k}</c> per plane the header counts, <c>plane-data</c>
///     when that area fits the record, <c>object-data</c> when its count is positive, and one
///     <c>unclaimed:{start}-{end}</c> per byte range the declared areas leave uncovered. No element is Dropped.
/// </summary>
/// <remarks>
///     Classifications: the header, point list and normal list are Typed (the header's undecoded dwords are kept in the
///     <c>bmt.xngine.header</c> row; when a point reaches no vertex, the point list's classification says so and names
///     the <c>bmt.xngine.unreferenced-points</c> row that keeps its coordinates, <see cref="UnreferencedPointsReason" />,
///     slice-6 review finding 2: 49 points in 9 retail meshes); a plane is Typed unless it yields no triangle, then
///     NativeOnly with the reason <see cref="ReasonFor" /> gives, carried by <c>bmt.xngine.omitted-planes</c>; the plane
///     data, object data and every unclaimed range are NativeOnly. The tiling is the gate-1c M-T rule (<see cref="ByteAreaTiling" />): header, point
///     list, normal list, walked plane list and plane-data area tile with zero overlaps on all 19,725 retail static meshes
///     and leave exactly one gap, always ending at the end of the record. The object-data area has no stated length, so
///     its element names the area and its reason says where the offset falls (<see cref="ObjectDataHolder" />): of the
///     19,722 retail meshes with a positive count, the offset starts the gap on 19,689 and lies inside it on 31 (the
///     gap's element then carries the bytes), and lies inside a declared area on 2, ARCH3D 906 (index 4722, inside the
///     plane list) and ARCH3D 907 (index 7614, inside the plane-data area), whose bytes no unclaimed element holds
///     (slice-5 review receipt <c>measure_review_fixes.json</c>).
/// </remarks>
internal static class XnGineModelCoverage
{
    /// <summary>The header element.</summary>
    public const string HeaderElement = "header";

    /// <summary>The point-list element.</summary>
    public const string PointsElement = "points";

    /// <summary>The normal-list element.</summary>
    public const string NormalsElement = "normals";

    /// <summary>The plane-data element.</summary>
    public const string PlaneDataElement = "plane-data";

    /// <summary>The object-data element.</summary>
    public const string ObjectDataElement = "object-data";

    /// <summary>The element kind of the header.</summary>
    public const string HeaderKind = "xngine.header";

    /// <summary>The element kind of the point list.</summary>
    public const string PointsKind = "xngine.point-list";

    /// <summary>The element kind of the normal list.</summary>
    public const string NormalsKind = "xngine.normal-list";

    /// <summary>The element kind of a plane.</summary>
    public const string PlaneKind = "xngine.plane";

    /// <summary>The element kind of the plane-data area.</summary>
    public const string PlaneDataKind = "xngine.plane-data";

    /// <summary>The element kind of the object-data area.</summary>
    public const string ObjectDataKind = "xngine.object-data";

    /// <summary>The element kind of an unclaimed byte range.</summary>
    public const string UnclaimedKind = "xngine.unclaimed";

    /// <summary>The reason of the plane-data element.</summary>
    public const string PlaneDataReason = "24-byte plane records undecoded; kept in bmt.xngine.plane-data";

    /// <summary>The reason of an unclaimed element.</summary>
    public const string UnclaimedReason = "bytes outside every declared area; kept in bmt.xngine.unclaimed";

    /// <summary>The elements besides the planes and the unclaimed ranges: header, points, normals, plane data, object data.</summary>
    public const int FixedElements = 5;

    /// <summary>The part of the object-data reason every record shares.</summary>
    private const string ObjectDataUndecoded =
        "object-data area undecoded (its records are variable-length and it has no stated length)";

    /// <summary>The element identity of plane <paramref name="index" />.</summary>
    public static string PlaneElement(int index)
    {
        return string.Create(CultureInfo.InvariantCulture, $"plane:{index}");
    }

    /// <summary>
    ///     The area of <paramref name="tiling" /> that holds the object-data <paramref name="offset" />: the unclaimed
    ///     range containing it, else the declared area containing it, else null (the offset lies outside the record).
    ///     Gaps and fitting areas are disjoint, so at most one of each kind can hold it.
    /// </summary>
    public static ByteArea? ObjectDataHolder(ByteAreaTiling tiling, int offset)
    {
        ArgumentNullException.ThrowIfNull(tiling);
        foreach (var gap in tiling.Gaps)
        {
            if (offset >= gap.Start && offset < gap.End)
            {
                return gap;
            }
        }

        foreach (var area in tiling.Areas)
        {
            if (offset >= area.Start && offset < area.End)
            {
                return area;
            }
        }

        return null;
    }

    /// <summary>
    ///     The NativeOnly reason of the object-data element, derived from where its offset falls
    ///     (<see cref="ObjectDataHolder" />): only an unclaimed range's element carries the bytes, so the reason names
    ///     <c>bmt.xngine.unclaimed</c> exactly when an unclaimed range holds the offset.
    /// </summary>
    public static string ObjectDataReason(ByteAreaTiling tiling, int offset)
    {
        ArgumentNullException.ThrowIfNull(tiling);
        var culture = CultureInfo.InvariantCulture;
        return ObjectDataHolder(tiling, offset) switch
        {
            { } gap when gap.Name.StartsWith(ByteAreaTiling.UnclaimedPrefix, StringComparison.Ordinal) =>
                string.Create(culture,
                    $"{ObjectDataUndecoded}; its offset {offset} lies in the unclaimed range {gap.Name}, whose bytes " +
                    $"bmt.xngine.unclaimed keeps; its offset and count are kept in bmt.xngine.object-data"),
            { } area => string.Create(culture,
                $"{ObjectDataUndecoded}; its offset {offset} lies inside the declared {area.Name} area " +
                $"[{area.Start}, {area.End}), so no unclaimed range holds its bytes; only its offset and count are " +
                $"kept, in bmt.xngine.object-data"),
            null => string.Create(culture,
                $"{ObjectDataUndecoded}; its offset {offset} lies outside the {tiling.Length}-byte record; only its " +
                $"offset and count are kept, in bmt.xngine.object-data")
        };
    }

    /// <summary>
    ///     The reason the Typed point list carries when <paramref name="count" /> points reach no vertex (slice-6 review
    ///     finding 2): their coordinates are in no typed state, only in the unreferenced-points row.
    /// </summary>
    public static string UnreferencedPointsReason(int count)
    {
        return string.Create(CultureInfo.InvariantCulture,
            $"typed as vertex positions for the points a primitive vertex names; the {count} point(s) no vertex names " +
            $"(no plane corner, or only planes that yield no triangle) are kept with their stored coordinates in " +
            $"{XnGineModelNativeState.UnreferencedPointsKind}");
    }

    /// <summary>The NativeOnly reason of a plane that yields no triangle (the omitted-planes row carries it).</summary>
    public static string ReasonFor(XnGinePlaneOmission omission)
    {
        return omission switch
        {
            XnGinePlaneOmission.FewerThanThreeCorners =>
                "fewer than 3 corners; not a drawable face; carried by bmt.xngine.omitted-planes",
            XnGinePlaneOmission.FewerThanThreeKeptCorners =>
                "fewer than 3 corners survive the reference corner test; carried by bmt.xngine.omitted-planes",
            XnGinePlaneOmission.ZeroNormalNoArea =>
                "zero authored normal and no area; no drawable surface; carried by bmt.xngine.omitted-planes",
            _ => throw new ArgumentOutOfRangeException(nameof(omission), omission, "The plane is not omitted.")
        };
    }

    /// <summary>
    ///     The independent census of a record: <paramref name="bytes" /> is the whole record (its header supplies the
    ///     plane count and the object-data count), <paramref name="tiling" /> the tiling of its declared areas.
    /// </summary>
    public static IReadOnlyList<ModelSourceElement> Census(ReadOnlySpan<byte> bytes, ByteAreaTiling tiling)
    {
        ArgumentNullException.ThrowIfNull(tiling);
        var planeCount = BinaryPrimitives.ReadInt32LittleEndian(bytes[8..]);
        var objectDataCount = BinaryPrimitives.ReadInt32LittleEndian(bytes[32..]);
        var elements = new List<ModelSourceElement>(FixedElements + Math.Max(planeCount, 0) + tiling.Gaps.Count)
        {
            new(HeaderElement, HeaderKind),
            new(PointsElement, PointsKind),
            new(NormalsElement, NormalsKind)
        };
        for (var k = 0; k < planeCount; k++)
        {
            elements.Add(new ModelSourceElement(PlaneElement(k), PlaneKind));
        }

        if (tiling.Areas.Any(static area => area.Name == PlaneDataElement))
        {
            elements.Add(new ModelSourceElement(PlaneDataElement, PlaneDataKind));
        }

        if (objectDataCount > 0)
        {
            elements.Add(new ModelSourceElement(ObjectDataElement, ObjectDataKind));
        }

        foreach (var gap in tiling.Gaps)
        {
            elements.Add(new ModelSourceElement(gap.Name, UnclaimedKind));
        }

        return elements;
    }

    /// <summary>
    ///     Classifies every element of <paramref name="census" />: a plane by <paramref name="planes" /> (one entry per
    ///     plane), the object-data area with <paramref name="objectDataReason" /> (<see cref="ObjectDataReason" />), the
    ///     point list with <see cref="UnreferencedPointsReason" /> when <paramref name="unreferencedPoints" /> is positive,
    ///     everything else by its kind (see the type remarks).
    /// </summary>
    /// <exception cref="InvalidDataException">The census names a plane the triangulation does not describe.</exception>
    public static IReadOnlyList<ModelSourceClassification> Classify(IReadOnlyList<ModelSourceElement> census,
        IReadOnlyList<XnGineTriangulatedPlane> planes, string objectDataReason, int unreferencedPoints)
    {
        ArgumentNullException.ThrowIfNull(census);
        ArgumentNullException.ThrowIfNull(planes);
        ArgumentException.ThrowIfNullOrWhiteSpace(objectDataReason);
        var rows = new List<ModelSourceClassification>(census.Count);
        var ordinal = 0;
        foreach (var element in census)
        {
            switch (element.Kind)
            {
                case PointsKind:
                    rows.Add(new ModelSourceClassification(element.Identity, ModelSourceCoverageKind.Typed,
                        unreferencedPoints > 0 ? UnreferencedPointsReason(unreferencedPoints) : null));
                    break;
                case HeaderKind or NormalsKind:
                    rows.Add(new ModelSourceClassification(element.Identity, ModelSourceCoverageKind.Typed));
                    break;
                case PlaneKind:
                    rows.Add(ClassifyPlane(element, ordinal < planes.Count ? planes[ordinal] : null));
                    ordinal++;
                    break;
                case PlaneDataKind:
                    rows.Add(new ModelSourceClassification(element.Identity, ModelSourceCoverageKind.NativeOnly,
                        PlaneDataReason));
                    break;
                case ObjectDataKind:
                    rows.Add(new ModelSourceClassification(element.Identity, ModelSourceCoverageKind.NativeOnly,
                        objectDataReason));
                    break;
                default:
                    rows.Add(new ModelSourceClassification(element.Identity, ModelSourceCoverageKind.NativeOnly,
                        UnclaimedReason));
                    break;
            }
        }

        return rows;
    }

    /// <summary>
    ///     Builds the coverage of one record: its census and classifications (<paramref name="unreferencedPoints" />, the
    ///     count of points no primitive vertex names, only shapes the point list's reason).
    /// </summary>
    public static ModelSourceCoverage Build(AssetReference source, ReadOnlySpan<byte> bytes, ByteAreaTiling tiling,
        IReadOnlyList<XnGineTriangulatedPlane> planes, int unreferencedPoints, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        var census = Census(bytes, tiling);
        var objectDataReason = ObjectDataReason(tiling, BinaryPrimitives.ReadInt32LittleEndian(bytes[28..]));
        var evidence = string.Create(CultureInfo.InvariantCulture,
            $"XnGine .3D census: the 64-byte header, its point and normal lists, one element per plane of the header's " +
            $"plane count ({planes.Count}), the plane-data area when it fits, the object-data area when its count is " +
            $"positive, and every byte range the declared areas leave unclaimed in the {tiling.Length}-byte record " +
            $"({tiling.Gaps.Count} range(s))");
        return new ModelSourceCoverage(source, evidence, census,
            Classify(census, planes, objectDataReason, unreferencedPoints), cancellationToken);
    }

    /// <summary>A plane's classification: Typed when it reaches the geometry, NativeOnly with its reason otherwise.</summary>
    private static ModelSourceClassification ClassifyPlane(ModelSourceElement element, XnGineTriangulatedPlane? plane)
    {
        if (plane is null)
        {
            throw new InvalidDataException($"The census names {element.Identity}, which the triangulation does not describe.");
        }

        return plane.IsOmitted
            ? new ModelSourceClassification(element.Identity, ModelSourceCoverageKind.NativeOnly, ReasonFor(plane.Omission))
            : new ModelSourceClassification(element.Identity, ModelSourceCoverageKind.Typed);
    }
}
