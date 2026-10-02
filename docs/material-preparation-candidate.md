# Material preparation candidate

This isolated candidate starts from BMT validation checkpoint
`fb75e345c16d10aeac751966ead9ac4c35cf5aa1`, with Shared pinned to
`2d498be3aac356795a983f34088bfddfd93686f8`. The September 20 continuation resumes
from `9f08c55d` in `C:/dev/Multitool-worktrees/bmt-material-preparation-20260912`.
The initial portable application and test builds passed with ordinary analyzers;
current acceptance is recorded below. This work does not complete M2.1. The
historical SpeedTree result of **0 carried, 10 declined** remains historical
evidence at its original source, separate from subsequent corpus measurements.

`NifMaterialPreparation` owns the policy extracted from the 534-line
`GlbWriter.GetOrCreateMaterial`: source precedence, tint and alpha preparation,
normal/gloss packing, environment-mask and height heuristics, static Starfield
ORM, BGSM and inline emission, authored sky and water routes. Its prepared result
holds encoded images, factors, addressing, diagnostic names and viewer metadata.
The original 35 material-key fields and the position of the cache lookup remain:
source preparation and CE2 ORM resolution precede lookup; secondary packing and
PNG encoding occur only after a miss. The legacy writer translates the result to
SharpGLTF. The normalized adapter translates the same result to `SceneMaterial`.
Execution exposed inherited aliasing when different source tints bake different
pixels from the same diffuse path: the textured base-color factor becomes white
for both. A 36th key field retains the baked diffuse tint. Independent pixel
expectations require separate differently tinted materials and reuse for equal
tints; this is an intentional correction to the legacy cache policy.

Eligible normal, metallic-roughness, specular and occlusion channels now cross the
normalized boundary with their factors. All image samplers retain explicit
linear minification/magnification and the resolved U/V addressing. Material reuse
uses the existing key; images use prepared occurrence identity, so equal names
cannot alias different content. An ORM image can back both metallic-roughness and
occlusion. Newly admitted normal-mapped geometry reuses the native tangent builder
and basis conversion. Vertex colors use the native material-dependent projection.

The pinned shared contract has no emissive/HDR, IOR, transmission or clearcoat
fields. Those outputs retain the native writer, as do viewer extensions and
unsupported CE2 glass composition. Existing source declines remain for separate
specular/gradient/glow/cube/classic maps, external emittance, decals, falloff,
effect tint, authored Oblivion shader inputs and carried Starfield color/alpha
state. Existing vegetation billboard, wind, LOD and far-LOD declines also remain.
No placeholder channel or exception catch disguises unsupported or malformed
input. The legacy writer retains its existing approximations; this extraction is
not a claim of engine rendering parity for those approximations.

`NifMaterialPreparationTests` supplies independent pixel and factor expectations
before comparing native and shared encoded artifacts. Cases cover normal/gloss
and height packing, alpha cutoff, sampler flags, missing-normal defaults, cache
reuse, equal image names, drawable CE2 ORM and its missing-image fallback, HDR
and inline glow, inactive external BGSM precedence, unlit routing, water optics,
resolver failures and an exact float-domain opacity threshold. A swapped-UV
fixture checks the generated tangent's direction and negative handedness after
basis conversion; a tinted surface checks that baked color replaces raw vertex
modulation without modifying source bytes. The original 16 cases are extended
with two native/neutral tint-cache regression cases. The synthetic ORM
database helper gains an optional diffuse slot, so its drawable tests actually
reach material construction. The older no-draw fixture remains unchanged.

The two source-text tests for ORM channel routing and float cutoff are replaced
by encoded-output fixture checks. The former blanket normal-map decline test
now verifies the still unsupported separate specular map; normal maps receive
positive pixel/factor assertions. Existing packer and corpus tests are retained.

