namespace EsmAnalyzer.Commands.Audits;

internal sealed record ScriptDeclarationIdentityResult(
    string Verdict,
    int DeclarationCount,
    string DeclarationIdentities,
    string SlsdIdentities,
    string Details);

internal static class ScriptDeclarationIdentityAudit
{
    internal static ScriptDeclarationIdentityResult Compare(
        string? source,
        IReadOnlyList<ScriptVariableInfo> variables)
    {
        var declarations = Parse(source, out var malformedCount);
        var declarationIdentities = string.Join(
            '|',
            declarations.Select(static declaration => $"{declaration.Kind}:{declaration.Name}"));
        var slsdIdentities = string.Join(
            '|',
            variables.Select(static variable =>
                $"{variable.Index}:{variable.Name ?? "<unnamed>"}:{StorageKind(variable.Type)}"));

        if (source is null)
        {
            return Result("not-represented", declarations, declarationIdentities, slsdIdentities, "no-source");
        }

        if (malformedCount > 0)
        {
            return Result(
                "mismatch",
                declarations,
                declarationIdentities,
                slsdIdentities,
                $"malformed-declarations={malformedCount}");
        }

        var problems = FindProblems(declarations, variables);
        var verdict = problems.Count == 0 ? "exact" : "mismatch";
        var details = problems.Count == 0
            ? declarations.Count == 0 ? "exact-empty" : "ordinal-ignore-case-name+storage-kind"
            : string.Join('|', problems);
        return Result(verdict, declarations, declarationIdentities, slsdIdentities, details);
    }

    private static List<string> FindProblems(
        IReadOnlyList<SourceDeclaration> declarations,
        IReadOnlyList<ScriptVariableInfo> variables)
    {
        var problems = new List<string>();
        if (declarations.GroupBy(static item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Any(static group => group.Count() > 1))
        {
            problems.Add("duplicate-source-identity");
        }

        if (variables.Any(static variable => string.IsNullOrWhiteSpace(variable.Name)))
        {
            problems.Add("unnamed-slsd-identity");
        }

        if (variables.Where(static variable => !string.IsNullOrWhiteSpace(variable.Name))
            .GroupBy(static variable => variable.Name!, StringComparer.OrdinalIgnoreCase)
            .Any(static group => group.Count() > 1))
        {
            problems.Add("duplicate-slsd-identity");
        }

        foreach (var declaration in declarations)
        {
            var matches = variables.Where(variable => string.Equals(
                variable.Name,
                declaration.Name,
                StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 0)
            {
                problems.Add($"missing-slsd:{declaration.Name}");
            }
            else if (matches.Count == 1 && !StorageMatches(declaration.Kind, matches[0].Type))
            {
                problems.Add($"storage-mismatch:{declaration.Name}");
            }
        }

        foreach (var variable in variables.Where(static variable => !string.IsNullOrWhiteSpace(variable.Name)))
        {
            if (!declarations.Any(declaration => string.Equals(
                    declaration.Name,
                    variable.Name,
                    StringComparison.OrdinalIgnoreCase)))
            {
                problems.Add($"missing-declaration:{variable.Name}");
            }
        }

        return problems.Distinct(StringComparer.Ordinal).OrderBy(static value => value, StringComparer.Ordinal)
            .ToList();
    }

    private static List<SourceDeclaration> Parse(string? source, out int malformedCount)
    {
        malformedCount = 0;
        var declarations = new List<SourceDeclaration>();
        if (source is null)
        {
            return declarations;
        }

        foreach (var rawLine in source.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            var commentIndex = rawLine.IndexOf(';');
            var line = (commentIndex < 0 ? rawLine : rawLine[..commentIndex]).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (!TryGetKind(tokens[0], out var kind))
            {
                continue;
            }

            if (tokens.Length != 2)
            {
                malformedCount++;
                continue;
            }

            declarations.Add(new SourceDeclaration(tokens[1], kind));
        }

        return declarations;
    }

    private static bool TryGetKind(string keyword, out string kind)
    {
        if (keyword.Equals("short", StringComparison.OrdinalIgnoreCase)
            || keyword.Equals("long", StringComparison.OrdinalIgnoreCase)
            || keyword.Equals("int", StringComparison.OrdinalIgnoreCase))
        {
            kind = "integer";
            return true;
        }

        if (keyword.Equals("float", StringComparison.OrdinalIgnoreCase))
        {
            kind = "float";
            return true;
        }

        if (keyword.Equals("ref", StringComparison.OrdinalIgnoreCase))
        {
            kind = "reference";
            return true;
        }

        kind = string.Empty;
        return false;
    }

    private static bool StorageMatches(string declarationKind, byte slsdType)
    {
        return declarationKind == "integer" ? slsdType != 0 : slsdType == 0;
    }

    private static string StorageKind(byte type)
    {
        return type == 0 ? "float-or-reference" : "integer";
    }

    private static ScriptDeclarationIdentityResult Result(
        string verdict,
        IReadOnlyCollection<SourceDeclaration> declarations,
        string declarationIdentities,
        string slsdIdentities,
        string details)
    {
        return new ScriptDeclarationIdentityResult(
            verdict,
            declarations.Count,
            declarationIdentities,
            slsdIdentities,
            details);
    }

    private sealed record SourceDeclaration(string Name, string Kind);
}