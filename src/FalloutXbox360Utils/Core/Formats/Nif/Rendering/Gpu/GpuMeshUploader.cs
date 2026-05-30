using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu;

/// <summary>
///     Converts <see cref="RenderableSubmesh" /> data into D3D11 vertex and index buffers.
/// </summary>
internal static class GpuMeshUploader
{
    /// <summary>
    ///     D3D11 input element layout matching <see cref="GpuVertex" />.
    ///     Semantic name is "TEXCOORD" with indices 0..5 — same packing as the prior
    ///     Veldrid <c>VertexElementSemantic.TextureCoordinate</c> binding so the HLSL
    ///     vertex shader inputs (TEXCOORD0..5) map slot-for-slot to the C# struct.
    /// </summary>
    public static readonly InputElementDescription[] InputElements =
    [
        new("TEXCOORD", 0, Format.R32G32B32_Float,    0, 0), // aPosition    (vec3)
        new("TEXCOORD", 1, Format.R32G32B32_Float,   12, 0), // aNormal      (vec3)
        new("TEXCOORD", 2, Format.R32G32_Float,      24, 0), // aTexCoord    (vec2)
        new("TEXCOORD", 3, Format.R32G32B32A32_Float, 32, 0), // aVertexColor (vec4)
        new("TEXCOORD", 4, Format.R32G32B32_Float,   48, 0), // aTangent     (vec3)
        new("TEXCOORD", 5, Format.R32G32B32_Float,   60, 0)  // aBitangent   (vec3)
    ];

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

            if (NifVertexColorPolicy.HasVertexColorData(sub) &&
                sub.VertexColors != null &&
                ci + 3 < sub.VertexColors.Length)
            {
                var color = NifVertexColorPolicy.Read(sub, i);
                vertices[i].VertexColor = new Vector4(
                    color.R / 255f,
                    color.G / 255f,
                    color.B / 255f,
                    color.A / 255f);
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
    ///     Creates an immutable D3D11 vertex buffer from GPU vertices.
    /// </summary>
    public static ID3D11Buffer CreateVertexBuffer(ID3D11Device device, GpuVertex[] vertices)
    {
        return CreateImmutableBuffer(device, vertices, BindFlags.VertexBuffer);
    }

    /// <summary>
    ///     Creates an immutable D3D11 index buffer from triangle indices.
    /// </summary>
    public static ID3D11Buffer CreateIndexBuffer(ID3D11Device device, ushort[] indices)
    {
        return CreateImmutableBuffer(device, indices, BindFlags.IndexBuffer);
    }

    private static ID3D11Buffer CreateImmutableBuffer<T>(ID3D11Device device, T[] data, BindFlags bindFlags)
        where T : unmanaged
    {
        var byteWidth = (uint)(data.Length * Marshal.SizeOf<T>());
        var gc = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            var desc = new BufferDescription
            {
                ByteWidth = byteWidth,
                Usage = ResourceUsage.Immutable,
                BindFlags = bindFlags,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.None,
                StructureByteStride = 0
            };

            var subresource = new SubresourceData(gc.AddrOfPinnedObject(), byteWidth);
            return device.CreateBuffer(desc, subresource);
        }
        finally
        {
            gc.Free();
        }
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
