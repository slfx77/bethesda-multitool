using EsmSchemaGen.Pascal;
using Xunit;

namespace EsmSchemaGen.Tests;

public class PascalLexerTests
{
    [Fact]
    public void Tokenizes_Call_With_String_And_Ident()
    {
        var tokens = PascalLexer.Tokenize("wbInteger('X', itU32)");

        Assert.Equal(
            [TokenKind.Ident, TokenKind.LParen, TokenKind.Str, TokenKind.Comma, TokenKind.Ident, TokenKind.RParen, TokenKind.Eof],
            tokens.Select(t => t.Kind));
        Assert.Equal("X", tokens[2].Text);
        Assert.Equal("itU32", tokens[4].Text);
    }

    [Fact]
    public void Decodes_Doubled_Quote_Escape()
    {
        var tokens = PascalLexer.Tokenize("'Can''t Drop'");
        Assert.Equal(TokenKind.Str, tokens[0].Kind);
        Assert.Equal("Can't Drop", tokens[0].Text);
    }

    [Fact]
    public void Parses_Hex_And_Decimal_Integers()
    {
        var tokens = PascalLexer.Tokenize("$80 255");
        Assert.Equal(TokenKind.Int, tokens[0].Kind);
        Assert.Equal(0x80, tokens[0].IntValue);
        Assert.Equal(TokenKind.Int, tokens[1].Kind);
        Assert.Equal(255, tokens[1].IntValue);
    }

    [Fact]
    public void Skips_Line_Brace_And_ParenStar_Comments()
    {
        var tokens = PascalLexer.Tokenize("a // line\n{ brace } b (* star *) c {$IFDEF X} d");
        Assert.Equal(
            ["a", "b", "c", "d"],
            tokens.Where(t => t.Kind == TokenKind.Ident).Select(t => t.Text));
    }

    [Fact]
    public void Recognizes_Assign_And_Float()
    {
        var tokens = PascalLexer.Tokenize("x := 1.5");
        Assert.Equal(TokenKind.Ident, tokens[0].Kind);
        Assert.Equal(TokenKind.Assign, tokens[1].Kind);
        Assert.Equal(TokenKind.Float, tokens[2].Kind);
        Assert.Equal(1.5, tokens[2].FloatValue);
    }
}
