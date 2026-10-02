# Gate 1a oracle harness: hops B, C and E

Independent Python oracles for the cut-1a writers (design `docs/design/model-document-design-20260923.md`
section 7.2, rows B, C and E, plus "Basis and units"). Every oracle parses the produced bytes itself and
compares them with the exact JSON dump the reader emits; nothing here imports, links or executes writer code.
Standard library plus numpy and Pillow only (Pillow 12.1.0 on this host; its `DdsImagePlugin` decodes
DXT1/BC1, DXT3/BC2, DXT5/BC3, ATI1/BC4U/DX10 BC4_UNORM and ATI2/BC5U/DX10 BC5_UNORM, measured on the smoke
textures and on synthetic files; `dds_decode.py` records which decoder handled each image).

## Files

| File | Purpose |
|---|---|
| `glb_reader.py` | GLB container (12-byte header, JSON and BIN chunks), accessors with componentType/type/normalized/byteStride, sparse accessors and accessors without a bufferView fail loudly, images by bufferView, every `extensions` site recorded. |
| `dds_decode.py` | DDS header parse; Pillow decode to RGBA8; this file's own BC1/BC2/BC3/BC4/BC5 and masked-format decoders as a cross-check (the receipt records the max difference between the two decoders); `swap_bc1_endpoints` for the hop-B control. |
| `package_reader.py` | Blender package v2/v3: `manifest.json`, `streams/*.bin` as `<f4`/`<i4` tuples (attribute streams in their declared type), `images/<sha256>.<ext>` with the manifest SHA-256 verified; `mutate_package` for the controls. |
| `dump_reader.py` | The exact dump (schema 1): vertices, indices, tangents, additional UV sets, skin influences, morph targets, faces, points, attribute streams (base64 with declared component types), images (original / standard / derivation payloads, base64, SHA-256 verified), units, basis, materials, nodes, skins. JSON `-0` is kept as -0.0 (Python's `json` would read it as integer 0). |
| `writer_rules.py` | The writer's declared conversions re-derived from the Shared sources (each function names its source lines): the coordinate parent matrix, normal normalization, the tangent rule of pinned Shared 591d083 (`tangent_plan`, with `classify_tangents`, `referenced_vertices`, `expected_tangent_rows` and the tuple form `normalize_tangents`: normalization, the unreferenced zero-direction placeholders of 1c2f5b7 and every row the writer emits for them; for refusal diagnostics only, the previous pin's tangent refusal `previous_pin_invalid_tangents` and both writers' refusal texts `TANGENT_REFUSAL_TEXTS`), the atan2 direction error (`direction_degrees`), weight normalization, four-slot joint sets, index reversal, MASK cutoff, gloss to roughness, sRGB EOTF for primary colors, integer normalization, absolute morph deltas, BC5 Z reconstruction, green inversion and companion packing, double-sided derivation. |
| `selfcheck_tangents.py` | Hand-vector self-check of the tangent and direction rules (exit 1 on any failed expectation): exact normalization, the unchanged unit case, both sides of the float32 tolerance boundary, every invalid class, the tiny angles the acos form of the previous writer rounded to zero, and (591d083) the placeholder bits of both handedness signs, a referenced zero lane (a degenerate triangle included) staying invalid, handedness and finiteness tested before the zero test, a subnormal direction normalized rather than filled, the lazy reference mask, the row list of every branch of `NormalizeTangents`, and hop E's use of the rule on the `gate1a_synthetic` GLB against a package of its source lanes (`hop_e.compare_geometry`): the placeholders written out by hand pass, three wrong placeholder forms fail as TANGENT value mismatches, and a zero lane the PACKAGE's index stream references (a real triangle, or only a degenerate one) is refused as unsupported although the GLB carries the placeholder there. |
| `hop_b.py` | Hop B: validator, accessors, nodes, materials, images, fidelity rows; seven controls. The three accessor controls are table-driven (`GEOMETRY_CONTROLS`, `run_geometry_control`), the MASK control is `run_mask_control`, and `synthetic_control_exercise(control name)` runs any of those four on the `gate1a_synthetic` set through the same mutation, pristine checks and verdict, for the driver's use when no corpus sample reaches the control; its `detected` requires the set's own pristine accessor, fidelity and material checks to pass, so a set that drifted from `writer_rules` cannot carry the control. |
| `gate1a_synthetic.py` | The hand-built hop-B artifact set (one implementation): the two-primitive dump (since 591d083 the first primitive carries two unreferenced zero-direction tangent lanes, one of each handedness, beside its normalized lane) and one MASK material, the GLB the pinned writer would emit for it, the fidelity rows (the filled, placeholder and geometry/tangent-normalization rows written out by hand); `write_pristine_set(dir)` writes `dump.json`, `model.glb` and `fidelity.json` and returns their SHA-256s; `build_package(path)` writes the Blender package (v3) of the same primitives with their source lanes, for hop E. Used by `selfcheck_hop_b_paths.py`, `selfcheck_tangents.py` (hop E) and `hop_b.synthetic_control_exercise`. |
| `selfcheck_hop_b_paths.py` | Variants of the `gate1a_synthetic` set exercising the hop-B paths no produced artifact of the 2026-09-24 manifest reaches (exit 1 on any failed expectation): the placeholder and reconstructed zero-normal lanes and the reconstructed unit-length bound, the derived `geometry/normals` rows, the morph-tangent row's presence, the tangent placeholder lanes (four wrong stored forms), the invalid tangent classes that stay refused, every tangent row the writer can and cannot emit (counts, featureIds, outcomes, bounds, missing rows), duplicated rows (in the report only, or in both the report and the carrier: filled, tangent placeholder, `geometry/normals`, normal placeholder and morph-tangent rows, and a wrong-count filled row ahead of the right one), the refusal evidence's previous-pin verdict and quoted refusal text, placeholders alone driving the morph-tangent row, the three accessor controls and their masking cases, the w flip's lane choice, the MASK control, the driver's synthetic exercise of all four, and that exercise reading NOT detected on a drifted set (a wrong stored TANGENT lane, a wrong stored tangent placeholder, a fidelity report missing a row, a wrong MASK cutoff) even where the control's own verdict is a detection. |
| `selfcheck_runner_controls.py` | Self-check of the driver's gate-level control policy on hand-built receipts (exit 1 on any failed expectation): a never-reached control with the synthetic exercise forced to not detected reads NEVER EXERCISED and fails the gate, forced to detected reads EXERCISED SYNTHETICALLY and passes, no exercise or a raising one stays NEVER EXERCISED, an exercise reporting detected on a set whose own pristine check failed stays NEVER EXERCISED (forced, and through the real dispatch on a drifted `gate1a_synthetic` set), a not-detected or detected control is never exercised synthetically, the real dispatch runs hop B's three accessor controls and its MASK control to a detection, and never-reached tangent placeholder and MASK controls reach EXERCISED SYNTHETICALLY through it (NEVER EXERCISED when forced not detected or on a drifted set); and the refusal-evidence detail of a partial sample (`record_refusal_evidence`), which names the previous pin's tangent refusal and the quoted refusal text. |
| `tangent_frame_census.py` | A RECORD, not a hop (the driver does not run it): does each GLB's TANGENT follow glTF's frame, judged from the GLB's own POSITION, NORMAL, TEXCOORD_0 and indices (TANGENT.xyz along +dP/du, cross(N, T) * w along -dP/dv, the per-vertex frame and exclusion classes of TestOutput/nif-tangent-frame-20260928's independent measurement). Hops B, C and E compare the artifacts with the dump, so they pass whichever stored array the reader types; this record is what sees the mapping. It also counts glTF's per-triangle rule, which no per-vertex rate can see: the triangles whose three TANGENT.w differ ("its tangent space is considered undefined"), how many are UV-non-degenerate and how many touch a mirror-seam vertex (reported, not gated: a source frame that flips across a shared seam vertex, which the reader may not split). `--min-along` / `--min-w` make it a check; `--selfcheck` pins it on hand-built quads (the swapped axis, the DirectX sense, a constant w and a mirror seam each fail or are excluded as they must, and a triangle across a seam whose vertices carry different w is counted once). |
| `hop_c.py` | Hop C: package streams and images versus the dump; one control. |
| `hop_e.py` | Hop E: GLB versus package; one control. |
| `run_gate1a.py` | Driver over a `gate1a-artifacts/1` manifest; writes `receipt.json`, `receipt.md`, one `samples/NNNN_<id>/result.json` per sample as soon as it completes, and for every control the corpus never reached the artifacts of its synthetic exercise under `synthetic/hop-<hop>/<control>/` (`model.glb`, `dump.json`, `fidelity.json` and the mutated copy, e.g. `control-placeholder-normal.glb`). |
| `gate1a_resolve.py` | Cover-manifest resolution (source, alsoIn, Steam Final Data folder, Steam install), SHA-256 checked, with its own v103/v104 BSA reader. |
| `produce_gate1a.ps1` | The production driver (PowerShell 7): runs the BMT exe four times per cover file, one process at a time at BelowNormal priority, records exit codes and per-command `timing` (elapsed seconds; peak working set sampled once per second while the process runs, the OS peak up to the last sample) into `artifacts.json`. Two rules learned on the first corpus run (2026-09-24): `mesh convert` treats its output argument as a directory for an archive entry and writes `<output>/<archive name>/<entry path>.glb` under it, so the driver locates the produced `*.glb` rather than expecting a file at the output path; and a producing command that exits nonzero because the writer refused the item (`Unsupported:`), skipped it (`Skipped:`, no drawable geometry), reported a `"failure"` in its fidelity JSON or declined the input (`reason: later-cut(...)`) marks the sample `partial` with `partialReasons`, so `run_gate1a.py` reports the missing hop as not applicable; any other nonzero exit stays a producing failure. |
| `smoke-artifacts.json` | The self-check manifest over the artifacts that already exist under `TestOutput/` (both samples are marked `partial`). |

