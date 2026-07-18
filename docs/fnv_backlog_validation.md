# Fallout: New Vegas Backlog Validation

Date: 2026-07-17

This ledger records the evidence behind the current Fallout: New Vegas rendering
backlog. It deliberately separates implemented behavior from live visual
acceptance so a headless gate is not later mistaken for a user verdict.

## Current-tree gate

- Packaged Windows profiler Release build: zero errors (329 existing analyzer and
  obsolete-API warnings across the full dependency graph).
- Portable Release test build: zero errors (302 existing analyzer warnings).
- Focused water/bloom/shadow/shader/profiler matrix: **132/132 passed**, zero
  skipped.
- Broad FNV matrix with `RUN_BUCKET_B=1`: **211/211 passed**, zero skipped,
  against the bundled PC-final `FalloutNV.esm` (121,525 parsed records).
- The additional PC-final water/geometry/grass/imagespace subset plus the
  large-phase animation fixture passes **10/10**.
- Whole-tree `git diff --check` is clean apart from line-ending notices.

## Issue ledger

### Grass was untextured, incomplete, and floating

The report had two independent causes, both corrected on the current tree.
`TallGrassShaderProperty` now has its own texture-property decode instead of the
incorrect no-lighting layout, and the disk-cache version invalidates old
untextured entries. FNV placement no longer inherits Skyrim/FO4 random yaw and
continuous scaling; it uses the recovered no-yaw packed-height transform and
B/T/N slope basis.

The installed-master gate emits 10,255 placements across 78 WastelandNV cells.
The three `NVGreenGrass` models decode as complete, double-sided textured cutouts
(40/24, 18/6, and 54/54 vertices/triangles), and
`textures\landscape\grass\nvgreengrass.dds` resolves from `Fallout - Textures2.bsa`
as BC2. The current steep-terrain capture is documented in
[`fnv-grass-morphology-transform-retail-20260717`](../TestOutput/fnv-completion/fnv-grass-morphology-transform-retail-20260717/evidence.md).
`FnvTallGrassRetailAssetTests` independently opens only the retail Meshes and
Textures2 archives and pins those paths, counts, double-sided flags, cutout
classification, and exact alpha thresholds.

Status: code and retail-data gates pass; reproduce the original GUI view before
making any further visual change. Exact retail RNG distribution and the packed
fractional per-instance terrain/color-light payload remain fidelity omissions,
but neither explains missing textures or floating/incomplete meshes.

### Some grass still showed isolated floating facets

The follow-up screenshot is not explained by corrupt retail topology. A complete
audit of all 17 `landscape\grass` NIFs found valid indices, finite nondegenerate
triangles, sane bounds, and coherent height-linked vertex alpha. That alpha is
the authored TallGrass wind weight: roots are fixed and tips bend horizontally;
one finite phase is shared by the whole instance. The renderer uses submesh-local
vertex/index views and a coherent `uInstanceBase + SV_InstanceID` route, so no
specific torn-triangle or instance-base defect is currently proven.

Status: open live artifact, with negative retail/CPU/GPU-contract evidence. The
next useful input is the exact offending grass model or placed reference plus a
camera pose and animation time. No global topology or wind workaround was added.

### Water became black at night, opaque, and changed across a cell boundary

The two reported Lake Mead positions straddle the exact exterior-cell boundary
at X=49152. Retail CELL `0x000DDDF8` at (11,12) authors
`DLC03TBCleanWater` (`0x000E2C29`), while adjacent CELL `0x000DDDF7` at
(12,12) authors `NVCleanWater` (`0x001009CA`). The old camera-cell-global bind
therefore changed every visible lake tile when the camera crossed that line.

