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
        // Both consumers are shader data rather than coverage: classic TallGrass reads alpha as
        // wind weight, while CE2 vertex Lerp reads it as mix(albedo, vertex.rgb, vertex.a).
        var preserveShaderVertexAlpha =
            preserveAuthoredVertexAlpha || sub.StarfieldMaterialColor.IsVertexLerp;

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

            if ((NifVertexColorPolicy.HasVertexColorData(sub) || preserveShaderVertexAlpha) &&
                sub.VertexColors != null &&
                ci + 3 < sub.VertexColors.Length)
            {
                var color = sub.StarfieldMaterialColor.IsVertexLerp
                    ? (R: sub.VertexColors[ci], G: sub.VertexColors[ci + 1],
                        B: sub.VertexColors[ci + 2], A: sub.VertexColors[ci + 3])
                    : NifVertexColorPolicy.Read(sub, i);
                vertices[i].VertexColor = new Vector4(
                    color.R / 255f,
                    color.G / 255f,
                    color.B / 255f,
                    // The ordinary upload path keeps policy-normalized coverage alpha. Identified
                    // shader-data routes retain the raw channel; dedicated shader flags keep that
                    // alpha out of generic opacity and coverage calculations.
                    preserveShaderVertexAlpha ? sub.VertexColors[ci + 3] / 255f : color.A / 255f);
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
    ///     GPU vertex layout: position(12) + normal(12) + texcoord(8) + color(4) + tangent(12) + bitangent(12)
    ///     = 60 bytes.
    ///     <para>
    ///         Colour is <c>R8G8B8A8_UNORM</c> rather than a float4, which is <b>bit-exact</b>: every
    ///         source is a NIF <c>byte</c> divided by 255, so packing it back recovers the same byte.
    ///         The input assembler expands it to <c>float4</c> before any shader sees it, so no HLSL
    ///         changed. This lands in the geometry arena, which is an UPLOAD heap — i.e. it is a
    ///         <b>system RAM</b> saving, not a VRAM one, despite looking like the latter.
    ///     </para>
    ///     <para>
    ///         Normal/tangent/bitangent deliberately stay <c>float3</c>: SpeedTree smuggles data in
    ///         their magnitudes (<c>|N|-1</c> = leaf wind weight, <c>|T|</c> = matrix index,
    ///         <c>aBitangent.z</c> = integer + <c>frac()</c> payload), so normalising them would
    ///         destroy information the renderer reads.
    ///     </para>
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct GpuVertex
    {
        public Vector3 Position;
        public Vector3 Normal;
        public Vector2 TexCoord;

        /// <summary>Packed RGBA, one byte per channel in memory order R,G,B,A — see <see cref="PackColor" />.</summary>
        public uint VertexColorRgba;

        public Vector3 Tangent;
        public Vector3 Bitangent;

        /// <summary>Convenience view of <see cref="VertexColorRgba" /> as the float4 shaders receive.</summary>
        public Vector4 VertexColor
        {
            get => UnpackColor(VertexColorRgba);
            set => VertexColorRgba = PackColor(value);
        }
    }

    /// <summary>
    ///     Packs a [0,1] RGBA colour into <c>R8G8B8A8_UNORM</c> byte order (R in the low byte, so the
    ///     little-endian memory bytes read R,G,B,A exactly as the format specifies).
    /// </summary>
    public static uint PackColor(Vector4 color)
    {
        static uint Channel(float v)
        {
            // Round-to-nearest, not truncate: the sources are b/255, and truncation would bias every
            // channel down by one whenever the float lands a hair below the integer.
            return (uint)MathF.Round(Math.Clamp(v, 0f, 1f) * 255f);
        }

        return Channel(color.X)
               | (Channel(color.Y) << 8)
               | (Channel(color.Z) << 16)
               | (Channel(color.W) << 24);
    }

    /// <summary>Inverse of <see cref="PackColor" />.</summary>
    public static Vector4 UnpackColor(uint packed)
    {
        return new Vector4(
            (packed & 0xFF) / 255f,
            ((packed >> 8) & 0xFF) / 255f,
            ((packed >> 16) & 0xFF) / 255f,
            ((packed >> 24) & 0xFF) / 255f);
    }
}
