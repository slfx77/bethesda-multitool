using System.Globalization;
using System.Text;

namespace BethesdaMultitool.Core.Formats.Papyrus;

/// <summary>
///     Reconstructs readable Papyrus source from a parsed PEX file. The declaration and metadata
///     layout follows the public Caprica/Champollion PEX models; function bodies are rebuilt from
///     the VM's destination-first instruction form and conservative control-flow patterns.
/// </summary>
public static class PexDecompiler
{
    /// <summary>Decompiles every script object in <paramref name="file" /> to Papyrus source.</summary>
    public static string Decompile(PexFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var writer = new SourceWriter(file);
        for (var i = 0; i < file.Objects.Length; i++)
        {
            if (i > 0)
            {
                writer.Line();
                writer.Line("; -----------------------------------------------------------------------------");
                writer.Line();
            }

            writer.WriteObject(file.Objects[i]);
        }

        return writer.ToString().TrimEnd();
    }

    internal static string FormatType(string type)
    {
        var mapped = type.Replace('#', ':');
        if (mapped.EndsWith("[]", StringComparison.Ordinal))
        {
            return FormatType(mapped[..^2]) + "[]";
        }

        return mapped.ToLowerInvariant() switch
        {
            "none" => "None",
            "bool" => "Bool",
            "float" => "Float",
            "int" => "Int",
            "string" => "String",
            "var" => "Var",
            _ => mapped
        };
    }

    internal static string FormatValue(PexValue value)
    {
        return value switch
        {
            PexNoneValue => "None",
            PexIdentifierValue identifier => FormatIdentifier(identifier.Identifier.Value),
            PexStringValue text => $"\"{EscapeString(text.String.Value)}\"",
            PexIntegerValue integer => integer.Value.ToString(CultureInfo.InvariantCulture),
            PexFloatValue floating => FormatFloat(floating.Value),
            PexBooleanValue boolean => boolean.Value ? "True" : "False",
            _ => "None"
        };
    }

    internal static string FormatIdentifier(string name)
    {
        if (name.Equals("::nonevar", StringComparison.OrdinalIgnoreCase))
        {
            return "None";
        }

        if (name.StartsWith("::", StringComparison.Ordinal) &&
            name.EndsWith("_var", StringComparison.OrdinalIgnoreCase) && name.Length > 6)
        {
            return name[2..^4];
        }

        if (name.StartsWith("::mangled_", StringComparison.OrdinalIgnoreCase))
        {
            var lastUnderscore = name.LastIndexOf('_');
            if (lastUnderscore > 10)
            {
                return name[10..lastUnderscore];
            }
        }

        if (name.StartsWith("::", StringComparison.Ordinal))
        {
            return name[2..].Replace(':', '_');
        }

        return name;
    }

    internal static string RawName(PexValue value)
    {
        return value switch
        {
            PexIdentifierValue identifier => FormatIdentifier(identifier.Identifier.Value),
            PexStringValue text => text.String.Value,
            _ => FormatValue(value)
        };
    }

    internal static bool IsCompilerTemporary(string name)
    {
        return name.StartsWith("::temp", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("::nonevar", StringComparison.OrdinalIgnoreCase);
    }

    internal static string EscapeString(string value)
    {
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal);
    }

    private static string FormatFloat(float value)
    {
        if (!float.IsFinite(value))
        {
            if (float.IsNaN(value))
            {
                return "0.0 ; NaN in PEX";
            }

            return float.IsPositiveInfinity(value)
                ? "3.4028235E+38 ; +Infinity in PEX"
                : "-3.4028235E+38 ; -Infinity in PEX";
        }

        var text = value.ToString("R", CultureInfo.InvariantCulture);
        return text.IndexOfAny(['.', 'E', 'e']) >= 0 ? text : text + ".0";
    }

    private sealed class SourceWriter(PexFile file)
    {
        private readonly StringBuilder _output = new();
        private int _indent;

        public void WriteObject(PexObject obj)
        {
            var header = new StringBuilder("ScriptName ").Append(obj.Name.Value);
            if (!string.IsNullOrWhiteSpace(obj.ParentClassName.Value))
            {
                header.Append(" Extends ").Append(FormatType(obj.ParentClassName.Value));
            }

            if (obj.IsConst)
            {
                header.Append(" Const");
            }

            AppendUserFlags(header, obj.UserFlags);
            Line(header.ToString());
            WriteDocString(obj.DocumentationString.Value);
            WriteFallout76TrailingStateReferences(obj);

            WriteStructs(obj);
            WriteVariables(obj);
            WriteProperties(obj);
            WriteStates(obj);
        }

