using System.Collections.Immutable;

namespace BethesdaMultitool.Core.Formats.Papyrus;

/// <summary>
///     Conservative function-body reconstruction for <see cref="PexDecompiler" />. Compiler
///     temporaries are inlined when their single definition and use stay in one straight-line
///     region; branch-crossing or multiply-used temporaries are materialized as ordinary locals.
/// </summary>
internal sealed class PexFunctionDecompiler
{
    private const int EqualityPrecedence = 40;
    private const int RelationalPrecedence = 50;
    private const int AddPrecedence = 60;
    private const int MultiplyPrecedence = 70;
    private const int UnaryPrecedence = 80;
    private const int MemberPrecedence = 90;
    private const int AtomPrecedence = 100;

    private readonly Dictionary<string, Expression> _expressions =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly PexFunction _function;
    private readonly List<string> _lines = [];
    private readonly HashSet<string> _materializedTemporaries;
    private readonly Dictionary<string, string> _types = new(StringComparer.OrdinalIgnoreCase);
    private int _indent;

    public PexFunctionDecompiler(PexFunction function, PexObject obj)
    {
        _function = function;
        foreach (var variable in obj.Variables)
        {
            _types[variable.Name.Value] = variable.TypeName.Value;
        }

        foreach (var parameter in function.Parameters)
        {
            _types[parameter.Name.Value] = parameter.TypeName.Value;
        }

        foreach (var local in function.LocalVariables)
        {
            _types[local.Name.Value] = local.TypeName.Value;
        }

        _materializedTemporaries = FindMaterializedTemporaries(function.Instructions);
    }

    public IReadOnlyList<string> Decompile()
    {
        WriteLocalDeclarations();
        var firstStatement = _lines.Count;
        DecompileRange(0, _function.Instructions.Length, null);
        if (_lines.Count == firstStatement)
        {
            Line("; Empty function");
        }

        return _lines;
    }