Generated FNV water is now sorted into contiguous per-`XCWT` draw batches. Each
batch carries its own WATR constants, maps, and recovered noise prepass while
sharing the one valid water plane and `WATER001` snapshot. Refraction validates
the displaced depth footprint before sampling scene color, preventing foreground
silhouettes from being pulled into sharp bright water edges. Translucent geometry
wholly below the plane is drawn into the snapshot and the complementary
intersecting/above-water set remains after the surface, so underwater decals no
longer bypass water composition.

The retail gate pins `FFEncounterWorld` (`0x00031E12`) to `DefaultWater`
(`0x18`). That WATR leaves layer 1 stationary and literally carries the finite
MSVC debug-fill float `0xCDCDCDCD` (`-431602080`) in the layer 2/3 wind
direction and speed fields. The recovered executable still integrates those
values and wraps the UV phase each frame. The viewer's absolute-time equivalent
had performed the large multiplication in single precision, discarding every
fractional UV and making the result stationary. Phase accumulation now uses the
deterministic double-precision continuous-time equivalent while preserving the
exact retail inputs; it does not imitate the engine's frame-rate-dependent float
rounding artifact. Both a pinned large-phase fixture and the PC-final
`DefaultWater` record change between fixed animation times.

The fixed noon/night profiler scenario passes 53 assertions in
[`fnv-water001-retail-fallback-final-20260717`](../TestOutput/fnv-completion/fnv-water001-retail-fallback-final-20260717/).

Status: per-WATR batching, composition ordering, displaced-refraction guard, and
animation have code/retail gates. Night color, perceived transparency, the two
reported Lake Mead positions, and FFEncounterWorld motion remain live GUI
acceptance checks.

### `SandDust02.NIF` was too opaque

The current decoder preserves all three authored material-alpha controllers,
including replacement rather than multiplication semantics, and the retail
`SRC_ALPHA / INV_SRC_ALPHA` (6/7) blend. A complete ESM census found eight
enabled, geographically separate placements; the isolated Goodsprings capture
proves one owner produces exactly three blended controller draws, so duplicate
placement or renderer fan-out is not the cause.

The direct current-binary GPU comparison actually becomes brighter than the old
path because it restores missing RGB controller windows; its alpha envelope is
effectively unchanged for this asset. Evidence:

- [`fnv-sanddust-current-gpu-20260717`](../TestOutput/fnv-completion/fnv-sanddust-current-gpu-20260717/evidence.md)
- [`fnv-sanddust-placed-audit-20260717`](../TestOutput/fnv-completion/fnv-sanddust-placed-audit-20260717/evidence.md)

Status: no narrower alpha/blend/placement defect is proven. Do not apply another
global opacity reduction without a matched retail/current GUI capture at the
same animation clock and camera. One real but not-yet-causal limitation is that
the default static particle cloud is baked at time zero while its material-alpha
controllers advance with renderer time; the opt-in live-particle path uses the
requested clock. Compare static versus live density before scoping any fix.

### Lighting looked flat and high-contrast

The world path now has a strict active FNV ID193/`BSSM_ADT`/`SLS2000` base route,
plus recovered classic emission, specular LOD, environment mapping, and simple
parallax. The current working tree also preserves signed XRDS and FNV's exact
full-float effective radius (`LIGH radius + XRDS`, no XSCL), geometry-bound light
membership/order, and the ID220/ID143 prepared point-color equation as CPU
oracles.

Status: materially improved but not declared complete. Positive local-light
routing remains fail-closed because the viewer cannot yet supply retail
per-property candidates, world-light/scene-offset/geometry bounds,
`fForcedDarkness`, current ShadowSceneLight LOD dimmers, batch splitting, or the
final sampler policy. See [`terrain_texture_engine_parity.md`](terrain_texture_engine_parity.md).

### Prospector neon bloom was much too broad

The follow-up diagonal smears had an exact topology cause. Retail
`ImageSpaceEffectBlur` is separable: one vertical `BPBLUR` bright-pass draw feeds
one horizontal plain `BLUR` draw. The port had collapsed both scales into a
single `(1/width, 1/height)` tap row, visibly drawing the kernel as a diagonal
streak. The intermediate target and second PSO are restored, including the
recovered plain-blur alpha behavior. A directional impulse gate now checks axial
symmetry and explicitly rejects the old diagonal response.

