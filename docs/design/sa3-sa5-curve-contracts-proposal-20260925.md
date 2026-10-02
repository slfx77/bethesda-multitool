# SA3 to SA5: one scalar-curve contract for Euler, property and per-target morph tracks

Status: proposal for the foundation's review, 2026-09-25. Nothing in Shared is edited until the foundation agrees
the shape and the file split (section 7). Owner: bethesda (SA3 to SA6 assigned at Shared `a5f9e1b`). SA6 (quaternion
TBC and QUADRATIC rotation) is section 6a, added after RE-24 settled the engine rule.

Why: the owner ruled that every vocabulary gap is closed before the cut-1b switch-over (D3), with one decode path
(D5) and no lossy extras (D11). Three of the gaps are SA3 (per-axis Euler rotation), SA4 (material, texture-layer and
visibility property tracks) and SA5 (morph weights with per-target timing). The foundation asked that they "share
scalar-curve representation and evaluation across Euler, property and morph tracks".

Sources:
- the engine rules: `docs/formats/nif-animation-engine-behavior-20260925.md` (RE-18 Hermite, RE-19 TBC, RE-20
  Euler, RE-22 clocks, RE-23 morph Base weight);
- the corpus: `TestOutput/cut1b-prep-20260925/animation-vocabulary-needs.md`;
- the Shared facts: a read-only survey of Shared `51ef128` (section 2).

## 1. What the retail data needs

| Gap | Retail population (FNV + FO3, 65,764 files) | Engine rule |
| --- | --- | --- |
| SA3 Euler | 67,206 rotation blocks; blocked interpolators: `.kf` 17,966, `.nif` 52,138. Axis key types in FNV: QUADRATIC 8,387, LINEAR 439, TBC 210 (embedded controllers). | RE-20: three independent scalar curves X, Y, Z (own times, own key type and derived tangents), a hold after each curve's last key, composed as q = qZ(z)·qY(y)·qX(x) |
| SA4 properties | Material and texture: 11,878 embedded plus 18,085 in sequences. Visibility: 10,781 plus 15,551. Compressed float and Point3 B-splines: 275 and 439. | Material colors are Point3 key groups, alpha and UV components are float key groups, visibility is bool (step) |
| SA5 morph targets | Embedded: 43 of 103 non-blend controllers do not fit one shared time vector. Sequences: 336 of 500 groups with non-Base targets do not fit (199 keyed plus constant, 95 differ, 36 B-spline, 6 all constant). | RE-23: with Relative Targets the Base weight is ignored; each other target has its own float key group |

Every curve above is one of the forms Shared already evaluates for transform tracks: LINEAR, STEP (the engine's
CONST), Hermite (QUADRATIC, RE-18), TBC with endpoints (RE-19), or a float/Int16 B-spline. What differs is the
width (1 or 3) and what the curve drives.

## 2. What Shared has now (`51ef128`)

- No curve object exists apart from a target. `SceneTransformTrack` carries every curve feature (times, values,
  interpolation, state, static value, spline, TBC parameters and endpoints, source policy), but it is bound to a node
  and a Translation, Rotation or Scale property, with the width fixed at 3 or 4
  (`SceneTransformChannelValidation.cs:22`). `SceneMorphTrack` has any width but one time vector for all targets and
  no state, static value or spline (`SceneAnimationValidation.cs:36-43`).
- The samplers are already width-generic but internal: `ScenePoseChannel.Sample` and `SampleTbc`, plus the public
  `SceneBSplineCurve.Sample` and `SceneTbcTangents`.
- The pose workspace publishes local and world matrices, morph weights and world positions. It has nothing for
  materials, UV transforms or node visibility. The layer mixer keeps one morph weight per node.
- The GLB writer has no KHR_animation_pointer or KHR_node_visibility code. The vendored SharpGLTF can write pointer
  channels (LINEAR/STEP only), material property channels and visibility channels. KHR_texture_transform is written
  statically today.