    private void WriteLocalDeclarations()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var local in _function.LocalVariables)
        {
            var rawName = local.Name.Value;
            if (PexDecompiler.IsCompilerTemporary(rawName) &&
                !_materializedTemporaries.Contains(rawName))
            {
                continue;
            }

            var name = PexDecompiler.FormatIdentifier(rawName);
            if (name.Equals("None", StringComparison.OrdinalIgnoreCase) || !seen.Add(name))
            {
                continue;
            }

            Line($"{PexDecompiler.FormatType(local.TypeName.Value)} {name}");
        }

        if (_lines.Count > 0 && _function.Instructions.Length > 0)
        {
            Line();
        }
    }

    private void DecompileRange(int start, int end, LoopContext? loop)
    {
        var instructions = _function.Instructions;
        var ip = start;
        while (ip < end)
        {
            var instruction = instructions[ip];
            if (instruction.OpCode is PexOpCode.JumpTrue or PexOpCode.JumpFalse &&
                TryGetConditionalTarget(ip, instruction, out var branchTarget) &&
                branchTarget > ip && branchTarget <= end)
            {
                var condition = ReadValue(instruction.Arguments[0]);
                if (instruction.OpCode == PexOpCode.JumpTrue)
                {
                    condition = Unary("!", condition);
                }

                var stateAtBranch = CloneExpressions();
                if (TryGetLoopTail(ip, branchTarget, out var loopTail, out var loopStart))
                {
                    Line($"While {condition.Text}");
                    _indent++;
                    DecompileRange(ip + 1, loopTail, new LoopContext(loopStart, branchTarget));
                    _indent--;
                    Line("EndWhile");
                    RestoreExpressions(stateAtBranch);
                    ip = branchTarget;
                    continue;
                }

                if (TryGetElseTail(branchTarget, end, out var thenEnd, out var elseEnd))
                {
                    Line($"If {condition.Text}");
                    _indent++;
                    DecompileRange(ip + 1, thenEnd, loop);
                    _indent--;
                    RestoreExpressions(stateAtBranch);
                    Line("Else");
                    _indent++;
                    DecompileRange(branchTarget, elseEnd, loop);
                    _indent--;
                    Line("EndIf");
                    RestoreExpressions(stateAtBranch);
                    ip = elseEnd;
                    continue;
                }

                Line($"If {condition.Text}");
                _indent++;
                DecompileRange(ip + 1, branchTarget, loop);
                _indent--;
                Line("EndIf");
                RestoreExpressions(stateAtBranch);
                ip = branchTarget;
                continue;
            }

            if (instruction.OpCode == PexOpCode.Jump && TryGetJumpTarget(ip, instruction, out var target))
            {
                if (target == ip + 1 || target == end)
                {
                    ip++;
                    continue;
                }

                if (loop is { } loopRange && target <= loopRange.Start)
                {
                    Line("Continue");
                }
                else if (loop is { } enclosingLoop && target >= enclosingLoop.End)
                {
                    Line("Break");
                }
                else
                {
                    Line($"; VM branch to instruction {target}");
                }

                ip++;
                continue;
            }

            DecompileInstruction(instruction);
            ip++;
        }
    }

    private void DecompileInstruction(PexInstruction instruction)
    {
        var a = instruction.Arguments;
        switch (instruction.OpCode)
        {
            case PexOpCode.Nop:
                return;
            case PexOpCode.IAdd:
            case PexOpCode.FAdd:
            case PexOpCode.StringConcat:
                Assign(a[0], Binary(ReadValue(a[1]), "+", ReadValue(a[2]), AddPrecedence));
                return;
            case PexOpCode.ISubtract:
            case PexOpCode.FSubtract:
                Assign(a[0], Binary(ReadValue(a[1]), "-", ReadValue(a[2]), AddPrecedence));
                return;
            case PexOpCode.IMultiply:
            case PexOpCode.FMultiply:
                Assign(a[0], Binary(ReadValue(a[1]), "*", ReadValue(a[2]), MultiplyPrecedence));
                return;
            case PexOpCode.IDivide:
            case PexOpCode.FDivide:
                Assign(a[0], Binary(ReadValue(a[1]), "/", ReadValue(a[2]), MultiplyPrecedence));
                return;
            case PexOpCode.IMod:
                Assign(a[0], Binary(ReadValue(a[1]), "%", ReadValue(a[2]), MultiplyPrecedence));
                return;
            case PexOpCode.Not:
                Assign(a[0], Unary("!", ReadValue(a[1])));
                return;
            case PexOpCode.INegate:
            case PexOpCode.FNegate:
                Assign(a[0], Unary("-", ReadValue(a[1])));
                return;
            case PexOpCode.Assign:
                Assign(a[0], ReadValue(a[1]));
                return;
            case PexOpCode.Cast:
                Assign(a[0], Cast(a[0], ReadValue(a[1])));
                return;
            case PexOpCode.CompareEqual:
                Assign(a[0], Binary(ReadValue(a[1]), "==", ReadValue(a[2]), EqualityPrecedence));
                return;
            case PexOpCode.CompareLessThan:
                Assign(a[0], Binary(ReadValue(a[1]), "<", ReadValue(a[2]), RelationalPrecedence));
                return;
            case PexOpCode.CompareLessThanOrEqual:
                Assign(a[0], Binary(ReadValue(a[1]), "<=", ReadValue(a[2]), RelationalPrecedence));
                return;
            case PexOpCode.CompareGreaterThan:
                Assign(a[0], Binary(ReadValue(a[1]), ">", ReadValue(a[2]), RelationalPrecedence));
                return;
            case PexOpCode.CompareGreaterThanOrEqual:
                Assign(a[0], Binary(ReadValue(a[1]), ">=", ReadValue(a[2]), RelationalPrecedence));
                return;
            case PexOpCode.Jump:
            case PexOpCode.JumpTrue:
            case PexOpCode.JumpFalse:
                return;
            case PexOpCode.CallMethod:
                Assign(a[2], Call(Member(ReadValue(a[1]), PexDecompiler.RawName(a[0])),
                    instruction.VariadicArguments), true);
                return;
            case PexOpCode.CallParent:
                Assign(a[1], Call(new Expression($"Parent.{PexDecompiler.RawName(a[0])}", MemberPrecedence),
                    instruction.VariadicArguments), true);
                return;
            case PexOpCode.CallStatic:
            {
                var type = PexDecompiler.FormatType(PexDecompiler.RawName(a[0]));
                Assign(a[2], Call(new Expression($"{type}.{PexDecompiler.RawName(a[1])}", MemberPrecedence),
                    instruction.VariadicArguments), true);
                return;
            }
            case PexOpCode.Return:
                if (_function.ReturnTypeName.Value.Equals("none", StringComparison.OrdinalIgnoreCase) ||
                    a[0] is PexNoneValue)
                {
                    Line("Return");
                }
                else
                {
                    Line($"Return {ReadValue(a[0]).Text}");
                }

                return;
            case PexOpCode.PropertyGet:
                Assign(a[2], Member(ReadValue(a[1]), PexDecompiler.RawName(a[0])));
                return;
            case PexOpCode.PropertySet:
                Line($"{Member(ReadValue(a[1]), PexDecompiler.RawName(a[0])).Text} = {ReadValue(a[2]).Text}");
                return;
            case PexOpCode.ArrayCreate:
            {
                var elementType = TypeOf(a[0]);
                if (elementType.EndsWith("[]", StringComparison.Ordinal))
                {
                    elementType = elementType[..^2];
                }

                Assign(a[0], new Expression(
                    $"new {PexDecompiler.FormatType(elementType)}[{ReadValue(a[1]).Text}]",
                    AtomPrecedence));
                return;
            }
            case PexOpCode.ArrayLength:
                Assign(a[0], Member(ReadValue(a[1]), "Length"));
                return;
            case PexOpCode.ArrayGetElement:
                Assign(a[0], Index(ReadValue(a[1]), ReadValue(a[2])));
                return;
            case PexOpCode.ArraySetElement:
                Line($"{Index(ReadValue(a[0]), ReadValue(a[1])).Text} = {ReadValue(a[2]).Text}");
                return;
            case PexOpCode.ArrayFindElement:
                Assign(a[1], Call(Member(ReadValue(a[0]), "Find"), [a[2], a[3]]));
                return;
            case PexOpCode.ArrayRFindElement:
                Assign(a[1], Call(Member(ReadValue(a[0]), "RFind"), [a[2], a[3]]));
                return;
            case PexOpCode.Is:
            {
                var left = ReadValue(a[1]);
                var type = PexDecompiler.FormatType(PexDecompiler.RawName(a[2]));
                Assign(a[0], new Expression($"{Wrap(left, RelationalPrecedence)} is {type}",
                    RelationalPrecedence));
                return;
            }
            case PexOpCode.StructCreate:
                Assign(a[0], new Expression($"new {PexDecompiler.FormatType(TypeOf(a[0]))}", AtomPrecedence));
                return;
            case PexOpCode.StructGet:
                Assign(a[0], Member(ReadValue(a[1]), PexDecompiler.RawName(a[2])));
                return;
            case PexOpCode.StructSet:
                Line($"{Member(ReadValue(a[0]), PexDecompiler.RawName(a[1])).Text} = {ReadValue(a[2]).Text}");
                return;
            case PexOpCode.ArrayFindStruct:
                Assign(a[1], Call(Member(ReadValue(a[0]), "FindStruct"), [a[2], a[3], a[4]]));
                return;
            case PexOpCode.ArrayRFindStruct:
                Assign(a[1], Call(Member(ReadValue(a[0]), "RFindStruct"), [a[2], a[3], a[4]]));
                return;
            case PexOpCode.ArrayAdd:
                Line(Call(Member(ReadValue(a[0]), "Add"), [a[1], a[2]]).Text);
                return;
            case PexOpCode.ArrayInsert:
                Line(Call(Member(ReadValue(a[0]), "Insert"), [a[1], a[2]]).Text);
                return;
            case PexOpCode.ArrayRemoveLast:
                Line(Call(Member(ReadValue(a[0]), "RemoveLast"), []).Text);
                return;
            case PexOpCode.ArrayRemove:
                Line(Call(Member(ReadValue(a[0]), "Remove"), [a[1], a[2]]).Text);
                return;
            case PexOpCode.ArrayClear:
                Line(Call(Member(ReadValue(a[0]), "Clear"), []).Text);
                return;
            case PexOpCode.ArrayGetAllMatchingStructs:
                Assign(a[1], Call(Member(ReadValue(a[0]), "GetMatchingStructs"),
                    [a[2], a[3], a[4], a[5]]));
                return;
            default:
                Line($"; Unsupported VM opcode {(byte)instruction.OpCode}: {instruction.OpCode}");
                return;
        }
    }

    private void Assign(PexValue destination, Expression value, bool sideEffect = false)
    {
        if (destination is not PexIdentifierValue identifier)
        {
            if (sideEffect)
            {
                Line(value.Text);
            }

            return;
        }

        var rawName = identifier.Identifier.Value;
        if (rawName.Equals("::nonevar", StringComparison.OrdinalIgnoreCase))
        {
            if (sideEffect)
            {
                Line(value.Text);
            }

            return;
        }

        if (PexDecompiler.IsCompilerTemporary(rawName) && !_materializedTemporaries.Contains(rawName))
        {
            _expressions[rawName] = value;
            return;
        }

        Line($"{PexDecompiler.FormatIdentifier(rawName)} = {value.Text}");
    }

    private Expression ReadValue(PexValue value)
    {
        if (value is PexIdentifierValue identifier)
        {
            var rawName = identifier.Identifier.Value;
            if (!_materializedTemporaries.Contains(rawName) &&
                _expressions.Remove(rawName, out var expression))
            {
                return expression;
            }
        }

        return new Expression(PexDecompiler.FormatValue(value), AtomPrecedence);
    }

    private Expression Cast(PexValue destination, Expression value)
    {
        if (value.Text.Equals("None", StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        var type = PexDecompiler.FormatType(TypeOf(destination));
        return new Expression($"{Wrap(value, RelationalPrecedence)} as {type}", RelationalPrecedence);
    }

    private string TypeOf(PexValue value)
    {
        if (value is PexIdentifierValue identifier &&
            _types.TryGetValue(identifier.Identifier.Value, out var type))
        {
            return type;
        }

        return "Var";
    }

    private Expression Call(Expression callable, ImmutableArray<PexValue> arguments)
    {
        return Call(callable, (IReadOnlyList<PexValue>)arguments);
    }

    private Expression Call(Expression callable, IReadOnlyList<PexValue> arguments)
    {
        var rendered = new string[arguments.Count];
        for (var i = 0; i < arguments.Count; i++)
        {
            rendered[i] = ReadValue(arguments[i]).Text;
        }

        return new Expression($"{Wrap(callable, MemberPrecedence)}({string.Join(", ", rendered)})",
            MemberPrecedence);
    }

    private static Expression Member(Expression target, string name)
    {
        return new Expression($"{Wrap(target, MemberPrecedence)}.{name}", MemberPrecedence);
    }

    private static Expression Index(Expression target, Expression index)
    {
        return new Expression($"{Wrap(target, MemberPrecedence)}[{index.Text}]", MemberPrecedence);
    }

    private static Expression Unary(string operation, Expression operand)
    {
        return new Expression(operation + Wrap(operand, UnaryPrecedence), UnaryPrecedence);
    }

    private static Expression Binary(Expression left, string operation, Expression right, int precedence)
    {
        return new Expression($"{Wrap(left, precedence)} {operation} {Wrap(right, precedence + 1)}", precedence);
    }

    private static string Wrap(Expression expression, int requiredPrecedence)
    {
        return expression.Precedence < requiredPrecedence ? $"({expression.Text})" : expression.Text;
    }

    private bool TryGetLoopTail(int conditionIp, int branchTarget, out int loopTail, out int loopStart)
    {
        loopTail = branchTarget - 1;
        loopStart = -1;
        if (loopTail <= conditionIp ||
            _function.Instructions[loopTail].OpCode != PexOpCode.Jump ||
            !TryGetJumpTarget(loopTail, _function.Instructions[loopTail], out loopStart))
        {
            return false;
        }

        return loopStart <= conditionIp;
    }

    private bool TryGetElseTail(int branchTarget, int enclosingEnd, out int thenEnd, out int elseEnd)
    {
        thenEnd = branchTarget - 1;
        elseEnd = -1;
        if (thenEnd < 0 || _function.Instructions[thenEnd].OpCode != PexOpCode.Jump ||
            !TryGetJumpTarget(thenEnd, _function.Instructions[thenEnd], out elseEnd))
        {
            return false;
        }

        return elseEnd > branchTarget && elseEnd <= enclosingEnd;
    }

    private static bool TryGetConditionalTarget(int ip, PexInstruction instruction, out int target)
    {
        if (instruction.Arguments.Length >= 2 && instruction.Arguments[1] is PexIntegerValue offset)
        {
            target = ip + offset.Value;
            return target >= 0;
        }

        target = -1;
        return false;
    }

    private static bool TryGetJumpTarget(int ip, PexInstruction instruction, out int target)
    {
        if (instruction.Arguments.Length >= 1 && instruction.Arguments[0] is PexIntegerValue offset)
        {
            target = ip + offset.Value;
            return target >= 0;
        }

        target = -1;
        return false;
    }

    private Dictionary<string, Expression> CloneExpressions()
    {
        return new Dictionary<string, Expression>(_expressions, StringComparer.OrdinalIgnoreCase);
    }

    private void RestoreExpressions(Dictionary<string, Expression> snapshot)
    {
        _expressions.Clear();
        foreach (var pair in snapshot)
        {
            _expressions.Add(pair.Key, pair.Value);
        }
    }

    private void Line(string text = "")
    {
        _lines.Add(text.Length == 0 ? string.Empty : new string(' ', _indent * 2) + text);
    }

    private static HashSet<string> FindMaterializedTemporaries(
        ImmutableArray<PexInstruction> instructions)
    {
        var writes = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        var uses = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        for (var ip = 0; ip < instructions.Length; ip++)
        {
            var instruction = instructions[ip];
            var destination = DestinationIndex(instruction.OpCode);
            for (var i = 0; i < instruction.Arguments.Length; i++)
            {
                if (instruction.Arguments[i] is not PexIdentifierValue identifier ||
                    !identifier.Identifier.Value.StartsWith("::temp", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var target = i == destination ? writes : uses;
                AddPosition(target, identifier.Identifier.Value, ip);
            }

            foreach (var argument in instruction.VariadicArguments)
            {
                if (argument is PexIdentifierValue identifier &&
                    identifier.Identifier.Value.StartsWith("::temp", StringComparison.OrdinalIgnoreCase))
                {
                    AddPosition(uses, identifier.Identifier.Value, ip);
                }
            }
        }

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in writes.Keys.Concat(uses.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var writePositions = writes.GetValueOrDefault(name) ?? [];
            var usePositions = uses.GetValueOrDefault(name) ?? [];
            if (writePositions.Count != 1 || usePositions.Count != 1)
            {
                result.Add(name);
                continue;
            }

            var write = writePositions[0];
            var use = usePositions[0];
            if (use <= write)
            {
                result.Add(name);
                continue;
            }

            for (var ip = write + 1; ip < use; ip++)
            {
                if (instructions[ip].OpCode is PexOpCode.Jump or PexOpCode.JumpTrue or PexOpCode.JumpFalse)
                {
                    result.Add(name);
                    break;
                }
            }
        }

        return result;
    }

    private static void AddPosition(Dictionary<string, List<int>> positions, string name, int ip)
    {
        if (!positions.TryGetValue(name, out var list))
        {
            list = [];
            positions.Add(name, list);
        }

        list.Add(ip);
    }

    private static int DestinationIndex(PexOpCode opcode)
    {
        return opcode switch
        {
            >= PexOpCode.IAdd and <= PexOpCode.CompareGreaterThanOrEqual => 0,
            PexOpCode.CallMethod or PexOpCode.CallStatic => 2,
            PexOpCode.CallParent => 1,
            PexOpCode.PropertyGet => 2,
            PexOpCode.ArrayCreate or PexOpCode.ArrayLength or PexOpCode.ArrayGetElement => 0,
            PexOpCode.ArrayFindElement or PexOpCode.ArrayRFindElement => 1,
            PexOpCode.Is or PexOpCode.StructCreate or PexOpCode.StructGet => 0,
            PexOpCode.ArrayFindStruct or PexOpCode.ArrayRFindStruct or
                PexOpCode.ArrayGetAllMatchingStructs => 1,
            _ => -1
        };
    }

    private sealed record Expression(string Text, int Precedence);

    private readonly record struct LoopContext(int Start, int End);
}