The FNV tonemap/bloom topology remains game-scoped, no-lighting draws no longer
receive the incorrect blanket imagespace `EmissiveMult`, and the fixed Prospector
Saloon off/on pair passes its deterministic contribution gate in
[`prospector-neon-bloom-retail-final-20260716`](../TestOutput/fnv-completion/prospector-neon-bloom-retail-final-20260716/).

Status: exact diagonal-kernel defect fixed; exact retail intensity and radius
remain a live visual verdict.

### A square around the camera skipped sun shadows

The near cascades are refreshed after all screen-space drawing. Dense scenes
could exhaust the shared transient ring, after which the pass cleared a near map
but failed its late constant-buffer allocation. Its old enabled bit remained, so
the shader selected the cleared near cascade before the populated farther one—an
axis-aligned, camera-centered fully lit square.

The frame now protects a 3,328-byte shadow tail before scene allocation (the
worst 12-allocation sequence needs at most 3,087 bytes), releases it only at
shadow replay, and accounts for that protected limit in blended/particle
planning. Cascade availability is published individually; an empty refreshed
map is disabled so sampling falls through to the next populated cascade.

Status: fixed by capacity and per-cascade fallback contracts; live acceptance in
the originally affected dense scene remains required.

### Camp Golf front window Z-fought

`dungeons\NV_CampGolfCourse\NVCampGolfCourse.NIF` decodes five valid shapes. The
front overlay is an authored no-lighting alpha-blended decal (flags
`0x8E000000`) composed of 38 strips, and it already reaches the positive
reversed-Z decal-bias PSO. Its closest audited strips are approximately 0.079
world units from the backing geometry; neither invalid topology nor missed decal
classification is present.

Status: open live artifact. Bias insufficiency at the reported top-left window
is not proven without the camera pose/angle, so the global decal bias was not
increased speculatively.

### Distant hot pixels, especially railroad tracks

The representative long rail
`dungeons\metro\exterior\metroextrailsstraightlong.nif` has 500 vertices, 420
valid triangles, and 144 authored high-aspect triangles. Its diffuse texture is
fully opaque through all ten mips, its normal/specular mask has no hot outliers
and converges through nine mips, and both use the established anisotropic
samplers. FNV's 500→800-unit specular LOD, the direct/instanced fade routes,
MSAA sample state, and lit-result firefly clamp are present. The material is
nevertheless gloss 100 and specular-enabled.

Status: open live discriminator, not a missing-mip or corrupt-topology defect.
Capture the exact offending rail FormID/camera with specular enabled and disabled.
If points survive specular-off, thin-silhouette temporal/post AA or geometry LOD
is the relevant boundary; if they disappear, the remaining work is specular
minification.

### Activators were always shown on

Placed REFR state now starts from the record's Initially Disabled flag and
resolves normal/inverse XESP enable-parent chains across loaded cells. The
resolver has an actual visited set, so long valid chains work and malformed
self/multi-node cycles fail disabled. The inspector's session-only
`Authored / On / Off` override affects drawing, picking, walk collision, and the
collision overlay.

The drawable retail gate is Hoover Dam REFR `0x0015E4A5`, whose
`Effects\Ambient\FXFireMed01.NIF` is authored on but disabled through parent
`0x0015D98C`.

Status: fixed and covered; this does not simulate later quest/script state.

### Effect planes could be stood on, and CliffVerti still clipped

Collision caching now has explicit unresolved, resolved-null, and resolved-mesh
states, separated by ordinary/effect category. Effect models keep authored Havok
but never receive visual-mesh or OBND collision fallback. Known-null results are
byte-bounded cached and do not repeatedly consume warmup slots. Collision-only
Havok is retained even when a model has no render geometry.

