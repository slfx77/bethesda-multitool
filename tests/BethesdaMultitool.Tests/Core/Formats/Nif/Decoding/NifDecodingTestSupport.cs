using BethesdaMultitool.Core.Formats.Nif.Decoding;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Schema;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Decoding;

/// <summary>Shared plumbing for the block-decoder tests: open a built fixture and read exact values back.</summary>
internal static class NifDecodingTestSupport
{
    /// <summary>Parses a built fixture with NifParser and opens a decoder over it.</summary>
    public static NifBlockDecoder Open(byte[] bytes, NifSchema? schema = null)
    {
        var info = NifParser.Parse(bytes);
        Assert.NotNull(info);
        Assert.Equal(info.BlockCount, info.Blocks.Count);
        return new NifBlockDecoder(schema ?? NifSchema.LoadEmbedded(), info, bytes);
    }

    /// <summary>Builds, opens and strictly decodes one block.</summary>
    public static NifDecodedBlock DecodeStrict(NifTestFileBuilder builder, int blockIndex, NifSchema? schema = null)
    {
        return Open(builder.Build(), schema).Decode(blockIndex, NifDecodeMode.Strict);
    }

    /// <summary>The IEEE bits of a float.</summary>
    public static uint Bits(float value)
    {
        return BitConverter.SingleToUInt32Bits(value);
    }

    /// <summary>Asserts a float field decoded to exactly these bits.</summary>
    public static void AssertFloat(NifStructValue owner, string field, float expected)
    {
        Assert.Equal(Bits(expected), owner.Get<NifFloatValue>(field).RawBits);
    }

    /// <summary>Asserts an integer field's numeric value.</summary>
    public static void AssertInteger(NifStructValue owner, string field, long expected)
    {
        Assert.Equal(expected, owner.Get<NifIntegerValue>(field).Value);
    }

    /// <summary>Asserts a Vector3 / Color3-style struct's three float components exactly.</summary>
    public static void AssertTriple(NifStructValue owner, string field, string[] names, float a, float b, float c)
    {
        var value = owner.Get<NifStructValue>(field);
        AssertFloat(value, names[0], a);
        AssertFloat(value, names[1], b);
        AssertFloat(value, names[2], c);
    }

    /// <summary>Asserts a bulk float array's components, element-major, bit for bit.</summary>
    public static void AssertFloats(NifFloatArrayValue array, int componentsPerElement, params float[] expected)
    {
        Assert.Equal(componentsPerElement, array.ComponentsPerElement);
        Assert.Equal(expected.Select(Bits).ToArray(), array.Bits.ToArray());
    }

    /// <summary>The Ref indices of a Ref/Ptr array.</summary>
    public static int[] RefIndices(NifStructValue owner, string field)
    {
        return owner.Get<NifArrayValue>(field).Items.Cast<NifRefValue>().Select(r => r.Index).ToArray();
    }

    /// <summary>Loads a schema from inline nif.xml text (for synthetic definitions).</summary>
    public static NifSchema SchemaFromXml(string xml)
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml));
        return NifSchema.LoadFromStream(stream);
    }
}
