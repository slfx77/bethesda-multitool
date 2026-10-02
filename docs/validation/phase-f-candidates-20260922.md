# Phase F: the four reviewed candidates, rebased and built, September 22, 2026

Status: in progress. Base: BMT `8e324dc52c4c89c1d4dd4d1f240f5585669c4866` (the route B caption overlay accepted by
the user), Shared `20e4e2c0fb2d304ed60c05e48d8c97a9a730b684`, DDXConv `805ef26b`. The four candidates were written on
2026-09-21 against `e38d0e30` and never compiled; this record covers their rebase onto the current HEAD, the
independent verification of each rebase, the recipes derived for the two builds, and the builds themselves.

The owner's admission ruling of 2026-09-22 ("Bypass the coordinator for now. This is the only GUI tool being worked on
at the moment.") is applied unchanged: every lease is taken with `-MinFreeGB 0` and every receipt states it; mutexes,
isolated compilation, the GC environment, the 12 GiB compiler heap cap, one MSBuild node, locked restore, the serial
analyzer response file and normal analyzers are retained.

## Rebase (workflow `wf_db8118b8-9c8`, eleven agents, no tree writes)

One agent per candidate regenerated or merged it under `TestOutput/<candidate>-rebase-20260922`, then one or two
adversarial verifiers per candidate tried to refute the rebase against HEAD, and one agent derived the recipes.

| Candidate | Rebase route | Files | Verified | Findings that changed the plan |
|---|---|---|---|---|
| `ui-adoption-analyzer-fixes-20260921` | `prepare.py` re-run against HEAD (its stale `source-before.json` set aside) | 9 changed | two lenses, survives | `AssetGalleryProjectionTests.cs` regenerated from HEAD keeps the three `CheckFeedback_` cases; after-texts preserve each file's line-ending census (one file is mixed 114 CRLF / 19 LF, one all CRLF) so they are written byte for byte |
| `psp-resource-bounds-20260921` | `prepare.py` re-run (tree-sitter) | 1 changed, 1 new (20 cases) | one lens, survives | the reader has no production caller at HEAD; the correction is exercised through the RenderWare/Travels test consumers; the original `review.json` keeps the peer-review block |
| `gallery-retirement-window-20260921` | three-way merge of the stale bases onto HEAD (`MainWindow.xaml.cs` after route B, `ExploreTab.xaml.cs`, `Resources.resw` add/add at the same anchor) | 4 changed | two lenses, survives | `AssetBrowserTab.Gallery.cs` is shared with the analyzer fixes, so its retirement property is applied as a hunk on the analyzer-fixed text, never as a whole file; follow-ups recorded below |
| `flc-native-consumer-20260921` | `prepare.py` re-run | 2 changed, 5 new (15 cases) | one lens, **refuted**: CS1503 | the candidate's `TryOpenBytes(ReadOnlySpan<byte>, string)` passes the span to `DaggerfallVidFile.Parse(byte[], string)`, the only overload at HEAD; the owner fix widens the helper to `byte[]` (both callers already hold one) and is applied at batch 2 time, recorded as an owner correction, not as the reviewed candidate |

Recipes: `TestOutput/phase-f-analyzer-fixes-20260922` (batch 1) and `TestOutput/phase-f-stage-a-20260922` (batch 2),
derived from the fourth caption attempt's helpers plus the stage-a recipe and the FLC native probe, with the exact
counts re-measured at HEAD: the carried 40-class filter yields **427** cases (the 424 of 2026-09-21 plus the three
`CheckFeedback_` cases of `a373d931`), so batch 2 expects **474** (427 + 15 `FlicPreviewPreparationTests` + 20
`OblivionPspResourceBoundsTests` + 12 existing `OblivionPspResourceReaderTests`). Both runners keep the instructed
minimums (424 / 471) and fail on any count other than 427 / 474. Both freezes bind HEAD `8e324dc5`, so batch 1 is not
committed before batch 2 runs; the commits follow batch 2.

## Batch 1: analyzer fixes (nine files)

Applied byte for byte from the rebase manifest after checking every file's HEAD hash. Freeze: 56 source inputs, 15
helpers, delta exactly the nine files. Portable build (`net10.0`, tests-only, normal analyzers): exit 0 in 731 s, no
copy-lock line, `Verified 56 source inputs and 15 helpers`. Warning delta against the accepted fourth attempt's
portable build, per log line: CA1068 6 -> 0, S3877 2 -> 0, S5034 6 -> 0 (the fixes), CA1861 134 -> 132 (the constant
array in `AssetGalleryProjectionTests`); no CA1001 or S2931 appeared on `ThumbnailCacheObservation` after it dropped
`IDisposable`. The remaining lines (CA1859, CA1861, CA5350, RCS1139, S108, S1854) are inherited test-project warnings.
PDB audits and the correspondence receipt bound 56 frozen inputs with exit 0.

