using BethesdaMultitool.Core.Formats.Esm.RecordModel.Decoding;
using BethesdaMultitool.Core.Formats.Esm.RecordModel.Schema;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.RecordModel;

/// <summary>
///     Unit coverage for <see cref="SchemaRecordDecoder" /> — the schema-driven engine that turns raw
///     subrecords into the labeled tree the GUI renders. The decoder is built/tested against a synthetic
///     NPC_-shaped schema so the behaviors that matter for multi-game reading are pinned independently of
///     any one generated schema: length-bounded struct decode (the mechanism that absorbs version
///     differences), single-signature arrays, enum/flag labels, FormID resolution, and raw passthrough.
/// </summary>
public class SchemaRecordDecoderTests
{
    private static RecordDef BuildNpcSchema() => new("NPC_",
    [
        new FieldDef(PrimType.ZString) { Signature = "EDID", Name = "Editor ID" },
        new FieldDef(PrimType.ZString) { Signature = "FULL", Name = "Name" },
        new StructDef(
        [
            new FieldDef(PrimType.U32)
            {
                Name = "Flags",
                InlineFlags = new FlagsDef(null, [new FlagMember(0, "Female"), new FlagMember(1, "Essential")])
            },
            new FieldDef(PrimType.U16) { Name = "Fatigue" }
        ]) { Signature = "ACBS", Name = "Configuration" },
        new ArrayDef(
            new StructDef(
            [
                new FormIdDef { Name = "Faction" },
                new FieldDef(PrimType.S8) { Name = "Rank" },
                // Trailing member present only in later games — must consume 0 bytes here.
                new RawMemberDef("IsFO4Plus") { Name = "Unused (FO4+)" }
            ]) { Signature = "SNAM", Name = "Faction" }
        ) { Name = "Factions", Count = 0 },
        // Two declared bytes; the test feeds only one to prove the trailing field is reported absent.
        new StructDef(
        [
            new FieldDef(PrimType.U8) { Name = "Aggression" },
            new FieldDef(PrimType.U8) { Name = "Confidence" }
        ]) { Signature = "DATA", Name = "AI Data" }
    ]);

    private static byte[] Le(uint v) => [(byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24)];

    private static byte[] Zstr(string s)
    {
        var bytes = new byte[s.Length + 1];
        System.Text.Encoding.ASCII.GetBytes(s).CopyTo(bytes, 0);
        return bytes;
    }

    private static DecodedNode? Find(IReadOnlyList<DecodedNode> nodes, string label) =>
        nodes.FirstOrDefault(n => n.Label == label);

    [Fact]
    public void Decodes_Strings_And_Flag_Labels()
    {
        var schema = BuildNpcSchema();
        var subs = new List<RawSubrecord>
        {
            new("EDID", Zstr("TestNpc")),
            new("FULL", Zstr("Test Person")),
            // Flags = 0x03 (Female|Essential), Fatigue = 50
            new("ACBS", [.. Le(0x03), 50, 0])
        };

        var tree = SchemaRecordDecoder.Decode(schema, subs);

        Assert.Equal("TestNpc", Find(tree, "Editor ID")!.Value);
        Assert.Equal("Test Person", Find(tree, "Name")!.Value);

        var acbs = Find(tree, "Configuration")!;
        var flags = Find(acbs.Children, "Flags")!;
        Assert.Contains("Female", flags.Value);
        Assert.Contains("Essential", flags.Value);
        Assert.Equal("50", Find(acbs.Children, "Fatigue")!.Value);
    }

    [Fact]
    public void LengthBounded_Struct_Reports_TrailingField_Absent()
    {
        var schema = BuildNpcSchema();
        // DATA carries only one of its two declared bytes — the engine must decode Aggression and treat
        // Confidence as absent rather than reading past the framed subrecord.
        var subs = new List<RawSubrecord> { new("DATA", [7]) };

        var tree = SchemaRecordDecoder.Decode(schema, subs);

        var data = Find(tree, "AI Data")!;
        Assert.Equal("7", Find(data.Children, "Aggression")!.Value);
        Assert.Null(Find(data.Children, "Confidence")); // trailing field absent in the shorter subrecord
    }

    [Fact]
    public void Decodes_SingleSignature_Array_With_FormId_Resolution()
    {
        var schema = BuildNpcSchema();
        var subs = new List<RawSubrecord>
        {
            new("SNAM", [.. Le(0x00000010), 2]), // faction 0x10, rank 2 (IsFO4Plus consumes 0 bytes)
            new("SNAM", [.. Le(0x00000020), 5])
        };

        var tree = SchemaRecordDecoder.Decode(schema, subs, resolveName: f => f == 0x10 ? "Powder Gangers" : null);

        var factions = Find(tree, "Factions")!;
        Assert.Equal(2, factions.Children.Count);

        var first = factions.Children[0];
        var faction = Find(first.Children, "Faction")!;
        Assert.Equal(0x10u, faction.FormId);
        Assert.Contains("Powder Gangers", faction.Value);
        Assert.Equal("2", Find(first.Children, "Rank")!.Value);
        // The FO4-only trailing member is absent in this Oblivion-length subrecord.
        Assert.Null(Find(first.Children, "Unused (FO4+)"));
    }

    [Fact]
    public void Unmatched_Subrecord_Is_Preserved_As_Raw()
    {
        var schema = BuildNpcSchema();
        var subs = new List<RawSubrecord> { new("ZZZZ", [1, 2, 3, 4]) };

        var tree = SchemaRecordDecoder.Decode(schema, subs);

        var raw = Find(tree, "ZZZZ")!;
        Assert.True(raw.IsRaw);
        Assert.Equal("ZZZZ", raw.Signature);
    }
}
