# Session handoff, 2026-09-25 (paused for the weekly usage limit)

Written for the next session on this worktree, and for the owner. Everything below is committed locally (never
pushed) unless it says otherwise.

## 1. Where things stand

**BMT** worktree `C:/dev/Multitool-worktrees/bmt-material-preparation-20260912`, branch
`work/bmt-material-preparation-20260912`, HEAD `8b8c0c27`. Shared submodule pinned at canonical main `dacc231`.

| Commit | What |
| --- | --- |
| `3cca29c5` | Pin Shared main `dacc231`. Portable and Windows builds: 0 errors. Targeted tests: 909 pass. Gate 1a hops B, C and E: 220 of 220. Smoke: pass. |
| `8b8c0c27` | Cut-1b slice 2: curve, channel-state, B-spline, clock (RE-22) and source-policy mapping components, with 55 tests. The Modeling and NIF animation namespaces: 804 pass. |
| `23a1a28c` | Cut-1b slice 1: lossless runtime views. |
| `1ea1d19a` | Cut-1b slice 0: the manifest and payload expectations. |
| `b977b1c9` | RE-24 (quaternion TBC and QUADRATIC rotation) added to `docs/formats/nif-animation-engine-behavior-20260925.md`. Confirmed with corrections. |
| `5dfc8ecc`, `11a3f64d` | `docs/design/sa3-sa5-curve-contracts-proposal-20260925.md`: the SA3 to SA6 proposal, with SA6 in section 6a. |
| `dc972c12` .. `06a0ff58` | `docs/design/sa1-glb-clock-lowering-20260925.md`: the SA1 specification and its delivery record. |
| `f64d4f71`, `a638e75a` | The gate-1a Blender harness (`tools/scripts/gate1a/blender/`) and its version-read fix. |
| `01b5da2b` | Harness fix: Blender 5.1 is not long-path aware, so its glTF importer reports "Please select a file" for a GLB path of 260 or more characters. 22 of the 57 sample GLBs are 264 to 297 characters, and B' failed 12 of 12 launches on each. B' now stages those GLBs at a SHA-256-verified short copy, and the launcher refuses any longer path argument. Self-check: 110 of 110. |
| `4f96355e` | This handoff: the foundation's answers (section 3a). |

**Shared SA1** (GLB clip and track clock lowering): branch `bethesda/animation-clock-lowering-v1` at `51ef128`,
rebased onto main `dacc231`, in worktree `C:/dev/Multitool-worktrees/shared-clock-lowering-20260925` (clean). It was
handed to the foundation at 17:31. The foundation recorded it for integration; it is **not merged yet**.
- Media Models tests: 380 of 380 pass.
- Full Media suite: its only failures are load-timeout process and validator-probe tests outside SA1's files. The
  set changed from run to run while other sessions held the machine under 3 GB free.

## 2. Possibly still running when the session paused

