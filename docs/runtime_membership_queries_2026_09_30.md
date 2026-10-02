# Runtime dialogue and terrain membership

The CLI now accepts `read-dialogue-state` and `read-cell-terrain` actions with explicit plugin/local FormIDs. Native20 implements the PC readers; CLI20 handles their terminal `record-membership` event. PC039 verified the selected retail dialogue and interior null-LAND controls. Broader engine validation remains pending.

```json
{
  "actions": [
    { "kind": "read-dialogue-state", "plugin": "BMTDialogueControl.esm", "formId": "000A00" },
    { "kind": "read-dialogue-state", "plugin": "BMTDialogueControl.esm", "formId": "000A00" }
  ],
  "observeMilliseconds": 2000
}
```

Each action executes on a separate game-loop callback. Compare the retained frame numbers and both observations. The control plugins are prepared locally and have not been installed into a game.

Dialogue output keeps quest groups and ordered INFO slots, including holes and duplicates. It retains direct and indirect ownership candidates, selects a unique validated path, and rechecks the consumed pointers and identities. Reads stop at 16 groups or 256 slots. Changed identities, unreadable storage, cycles, count mismatches and limits produce Partial results.

CELL output keeps load state, raw fields and the actual LAND identity at `CELL+0x4C`. A null pointer in a loaded/attached cell is Empty; an unloaded cell reports Unavailable. Height decoding remains a separate dependency.

Current live result: CLI40 / Native33 observed retail Doc Mitchell INFO order 00105598 → 00105599 and the interior CELL 00103DF9 null LAND pointer. Later-frame reads and independent reload/readback agreed. Four captures retained 7,269 events without errors or loss; normal closure and originals passed. Response selection and positive exterior LAND remain unperformed. [PC039](../artifacts/prototype-runtime/pc/session-039-summary.md).

Validation:

- CLI20 build: zero warnings/errors; all 95 focused runtime-session cases passed, including encoding, target rejection and waiting for the membership terminal event. [Receipt](../artifacts/prototype-feedback/focused-twentieth-membership.receipt.json).
- Native20 build: zero warnings/errors; 28 membership cases passed alongside the existing native fixtures. These cover sparse/duplicate INFOs, competing paths, unreadable memory, cycles, bounds, changing membership, expected-owner mismatch, CELL state, LAND identity changes and request admission. [Receipt](../artifacts/prototype-runtime/pc/native-generation-020/validation.json).
- Independent review found two identity races in Native18. Native19 checks the resolved subject identity and rechecks consumed links/record identities. Native18 is preserved.
- Native20 requires an observed game load and cross-checks the form map against the SDK player identity. Requests outside a capture return an explicit error.
- The full CLI20 runtime is frozen with 107 hash-verified files. [Manifest](../artifacts/prototype-runtime/pc/tools/cli-generation-020.manifest.json).

All six full-master dialogue control readbacks passed: base, pure parent-only, ordinary move/delete/order, fork, cycle and duplicate. Inputs and the frozen CLI18 runtime stayed unchanged. [Static validation](../artifacts/prototype-feedback/merge-dialogue-controls/validation.json).

Next: bind each dialogue control's loaded slot and file hash, compare the two engine observations with the selected report, and repeat in fresh sessions for parent-only, ordinary and diagnostic patches. LAND controls additionally require a loaded exterior cell. [Control design](../artifacts/prototype-feedback/world-actor-rendering/DIALOGUE-LAND-CONTROLS.md) · [Prepared dialogue controls](../artifacts/prototype-feedback/merge-dialogue-controls/plugins/manifest.json).
