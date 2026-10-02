This implements the concrete findings in the [29 September testing audit](testing-audit-2026-09-29.md). The working tree already contained extensive uncommitted work. Changes were compared against a byte-exact snapshot of the on-disk test sources, rather than treating every difference from HEAD as part of this cleanup.

The reduction removes 62 expanded test cases and 60 attributed test methods. Parameterization retains every distinct input in the converted utility and EDID tests; those conversions reduce source duplication, not the runner's case count. Runtime improvements also come from avoiding repeated filesystem scans, source reads, and JSON parsing/searches.

The final default suite passes. These are fresh local Release runs of the same main test project, using default parallelism with retail/GPU/shader opt-in variables unset. Timings exclude compilation.

| Metric | Before | Final | Change |
|---|---:|---:|---:|
| Expanded runner cases | 16,464 | 16,402 | -62 |
| Passed | 15,289 | 15,231 | -58 |
| Skipped | 1,171 | 1,171 | Unchanged |
| Failed | 4 | 0 | All four baseline failures repaired |
| Runner duration | 22.269 s | 20.381 s | 8.5% shorter in this comparison |
| Process wall time | 22.679 s | 21.108 s | 6.9% shorter in this comparison |
| Attributed source methods | 11,238 | 11,178 | -60 |
| Files with test methods | 1,509 | 1,508 | -1 |

This is a local before/after observation, not a statistically controlled benchmark. An intermediate run took 18.673 seconds but exposed two newly enforced category failures; those were corrected before the final run above. CLI subprocess timings vary, so the final measured reduction is more modest than that preliminary result. The Hoover test itself fell from about 15.2 seconds across two cases to about one second in the intermediate run by eliminating repeated parsing and linear JSON-property searches.

Three targeted native WARP cases also passed, with no skips, in 2.953 seconds: both mandatory-factory scene-sample rows (1 and 4) and the explicit single-sample adapter case. The ambient sample setting was 1, so the 4-sample row verified that the explicit request overrides it on this machine. Native capability probing and its existing fallback remain in place.

The final analyzer-enabled test-project build completed in 10m48.48s with zero compilation errors and 241 warnings. Several `AD0001` warnings report `OutOfMemoryException` inside ILLink and Sonar analyzers, so this is **not a clean analyzer validation**. No process was terminated: the main compile advanced just before the planned time-limit stop. The earlier builds using the repository's documented `SkipAnalyzers=true` option succeeded with zero warnings/errors in 48.85 and 65.62 seconds. These differing build modes are not a build-speed comparison. Analyzer cost is a separate observed contributor to the overall workflow time; this cleanup does not claim to solve it.

The production DLL and the copy loaded from the test output directory have matching SHA256 hashes (`BA67849024D6F48437FEFDB6256768E55904DBE6AC84A833388D5E896C6A424D`). The final test assembly was compiled after the last test-source edit. Snapshot-relative review and `git diff --check` on the affected tracked files passed.

| Retired or combined cases | Cases removed | Surviving protection or reason for retirement |
|---|---:|---|
| Logger | 11 | Five exact complete-output checks replace separate prefix/message checks; six auto-property readback rows are removed. Severity filtering, formatting, and repeated writes remain. |
| Carve manifest | 4 | Persisted defaults and every meaningful field remain checked. Independent JSON assertions cover serialization, and a literal JSON fixture covers loading. Separate file-exists/valid-JSON checks are subsumed by parsing the saved file. |
| Analysis tab policy | 2 | The exact Classic tab sequence already implies both retired subset assertions; the regression rationale stays beside it. |
| Analysis record availability | 2 | Ten explicit source/scan combinations remain, together with the cross-policy check; duplicate examples are removed. |
| Shadow comparison | 10 | Calculations compared two test-local versions of the same arithmetic, without invoking a shipping implementation. Derivations are preserved as documentation. |
| Double-sided normal basis | 1 | Test-local algebra did not exercise the renderer. Production shader contracts remain. |
| Tonemap settings | 2 | Test-local arithmetic did not exercise production tonemapping. Settings and shader contracts remain. |
| Grass alpha-to-coverage routing | 1 | The retained grass shader test now also owns the unique grass/alpha/decal gate and both pipeline-result assignments. |
| Recovered HDR reference | 21 | These checked BCL operations and a test-local model, not production code. The complete original file is preserved as a reference artifact. |
| File-header performance helpers | 4 | These exercised file scanning/error handling written inside the test. All 15 actual NIF/DDX classifier rows remain; unnecessary temporary-directory setup is removed. |
| Non-pumping parallel balancing | 1 | The assertion checked only completion count after a 120 ms sleep, so it could not establish balancing. Existing execution/completion coverage remains. |
| Category consistency | 2 | Three scans become one scan checking all three guard/category pairs; runtime guards additionally validate the executing test's effective xUnit traits. |
| Hoover attribution | 1 | One parse and indexed lookup retain all 477 IDs, ten gap exclusions, 467 required mappings, the interior flag, and editor-ID prefix. |
| **Total** | **62** | |

