using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.RecordModel;
using BethesdaMultitool.Core.Formats.Esm.RecordModel.Decoding;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.RecordModel;

/// <summary>
///     S2: typed CTDA decoding through the schema decoder. The Parameter #1/#2 union variants are
///     selected by the already-decoded sibling Function index via the game's condition-function
///     table, and the Comparison Value picks GLOB-vs-float from the sibling Type's UseGlobal flag —
///     previously every union decoded its first variant, rendering params as raw bytes/numbers.
///     Unknown game must keep the historical behavior byte-for-byte.
/// </summary>
public class SchemaCtdaTypedDecodeTests
{
    private static byte[] BuildCtda(byte type, float compValue, ushort functionIndex, uint param1, uint param2)
    {
        // TES4 CTDA: Type u8 + unused(3) + CompValue f32 + Function u16 + unused(2) + Param1 u32 + Param2 u32.
        var data = new byte[20];
        data[0] = type;
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(4), compValue);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(8), functionIndex);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), param1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), param2);
        return data;
    }

    private static DecodedNode DecodeOblivionCondition(byte[] ctda)
    {
        var schema = EsmSchemas.IndexForGame(BethesdaGame.Oblivion);
        Assert.NotNull(schema);
        Assert.True(schema!.TryGetValue("INFO", out var infoDef), "Oblivion schema must define INFO");

        var tree = SchemaRecordDecoder.Decode(
            infoDef!, [new RawSubrecord("CTDA", ctda)], game: BethesdaGame.Oblivion);

        var condition = FindNode(tree, "Condition");
        Assert.NotNull(condition);
        return condition!;
    }

    private static DecodedNode? FindNode(IEnumerable<DecodedNode> nodes, string label)
    {
        foreach (var node in nodes)
        {
            if (node.Label?.Contains(label, StringComparison.Ordinal) == true)
            {
                return node;
            }

            if (node.Children is { Count: > 0 } && FindNode(node.Children, label) is { } hit)
            {
                return hit;
            }
        }

        return null;
    }

    private static DecodedNode Param(DecodedNode condition, string label)
    {
        var node = FindNode(condition.Children ?? [], label);
        Assert.NotNull(node);
        return node!;
    }

    [Fact]
    public void Oblivion_FormIdParam_DecodesAsFormId()
    {
        // GetIsID (index 0x048): param1 is a base-object FormID.
        var condition = DecodeOblivionCondition(BuildCtda(0, 1f, 0x048, 0x00001234, 0));
        var param1 = Param(condition, "Parameter #1");
        Assert.Equal(0x1234u, param1.FormId);
    }

    [Fact]
    public void Oblivion_NumericParam_DecodesAsNumber()
    {
        // GetActorValue (index 0x00E): param1 is an actor-value index — no FormID.
        var condition = DecodeOblivionCondition(BuildCtda(0, 50f, 0x00E, 5, 0));
        var param1 = Param(condition, "Parameter #1");
        Assert.Null(param1.FormId);
        Assert.Equal("5", param1.Value);
    }

    [Fact]
    public void Oblivion_CompValue_FollowsUseGlobalFlag()
    {
        // Type & 0x04 (UseGlobal) → the comparison value is a GLOB FormID; else a float.
        var asFloat = DecodeOblivionCondition(BuildCtda(0, 42f, 0x00E, 0, 0));
        var floatNode = Param(asFloat, "Comparison Value");
        Assert.Null(floatNode.FormId);
        Assert.Equal("42", floatNode.Value);

        var globBytes = BuildCtda(0x04, 0f, 0x00E, 0, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(globBytes.AsSpan(4), 0x00ABCDEF);
        var asGlobal = DecodeOblivionCondition(globBytes);
        var globNode = Param(asGlobal, "Comparison Value");
        Assert.Equal(0x00ABCDEFu, globNode.FormId);
    }

    [Fact]
    public void UnknownGame_KeepsHistoricalFirstVariantDecode()
    {
        var schema = EsmSchemas.IndexForGame(BethesdaGame.Oblivion);
        var infoDef = schema!["INFO"];
        var ctda = BuildCtda(0, 1f, 0x048, 0x00001234, 0);

        var tree = SchemaRecordDecoder.Decode(infoDef, [new RawSubrecord("CTDA", ctda)]);
        var condition = FindNode(tree, "Condition");
        Assert.NotNull(condition);

        // Historical behavior: Variants[0] (an opaque 4-byte value surfaced as u32) — NOT a FormID.
        var param1 = Param(condition!, "Parameter #1");
        Assert.Null(param1.FormId);
    }
}
