using System.Globalization;
using System.Text.Json;

namespace BethesdaMultitool.Core.RuntimeSession;

/// <summary>Versioned numeric setting operations on the runtime's game thread.</summary>
internal static class RuntimeGameSettingAction
{
    internal static RuntimeFrame? TryCreate(RuntimeAction action, ulong requestId)
    {
        if (action.Kind is not ("read-game-setting" or "set-game-setting")) return null;
        if (action.Target is not null || action.Command is not null || action.Plugin is not null ||
            action.FormId is not null || action.Message is not null || action.Button is not null ||
            action.ButtonIndex is not null || action.TimeoutMilliseconds is not null ||
            action.PollMilliseconds is not null || action.OtherPlugin is not null ||
            action.OtherFormId is not null || action.OtherTarget is not null || action.Count is not null ||
            action.Index is not null || action.Gamepad is not null)
            throw new ArgumentException("Game setting actions require only a name and an optional write value.");
        var name = action.Name;
        if (name is not { Length: >= 2 and <= 128 } || name[0] is not ('b' or 'i' or 'u' or 'f') ||
            !name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            throw new ArgumentException("Game settings require a bounded numeric setting name.");
        if (action.Kind == "read-game-setting")
        {
            if (action.Value is not null) throw new ArgumentException("Setting reads cannot supply a value.");
            return new(RuntimeRequestKind.Evaluate, requestId, $"gmst/1\tread\t{name}");
        }
        if (action.Value is not { } value || !double.IsFinite(value) || !float.IsFinite((float)value))
            throw new ArgumentException("Setting writes require a finite single-precision value.");
        var supported = name[0] switch
        {
            'b' => value is 0 or 1,
            'i' => value >= int.MinValue && value <= int.MaxValue && value == Math.Truncate(value) && (double)(float)value == value,
            'u' => value >= uint.MinValue && value <= uint.MaxValue && value == Math.Truncate(value) && (double)(float)value == value,
            _ => true
        };
        if (!supported)
            throw new ArgumentException("Integer settings require an in-range value represented exactly by the engine command.");
        return new(RuntimeRequestKind.Execute, requestId,
            $"gmst/1\twrite\t{name}\t{value.ToString("R", CultureInfo.InvariantCulture)}");
    }

    internal static void RequireCapability(JsonElement identity, string kind)
    {
        var capability = kind switch
        {
            "read-game-setting" => "gameSettingRead",
            "set-game-setting" => "gameSettingWrite",
            _ => null
        };
        if (capability is null) return;
        if (!identity.TryGetProperty("capabilities", out var capabilities) ||
            capabilities.ValueKind != JsonValueKind.Object ||
            !capabilities.TryGetProperty(capability, out var available) || available.ValueKind != JsonValueKind.True)
            throw new InvalidOperationException($"This runtime backend does not support {kind}.");
    }
}
