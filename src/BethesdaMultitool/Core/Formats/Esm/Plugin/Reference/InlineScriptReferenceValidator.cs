using System.Collections.Immutable;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.AI;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Script;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.Reference;

/// <summary>
///     Validates the fixed-slot reference/local tables carried by embedded INFO, PACK, and
///     TERM scripts. SCDA operands address the mixed SCRO/SCRV table by position, so a
///     missing target cannot be represented by dropping or nulling one entry.
/// </summary>
internal static class InlineScriptReferenceValidator
{
    public static Result Validate(
        DialogueResultScript? script,
        string scriptPath,
        IReadOnlySet<uint>? validFormIds,
        IReadOnlyDictionary<uint, uint>? remapTable)
    {
        return script is null
            ? new Result(true, ImmutableArray<uint>.Empty, null, null, null)
            : Validate(
                script.CompiledData,
                script.SourceText,
                script.Variables,
                script.ReferencedObjects,
                scriptPath,
                validFormIds,
                remapTable,
                script.IsIncompleteExecutableBundle,
                script.IsDmpDerived,
                script.SourceTextOrigin,
                script.DecompiledText,
                script.IsBigEndianBytecode);
    }

    public static Result Validate(
        byte[]? compiledData,
        string? sourceText,
        IReadOnlyList<ScriptVariableInfo> variables,
        IReadOnlyList<uint> referencedObjects,
        string scriptPath,
        IReadOnlySet<uint>? validFormIds,
        IReadOnlyDictionary<uint, uint>? remapTable,
        bool isKnownIncompleteExecutableBundle = false,
        bool isDmpDerived = false,
        ScriptSourceTextOrigin sourceTextOrigin = ScriptSourceTextOrigin.None,
        string? decompiledText = null,
        bool isBigEndianBytecode = false)
    {
        if (isKnownIncompleteExecutableBundle)
        {
            var issue = new Issue(
                scriptPath,
                scriptPath,
                $"{scriptPath} declared executable content, but its runtime SCDA/SLSD/SCRO/SCRV " +
                "bundle was not captured atomically.");
            return new Result(false, ImmutableArray<uint>.Empty, issue, null, null);
        }

        if (compiledData is not { Length: > 0 }
            && (variables.Count > 0 || referencedObjects.Count > 0))
        {
            var issue = new Issue(
                scriptPath,
                scriptPath,
                $"{scriptPath} carries SLSD/SCRO/SCRV metadata without SCDA; the inline script bundle is incomplete.");
            return new Result(false, ImmutableArray<uint>.Empty, issue, null, null);
        }

        var sourceDecision = CapturedScriptEmissionContract.EvaluateInline(
            isDmpDerived,
            sourceTextOrigin,
            compiledData,
            sourceText,
            decompiledText,
            variables,
            referencedObjects,
            isBigEndianBytecode);
        if (!sourceDecision.ExecutableBundleSafe)
        {
            var issue = new Issue(
                scriptPath,
                scriptPath,
                $"{scriptPath} has an unsafe captured executable bundle: {sourceDecision.BundleIssue}.");
            return new Result(false, ImmutableArray<uint>.Empty, issue, null, null);
        }

        var sourceContractIssue = sourceDecision.SourceIssue is null
            ? null
            : new Issue(
                scriptPath,
                $"{scriptPath}.SCTX",
                $"{scriptPath}.SCTX omitted: {sourceDecision.SourceIssue}.");

        var variableIds = variables.Select(static variable => variable.Index).ToHashSet();
        var resolved = ImmutableArray.CreateBuilder<uint>(referencedObjects.Count);
        for (var index = 0; index < referencedObjects.Count; index++)
        {
            var raw = referencedObjects[index];
            if ((raw & 0x80000000u) != 0)
            {
                var localId = raw & 0x7FFFFFFFu;
                if (localId == 0 || !variableIds.Contains(localId))
                {
                    var fieldPath = $"{scriptPath}.SCRV[{index}]";
                    var issue = new Issue(
                        scriptPath,
                        fieldPath,
                        $"{fieldPath} local {localId} has no matching SLSD.",
                        LocalVariableId: localId);
                    return new Result(
                        false,
                        ImmutableArray<uint>.Empty,
                        issue,
                        sourceDecision.SourceText,
                        sourceContractIssue);
                }

                resolved.Add(raw);
                continue;
            }

            var resolvedFormId = FormIdReferenceResolver.Resolve(raw, validFormIds, remapTable);
            if (resolvedFormId is null or 0u)
            {
                var fieldPath = $"{scriptPath}.SCRO[{index}]";
                var issue = new Issue(
                    scriptPath,
                    fieldPath,
                    $"{fieldPath} target 0x{raw:X8} does not resolve; the owning record must be retained or suppressed.",
                    raw,
                    resolvedFormId);
                return new Result(
                    false,
                    ImmutableArray<uint>.Empty,
                    issue,
                    sourceDecision.SourceText,
                    sourceContractIssue);
            }

            resolved.Add(resolvedFormId.Value);
        }

        return new Result(
            true,
            resolved.MoveToImmutable(),
            null,
            sourceDecision.SourceText,
            sourceContractIssue);
    }

