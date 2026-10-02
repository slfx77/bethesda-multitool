# Normalized NIF export: per-vertex parity gate (2026-09-23)

This is the corpus evidence for program item M2.1: switching BMT's production NIF and SpeedTree GLB export from the
native writer (`GlbWriter`) to the normalized path (`NifNeutralSceneAdapter` into Shared `SceneGltfBuilder` and
`GltfExporter`). No production call site has switched yet. The router exists with every call site defaulting to the
native writer and no NIF family admitted. This record says what the normalized path carries today, measured file by
file against the native writer, so admissions can cite it.

## What the gate compares

`NifGlbProductionParityCorpusTests` (Bucket B, sequential) runs each stratum through the production assembly route
(the CLI's `NifExportSceneAssembly`, or the GUI Mesh Viewer's `BuildViewerSceneWithDiagnostics` and `ToGlbScene`),
plans the file through `NifGlbExport.Plan` with the normalized preference forced (production admits no family), and
encodes both writers' GLBs.

- **Layer A, source to document, exact.** Per drawable part: positions, normals, colors, UVs, indices, owning node,
  material mapping, node prefix and edges, and tangents all equal the values the native helpers compute. Surfaces with
  authored tangents must equal `GlbWriter.ReadTangent` over `NpcGlbTangentBuilder` exactly. The source graph must be
  unchanged afterwards.
- **Layer B, decoded GLB against decoded GLB.** Triangles pair by oriented identity (exact key first, then tolerance
  matching within the same material in a spatial cell); any unpaired triangle fails. Tolerances: local position
  max(1e-5, 2^-20 x |p|), world position max(1e-3, 1e-6 x |p|), normal and tangent 1e-5 per component, tangent w
  exact, UV 1e-6, colors within the exact range the native byte encoding maps to (the native writer truncates to
  UNSIGNED_BYTE, measured). Material content signatures per triangle, native mesh and material names present in the
  normalized output, native world placements contained, `ExtensionsUsed` equal.
- **Known native behavior accounted without admitting loss.** The native writer drops triangles with two equal
  positions: the normalized output must hold exactly that many such triangles, and the rest must pair one to one.
  Identical material rows the native writer merges must match by signature.
- **Khronos glTF Validator 2.0.0-dev.3.10**, required for every run below: zero errors and zero warnings on every
  normalized output.
- A frozen ratchet (`NifGlbParityRatchet.json`) now pins each stratum's compared floor, parse-error and no-scene
  lists, the allowed decline reasons with their ceilings, and the feature classes it must keep exercising.

## Results

Sample size is the first N candidates ordered by the SHA-256 of the lower-cased relative path, so the selection is
stable and spread across folders. "Declined" files go to the native writer with a named reason.

| Stratum | Source | Candidates | Inspected | Compared (all pass) | Declined | No scene | Native triangles | Repeated-position drops accounted | Validated, errors, warnings |
|---|---|---:|---:|---:|---:|---:|---:|---:|---|
| S1 | FNV PC loose, CLI route | 20,542 | 600 | 539 | 33 | 28 | 915,500 | 221,020 | 539, 0, 0 |
| S2 | FNV PC loose, GUI route | 20,542 | 150 | 135 | 10 | 5 | 212,047 | 51,432 | 135, 0, 0 |
| S3 | FNV Xbox 360 July 2010, big-endian | 22,014 | 100 | 92 | 5 | 3 | 134,974 | 14,434 | 92, 0, 0 |
| S4 | Oblivion | 8,032 | 150 | 133 | 16 | 1 | 288,613 | 277 | 133, 0, 0 |
| S5 | Fallout 3 | 10,989 | 150 | 137 | 5 | 8 | 171,366 | 50,164 | 137, 0, 0 |
| S6 | Skyrim | 17,216 | 100 | 62 | 30 | 8 | 70,643 | 0 | 62, 0, 0 |
| S6 | Skyrim Special Edition | 18,862 | 100 | 40 | 18 | 42 | 42,495 | 0 | 40, 0, 0 |
| S7 | Fallout 4 | 34,995 | 100 | 10 | 86 | 4 | 9,929 | 0 | 10, 0, 0 |
| S7 | Fallout 76 | 63,305 | 100 | 3 | 96 | 1 | 8,014 | 0 | 3, 0, 0 |
| S7 | Starfield | 31,058 | 100 | 61 | 39 | 0 | 232,581 | 183 | 61, 0, 0 |
| S8 | SpeedTree, 10 trees at seeds 1, 2, 7 | 30 | 30 | 30 | 0 | 0 | 99,332 | 0 | 30, 0, 0 |
| S9 | Fixed FNV fixtures (tincan, neon) | 3 | 3 | 2 | 0 | 0 | 2,017 | 0 | 2, 0, 0 |

