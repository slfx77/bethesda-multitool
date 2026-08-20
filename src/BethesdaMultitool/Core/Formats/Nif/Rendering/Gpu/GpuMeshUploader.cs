using System.Numerics;
using System.Runtime.InteropServices;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Inspection;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu;

/// <summary>
///     API-agnostic conversion from <see cref="RenderableSubmesh" /> to the shared
///     <see cref="GpuVertex" /> layout consumed by the D3D12 renderers. GPU buffer creation
///     lives in <c>GpuMeshBufferFactory12</c>.
/// </summary>
internal static class GpuMeshUploader
{
    /// <summary>Interleaved <see cref="GpuVertex" /> stride in bytes (Sequential layout, packed floats).</summary>
    public const int GpuVertexSize = 72;

    /// <summary>
    ///     Converts a <see cref="RenderableSubmesh" /> to a GPU vertex array.
    /// </summary>
    public static GpuVertex[] BuildVertices(
        RenderableSubmesh sub,
        bool preserveAuthoredVertexAlpha = false)
    {
        var vertexCount = sub.VertexCount;
        var vertices = new GpuVertex[vertexCount];

        for (var i = 0; i < vertexCount; i++)
        {
            var pi = i * 3;
            var uvi = i * 2;
            var ci = i * 4;

            vertices[i].Position = new Vector3(sub.Positions[pi], sub.Positions[pi + 1], sub.Positions[pi + 2]);

            if (sub.Normals != null && pi + 2 < sub.Normals.Length)
                vertices[i].Normal = new Vector3(sub.Normals[pi], sub.Normals[pi + 1], sub.Normals[pi + 2]);

            if (sub.UVs != null && uvi + 1 < sub.UVs.Length)
                vertices[i].TexCoord = new Vector2(sub.UVs[uvi], sub.UVs[uvi + 1]);

            if ((NifVertexColorPolicy.HasVertexColorData(sub) || preserveAuthoredVertexAlpha) &&
                sub.VertexColors != null &&
                ci + 3 < sub.VertexColors.Length)
            {
                var color = NifVertexColorPolicy.Read(sub, i);
                vertices[i].VertexColor = new Vector4(
                    color.R / 255f,
                    color.G / 255f,
                    color.B / 255f,
                    // The ordinary upload path keeps policy-normalized coverage alpha. The
                    // reference renderer opts into the raw channel only for an explicitly
                    // identified TallGrassShaderProperty, whose VS consumes it as wind weight
                    // and resets the outgoing coverage alpha before any pixel shader sees it.
                    preserveAuthoredVertexAlpha ? sub.VertexColors[ci + 3] / 255f : color.A / 255f);
            }
            else
            {
                vertices[i].VertexColor = Vector4.One;
            }

            if (sub.Tangents != null && pi + 2 < sub.Tangents.Length)
                vertices[i].Tangent = new Vector3(sub.Tangents[pi], sub.Tangents[pi + 1], sub.Tangents[pi + 2]);

            if (sub.Bitangents != null && pi + 2 < sub.Bitangents.Length)
                vertices[i].Bitangent = new Vector3(sub.Bitangents[pi], sub.Bitangents[pi + 1], sub.Bitangents[pi + 2]);
        }

        return vertices;
    }

    /// <summary>
    ///     GPU vertex layout: position(12) + normal(12) + texcoord(8) + color(16) + tangent(12) + bitangent(12) = 72 bytes.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct GpuVertex
    {
        public Vector3 Position;
        public Vector3 Normal;
        public Vector2 TexCoord;
        public Vector4 VertexColor;
        public Vector3 Tangent;
        public Vector3 Bitangent;
    }
}
