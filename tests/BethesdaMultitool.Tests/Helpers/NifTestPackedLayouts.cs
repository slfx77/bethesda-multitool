using System.Buffers.Binary;

namespace BethesdaMultitool.Tests.Helpers;

/// <summary>
///     Lays out the six measured console packed layouts from float arrays, transcribed by hand from the measurement
///     table (TestOutput/packed-semantics-20260924/README.md, "Layout table for the C# decoder"), never from the
///     production <c>NifPackedGeometryLayout</c>, so the reader is compared with an independent reading of the
///     measurement. Every multi-byte value is big-endian; halves are rounded to nearest even
///     (<see cref="HalfBits" />), the inverse of the decoder's exact widening.
/// </summary>
internal static class NifTestPackedLayouts
{
    /// <summary>The NiAGDDataStream Type of a half4 channel.</summary>
    public const uint Half4 = 16;

    /// <summary>The NiAGDDataStream Type of a half2 channel.</summary>
    public const uint Half2 = 14;

    /// <summary>The NiAGDDataStream Type of a four-byte channel (a D3DCOLOR or four bone indices).</summary>
    public const uint FourBytes = 28;

    /// <summary>The NiAGDDataStream Type of a float3 channel.</summary>
    public const uint Float3 = 3;

    /// <summary>The layout ids in measurement order.</summary>
    public static readonly string[] Ids = ["L1", "L2", "L3", "L4", "L5", "L6"];

    /// <summary>The measured stride of a layout.</summary>
    public static int Stride(string layout)
    {
        return layout switch
        {
            "L1" => 40,
            "L2" => 36,
            "L3" => 48,
            "L4" => 52,
            "L5" => 48,
            "L6" => 52,
            _ => throw new ArgumentOutOfRangeException(nameof(layout), layout, "Not a measured layout.")
        };
    }

    /// <summary>The measured Shader Index of a layout (recorded by the reader, never a key).</summary>
    public static int ShaderIndex(string layout)
    {
        return layout switch
        {
            "L1" => 126,
            "L2" => 124,
            "L3" => 253,
            "L4" => 255,
            "L5" => 52,
            "L6" => 54,
            _ => throw new ArgumentOutOfRangeException(nameof(layout), layout, "Not a measured layout.")
        };
    }

    /// <summary>True for the layouts whose position, normal and tangent frame are halves (L5 and L6 store float3 frames).</summary>
    public static bool HasHalfFrame(string layout)
    {
        return layout is "L1" or "L2" or "L3" or "L4";
    }

    /// <summary>True for the layouts with a vertex color channel.</summary>
    public static bool HasColors(string layout)
    {
        return layout is "L1" or "L4" or "L6";
    }

    /// <summary>True for the skinned layouts.</summary>
    public static bool IsSkinned(string layout)
    {
        return layout is "L3" or "L4";
    }

    /// <summary>
    ///     The measured stream table of a layout: (Type, Unit Size, Block Offset, semantic) in stored order, with the
    ///     semantic named as the README names it.
    /// </summary>
    public static (uint Type, uint UnitSize, uint Offset, string Semantic)[] StreamTable(string layout)
    {
        return layout switch
        {
            "L1" =>
            [
                (Half4, 8, 0, "position"), (Half4, 8, 8, "normal"), (FourBytes, 4, 16, "color"), (Half2, 4, 20, "uv"),
                (Half4, 8, 24, "bitangent"), (Half4, 8, 32, "tangent")
            ],
            "L2" =>
            [
                (Half4, 8, 0, "position"), (Half4, 8, 8, "normal"), (Half2, 4, 16, "uv"), (Half4, 8, 20, "bitangent"),
                (Half4, 8, 28, "tangent")
            ],
            "L3" =>
            [
                (Half4, 8, 0, "position"), (Half4, 8, 8, "weights"), (FourBytes, 4, 16, "boneIndices"),
                (Half4, 8, 20, "normal"), (Half2, 4, 28, "uv"), (Half4, 8, 32, "bitangent"), (Half4, 8, 40, "tangent")
            ],
            "L4" =>
            [
                (Half4, 8, 0, "position"), (Half4, 8, 8, "weights"), (FourBytes, 4, 16, "boneIndices"),
                (Half4, 8, 20, "normal"), (FourBytes, 4, 28, "color"), (Half2, 4, 32, "uv"),
                (Half4, 8, 36, "bitangent"), (Half4, 8, 44, "tangent")
            ],
            "L5" =>
            [
                (Half4, 8, 0, "position"), (Float3, 12, 8, "normal"), (Half2, 4, 20, "uv"),
                (Float3, 12, 24, "bitangent"), (Float3, 12, 36, "tangent")
            ],
            "L6" =>
            [
                (Half4, 8, 0, "position"), (Float3, 12, 8, "normal"), (FourBytes, 4, 20, "color"),
                (Half2, 4, 24, "uv"), (Float3, 12, 28, "bitangent"), (Float3, 12, 40, "tangent")
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(layout), layout, "Not a measured layout.")
        };
    }

    /// <summary>The binary16 bits of a float, rounded to nearest even (the console exporter's quantization).</summary>
    public static ushort HalfBits(float value)
    {
        return BitConverter.HalfToUInt16Bits((Half)value);
    }

    /// <summary>The float a half round trip yields: what the reader must reproduce bit for bit.</summary>
    public static float RoundTrip(float value)
    {
        return (float)BitConverter.UInt16BitsToHalf(HalfBits(value));
    }

