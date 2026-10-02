# SA6: a GLB error certificate for `GamebryoSquad` rotation

2026-09-26. Bethesda, acting for the paused foundation (owner instruction of the same day). Canonical Shared main
`68335d3` ships the `SceneInterpolation.GamebryoSquad` PC Float32 sampler (`SceneGamebryoSquad`, RE-24) and refuses
every GLB export that contains it: "Gamebryo Squad requires a certificate for its nested FastNormalize curve before
GLB lowering". This is that certificate. It follows the foundation's stated boundaries (mailbox, 2026-09-25): the
existing cubic proof does not apply, fitted controls prove nothing, the certificate covers the actual nested sampler
and its rounding, Float32 fraction and weight order and the internal-key segment choice are explicit, refusal is
explicit, the result is Approximated with the policy retained.

Branch: `bethesda/gamebryo-squad-certificate-v1` from `68335d3`, worktree
`C:/dev/Multitool-worktrees/shared-squad-certificate-20260926`. It touches Core (`SceneGamebryoSquad` becomes public),
Media (new certificate and lowering, dispatch, admission, metadata), Media tests and two docs. Blender stays as it is
(it refuses Squad; out of scope here).

## 1. What is certified

Input: a `SceneTransformTrack` with `Property == Rotation`, `Interpolation == GamebryoSquad`, `State == Keyed`,
`GamebryoSquadPolicy == PcFloat32`, `Times` of n >= 1 Float32 keys, `Values` of 12n floats per key i:
incoming inner point `b_i` (offset 12i), key `q_i` (12i + 4), outgoing inner point `a_i` (12i + 8). It arrives
clock-free: `ModelGltfTransformChannels` lowers the clock-free view first and the clock stage retimes the emitted
keys afterwards, exactly as for `GamebryoCounterWarpedNlerp`.

Output: LINEAR quaternion keys (a single STEP hold for n = 1) at Float32 times t_k, every key being
`Normalize(SceneGamebryoSquad.Sample(track, t_k))` rounded to Float32. The player interprets them with shortest-path
slerp. Claim: for every source time t in [t_0, t_{n-1}] the SO(3) angle between the player's rotation and the
direction of `SceneGamebryoSquad.Sample(track, t)` is at most `ModelGltfQuaternionSpline.AllowedRadians` (0.1
degrees). Before t_0 and after t_{n-1} both hold their end key. `Xbox360Estimate` refuses with the existing code
`animation.gamebryo-squad-unsupported` and the reason that the hardware estimate sampler is not implemented.

Fidelity row: `Approximated`, code `animation.gamebryo-squad-converted`, bound ("Gamebryo Squad rotation",
"degrees", 0.1, observed). Metadata keeps the original interpolation and adds `gamebryoSquadPolicy`.

## 2. The curve, exactly as the sampler computes it

Segment i runs from t_i to t_{i+1}, d = t_{i+1} - t_i. The sampler (Core `SceneGamebryoSquad.Sample`) computes, all
in Float32 and in this operation order:

```
numerator = t - t_i;  duration = t_{i+1} - t_i;  u = numerator / duration
A = Interp(q_i, q_{i+1}, u)            // inner endpoints
B = Interp(a_i, b_{i+1}, u)            // inner controls: OUTGOING of key i, INCOMING of key i+1
w = (2*u) * (1 - u)
S = Interp(A, B, w)                    // outer
```

`Interp(first, second, amount)` is `SceneGamebryoRotation.InterpolateComponents` followed by the Blow FastNormalize:

```
dot  = ((first.W*second.W + first.X*second.X) + first.Y*second.Y) + first.Z*second.Z
att  = 1 - 0.8227969*dot
k    = 0.5854922*(att*att)
warp = amount <= 0.5 ? CW(amount, k) : 1 - CW(1 - amount, k)
CW(x, k) = x * (k + (1 + (k*x) * ((x + x) - 3)))
x_c  = first_c + warp*(second_c - first_c)          for c in X, Y, Z, W
s    = ((x.X*x.X + x.Y*x.Y) + x.Z*x.Z) + x.W*x.W
f    = Step(s)                                        Step(y) = ((y - 0.9590659737586975) * -0.5325155854225159) + 1.0214351415634155
if !(s > 0.9152119755744934): f = f * Step((f*f)*s)
if !(s > 0.6521196961402893): f = f * Step((f*f)*s)  // uses the refined f
result_c = x_c * f
```

