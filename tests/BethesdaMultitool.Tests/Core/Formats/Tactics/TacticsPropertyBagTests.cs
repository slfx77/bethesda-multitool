using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BethesdaMultitool.Core.Formats.Tactics;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Tactics;

/// <summary>
///     Vectors for Fallout Tactics' <c>.ent</c> / <c>.chr</c> property bags, shaped after
///     <c>entities_0.bos</c> measured 2026-09-06: 1,498 entities (23,740 properties) and 39
///     characters (886), every one consumed exactly.
/// </summary>
public sealed class TacticsPropertyBagTests
{
    private static byte[] Bag(string kind, (string Name, uint Type, byte[] Value)[] properties,
        string tag = "<entity>", char version = '2', int trailing = 0)
    {
        var b = new List<byte>();
        b.AddRange(Encoding.ASCII.GetBytes(tag));
        b.Add(0);
        b.Add((byte)version);
        b.Add(0);
        b.AddRange(BitConverter.GetBytes((uint)kind.Length));
        b.AddRange(Encoding.ASCII.GetBytes(kind));

        b.AddRange("<esh>"u8);
        b.Add(0);
        b.Add((byte)'1');
        b.Add(0);
        b.AddRange(BitConverter.GetBytes((uint)properties.Length));

        foreach (var (name, type, value) in properties)
        {
            b.AddRange(BitConverter.GetBytes((uint)name.Length));
            b.AddRange(Encoding.ASCII.GetBytes(name));
            b.AddRange(BitConverter.GetBytes(type));
            b.AddRange(BitConverter.GetBytes((uint)value.Length));
            b.AddRange(value);
        }

        b.AddRange(new byte[trailing]);
        return [.. b];
    }

    [Fact]
    public void Parse_ReadsEveryPropertyFromItsOwnDeclaredSize()
    {
        var bag = TacticsPropertyBag.Parse(
            Bag("Actor",
            [
                ("Display Name", 4, Encoding.ASCII.GetBytes("Raider")),
                ("XP Reward", 3, [10, 0, 0, 0]),
            ]), "raider.ent");

        Assert.Equal("Actor", bag.Kind);
        Assert.Equal(2, bag.Properties.Count);
        Assert.Equal("Display Name", bag.Properties[0].Name);
        Assert.Equal("Raider", bag.Properties[0].AsText);
        Assert.Equal(3u, bag.Properties[1].Type);
    }

    [Fact]
    public void Parse_NeedsNoTypeSizeTableEvenForTheNestedType()
    {
        // ⛔ The backlog called for a type-size table and for type 11 to be walked as a recursive
        // nested <esh>. Both are wrong: `size` already spans the nested content, and the special
        // case parses only 107 of the 1,498 retail files while the flat reading parses all of them.
        // Here type 11's payload IS a nested block, and the flat walk still consumes it.
        var nested = Bag("Inner", [("Leaf", 3, [1, 2, 3, 4])])[..];
        var bag = TacticsPropertyBag.Parse(
            Bag("Actor", [("Attached", 11, nested), ("After", 2, [9, 9, 9, 9])]), "x.ent");

        Assert.Equal(2, bag.Properties.Count);
        Assert.Equal(11u, bag.Properties[0].Type);
        Assert.Equal(nested.Length, bag.Properties[0].Value.Length);
        Assert.Equal("After", bag.Properties[1].Name);
    }

    [Fact]
    public void Parse_AcceptsTheCharacterTagToo()
    {
        var bag = TacticsPropertyBag.Parse(
            Bag("Player", [("Race Type", 3, [1, 0, 0, 0])], tag: "<character>"), "hero.chr");

        Assert.Equal("Player", bag.Kind);
        Assert.Equal("Race Type", bag.Properties[0].Name);
    }

    [Fact]
    public void Parse_RejectsTrailingBytes()
    {
        // The properties must consume the file exactly — that is what makes the walk a proof.
        var error = Assert.Throws<InvalidDataException>(
            () => TacticsPropertyBag.Parse(Bag("Actor", [("A", 3, [1, 2, 3, 4])], trailing: 4), "bad.ent"));
        Assert.Contains("end at", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsAPayloadThatRunsPastTheFile()
    {
        var bytes = Bag("Actor", [("A", 3, [1, 2, 3, 4])]);
        bytes[^8] = 0xFF;   // inflate the declared size

        Assert.Throws<InvalidDataException>(() => TacticsPropertyBag.Parse(bytes, "bad.ent"));
    }

    [Fact]
    public void Parse_RejectsSomethingThatIsNotAPropertyBag()
    {
        Assert.Throws<InvalidDataException>(() => TacticsPropertyBag.Parse("<zar>\0"u8.ToArray(), "x.zar"));
    }

    [Fact]
    public void Find_ReturnsThePropertyByName()
    {
        var bag = TacticsPropertyBag.Parse(
            Bag("Actor", [("Sprite", 4, Encoding.ASCII.GetBytes("raider"))]), "x.ent");

        Assert.Equal("raider", bag.Find("Sprite")!.Value.AsText);
        Assert.Null(bag.Find("Missing"));
    }

    [Fact]
    public void IsPropertyBag_RecognisesBothTags()
    {
        Assert.True(TacticsPropertyBag.IsPropertyBag(Bag("A", [])));
        Assert.True(TacticsPropertyBag.IsPropertyBag(Bag("A", [], tag: "<character>")));
        Assert.False(TacticsPropertyBag.IsPropertyBag("<zar>"u8.ToArray()));
    }
}