## How to run

Production (the owner runs the exe; this harness never builds and never launches Blender):

```powershell
pwsh -NoProfile -File tools/scripts/gate1a/produce_gate1a.ps1 -Exe src\BethesdaMultitool\bin\Release\net10.0\BethesdaMultitool.exe -RunOracles
```

Options: `-Manifest` (default: the checked-in `cut1a-cover-manifest.json`), `-SampleRoot` (default
`BETHESDA_TEST_DATA_ROOT` or `C:\dev\Multitool\BethesdaMultitool\Sample`), `-OutRoot`
(default `TestOutput/gate1a-<stamp>`), `-Roles cover,floor`, `-Limit N`, `-Filter <substring>`,
`-TimeoutSeconds 1800`, `-Python python`. Per file the driver runs, in order and one at a time:

```
mesh convert  <input> <out>/model.glb   --format glb --overwrite [--entry e] [--game fnv|fo3] [--platform x360|ps3]
mesh package  <input> <out>/package.zip --overwrite ...
mesh dump     <input> --native --output <out>/dump.json --overwrite ...
mesh fidelity <input> --format glb --json ...            (stdout is fidelity.json)
```

`--game` is fnv or fo3 from the manifest's game (omitted for the Oblivion and Skyrim files, whose units the
reader leaves unestablished); `--platform` is x360 or ps3 for a big-endian key, read off the PRIMARY source
exactly as `Cut1aCoverFile.ConsolePlatform` does, even when the bytes were finally read from the other
console's archive (only byte-identical copies pass the SHA-256 check). No memory or gate option is ever
passed: the exe's own memory gate decides, and a refusal is recorded as that command's exit code. A failed
command leaves its artifact null and the run continues; `artifacts.json` records every exit code and log.

Oracles alone, over an existing artifacts manifest:

```
python tools/scripts/gate1a/run_gate1a.py <OutRoot>/artifacts.json --out <OutRoot>/receipt
```

The validator is located through `shared/Multitool.Shared/tools/vendor/gltf-validator/gltf_validator_tool.py
locate --json`; a validator that is not restored is a hard failure of the run (exit before any hop), never a
skip. `--validator <exe>` overrides the lookup. `--limit` and `--filter` are recorded in the receipt
(`manifest.limit`, `manifest.filter`, `manifest.samplesInManifest`, `manifest.subset`) and the markdown header
says **SUBSET** when the run did not cover the whole manifest. The receipt's `tools.oracle` records the SHA-256
of every script in this directory and the git HEAD, so a receipt can be tied to the oracle that produced it.

Robustness: each hop's `run` catches an `OracleError` (the hop cannot run: `error`) and any other exception
(an oracle defect: `error` plus `traceback`), keeping the checks already recorded; the driver has a second
guard so one sample's exception never discards the samples before it. A sample the manifest declares `partial`
(a GLB-writer refusal or skip, a declined `.kf`) reads not applicable on the hops whose inputs are absent, never
as an error; a missing artifact on a sample not declared partial is a producing-command failure, recorded as an
error and counted in `samplesIncomplete`, which fails the gate. A validator timeout (600 s) or a non-JSON
validator report reads as a hop-B `OracleError`. Each sample's receipt entry is written to
`samples/NNNN_<id>/result.json` as soon as the sample is complete, so an interrupted run leaves the finished
samples on disk and the receipt can be rebuilt from them.

Self-check over the existing smoke artifacts (what this harness was developed against):

```
python tools/scripts/gate1a/run_gate1a.py tools/scripts/gate1a/smoke-artifacts.json --out TestOutput/gate1a-selfcheck-<stamp>
```

Self-check of the tangent and direction rules on hand vectors, and of hop E's use of the tangent rule on the
`gate1a_synthetic` GLB and package (no artifacts needed; exit 1 on any failure):

```
python tools/scripts/gate1a/selfcheck_tangents.py
```

Tangent-frame record over produced GLBs (no dump, package or writer code read; exit 1 only below a threshold given on
the command line, and on `--selfcheck` failures). On the 2026-09-28 pin-0152179 GLBs, produced by the reader before
the tangent mapping fix, it records TANGENT along +dP/du on 0.06% of 92,315 scored vertices and along +dP/dv on 98.6%
(the stored V axis typed as TANGENT); after the fix the independent measurement predicts about 99.9% along +dP/du.
Per triangle, 2,652 of those GLBs' 89,405 triangles carry mixed w (2,474 UV-non-degenerate; 1,009 of them in
`landscape/trees/wastelandhedgerow04.nif`, 998 at a UV-winding seam); the fix leaves w bit-identical, so the count is
a source property, not a regression:

```
python tools/scripts/gate1a/tangent_frame_census.py --selfcheck
python tools/scripts/gate1a/tangent_frame_census.py <OutRoot>/artifacts.json --out <OutRoot>/tangent-frame --min-along 0.998
```

Self-check of hop B's zero-normal, fidelity-row and control paths on the hand-built GLB, dump and report of
`gate1a_synthetic.py` (no artifacts needed; exit 1 on any failure; the only evidence for those paths until a cover
sample reaches them, and the set the driver's synthetic control exercise runs on):

```
python tools/scripts/gate1a/selfcheck_hop_b_paths.py
```

Self-check of the driver's gate-level control policy (synthetic exercise of a control the corpus never reaches;
no artifacts needed; exit 1 on any failure, and it proves the policy can fail):

```
python tools/scripts/gate1a/selfcheck_runner_controls.py
```

## What each hop proves

**Hop B (document to GLB).**
1. *Validator*: the pinned Khronos validator 2.0.0-dev.3.10 reports 0 errors (warnings are recorded; the
   report JSON is saved beside the receipt).
