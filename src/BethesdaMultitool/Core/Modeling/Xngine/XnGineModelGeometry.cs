using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using BethesdaMultitool.Core.Formats.Xngine.Mesh;
using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Xngine;

/// <summary>
///     Builds an XnGine mesh's primitives (cut-1c plan sections 3.1 and 3.2): one primitive per texture key that keeps a
///     plane with a triangle, in first-use order over ALL planes (<see cref="XnGineMesh.UniqueTextures" /> minus the
///     emptied keys, which is the legacy grouping); per primitive one vertex per (plane, corner) of every plane that
///     reaches the geometry, the source faces, the explicit source point domain, the <c>xngine.uv16</c> and
///     <c>xngine.plane</c> streams, and the reference triangles reversed for the Y negation.
/// </summary>
/// <remarks>
///     <para>
///         Per vertex: position <c>(X, -Y, Z)</c> of the source point as float32 (exact below 2^24, which every retail
///         coordinate is); normal <c>(Nx, -Ny, Nz) / 256</c> of the plane's authored normal, exact and NOT normalized
///         (the GLB writer normalizes, an Approximated row); color white; texture coordinate the reference UV rule's value
///         <c>/ 16 / size</c> (<see cref="XnGineUvRule" />), computed as the legacy export computes it. Normal mode
///         Vertex, provenance Authored.
///     </para>
///     <para>
///         Faces: one per plane that reaches the geometry, its size the plane's corner count and its corners sequential
///         vertex ordinals, in the order <c>(c0, c(n-1), ..., c1)</c>, which reverses the stored orientation to compensate
///         for the Y negation (<see cref="XnGineModelBasis.WindingRuleId" />). Vertex <c>j</c> of a plane is source corner
///         <see cref="CornerOfVertex" />. Triangles: each reference triangle <c>(A, B, C)</c> is written <c>(A, C, B)</c>.
///         Collinear corners the corner test dropped stay in the face; their vertices are not referenced by a triangle.
///         A plane whose corners name one source point twice (147 drawn retail planes in 60 meshes, 135 of them with a
///         triangle that covers area; slice-5 review receipt <c>measure_review_fixes.json</c>) is written the same way,
///         as one face whose point indices repeat that point. A Blender mesh cannot hold such a face, so Shared's Blender
///         admission omits it (a Dropped <c>faces-repeat-vertex</c> row) and its triangles are lost there, while the GLB
///         draws them. <see cref="XnGineModelGeometryResult.RepeatedPointPlanes" /> lists these planes for the
///         <c>bmt.xngine.uv-rule</c> row and the <c>bmt.xngine.repeated-point-faces</c> diagnostic; how they should
///         reach <c>Faces</c> is an open owner question.
///     </para>
///     <para>
///         Point indices: every primitive of the mesh declares the header's point count and the source point index of
///         each vertex, with the SAME source-domain identity (<see cref="PointDomainId" />, derived from the mesh's
///         point-list identity: container, record and point-list offset; never from sizes or positions), so a point is
///         counted once however many texture keys split it (Shared SA6). Two distinct points at the same position stay
///         distinct: nothing is welded.
///     </para>
///     <para>
///         <c>xngine.uv16</c> (semantic <c>xngine.texel16</c>): FaceCorner domain, Int16 x 2, the STORED u and v per
///         corner in face order, before the unfold and before accumulation (plan D1). <c>xngine.plane</c> (semantic
///         <c>xngine.plane-ordinal</c>): Face domain, Int32, the source plane ordinal, because primitives regroup planes
///         by key and omitted planes leave gaps.
///     </para>
///     <para>
///         A POSED mesh (a Redguard <c>.3DC</c> keyframe, plan section 4, passed with an <see cref="IXnGinePoseStack" />)
///         is built the same way from its keyframe, with two differences (plan decision D4): every vertex normal is
///         (0, 0, 0) with <see cref="SceneNormalMode.Flat" /> and Flat provenance, because a <c>.3DC</c> stores no normal
///         a <c>.3D</c> reader could use and Flat shading derives each pose's directions from its own triangles (Flat
///         forbids normal morph deltas); and every primitive carries the stack's later poses as morph targets in its own
///         vertex domain (<see cref="IXnGinePoseStack.TargetsFor" /> over the vertices' source points).
///     </para>
/// </remarks>
internal static class XnGineModelGeometry
{
    /// <summary>The stored-UV attribute's name.</summary>
    public const string UvAttributeName = "xngine.uv16";

