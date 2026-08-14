using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Esm.RecordModel;
using BethesdaMultitool.Core.Formats.Esm.RecordModel.Decoding;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.RecordModel;

public sealed class SchemaConditionStringAuthorityTests
{
    private const uint Param1Bits = 0x00123456;
    private const uint Param2Bits = 0x00654321;

    [Fact]
    public void Cis1_RelabelsStringAndKeepsExactPlaceholderBitsWithoutFormIdSemantics()
    {
        var groups = Decode(
            [
                new RawSubrecord("CTDA", BuildCtda(functionIndex: 0x048)),
                new RawSubrecord("CIS1", ZString("QuestVariable"))
            ],
            resolveName: _ => "FalseResolverName");

        var group = Assert.Single(groups);
        Assert.Equal(["CTDA", "CIS1"], group.Children.Select(node => node.Signature));

        var ctda = ChildBySignature(group, "CTDA");
        var placeholder = ChildByLabel(ctda, "Parameter #1 (CTDA placeholder)");
        Assert.Equal(Param1Bits, Assert.IsType<uint>(placeholder.RawValue));
        Assert.Equal("0x00123456", placeholder.Value);
        Assert.Null(placeholder.FormId);

        var cis = ChildBySignature(group, "CIS1");
        Assert.Equal("Parameter #1 (CIS1 authoritative string)", cis.Label);
        Assert.Equal("QuestVariable", cis.Value);
        Assert.Equal("QuestVariable", Assert.IsType<string>(cis.RawValue));
    }

    [Fact]
    public void BigEndianCis2Only_AuthorizesParameter2WithoutChangingParameter1()
    {
        var groups = Decode(
            [
                new RawSubrecord("CTDA", BuildCtda(functionIndex: 0x240, bigEndian: true)),
                new RawSubrecord("CIS2", ZString("EventMember"))
            ],
            bigEndian: true,
            resolveName: _ => "FalseResolverName");

        var group = Assert.Single(groups);
        var ctda = ChildBySignature(group, "CTDA");
        Assert.NotNull(ChildByLabel(ctda, "Parameter #1"));
        Assert.DoesNotContain(ctda.Children, node => node.Label.Contains("Parameter #1 (CTDA placeholder)", StringComparison.Ordinal));

        var placeholder = ChildByLabel(ctda, "Parameter #2 (CTDA placeholder)");
        Assert.Equal(Param2Bits, Assert.IsType<uint>(placeholder.RawValue));
        Assert.Equal("0x00654321", placeholder.Value);
        Assert.Null(placeholder.FormId);

        var cis = ChildBySignature(group, "CIS2");
        Assert.Equal("Parameter #2 (CIS2 authoritative string)", cis.Label);
        Assert.Equal("EventMember", cis.Value);
    }

    [Fact]
    public void PresentEmptyCis_RemainsVisiblyAuthoritative()
    {
        var group = Assert.Single(Decode(
        [
            new RawSubrecord("CTDA", BuildCtda(functionIndex: 0x048)),
            new RawSubrecord("CIS1", [])
        ]));

        var cis = ChildBySignature(group, "CIS1");
        Assert.Equal("Parameter #1 (CIS1 authoritative string)", cis.Label);
        Assert.Equal("(empty string)", cis.Value);
        Assert.Equal(string.Empty, Assert.IsType<string>(cis.RawValue));
        Assert.NotNull(ChildByLabel(ChildBySignature(group, "CTDA"), "Parameter #1 (CTDA placeholder)"));
    }

    [Fact]
    public void BothCisSiblings_PreservePhysicalNodeOrderAndRawValues()
    {
        var group = Assert.Single(Decode(
        [
            new RawSubrecord("CTDA", BuildCtda(functionIndex: 0x240)),
            new RawSubrecord("CIS1", ZString("Selector")),
            new RawSubrecord("CIS2", ZString("Member"))
        ]));

        Assert.Equal(["CTDA", "CIS1", "CIS2"], group.Children.Select(node => node.Signature));
        var ctda = ChildBySignature(group, "CTDA");
        Assert.NotNull(ChildByLabel(ctda, "Parameter #1 (CTDA placeholder)"));
        Assert.NotNull(ChildByLabel(ctda, "Parameter #2 (CTDA placeholder)"));
        Assert.Equal("Selector", ChildBySignature(group, "CIS1").RawValue);
        Assert.Equal("Member", ChildBySignature(group, "CIS2").RawValue);
    }

    [Fact]
    public void NoCis_ControlRetainsOriginalTypedParameterAndResolverDisplay()
    {
        var group = Assert.Single(Decode(
            [new RawSubrecord("CTDA", BuildCtda(functionIndex: 0x048))],
            resolveName: _ => "ResolvedBase"));

        var param1 = ChildByLabel(ChildBySignature(group, "CTDA"), "Parameter #1");
        Assert.Equal(Param1Bits, param1.FormId);
        Assert.Equal("ResolvedBase (0x00123456)", param1.Value);
        Assert.DoesNotContain(group.Children, node => node.Signature is "CIS1" or "CIS2");
    }