Focused tests: 427 passed, 0 failed, 0 skipped on Windows (`--max-threads 1`, `RUN_BUCKET_B=1`, fail on skips) and
the same 427 under WSL on ext4 with the seven managed binaries unchanged; accessibility 2 and 2. Windows publish:
exit 0 in 537 s, no copy-lock line; warning lines S3264 x1 and WIN2D0001 x3, the CA1068 and S3877 lines of the
inherited six gone. Seven PDB audits (same document counts as the caption attempts), correspondence bound to
56 frozen inputs, published-output audit passed with the actual `--no-gui --help` returning 0 in 2.23 s and
unchanged bytes; executable `publish/windows-phase-f-analyzer-fixes/BethesdaMultitool.exe`, SHA-256
`f73c2a32b8210d7e2d2556fb20ecbda6dd46100a912d00b273661cd0c09e0a61`.

## Batch 2: PSP bounds + gallery retirement + FLC Stage A (thirteen stage files on top of batch 1)

Prerequisite: the profiler lock (`src/BethesdaRendererProfiler/packages.lock.json`) refreshed through the coordinator
wrapper with `--force-evaluate`; exactly the three `Microsoft.NET.ILLink.Tasks` lines changed (10.0.11 -> 10.0.12),
and the Shared submodule's five lock files, rewritten with different line endings by the re-evaluation, were restored
byte for byte. Applied on top of batch 1 with every whole file checked against its recorded HEAD hash: the PSP
reader and its new test file, the three gallery-retirement files, the retirement property merged as a hunk into the
analyzer-fixed `AssetBrowserTab.Gallery.cs`, the seven FLC files, then the owner fix in `ClassicVideoClip.cs`
(`TryOpenBytes(byte[] bytes, string name)`, `bytes.Length == 0`). Two recipe corrections before the freeze, both
recorded here because the freeze hashes its helpers: the freeze helper compared the profiler lock's ILLink versions
including its RID-specific dependency group, which carries no ILLink entry (`None`), against the application lock's
filtered set, so the parity assertion could never pass and its message crashed on sorting `None`; the comparison now
filters both sides the same way. The driver script gained a skip for an already-applied tree.

Freeze: 67 source inputs, 22 helpers; preflight confirmed batch 1's 427+2 on both runtimes and its published audit.
Portable build: exit 0 in 661 s, no copy-lock line, `Verified 67 source inputs and 22 helpers`. Warning census
against batch 1: unchanged except four new lines, all in the new `FlicPreviewPreparationTests.cs` (S5034 x2 at
lines 177 and 197, a `DisposeAsync().AsTask()` consumed twice on purpose to prove idempotent retirement; S3358 at
358, a nested ternary in the test double's `EnumerateFiles`; S3877 at 370, the test double's `Dispose` throwing an
injected failure, which is the point of that double). They are test-only style findings of the same families the
analyzer-fixes batch cleared elsewhere; they ride with the next batch that builds the test project rather than a
dedicated build (see follow-ups).

Focused tests: 474 passed, 0 failed, 0 skipped on Windows and the same 474 under WSL on ext4; accessibility 2 and
2. Windows publish (the first compilation of `AssetFlicPreview.cs`, the merged retirement gate in
`AssetBrowserTab.Gallery.cs`, `MainWindow.xaml.cs` after its three-way merge, `ExploreTab.xaml.cs` and the resw
entry): exit 0 in 505 s, no copy-lock line, warning lines exactly batch 1's (S3264 x1, WIN2D0001 x3), no new
App-side diagnostic.

Publish audits passed (seven PDB receipts, correspondence, published audit with `--no-gui --help` in 2.04 s). Profiler
build (`-Full -Isolated`, normal analyzers): exit 0 in 162 s, eight PDB receipts, correspondence with the profiler
executable `c621f753…`.