        private void WriteFallout76TrailingStateReferences(PexObject obj)
        {
            if (!obj.HasFallout76TrailingStateReferenceTable)
            {
                return;
            }

            Line("; Unmapped Fallout 76 PEX trailing state references (raw order):");
            if (obj.Fallout76TrailingStateReferences.IsDefaultOrEmpty)
            {
                Line("; (explicit empty table)");
                return;
            }

            foreach (var reference in obj.Fallout76TrailingStateReferences)
            {
                Line($"; [{reference.Index}] \"{EscapeString(reference.Value)}\"");
            }
        }

        public void Line(string text = "")
        {
            if (text.Length > 0 && _indent > 0)
            {
                _output.Append(' ', _indent * 2);
            }

            _output.AppendLine(text);
        }

        public override string ToString()
        {
            return _output.ToString();
        }

        private void WriteStructs(PexObject obj)
        {
            if (obj.Structs.IsEmpty)
            {
                return;
            }

            Line();
            Line(";-- Structs -------------------------------------------------------------------");
            foreach (var structure in obj.Structs)
            {
                Line($"Struct {structure.Name.Value}");
                _indent++;
                foreach (var member in OrderStructMembers(obj, structure))
                {
                    var declaration = new StringBuilder()
                        .Append(FormatType(member.TypeName.Value)).Append(' ').Append(member.Name.Value);
                    if (member.DefaultValue is not PexNoneValue)
                    {
                        declaration.Append(" = ").Append(FormatValue(member.DefaultValue));
                    }

                    AppendUserFlags(declaration, member.UserFlags);
                    if (member.IsConst)
                    {
                        declaration.Append(" Const");
                    }

                    Line(declaration.ToString());
                    WriteDocString(member.DocumentationString.Value);
                }

                _indent--;
                Line("EndStruct");
                Line();
            }
        }

        private IEnumerable<PexStructMember> OrderStructMembers(PexObject obj, PexStruct structure)
        {
            var order = file.DebugInfo?.StructOrders.FirstOrDefault(candidate =>
                candidate.ObjectName.Value.Equals(obj.Name.Value, StringComparison.OrdinalIgnoreCase) &&
                candidate.StructName.Value.Equals(structure.Name.Value, StringComparison.OrdinalIgnoreCase));
            if (order is null || order.Members.Length != structure.Members.Length)
            {
                return structure.Members;
            }

            var members = structure.Members.ToDictionary(
                member => member.Name.Value,
                StringComparer.OrdinalIgnoreCase);
            return order.Members.Select(member => members.GetValueOrDefault(member.Value))
                .Where(static member => member is not null)!;
        }

        private void WriteVariables(PexObject obj)
        {
            var variables = obj.Variables.Where(variable =>
                    !variable.Name.Value.StartsWith("::", StringComparison.Ordinal))
                .ToArray();
            if (variables.Length == 0)
            {
                return;
            }

            Line();
            Line(";-- Variables -----------------------------------------------------------------");
            foreach (var variable in variables)
            {
                var declaration = new StringBuilder()
                    .Append(FormatType(variable.TypeName.Value)).Append(' ').Append(variable.Name.Value);
                if (variable.DefaultValue is not PexNoneValue)
                {
                    declaration.Append(" = ").Append(FormatValue(variable.DefaultValue));
                }

                AppendUserFlags(declaration, variable.UserFlags);
                if (variable.IsConst)
                {
                    declaration.Append(" Const");
                }

                Line(declaration.ToString());
            }
        }

