using System.Globalization;
using System.Text.Json;

namespace BethesdaMultitool.Core.RuntimeSession;

/// <summary>Owner-bound conditions for the fixed PC quest pilot.</summary>
internal static class RuntimeOwnerConditionAction
{
    internal static RuntimeFrame? TryCreate(RuntimeAction action, ulong requestId)
    {
        if (action.Kind == "read-quest-conditions") return QuestFrame(action, requestId);
        if (action.Kind != "read-owner-conditions") return null;
        if (action.Target != "player" || action.Name is not null || action.Value is not null ||
            action.Command is not null || action.Message is not null || action.Button is not null ||
            action.ButtonIndex is not null || action.TimeoutMilliseconds is not null ||
            action.PollMilliseconds is not null || action.Count is not null || action.Index is not null ||
            action.Gamepad is not null)
            throw new ArgumentException("Owner conditions require the pilot quest, player subject and an explicit actor target.");
        if (!string.Equals(action.Plugin, "BMTConditionControl.esp", StringComparison.OrdinalIgnoreCase) ||
            !MatchesLocalId(action.FormId, 0x800))
            throw new ArgumentException("Owner conditions currently support BMTConditionControl.esp:000800.");
        string target;
        if (action.OtherTarget == "player" && action.OtherPlugin is null && action.OtherFormId is null)
            target = "@player\t000014";
        else if (action.OtherTarget is null &&
                 string.Equals(action.OtherPlugin, "FalloutNV.esm", StringComparison.OrdinalIgnoreCase) &&
                 MatchesLocalId(action.OtherFormId, 0x104C0F))
            target = "FalloutNV.esm\t104C0F";
        else
            throw new ArgumentException("Owner conditions require player or FalloutNV.esm:104C0F as the pilot target.");
        return new(RuntimeRequestKind.OwnerConditions, requestId,
            $"quest-conditions-v1\tBMTConditionControl.esp\t000800\t@player\t000014\t{target}");
    }

    private static RuntimeFrame QuestFrame(RuntimeAction action, ulong requestId)
    {
        if (action.Name is not null || action.Value is not null || action.Command is not null ||
            action.Message is not null || action.Button is not null || action.ButtonIndex is not null ||
            action.TimeoutMilliseconds is not null || action.PollMilliseconds is not null ||
            action.Count is not null || action.Index is not null || action.Gamepad is not null)
            throw new ArgumentException("Quest condition reads require only explicit owner, subject and target identities.");
        var owner = Pair(action.Plugin, action.FormId);
        var subject = ActorPair(action.Target, action.SubjectPlugin, action.SubjectFormId);
        var target = ActorPair(action.OtherTarget, action.OtherPlugin, action.OtherFormId);
        return new(RuntimeRequestKind.OwnerConditions, requestId, $"quest-conditions-v2\t{owner}\t{subject}\t{target}");
    }

    private static string ActorPair(string? alias, string? plugin, string? local)
    {
        if (alias == "player" && plugin is null && local is null) return "@player\t000014";
        if (alias is not null) throw new ArgumentException("Condition actors require player or one plugin/local pair.");
        return Pair(plugin, local);
    }

    private static string Pair(string? plugin, string? local)
    {
        var text = local?.StartsWith("0x", StringComparison.OrdinalIgnoreCase) == true ? local[2..] : local;
        if (plugin is not { Length: >= 5 and <= 255 } ||
            plugin.Any(c => c is < ' ' or >= '\x7F' || c is '"' or '\\' or '/' or ':') ||
            !(plugin.EndsWith(".esm", StringComparison.OrdinalIgnoreCase) || plugin.EndsWith(".esp", StringComparison.OrdinalIgnoreCase)) ||
            text is not { Length: >= 1 and <= 8 } || !text.All(char.IsAsciiHexDigit) ||
            !uint.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var id) || id is 0 or > 0xFFFFFF)
            throw new ArgumentException("Condition identities require a plugin filename and nonzero 24-bit local FormID.");
        return $"{plugin}\t{id:X6}";
    }

    private static bool MatchesLocalId(string? text, uint expected)
    {
        var local = text?.StartsWith("0x", StringComparison.OrdinalIgnoreCase) == true ? text[2..] : text;
        return local is { Length: >= 1 and <= 8 } && local.All(char.IsAsciiHexDigit) &&
            uint.TryParse(local, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var id) && id == expected;
    }

    internal static void RequireCapability(JsonElement identity, string kind)
    {
        if (kind is not ("read-owner-conditions" or "read-quest-conditions")) return;
        var capability = kind == "read-quest-conditions" ? "questConditionListRead" : "ownerConditionListProbe";
        if (!identity.TryGetProperty("capabilities", out var capabilities) ||
            capabilities.ValueKind != JsonValueKind.Object ||
            !capabilities.TryGetProperty(capability, out var available) || available.ValueKind != JsonValueKind.True)
            throw new InvalidOperationException($"This runtime backend does not support {kind}.");
    }
}
