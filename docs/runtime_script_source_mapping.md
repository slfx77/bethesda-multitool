# Runtime script source mapping

**Reconstruction.** `RuntimeScriptSourceMap.BuildAsync` accepts an imported trace and an explicit ordered plugin list. Exact capture-time plugin names, slots and SHA-256 hashes gate every join. Physical winners, canonical script-block indexes, SCDA hash/order, ordered SCRO/SCRV slots and local metadata identify source blocks. Deleted, conflicting, duplicate or clamped winners remain unavailable. Stored source bytes are retained unchanged.

Each event reports owner, bytecode and location status separately, with its trace hash, line, byte span and sequence. Native command samples require stable before/after Script identity before matching the owner; matching bytecode additionally requires complete stable fingerprints. Temporary and embedded Script ownership remains unavailable without explicit engine evidence. Raw actor/reference context and EditorIDs do not supply script ownership.

The canonical decompiler now optionally emits statement and nested expression spans, SetRef associations and reconstruction-line ranges. Unknown/truncated ranges stay explicit. Uncompressed records with exact ordinary subrecord framing include physical SCDA file offsets; other records retain their physical record offset and canonical subrecord index.

| Dependency | Status |
|---|---|
| Runtime command offset calibration | Verified in PC047 for the expression-command route; broader routes Unavailable |
| Runtime reference/local table equality | Unavailable |
| General command/CTDA execution coverage | Unavailable |
| Managed build and focused checks | Current CLI51: 63 reuse/cache cases passed; CLI50 mapping validation: 93 cases; earlier 363-case validation retained below |
| Retained PC010 CLI replay | Passed: 1,106 mappings |

CLI50 / Native37 enable the PC expression-command profile only when a complete, error-free trace retains matching start/end fingerprints of the 1,161-byte dispatch body, executable identity and mapped bridge. The verified return route and SDK data pointers must match; the raw cursor minus four must identify a decoded expression opcode. PC047 matched 600 GetButtonPressed observations in SCPT00105230 at SCDA offset 79 (raw cursor 83); 492 other mappings remain Unavailable. [Expression-offset replay](../artifacts/prototype-runtime/pc/session-047-script-map-001/validation.json). PC043 and PC046 remain uncalibrated. `Script::Execute` entry/return pairs remain separate from instruction coverage. [CLI50 checks](../artifacts/prototype-feedback/cli50-dispatch-reference-sdk-002/validation.json) | [Native37 checks](../artifacts/prototype-runtime/pc/native-generation-037/fixture-followup-001/validation.json).

Native38 aggregates eight scene/reference notification kinds into explicit counters; queue loss still fails capture. Its 31 notification controls passed. PC049 retained 3,617 events and 18,311 aggregated notifications across five captures, with zero loss or errors. [PC049 review](../artifacts/prototype-runtime/pc/session-049-review-001/validation.json). [Native38](../artifacts/prototype-runtime/pc/native-generation-038/validation.json) | [CLI51](../artifacts/prototype-feedback/cli51-reusable-evidence-cache-001/validation.json) | [PC048 review](../artifacts/prototype-runtime/pc/session-048-review-001/validation.json).

API: `BuildAsync(document, sourcePaths, token)` returns `RuntimeScriptSourceMapReport`; `Serialize(report)` uses generated JSON metadata. See the [retained mapping audit](../artifacts/prototype-feedback/runtime-script-mapping-audit-001/FINDINGS.md) and [Native22 validation](../artifacts/prototype-runtime/pc/native-generation-022/validation.json).

CLI: `runtime import capture.ndjson --script-map new-map.json --source-plugin FalloutNV.esm Addon.esp`. Supply the complete captured plugin order. `--script-calls new-calls.json` writes the separate call-pair assessment; both output files must be new paths.

Call reports retain entry/exit lines, sequences, frames, Script identities and nesting per thread. Missing endpoints are **Partial**; duplicate endpoints, changed identities and conflicting nesting are **Ambiguous**. Event loss is retained. The stdout summary reports transport completion and call status separately.

CLI23 replayed PC010 against its ten captured plugins: 1,104 owner matches, two temporary-owner exclusions and one observed entry/exit pair at lines 145/146. Two physical source blocks were retained. Bytecode and location evidence are unavailable in this older trace. Source and tool hashes remained unchanged. [Replay](../artifacts/prototype-feedback/runtime-script-import-023/attempt-001/validation.json) · [363 focused checks](../artifacts/prototype-feedback/focused-twentythird-water-script-corrected.receipt.json) · [Independent review](../artifacts/prototype-feedback/runtime-script-import-023/review.md).
