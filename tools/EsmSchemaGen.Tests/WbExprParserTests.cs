using EsmSchemaGen.Pascal;
using Xunit;

namespace EsmSchemaGen.Tests;

public class WbExprParserTests
{
    [Fact]
    public void Parses_Call_With_Args_And_Fluent_Chain()
    {
        var value = WbExprParser.Parse("wbStruct(DATA, 'd', [wbInteger('a', itU8)]).SetRequired");

        var call = Assert.IsType<WbCall>(value);
        Assert.Equal("wbStruct", call.Name);
        Assert.Equal(3, call.Args.Count);
        Assert.Equal("DATA", Assert.IsType<WbIdent>(call.Args[0]).Name);
        Assert.Equal("d", Assert.IsType<WbStr>(call.Args[1]).Value);
        Assert.IsType<WbList>(call.Args[2]);
        Assert.Single(call.Modifiers);
        Assert.Equal("SetRequired", call.Modifiers[0].Name);
    }

    [Fact]
    public void Parses_Modifier_With_Arguments()
    {
        var value = WbExprParser.Parse("wbInteger('a', itS8).SetDefaultNativeValue(-1)");

        var call = Assert.IsType<WbCall>(value);
        Assert.Single(call.Modifiers);
        Assert.Equal("SetDefaultNativeValue", call.Modifiers[0].Name);
        var num = Assert.IsType<WbNum>(call.Modifiers[0].Args[0]);
        Assert.Equal(-1, num.IntValue);
    }

    [Fact]
    public void Bare_Identifier_Becomes_WbIdent()
    {
        Assert.Equal("itU32", Assert.IsType<WbIdent>(WbExprParser.Parse("itU32")).Name);
        Assert.IsType<WbNil>(WbExprParser.Parse("nil"));
        Assert.True(Assert.IsType<WbBool>(WbExprParser.Parse("True")).Value);
    }

    [Fact]
    public void Tolerates_Trailing_Comma_In_List()
    {
        var value = WbExprParser.Parse("['a', 'b', ]");
        Assert.Equal(2, Assert.IsType<WbList>(value).Items.Count);
    }

    [Fact]
    public void Tolerates_Procedural_Modifier_Args_And_Operator_Expressions()
    {
        // Mirrors the real TES3 constructs that broke the bootstrap: an operator expression in
        // .IncludeFlag and an inline anonymous function (with nested parens + begin/end) in a callback.
        const string src =
            "wbFloat('Version').IncludeFlag(dfInternalEditOnly, not wbAllowEditHEDRVersion)" +
            ".SetGetFormIDCallback(function(const aMainRecord: IwbMainRecord; out aFormID: TwbFormID): Boolean " +
            "begin Result := aMainRecord.GetGridCell(GridCell); aFormID := TwbFormID.Null; end)";

        var call = Assert.IsType<WbCall>(WbExprParser.Parse(src));
        Assert.Equal("wbFloat", call.Name);
        Assert.Equal(["IncludeFlag", "SetGetFormIDCallback"], call.Modifiers.Select(m => m.Name));
        // Procedural/expression args are discarded, not mis-parsed.
        Assert.All(call.Modifiers, m => Assert.Empty(m.Args));
    }
}
