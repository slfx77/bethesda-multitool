# Runtime scenario milestones

Built; [156 focused runtime checks pass](../artifacts/prototype-feedback/focused-seventeenth-runtime-corrected.receipt.json). Engine acceptance is pending in isolated PC session011. No runtime GUI changes.

Scenarios can declare `milestones` alongside `actions`, `observeMilliseconds` and `scriptTrace`. Each milestone names an event, typed field comparisons, a deadline and optional plugin/local form identities. `afterActionIndex: -1` checks initial state; other indices check after the corresponding action. Failure stops further actions and retains every outstanding milestone as a result.

`observeMilliseconds` is the total capture duration, including start acknowledgement, actions and milestones. Allow time for the complete sequence; the PC settings control uses 10,000 ms.

```json
{
  "actions": [],
  "observeMilliseconds": 10000,
  "milestones": [{
    "id": "reference-enabled",
    "afterActionIndex": -1,
    "eventKind": "reference-state",
    "timeoutMilliseconds": 800,
    "probe": { "kind": "read-reference-state", "target": "player" },
    "fields": [
      { "path": "state.statistics.Disabled.status", "value": "observed" },
      { "path": "state.statistics.Disabled.value", "value": 0 }
    ],
    "forms": [{ "path": "engineTargetFormId", "target": "player" }]
  }]
}
```

Probe milestones repeat only the specified read action and accept its terminal event with the matching request ID. Passive milestones require explicit observed form identities. After-action passive checks also require the backend's `actionBoundaries` capability and an actual `action-begin` event for that action. Initial passive checks begin at `capture-start`. Plugin identities resolve through the verified active plugin namespace.

Field paths use bounded dot-separated properties or array indices. Comparisons are `equal`, `not-equal`, `less`, `less-or-equal`, `greater` and `greater-or-equal`; numeric equality accepts an explicit nonnegative `tolerance`. Strings and Booleans retain their types. A missing field does not match zero or false.

Limits: 64 milestones, 32 fields and eight form bindings per milestone, 30-second milestone deadlines, 50–1,000 ms probe intervals, 128 actions and 64 KiB scenario files. The overall capture deadline still applies. Host menu waits require probe milestones.

Trace schema 3 retains the scenario, issued action/probe identities, actual native observations, matched observation links and separate controller outcomes. The wire protocol stays at version 1. Import verifies each matched outcome against its original native line, form bindings, request/action boundary and recorded deadline. Missing outcomes, loss, cancellation and disconnect remain visible. Schema 1/2 imports remain supported.

The CLI reports missing sequences and returns a nonzero result for event loss. Focused checks cover queued pre-action events, passive events between probe polls, timeouts, wrong owners/request IDs, loss, cancellation, disconnects, unreached milestones and altered imported assertions. The initial disconnect failure is preserved in the [first focused receipt](../artifacts/prototype-feedback/focused-seventeenth-runtime-actors.receipt.json); the corrected runner avoids sending stop after capture end.

Typed reference, combat, inventory and quest operations are described in [runtime_typed_operations_2026_09_30.md](runtime_typed_operations_2026_09_30.md). Native acceptance and observed state remain separate fields.