The constants are the Float32 literals in `SceneGamebryoRotation` and `SceneGamebryoSquad`; the exact-real model
uses their exact Float32 values (cast the float literal to double). .NET does not contract to FMA, so each operation
above is one Float32 rounding.

Facts the certificate relies on (each is checked by a test in section 6):

- In exact arithmetic the two `warp` branches are the same cubic `(1+k)x - 3k x^2 + 2k x^3` (because
  `f(x) + f(1-x) = 1` for that cubic). Only the Float32 rounding differs by branch.
- The outer weight `w = 2u(1-u)` never exceeds 0.5 in exact arithmetic, and its Float32 value never exceeds 0.5
  either (0.5 is representable and rounding is monotone), so the outer `Interp` always takes the first branch.
- At u = 0 the direction of S is the direction of q_i; at u = 1 it is the direction of q_{i+1} (both inner results
  reduce to the key, FastNormalize scales but does not turn). At an internal key the sampler evaluates the
  PRECEDING segment at amount 1 (`InternalKeyUsesPrecedingSegment`), so the emitted key at t_i comes from segment
  i-1; its direction equals segment i's start direction up to Float32 rounding, which section 4 charges.
- FastNormalize changes amplitude, never direction. A threshold crossing (`s` passing 0.9152... or 0.6521...)
  changes the amplitude of an INNER result discontinuously, which turns the OUTER result by a small discontinuous
  amount. The curve is therefore piecewise smooth with small jumps at inner thresholds; the certificate must not
  assume C2 across them (section 4, the `discontinuous` flag).

## 3. Interval Taylor arithmetic with a rounding bound

One node per intermediate scalar over a parameter interval U = [u_lo, u_hi]:

- `V`: an outward interval (`ModelGltfQuaternionInterval`) enclosing the exact-real value for every u in U;
- `D1`, `D2`: outward intervals enclosing the first and second derivatives with respect to u over U;
- `E`: a double >= 0 with |(Float32 value the sampler computes) - (exact-real value)| <= E for every u in U.

Rules (all interval operations outward; `Mag(X)` is `X.Magnitude()`; `r = 2^-24`; `eta = 2^-149`; `rho(V, E) = r *
(Mag(V) + E) + eta` is the rounding of one Float32 operation whose exact result lies within E of V):

| Node | V | D1 | D2 | E |
| --- | --- | --- | --- | --- |
| constant c | [c, c] | 0 | 0 | 0 |
| u | U | [1, 1] | 0 | E_u (below) |
| X + Y | Vx + Vy | D1x + D1y | D2x + D2y | Ex + Ey + rho(Vx + Vy, Ex + Ey) |
| X - Y | Vx - Vy | D1x - D1y | D2x - D2y | Ex + Ey + rho(Vx - Vy, Ex + Ey) |
| X * Y | Vx * Vy | D1x*Vy + Vx*D1y | D2x*Vy + 2*D1x*D1y + Vx*D2y | Ex*Mag(Vy) + Ey*Mag(Vx) + Ex*Ey + rho(Vx*Vy, Ex*Mag(Vy) + Ey*Mag(Vx) + Ex*Ey) |

`E_u`: the sampler computes `u` from Float32 `t`, `t_i` and `t_{i+1}` with three roundings. With the exact
u-interval U and d exact: numerator exact value u*d, error E_num = rho(U*d, 0); duration exact value d, error
E_dur = rho(d, 0); quotient error E_u = (E_num + Mag(U)*E_dur) / (d - E_dur) + rho(U, 0). Refuse the segment if
`d - E_dur <= 0`.

Branches: decide on the Float32-inclusive enclosure `[V.Low - E, V.High + E]` of the tested quantity (`amount` against
0.5; `s` against each threshold). Entirely on one side: follow that branch. Straddling: V, D1 and D2 become the hulls
of both branches' results, E the larger of the two, and the sub-interval is flagged `discontinuous`. The outer
`amount <= 0.5` branch is proved by the second fact above; still evaluate it with the general rule.

The same evaluation, with U a point, reproduces the exact-real curve; with U an interval it encloses the curve, its
derivatives and the sampler's deviation from it. The dependency problem (k, warp, f appearing several times) only
loosens the enclosures; subdivision shrinks them.

## 4. The certificate for one sub-interval

