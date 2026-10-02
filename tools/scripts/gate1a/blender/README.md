# Gate 1a Blender hops: B', D and F

The three Blender rows of design section 7.2 (`docs/design/model-document-design-20260923.md`, with sections 5.3, 6.2,
6.3 and 6.4), run serially under the owner's memory conditions. The harness writes every input, launches Blender one
process at a time, and judges what Blender wrote with independent Python oracles. It reuses the gate-1a readers
(`glb_reader`, `package_reader`, `dump_reader`, `dds_decode`, `gate1a_common`, `hop_b.rebuild_glb`,
`hop_c.expected_color_stream`) and the Shared scripts it tests (`import_model.py`, `tools/blender/readback.py`).
Standard library plus numpy and Pillow only.

**What the main session has to do:** run the three commands under "How to run", in order. The harness waits for memory
on its own, never starts a second Blender, and resumes where it stopped. Nothing else is needed: no build, no dotnet, no
manual Blender step, no hand-made input.

## Files

| File | Purpose |
|---|---|
| `run_blender_hops.py` | The driver: subcommands `bprime`, `d`, `f`, `all`; builds the inputs and controls, launches Blender through `blender_launcher`, runs the comparisons, writes `receipt.json` / `receipt.md`. |
| `blender_launcher.py` | The gate every Blender process goes through: admission at >= 6 GiB available memory (polled, aborts after a timeout), one process at a time (a Global run mutex, serial launches, waits while any other `blender.exe` runs), BelowNormal priority (enforced), creation SUSPENDED into a kill-on-close job object, the watchdog (below 2 GiB available, above 4 GiB private bytes, or either limit unmeasurable), the per-launch timeout, a tree kill on any exception, per-launch receipts and resume. Memory through ctypes (`GlobalMemoryStatusEx`, `K32GetProcessMemoryInfo`, the job's `PeakProcessMemoryUsed`); no psutil. |
| `compare_bprime.py` | Hop B' verdicts: one launch, one GLB over N fresh processes, the custom-attribute control (judged on the set-order defect's own signature). |
| `compare_d.py` | Hop D: the readback dump against the package and the exact dump (geometry, node hierarchy and stored transforms, every copied `mt_*` property, native states, stray skin data); control verdicts with element-boundary matching and masking. |
| `compare_d_anim.py` | Hop D over the animation (gate 1b): owners keyed by node, primitive and material variant; actions, slots, F-curve keys, FREE handles and per-key modes against the package (bit for bit); rest curves derived from the package; Blender's own `FCurve.evaluate` against Blender's restated arithmetic; NLA tracks, active clips, AnimData settings, visibility drivers, stray owners and scene timing. `PackageModel` also gives the static checks the animation's effects (Euler rotation mode, the visibility split, the first clip's frame-0 pose, widened shape-key sliders). |
| `blender_fcurve.py` | Blender's keyframe F-curve evaluation restated from `fcurve.cc` (key search with the 1e-4-frame threshold, the flat-span shortcut, Float32 `findzero` coefficients with a double root, Float32 `berekeny`, CONSTANT extrapolation) and the residual bound hop D allows between it and Blender. |
| `compare_f.py` | Hop F: the blend models (drawn, Gamebryo, linear) and the probe-design exercise check, the importer's lit route against lit twins, the ramp, the DDS orientation (render and loaded buffers), the transmission route and its bindings by identity; control verdicts. |
| `import_rules.py` | The importer's declared conversions (winding reversal, welding, degenerate faces, UV flip, color storage and domain, attribute layouts and domains, shape keys, image color space and alpha mode, material variants, culling, the E/T blend terms, lighting model, depth bias, shear and billboard offsets). Each function names the importer lines it restates; the ones the design states are also pinned by hand vectors from the design (see "Independence argument"). |
| `probe_builders.py` | Synthetic inputs, built from bytes: the package writer, PNG / uncompressed DDS / BC1 DDS writers, the hop-D coverage package and its exact dump, the four F probe packages (blend pairs, lit route, ramp, orientation) and their render specs, the transmission GLB (T, E and COLOR_0) and its expected bindings, the B' control GLB; and the Shared writer's display-record decision restated (`display_record_and_kinds`). |
| `controls.py` | The control builders: each a copy of a pristine input with one declared change. |
| `inside_blender/probe_render.py` | Runs INSIDE Blender (hop F): reads the loaded image buffers, sets up an orthographic camera, emission backgrounds and a deterministic render in the engine the spec names (Cycles on the CPU or EEVEE), saves EXR and PNG, samples the requested pixels; for the lit probe also a sun and the display-blend variant's engine settings and lit pass (inner-block means). |
| `inside_blender/repack_dds_control.py` | Runs INSIDE Blender (hop-D control): re-encodes one packed DDS through `save()` and packs it again. |
| `fake_blender.py`, `fake_bpy.py`, `fake_render.py` | The FAKE Blender for the self-check: a Python script taking Blender's command line. It runs the REAL `readback.py` and the REAL importer's root, geometry, image, object-hierarchy, shape-key, native-state and metadata builders against a recording `bpy` stand-in with a float32 `mathutils`, and computes probe renders analytically, delivering each texel as Cycles does for the alpha mode the importer stored (held to the recorded 2026-09-25 render). It refuses to model `PREMUL`, whose texel is recalled on both readings and not yet rendered. Never used for a gate verdict. |
| `selfcheck_blender_harness.py` | Proves every non-Blender path with the fake (the run prints its expectation count and `selfcheck.json` records it; see "Self-check"). |

