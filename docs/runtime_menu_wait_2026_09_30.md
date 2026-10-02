# Repeatable runtime message scenarios

PC009 proved the native guarded engine handler and its dedicated message callback. Its CLI scenario also exposed a timing gap: the next message may not exist on the frame immediately following dismissal. The artifact harness succeeded by repeating only read-only inspections. The supported `wait-message-state` action brings that behavior into ordinary scenario manifests.

```json
{
  "actions": [
    { "kind": "message-probe" },
    {
      "kind": "wait-message-state",
      "target": "probe",
      "message": "BMT runtime observation: click OK.",
      "button": "Ok",
      "buttonIndex": 0,
      "timeoutMilliseconds": 10000,
      "pollMilliseconds": 100
    },
    {
      "kind": "choose-message",
      "target": "probe",
      "message": "BMT runtime observation: click OK.",
      "button": "Ok",
      "buttonIndex": 0
    }
  ],
  "observeMilliseconds": 15000,
  "scriptTrace": { "mode": "requested" }
}
```

The wait requires exact ASCII message text, label and single-button index0. Its timeout is1–30000ms and poll interval50–1000ms. Only a validated native `message-state` with status`unavailable` and reason`menu-not-visible` is retried. A visible text/button/owner mismatch or a native structural error terminates the scenario. A choice is sent once and remains independently guarded by the engine-thread menu/queue/callback checks. No Windows foreground/input is required.

Native wire protocol remains1. Native13 adds the `messageStateAvailability` capability and distinguishes a successfully read hidden-menu visibility flag from unreadable/invalid layout or ownership. A wait is rejected before capture if the backend lacks that contract; older backends are not silently treated as supporting normal unavailable observations.

Traces containing a wait use schema2. Controller `scenario-result` rows have their own origin/action/outcome/monotonic elapsed time and only associate a native request/frame where actually observed. They do not create native sequence numbers or increment native event counts. Controller results/errors are separately counted; errors still contribute to the overall scenario result. Import supports schemas1 and2. Prior readers explicitly reject schema2 instead of silently accepting an incomplete interpretation. Captures without a wait continue using schema1.

Native13 and the existing native forwarding/ownership/mutation suite passed. Parent-coordinated managed checks passed 123 runtime/isolation and 101 message/script cases. Actual PC010 supported CLI replay passed: exact probe wait, one guarded choice, dedicated instance-1/button-0 engine callback, complete verified 10-plugin namespace, and zero errors in the positive trace. Its unqueued-message control timed out without sending the subsequent choice (expected CLI exit 2). All 13 traces import complete with zero drops/gaps; one setup post-dismissal layout/owner guard error and the expected host timeout remain preserved. See [PC010 evidence](../artifacts/prototype-runtime/pc/session-010-summary.md).

Immediate post-dismissal inspection may still return the guarded layout/owner mismatch; the wait retries only validated unavailable visibility states. The GUI now shows controller rows/counts separately under Session outcomes, with exact source-line/hash access. Fast15 actual replay verified Observed, Timed out, record-independent outcomes and legacy hiding; all five screenshots were viewed. The 107 focused document/session tests passed. [GUI evidence](../artifacts/prototype-feedback/bmt-gui-session-outcomes-fifteenth-attempt2/FINDINGS.md). The frozen generation10 research exports and native012/PC009 evidence remain unchanged. These scenarios do not add combat targeting, weapon damage calculation, full CTDA evaluation, level formulas or navigation.
