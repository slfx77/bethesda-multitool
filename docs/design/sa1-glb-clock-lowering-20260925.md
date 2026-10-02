# SA1: GLB clip and track clock lowering

Status: implemented 2026-09-25 (Shared `51ef128`); specification below. Owner: bethesda (assigned by the foundation, Shared `a5f9e1b`). Implemented on
a `bethesda/` Shared branch under the file claim in section 8, then handed to the foundation for integration.

Why: the Shared GLB writer refuses every clip or track clock (`Slfx77.Multitool.Media/Models/ModelGltfAnimationMetadata.cs`
rows `animation.clock-unsupported`). Every NIF sequence (13,280) and every embedded controller (67,078) carries a
clock, so cut 1b's switch-over (slice 10) cannot type a clip until this lands (cut-1b plan, D1).

Engine semantics: `docs/formats/nif-animation-engine-behavior-20260925.md`, RE-22. The reader maps each engine clock
onto `SceneAnimationClock` exactly as that section's implementation rule says. This document only concerns how the
writer turns a `SceneAnimationClock` into standard glTF keys.

## 1. What is lowered

For a clip with an optional clip clock C and a track with an optional track clock T, the source is sampled at

    s(tau) = T(C(tau))        (an absent clock is the identity)

where tau is clip-local output time in seconds, tau >= 0. This is the composition Shared's own sampler uses
(`ScenePoseEvaluator`: clip clock first, then the track clock), so that sampler is the oracle.

The stage runs after the existing per-track lowering. That lowering already turns Hermite and TBC into CUBICSPLINE,
B-splines into CUBICSPLINE or LINEAR keys, Gamebryo counter-warped nlerp into bounded LINEAR slerp keys, and
Constant state into STEP keys. The clock stage therefore only ever sees LINEAR (vector lerp or quaternion slerp),
STEP and CUBICSPLINE, and it gives each per-track stage a clock-free view of the track.

Out of scope: matrix tracks (their channels are refused for other reasons and keep the clock refusal), the direct
legacy path (`SceneGltfBuilder.Build` keeps refusing every clock), and Blender (SA9).

## 2. The piecewise map

`SceneAnimationClock.Map` is piecewise linear in its input:

| Cycle | Slope inside the interval | Discontinuity |
| --- | --- | --- |
| Loop | +f | a jump back to start at every wrap |
| Reverse | +f, then -f | none (turnarounds are continuous) |
| Clamp | +f inside, 0 outside | none |
| any, stop == start, or f == 0 | 0 | none |

A composition of two such maps has the same form. Over an output window [0, W] the stage enumerates the pieces
exactly: each piece is (tauStart, tauEnd, sourceStart, slope), computed in double from the clock fields, with the
wrap and turnaround instants solved in closed form rather than by stepping.

## 3. Keys for one piece

- **Slope 0:** the value v(sourceStart) is held. One key at tauStart; a second at tauEnd when a later piece follows
  under LINEAR or CUBICSPLINE (zero tangents), so the hold stays flat.
- **Slope m != 0:** a key at tauStart with v(sourceStart); a key at tauStart + (t - sourceStart) / m for every lowered
  key time t strictly inside the piece's source range; a key at tauEnd with the left limit v(sourceEnd).
- **LINEAR:** inserted keys are the curve's own values at those instants (lerp, or slerp for rotations), so the
  output traces the same curve. Reversal is exact because lerp and slerp are symmetric.
- **STEP:** in a descending piece the value held after a key's instant is the key before it. Only the single instant
  of each reversed key differs from the source.
- **CUBICSPLINE:** a Hermite segment restricted to a sub-interval is a Hermite segment. Output tangents are
  m x the source derivative at that instant (glTF tangents are per second). In a descending piece a key's in and out
  tangents swap. Rotations are split in unnormalized quaternion space, which the player normalizes as the source does.
