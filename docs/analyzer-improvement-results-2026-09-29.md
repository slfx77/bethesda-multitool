# Analyzer improvements, 29 September 2026

The analyzer upgrades and build policy now live in `Multitool.Shared`, with Bethesda importing that policy. The same changes are applied to the canonical checkout at `C:\dev\Multitool\Multitool.Shared` and Bethesda's integration copy. They remain local changes; no submodule pin, commit, or remote was updated.

## Changes

- SonarAnalyzer.CSharp **10.27.0.140913 → 10.35.0.4138** and Roslynator.Analyzers **4.15.0 → 5.0.0**, centrally pinned in shared `build/Multitool.Analyzers.Versions.props`. The installed .NET SDK remains **10.0.401**, with its existing ILLink package **10.0.12**.
- Local **Development** retains .NET, Sonar, Roslynator, and test code analyzers. Packaging analysis moves to **Full**, required by CI, pack, and publish. **Fast** explicitly skips build analysis. Compiler errors remain active in every profile.
- Development and Fast each have separate `bin`/`obj` subdirectories. Full keeps the existing paths. All profiles exclude generated outputs from source globs. Single-file applications retain their SDK tool-pack dependency across profiles so their lockfiles do not oscillate.
- `AD0001` and `AD0002` are errors. A failed analyzer can no longer produce a successful analyzed build. Full rejects disabled code analysis and disabled packaging analysis where the corresponding shipping feature is enabled.
- Shared coordinator options expose project concurrency, compiler concurrency, and heap limits. Compiler concurrency uses a checked-in `/parallel-` or `/parallel+` response file; the installed SDK does not pass `ConcurrentBuild` to Csc. A reusable profiling script captures invocation, compiler/process memory samples, detailed analyzer timings, and a binlog, with a timeout for its owned process tree.
- Shared now has a Full CI workflow covering its six test executables, coordinator regressions, and real-SDK analyzer-policy fixtures. Bethesda's existing workflows explicitly use Full.

