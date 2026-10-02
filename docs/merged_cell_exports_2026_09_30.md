# Merged cell exports

CLI18 built with zero warnings or errors. All 105 focused placement and actor checks passed. Three full-master readbacks passed; engine checks are pending. [Build](../artifacts/prototype-feedback/build-eighteenth-fast-retry.receipt.json) · [Tests](../artifacts/prototype-feedback/focused-eighteenth-placements-actors.receipt.json) · [Readback](../artifacts/prototype-feedback/merge-engine-controls/full-master-cli018-001/validation.json).

`esm cell objects` now accepts the existing `--load-order` and `--allow-missing-masters` options. It uses the shared record selection and physical placement query. A parent override retains independently selected children; replacements and moved children use their own physical source.

```text
btool esm cell objects Patch.esp CellEditorID --load-order FalloutNV.esm;Patch.esp --format json --output placements.json --limit 0
```

JSON appends the load order, missing masters, selected cell source and excluded physical occurrences. CSV appends the same identity fields and emits excluded rows with `SelectionStatus`. Deleted, ambiguous and unparsed winners stay visible as issues. Existing columns retain their order. Coordinates retain stored float precision.

Merged FormIDs use the explicit load-order namespace. `fileLocal` fields retain source identities; each row's source path, record offset and parent-cell offset identify its physical occurrence. The selected cell can come from a different plugin. `--offset` remains a single-file selector.

The [engine controls](../artifacts/prototype-feedback/merge-engine-controls/manifest.json) cover retained, replaced, deleted and moved children, plus a separate duplicate-record order. They are not installed. The [original readback failure](../artifacts/prototype-feedback/merge-engine-controls/ordinary-a.receipt.json) records CLI17 rejecting `--load-order`; it prompted this correction.

Parameterized fixtures cover merged JSON/CSV, provenance, fractional coordinates, deletion and duplicate offsets. Dialogue and LAND engine acceptance use separate controls.

The full PC master plus the authored controls produced three selected references and one deleted issue in cell A, one moved reference in cell B, and four selected references plus two distinct ambiguous occurrences in the duplicate order. Coordinates, base identities, physical source offsets and original parent offsets passed. Each command finished in 39–40 seconds with unchanged inputs.