**Native FLC probe: not accepted.** The recipe's launcher (`run-native-probe.ps1`, inherited from the candidate)
timed out at 130 s with an empty report (`native-flc/`). Two manual runs behaved the same, so the hidden window
style the launcher uses was not the cause. A heap dump of a hung instance (`dotnet-dump collect --type Heap`,
then `dumpheap -type Exception`, `pe`, and `dumpvc` on the async state machines) showed: the probe had played
MAGE.CEL through the finite and replay checks, its `WaitAsync("loop end approach")` had timed out, and on cleanup
`StopObserving` had called `MediaPlayer.PlaybackSession` on the player the consumer had already retired
(`COMException` E_ABORT), so `RetireAsync` threw, the shared `WindowLifetime` deferred the close ("Window closure
remains deferred while native resources require the mounted window.") and the report writer in `OnClosed` never
ran. The probe was hardened (commit pending with the FLC work): the observer detaches before cancellation and
tolerates a retired player, a deferred close still writes the report and ends the process, and every wait samples
the native clock into a `trace` field. Rebuilt with the compiler server and analyzers skipped (the user's ruling of
2026-09-22) in 184 s and rerun (`TestOutput/phase-f-flc-probe-20260922/p1`): 14.9 s, exit 1, report written.

The report isolates a real playback defect, not a probe timing assumption: with looping enabled after the first
end of stream, the native clock wraps every 700 to 750 ms instead of at the clip's 1,065 ms (positions 112 ->
363 -> 74 -> 318 -> 559 -> 34 -> 300 -> 543 -> 222 …, nineteen native seek completions in twelve seconds), which
is about ten of the fifteen frames per loop. The first, non-looping playthrough ended correctly at 1,065 ms.
A nine-agent investigation (three angles, six adversarial verifications) converged on one surviving cause: the shared
bridge answers the native sample request that follows the last frame with end-of-stream immediately
(`DecodedMediaStreamSource.PublishEnd`, ungated), while Media Foundation has already pulled four to six samples
ahead of its presentation clock and ends or loops the presentation on the source's end without presenting the
samples it still holds. It is not loop-specific: the finite run's `MediaEnded` also arrived at a clock of about
760 to 777 ms and the reported 1,065 ms was the end snap. Refuted along the way: the FLC decoder (returns null
only after frame 14; seek to 0 resets exactly), the buffer budget (`Processed` fires at ingestion, so the 2-sample
budget is a memory bound, not a presentation bound), the missing frame-rate hint, the zero `BufferTime`, and a
sink-side marker theory (indistinguishable from the engine-side one from outside Windows).

Fix (Shared branch `bethesda/decoded-eos-presentation-v1` from the pin `20e4e2c`, in this worktree's submodule
checkout): the bridge records the end of the last sample it published per track, the owning `NativeMediaSession`
lends it the player's `MediaPlaybackSession` (attached after the seek coordinator, detached on retirement), and
the request that would carry end-of-stream is held until the presentation clock has passed that end, bounded by a
pure `NativeEndOfStreamGate` (remaining presentation plus 250 ms of slack, at most 5 s of running clock; a paused
clock holds until the request is retired by seek, stop or shutdown). The gate is linked into the Core tests like
the buffer budget and pinned by six cases.