    /// <summary>The stored-UV attribute's semantic.</summary>
    public const string UvAttributeSemantic = "xngine.texel16";

    /// <summary>The plane-ordinal attribute's name.</summary>
    public const string PlaneAttributeName = "xngine.plane";

    /// <summary>The plane-ordinal attribute's semantic.</summary>
    public const string PlaneAttributeSemantic = "xngine.plane-ordinal";

    /// <summary>
    ///     The texel size portable UVs divide by when a texture is not resolved: the legacy export's
    ///     <c>DefaultTextureSize</c>, Assumed. Slice 5 resolves no texture, so every primitive uses it.
    /// </summary>
    public const int FallbackTextureSize = 64;

    /// <summary>The largest coordinate magnitude a float32 holds exactly.</summary>
    private const int ExactFloatLimit = 1 << 24;

    /// <summary>
    ///     The source corner a plane's vertex (face corner) <paramref name="vertex" /> carries, for a plane of
    ///     <paramref name="cornerCount" /> corners: 0 for vertex 0, else <c>n - vertex</c>. The mapping is its own inverse,
    ///     so it also gives the vertex of a source corner.
    /// </summary>
    public static int CornerOfVertex(int cornerCount, int vertex)
    {
        return vertex == 0 ? 0 : cornerCount - vertex;
    }

    /// <summary>
    ///     The source point-domain identity every primitive of one mesh shares (Shared SA6): the container, record and
    ///     point-list offset for an archive entry, else the source occurrence and point-list offset.
    /// </summary>
    public static string PointDomainId(string sourceId, string path, ClassicContainerFacts? container,
        int pointListOffset)
    {
        ArgumentNullException.ThrowIfNull(sourceId);
        ArgumentNullException.ThrowIfNull(path);
        return container is null
            ? string.Create(CultureInfo.InvariantCulture, $"xngine.points:{sourceId}:{path}@{pointListOffset}")
            : string.Create(CultureInfo.InvariantCulture,
                $"xngine.points:{container.Kind}:{container.ContainerName}#{container.EntryIndex}:{container.EntryName}@{pointListOffset}");
    }

    /// <summary>
    ///     Builds the primitives (see the type remarks). <paramref name="planes" /> and <paramref name="rules" /> are per
    ///     plane of <paramref name="mesh" />, which must have been parsed with <see cref="XnGineUvHandling.Stored" />.
    ///     <paramref name="textureSize" /> gives the texel size a key's portable UVs divide by. <paramref name="poses" />
    ///     is the later poses of a posed mesh whose keyframe <paramref name="mesh" /> is (a Redguard <c>.3DC</c>), or null
    ///     for a static mesh (see the type remarks for what it changes).
    /// </summary>
    /// <exception cref="ArgumentException">The per-plane lists do not match the mesh, or the mesh was not parsed in stored-UV mode.</exception>
    /// <exception cref="InvalidDataException">No plane of the mesh yields a triangle, so it has no primitive.</exception>
    public static XnGineModelGeometryResult Build(XnGineMesh mesh, IReadOnlyList<XnGineTriangulatedPlane> planes,
        IReadOnlyList<XnGineUvRuleResult> rules, string pointDomainId,
        Func<XnGineTextureKey, (int Width, int Height)> textureSize, CancellationToken cancellationToken,
        IXnGinePoseStack? poses = null)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(planes);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentException.ThrowIfNullOrWhiteSpace(pointDomainId);
        ArgumentNullException.ThrowIfNull(textureSize);
        if (planes.Count != mesh.Planes.Count || rules.Count != mesh.Planes.Count)
        {
            throw new ArgumentException("One triangulation and one UV rule result per plane are required.", nameof(planes));
        }