        private void WriteProperties(PexObject obj)
        {
            if (obj.Properties.IsEmpty)
            {
                return;
            }

            Line();
            Line(";-- Properties ----------------------------------------------------------------");
            var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var groups = file.DebugInfo?.PropertyGroups.Where(group =>
                    group.ObjectName.Value.Equals(obj.Name.Value, StringComparison.OrdinalIgnoreCase))
                .ToArray() ?? [];
            var properties = obj.Properties.ToDictionary(
                property => property.Name.Value,
                StringComparer.OrdinalIgnoreCase);

            foreach (var group in groups)
            {
                var groupProperties = group.Properties
                    .Select(property => properties.GetValueOrDefault(property.Value))
                    .Where(static property => property is not null)
                    .ToArray();
                if (groupProperties.Length == 0)
                {
                    continue;
                }

                var hasName = !string.IsNullOrWhiteSpace(group.GroupName.Value);
                if (hasName)
                {
                    var declaration = new StringBuilder("Group ").Append(group.GroupName.Value);
                    AppendUserFlags(declaration, group.UserFlags);
                    Line(declaration.ToString());
                    WriteDocString(group.DocumentationString.Value);
                    _indent++;
                }

                foreach (var property in groupProperties)
                {
                    WriteProperty(obj, property!);
                    written.Add(property!.Name.Value);
                }

                if (hasName)
                {
                    _indent--;
                    Line("EndGroup");
                    Line();
                }
            }

            foreach (var property in obj.Properties.Where(property => !written.Contains(property.Name.Value)))
            {
                WriteProperty(obj, property);
            }
        }

        private void WriteProperty(PexObject obj, PexProperty property)
        {
            var declaration = new StringBuilder()
                .Append(FormatType(property.TypeName.Value)).Append(" Property ").Append(property.Name.Value);
            var autoVariable = property.AutoVariableName is { } autoName
                ? obj.Variables.FirstOrDefault(variable =>
                    variable.Name.Value.Equals(autoName.Value, StringComparison.OrdinalIgnoreCase))
                : null;
            var autoReadOnly = IsAutoReadOnly(property);
            if (autoVariable is not null)
            {
                if (autoVariable.DefaultValue is not PexNoneValue)
                {
                    declaration.Append(" = ").Append(FormatValue(autoVariable.DefaultValue));
                }

                declaration.Append(" Auto");
                AppendUserFlags(declaration, autoVariable.UserFlags);
                if (autoVariable.IsConst)
                {
                    declaration.Append(" Const");
                }
            }
            else if (autoReadOnly)
            {
                declaration.Append(" = ")
                    .Append(FormatValue(property.ReadFunction!.Instructions[0].Arguments[0]))
                    .Append(" AutoReadOnly");
            }

            AppendUserFlags(declaration, property.UserFlags);
            Line(declaration.ToString());
            WriteDocString(property.DocumentationString.Value);

            if (autoVariable is not null || autoReadOnly)
            {
                return;
            }

            _indent++;
            if (property.ReadFunction is { } getter)
            {
                WriteFunction(obj, getter, "Get", true);
            }

            if (property.WriteFunction is { } setter)
            {
                WriteFunction(obj, setter, "Set", true);
            }

            _indent--;
            Line("EndProperty");
        }

        private void WriteStates(PexObject obj)
        {
            foreach (var state in obj.States)
            {
                if (state.Functions.IsEmpty)
                {
                    continue;
                }

                if (string.IsNullOrEmpty(state.Name.Value))
                {
                    Line();
                    Line(";-- Functions -----------------------------------------------------------------");
                    foreach (var function in state.Functions)
                    {
                        var name = function.Name?.Value ?? "Unknown";
                        if (!IsCompilerGeneratedFunction(obj, name))
                        {
                            WriteFunction(obj, function, name);
                        }
                    }

                    continue;
                }

                Line();
                Line(";-- State ---------------------------------------------------------------------");
                var prefix = state.Name.Value.Equals(obj.AutoStateName.Value, StringComparison.OrdinalIgnoreCase)
                    ? "Auto "
                    : string.Empty;
                Line($"{prefix}State {state.Name.Value}");
                _indent++;
                foreach (var function in state.Functions)
                {
                    var name = function.Name?.Value ?? "Unknown";
                    if (!IsCompilerGeneratedFunction(obj, name))
                    {
                        WriteFunction(obj, function, name);
                    }
                }

                _indent--;
                Line("EndState");
            }
        }