- The Blender writer drops every clip (SA9).
- The approved design already fixes the Appendix B rows (`model-document-design-20260923.md` :861-884): Euler becomes
  resampled quaternions within 0.1 degrees in GLB (Approximated) and Euler F-curves in Blender (Exact); material
  colors, alpha, emissive strength and UV transforms become pointer channels (Exact); ambient is Metadata; visibility
  uses KHR_node_visibility plus a scale fallback.

## 3. Proposed contracts (Core, all public, all additive)

**`SceneCurve`**: one source curve with no target.
- `ComponentCount` (1 to 4), `State` (Keyed or Constant; NotDriven stays a track-level concept), `Times`, `Values`
  (key-major; Hermite and CubicSpline store in, value, out), `Interpolation` (Linear, Step, CubicSpline, Hermite,
  Tbc, BSpline), `TbcParameters`, `TbcEndpoints`, `Spline` (`SceneBSplineCurve`, ComponentCount equal), `StaticValue`.
- These are exactly `SceneTransformTrack`'s payload rules with the width made explicit. Validation reuses
  `ValidateValues`, `SceneTbcTrackValidation` and the B-spline checks. Clocks stay on tracks, because every source
  curve group shares its controller's clock.
- `Sample(float seconds, Span<float> destination, CancellationToken)`, public, over the existing samplers. Semantics
  unchanged: hold before the first and after the last key.
- Engine note: the engine extrapolates before a scalar curve's first key (RE-20 rule 7), but retail never reaches
  that point (every clock starts on the first key: 210,294 of 210,294 axis/driver pairs). The reader refuses the
  rare case rather than Shared adding an extrapolation mode.

**`SceneEulerRotationTrack`**: `(nodeIndex, SceneEulerOrder order, SceneCurve x, SceneCurve y, SceneCurve z,
SceneAnimationClock? clock, SceneAnimationTrackSourcePolicy? sourcePolicy)`, in a new
`SceneAnimation.EulerRotationTracks`.
- `SceneEulerOrder` has one member now, `Xyz`: X is applied first, then Y, then Z, with positive angles in radians,
  q = qZ(z)·qY(y)·qX(x) (Hamilton product, RE-20 rule 9). An enum leaves room for other orders.
- Each axis curve has ComponentCount 1.
- Validation: the node needs `LocalTrs`, and one rotation driver per node per clip. The existing (node, Rotation)
  uniqueness rule covers an Euler track and a quaternion track together.
- Pose: sample the three curves, compose, and write `SceneTrs.Rotation`, exactly where a quaternion Rotation track is
  written today. Layers blend the composed quaternion as they blend any rotation.

**`ScenePropertyTrack`**: `(ScenePropertyTarget target, SceneCurve curve, SceneAnimationClock? clock,
SceneAnimationTrackSourcePolicy? sourcePolicy)`, in a new `SceneAnimation.PropertyTracks`.
- `ScenePropertyTarget(ScenePropertyKind kind, int index, int layerIndex = -1)`.
- `ScenePropertyKind` and required curve width:
  - material, width 3: `MaterialBaseColor`, `MaterialSpecularColor`, `MaterialAmbientColor`, `MaterialEmissiveColor`;
  - material, width 1: `MaterialAlpha`, `MaterialEmissiveStrength`;
  - texture layer (material index plus layer ordinal in `SceneMaterialSource.Layers`), width 1: `LayerOffsetU`,
    `LayerOffsetV`, `LayerScaleU`, `LayerScaleV`, `LayerRotation`;
  - node, width 1: `NodeVisibility` (Step only; values 0 or 1).
- One track per target per clip. The target must exist, and the layer must have a texture binding.
- Pose: new published outputs, one set per material, per (material, layer) and per node. They default to the
  document's static values: `BaseColor`, `SpecularColorFactor`, `EmissiveFactor`, `EmissiveStrength`, the layer's
  static `SceneTextureTransform`, and visible. The renderer's adoption stays separate (question 3).