        if (mesh.UvHandling != XnGineUvHandling.Stored)
        {
            throw new ArgumentException("The mesh must be parsed with stored UVs (the xngine.uv16 stream).", nameof(mesh));
        }

        var (keys, emptied) = PrimitiveKeys(mesh, planes);
        if (keys.Count == 0)
        {
            throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture,
                $"None of the mesh's {mesh.Planes.Count} planes yields a triangle under the reference triangulation " +
                $"(every texture key is emptied), so the mesh has no primitive to draw."));
        }

        var rounded = mesh.Points.Any(static point =>
            Math.Abs((long)point.X) >= ExactFloatLimit || Math.Abs((long)point.Y) >= ExactFloatLimit ||
            Math.Abs((long)point.Z) >= ExactFloatLimit);
        var primitives = new ScenePrimitive[keys.Count];
        for (var index = 0; index < keys.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            primitives[index] = BuildPrimitive(mesh, planes, rules, keys[index], index, pointDomainId,
                textureSize(keys[index]), poses, cancellationToken);
        }

        var repeated = new List<int>();
        var repeatedWithArea = 0;
        for (var k = 0; k < mesh.Planes.Count; k++)
        {
            if (planes[k].IsOmitted || !RepeatsSourcePoint(mesh.Planes[k]))
            {
                continue;
            }

            repeated.Add(k);
            repeatedWithArea += CoversArea(mesh, mesh.Planes[k], planes[k]) ? 1 : 0;
        }

        return new XnGineModelGeometryResult(primitives, keys, emptied, rounded, repeated, repeatedWithArea);
    }

    /// <summary>
    ///     The primitive keys (first use over all planes, every key that keeps a plane with a triangle) and the emptied
    ///     keys (first use, every key none of whose planes yields a triangle).
    /// </summary>
    public static (IReadOnlyList<XnGineTextureKey> Kept, IReadOnlyList<XnGineTextureKey> Emptied) PrimitiveKeys(
        XnGineMesh mesh, IReadOnlyList<XnGineTriangulatedPlane> planes)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(planes);
        var order = new List<XnGineTextureKey>();
        var drawn = new HashSet<uint>();
        for (var k = 0; k < mesh.Planes.Count; k++)
        {
            var key = XnGineTextureKey.Of(mesh.Planes[k]);
            if (!order.Contains(key))
            {
                order.Add(key);
            }

            if (!planes[k].IsOmitted)
            {
                drawn.Add(key.Key);
            }
        }

        return (order.Where(key => drawn.Contains(key.Key)).ToList(),
            order.Where(key => !drawn.Contains(key.Key)).ToList());
    }

    /// <summary>
    ///     True when two corners of <paramref name="plane" /> name the same source point, so its face's point indices
    ///     repeat that point (see <see cref="XnGineModelGeometryResult.RepeatedPointPlanes" />). Identity is the point
    ///     index, never the position: two distinct points at one position do not repeat.
    /// </summary>
    public static bool RepeatsSourcePoint(XnGinePlane plane)
    {
        ArgumentNullException.ThrowIfNull(plane);
        var seen = new HashSet<int>();
        foreach (var corner in plane.Points)
        {
            if (!seen.Add(corner.PointIndex))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>True when one of the plane's kept triangles covers area (its exact integer cross product is not zero).</summary>
    private static bool CoversArea(XnGineMesh mesh, XnGinePlane plane, XnGineTriangulatedPlane triangulated)
    {
        foreach (var triangle in triangulated.Triangles)
        {
            var cross = XnGineTriangulation.Cross(mesh.Points[plane.Points[triangle.A].PointIndex],
                mesh.Points[plane.Points[triangle.B].PointIndex], mesh.Points[plane.Points[triangle.C].PointIndex]);
            if (cross != (0L, 0L, 0L))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     One primitive: every plane of <paramref name="key" /> that reaches the geometry, in plane order; with
    ///     <paramref name="poses" />, Flat and carrying the later poses as morph targets.
    /// </summary>
    private static ScenePrimitive BuildPrimitive(XnGineMesh mesh, IReadOnlyList<XnGineTriangulatedPlane> planes,
        IReadOnlyList<XnGineUvRuleResult> rules, XnGineTextureKey key, int materialIndex, string pointDomainId,
        (int Width, int Height) size, IXnGinePoseStack? poses, CancellationToken cancellationToken)
    {
        var scaleU = 1f / size.Width;
        var scaleV = 1f / size.Height;
        var vertices = new List<SceneVertex>();
        var points = new List<int>();
        var indices = new List<int>();
        var faceSizes = new List<int>();
        var faceCorners = new List<int>();
        var storedUv = new List<short>();
        var planeOrdinals = new List<int>();
        for (var k = 0; k < mesh.Planes.Count; k++)
        {
            var plane = mesh.Planes[k];
            if (planes[k].IsOmitted || plane.TextureKey != key.Key)
            {
                continue;
            }

            if ((k & 255) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var count = plane.Points.Count;
            var first = vertices.Count;
            var normal = poses is not null
                ? Vector3.Zero
                : new Vector3(plane.Normal.X / XnGineMesh.PointDivisor,
                    -(float)plane.Normal.Y / XnGineMesh.PointDivisor, plane.Normal.Z / XnGineMesh.PointDivisor);
            var rule = rules[k].Values;
            for (var j = 0; j < count; j++)
            {
                var corner = plane.Points[CornerOfVertex(count, j)];
                var point = mesh.Points[corner.PointIndex];
                var uv = rule[CornerOfVertex(count, j)];
                vertices.Add(new SceneVertex(new Vector3(point.X, -(float)point.Y, point.Z), normal, Vector4.One,
                    new Vector2(uv.U / XnGineMesh.TextureDivisor * scaleU, uv.V / XnGineMesh.TextureDivisor * scaleV)));
                points.Add(corner.PointIndex);
                faceCorners.Add(first + j);
                storedUv.Add(checked((short)corner.U));
                storedUv.Add(checked((short)corner.V));
            }

            faceSizes.Add(count);
            planeOrdinals.Add(k);
            foreach (var triangle in planes[k].Triangles)
            {
                indices.Add(first + CornerOfVertex(count, triangle.A));
                indices.Add(first + CornerOfVertex(count, triangle.C));
                indices.Add(first + CornerOfVertex(count, triangle.B));
            }
        }

        var faces = new SceneFaceList(faceSizes, faceCorners);
        var targets = poses?.TargetsFor(points, cancellationToken);
        return new ScenePrimitive(key.MaterialName, vertices, indices, materialIndex, morphTargets: targets,
            normalMode: poses is null ? SceneNormalMode.Vertex : SceneNormalMode.Flat)
        {
            Faces = faces,
            PointIndices = new ScenePointIndices(mesh.Points.Count, points, sourceDomainId: pointDomainId),
            Attributes =
            [
                new SceneAttributeStream(UvAttributeName, UvAttributeSemantic, SceneAttributeDomain.FaceCorner,
                    SceneAttributeComponentType.Int16, 2, faces.CornerCount, Int16Bytes(storedUv)),
                new SceneAttributeStream(PlaneAttributeName, PlaneAttributeSemantic, SceneAttributeDomain.Face,
                    SceneAttributeComponentType.Int32, 1, faces.FaceCount, Int32Bytes(planeOrdinals))
            ],
            NormalProvenance = new SceneNormalProvenance(poses is null
                ? SceneNormalProvenanceKind.Authored
                : SceneNormalProvenanceKind.Flat)
        };
    }

    /// <summary>Little-endian int16 bytes, the storage <see cref="SceneAttributeStream" /> requires.</summary>
    private static byte[] Int16Bytes(List<short> values)
    {
        var bytes = new byte[values.Count * 2];
        for (var i = 0; i < values.Count; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), values[i]);
        }

        return bytes;
    }

    /// <summary>Little-endian int32 bytes, the storage <see cref="SceneAttributeStream" /> requires.</summary>
    private static byte[] Int32Bytes(List<int> values)
    {
        var bytes = new byte[values.Count * 4];
        for (var i = 0; i < values.Count; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(i * 4), values[i]);
        }

        return bytes;
    }
}
