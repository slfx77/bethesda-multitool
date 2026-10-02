using System.Globalization;

namespace BethesdaMultitool.Core.RuntimeSession;

internal static class RuntimeTypedAction
{
    internal static RuntimeFrame? TryCreate(RuntimeAction action, ulong requestId)
    {
        return action.Kind switch
        {
            "read-dialogue-state" or "read-cell-terrain" when action.Target == null =>
                Frame(RuntimeRequestKind.RecordMembership, Operation()),
            "read-reference-state" => Frame(RuntimeRequestKind.ReferenceSnapshot,
                action.Target == "player" && action.Plugin == null && action.FormId == null ? "player" : Subject()),
            "start-combat" or "move-reference" => Frame(RuntimeRequestKind.ReferenceAction, Operation() + "\t" + Other()),
            "start-combat-leased" => Frame(RuntimeRequestKind.ReferenceAction, CombatLease()),
            "stop-combat" or "enable-reference" or "disable-reference" => Frame(RuntimeRequestKind.ReferenceAction, Operation()),
            "set-position" or "set-rotation" when action.Name is "X" or "Y" or "Z" && action.Value is >= -10000000 and <= 10000000 =>
                Frame(RuntimeRequestKind.ReferenceAction, Operation() + "\t" + action.Name + "\t" + Number()),
            "read-inventory" => Frame(RuntimeRequestKind.Inventory, Operation() +
                (action.OtherPlugin != null || action.OtherFormId != null || action.OtherTarget != null ? "\t" + Other() : "")),
            "add-item" or "remove-item" when action.Count is >= 1 and <= 10000 =>
                Frame(RuntimeRequestKind.Inventory, Operation() + "\t" + Other() + "\t" + action.Count.Value.ToString(CultureInfo.InvariantCulture)),
            "equip-item" or "unequip-item" => Frame(RuntimeRequestKind.Inventory, Operation() + "\t" + Other()),
            "read-quest-state" => Frame(RuntimeRequestKind.QuestState, Operation() + (action.Index.HasValue ? "\t" + Index() : "")),
            "set-quest-stage" => Frame(RuntimeRequestKind.QuestState, Operation() + "\t" + Index()),
            "set-quest-objective" when action.Name is "completed" or "displayed" && action.Value is 0 or 1 =>
                Frame(RuntimeRequestKind.QuestState, Operation() + "\t" + Index() + "\t" + action.Name + "\t" + Number()),
            _ => null
        };

        RuntimeFrame Frame(RuntimeRequestKind kind, string payload) => new(kind, requestId, payload);
        string Operation() => action.Kind + "\t" + Subject();
        string Subject() => Identity(action.Plugin, action.FormId, action.Target);
        string Other() => Identity(action.OtherPlugin, action.OtherFormId, action.OtherTarget);
        string CombatLease()
        {
            if (action.Target != null || action.TimeoutMilliseconds is not (>= 1 and <= 5000) ||
                action.Name != null || action.Value != null || action.Command != null || action.Message != null ||
                action.Button != null || action.ButtonIndex != null || action.PollMilliseconds != null ||
                action.Count != null || action.Index != null)
                throw new ArgumentException("Leased combat requires an explicit non-player attacker and a 1–5000 ms deadline.");
            var subject = Subject();
            var other = Other();
            if (string.Equals(subject, other, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Leased combat requires distinct actors.");
            return action.Kind + "\t" + subject + "\t" + other + "\t" +
                action.TimeoutMilliseconds.Value.ToString(CultureInfo.InvariantCulture);
        }
        string Number() => action.Value is { } value && double.IsFinite(value) && Math.Abs(value) <= float.MaxValue
            ? value.ToString("R", CultureInfo.InvariantCulture) : throw new ArgumentException("Runtime operation requires a finite single-precision number.");
        string Index() => action.Index is >= 0 and <= 65535
            ? action.Index.Value.ToString(CultureInfo.InvariantCulture) : throw new ArgumentException("Quest stage/objective requires an index from 0 to 65535.");
    }

    private static string Identity(string? plugin, string? formId, string? target)
    {
        if (target == "player" && plugin == null && formId == null) return "@player\t000014";
        if (target != null || plugin is not { Length: > 4 and <= 255 } ||
            !plugin.All(c => c is >= ' ' and <= '~' && c is not ('"' or '\\' or '/' or ':' or '\t')) ||
            !(plugin.EndsWith(".esm", StringComparison.OrdinalIgnoreCase) || plugin.EndsWith(".esp", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Runtime operation requires player or an explicit plugin/local form identity.");
        var local = formId?.StartsWith("0x", StringComparison.OrdinalIgnoreCase) == true ? formId[2..] : formId;
        if (local == null || local.Length is < 1 or > 8 ||
            !uint.TryParse(local, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var id) || id is 0 or > 0xFFFFFF)
            throw new ArgumentException("Runtime operation requires a nonzero local FormID without a load-order prefix.");
        return $"{plugin}\t{id:X6}";
    }
}