Sub-interval [t_a, t_b] of segment i with t_a < t_b both Float32 times (see section 5), U = [(t_a - t_i)/d,
(t_b - t_i)/d] computed outward, h = U.High - U.Low. Emitted keys K_a = Round(Normalize(Sample(t_a))) and K_b likewise,
where Round casts the double-normalized components to Float32. Let S(u) be the exact-real curve and L(u) the straight
line between S(u_a) and S(u_b) at the same u. Reference direction r = K_a + K_b (exact doubles), require |r| > 0.
Projection floor p = the smallest of: the minimum of `v . r/|r|` over the four component intervals of the final
result widened by E (each component interval `[V_c.Low - E_c, V_c.High + E_c]`), K_a . r/|r| and K_b . r/|r|
(all outward, as `ProjectionMinimum` does). Require p > 10^-12 times the largest norm among the segment's six source
quaternions; otherwise refuse ("normalization is not provably nonzero").

The SO(3) error at any t in [t_a, t_b] is bounded by the outward sum of:

1. **Curve to chord.** If not `discontinuous`: dev = (h^2 / 8) * sqrt(sum over c of Mag(D2_c)^2), the linear
   interpolation remainder bound applied per component. If `discontinuous`: dev = the diameter of the widened value
   box, sqrt(sum over c of (V_c.High - V_c.Low + 2 E_c)^2), which encloses both S(u) and L(u) regardless of jumps.
   Term = 2 * dev / p. (Justification: a straight segment between two vectors whose projections on r/|r| are at
   least p keeps that projection, so the arc length of its projection onto the sphere is at most its length over p;
   quaternion-space angle at most dev/p, SO(3) angle twice that.)
2. **Sampler rounding at the endpoints.** The emitted keys are directions of the Float32 sampler, not of S. Term
   = 2 * 2 * E_max / p, where E_max is the largest Euclidean E, sqrt(sum E_c^2), over every whole segment of the
   track evaluated once with U = [0, 1] (a pre-pass; one E_max for the track). The factor 2 covers the internal-key
   case where the emitted key comes from the preceding segment.
3. **Unequal endpoint magnitudes.** The raw sampler outputs have norms n_a, n_b; the player's nlerp between the
   NORMALIZED keys differs from the normalized chord between the raw outputs by at most tan(phi/2) * |n_b - n_a| /
   min(n_a, n_b), exactly the `chordWeight` term of `ModelGltfQuaternionSpline.GeometryError`; phi is the
   quaternion angle between K_a and K_b, computed as `HalfAngleTangent` does (refuse when the dot is not proved
   positive).
