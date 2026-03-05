using System.Numerics;
using System.Runtime.InteropServices;
using Veldrid;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu;

/// <summary>
///     Converts <see cref="RenderableSubmesh" /> data into Veldrid vertex and index buffers.
/// </summary>
internal static class GpuMeshUploader
{
    /// <summary>
    ///     Veldrid vertex layout description matching <see cref="GpuVertex" />.
    /// </summary>
    public static readonly VertexLayoutDescription VertexLayout = new(
        new VertexElementDescription("aPosition", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float3),
        new VertexElementDescription("aNormal", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float3),
        new VertexElementDescription("aTexCoord", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float2),
        new VertexElementDescription("aVertexColor", VertexElementSemantic.TextureCoordinate,
            VertexElementFormat.Float4),
        new VertexElementDescription("aTangent", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float3),
        new VertexElementDescription("aBitangent", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float3)
    );

    /// <summary>
    ///     Converts a <see cref="RenderableSubmesh" /> to a GPU vertex array.
    /// </summary>
    public static GpuVertex[] BuildVertices(RenderableSubmesh sub)
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

            if (sub.VertexColors != null && ci + 3 < sub.VertexColors.Length)
            {
                vertices[i].VertexColor = new Vector4(
                    sub.VertexColors[ci] / 255f,
                    sub.VertexColors[ci + 1] / 255f,
                    sub.VertexColors[ci + 2] / 255f,
                    sub.VertexColors[ci + 3] / 255f);
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
    ///     Creates a Veldrid vertex buffer from GPU vertices.
    /// </summary>
    public static DeviceBuffer CreateVertexBuffer(GraphicsDevice device, GpuVertex[] vertices)
    {
        var size = (uint)(vertices.Length * Marshal.SizeOf<GpuVertex>());
        var buffer = device.ResourceFactory.CreateBuffer(new BufferDescription(size, BufferUsage.VertexBuffer));
        device.UpdateBuffer(buffer, 0, vertices);
        return buffer;
    }

    /// <summary>
    ///     Creates a Veldrid index buffer from triangle indices.
    /// </summary>
    public static DeviceBuffer CreateIndexBuffer(GraphicsDevice device, ushort[] indices)
    {
        var size = (uint)(indices.Length * sizeof(ushort));
        var buffer = device.ResourceFactory.CreateBuffer(new BufferDescription(size, BufferUsage.IndexBuffer));
        device.UpdateBuffer(buffer, 0, indices);
        return buffer;
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
