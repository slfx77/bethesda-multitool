namespace BethesdaMultitool.Core.RuntimeSession;

internal static class RuntimeResponse
{
    internal static string Kind(RuntimeRequestKind request, string? action = null) => request switch
    {
        RuntimeRequestKind.Start => "capture-start",
        RuntimeRequestKind.GuestRunBind => "guest-run-bound",
        RuntimeRequestKind.GamepadPulse => "gamepad-result",
        RuntimeRequestKind.Evaluate or RuntimeRequestKind.QuestRead => "snapshot",
        RuntimeRequestKind.MessageProbe => "message-probe-start",
        RuntimeRequestKind.ConditionProbe => "condition-probe-result",
        RuntimeRequestKind.OwnerConditions => "condition-list-exit",
        RuntimeRequestKind.ActorSnapshot => "actor-state",
        RuntimeRequestKind.ReferenceSnapshot => "reference-state",
        RuntimeRequestKind.Inventory when action == "read-inventory" => "inventory-state",
        RuntimeRequestKind.QuestState when action == "read-quest-state" => "quest-state",
        RuntimeRequestKind.RecordMembership => "record-membership",
        RuntimeRequestKind.MessageMenu when action is "read-message-state" or "wait-message-state" => "message-state",
        _ => "action-result"
    };
}
