<!-- Implementation plan for design row 13, BMT side (cut 1b). Drafted 2026-09-25 by a read-only planning pass over the worktree at 7124ba12 (Shared pin 66bf702). Revised the same day after review: twelve findings verified, corrections applied, refuted parts recorded. The design (docs/design/model-document-design-20260923.md) stays authoritative. -->

# Cut-1b NIF animation reader: implementation plan

This pass was read-only. Nothing was built, tested, launched or edited. The only exception is a set of read-only Python measurements, run one process at a time, over the 19 in-scope archives under Sample/Builds. They use the committed independent probe: `tools/scripts/nif_feature_probe.py` at f3ecc8f2 (SHA-256 `ab6ce842...d4c3`, `scope='cut1b'`, `payloads=True`), one record per distinct SHA-256.

**Path prefixes**

| Prefix | Location |
|---|---|
| `BMT/` | `src/BethesdaMultitool/` |
| `T/` | `tests/BethesdaMultitool.Tests/` |
| `A/` | `BMT/Core/Formats/Nif/Rendering/Animation/` |
| `C/` | `shared/Multitool.Shared/src/Slfx77.Multitool.Core/Models/` (at 66bf702) |
| `M/` | `.../Slfx77.Multitool.Media/Models/` |
| `B/` | `.../Slfx77.Multitool.Media.Blender/` |
| `SCR/` | `C:/Users/mmc99/AppData/Local/Temp/claude/c--dev-Multitool-BethesdaMultitool/0260887b-8cfa-44d9-9419-927f83483067/scratchpad/` (the receipts; copy them into `TestOutput/cut1b-plan-20260925/` before they are cited in a commit) |

**Labels**

| Label | Meaning |
|---|---|
| [M] | Measured here, or in a named receipt |
| [S] | A Shared 66bf702 contract fact, cited |
| [I] | Inferred from BMT code or data |
| [A] | Assumed |
| [R] | Recalled, not read |

---

## Decisions this plan asks the owner for