        private void WriteFunction(
            PexObject obj,
            PexFunction function,
            string requestedName,
            bool forceFunction = false)
        {
            Line();
            var (name, remoteEvent) = NormalizeRemoteEventName(requestedName, function);
            var isEvent = !forceFunction && IsEvent(name, function, remoteEvent);
            var declaration = new StringBuilder();
            if (!isEvent && !function.ReturnTypeName.Value.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                declaration.Append(FormatType(function.ReturnTypeName.Value)).Append(' ');
            }

            declaration.Append(isEvent ? "Event " : "Function ").Append(name).Append('(');
            for (var i = 0; i < function.Parameters.Length; i++)
            {
                if (i > 0)
                {
                    declaration.Append(", ");
                }

                var parameter = function.Parameters[i];
                declaration.Append(FormatType(parameter.TypeName.Value)).Append(' ')
                    .Append(FormatIdentifier(parameter.Name.Value));
            }

            declaration.Append(')');
            if (function.Flags.HasFlag(PexFunctionFlags.Global))
            {
                declaration.Append(" Global");
            }

            if (function.Flags.HasFlag(PexFunctionFlags.Native))
            {
                declaration.Append(" Native");
            }

            AppendUserFlags(declaration, function.UserFlags);
            Line(declaration.ToString());
            WriteDocString(function.DocumentationString.Value);
            if (function.UnmappedFlags != 0)
            {
                Line(
                    $"; Unmapped PEX function flag bits: 0x{function.UnmappedFlags:X2} " +
                    $"(raw byte 0x{function.RawFlags:X2})");
            }

            if (function.Flags.HasFlag(PexFunctionFlags.Native))
            {
                return;
            }

            _indent++;
            var body = new PexFunctionDecompiler(function, obj).Decompile();
            foreach (var line in body)
            {
                Line(line);
            }

            _indent--;
            Line(isEvent ? "EndEvent" : "EndFunction");
        }

        private void WriteDocString(string documentation)
        {
            if (string.IsNullOrWhiteSpace(documentation))
            {
                return;
            }

            var flattened = documentation.Replace("\r", string.Empty, StringComparison.Ordinal)
                .Replace("}", "\\}", StringComparison.Ordinal);
            Line($"{{{flattened}}}");
        }

        private void AppendUserFlags(StringBuilder destination, uint flags)
        {
            foreach (var flag in file.UserFlags)
            {
                if (flag.BitIndex < 32 && (flags & (1u << flag.BitIndex)) != 0)
                {
                    destination.Append(' ').Append(flag.Name.Value);
                }
            }
        }

        private static bool IsAutoReadOnly(PexProperty property)
        {
            return property.AutoVariableName is null &&
                   property.Flags.HasFlag(PexPropertyFlags.Readable) &&
                   !property.Flags.HasFlag(PexPropertyFlags.Writable) &&
                   property.ReadFunction is
                   {
                       Instructions.Length: 1
                   } getter &&
                   getter.Instructions[0] is
                   {
                       OpCode: PexOpCode.Return,
                       Arguments.Length: 1
                   } instruction &&
                   instruction.Arguments[0] is not PexIdentifierValue;
        }

        private static bool IsEvent(string name, PexFunction function, bool remoteEvent)
        {
            if (remoteEvent)
            {
                return true;
            }

            return !function.Flags.HasFlag(PexFunctionFlags.Global) &&
                   function.ReturnTypeName.Value.Equals("none", StringComparison.OrdinalIgnoreCase) &&
                   name.Length > 2 && name.StartsWith("On", StringComparison.OrdinalIgnoreCase) &&
                   char.IsUpper(name[2]);
        }

        private static bool IsCompilerGeneratedFunction(PexObject obj, string name)
        {
            if (obj.Name.Value.Equals("ScriptObject", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return name.Equals("GotoState", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("GetState", StringComparison.OrdinalIgnoreCase);
        }

        private static (string Name, bool RemoteEvent) NormalizeRemoteEventName(
            string name,
            PexFunction function)
        {
            if (!name.StartsWith("::remote_", StringComparison.OrdinalIgnoreCase))
            {
                return (name, false);
            }

            var remote = name[9..];
            if (function.Parameters.Length > 0)
            {
                var typeLength = function.Parameters[0].TypeName.Value.Length;
                if (typeLength > 0 && typeLength < remote.Length)
                {
                    remote = remote[..typeLength] + "." + remote[typeLength..].TrimStart('_');
                }
            }

            return (remote, true);
        }
    }
}
