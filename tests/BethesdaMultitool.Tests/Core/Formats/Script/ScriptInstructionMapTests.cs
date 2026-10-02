using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Script;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Script;

public sealed class ScriptInstructionMapTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Canonical_walk_maps_nested_call_and_SetRef_without_changing_text_or_bytes(bool big)
    {
        var bytes = new List<byte>();
        Add(bytes, ScriptOpcodes.ScriptName, big, []);
        Word(bytes, ScriptOpcodes.SetRef, big); Word(bytes, 1, big);
        var expression = new List<byte> { 0x20, 0x58 };
        Add(expression, 0x102E, big, []); // GetDead at SCDA offset 18.
        var condition = new List<byte>(); Word(condition, 0, big); Word(condition, (ushort)expression.Count, big);
        condition.AddRange(expression); Add(bytes, ScriptOpcodes.If, big, condition.ToArray());
        Add(bytes, ScriptOpcodes.EndIf, big, []);
        var input = bytes.ToArray(); var original = input.ToArray();
        var decompiler = new ScriptDecompiler([], [0x14], _ => "Player", big);
        var ordinary = decompiler.Decompile(input);
        var mapped = decompiler.DecompileWithMap(input);
        Assert.Equal(ordinary, mapped.Reconstruction);
        Assert.Equal(original, input);
        var nested = Assert.Single(mapped.Instructions, item => item.Kind == "ExpressionCall");
        Assert.Equal(18, nested.Offset); Assert.Equal(4, nested.Length); Assert.Equal(8, nested.ParentOffset);
        Assert.Equal(4, nested.ReferencePrefixOffset); Assert.Equal((ushort)0x102E, nested.Opcode);
        Assert.Equal("Decoded", nested.Status); Assert.Equal(2, nested.ReconstructionLineStart);
        Assert.Contains("If player.GetDead", mapped.Reconstruction);
        Assert.Null(Assert.Single(mapped.Instructions, item => item.Opcode == ScriptOpcodes.SetRef).ReconstructionLineStart);
        Assert.Equal(ordinary, decompiler.Decompile(input)); // Recorder state does not leak to later calls.
    }

    [Theory]
    [InlineData("unknown", "Unknown", 4)]
    [InlineData("truncated-payload", "Truncated", 6)]
    [InlineData("truncated-header", "Truncated", 3)]
    [InlineData("unknown-expression", "Partial", 9)]
    public void Opaque_and_truncated_ranges_stay_explicit(string kind, string status, int length)
    {
        byte[] bytes = kind switch
        {
            "unknown" => [0xFF, 0x77, 0, 0],
            "truncated-payload" => [0x1F, 0x10, 8, 0, 0, 0],
            "truncated-header" => [0x1F, 0x10, 0],
            _ => [0x16, 0, 5, 0, 0, 0, 1, 0, 0xFF]
        };
        var decompiler = new ScriptDecompiler([], [], _ => null);
        var text = decompiler.Decompile(bytes);
        var result = decompiler.DecompileWithMap(bytes);
        Assert.Equal(text, result.Reconstruction);
        var first = result.Instructions[0];
        Assert.Equal(status, first.Status); Assert.Equal(length, first.Length); Assert.Equal(0, first.Offset);
        Assert.All(result.Instructions, item => Assert.InRange(item.Offset + item.Length, 0, bytes.Length));
        if (kind == "unknown-expression")
            Assert.Contains(result.Instructions, item => item.Kind == "ExpressionUnknown" && item.Status == "Unknown" && item.ParentOffset == 0);
    }

    private static void Add(List<byte> bytes, ushort opcode, bool big, byte[] data)
    { Word(bytes, opcode, big); Word(bytes, (ushort)data.Length, big); bytes.AddRange(data); }
    private static void Word(List<byte> bytes, ushort value, bool big)
    {
        Span<byte> pair = stackalloc byte[2];
        if (big) BinaryPrimitives.WriteUInt16BigEndian(pair, value); else BinaryPrimitives.WriteUInt16LittleEndian(pair, value);
        bytes.AddRange(pair.ToArray());
    }
}
