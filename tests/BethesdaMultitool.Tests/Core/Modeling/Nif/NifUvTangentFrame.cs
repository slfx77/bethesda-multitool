using System.Numerics;
using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Tests.Core.Modeling.Nif;

/// <summary>
///     The per-vertex UV frame of a primitive, from its positions, normals, UV set 0 and triangles alone: the direction
///     of increasing U (dP/du) and of increasing V (dP/dv) in each vertex's tangent plane, and the handedness glTF asks
///     of a tangent there. It never reads a stored tangent, so it can judge one. A C# transcription of the independent
///     measurement's <c>uv_frame</c> (TestOutput/nif-tangent-frame-20260928/measure/nif_tangent_frame.py), with its
///     thresholds and exclusion classes, so a cover census here scores the same vertices MEASURE.md reports.
/// </summary>
/// <remarks>
///     <para>
///         Per kept triangle with finite values: e1 = p1 - p0, e2 = p2 - p0, d1 = uv1 - uv0, d2 = uv2 - uv0,
///         det = d1.u d2.v - d2.u d1.v; dP/du = (e1 d2.v - e2 d1.v) / det and dP/dv = (e2 d1.u - e1 d2.u) / det. A
///         triangle is UV-degenerate when |det| is not above <see cref="UvDegenerateRelative" /> |d1| |d2| and zero-area when
///         |e1 x e2| is not above <see cref="ZeroAreaRelative" /> |e1| |e2|; neither contributes. Each corner accumulates
///         the unit derivatives weighted by the triangle's area (all first corners in triangle order, then all second,
///         then all third, as the measurement's numpy scatter-add does), and counts the orientation
///         sign(dot(dP/du x dP/dv, N)). The sums are projected into the plane of the unit normal and normalized.
///     </para>
///     <para>
///         Convention (glTF's, and the Shared contract's): TEXCOORD is top-left origin with V running down the image, NIF
///         UVs are passed through unflipped, and glTF's normal texture is +X right, +Y up. So a glTF tangent runs along
///         +dP/du and its bitangent cross(N, T) * w along -dP/dv (up the image), which makes the handedness from the UVs
///         <c>w = sign(dot(cross(N, dP/du), -dP/dv))</c>: -1 where the incident triangles' orientation is positive, else
///         +1. The DirectX sense (bitangent along +dP/dv) is the negation.
///     </para>
///     <para>
///         Classes (a vertex is scored only when <see cref="VertexClass.Valid" />): unreferenced by a kept triangle; every
///         incident triangle UV-degenerate or zero-area; a normal shorter than <see cref="ZeroVector" />; incident
///         triangles of both orientations (a mirror seam through the vertex); no triangle with a non-zero orientation;
///         a cancelled accumulated derivative (tangent-plane length not above <see cref="CancelledRelative" /> times the
///         incident area).
///     </para>
/// </remarks>
internal sealed class NifUvTangentFrame
{
    /// <summary>The |cos| above which a direction is said to run along a reference direction.</summary>
    public const double Align = 0.7;

    /// <summary>A triangle is UV-degenerate when |det| is not above this times |d1| |d2|.</summary>
    public const double UvDegenerateRelative = 1e-7;

    /// <summary>A triangle is zero-area when |e1 x e2| is not above this times |e1| |e2|.</summary>
    public const double ZeroAreaRelative = 1e-10;

    /// <summary>An accumulated derivative is cancelled when its tangent-plane length is not above this times the area sum.</summary>
    public const double CancelledRelative = 1e-3;

    /// <summary>A vector (or normal) not longer than this is zero.</summary>
    public const double ZeroVector = 1e-6;

    private readonly double[] _normals;
    private readonly double[] _u;
    private readonly double[] _v;
    private readonly int[] _gltfW;
    private readonly VertexClass[] _classes;

    private NifUvTangentFrame(double[] normals, double[] u, double[] v, int[] gltfW, VertexClass[] classes)
    {
        _normals = normals;
        _u = u;
        _v = v;
        _gltfW = gltfW;
        _classes = classes;
    }

    /// <summary>Why a vertex is or is not scored.</summary>
    public enum VertexClass
    {
        /// <summary>Scored.</summary>
        Valid,

        /// <summary>No kept triangle references the vertex.</summary>
        Unreferenced,

        /// <summary>Every incident triangle is UV-degenerate, zero-area or not finite.</summary>
        DegenerateUv,

        /// <summary>The vertex normal is zero.</summary>
        ZeroNormal,

        /// <summary>Incident triangles of both UV orientations (a mirror seam through the vertex).</summary>
        MirrorSeam,

        /// <summary>No incident triangle has a non-zero orientation against the normal.</summary>
        EdgeOn,

        /// <summary>An accumulated derivative cancelled out.</summary>
        Cancelled
    }

    /// <summary>The vertex count.</summary>
    public int VertexCount => _classes.Length;

    /// <summary>The class of every vertex.</summary>
    public IReadOnlyList<VertexClass> Classes => _classes;

