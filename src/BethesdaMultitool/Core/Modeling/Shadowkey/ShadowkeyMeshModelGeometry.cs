using System.Globalization;
using System.Numerics;
using BethesdaMultitool.Core.Formats.Travels.Shadowkey;
using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Shadowkey;

/// <summary>The geometry of one Shadowkey mesh record over its UV vertex domain (<see cref="ShadowkeyMeshModelGeometry.Build" />).</summary>
/// <param name="Vertices">One vertex per UV index, in stored UV order.</param>
/// <param name="Indices">The stored UV index triples of the faces, as stored.</param>
/// <param name="Owners">The owner record vertex of every UV-domain vertex.</param>
/// <param name="PointCount">The record's vertex count (unused vertices included).</param>
/// <param name="Targets">Frames 1 to N-1 as absolute morph targets, or empty.</param>
internal sealed record ShadowkeyMeshModelGeometryResult(
    IReadOnlyList<SceneVertex> Vertices,
    IReadOnlyList<int> Indices,
    IReadOnlyList<int> Owners,
    int PointCount,
    IReadOnlyList<SceneMorphTarget> Targets);

/// <summary>
///     Whether a record's frame 0 is a closed oriented surface and its signed volume
///     (<see cref="ShadowkeyMeshModelGeometry.Winding" />).
/// </summary>
/// <param name="Closed">
///     True when, over the faces with three distinct vertex indices, every directed edge occurs once and its reverse
///     once, either by vertex index or with the vertices welded by frame-0 position (the plan's measurement rule).
/// </param>
/// <param name="SignedVolumeX6">
///     Six times the signed volume, <c>sum of p . (q x r)</c> over the same faces in stored (v0, v1, v2) order, exact
///     integers; positive is counter-clockwise seen from outside under the right-handed basis.
/// </param>
internal readonly record struct ShadowkeyMeshWinding(bool Closed, long SignedVolumeX6)
{
    /// <summary>True for a closed surface wound clockwise seen from outside (4 of the 79 closed retail records).</summary>
    public bool IsReversed => Closed && SignedVolumeX6 < 0;
}

