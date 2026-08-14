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
    public void Builds_Script_Source_As_A_Signed_String()
    {
        const string src =
            "wbRecord(SCPT, 'Script', [ wbStringScript(SCTX, 'Script Source').SetRequired ])";

        var rec = new IrBuilder().BuildRecord(src);

        var source = Assert.IsType<FieldDef>(Assert.Single(rec.Members));
        Assert.Equal(PrimType.ZString, source.Type);
        Assert.Equal("SCTX", source.Signature);
        Assert.Equal("Script Source", source.Name);
        Assert.True(source.Required);
    }

    [Fact]
    public void Retains_FromVersion_On_Field_Unused_And_Raw_Members()
    {
        const string src = """
            wbRecord(TEST, 'Versioned', [
              wbFromVersion(97, wbFloat('Inner Radius')),
              wbFromVersion(112, wbUnused(4)),
              wbFromVersion(125, wbNotModeledYet)
            ])
            """;

        var rec = new IrBuilder().BuildRecord(src);

        Assert.Equal((ushort)97, Assert.IsType<FieldDef>(rec.Members[0]).MinFormVersion);
        Assert.Equal((ushort)112, Assert.IsType<UnusedDef>(rec.Members[1]).MinFormVersion);
        Assert.Equal((ushort)125, Assert.IsType<UnknownMemberDef>(rec.Members[2]).MinFormVersion);
    }

    [Fact]
    public void FromVersion_ThreeArgumentOverload_Applies_Tes5_Dalc_Signature_To_Inner_Member()
    {
        const string src = """
            wbRecord(WTHR, 'Weather', [
              wbFromVersion(111, DALC, wbStruct('Early Sunrise', [wbFloat('X')]))
            ])
            """;

        var rec = new IrBuilder().BuildRecord(src);

        var dalc = Assert.IsType<StructDef>(Assert.Single(rec.Members));
        Assert.Equal("DALC", dalc.Signature);
        Assert.Equal("Early Sunrise", dalc.Name);
        Assert.Equal((ushort)111, dalc.MinFormVersion);
    }

    [Fact]
    public void FromVersion_ThreeArgumentOverload_Applies_Fo76_Nnam_Signature_To_Inner_Member()
    {
        const string src = """
            wbRecord(WEAP, 'Weapon', [
              wbFromVersion(76, NNAM, wbFormIDCk('Embedded Weapon Mod', [OMOD]))
            ])
            """;

        var rec = new IrBuilder().BuildRecord(src);

        var nnam = Assert.IsType<FormIdDef>(Assert.Single(rec.Members));
        Assert.Equal("NNAM", nnam.Signature);
        Assert.Equal("Embedded Weapon Mod", nnam.Name);
        Assert.Equal(["OMOD"], nnam.Targets);
        Assert.Equal((ushort)76, nnam.MinFormVersion);
    }

    [Fact]
    public void FromVersion_ThreeArgumentOverload_Applies_Tes5_Lnam_Signature_To_Inner_Member()
    {
        const string src = """
            wbRecord(SNDR, 'Sound Descriptor', [
              wbFromVersion(34, LNAM, wbStruct('Values', [wbInteger('Looping', itU8)]))
            ])
            """;

        var rec = new IrBuilder().BuildRecord(src);

        var lnam = Assert.IsType<StructDef>(Assert.Single(rec.Members));
        Assert.Equal("LNAM", lnam.Signature);
        Assert.Equal("Values", lnam.Name);
        Assert.Equal((ushort)34, lnam.MinFormVersion);
    }

    [Fact]
    public void FromVersion_Retains_Threshold_Above_Byte_Range()
    {
        var rec = new IrBuilder().BuildRecord(
            "wbRecord(TEST, 'Future', [ wbFromVersion(555, wbInteger('Future Field', itU32)) ])");

        Assert.Equal((ushort)555, Assert.Single(rec.Members).MinFormVersion);
    }

    [Fact]
    public void Retains_BelowVersion_On_Field_Unused_And_Raw_Members()
    {
        const string src = """
            wbRecord(TEST, 'Versioned', [
              wbBelowVersion(97, wbInteger(DATA, 'Old Flags', itU32).SetRequired),
              wbBelowVersion(112, wbUnused(4)),
              wbBelowVersion(125, wbNotModeledYet)
            ])
            """;

        var rec = new IrBuilder().BuildRecord(src);

        var field = Assert.IsType<FieldDef>(rec.Members[0]);
        Assert.Equal("DATA", field.Signature);
        Assert.Equal("Old Flags", field.Name);
        Assert.True(field.Required);
        Assert.Equal((ushort)97, field.MaxFormVersionExclusive);
        Assert.Equal((ushort)112, Assert.IsType<UnusedDef>(rec.Members[1]).MaxFormVersionExclusive);
        Assert.Equal((ushort)125, Assert.IsType<UnknownMemberDef>(rec.Members[2]).MaxFormVersionExclusive);
    }

    [Fact]
    public void BelowVersion_ThreeArgumentOverload_Applies_Tes5_Fnam_Signature()
    {
        const string src = """
            wbRecord(SNDR, 'Sound Descriptor', [
              wbBelowVersion(35, FNAM, wbInteger('Flags', itU32, wbFlags([0, 'Unknown 0', 4, 'Loop'])))
            ])
            """;

        var rec = new IrBuilder().BuildRecord(src);

        var fnam = Assert.IsType<FieldDef>(Assert.Single(rec.Members));
        Assert.Equal(PrimType.U32, fnam.Type);
        Assert.Equal("FNAM", fnam.Signature);
        Assert.Equal("Flags", fnam.Name);
        Assert.Equal((ushort)35, fnam.MaxFormVersionExclusive);
        Assert.Equal([(0, "Unknown 0"), (4, "Loop")],
            fnam.InlineFlags!.Bits.Select(bit => (bit.Bit, bit.Label)));
    }

    [Fact]
    public void Nested_Version_Gates_Keep_Strictest_Lower_And_Upper_Bounds()
    {
        const string src = """
            wbRecord(TEST, 'Windows', [
              wbFromVersion(10, wbFromVersion(12,
                wbBelowVersion(30, wbBelowVersion(20, wbInteger(DATA, 'Window', itU32))))),
              wbBelowVersion(20, wbFromVersion(20, wbInteger(EMPT, 'Empty Window', itU32)))
            ])
            """;

        var rec = new IrBuilder().BuildRecord(src);

        Assert.Equal((ushort)12, rec.Members[0].MinFormVersion);
        Assert.Equal((ushort)20, rec.Members[0].MaxFormVersionExclusive);
        Assert.Equal((ushort)20, rec.Members[1].MinFormVersion);
        Assert.Equal((ushort)20, rec.Members[1].MaxFormVersionExclusive);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(65536)]
    public void BelowVersion_Rejects_Thresholds_Outside_UInt16(int threshold)
    {
        var builder = new IrBuilder();
        var rec = builder.BuildRecord(
            $"wbRecord(TEST, 'Invalid', [ wbBelowVersion({threshold}, wbInteger('Value', itU32)) ])");

        Assert.Equal("wbBelowVersion", Assert.IsType<UnknownMemberDef>(Assert.Single(rec.Members)).CallName);
        Assert.Equal(1, builder.UnknownCalls["wbBelowVersion"]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65535)]
    public void BelowVersion_Accepts_UInt16_Endpoints(int threshold)
    {
        var rec = new IrBuilder().BuildRecord(
            $"wbRecord(TEST, 'Boundary', [ wbBelowVersion({threshold}, wbInteger('Value', itU32)) ])");

        Assert.Equal((ushort)threshold, Assert.Single(rec.Members).MaxFormVersionExclusive);
    }

    [Theory]
    [InlineData("wbBelowVersion")]
    [InlineData("wbFromVersion")]
    public void Version_Gates_Do_Not_Borrow_A_Numeric_Threshold_From_The_Inner_Member(string builderName)
    {
        var builder = new IrBuilder();
        var rec = builder.BuildRecord(
            $"wbRecord(TEST, 'Invalid', [ {builderName}(BAD, wbUnused(3)) ])");

        Assert.Equal(builderName, Assert.IsType<UnknownMemberDef>(Assert.Single(rec.Members)).CallName);
        Assert.Equal(1, builder.UnknownCalls[builderName]);
    }

    [Theory]
    [InlineData("wbBelowVersion")]
    [InlineData("wbFromVersion")]
    public void Version_Gates_Reject_Fractional_Thresholds(string builderName)
    {
        var builder = new IrBuilder();
        var rec = builder.BuildRecord(
            $"wbRecord(TEST, 'Invalid', [ {builderName}(35.5, wbInteger('Value', itU32)) ])");

        Assert.Equal(builderName, Assert.IsType<UnknownMemberDef>(Assert.Single(rec.Members)).CallName);
    }

    [Fact]
    public void Direct_Literal_FormVersionDecider_Lowers_To_Complementary_TwoArm_Gates()
    {
        const string src = """
            wbRecord(ECZN, 'Encounter Zone', [
              wbUnion(DATA, '', wbFormVersionDecider(34), [
                wbStruct('Old', [
                  wbFormIDCkNoReach('Owner', [NPC_, FACT, NULL]),
                  wbFormIDCk('Location', [LCTN, NULL])
                ]),
                wbStruct('New', [
                  wbFormIDCkNoReach('Owner', [NPC_, FACT, NULL]),
                  wbFormIDCk('Location', [LCTN, NULL]),
                  wbInteger('Rank', itS8),
                  wbInteger('Min Level', itS8),
                  wbInteger('Flags', itU8),
                  wbInteger('Max Level', itS8)
                ])
              ])
            ])
            """;

        var builder = new IrBuilder();
        var rec = builder.BuildRecord(src);

        var union = Assert.IsType<UnionDef>(Assert.Single(rec.Members));
        Assert.Equal("DATA", union.Signature);
        Assert.Equal("wbFormVersionDecider", union.DeciderName);
        Assert.Equal(2, union.Variants.Count);

        var below = Assert.IsType<StructDef>(union.Variants[0]);
        Assert.Null(below.MinFormVersion);
        Assert.Equal((ushort)34, below.MaxFormVersionExclusive);
        Assert.Equal(2, below.Members.Count);

        var from = Assert.IsType<StructDef>(union.Variants[1]);
        Assert.Equal((ushort)34, from.MinFormVersion);
        Assert.Null(from.MaxFormVersionExclusive);
        Assert.Equal(6, from.Members.Count);
        Assert.Empty(builder.UnknownCalls);
    }

    [Theory]
    [InlineData("wbFormVersionDecider(10, 20)")]
    [InlineData("wbFormVersionDecider(VERSION)")]
    [InlineData("wbFormVersionDecider([10, 20])")]
    [InlineData("wbOtherDecider(34)")]
    public void Unsupported_Direct_Union_Deciders_Remain_Ungated_And_Opaque(string decider)
    {
        var rec = new IrBuilder().BuildRecord($"""
            wbRecord(TEST, 'Unsupported', [
              wbUnion(DATA, '', {decider}, [
                wbStruct('First', [wbInteger('A', itU32)]),
                wbStruct('Second', [wbInteger('B', itU32)])
              ])
            ])
            """);

        var union = Assert.IsType<UnionDef>(Assert.Single(rec.Members));
        Assert.Equal("<unknown-decider>", union.DeciderName);
        Assert.All(union.Variants, variant =>
        {
            Assert.Null(variant.MinFormVersion);
            Assert.Null(variant.MaxFormVersionExclusive);
        });
    }

    [Fact]
    public void Literal_FormVersionDecider_With_More_Than_Two_Arms_Remains_Opaque()
    {
        var rec = new IrBuilder().BuildRecord("""
            wbRecord(TEST, 'Unsupported', [
              wbUnion(DATA, '', wbFormVersionDecider(34), [
                wbInteger('A', itU32),
                wbInteger('B', itU32),
                wbInteger('C', itU32)
              ])
            ])
            """);

        var union = Assert.IsType<UnionDef>(Assert.Single(rec.Members));
        Assert.Equal("<unknown-decider>", union.DeciderName);
        Assert.All(union.Variants, variant =>
        {
            Assert.Null(variant.MinFormVersion);
            Assert.Null(variant.MaxFormVersionExclusive);
        });
    }

    [Fact]
    public void Named_General_Union_Decider_Remains_Named_And_Ungated()
    {
        var rec = new IrBuilder().BuildRecord("""
            wbRecord(TEST, 'General', [
              wbUnion(DATA, '', wbGeneralDecider, [
                wbInteger('A', itU32),
                wbInteger('B', itU32)
              ])
            ])
            """);

        var union = Assert.IsType<UnionDef>(Assert.Single(rec.Members));
        Assert.Equal("wbGeneralDecider", union.DeciderName);
        Assert.All(union.Variants, variant =>
        {
            Assert.Null(variant.MinFormVersion);
            Assert.Null(variant.MaxFormVersionExclusive);
        });
    }

    [Fact]
    public void Numeric_Display_Metadata_Does_Not_Become_A_Fixed_Byte_Width()
    {
        const string src = """
            wbRecord(MOVT, 'Movement Type', [
              wbFloat('Hours Until Reset', cpNormal, True, 1/24),
              wbFloat('Rotate while Moving Run', cpNormal, True, 180/pi, 2),
              wbByteArray(DATA, 'Payload', 4)
            ])
            """;

        var rec = new IrBuilder().BuildRecord(src);

        Assert.Null(Assert.IsType<FieldDef>(rec.Members[0]).FixedSize);
        Assert.Null(Assert.IsType<FieldDef>(rec.Members[1]).FixedSize);
        Assert.Equal(4, Assert.IsType<FieldDef>(rec.Members[2]).FixedSize);
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
