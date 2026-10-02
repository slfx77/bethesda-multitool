using BethesdaMultitool.Core.Formats.Nif.Decoding;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Schema;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Conversion;

/// <summary>
///     The stored tangent frame of every geometry shape of a NIF, read through the schema decoder (never through the
///     converter or the cut-1a reader): per shape in block order, its data block, whether that block references a
///     BSPackedAdditionalGeometryData, its Num Vertices and the two nif.xml arrays in stored order, "Tangents" (first) and
///     "Bitangents" (second), null when the block stores none. Shape types follow the converter measurement's mirror
///     (TestOutput/nif-tangent-frame-20260928/converter): NiTriShape, NiTriStrips, BSSegmentedTriShape, BSLODTriShape.
/// </summary>
internal static class NifStoredFrameArrays
{
    private static readonly HashSet<string> ShapeTypes =
        new(["NiTriShape", "NiTriStrips", "BSSegmentedTriShape", "BSLODTriShape"], StringComparer.Ordinal);

    /// <summary>Every geometry shape of <paramref name="bytes" />, in block order.</summary>
    public static List<NifStoredFrame> Read(byte[] bytes)
    {
        var nif = Assert.IsType<NifInfo>(NifParser.Parse(bytes));
        var decoder = new NifBlockDecoder(NifSchema.LoadEmbedded(), nif, bytes);
        var shapes = new List<NifStoredFrame>();
        for (var i = 0; i < nif.Blocks.Count; i++)
        {
            if (!ShapeTypes.Contains(nif.Blocks[i].TypeName))
            {
                continue;
            }

            var shape = decoder.Decode(i, NifDecodeMode.Tolerant).Root;
            if (!shape.TryGet("Data", out var link) || link is not NifRefValue { IsNone: false } dataLink ||
                (uint)dataLink.Index >= (uint)nif.Blocks.Count)
            {
                shapes.Add(new NifStoredFrame(i, -1, false, 0, null, null));
                continue;
            }

            var data = decoder.Decode(dataLink.Index, NifDecodeMode.Tolerant).Root;
            var packed = data.TryGet("Additional Data", out var additional) &&
                         additional is NifRefValue { IsNone: false } additionalLink &&
                         (uint)additionalLink.Index < (uint)nif.Blocks.Count &&
                         nif.Blocks[additionalLink.Index].TypeName == "BSPackedAdditionalGeometryData";
            var vertexCount = data.TryGet("Num Vertices", out var count) && count is NifIntegerValue integer
                ? (int)integer.Value
                : 0;
            shapes.Add(new NifStoredFrame(i, dataLink.Index, packed, vertexCount, Floats(data, "Tangents"),
                Floats(data, "Bitangents")));
        }

        return shapes;
    }

    /// <summary>
    ///     True when a converted component reproduces the PC one within binary16 precision: |converted - pc| at most one
    ///     binary16 ulp at |pc| (floor 2^-12). A console channel is the PC value rounded to the nearest half, so it is
    ///     within half an ulp; NaN never agrees.
    /// </summary>
    public static bool HalfAgrees(float converted, float pc)
    {
        return Math.Abs((double)converted - pc) <= Math.Max(HalfUlp(pc), 1.0 / 4096);
    }

    /// <summary>True when all three components of element <paramref name="i" /> agree (<see cref="HalfAgrees" />).</summary>
    public static bool VectorAgrees(NifFloatArrayValue converted, NifFloatArrayValue pc, int i)
    {
        return HalfAgrees(converted.Get(i, 0), pc.Get(i, 0)) && HalfAgrees(converted.Get(i, 1), pc.Get(i, 1)) &&
               HalfAgrees(converted.Get(i, 2), pc.Get(i, 2));
    }

    private static double HalfUlp(float value)
    {
        var magnitude = Math.Abs((double)value);
        if (magnitude == 0)
        {
            return Math.Pow(2, -24);
        }

        return Math.Pow(2, Math.Max(Math.Floor(Math.Log2(magnitude)), -14) - 10);
    }

    private static NifFloatArrayValue? Floats(NifStructValue block, string field)
    {
        return block.TryGet(field, out var value) && value is NifFloatArrayValue { ComponentsPerElement: 3 } array
            ? array
            : null;
    }
}
