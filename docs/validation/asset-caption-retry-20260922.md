# Asset check crash and caption hover: retry validation, September 22, 2026

Status: in progress. Sections marked pending are filled in as each gate completes; nothing
below claims a result that has not been executed.

Base: BMT `e38d0e3008646046e50d386b38a5ccf4e1d0878d`, retained clean Shared
`20e4e2c0fb2d304ed60c05e48d8c97a9a730b684`, DDXConv `805ef26b605be360ff0ca25aa9a54435be51bcfe`.
This continues the [September 21 follow-up](asset-caption-regression-20260921.md), whose queued
portable build timed out before compilation at the coordinator's 4 GiB admission gate. All
completion-program gates and preservation requirements remain active; the session runs from
Claude Code, registered as the `bethesda` root with an explicit thread id because no Codex thread
exists here.

## Owner rulings taken on September 22

- "Bypass the coordinator for now. This is the only GUI tool being worked on." Applied as
  `-MinFreeGB 0` on the maintained wrapper and on every lease the helpers take; the checkout and
  slot mutexes, isolated compilation, `DOTNET_gcServer=0`, conserve-memory, the 12 GiB compiler
  heap cap, one MSBuild node, `RestoreLockedMode=true`, the serial analysis response file and
  normal analyzers are all retained. Every receipt carries that statement. Build launchers run at
  BelowNormal priority so a paging build leaves the desktop usable.
- The caption fix is validated as the "8-DIP strip plus fixes" variant (below), not the applied
  one-pixel strip.
- After validation the five fix files and the documents become one local checkpoint, no push.

## Source amendment before the freeze (App files only)

The adversarial review of the one-pixel strip found that at scale 1.00 the help passthrough
rectangle and the native caption boundary touch rather than overlap, that the native caption hover
state clears only through a leave notification the passthrough region prevents, and that a
one-physical-pixel drag strip only helps when a discrete pointer report lands on it (a 125 Hz
mouse at ordinary speed moves about 4 px per report). It also found that the new presenter checks
gated the pre-existing 138 to 144 pixel padding repair, and that one shared 64-event budget could be
spent by incidental caption or border events before a review. `MainWindow.xaml` and
`MainWindow.xaml.cs` therefore changed as follows before any freeze:

- `TitleBarMinDragRegionWidth` construction fallback is 8 DIP; after layout the strip is
  `max(8 DIP, 1 / RasterizationScale)` and only the strip depends on the trailing header presenter.
- Pointer diagnostics use three budgets (help; Minimize, Maximize and Close; other native kinds), each
  announcing exhaustion once, and a `RegionsChanged` snapshot (verified to exist in
  `Microsoft.InteractiveExperiences.Projection` 2.1.6) records native region replacements.
- The file was rewritten with LF line endings; git and the LF-normalized freeze hashes see no
  difference from that.

Independent source review predicted no compile failure for the frozen change (pattern variable
in a `&&` initializer, `ref` to instance fields, `or` patterns, `ChangedRegions` as an array,
handler signatures, no warnings-as-errors path, no namespace ambiguity, template gates matching the
stock WinUI 2.3.6 TitleBar template). Runtime notes: at scale 1.00 the separator refresh line does
not fire because the computed 8 equals the fallback; the 138 to 144 repair still runs before the
presenter is realized.

## Attempts

| Attempt | Directory | Result |
| --- | --- | --- |
| 1 | `TestOutput/asset-caption-retry-20260922` | Freeze verified (48 inputs, 15 helpers). Admitted at 1.69 GB free under the bypass. Every changed source compiled with only the two inherited warnings (CA1068, S3877), then the SDK's `GenerateDepsFile` task failed: `Could not load file or assembly 'Microsoft.Extensions.DependencyModel, Version=10.0.0.11'`. Cause: the Visual Studio Installer update that started at 10:39 local removed .NET SDK 10.0.400 while the build was in flight (28:25 elapsed). Exit 1 after 1758 s. Preserved. |
| 2 | `TestOutput/asset-caption-retry2-20260922` | After the installer finished (SDK 10.0.401 present, `DependencyModel` 10.0.0.12), the locked restore failed with NU1004: the new SDK raises the implicit `Microsoft.NET.ILLink.Tasks` reference from 10.0.11 to 10.0.12. Exit 1 after 5.6 s. Preserved. |
| lock refresh | `src/BethesdaMultitool/packages.net10.0.lock.json`, `packages.lock.json` | Refreshed through the wrapper with `--force-evaluate` (portable profile, then full profile). Exactly three ILLink.Tasks entries changed, nothing else. The Shared submodule's five lock files were rewritten with different line endings only; they were restored from the pinned content so Shared stays clean. The three companion-app locks (AudioTranscriber, Map2DProfiler, RendererProfiler) still pin 10.0.11 and are outside this batch's graph. |
| 3 | `TestOutput/asset-caption-retry3-20260922` | Launcher misfire: the freeze aborted on the not-yet-restored Shared line endings and a shell pipeline masked that exit code, so the launcher ran and stopped at its own freeze check. No compile. Preserved as a misfire record. |
| 4 | `TestOutput/asset-caption-retry4-20260922` | Freeze verified (48 inputs including both refreshed locks, 15 helpers) with Shared clean; portable build launched 16:19:39 UTC and **succeeded**: exit 0 after 1,875 s (28:18 of MSBuild time under paging at 3.7 GB free), 0 errors, 103 warnings, all in the test project's inherited set (none in `AssetGalleryProjection.cs`, `AssetGalleryItem.cs` or the changed test file); no MSB3021/MSB3026/MSB3027 copy-lock line; `obj` and `bin` application assemblies byte-identical; the post-build freeze check verified 48 inputs and 15 helpers again. |

