# BMT goal and UI continuation — September 22, 2026

Resume the existing full BMT program. Finish the immediate UI/crash correction
batch first, then continue the remaining program; this handoff does not replace
the goal or reduce its acceptance gates. No build or GUI launch was started while
preparing this handoff.

## Start here and preserve ownership

1. Read the **current canonical**
   `C:/dev/Multitool/Multitool.Shared/INTER-SESSION.md`.
2. Read `C:/dev/Multitool/Multitool.Shared/docs/completion-program-2026-09-12.md`,
   its linked requirement/workflow ledgers and the original
   `docs/bethesda-session-handoff.md`. The latter's checkout table is historical;
   use the current state below and the live mailbox, not its older commit/pin.
3. Read this checkout's `docs/validation/asset-caption-regression-20260921.md`,
   `caption-hitboxes-20260921.md`, `ui-feedback-20260921.md` and
   `gallery-media-20260921.md`.
4. Register only the new **root** session and start notifications:

```powershell
python C:/dev/Multitool/Multitool.Shared/tools/scripts/inter_session.py register --role bethesda
python C:/dev/Multitool/Multitool.Shared/tools/scripts/inter_session.py start
Set-Location C:/dev/Multitool-worktrees/bmt-material-preparation-20260912
git status --short
git submodule status
```

This session owns BMT. The other session owns Shared/AWE. Coordinate Shared
contract/source changes before editing, and adopt exact reviewed commits only
with focused BMT validation. Current foundation commits must be read from the
mailbox; they do not authorize moving BMT's pin.

The user authorized this parallel BMT continuation. Preserve the dirty primary
`C:/dev/Multitool/BethesdaMultitool`, historical checkouts, their indexes,
intentional deletions, dumps and all existing evidence. Reuse this continuation
worktree. No reset/clean, routine clone/worktree/backup or private payload copies.

## Exact current source

- Worktree: `C:/dev/Multitool-worktrees/bmt-material-preparation-20260912`.
- Branch: `work/bmt-material-preparation-20260912`.
- 2026-09-23 ~05:00: the Shared glTF validator extraction is committed and handed to the foundation: Shared `95913d4`
  plus the Python probe follow-up `f8fa5c7` the foundation asked for (bounded output, whole-tree retirement), both on
  `bethesda/gltf-validator-v1` in `C:/dev/Multitool-worktrees/shared-gltf-validator-20260923`. BMT adopts it only after
  the foundation integrates, migrating `GltfValidatorRunner` and `NifGlbKhronosValidator`. UNCOMMITTED in this worktree:
  the M2.1 router's `BMT_GLB_WRITER` environment override and option precedence are removed (standing owner ruling: no
  flags to test a feature; a flip is a row in `NifGlbExportDefaults` plus admissions, then the owner tests). Roslyn
  parse is clean and nothing references the removed members, but the build was denied by the auto-mode classifier, so
  a gated build and the focused `NifGlbExport*` tests are owed before that change is committed.
- HEAD: `41c7dda9` (local, not pushed), M2.1 steps 1-5: `72f7d822` assembly refactor, `d3cff251` normalized GLB router
  (unused: every call site native, no family admitted), `c7aa842e` adapter declines + native tangent builder,
  `87d48f25` Magick pixel-collection leak fix, `41c7dda9` per-vertex parity gate. All 12 corpus strata pass with 0
  comparison failures and 0 Khronos errors/warnings; record `docs/validation/nif-normalized-parity-20260923.md`.
  NEXT (owner decisions first): per-family admission + call-site flips (CLI `export nif`, GUI export), output size,
  `export spt`, full-FNV run. In parallel: the Shared glTF validator extraction ASSIGNED to BMT by the foundation, in
  worktree `C:/dev/Multitool-worktrees/shared-gltf-validator-20260923` (branch `bethesda/gltf-validator-v1` from
  `7bc6903`). ⚠ Long test runs go through a memory watchdog runner (heap cap + kill below 2.5 GB free).