Zero comparison failures and zero Shared-validation faults in every stratum. Decline reasons by family:

- FNV, Fallout 3, Skyrim: skinned parts only (outside M2.1's static scope). Oblivion adds one non-finite texture
  coordinate.
- Skyrim Special Edition: decals 13, emissive and external emittance 3, effect tint 1, falloff 1.
- Fallout 4: maps beyond diffuse 77, decals 6, effect tint 2, falloff 1. Fallout 76: maps beyond diffuse 83, skinned
  10, effect tint 2, falloff 1. Starfield: material render state 37, effect composition 2.

So the normalized path is exact where it adapts, and today it adapts most static FNV, Fallout 3, Oblivion and
Skyrim content. The modern games decline mostly because the shared material contract cannot yet carry their maps and
render state.

## Defects the gate found and fixed

- **Tangents on surfaces with authored tangents** (Xbox 360 stratum, 2 files). The adapter converted authored tangents
  itself (near-zero cutoff 1e-4, fallback +X) while the native writer runs `NpcGlbTangentBuilder` (cutoff 1e-6,
  cross-product fallback). The adapter now uses the native builder whenever tangents are authored, which also removed
  an out-of-range read on short arrays. Regression tests pin it with a fixture where the two fallbacks differ.
- **A native memory leak in the test oracle.** `GetPixels()` on a Magick.NET image was never disposed, and the view
  keeps the image's native pixel cache alive past `Dispose` and the GC (about 7.8 MB per 1024x1024 decode, measured).
  One unguarded 600-file run grew to 10.9 GB private and left the machine at 0.85 GB free before it was stopped by
  hand. All nine `GetPixels()` sites in tests and the EgtAnalyzer tool now dispose the view; production already did.
  After the fix the same 600 files peak at 1.24 GB.
- The gate's GUI-route strata now build the export resolver per file and renew the browser service every 25 files,
  because `NifBrowserService`'s texture cache only shrinks under the GUI's memory coordinator. That production growth
  path is recorded for the resource-budget program; it is not fixed here.

## How it was run

Every run went through a runner that takes a machine-wide build slot at the default 4 GiB admission, sets
`DOTNET_GCHeapHardLimit` to 6 GiB, and polls every 5 seconds, killing the test host below 2.5 GB free or above 6 GB
private. The admission gate only measures at entry, which is how the leak above nearly starved the machine. Two runs
were killed by the watchdog while a virtual machine was using about 8 GB; both were rerun once memory returned.
Sample data was read from the primary checkout's `Sample` tree; the validator was NeversoftMultitool's local copy
through `GLTF_VALIDATOR_EXE`, until the shared validator tooling lands.

Receipts: `TestOutput/nif-glb-parity/<stratum>.receipt.json`. Runner summaries, JUnit files and console logs:
`TestOutput/m21-nif-switch-20260923/gate*/`.

## Not done yet

- **No family is admitted and no call site has switched.** Admission is one line per family in
  `NifGlbNormalizedAdmission` plus a call-site default, and needs the owner's decisions below.
- **Owner decisions.** Whether the larger, structurally different normalized output is acceptable (for example the
  neon fixture is about 1.5 times the native size, because every source vertex and image occurrence is kept); whether
  M2.1 adds an `export spt` command or counts SpeedTree as covered through NIF composition; whether a sampled corpus is
  enough for the retirement receipt or a full FNV run is required (`BMT_NIF_PARITY_FULL=1` runs every candidate).
- Skinned export, NPC and creature export, terrain and runtime-mesh export and Granny remain on the native writer, as
  planned for later M2.1 steps.