Retail gates prove no authored Havok for
`NVLimestoneDustStormHalfViz.NIF` or `IndFXLightRaysRight01.NIF`, preserve the
17-triangle authored Havok in `effects\box03.nif`, and preserve all 1,460
triangles in `landscape\rocks\cliffs\CliffVerti_C2.NIF`.

Status: fixed and covered for walking, ground probing, picking, and overlay.

### Layered dust storms glowed instead of dimming

The named limestone and 18-layer storm assets are authored as standard 6/7 alpha,
not multiplicative. A pale HalfViz texture can therefore brighten a dark
background and dim a white one; forcibly reclassifying it as multiply would be
incorrect. The actual Hidden Valley 18-layer night event dims its authored
weather backdrop in the current GPU gate. True multiplicative controls continue
to use their decoded factors.

Status: blend classification is correct. A full scripted Hidden Valley 23:00
on/off capture remains the useful live acceptance gate; see
[`layered-dust-retail-20260716`](../TestOutput/fnv-completion/layered-dust-retail-20260716/evidence.md).

### Clouds moved too quickly

FNV cloud motion now follows the retail scalar contract:
`ONAM/255 * fWeatherCloudSpeedMax(0.1) * weather wind * seconds`. Weather
transitions blend ONAM and wind independently before multiplying them, avoiding
the former two-times-fast midpoint. Empty ONAM uses retail byte `0x33`, and an
out-of-range nonempty array reuses slot zero.

Status: fixed, with an installed-master census and deterministic current/outgoing
weather captures in the `fnv-cloud-motion-*` evidence directories.

### Imagespace appeared permanently orange outdoors

There is no missing CELL/WRLD-to-IMGS link in the current tree. Each frame resolves
the camera/focus CELL and applies `CELL XCIM -> WRLD/parent INAM -> engine
default`, then evaluates WTHR time-band IMAD. Camera-position REGN weather is
wired through the bounded XCLR/RPLI/RPLD/RDWT route.

Retail data explains the ordinary Mojave appearance: WastelandNV uses
`NVDefaultExterior` (`0x0008809D`); its only exterior XCIM names that same IMGS,
and every other exterior cell has no XCIM. `FFEncounterWorld` is the distinct
base-grade control (`WastelandBaseImageSpace`, `0x00064608`). Searchlight proves
the separate area chain `CELL -> REGN -> WTHR -> IMAD`.

Status: base selection is correct. Remaining weather-state gaps are chance/RNG
lists, GLOB predicates, overlapping-region priority, edge-falloff blending,
climate weather RNG, and per-IMAD elapsed animation time. If a fresh view still
looks stuck, inspect `baseImageSpace`, `weatherSelectionSource`, and region
telemetry before changing the selector. Evidence:

- [`imagespace-boundary-retail-20260716`](../TestOutput/fnv-completion/imagespace-boundary-retail-20260716/)
- [`fnv-weather-imagespace-bands-retail-20260716`](../TestOutput/fnv-completion/fnv-weather-imagespace-bands-retail-20260716/)
- [`fnv-region-weather-retail-20260717`](../TestOutput/fnv-completion/fnv-region-weather-retail-20260717/)

`FnvImageSpaceSelectionRetailTests` now pins that exact Wasteland source change,
same-grade result, and the distinct FFEncounter control against the installed
master.

## Live validation still worth doing

The next build should be checked at the two supplied Lake Mead positions,
FFEncounterWorld, Prospector Saloon, the original shadow-square scene, the named
Camp Golf window, the offending grass placement, and an exact sparkling rail
reference. Grass/Camp Golf/rail need the missing model or FormID plus camera pose
to become deterministic gates. SandDust perceived opacity and a named
non-default region weather remain useful older checks. These are visual
acceptance checks, not permission to weaken recovered data contracts.