**Resource-starvation crash (2026-09-22, about 17:45 local).** The verification cycle `p2` started the profiler build
through the wrapper with the admission gate bypassed (the owner's afternoon ruling, applied to every build that day),
the compiler server and two MSBuild nodes, at 1.64 GB free; the machine ran out of memory and crashed. The owner
reported it after the reboot. Both repositories pass `git fsck` and every edited file is intact; `p2` holds only
the partial build log. From that point this session runs every build, test and publish through the wrapper's
default 4 GiB gate, on one MSBuild node, one at a time, and never lets a sub-agent build; the bypass ruling is
treated as withdrawn. The cycle was rerun as `p3` under those settings.

**`p3` (profiler build 275 s, gated at 9.07 GB free, one node; probe 7.2 s, exit 0): all thirteen checks passed**
(`native-metadata-MAGE.CEL`, `initial-paused-clock`, `consumer-default-loops`, `finite-input-ended`,
`loop-restarts-and-advances`, `half-frame-seek`, `overlap-pause-cancels-resume`, `pane-return-does-not-resume`,
`native-metadata-KING.FLC`, `source-replacement-retired-first`, `blocked-source-retained`,
`canceled-source-never-adopts`, `clear-has-no-current-player`), released, window closed, seven original reads of
6,293,084 bytes as the recipe requires, every source disposed exactly once. The clock trace shows the finite run
ending at 1,071 ms (the true end plus one poll) instead of about 770 ms, and the loop wrapping from 1,022 ms to
13 ms, the full 1,065 ms period, with seven native seek completions instead of nineteen. The bridge's own trace,
captured by the probe's new listener, reads `DecodedEnd published track=video lastEnd=10650000 clock=10709814
heldRunning=301 polls=19`: the end was held for the 300 ms the pipeline still had queued and released once the
clock passed the last sample. This closes the native probe gate for FLC Stage A. The gate's nine cases plus the
four buffer-budget cases pass (13 of 13, `Slfx77.Multitool.Core.Tests` built through the gated wrapper on one
node; the wrapper's `--` separator did not reach the runner, so the executable was run directly). Shared commit
`71e738cf4aed6eb543f2e421897ddeddbf9c1589` on `bethesda/decoded-eos-presentation-v1`; BMT's submodule pin moves to it.

## Integer (pixel-art) video scaling and Stage B (in progress)

User request of 2026-09-22 evening: disable resampling for pixel-art movies with an integer-scaling toggle like the
core image viewer's, in the core ("the scaling piece ... needs to become part of the core"), and let the user judge
several Arena videos through the native path. Design: two readers and a judge (`wf_55eb9af9-abc`) settled on a
28-step plan: pure Core helpers (`NearestNeighborUpscaler`, `VideoPresentationFit`, `VideoPresentationPlanner`),
a bridge pre-upscale by a presentation scale fixed at construction, `NativeMediaSession.PresentDecodedAsync`
re-presenting the retained decoded session at a new scale on resize, `NativeMediaPreview.FilteringEnabled` with
a code-built toolbar checkbox mirroring `ImagePreviewControl`, `NativeVideoLayout` presenter sizing in device
pixels, and in BMT `ClassicVideoRouting` sending `.flc`/`.cel` to `AssetFlicPreview` hosted in a
`NativeMediaPreview` in the asset browser (other classic movies keep the timer path), with the probe extended.

Shared Core helpers: implemented on the branch (seven files: three helpers, three test classes, the
`Playback.FilteringHelp` resource line), all parse-clean; the Core test project built through the gated wrapper on
one node in 15 s and **37 of 37** cases pass (24 new: 6 upscaler, 9 fit, 8 planner and one defaults case; plus the
9 gate and 4 budget regressions). Implementer deviations, all additive: `Int128` clamp arithmetic, explicit
rasterization-scale validation on every planner path, spelled-out parameter names, two extra tests; one open item
is that `VideoPresentationPlanner.cs` holds three types where the repository keeps one per file.

Shared Playback: implemented (seven Playback files rewritten or edited plus `NativeDecodedPresentation.cs`); the
shared WinUI project compiles through the gated wrapper (21.8 s, 0 errors, one new CA1068 on the reordered private
helper). Adversarial review: no compile break (every WinRT member checked against the restored XML docs), two
behavioural blockers sent back for repair: the Filtering toolbar showed on every non-audio host even where nothing
scales (now gated by a `ShowFilteringToggle` property the pixel-scaling hosts set), and play intent was lost when the
first measured layout re-presented an autoplaying movie still in the Opening state (the coordinator now tracks a
requested play). Also sent back: the `SeekKind.None` early return, the `MediaOpened` race in the position restore,
replaying notifications dropped while the pane was inactive, the audio-track budget carve-out, the CA1068 and the
dead `PreparedMediaInput.PresentationScale`. Follow-ups recorded: per-frame allocation growing with k squared (an
array pool through the Processed hook), fixed-row hosts, consumer exceptions from `PlayerReplaced`, and the
pixel-perfect claim itself, which only a capture at 100, 125 and 150 percent can settle.

Review fixes applied to Playback (all nine items, plus a correction of the implementer's own line-ending mistake back
to LF). BMT Stage B implemented: `ClassicVideoRoute`/`ClassicVideoRouting` (Native for Arena or unclassified
`.flc`/`.cel` leaves, Legacy for every other classic movie, None otherwise) with ten routing cases; the browser hosts
a `NativeMediaPreview` named `NativeVideoPreview` (automation id `Assets.Video.Playback`) beside the legacy image
surface; `ShowVideo` routes through it with the legacy body kept for the other formats; `AssetFlicPreview` adopts
`PresentDecodedAsync` (loop and scaling owned by Shared), publishes the decoded format and duration, forwards
`PlayerReplaced`, and turns on the Filtering row; the window-close gate also waits for the FLC preview; the probe
gains five checks (pixel-perfect fit, re-presentation on the Filtering toggle both ways, on a window resize with
position kept, and no second read), schema v2 with a `playerReplacements` count. Portable build through the gated
wrapper with the compiler server and analyzers skipped: **100 s** (against 661 to 731 s before), 0 errors; focused
run **85 of 85** (10 routing, 16 preparation, 28 decoder, 11 gallery, 20 PSP bounds), no skips.

Windows compile of the whole stack (profiler build `p4` through the gated wrapper at 8.11 GB free, one node, compiler
server, analyzers skipped): 211 s, 0 errors. Extended probe (`TestOutput/phase-f-flc-probe-20260922/p4`, 10.6 s,
exit 0): **18 of 18 checks passed**, schema v2, six player replacements, seven original reads of 6,293,084 bytes,
every source disposed once. Measured: MAGE.CEL presents at whole scale 3 in the 720x540 probe window and at 7 after
the window is resized to 1440x1080, the Filtering toggle re-presents at scale 1 and back at 3, each re-presentation
settles in about 230 ms and keeps the 284 ms position paused, the resize re-presentation reads no original bytes
again, and KING.FLC replaces it at scale 1 (its 640x400 double does not fit the probe window's video row).

BMT review: one blocker (the post-adoption `Player is null` guard raced a re-presentation begun inside the
adoption; replaced by an adoption counter captured before `PresentDecodedAsync`) and four follow-ups (stale
selection guard by version, failure UI restored when neither decoder accepts, the retired-player
`ObjectDisposedException` only swallowed after the preview's own disposal, the probe settling the player after the
pane return) applied. Rebuilt through the gated wrapper (`p5`, 8.91 GB free, one node, compiler server, analyzers
skipped): 176 s, 0 errors, no locked-copy warnings; probe **18 of 18** again in 10.8 s (six player replacements,
seven reads of 6,293,084 bytes). The Windows compile re-evaluated the two companion lock files
(`BethesdaAudioTranscriber`, `BethesdaMap2DProfiler`) to ILLink.Tasks 10.0.12 and the Shared WinUI project's
ImageSharp edge, the same refresh the profiler and main locks already carry; both are committed with this batch.

GUI check (`TestOutput/phase-f-flc-gui-20260922/gui6`, UI Automation patterns only on the harness-launched process,
`--asset-source` = the Arena Steam data root, `BethesdaMultitool.dll` SHA-256 `a3b5f220…`): MAGE.CEL, KING.FLC and
INTRO.FLC each selected in the asset tree, the native host (`Assets.Video.Playback`) shown on screen with the
Filtering row and the stock transport, playback started, Filtering toggled on and back off, nine PrintWindow
captures at 1450 x 900, no `[CRASH]`/`[ERR]` line, exit 0. Measured on the captures: with Filtering off MAGE.CEL
(110 x 119) presents as a 4x box and the two 320 x 200 movies as 2x boxes (640 x 400; 3x would not fit the
555-pixel video row), crisp pixel edges; with Filtering on the element fills the row and the video is a uniform,
resampled fit (MAGE.CEL content 519 x 555, aspect 0.935 against the stored 0.924, so no non-uniform stretch).
Three earlier launches (`gui3`..`gui5`) found no movie row: the toolbar's search box filters the gallery, not the
tree, so the run pages the virtualized tree with the Scroll pattern instead. The user then ruled the GUI
automation too slow and focus-stealing; no further launches this session, and an asset TYPE filter (video,
sprite, texture, model, audio) is recorded as the framework requirement that would make such a check trivial.

Publish for the user's review: not run; the review build is the rebuilt
`src/BethesdaMultitool/bin/Release/net10.0-windows10.0.19041.0/win-x64/BethesdaMultitool.exe` from `p5`.

## User feedback batch after the review build (2026-09-22 late)

The user reviewed the Stage B build and reported four things. (1) "The ? glyph is still small": the caption help
overlay's `FontIcon` (Segoe Fluent Icons U+E897) goes from size 10, which inked 6 x 9 px, to size 14. (2) "Is Arena a
flat structure mainly? Would opening a different game present the user with a filetree browser?": yes to both. The tree
is synthesized from the virtual paths of the merged source (`AssetTreeBuilder`), so any source whose paths carry
directories (New Vegas `Data` with its BSA-internal meshes/textures/sound folders, Daggerfall's ARENA2) shows a
folder hierarchy; Arena's install is flat and its GLOBAL.BSA stores no directories, so the 2,807 entries collapse into
one folder. (3) "Why are Arena's sprites listed as 'thumbnail'?": the gallery caption was
`{bytes}; thumbnail {w} x {h} pixels` and quoted the realized thumbnail bitmap's size, not the asset's. Now the
thumbnail worker publishes `AssetImageInfo` (first-frame stored size + frame count) on the node before its artwork
crosses to the dispatcher, and the caption reads `Sprite; 64 x 64 pixels; 6 frames; 1,234 bytes` (frames only when
more than one; `Texture; ...` for textures; kind plus bytes before decode). The DDS persistent path describes the
texture from its header (height +12, width +16) so a cache hit needs no decode; DDX stays undescribed. Three new Core
cases (`AssetThumbnailSourceTests`: a three-frame 6 x 4 FRM thumbnails into a 2-pixel cell yet is described 6 x 4 x 3;
a 4 x 4 DDS is 4 x 4 x 1; a non-picture node describes nothing). Portable build through the gated wrapper 65 s,
0 errors; focused run **148 of 148** (AssetThumbnailSource, DdsThumbnailProducer, AssetGalleryProjection, AssetNode,
AssetTreeBuilder, SpriteDecodeBytes, ThumbnailScaler, ClassicVideoRoute), no skips. The App side (caption, loader,
glyph) compiled in a gated Windows build of the main project (`TestOutput/phase-f-caption-fix-20260922/windows-build.log`,
8.52 GB free, one node, compiler server, analyzers skipped): 158 s, 0 errors, 3 warnings none in the changed files; the
review binary is the same bin path (`BethesdaMultitool.dll` SHA-256 `73ed7999…`). No GUI launch was made, per the
user's ruling. (4) A requirement, in the
user's words: "when an asset is clicked in the browser, it opens a full view of it in a preview pane on the right. The
pane can be collapsed, which is generally a requirement for left and right panes / panels in the 3-column layouts."
Recorded in the plan and relayed to the foundation through the mailbox together with the asset type filter; the two
form the next batch (tree | gallery | preview, collapsible panes, type filter), designed before implementation.

Mailbox incident: the first relay passed only the Bethesda section to `inter_session.py publish`, which writes its
input as the whole document, so the foundation's sections were missing for about 25 minutes; the document was
rebuilt from the tracked HEAD plus the foundation's uncommitted bullet and republished (all four headings present).

## Browser layout batch: three columns, preview pane, asset type filter (2026-09-22 late evening)

Requirements (user, verbatim in the plan file): filtering assets by type, and "when an asset is clicked in the browser,
it opens a full view of it in a preview pane on the right. The pane can be collapsed, which is generally a requirement
for left and right panes / panels in the 3-column layouts." Design: one planning agent over the current code and the
Shared controls, revised after the foundation's 22:02 mailbox update (reuse Shared `CollapsiblePanel` +
`PanelCollapseController`; the type-filter control is a foundation extraction, so BMT builds only the consumer side
behind a marked interim checkbox block). Coordinator decisions D1-D10 in
`scratchpad/browser-layout-plan.md` (copied into this record's evidence directory): the gallery lists every leaf kind
with kind glyph tiles; Select all stays source-wide; the name search stays gallery-only; a gallery click mirrors the
tree silently; pane widths and collapsed flags persist through the workflow store; the audio waveform stays off.

Implementation: two agents in parallel (Core, App), no agent built anything. Core: `AssetKindFilter` (immutable
excluded-kind set with `WithIncluded(present, included)`), `AssetKindCount`, `AssetKindCensus`,
`AssetTreeVisibility`, `AssetPreviewSurface` + `AssetPreviewRouting` (byte-free, content probe last),
`AssetImageFrame`/`AssetImagePreview`/`AssetImageDecoding`/`AssetImagePreviewSource` (full-frame decode through the
same two families as thumbnails, 16,777,216-pixel cap), `Core/Ui/AssetPaneLayout` (parse/clamp/persist), the
projection's kinds filter and every-leaf scope, two shortcut rows. App: `AssetColumns` five-column grid (tree 280/180,
gap, gallery */240, gap, preview 420/320) with `TreePanel`/`PreviewPanel` Shared collapsible panels
(`Assets.TreePanel.Collapse/.Expand`, `Assets.PreviewPanel.Collapse/.Expand`), `PanelWidthClamp`, Ctrl+Shift+T/P,
`AssetBrowserTab.Layout.cs`/`.Preview.cs`/`.TypeFilter.cs`, `AssetKindFilterOption`, the preview surfaces moved into
the pane with their names, events, ids and lifetime rules intact, `ImagePreviewControl` for the full image view (whole
scale by default, the control's own Filtering toggle), kind glyph tiles, 34 new resource keys.

Verification. Portable build through the gated wrapper: 177 s, 0 errors. Focused run over 18 classes
(`TestOutput/phase-f-browser-layout-20260922/browser-layout-focused-1.junit.xml`): **254 of 255 passed**, the one
skip being the accessibility ratchet's opt-in gap dump; the ratchet itself and `LocalizedTextValueTests` pass on the
new XAML and strings. Adversarial review (read-only agent over the whole diff against the actual Shared sources):
no compile error found; one blocker, that collapsing the preview pane left the two Direct3D 12 surfaces rendering
because neither the mesh viewer nor the Shadowkey presenter observes ancestor visibility (the implementer had dropped
the planned calls on that assumption) - fixed by pausing both from `PreviewPanel_CollapsedChanged` and gating the
Shadowkey presenter on the collapsed flag; also applied: clamp-forced widths are no longer persisted (only a resize
that leaves the gallery above its minimum is a drag), the tree status claims a filter only when it hides something,
and a failed sky read shows the placeholder. The reviewer's claim that the new files were LF-only was refuted by a
byte count (every new file is CRLF, no BOM). Windows builds through the gated wrapper: 298 s, then 260 s with the
review fixes, then 218 s with the expansion fix; 0 errors each, 3 warnings (unchanged count).

The single announced GUI launch (`TestOutput/phase-f-browser-layout-20260922/layout1`, DLL `beb18be1...`, before the
expansion fix; UI Automation patterns only, exit 0, no `[CRASH]`/`[ERR]` line): the captures show the three columns
with collapse buttons on both side panels; MAGE.CEL plays in the preview pane through the native host; LOGBOOK.IMG
opens as a full 320 x 200 view with "Zoom to fit", the Filtering toggle and a checkerboard; gallery captions read
`Sprite; 89 x 58 pixels; ...`; collapsing the preview pane leaves a 36-pixel strip and the gallery widens to seven
columns; with Video unchecked the tree status reads "2,787 of 2,807 file(s) match the type filter" and the gallery
"2,611 of 2,631 file(s) shown; 20 excluded by the type filter". Four of the eight scripted checks failed by locator,
not by behaviour: `ImagePreviewControl` exposes no automation peer of its own (find `Image.ZoomMode` next time), and
the filter-restore check found no rows because the root had collapsed. That last one was a real defect: clearing and
re-adding a node's children collapses it in WinUI, so `ApplyTreeFilter` now restores each folder's expansion; the fix
is compiled (DLL `d1716e56...`) but not launched again, per the one-launch rule.

Review build for the user: `src/BethesdaMultitool/bin/Release/net10.0-windows10.0.19041.0/win-x64/BethesdaMultitool.exe`
(not published). Follow-ups: the standalone (TabView) host's 16-pixel padding is not subtracted by the clamp; Audio
leaves row 0 blank; the layout store writes four keys per save; a gallery click mirrors a tree row whose ancestors
may be collapsed; replace the interim type-filter block with Shared `BrowserTypeFilterControl` when the pin moves to
`9d32ace`; propose an automation peer for `ImagePreviewControl` and an active-filter indicator on the Filters button.

## Shared pin 9d32ace and the shared asset-type filter (2026-09-22/23 night)

The foundation merged BMT's scaling commit `6f35ffc` into Shared production as `8be814f`, fixed the follow-ups BMT's
review had recorded (retirement before decoder release, isolated `PlayerReplaced` observer failures, no resize
restoration after a pane departure, the two presentation types in matching files), added
`BrowserTypeFilterControl` over Core `BrowserTypeFilterOption`/`BrowserTypeFilterSelection` (`7668c82`), and
published `9d32ace` as available for deliberate adoption (its native scaling checks 8 of 8). BMT's submodule branch
`bethesda/decoded-eos-presentation-v1` was fast-forwarded to `9d32ace` (it contains `6f35ffc`), so the pin moves
without a merge. API check before building: `PlayerReplaced` is still an `EventHandler`; the refactored thumbnail
disk cache keeps its members as forwards; `VideoPresentationOptions`/`VideoPresentationPlan` moved into their own
files unchanged; the toolbar gained an optional `Actions` slot.

The interim BMT checkbox block is gone. `AssetKindLabels` (Core, four tests) maps kinds to Shared options (identity
= enum member name; Shared plural `Browser.AssetType.*` labels, `AssetTypeFilter_Saves` as the one application
fallback, Raw = "Other"); the tab hosts `BrowserTypeFilterControl` (`Assets.Types`, rows `Assets.Types.Type.<Kind>`)
over a retained `BrowserTypeFilterSelection` and derives `AssetKindFilter` from it on every change. Semantics now
follow the Shared selection: a kind the next source also contains keeps its decision, a kind new to it arrives
checked (pinned through the real selection in `AssetKindLabelsTests`). Six interim resource keys and
`AssetKindFilterOption.cs` were removed.

Verification: portable build 75 s, 0 errors, no lock-file change; focused run over 22 classes
(`TestOutput/phase-f-pin-9d32ace-20260923/pin-9d32ace-focused-1.junit.xml`) **293 of 294**, the skip being the
accessibility ratchet's opt-in gap dump. Profiler build 229 s and the FLC native probe `p6`
(`TestOutput/phase-f-flc-probe-20260922/p6`): **18 of 18** on the foundation's reworked `NativeMediaSession`, six
player replacements, seven reads of 6,293,084 bytes. Windows App build 131 s, 0 errors, 3 warnings (unchanged count), no locked-copy warning; review binary DLL SHA-256 `fcb65990...`. No GUI launch was made for this step.

Kept as local workarounds until the foundation's announced repairs land: BMT pauses the mesh viewer and the Shadowkey
presenter itself when the preview pane collapses (foundation will make `NativeViewport` observe ancestor collapse
through `PresentationActivity`), and scripts locate the image viewer through its inner `Image.ZoomMode` (foundation
will add a group automation peer). Follow-up: move the thumbnail-cache button into the toolbar's new `Actions` slot
through the Shared `ThumbnailCacheControl`, as AWE did.

## Shared ef4f8d6: viewport ancestor visibility and image automation identity (2026-09-23)

The foundation fixed both gaps BMT reported from the browser layout batch in one commit on top of `9d32ace`:
`NativeViewport` now suspends through `PresentationActivity` when an ancestor collapses (expansion redraws without
restarting animation; an explicit inactive request survives expansion), and `ImagePreviewControl` exposes its outer
automation identity through a group peer. The submodule fast-forwarded to `ef4f8d6` (no merge). Its only surface
change BMT reads is `NativeViewport.IsPresentationActive`, now also false while an ancestor is hidden; BMT reads it
only to gate capture, which already requires effective visibility. BMT's explicit pause of the mesh viewer and the
Shadowkey presenter on preview collapse stays for now: it is compatible (collapse requests inactive, expand requests
active) and retiring it needs a consumer check in the running GUI. Gated App build 221 s, 0 errors; its 15 distinct
warning sites are all in Shared or the third-party SharpGLTF copy, none in BMT sources.

Record correction: `build.ps1` writes MSBuild's output to the console, so the `*> <name>.log` files named in this
record for the caption, layout and pin builds hold only a stub. The real build output of each is saved beside it as
`<name>.console.log` (copied from the session's background-task output).

## Follow-ups recorded by the verifiers (not in these batches)

- Pin the new `WindowResources_CloseBlocked.Text` key in `LocalizedTextValueTests` beside the existing
  `ShadowkeyNative_CloseBlocked.Text` pin, which no source references any more after the gate change.
- `ExploreTab.NativeResourcesRetired` is unreferenced after the gate change; delete it or update the
  `EnableShadowkeyPreview` doc comment.
- On a blocked close, `DisposeWindowOwnersAsync` now throws before the caption input handlers are detached; the
  retained window keeps them attached, which is harmless while it lives, but a later pass could detach first.
- `ThumbnailCacheObservation` keeps `_disposed` and `ObjectDisposedException` after losing `IDisposable`; a rename to
  match `CompleteBrowserRetirement` is cosmetic.
- Clear the four analyzer lines in `FlicPreviewPreparationTests.cs` (hold each `DisposeAsync()` in a local before
  `AsTask()`, extract the nested ternary, suppress S3877 on the test double's throwing `Dispose` with a
  justification) in the next batch that compiles the test project.
- `FlicPreviewPreparation` and `AssetFlicPreview` create linked cancellation sources from a snapshot token that can
  already be disposed (`ObjectDisposedException` rather than `OperationCanceledException` on that race); the probe
  window's `async void OnLoaded` awaits `CloseAsync` unguarded.