    /// <summary>Computes the frame of one primitive.</summary>
    /// <param name="vertices">Positions, normals and UV set 0.</param>
    /// <param name="indices">The kept triangles, three indices each.</param>
    public static NifUvTangentFrame Compute(IReadOnlyList<SceneVertex> vertices, IReadOnlyList<int> indices)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        ArgumentNullException.ThrowIfNull(indices);
        var count = vertices.Count;
        var normals = new double[count * 3];
        var normalLength = new double[count];
        for (var i = 0; i < count; i++)
        {
            var n = vertices[i].Normal;
            var (x, y, z, length) = Unit(n.X, n.Y, n.Z);
            (normals[i * 3], normals[i * 3 + 1], normals[i * 3 + 2]) = (x, y, z);
            normalLength[i] = length;
        }

        var triangles = indices.Count / 3;
        var references = new int[count];
        var ok = new bool[triangles];
        var uWeighted = new double[triangles * 3];
        var vWeighted = new double[triangles * 3];
        var orientation = new double[triangles * 3];
        var areas = new double[triangles];
        for (var t = 0; t < triangles; t++)
        {
            int a = indices[t * 3], b = indices[t * 3 + 1], c = indices[t * 3 + 2];
            references[a]++;
            references[b]++;
            references[c]++;
            var p0 = vertices[a].Position;
            var p1 = vertices[b].Position;
            var p2 = vertices[c].Position;
            var uv0 = vertices[a].TexCoord;
            var uv1 = vertices[b].TexCoord;
            var uv2 = vertices[c].TexCoord;
            double e1x = (double)p1.X - p0.X, e1y = (double)p1.Y - p0.Y, e1z = (double)p1.Z - p0.Z;
            double e2x = (double)p2.X - p0.X, e2y = (double)p2.Y - p0.Y, e2z = (double)p2.Z - p0.Z;
            double d1u = (double)uv1.X - uv0.X, d1v = (double)uv1.Y - uv0.Y;
            double d2u = (double)uv2.X - uv0.X, d2v = (double)uv2.Y - uv0.Y;
            var det = d1u * d2v - d2u * d1v;
            var (fx, fy, fz) = Cross(e1x, e1y, e1z, e2x, e2y, e2z);
            var area2 = Math.Sqrt(fx * fx + fy * fy + fz * fz);
            if (!double.IsFinite(det) || !double.IsFinite(area2))
            {
                continue;
            }

            var uvDegenerate = !(Math.Abs(det) > UvDegenerateRelative * Math.Sqrt(d1u * d1u + d1v * d1v) *
                Math.Sqrt(d2u * d2u + d2v * d2v));
            var zeroArea = !(area2 > ZeroAreaRelative * Math.Sqrt(e1x * e1x + e1y * e1y + e1z * e1z) *
                Math.Sqrt(e2x * e2x + e2y * e2y + e2z * e2z));
            if (uvDegenerate || zeroArea)
            {
                continue;
            }

            ok[t] = true;
            var inverse = 1.0 / det;
            double dux = (e1x * d2v - e2x * d1v) * inverse, duy = (e1y * d2v - e2y * d1v) * inverse,
                duz = (e1z * d2v - e2z * d1v) * inverse;
            double dvx = (e2x * d1u - e1x * d2u) * inverse, dvy = (e2y * d1u - e1y * d2u) * inverse,
                dvz = (e2z * d1u - e1z * d2u) * inverse;
            var area = 0.5 * area2;
            areas[t] = area;
            var (ux, uy, uz, _) = Unit(dux, duy, duz);
            var (vx, vy, vz, _) = Unit(dvx, dvy, dvz);
            (uWeighted[t * 3], uWeighted[t * 3 + 1], uWeighted[t * 3 + 2]) = (ux * area, uy * area, uz * area);
            (vWeighted[t * 3], vWeighted[t * 3 + 1], vWeighted[t * 3 + 2]) = (vx * area, vy * area, vz * area);
            (orientation[t * 3], orientation[t * 3 + 1], orientation[t * 3 + 2]) = Cross(dux, duy, duz, dvx, dvy, dvz);
        }

        var uSum = new double[count * 3];
        var vSum = new double[count * 3];
        var areaSum = new double[count];
        var contributing = new int[count];
        var positive = new int[count];
        var negative = new int[count];
        for (var corner = 0; corner < 3; corner++)
        {
            for (var t = 0; t < triangles; t++)
            {
                if (!ok[t])
                {
                    continue;
                }

                var k = indices[t * 3 + corner];
                for (var c = 0; c < 3; c++)
                {
                    uSum[k * 3 + c] += uWeighted[t * 3 + c];
                    vSum[k * 3 + c] += vWeighted[t * 3 + c];
                }

                areaSum[k] += areas[t];
                contributing[k]++;
                var s = orientation[t * 3] * normals[k * 3] + orientation[t * 3 + 1] * normals[k * 3 + 1] +
                        orientation[t * 3 + 2] * normals[k * 3 + 2];
                if (s > 0)
                {
                    positive[k]++;
                }
                else if (s < 0)
                {
                    negative[k]++;
                }
            }
        }