    /// <summary>
    ///     The vertex payload of a layout: <c>VertexCount x Stride</c> big-endian bytes. A D3DCOLOR is written A, R, G, B
    ///     for the X360 and A, G, B, R for the PS3; the bone-index word is written so that slot k lands in byte 3 - k.
    /// </summary>
    public static byte[] Payload(string layout, NifTestPackedVertexData data, bool ps3 = false)
    {
        var stride = Stride(layout);
        var count = data.VertexCount;
        var payload = new byte[count * stride];
        foreach (var (type, _, offset, semantic) in StreamTable(layout))
        {
            for (var v = 0; v < count; v++)
            {
                var at = v * stride + (int)offset;
                switch (semantic)
                {
                    case "position":
                        WriteHalf3(payload, at, data.Positions, v, data.FourthHalfBits);
                        break;
                    case "normal":
                        WriteFrame(payload, at, type, data.Normals, v, data.FourthHalfBits);
                        break;
                    case "bitangent":
                        WriteFrame(payload, at, type, data.Bitangents, v, data.FourthHalfBits);
                        break;
                    case "tangent":
                        WriteFrame(payload, at, type, data.Tangents, v, data.FourthHalfBits);
                        break;
                    case "uv":
                        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(at), HalfBits(data.Uvs[v * 2]));
                        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(at + 2), HalfBits(data.Uvs[v * 2 + 1]));
                        break;
                    case "color":
                        WriteColor(payload, at, Required(data.Colors, layout, "Colors"), v, ps3);
                        break;
                    case "weights":
                        var weights = Required(data.Weights, layout, "Weights");
                        for (var k = 0; k < 4; k++)
                        {
                            BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(at + k * 2),
                                HalfBits(weights[v * 4 + k]));
                        }

                        break;
                    case "boneIndices":
                        var bones = Required(data.BoneIndices, layout, "BoneIndices");
                        for (var k = 0; k < 4; k++)
                        {
                            payload[at + 3 - k] = bones[v * 4 + k];
                        }

                        break;
                    default:
                        throw new InvalidOperationException(semantic);
                }
            }
        }

        return payload;
    }

    /// <summary>
    ///     Lays out a whole BSPackedAdditionalGeometryData body for one measured layout: the stream table, one data block
    ///     with <see cref="Payload" /> and the layout's measured Shader Index (or <paramref name="shaderIndex" />).
    /// </summary>
    public static void Write(NifTestBlockWriter w, string layout, NifTestPackedVertexData data, bool ps3 = false,
        int? shaderIndex = null)
    {
        var table = StreamTable(layout).Select(s => (s.Type, s.UnitSize, s.Offset)).ToArray();
        NifTestBlockLayouts.PackedAdditionalGeometryData(w, (ushort)data.VertexCount, table, (uint)Stride(layout),
            Payload(layout, data, ps3), (uint)(shaderIndex ?? ShaderIndex(layout)), DataSizes(layout));
    }

    /// <summary>
    ///     The per-component Data Sizes the retail blocks list (2 per half, 1 per byte, 4 per float): 22, 18, 26, 30, 15
    ///     and 19 entries for L1 to L6, as measured.
    /// </summary>
    public static uint[] DataSizes(string layout)
    {
        var sizes = new List<uint>();
        foreach (var (type, _, _, _) in StreamTable(layout))
        {
            switch (type)
            {
                case Half4:
                    sizes.AddRange([2u, 2u, 2u, 2u]);
                    break;
                case Half2:
                    sizes.AddRange([2u, 2u]);
                    break;
                case FourBytes:
                    sizes.AddRange([1u, 1u, 1u, 1u]);
                    break;
                case Float3:
                    sizes.AddRange([4u, 4u, 4u]);
                    break;
                default:
                    throw new InvalidOperationException(type.ToString());
            }
        }

        return [.. sizes];
    }

    private static void WriteFrame(byte[] payload, int at, uint type, float[] values, int vertex, ushort fourth)
    {
        if (type == Float3)
        {
            for (var c = 0; c < 3; c++)
            {
                BinaryPrimitives.WriteSingleBigEndian(payload.AsSpan(at + c * 4), values[vertex * 3 + c]);
            }
        }
        else
        {
            WriteHalf3(payload, at, values, vertex, fourth);
        }
    }

    private static void WriteHalf3(byte[] payload, int at, float[] values, int vertex, ushort fourth)
    {
        for (var c = 0; c < 3; c++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(at + c * 2), HalfBits(values[vertex * 3 + c]));
        }

        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(at + 6), fourth);
    }

    private static void WriteColor(byte[] payload, int at, byte[] colors, int vertex, bool ps3)
    {
        byte r = colors[vertex * 4], g = colors[vertex * 4 + 1], b = colors[vertex * 4 + 2], a = colors[vertex * 4 + 3];
        if (ps3)
        {
            payload[at] = a;
            payload[at + 1] = g;
            payload[at + 2] = b;
            payload[at + 3] = r;
        }
        else
        {
            payload[at] = a;
            payload[at + 1] = r;
            payload[at + 2] = g;
            payload[at + 3] = b;
        }
    }

    private static T Required<T>(T? value, string layout, string name) where T : class
    {
        return value ?? throw new ArgumentException($"Layout {layout} needs {name}.", nameof(value));
    }
}
