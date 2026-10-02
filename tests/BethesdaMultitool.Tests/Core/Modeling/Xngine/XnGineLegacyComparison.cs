using System.Globalization;
using System.Numerics;
using BethesdaMultitool.Core.Formats.Xngine.Mesh;
using BethesdaMultitool.Core.Modeling.Xngine;
using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Tests.Core.Modeling.Xngine;

/// <summary>
///     The A3x comparison (cut-1c plan section 9): the reader's document against the legacy extractor
///     (<see cref="XnGineMesh.Parse" /> plus <see cref="XnGineMeshDecomposer.Decompose" />, as <c>classic mesh export</c>
///     runs them) PER PLANE, TRIANGLE FOR TRIANGLE, against the legacy exporter's swapped stream: each legacy polygon
///     triangle <c>(0, i, i+1)</c> is written <c>(0, i+1, i)</c> by <c>XnGineMeshGlbExporter</c>, and the reader's
///     i-th triangle of the same plane must carry the same three corners in the same order: position <c>(x, -y, z)</c>
///     at native scale (the legacy /256 undone exactly), the legacy's normalized normal with Y negated (the reader's
///     authored normal normalized), and the legacy texel UV times 1/64 (the fallback both sides use without textures).
/// </summary>
/// <remarks>
///     <para>
///         Exclusions, each counted and returned rather than silently skipped: a plane the legacy keeps and the reader
///         omits (the zero-normal three-corner planes, plan D5), whose shape the caller asserts; and a plane whose UVs
///         the legacy parse unfolded while the reader did not (a Redguard record reaches the reference's unfold through
///         its object id of 0 or its segment index), whose UVs are not compared but whose positions, normals and
///         triangles still are. The comparison shares <see cref="XnGineMesh.Parse" /> with the reader, so it catches
///         reader errors, not parser errors (hop A6 covers the parser).
///     </para>
///     <para>
///         The reader side is extracted into plain per-plane lists (<see cref="ReaderPlanes" />) so a control can
///         perturb them (move a vertex, fan from c0) and show the comparison fails.
///     </para>
/// </remarks>
internal static class XnGineLegacyComparison
{
    /// <summary>One plane as the reader drew it: its face's vertices in face order and its triangles as face-local ordinals.</summary>
    /// <param name="Plane">The source plane ordinal.</param>
    /// <param name="Vertices">The face's vertices in face order.</param>
    /// <param name="Triangles">The triangles whose corners lie in this face, as face-local vertex ordinals, in index order.</param>
    internal sealed record ReaderPlane(int Plane, IReadOnlyList<SceneVertex> Vertices, IReadOnlyList<(int A, int B, int C)> Triangles);

    /// <summary>The outcome of one comparison.</summary>
    /// <param name="ComparedPlanes">Planes compared triangle for triangle.</param>
    /// <param name="ComparedTriangles">Triangles compared.</param>
    /// <param name="Mismatches">Every difference found (empty when the reader agrees).</param>
    /// <param name="LegacyKeptReaderOmitted">Planes the legacy keeps and the reader omits (ordinal list).</param>
    /// <param name="UvExcludedPlanes">Planes whose UVs were not compared because only the legacy parse unfolded them.</param>
    internal sealed record Result(
        int ComparedPlanes,
        int ComparedTriangles,
        IReadOnlyList<string> Mismatches,
        IReadOnlyList<int> LegacyKeptReaderOmitted,
        IReadOnlyList<int> UvExcludedPlanes);

