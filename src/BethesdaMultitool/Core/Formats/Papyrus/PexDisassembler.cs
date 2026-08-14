using System.Text;

namespace BethesdaMultitool.Core.Formats.Papyrus;

/// <summary>Writes a lossless, label-based listing of the Papyrus VM instructions in a PEX model.</summary>
public static class PexDisassembler
{
    public static string Disassemble(PexFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var output = new StringBuilder();
        output.AppendLine($"; PEX {file.Header.GameId} {file.Header.MajorVersion}.{file.Header.MinorVersion} " +
                          $"{file.Header.Endianness}");
        output.AppendLine($"; Source: {file.Header.SourceFileName}");
        foreach (var obj in file.Objects)
        {
            output.AppendLine();
            output.Append(".object ").Append(obj.Name.Value);
            if (!string.IsNullOrEmpty(obj.ParentClassName.Value))
            {
                output.Append(" extends ").Append(obj.ParentClassName.Value);
            }

            output.AppendLine();
            if (obj.HasFallout76TrailingStateReferenceTable)
            {
                output.Append("  .fallout76TrailingStateReferenceCount ")
                    .AppendLine(obj.Fallout76TrailingStateReferences.Length.ToString());
                foreach (var reference in obj.Fallout76TrailingStateReferences)
                {
                    output.Append("  .fallout76TrailingStateReference ")
                        .Append(reference.Index).Append(" \"")
                        .Append(PexDecompiler.EscapeString(reference.Value)).AppendLine("\"");
                }
            }

            foreach (var state in obj.States)
            {
                output.Append("  .state ").AppendLine(state.Name.Value);
                foreach (var function in state.Functions)
                {
                    WriteFunction(output, function, function.Name?.Value ?? "<unnamed>", 4);
                }

                output.AppendLine("  .endState");
            }

            foreach (var property in obj.Properties)
            {
                if (property.ReadFunction is { } getter)
                {
                    WriteFunction(output, getter, $"{property.Name.Value}.Get", 2);
                }

                if (property.WriteFunction is { } setter)
                {
                    WriteFunction(output, setter, $"{property.Name.Value}.Set", 2);
                }
            }

            output.AppendLine(".endObject");
        }

        return output.ToString().TrimEnd();
    }

    private static void WriteFunction(StringBuilder output, PexFunction function, string name, int indent)
    {
        AppendIndent(output, indent).Append(".function ").AppendLine(name);
        AppendIndent(output, indent + 2).Append(".return ").AppendLine(function.ReturnTypeName.Value);
        AppendIndent(output, indent + 2).Append(".rawFlags 0x")
            .AppendLine(function.RawFlags.ToString("X2"));
        foreach (var parameter in function.Parameters)
        {
            AppendIndent(output, indent + 2).Append(".param ").Append(parameter.Name.Value).Append(' ')
                .AppendLine(parameter.TypeName.Value);
        }

        foreach (var local in function.LocalVariables)
        {
            AppendIndent(output, indent + 2).Append(".local ").Append(local.Name.Value).Append(' ')
                .AppendLine(local.TypeName.Value);
        }

        var labels = FindLabels(function.Instructions);
        for (var ip = 0; ip < function.Instructions.Length; ip++)
        {
            if (labels.TryGetValue(ip, out var label))
            {
                AppendIndent(output, indent + 2).Append(label).AppendLine(":");
            }

            var instruction = function.Instructions[ip];
            AppendIndent(output, indent + 4).Append(ip.ToString("D4")).Append("  ")
                .Append(OpCodeName(instruction.OpCode));
            WriteArguments(output, instruction, ip, labels);
            output.AppendLine();
        }

        if (labels.TryGetValue(function.Instructions.Length, out var endLabel))
        {
            AppendIndent(output, indent + 2).Append(endLabel).AppendLine(":");
        }

        AppendIndent(output, indent).AppendLine(".endFunction");
    }

    private static void WriteArguments(
        StringBuilder output,
        PexInstruction instruction,
        int ip,
        IReadOnlyDictionary<int, string> labels)
    {
        if (instruction.OpCode == PexOpCode.Jump &&
            instruction.Arguments[0] is PexIntegerValue jumpOffset)
        {
            output.Append(' ').Append(LabelFor(ip + jumpOffset.Value, labels));
            return;
        }

        if (instruction.OpCode is PexOpCode.JumpTrue or PexOpCode.JumpFalse &&
            instruction.Arguments[1] is PexIntegerValue conditionalOffset)
        {
            output.Append(' ').Append(FormatAssemblyValue(instruction.Arguments[0]))
                .Append(' ').Append(LabelFor(ip + conditionalOffset.Value, labels));
            return;
        }

        foreach (var argument in instruction.Arguments)
        {
            output.Append(' ').Append(FormatAssemblyValue(argument));
        }

        foreach (var argument in instruction.VariadicArguments)
        {
            output.Append(' ').Append(FormatAssemblyValue(argument));
        }

        if (!instruction.VariadicArguments.IsEmpty)
        {
            output.Append(" ; ").Append(instruction.VariadicArguments.Length).Append(" variable args");
        }
    }