4. **Float32 rounding of the emitted keys.** As `GeometryError` computes `rounding`: 2 * (distance between the
   exact normalized direction and the Float32 key's direction) / (their projection floor).
5. **Nlerp against slerp.** 2 * tan(phi/2)^3, the bound `GeometryError` already proves in its remarks.

No time-displacement term exists: sub-interval boundaries are exact Float32 times and the keys are emitted at those
times, so the player's parameter (t - t_a)/(t_b - t_a) equals (u - u_a)/(u_b - u_a) exactly.

Accept the sub-interval when the sum is finite and at most `AllowedRadians`; record the sum in the output's
`MaximumErrorRadians`. Otherwise split at the Float32-rounded midpoint m = (float)((t_a + t_b) / 2): refuse if
m <= t_a or m >= t_b ("cannot prove the bound within Float32 time resolution"), refuse beyond depth 32 and beyond
4,194,304 proof nodes, exactly as the existing certificate does; count keys against `MaximumKeys`.

## 5. Traversal and emission

`Analyze` counts keys and the maximum error without allocating; `Emit` repeats the identical deterministic traversal
into exactly sized arrays, through `ModelGltfQuaternionSplineOutput`. For n = 1: one STEP key,
Round(Normalize(Sample(t_0))), error = term 4 only (the sampler returns the raw key; normalization is exact-real).
For n >= 2: add K(t_0); for each segment, certify [t_i, t_{i+1}] recursively, adding K(t_b) at every accepted leaf.
Pre-pass first: every segment with U = [0, 1] for E_max and for early refusals. Check the cancellation token at every
node.

Reuse `ModelGltfQuaternionSplineOutput`, `ModelGltfQuaternionSplineAnalysis`, `ModelGltfQuaternionInterval`,
`AllowedRadians` and `MaximumKeys` (make the two constants `internal` if they are private). Do not modify
`ModelGltfQuaternionSpline`'s cubic traversal; factor `HalfAngleTangent`, `ProjectionMinimum`, `Norm`,
`DifferenceNorm`, `Normalize` and `QuaternionIntervals` into an internal static helper class if you need them from
the new code, keeping their bodies byte-identical and the existing callers compiling.

## 6. Files and tests

Core: `SceneGamebryoSquad` and its `Sample` become `public` (additive; the doc says why). Nothing else in Core.

Media (new): `ModelGltfGamebryoSquadNode` (the section 3 arithmetic, a readonly struct), `ModelGltfGamebryoSquadCurve`
(the section 2 composition over nodes for one segment and one U, plus the Float32 emitted key), `ModelGltfGamebryoSquadCertificate`
(sections 4 and 5: Analyze and Emit), `ModelGltfGamebryoSquadRotation` (Plan and Lower, mirroring
`ModelGltfGamebryoRotation`: the row, the budget reservation, the refusal row with `animation.gamebryo-squad-unrepresentable`).
Media (modified): `ModelGltfTransformChannels` dispatches `GamebryoSquad` beside `GamebryoCounterWarpedNlerp` at all
three sites; `ModelGltfAdmission` drops the blanket refusal (the policy refusal moves into Plan); `ModelGltfAnimationMetadata`
drops the limitation for `PcFloat32`, keeps it for `Xbox360Estimate`, and writes `gamebryoSquadPolicy`. The direct
`SceneGltfBuilder` path keeps refusing an unlowered Squad track. Update `ModelGamebryoSquadAdmissionTests` to the new
behavior (PC plans convert; X360 and the direct builder refuse; a mixed clip with an X360 track refuses whole).

Media tests (new `ModelGlbGamebryoSquadTests`), each with a control that must fail:

- **Playback.** For each fixture, write a GLB through the planned writer, read it back with the vendored SharpGLTF,
  and compare the slerp between the emitted keys with `ScenePoseEvaluator` sampling of the source at 4,097 times per
  segment plus the Float32 neighbors of every key: SO(3) angle <= 0.1 degrees and <= the row's observed bound.
  Fixtures: the RE-24 receipt track from `SceneGamebryoSquadTests.ReceiptTrack` (copy its literals; the Media test
  project cannot see Core test helpers), a three-key track with a 120-degree segment and inner points 30 degrees off
  the keys, a segment crossing both FastNormalize thresholds (inner nlerp midpoint norm below sqrt(0.6521)), a
  track with a tiny segment (two keys 2 Float32 ulps apart in time), and the `p.W = 1 / q.W = 1e-8` segment-choice
  track from `InternalKeyUsesPrecedingSegment`. Control: a textbook Squad (exact slerp, no counter-warp, exact
  normalization) evaluated at the same times differs from the emitted playback by more than 0.1 degrees on the
  120-degree fixture, so the keys follow the engine curve, not the textbook one.
- **Rounding bound.** For the fixtures and 10,000 random triples of near-unit quaternions (components in
  [-1.1, 1.1], norms in [0.9, 1.1], seeded), compare `SceneGamebryoSquad.Sample` with a double-precision evaluation of
  section 2 at 64 times per segment: the Euclidean difference is at most the certificate's E for that segment
  (expose E through an internal accessor on the analysis for the test) on every sample. Control: an E computed with
  `r = 2^-30` instead of `2^-24` is exceeded by at least one sample.
- **Facts of section 2.** `f(x) + f(1-x) == 1` for the cubic at 10,000 random k and x in double within 1e-12;
  `(2f*u)*(1f-u) <= 0.5f` for every Float32 u in [0, 1] stepping through all 2^23 values in [0.25, 0.75] and 10,000
  elsewhere; the emitted key at every internal key time equals `Round(Normalize(Sample(t_i)))` bit for bit.
- **Refusals.** `Xbox360Estimate` refuses with `animation.gamebryo-squad-unsupported`; two keys one Float32 ulp
  apart in time with a 90-degree turn refuse with the Float32-resolution reason; a direct-builder Squad track
  refuses; cancellation during Analyze is an `OperationCanceledException`, not a fidelity row.
- **Row and metadata.** The row is Approximated with the observed bound below 0.1; metadata carries the interpolation
  and `gamebryoSquadPolicy`; the source track is unchanged after lowering (reference equality of its arrays).

Docs: `docs/scene-gamebryo-squad.md` replaces "GLB plans refuse..." with the certificate (sections 3 to 5 in prose,
the terms and their justifications, what is NOT claimed: the player's own Float32 slerp arithmetic, Blender, X360);
`docs/scene-curve-tracks.md` "Next implementation" drops the Squad certificate from the outstanding list. Both say
who did it and when.

## 7. What this does not claim

The player's slerp is taken as exact-real, as the existing certificate does. Blender still refuses Squad. The Xbox
360 estimate sampler is unimplemented and refuses. The certificate proves the export against `SceneGamebryoSquad`;
that sampler's agreement with the engine is RE-24's evidence, not this document's.

## 8. Implementation notes (2026-09-26, branch `bethesda/gamebryo-squad-certificate-v1`)

What the implementation does differently from sections 3 and 4 above, each for a measured reason, and the review
that drove it (`TestOutput/sa6-squad-certificate-20260926/review/VERDICT.md`, with the reviewer's Python
transliteration of the sampler and the certificate beside it):

- **Term 2 charges a local rounding bound and a start-key deviation, not a track-wide pre-pass.** A whole-segment
  E over U = [0, 1] explodes through the FastNormalize refinement's dependency problem (E of order 10 to 1000), so
  every leaf carries its own E over its own domain. Term 2 is 2 (D_a + 2 E) / p on both paths: |S(u) - L_F(u)| is at
  most the remainder plus max(|S(u_a) - F(u_a)|, |S(u_b) - F(u_b)|), and the interior |F(u) - S(u)| adds one E.
  F(u_b) is this segment's own Float32 path at a point of the leaf's domain, so on a settled leaf its branches are
  the settled ones and it is within E of S(u_b); within a segment the same holds for F(u_a), so D_a = 0. At an
  internal key t_i the start key F_{i-1}(1) came from the PREVIOUS segment's Float32 path, whose FastNormalize
  branches may differ from this leaf's settled ones (the review measured the amplitude jump at 1.54e-4 for the outer
  threshold and 1.2e-5 for an inner one); D_a is then the diameter of the previous segment's widened point enclosure
  at amount 1 (x / x is exact, so the sampler evaluates exactly there; a branch it could have taken makes the point
  enclosure straddle; S_i(0) = S_{i-1}(1) in exact arithmetic lies in the same enclosure). The first version of the
  repair charged the previous LEAF's whole widened box instead, which can equal the entire budget and walked every
  wide fixture to the Float32-resolution wall; the point enclosure is the right quantity.
- **The counter-warp's `amount <= 0.5` straddle is not a discontinuity.** Both branches are one cubic in exact
  arithmetic (section 2, first fact), so the hull of their (V, D1, D2) enclosures is a valid smooth enclosure and
  only the rounding bound differs (E = max). Flagging it discontinuous forced the box path in a +-0.0055 band
  around every segment's middle and spent about a third of the keys there.
- **E_u charges the quotient's own magnitude** (`rho(U, quotientError)`, not `rho(U, 0)`), and **durations are
  interval differences** of the two Float32 key times, since a double difference of two floats is exact only when
  their exponents differ by at most 29.
- **An overflowed enclosure is unbounded and splits; it never throws.** The node keeps an overflowed bound as
  positive infinity and `Enclose` returns the whole line for a non-finite endpoint; nothing unbounded can be
  accepted, so this cannot certify more than a finite enclosure would.
- **Tests compare per leaf.** An internal `Leaves` accessor returns the accepted sub-intervals with their certified
  errors, and the playback test checks each against `ScenePoseEvaluator` at 64 Float32 times inside it (the review
  measured the thinnest true/certified margin at 10 percent, Taylor-dominated, which a track-wide comparison would
  not catch). The random near-unit class (norms 0.9 to 1.1, arbitrary directions) is stated as it is: each track is
  refused at Float32 resolution or accepted with every leaf holding, and both outcomes occur; unit-norm keys with
  turns of at most 120 degrees and inner points within 30 degrees are all accepted.

Results (BMT `TestOutput/sa6-squad-certificate-20260926/run-5/`): Core and Media builds 0 errors, no warning in the
changed files; Media Models 610 of 610; Core rotation classes 57 of 57. The feasibility prototype
(`prototype/RESULT.md`) predicted the key counts before the flag change: 8 to 24 keys for 10 to 60-degree segments,
about 700 for 120 and 170 degrees; the flag change removes roughly a third of those.