    public static ImmutableArray<Issue> FindSourceContractIssues(
        object owner,
        IReadOnlySet<uint>? validFormIds,
        IReadOnlyDictionary<uint, uint>? remapTable)
    {
        var issues = ImmutableArray.CreateBuilder<Issue>();
        switch (owner)
        {
            case DialogueRecord info:
                CollectSourceIssues(
                    info.ResultScripts.Select((script, index) =>
                        (script, $"ResultScripts[{index}]")),
                    validFormIds,
                    remapTable,
                    issues);
                break;
            case PackageRecord package:
                CollectSourceIssues(
                    EnumeratePackageScripts(package),
                    validFormIds,
                    remapTable,
                    issues);
                break;
            case TerminalRecord terminal:
                for (var index = 0; index < terminal.MenuItems.Count; index++)
                {
                    var item = terminal.MenuItems[index];
                    var result = ValidateTerminalItem(
                        item,
                        $"MenuItems[{index}]",
                        validFormIds,
                        remapTable);
                    if (result.SourceContractIssue is not null)
                    {
                        issues.Add(result.SourceContractIssue);
                    }
                }

                break;
        }

        return issues.ToImmutable();
    }

    private static void CollectSourceIssues(
        IEnumerable<(DialogueResultScript Script, string Path)> scripts,
        IReadOnlySet<uint>? validFormIds,
        IReadOnlyDictionary<uint, uint>? remapTable,
        ImmutableArray<Issue>.Builder issues)
    {
        foreach (var (script, path) in scripts)
        {
            var result = Validate(script, path, validFormIds, remapTable);
            if (result.SourceContractIssue is not null)
            {
                issues.Add(result.SourceContractIssue);
            }
        }
    }

    public static Issue? FindFirstIssue(
        object owner,
        IReadOnlySet<uint>? validFormIds,
        IReadOnlyDictionary<uint, uint>? remapTable)
    {
        return owner switch
        {
            DialogueRecord info => FindFirstIssue(
                info.ResultScripts.Select((script, index) =>
                    (script, $"ResultScripts[{index}]")),
                validFormIds,
                remapTable),
            PackageRecord package => FindFirstIssue(
                EnumeratePackageScripts(package),
                validFormIds,
                remapTable),
            TerminalRecord terminal => FindFirstTerminalIssue(
                terminal,
                validFormIds,
                remapTable),
            _ => null
        };
    }

    private static Issue? FindFirstIssue(
        IEnumerable<(DialogueResultScript Script, string Path)> scripts,
        IReadOnlySet<uint>? validFormIds,
        IReadOnlyDictionary<uint, uint>? remapTable)
    {
        foreach (var (script, path) in scripts)
        {
            var result = Validate(script, path, validFormIds, remapTable);
            if (!result.IsSafe)
            {
                return result.Issue;
            }
        }

        return null;
    }

    private static IEnumerable<(DialogueResultScript Script, string Path)> EnumeratePackageScripts(
        PackageRecord package)
    {
        foreach (var item in EnumeratePackageScripts(package.OnBegin, "OnBegin"))
        {
            yield return item;
        }

        foreach (var item in EnumeratePackageScripts(package.OnEnd, "OnEnd"))
        {
            yield return item;
        }

        foreach (var item in EnumeratePackageScripts(package.OnChange, "OnChange"))
        {
            yield return item;
        }
    }

    private static IEnumerable<(DialogueResultScript Script, string Path)> EnumeratePackageScripts(
        PackageEventAction? action,
        string actionPath)
    {
        if (action is null)
        {
            yield break;
        }

        for (var index = 0; index < action.Scripts.Count; index++)
        {
            yield return (action.Scripts[index], $"{actionPath}.Scripts[{index}]");
        }
    }

    private static Issue? FindFirstTerminalIssue(
        TerminalRecord terminal,
        IReadOnlySet<uint>? validFormIds,
        IReadOnlyDictionary<uint, uint>? remapTable)
    {
        for (var index = 0; index < terminal.MenuItems.Count; index++)
        {
            var item = terminal.MenuItems[index];
            var result = ValidateTerminalItem(
                item,
                $"MenuItems[{index}]",
                validFormIds,
                remapTable);
            if (!result.IsSafe)
            {
                return result.Issue;
            }
        }

        return null;
    }

    private static Result ValidateTerminalItem(
        TerminalMenuItem item,
        string scriptPath,
        IReadOnlySet<uint>? validFormIds,
        IReadOnlyDictionary<uint, uint>? remapTable)
    {
        return Validate(
            item.CompiledData,
            item.SourceText,
            item.Variables,
            item.ReferencedObjects,
            scriptPath,
            validFormIds,
            remapTable,
            item.IsIncompleteExecutableBundle,
            item.IsDmpDerived,
            item.SourceTextOrigin,
            item.DecompiledText,
            item.IsBigEndianBytecode);
    }

    internal sealed record Issue(
        string ScriptPath,
        string FieldPath,
        string Message,
        uint? SourceFormId = null,
        uint? ResolvedFormId = null,
        uint? LocalVariableId = null);

    internal sealed record Result(
        bool IsSafe,
        ImmutableArray<uint> ResolvedReferences,
        Issue? Issue,
        string? SourceTextForEmission,
        Issue? SourceContractIssue);
}