- **Joins:** where adjacent pieces are continuous (a clamp hold or a reverse turnaround), they share one key, with
  CUBICSPLINE in and out tangents taken from each side. At a Loop wrap the value jumps. glTF requires strictly
  increasing times, so the earlier piece's last key moves one Float32 step before the wrap instant; the wrap instant
  itself belongs to the new cycle, as in `Map`.

## 4. The output window

- **Clip clock present:** W is one clip period. Clamp: the input time at which C reaches its far endpoint, or 0 if it
  starts there. Loop: L / |f|. Reverse: 2L / |f|. A zero-length interval or f == 0: W = 0. Track clocks inside the clip
  are evaluated over this window, so the clip's own repetition repeats them exactly.
- **No clip clock, track clocks only** (BMT's `(controllers)` clip, one clock per controller): each track has a
  natural window by the same rule. W is the smallest k x max(W_k), k from 1 to 64, that every repeating track's period
  divides to within 1e-6 relative, and each repeating track is unrolled to fill W. When no such k exists within the key
  budget, W = max(W_k), every shorter repeating track is unrolled and cut at W, and the cycle row says the periods are
  incommensurate.
- **After W:** Clamp-only clips hold their final keys, which glTF samplers also do, so they are exact for all tau.
  glTF has no repeat flag, so a repeating clip (any Loop or Reverse) is exact over [0, W] and its repetition is
  Metadata: the clocks are in root metadata, and a player that repeats the clip every W reproduces the source exactly
  when the periods are commensurate.

## 5. Identity

When every piece is s = tau with slope 1 (for example Clamp with f = 1, phase 0 and an interval covering the keys, or
Loop with f = 1, phase 0, start at the first key and stop at the last), the keys are unchanged and the row is Exact.
Retail FNV sequences (start 0, frequency 1, no phase) take this path unless their stop cuts the keys.

## 6. Fidelity rows

| Feature | Outcome | Reason code | Notes |
| --- | --- | --- | --- |
| `animation/clock`, `animation/transform-tracks/N/clock`, `animation/morph-tracks/N/clock` | Exact | `animation.clock-identity` | keys unchanged |
| same | Converted | `animation.clock-retimed` | bound: output key time, unit ulp (Float32 steps at the emitted time), budget 2; a Loop wrap moves one key by one step |
| `animation/cycle` | Metadata | `animation.cycle-metadata` | repeating clip; W and commensurability stated |
| same features | Degraded | `animation.clock-unsupported` | matrix-track clocks (unchanged) |
| `animation/clock` | Degraded | `animation.clock-duration-unsupported` | a clocked clip that also declares DurationSeconds (ambiguous interval) |
| same features | Degraded | `animation.clock-budget-exceeded` | window or derived keys exceed the document key budget |
| same features | Degraded | `animation.clock-unrepresentable` | the window or a retimed key time is not a finite Float32, or two authored keys collapse onto one output time |

Negative frequency needs no refusal: its pieces descend, which the same key rules cover. The cut-1b plan's
"frequency <= 0 refuses" is superseded. f == 0 and zero-length intervals are holds (Converted, or Exact when the held
value equals the only key). Retail FNV has no negative or zero frequency.

## 7. Metadata carrier

- The clip row of `multitoolAnimationMetadata` gains `clock` (frequency, phaseSeconds, startSeconds, stopSeconds,
  cycle) and `outputWindowSeconds`. A clocked clip always has a row.
- Transform and morph track rows gain `clock`; a clocked track always has a row.
- Event times stay in source clip time, as today. The clip clock maps them to output time.

## 8. File claim (Shared)

New, under `src/Slfx77.Multitool.Media/Models/`:
- `ModelGltfClockLowering.cs`: plan rows, windows, and the retiming of lowered tracks.
- `ModelGltfClockMap.cs`: exact piece enumeration for one clock and for a composition.
- `ModelGltfClockPiece.cs`: one linear piece.
- `ModelGltfLoweredCurve.cs`: value and derivative sampling of a lowered LINEAR, STEP or CUBICSPLINE channel.

Modified:
- `src/Slfx77.Multitool.Media/Models/ModelGltfAnimationMetadata.cs`
- `src/Slfx77.Multitool.Media/Models/ModelGltfTransformChannels.cs`
- `src/Slfx77.Multitool.Media/Models/ModelGltfMorphChannels.cs`
- `src/Slfx77.Multitool.Media/Models/ModelGltfLowering.cs`
- `src/Slfx77.Multitool.Media/Models/ModelGltfAdmission.cs`

Tests and docs:
- `tests/Slfx77.Multitool.Media.Tests/Models/ModelGlbAnimationClockTests.cs` (modified)
- `tests/Slfx77.Multitool.Media.Tests/Models/ModelGltfClockLoweringTests.cs` (new)
- `docs/scene-animation-clock-lowering.md` (new)
- `docs/scene-gamebryo-rotation.md` (one sentence: clocks are now lowered by the clock stage)

Amended the same day, before any edit to them: the shared quaternion emitter and the Gamebryo stage require a
nonnegative timeline, but 12,950 retail key groups start before 0 under a clock. Document validation already rejects
signed times on clock-free tracks (`SceneAnimationValidation`, "negative source times require an explicit clock"),
so the clock stage hands these stages signed intermediate timelines and only the stage's own output must be
nonnegative. The sign check is removed from:
- `src/Slfx77.Multitool.Media/Models/ModelGltfQuaternionSpline.cs`
- `src/Slfx77.Multitool.Media/Models/ModelGltfGamebryoCurve.cs`
- `src/Slfx77.Multitool.Media/Models/ModelGltfGamebryoRotation.cs`

Delivered 2026-09-25 as Shared `51ef128` on branch `bethesda/animation-clock-lowering-v1` (rebased onto main `dacc231`; first committed as `4e0ea99` on `bb68986`).
Differences from the claim, all inside the claimed folders:
- `ModelGltfSplineTimeline.cs` was modified too: its own comment said it rejected signed timelines until a
  clock-lowering policy existed. A clock-free signed spline is still refused, now in `ModelGltfTransformChannels`
  with the same feature and reason code.
- `ModelGltfClockKeyList.cs` is a fifth new file (the emitter's key bookkeeping; Shared keeps one type per file).
- `tests/Slfx77.Multitool.Media.Tests/Models/ModelGltfQuaternionSplineTests.cs`: its "negative time" refusal case
  became a test that a signed domain emits keys in its own source time.
- `ModelGltfLowering.cs` needed no change.
- Two rules were refined while testing. Keys with different values that round onto one Float32 time keep both
  (the earlier moves one step back), and a channel refuses when that is impossible. Dropping either key would
  reshape a segment. A one-key CUBICSPLINE result (a hold) is written as STEP, because CUBICSPLINE needs two keys.

Nothing in Core changes; `SceneAnimationClock` and the sampler are used as they are.

## 9. Verification

- Oracle: the written GLB is read back with SharpGLTF and sampled with its own animation sampler at dense output
  times (every piece boundary, every key, and 64 points per piece). Each sample is compared with Shared's source
  sampler at the same tau. That sampler implements `SceneAnimationClock.Map`, not this stage.
- Matrix: cycles Loop, Reverse and Clamp; frequencies 1, 0.5, 2, -1 and 0; phases 0 and mid-interval; intervals that
  cut and that extend past the keys; LINEAR vector, LINEAR rotation, STEP, CUBICSPLINE, Gamebryo and TBC tracks; morph
  tracks; clip clock alone, track clocks alone, both composed; commensurate and incommensurate `(controllers)`-style
  clips; zero-length intervals.
- Discriminating controls: the same comparison must fail for a lowering that drops the phase, one that plays Reverse
  as Loop, and one that ignores the track clock under a clip clock.
- Existing tests: the refusal theory keeps the matrix case and the legacy direct path for every kind; clip, transform
  and morph clocks become lowering cases.
- BMT acceptance after the pin: the gate-1a sample converts unchanged (no clocks are typed before slice 10), and the
  cut-1b slice-10 checks run against this stage.
