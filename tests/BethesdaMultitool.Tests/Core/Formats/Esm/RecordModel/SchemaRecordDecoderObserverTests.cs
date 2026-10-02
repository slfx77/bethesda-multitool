using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.RecordModel;
using BethesdaMultitool.Core.Formats.Esm.RecordModel.Decoding;
using BethesdaMultitool.Core.Formats.Esm.RecordModel.Schema;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.RecordModel;

public sealed class SchemaRecordDecoderObserverTests
{
    [Fact]
    public void ObserverReportsPhysicalOrdinalAndFieldOffsetWithoutChangingTree()
    {
        var schema = new RecordDef("TEST", [new ArrayDef(new StructDef([
            new FieldDef(PrimType.U32) { Name = "Number" }, new FormIdDef { Name = "Reference" }
        ]) { Signature = "DATA", Name = "Item" }) { Name = "Items" }]);
        RawSubrecord[] raw = [new("DATA", [7, 0, 0, 0, 0x34, 0x12, 0, 0]), new("DATA", [8, 0, 0, 0, 0x78, 0x56, 0, 0])];
        var seen = new List<SchemaFormIdObservation>();
        var plain = SchemaRecordDecoder.Decode(schema, raw);
        var observed = SchemaRecordDecoder.Decode(schema, raw, observeFormId: seen.Add);
        Assert.Equal(Flatten(plain), Flatten(observed));
        Assert.Collection(seen,
            a => { Assert.Equal(0, a.SubrecordOrdinal); Assert.Equal(4, a.FieldOffset); Assert.Equal(0x1234u, a.FormId); },
            b => { Assert.Equal(1, b.SubrecordOrdinal); Assert.Equal(4, b.FieldOffset); Assert.Equal(0x5678u, b.FormId); });
        Assert.All(seen, item => Assert.Equal(SchemaFormIdOrigin.TypedField, item.Origin));
    }

    [Fact]
    public void UnselectedUnionFormIdIsMarkedAsFallback()
    {
        var schema = new RecordDef("TEST", [new StructDef([
            new UnionDef("UnknownDecider", [new FormIdDef { Name = "Maybe" }, new FieldDef(PrimType.U32)]) { Name = "Value" }
        ]) { Signature = "DATA" }]);
        var seen = new List<SchemaFormIdObservation>();
        SchemaRecordDecoder.Decode(schema, [new("DATA", [0x34, 0x12, 0, 0])], observeFormId: seen.Add);
        Assert.Equal(SchemaFormIdOrigin.UnionFallback, Assert.Single(seen).Origin);
    }

    [Fact]
    public void AuthoritativeConditionStringDoesNotLeakPlaceholderBitsIntoReferences()
    {
        var schema = EsmSchemas.IndexForGame(BethesdaGame.Fallout4)!["INFO"];
        var data = new byte[32];
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(8), 0x48); // GetIsID, normally a FormID parameter
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), 0x123456);
        var control = new List<SchemaFormIdObservation>();
        SchemaRecordDecoder.Decode(schema, [new("CTDA", data)], game: BethesdaGame.Fallout4, observeFormId: control.Add);
        Assert.Contains(control, field => field.FormId == 0x123456 && field.Field == "Parameter #1");
        var strings = new List<SchemaFormIdObservation>();
        SchemaRecordDecoder.Decode(schema, [new("CTDA", data), new("CIS1", "Authoritative\0"u8.ToArray())],
            game: BethesdaGame.Fallout4, observeFormId: strings.Add);
        Assert.DoesNotContain(strings, field => field.FormId == 0x123456);
    }

    private static IEnumerable<string> Flatten(IEnumerable<DecodedNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return $"{node.Label}|{node.Value}|{node.FormId}|{node.Signature}|{node.IsRaw}";
            foreach (var child in Flatten(node.Children)) { yield return child; }
        }
    }
}
