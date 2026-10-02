using System.Globalization;
using System.Text.Json;

namespace BethesdaMultitool.Core.RuntimeSession;

/// <summary>Read-only storage or SDK numeric reads, with an explicit persistent reference owner.</summary>
internal static class RuntimeReferenceVariableAction
{
    internal static RuntimeFrame? TryCreate(RuntimeAction action, ulong requestId)
    {
        if (action.Kind is not ("read-reference-variable" or "read-reference-variable-sdk")) return null;
        if (action.Target is not null || action.Value is not null || action.Command is not null ||
            action.Message is not null || action.Button is not null || action.ButtonIndex is not null ||
            action.TimeoutMilliseconds is not null || action.PollMilliseconds is not null ||
            action.OtherPlugin is not null || action.OtherFormId is not null || action.OtherTarget is not null ||
            action.Count is not null || action.Index is not null || action.Gamepad is not null ||
            action.SubjectPlugin is not null || action.SubjectFormId is not null)
            throw new ArgumentException("Reference variable reads require only plugin, local FormID and variable name.");
        var plugin = action.Plugin;
        if (plugin is not { Length: > 4 and <= 255 } ||
            !plugin.All(c => c is >= ' ' and <= '~' && c is not ('"' or '\\' or '/' or ':')) ||
            !(plugin.EndsWith(".esm", StringComparison.OrdinalIgnoreCase) || plugin.EndsWith(".esp", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Reference variable reads require a plugin filename.");
        var local = action.FormId?.StartsWith("0x", StringComparison.OrdinalIgnoreCase) == true ? action.FormId[2..] : action.FormId;
        if (local is null || local.Length is < 1 or > 8 ||
            !uint.TryParse(local, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var id) || id is 0 or > 0xFFFFFF)
            throw new ArgumentException("Reference variable reads require a nonzero local FormID.");
        var name = action.Name;
        if (name is not { Length: >= 1 and <= 128 } || !(char.IsAsciiLetter(name[0]) || name[0] == '_') ||
            !name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            throw new ArgumentException("Reference variable reads require a bounded variable identifier.");
        var route = action.Kind == "read-reference-variable-sdk" ? "reference-local-sdk/1" : "reference-local/1";
        return new(RuntimeRequestKind.Evaluate, requestId, $"{route}\t{plugin}\t{id:X6}\t{name}");
    }

    internal static void RequireCapability(JsonElement identity, string kind)
    {
        if (kind is not ("read-reference-variable" or "read-reference-variable-sdk")) return;
        var capability = kind == "read-reference-variable-sdk" ? "referenceScriptLocalsSdk" : "referenceScriptLocals";
        if (!identity.TryGetProperty("capabilities", out var capabilities) || capabilities.ValueKind != JsonValueKind.Object ||
            !capabilities.TryGetProperty(capability, out var available) || available.ValueKind != JsonValueKind.True)
            throw new InvalidOperationException("This runtime backend does not support the requested reference script-local read route.");
    }
}
