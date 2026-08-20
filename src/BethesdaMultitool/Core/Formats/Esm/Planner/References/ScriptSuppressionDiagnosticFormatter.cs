using System.Globalization;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;

namespace BethesdaMultitool.Core.Formats.Esm.Planner.References;

/// <summary>
///     Stable, delimiter-safe identity fields for fail-closed SCPT diagnostics. Event
///     FormIDs intentionally retain their existing semantics; these fields let reports
///     recover the captured script identity even after plugin-range allocation.
/// </summary>
internal static class ScriptSuppressionDiagnosticFormatter
{
    public static string ScriptIdentity(
        ScriptRecord? script,
        uint? sourceFormId,
        uint? emittedFormId)
    {
        var editorId = string.IsNullOrEmpty(script?.EditorId)
            ? "<none>"
            : Uri.EscapeDataString(script.EditorId);
        return $"[script-source={FormatFormId(sourceFormId)};"
               + $"script-emitted={FormatFormId(emittedFormId)};"
               + $"script-edid={editorId}]";
    }

    public static string ReferenceIdentity(ResolvedRef reference)
    {
        return $"{reference.FieldPath}[target-source={FormatFormId(reference.OriginalFormId)};"
               + $"target-emitted={FormatFormId(reference.FinalFormId)};"
               + $"action={reference.Action}]";
    }

    public static string LocalIdentity(int index, uint localId)
    {
        return $"SCRV[{index}][local-id={localId}]";
    }

    public static IReadOnlyDictionary<string, string?> Metadata(
        ScriptRecord? script,
        uint? sourceFormId,
        uint? emittedFormId,
        IReadOnlyList<Issue> issues)
    {
        var metadata = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["script-source-form-id"] = FormatNullableFormId(sourceFormId),
            ["script-emitted-form-id"] = FormatNullableFormId(emittedFormId),
            ["script-editor-id"] = string.IsNullOrEmpty(script?.EditorId) ? null : script.EditorId,
            ["script-owner-quest-form-id"] = FormatNullableFormId(script?.OwnerQuestFormId),
            ["issue-count"] = issues.Count.ToString(CultureInfo.InvariantCulture)
        };

        for (var index = 0; index < issues.Count; index++)
        {
            var issue = issues[index];
            var prefix = issues.Count == 1 ? string.Empty : $"issue-{index}-";
            if (issue.Reference is { } reference)
            {
                metadata[$"{prefix}reference-field"] = reference.FieldPath;
                metadata[$"{prefix}reference-action"] = reference.Action.ToString();
                metadata[$"{prefix}target-source-form-id"] =
                    FormatNullableFormId(reference.OriginalFormId);
                metadata[$"{prefix}target-emitted-form-id"] =
                    FormatNullableFormId(reference.FinalFormId);
            }
            else if (issue.LocalIndex is { } localIndex && issue.LocalId is { } localId)
            {
                metadata[$"{prefix}reference-field"] = $"SCRV[{localIndex}]";
                metadata[$"{prefix}local-variable-id"] =
                    localId.ToString(CultureInfo.InvariantCulture);
            }
        }

        return metadata;
    }

    private static string FormatFormId(uint? formId)
    {
        return formId.HasValue ? $"0x{formId.Value:X8}" : "<none>";
    }

    private static string? FormatNullableFormId(uint? formId)
    {
        return formId.HasValue ? $"0x{formId.Value:X8}" : null;
    }

    internal sealed record Issue(
        string Message,
        ResolvedRef? Reference = null,
        int? LocalIndex = null,
        uint? LocalId = null);
}
