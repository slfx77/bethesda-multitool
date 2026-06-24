using EsmSchemaGen.Ir;
using Xunit;

namespace EsmSchemaGen.Tests;

public class IrBuilderTests
{
    [Fact]
    public void Builds_Dial_Record_With_Struct_Enum_And_Unused()
    {
        const string src = """
            wbRecord(DIAL, 'Dialog Topic', [
              wbEDID,
              wbStruct(DATA, 'Data', [
                wbInteger('Dialog Type', itU8, wbEnum(['Topic','Conversation','Greeting'])),
                wbUnused(3)
              ]).SetRequired
            ])
            """;

        var rec = new IrBuilder().BuildRecord(src);

        Assert.Equal("DIAL", rec.Signature);
        Assert.Equal("Dialog Topic", rec.Name);
        Assert.Equal(2, rec.Members.Count);

        var edid = Assert.IsType<FieldDef>(rec.Members[0]);
        Assert.Equal(PrimType.ZString, edid.Type);
        Assert.Equal("EDID", edid.Signature);

        var data = Assert.IsType<StructDef>(rec.Members[1]);
        Assert.Equal("DATA", data.Signature);
        Assert.True(data.Required);
        Assert.Equal(2, data.Members.Count);

        var dialogType = Assert.IsType<FieldDef>(data.Members[0]);
        Assert.Equal(PrimType.U8, dialogType.Type);
        Assert.Equal("Dialog Type", dialogType.Name);
        Assert.NotNull(dialogType.InlineEnum);
        Assert.Equal(3, dialogType.InlineEnum!.Members.Count);
        Assert.Equal(0, dialogType.InlineEnum.Members[0].Value);
        Assert.Equal("Topic", dialogType.InlineEnum.Members[0].Label);
        Assert.Equal(2, dialogType.InlineEnum.Members[2].Value);
        Assert.Equal("Greeting", dialogType.InlineEnum.Members[2].Label);

        var unused = Assert.IsType<UnusedDef>(data.Members[1]);
        Assert.Equal(3, unused.Size);
    }

    [Fact]
    public void Builds_Greedy_Array_Of_FormId_With_Targets()
    {
        const string src = "wbRecord(INFO, 'x', [ wbRArray('Add Topics', wbFormIDCk(NAME, 'Topic', [DIAL])) ])";

        var rec = new IrBuilder().BuildRecord(src);

        var array = Assert.IsType<ArrayDef>(rec.Members[0]);
        Assert.Equal(0, array.Count); // greedy / repeat-until-end
        Assert.Equal("Add Topics", array.Name);

        var formId = Assert.IsType<FormIdDef>(array.Element);
        Assert.Equal("NAME", formId.Signature);
        Assert.Equal(["DIAL"], formId.Targets);
    }

    [Fact]
    public void Builds_SignatureFirst_Integer_With_Flags()
    {
        const string src = "wbRecord(WEAP, 'Weapon', [ wbInteger(DNAM, 'Flags', itU8, wbFlags(['Notdetect','Notrespond','Continuous'])) ])";

        var rec = new IrBuilder().BuildRecord(src);

        var flags = Assert.IsType<FieldDef>(rec.Members[0]);
        Assert.Equal(PrimType.U8, flags.Type);
        Assert.Equal("DNAM", flags.Signature);
        Assert.Equal("Flags", flags.Name);
        Assert.NotNull(flags.InlineFlags);
        Assert.Equal(3, flags.InlineFlags!.Bits.Count);
        Assert.Equal(1, flags.InlineFlags.Bits[1].Bit);
        Assert.Equal("Notrespond", flags.InlineFlags.Bits[1].Label);
    }

    [Fact]
    public void Applies_DefaultNativeValue_Modifier()
    {
        const string src = "wbRecord(TST_, 't', [ wbInteger('Rank', itS8).SetDefaultNativeValue(-1) ])";

        var rec = new IrBuilder().BuildRecord(src);

        var rank = Assert.IsType<FieldDef>(rec.Members[0]);
        Assert.Equal(PrimType.S8, rank.Type);
        Assert.Equal(-1, rank.DefaultValue);
    }

    [Fact]
    public void Builds_TexturedModel_As_Model_Group()
    {
        var rec = new IrBuilder().BuildRecord(
            "wbRecord(ACTI, 'Activator', [ wbTexturedModel('Model', [MODL, MODB, MODT], [ wbByteArray(MODS, 'Alternate Textures') ]) ])");

        var group = Assert.IsType<StructDef>(rec.Members[0]);
        Assert.Equal("Model", group.Name);
        Assert.Equal(4, group.Members.Count); // MODL + MODB + MODT + the MODS texture subrecord

        var modl = Assert.IsType<FieldDef>(group.Members[0]);
        Assert.Equal("MODL", modl.Signature);
        Assert.Equal(PrimType.ZString, modl.Type);
        Assert.Equal("MODB", Assert.IsType<FieldDef>(group.Members[1]).Signature);
        Assert.Equal("MODT", Assert.IsType<FieldDef>(group.Members[2]).Signature);
        Assert.Equal("MODS", Assert.IsType<FieldDef>(group.Members[3]).Signature);
    }

    [Fact]
    public void Resolves_Builder_Names_Case_Insensitively()
    {
        // Pascal identifiers are case-insensitive; xEdit mixes wbFormIDCk / wbFormIDCK for the same function.
        var rec = new IrBuilder().BuildRecord("wbRecord(WEAP, 'Weapon', [ wbFormIDCK(SCRI, 'Script', [SCPT]) ])");

        var formId = Assert.IsType<FormIdDef>(rec.Members[0]);
        Assert.Equal("SCRI", formId.Signature);
        Assert.Equal(["SCPT"], formId.Targets);
    }

    [Fact]
    public void Unmapped_Builder_Is_Tracked_As_Unknown()
    {
        const string src = "wbRecord(WEAP, 'Weapon', [ wbNotModeledYet, wbAlsoUnmodeled(True) ])";

        var builder = new IrBuilder();
        var rec = builder.BuildRecord(src);

        Assert.All(rec.Members, m => Assert.IsType<UnknownMemberDef>(m));
        Assert.True(builder.UnknownCalls.ContainsKey("wbNotModeledYet"));
        Assert.True(builder.UnknownCalls.ContainsKey("wbAlsoUnmodeled"));
    }
}
