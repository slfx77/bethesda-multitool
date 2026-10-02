using System.Text.Json;

namespace BethesdaMultitool.Core.RuntimeSession;

internal enum RuntimeMenuInspectionState { Invalid, Unavailable, Visible }

/// <summary>Classifies read-only menu observations; native errors and choice responses are never retryable.</summary>
internal static class RuntimeMenuInspection
{
    internal static RuntimeMenuInspectionState Classify(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object || Text(row, "kind") != "message-state")
            return RuntimeMenuInspectionState.Invalid;
        if (Text(row, "status") == "unavailable")
            return Text(row, "reason") is "menu-not-visible" or "menu-not-ready"
                ? RuntimeMenuInspectionState.Unavailable : RuntimeMenuInspectionState.Invalid;
        if (Text(row, "status") == "visible" && Text(row, "text") != null && Text(row, "buttonLabel") != null &&
            Text(row, "owner") is "dedicated-probe" or "stale-probe" or "explicit-visible-text" &&
            row.TryGetProperty("buttonIndex", out var button) && button.ValueKind == JsonValueKind.Number &&
            button.TryGetInt32(out var index) && index >= 0)
            return RuntimeMenuInspectionState.Visible;
        return RuntimeMenuInspectionState.Invalid;
    }

    private static string? Text(JsonElement row, string key) =>
        row.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