- Earlier: Shared pin `ef4f8d6` (foundation's viewport ancestor-visibility + image automation-peer repair over `9d32ace`),
  local, not pushed. BMT's explicit D3D pause on preview collapse is kept until a GUI consumer check retires it.
- Previous: the Shared pin bump to `9d32ace` (local, not pushed): foundation's merge of BMT `6f35ffc` with its Playback
  fixes and `BrowserTypeFilterControl`; BMT's interim type-filter block replaced by the shared control. Probe p6 18/18,
  focused 293/294. NEXT: M2.1 normalized adoption; optional: thumbnail-cache button into the toolbar `Actions` slot
  via Shared `ThumbnailCacheControl`; drop BMT's D3D collapse workaround once foundation's `NativeViewport` repair lands.
- Previous: the browser layout batch commit `59750272` (local, not pushed): three-column Assets pane (tree | gallery | preview) on
  Shared `CollapsiblePanel`, full-view preview pane (image/mesh/video/audio/sky/Shadowkey), asset TYPE filter
  (BMT interim block in the toolbar's Filters slot over Core `AssetKindFilter`), kind glyph tiles for every leaf
  kind. Record: `docs/validation/phase-f-candidates-20260922.md` ("Browser layout batch"). NEXT: deliberate pin
  bump to Shared `9d32ace` (foundation's merge of `6f35ffc` + its `BrowserTypeFilterControl` + Playback fixes;
  native checks 8/8 there) and swap the interim filter block for the shared control (removal checklist in the
  plan file); rerun the FLC probe on the new pin. Then M2.1, shell/context, TB1-TB6 remainder.
- Previous: `6f131b64` (local, not pushed): the user-feedback batch, gallery caption = kind + full size + frames
  (`AssetImageInfo`), help glyph size 14; over `c4a274f8` (handoff) and `0ea55151` (Stage B). NEXT BATCH designed before
  implementation: Assets pane as tree | gallery | preview with collapsible left/right panes, click opens the FULL view
  in the right pane, asset TYPE filter in the toolbar's Filters slot applied to tree and gallery (user requirements
  2026-09-22 late, relayed in the mailbox). ⚠ `inter_session.py publish --input` writes the WHOLE document: publish
  the full file with only the Bethesda section edited (a section-only publish wiped the other sections once).
- Previous: `0ea55151` (local, not pushed): Stage B native FLC playback with integer pixel scaling in the asset browser,
  Shared pin `6f35ffc` = the scaling commit on `bethesda/decoded-eos-presentation-v1` (two commits over `20e4e2c`:
  the end-of-stream hold `71e738cf`, then integer scaling `6f35ffc`; both live only in this worktree's submodule
  checkout and are announced in the mailbox). Companion lock files refreshed to ILLink.Tasks 10.0.12. Review build
  for the user = `src/BethesdaMultitool/bin/Release/net10.0-windows10.0.19041.0/win-x64/BethesdaMultitool.exe`
  (profiler cycle `p5`; DLL SHA-256 `a3b5f220…`); not published. Evidence: `TestOutput/phase-f-flc-probe-20260922/p5`
  (probe 18/18), `TestOutput/phase-f-flc-gui-20260922/gui6` (three Arena movies through the native host, Filtering
  on and off, captures), record `docs/validation/phase-f-candidates-20260922.md`.
- **User rulings 2026-09-22 late:** UIA GUI checks are too slow and steal focus while the user works; at most one
  announced launch when a check is unavoidable, prefer the headless probe. Filtering assets by TYPE (video, sprite,
  texture, model, audio) is a framework requirement the user names; it joins TB1 ahead of the cosmetic items. Analyzers
  are a milestone gate (four `FlicPreviewPreparationTests` lines and the new families are owed before the milestone
  closes); every build keeps the default 4 GiB gate, one node, one at a time, agents never build.
- Previous HEAD: `29d4c2a8` (local, not pushed), on top of `ed0b76c0` (hardened FLC probe), `f76d3d26` (Phase F record),
  `67736939` (FLC Stage A), `1a0135f1` (PSP bounds), `a89a568d` (analyzer fixes + window retirement gate),
  `8e324dc5` (route B caption overlay) and `a373d931` (asset check crash fix). Shared pin `71e738cf` =
  `bethesda/decoded-eos-presentation-v1` (one commit over `20e4e2c`, the end-of-stream hold; lives only in this
  worktree's submodule checkout; announced in the mailbox for the foundation to merge). DDXConv `805ef26b`.
  Working tree clean apart from evidence directories under `TestOutput/`.
- **Phase F item 1 is done** (`docs/validation/phase-f-candidates-20260922.md`): the four reviewed candidates were
  rebased, verified, built and tested (427+2 then 474+2 on Windows and WSL, publishes audited), the FLC native
  probe found a real shared-bridge defect (end-of-stream published before the pipeline drained) which is fixed and
  probe-verified 13/13.
- **Machine crash 2026-09-22 ~17:45:** a build admitted at 1.64 GB free with the coordinator's gate bypassed
  crashed the machine. Rule from then on: default 4 GiB gate, one MSBuild node, one build at a time, sub-agents
  never build. Builds also run with the compiler server and `SkipAnalyzers=true` per the owner's ruling;
  analyzers return as the milestone gate (four test-file warnings in `FlicPreviewPreparationTests.cs` are owed).
- **In progress:** integer (pixel-art) scaling for native video in the core with a Filtering toggle like the image
  viewer, plus Stage B (`.flc`/`.cel` in the asset browser through the native consumer) so the owner can review
  several Arena videos. Plan: the judged `mergedPlan` in workflow `wf_55eb9af9-abc` (28 steps).
- (older) HEAD `a373d931ad4a104867cc4c78bed11c46a8fd4de6` (local checkpoint of 2026-09-22, not pushed):
  the asset check crash repair, its three regression cases, the two app lock profiles
  (ILLink.Tasks 10.0.12) and the validation records. Its parent `e38d0e30` is the base
  every 2026-09-22 evidence freeze binds.
- Shared: `20e4e2c0fb2d304ed60c05e48d8c97a9a730b684` (retained reviewed pin, unchanged).
- DDXConv: `805ef26b605be360ff0ca25aa9a54435be51bcfe`.
- The route B caption overlay (eighth attempt) was accepted by the user on 2026-09-22 and
  is committed; the working tree is clean apart from ignored evidence. Open cosmetic
  follow-up: the ? glyph reads smaller than the user would like (caption icon font, size
  10); raise it in a later App-only build rather than now.

## Immediate UI work: crash and caption hover (state on 2026-09-22 evening)

- **Crash on checking assets: fixed and validated.** Cause and fix as recorded in
  `docs/validation/asset-caption-retry-20260922.md`: the gallery two-way binding echoed a
  tree check back into the shared selection. Portable build, 19 focused + 2 accessibility
  cases on Windows and WSL, publish, PDB/correspondence/bundle audits, and a UI Automation
  replay pair (old build reaches the crash path; fixed build survives twelve actions) all
  passed; replays r6 and r7 on the route B builds passed too.
- **Simultaneous help/Minimize hover: fixed and user-confirmed** on the 8-pixel native
  strip build (fourth attempt): "They're too far away to both be highlighted at once now.
  That is fixed." Cause: a passthrough region suppresses the native caption leave.
- **Cosmetics led to route B** (user choice, "Route B now"): the ? is an overlay in the
  native caption region driven by `InputNonClientPointerSource`; no passthrough at all.
  Record: `docs/validation/caption-route-b-20260922.md`. Attempts five (crashed on a null
  region array), six (null guards; audited, replayed, observed), seven (review fixes: the
  pressed-state machine, UI-thread dispatch, RTL placement, native-metric refresh, input
  diagnostics, tab order; **its idle review instance crashed**: the queued native-metric
  refresh read a right inset of -8 after a restore and `GridLength` threw, proven from dump
  `BethesdaMultitool.exe.32452.dmp`) and eight (unusable caption metrics skipped, negative
  widths refused, the queued refresh guarded) are under `TestOutput/asset-caption-retry{5,6,7,8}-20260922`.
  Eighth executable: `TestOutput/asset-caption-retry8-20260922/publish/windows-asset-caption-retry8/BethesdaMultitool.exe`,
  SHA-256 `d4e60419648ea227ca04f91c0a48243d4c96dfffc95ba1bbe54c93f7f71c4361`; audited, replayed
  (twelve actions), observed at startup (process 67532, geometry as intended).
- **User review of the eighth build (2026-09-22):** accepted, "The ? glyph is smaller than
  I'd like but otherwise all looks good." The glyph size stays an open cosmetic item.
  Accepted costs of route B: double-click on ? maximizes, press-drag moves the window,
  no hover tooltip.
- **Shared follow-up to propose through the mailbox:** `ThumbnailGallery.xaml` binds
  `IsChecked` two-way at two sites; the `CheckBoxState` one-way + Click pattern the tree
  uses would remove the echo at its source. Foundation-owned; not changed here.
- Small follow-ups: companion-app lock profiles still pin ILLink 10.0.11; the comment at
  `AssetBrowserTab.xaml.cs:141` names the wrong `--asset-source` consumer.

## Validation history

The 2026-09-21 attempt timed out at the 4 GiB admission gate (preserved in
`TestOutput/asset-caption-regression-20260921`). On 2026-09-22 the user ruled the
coordinator bypassed (`-MinFreeGB 0`, everything else retained) and the fresh attempts
ran: one lost to a VS Installer SDK removal mid-build, one to the resulting locked-restore
failure, one to a shell misfire, then the accepted fourth. Details and receipts:
`docs/validation/asset-caption-retry-20260922.md`.

## Earlier UI corrections already in the checkpoint

`12498100` implemented the twelve-item feedback batch; `e38d0e30` added the first
caption inset correction. Preserve their behavior while repairing the regressions:

1. Caption-style keyboard shortcut button and F1 behavior.
2. Missing pane resize bars.
3. File Carver output directory below file path on all tabs.
4. Texture Tools renamed DDX to DDS Converter.
5. Batch Dump Analysis moved under Recovery.
6. Model Tools pane alignment/background/card, understandable empty prompt and
   equal directory/texture input lengths.
7. Archive Browser renamed Archive Extractor.
8. Maps' redundant single World tab hidden.
9. Explore automatic source discovery combined with Load Order.
10. Subtitle CSV label specifies its use for memory dumps.
11. Physical BSA open action in Assets, retaining source/provenance checks.
12. Selected ESM changes retain the active Explore pane.

The previous UI batch has 39+2 Windows/WSL checks. The old caption publication
passed startup, normal build/audits and CLI help, but failed manual hover and
then crashed. Do not describe those old receipts as acceptance of the new repairs.

## Full goal remains active

The full approved completion program, original requirement IDs, **M1–M6 and
TB1–TB6**, implementation/integration/release distinctions and unavailable-fixture
gaps remain. The architecture survey is already complete; maintain affected
entries instead of restarting it. The goal tool was last recorded as `blocked`,
not complete; this does not cancel the authorized scope.

Original feedback: `C:/dev/Multitool/Feedback/npc/_thumbs.txt` and
`C:/dev/Multitool/Feedback/BMT-NMT-AMT/_Notes.txt`.

After the immediate UI batch, continue the approved sequence against actual
current receipts, without repeating work already accepted:

- Normalized NIF, SpeedTree, Shadowkey and Oblivion PSP adoption; preserve Granny
  paths and native direct normalized consumption. Unsupported fidelity retains
  its working route. Material/skin synthetic success does not finish M2.1.
- Shared shell/context/status, retained browsers/tables/filters/galleries and
  controls, preserving Data/Maps/Assets, saves, navigation and recovery ownership.
- List/Thumbnail/search, accessible metadata, persistent cache, artwork/posters
  and waveforms; coordinate decoded-media transport before classic-video timer
  replacement, preserving timestamps, palettes, tracks, seeking and buffer life.
- Bethesda rendering, animation, camera, picking, maps and capture with reusable
  components; map focus retains generation/world/cell/coordinates/units/placement.
  Portraits use the inspector's resolved actor/equipment/seed/idle pose.
- Shared records/characters/dialogue presentation; EGM rendering/CLI; FO3/FNV
  synchronized facial/body/audio playback; Skyrim FUZ/audio/LIP and DialogueViews.
- Shared GUI/CLI operations with original bytes, donor ordering, provenance,
  cancellation and partial success; localization, layering/analyzers, dependency
  graphs, complexity, documentation, measured performance and licensing.
- Actual changed GUI/CLI workflows, replacement/cancellation/shutdown/localization,
  current Windows GUI/publish and portable platform release evidence remain gates.

Retained evidence is in material-resume, emission-adoption, travels-fidelity,
gallery-media and UI receipts. Latest earlier gallery/media acceptance includes
424+2 Windows/WSL cases and 105 original FLC frame hashes. Historical corpus:
NIF 119/120 compared; SpeedTree ten carried, zero declined, eight missing textures,
historical 0/10 unchanged; Shadowkey 237 slots = 193 carried, 33 animation declines,
11 empty. Keep these exact limitations. Legacy NIF production dispatch and classic
video timers remain.

Reviewed **unapplied** candidates remain under TestOutput: FLC native Stage A,
gallery/window retirement, PSP resource bounds and analyzer drafts. FLC Stage B
is a paused incomplete text draft. Inspect their own receipts and current overlap
before applying; do not confuse patch existence with integrated implementation.
These ignored candidates require retaining this worktree. FLC's 47 prerequisite
cases passed in the earlier 424+2 batch; Stage A's 15 cases/native probe remain
unrun. Do not reapply already-integrated gallery/FLC adapter/cache candidates.
Current WSL runtime evidence is separate from the older Linux source/publish
evidence bound to `5caf8ee3` / Shared `88326240`.

Settled defaults: same-source reload retains primary plugin/load order in-session;
cross-session restoration requires an explicitly saved workspace. Persistent cache
512 MiB; cached head-and-shoulders idle portraits; established label fallbacks;
waveform seeking uses the existing media clock. Game records/codecs/inventory/
voice resolution remain BMT adapters. Public pushes/releases, PS3 repacking,
OTV reconstruction and CorpusTool redesign remain outside this continuation.
