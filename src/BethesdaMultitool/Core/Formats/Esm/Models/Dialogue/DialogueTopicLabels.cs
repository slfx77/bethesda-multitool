using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using System.Diagnostics.CodeAnalysis;

namespace BethesdaMultitool.Core.Formats.Esm.Models.Dialogue;

/// <summary>Identity labels for shared topics must not borrow one child's spoken text.</summary>
internal static class DialogueTopicLabels
{
    internal static bool IsGreeting([NotNullWhen(true)] string? editorId) =>
        string.Equals(editorId, "GREETING", StringComparison.OrdinalIgnoreCase);

    internal static string FormatReference(uint id, FormIdResolver resolver) => IsGreeting(resolver.GetEditorId(id))
        ? $"{resolver.GetEditorId(id)} (0x{id:X8})"
        : resolver.FormatFull(id);
}