## Portable build, PDB and correspondence

Build: attempt 4 above. The compiled SDK is 10.0.401 (the attempt-1 log names 10.0.400). The one compiler
warning naming a changed file is the pre-existing CA1861 on the test file's constant array at line 21,
already covered by the queued analyzer-fixes candidate; the changed production files have none.
PDB verification: the portable application PDB matches 2,796 available source documents with 93 virtual
generated documents recorded separately; the test PDB matches 1,543 documents. Correspondence
(`portable-asset-caption-retry4-build-correspondence.json`): 48 frozen inputs verified, twelve
producer/consumer DLL/PDB files matched between the application and test outputs.

## Focused tests (19 selection cases plus two accessibility cases, Windows then WSL)

Windows (`asset-caption-retry4-tests.receipt.json`, junit `asset-caption-retry4-focused.junit.xml` and
`asset-caption-retry4-accessibility.junit.xml`): the focused filter `*AssetGalleryProjectionTests` plus
`*AssetTreeSelectionTests` ran 19 cases, 19 succeeded, 0 failed, 0 skipped (2.4 s); the two
`XamlAccessibilityRatchetTests` methods ran 2, 2 succeeded, 0 skipped. Four threads, skips rejected,
minimums 19 and 2, leases taken at 4.53 and 4.55 GB free under the bypass, payload hashes unchanged.
The runner was invoked with the identical argument list `bmt-test.ps1` builds, because that Shared
wrapper cannot pass the bypass. Runner: xUnit.net v3 3.2.2 on .NET 10.0.12.

WSL (Ubuntu, `/tmp` on ext4, the same Windows-built managed payload over `/mnt/c`, .NET 10.0.12 runtime):
the focused filter ran 19 cases, 19 succeeded, 0 failed, 0 skipped (2.7 s); the two accessibility methods
ran 2, 2 succeeded, 0 skipped. Receipts `asset-caption-retry4-linux-focused.receipt.json` and
`asset-caption-retry4-linux-accessibility.receipt.json` record exit 0, the assembly hashes unchanged
during execution, and leases at 6.05 and 4.01 GB free under the bypass. This is Linux execution of the
Windows-built portable assemblies, not a Linux source build or publish. Each JUnit report contains one
`testsuite` per test class; the totals above sum those suites.

## Windows publication, PDB audits and published-output audit

The normal-analyzer Windows publication (`run-windows-ui-publish.py`, full profile, locked restore, one
node, 12 GiB compiler heap cap, serial analysis, admission bypassed by the ruling) exited 0 after 1,315 s
with the same six warning lines as every earlier publication (three WIN2D0001, CA1068, S3877 and the
reviewed S3264) and no error; none names `MainWindow` or the asset gallery files. The Windows freeze equals
the portable freeze and was verified again after publication. Output:
`TestOutput/asset-caption-retry4-20260922/publish/windows-asset-caption-retry4/BethesdaMultitool.exe`
(121,522,690 bytes, SHA-256 `e6bec3ada1271a1f0314879c8bb20a81f833792ab0c424c62ca2960495488043`) beside
the 124-permutation shader pack.

Seven PDB audits passed: BethesdaMultitool 3,101 verified documents (142 virtual generated recorded
separately), Slfx77.Multitool.Core 237 (17), Media 77, WinUI 119 (32), WinUI.Direct3D12 82 (3),
SharpGLTF.Core 118, DDXConv 21. The publish correspondence receipt
(`windows-asset-caption-retry4-build-correspondence.json`) binds 48 frozen inputs, fourteen matching
producer/consumer DLL/PDB groups, the published executable hash and the post-publish deps, host and
runtime-pack manifest. The published-output audit passed with no issues over 56 physical files and the
bundle, runtime asset and scoped notice checks; the actual published `--no-gui --help` returned 0 in
4.19 s with the accepted 4,101-byte help text and the published bytes unchanged. The audit receipt
records the admission ruling and the 180 s help deadline deviation.