    [Fact]
    public void OrphanRepeatedAndOutOfOrderCis_NeverAttachAcrossConditionGroups()
    {
        var groups = Decode(
        [
            new RawSubrecord("CIS1", ZString("orphan-before")),
            new RawSubrecord("CTDA", BuildCtda(functionIndex: 0x240)),
            new RawSubrecord("CIS2", ZString("second-only")),
            new RawSubrecord("CIS1", ZString("out-of-order")),
            new RawSubrecord("CTDA", BuildCtda(functionIndex: 0x048)),
            new RawSubrecord("CIS1", ZString("first")),
            new RawSubrecord("CIS1", ZString("repeated"))
        ]);

        Assert.Equal(5, groups.Count);
        Assert.Equal("Parameter #1", Assert.Single(groups[0].Children).Label);

        Assert.Equal("Parameter #2 (CIS2 authoritative string)", ChildBySignature(groups[1], "CIS2").Label);
        Assert.NotNull(ChildByLabel(
            ChildBySignature(groups[1], "CTDA"),
            "Parameter #2 (CTDA placeholder)"));
        Assert.Equal("Parameter #1", Assert.Single(groups[2].Children).Label);

        Assert.Equal("Parameter #1 (CIS1 authoritative string)", ChildBySignature(groups[3], "CIS1").Label);
        Assert.NotNull(ChildByLabel(
            ChildBySignature(groups[3], "CTDA"),
            "Parameter #1 (CTDA placeholder)"));
        Assert.Equal("Parameter #1", Assert.Single(groups[4].Children).Label);
    }

    [Fact]
    public void PropertyAdapter_DoesNotCreateLinkOrReResolveCisPlaceholder()
    {
        var tree = DecodeTree(
            [
                new RawSubrecord("CTDA", BuildCtda(functionIndex: 0x048)),
                new RawSubrecord("CIS1", ZString("QuestVariable"))
            ],
            resolveName: _ => "DecodeResolverName");
        var resolver = new FormIdResolver(
            new Dictionary<uint, string> { [Param1Bits] = "AdapterResolverName" },
            []);

        var rows = global::BethesdaMultitool.DecodedTreePropertyAdapter.Convert(
            new GenericEsmRecord { FormId = 0x01000001, RecordType = "INFO" },
            tree,
            resolver);

        var placeholder = FindRow(rows, "Parameter #1 (CTDA placeholder)");
        Assert.NotNull(placeholder);
        Assert.Equal("0x00123456", placeholder!.Value);
        Assert.Null(placeholder.LinkedFormId);

        var cis = FindRow(rows, "Parameter #1 (CIS1 authoritative string)");
        Assert.NotNull(cis);
        Assert.Equal("QuestVariable", cis!.Value);
        Assert.Null(cis.LinkedFormId);
    }

    private static IReadOnlyList<DecodedNode> Decode(
        IReadOnlyList<RawSubrecord> subrecords,
        bool bigEndian = false,
        SchemaRecordDecoder.FormIdNameResolver? resolveName = null) =>
        Assert.Single(DecodeTree(subrecords, bigEndian, resolveName)).Children;

    private static IReadOnlyList<DecodedNode> DecodeTree(
        IReadOnlyList<RawSubrecord> subrecords,
        bool bigEndian = false,
        SchemaRecordDecoder.FormIdNameResolver? resolveName = null)
    {
        var schema = EsmSchemas.IndexForGame(BethesdaGame.Fallout4);
        Assert.NotNull(schema);
        Assert.True(schema!.TryGetValue("INFO", out var info));

        return SchemaRecordDecoder.Decode(
            info!, subrecords, bigEndian, resolveName, BethesdaGame.Fallout4);
    }

    private static byte[] BuildCtda(
        ushort functionIndex,
        bool bigEndian = false,
        uint param1 = Param1Bits,
        uint param2 = Param2Bits)
    {
        var data = new byte[32];
        if (bigEndian)
        {
            BinaryPrimitives.WriteSingleBigEndian(data.AsSpan(4), 1f);
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(8), functionIndex);
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(12), param1);
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(16), param2);
            BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(28), -1);
        }
        else
        {
            BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(4), 1f);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(8), functionIndex);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), param1);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), param2);
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(28), -1);
        }

        return data;
    }

    private static byte[] ZString(string value) => Encoding.ASCII.GetBytes(value + '\0');

    private static DecodedNode ChildBySignature(DecodedNode parent, string signature) =>
        Assert.Single(parent.Children, node => node.Signature == signature);

    private static DecodedNode ChildByLabel(DecodedNode parent, string label) =>
        Assert.Single(parent.Children, node => node.Label == label);

    private static global::BethesdaMultitool.EsmPropertyEntry? FindRow(
        IEnumerable<global::BethesdaMultitool.EsmPropertyEntry> rows,
        string name)
    {
        foreach (var row in rows)
        {
            if (row.Name == name)
            {
                return row;
            }

            if (row.SubItems is { Count: > 0 } && FindRow(row.SubItems, name) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }
}
