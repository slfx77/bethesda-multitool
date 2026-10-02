# New Vegas audit: export, condition, and provenance follow-up

These notes describe the continuation of Claude's audit-improvement work. They cover the export/provenance review assignment, not every unrelated change in the existing working tree. No live TCRF content was accessed.

## Corrections implemented

- An explicitly blank `esm diagnose-scripts --actor` does not activate the historical Ulysses/Chomps defaults. Blank-only input is rejected before reports are written.
- Plugin SCTX is described as source stored in that plugin. Storage alone does not establish authorship. SCTX carrying BethesdaMultitool's known dump-conversion reconstruction banner is recognized after a plugin is reopened, even when in-memory origin metadata is gone.
- Script manifests distinguish `stored-source`, `captured-source`, and `decompiled-from-scda`. The summary uses `storedSourceFiles` and `capturedSourceFiles`. This replaces the undeployed Batch 4 `authoredSourceFiles` contract; no alias is retained. Legacy provenance API names such as `AuthoredPlugin` remain for compatibility and are documented without an authorship guarantee.
- A captured inline INFO/TERM SCTX rejected by the executable/source validation gate retains a withheld-source reason through parsing and duplicate merges. Reports distinguish rejected captured evidence from source absent in a partial dump. Incomplete executable bundles no longer imply that their code was absent from the capture.
- Both aggregate and per-file script reports receive dump identity, including the explicit `ReportDataSources.RuntimeEditorIds` hint. Extraction supplies its input path to the script export source manifest/hash path.
- Generic `show --full` preserves long decoded/PDB string values in verbatim blocks. The active DIAL presenter lists every INFO, removing the old twenty-response cap. The typed FO3/FNV INFO presenter defers other games to their schema presentation.
- CTDA type flags are game-aware. The 0x08/0x10 bits remain unknown in Oblivion/FO3/FNV rather than being called modern Pack Data/Swap Subject-Target flags. Structured JSON retains the unknown bits.
- Structured dialogue report condition values omit their next-list connector; grouping is a separate section. Appending a condition therefore does not spuriously change the preceding condition's own comparison value.
- The older `tools/EsmAnalyzer` dump script comparator excludes reconstructed SCTX from source-versus-decompilation comparisons, preventing self-comparison after BMT reconstruction.

## Load-order integration

`RecordCollectionFormIdRebaser` now treats PACK locations/targets and CTDA operands as typed unions. It rebases actual FormIDs, global comparison references, and semantic Run-On references while retaining scalar indexes, ignored storage, and authoritative CIS string placeholders. Ordered script SCRO/SCRV tables preserve high-bit-tagged local-variable indexes instead of rebasing them as FormIDs.

Stored source, decompiled strings, raw SCDA, and schema-decoded textual values retain their source file's namespace. The load-order command labels that limitation; typed reference values use the load-order namespace. Root integration also limits the typed merged session to 128 loaded/reserved slots because the current script model uses the high bit to tag SCRV locals.

## GUI extraction route

The existing Single File Extract action requests ESM reports. `MinidumpExtractionReporter` passes captured scripts to `EsmRecordExporter.ExportParsedScriptsAsync`, which uses the shared script writer and the game-default extension. FO3/FNV per-script output therefore uses `.gek` through this GUI route as well as the CLI. The batch route uses the same exporter. This route was checked statically; both CLI and Windows GUI targets compiled successfully. GUI interaction was not exercised.

## Final validation

The coordinating agent completed sequential validation: CLI/test and CLI/GUI builds passed with zero compiler warnings/errors; focused regressions passed 980/980 with 25 opt-in skips; the default suite passed 15,210/15,210 with 1,126 skips; all 21 selected corpus checks and three real CLI evidence checks passed. The old `authored` text assertion was corrected to require the neutral stored-source label and reject the obsolete wording. The analyzer-enabled build had 238 warnings, including four out-of-memory warnings from the trimming analyzer, so that pass is not a clean analyzer result. Exact commands, logs and remaining limits are in the [completion report](../../../Media/new-vegas-cut-content/continuation-2026-09-29/COMPLETION.md).

Targeted classes include `TaggedFormIdRebaseTests`, `EsmLoadOrderAndRebaseTests`, `ScriptSourceProvenanceTests`, `ScriptExportWriterTests`, `ScriptExportRetailTests`, `WithheldCapturedSourceTests`, `ConditionDescriberTests`, `TerminalMenuReportTests`, `DialogueReportConditionTests`, `InfoRecordDetailTests`, `GenericShowRendererTests`, `ShowFullTextTests`, and `EsmDiagnoseScriptsCommandTests`.

## Real-asset assertion corrections

The coordinated corpus pass exposed two inaccurate expectations, investigated with the existing EsmAnalyzer raw tools:

- Retail OldWorldBlues NPC 0x01013057 at file offset 0x138FD9 has ACBS template flags 0x03FD and local CNTO order 0016BB9D, 0101387F, 0101320D, 01013880. Its inventory source 0x0101304B at 0x139C81 has flags 0x0041 and order 01013880, 0101320D, 0101387F, 0016BB9D. Both contain the supposedly absent item. The corrected test checks inherited group/source attribution and source ordering, proving UseInventory wins despite resident local CNTO.
- July 2010 FalloutNV.esm physically contains 43,926 INFO records, of which 6,105 distinct FormIDs carry 6,830 SCDA blocks; none of these FormIDs has SCDA in both physical copies. The old 5,729/5,122 counts are correct for the outer record stream but exclude the serialized INFO records inside TOFT. The BMT commands `search raw-hex` and `subrecord-size-census` both include that stream; they agreed on the combined census, but share scanning code and are not independent corroboration of one another. TERM remains 197 blocks/128 records and PACK 457 blocks. The regression test now includes its own framed header/subrecord walk and compares every INFO bytecode payload against the parsed models, without relying on the semantic decoder for expected values.

No production change was needed for either finding. The final selected corpus run passed all 21 tests.

The independent census initially rejected an unrelated July world-group size mismatch: GRUP 0x02A74A4C declares size 0x2CC3D/end 0x02AA1689, while its last child GRUP at 0x02A9D81D has size 0x3E84/end 0x02AA16A1. The next valid world-child group starts at that latter offset. Raw hex inspection confirms the 24-byte disagreement. The INFO oracle now walks physical GRUP headers and record-sized bodies sequentially, checking file/subrecord bounds without imposing strict nested GRUP bounds. Its INFO counts and every-byte payload comparisons remain unchanged.

An independent Python physical-header/subrecord walk subsequently isolated the populations in the exact 260,570,346-byte July file. The outer stream has 36,360 INFO records, 5,729 SCDA blocks, and 5,122 script-bearing FormIDs. Nonempty TOFT at 0x01A24CBF frames a 1,054,149-byte payload at 0x01A24CD7..0x01B2629C; this payload tiles exactly as 7,566 INFO records, with 1,101 SCDA blocks across 983 further script-bearing FormIDs. The combined totals are 43,926 INFO / 6,830 SCDA / 6,105 script-bearing FormIDs, without duplicate script-bearing IDs across populations. These describe serialized records, not runtime activation or execution. The test now descends the recognized TOFT container with strict payload bounds and pins its contribution separately while comparing every combined SCDA payload with the semantic model. Raw evidence and the standalone reproducible diagnostic are in `C:\dev\Media\new-vegas-cut-content\continuation-2026-09-29\independent-july-complete-census.json` and `independent_july_census.py`.