        var u = new double[count * 3];
        var v = new double[count * 3];
        var gltfW = new int[count];
        var classes = new VertexClass[count];
        for (var i = 0; i < count; i++)
        {
            double nx = normals[i * 3], ny = normals[i * 3 + 1], nz = normals[i * 3 + 2];
            var (ux, uy, uz, uLength) = Projected(uSum, i, nx, ny, nz);
            var (vx, vy, vz, vLength) = Projected(vSum, i, nx, ny, nz);
            (u[i * 3], u[i * 3 + 1], u[i * 3 + 2]) = (ux, uy, uz);
            (v[i * 3], v[i * 3 + 1], v[i * 3 + 2]) = (vx, vy, vz);
            gltfW[i] = positive[i] > 0 ? -1 : 1;
            classes[i] = Classify(references[i], contributing[i], normalLength[i], positive[i], negative[i],
                uLength > CancelledRelative * areaSum[i] && vLength > CancelledRelative * areaSum[i]);
        }

        return new NifUvTangentFrame(normals, u, v, gltfW, classes);
    }

    private static VertexClass Classify(int references, int contributing, double normalLength, int positive,
        int negative, bool derivativesKept)
    {
        if (references == 0)
        {
            return VertexClass.Unreferenced;
        }

        if (contributing == 0)
        {
            return VertexClass.DegenerateUv;
        }

        if (!(normalLength > ZeroVector))
        {
            return VertexClass.ZeroNormal;
        }

        if (positive > 0 && negative > 0)
        {
            return VertexClass.MirrorSeam;
        }

        if (positive == 0 && negative == 0)
        {
            return VertexClass.EdgeOn;
        }

        return derivativesKept ? VertexClass.Valid : VertexClass.Cancelled;
    }

    /// <summary>The glTF handedness the UVs ask of a tangent at <paramref name="vertex" />: -1 or +1.</summary>
    public int GltfW(int vertex)
    {
        return _gltfW[vertex];
    }

    /// <summary>True when the vertex is scored.</summary>
    public bool IsValid(int vertex)
    {
        return _classes[vertex] == VertexClass.Valid;
    }

    /// <summary>The unit +dP/du in the vertex's tangent plane.</summary>
    public Vector3 DirectionOfU(int vertex)
    {
        return new Vector3((float)_u[vertex * 3], (float)_u[vertex * 3 + 1], (float)_u[vertex * 3 + 2]);
    }

    /// <summary>The unit +dP/dv in the vertex's tangent plane.</summary>
    public Vector3 DirectionOfV(int vertex)
    {
        return new Vector3((float)_v[vertex * 3], (float)_v[vertex * 3 + 1], (float)_v[vertex * 3 + 2]);
    }

    /// <summary>The cosine between a direction (normalized here, in double) and +dP/du; NaN for a zero direction.</summary>
    public double CosineToU(int vertex, Vector3 direction)
    {
        return Cosine(_u, vertex, direction);
    }

    /// <summary>The cosine between a direction (normalized here, in double) and +dP/dv; NaN for a zero direction.</summary>
    public double CosineToV(int vertex, Vector3 direction)
    {
        return Cosine(_v, vertex, direction);
    }

    /// <summary>
    ///     The bitangent a glTF consumer rebuilds from a tangent, cross(N, T') * w with T' the tangent projected into the
    ///     vertex's tangent plane and normalized; zero when the tangent has no tangent-plane component.
    /// </summary>
    public Vector3 GltfBitangent(int vertex, Vector4 tangent)
    {
        double nx = _normals[vertex * 3], ny = _normals[vertex * 3 + 1], nz = _normals[vertex * 3 + 2];
        double tx = tangent.X, ty = tangent.Y, tz = tangent.Z;
        var dot = tx * nx + ty * ny + tz * nz;
        var (px, py, pz, _) = Unit(tx - nx * dot, ty - ny * dot, tz - nz * dot);
        var (bx, by, bz) = Cross(nx, ny, nz, px, py, pz);
        return new Vector3((float)(bx * tangent.W), (float)(by * tangent.W), (float)(bz * tangent.W));
    }

    private static double Cosine(double[] reference, int vertex, Vector3 direction)
    {
        var (x, y, z, length) = Unit(direction.X, direction.Y, direction.Z);
        return length > 0
            ? x * reference[vertex * 3] + y * reference[vertex * 3 + 1] + z * reference[vertex * 3 + 2]
            : double.NaN;
    }

    private static (double X, double Y, double Z, double Length) Projected(double[] sums, int i, double nx, double ny,
        double nz)
    {
        double x = sums[i * 3], y = sums[i * 3 + 1], z = sums[i * 3 + 2];
        var dot = x * nx + y * ny + z * nz;
        return Unit(x - nx * dot, y - ny * dot, z - nz * dot);
    }

    private static (double X, double Y, double Z, double Length) Unit(double x, double y, double z)
    {
        var length = Math.Sqrt(x * x + y * y + z * z);
        return length > 0 ? (x / length, y / length, z / length, length) : (0, 0, 0, length);
    }

    private static (double X, double Y, double Z) Cross(double ax, double ay, double az, double bx, double by,
        double bz)
    {
        return (ay * bz - az * by, az * bx - ax * bz, ax * by - ay * bx);
    }
}