    private static SortedDictionary<int, string> FindLabels(
        System.Collections.Immutable.ImmutableArray<PexInstruction> instructions)
    {
        var targets = new SortedSet<int>();
        for (var ip = 0; ip < instructions.Length; ip++)
        {
            var instruction = instructions[ip];
            PexIntegerValue? offset = instruction.OpCode switch
            {
                PexOpCode.Jump when instruction.Arguments[0] is PexIntegerValue jump => jump,
                PexOpCode.JumpTrue or PexOpCode.JumpFalse
                    when instruction.Arguments[1] is PexIntegerValue conditional => conditional,
                _ => null
            };
            if (offset is not null)
            {
                var target = ip + offset.Value;
                if (target >= 0 && target <= instructions.Length)
                {
                    targets.Add(target);
                }
            }
        }

        var labels = new SortedDictionary<int, string>();
        var index = 0;
        foreach (var target in targets)
        {
            labels[target] = $"L{index++:D3}";
        }

        return labels;
    }

    private static string LabelFor(int target, IReadOnlyDictionary<int, string> labels)
        => labels.GetValueOrDefault(target) ?? $"@{target}";

    private static string FormatAssemblyValue(PexValue value) => value switch
    {
        PexIdentifierValue identifier => identifier.Identifier.Value,
        PexStringValue text => $"\"{PexDecompiler.EscapeString(text.String.Value)}\"",
        _ => PexDecompiler.FormatValue(value)
    };

    private static StringBuilder AppendIndent(StringBuilder output, int spaces)
        => output.Append(' ', spaces);

    private static string OpCodeName(PexOpCode opcode) => opcode switch
    {
        PexOpCode.Nop => "NOOP",
        PexOpCode.IAdd => "IADD",
        PexOpCode.FAdd => "FADD",
        PexOpCode.ISubtract => "ISUBTRACT",
        PexOpCode.FSubtract => "FSUBTRACT",
        PexOpCode.IMultiply => "IMULTIPLY",
        PexOpCode.FMultiply => "FMULTIPLY",
        PexOpCode.IDivide => "IDIVIDE",
        PexOpCode.FDivide => "FDIVIDE",
        PexOpCode.IMod => "IMOD",
        PexOpCode.Not => "NOT",
        PexOpCode.INegate => "INEGATE",
        PexOpCode.FNegate => "FNEGATE",
        PexOpCode.Assign => "ASSIGN",
        PexOpCode.Cast => "CAST",
        PexOpCode.CompareEqual => "COMPAREEQ",
        PexOpCode.CompareLessThan => "COMPARELT",
        PexOpCode.CompareLessThanOrEqual => "COMPARELTE",
        PexOpCode.CompareGreaterThan => "COMPAREGT",
        PexOpCode.CompareGreaterThanOrEqual => "COMPAREGTE",
        PexOpCode.Jump => "JUMP",
        PexOpCode.JumpTrue => "JUMPT",
        PexOpCode.JumpFalse => "JUMPF",
        PexOpCode.CallMethod => "CALLMETHOD",
        PexOpCode.CallParent => "CALLPARENT",
        PexOpCode.CallStatic => "CALLSTATIC",
        PexOpCode.Return => "RETURN",
        PexOpCode.StringConcat => "STRCAT",
        PexOpCode.PropertyGet => "PROPGET",
        PexOpCode.PropertySet => "PROPSET",
        PexOpCode.ArrayCreate => "ARRAYCREATE",
        PexOpCode.ArrayLength => "ARRAYLENGTH",
        PexOpCode.ArrayGetElement => "ARRAYGETELEMENT",
        PexOpCode.ArraySetElement => "ARRAYSETELEMENT",
        PexOpCode.ArrayFindElement => "ARRAYFINDELEMENT",
        PexOpCode.ArrayRFindElement => "ARRAYRFINDELEMENT",
        PexOpCode.Is => "IS",
        PexOpCode.StructCreate => "STRUCTCREATE",
        PexOpCode.StructGet => "STRUCTGET",
        PexOpCode.StructSet => "STRUCTSET",
        PexOpCode.ArrayFindStruct => "ARRAYFINDSTRUCT",
        PexOpCode.ArrayRFindStruct => "ARRAYRFINDSTRUCT",
        PexOpCode.ArrayAdd => "ARRAYADDELEMENTS",
        PexOpCode.ArrayInsert => "ARRAYINSERTELEMENT",
        PexOpCode.ArrayRemoveLast => "ARRAYREMOVELASTELEMENT",
        PexOpCode.ArrayRemove => "ARRAYREMOVEELEMENTS",
        PexOpCode.ArrayClear => "ARRAYCLEARELEMENTS",
        PexOpCode.ArrayGetAllMatchingStructs => "ARRAYGETALLMATCHINGSTRUCTS",
        _ => opcode.ToString().ToUpperInvariant()
    };
}