/// <summary>
///     The geometry of a Shadowkey mesh document (cut-2 plan section 3.2, decision D2): the UV list is the primitive's
///     vertex domain.
/// </summary>
/// <remarks>
///     <para>
///         A face stores three vertex indices and three UV indices, and on 226 of 226 retail records every UV index
///         belongs to exactly one vertex (the transposed claim holds on 0 records). So vertex i of the primitive is UV
///         index i: its position is frame 0 of the owning record vertex (exact integers), its texture coordinate is
///         <c>fl32(U) / fl32(256 x width)</c> and <c>fl32(V) / fl32(256 x height)</c>, one correctly rounded division
///         each (exact on every retail texture, whose sides are powers of two), values past 1 kept (UVs reach 7.875 x
///         the texture and the sampler repeats); the triangles are the stored UV index triples; and
///         <see cref="ScenePointIndices" /> carries the owner of every vertex, so both source index spaces survive with
///         no welding by value. Unused record vertices stay in the point domain's count; no UV names them, so their
///         positions travel in native state instead (<see cref="UnusedVertices" />, the census element
///         <c>unused-vertices</c>, NativeOnly).
///     </para>
///     <para>
///         No normals are stored: every vertex normal is zero with <see cref="SceneNormalMode.Flat" /> and a Flat
///         <see cref="SceneNormalProvenance" /> (the cut-1c rule for absent normals); the color is white. An animated
///         record's frames 1 to N-1 become absolute morph targets over the same domain, the owner positions of each
///         frame, exact integers; frame 0 is the base.
///     </para>
///     <para>
///         The reader refuses (invalid data) a UV index owned by two vertices or used by no face (0 retail), which is
///         what makes the domain well defined.
///     </para>
/// </remarks>
internal static class ShadowkeyMeshModelGeometry
{
    /// <summary>The owner record vertex of every UV index, from the faces in stored order.</summary>
    /// <exception cref="InvalidDataException">A UV index is owned by two vertices or used by no face.</exception>
    public static int[] UvOwners(ShadowkeyMesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        var owners = new int[mesh.Uvs.Count];
        Array.Fill(owners, -1);
        for (var f = 0; f < mesh.Faces.Count; f++)
        {
            var face = mesh.Faces[f];
            Own(mesh, owners, f, face.T0, face.V0);
            Own(mesh, owners, f, face.T1, face.V1);
            Own(mesh, owners, f, face.T2, face.V2);
        }

        for (var uv = 0; uv < owners.Length; uv++)
        {
            if (owners[uv] < 0)
            {
                throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture,
                    $"'{mesh.Name}': UV {uv} is used by no face, so the UV vertex domain gives it no position."));
            }
        }

        return owners;
    }

    /// <summary>The frame-<paramref name="frame" /> positions of the owners, in UV-domain order (exact integers).</summary>
    public static Vector3[] OwnerPositions(ShadowkeyMesh mesh, IReadOnlyList<int> owners, int frame)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(owners);
        var positions = new Vector3[owners.Count];
        var frameBase = frame * mesh.VertexCount;
        for (var i = 0; i < owners.Count; i++)
        {
            positions[i] = mesh.Vertices[frameBase + owners[i]].ToVector3();
        }

        return positions;
    }

    /// <summary>
    ///     The record vertices no face names, in index order (cut-2 review finding 3). They have no UV, so the UV vertex
    ///     domain carries none of their positions; <see cref="ShadowkeyModelNativeState.Mesh" /> keeps them per frame.
    ///     Retail: umbra 22 (over 157 frames), jelly 4, umbrastat 1.
    /// </summary>
    public static IReadOnlyList<int> UnusedVertices(ShadowkeyMesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        var used = new bool[mesh.VertexCount];
        foreach (var face in mesh.Faces)
        {
            used[face.V0] = true;
            used[face.V1] = true;
            used[face.V2] = true;
        }

        var unused = new List<int>();
        for (var vertex = 0; vertex < used.Length; vertex++)
        {
            if (!used[vertex])
            {
                unused.Add(vertex);
            }
        }

        return unused.AsReadOnly();
    }

    /// <summary>
    ///     The closed-surface test and the signed volume of frame 0 (<see cref="ShadowkeyMeshWinding" />), the rule the
    ///     plan measured the 79 closed retail records with (75 positive; door_5units_right, door_7units_right, woodenbucket
    ///     and ax negative). Faces with a repeated vertex index are skipped.
    /// </summary>
    public static ShadowkeyMeshWinding Winding(ShadowkeyMesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        var directed = new Dictionary<(int, int), int>();
        var welded = new Dictionary<(ShadowkeyVertex, ShadowkeyVertex), int>();
        long volume = 0;
        foreach (var face in mesh.Faces)
        {
            int a = face.V0, b = face.V1, c = face.V2;
            if (a == b || b == c || a == c)
            {
                continue;
            }

            var p = mesh.Vertices[a];
            var q = mesh.Vertices[b];
            var r = mesh.Vertices[c];
            volume += (long)p.X * ((long)q.Y * r.Z - (long)q.Z * r.Y) - (long)p.Y * ((long)q.X * r.Z - (long)q.Z * r.X) +
                      (long)p.Z * ((long)q.X * r.Y - (long)q.Y * r.X);
            Count(directed, (a, b));
            Count(directed, (b, c));
            Count(directed, (c, a));
            if (p != q && q != r && p != r)
            {
                Count(welded, (p, q));
                Count(welded, (q, r));
                Count(welded, (r, p));
            }
        }

        return new ShadowkeyMeshWinding(IsClosed(directed) || IsClosed(welded), volume);
    }

    /// <summary>The texture coordinate of one stored UV: one correctly rounded float32 division per axis.</summary>
    public static Vector2 TexCoord(ShadowkeyUv uv, int width, int height)
    {
        return new Vector2(uv.U / (256f * width), uv.V / (256f * height));
    }

    /// <summary>The morph target name of frame <paramref name="frame" /> (1-based over the record's frames).</summary>
    public static string TargetName(int frame)
    {
        return string.Create(CultureInfo.InvariantCulture, $"frame {frame:000}");
    }

    /// <summary>Builds the UV-domain geometry, with frames 1 to N-1 as targets when <paramref name="includeTargets" />.</summary>
    public static ShadowkeyMeshModelGeometryResult Build(ShadowkeyMesh mesh, bool includeTargets,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        var owners = UvOwners(mesh);
        var basePositions = OwnerPositions(mesh, owners, 0);
        var vertices = new SceneVertex[owners.Length];
        for (var i = 0; i < vertices.Length; i++)
        {
            if ((i & 1023) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            vertices[i] = new SceneVertex(basePositions[i], Vector3.Zero, Vector4.One,
                TexCoord(mesh.Uvs[i], mesh.Textures.Width, mesh.Textures.Height));
        }

        var indices = new int[mesh.Faces.Count * 3];
        for (var f = 0; f < mesh.Faces.Count; f++)
        {
            var face = mesh.Faces[f];
            indices[f * 3] = face.T0;
            indices[f * 3 + 1] = face.T1;
            indices[f * 3 + 2] = face.T2;
        }

        var targets = new List<SceneMorphTarget>();
        if (includeTargets)
        {
            for (var frame = 1; frame < mesh.FrameCount; frame++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                targets.Add(new SceneMorphTarget(TargetName(frame), Array.Empty<Vector3>(),
                    absolutePositions: OwnerPositions(mesh, owners, frame)));
            }
        }

        return new ShadowkeyMeshModelGeometryResult(vertices, indices, owners, mesh.VertexCount, targets.AsReadOnly());
    }

    /// <summary>
    ///     One primitive over the geometry, bound to <paramref name="materialIndex" />: flat, with the point domain and the
    ///     shared target objects (a multi-skin record's primitives share them by reference).
    /// </summary>
    public static ScenePrimitive Primitive(string name, ShadowkeyMeshModelGeometryResult geometry, int materialIndex)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(geometry);
        return new ScenePrimitive(name, geometry.Vertices, geometry.Indices, materialIndex,
            morphTargets: geometry.Targets, normalMode: SceneNormalMode.Flat)
        {
            PointIndices = new ScenePointIndices(geometry.PointCount, geometry.Owners),
            NormalProvenance = new SceneNormalProvenance(SceneNormalProvenanceKind.Flat)
        };
    }

    /// <summary>Counts one directed edge.</summary>
    private static void Count<T>(Dictionary<(T, T), int> edges, (T, T) edge)
        where T : notnull
    {
        edges[edge] = edges.GetValueOrDefault(edge) + 1;
    }

    /// <summary>True when there is an edge and every directed edge occurs once with its reverse once.</summary>
    private static bool IsClosed<T>(Dictionary<(T, T), int> edges)
        where T : notnull
    {
        if (edges.Count == 0)
        {
            return false;
        }

        foreach (var pair in edges)
        {
            var (start, end) = pair.Key;
            if (pair.Value != 1 || edges.GetValueOrDefault((end, start)) != 1)
            {
                return false;
            }
        }

        return true;
    }

    private static void Own(ShadowkeyMesh mesh, int[] owners, int face, int uv, int vertex)
    {
        if (owners[uv] < 0)
        {
            owners[uv] = vertex;
            return;
        }

        if (owners[uv] != vertex)
        {
            throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture,
                $"'{mesh.Name}': UV {uv} is used by vertex {owners[uv]} and, in face {face}, by vertex {vertex}; the UV " +
                $"vertex domain needs every UV owned by one vertex (226 of 226 retail records)."));
        }
    }
}