    /// <summary>The reader's planes, keyed by source plane ordinal (see <see cref="ReaderPlane" />).</summary>
    /// <exception cref="InvalidOperationException">A triangle spans two faces, or a plane appears twice.</exception>
    public static Dictionary<int, ReaderPlane> ReaderPlanes(ModelDocument document)
    {
        var planes = new Dictionary<int, ReaderPlane>();
        foreach (var primitive in document.Meshes.SelectMany(static mesh => mesh.Primitives))
        {
            var faces = primitive.Faces ?? throw new InvalidOperationException("The primitive carries no faces.");
            var ordinals = XnGineModelTestSupport.PlaneOrdinals(primitive);
            var faceOfVertex = new int[primitive.Vertices.Count];
            var firstOfFace = new int[faces.FaceCount];
            var cursor = 0;
            for (var f = 0; f < faces.FaceCount; f++)
            {
                firstOfFace[f] = cursor;
                for (var j = 0; j < faces.FaceSizes[f]; j++)
                {
                    faceOfVertex[faces.CornerIndices[cursor + j]] = f;
                }

                cursor += faces.FaceSizes[f];
            }

            var triangles = new List<(int, int, int)>[faces.FaceCount];
            for (var f = 0; f < triangles.Length; f++)
            {
                triangles[f] = [];
            }

            for (var t = 0; t + 2 < primitive.Indices.Count; t += 3)
            {
                var face = faceOfVertex[primitive.Indices[t]];
                if (faceOfVertex[primitive.Indices[t + 1]] != face || faceOfVertex[primitive.Indices[t + 2]] != face)
                {
                    throw new InvalidOperationException($"Triangle {t / 3} spans two faces.");
                }

                var first = firstOfFace[face];
                triangles[face].Add((primitive.Indices[t] - first, primitive.Indices[t + 1] - first,
                    primitive.Indices[t + 2] - first));
            }

            for (var f = 0; f < faces.FaceCount; f++)
            {
                var vertices = new SceneVertex[faces.FaceSizes[f]];
                for (var j = 0; j < vertices.Length; j++)
                {
                    vertices[j] = primitive.Vertices[faces.CornerIndices[firstOfFace[f] + j]];
                }

                if (!planes.TryAdd(ordinals[f], new ReaderPlane(ordinals[f], vertices, triangles[f])))
                {
                    throw new InvalidOperationException($"Plane {ordinals[f]} appears twice.");
                }
            }
        }

        return planes;
    }

    /// <summary>
    ///     A control's reader planes: the same kept corners (the vertices the triangles reference), but fanned from the
    ///     lowest SOURCE corner (c0 when kept) as the withdrawn draft did, in the reader's reversed orientation.
    /// </summary>
    public static Dictionary<int, ReaderPlane> FanFromCornerZero(Dictionary<int, ReaderPlane> planes)
    {
        var result = new Dictionary<int, ReaderPlane>();
        foreach (var (ordinal, plane) in planes)
        {
            var n = plane.Vertices.Count;
            var kept = plane.Triangles.SelectMany(static t => new[] { t.A, t.B, t.C }).Distinct()
                .Select(vertex => (Vertex: vertex, Corner: vertex == 0 ? 0 : n - vertex))
                .OrderBy(static pair => pair.Corner)
                .Select(static pair => pair.Vertex)
                .ToList();
            var fan = new List<(int, int, int)>();
            for (var i = 1; i + 1 < kept.Count && n > 3; i++)
            {
                fan.Add((kept[0], kept[i + 1], kept[i]));
            }

            result[ordinal] = n > 3 ? plane with { Triangles = fan } : plane;
        }

        return result;
    }

