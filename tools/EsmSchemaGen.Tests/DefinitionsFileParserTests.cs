using EsmSchemaGen;
using EsmSchemaGen.Ir;
using Xunit;

namespace EsmSchemaGen.Tests;

public class DefinitionsFileParserTests
{
    [Fact]
    public void Resolves_Symbol_Reference_From_Assignment()
    {
        // Mirrors wbDefinitionsTES3.pas: `wbEditorID := wbString(NAME, 'Editor ID')` then a record
        // that references `wbEditorID` by name.
        const string src = """
            wbEditorID := wbString(NAME, 'Editor ID');
            wbModel := wbString(MODL, 'Model');
            wbRecord(STAT, 'Static', [ wbEditorID, wbModel ]);
            """;

        var parser = new DefinitionsFileParser();
        parser.ParseFile(src);

        Assert.Equal(2, parser.Builder.Symbols.Count);
        var rec = Assert.Single(parser.Records);
        Assert.Equal(2, rec.Members.Count);

        var edid = Assert.IsType<FieldDef>(rec.Members[0]);
        Assert.Equal(PrimType.ZString, edid.Type);
        Assert.Equal("NAME", edid.Signature);

        var model = Assert.IsType<FieldDef>(rec.Members[1]);
        Assert.Equal("MODL", model.Signature);

        // Fully resolved: nothing fell through to Unknown.
        Assert.Empty(parser.Builder.UnknownCalls);
    }

    [Fact]
    public void Applies_Modifier_On_A_Symbol_Reference()
    {
        const string src = """
            wbModel := wbString(MODL, 'Model');
            wbRecord(STAT, 'Static', [ wbModel.SetRequired ]);
            """;

        var parser = new DefinitionsFileParser();
        parser.ParseFile(src);

        var model = Assert.IsType<FieldDef>(Assert.Single(parser.Records).Members[0]);
        Assert.Equal("MODL", model.Signature);
        Assert.True(model.Required);
    }

    [Fact]
    public void Builds_ByteColors_Common_Helper()
    {
        const string src = "wbRecord(WTHR, 'Weather', [ wbStruct(NAM0, 'Colors', [ wbByteColors('Sunrise') ]) ])";

        var parser = new DefinitionsFileParser();
        parser.ParseFile(src);

        var colors = Assert.IsType<StructDef>(Assert.Single(parser.Records).Members[0]);
        var sunrise = Assert.IsType<StructDef>(colors.Members[0]);
        Assert.Equal("Sunrise", sunrise.Name);
        Assert.Equal(4, sunrise.Members.Count);
        Assert.All(sunrise.Members.Take(3),
            m => Assert.Equal(PrimType.U8, Assert.IsType<FieldDef>(m).Type));
        Assert.Equal(1, Assert.IsType<UnusedDef>(sunrise.Members[3]).Size);
        Assert.Empty(parser.Builder.UnknownCalls);
    }

    [Fact]
    public void Builds_Obnd_And_GenericModel_Common_Helpers()
    {
        const string src = "wbRecord(ACTI, 'Activator', [ wbOBND, wbGenericModel ])";

        var parser = new DefinitionsFileParser();
        parser.ParseFile(src);
        var members = Assert.Single(parser.Records).Members;

        var obnd = Assert.IsType<StructDef>(members[0]);
        Assert.Equal("OBND", obnd.Signature);
        Assert.Equal(6, obnd.Members.Count);
        Assert.All(obnd.Members, m => Assert.Equal(PrimType.S16, Assert.IsType<FieldDef>(m).Type));

        var model = Assert.IsType<StructDef>(members[1]);
        Assert.Equal(["MODL", "MODB", "MODT", "MODS", "MODD"],
            model.Members.Select(m => ((FieldDef)m).Signature));
        Assert.Empty(parser.Builder.UnknownCalls);
    }

    [Fact]
    public void Resolves_Array_Element_That_Is_A_Symbol_Reference()
    {
        // wbArray with a symbol-ref element (not a wb* call) should resolve, not become "array-element".
        const string src = """
            wbCoordinate := wbInteger('Coord', itS32);
            wbRecord(CELL, 'Cell', [ wbArray(XCLC, 'Grid', wbCoordinate, 2) ]);
            """;

        var parser = new DefinitionsFileParser();
        parser.ParseFile(src);

        var array = Assert.IsType<ArrayDef>(Assert.Single(parser.Records).Members[0]);
        var element = Assert.IsType<FieldDef>(array.Element);
        Assert.Equal(PrimType.S32, element.Type);
        Assert.DoesNotContain("array-element", parser.Builder.UnknownCalls.Keys);
    }