2. *Accessors*: for every primitive (paired by mesh name and the `multitoolPrimitiveName` extra) POSITION,
   TEXCOORD_n and morph deltas equal the dump bit for bit, the sign of zero included (the writer
   copies float bits verbatim, `SceneGltfBuilder.cs` lines 428-433, and the dump keeps `-0`; a `-0`/`+0`
   difference is a mismatch unless `check_geometry` is called with `allow_signed_zero=True`, which no runner
   does); TANGENT equals the dump tangents after `ModelGltfGeometry.NormalizeTangents` (pinned Shared 591d083,
   lines 252-324, `writer_rules.tangent_plan`), tested in the writer's order: a lane is INVALID when x, y or z is
   not finite or w is not exactly +1 or -1 (line 270, first, so an unused slot does not relax either); a lane whose
   double squared length is 0 is INVALID when any index of the primitive references its slot (lines 272-275; the
   reference mask is `writer_rules.referenced_vertices`, ModelGltfGeometry.ReferencedVertices lines 331-345: every
   corner of every triangle, degenerate ones included, the same marking the normal placeholders use) and is a
   PLACEHOLDER otherwise, stored as exactly `(1, +0, +0, w)` with the authored w (line 281; since Shared 1c2f5b7, the
   previous pin 0fe3212 refused it); any invalid lane makes the item Unsupported, so a GLB that exists beside such a
   dump FAILS the check (`unsupported`); a lane is UNCHANGED when the float32 `Vector3.LengthSquared` satisfies
   `MathF.Abs(squared - 1) <= 0.0001f` (line 286); every other lane becomes `(float)(component / Math.Sqrt(double
   squared))` with w kept (lines 289-296), and the receipt records `tangentsMaxDegrees` per primitive. When a
   primitive has placeholders, its authored lanes are compared first under the `TANGENT` label and its placeholder
   lanes then as their own stream, `TANGENT (unreferenced zero-tangent placeholders, expected (1, 0, 0, w))`, each
   mismatch naming the accessor lane, and the receipt records `tangentPlaceholders` (lanes, counts, handedness);
   NORMAL equals the dump normal
   after the writer's normalization (`(float)(component / sqrt(double squared length))`, only for non-unit, non-zero
   normals; Flat mode has no NORMAL accessor) on every non-zero lane, and the zero lanes follow
   `SceneVertexNormals.ReconstructMissing(fillUnreferenced: true)`: a zero lane that no triangle index references
   (references are counted over every index of every triangle, degenerate ones included) must be exactly
   `(0, 1, 0)` (Vector3.UnitY, bit for bit, +0 not -0), a referenced zero lane must be unit length within 2^-22 of
   squared length, the float32 rounding bound of the double unit direction `SceneMissingNormalAccumulator.Normalize`
   produces (each component rounds with relative error at most 2^-24, so the squared length measured from the stored
   components deviates from 1 by at most 2 * 2^-24 + 3 * 2^-48 plus a few 2^-53, below 2^-23; the area-weighted
   reconstruction itself is not reproduced, and a lane outside the bound fails the check), and the receipt records the
   zero, unreferenced and reconstructed lane counts per primitive; COLOR_0 equals the dump vertex colors, or the primary color
   attribute's normalized samples through the sRGB EOTF when the stream declares Srgb (when the consuming
   material takes the writer's blend route, `ModelGltfBlendPlan.TryCreate` re-derived in `writer_rules
   .blend_plan_applies`, COLOR_0 may carry transmission tint instead of the attribute: it is compared, and when
   the copy does not hold it is listed as not compared in `data.colorsNotCompared` and in the check detail,
   never as a writer mismatch); indices equal the dump
   indices, with corners 2 and 3 exchanged when the consuming material declares clockwise fronts; index
   width is uint16 exactly when every index is below 65535; JOINTS_n/WEIGHTS_n equal the influences in
   four-slot sets with weights normalized as `(float)(w / double sum)` when the sum is not 1; skin joints and
   inverse bind matrices equal the dump. Where the writer applies the units factor and the basis rotation
   is checked on the *nodes*: one `multitool coordinates/<root>` parent per kept scene root, its `matrix` equal to
   `orientation * scale(float32(metersPerUnit * scale))` with the exact cardinal rotation of
   `ModelGltfCoordinates.Orientation` (for NIF's +Z up, +Y forward: `(x, y, z) -> (x, z, -y)`), and every
   scene root is such a parent; the vertex accessors themselves stay in source units and basis. Node children,
   mesh and skin bindings follow the writer's static layer selection and draw selection re-derived in
   `writer_rules.layer_plan` (`ModelGltfLayers.Plan`, `ModelGltfDrawSelection.Plan`, Default mode: a member of a
   default-off layer is not needed unless it is a skin joint ancestor, a needed node keeps only its needed children,
   a disabled ancestor survives as transform-only, a node draws its mesh only when reachable from the kept roots with
   an ordinary role and a Render primitive); a child or mesh the writer keeps that the GLB lacks, or drops that the
   GLB keeps, fails the check, and the receipt records how many edges and bindings the selection dropped.
3. *Images*: every GLB texture binding's PNG (Pillow) equals Pillow's decode of the payload the writer selects
   from the dump (original, or a PNG/DDS StandardPayload, or the recovery derivation), after the declared
   transforms: BC5 Z reconstruction (`round-half-away((sqrt(max(0, 1 - x^2 - y^2)) + 1) / 2 * 255)`), green
   inversion `255 - G` for a normal map declared green-down, and companion packing (the single Specular
   layer's declared channel copied into alpha). A companion counts as packed only on the writer's own
   evidence: the layer shape AND a `KHR_materials_specular.specularTexture` binding on the GLB material
   (`ModelGltfNormalCompanion.Derive` falls back to the strict summary, which binds none). When it is packed
   the writer rebinds the specular texture to the prepared normal PNG (`ModelGltfMaterials.Clone` line 440),
   so the oracle expects the specular binding to be that PNG (image identity asserted) and compares it against
   the prepared normal image, whatever the dump's strict summary bound as `specularTexture`. Pass is max
   per-channel difference <= 1/255; the receipt records the maximum difference, the differing pixel count,
   the decoder cross-check, and `bindingsCompared` / `bindingsNotCompared`. A binding whose expectation the
   oracle cannot build (an unreadable payload, a missing payload, an unsupported surface) is a FAILED check
   of kind `expectation`, never a silent skip; the check reads not-applicable only when no binding was
   compared and none failed (an untextured mesh, or materials all on the blend route, whose derived images
   are listed as not compared). `expectation` failures are the oracle's limitation, not the writer's, and the
   receipt labels them so.
4. *Fidelity rows*: the rows of `mesh fidelity --format glb --json` and the GLB's `multitoolFidelity`
   carrier are the same MULTISET of (target, feature, outcome, reason), each copy with equal bounds, and every row is
   judged on its own (until 2026-09-25 the rows were keyed by that tuple in a dict, which kept only the last copy of a
   duplicated row, so a duplicate, or a wrong-count row ahead of the right one, read as one agreeing row; no report of
   either gate run carries a duplicated key, 0 of 4,711 rows on each pin, so nothing measured changes), and the values
   rows declare are present in the file: the "stored float32 factor" on the coordinate parents, `(T+0.5)/255` as the
   MASK cutoff, `sqrt(2/(g+2))` as roughness, and the green-inversion / scalar-packing / normal-Z rows verified
   through the image check. The geometry rows are re-derived from the dump primitive each row targets:
   `geometry.normals-normalized` and `geometry.tangents-normalized` re-measure the observed error with the writer's
   own formula (`ModelGltfGeometry.DirectionDegrees`, since 0fe3212 and unchanged at 591d083: `atan2(|cross(source, output)|, dot)` in
   double, which keeps the sub-1e-8-radian angles the earlier `acos(clamp(dot / sqrt(|s|^2 |o|^2)))` rounded
   to zero), require `observedError` to agree within 1e-9, `maximumError` to be 0.1, and the outcome to be
   Approximated when the measured error is <= 0.1 degrees and Degraded otherwise (a normals mismatch also reports
   the previous pin's acos-form measurement, `writer_rules.previous_pin_normal_degrees`, and says when the declared
   value equals it, which identifies a fidelity report written by the previous writer 6c94992: on the 2026-09-24
   artifacts 20 of the 37 GLB samples fail exactly so, 4 declaring 0 and 16 the quantized acos(1 - k * 2^-53)
   values for k = 1, 2, 3, 4, 6, 8, from 8.5377e-7 to 2.4148e-6 degrees; the expectation itself stays the pinned
   atan2 form); the tangent rows must be exactly the ones `writer_rules.expected_tangent_rows` derives from the dump
   primitive (pinned 591d083, `NormalizeTangents` lines 299-321), judged row by row (featureId, reason code, outcome,
   bound) and then as a set per primitive, on every primitive that has any tangent row and on every dump primitive
   with tangents that a GLB primitive maps to: `geometry/tangents` is `tangent-basis-unsupported` (a failure, since no
   GLB should exist) when a lane is invalid, else `unreferenced-tangents-filled` (Degraded, no bound, "Filled {N}
   unused zero-tangent slots") when N unreferenced zero-direction lanes exist, else `tangents-preserved` (Exact) when
   no lane was normalized; `geometry/unreferenced-tangent-slots` `unreferenced-tangent-placeholders` (Degraded, no
   bound, "Exactly {N} zero-tangent vertex slot(s)") exists exactly when N > 0 (even beside an unsupported row, line
   312); `tangents-normalized` (Approximated at <= 0.1 degrees, else Degraded, with the re-measured bound) exists
   exactly when a lane was normalized and none is invalid, under `geometry/tangent-normalization` when N > 0 and under
   `geometry/tangents` otherwise (line 317); both declared N are parsed from the row text and must equal the oracle's
   count, so a wrong or missing count, a missing, extra or duplicated row (the set per primitive keeps every
   occurrence: `NormalizeTangents` adds each featureId at most once), or the normalization row under the wrong
   featureId fails; the `geometry/normals` row of every
   primitive that has one must be the row `hop_b.expected_normals_row` derives from the dump primitive (a
   reconstruction row only when a zero lane exists, `normals-normalized` / `normals-preserved` only when none does,
   since `NormalizeNormals` takes its zero-lane branch INSTEAD of normalizing, and `normal-direction-absent` a
   failure because no GLB should exist), so a row the writer can never emit fails even when the row present would
   parse; `geometry.zero-normals-reconstructed` / `geometry.unreferenced-normals-filled` must carry
   `Reconstructed {M}` and `Filled {N}` equal to the oracle's count of referenced and unreferenced zero lanes, with
   the `-filled` code exactly when M is 0 and M + N >= 1; `geometry.unreferenced-normal-placeholders` must carry
   `Exactly {N}` equal to the unreferenced count, N > 0, and be present on exactly the primitives whose N > 0 (a
   missing row fails, and so does a second copy: the `geometry/normals` comparison is exactly one row per primitive
   and the placeholder row is added at most once); and `geometry.normalized-base-tangent-morph-unbounded` must be
   present on exactly the morph targets whose primitive's tangents changed (a normalized lane or, since 591d083, a
   filled placeholder: the writer's `changed`, lines 277 and 287) and which carry tangent deltas, at most once per
   target, judged over every primitive that has a tangent row. On the 2026-09-24 manifest no dump carries a morph
   target with tangent deltas (0 of 208;
   14 carry position-only targets), so real artifacts can only ever judge that row ABSENT; its presence is proven by
   `selfcheck_hop_b_paths.py` alone, and the regress-old receipt judged 0 morph targets on its 17 passing fidelity
   checks (the 20 failing ones returned at the normals-normalized mismatch first).

**Hop C (document to package).** Every stream is compared bit for bit with the dump: position, normal,
color (the dump vertex colors, or the primary attribute's normalized samples without EOTF), uv0..uvN,
tangent, indices, faces.sizes/corners, points, skin.joints/weights, every morph target's deltas or
positions, normals and tangents, and every attribute stream's raw bytes and declarations. Every packed
image equals the dump payload its packing decision names (Original, StandardPayload, Derivation, LegacyPng)
with the manifest SHA-256. Units, basis, root scale, node transforms and material declarations are compared.

**Hop E (writer versus writer).** GLB positions and UVs equal the package streams; GLB indices
equal the package indices after the reversal the package manifest's material front face implies; GLB
normals equal the normalized package normals; GLB tangents equal the package tangents after
`writer_rules.normalize_tangents` (the package keeps the source lanes, the GLB normalizes the ones outside the
float32 unit tolerance and, pinned 591d083, stores `(1, 0, 0, w)` on the zero-direction lanes no index of the
package's own index stream references; an invalid package lane, a referenced zero direction included, beside an
existing GLB is a failure; no produced artifact reaches that refusal, since every dump with a referenced zero tangent
is partial and has no GLB, so `selfcheck_tangents.py` pins it on the `gate1a_synthetic` GLB and package: a zero lane
the package indices reference, through a real or a degenerate triangle, reads `unsupported` although the GLB carries
the placeholder there); packed image bytes equal the dump's Original or
StandardPayload; every GLB PNG equals the pinned Pillow decode of the packed DDS after the declared normal
preparation, within 1/255. Only a material on the writer's blend route (`writer_rules.blend_plan_applies`,
`ModelGltfBlendPlan.TryCreate`) is left uncompared, since the writer derives its images; an absent render state or
a null blend declaration is not blend enabled (`ModelGltfMaterials.Alpha` reads it as disabled).

## What each control proves (each must be detected, on a corpus sample or synthetically; a control that passes invalidates the oracle)

| Control | Mutation | Proves |
|---|---|---|
| B: swapped BC1 endpoints re-encoded into a copy of the GLB PNG | The two 565 endpoints of one block of the base-color DDS of the first material the image check compares (not on the blend route) are exchanged in a copy of the payload (saved as `control-bc1-endpoints.dds`): the first block whose exchange moves at least one decoded texel by 2/255 or more, confirmed on the pinned full decode, since a change inside the 1/255 tolerance could never be detected (equal endpoints, near-equal endpoints, or a palette entry no selector references); the pixels that change are spliced into a Pillow decode of the GLB's base-color PNG, re-encoded losslessly and appended to the BIN chunk of a copy of the GLB (`control-bc1-endpoints.glb`, the image's bufferView repointed); the image check runs the pristine expectation against that copy. Not applicable when no compared base color is block-compressed or no block qualifies (a solid-color texture). | The PNG comparison sees a one-block (16-pixel) change in the artifact. |
| B: MASK cutoff written as T/255 | A copy of the GLB JSON chunk (`control-mask-cutoff.glb`) carries `alphaCutoff = T/255` on the first MASK material (`run_mask_control`). Not applicable when no material takes the eight-bit Greater rule; when no corpus sample reaches it (a one-sample run, such as the combat-armor go.nif below), the driver exercises it on the `gate1a_synthetic` MASK material (T = 128), counted only while that set passes its own pristine accessor, fidelity and material checks. | The material check compares the cutoff against the declared `(T+0.5)/255` at float32 precision (for T = 0 the wrong value is 0). |
| B: one-texel UV shift | Every TEXCOORD_0 u in a copy of the GLB BIN chunk (`control-uv-shift.glb`) is shifted by `1/width` of the first texture. | The accessor check is bit-exact; a one-texel shift is detected on the first vertex. |
| B: base-color payload made undecodable in a copy of the dump | In a copy of the dump the base-color image's selected DDS payload has its FourCC rewritten to `XXXX` (any other container: every payload dropped); the GLB is untouched. | An expectation the oracle cannot build FAILS the image check (kind `expectation`) instead of leaving the binding silently uncompared. |
| B: handedness sign of one TANGENT lane flipped | The sign bit of w on one lane of the first primitive that carries a TANGENT accessor is inverted in a copy of the GLB BIN chunk (`control-tangent-w-flip.glb`); +1 becomes -1 and -1 becomes +1, so the lane is still a valid basis. The lane is 0, or, when the dump primitive carries tangent placeholders, the first lane that is not one (recorded as `tangentPlaceholderLanes`), so the flip lands in the authored lanes the `TANGENT` stream compares. Not applicable when no primitive carries TANGENT. | The tangent comparison keeps every w exactly (the writer keeps the source w on every lane it writes, `ModelGltfGeometry.cs` lines 281, 286 and 296 at 591d083); the detection must be a `value` mismatch reported exactly at the mutated primitive's TANGENT stream by a comparison the pristine check did not already fail there; a probe failing elsewhere, or reproducing a pristine `unsupported`, `presence` or `value` failure on that stream, is not a detection. Must be detected on every sample with tangents. `selfcheck_hop_b_paths.py` shows why the lane is chosen: flipping lane 0 when it is a placeholder reports on the placeholder stream and reads NOT detected. |
| B: one unreferenced zero-normal placeholder lane rewritten as (1, 0, 0) | The first zero-normal lane no triangle index references, on the first primitive that has one, is rewritten as `(1, 0, 0)` in a copy of the GLB BIN chunk (`control-placeholder-normal.glb`). Not applicable when no primitive has such a lane. No file of the cut-1a corpus reaches it at the current pin (see "Assumptions"), so the driver exercises it synthetically; that exercise counts only while the synthetic set passes its own pristine accessor and fidelity checks. | The mutated lane is unit length, so the unit-length check of the previous oracle could never see it; only the exact `(0, 1, 0)` comparison of the placeholder lanes reports it, and the verdict requires that comparison's own stream label (`NORMAL (unreferenced zero-normal placeholders, expected (0, 1, 0))`), so a pristine mismatch on the non-zero source lanes is masking, not detection. |
| B: one unreferenced zero-tangent placeholder lane rewritten as (0, 1, 0, w) | The first zero-direction tangent lane no triangle index references (a lane the writer fills with `(1, 0, 0, w)`), on the first primitive that has one, is rewritten as `(0, 1, 0, w)` with its stored w kept in a copy of the GLB BIN chunk (`control-placeholder-tangent.glb`). Not applicable when no primitive has such a lane. Reached by FNV combat-armor go.nif at 591d083 (12 lanes); no file of the 2026-09-24 manifest reaches it, so there the driver exercises it synthetically on the two placeholder lanes of `gate1a_synthetic`. | The mutated lane is a unit direction with an exact handedness, so neither a length nor a handedness test can see it; only the exact `(1, +0, +0, w)` comparison of the placeholder lanes reports it, and the verdict requires that comparison's own stream label (`TANGENT (unreferenced zero-tangent placeholders, expected (1, 0, 0, w))`); a pristine mismatch on the authored TANGENT lanes, compared first, is masking, not detection. |
| C: one face index flipped | Corners 2 and 3 of triangle 0 are exchanged in a copy of the indices stream of the first primitive that carries a triangle (`control-face-flipped.zip`). Not applicable when the package has no such primitive (a model with no drawable geometry packages an empty mesh list). | The index stream check is exact and per element. |
| E: one UV swapped in the package only | u and v of the first vertex whose u and v bits differ, searching every UV stream of every primitive, are exchanged in a copy of that stream (`control-uv-swapped.zip`); the GLB is untouched. Not applicable when every vertex of every stream has u == v (an exchange would change nothing). | The writer-versus-writer UV check is exact; a single swapped vertex is detected. |

The runner executes each control on the mutated copy (saved beside the receipt so a reviewer can re-run it by
pointing a manifest at it), records whether the oracle reported a mismatch and the first mismatch it reported.
Two verdicts are kept apart: a hop's `passed` speaks for the artifact (its checks alone) and its `oracleValid`
for the oracle (its controls). A control that was NOT detected marks the hop's oracle INVALID, distinct from an
artifact failure, and fails the gate; a control that is not applicable on a sample (no block-compressed base
color, no MASK material, no TEXCOORD_0, no TANGENT, no unreferenced zero-normal lane) neither passes nor
invalidates that hop. The three accessor controls (tangent w, placeholder normal, placeholder tangent) are judged on the stream they
mutate (`hop_b.geometry_control_verdict`): the probe's first mismatch must be a `value` mismatch exactly at that
stream, and the pristine accessor check must not have failed at that stream; when the pristine check already fails
at the mutated stream (an `unsupported` tangent basis, a wrong non-zero normal lane, another lane of the same
stream) or ahead of it, the probe reproduces that failure and the control reads not applicable with the masking
mismatch named, never as a detection (until 2026-09-24 the verdict tested the stream name before the pristine
state, so a masked control counted as detected; `selfcheck_hop_b_paths.py` pins the masking cases).

At the gate level (`run_gate1a.summarize`) a control that no sample reached (detected 0, not detected 0) is
exercised synthetically when its hop module offers `synthetic_control_exercise(control name)`: hop B offers it for
its three accessor controls (the tangent w flip, the normal placeholder lane and, since 591d083, the tangent
placeholder lane) and for its MASK control, building the `gate1a_synthetic` set (dump, GLB, fidelity report) under
`<out>/synthetic/hop-b/<control>/`, running the SAME pristine accessor, fidelity and material checks, the SAME
mutation function and the SAME verdict function the corpus run uses (`run_geometry_control` or `run_mask_control`,
no parallel implementation), and returning `detected`, `detail`, `expectedMismatchWhere` and `artifactSha256` (the
pristine GLB, dump and fidelity report and the mutated copy). The receipt records it under the control's summary entry as
`syntheticExercise` beside `neverReachedByCorpus: true`, and the markdown status reads `EXERCISED SYNTHETICALLY
(never reached by the corpus: N not applicable)`. `allPassed` stays true only when the synthetic exercise reports
`detected` AND the synthetic set passed its own pristine accessor, fidelity and material checks (`pristineChecks`,
`pristinePassed`): a pristine failure on a corpus sample fails its hop, so a detection on a synthetic set that
drifted from `writer_rules` must not stand in for one (the placeholder stream is compared ahead of TANGENT, so a
set whose stored tangents drifted would otherwise still carry the placeholder control); the hop module reads such
an exercise as not detected (`verdict` keeps the control's own verdict) and `run_gate1a.synthetic_exercise_detected`
refuses it again at the gate. A control with no synthetic exercise (hops C and E, hop B's two image controls and
its UV shift), one
that is not detected, or one detected on a set that failed its own pristine check is reported NEVER EXERCISED and
fails the gate, so the rule "every hop needs a control that must fail,
with the receipt recording that it failed" holds over the run even where the corpus cannot exercise every
control, and a control that silently never runs still cannot masquerade as a pass. The existing summary fields
(`detected`, `notDetected`, `notApplicable`, `neverExercised`) keep their names and meanings; a control the corpus
detected is never exercised synthetically, and a control with a not-detected sample stays NOT DETECTED.
`selfcheck_runner_controls.py` proves the policy can fail: with the exercise forced to not detected the summary is
allPassed false and the status NEVER EXERCISED; forced to detected it is allPassed true and the new status; forced
to detected on a set whose pristine check failed, and through the real dispatch on a drifted `gate1a_synthetic` set,
it is allPassed false and NEVER EXERCISED again.

**Owner decision pending: the synthetic MASK exercise changes gate policy.** The checked-in oracle offers a synthetic
exercise for its accessor controls only, so a MASK control that no sample reached read NEVER EXERCISED and failed the
gate. The 591d083 staging adds `run_mask_control` to `hop_b.SYNTHETIC_CONTROLS`, which the brief did not ask for: a
one-sample run that reaches no MASK material (the combat-armor go.nif run) now reads EXERCISED SYNTHETICALLY for MASK
instead of NEVER EXERCISED. The red baseline failed on MASK as well as on the tangent rule
(`TestOutput/pin-591d083-20260925/extra-go/receipt-old-oracle/receipt.md` lines 26, 34 and 40), and with the MASK
entry removed from `SYNTHETIC_CONTROLS` the new oracle still fails that run, on MASK alone, every hop passing
(`TestOutput/pin-591d083-20260925/oracle-staging/review-fixes/runs/f3-a-without-mask-exercise/receipt/receipt.md`).
The exercise is held to the same standard as the others (same mutation, same verdict, counted only while the
synthetic set passes its own pristine accessor, fidelity and material checks, and able to fail,
`selfcheck_runner_controls.py` section 8), and on the 220-sample manifests the corpus reaches MASK (39 detected), so
nothing changes there. Keep it only with the owner's approval; without it, a one-sample run that reaches no MASK
material reads FAIL overall whatever its hops say.

## Independence argument

- Specifications read: glTF 2.0 (container, accessor and material sections) and the Khronos validator's
  own report; Direct3D block-compression layouts (BC1 to BC5) and the DDS_HEADER / DDS_PIXELFORMAT /
  DDS_HEADER_DXT10 layouts; the Shared dump schema (`shared/Multitool.Shared/docs/model-dump-json.md`);
  the package layout from the Media.Blender remarks (`BlenderPackageWriter.cs`, `BlenderPackageStreams.cs`,
  `BlenderPackageGeometryMapper.cs`, `BlenderPackageImageMapper.cs`) and the vocabulary of
  `tools/blender/readback.py`; the BSA v103/v104 layout.
- Writer sources were *read* to learn the declared conversions and their float32 rounding points
  (`ModelGltfCoordinates.cs`, `ModelGltfGeometry.cs` and `SceneVertexNormals.cs` at pinned Shared 591d083,
  `ModelGltfWinding.cs`, `ModelGltfMaterials.cs`,
  `ModelGltfVertexColors.cs`, `ModelGltfNormalMaps.cs`, `ModelGltfNormalCompanion.cs`, `ModelGltfImages.cs`,
  `ModelGltfImageWorkspace.cs`, `ModelGltfResources.cs`, `ModelGltfLowering.cs`, `SceneGltfBuilder.cs`,
  `SceneGltfSkinBuilder.cs`, `ModelGltfCarriers.cs`); `writer_rules.py` cites the line ranges. No writer
  code is executed: no SharpGLTF, no BMT .NET assembly, no Shared assembly, no `dotnet`. The GLB, package
  and dump are parsed from bytes by this harness alone; the DDS decode is Pillow's (cross-checked by this
  harness's own decoder), never the Shared `S3tcDecoder` / `Bc4Bc5Decoder` / `DdsImageDecoder`.
- The only external program the oracles run is the pinned Khronos validator. The production driver runs
  the BMT exe (that is its job) and `gate1a_resolve.py`, which reads archives with its own BSA reader.

## Assumptions that could not be confirmed on the smoke artifacts

- Zero source normals: the writer reconstructs a referenced one from area-weighted incident triangles
  (`SceneVertexNormals.ReconstructMissing`) and fills an unreferenced one with UnitY; this harness reproduces
  the reference marking and the exact placeholder, not the accumulator, so a referenced zero lane is only checked
  to be unit length (within 2^-22 of squared length) while every other lane is bit-exact. The receipt names the
  affected primitives and counts. No artifact of the 2026-09-24 manifest exercises either placeholder path on either
  pin: its one dump with unreferenced zero-normal lanes (`meshes/armor/wastelandsettler03/m/go.nif`, 8 lanes) carries
  a zero tangent on exactly those 8 lanes, which 0fe3212 refused (its `NormalizeTangents` tested finiteness,
  handedness and length only) and which 591d083 fills, but at 591d083 the item is refused for its material instead
  (the Blend alpha-equation limit its `partialReasons` name), so there is still no GLB; its hop-B refusal evidence now
  reads 8 placeholder lanes and no tangent refusal under the pinned rule, beside the previous pin's refusal of those
  8 lanes and the refusal text each manifest quotes (see the refusal-evidence item below). Since the reader's tangent
  mapping fix (2026-09-28: the typed xyz is the stored Bitangents array, which runs along +dP/du) the same 8 lanes are
  still zero directions: both stored arrays are zero there (TestOutput/nif-tangent-frame-20260928/impact, re-derived
  from the NIF for every cover shape: the invalid, placeholder and handedness decisions are unchanged on all 978
  tangent-bearing shapes but one refused gauss-scope shape that gains a second invalid lane). The combat-armor go.nif
  of FNV PC (`meshes/armor/combatarmor/m/go.nif`, 12 unreferenced lanes with zero normals AND zero tangents, all w
  +1, beside one normalized lane) is the one corpus file whose 591d083 GLB carries both placeholders; it was produced
  by hand outside the manifest (`TestOutput/pin-591d083-20260925/extra-go/artifacts.json`) and on it the new oracle
  passes hops B, C and E with
  both placeholder controls DETECTED on the real sample. On the manifest the two placeholder controls stay never
  reached (57 not applicable each) and the driver exercises them synthetically on the `gate1a_synthetic` set
  (`hop_b.synthetic_control_exercise`, the same mutation, check and verdict, counted only while that set passes its
  own pristine checks); the receipt reports them EXERCISED SYNTHETICALLY with the artifact SHA-256s, and the paths'
  other cases are pinned by `selfcheck_hop_b_paths.py`. A corpus sample that reaches a control takes precedence: the
  synthetic exercise runs only when detected and not detected are both 0.
- The float32 tolerance test of `NormalizeTangents` (`MathF.Abs(direction.LengthSquared() - 1) <= 0.0001f`) is
  reproduced as `(x*x + y*y) + z*z` with every product and sum rounded to float32. RyuJIT does not contract a
  multiply-add into a fused one and its SIMD lowerings of `Vector3.Dot` sum the four lanes as `(xx + yy) + (zz + 0)`,
  the same value; this is read from the runtime's documented behavior and not measured on the producing host. A
  fused product could move the squared length by one float32 ulp, which matters only for a lane whose |squared - 1|
  is within one ulp of 0.0001f (838.86 ulps of 1): such a lane would be classed unchanged by one side and
  normalized by the other, and would surface as a TANGENT bit mismatch on that lane, never silently.
- On a partial sample with a dump, hop B's not-applicable entry records `refusalEvidence`: the primitives whose
  tangents carry an invalid lane (the writer's `geometry.tangent-basis-unsupported` refusal), those it would
  normalize and (591d083) those whose unreferenced zero-direction lanes it would fill (`placeholderTangentPrimitives`).
  It ties a refusal to the tangent rule when that is the cause, judged by the pinned rule. The artifacts manifest does
  not name the writer pin that produced it, so the evidence also records what the previous pin decided
  (`previousPinRule`: Shared 0fe3212 refused every zero-direction lane, referenced or not, its lines 266-268,
  `writer_rules.previous_pin_invalid_tangents`) and which writer's tangent-basis refusal text the manifest's
  `partialReasons` quote (`manifestQuotesTangentRefusal`: the two texts differ, 591d083 line 301 against 0fe3212 line
  284, `writer_rules.TANGENT_REFUSAL_TEXTS`). When only the previous rule predicts a refusal the detail says so: the
  wastelandsettler03 entry reads "zero-direction tangent lanes no index references on 1 primitive(s), which the
  previous writer (Shared 0fe3212) refuses and the pinned writer (Shared 591d083) fills with (1, 0, 0, w)", then "the
  manifest quotes the Shared 0fe3212 tangent-basis refusal" on the 0fe3212 artifacts and "no tangent-basis refusal"
  on the 591d083 ones (refused there for its Blend alpha equation), so a receipt over an older writer's artifacts no
  longer describes that writer's tangent refusal as absent. A refusal both rules predict (a referenced zero,
  nonfinite or unhanded lane: 14 primitives of 8 partial samples on each gate manifest, all referenced zero lanes,
  `TestOutput/pin-591d083-20260925/oracle-staging/review-fixes/census.md`) keeps its pin-neutral detail text, the
  quoted text being in the evidence. The writer's other admission limits are not reproduced.
- Skins, morph targets, additional UV sets, attribute streams, point-domain colors, absolute morphs and
  BC4/BC5 companions were implemented from the sources but no smoke artifact exercises them end to end
  (the upperbody GLB has skins but no dump or package was produced for it).
- Materials on the transmission route (blend enabled) derive material-specific images the harness does
  not reproduce; their bindings are listed as not compared.
- The MASK rule is checked only for the eight-bit Greater path; other alpha algebra is not re-derived.
- `mesh fidelity --format glb --json` output was seen only for `upperbody`; the row comparison against the
  GLB carrier passed there (143 rows).

# Gate 1b: the animation hops (B-anim, C-anim, E-anim)

Gate 1b has gate 1a's shape over the animation (design `docs/design/model-document-design-20260923.md` sections 7.2
and 8, "Gate 1b has the same shape plus the 170-degree rotation control"): the GLB against the dump, the Blender
package against the dump, the two writers against each other, each with controls that must be detected, plus the
170-degree control. The static hops of gate 1a already pass on animated artifacts; these hops compare the animation.
They read the same `gate1a-artifacts/1` manifest (`mesh convert`, `mesh package` and `mesh dump` already carry the
animation; no new producing command) and write their own receipt.

## Files

| File | Purpose |
|---|---|
| `../nif_curve_eval.py` | The slice-9 A8 evaluator, extended (not duplicated) as the dump evaluator: `dump_curve_node` (the dump's curves in the evaluator's bit form), `clock_map` / `map_clocks` (SceneAnimationClock.Map and the clip-then-track composition with the Float32 rounding between them), CUBICSPLINE components, keyed LINEAR rotations as System.Numerics `Quaternion.Slerp` (`numerics_slerp`) and component-sampled Hermite/CubicSpline/TBC/Step rotations. The A8 inputs never reach the new branches (the NIF reader emits none of those forms); a generated A8 input over every curve kind gives byte-identical output before and after. |
| `anim_dump.py` | The dump's clips as pose keys (node translation/rotation/scale/weights, node visibility, material factors, layer transforms) with their drivers in ScenePoseEvaluator's order (transform tracks, then Euler tracks; morph tracks, then morph-target tracks; property tracks), rest values (authored TRS, SceneMorphDefaults, visible, source-first material factors), `value(clip, key, t)` at an output time and `track_value` for one track; `clip_clock_window`. Matrix tracks, transform bases and the Xbox 360 Squad estimate are listed as unsupported, never guessed. |
| `anim_glb.py` | An independent glTF animation reader and sampler: STEP, LINEAR (the specification's shortest-path slerp for rotations), CUBICSPLINE with the tangents multiplied by the key interval (an `unscaled` flag reproduces SharpGLTF's interval-blind sampler for the control only), morph weights, KHR_animation_pointer targets (`/nodes/{n}/extensions/KHR_node_visibility/visible`, `/materials/{m}/...Factor`, emissive strength, specular color, `.../KHR_texture_transform/{offset,scale,rotation}`), and the writer's `multitoolAnimationMetadata`. |
| `anim_package.py` | The package v4 `animations` reader and a Blender F-curve player re-derived from `import_model.py` (`_animation_coordinates`, `_animation_fcurve`): 32 frames per second, Float32 points, FREE handles one third of the interval away, CONSTANT/LINEAR/BEZIER per interval from `segmentInterpolations`, CONSTANT extrapolation, quaternion components interpolated independently and normalized, XYZ Euler axes; `admission_problems` re-derives the importer refusals a package could otherwise pass with (times inside [0, duration] compared in Float32, finite values, segment-list shape, binary STEP visibility, quaternion amplitude [1/8, 8], the nonnegative LINEAR hemisphere, cubic quaternions only as ComponentPolynomial, morph values in [-10, 10], collapsed or vanishing Float32 handles). |
| `anim_compare.py` | The shared rules: bounds from the writers' rows, sample times, the discontinuity skip, deviation measures, `compare_series`, channel records, the DON'T-SAMPLE list, `declared_drop`, clock and event equality. |
| `hop_b_anim.py` | Hop B-anim (GLB against the dump), four checks and four controls, `synthetic_control_exercise`. |
| `hop_c_anim.py` | Hop C-anim (package against the dump), four checks and three controls, `synthetic_control_exercise`. |
| `hop_e_anim.py` | Hop E-anim (GLB against the package), two checks and two controls, `synthetic_control_exercise`. |
| `gate1b_synthetic.py` | The hand-built animated set (dump, GLB, fidelity report, package) the synthetic exercises run on, with certificates measured by this harness and rounded up; and the 170-degree control on slice 9's synthetic pair. |
| `selfcheck_anim.py` | Hand vectors for every clock and sampler rule, every control on the synthetic set, each hop reading NOT detected on a drifted set, the declarations check, the gate policy and the 170-degree control both ways (exit 1 on any failure). |
| `run_gate1b.py` | The driver: `receipt.json`, `receipt.md`, `samples/NNNN_<id>/result.json`, synthetic artifacts under `synthetic/`. |
| `dont_sample.json` | The DON'T-SAMPLE list (empty; see below). |

## How to run

```
python tools/scripts/gate1a/selfcheck_anim.py
python tools/scripts/gate1a/run_gate1b.py <OutRoot>/artifacts.json --out <OutRoot>/receipt-1b [--limit N] [--filter s]
```

No validator, no dotnet, no Blender. On the 2026-09-26 slice-10 artifacts (Shared fdf7964, 220 samples) the run takes
under a minute.

Why a separate driver: gate 1a's receipt schema, HOPS tuple and control policy are pinned by
`selfcheck_runner_controls.py` and by the receipts already taken, and the animation hops need other inputs (the GLB
fidelity rows are their bounds) and report per-channel coverage. `run_gate1b.py` reuses gate 1a's gate-level policy
functions unchanged (`synthetic_exercise_detected`, `control_status`, `synthetic_exercise_line`, `oracle_identity`), so
the two gates judge controls identically.

## Oracles

**The source side.** Every hop evaluates the dump at an OUTPUT time `tau`: the clip clock maps `tau`, the track clock
maps that Float32 result, and the track's curve is sampled there by `nif_curve_eval` (Shared
docs/scene-animation-clock-lowering.md: "A track is sampled at T(C(tau))"; ScenePoseEvaluator.ApplyClip). Hop B-anim
compares at the POSE level (the effective value of a node property after every track that drives it), so the writer's
track-to-channel mapping is not assumed; hop C-anim compares each channel with the track its `sourceDomain` /
`sourceTrack` names.

**Bounds.** A channel passes when every compared sample is within the writer's CERTIFICATE (the row's
`observedError`) plus the evaluation slack; the ratio to the design limit (`maximumError`) is recorded beside it. GLB:
`animation/<domain>/<track>/clock/value` for every track that drives the property (that row includes the observed prior
conversion error: Hermite/TBC tangents, B-splines, Gamebryo counter-warp and Squad, Euler), else the track's conversion
row, plus `animation/morph-target-nodes/<n>/packing` for weights; units meters (Euclidean, after the source-unit
factor), degrees (the angle between normalized rotations), ratio/weight/component (largest absolute component).
Package: `animation/<domain>/<track>/<component>/clock-retiming` (ratio over `max(1, lowered key magnitude)`, the
denominator taken per component from the channel's own keys, or degrees for a quaternion) and the rotation rows.
E-anim: the SUM of both certificates, each converted to the GLB channel's unit. Slack (what no certificate covers:
Float32 key storage and the two-ulp key placement): 4 Float32 ulps of the value magnitude for scalars, 2e-4 degrees for
rotations (the A8 tolerances), plus the local speed times 2 ulps of the sample time.

**Sample times.** Every key time of the channel, the quarter points of every key interval and 33 times over the
playback window, inside `[0, window]` (the window from the GLB metadata's `outputWindowSeconds`, checked against the
clip clock: one Clamp reach, one Loop interval, out and back for Reverse; the package's `durationSeconds`, checked the
same way or against an authored duration); at most 1,200 per channel.

**Discontinuities.** A sample is skipped (and counted) when either side jumps within +-4 Float32 steps of it: a Loop
wrap is two keys one Float32 step apart, and a STEP key or wrap may sit two ulps from the source instant. The jump test
compares the value at `t - 4 ulp` and `t + 4 ulp` against the larger of the design bound and 100 times the local
speed over those 8 ulps, where the speed is the SMALLER one-sided difference (a central difference across a wrap reads
a speed of 1/(2h) and would hide the wrap: that happened on nvdlc03inversalaxe before the fix, the one false failure the
development runs showed). Everywhere else a discontinuity a writer failed to emit is a mismatch.

**DON'T-SAMPLE list.** `dont_sample.json` takes (sample, clip, channel, from, to, reason) entries; every applied entry
is counted in the channel record. It ships EMPTY: the 2026-09-26 artifacts needed none.

**Hop B-anim checks.** (1) clips: every GLB animation names its source clip, the metadata clock and events equal the
dump's, the window agrees with the clip clock, every key time lies in the window, and a dump clip that drives tracks
but has no output animation is declared by a writer row (Dropped/Degraded/Metadata on `animation/channels` or
`animation/clock`), else a silent drop fails; (2) channels within their certificates; (3) coverage: every driven
property has a compared channel or holds the rest value over the window, every channel resolves, and what the oracle
cannot evaluate is listed with its reason. The GLB is judged by the glTF specification alone (owner, 2026-09-26: the
GLB and Blender writers are separate output paths and neither is limited by the other): a LINEAR rotation is the
shortest-path slerp, so a key's sign is free, and how a third-party importer plays it is not a rule of this hop.
The self-check requires that a negated LINEAR key passes B-anim.

**Hop C-anim checks.** (1) clips: pairing, the window, the importer's admission for every channel, declared drops;
(2) channels within their certificates, played as Blender plays them; (3) coverage; (4) declarations: the clip
metadata's clock and ordered events, every metadata channel entry's node, property, target, state and clock, and every
numeric channel's node, property, target, component offset and width equal the dump track they name.

**Hop E-anim checks.** (1) the two writers carry the same clips; (2) every GLB channel against the package channels on
the same node property (Translation, Scale, Rotation or the three EulerRotation axes composed as qZ * qY * qX,
MorphWeights over the rest weights, NodeVisibility, material pointers under B-anim's identity condition) within the
sum of the certificates, at the union of both writers' keys.

## Controls (each must be detected; a control no sample reaches is exercised on the synthetic set)

| Hop | Control | Mutation | Must be reported |
|---|---|---|---|
| B-anim | sampler output shifted by one key | the first compared channel with two keys whose shift moves its own samples by more than ten times its bound: key k takes key k+1's value | `value` at that channel |
| B-anim | CUBICSPLINE tangents unscaled | the first compared CUBICSPLINE channel whose interval scaling is visible: every out-tangent divided by the following interval, every in-tangent by the preceding one, so a specification reader of the copy computes what SharpGLTF's interval-blind sampler computes on the original | `value` at that channel |
| B-anim | quaternion sign flip on one key | the first interior key (not beside a one-step pair, all three parts) of a compared CUBICSPLINE rotation negated (same orientation); a LINEAR rotation is never mutated, because the specification's slerp plays it identically; with no corpus CUBICSPLINE rotation it runs on the synthetic set's CubicSpline variant | `value` at that channel (a long-way normalized cubic) |
| B-anim | visibility value inverted | key 0 of the first compared KHR_node_visibility channel, 0 <-> 1 | `value` at that channel |
| C-anim | segment policy flipped to LINEAR on a cubic span | `segmentInterpolations` written with the first span whose LINEAR replacement moves the midpoint by more than ten times the bound set to Linear | `value` at that channel |
| C-anim | quaternion sign flip on one key | an interior non-STEP key (all three parts of a cubic key) of a compared Rotation channel negated | `value` at that channel (a LINEAR channel is also refused by the importer, `importer`) |
| C-anim | visibility value inverted | key 0 of the first compared NodeVisibility channel | `value` at that channel |
| E-anim | GLB sampler output shifted by one key, GLB only | as B-anim's | `value` at the GLB channel |
| E-anim | package cubic span flipped to LINEAR, package only | as C-anim's | `value` at the GLB channel paired with the flipped channel |
| gate | 170 degrees | slice 9's synthetic pair (identity and 170 degrees about normalize(1, 2, 3), counter-warped nlerp, keys at 0 and 1 s): with 257 inserted keys the GLB and the package pass; the two keys alone, as a GLB LINEAR slerp and as a package LINEAR nlerp, must both be reported; plain slerp and nlerp of the pair must differ by more than 6 degrees | detected only when all of that holds and the three figures reproduce slice 9's receipt (6.7576 slerp versus nlerp; misses 1.7896 and 8.4731) within 0.01 degrees; the self-check shows the same fixture at 20 degrees reading NOT detected |

Verdicts follow gate 1a: a control is detected only when the probe reports the expected kind exactly at the mutated
channel's label and the pristine run reports nothing there (a pristine mismatch there is masking and reads not
applicable); a not-detected control marks its hop INVALID and fails the gate; a synthetic exercise stands in for a
corpus detection only when the synthetic set passes its own pristine checks.

## Results on the 2026-09-26 slice-10 artifacts (Shared fdf7964, 220 samples)

93 dumps carry 145 clips (3,035 driven tracks). 4 GLBs and 52 packages carry animation (3 samples both). B-anim: 4 of
4 pass, 110 channels, 16,540 samples compared, 11 skipped at discontinuities, worst 0.773 x the certificate. C-anim: 52
of 52, 1,200 channels, 72,557 samples, 340 skipped, worst 0.459. E-anim: 3 of 3, 95 channels, 11,704 samples, 7
skipped, worst 0.771 x the summed certificate. Every control is detected (B: 4/4, 1/1, 1/1 and the sign flip on the
synthetic CubicSpline set, because no corpus GLB carries a CUBICSPLINE rotation: 24 LINEAR and 6 STEP rotation
channels; C: 17, 8, 9; E: 3, 1); none reads not detected; the 170-degree control is detected and reproduces slice 9.
Receipt: `TestOutput/gate1b-harness-20260926/runs/slice10-fdf7964-glb-spec/`.

## Independence and limits

- Read, never executed: the glTF 2.0 specification and the KHR_animation_pointer, KHR_node_visibility and
  KHR_texture_transform texts; Shared `SceneAnimationClock.Map`, `ScenePoseEvaluator.ApplyClip`, `ScenePoseChannel`,
  `SceneCurve`, `ScenePropertyPoseState` (evaluation order and rest values), docs/scene-animation-clock-lowering.md and
  docs/model-dump-json.md; `import_model.py`'s channel contract, `_animation_coordinates`, `_animation_fcurve` and
  `_animation_prepare`; `BlendAdmission.AddAnimationRows` (package rows are indexed by the source clip);
  `ModelGltfPropertyBinding` (pointer paths). No writer, SharpGLTF, Blender or Shared code runs.
- A material factor pointer is compared only where the writer's layer projection is the identity (the dump's portable
  rest equals its source rest); otherwise it is listed as not compared (`SceneTextureLayerProjection` is not
  reproduced). No GLB of the 2026-09-26 manifest carries a material pointer; the path is exercised by the synthetic set.
- Matrix tracks, transform bases and the Xbox 360 Squad estimate are not evaluated (Shared refuses the last; the GLB
  writer refuses matrix clips). PC Squad is evaluated through `nif_curve_eval.sample_squad`, but no GLB or package of
  the manifest carries one.
- The Blender F-curve player is re-derived from the importer and Blender's documented Bezier behavior, not measured
  in Blender; hop D (owner-run) plays the saved `.blend`.
- No hemisphere rule on the GLB: an earlier revision required component-linear nlerp to equal slerp at every LINEAR
  rotation midpoint (for Blender's glTF importer); it was removed on the owner's ruling of 2026-09-26. The package
  keeps its own nonnegative LINEAR hemisphere, because that is the Blender importer's admission rule for the package
  path (hop C-anim).