    /// <summary>Compares the reader's planes with the legacy extractor's output (see the type summary).</summary>
    /// <param name="reader">The reader's planes (<see cref="ReaderPlanes" />).</param>
    /// <param name="legacyMesh">The legacy parse (reference UVs, the legacy object id and layout).</param>
    /// <param name="stored">The stored-UV parse of the same bytes, to tell an unfolded corner from a stored one.</param>
    /// <param name="readerUnfolded">Whether the reader applied the unfold (its uv-rule row).</param>
    public static Result Compare(IReadOnlyDictionary<int, ReaderPlane> reader, XnGineMesh legacyMesh, XnGineMesh stored,
        bool readerUnfolded)
    {
        var legacy = XnGineMeshDecomposer.Decompose(legacyMesh);
        var cursors = new Dictionary<(int, int), int>();
        var mismatches = new List<string>();
        var omitted = new List<int>();
        var uvExcluded = new List<int>();
        int planesCompared = 0, trianglesCompared = 0;
        foreach (var plane in legacyMesh.Planes)
        {
            var n = plane.Points.Count;
            var kept = n < 3 ? 0 : n == 3 ? 3 : XnGineMeshDecomposer.CornerIndices(PurePoints(legacyMesh, plane)).Count;
            if (kept < 3)
            {
                if (reader.ContainsKey(plane.Index))
                {
                    mismatches.Add(string.Create(CultureInfo.InvariantCulture,
                        $"plane {plane.Index}: the legacy drops it (kept {kept}) but the reader draws it"));
                }

                continue;
            }

            var submesh = legacy.SubMeshes.First(s => s.TextureArchive == plane.TextureArchive &&
                                                      s.TextureRecord == plane.TextureRecord);
            var key = (plane.TextureArchive, plane.TextureRecord);
            var first = cursors.GetValueOrDefault(key);
            cursors[key] = first + kept;
            if (!reader.TryGetValue(plane.Index, out var drawn))
            {
                omitted.Add(plane.Index);
                continue;
            }

            var compareUv = readerUnfolded || !UnfoldedByLegacyOnly(plane, stored.Planes[plane.Index]);
            if (!compareUv)
            {
                uvExcluded.Add(plane.Index);
            }

            planesCompared++;
            var legacyTriangles = new List<(int, int, int)>();
            for (var i = 1; i + 1 < kept; i++)
            {
                legacyTriangles.Add((0, i + 1, i));
            }

            if (legacyTriangles.Count != drawn.Triangles.Count)
            {
                mismatches.Add(string.Create(CultureInfo.InvariantCulture,
                    $"plane {plane.Index}: legacy {legacyTriangles.Count} triangles, reader {drawn.Triangles.Count}"));
                continue;
            }

            for (var t = 0; t < legacyTriangles.Count; t++)
            {
                trianglesCompared++;
                var (la, lb, lc) = legacyTriangles[t];
                var (ra, rb, rc) = drawn.Triangles[t];
                CompareCorner(submesh.Vertices[first + la], drawn.Vertices[ra], compareUv, plane.Index, t, 0, mismatches);
                CompareCorner(submesh.Vertices[first + lb], drawn.Vertices[rb], compareUv, plane.Index, t, 1, mismatches);
                CompareCorner(submesh.Vertices[first + lc], drawn.Vertices[rc], compareUv, plane.Index, t, 2, mismatches);
            }
        }

        foreach (var ordinal in reader.Keys.Where(k => k >= legacyMesh.Planes.Count))
        {
            mismatches.Add(string.Create(CultureInfo.InvariantCulture, $"plane {ordinal}: not a plane of the record"));
        }

        return new Result(planesCompared, trianglesCompared, mismatches, omitted, uvExcluded);
    }

    /// <summary>True when the legacy parse's first three corners differ from the stored values (the unfold ran there).</summary>
    public static bool UnfoldedByLegacyOnly(XnGinePlane legacy, XnGinePlane stored)
    {
        for (var q = 0; q < Math.Min(3, legacy.Points.Count); q++)
        {
            if (legacy.Points[q].U != stored.Points[q].U || legacy.Points[q].V != stored.Points[q].V)
            {
                return true;
            }
        }

        return false;
    }

    private static XnGineMeshDecomposer.PurePoint[] PurePoints(XnGineMesh mesh, XnGinePlane plane)
    {
        return plane.Points.Select(p => new XnGineMeshDecomposer.PurePoint(mesh.Points[p.PointIndex], plane.Normal, p.U, p.V))
            .ToArray();
    }

    private static void CompareCorner(XnGineVertex legacy, SceneVertex reader, bool compareUv, int plane, int triangle,
        int corner, List<string> mismatches)
    {
        var position = new Vector3(legacy.Position.X * XnGineMesh.PointDivisor, -(legacy.Position.Y * XnGineMesh.PointDivisor),
            legacy.Position.Z * XnGineMesh.PointDivisor);
        var normal = new Vector3(legacy.Normal.X, -legacy.Normal.Y, legacy.Normal.Z);
        var readerNormal = reader.Normal.LengthSquared() > 0 ? Vector3.Normalize(reader.Normal) : reader.Normal;
        var uv = new Vector2(legacy.TexelUv.X * (1f / XnGineModelGeometry.FallbackTextureSize),
            legacy.TexelUv.Y * (1f / XnGineModelGeometry.FallbackTextureSize));
        if (position != reader.Position)
        {
            mismatches.Add(Describe(plane, triangle, corner, "position", position, reader.Position));
        }

        if (normal != readerNormal)
        {
            mismatches.Add(Describe(plane, triangle, corner, "normal", normal, readerNormal));
        }

        if (compareUv && uv != reader.TexCoord)
        {
            mismatches.Add(string.Create(CultureInfo.InvariantCulture,
                $"plane {plane} triangle {triangle} corner {corner}: uv legacy {uv} reader {reader.TexCoord}"));
        }
    }

    private static string Describe(int plane, int triangle, int corner, string what, Vector3 legacy, Vector3 reader)
    {
        return string.Create(CultureInfo.InvariantCulture,
            $"plane {plane} triangle {triangle} corner {corner}: {what} legacy {legacy} reader {reader}");
    }
}