- **Blender full run** (gate 1a hops B', D and F), started from the main session under the owner's approval. It
  gates each launch on 6 GiB free. It runs BelowNormal under a kill-on-close job object and a watchdog, holds the
  global mutex `Global\Multitool.Gate1a.BlenderHops.v1`, and resumes from its receipts.
  - Check first whether it is alive:
    `Get-CimInstance Win32_Process -Filter "Name='python.exe'" | ? CommandLine -like '*run_blender_hops*'`.
  - If it is not alive, resume from the repo root:
    ```
    python tools/scripts/gate1a/blender/run_blender_hops.py all --out TestOutput/gate1a-blender-20260925 --artifacts TestOutput/pin-dacc231-20260925/gate/artifacts.json
    ```
    The latest log is `full-4.log`, restarted at 21:55 after `01b5da2b`. Its B' receipts from before the fix are
    reused for short paths, and the three long-path failures (m0-0015, m0-0017, m0-0019) rerun on their own because
    the command changed. Before the fix, `full-3.log` had 3 samples pass (m0-0011, m0-0014, m0-0024) and those 3 fail;
    the VEC4 + VEC3 control runs after every sample, so it has not run yet in the full run.
    Exit codes: 0 pass; 1 a check failed; 2 refused; 3 aborted, for example after an hour below 6 GiB, which is
    normal on this machine. Just rerun it.
  - Read `TestOutput/gate1a-blender-20260925/receipt.md`.
  - Never run it beside a build: stop it (it is safe between launches), build, then resume.
- **Cut-1b slices 3 and 5 agent** (skeleton resolution, text-key events). Brief:
  `TestOutput/cut1b-slice35-20260925/BRIEF.md`. It adds new files only, under `src/BethesdaMultitool/Core/Modeling/Nif/`
  and `tests/BethesdaMultitool.Tests/Core/Modeling/Nif/`. If it was cut off, those untracked files are unverified and
  may be partial. Build and test them (the chain is `TestOutput/cut1b-slice2-20260925/chain.ps1 <label>`), or delete
  them and re-brief. Never commit them untested.

## 3. Waiting on others

- **Foundation**, through `C:/dev/Multitool/Multitool.Shared/INTER-SESSION.md`:
  - Integrate SA1 (`51ef128`). Once main carries it, bump the pin (the chain is `TestOutput/pin-dacc231-20260925/chain.ps1`;
    copy it with the new commit).
  - Answer the SA3 to SA6 proposal (section 7 questions 1 to 5, plus question 6 on the Squad certificate adapter).
    Until then, **no Core contract edit**.
- **Owner:**
  - Blend model for hop F, now a failure rather than an open question. The smoke run's blend pairs fail under the
    display model: Blender composites the importer's Transparent-plus-Emission graph in linear space, worst 56.89/255,
    while the linear model is within 1.03/255.
  - The ruling needed: change the Media.Blender importer to reproduce display-space blending, or accept a declared
    deviation. Hops B' and D and the rest of F are unaffected.

## 3a. Foundation answers read just before the pause (canonical main `b80f9f6`)

These override section 3 where they differ. Quote the mailbox before acting; the text there is authoritative.

- **SA1 is merged into canonical main (`88303fa`), but consumer adoption is held for a Bethesda precision repair.**
  - The foundation added `5280d37` (preserve terminal clock holds; trim derived keys in linear time) and `74dbe96`
    (failing expectations: "Cover intermediate rounding in composed animation clocks").
  - At `77e583a` the Models run passes 392 of 394. The only failures are the two large-origin precision cases,
    0 vs 0.5 and 2 vs 1.5.
  - The cause, as I read it: `ModelGltfClockMap` composes clip and track clocks in double, while Core's
    `ScenePoseEvaluator` rounds the clip clock's output to Float32 (`SceneAnimationClock.Map` returns float) before
    the track clock maps it.
  - The repair: preserve Core's intermediate Float32 rounding, keep the `5280d37` fixes, start from current main, and
    claim the files first. A broad refusal of composed clocks does NOT count as completing SA1.
- **SA3 to SA5 proposal: proceed.**
  - `SceneCurve` as a public, target-free seam is right. It is numeric only (no quaternion rules), reuses the
    existing sampling and validation internally (no copies), and keeps immutable ownership, cancellation and source
    policy. Composition must remap material indices, not just nodes, and keep layer occurrence identity.
  - Bethesda may also draft the evaluator, mixer, blend-weight, pose-state and workspace slice under an exact claim:
    - one driver per rotation component;
    - explicit per-target morph participation;
    - undriven targets keep their defaults.
  - Publish typed animated material, layer and visibility outputs now. Keep their static source values and any unknown
    optional values, including ambient. Renderer adoption is a separate gate that the foundation keeps.
  - Until a backend supports a new collection, its admission must refuse it explicitly, including mixed old and new
    clips. Slice 1 must not let an export silently omit new tracks.
  - Cubic property curves: add a narrow SharpGLTF cubic pointer-channel patch (with a patch manifest, notices and
    independent emitted-accessor readback). Do not resample to LINEAR. Mixed STEP/cubic merges and the visibility
    wrapper fallback must disclose their timing or fallback differences.
- **SA6 is assigned to Bethesda: the Squad sampler and the GLB certificate extension.**
  - Correction to proposal section 6a: `SceneGamebryoRotation.InterpolateComponents` returns UNNORMALIZED components.
    RE-24 FastNormalizes both inner slerps before the outer one, so the design needs an explicit PC/X360
    normalization policy with its evidence.
  - Keep source signs, raw keys and the supplied inner points.
  - The quaternion certificate (`IModelGltfQuaternionCurve.ReadControls`) is cubic-only. Extend the proof for the
    nested sampler; fitted cubic controls cannot establish the 0.1-degree bound.
  - Handle RE-24's Float32 fraction and weight order, and its exact internal-key segment choice, explicitly; the common
    sampler differs.
  - Validate the inner-point slots as quaternions, not tangents.

## 3b. Work done after the pause note (2026-09-25, 21:55 to 22:07)

- **SA1 precision repair: DELIVERED at 22:06, ready for the foundation to integrate.** Claim delivered 21:58; delivery
  note delivered 22:06:03. Results: Release Media.Tests build 99 warnings (none in the changed files), 0 errors;
  Models namespace 398 of 398, no skips (the foundation's 394, both `74dbe96` cases included, plus 4 new). Next:
  when the foundation merges it, bump the pin to the latest main (always the latest).
  - Shared branch `bethesda/clock-intermediate-rounding-v1` at `90fad2e`, from `b80f9f6`, in worktree
    `C:/dev/Multitool-worktrees/shared-clock-lowering-20260925`. It is committed locally, never offered, and was not
    built at commit time.
  - Rule (`ModelGltfClockMap`): the rounding of the clip clock's output moves the track's local time by at most
    |f_T| times half the Float32 spacing of C(tau). Within 2 ulp of the track clock's range the piece stays affine.
    Beyond that it becomes held steps, one per Float32 clip value, with boundaries found by bisection with
    `SceneAnimationClock.Map` and each held time from `T.Map`. Steps beyond the key budget are refused
    (`RoundingBudgetMessage`, row `clock-budget-exceeded`).
  - Tests added to `ModelGltfClockPrecisionTests`: every-instant checks with Clamp and Loop tracks, an
    affine-stays-affine control, and the budget refusal.
  - Chain: `TestOutput/sa1-precision-20260925/chain.ps1 <label>` (Media.Tests build, then the Models namespace).
    Evidence in `run-1/` and `chain-run-1.log`. Expect the foundation's 392 of 394 to become 394 of 394, plus the new
    cases.
- **Slices 3 and 5: COMMITTED `57fd0db7`** (portable build 0 errors first time; 26 new tests pass with 0 skipped; the
  Modeling and NIF animation namespaces pass 830, 0 failed, 3,426 opt-in skips unchanged). The agent's report:
  evidence `TestOutput/cut1b-slice35-20260925/run-1/`. Owner decisions it lists: a null
  text-key label becomes an empty event with `IsNullLabel`; a misaligned text-key block stays native; empty names
  are valid keys; an unreadable palette blocks every lookup; provenance lists each name once.
- **Blender run:** stopped for the build window and resumed as `full-5.log` at 22:06. It reuses its receipts and
  was waiting for 6 GiB admission (about 4 GiB free) at m0-0015, the first long-path item. Resume it with the
  section 2 command if it is not running. Still open from the agent's report: a Bucket-B check that every manifest
  `.kf` resolves its pinned skeleton, the archive-order choice for duplicate skeleton paths, and how slice 10
  opens a `--skeleton` outside the data VFS.

- **Slices 4 and 6: COMMITTED `4fe98146`.** `NifModelAnimationReader` assembles the slice 1-5 components into clips:
  sequence clips, manager multi-clip, and the `(controllers)` clip. Portable build 0 errors first time; 22 new cases
  pass; 852 pass in the namespaces. Evidence: `TestOutput/cut1b-slice46-20260925/run-1/`. The agent's open items (in
  its report) for the owner:
  - an admitted sequence becomes a clip even with zero typed tracks (matters for D14 in slice 10);
  - BSRotAccum stays native per plan 2.1;
  - the inactive multi-target check blocks all of that manager's sequences;
  - unlisted `.nif` sequences are left for slice 8.
- **RE-21 collapse: COMMITTED (see git log after `e08bbede`; 854 pass, 0 failed).** It replaces D7's whole-sequence-native rule, since RE-21 is
  settled. Duplicates of one target collapse to the lowest controlled block when they share an interpolator or map
  to bit-identical channels (new `NifModelTrackContent`). Differing content, or differing priorities (0 retail
  cases, and 0xFF makes priority comparison uncertain), keep the sequence native. Tests: the D7 test became the
  identical, shared, differing-content and differing-priority cases. Chain:
  `TestOutput/cut1b-re21-20260925/chain.ps1`; evidence in `run-1/`.
- **Slices 7 and 8 agent (started ~23:12):** morph weights (`NifModelAnimationMorphs`), then coverage
  classification (`NifModelAnimationCoverage`) and native state (`NifModelAnimationNativeState`). Brief:
  `TestOutput/cut1b-slice78-20260925/BRIEF.md`. It may edit the committed cut-1b animation files. If it was cut
  off, its changes are UNVERIFIED: build and test them with a copy of `TestOutput/cut1b-re21-20260925/chain.ps1`,
  or discard them with `git checkout` plus deleting its untracked files, then re-brief.
- **Blender run:** resumed as `full-7.log` after the RE-21 build (19 B' samples had passed by `full-5.log`,
  including the three long-path ones).
- **SA3 to SA6 slice 1 deliberately NOT claimed yet.** It edits foundation-owned Core files for several hours, and a
  claim held through a multi-day pause would block the foundation. Claim it on resume, when there is time to finish.

## 3c. Resumed 2026-09-26 ~00:00 (Fable 5.1): the foundation took over Shared animation while I was stopped

Read this before section 4, which it changes.

- **Owner-directed takeover (mailbox, 2026-09-25):** with Bethesda stopped, the foundation took over the SA1
  precision repair and "remaining Shared animation integration", preserving my branches, worktree and pin. It
  integrated my `90fad2e` as `e842d08`, repaired the two boundary defects itself (`ef01f74`, with the four
  `ModelGltfClockPrecisionBoundaryTests` on main), then delivered, in ~100 commits up to canonical main `68335d3`:
  the SA3 to SA5 contracts (`SceneCurve`, `SceneEulerRotationTrack`, `ScenePropertyTrack`, `SceneMorphTargetTrack`,
  `84c6486`), the evaluator and mixer (`23d1617`), typed property poses and the SA6 `GamebryoSquad` PC sampler
  (`c1995ac`; export still refuses Squad, no certificate yet; X360 policy refuses), GLB writers for per-target
  morphs (`3598586`, `02be711`), material pointers (`e30f175`, `bc54406`, SharpGLTF cubic pointer patch), Euler
  (`c2910b7`), and SA9 Blender native animation (`7f3935d`, `005a4a8`, `1f8a898`, matrix clocks `8a72716`). Docs:
  `docs/scene-curve-tracks.md`, `scene-morph-target-export.md`, `scene-euler-export.md`,
  `scene-material-property-export.md`, `scene-gamebryo-squad.md`, `blender-native-animation.md`,
  `native-animated-properties.md`. The foundation then hit its own usage limit; I am the only active session.
- **Consequences:** my uncommitted boundary fix in `C:/dev/Multitool-worktrees/shared-clock-lowering-20260925` was
  superseded and discarded (diff kept as `TestOutput/sa1-precision-20260925/boundary-fix-superseded.diff`); the
  branch `bethesda/clock-intermediate-rounding-v1` (`f05a7ff`, rebased) is fully integrated and needs nothing.
  The SA3 to SA6 slice-1 claim I was going to make on resume is moot. Every Shared-side blocker of cut 1b is gone;
  what remains is BMT-side mapping onto the shipped contracts (slices 7, 8, 12-15), then the Bucket-B hops (9) and
  the switch-over (10).
- **Pin bump to `68335d3` COMMITTED `cd510460`:** portable build 0 errors; targeted tests 1,014 pass; gate 1a B/C/E
  220 of 220 with every control detected (57 converted, 163 partial, as at dacc231); smoke exit 0; Windows main-app
  build exit 0 with a fresh net10.0-windows dll at 14:07. Evidence `TestOutput/pin-68335d3-20260926/`.
- **Blender full run FINISHED (`receipt.md`, 04:07 to 04:51 UTC):** B' 57 of 57 PASS (control detected); D 168
  pass, 41 fail; F: blend pairs fail under the display model (the owner ruling still owed), the other four F checks
  pass, all 5 F controls detected. The D failures are 25 corners off by 0.13 to 0.64 degrees, 13 corners off by 82
  to 90 degrees, and 3 X360 creature packages the importer refuses ("shares a source point ... but has different
  skin joints"). An investigation agent is writing `TestOutput/gate1a-blender-20260925/investigate-d/REPORT.md`.
- **Slices 7 and 8: COMMITTED `c01a5497`** (second attempt, against the `68335d3` contracts; brief
  `TestOutput/cut1b-slice78-20260926/BRIEF.md`; 22 files; Modeling 925 pass). Open items from its report: the runtime
  `NifGeometryMorphReader` was not re-pointed at the new morph views (a D5 one-decode-path follow-up for slice 10);
  repeated morph targets are kept native even when identical (an RE-21 step 7 collapse could be added); the native
  row's over-budget summarization path is untested. The hop-D investigation is done (section 3e).
- **Packed skin padding fix: COMMITTED `bad092a6`** (section 3e).
- **Slices 13 + 16b: COMMITTED `d83c8a6e`** (Euler onto `SceneEulerRotationTrack` with the RE-20 rules; TBC/QUADRATIC
  quaternions onto `GamebryoSquad` with RE-24 inner points bit-equal to the retail receipt; policy by platform, PS3
  blocked as unmeasured; 941 pass). Owner item from its report: the PS3 policy needs the PS3 build's
  FastNormalize/Squad read from its executable, checked against the GECK model as RE-24 did for X360.
- **Slice 14: COMMITTED `747faaed`** (property and visibility tracks onto `ScenePropertyTrack`: alpha, the four
  material colors, emittance, texture transform, UV and visibility controllers, embedded and in sequences; one track
  per fed material, layer ordinal or node occurrence; 980 pass). The staging agent died on the Fable monthly spend
  limit (~16:47) before its report; the main session (switched to Opus 5.5) finished it: two tuple arities, a
  truncated test-support file, and three test-side causes (Shared refuses unlowered source geometry, so documents use
  legacy stand-in triangles; negative sample times are rejected, so rest controls read `EvaluateRest`; the reader now
  decides the property controllers in coverage). Landing record `TestOutput/cut1b-slice14-20260926/README.md`, which
  also records the retail texture-transform gate: ROTATE and SCALE_U/V refuse wherever translation != center (Shared's
  `SceneTextureTransform` has no center); a derived companion offset track would be exact (follow-up candidate).
- **Slice 10 is blocked on two Blender gaps (found 2026-09-26 ~17:25).** At the pin, `BlendAnimationAdmission.Validate`
  THROWS for the whole document on any clip with property tracks ('Material and visibility channels require native
  Blender property bindings') and on any GamebryoSquad track ('requires certified Blender conversion'). So switching the
  reader over would turn `mesh convert --format blend` Unsupported for every NIF with material, texture or visibility
  animation and every QUADRATIC/TBC quaternion. Under D3 ('close the gaps first') both close before slice 10. GLB also
  still refuses Squad under `Xbox360Estimate` (X360 files) and PS3 is blocked: an owner/RE item.
- **Blender property animation: DONE on Shared branch `bethesda/blender-property-animation-v1`** (worktree
  `C:/dev/Multitool-worktrees/shared-blender-property-20260926`): `c3ffc93` = the foundation's uncommitted slice
  verbatim; `da616d4` = the float3 custom-normal importer (cherry-pick of 005eb0a); `d645136` compile + test repairs;
  `add3684` completes WIP items 2-6 (specular refused where the blend draws the unlit color, via the shared predicate
  `BlendAdmission.EmissionKeepsLighting`; the C# visibility-driver reservation; the real-Blender
  `BlendPropertyAnimationIntegrationTests` with a clip-swap control; metadata + doc; a flaky temp-dir count fixed).
  Portable 568 pass; ALL 14 real-Blender integration cases pass (evidence
  `TestOutput/blender-property-20260926/`, runner `blender-run.ps1`: one Blender, >= 6 GiB, BelowNormal, watchdog).
  Finding for the clock lowering (pre-existing, not property-specific): a Loop wrap is two keys one Float32 step apart,
  and Blender's F-curve search treats keys within 0.01 frame as one, so a frame exactly on a wrap shows the pre-wrap
  value.
  **NOT INTEGRATED: the fast-forward of canonical main needs the canonical working tree's 15 WIP paths restored to HEAD
  first (git refuses otherwise), and the auto-mode classifier DENIED that as irreversible local destruction, although
  every byte is preserved in `c3ffc93` and in `C:/dev/Multitool-worktrees/foundation-blender-property-wip-backup-20260926/`
  (SHA256SUMS; verified live == backup == commit on all 15). OWNER DECISION: allow the restore + `merge --ff-only`, or do
  it by hand. Do not retry or route around it.**
- **Blender Squad: DONE on Shared branch `bethesda/blender-gamebryo-squad-v1` `a795a47`** (stacked on `add3684`;
  worktree `C:/dev/Multitool-worktrees/shared-blender-squad-20260926`): the SA6 certificate moved into Core
  `Models/Export` (public `SceneGamebryoSquadCertificate`, `SceneGamebryoSquadAnalysis`, `SceneQuaternionGeometry`);
  GLB unchanged (Media Models 610, Core 58); Blender lowers PC Float32 Squad to the certified LINEAR keys with two
  Blender refusals (non-positive key dot; keys closer than 2e-4 frame at 32 fps, Blender merging keys within 1e-4
  frame); Xbox 360 estimate still refused. Blender portable 570; all 15 real-Blender cases pass (Squad 0.045/0.036
  degrees against certified 0.0876/0.0873). All 16 retail PC Squad tracks of the manifest pass the spacing rule
  (closest 0.000397 frame). Evidence `TestOutput/blender-squad-20260926/README.md`. Canonical integration of BOTH
  branches (`merge --ff-only bethesda/blender-gamebryo-squad-v1` brings `add3684` too) waits on the same owner decision.
- **Slice 10: STAGED AND COPIED IN, UNCOMMITTED** (brief + staging `TestOutput/cut1b-slice10-20260926/`): NifModelReader
  wiring, `.kf` admission (`NifModelAnimationStreamReader`, walk-up or `--skeleton`/`bmt.skeleton`, D4 and D14
  refusals), `--skeleton` on the six mesh read commands, the D5 re-point of `NifGeometryMorphReader`, a morph gate
  (`MorphTargetsNotTyped`, so weight tracks never target untyped morphs). Main-session fix: a held (static) visibility
  value was a Linear Constant, which Shared rejects ('Visibility requires step interpolation'; 4 retail files failed to
  read) -> Step, with a regression test and A1's expectation fixed. At pin ec24839: default suite = the 7 pre-existing
  environment failures only (identical to slice 1's baseline), Modeling Bucket-B oracles 3,691 pass / 0 fail. Now
  testing with the submodule DETACHED at `a795a47` (uncommitted): `TestOutput/slice10-at-a795a47-20260926/`. Commit
  only after canonical main carries both Blender branches and the pin moves there. Owner items from the agent: D4
  under SA8 (no writer reads `RestPoseProvenance` yet); the GLB key budget on the largest manifest `.kf`
  (`dlcpittusingautoaxehigh.kf`, 23,685 stored keys); loose `.kf` inputs need `--skeleton`; a directory convert maps
  `idle.nif` and `idle.kf` to one `idle.glb` (planner collision).
- **Slice 10 at `a795a47` (TestOutput/slice10-at-a795a47-20260926/README.md): checks green but conversions REGRESS**:
  Blender packages 208 -> 152 of 220 (61: the two Shared writers contradict on clocked clips, GLB derives the window
  from the clock and refuses an authored duration while Blender requires an explicit duration and never derives one;
  3: Blender demands animated skin weights sum EXACTLY to 1); GLB loses 2 (NodeVisibility needs KHR_node_visibility,
  which the approved cut-1b scope lists; one clock over the derived-key budget). These are the next D3 gaps.
- **Writer gaps: DONE on Shared branch `bethesda/cut1b-writer-gaps-v1` `d2d1d15`** (stacked on `a795a47`; worktree
  `C:/dev/Multitool-worktrees/shared-writer-gaps-20260926`; evidence `TestOutput/writer-gaps-20260926/`): (A) Core
  `SceneClockConversion.PlaybackWindow` (GLB's window moved to Core); Blender uses it when `DurationSeconds` is null;
  (B) Blender admits near-unit animated skin weights (|s-1| <= 2^-17 per lane, <= 2^-12) with an Approximated
  `skin/weightNormalization` row; MEASURED in Blender 5.1: the armature divides by the weight sum exactly; (C) GLB
  `KHR_node_visibility` via KHR_animation_pointer. Also repaired the importer's Python test harnesses (broken since
  the foundation's WIP `c3ffc93`; 50/50). Core 46, Media 612, Blender portable 591, every real-Blender case passes.
  PROPOSALS for the owner/foundation: (A) and (B) change deliberate foundation rules. Open: X360 `nv_ncr_flag` has
  quantized weights |s-1| = 3.66e-4 (above 2^-12; the design's 1/255 bound would admit it: one constant);
  `citadelflag01` GLB clock budget untouched. Slice 10 chain at `d2d1d15`: `TestOutput/slice10-at-d2d1d15-20260926/`.
- **Slice 10 at `d2d1d15`:** gate B/C/E 220/220 after the gate readers learned package v4 and KHR_animation_pointer
  (harness edits uncommitted with slice 10). Blender packages 208 -> 172 (39 samples still regress on deeper Shared limits:
  cubic handles across keys one Float32 step apart, the clock derived-key budget, animated billboards). OWNER DECISION
  before any more Shared work: close each gap, or let the Blender writer drop an unpublishable clip with an explicit
  Dropped row instead of refusing the whole document. Details `TestOutput/slice10-at-d2d1d15-20260926/README.md`.
- **Cubic bridge `fdf7964`** (writer-gaps branch): Blender emits cubic spans too narrow for a Float32 handle (loop-wrap
  keys) as LINEAR inside the cubic buffer, as Core already does for matrix clocks. Slice 10 at `fdf7964`: Blender
  packages 185/220 (208 before), GLB 84 (74 before), gate B/C/E 220/220. 28 regressions left, all foundation-design
  limits (SA1 clock-rounding certificate budget 14, animated billboards 7, erased handle value 4, window budget 1, X360
  Squad 1, quantized weights 1): `TestOutput/slice10-at-fdf7964-20260926/README.md`.
- **Gate 1b harness: COMMITTED `3b3658ac`** (`tools/scripts/gate1a/run_gate1b.py`, hops B-anim/C-anim/E-anim, the
  170-degree control, `selfcheck_anim.py`; `nif_curve_eval.py` extended with the dump's curves and clock composition,
  A8 unchanged: slice 9 A8 + rotation hops still 232 pass). On slice 10 at `fdf7964`: PASS, every control detected
  (B 4 samples, C 52, E 3; worst 0.773 of the writer's certificate). Owner item: B-anim's hemisphere rule (component-
  linear nlerp must equal slerp at LINEAR midpoints) is the agent's portability rule for Blender's glTF importer, not
  the glTF spec. Coverage is thin until the 28 writer regressions close (only 4 animated GLBs).

- **Slice 9: COMMITTED `976c8f78`** (Bucket-B hops A1-anim, A2-anim, A3-anim, A8-sample, 170 degrees, RE-17 +
  `tools/scripts/nif_curve_eval.py`): RUN_BUCKET_B=1 1,599 pass / 25 skip / 0 fail, every control detected. Landing
  record `TestOutput/cut1b-slice9-20260926/README.md`. OWNER QUESTION from it: X360 `nvslave_02_go.nif` (the plan's
  170-degree .nif twin) stores NaN in NiNode 109 Translation.x and the cut-1a node reader refuses the whole file (pinned
  as a known refusal); admit such files with the node refused instead?

## 3d. Owner instruction 2026-09-26 ~13:55: with the foundation paused, Bethesda handles its work too

- Acting for the foundation means: Shared changes go on `bethesda/` branches in worktrees of
  `C:/dev/Multitool/Multitool.Shared`, are built and tested there with the foundation's own gate, and are integrated
  by fast-forwarding canonical `main` (`git -C C:/dev/Multitool/Multitool.Shared merge --ff-only <branch>`), with the
  commit listed in the Bethesda mailbox section so the foundation can review on resume. The mailbox note of
  13:50 says so.
- **The canonical checkout is DIRTY with the foundation's unfinished Blender property-binding slice**
  (`git -C C:/dev/Multitool/Multitool.Shared status`: `import_model.py` +331 lines, `BlendAdmission`,
  `BlendAnimationAdmission`, `BlendAnimationClockCurves`, `BlendAnimationCurve(s)`, `BlendAnimationMetadata`,
  `BlendReasonCodes`, the package manifest and mapper, a deleted `LICENSE.BMT.txt`, and `docs/bethesda-session-handoff.md`).
  The foundation was briefly active after my 13:50 note: it left `docs/blender-property-animation-wip-20260926.md`
  (untracked) and an uncommitted `WORKING-STATE.md` edit that acknowledge the takeover and ask that these edits,
  `Python/import_model.py` above all, be PRESERVED. The WIP note lists seven unfinished items (review and compile the
  C# and Python together; align unlit decisions between Python and C#; mirror the visibility-driver reservation in
  C#; real Blender save/reopen property tests; `ModelBlendFormatMetadata` wording; scoped checks; commit before
  verification and an AWE pin). Never stage, discard or overwrite the tree; a fast-forward that would touch those
  files is refused by git, which is the guard. Consequence: the float3 custom-normal importer branch (`005eb0a`,
  section 3e) integrates only after that slice is committed, whether by the foundation on resume or by me finishing
  it per the WIP note once the cut-1b critical path (slices 14, 9, 10) is done.
- **SA6 GLB certificate for `GamebryoSquad`:** spec `docs/design/sa6-squad-glb-certificate-20260926.md`
  (`2ad4fa2e`), being implemented by an agent in `C:/dev/Multitool-worktrees/shared-squad-certificate-20260926`
  (branch `bethesda/gamebryo-squad-certificate-v1` from `68335d3`). IMPLEMENTED (uncommitted in that worktree,
  ~15:00): new Media files `ModelGltfGamebryoSquadNode/Curve/Certificate/Rotation.cs` and
  `ModelGltfQuaternionGeometry.cs` (the cubic certificate's helpers factored out); `SceneGamebryoSquad` public;
  dispatch in `ModelGltfTransformChannels`, `ModelGltfAdmission`, `ModelGltfAnimationMetadata`,
  `ModelGltfClockLowering` (policy kept through `WithoutClock`), `ModelGltfClockPrecision` (prior bound); tests
  `ModelGlbGamebryoSquadTests` + rewritten `ModelGamebryoSquadAdmissionTests`; docs. One documented deviation:
  term 2 uses each sub-interval's own E (max with the leaf that produced its start key), because a whole-segment E
  explodes through the refinement's dependency problem; the chain of three discrepancies is still covered by
  4*max(E)/p. Chain `TestOutput/sa6-squad-certificate-20260926/chain.ps1 run-1` (Core + Media test builds, Models
  namespace, Core rotation classes): run-3 GREEN after two guard fixes (an overflowed rounding bound or enclosure
  now reads as unbounded and splits instead of throwing; the collapse message names the Float32-resolution wall).
  COMMITTED on the branch as `0e29d1b` (16 files, +1,568/-98). Spec section 8 records the deviations. The
  adversarial review (`TestOutput/sa6-squad-certificate-20260926/review/VERDICT.md`, transliteration and
  experiments beside it): trustworthy on every accepted input (7,000+ leaves, zero per-leaf violations) but two
  derivation defects (term 2 at an internal key across a FastNormalize branch mismatch; E_u's quotient rounding), an
  inexact duration, a needless discontinuity flag on the outer-weight straddle (a third of the keys), and tests that
  compare track-wide instead of per leaf. Six repairs were sent to the implementer (~15:15); its first version
  (chain run-4, 15:44) compiled but charged each leaf the whole widened box diameter of the preceding box-path leaf
  as the start deviation, which can equal the entire budget, so wide fixtures walked to the Float32-resolution wall
  (14 failures). The rigorous quantity was sent back (~15:50): within a segment nothing beyond the leaf's own E; at
  an internal key the diameter of the previous segment's widened POINT enclosure at amount 1 (branch jump plus
  rounding). Chain run-5 GREEN (610 of 610; three new tests). The re-review (`review/VERDICT-2.md`) HOLDS on every
  item, with one remaining formal gap (the projection floor must allow for a start key up to D_a outside the box)
  and a request for a threshold-adjacent fixture; both applied (`ec24839`), chain run-8 GREEN: Media Models 611 of
  611, Core 57 of 57. **INTEGRATED: canonical Shared main fast-forwarded to `ec24839`** (0e29d1b, 07a4b91, ec24839
  on 68335d3; none of the foundation's dirty files touched). Mailbox note delivered 16:08. **BMT pin bump to
  `ec24839` COMMITTED** (see git log; build 0 errors, targeted 1,101 pass, gate 1a 220 of 220, smoke and Windows
  build exit 0; `TestOutput/pin-ec24839-20260926/`). Without it every file with TBC rotations refuses GLB after the switch-over
  (ruling D3 wants the gaps closed first).

- **Briefs ready for the next reader slices (spawn in this order, one at a time, each after the previous lands,
  because all three edit `NifModelAnimationReader`):** slices 13 + 16b (Euler onto `SceneEulerRotationTrack`;
  quaternion TBC/QUADRATIC onto `GamebryoSquad` with RE-24 inner points and a per-platform policy):
  `TestOutput/cut1b-slice13-16b-20260926/BRIEF.md`; then slice 14 (property and visibility tracks onto
  `ScenePropertyTrack`): `TestOutput/cut1b-slice14-20260926/BRIEF.md`. Both stage under their own `staging/` tree.
  After them: slice 9 (Bucket-B hops A1-anim, A2-anim, A3-anim, A8, the 170-degree control) and slice 10 (the
  switch-over), which also needs the SA6 certificate on the pin.

## 3e. Hop D settled (2026-09-26 ~15:30): two importer limits and one reader padding rule

`TestOutput/gate1a-blender-20260925/investigate-d/REPORT.md` (agent investigation plus my Blender 5.1.1 probe):

- 25 failures: Blender's INT16_2D custom-normal encoder snaps a normal within 0.81 degrees of its fan's reference
  edge onto it; 13 failures: corners Blender cannot encode (zero-length-edge faces, back-to-back two-sided cards).
  Neither is a data or reader defect. Fix, measured on 5.1.1 (`probe-float3-normals/probe.json`): write a CORNER
  FLOAT_VECTOR `custom_normal` attribute instead of `normals_split_custom_set`; Blender returns it verbatim (0.0000
  degrees; unit vectors even on the degenerate corner). Done in Shared `import_model.py` on branch
  `bethesda/blender-float3-custom-normals-v1` (worktree `C:/dev/Multitool-worktrees/shared-blender-normals-20260926`,
  COMMITTED `005eb0a`). The rerun of hop D with that importer (`run_blender_hops.py d --shared <that worktree>`
  on the `68335d3` packages, `TestOutput/gate1a-blender-20260926-normals/receipt.md`): 206 of 209 pass, both
  controls detected, all 38 custom-normal failures gone; the 3 left are the console skin packages below (old exe). ⚠ `import_model.py` is one of the files the paused foundation left
  DIRTY in canonical: this branch integrates only after that working tree is committed.
- 3 failures (X360 creatures): the packed skin path left each partition's own bone index in zero-weight slots, so a
  point shared by two partitions got different lanes and the welding importer refused it. Fixed in the BMT reader:
  `NifModelSkinInfluences.FromPacked` pads a zero-weight slot with joint 0 (like the NiSkinData path); `PackedRule`
  and a facts count record it; `NifModelPackedGeometryTests` expectation updated plus a welding-consistency test with
  the old reading as control; the Bucket-B console oracle (`NifPackedGeometryComparison`) now compares weighted
  slots and requires joint 0 in zero-weight ones. Built with slices 7/8 in one tree (chain
  `TestOutput/packed-padding-20260926/chain.ps1`: portable build 0 errors, Modeling 924 pass, Bucket-B console/
  packed/field oracles 352 pass, 0 fail); my first welding test used the layout fixture, whose duplicated points do
  not share weighted lanes, and was rewritten on a retail-shaped fixture (`chain-run-2.log`). The three creature
  packages re-produced with the fixed exe (`repackage_kind3.py`, `gate-kind3/`) pass hop D with the float3 importer:
  `blender-kind3-2/receipt.md` 4 of 4 (3 packages + the synthetic set), both controls detected. With the 206 of 209
  rerun that makes every original D failure resolved.

## 3f. 2026-09-26 evening to 2026-09-27: slice 10 landed, canonical Shared integrated, hop D over animation

Owner rulings (2026-09-26/27; memory `feedback_owner_rulings_2026_09_27`, `feedback_glb_blender_paths_independent`):
the GLB and Blender writers are separate paths and neither is limited by the other; conversions to glTF and Blender
must be as visually lossless as possible; integrate the Shared stack; Blender clip gaps are closed one by one (no clip
dropping); approximate display-space blending in the importer; keep the writer-gap rules (A) and (B) as they are.

| Commit | What |
| --- | --- |
| `05c7ce3c` | Gate 1b judges the GLB by the glTF specification alone: B-anim's Blender-motivated hemisphere rule is gone; the sign-flip control mutates CUBICSPLINE rotations (a synthetic CubicSpline set, since no corpus GLB has one). |
| `bf6fb6a8` | Cut-1b slice 10 (the reader switch-over) plus the pin to canonical Shared main `bffa1af`. Portable build 0 errors, 1,177 targeted tests pass, gates 1a and 1b PASS, smoke, Windows build exit 0 (`TestOutput/pin-bffa1af-20260927/`). |
| `e9c14983` | Hop D over animation (`compare_d_anim.py`, `blender_fcurve.py`) after a 39-finding adversarial review; pin `48d744a` (readback additions). Real Blender 5.1: 184 of 186 pass, 3 of 3 controls (`TestOutput/gate1b-blender-d-20260927/full-1/`). |

Canonical Shared main was fast-forwarded `ec24839..bffa1af` and then to `48d744a` (the foundation's 15 WIP files were
byte-identical to `c3ffc93`; a zip is in `TestOutput/shared-integration-20260927/`). The mailbox note was delivered.

The 2 hop D failures are a real writer gap: vertibirdrrevac01 (LE, BE) packs a Loop-wrap pair `q`, `-q` as a LINEAR
bridge span, which the importer's amplitude proof refuses.

Gap program (28 slice-10 Blender refusals plus the wrap pair): designs and skeptic reviews in
`TestOutput/gate1b-gap-designs-20260927/<topic>/` (clock-rounding, billboards, erased-handles, one-offs, display-blend).
Implementation workflow `wf_1679b80b-279` writes five of them in separate Shared worktrees
(`C:/dev/Multitool-worktrees/shared-<topic>-20260927`, branches `bethesda/gap-<topic>-v1` from `48d744a`):
clock-exactness (Part A), erased-handles, wrap-sign, straight-texels, x360-squad. Each is then built and tested with
`TestOutput/gap-impl-20260927/shared-chain.ps1 -Topic <topic>`.

## 3g. 2026-09-27: every slice-10 Blender gap closed; the display-blend probe

| Commit | What |
| --- | --- |
| `6f753552` | Pin canonical Shared `cc5cda3`: clock exactness Part A (`46d729d`), erased cubic handle ordinates as reported zero derivatives (`686baa0`), quaternion key signs continuous across Loop wraps (`c72baef`, `562de50`), straight texels load CHANNEL_PACKED (`c2caf24`), the X360 Squad estimate published at the unit-normalization centre (`cc5cda3`). Gate 1b jump skips use each side's own local speed. |
| `9772cc87` | Hop F BC3 probe texels use RGB565 levels on which bit replication and rounding agree. |
| `12a3faff` | Pin canonical Shared `fa0ae8c`: billboards phase 1 (`9bab278`: controller B + facing child F + pivot empty only where needed; only a basis-changing matrix key still refuses) and the X360 signed engine lanes (`c1adabe`, `fa0ae8c`: signed lane written 0, positives divided by their sum, seam copies welded, a measured displacement row). BMT types the engine lanes (`NifPackedEngineLanes`); `tools/scripts/gate1a/skin_lanes.py` restates the written lanes for hop C. |

At `12a3faff`: Blender packages 219 of the 220 gate samples (the chimera `1hpunequip.kf` has no skeleton: an input gap,
not a writer refusal), GLB 57; gates 1a and 1b PASS with every control detected; real Blender 5.1 hop D 220 of 220 with 3
of 3 controls; Bucket-B console oracles 112 pass. Canonical Shared main was fast-forwarded to `fa0ae8c` and the mailbox
note was published (queued; the foundation is paused).

**Display-blend probe (owner ruling "approximate display space").** The staged probe was promoted into
`tools/scripts/gate1a/blender/` and run in Blender 5.1 (`TestOutput/display-blend-probe-20260927/`). Cycles: valid, set B
(Transparent colors above 1) available; worst additive error 13.1/255 against 49.7 (set A, T <= 1) and 80.2 (set K,
today's black-anchored graph). EEVEE: set B available too, but one validity cell failed: the set-B ONE/ONE cell k242 over
black drew linear 0.9668 where the fit declares 1.0 (3.8/255). Cause: the anchored fit's guard (1e-4) put its two secant
nodes so close that T jumps from 0 to tmax (8) across about 3e-5 of Cs at the saturation point, below a GPU's half-float
texture noise, so a renderer can draw a boundary texel on either side. Fix (`display-blend-probe/set_b_guard.py`,
`guard-sweep/`): guard 1/16 keeps every node and every set-B bound (re-bounded with the seeded routine: identical to 4
decimals) and makes a 5e-4 input shift move the drawn value by at most 0.41/255 instead of up to 4.9/255.
Rerun with guard 1/16 (`TestOutput/display-blend-probe-20260927-2/`): **both engines VALID, set B AVAILABLE, controls 20 of
20 in each**. D1 inputs (identical in Cycles and EEVEE): additive worst case K 80.16, A 49.65, B 13.12 /255; over black
K 0, A 49.6, B 13.05.

## 3h. 2026-09-27 afternoon: FNV runtime image, display blend integrated, billboards phase 2 in design

**FNV runtime image.** The owner allowed running the game while away. The DRM-unpacked FalloutNV.exe image was dumped
without mods (`TestOutput/fnv-runtime-dump-20260927/`; start FalloutNV.exe with `SteamAppId`/`SteamGameId` 22380, read the
module with ReadProcessMemory at the main menu; the install was left vanilla, an empty `ExitData.mhd` removed). Workflow
`wf_e764cd68-bff` (4 investigators + 4 verifiers) then settled, in `a40698e9`
(`docs/formats/nif-animation-engine-behavior-20260925.md` "Runtime re-check" and the new
`docs/formats/fnv-pc-framebuffer-and-mirror-cull-20260927.md`):
- RE-17 to RE-24 unchanged on the real runtime; RE-25's facing code byte-identical to the GECK modulo relocations (its
  near-axis drift and skip-cone edge depend on the camera's float32 view-column norm; live x87 precision unsettled);
- the PC framebuffer: no sRGB write or read, texture color raw; HDR off = A8R8G8B8 with a clamp per blend; HDR on (the
  owner's INI) = FP16 with no clamp between layers and one clamp at display (a single layer shows identically);
- mirrored matrices are drawn uncompensated (no handedness-aware cull); 15 retail single-sided unlit mode-5 geometries are
  visible only because of the mirror.

**Display blend: integrated.** Implemented by an agent, reviewed (9 findings, all fixed, incl. restoring lit fidelity via
a generic lit route), reviewed again by a 3-lens workflow (6 findings, all fixed: exponent chosen by the grid-measured lit
bound, stricter row validation, discriminating self-check controls), then the framebuffer wording moved from inferred to
reverse-engineered. Shared `a87a7e8` + `9c2e84f`, canonical main fast-forwarded to `9c2e84f`. Real Blender at `a87a7e8`:
18/18 integration tests; gates 1a/1b PASS (packages 219, GLB 57); hop D 220/220 (3/3 controls); hop F PASS in Cycles and
EEVEE after one harness fix: EEVEE's opaque (deferred) twin under-reads a dark albedo (blue 0.00977 vs lin(26/255) =
0.01033), so the lit route is now judged against a replace-blend twin drawn through the same forward path, with the
opaque twin as a Cycles cross-check (agree to 0.000/255) and an EEVEE diagnostic (0.88/255); EEVEE lit worst 1.06 -> 0.096/255.
D1 is set B provisionally (a data edit in `BlendDisplayConstants` + the harness setting).

**Billboards phase 2: in design.** Design (`TestOutput/gate1b-gap-designs-20260927/billboards-phase2/DESIGN.md`) ->
skeptic review (2 high: false mode-3 bound in the skip cone; a work order that could land Converted rows over wrong
stacks) -> rev2 (exact cone/ball switches via drivers) -> two verifiers (2 high: the driver transform space excludes
constraints; the mode-4 law overshoots its own 0.1 degree maximum) -> rev3 loop (workflow `wf_fe60719c-f28`, up to three
revise/verify rounds until no high or medium item) with the runtime findings folded in. Implementation waits for a clean
verification; the reader, writer, importer and contract then land together (BMT pins only the complete Shared change).

## 3i. 2026-09-28: the foundation's takeover adopted; the BMT billboard reader emits the extended contract

The weekly limit stopped Bethesda 09-27 ~14:50 mid-workflow. The foundation (owner-authorized) implemented every Shared
claim we held: scale-relative signed-lane allowance, clock Part B, per-use PREMUL (our route-dependent draft was not
route-dependent and is withdrawn), D1-confirmed wording, and billboards phase 2's whole Shared side (extended contracts +
ideal evaluator `5ff50b5`, typed Blender facing `c7bb6e9`, reflected faces `56c5f3a`/`d621afb`, native `f9b94be`). On
resume Bethesda released all Shared claims (mailbox) and returned to BMT-only work.

| Commit | What |
| --- | --- |
| `3f69ae50` | Pin canonical Shared `0152179` + the harness restatement of per-use PREMUL (canonical datablock always CHANNEL_PACKED; a " [PREMUL]" variant only for a declared-premultiplied color base sample on portable Blend/Additive). Chain green; gates 1a/1b PASS; hop D 220/220; hop F both engines; self-check 144/144. |
| `d572fb94` | The NIF reader emits the extended billboard contract engine-faithfully (RE-25 as re-checked on the real runtime): value & 7, per-mode encodings, thresholds, the mode-5 mirror, DrawnWinding, provenance with evidence text, a scale-sign diagnostic stage. Tests pin the 18 engine vectors + 218 cone/ball cells through Shared's evaluator (worst 4.8e-7; old-mapping control misses by 0.938 rad). Verified: builds 0 errors, targeted 1,287, gates 1a/1b PASS, hop D 220/220 over the VERSION 7 packages, hop F both engines. 74 GLBs now carry the honest `reflected-face-unlowered` Degraded row (the GLB writer's gap, reported to the foundation). |

Housekeeping: 16 merged `bethesda/*` Shared worktrees + branches removed (uncommitted remnants archived in
`TestOutput/owner-rulings-20260927/worktree-final-state/`); 3 kept (not in main's ancestry). Owner decisions all
answered 09-27 (D1 = B confirmed; clock jitter not reproduced when imperceptible; scale-relative signed lanes; PREMUL
route-dependent) and implemented by the foundation.

## 3j. 2026-09-28 (10:00 to 15:00): the foundation delivered the cut-1c asks; pin, Linux gate, slices 3-4, the .kf carry

- **Foundation deliveries (canonical Shared main):** SA1 `ScenePalette` (`04ba005`: `ModelDocument(palettes:)`,
  `SceneImageSource(paletteIndex:)`, `SceneElementKind.Palette`; both writers emit `Dropped source.palette-provenance-omitted`;
  a Blender package moves to revision 8 only when a native row targets a palette), SA4 (`585dd1f`: Int16x2/UInt16x2
  widen to INT32_2D in Blender, no reader change), SA6 (`c674b2a`, verified `cbdbf3d`: `ScenePointIndices(...,
  sourceDomainId:)` from real source identity), SA3 as a gate-scope ruling (keep the Dropped rows; the gate enumerates
  the reasons it allows with reader evidence per reason), the layering policy adopted (`244b8ad`), and
  `SceneBillboard.SourceScale` (`853b6f1`) for the cut-1b scale-sign case. API note: Shared `docs/xngine-shared-contracts.md`.
- **BMT commits (local only):** `471c3086` cut-1c plan, second amendment (slice 10 folds into 7, slice 11 =
  sourceDomainId + the enumerated gate allowance, slice 12 no reader change, M-T to M-C recorded, SA2 not triggered);
  `6c5c1fdb` pin Shared main `2e7af70` (code tip `1fac23d`: portable build 0 errors, targeted 1,357, Bucket-B console
  112, gate 1a PASS 220/220, gate 1b PASS, smoke; Windows main-app build 0 errors at `7f7f5a44`; evidence
  `TestOutput/pin-1fac23d-20260928/`); `7f7f5a44` Phase F item 8 Linux portability (`Core/Utils/EnginePath` splits
  engine paths on both separators, `HostPath` joins with the host separator and retries an exact miss ignoring case
  on a case-sensitive host; 26 product fixes plus the seven excluded-area one-liners; seven local-only source pins now
  skip naming their path; Windows default suite 22,205 with 0 failures, Linux default suite 22,205 with 0 failures, was
  45; evidence `TestOutput/linux-gate-20260928/`); `9b17ed38` cut-1c slices 3-4 (units, game identity, container
  facts, tiling; stored-UV mode, .3DC accessors, texture and BSI header reads; legacy `classic mesh export` byte-identical
  on 25/25 cover GLBs); `0ea6cf04` cut-2 20.0.0.4 `.kf` carry (the five FNV idle animations through the 20.2.0.7 path
  with a synthesized string table; probe and reader share one identity plus the block-0 rule; gate manifests and
  expectations regenerated, every other row byte-identical; gates 1a and 1b PASS at `TestOutput/land-20260928/`).
- **Pre-existing Bucket-B failure, not today's:** `RedguardSceneRetailTests` placement pins (Island 510 -> 509, all
  maps 1,552 -> 1,551, the catacomb sign control) fail identically at `6c5c1fdb`, `7f7f5a44` and the landing
  (`TestOutput/land-20260928/bisect.ps1`); the pins date from the worktree base `c94ac2f1`, the RGM reader is unchanged
  since, and `ISLAND.RGM` is byte-identical between the Steam install and the staged build. One placement fewer with
  `HasMesh` and a non-empty `MeshStem` reaches the assembler; the cause is not found. Backlog.
- **Mailbox:** published 10:55 (pin verified, asks adopted, picker instruction re-raised: the foundation closed
  PID15620 before a destination was selected and has not answered the owner's autonomous-picker instruction).
- **Owner question answered:** the awesome-copilot `winui` plugin is Microsoft's `microsoft/win-dev-skills`, packaged
  for Copilot, Claude Code and Codex; not installed (a user-scope change), assessment given in chat.
- **In flight at the time of writing:** workflow `cut1c-slices56` (the `.3D` reader, then the `.3DC` reader, staged under
  `TestOutput/cut1c-20260928/slice56/`) and the SourceScale adoption agent (`TestOutput/billboards-sourcescale-20260928/`).

## 4. Next steps, in order

1. Land slices 5-6 (build, the Modeling/XnGine/Redguard suites with Bucket-B on, the A6/A7 oracle rows, commit), then
   the SourceScale adoption (build, billboard tests, gates 1a/1b), then slice 7 (typed palettes on SA1), 8 (workflow
   plumbing and archive naming), 9 (gate-1c harness), 11 (the enumerated gate allowance), 13 (gate 1c).
2. Cut 2 remainder: Starfield `.mesh`, Shadowkey, `mesh census`, the 3XDR mip-chain decode, WinUI ExternalToolsSettings,
   D3D12 renderer admission, 20.0.0.4 scene graphs, cameras/lights/markers.
3. Phase F items 3-7 (shell/context/status, TB1-TB6, rendering/capture, records/dialogue, quality: the four remaining
   layering crossings as design decisions; the Redguard placement pins above).
4. Foundation-owned, tracked in its mailbox section: the picker route (owner instruction), the GLB DrawnWinding
   lowering, the lit single-sided normals row, the billboard evidence-text cost.

## 5. Standing rules that bit today

- The machine is shared and often has under 4 GiB free. Builds and tests wait at the 4 GiB gate, Blender at 6 GiB, and
  the BMT executable's own memory policy refuses items below 4 GiB, so gate production must wait for headroom first.
- Agents never build, test or launch Blender. The main session does, one process at a time.
- SharpGLTF's cubic curve sampler ignores key intervals; never use it as a glTF oracle for CUBICSPLINE.
- Re-read the whole foundation section before publishing to the mailbox.

## 6. Evidence (gitignored `TestOutput/`, local to this worktree)

- `pin-dacc231-20260925/`: the pin chain, the gate receipt and the smoke run.
- `sa1-clock-lowering-20260925/`: SA1 build and test runs 1 to 9.
- `cut1b-re-20260925/RE-24/`: the RE-24 answer, verification and receipts.
- `cut1b-slice2-20260925/`: the slice-2 brief and its build/test run.
- `gate1a-blender-20260925/`: the Blender smoke and full runs and their receipts.
- `gate1a-blender-dev/`: the harness self-checks.
