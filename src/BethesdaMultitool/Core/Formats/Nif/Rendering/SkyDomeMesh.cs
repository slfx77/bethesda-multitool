using System.Numerics;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering;

/// <summary>
///     Procedural celestial-sphere ("sky dome") mesh — a unit UV-sphere whose vertices are world-space
///     directions (Z up, matching the AtmosphereState sun frame). The sky renderer centers it on the
///     camera (world = camPos + dir·radius) and projects it with the absolute viewProj, so the gradient,
///     stars and clouds map onto real geometry oriented in WORLD space (turning the camera sweeps a
///     fixed sky, not a screen overlay) instead of a fullscreen-triangle stereographic projection.
///     <para>
///         A full sphere (nadir → zenith) rather than a hemisphere guarantees the dome always covers the
///         whole view — the camera sits at the centre — so there is never an uncovered band when looking
///         toward or below the horizon. UVs are lat-long (u = azimuth, v = elevation), the layout the
///         engine's cloud/star sky textures are authored for, so the texture wraps around the horizon and
///         stacks toward the zenith the way the source art intends.
///     </para>
///     <para>
///         Pure CPU + <see cref="System.Numerics" />, no GPU dependency, so the geometry + UV + winding
///         are unit-testable without a device.
///     </para>
/// </summary>
internal static class SkyDomeMesh
{
    /// <summary>One sky-dome vertex: a unit world-space direction + its lat-long texture coordinate.</summary>
    internal readonly record struct Vertex(Vector3 Direction, Vector2 Uv);

    /// <summary>The generated dome geometry: unit-direction vertices + 16-bit triangle indices.</summary>
    internal sealed record Mesh(Vertex[] Vertices, ushort[] Indices);

    /// <summary>
    ///     Generates a UV-sphere dome with <paramref name="rings" /> latitude bands and
    ///     <paramref name="segments" /> longitude slices. Vertex count is
    ///     <c>(rings + 1) · (segments + 1)</c> (the seam longitude is duplicated so the U coordinate runs
    ///     0→1 without a wrap discontinuity); index count is <c>rings · segments · 6</c>. Winding is
    ///     inward (triangle normals point toward the centre) so the inside of the sphere — the side the
    ///     camera sees — faces front; the renderer draws with culling off regardless.
    /// </summary>
    internal static Mesh Generate(int rings = 24, int segments = 48)
    {
        rings = Math.Max(2, rings);
        segments = Math.Max(3, segments);

        var vertices = new Vertex[(rings + 1) * (segments + 1)];
        var vi = 0;
        for (var r = 0; r <= rings; r++)
        {
            // v: 0 at the nadir (−Z), 1 at the zenith (+Z). Elevation θ = −π/2 → +π/2.
            var v = (float)r / rings;
            var elevation = (v - 0.5f) * MathF.PI;
            var cosE = MathF.Cos(elevation);
            var sinE = MathF.Sin(elevation);
            for (var s = 0; s <= segments; s++)
            {
                var u = (float)s / segments;
                var azimuth = u * MathF.Tau;
                var dir = new Vector3(MathF.Cos(azimuth) * cosE, MathF.Sin(azimuth) * cosE, sinE);
                // The poles collapse to a point; normalize defends against tiny float drift only.
                vertices[vi++] = new Vertex(Normalize(dir), new Vector2(u, v));
            }
        }

        var indices = new ushort[rings * segments * 6];
        var ii = 0;
        var stride = segments + 1;
        for (var r = 0; r < rings; r++)
        {
            for (var s = 0; s < segments; s++)
            {
                var i0 = (ushort)(r * stride + s);
                var i1 = (ushort)(r * stride + s + 1);
                var i2 = (ushort)((r + 1) * stride + s);
                var i3 = (ushort)((r + 1) * stride + s + 1);
                // Inward-facing winding (toward the sphere centre / camera).
                indices[ii++] = i0;
                indices[ii++] = i2;
                indices[ii++] = i1;
                indices[ii++] = i1;
                indices[ii++] = i2;
                indices[ii++] = i3;
            }
        }

        return new Mesh(vertices, indices);
    }

    private static Vector3 Normalize(Vector3 v)
    {
        var lenSq = v.LengthSquared();
        return lenSq > 1e-12f ? v / MathF.Sqrt(lenSq) : new Vector3(0f, 0f, 1f);
    }
}
