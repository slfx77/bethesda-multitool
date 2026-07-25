using System.Text;
using BethesdaMultitool.Core.Formats.Esm.RecordModel.Decoding;
using BethesdaMultitool.Core.Formats.Esm.RecordModel.Generated;
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
    private static RecordDef BuildNpcSchema()
    {
        return new RecordDef("NPC_",
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
    }

    private static byte[] Le(uint v)
    {
        return [(byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24)];
    }

    private static byte[] Zstr(string s)
    {
        var bytes = new byte[s.Length + 1];
        Encoding.ASCII.GetBytes(s).CopyTo(bytes, 0);
        return bytes;
    }

    private static DecodedNode? Find(IReadOnlyList<DecodedNode> nodes, string label)
    {
        return nodes.FirstOrDefault(n => n.Label == label);
    }

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
    public void Decodes_Real_OblivionSchema_Npc_With_GroupStruct_And_Stats()
    {
        var npcSchema = OblivionSchema.Records.First(r => r.Signature == "NPC_");

        // ACBS (16B): Flags=0x01(Female), spell=50, fatigue=50, barter=0, level=1, calcMin=0, calcMax=0
        var acbs = new List<byte>();
        acbs.AddRange(Le(0x01));
        acbs.AddRange(U16(50));
        acbs.AddRange(U16(50));
        acbs.AddRange(U16(0));
        acbs.AddRange(U16(1));
        acbs.AddRange(U16(0));
        acbs.AddRange(U16(0));

        // DATA (33B): 21 U8 skills (Armorer=10, rest 5), Health U16=50, 2 unused, 8 U8 attributes (Str=40)
        var data = new List<byte> { 10 };
        data.AddRange(Enumerable.Repeat((byte)5, 20));
        data.AddRange(U16(50));
        data.AddRange([0, 0]);
        data.AddRange([40, 50, 50, 50, 50, 50, 50, 50]);

        var subs = new List<RawSubrecord>
        {
            new("EDID", Zstr("TestNpcOblivion")),
            new("MODL", Zstr("characters\\test.nif")), // Model group struct (MODL/MODB/MODT)
            new("ACBS", [.. acbs]),
            new("SNAM", [.. Le(0x000A2B62), 1]), // faction 0x0A2B62, rank 1 (IsFO4Plus consumes 0)
            new("DATA", [.. data]),
            new("CNAM", Le(0x00023F2A)) // class FormID
        };

        var tree = SchemaRecordDecoder.Decode(npcSchema, subs, resolveName: f => f == 0x0A2B62 ? "TestFaction" : null);

        // Group struct: Model appears once with the filename child (not duplicated per MODL/MODB/MODT).
        var model = Assert.Single(tree, n => n.Label == "Model");
        Assert.Equal("characters\\test.nif", Find(model.Children, "Model Filename")!.Value);

        var acbsNode = Find(tree, "Configuration")!;
        Assert.Contains("Female", Find(acbsNode.Children, "Flags")!.Value);

        var stats = Find(tree, "Stats")!;
        Assert.Equal("10", Find(stats.Children, "Armorer")!.Value);
        Assert.Equal("40", Find(stats.Children, "Strength")!.Value);
        Assert.Equal("50", Find(stats.Children, "Health")!.Value);

        var factions = Find(tree, "Factions")!;
        var faction = Find(factions.Children[0].Children, "Faction")!;
        Assert.Equal(0x0A2B62u, faction.FormId);
        Assert.Contains("TestFaction", faction.Value);
    }

    private static byte[] U16(ushort v)
    {
        return [(byte)v, (byte)(v >> 8)];
    }

    [Fact]
    public void Decodes_Array_With_Signature_On_The_Array_Not_The_Element()
    {
        // ENAM (eyes) / KFFZ (animations) pattern: the array owns the repeating subrecord signature and
        // the element is a bare inline field. Must consume each subrecord (and never spin forever).
        var schema = new RecordDef("TEST",
        [
            new ArrayDef(new FormIdDef { Name = "Eye" }) { Signature = "ENAM", Name = "Eyes", Count = -1 }
        ]);
        var subs = new List<RawSubrecord>
        {
            new("ENAM", Le(0x00000011)),
            new("ENAM", Le(0x00000022))
        };

        var tree = SchemaRecordDecoder.Decode(schema, subs);

        var eyes = Find(tree, "Eyes")!;
        Assert.Equal(2, eyes.Children.Count);
        Assert.Equal(0x11u, eyes.Children[0].FormId);
        Assert.Equal(0x22u, eyes.Children[1].FormId);
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