The latest releases do not establish a fix for the previous out-of-memory failures. Release sources: [Sonar 10.35](https://github.com/SonarSource/sonar-dotnet/releases/tag/10.35.0.4138), [Roslynator 5.0](https://github.com/dotnet/roslynator/releases/tag/v5.0.0), and [Microsoft's .NET 10 downloads](https://dotnet.microsoft.com/en-us/download/dotnet/10.0).

Roslynator 5 removes the previously enabled informational RCS1063 `do (true)` style hint. Its successor requires a style setting that also affects `for (;;)`; that broader style preference was not introduced. The other release-listed removed rules were already disabled, or their implementation was already absent in 4.15. This review found no warning/error rule coverage lost through those removals. No `.editorconfig` diagnostic severities were weakened.

Full CLI builds now enable trimming diagnostics instead of the previous blanket suppression. Runtime publishing settings are retained. Existing ordinary warnings remain warnings; the crash gate guarantees that analysis completed, not that all preexisting warning debt has been resolved.

## Measurements

Measurements below force a Release `net10.0` compilation of Bethesda's main project with dependencies already built. They use one MSBuild node, an isolated compiler, and `ReportAnalyzer=true`. Memory is sampled process memory, not managed allocation attribution or an exact peak. Each row is one local observation, not a statistically controlled benchmark.

| Profile | Compiler | Heap allowance | Elapsed | Peak compiler working set | Result |
| --- | --- | --- | --- | --- | --- |
| Full | Serial | 6 GiB | 240.85 s | 6.22 GiB | Timed out at the four-minute limit |
| Full | Serial | 8 GiB | 293.75 s | 8.18 GiB | Passed, 198 warnings, zero errors or analyzer crashes |
| Development | Serial | 8 GiB | 172.31 s | 4.33 GiB | Passed, 173 warnings, zero errors or analyzer crashes |
| Development | Parallel | 8 GiB | 95.10 s | 4.49 GiB | Passed, 173 warnings, zero errors or analyzer crashes |
| Development | Parallel | 6 GiB | 94.57 s | 4.46 GiB | Passed, 173 warnings, zero errors or analyzer crashes |

Holding compiler mode and heap allowance constant, Development completed **41% sooner** and used **47% less peak compiler working memory** than Full. Parallel Development then completed **45% sooner** than serial Development with a small increase in working memory. Compiler parallelism therefore remains the default; serial mode is an explicit option for constrained Full analysis. Project-node and automatic heap defaults were not raised globally.

The 6 GiB Development run completed with essentially the same time and memory as the 8 GiB run. This workload gained no meaningful benefit from the larger allowance. The normal local profile keeps code analysis enabled while avoiding the packaging work that dominated Full.

The completed Full run reports 223.62 seconds of analyzer execution: **ILLink 132.13 s (59%)**, **Sonar 71.32 s (31%)**, and **Roslynator 3.51 s (1%)**. These execution timings identify expensive analysis, not which analyzer allocated the memory. A ten-second sampled-thread/GC trace is also retained; its thread-wait percentages are not CPU attribution.

Serial Development reports 100.14 seconds of analyzer execution, including Sonar 78.51 s, Roslynator 3.86 s, and ILLink 0.005 s. ILLink remains registered through the unchanged packaging dependency but does no substantive packaging analysis in this profile. Code analysis remains enabled. These results support changing the analysis schedule, not removing code-quality rules or claiming that the package upgrade alone caused the improvement.

The earlier 10m48s build had seven analyzer out-of-memory exceptions. It is historical context, not an equivalent benchmark: package versions, analysis completion, enabled trimming diagnostics, build scope, and resource settings differ. The first 6 GiB experiment also preceded exclusion of a stray generated `TestOutput` source file. A failed Development preparation run only discovered a missing DDXConv profile dependency and is excluded from performance comparisons.

## Validation and evidence

- **65/65 real-SDK policy checks passed** in 35 seconds: real throwing-analyzer errors, compiler errors under Fast, CI and distribution guards, profile cache reuse, and identical single-file lockfiles across Full → Development → Fast → Full. Both original Full DLL hashes and timestamps were retained after switching profiles.
- Canonical Shared Full restore and a subsequent `--locked-mode` restore passed. All 15 affected Shared solution lockfiles (eight libraries, six test projects, and the native rendering sample) add only the two analyzer packages. Locked verification did not change their bytes.
- **20/20 coordinator regression tests passed**, including process-environment restoration, CI/profile selection, both-slot admission, and abandoned-owner recovery. Their SDK calls are fake; policy fixtures separately exercise the real SDK.
- Bethesda's Windows Full/x64 build passed with **875 warnings, zero errors and zero analyzer crashes**, including generation of all **124 shader permutations**. Its 7m40s MSBuild time includes dependency builds, Windows/XAML work and shader generation, so it is not comparable to the isolated main-project profiling rows.
- Canonical Shared's Full/x64 solution build passed with **1,467 warnings, zero errors and zero analyzer crashes**. The new CI workflow uses explicit solution/project x64 mappings so `--no-build` test commands resolve the outputs actually built; existing AnyCPU mappings remain available.
- Bethesda's final complete default suite passed: **16,402 total, 15,231 passed, 1,171 skipped, zero failed**, in **20.131 seconds** of runner time. Repeated validation exposed two intermittent fixture defects: blocking thread-pool work in a disposal test and discovery in the shared temporary directory. The repairs retain all assertions and timeouts, use dedicated workers for the blocking operations, and confine directory discovery and its parent search to a fixture-owned tree. The final test build retained code analysis and passed with 412 warnings and no errors or analyzer crashes.
- Canonical Shared's final results are **8,579 total, 8,536 passed, 43 skipped, zero failed**, combining the five unchanged passing suites with the final complete Blender rerun. Blender passed **904 cases with 23 explicit skips**, retaining its 927-case total. Validation exposed six stale checks from committed typed-billboard implementation `805d4c767`. Repairs check the distinct construction and evaluation-limit rows (including the 180-degree bound), retain unsupported-mode expectations, and initialize the owned context cache explicitly while preserving the existing architecture guard. A further test counted temporary directories owned by other tests; it now asserts its own output remains absent, the exact missing-tool error, and that Blender is never invoked. These are test/initialization repairs, not suppressed or skipped failures.

| Canonical Shared suite | Passed | Skipped | Failed |
| --- | --- | --- | --- |
| Core | 3,473 | 6 | 0 |
| DevTools | 125 | 0 | 0 |
| Media | 3,227 | 14 | 0 |
| Blender | 904 | 23 | 0 |
| WinUI | 199 | 0 | 0 |
| Direct3D12 | 608 | 0 | 0 |

Ordinary existing code warnings remain visible. Full analysis is required and analyzer crashes are fatal; this change does not claim that all warning debt has been fixed. The builds, package restores and test executables were verified locally; hosted CI and release publishing were not invoked.

Bethesda's Full restore also reconciles its previously incomplete Windows lock graph and existing project-reference metadata. AudioTranscriber's SDK-selected ILLink lock moves from 10.0.11 to the installed SDK's 10.0.12. Other resolved runtime package versions are unchanged in the comparable preexisting framework graphs; no runtime package version pins were edited.

Local artifacts:

- `TestOutput/analyzer-improvements-20260929/final-validation.json`: combined final counts, matching policy/repair hashes, and confirmation that preexisting canonical changes were preserved.
- `TestOutput/analyzer-improvements-20260929/`: targeted before-file snapshots and validation logs.
- `TestOutput/analyzer-profiles/upgraded-full-serial6-20260929-124025-673/`: bounded 6 GiB experiment.
- `TestOutput/analyzer-profiles/upgraded-full-serial8-20260929-124548-519/`: successful Full profile, timings, binlog, memory samples, and trace.
- `TestOutput/analyzer-profiles/upgraded-development-serial8-ready-20260929-125705-778/`: comparable Development serial profile.
- `TestOutput/analyzer-profiles/upgraded-development-parallel8-20260929-130043-380/`: Development compiler-concurrency comparison.
- `TestOutput/analyzer-profiles/upgraded-development-parallel6-20260929-130309-122/`: successful Development run with the smaller heap allowance.
- `shared/Multitool.Shared/TestOutput/analyzer-policy-20260929-125619-371c6bf3/receipt.json`: 65-check policy receipt.
- `TestOutput/analyzer-improvements-20260929/test-results/bethesda-tests-final.trx`: final Bethesda suite.
- `TestOutput/analyzer-improvements-20260929/canonical-shared-validation-20260929-131152/`: Full/x64 Shared build and five unchanged passing suites, with the original Blender failures retained for diagnosis.
- `TestOutput/analyzer-improvements-20260929/canonical-blender-repair-20260929-132333/TestResults-final/Slfx77.Multitool.Media.Blender.Tests.trx`: repaired Blender suite.

See [shared profile instructions](../shared/Multitool.Shared/docs/analyzer-profiles.md) for exact commands and [the preceding investigation](analyzer-performance-review-2026-09-29.md) for the original configuration findings.
