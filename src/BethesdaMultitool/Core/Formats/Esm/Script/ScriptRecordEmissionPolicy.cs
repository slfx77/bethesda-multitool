using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;

namespace BethesdaMultitool.Core.Formats.Esm.Script;

/// <summary>
///     Shared planner/writer policy for deciding whether a captured standalone script has
///     enough authored identity or executable content to serialize. Keeping this check in
///     one place prevents the planner from advertising a SCPT FormID that the writer later
///     declines.
/// </summary>
internal static class ScriptRecordEmissionPolicy
{
    internal static bool CanEmitNew(ScriptRecord script, out string? issue)
    {
        ArgumentNullException.ThrowIfNull(script);

        if (script.IsIncompleteExecutableBundle
            || script.HasMalformedSerializedHeader
            || script.HasMalformedSerializedTable)
        {
            issue = "has an incomplete or inconsistent SCHR/SCDA executable bundle";
            return false;
        }

        if (!string.IsNullOrEmpty(ResolveEditorId(script))
            || !string.IsNullOrEmpty(script.SourceText)
            || script.CompiledData is { Length: > 0 }
            || script.Variables.Count > 0
            || script.ReferencedObjects.Count > 0)
        {
            issue = null;
            return true;
        }

        issue = "has no EditorId, source text, bytecode, variables, or references";
        return false;
    }

    /// <summary>
    ///     Prefer the captured EDID. When runtime metadata omitted it, recover only an exact,
    ///     unambiguous <c>scn</c>/<c>ScriptName</c> declaration from the same SCTX text.
    /// </summary>
    internal static string? ResolveEditorId(ScriptRecord script)
    {
        return ResolveEditorId(script.EditorId, script.SourceText);
    }

    internal static string? ResolveEditorId(string? editorId, string? sourceText)
    {
        if (!string.IsNullOrWhiteSpace(editorId))
        {
            return editorId;
        }

        return TryExtractDeclaredScriptName(sourceText, out var declaredName)
            ? declaredName
            : editorId;
    }

    internal static bool TryExtractDeclaredScriptName(
        string? sourceText,
        out string? declaredName)
    {
        declaredName = null;
        if (string.IsNullOrWhiteSpace(sourceText))
        {
            return false;
        }

        foreach (var rawLine in sourceText
                     .Replace("\r\n", "\n", StringComparison.Ordinal)
                     .Replace('\r', '\n')
                     .Split('\n'))
        {
            var commentIndex = rawLine.IndexOf(';');
            var line = (commentIndex >= 0 ? rawLine[..commentIndex] : rawLine).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length != 2
                || (!tokens[0].Equals("scn", StringComparison.OrdinalIgnoreCase)
                    && !tokens[0].Equals("ScriptName", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (declaredName is null)
            {
                declaredName = tokens[1];
                continue;
            }

            if (!string.Equals(declaredName, tokens[1], StringComparison.Ordinal))
            {
                declaredName = null;
                return false;
            }
        }

        return !string.IsNullOrWhiteSpace(declaredName);
    }
}