## How to run

Run from the repository root, in this order. `<out>` is one directory for all three steps (the full run then reuses the
smoke run's launches).

```
python tools/scripts/gate1a/blender/run_blender_hops.py all --limit 1 --dry-run --out TestOutput/gate1a-blender-<stamp>
python tools/scripts/gate1a/blender/run_blender_hops.py all --limit 1 --out TestOutput/gate1a-blender-<stamp>
python tools/scripts/gate1a/blender/run_blender_hops.py all --out TestOutput/gate1a-blender-<stamp>
```

1. The dry run launches nothing (no Blender): it prints every command the smoke run would make.
2. The smoke run is 48 launches: `blender --version`, B' on the first GLB (12 fresh processes) plus the control
   (12), D on the first package plus the synthetic set plus both controls (8), F (15). It exercises every Blender path.
3. The full run is about 1,150 launches (see "Estimate"). Interrupt it at any time; the same command resumes it.

Defaults: `--artifacts` is the current-pin manifest `TestOutput/pin-66bf702-20260925/gate/artifacts.json` (220 samples,
57 GLBs, 208 packages) plus the go.nif extra `TestOutput/pin-591d083-20260925/extra-go/artifacts.json`; `--blender` is
`C:/Program Files/Blender Foundation/Blender 5.1/blender.exe`. Other options: `--limit N` and `--filter TEXT` (corpus
samples, per hop), `--repeats` (B' processes per GLB, 12 for the gate), `--timeout-seconds 900` (per launch),
`--render-timeout-seconds 1800` (F probe launches), `--admission-timeout-seconds 3600`, `--render-samples 16`,
`--retry-failed`, `--dry-run`, `--allow-blender-version`. The F blend reference is fixed (see below) and hop F renders
every probe in both engines, Cycles and EEVEE, on every run; the former `--blend-model display|linear` and
`--engine CYCLES|EEVEE` switches are gone. A single hop: `bprime`, `d` or `f` in place of `all`.

Exit codes: 0 every selected hop passed with every control detected; 1 a check failed, or a control was not detected, or
was never exercised or masked; 2 usage, a refused limit or a Blender that is not 5.1.x; 3 aborted (the admission timeout,
or another harness run holds the run mutex). A run with `--limit`, `--filter`, `--repeats` other than 12 or `--dry-run`
is marked SUBSET in the receipt.

Outputs under `<out>`: `receipt.json` and `receipt.md` (per hop: samples, checks, controls, launch counts, Blender
seconds, peak private bytes, minimum available memory; the Blender path, version and SHA-256; the SHA-256 of every
Shared script and harness script; the limits); `launches/<hop>/<unit>/<name>.json` (one receipt per Blender process,
with its stdout and stderr beside it); `results/<hop>/<unit>.json` (every comparison); `work/` (probe packages, specs,
control copies, .blend files, readback dumps, EXR and PNG renders).

## The owner's execution rules, and where each is enforced

| Rule | Enforcement |
|---|---|
| Exactly one Blender process at a time | Launches are strictly serial (`Launcher.run` returns only after its process exits and refuses to start a second one); the run holds the named mutex `Global\Multitool.Gate1a.BlenderHops.v1` (Global, so a run in another logon session is excluded too) so a second harness run waits and then aborts; admission also waits while any other `blender.exe` is running (Toolhelp32 process list). Every process is created suspended, put in a kill-on-close job and only then resumed, so anything it starts is in the job; if anything escapes the monitoring loop (an exception, Ctrl+C) the process tree is killed before the exception propagates. Without a job (a nested-job refusal, recorded) a kill falls back to `taskkill /T /F`. |
| Wait for >= 6 GiB available physical memory before each launch, polling, abort on timeout, never bypass | `Launcher.admit`: `GlobalMemoryStatusEx.ullAvailPhys`, polled every 5 s; after `--admission-timeout-seconds` the run aborts (exit 3) with the reason in the receipt. `Limits` refuses a lower floor. |
| BelowNormal priority | `BELOW_NORMAL_PRIORITY_CLASS` at creation, read back with `GetPriorityClass` into every launch receipt; a process that reads back at any other priority is killed (outcome `priority-not-below-normal`, a failure). |
| Watchdog: kill below 2 GiB available or above 4 GiB private bytes, recorded as a failure | Sampled every 0.25 s; the job object is terminated; outcome `watchdog-free-memory` or `watchdog-private-bytes`, a failed launch. `Limits` refuses a lower floor or a higher ceiling. A limit the harness cannot measure is not enforced, so 8 consecutive samples with unreadable private bytes or priority kill the launch fail-closed (outcome `watchdog-unmeasured`). |
| Per-launch timeout | `--timeout-seconds` (900) and `--render-timeout-seconds` (1800); outcome `timeout`. |
| Record per launch: command, exit code, seconds, peak private bytes, minimum free memory | Every launch receipt: `command`, `run.exitCode`, `run.seconds`, `run.peakPrivateBytes` (the largest of the sampled `PrivateUsage`, the OS-tracked `PeakPagefileUsage` and the job's `PeakProcessMemoryUsed`, each also recorded), `run.minAvailableBytes`, plus the admission record. |
| Resumable | A launch whose receipt exists, is complete, has the same fingerprint (command, input SHA-256s, script SHA-256s, Blender SHA-256) and whose recorded outputs are unchanged is skipped; `--retry-failed` relaunches failed ones. |
| `--limit` / `--filter` for a short smoke run | Per hop over the corpus samples; the receipt says SUBSET. |
| Blender path default and version recorded | `blender --version` is itself a gated launch; the receipt records path, SHA-256 and version; anything but 5.1.x is refused. |

The child environment is the parent's minus `PYTHONHASHSEED`, `PYTHONPATH`, `PYTHONHOME` and the `BLENDER_*` override
variables (recorded per launch): a fixed hash seed would make the twelve "fresh" B' processes iterate sets in one order,
which is exactly what hop B' must not do. Available physical memory is the quantity `GlobalMemoryStatusEx` reports as
`ullAvailPhys` (Task Manager's "Available").

## What each hop proves

**B' (regression guard).** Each sampled GLB (57 plus go.nif) is imported by `readback.py --import-glb` (Blender 5.1's
io_scene_gltf2) in 12 fresh Blender processes. A launch passes when Blender exits 0, the dump has schema
`multitool.blender-readback/1` and mode `glb`, and the import produced a mesh object whenever the GLB draws a mesh
(readback does not check the operator's return value, so an importer that reports an error and returns CANCELLED would
otherwise leave an empty scene and exit 0). A sample passes when all 12 launches pass AND their dumps are identical: the
defect this hop guards against (io_scene_gltf2 `blender/imp/mesh.py` lines 108-118 and 270-279 name custom attributes
from a hash-ordered set but keep their arrays in discovery order) also has a silent form, two same-width attributes
exchanging data, which only a disagreement between fresh processes shows.

A sample whose declared GLB (or, for D, package) is missing on disk is a FAILED sample: moving or cleaning TestOutput
cannot shrink the gate silently.

**D (package to .blend).** Shared `import_model.py` builds a .blend from each package; `readback.py --blend
--include-values` dumps it. The dump must equal the package AND the exact mesh dump:
- the package and the exact dump must first yield the same importer output for every primitive (plans from both);
- vertices, loops (after the declared winding reversal and degenerate-face omission), n-gon sizes and their histogram;
- UV maps `uv{k}` as (u, 1 - v), including the FLOAT2 attributes past Blender's 8 maps;
- the `Color` attribute (eight-bit storage compared as its stored bytes, float storage bit for bit; POINT, or CORNER when
  welded) and the active color;
- `mt_source_normal` bit for bit, smooth flags, and the custom normals within the declared 0.1 degrees (the one
  tolerance; zero source normals are counted, not compared);
- `mt_source_tangent`, every `mt_attr_*` attribute in its declared Blender type and domain (and no attribute for a
  stream the admission leaves unrepresented), `mt_point_index` on welded primitives;
- absolute shape keys: Basis plus one block per target (relative targets added in float32), names, rest values, sliders;
- the mesh, material, node, root and scene `mt_*` properties, material variants and culling, one object per node and per
  primitive;
- every packed image's bytes (SHA-256 and length), color space and alpha mode;
- the animation (`compare_d_anim.py`; a package without animations must read back no action):
  - one slotted action per clip and owner, for every owner group any clip animates: a node object (TRS,
    `LocalMatrix`/`PostTransform`, visibility), a shape-key block per primitive of a morph-animated node, a material node
    tree per material variant a material property targets; one slot of the owner's ID type, one layer, one KEYFRAME
    strip, one channel bag; `use_frame_range` and the range [0, max(1, duration x 32)]; `mt_animation_index`, `mt_name`,
    `mt_duration_seconds`;
  - one F-curve per channel component at the importer's data path (`location`, `scale`, `rotation_quaternion` in
    Blender's W, X, Y, Z order, `rotation_euler`, `matrix_parent_inverse`, `["mt_visibility"]`, a key block's `value` at
    offset + component + 1; a material component is matched by content inside its material's action), keys equal to
    (time x 32, value) in Float32 bit for bit, CONSTANT extrapolation, per-key interpolation from the segment policy,
    and FREE handles equal bit for bit to the one-third handles on a cubic channel;
  - every other F-curve a rest curve listed in `mt_animation_rest_components` (one key at frame 0, CONSTANT), and a TRS
    or visibility rest equal to the package node's;
  - Blender's `FCurve.evaluate` (readback's `evaluated`: every key, the quarter points of every interval, one frame
    beyond each end, and exactly those frames) equal to `blender_fcurve.evaluate` on the package curve within
    `residual_bound` (8 Float32 steps of the span's largest control value plus one Float32 step of the root times the
    span's slope); how far Blender's playback strays from the ideal curve is reported per property
    (`blenderVersusIdeal`), not hidden;
  - each action on exactly one muted NLA track of the right owner (track and strip named by the action, the action's
    slot, frame ranges [0, frame end], extrapolation NOTHING, blend REPLACE); each owner's active action and slot are the
    first packaged clip's; with animated visibility, the `mt_effective_visibility` drivers on node objects and the
    `hide_render` / `hide_viewport` drivers on primitive objects; the scene's 32 frames per second, frame range and
    `mt_animation_*` properties;
- the importer's own summary counts;
- the node hierarchy (every node object's parent is its package parent's attach object or `mt_root`; skinned primitives
  sit under `mt_root`), `mt_local_matrix` bit for bit, the stored location and scale (the authored TRS as float32; a node
  without TRS decomposes its matrix, scale within a declared 1e-5 relative bound), the rotation mode, the object type,
  and every `matrix_parent_inverse` the importer sets;
- camera-facing nodes (a typed billboard, or a legacy presentation billboard other than None; the Shared billboards gap,
  phase 1): the node object is the controller (EMPTY, never the mesh object, the source TRS even with an anchor or, without
  TRS, the decomposition in every package version, no constraint, neither `mt_billboard_constraint` nor
  `mt_billboard_raw_mode`, every action); one presentation child `<name>.mt_facing` per such node
  (`mt_facing_node_index`, parented to the controller, identity parent inverse, location zero, unit scale,
  `mt_billboard_constraint` listing the facing constraint types and `mt_billboard_raw_mode` exactly when the typed
  billboard has one, and exactly these constraints: `import_rules.anchor_constraint` first for a nonzero anchor (a
  COPY_LOCATION with offset in mt_root's custom space toward the anchor empty), then the facing constraints
  `import_rules.billboard_constraints` restates: name, type, target and settings); one anchor empty `<name>.mt_anchor`
  exactly where `import_rules.anchor_empty` gives an anchor (`mt_anchor_node_index`, parented to mt_root, identity parent
  inverse, the Float32 document anchor as its location); one pivot empty `<name>.mt_pivot` exactly where
  `import_rules.pivot_empty` says (`mt_pivot_node_index`, parented to the presentation child, `Translation(-pivot)` or the
  shear residual as its parent inverse); the node's children and primitives under the pivot empty or else the
  presentation child with an identity parent inverse; and, in the animation, no action, NLA track or driver on a
  presentation child, pivot empty or anchor empty (a complete-matrix channel on a camera-facing node without TRS, which
  keys the controller's location, is reported as unmodeled: BMT emits no such node);
- package versions 6 and 7 (canonical Shared 0152179): a typed billboard with a typed construction
  (`import_rules.typed_facing`) is compared as `_typed_facing_constraints` builds it: the presentation child's
  `mt_billboard_NN` constraints (`import_rules.typed_facing_constraints`), every helper empty
  (`import_rules.typed_facing_helpers`: name, parent, identity parent inverse, rotation mode, stored location and scale,
  constraints), the node's content under the helper a node lock or a reflection inserts, and every gate and handedness
  driver (`compare_d_anim.construction_drivers`, exact expression and variables); in version 7, every primitive object's
  `mt_reflected_face_policy`, the `mt_reflected_face_winding` NODES modifier and its determinant driver exactly on the
  drawn-winding primitives, never-drawn objects hidden, and no lone never-drawn primitive as its node's mesh object.
  The restatements were compared with the 0152179 importer's own functions, executed against recording stand-ins for
  bpy (TestOutput/billboards-reader-20260928, review fixes);
- every `mt_*` property the importer copies from the package, on nodes, primitive children, materials, images, meshes,
  shape keys, the scene and the root (the stored value, or the JSON text `_set_prop` writes), with the lighting model,
  the blend record's E and T terms and the surface render method they imply, and `mt_attributes` naming attributes
  that exist with their declared type and domain;
- `mt_native`: every owner a native-state row targets holds exactly its rows, in order, and no other owner holds any;
- polygon material indices, and no vertex group, modifier, armature or constraint where the package declares no skin,
  depth bias, billboard or (version 7) drawn-winding reflected-face rule.

What D does NOT compare, stated rather than implied: node ROTATION (readback.py dumps neither `rotation_quaternion` nor
`matrix_basis`, and the importer sets location, rotation and scale without updating the view layer before it saves, so
the saved `matrix_world` / `matrix_local` are not guaranteed to reflect them; rotation is covered only through
`mt_local_matrix`); the parent inverse under a sheared parent (it depends on Blender's own decomposition; the corpus has 0
and every skip is counted in the result's `stats`); a locked-track constraint on a singular rest world
(`billboardConstraintsNotCompared`); the material node graphs and their texture bindings (hop F's renders judge the
drawn result); armature bones and vertex-group weights; the derived `mt_billboard_constraint` JSON beyond its listed
constraint types (the constraints themselves are compared); a typed-facing helper's lever location when a node's
extent lies within 1e-6 of a power of two (`typedFacingHelperLocationsNotCompared`), and the driven scale of a
lock-handedness helper (`typedFacingDrivenScalesNotCompared`).

A synthetic package (`probe_builders.build_d_synthetic`, with its exact dump) joins the corpus samples: reversed winding,
a degenerate triangle, eight-bit and float colors, two UV sets, tangents, welded n-gons with every attribute domain, an
unrepresentable stream, relative and absolute morphs, a flat-shaded primitive, a multi-primitive node, a node without
TRS (a quarter turn with a non-uniform scale), node / material / image extras, native-state rows for every owner kind,
a BC1 DDS and a Non-Color PNG.

**F (render probes on Blender 5.1).** Every probe and every probe control is rendered in BOTH engines on every run
(design 6.4): Cycles on the CPU and EEVEE, one receipt document per engine (`probes-cycles`, `probes-eevee`) and each
control once per engine; the engine-independent transmission route has its own document.
- *Blend pairs.* The 11 FNV pairs as unlit textured cells over five emission background columns, eleven source colors
  and alphas per pair (the six historical ones and the design 6.4 worst-case sources), rendered with no denoising, no
  adaptive sampling, no clamping, Standard view transform, dither 0, and read back from a 32-bit EXR. The measured display value is the scene-linear sample through the
  sRGB curve; Blender's own 8-bit PNG is cross-checked against it (a diagnostic). Every enabled blend in the probe
  package carries the display record the Shared writer emits for it (`renderState.blend.display`: family, fit, bound,
  lit curve; `probe_builders.display_record_for_blend` restates the writer's classification over set B of
  `display_blend_constants.json`), and the importer draws that fit. Three analytic results are reported per cell and
  pair: `drawn` (the declared fit, E and T from display-space k0 and k1, composited as Blender does, E + T * lin(Cd)
  through the Standard view), `gamebryo` (the source's framebuffer, out = sf * Cs + df * Cd on display-encoded values,
  clamped) and `linear` (the retired linear graph, a diagnostic). The gate is fixed, with no switch: every cell within
  1/255 of `drawn` (Blender draws the declared graph) and within the declared bound + 1/255 of `gamebryo` (the reported
  bound holds on a real render; Exact pairs have bound 0). The receipt's `blenderBlendedIn` names `the declared display
  fit`, `linear space` or `mixed or neither`. A probe-design check beside it needs no render (design 6.4 check 3,
  `check_blend_exercise`): each non-exact pair's declared fit reaches its bound - 2/255 against Gamebryo on some cell,
  recomputed from the cells' bytes (with the worst-case sources every set-B bound is reached within 0.62/255; over
  within 0.13), so an understated bound would show in the render. Every pair is drawn through three copies of the
  source texture: the PNG (whose cells keep their historical ids), an uncompressed BGRA8 DDS with the same bytes, and a
  BC3 DDS of solid 4 x 4 blocks carrying each source's nearest RGB565-exact color and its exact 8-bit alpha (the corpus
  ships DDS; Cycles decodes it through its own path and EEVEE may sample the blocks directly). The per-pair table
  reports the worst error per container.
- *Lit route* (review finding 3, design 6.4 "lit twin probe"): the importer's OWN lit route, one lit family per lit
  curve it draws: over (power, p 1.5), SRC_ALPHA/ONE (scaled-power), ONE/ONE (constant), premultiplied over
  ONE/INV_SRC_ALPHA (the generic linear curve at exponent 1, grid-measured lit bound) and SRC_ALPHA/ZERO (the generic
  linear curve at its searched exponent 2.07, so the render draws the POWER nodes the exponent adds; review fixes 2).
  Lit blended cells (five texels at
  alphas 64, 128, 191, 85 and 255, over the five backgrounds) beside one opaque lit twin per texel (the same material
  without a blend), under one sun of strength pi at normal incidence (angle 0, no shadow, specular factor 0; no
  indirect light in Cycles). The expectation needs no lighting model: every blended lit cell within 1/255 of
  m(As) L_twin + tau(As) lin(Cd), with L_twin the twin's rendered color and (m, tau) the declared curve, and within the
  declared lit bound + 1/255 of Gamebryo blending the lit display color display(L_twin). Lit and twin cells are read as
  inner-block means (the display-blend variant's lit pass; in Cycles from a border render at 4096 samples, since
  Cycles' closure pick makes a lit blended cell noisy).
- *Texel rule* (display-blend design 4.1 and CHALLENGE items 5 and 6), on the same render: unlit OPAQUE cells (no blend,
  no alpha test, so the importer never links the Alpha output) sample every source texel, alpha 64 to 191 included,
  through each container. Every cell must show its STRAIGHT texel within 1/255, because the importer loads every image
  not declared premultiplied as `CHANNEL_PACKED`. Two hypotheses are reported beside it, and the receipt's `texelRule`
  names the one the render fits per container: `cycles-premultiplied` (Blender's default STRAIGHT in Cycles,
  floor(c * a / 255) in byte space, measured on the 2026-09-25 render) and `eevee-premultiplied` (STRAIGHT in EEVEE, the
  decoded color times alpha, recalled). The check also fails when a partial-alpha cell separates the hypotheses by 2/255
  or less, so a passing render has excluded premultiplication; the alpha-255 cells agree under every hypothesis. The
  separations on the probe are 27.7 to 96.3/255.
- *Asymmetric color ramp* (section 6.3): 16 asymmetric texels times 8 vertex colors, eight-bit and float storage, unlit;
  the expectation is the gamma-space product tex * vc.
- *DDS orientation* (section 6.4): one asymmetric 16 x 16 pattern as a PNG, an uncompressed DDS and a BC1 DDS (solid
  RGB565-exact blocks, so every decoder returns the authored colors). The gate is the render (every block at its authored
  place); the image buffers Blender loaded are also classified `same` / `flipped` against the authored rows, which answers
  whether `V_FLIP_CONTAINERS` needs `dds`.
- *Transmission route* (R22): `readback.py --import-glb` with `--expect-transmission 1 --expect-ior 1.0
  --expect-roughness 0 --expect-metallic 0 --expect-base-color-image --expect-emission-image` on a synthetic GLB that
  takes the route with distinct T and E images and a per-vertex COLOR_0. readback.py only asks that SOME image feeds
  each socket, so the harness also judges the bindings by identity: the one image upstream of Base Color must hold the
  bytes of baseColorTexture (T), the one upstream of Emission Color those of emissiveTexture (E), by packed SHA-256; a
  vertex-color node must feed Base Color (T = baseColor x COLOR_0) and must not feed Emission Color; the mesh must
  carry the color attribute. The pinned GLB writer does not emit the route yet (`material.transmission-unimplemented`;
  0 of the 57 corpus GLBs carry KHR_materials_transmission), so the GLB is built here to the design's declared
  semantics.

## Controls (each must be detected; a control that passes invalidates its hop)

| Hop | Control | Detected when |
|---|---|---|
| B' | A GLB with a VEC4 (`_PSX_COLOR_0`) and a VEC3 (`_PSX_FLAGS_0`) float custom attribute, 12 fresh processes | At least one, but not every, judged process fails WITH the set-order defect's signature (numpy's "input array dimensions except for the concatenation axis must match exactly", io_scene_gltf2 `mesh.py` line 273), and no process fails without it. INCONCLUSIVE (the hop fails) on a failure without the signature, on disagreeing dumps, or on the defect in all 12 (it is intermittent by construction: 12 of 12 by chance is about (5/12)^12, 3e-5, so a deterministic failure means the processes are not independent). NMT measured 5 of 12 on Blender 5.1; if each process fails independently with probability 5/12, all 12 pass with probability (7/12)^12, about 0.15%. Launches the harness ended (timeout, watchdog) never count. |
| D | One shape-key vertex moved by +1 source unit in a package copy (every primitive vertex welded to it moves together) | The comparison of the control's readback against the PRISTINE expectation reports a mismatch AT `mesh M prim P/shape_keys/1` (an element boundary: `shape_keys/10` does not count). |
| D | One packed DDS re-encoded through `save()` and packed again inside Blender (`inside_blender/repack_dds_control.py`) | The comparison reports `images/<index>/packed_sha256`. |
| D | One animation key moved: +0.5 on key 1 of component 0 of the first Translation, Scale or MorphWeights channel with two keys or more, in a package copy (`controls.move_animation_key`) | The comparison against the PRISTINE package reports `animations/<clip>/channels/<channel>/0`. |
| F (each engine) | Every pair's source and destination factors exchanged in a package copy (E and T swapped; each swapped equation carries the display record the writer would give it, since the importer refuses an enabled affine blend without one) | Every pair whose pristine cells passed the `drawn` model and whose `drawn` expectation the swap changes misses the unswapped expectation by more than 1/255 (or, when `drawn` is masked on every such pair, the same under the `linear` diagnostic; the detail says which). |
| F (each engine) | C2, planted display nodes (`controls.plant_display_nodes`): every declared two-node or anchored fit's nodes at (0, 1), every one-node fit's node at 0, in a package copy | Every pair whose pristine cells passed and whose `drawn` expectation the planted fit moves by more than 1/255 misses the PRISTINE expectation by more than 1/255 (separations 4.1 to 14.6/255 on the probe): the importer draws the record's constants, not a hard-coded rule. |
| F (each engine) | C4, planted lit exponent (`controls.plant_lit_exponent`): every power lit curve (lit over, p 1.5) and every generic linear lit curve whose exponent is not 1 (SRC_ALPHA/ZERO, p 2.07) at p 1 in a package copy | The over and SRC_ALPHA/ZERO lit cells miss the PRISTINE lit expectation by more than 1/255 where the pristine lit pair passed (premultiplied over is already at exponent 1: not applicable). |
| F (each engine) | The ramp's vertex colors declared Linear in a package copy (the gamma-space step omitted) | Some cell misses tex * vc by more than 1/255. |
| F (each engine) | Both DDS images stored upside down in a package copy | Both DDS quads render wrong while the PNG quad does not change. |
| F | The transmission GLB with KHR_materials_transmission stripped | readback exits 3 on the route's expectations AND the copy reads back opaque (`--expect-opaque`, re-evaluated with readback.py's own `check_expectations`). |
| F | The transmission GLB with E and T exchanged (baseColorTexture <-> emissiveTexture, base color RGB <-> emissive factor; `controls.swap_transmission_terms`), design row F's "E and T swapped (both blend checks)" | The binding identity fails at BOTH sockets: Base Color binds E's bytes and Emission Color binds T's. |

A control is judged at the element it mutates, and only where the pristine comparison passed: when the pristine input
already fails there (masking) the control reads masked, never detected, and the hop fails as NEVER EXERCISED, as in the
gate-1a runner. Masking counts every mismatch, not only the first 200 whose details a check records. Corpus samples come
first; a D candidate whose pristine comparison already fails at the control element is skipped BEFORE anything is
launched (and listed), so the control runs on the next candidate; when no selected sample reaches a D control, it runs
on the synthetic set. An F control is judged only on a probe launched in this run: when its import or probe launch
fails (or the probe reports a setting it could not apply) it reads NEVER EXERCISED, and a `probe-result.json` left by
an earlier run is never read.

## Two questions the F probe answers, and one the owner decides

- **Where Blender blends.** Answered: Blender 5.1 composites the Transparent BSDF plus Emission as E + T * Cd in
  scene-linear light (the 2026-09-25 render, 330 of 330 cells to 0.000/255 once the texel is modeled), while Gamebryo
  blends display-encoded values (inferred for the Gamebryo PC source: a D3D9 8-bit UNORM target without sRGB write; not
  established for Xbox 360 or PS3 framebuffers). The importer therefore draws a display-space fit for every enabled
  affine render-state blend (display-blend implementation 2026-09-27): exact where the source's result is affine in the
  linear destination, otherwise the set-B fit whose error the fidelity row reports as a bound (over 14.1854/255,
  additive 13.1153, multiplicative 4.1849, SRC_COLOR/SRC_COLOR 15.9039, SRC_ALPHA/SRC_COLOR 16.1405; lit over 36.4189,
  lit SRC_ALPHA/ONE 45.5909, lit ONE/ONE 43.637). An equation outside every family and tabulated pair (none in the FNV
  slice-10 census) draws the candidate fit with the smallest grid-measured bound (premultiplied over: the
  SRC_ALPHA/SRC_COLOR fit, 16.1935/255), and every other lit-scaled equation the generic linear lit curve (design 3.5,
  m = s(As)^p, tau = t(As)^p) at the exponent from 0.5 to 3, in hundredths, with the smallest grid-measured lit bound
  (SRC_ALPHA/ZERO: p 2.07, 6.9032/255 where p 1 measures 73.2697; premultiplied over: p 1, 80.1618/255, since it is
  ONE/ONE at As 0 and the route keeps tau within [0, 1], which its lighting row states). The probe-only variant (`run_display_blend_probe.py`)
  measured that Cycles and EEVEE draw those graphs within 1/255 of the model, transmission above 1 included through
  Value nodes; hop F now checks, in both engines, that the importer's own graph does, lit route included, on the same
  judgment. Owner decision D1 (the additive trade-off) is taken as set B provisionally; set K (exact over black,
  80.1618/255 at mid gray) is a data edit in the Shared `BlendDisplayConstants` and in
  `probe_builders.DISPLAY_ADDITIVE_SET`, then a re-pin of the tests and documents `BlendDisplayConstants`' remarks list.
- **DDS orientation.** `loadedBufferOrientation` answers the section-6.4 question directly.
- **Texel rule.** `texelRule` per container says whether Blender hands the graph the straight texel under
  `CHANNEL_PACKED`, per engine (both are rendered on every run). Before the fix, 47 opaque materials
  in the slice-10 packages (17 packages) rendered darker than their source because their BC2/BC3 base texture carries
  alpha below 255 on nonzero texels.
- **Color ramp.** Expected to pass as the importer multiplies vertex colors in the declared space (RE-11 is still an
  inference about Gamebryo).

## Estimate

About 1,175 Blender launches for the full run: `--version` (1), B' 58 GLBs x 12 + the control 12 (708), D 209 packages
plus the synthetic set, import and readback each (420) plus 4 control launches, F 39 (per engine 18: four probes and
their five controls, an import and a probe launch each; plus the transmission route's three readbacks). Assumed per launch on this host
(not measured, Blender has not been run here): 3 to 5 s for a B' import (Blender start plus a small GLB), 5 to 10 s
each for a D import and an `--include-values` readback (larger packages up to 20 MB), 10 to 30 s for an F render. That
gives roughly 45 to 60 min for B', 35 to 70 min for D and 8 to 20 min for F (the lit probe's Cycles border render at 4096
samples included): **about 1.5 to 2.5 hours** when memory is
available, plus any admission waits. The smoke run is 48 launches, 5 to 8 minutes. Per-launch peak private bytes
are expected well under the 4 GiB ceiling (a 20 MB package; the fake measured 0.05 to 0.65 GiB for a Python process).

## Independence argument

- What is independent of the code under test: the readback dump (Blender's stored data, parsed from JSON), every
  package and GLB value (read with the gate-1a readers), the exact mesh dump (the C# side), the blend expectations from
  the NIF factor definitions (the `gamebryo` model) and the declared display fit evaluated by the committed
  `display_blend_math` (the `drawn` model), the ramp and orientation expectations typed from the authored bytes, the
  transmission bindings read from the GLB itself, and every direct `mt_*` copy (compared with the package member it
  copies).
- What is NOT independent, said plainly: hop D's geometry expectation applies the importer's DECLARED conversions,
  restated in `import_rules.py` from `import_model.py` (winding reversal, welding, degenerate-face omission, UV
  (u, 1 - v), color storage and domain, attribute domains). An error in such a rule that the importer and
  `import_rules` share would pass D; D then proves that Blender stores exactly what those rules produce (storage, round
  trip, packing, shape keys), not that the rules are right. The package-versus-dump agreement check runs the same rules
  on both sources, so it re-checks hop C's inputs rather than the rules.
- Where the rules ARE pinned from outside the importer: the self-check's design vectors, typed from the design text and
  checked against `import_rules` (section 6.2: stencil Both = culling off, Clockwise = culling on and winding reversed,
  NIF triangles exact, UV maps exact up to 8, relative morphs added to the base in float32 as absolute shape keys;
  section 6.3: BYTE_COLOR keeps the eight-bit source bytes, FLOAT_COLOR the raw floats; section 6.4: an exact
  `mt_source_normal` per corner); the E/T terms of the 11 pairs worked out by hand from out = sf * S + df * Cd; hop F's
  renders (the orientation probe catches a wrong V flip, the ramp a wrong color space, the blend pairs a wrong E/T
  graph); and hop E (writer versus writer, consistency). Welding order, degenerate-face omission and loop order have no
  outside anchor beyond the exact dump's own face arrays.
- The self-check also runs the importer's own pure functions against `import_rules` on hand tables and on every real
  package: a disagreement there is a finding to adjudicate against the design (either side may be wrong), never
  presumed an oracle bug.
- The only programs launched are Blender (by the driver, through the gate), the fake (by the self-check) and, in the
  self-check only, the pinned Khronos validator on the synthetic GLBs.

## Self-check

```
python tools/scripts/gate1a/blender/selfcheck_blender_harness.py
```

No Blender, no build, no dotnet. Writes `TestOutput/gate1a-blender-dev/selfcheck-<stamp>/selfcheck.json` and exits 1 on
any failed expectation. It proves: admission waits, admits and aborts on timeout; admission waits while another Blender
runs; weaker limits are refused (API and CLI); the watchdog kills on low available memory, on scripted private bytes and
on the REAL private-bytes measurement of a fake that allocates 400 MB against a 150 MB cap; unmeasurable limits and a
wrong priority kill fail-closed; the timeout; suspended creation, BelowNormal and the job object; a job kill ends a
grandchild; an exception escaping the loop kills the tree; the taskkill fallback; the REAL Toolhelp32 scan; the
environment scrub; serial launches and the cross-process Global run mutex; the version refusal; resume (nothing
relaunched, one launch after a deleted receipt, two after a changed input, a failed launch kept until
`--retry-failed`); `--limit`, `--filter`, `--dry-run`; a missing declared file failing its sample; hop D on a
hand-typed readback and every mutation (hierarchy, transforms, parent inverses, copied properties, native states, stray
skin data included); `import_rules` against the design's hand vectors and against the importer, the importer's display
graph against `import_rules.display_blend` and its refusal of a record-less blend; the untabulated candidate choice and
the generic linear lit curve (its searched exponent included) against the numpy mirror of the C# routine; the
importer's lit factors against `display_blend_math` for every lit form; hop D's display-record checks each failing at
its element (a changed `mt_blend_graph.display`, an unlit route on a lit surface, a package record drifting from set B)
and counting records against the constants end to end; the blend models (drawn, Gamebryo, linear) on hand values; the
probe exercising every declared bound (design 6.4 check 3), failing on bounds planted 3/255 above what the probe
attains; hop F's bound-holds clauses failing on bounds planted 2/255 below what a render drawing the declared fits and
curves attains (blend pairs and lit route); every B', D and F verdict including masking, element boundaries and the
detail cap; hop F in both engines with its twelve controls (C2 and C4 included), the retired lit route failing the lit
check on the tabulated curves, and an ideal Gamebryo framebuffer failing the drawn and lit checks while holding every
bound; the transmission binding
identity and the E/T swap verdict; the DDS writers against both decoders; the probe script's pixel helpers; the control
builders (C2's planted nodes and C4's planted exponent included); and every hop end to end through the fake, passing and failing each way a real run can (crossed E/T
bindings, a dropped COLOR_0, a masked D candidate, a stale probe result next to a failed control launch, and B'
controls failing without the signature or in every process included). Use `--skip-end-to-end` for the fast half and `--real-packages N` to
bound the real-package cross-check.

## Assumptions and limits

- Blender itself has never been run by this harness's author: `inside_blender/probe_render.py` and
  `repack_dds_control.py` are written against the Blender 5.1 Python API and only their pure helpers are tested. Every
  setting the probe applies, or fails to apply, is recorded in the probe result (`settings`, `errors`); an error makes
  the probe launch exit 4 and fails the F check with the message.
- `readback.py` finds the base-color and emission bindings by socket identifier (`Base Color`, `Emission Color`), and the
  harness's identity check walks the same links; if Blender 5.1's Principled BSDF identifiers differ from those names,
  the pristine transmission check fails with readback's own message, which then concerns readback.py, not the importer.
- Node rotation is not in readback.py's object dump (see "What D does NOT compare"). Adding `rotation_quaternion` (or
  `matrix_basis`) to `_dump_object` in Shared would let hop D compare it bit for bit against the authored TRS.
- The blend-pair sources include low alphas (64/255). The 2026-09-25 render measured what Blender's default STRAIGHT does
  to them in Cycles: the texel is associated in byte space with truncation, then unassociated before the sRGB decode
  only where the Alpha output is linked (a 1.03/255 residue on the alpha pairs, 153 to 200/255 on the pairs that never
  read alpha). The importer now loads `CHANNEL_PACKED`; `fixtures/hop_f_blend_pairs_20260925.json` keeps that render so
  the self-check holds `fake_render.delivered_texel` to it. The importer's `PREMUL` branch (images declared
  premultiplied) is exercised by neither hop D nor hop F: no probe or corpus image declares premultiplied alpha.
- The EXR from `save_render` is taken as scene-linear (the Standard view transform applies to the PNG only); the
  PNG-versus-EXR difference recorded per probe shows it if not.
- Hop F's viewer half (Khronos Sample Viewer or three.js for the transmission route) is not a Blender hop and is not
  covered here.
- Hop F renders both engines on every run (design 6.4), which doubles its launches; EEVEE needs a GPU context in
  background mode, which the probe-only variant had on this host.
- Hop D holds `mt_blend_graph.display` to the package's record and `route` to `import_rules.display_route`, and every
  C#-written record of a NIF factor pair to `probe_builders.display_record_and_kinds` (set B of the committed constants
  file): a drift between the Shared writer's constants and `display_blend_constants.json` fails hop D. A pinned or exact
  bound must agree to 1e-12; a bound the writer measured at plan time (an untabulated equation's fit, the generic
  linear lit curve) within 1e-6/255, since C# `Math.Pow` and numpy may differ in the last ulp (review finding 6); every
  fit and curve parameter agrees exactly, the generic curve's searched exponent included (its candidates are a
  hundredth apart, and the restated search reproduces the C# choice on every equation of
  `derive/review_fixes2_mirror.json`; a libm-level tie between two exponents would read as drift).
- Design 6.4 control C3 (the texel declared premultiplied) is NOT implemented: the fake refuses to model `PREMUL`,
  whose texel is recalled on both readings and not yet rendered, so the self-check could not hold an expectation for
  it, and the importer's `PREMUL` load under a render-state equation is itself an open item (no fidelity row reports
  it). Hop F's texel rule plus the recorded 2026-09-25 render still discriminate straight from premultiplied texels.
- A package the importer refuses (`import_model:` on stderr, exit 1) fails its D sample; the admission rows that should
  have kept such an item out of a package are not re-derived here.