The neutral skin-placement correction continues from candidate checkpoint
`59c63252d1d983f919d1d01bfc30b205965fc0a9`. The production scene builder can emit a
skinned part without `NodeIndex`. The legacy GLB writer places each such part
through its joint palette and inverse binds, independently of an optional mesh
owner. The adapter now preserves the complete original node prefix, including
node indices, labels, transforms and original parent-child edges, then appends
one identity-transform placement per drawable skinned occurrence as a root child.
Each placement keeps its own mesh, palette and inverse binds. It records the
original unfiltered mesh-part ordinal, with optional original node and geometry
block references, so repeated labels and repeated part objects remain distinct.
Rigid parts keep their original owning-node grouping and cannot share a mesh
with skinned parts. Winding normalization uses an owned geometry clone, preserving
source objects and arrays. All authored influences remain available to the
shared pose evaluator and exporter.

`NifNeutralSkinPlacementTests` contains **17 synthetic cases**. They cover
anonymous builder-shaped skins, explicit owner transforms without applying them
twice, hierarchical noncommuting scale/rotation/translation order, different
joint palettes and inverse binds on one owner, mixed rigid and
skinned geometry, repeated names and object references, filtered occurrence
ordinals, a fifth influence, rigid winding and source-buffer preservation, and
invalid owner references. The transform cases use independently derived world
positions and inspect actual native and shared GLB output. The corpus test keeps
its triangle-count and normal assertions and additionally requires the exact
original node prefix and child edges, one separate placement per drawable skin
occurrence, independent meshes and skins, exact palettes and converted inverse
binds, and matching provenance. The real-corpus class uses the required sequential
integration collection so large private-asset parses cannot run in parallel.

Six added samples vary the fifth joint over a one-second translation beneath a
noncommuting scaled/rotated hierarchy, with either one influence or all five.
They compare independently derived world positions with the shared evaluator and
all encoded JOINTS/WEIGHTS sets, preserving the source. These are sampled static
poses, not evidence of NIF animation-track transport or playback. Execution also
exposed SharpGLTF Toolkit 1.0.6 rejecting duplicate labels in a skin armature.
The native writer now registers unnamed temporary builders and restores original
names immediately before conversion, which identifies nodes by object identity.
Output and source labels remain repeated; hierarchy and bind matrices are unchanged.

Two fixed-hash external FNV fixtures (`tincan01.nif` and `_male/upperbody.nif`)
require actual base-color and normal images. Their oracle compares decoded RGBA
pixels, material factors/samplers, oriented triangle attributes and multiplicity,
world-space skin positions, complete source influences, original hierarchy,
palette/bind domains and occurrence provenance. Source object/buffer identities
and content are checked before the legacy writer's existing winding mutation.
Adversarial portable fixtures verify that the oracle detects geometry, winding,
image, placement, duplicate-occurrence, fifth-influence and source-mutation errors.
The null-texture broad corpus remains a separate geometry/admission check.

The [scoped source review](complexity/neutral-skin-placement-v1.json) records file
and callable hashes, complexity bounds, external costs and validation limits.
C# syntax parsing and `git diff --check` passed at that historical review; it is
not a source-hash receipt for the later test/writer changes. Production dispatch still uses the
legacy writer. This correction does not enable a shared renderer or replace the
legacy export route before parity checks pass.

Before adoption, build through the pinned bounded wrapper and run the material,
NIF adapter, water/sky/emission/alpha/ORM and affected export suites. Then run the
opt-in private NIF and SpeedTree corpus checks and existing visual comparisons.
Syntax parsing and source comparisons can detect extraction mistakes but do not
establish compilation, image parity, memory bounds or corpus acceptance.

## September 20 execution