## GUI acceptance

Harness: `TestOutput/asset-caption-retry-observation-20260922` (not frozen). It contains the
startup and region observation (`launch-and-observe.ps1`), the UI Automation crash replay
(`replay-check-crash.ps1`, patterns only, no synthetic input, only the process it starts), the
hover-log analyzer (`analyze-hover-log.ps1`) and the read-only memory sampler. An adversarial
review of the harness fixed, before any run: acceptance after a mid-run harness error, an exit
code that ignored the logged-error rejection, unverified view-mode switches, an unmeasured
Toggle-raises-Click assumption, a log reader that could hide crash lines beyond 4 MiB, and an
analyzer that classified one-event ordering jitter as failure while never using its timestamps.
The analyzer now measures overlap duration (jitter threshold 100 ms), freezes a family's state once
its diagnostic budget is spent, and derives the expected physical gap from the logged scale.

### Startup and geometry (`startup-and-caption-regions.json`, PID 41980)

The fresh publication started (activation after 2 s, no error line), its bytes unchanged. Arranged shortcut
1234..1282 DIP against native content 0..1290 at scale 1.00: an 8-pixel gap, as expected. The native input
source exposes the Minimize rectangle: first `x=1296, width=46, height=32` (Standard caption), then
`x=1290, width=48, height=48` after the Tall transition, with `Native regions changed` notifications for each
kind; the Passthrough rectangle is `x=1234, width=48, height=48`, so the physical gap between them is 8.
The caption padding refresh logged `right=138.00->144.00, separator=8.00->8.00`.

### Hover log analysis (`hover-analysis-41980-pass1-*.json`)

The user hovered on that window between 12:36:26 and 12:36:55 local. Before the diagnostic budgets were
spent (help at 12:36:41, caption buttons and other native regions at 12:36:55) the log holds 192 pointer
events: 64 help, 56 Minimize, 52 Caption, 6 Maximize, 2 Close and 12 border events. Every Minimize-to-help
crossing reads `Minimize exited` at x=1287..1289, then `Caption entered` (the 8-pixel native strip), then
`Help entered`; every return reads `Help exited`, `Caption exited` at 1290, `Minimize entered` at 1290.
Overlap intervals: 0 failures, 0 jitter, none open at the end; no anomalies. The analyzer's own verdict is
"inconclusive: a diagnostic budget was spent and no failure was seen before that", which is the correct
reading of the log alone: the native strip received a pointer report on all 28 measured crossings and the
Minimize exit always preceded the help entry. Whether the rendered Minimize highlight cleared is not
observable in the log. The user then confirmed: "They're too far away to both be highlighted at once now.
That is fixed. The issues I was referring to are all cosmetic." The cosmetic points (glyph not centered,
glyph gray, the visible 8 px gap, the box 1 px taller) are handled by the route B batch recorded in
[the caption route B record](caption-route-b-20260922.md).

### Crash replay

First pair (`replay-control.json`, `replay-candidate.json`): both inconclusive with `element not found: asset
tree`, a harness locator fault: the TreeView is exposed as a Group named "Asset tree" with automation id
`AssetTreeView` and an unnamed inner `Tree`. A read-only diagnostic instance (`diagnose-asset-source.ps1`)
confirmed the launch argument opens the folder ("Assets - 257 file(s)", "1 of 1 previewable, 1 other",
gallery check box `AppIcon.png`). The locator was changed to the automation id and the pair rerun with the
run label `r2`; the first pair is preserved.

Pair `r2` reached the tree, gallery and initial states but the control's `Toggle` threw "Operation is not
valid due to the current state of the object" and the candidate run stopped early on a harness fault (a
status line captured into a boolean). Pair `r3` added post-failure state reads and the fix for that fault:
the control's toggle had reached the application (tree leaf and gallery both read On, root Indeterminate),
so under automation the old build's nested-selection exception is returned through the automation call
instead of terminating the process; the candidate then completed nine actions before a locator fault on the
search box. Pair `r4` (`replay-control-r4.json`, `replay-candidate-r4.json`, `replay-verdict-r4.json`):
**accept**. Control: the tree toggle reached the crash path (states On/On/Indeterminate, exception returned,
no dump written, the 63888 dump unchanged). Candidate (the fourth attempt's publication): all twelve
actions completed with the expected tree, gallery and root states: leaf toggle On, second toggle Off,
Select all, Clear selection, gallery toggle, List view (verified On), leaf toggle in List view, Thumbnail
view (verified On), a search that hides the row, a toggle while hidden, and the cleared search restoring
the gallery check; no crash line, no error line, clean close with exit 0. Patterns used: SelectionItem,
ExpandCollapse, Toggle, Invoke, Value and a TreeWalker fallback; no synthetic input; only the launched
process was touched.