**`SceneMorphTargetTrack`**: `(nodeIndex, int targetIndex, SceneCurve weight, SceneAnimationClock? clock,
SceneAnimationTrackSourcePolicy? sourcePolicy)`, in a new `SceneAnimation.MorphTargetTracks`.
- The weight curve has ComponentCount 1. Each (node, target) appears once per clip, and a node cannot have both a
  whole-vector `SceneMorphTrack` and target tracks in one clip.
- Targets without a track keep `SceneMorphDefaults`. A Base weight never appears (RE-23: the reader never emits one).
- Pose: write single weights. The mixer then needs a weight per (node, target) instead of one per node.

All new constructor parameters are optional and trailing, so current call sites compile unchanged. Pinned consumers
rebuild against the new commit. Composition rebases the new node, material and layer references
(`ModelCompositionAnimation.cs`), and the dump context registers the new types.

## 4. Writers

GLB (Media):
- **Euler:** the axes are sampled and composed into quaternion keys, and the result is certified like the Gamebryo
  and B-spline rotation stages (maximum 0.1 degrees, Approximated, adaptive). The bound comes from per-segment
  derivative bounds of the three axis curves; the angular speed is at most the sum of the axis speeds.
- **Morph targets:** the per-target curves are merged onto one key set, because glTF allows one weights sampler per
  node. The merge uses the same machinery as SA1: `ModelGltfLoweredCurve` samples each target at the union of key
  times, and CUBICSPLINE splits are exact. Mixed interpolations unify: LINEAR with CUBICSPLINE becomes CUBICSPLINE,
  and STEP in a mix uses the one-step key rule.
- **Properties:** pointer channels (KHR_animation_pointer):
  - `baseColorFactor` (RGB and alpha merged into one vec4 channel);
  - `KHR_materials_specular` `specularColorFactor`;
  - `emissiveFactor`;
  - `KHR_materials_emissive_strength`;
  - `KHR_texture_transform` offset, scale and rotation (U and V merged).
  Ambient is Metadata. Visibility uses KHR_node_visibility, plus the design's scale fallback on an inserted wrapper
  node. SharpGLTF's pointer channels are LINEAR/STEP only, so Hermite and TBC property curves need either cubic
  pointer support or bounded resampling (question 4).

Blender (SA9, later): Euler as native Euler F-curves (Exact), morph targets as shape-key F-curves, properties as
material and mapping node F-curves.

## 5. What BMT does with them

The cut-1b reader maps:
- NiTransformData Euler blocks to `SceneEulerRotationTrack`;
- NiMaterialColorController, NiAlphaController, NiTextureTransformController and NiVisController (and their sequence
  interpolators) to `ScenePropertyTrack`;
- NiGeomMorpherController targets 1..n to `SceneMorphTargetTrack`, or to one `SceneMorphTrack` when all targets share
  one time vector and interpolation, which keeps the common case unchanged.
Each gets its source policy (SA7). The coverage classifier drops the corresponding 'later-cut' reasons.

## 6. Delivery in slices, each on a `bethesda/` branch with its own claim

1. Core contracts, validation and `SceneCurve.Sample`, with tests (no evaluator change). Unblocks the BMT reader's
   component work.
2. Pose evaluation: Euler and morph targets in `ApplyClip` and the mixer; property outputs published by the workspace.
3. GLB: Euler certification, morph-target merge, then property pointer channels and visibility.
4. Blender (SA9).
5. SA6 after RE-24.

## 6a. SA6 addendum: engine quaternion Squad (after RE-24, 2026-09-25)

RE-24 (`docs/formats/nif-animation-engine-behavior-20260925.md`, confirmed with corrections) settles the engine rule:
- TBC (key type 3) and QUADRATIC (key type 2) rotations both evaluate as Squad(u; q_i, a_i, b_(i+1), q_(i+1)) =
  Slerp(2u(1-u), Slerp(u, q_i, q_(i+1)), Slerp(u, a_i, b_(i+1))), with the weight formed in Float32.