The recovered calculations are documented in [retired derivations](testing-retired-derivations-2026-09-29.md). The complete [HDR reference](reference/RecoveredHdrShaderReference.cs.txt) is byte-for-byte equal to the captured pre-cleanup source: 17,993 bytes, SHA256 `35756B759BA682024882AFB54C91C47F92C8068FC63CC22E0CEE964DBE2733C7`. It remains available for future production-output comparisons; its retirement does not imply that rendering has been validated against that model.

The cleanup also repairs weak tests without reducing their cases:

- The BSA known-hash test now writes a real archive and checks the emitted hash bytes, retaining both independent vanilla constants. It no longer invokes a private method through reflection.
- The reset-view handler check accepts equivalent block and expression bodies. Settings checks use member boundaries and assignments instead of documentation comments, and distinguish writes from comparisons.
- Four source-contract failures present in the fresh baseline were reconciled with existing renderer refactors. Checks still require packet retirement, staged signature disposal after packet release, disposed-mesh rejection, and absence of legacy counter writes inside the packet-hit path.
- Both single-flight tests retain 32 callers and all result/load-count assertions. Dedicated workers and cache-hit coordination establish arrival while the first load is held; fixed delays are removed. Failure paths release and await workers before disposing resources.
- GPU fixtures pass a scene-sample request directly into `GpuDevice12.Create`. Ordinary callers retain the environment fallback. The fixture class that still changes shader configuration uses the nonparallel process-environment collection.
- Guard validation now requires the corresponding category, including the executing test's effective metadata before skipping. This caught a BucketB label on the synthetic `Tes4PersistentDummyCellTests` class instead of its neighboring retail integration class; the label was moved. Ingredient parser synthetic cases are likewise separated from the retail method's BucketB label. Static inspection is still file-scoped; runtime validation supplies scope accuracy for guard paths that execute.

`SourceContract` caches normalized source text and filename indexes for one test process. Three architecture scans share the production-file inventory and cached text while retaining their original file filters and assertions. Missing or duplicate flat filenames still fail. The cache assumes the checkout is stable during a run and refreshes in the next process.

The initial build exposed a pre-existing shared-type rename: three new export adapters and two accompanying tests still referred to `SceneDocument`, which is now `ModelDocument`. Those five files were updated to the existing type before capturing the runnable baseline. Original copies are preserved separately. This prerequisite repair is distinct from the test reductions.

No coverage percentage was collected. Preservation was reviewed against the original inputs, assertions, emitted-byte oracles, and production entry points; passing tests alone cannot prove identical fault detection. Distinct format, malformed-input, byte-order, encoding, game/release, and CLI executable checks were retained. No tests were moved behind opt-in flags to manufacture a faster default run.

The existing source-contract suite remains a review queue, not a deletion list. Its 181 files mentioning `SourceContract` still include valuable compatibility and architecture assertions. The remaining slow CLI executable tests exercise real packaging/runtime behavior, and the large format suites retain independently meaningful cases.

Local evidence is under `TestOutput/testing-reduction-20260929`: source snapshots and SHA256 manifest, prerequisite originals, snapshot-relative `changes.patch` and `changed-files.json`, Roslyn inventories, build logs, xUnit XML reports, timing receipts, and `results-summary.json`. The snapshot-relative patch covers 37 test-cleanup files, including the small production GPU option change; the five prerequisite type-renaming files have separate originals. This directory is ignored by Git. The substantive changes, counts, preservation rationale, and validation outcomes are recorded in this report for durability.

To repeat the default execution after building:

```powershell
pwsh -NoProfile -File tools/scripts/build.ps1 -Project tests/BethesdaMultitool.Tests/BethesdaMultitool.Tests.csproj -Property SkipAnalyzers=true
pwsh -NoProfile -File TestOutput/testing-reduction-20260929/run-validation.ps1 -Run final
```

Use `-Run warp` for the three targeted native checks. The script changes opt-in variables only inside its child PowerShell process. The XML reports and small JSON summaries preserve outcomes; do not confuse the source-method inventory with expanded case counts.