The first run passed 61/67 candidate/oracle cases and exposed the two inherited
defects above (two tint rows and four duplicate-name skin rows). The 87 affected
portable sky/water/emission/alpha/ORM/material/hierarchy cases and retail Starfield
water check passed. Both fixed textured corpus fixtures passed. Khronos validator
2.0.0-dev.3.10 reported zero errors for their four GLBs; the shared upper-body
export has two non-root skin-node warnings and one retained empty-node info.
The broad NIF run exposed repeated-position triangles discarded by the legacy
Toolkit; corpus assertions now distinguish that exact rule from geometry loss.
The first Xbox Vault 22 alpha-conversion attempt failed the fail-skips gate because
the historical loose fixture path was unavailable. The original Xbox entry was
subsequently located inside its Final-build BSA; a bounded in-memory lookup with
Xbox metadata, big-endian marker and pinned hash now passes on Windows and Ubuntu.
Both alpha-conversion cases pass without skips. Logs are under
`TestOutput/material-resume-20260920`, with filtered JUnit files in `TestOutput`.

After the fixes, the candidate, oracle and CLI group passed **88/88**, and the
affected material/viewer group passed **92/92**, both without skips. Eight further
portable water cases, two retail-water/synthetic-alpha cases and the accessibility
ratchet also passed. The broader NIF run inspected 120 files and compared 119
adapted scenes with no adapter declines; one file did not reach adaptation. Across
three scenes, native output discarded 59 exact repeated-position triangles. The
normalized output retains them, and the oracle explicitly accounts for this
native-writer behavior without admitting missing distinct-position triangles.

SpeedTree comparison exposed a material-table difference: native output merges
identical missing-texture material rows, whereas Shared retains their occurrences.
The revised oracle compares distinct material content and still checks every
triangle's material binding. Two adversarial cases cover identical-row reuse and
changed per-triangle bindings. The formal rerun passed: **10 carried, 0 declined**,
with 32,534 static triangles compared and no native degenerate drops. Eight trees
have missing authored leaf textures: 17 path occurrences representing 13 distinct
paths. These remain material-fixture gaps; matching fallback pixels do not establish
the missing authored appearance. `wastelandshrub01` and `wastelandundergrowth01`
have complete authored textures and produce bounded render-comparison artifacts.
This new measurement does not rewrite the historical 0/10 result at its old source.

Blender 5.1.1 CPU Cycles comparisons use four common cameras, 384-pixel captures,
32 samples, a fixed seed, and imported materials without overrides. The two
`tincan01` exports are pixel-identical in every view. The upper-body silhouette
IoU is 1 in every view; maximum foreground linear RGB RMSE is 0.00003584. Both
fully textured SpeedTrees also have pixel-identical native/shared captures in all
four views. Their four GLBs have zero Khronos errors or warnings (native output
has one unused-object information item per tree). These
are third-party GLB export comparisons, not Bethesda D3D renderer acceptance.
The actual portable CLI exported both fixtures successfully, producing bytes
identical to the tested native GLBs. `pex inspect --help` also succeeded; portable
execution alone does not establish Windows host selection.

A later portable build compiled successfully but reported six AD0001 analyzer
memory failures under the coordinator's adaptive 6 GiB compiler heap. That run
does not satisfy analyzer acceptance. A retry passed using ordinary analyzers,
serial compilation and an explicit 12 GiB compiler heap, retaining the default
4 GiB admission threshold. The application then rebuilt without warnings; the
final test build has 71 inherited warnings, zero errors and no AD0001 or copy-lock warnings.
Application/test DLL and PDB hashes agree across object, application and test
output directories. The expanded candidate/helper/CLI/export group passes
**99/99** on Windows and Ubuntu, without skips. Ubuntu ran the same portable
managed binaries in place under the coordinator; this is runtime evidence, not
a Linux source-build or self-contained-publish claim. The NIF/SpeedTree corpus
group passes **10/10** on Windows without skips. The locked Windows GUI build and
self-contained publish also pass, with nine existing warnings, zero errors and
no AD0001 or copy-lock warnings. The published executable's bare
`pex inspect --help` selects the CLI successfully, and its two NIF exports match
the tested native GLBs byte-for-byte. Actual GUI workflow acceptance remains
pending. The computer-use native pipe is
unavailable; no desktop workflow acceptance is inferred from source checks.
