using System.Collections.Immutable;
using BethesdaMultitool.Core.Formats.Papyrus;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Papyrus;

public sealed class PexDecompilerTests
{
    [Fact]
    public void Decompile_ReconstructsDeclarationsAutoPropertiesAndExpressions()
    {
        var backingVariable = new PexVariable(
            Ref("::Enabled_var"), Ref("Bool"), 0, new PexBooleanValue(true), false);
        var property = new PexProperty(
            Ref("Enabled"), Ref("Bool"), Ref("Whether the script is enabled"), 0,
            PexPropertyFlags.Readable | PexPropertyFlags.Writable | PexPropertyFlags.Auto,
            Ref("::Enabled_var"), null, null);
        var function = Function(
            "OnInit",
            "None",
            [new PexTypedName(Ref("Result"), Ref("Int")), new PexTypedName(Ref("::temp0"), Ref("Int"))],
            [
                Instruction(PexOpCode.IAdd, Id("::temp0"), Int(1), Int(2)),
                Instruction(PexOpCode.Assign, Id("Result"), Id("::temp0")),
                Instruction(PexOpCode.Return, new PexNoneValue())
            ]);
        var file = File(Object(
            [backingVariable],
            [property],
            [new PexState(Ref(""), [function])]));

        var source = PexDecompiler.Decompile(file);

        Assert.Contains("ScriptName ExampleScript Extends Quest", source, StringComparison.Ordinal);
        Assert.Contains("Bool Property Enabled = True Auto", source, StringComparison.Ordinal);
        Assert.Contains("{Whether the script is enabled}", source, StringComparison.Ordinal);
        Assert.Contains("Event OnInit()", source, StringComparison.Ordinal);
        Assert.Contains("Int Result", source, StringComparison.Ordinal);
        Assert.DoesNotContain("temp0", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Result = 1 + 2", source, StringComparison.Ordinal);
        Assert.Contains("EndEvent", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Decompile_ReconstructsWhileLoopFromForwardConditionAndBackEdge()
    {
        var function = Function(
            "CountToTen",
            "None",
            [new PexTypedName(Ref("Count"), Ref("Int")), new PexTypedName(Ref("::temp0"), Ref("Bool"))],
            [
                Instruction(PexOpCode.CompareLessThan, Id("::temp0"), Id("Count"), Int(10)),
                Instruction(PexOpCode.JumpFalse, Id("::temp0"), Int(3)),
                Instruction(PexOpCode.IAdd, Id("Count"), Id("Count"), Int(1)),
                Instruction(PexOpCode.Jump, Int(-3)),
                Instruction(PexOpCode.Return, new PexNoneValue())
            ]);
        var file = File(Object(states: [new PexState(Ref(""), [function])]));

        var source = PexDecompiler.Decompile(file);

        Assert.Contains("While Count < 10", source, StringComparison.Ordinal);
        Assert.Contains("  Count = Count + 1", source, StringComparison.Ordinal);
        Assert.Contains("EndWhile", source, StringComparison.Ordinal);
        Assert.DoesNotContain("VM branch", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Decompile_ReconstructsIfElseAndMaterializesSharedTemporary()
    {
        var function = Function(
            "Choose",
            "Bool",
            [
                new PexTypedName(Ref("Input"), Ref("Bool")),
                new PexTypedName(Ref("::temp0"), Ref("Bool"))
            ],
            [
                Instruction(PexOpCode.JumpFalse, Id("Input"), Int(3)),
                Instruction(PexOpCode.Assign, Id("::temp0"), new PexBooleanValue(true)),
                Instruction(PexOpCode.Jump, Int(2)),
                Instruction(PexOpCode.Assign, Id("::temp0"), new PexBooleanValue(false)),
                Instruction(PexOpCode.Return, Id("::temp0"))
            ]);
        var file = File(Object(states: [new PexState(Ref(""), [function])]));

        var source = PexDecompiler.Decompile(file);

        Assert.Contains("Bool temp0", source, StringComparison.Ordinal);
        Assert.Contains("If Input", source, StringComparison.Ordinal);
        Assert.Contains("temp0 = True", source, StringComparison.Ordinal);
        Assert.Contains("Else", source, StringComparison.Ordinal);
        Assert.Contains("temp0 = False", source, StringComparison.Ordinal);
        Assert.Contains("Return temp0", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Decompile_MapsModernStructAndArrayOpcodesToPapyrusSyntax()
    {
        var function = Function(
            "Build",
            "None",
            [
                new PexTypedName(Ref("Items"), Ref("Int[]")),
                new PexTypedName(Ref("Entry"), Ref("ExampleScript#Entry")),
                new PexTypedName(Ref("Index"), Ref("Int"))
            ],
            [
                Instruction(PexOpCode.ArrayCreate, Id("Items"), Int(4)),
                Instruction(PexOpCode.ArraySetElement, Id("Items"), Int(0), Int(7)),
                Instruction(PexOpCode.ArrayFindElement, Id("Items"), Id("Index"), Int(7), Int(0)),
                Instruction(PexOpCode.StructCreate, Id("Entry")),
                Instruction(PexOpCode.StructSet, Id("Entry"), Id("Value"), Id("Index")),
                Instruction(PexOpCode.Return, new PexNoneValue())
            ]);
        var file = File(Object(states: [new PexState(Ref(""), [function])]));

        var source = PexDecompiler.Decompile(file);

        Assert.Contains("Items = new Int[4]", source, StringComparison.Ordinal);
        Assert.Contains("Items[0] = 7", source, StringComparison.Ordinal);
        Assert.Contains("Index = Items.Find(7, 0)", source, StringComparison.Ordinal);
        Assert.Contains("Entry = new ExampleScript:Entry", source, StringComparison.Ordinal);
        Assert.Contains("Entry.Value = Index", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Disassemble_ReplacesRelativeJumpsWithDeterministicLabels()
    {
        var function = Function(
            "Loop",
            "None",
            [new PexTypedName(Ref("Condition"), Ref("Bool"))],
            [
                Instruction(PexOpCode.JumpFalse, Id("Condition"), Int(3)),
                Instruction(PexOpCode.Nop),
                Instruction(PexOpCode.Jump, Int(-2)),
                Instruction(PexOpCode.Return, new PexNoneValue())
            ]);
        var file = File(Object(states: [new PexState(Ref(""), [function])]));

        var listing = PexDisassembler.Disassemble(file);

        Assert.Contains("L000:", listing, StringComparison.Ordinal);
        Assert.Contains("JUMPF Condition L001", listing, StringComparison.Ordinal);
        Assert.Contains("JUMP L000", listing, StringComparison.Ordinal);
        Assert.DoesNotContain("JUMPF Condition 3", listing, StringComparison.Ordinal);
    }

    [Fact]
    public void DecompileAndDisassemble_Fallout76UnmappedMetadata_RemainsVisible()
    {
        var file = PexParser.Parse(PexParserTests.BuildFixture(
            PexGameId.Fallout76,
            15,
            functionFlags: 0x28,
            stateNameIndex: 4,
            trailingStringReferences: [4]));

        var source = PexDecompiler.Decompile(file);
        var listing = PexDisassembler.Disassemble(file);

        Assert.Contains(
            "; Unmapped Fallout 76 PEX trailing state references (raw order):",
            source,
            StringComparison.Ordinal);
        Assert.Contains("; [4] \"Auto\"", source, StringComparison.Ordinal);
        Assert.Contains(
            "; Unmapped PEX function flag bits: 0x28 (raw byte 0x28)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(".fallout76TrailingStateReference 4 \"Auto\"", listing,
            StringComparison.Ordinal);
        Assert.Contains(".fallout76TrailingStateReferenceCount 1", listing,
            StringComparison.Ordinal);
        Assert.Contains(".rawFlags 0x28", listing, StringComparison.Ordinal);
    }

    [Fact]
    public void DecompileAndDisassemble_Fallout76ExplicitEmptyTail_RemainsVisible()
    {
        var file = PexParser.Parse(PexParserTests.BuildFixture(PexGameId.Fallout76, 15));

        var source = PexDecompiler.Decompile(file);
        var listing = PexDisassembler.Disassemble(file);

        Assert.Contains("; (explicit empty table)", source, StringComparison.Ordinal);
        Assert.Contains(".fallout76TrailingStateReferenceCount 0", listing,
            StringComparison.Ordinal);
    }

    private static PexFile File(PexObject obj)
    {
        return new PexFile(
            new PexHeader(PexEndianness.Big, 3, 2, PexGameId.Skyrim, 0,
                "ExampleScript.psc", "user", "computer"),
            ImmutableArray<string>.Empty,
            null,
            ImmutableArray<PexUserFlag>.Empty,
            [obj],
            0);
    }

    private static PexObject Object(
        ImmutableArray<PexVariable> variables = default,
        ImmutableArray<PexProperty> properties = default,
        ImmutableArray<PexState> states = default)
    {
        return new PexObject(
            Ref("ExampleScript"),
            0,
            Ref("Quest"),
            Ref(""),
            false,
            0,
            Ref(""),
            ImmutableArray<PexStruct>.Empty,
            variables.IsDefault ? [] : variables,
            properties.IsDefault ? [] : properties,
            states.IsDefault ? [] : states);
    }

    private static PexFunction Function(
        string name,
        string returnType,
        ImmutableArray<PexTypedName> locals,
        ImmutableArray<PexInstruction> instructions)
    {
        return new PexFunction(
            Ref(name),
            Ref(returnType),
            Ref(""),
            0,
            PexFunctionFlags.None,
            ImmutableArray<PexTypedName>.Empty,
            locals,
            instructions);
    }

    private static PexInstruction Instruction(PexOpCode opcode, params PexValue[] arguments)
    {
        return new PexInstruction(opcode, [.. arguments], ImmutableArray<PexValue>.Empty);
    }

    private static PexIdentifierValue Id(string value)
    {
        return new PexIdentifierValue(Ref(value));
    }

    private static PexIntegerValue Int(int value)
    {
        return new PexIntegerValue(value);
    }

    private static PexStringReference Ref(string value)
    {
        return new PexStringReference(0, value);
    }
}