- Every Slerp is RE-17's counter-warped nlerp with FastNormalize and no sign flip. A shortest-path flip inside the
  Squad is up to 55 to 94 degrees wrong on retail segments. A textbook exact Squad differs by up to 2.43 degrees
  (9.79 on the 211 segments whose inner points are more than 90 degrees apart).
- The inner points come from the engine's own Float32 Log and Exp. For TBC they come from CalculateDVals; for
  QUADRATIC, from Shoemake's Intermediate with self-neighbours at the ends. Keys are chain-flipped and W-clamped but
  not normalized.
- Retail: 901 distinct TBC rotation groups (1,391 instances, 46,662 keys); 0 QUADRATIC rotation groups.

Proposed contract:
- **`SceneInterpolation.GamebryoSquad`** for keyed quaternion Rotation tracks only. Its values use the CubicSpline
  layout with quaternions: per key, the incoming inner point b_i, the key q_i and the outgoing inner point a_i. The
  reader computes the inner points engine-faithfully (RE-24 implementation rule), so Shared never re-derives
  Kochanek-Bartels or Intermediate points and stores no T, C, B for rotations.
- **Sampling** reuses `SceneGamebryoRotation.InterpolateComponents` (the SA12 counter-warped nlerp, signs as
  authored) for the three slerps and forms the weight in Float32. The final normalization follows SA12's stated
  policy: a normalized orientation, not FNV's FastNormalize amplitude. Key normalization is not assumed; validation
  accepts the chain-flipped, W-clamped keys that `ValidateRotations` already accepts. Before the first key, Shared
  holds (the engine extrapolates, but retail never reaches that point).
- **GLB:** bounded LINEAR conversion within 0.1 degrees (Approximated), like SA12. The Squad of three normalized
  nlerps is not polynomial, so the quaternion certificate needs a Squad curve adapter. This extends your SA12
  machinery: do you want to own it, or should I draft it under a claim?
- **Blender (SA9):** resampled quaternion keys within the same bound, or native F-curves if one proves exact.

## 7. Questions for the foundation

1. **Shape.** Is one public `SceneCurve` with three new track collections the right seam, or would you rather extend
   `SceneTransformTrack` and `SceneMorphTrack` in place?
2. **Evaluator and mixer.** Slice 2 edits `ScenePoseEvaluator`, `ScenePoseLayerMixer`, `ScenePoseBlendWeights`,
   `ScenePoseState` and `ScenePoseWorkspace`, which are yours. Do you want Bethesda to draft them under a claim, or
   to take slice 2 yourself?
3. **Property outputs and the renderer.** Should the workspace publish animated material, layer and visibility
   values now, with the D3D12 renderer adopting them later? The renderer bakes UV transforms at staging today.
4. **Cubic pointer channels.** Hermite and TBC property curves exist in retail (their count per property kind is not
   measured yet; BMT will measure it before slice 3). They need either a cubic pointer-channel path in the vendored
   SharpGLTF, or bounded LINEAR resampling. Which do you prefer?
5. **File claims.** Slice 1 would add, under `src/Slfx77.Multitool.Core/Models/`, the files `SceneCurve.cs`,
   `SceneCurveValidation.cs`, `SceneEulerRotationTrack.cs`, `SceneEulerOrder.cs`, `ScenePropertyTrack.cs`,
   `ScenePropertyTarget.cs`, `ScenePropertyKind.cs` and `SceneMorphTargetTrack.cs`. It would modify
   `SceneAnimation.cs`, `SceneAnimationValidation.cs`, `Composition/ModelCompositionAnimation.cs` and
   `Inspection/Dump/ModelDumpJsonContext.cs`, plus tests under `tests/Slfx77.Multitool.Core.Tests/` and a new
   `docs/scene-curves.md`. Are any of these in flight on your side?