    [Fact]
    public void Builds_Vec3_Common_Helper_As_Three_Floats()
    {
        const string src = "wbRecord(REFR, 'Ref', [ wbStruct(DATA, 'Position/Rotation', [ wbVec3('Position'), wbVec3('Rotation') ]) ])";

        var parser = new DefinitionsFileParser();
        parser.ParseFile(src);

        var data = Assert.IsType<StructDef>(Assert.Single(parser.Records).Members[0]);
        Assert.Equal(2, data.Members.Count);
        var position = Assert.IsType<StructDef>(data.Members[0]);
        Assert.Equal("Position", position.Name);
        Assert.Equal(["X", "Y", "Z"], position.Members.Select(m => m.Name));
        Assert.All(position.Members, m => Assert.Equal(PrimType.Float, Assert.IsType<FieldDef>(m).Type));
    }

    [Fact]
    public void Parses_Multiple_Records_And_Skips_Procedural_Modifier_Bodies()
    {
        const string src = """
            wbRecord(AAAA, 'A', [ wbInteger('x', itU8) ])
              .SetGetFormIDCallback(function(const aMainRecord: IwbMainRecord; out aFormID: TwbFormID): Boolean
                begin Result := True; end);
            wbRecord(BBBB, 'B', [ wbInteger('y', itU8) ]);
            """;

        var parser = new DefinitionsFileParser();
        parser.ParseFile(src);

        Assert.Equal(0, parser.ParseFailures);
        Assert.Equal(["AAAA", "BBBB"], parser.Records.Select(r => r.Signature));
    }

    [Fact]
    public void Parses_Division_Expressions_Without_Dropping_Records_Or_Version_Gates()
    {
        const string src = """
            wbRecord(SMQN, 'Story Manager Quest Node', [
              wbFloat(RNAM, 'Hours until reset', cpNormal, True, 1/24)
            ]);
            wbRecord(MOVT, 'Movement Type', [
              wbStruct(SPED, 'Default Data', [
                wbFloat('Rotate In Place Run'),
                wbFromVersion(28, wbFloat('Rotate while Moving Run', cpNormal, True, 180/pi, 2))
              ])
            ]);
            """;

        var parser = new DefinitionsFileParser();
        parser.ParseFile(src);

        Assert.Equal(0, parser.ParseFailures);
        Assert.Equal(2, parser.Records.Count);
        Assert.Contains(parser.Records, record => record.Signature == "SMQN");
        var movement = Assert.Single(parser.Records, record => record.Signature == "MOVT");
        var sped = Assert.IsType<StructDef>(Assert.Single(
            movement.Members,
            member => member.Signature == "SPED"));
        var gated = Assert.IsType<FieldDef>(Assert.Single(
            sped.Members,
            member => member.Name == "Rotate while Moving Run"));
        Assert.Equal((ushort)28, gated.MinFormVersion);
        Assert.Null(gated.FixedSize);
    }

    [Fact]
    public void Parses_SelfContained_Tes5_Sound_Upper_And_Lower_Version_Gates()
    {
        const string src = """
            wbRecord(SNDR, 'Sound Descriptor', [
              wbBelowVersion(35, FNAM,
                wbInteger('Flags', itU32, wbFlags([
                  0, 'Unknown 0',
                  1, 'Unknown 1',
                  2, 'Unknown 2',
                  4, 'Loop'
                ]))),
              wbFromVersion(34, LNAM,
                wbStruct('Values', [wbInteger('Looping', itU8)]))
            ]);
            """;

        var parser = new DefinitionsFileParser();
        parser.ParseFile(src);

        Assert.Equal(0, parser.ParseFailures);
        var sound = Assert.Single(parser.Records);
        var fnam = Assert.IsType<FieldDef>(sound.Members[0]);
        Assert.Equal("FNAM", fnam.Signature);
        Assert.Equal((ushort)35, fnam.MaxFormVersionExclusive);
        Assert.Equal([0, 1, 2, 4], fnam.InlineFlags!.Bits.Select(bit => bit.Bit));
        var lnam = Assert.IsType<StructDef>(sound.Members[1]);
        Assert.Equal("LNAM", lnam.Signature);
        Assert.Equal((ushort)34, lnam.MinFormVersion);
        Assert.Empty(parser.Builder.UnknownCalls);
    }
}