| # | Decision | Recommendation |
|---|---|---|
| D1 | **GLB refuses every clock, so typed clips must not reach NifModelReader before SA1.** Every sequence (13,280) and every embedded controller (67,078) carries a clock [M]. The GLB writer turns any clip or track clock into a limitation and refuses the whole document (M/ModelGltfAnimationMetadata.cs:26, 31-32, 78-84; M/ModelGlbPlan.cs:53). Typing clips at the 66bf702 pin would turn every animated NIF's GLB conversion into Unsupported. Gate 1a passes these files today because animation is NativeOnly. The owner's standing ruling rules out an option or flag that gates a feature decision ("switch the code over and let me test", restated 2026-08-11). | **Land SA1 first, then switch the reader over in one slice (slice 10), with no option.** Bethesda may make the Shared change itself and announce it in the mailbox (owner ruling 2026-09-23). Until slice 10, slices 1-9 build the animation stage as components that tests call directly. NifModelReader does not call them, so shipped behavior does not change. Alternative for the owner: switch over at slice 4 without SA1 and accept that `mesh convert --format glb` refuses every animated NIF until SA1 lands (gate 1a's GLB hop then regresses on those items); the owner tests the switched build. Rejected: an app option or CLI switch (`bmt.nif.animation=typed\|native`, `--animation`) that chooses typed or native output. |
| D2 | **Non-unit rotation keys.** 254,694 of 3,979,629 retail rotation keys fail Shared's 1e-4 unit check on the squared length: 44,669 groups in 4,432 files, maximum deviation 2.64e-3 [M] (C/SceneAnimationValidation.cs:125-127, 174-184). The two retail rotation controls first named for the 170-degree and RE-17 checks fall in this class (section 3). | Keep those interpolators NativeOnly and raise SA2. Do not normalize in the reader: the engine's handling is RE-17. Classify with Shared's own Float32 expression (IsUnitRotation), not a double re-implementation. |
| D3 | **Partial clips.** | A transform track on a present target that the vocabulary cannot express (Euler, non-unit, quaternion TBC, component TBC awaiting RE-19, Quadratic translation or scale awaiting RE-18 (D9), duplicate target) keeps the **whole sequence** NativeOnly, so no clip carries a partial pose. A target absent from the document (the '##' weapon nodes a skeleton lacks) makes only that track NativeOnly, with a diagnostic. Property, visibility and particle blocks never block a clip. |
| D4 | **A `.kf` with no skeleton.** The design wants invented nodes classed Degraded (design:163, 868), but no Shared field tells a writer that a rest pose is invented. | Unsupported ('no skeleton found; pass --skeleton') until SA8. 316 FNV PC `.kf` have no ancestor skeleton in FNV's archives; they are FO3 DLC creature folders [M]. |
| D5 | **Decoder route.** The design extends A/NifKeyGroupReader before reuse (design:163, 721). | Use schema-driven views over the cut-1a `NifBlockDecoder` (raw float bits, strict decode, Latin-1 bytes). The runtime reader stays with the renderer and serves only as hop A3-anim's cross-check. Extending it becomes an optional renderer change. |
| D6 | **Inactive embedded controllers.** 3,546 have flags bit 3 clear: 3,360 not manager-controlled, 186 manager-controlled [M]. | Exclude them from the `(controllers)` clip as NativeOnly 'inactive controller' until RE-22 says otherwise. Manager-controlled controllers (28,699) bind only through sequences. |
| D7 | **Duplicate targets.** 41 sequences name one node twice under NiTransformController: 38 with overlapping channels, 3 with both empty [M]. Shared rejects a repeated (node, property) (C/SceneAnimationValidation.cs:49). No node is targeted by two active, non-manager-controlled embedded NiTransformControllers, so the `(controllers)` clip never repeats a target [M review_ctlclip]. | Keep the whole sequence NativeOnly until RE-21. |
| D8 | **TBC key groups.** 2,297 in all [M]: quaternion rotation 901 (refused by Shared, SA6), Euler axes 477 (no Shared form, SA3; 475 multi-key and both single-key groups), translation 448, scale 349, NiFloatData 122. Component TBC on transform channels is 797 groups (translation 448, scale 349), all multi-key, and Shared requires explicit endpoint tangents for every multi-key TBC track (C/SceneTbcTrackValidation.cs:35-36). The 122 NiFloatData groups type only through their consumer (a property track, SA4, or a morph weight). | NativeOnly 'TBC endpoint rule not established' until RE-19. No single-key TBC group is typable: both are Euler axes. |
| D9 | **Hermite orientation and units.** Of 266,184 Quadratic groups, 159,735 have Forward and Backward bits that differ on some key, but 107,385 of those differ only in the sign of zero [M review_tangent_numeric]. 52,350 multi-key groups differ numerically: Euler axes 42,062 (SA3), NiFloatData 6,477 and NiPosData 1,556 (property tracks, SA4), scale 1,980, translation 275. Only **2,255 groups** (scale 1,980, translation 275) can discriminate the orientation on a channel this reader types. **Data evidence** [M review2_hermite_kb]: at interior keys with unequal intervals, Forward/Backward equals dt_prev/dt_next on 1,386 of 1,386 translation and 1,524 of 2,389 scale components, and the reverse ratio on 0 of either (Euler axes 28,558 against 32; NiFloatData 3,472 against 3; NiPosData 8,136 against 0). The full zero-parameter Kochanek-Bartels magnitude in normalized-segment units matches Forward-as-incoming on 634 translation and 59 scale components and the reverse on none. Per-second tangents would make Forward equal Backward at such keys, so the exporter wrote Forward as the incoming tangent in normalized-segment units: BMT's convention (A/NifQuadraticVectorCurve.cs:5-18). | Map [Forward, Value, Backward] to Shared's [incoming, value, outgoing] with no time scaling. Keep RE-18 (decompile-first) as the gate for the engine evaluator, with this data result as its acceptance test. If the owner accepts the data alone, Quadratic translation and scale type in slice 2 without waiting. |
| D10 | **Controller clocks inside a sequence clip.** | Apply the clip clock only [I]; keep the controller's own clock fields native. Confirm with RE-22. |
| D11 | **Priority, weight, accumulation root.** Shared has no field for them. | Keep them in native state and in clip ExtrasJson. Both writers drop extras (M/ModelGltfLowering.cs:246-258; B/BlendAdmission.cs:791-842). Raise SA7. |
| D12 | **What a `.kf` document holds.** | Skeleton nodes only, with no skeleton geometry or collision, plus a skeleton provenance row. Match target names on exact bytes, case-sensitive; case-insensitive matching rescues 0 names [M]. A name repeated in the skeleton (2 FO3 skeletons have duplicates [M]) makes the track NativeOnly 'ambiguous target'. |
| D13 | **Pin.** | Bump to the tip of canonical main (owner policy), at least 03ac991. That docs-only commit corrects the 66bf702 mailbox, which says GLB refuses B-splines while the code converts them. |
| D14 | **A `.kf` whose sequences all stay NativeOnly.** Under D12 its document is the skeleton alone, and no writer refuses it: the GLB writer refuses only on limitations (M/ModelGlbPlan.cs:53), and a node-only document raises none. Up to 5,176 of 5,760 `.kf` carry a blocked transform interpolator at the pinned Shared with RE-18 unsettled [M review_typable_rel18; an upper bound, because D3 exempts tracks on absent targets]. A single-sequence file among them types no clip. | The reader answers Unsupported ('no clip expressible at the pinned Shared: <first blocking reasons>') for such a `.kf`, instead of a skeleton-only document. `.kf` admission lands in slice 10 with the switch-over, so no build ever exports a node-only `.kf` GLB. |

### Owner rulings (2026-09-25), superseding the recommendations above where they differ

| # | Ruling |
|---|---|
| D1 | As recommended: SA1 first, then one switch-over slice with no option. |
| D2 | The alternative: the reader normalizes non-unit rotation keys, and RE-17 is done as part of it so the normalization matches what the engine does. SA2 is then needed only if RE-17 shows the engine does not normalize. |
| D3 | Neither whole-sequence NativeOnly nor partial clips: **close the gaps first**. Every vocabulary gap that would block a clip (Euler SA3, property and visibility tracks SA4, per-target morphs SA5, quaternion TBC SA6, component TBC via RE-19, Hermite via RE-18, duplicate targets via RE-21) is closed before the switch-over. |
| D4 | SA8 first, then re-evaluate the skeleton-less `.kf`. |
| D5 | **The design route**: extend the runtime `NifKeyGroupReader` and its siblings so one decode path serves the renderer and the ModelDocument reader. Reducing total code is the point of Shared. The independent oracle stays the Python probe's payloads. |
| D6 | RE-22 first. |
| D7 | RE-21 first. |
| D8 | RE-19 first. |
| D9 | RE-18 first. |
| D10 | RE-22 first. |
| D11 | Not the lossy route (extras that both writers drop). Typed fields first (SA7), so priority, weight and accumulation root survive export. |
| D12 | As recommended. |
| D13 | As recommended (latest main). |
| D14 | As recommended. |

Consequence for the slice order: the RE items (RE-17 to RE-23) come first, then the Shared asks the rulings make prerequisites (SA1, SA3 to SA8, and SA2 only if RE-17 requires it), then the reader slices, rebuilt on the extended runtime readers (D5), then the switch-over.

## Receipts (SCR/, read-only, scripts beside their outputs)

| Script | Output and what it holds |
|---|---|
| `cut1b_contract_fit.py` | `.out.json`: strict key times, negative times, unit quaternions, adjacent-key angles, TBC key counts, B-spline handle and half-range signs, controller flags and clocks, duplicate targets, embedded and in-sequence morph shapes, target orthonormality |
| `cut1b_contract_fit2.py` | `.out.json`: non-unit quaternion histogram by key type, extension and group; duplicate-target shapes; morph shapes without target 0; active and manager cross-tab; skeleton orthonormality; text-key bytes |
| `cut1b_contract_fit3.py` | `.out.json`: whether duplicates' channels overlap; in-sequence morph groups without Base; every adjacent pair of 170 degrees or more |
| `cut1b_tangent_discrim.py` | `.out.json`: Quadratic groups with Forward != Backward (bits); TBC groups with continuity != bias |
| `cut1b_typable.py` | `.out.json`: each transform interpolator's first blocking class under 66bf702 |
| `cut1b_skeleton_walkup.py` | `.out.json`; `kf_noskel.py` (stdout): walk-up resolution per group |
| `cut1b_cover_seed.py` | `.out.json`: a greedy plus reverse-delete seed cover per version key, item populations, named controls with SHA-256 |
| `nlerp_dev.py` | The worst deviation between slerp and nlerp at 90, 150, 170 and 174 degrees |
| Review: `review_one.py`, `review_quat_ctrl.py`, `review_controls.py` | `.out.json`: per-group unit status and angles of the first named controls; unit and non-unit split of dot < 0 and 170-degree pairs; block lists of every named control |
| Review: `review_tangent_numeric.py` | `.out.json`: Quadratic groups whose tangents differ only by signed zero, and multi-key groups that differ numerically, per channel |
| Review: `review_typable_re18.py` | `review_typable_rel18.out.json`: `cut1b_typable.py` plus one class, Quadratic translation or scale (diffed: one added line) |
| Review: `review_ctlclip.py`, `review_quat_conj.py`, `review_det.py`, `review_180.py` | `.out.json`: repeated `(controllers)` targets (0) and morph Base weights; key 0 against the skeleton's R and R^T separately; det < 0 targets (0); the 180-degree pair's bits |
| Revision: `review2_verify.py` | `.out.json`: clip-level typability; replacement 170-degree and dot < 0 controls in clips that type whole with a resolved target; Hermite controls on typed channels; morph Relative Targets and Base keys; discriminators inside the seed-cover files |
| Revision: `review2_named.py` | `.out.json`: each replacement control re-read from its archive (size, SHA-256), the unit check in double and in Shared's Float32 expression, and the discriminating keys |
| Revision: `review2_hermite_kb.py` | `.out.json`: Forward/Backward against the Kochanek-Bartels interval ratio at interior Quadratic keys (the D9 data evidence) |
| Revision: `review2_cover_seed.py` | `.out.json`: the seed cover re-run with four discriminator item families added |

Every run covered 65,764 distinct files and took 46 to 67 s.

---

## 0 Baseline facts

### 0.1 Worktree and reader today
- **Pin.** HEAD 7124ba12 pins Shared 66bf702.
- **Scope and declines.** The cut-1a reader declines:
  - BS 24, 25, 27, 28, 30, 31 and 33 (BMT/Core/Modeling/Nif/NifModelProbe.cs:51, 124)
  - `.kf` roots (NifModelProbe.cs:86-92, 145-151; NifModelReader.cs:131, 146)
- **Animation blocks are NativeOnly.** Every controller, interpolator and key-data block gets the reason 'later-cut(1b): animation' (NifModelCoverage.cs:145, 217, 434-436). Particle controllers get 'particles' first (:151, :363).
- **No animation stage.** `ModelDocument` is built without animations (NifModelReader.cs:202). The row budget has no animation term (:175).
- **App options are a free-form bag** (C/Sources/ModelReadContext.cs:32, 47). A new `bmt.skeleton` key therefore needs no Shared change. Existing keys are at BMT/Core/Modeling/BethesdaModelRegistration.cs:13-29.
- **The probe and the cover tools.** `nif_feature_probe.py` has `--scope cut1b` and `--payloads` (f3ecc8f2). `nif_cover.py` and `nif_cover_expectations.py` have neither yet (grep).

### 0.2 What Shared 66bf702 offers a reader [S]

**Clip**
- `SceneAnimation(name, morphTracks, extrasJson, transformTracks, matrixTracks, durationSeconds, transformBases, clock, timing, events)` (C/SceneAnimation.cs:23-27).
- A clip needs channels, events, a clock, or timed flipbook content (C/SceneAnimationValidation.cs:24-27).

**Clock**
- Constructor: `SceneAnimationClock(frequency, phase, start, stop, cycle)`. All values must be finite and stop >= start; zero or negative frequency, a negative start and a zero length are all accepted (C/SceneAnimationClock.cs:16-24).
- A zero-length interval maps to its start (:54). Loop excludes stop (:59-63).
- Composition: the clip clock maps first, then the track clock (C/ScenePoseEvaluator.cs:351-358).
- The NIF sentinel (+FLT_MAX, -FLT_MAX) cannot be constructed. The coordination document says: absent clock, fields kept native (docs/model-export-coordination.md:34-37).

**Timing and events**
- `SceneAnimationTiming(fps?, rawRate?, unit?, provenance, evidence)` never retimes keys.
- `SceneAnimationEvent(time, exact UTF-16 text)` keeps order and duplicates (C/SceneAnimationEvent.cs:4-20).

**Transform track** (C/SceneTransformTrack.cs:18-22)
- Signature: `SceneTransformTrack(node, property, times, values, interpolation, clock, state, staticValue, spline, tbcParameters, tbcEndpoints)`.
- Rotation is XYZW. Cubic values are laid out [in, value, out].
- **States** (C/SceneTransformChannelValidation.cs:21-30): NotDriven carries nothing; Constant needs the full static value; Keyed may keep a static fallback.
- **TBC rotation is refused** (:34-35).
- **B-spline:** Keyed with empty key arrays, and `Spline.ComponentCount` equal to the property width (:36-41).

**Key rules** (C/SceneAnimationValidation.cs)
- Times strictly increasing; negative times only under a clock (:166).
- Unit rotation keys within 1e-4 on the squared length, computed in Float32 (`MathF.Abs(value.LengthSquared() - 1) <= 0.0001f`, :125-127).
- One (node, property) per clip (:49).
- A driven node needs LocalTrs (:53-54).

**Sampling** (C/ScenePoseChannel.cs)
- Linear rotation uses float `Quaternion.Slerp`, documented as shortest-path, and the result is renormalized (:138, :153, :161-164).
- Component channels accumulate in double and round to Float32 once per component (:54, :133). TBC tangents are rounded to Float32 before the Hermite (C/SceneTbcTangents.cs:104-105). Int16 B-spline controls decode in Float32 (C/SceneBSplineCurve.cs:191).

**Interpolation kinds:** Linear, Step, CubicSpline (per second), BSpline, Hermite (normalized segment), Tbc (C/SceneInterpolation.cs:4-23).

**TBC:** named `SceneTbcParameters(Tension, Continuity, Bias)`. A multi-key track needs `SceneTbcEndpointTangents` (C/SceneTbcTrackValidation.cs:28-38).

**B-spline curve**
- `SceneBSplineCurve`: degree 0 to 3, 1 to 4 components, start < stop strictly.
- Two control forms: Float32, or Int16 with one bias and multiplier, decoded as `bias + (s/32767f)*multiplier`. The multiplier's sign is left to the reader (C/SceneBSplineCurve.cs:26-81).

**Morph track:** one Times vector and one interpolation for all targets. TargetCount must equal the mesh's target count. There is no state and no spline (C/SceneMorphTrack.cs:15-17; C/SceneAnimationValidation.cs:31-42).

**Not in Shared:** property tracks, node visibility, particle tracks, per-axis Euler, quaternion TBC or Squad, Path and LookAt, and priority or weight fields (survey 1).

**Writers**
- **GLB** converts Hermite and TBC to CUBICSPLINE, constants to STEP, and B-splines by degree or by bounded adaptive quaternion keys. It refuses clocks, matrix tracks, flipbooks and malformed UTF-16, and drops clip extras (survey 1: M/ModelGltfSourceCurves.cs, ModelGltfBSpline.cs, ModelGltfQuaternionSpline.cs, ModelGltfAdmission.cs:147-190). A clip duration that differs from its last standard key also refuses (ModelGltfAdmission.cs:176-185).
- **Blender** drops every clip and declaration as later-cut(1b) (B/BlendAdmission.cs:791-842). Its importer refuses packages that carry animations (B/Python/import_model.py:46-48).

### 0.3 How the retail corpus fits the 66bf702 contract [M]

| Contract rule | Retail population | Consequence |
|---|---|---|
| Strictly increasing times (C/SceneAnimationValidation.cs:166) | 0 groups with equal consecutive times. The probe only checks non-decreasing times (nif_feature_probe.py:707-718). | A Bucket-B assertion only |
| Negative times need a clock (:166) | 12,950 groups start below 0; all sit under a sequence or controller clock | Always emit the clock |
| Unit rotation keys (:125-127, Float32) | 254,694 of 3,979,629 keys fail: 49 in (1e-4, 2e-4], 350 in (2e-4, 5e-4], 160,586 in (5e-4, 1e-3], 93,709 above 1e-3; maximum 2.636e-3 (measured in double). Groups: LINEAR 44,487, TBC 182; 43,706 in `.kf`. Keys per group: FNV PC 209,801, FO3 44,013, X360 635, PS3 245. The split matters for controls: of 6,091 dot < 0 LINEAR pairs, 3,230 sit in unit groups and 2,861 in non-unit ones; of 83 pairs at 170 degrees or more, 73 and 10 (review_quat_ctrl). | D2, SA2. Classify with Shared's Float32 expression so keys near the boundary land where Shared puts them. |
| TBC rotation refused | 901 quaternion TBC groups | SA6 |
| Multi-key TBC needs endpoints | 2,297 TBC groups: rotation 901, Euler axes 477 (2 single-key), translation 448, scale 349, NiFloatData 122. Component TBC on transform channels: 797, all multi-key. Only 247 groups carry any nonzero parameter; 79 have continuity != bias (64 translation, 15 rotation). | D8, RE-19; the TBC-order control population |
| B-spline start < stop | All 123,655 B-spline interpolators start at 0 with stop > 0 (survey 1) | None |
| B-spline width equals the property width | NIF scale has 1 component; rotation controls are stored W, X, Y, Z | Replicate scale 3 times; permute to X, Y, Z, W (both exact) |
| Multiplier sign belongs to the reader | Every compact half range is > 0. Keyed / absent handles: rotation 117,462 / 5,331; translation 19,997 / 102,796; scale 469 / 122,324. | Keep BMT's rule (A/NifBsplineTransformReader.cs:339) |
| One (node, property) per clip | 41 sequences repeat a target; 0 repeated targets in `(controllers)` clips | D7 |
| A driven node needs LocalTrs | All targets are orthonormal: 19,269 embedded and 25,724 in-file sequence targets within 1e-5, and 184 `skeleton.nif` (8,882 nodes) within 1e-6; none has det < 0 (review_det). 0 in-file names are ambiguous. | The 1a TRS rule gives every target TRS |
| Morph track: one shared time vector | Embedded: 216 controllers, of which 113 are manager-blend and 103 are keyed. With target 0 (Base) excluded, 60 fit and 38 differ in times. Of the 43 that do not fit, 5 include constant targets. In sequences: 582 node groups, of which 82 hold only Base, 6 are all constant, 164 fit, 199 are keyed plus constant, 95 differ and 36 use B-splines. All 216 NiMorphData read by embedded controllers carry Relative Targets = 1, and all 103 non-blend Base weights are keyed at 0.0 on every key (review2_verify). | SA5; section 1.8, RE-23 |
| Clock stop >= start | 14,623 sentinels (all curve-less), 0 other reversed | Sentinel maps to a null clock |
| Exact event text | 48,565 text keys; 0 contain a byte >= 0x80; 911 contain CR or LF; 115 are empty | Latin-1 decoding is bijective and exact |
| Linear rotation sampled with shortest-path `Quaternion.Slerp` [S] (C/ScenePoseChannel.cs:138, 153) | 3,846,418 adjacent LINEAR pairs: 6,091 with dot < 0 (1,404 files); 2,973 at 90 degrees or more, 166 at 150 or more, 83 at 170 or more (37 files). In clips that type whole (conservatively: every transform track typable, RE-18 blocking, duplicates blocking) with a uniquely resolved target: 58 pairs at 170 degrees or more in 22 files, 41 of them in [170, 179) in 9 files; 292 dot < 0 pairs in 51 `.kf`, none in a `.nif` (review2_verify). | The RE-17 and 170-degree control populations (section 3) |
| Euler has no form | 67,206 rotation blocks (needs doc) | SA3 |
| Hermite orientation and units (a BMT convention, not a Shared rule) | 159,735 of 266,184 Quadratic groups have Forward != Backward bits, but 107,385 differ only in the sign of zero. 2,255 multi-key groups on typed channels differ numerically (scale 1,980, translation 275). Interval-ratio evidence favors Forward as incoming in normalized-segment units (D9). | D9, RE-18 |

**What would type today under D3, with no Shared change** (each interpolator counted in its first blocking class; review_typable_rel18 adds Quadratic translation or scale as its own class, since D9 blocks Hermite until RE-18):

| | Typable, RE-18 unsettled | Typable, Hermite admitted | quadraticComponent | nonUnitQuat | Euler | tbcQuat | bsRotAccum | tbcComponent | Files fully typable (RE-18 unsettled / Hermite admitted) |
|---|---|---|---|---|---|---|---|---|---|
| `.kf` | 226,861 of 291,301 | 229,388 | 2,527 | 43,668 | 17,966 | 236 | 33 | 10 | 584 / 591 of 5,760 |
| `.nif` | 11,772 of 67,474 | 13,827 | 2,055 | 819 | 52,138 | 665 | 0 | 25 | 717 / 1,061 of 4,941 |

SA2 and SA3 are therefore what makes cut 1b useful; settling RE-18 (D9) adds about a third to the fully typable `.nif` files. The clock rows are what make GLB possible at all.

**Skeleton walk-up** (the design rule; per group, all the group's archives form one namespace):

| Group | `.kf` with all targets resolved | Some targets missed | No skeleton found |
|---|---|---|---|
| FNV PC | 2,882 | 1,498 | 282 |
| FO3 | 2,664 | 843 | 0 |
| X360 | 2,792 | 1,183 | 262 |
| PS3 | 2,786 | 1,181 | 261 |

- **The missed names are attachment nodes.** Almost all start with '##', such as `##NifRound` (157), `##SpeedLoader` (147) and `##Trigger` (139). They live in the equipped weapon model, not in the skeleton.
- **`.kf` without an ancestor skeleton (path level):** 316 FNV PC files: `dlc04` 118, `dlc05` 89, `nvdlc03` 49, `creatures/yaoguai` 30, `dlcanch` 25, `update` 4, `nvdlc01` 1.
- **`.nif` targets:** in-file names alone leave 101 FNV PC and 791 X360 `.nif` unresolved. X360 stores null names, so these resolve through the manager's `NiDefaultAVObjectPalette` [I, verified in slice 3].

### 0.4 Reuse audit of BMT runtime animation code (survey 2; verdicts under D5)

| Component | Verdict | Reason |
|---|---|---|
| A/NifKeyGroupReader | Layout reference only | Strides are right (:13-17). It drops scalar tangents, TBC and each Euler axis's own key type (:81 `out _`). |
| A/NifBsplineTransformReader | Formula reference and A3 cross-check | Its layout, handle sentinel and dequantization (:543) match Shared's Int16 decode. It keeps only floats and rejects negative multipliers (:339). |
| A/NifOpenUniformCubicBspline | Basis reference (Shared cites it) | Degree 3 only |
| A/NifQuadraticVectorCurve, NifGeometryMorphData | Hermite orientation reference | Earlier key's Backward is outgoing, later key's Forward incoming, in normalized-segment units (A/NifQuadraticVectorCurve.cs:5-18); the retail interval ratios agree (D9), and the engine side is RE-18. |
| A/NifTimeControllerReader | Layout reference | 26-byte header, cycle bits 1-2, active bit 3 |
| A/NifControllerSequenceNameTrackReader | Layout reference and A3 cross-check | It never reads priority, controller-type or controller-ID strings, or weight. It rejects frequency < 0 and zero-length clips (:124-131) and silently skips non-transform interpolators (:190-198). |
| A/NifTextKeyReader | Do not reuse | ASCII decoding, trimming, an unstable sort, and rejection of empty keys (:98-109, 199-221) |
| A/NifModelFamilyAnimationResolver | Ancestor enumeration reusable; rule not | It already walks from a model's folder upward to the nearest ancestor `skeleton.nif` (:131-161, EnumerateAncestorDirectories :299-311), the direction a `.kf` needs. But it skips a skeleton unless every required skin-bone name is present (:144, :270-278) and compares names case-insensitively (:81, :276). Cut 1b needs nearest-wins with no compatibility gate (the '##' attachment names would reject every weapon `.kf`'s skeleton) and exact bytes (D12). |
| Rig builder, clip selector, baker, viewer adapters, snapshot readers | Do not reuse | Renderer policy: idle selection, retiming to zero, 30 Hz baking, wrong strides |
| NifParticleSystemParser key readers | Reference | The only BMT reader that already keeps TBC in engine order (:762-770) |

---

## 1 Mapping onto the G13 types

### 1.1 Clocks and timing

| Source | Shared | Label |
|---|---|---|
| NiControllerSequence: frequency, start, stop, cycle (LOOP 0, REVERSE 1, CLAMP 2) | Clip `SceneAnimationClock(frequency, 0, start, stop, cycle)`. Sequences have no phase field (probe p_sequence, nif_feature_probe.py:1845-1880); the cycle ordinals are identical. | [M] layout; [S] |
| Measured values | Frequency 1.0 on all 13,280; start >= 0; 0 zero-length; 0 reversed | [M] |
| Embedded NiTimeController (26-byte header; cycle = flags bits 1-2; active = bit 3) | Per-track clock (frequency, phase, start, stop, cycle) | [M] layout (A/NifTimeControllerReader.cs:12-26) |
| Sentinel (+FLT_MAX, -FLT_MAX) | Null clock, fields native. All 14,623 are curve-less, so they never reach a track. | [M]; [S] coordination doc |
| Zero-length (7,542), negative start (6,099), phase != 0 (2,260) | Kept as authored | [M]; [S] |
| Controller clock of a sequence-controlled block | Not applied (D10) | [I] RE-22 |
| Timing | `SceneAnimationTiming(null, null, null, Authored, 'NIF key and clock times are float seconds')`. No FPS is invented. | [A] wording |

### 1.2 Curves per key group

| NIF key type and value | Shared form | Label |
|---|---|---|
| LINEAR (1) float or Vector3 | Linear. Scale float s becomes (s, s, s). | [S] |
| LINEAR quaternion | Linear; W, X, Y, Z becomes X, Y, Z, W, with no conjugation. Component order: 55,501 vs 1,794 closer (review_quat_order, which takes the minimum over R and R^T and so settles order only). Conjugation: key 0's standard matrix is closer to the skeleton's stored R than to R^T on 48,442 vs 7,474 of 56,225 pairs, within 1 degree on 5,844 vs 667 (review_quat_conj); the 1a rest quaternion is the standard quaternion of the stored R (NifModelTransform.cs:10-19), so key and rest use one convention. | [M] |
| QUADRATIC (2) float or Vector3 | Hermite, values [Forward, Value, Backward] as [in, value, out], no time scaling. The interval ratios support this orientation and unit (D9); typing waits on RE-18 unless the owner accepts the data. | [M] data; engine [I] until RE-18 |
| QUADRATIC quaternion | 0 groups. NativeOnly (fail-closed), pinned by a synthetic test. | [M] |
| TBC (3) float or Vector3 | Tbc with `SceneTbcParameters(Tension = float 0, Continuity = float 1, Bias = float 2)`. This is the TBC order rule: nif.xml's t, b, c labels are wrong for the second and third floats (tbc_engine_oracle.json; needs doc:122-132). Multi-key groups wait on RE-19 (D8). | [M] order |
| TBC quaternion | NativeOnly (SA6) | [S] refusal |
| XYZ_ROTATION (4) | NativeOnly (SA3) | [S] no form |
| CONST (5) | Step | [S] |
| Keys with a squared length off by more than 1e-4 (Float32) | The interpolator stays NativeOnly (D2, SA2) | [M] |

### 1.3 Channel states (NiTransformInterpolator and the B-spline statics)

- **Keys present** (a group with keys, or a B-spline handle other than 0xFFFF): Keyed. A valid static value is kept as StaticValue fallback (2,487 slots [M]).
- **No keys, valid static:** Constant(static) (264,809 slots [M]).
- **No keys, static equal to #INV_FLT# (bits 0xFF7FFFFF):** NotDriven (376,316 slots [M]).
- **Mixed partial sentinel components:** throw, as BMT does (A/NifBsplineTransformReader.cs:228-238). Expected 0; asserted in Bucket-B.
- **Static rotations** are permuted to X, Y, Z, W. A zero quaternion throws in Shared (C/SceneTransformChannelValidation.cs:61) and is asserted absent. Static scale becomes (s, s, s).

### 1.4 B-splines
- **NiBSplineCompTransformInterpolator**, per driven channel:
  - `SceneBSplineCurve(3, width, start, stop, shorts, bias = Offset, multiplier = Half Range)`.
  - The shorts are `NiBSplineData.CompactControlPoints[handle .. handle + n*c)`, with `n = NiBSplineBasisData.NumControlPoints`.
  - Rotation shorts are permuted per control, W, X, Y, Z to X, Y, Z, W. Scale shorts are replicated 3 times.
  - Degree 3 [I: BMT basis and Shared comment]. Admission: n >= 4 (BMT).
- **NiBSplineTransformInterpolator** (148, uncompressed): the Float32 form, sliced from `FloatControlPoints` the same way.
- **Float and Point3 compressed B-splines** (275 and 439): only through SA4 and SA5.

### 1.5 Events
- **Source:** the sequence's NiTextKeyExtraData keys, in file order, with no sort, trim or drop. Each becomes `SceneAnimationEvent(time bits, Latin-1 decode of the raw string bytes)`. The raw bytes stay native.
- **Anim notes:** BSAnimNotes and BSAnimNote stay NativeOnly. 130 files carry them in the widened walk [M]; the needs doc's 13 files predate the widening.
- **Controls:**
  - CRLF: `meshes/characters/_1stperson/swimmtleft.kf`, SHA `6e41ad519e8fe9e2ad36a5a2073d38c7d9b6e0a24138e054319cb303b1431980`
  - Two events in one key: `meshes/creatures/libertyprime/mtturnleft.kf`, SHA `c7a6e6af74dab06992f7f9bf57d49d2bcc414987ba8e165620891b3b21e03f68`
  - Empty: `meshes/creatures/nvsecuritron/2hhholster.kf`, SHA `bd373a1d1e8cb96e6db1ded75d47cc9c3c9d992b455578348a2841aa19ffb5db`

  All three are in FNV PC Fallout - Meshes.bsa.

### 1.6 Clips
- **One clip per NiControllerSequence.**
  - Order: the NiControllerManager's sequence list for a `.nif` (1,913 multi-sequence manager `.nif` [needs doc]); footer order for a `.kf` (31 multi-sequence `.kf`).
  - Name: the sequence name's Latin-1 text.
  - Tracks come from the controlled blocks whose controller type is NiTransformController (and later the SA4/SA5 kinds).
  - `durationSeconds` stays null: the clip clock carries start and stop, and a duration that differs from the last standard key would add its own GLB refusal (M/ModelGltfAdmission.cs:176-185) [I].
- **One `(controllers)` clip** per `.nif` holds the transform controllers that are active and not manager-controlled (35,019 controllers of all types qualify [M]; transform ones become tracks). It has no clip clock; each track carries its own clock. It comes after the sequences.
- **Stays native (D11):** weight (1.0 on all [M]), accumulation root name (on all 13,280 [M]), manager ref, text-key ref, and each controlled block's priority, property type, controller type, controller ID and interpolator ID strings. They are also copied into clip ExtrasJson.
- **Manager-side state:** NiBlend*Interpolators (28,536) and NiMultiTargetTransformController (3,605) are native. Each sequence's own interpolator is its track (Appendix B).

### 1.7 Targets and the `.kf` skeleton

**`.nif` targets**
1. The manager's NiDefaultAVObjectPalette, name to block. The engine binds this way [R]; the 1a palette index must gain a name-to-block map.
2. Then string-table names, exact bytes.
3. Then one track per occurrence of the target block.

**`.kf` targets**
- **Where the skeleton comes from:**
  - `bmt.skeleton` (CLI `--skeleton <file>`) wins.
  - Otherwise, walk up from the `.kf`'s virtual path; the nearest ancestor `skeleton.nif` wins, with no compatibility gate. Candidates are resolved through the context's companion resolver, over the data-root VFS (BethesdaTextureCompanions).
  - The upward walk already exists in the renderer's A/NifModelFamilyAnimationResolver.cs:131-161 (ancestor enumeration :299-311), and its enumeration can be reused. Its rule cannot: it skips a skeleton unless every skin-bone name is present (:144, :270-278) and compares names case-insensitively (:81, :276). The rule here (nearest wins, exact bytes) is what `cut1b_skeleton_walkup.py` measured.
- **Reading it:** the 1a node reader reads the skeleton's nodes only (D12). It gets its own read state and a row `bmt.nif.animation.skeleton` holding path, SHA-256, rule, candidates, and matched and unmatched names.
- **Matching:** exact, case-sensitive bytes. A missing target makes the track NativeOnly 'target not in the resolved skeleton (attachment node)' (D3). An ambiguous name gives 'ambiguous target' (D12). With no skeleton, the file is Unsupported (D4). With no typed clip, the file is Unsupported (D14).
- **Declines:** the five FNV 20.0.0.4 `.kf` (eatidle.kf and the others) stay Unsupported 'cut 2' (design:163).

### 1.8 Morph weights
- **Embedded NiGeomMorpherController:** interpolator i drives morph i. Morph 0 is the base, which the 1a reader compares with the stored positions and does not emit as a target (NifModelMorphReader.cs:12-13, :121). Targets 1 and up map to 1a target i-1.
- **The Base weight is treated as having no effect [I], pending RE-23.** All 216 NiMorphData read by embedded controllers carry Relative Targets = 1, so the 1a reader emits morphs 1..n as deltas over the stored positions (NifModelMorphReader.cs:12-13). All 103 non-blend controllers key the Base weight at 0.0 on every key [M review2_verify]; if the engine scaled the base by that weight, every one of those meshes would collapse to the origin while it animates. Its curve stays native with the reason 'Base weight: no effect under relative targets (RE-23)'.
- **In sequences:** the controlled block's Interpolator ID names the frame. 'Base' stays native under the same rule; the other names resolve to targets by Frame Name.
- **One SceneMorphTrack** is emitted only when every non-Base target is keyed with identical time bits and one key type. Otherwise the controller and its interpolators are NativeOnly (SA5).
- **Manager-blend controllers** (113) stay native as blend state.

---

## 2 Coverage and native state

### 2.1 Block table
Typed wins when one data block feeds both typed and native consumers (MergeDispositions). The reader emits no Dropped rows. Until slice 10 wires the stage in, the reader keeps today's 'later-cut(1b)' reasons; the table below is the classification the stage computes and slice 10 switches on.

| Blocks | Disposition | Reason (NativeOnly) or typed target |
|---|---|---|
| NiControllerSequence | Typed when it becomes a clip, else NativeOnly | The blocking reason (D3, D7) |
| NiControllerManager | Typed when at least one of its sequences becomes a clip | 'manager: no clip' |
| NiDefaultAVObjectPalette | Typed when it supplies names or binding | The 1a rule, extended |
| NiTextKeyExtraData | Typed when its sequence becomes a clip | 'text keys outside a clip' |
| NiTransformController | Typed when it yields tracks | 'inactive controller' (D6); 'manager binding: no curve' (null interpolator) |
| NiMultiTargetTransformController | NativeOnly | 'manager binding: no curve' |
| NiTransformInterpolator, NiTransformData, NiKeyframeData, NiBSpline(Comp)TransformInterpolator, NiBSplineData, NiBSplineBasisData | Typed when mapped | 'Euler rotation: no Shared form (SA3)'; 'rotation keys outside Shared unit tolerance (SA2)'; 'TBC rotation refused by Shared (SA6)'; 'TBC endpoint rule not established (RE-19)'; 'Hermite tangent roles awaiting RE-18' (until D9 is settled); 'repeated target in sequence (RE-21)'; 'target not in the resolved skeleton' |
| BSRotAccumTransfInterpolator | NativeOnly | 'rotation-accumulation semantics not established' |
| BSTreadTransfInterpolator, BSTreadTransfController | NativeOnly | 'no Shared form' |
| NiPathInterpolator, NiLookAtInterpolator and their data | NativeOnly | 'Path/LookAt: no Shared constraint' |
| NiBlend*Interpolator | NativeOnly | 'manager blend state (runtime)' |
| NiVisController, NiBool(Timeline)Interpolator, NiBoolData, NiVisData | NativeOnly | 'node visibility: no Shared track (SA4)' |
| NiAlphaController, NiMaterialColorController, BSMaterialEmittanceMultController, NiTextureTransformController, NiUVController, and the NiFloat, NiPoint3 and NiBSplineComp{Float,Point3} interpolators and NiFloat, NiPos, NiColor and NiUV data feeding them | NativeOnly | 'material/texture property track: no Shared vocabulary (SA4)' |
| NiGeomMorpherController and its interpolators and data | Typed when it fits | 'morph weights need per-target times or states (SA5)'; the Base interpolator: 'Base weight: no effect under relative targets (RE-23)' |
| BSRefractionStrengthController, BSRefractionFirePeriodController | NativeOnly | 'refraction: no typed vocabulary (cut 2)' |
| BSFrustumFOVController, NiLightColorController, NiLightDimmerController | NativeOnly | 'later-cut(2): cameras and lights'. This fixes the category order: today they fall to the animation reason (NifModelCoverage.cs:361-369, 432-437). |
| NiFloatExtraDataController | NativeOnly | 'drives extra data, no visual' |
| NiBSBoneLODController | NativeOnly | 'runtime bone LOD' |
| bhkBlendController | NativeOnly | 'Havok (later-cut 2)' (unchanged) |
| NiPSys*Ctlr, BSPSysMultiTargetEmitterCtlr, and the interpolators and data reached only from them | NativeOnly | 'particles' (coordination doc:71-73) |
| BSAnimNotes, BSAnimNote | NativeOnly | 'anim notes: no Shared vocabulary' |

### 2.2 Native state
- **`bmt.nif.animation.clip`** (version 1, target Animation k):
  - Source: the sequence block, or '(controllers)'.
  - Raw name bytes; weight, cycle, frequency, start and stop bits; accumulation root; text-key and anim-note refs.
  - Every controlled block's fields, with raw strings.
  - A per-track map: track ordinal to controlled-block ordinal, interpolator block, channel, state, key type and applied permutation or replication.
  - The NativeOnly tracks, each with its reason.
  - The events' raw bytes.
- **`bmt.nif.animation.skeleton`** (Document).
- **`bmt.nif.block` rows are unchanged.** Arrays longer than 64 elements are summarized there, so key payloads of NativeOnly blocks are exact only at NativeDetail Full, the 1a rule (NifModelNativeValues.cs:11-16).
- **Individual tracks cannot be targeted** (C/SceneElementRef.cs:9-13), so track identity lives in the payload (SA10).

### 2.3 Budgets
- **Row count:** add clips plus 1 (skeleton) to NifModelReader.cs:175 (slice 10).
- **GLB key budget:** the GLB spline budget (2,097,152 derived keys per document) is checked in slice 10 on the largest manifest `.kf`.

---

## 3 Oracles and controls (each control must fail; receipts record that it did)

Every rotation control used before slice 12 (SA2) sits in a unit group, in a clip that types whole under D3, on a uniquely resolved target, and passes Shared's Float32 unit check [M review2_verify, review2_named].

| Hop | Oracle | Control that must fail |
|---|---|---|
| A0-decode (slice 1) | NifBlockDecoder values against the probe payload bits (`cut1b-probe-expectations.jsonl`): every time, value, tangent, TBC triple, B-spline array element, offset and half range, handle, clock field and controlled-block string | (1) TBC order: the second float named Bias per nif.xml (nif.xml:6388-6395), on FNV PC `meshes/characters/_male/sneak2hhattackspin.kf` block 85 (rotation, 41 keys, 26 of them store (0, -1, 0)), the smallest continuity != bias file. A0 compares views with the probe, so the group's NativeOnly status (SA6) does not matter here. No earlier named TBC control can fail this check: all carry zero parameters. (2) Quaternions read as X, Y, Z, W. (3) One compact short changed. (4) Forward and Backward exchanged at decode, on `meshes/creatures/libertyprime/talking.kf` block 2 (NiPosData; key 1 stores Forward -0.0 and Backward +0.0), which only a bit comparison can see. Negative control: the TBC swap on a group with continuity == bias passes, which shows only 79 groups discriminate. |
| A1-anim (slices 4-7, 9, through the stage) | Typed tracks against the expectations, through only the declared conversions: permutation, scale replication, Hermite triple, TBC naming, state rule. Clocks and events bit-exact. | Forward and Backward exchanged in the Hermite triple, once Quadratic types (D9): FO3 `meshes/traps/fxgastrapblast.nif` block 7 (scale, 2 keys: key 0 Forward -0.0 / Backward 13.7186, key 1 Forward 13.7186 / Backward -0.0; as mapped the segment is a straight line, exchanged it becomes an ease), and FNV PS3 `meshes/dlcpitt/effects/dlcpittfireburst01.nif` block 31 (translation; key 1's pair 116.72 / 44.74 is the Kochanek-Bartels pair for intervals 2.0 and 0.767). The sentinel static typed as Constant; the controller clock applied inside a sequence; the event CRLF trimmed. |
| A2-anim | The header block census: every block in exactly one class, reasons from the section 2.1 list only | One block removed from the classification; a controller type from the census `controllersByType` missing from the table |
| A3-anim | The BMT runtime reader (A/NifControllerSequenceNameTrackReader ReadAll, A/NifBsplineTransformReader) against the document: each Int16 control decoded as `bias + s/32767f*mult` equals the runtime float bit for bit; linear key values are equal. It shares only NifParser. | One short changed in a document copy |
| A8-sample | A new independent `tools/scripts/nif_curve_eval.py` (normalized-segment Hermite, Kochanek-Bartels interior, open-uniform clamped Cox-de Boor) against Shared ScenePoseEvaluator at key times and midpoints. The evaluator reproduces Shared's documented Float32 rounding points (the Int16 decode, TBC tangents cast before the Hermite, one final cast per component: C/SceneBSplineCurve.cs:191; C/SceneTbcTangents.cs:104-105; C/ScenePoseChannel.cs:54, 133). Tolerance: scalar components within 4 ulp of the largest key or control magnitude in the segment, not of the result; rotations by the angle between the two unit quaternions, within a bound stated in the receipt, because Shared slerps in float (C/ScenePoseChannel.cs:153) [A]. The receipt records the worst observed deviation, and every control must exceed the tolerance by at least 100 times. | A B-spline evaluated with an unclamped knot vector; the 170-degree control below |
| 170 degrees (design:870) | Retail FNV PC `meshes/creatures/queenant/idleanims/specialidle_ antenna.kf` (the name has a space; 6,266 B, SHA `19a96da802807cad102cb0ab41d51e8eb3b7e7fbd6a2718d7cf40334cc982c8e`; sequence SpecialIdle__Antenna, block 17, 0-based keys 10 and 11: 174.5 degrees, dot 0.048; target 'Bip01 Ponytail3 R Antenna' resolves once in `meshes/creatures/queenant/skeleton.nif`). A `.nif` twin needing no skeleton: FNV X360 `meshes/armor/headgear/slavehats/nvslave_02_go.nif` (56,416 B, SHA `1a3622e0194abb50c45ee77c99f135c39fe513b963870223fbf8f3d133e8a513`; `(controllers)` clip, block 161, keys 12 and 13: 174.276 degrees). Plus a synthetic 170-degree pair. | At the document level, Shared's slerp and nlerp differ by at least 6.76 degrees at 170 degrees, 7.33 at 174.276 and 7.36 at 174.5 [M, nlerp_dev.py's deviation function]. The comparator must detect nlerp. At the writer level (slices 10-11): GLB LINEAR equals slerp within 0.1 degrees, and Blender deviates by at least 6.7 degrees before key insertion and at most 0.1 degrees after. Not used: `1hmaim.kf` (block 38 and 11 more of its 37 rotation groups are non-unit, D2) and `fxnullexplosionart.nif` (exactly 180 degrees with dot +7.55e-8, a knife edge whose arc a Float32 dot may flip). |
| RE-17 shortest path | Retail FNV PC `meshes/creatures/protectron/h2hrecoil.kf` (8,869 B, SHA `303762d47501a741455ef7ea43c4163cd6d3d941a921d09852da3a7860050561`; sequence Recoil, block 15, keys 27 and 28: dot -0.99998, 0.743 degrees, so without the flip the segment turns about 359 degrees; target 'Bip01 Spine' in `meshes/creatures/protectron/skeleton.nif`). A moderate case: FNV PC `meshes/creatures/nvmantis/idleanims/specialidle_hitarmleft.kf` (33,753 B, SHA `6361891d4aefd6e49c9a2f7aefd648e5d281f165e837ea964cb96e7a8170f2df`; block 20, keys 0 and 1: dot -0.869, 59.3 degrees). 292 such pairs in 51 `.kf` type whole; none in a `.nif`. | The shortest-path flip disabled in the evaluator. Not used before SA2: FO3 `2haattackrightdown.kf` (its block 13 is non-unit). |
| B, B', C, D, E, F | As in gate 1a, for animation | Need SA1 and SA9 (slices 10-11) |

---

## 4 Slices (in order; every slice builds and tests on its own; the owner runs builds and Bucket-B)

Slices 1-9 add the animation stage as components that tests call directly with a read state built the way NifModelReader builds one. NifModelReader, the probe and the CLI stay as they are until slice 10, so every legacy output stays byte-identical through slice 9 (D1).

| # | Slice | Shared | Files |
|---|---|---|---|
| 0 | Cover manifest and payload expectations | none | `tools/scripts/nif_cover.py`, `nif_cover_expectations.py` (`--scope cut1b --payloads`), probe tags under cut1b plus payloads only, `T/Core/Modeling/Samples/cut1b-cover-manifest.json`, `cut1b-probe-expectations.jsonl`, `T/Core/Modeling/RealAsset/Cut1b*` (the cut-1a manifest classes generalized) |
| 1 | Decode views and oracle A0 | none | `BMT/Core/Modeling/Nif/Animation/NifAnimationViews.cs` (key group, quaternion key, TBC key with named fields, interpolators, B-spline data and basis, sequence and controlled block, time controller, manager, text keys); strict decode for these types; `T/Helpers/NifTestFileBuilder` extended; schema lint at BS 14-34 including 24, 25, 27, 28, 30, 31, 33 |
| 2 | Curve, state and clock mapping | none (Quadratic rows after RE-18, or at once if the owner accepts D9's data) | `NifModelCurveMapping.cs` (pure): sections 1.1 to 1.4, plus a blocked-reason enum |
| 3 | Skeleton resolution, as a component | none | `NifModelSkeletonResolver.cs` (nearest-wins walk-up, reusing the ancestor enumeration idea of A/NifModelFamilyAnimationResolver.cs:299-311), NifModelPaletteNames (name to block), BethesdaTextureCompanions (walk-up). `.kf` admission is not in this slice (D14). |
| 4 | Sequence clips and manager multi-clip (transform), as a component | none | `NifModelAnimationReader.cs`; NifModelReader untouched |
| 5 | Text keys to events | none | NifModelAnimationReader |
| 6 | The `(controllers)` clip | none | NifModelAnimationReader |
| 7 | Morph weights where they fit | none | `NifModelAnimationMorphs.cs` |
| 8 | Coverage classification, native rows, info diagnostic, as components | none | `NifModelAnimationCoverage.cs` (the section 2.1 table as a pure classifier), `NifModelAnimationNativeState.cs` |
| 9 | Document-level Bucket-B hops A1-anim, A2-anim (classifier), A3-anim, A8, and the 170-degree control, through the stage | none | `T/Core/Modeling/RealAsset/NifAnimation*OracleTests.cs`, `tools/scripts/nif_curve_eval.py` |
| 10 | SA1 on the pin, then the switch-over | **SA1** (Bethesda may implement it; mailbox) | Pin bump to canonical main's tip; NifModelReader (`animations:`, row budget :175), NifModelCoverage (the classifier replaces the 'later-cut(1b)' reasons), NifModelProbe, NifModelReader.CheckRoots and NifModelFormatMetadata (`.kf` admission, BS 24-33), BethesdaModelRegistration (`bmt.skeleton`), MeshCommand (`--skeleton`), D4 and D14 answers; hops A2-anim end to end and B for animation |
| 11 | Blender animation | **SA9** (Media.Blender claim) | BlendAdmission, package, `import_model.py`, `readback.py`; hops B', C, D |
| 12 | Non-unit rotation keys typed | none (SA2 withdrawn: slice 1 types them through `NifRotationKeyEngineRule`, the engine's own exact normalization, RE-17) | DONE in slices 1-2; `1hmaim.kf` and `2haattackrightdown.kf` become controls |
| 13 | Euler rotation | none since Shared `68335d3` (`SceneEulerRotationTrack`, `SceneEulerOrder.Xyz`, RE-20 composition) | Mapping and coverage |
| 14 | Property and visibility tracks | none since Shared `68335d3` (`ScenePropertyTrack`, `ScenePropertyTarget`, `ScenePropertyKind`) | Material, layer and node target maps (1a MaterialByFedBlock is first-only: NifModelMaterialReader.cs:256-261) |
| 15 | Morph per target, B-spline morph | none since Shared `68335d3` (`SceneMorphTargetTrack` over `SceneCurve`) | Folded into slice 7 |
| 16a | Component TBC | none, but needs RE-19 | Mapping; control: X360 `vgeardoorr106.nif` block 242 (translation, 3 of 14 keys with continuity != bias) |
| 16b | Quaternion TBC and QUADRATIC | none since Shared `68335d3` (`SceneInterpolation.GamebryoSquad` + `SceneGamebryoSquadPolicy.PcFloat32`; RE-24 inner points from the reader). GLB still refuses Squad until its certificate exists (foundation). | Mapping; control: `sneak2hhattackspin.kf` block 85 |
| 17 | Gate 1b (owner) | after 10 and 11 | Hops A0, A1-anim, A2-anim, A3-anim, A8, B, B', C, D, E, F; the 170-degree control; Dropped only with later-cut reasons; legacy outputs byte-identical; peak memory recorded |

### Slice 0 detail: the manifest
- **Items** (the vocabulary of SCR/cut1b_cover_seed.py, plus the four discriminator families of SCR/review2_cover_seed.py):
  - block types
  - key kind per block and channel
  - channel states
  - clock classes: sentinel, negative start, zero length, phase != 0
  - controller type by cycle, active and manager flags
  - sequence controlled-block controller and interpolator pairs, and controller-ID classes: SELF_ILLUM 476, SPEC 49, N-N-TT_* 457, particle emitter names
  - multi-sequence files
  - duplicate targets
  - morph shapes
  - text-key shapes: CRLF, two events, empty
  - quaternion classes: non-unit, dot < 0, 170 degrees or more; and the same two angle classes restricted to unit groups (`quat:dotNeg:unit` 396 files, `quat:ge170:unit` 30 files)
  - TBC per channel, plus `tbc:<channel>:cneb` (continuity != bias: rotation 15 files, translation 16)
  - `quad:<block>/<channel>:fneb`: multi-key Quadratic groups with a numeric Forward != Backward (translation 183 files, scale 499, Euler 2,331, NiFloatData 1,616, NiPosData 718)
  - B-spline handle states
- **Seed measured** [M]: greedy plus reverse-delete over the original vocabulary gives 159 files over 15 version keys, about 29.5 MB (BS 34: LE `.nif` 52, BE `.nif` 53, LE `.kf` 19; other keys 1 to 6 each). With the four discriminator families added it gives **162 files, about 24.5 MB** (BS 34: LE `.nif` 52, BE `.nif` 52, LE `.kf` 20) [M review2_cover_seed]; the greedy is not monotone, so bytes fell while files rose. None of the 146 original seed files that carry key data holds a continuity != bias group; they hold numeric Forward != Backward groups only by accident (scale 59, translation 1), because the item was not in the vocabulary [M review2_verify]. The MILP result must be no larger than the re-run seed.
- **Additions to the cover:**
  - the skeleton companions of every covered `.kf`, SHA-pinned; FO3's skeletons for FNV-shipped FO3-DLC creature `.kf`, through `--skeleton`
  - the decline controls: the five 20.0.0.4 `.kf`
  - the named controls below, with SHA-256 (all measured from their archives)

**Named controls**

| Item | File (archive) | Bytes | SHA-256 |
|---|---|---|---|
| 170 degrees, unit, clip types whole | `meshes/creatures/queenant/idleanims/specialidle_ antenna.kf` (block 17, keys 10 and 11, 174.5 degrees; skeleton `meshes/creatures/queenant/skeleton.nif`) | 6,266 | `19a96da802807cad102cb0ab41d51e8eb3b7e7fbd6a2718d7cf40334cc982c8e` |
| 170 degrees, unit, `.nif` | `meshes/armor/headgear/slavehats/nvslave_02_go.nif` (FNV X360 Meshes; `(controllers)` clip, block 161, keys 12 and 13, 174.276 degrees) | 56,416 | `1a3622e0194abb50c45ee77c99f135c39fe513b963870223fbf8f3d133e8a513` |
| Exactly 180 degrees (edge case, not the 170-degree control) | `meshes/effects/fxnullexplosionart.nif` (FNV X360 Meshes; block 7, dot +7.55e-8; the PC copy is `7a70421c...8cb9`) | 3,162 | `1028b7bc694b433441bb8c28d389ccbfc042084ba86b1e5e4c83619e141c3f77` |
| dot < 0, unit, clip types whole | `meshes/creatures/protectron/h2hrecoil.kf` (block 15, keys 27 and 28, dot -0.99998; skeleton `meshes/creatures/protectron/skeleton.nif`) | 8,869 | `303762d47501a741455ef7ea43c4163cd6d3d941a921d09852da3a7860050561` |
| dot < 0, moderate | `meshes/creatures/nvmantis/idleanims/specialidle_hitarmleft.kf` (block 20, keys 0 and 1, dot -0.869) | 33,753 | `6361891d4aefd6e49c9a2f7aefd648e5d281f165e837ea964cb96e7a8170f2df` |
| Non-unit quaternion | `meshes/characters/_male/2hmholster.kf` (FNV PC Meshes) | 600 | `b11f6ea9c207eabfe6bd0fd59c4be479bf6b6a4e57f7176f318a1eb3d284c4e9` |
| Non-unit at 174 degrees (slice 12) | `meshes/creatures/mistergutsy/specialanims/1hmaim.kf` (block 38, 0-based keys 3 and 4, dot 0.052; 12 of 37 rotation groups non-unit) | 8,992 | `726748ed31197c1cb5c2ae33b377c2973a2f7b7764b85bf8c5f4943e20528c13` |
| Non-unit, dot < 0 (slice 12) | `meshes/characters/_male/2haattackrightdown.kf` (FO3 Meshes; block 13) | 3,231 | `d5807c869cf325eb872b0e8302f529fcc6c65e4c493523b6589e827a6c5553ed` |
| Duplicate target | `meshes/characters/_male/2hadeath.kf` (FNV PC Meshes, BS 21) | 520 | `25e39b27032fa0fe7364303c3fe0cd6b35497b95cd7d7e825c48f80af1ae2368` |
| Sentinel clock | `meshes/dlc05/clutter/alienbridgescreen/dlc05gobowarning.nif` (FO3 Zeta) | 2,182 | `4f78a803d03ab4b03157d212eeb3539a1b41e164a30ea5b61f5a142a5e0c9294` |
| Negative start and negative key time | `meshes/vatscameras/defaultshootright01.nif` (FNV X360) | 2,030 | `50500d7febcbf6ff311fef4c761740aa6774d4f302a7c10d9a944b2399e3e189` |
| Zero-length clock | `meshes/furniture/pushupsmarker.nif` (FNV PC Meshes) | 1,315 | `acdf2259f343132e40e1c4376598b68e24e93e8f1fbc91c23d16dc165adc3a34` |
| Phase != 0 | `meshes/nvdlc02/sky/nvdlc02rainup.nif` (FNV HonestHearts) | 2,098 | `b3ce4af6a2fff09ff6b0c3d93f19e0dc60e78322f8184caa97d02ea4e47c73ec` |
| Multi-sequence | `meshes/dlc05/clutter/alienbridgescreen/dlc05goboshiphealth25.nif` (FO3 Zeta) | 2,314 | `1b83cc280f809994fcb2a8640ca6349e63ec6640a0dc9e0811ca28e4ee211c5b` |
| TBC rotation (all parameters zero) | `meshes/vatscameras/targethandycamrt.nif` (FNV X360) | 726 | `d35d5a4bbaf457d753b8fda15bcc843de2830395be42233aaf352cfef018efa6` |
| TBC continuity != bias, rotation (A0 order control) | `meshes/characters/_male/sneak2hhattackspin.kf` (FNV PC Meshes; block 85, 26 of 41 keys store (0, -1, 0)) | 20,889 | `57af689067e526dd7a3af20f9769e7b3bcb4deb0a9de266ed177f14ec928241c` |
| TBC continuity != bias, translation (slice 16a) | `meshes/dungeons/vaultruined/doors/vgeardoorr106.nif` (FNV X360; block 242, 3 of 14 keys differ, such as (1, -1, 1)) | 223,759 | `750899dd4a35711d18e9beab2f4ddc120f9c9ea49fd1093d059a23b2b414b845` |
| TBC translation (all parameters zero) | `meshes/vatscameras/megatonnukedeathcam.nif` (FNV X360) | 626 | `60c2869965e62c4f2a74ed333f8fe9690994b257e3a8c8f77f116a7d78c2b65d` |
| TBC scale (all parameters zero) | `meshes/effects/actorfirefx/fireball04.nif` (FNV PS3) | 4,389 | `0723ff4e0c2bb17212a20cbc401720f9b0282acbc1c0002e96d1674112a82561` |
| TBC Euler axis | `meshes/vatscameras/ninjacamplayer02.nif` (FNV X360) | 870 | `f469b862b83fd6f0aeb968dfc58a220ffdc5628a791cfc72046d8ecba7882f71` |
| TBC float | `meshes/vatscameras/snipercamshoot02.nif` (FNV X360) | 1,754 | `0fd96857522130eeac326fc95ba81962c5d2302c3ba2904d43964313fd2a6531` |
| Embedded morph that fits | `meshes/dlc05/dungeons/mz/misc/dlc05holoteleporter.nif` (FNV X360; its Base weight is keyed [0, 0, 0]) | 21,456 | `eb9dc7d1ea133a9f54db1992a2612372fe8f28c4be6d7eee57a62311c45233cb` |
| Embedded morph that differs | `meshes/furniture/nv_legionflag_largnopole.nif` (FNV X360) | 46,018 | `87edd175035d0cd79f2c7fadeca011c82ff28c01eee235d1862158d7b1afe3a4` |
| Sequence morph that fits | `meshes/characters/_male/sneak1hpaimisup.kf` (FO3 Meshes) | 5,920 | `c8ec51048e5fe4b9688d82af47d95908aeb8ed9b4e4f2da88690ce5a83248253` |
| Sequence morph that differs | `meshes/nvdlc04/dungeons/utemple/nvdlc04udomeirislightbeam.nif` (FNV LonesomeRoad) | 10,557 | `21bd6adbefed3f0806bafa09b82f368ee919459c687f8b2e26c8399389d9f5cc` |
| Sequence morph, keyed plus constant | `meshes/characters/_male/idleanims/dlc05cryopoddynamicidle.kf` (FNV PC Meshes) | 4,229 | `0ca86ccaabfb258693daf180fb21ef6062b234fed3f900aa483dca6a21852a25` |
| BSAnimNotes (BS 24) | `meshes/characters/_male/idleanims/talk_headnodsingle_neutral.kf` | 8,190 | `d88590de0a9b82426dece6345653660619db692324e180803cce75b66d9f931b` |
| BSRotAccum | `meshes/dlcanch/creatures/chimera/1hpunequip.kf` | 1,335 | `0c5a269209a49bbdc2d7e40bad4fb5c426a15e9b130b3f433f0ffc7fc406908f` |
| BSTread | `meshes/characters/_1stperson/1hmidle.kf` | 3,701 | `c666d2a7784b8c4e4831b500f18c83819c3a5e8e3106d11f8b4a429c0dec194e` |
| Path | `meshes/vatscameras/target_deathspinlow02.nif` (FNV X360) | 752 | `02a61e761517a667f89421fd7be498d95ec2cc738b6c64b4c3d18f3f7d23685d` |
| LookAt | `meshes/terminals/nv_roulette/nv_roulette-table.nif` (FNV PS3) | 241,761 | `041119f4ba2fbfd45cf64b6539ff836ae3cd14a526120b11cf92ef38f25135c5` |
| Uncompressed B-spline | `meshes/dlc05/creatures/alien/locomotion/1hmforwardwalk.kf` | 18,300 | `0eeae0ac7bf4a7eb901c3ee7ac9080c620797d8e29d3e5d237464210ce260cce` |
| Compressed float B-spline | `meshes/characters/_male/2haaimisup.kf` (FO3) | 2,355 | `38a33beeca9482482fbb22334f362dfa92845734eed146c202494feb31cd7372` |
| Compressed Point3 B-spline | `meshes/creatures/sentrybot/death.kf` | 788 | `2dd8069c091934c70837825e09aa8d6dc6b6ca740672c5676ef0f9e15275a3de` |
| B-spline with a keyed scale channel | `meshes/creatures/nvsecuritron/1hpdeath.kf` | 2,199 | `d0a71e6174e08e30ded4b78177fcd11aff9bb4b45b3b8ef78eda66153066bcca` |
| LOOP sequence | `meshes/characters/_1stperson/h2hidle.kf` | 415 | `baf39f37b4c1ebfb44e3dedffdfc0b04cd35de842bce774cddac1d496d5cf3db` |
| Forward != Backward, signed zero only (A0 only) | `meshes/creatures/libertyprime/talking.kf` (block 2: NiPosData under an NiPoint3Interpolator, a property track, SA4) | 645 | `8d9c9e5e96c1ecd870a11e1540cf9ccc6d099190fb0435a5d1c3022f7f192087` |
| Forward != Backward on a typed channel (scale) | `meshes/traps/fxgastrapblast.nif` (FO3 Meshes; `(controllers)` clip, block 7) | 7,421 | `3b5ce0bb7c4c4c4b60c320cb97a2e9238ac2eab6c52b9a53453192b4f45b5c67` |
| Forward != Backward on a typed channel (translation) | `meshes/dlcpitt/effects/dlcpittfireburst01.nif` (FNV PS3; sequence Idle, block 31; the X360 copy is `4ab5d24e...7030`) | 18,141 | `14ad86d977452c4887effb17ca88d10152740a78afa0ea71d9343b0943ab7061` |

Rows without an archive named are in FNV PC Fallout - Meshes.bsa. Also pin the existing Bucket-B file, `meshes/creatures/nightstalker/h2hattackleft.kf` (T/Core/Formats/Nif/Rendering/Viewer/FnvNightstalkerBsplineAnimationRetailTests.cs:22).

### Verification per slice (summary; the JSON rows carry the full text)
- **0:**
  - Every census item is covered, the four discriminator families included. Control: drop one cover file and the tool names the uncovered item.
  - Every SHA is reproduced by the Python BSA reader. Control: one flipped byte gives a mismatch.
  - The cut-1a expectations regenerate byte-identical at scope cut1a. Control: scope cut1b changes them.
- **1:** Hop A0 bit-equal on every manifest file, with its controls. Synthetic LE and BE fixtures cover every key type.
- **2:** One closed-form test per mapping row through Shared's evaluator, each with a failing control:
  - an asymmetric Hermite fixture, plus `fxgastrapblast.nif`'s two-key scale segment (control: Forward and Backward exchanged)
  - unequal C and B with intervals 1.25 and 0.75
  - an Int16 decode against the runtime formula (control: 32768)
  - a sentinel static (control: typed as Constant)
  - scale replication and the rotation permutation (control: an unpermuted quaternion)
  - the synthetic 170-degree pair (control: nlerp)
- **3:**
  - Walk-up picks the nearest skeleton, with no compatibility gate. Control: the farther one chosen, or a skeleton skipped because a '##' name is missing.
  - `--skeleton` input wins over walk-up (tested on the component; the CLI switch lands in slice 10).
  - Missing and ambiguous targets get their reasons.
  - Bucket-B: each covered `.kf` resolves the skeleton pinned in the manifest. Control: walk-up disabled gives 'no skeleton'.
- **4:**
  - Clip order and exact occurrence indices under instancing. Control: a name-matched wrong occurrence.
  - `ValidateStructure` passes on the stage's document.
  - A1-anim transform family.
  - `2hadeath.kf` stays NativeOnly (D7).
  - NifModelReader outputs stay byte-identical to 7124ba12 on the cut-1a manifest. Control: calling the stage from the reader changes them.
- **5:** CRLF, two-event and empty keys kept. Controls: trimming, rejecting empties, and sorting equal-time pairs each fail.
- **6:** Phase, frequency and cycle round-trip through ScenePoseEvaluator. Control: retiming keys at read. The clock-control files are bit-exact.
- **7:** Measured shapes on the named files; the Base interpolator stays native with the RE-23 reason. Control: forcing a shared time vector on the differ file fails A1-anim.
- **8:** Hop A2-anim on the classifier with both controls; row budget; a table-completeness test over the census controller types.
- **9:** All document-level hops pass on the manifest, and every control fails.
- **10:** After the pin carries SA1: `mesh convert --format glb` on each animated manifest file is Converted with its clips (hop B for animation); control: a synthetic clip with frequency 0 refuses (SA1 refuses frequency <= 0). `.kf` admission: `eatidle.kf` stays Unsupported 'cut 2'; `1hmaim.kf` gives D14's answer before SA2; an FNV-shipped FO3-DLC `.kf` gives D4's answer without `--skeleton` and resolves with it.
- **11-16:** Listed in the JSON slice rows.

---

## 5 Shared asks (exact contract missing at 66bf702)

**Status at canonical Shared `68335d3` (2026-09-26):** SA1 landed (`88303fa`, `e842d08`, `ef01f74`); SA2 withdrawn (RE-17); SA3, SA4, SA5 and the SA6 PC sampler landed (`84c6486`, `23d1617`, `c1995ac`), with GLB writers for Euler, properties and per-target morphs; SA7/SA8 (`8edca2e`) and SA12 (`e00e4cc`..) landed earlier; SA9 landed as Blender native animation (`7f3935d`, `005a4a8`, `1f8a898`); SA11 done. Open: SA10 (track-level `SceneElementRef`; native rows keep track identity in the payload) and the SA6 GLB certificate plus the Xbox 360 Squad sampler (foundation, per its 2026-09-26 note). The table below is the record as asked.

| # | Ask | Population [M] | Why |
|---|---|---|---|
| SA1 | GLB clip and track clock lowering. The refusal is at M/ModelGltfAnimationMetadata.cs:26, 31-32, 78-84. Appendix B rows: frequency and phase become retimed keys (Converted); Clamp is Exact when it is the identity over the keys; Loop is one interval plus a Metadata cycle; Reverse gets mirrored keys (Converted); a zero-length interval becomes STEP; track clocks compose after the clip clock; frequency <= 0 refuses. Now the prerequisite of slice 10's switch-over (D1); Bethesda may implement it (owner ruling 2026-09-23) and announce it in the mailbox. | 13,280 sequences (LOOP 3,865, CLAMP 9,415); 67,078 controllers (CLAMP 56,395, LOOP 10,630, REVERSE 53); phase != 0 on 2,260; zero-length 7,542; frequency 1.0 everywhere | Without it, every animated NIF refuses GLB once clips are typed |
| SA2 | Source rotation keys: accept finite, nonzero, non-unit quaternion keys as source values (as B-spline controls already are), with sampling and writers normalizing only derived values; GLB states output normalization as Converted. Or widen the tolerance to at least 3e-3 on the squared length with a stated bound. | 254,694 keys, 44,669 groups, 4,432 files; maximum 2.64e-3 | Rejected at C/SceneAnimationValidation.cs:125-127 |
| SA3 | A per-axis Euler rotation form: three independent scalar curves (own times, Linear, Step, Hermite or TBC, own TBC parameters and endpoints), an axis-order declaration (NIF XYZ, composition per RE-20), and optional static fallback and state per axis | 67,206 rotation blocks; blocked interpolators: `.kf` 17,966, `.nif` 52,138 | The largest blocked class in `.nif` |
| SA4 | Property and visibility tracks: targets for material (Diffuse, Specular, Ambient, Emissive color, Emissive multiplier, Alpha), layer UV (TranslateU, TranslateV, ScaleU, ScaleV, Rotate) and node visibility. Values: float, color3, bool. Curves: Linear, Step, Hermite, TBC and Float32/Int16 B-spline, with per-track clock and state. | Material and texture 11,878 embedded plus 18,085 in sequences; visibility 10,781 plus 15,551; compressed float and Point3 B-splines 275 and 439 | Appendix B rows exist in the design but have no Shared type |
| SA5 | Morph tracks with per-target times and interpolation, or per-target Constant state, plus B-spline morph weights | Embedded: 43 of 103 non-blend controllers do not fit. Sequences: 336 of the 500 groups with non-Base targets do not fit (199 keyed plus constant, 95 differ, 36 B-spline, 6 all constant). | Rejected at C/SceneMorphTrack.cs:15-17 |
| SA6 | A quaternion TBC or Squad curve kind, after RE-17 settles the engine formula | 901 groups (`.kf` 236, `.nif` 665 interpolators) | Refused at C/SceneTransformChannelValidation.cs:34-35 |
| SA7 | Typed clip weight, per-track priority and accumulation-root fields, or writers that keep clip ExtrasJson | 375,971 controlled blocks with priorities 0-141; 13,280 accumulation roots | Extras are dropped by both writers |
| SA8 | Node rest-pose provenance (for example, Invented or Assumed), so a writer can class a skeleton-less `.kf` Degraded as design:868 requires | 316 FNV PC `.kf` | D4 |
| SA9 | The Blender animation path: F-curves, NLA strips carrying the clip clock, markers for events, and key insertion for nlerp within 0.1 degrees. The importer must accept `animations`. This is under a bethesda claim per design row 13. | All typed clips | Blender drops them today (B/BlendAdmission.cs:791-842; import_model.py:46-48) |
| SA10 | `SceneElementRef` able to target one track, for native rows | n/a | C/SceneElementRef.cs:9-13 |
| SA11 | Mailbox and coordination text at the pinned commit should match the code on B-spline GLB conversion. This is already corrected at canonical 03ac991. | n/a | Stale note at INTER-SESSION.md@66bf702:61-62 |

## 6 BMT reverse-engineering items (Ghidra-first; FO4 PDB names and FNV runtime-image emulation, following `TestOutput/probe-payloads-20260925/tbc_engine_oracle.py`)
- **RE-17 (existing):** Linear quaternion evaluation. Does the engine flip on dot < 0 (6,091 pairs; 3,230 in unit groups)? Does it normalize non-unit keys (254,694)? How does it evaluate B-spline rotation? Controls: `h2hrecoil.kf` and `specialidle_hitarmleft.kf` (unit); `2haattackrightdown.kf` (non-unit).
- **RE-18:** Quadratic key tangent roles and units. Which of Forward and Backward is incoming, and are tangents per segment or per second (NiBezFloatKey and NiBezPosKey LoadBinary and the Hermite evaluator, names [R])? Acceptance test: the engine reading must agree with the retail interval ratios of D9 (Forward/Backward = dt_prev/dt_next on 1,386 of 1,386 translation components, 0 the reverse) [M review2_hermite_kb]. Discriminating population on typed channels: 2,255 groups.
- **RE-19:** The TBC endpoint tangents at the first and last keys (CalculateDVals boundary cases [R]). Population: 797 component groups on transform channels, plus the TBC groups that SA3, SA4 and SA6 would admit.
- **RE-20:** Euler composition order and per-axis interpolation.
- **RE-21:** Which duplicate controlled block a sequence uses (41 sequences).
- **RE-22:** NiTimeController scaled time (frequency times t plus phase, then cycle [R]); whether Loop excludes the endpoint; sequence clock against controller clock precedence; whether the active flag matters.
- **RE-23 (new):** NiGeomMorpherController with Relative Targets = 1: does the engine apply morph 0's (Base) weight to the base positions, or ignore it? Population: 103 non-blend controllers keying Base at 0.0 throughout, 113 blend controllers, and the in-sequence 'Base' blocks. Section 1.8 assumes "ignored" [I].

## 7 Risks and open questions
- **The clock problem.** Typed animation cannot reach GLB until SA1; D1 therefore lands SA1 before the switch-over, so gate 1a stays intact and no option is added. If SA1 slips, slices 1-9 still land and test through the stage.
- **Under D3 and 66bf702, few whole clips type:** 591 of 5,760 `.kf` are fully typable with Hermite admitted, 584 with RE-18 unsettled; `.nif` 1,061 and 717. SA2 and SA3 carry the value of cut 1b; say so in the mailbox.
- **Skeleton-only `.kf` documents.** Without D14, most admitted `.kf` would export a skeleton with no clip and no refusal.
- **Skeleton reads double the memory per item.** The skeleton goes through NifModelReadCache.ReadCompanion within its 256 MiB budget.
- **Occurrence fan-out.** A target block with several occurrences yields several tracks. The count is measured in slice 4 (expected rare).
- **Probe independence.** The probe must stay independent: the payload expectations come from it; the reader shares no code with it.
- **Tolerance honesty.** A8 compares a double evaluator with Shared's Float32 pipeline; its tolerance is stated per hop and every control must clear it by a wide margin (section 3).
- **The runtime renderer is untouched.** A/NifKeyGroupReader keeps its approximations. Its tests pin them (T/Core/Formats/Nif/Rendering/Animation/NifQuadraticVectorAnimationTests.cs:81-89).

## Critical files
- `C:/dev/Multitool-worktrees/bmt-material-preparation-20260912/src/BethesdaMultitool/Core/Modeling/Nif/NifModelReader.cs` (orchestration :127-211, ModelDocument :202)
- `C:/dev/Multitool-worktrees/bmt-material-preparation-20260912/src/BethesdaMultitool/Core/Modeling/Nif/NifModelCoverage.cs`, `NifModelProbe.cs` and `NifModelMorphReader.cs`
- `C:/dev/Multitool-worktrees/bmt-material-preparation-20260912/src/BethesdaMultitool/Core/Formats/Nif/Rendering/Animation/NifModelFamilyAnimationResolver.cs` (the ancestor walk to reuse) and `NifQuadraticVectorCurve.cs` (the Hermite convention)
- `C:/dev/Multitool-worktrees/bmt-material-preparation-20260912/shared/Multitool.Shared/src/Slfx77.Multitool.Core/Models/SceneTransformTrack.cs`, `SceneAnimationValidation.cs`, `SceneTransformChannelValidation.cs`, `SceneBSplineCurve.cs`, `ScenePoseChannel.cs`, `SceneTbcTangents.cs`
- `C:/dev/Multitool-worktrees/bmt-material-preparation-20260912/shared/Multitool.Shared/src/Slfx77.Multitool.Media/Models/ModelGltfAnimationMetadata.cs` (the clock refusal) and `ModelGltfAdmission.cs`
- `C:/dev/Multitool-worktrees/bmt-material-preparation-20260912/tools/scripts/nif_feature_probe.py` (the oracle), `nif_cover.py`, `nif_cover_expectations.py`
- `C:/dev/Multitool-worktrees/bmt-material-preparation-20260912/TestOutput/cut1b-prep-20260925/animation-vocabulary-needs.md`
