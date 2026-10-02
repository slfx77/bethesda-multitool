<!-- Implementation plan for the cut-2 Starfield .mesh model reader (design section 8, "Later cuts", and Appendix C.1). Drafted 2026-09-28 by a read-only planning pass over the worktree at BMT 2fa671a6 (clean) with Shared at 2e7af70 (the gitlink, equal to canonical main). The design (docs/design/model-document-design-20260923.md) stays authoritative; where this plan disagrees with it or with the existing decoder, section 0.4 says why. Nothing was built, tested or launched; the measurements are one read-only Python process at a time over the Starfield install. -->

# Cut-2 Starfield `.mesh` model reader: implementation plan

I read the design, the cut-1a and cut-1c plans, the Shared contracts at 2e7af70, BMT's `.mesh` decoder and its one caller, and I measured the whole retail `.mesh` population with an independent Python decoder. I changed nothing under `src`, `tests` or Shared. The staged files are this plan and the probe (`tools/scripts/gate2/starfield_mesh_probe.py`).

- **Worktree:** `C:/dev/Multitool-worktrees/bmt-material-preparation-20260912`, HEAD 2fa671a6. The Shared submodule is at 2e7af70, which is canonical main, so this cut needs no pin bump.
- **Starfield install:** `D:/SteamLibrary/SteamApps/common/Starfield/Data`, found the way `RealAssetPaths.SteamGameFile` finds a game (every fixed drive's `SteamLibrary`). The task text named `E:`, where Starfield is not installed on this machine.
- **Path prefixes:** `BMT/` = `src/BethesdaMultitool/`, `SH/` = `shared/Multitool.Shared/src/`, `T/` = `tests/BethesdaMultitool.Tests/`, `R/` = `TestOutput/cut2-starfield-mesh-20260928/receipts/`, `P` = `tools/scripts/gate2/starfield_mesh_probe.py` (staged).
- **Labels:** **(measured)** has a receipt in R/; **(assumed)** is a chosen value awaiting RE or an owner ruling; **(inferred)** is reasoning not checked against data; **(recalled)** is memory of external software, unverified.

## Receipts (R/)

| File | Holds |
|---|---|
| `trial/` | The first 3,000 records decoded with the layout `StarfieldMeshFile` implements (meshlet tail mandatory): 530 failures, all "meshletCount runs past the end" at exactly EOF. This is the run that found the optional tail. |
| `census/census-records.jsonl.gz`, `census-summary.json`, `census.log` | `P census`: all 720,957 `.mesh` entries of the 11 GNRL archives that hold `geometries\`, decoded in full (one record per entry: sizes, SHA-256, every count, the value checks below, the probe verdict on the 64 KiB prefix). 878 s, one process. |
| `census/controls.jsonl.gz`, `controls-summary.json` | The NotAModel population: every non-`.mesh` entry of those 11 archives plus up to 1,000 evenly spaced entries of every other GNRL archive, 180,350 entries, probed on their 64 KiB prefix with two bound settings. |
| `plan_measure_census.py`, `.json` | Distributions over the census records: scale values, tail cross-tabs (by normal W, archive, weights, LODs), weights by `weightsPerVertex`, cull overshoot, color saturation, bounds. |
| `plan_measure_dec4.py`, `.json` | The float32 value of every 10-bit Dec4 code under three routes (exact, binary64-then-float32, the legacy float32 route). |
| `frames/frames.json` | `P frames`: 3,604 files (every 200th): stored tangent against the UV-derived frame, both W readings, and one-rounding positions against the legacy two-rounding product; plus the raw codes of every near-zero normal and tangent. |
| `cover/cover-manifest.json`, `cover-report.txt` | `P cover`: the pairwise MILP joint cover (60 files), 7 edge files and 16 decline controls, SHA-256 pinned, with every other place each payload lives (`alsoIn`). |
| `verify/verify.json` | `P verify`: all 79 retail rows re-read from their archives, size and SHA-256 equal; the altered-digit control detected. |
| `selfcheck-synthetic/`, `selfcheck-retail/` | `P selfcheck`: synthetic round trips (tail and tail-less, LODs, weights, UV1), every truncation point, trailing byte, version 3, out-of-range index, the tail-boundary control; then every truncation of the three smallest cover files. 45 and 48 checks, 0 failed. |

## Decisions this plan asks the owner for (all before slice 3)

| # | Decision | Recommendation |
|---|---|---|
| D1 | What the internal LOD index lists become | **Alternatives, not primitives.** LOD 0 (the main list) is the drawn primitive; each LOD list k becomes its own `SceneMesh` whose one primitive shares the main primitive's vertex and attribute buffers through `ScenePrimitive.WithTriangleIndices` (SH `Slfx77.Multitool.Core/Models/ScenePrimitive.cs:90`), on its own node, and the nodes form one exclusive `SceneLayerSet` group with LOD 0 default-on. Why: a mesh's primitives are "Ordered draw primitives" drawn together (`SceneMesh.cs:23`), so LOD primitives in one mesh would draw every level at once; layer sets are the contract's only alternative-selection vocabulary and they select nodes (`SceneLayerSet.cs`); cut 1a already maps NiLODNode this way ("finest level on", BMT `Core/Modeling/Nif/NifModelLayerReader.cs:24, 51`). Native state alone is refused by the design's rule that a writer never needs a native kind to reproduce appearance. Writers: GLB writes the default-on LOD and lists the rest as Metadata (`ModelGltfLayers.cs:386-396`); Blender makes collections, hidden unless default-on, Exact (`BlendAdmission.cs:211-217`). 1,744 files carry LOD lists (section 0.2). |
| D2 | The optional meshlet tail | **Fix `StarfieldMeshFile` itself** (additive, see section 1): after the LOD section, end of data means "no tail". Today it returns null on 6,470 retail files (0.9%), including all 4,832 in `Starfield - FaceMeshes.ba2`, so the viewer silently drops them. The fix changes the legacy renderer's output for exactly those files and must bump `ReferenceDecodedMeshDiskCache12.DecoderVersion` (99 today, `BMT/Core/Formats/Nif/Rendering/Gpu/D3D12/ReferenceDecodedMeshDiskCache12.cs:130`), because that cache persists negatives (memory `starfield_mesh_format_and_cell_units`). **Alternative:** a model-reader-only path; that is a second decode path, against the owner's cut-1b ruling "ONE decode path (extend runtime readers)". |
| D3 | Skin weights on a lone `.mesh` | **Typed attribute streams, not `SceneSkinInfluences`.** Per slot k, `starfield.bone.{k}` (UInt16 x1) and `starfield.weight.{k}` (UInt16 x1, normalized), Vertex domain, values exactly as stored. Why: `SceneSkinInfluences.JointIndices` are "Indices into the bound skin's joint palette" (`SceneSkinInfluences.cs:8`), and the palette (BSSkin::Instance bones, BSSkin::BoneData binds) lives in the referencing NIF, not in the `.mesh`. Typed as influences without a palette, both writers misbehave on retail data: 8,889 of the 17,544 weighted files repeat a bone with nonzero weight in one vertex (the exporter's rounding remainder, for example `(0, 1)` in the last slot, section 0.2), which the GLB writer classes invalid and refuses (`ModelGltfSignedLanes.cs:57`, `ModelGltfGeometry.cs:445-449`); and Blender's admission reports vertex groups Exact for any primitive with influences (`BlendAdmission.cs:651-653`) while `import_model.py` writes them only through `_bind_skin`, which only a node with a skin reaches (`import_model.py:4363-4364`). With streams: Blender widens each (UInt16, 1) to INT, Converted with values unchanged (`BlendAdmission.cs:678-690`); GLB drops them under `geometry.source-attribute-storage-omitted` (`ModelGltfGeometry.cs:649-652`), which the gate scope allows with reader evidence (as cut 1c's SA3 ruling does). The Starfield NIF reader (a later cut-2 row) turns them into `SceneSkinInfluences` when BSSkin::Instance supplies the palette. **Alternative:** `SceneSkinInfluences` with no skin, plus Shared ask SA-M1 (section 9). |
| D4 | Vertex colors | **A non-primary typed stream** `starfield.color` (UInt8 x4, normalized, RGBA after the BGRA relayout, Vertex domain, `ColorSpace.Unknown` with provenance Unknown and the evidence below); `SceneVertex.Color` stays (1, 1, 1, 1). Why: a lone `.mesh` has no material, and Starfield's CDB material decides whether vertex color tints at all (the legacy renderer fails closed without it, BMT `Core/Formats/Nif/Rendering/Geometry/NifSubmeshExtractor.cs:1291-1302`); 271,275 of the 343,029 color-bearing files are fully saturated masks (every RGB channel 0 or 255). The `VertexColorUse.Ignore` route is not available: the GLB writer blocks any vertex-color use other than base modulation (`ModelGltfMaterials.cs:345-353`). Blender stores the stream as BYTE_COLOR, Exact; GLB drops it with the allowed reason. **Alternative:** make it primary (drawn), which tints most files with masks. |
| D5 | Numeric routes of the typed values | **Correctly rounded float32 from the raw codes:** position = fl32(q x scale / 32767) evaluated in binary64; Dec4 channel = fl32((2v - 1023) / 1023) in binary64; UV = the half value (exact). Both are invertible on the whole sample (section 0.2), so they are Converted at 0.5 ulp as design section 6.1 defines. The legacy `Positions` and `Normals` (two float32 roundings) stay as they are for the renderer: they differ from the correctly rounded value on 5.99% of position components and on 556 of the 1,024 Dec4 codes, which is exactly what makes them hop A1's control. |
| D6 | The Dec4 zero sentinel `(511, 511, 511)` | **Keep the decoded value** (length 0.00169, direction (-1, -1, -1)): it is what the stream says and what a shader decoding the same code gets (inferred). The GLB writer normalizes it (`ModelGltfGeometry.cs:290-360`, Approximated, direction unchanged). The reader counts sentinel vectors in a diagnostic and native state. Every near-zero vector `P frames` collected is this code (up to 64 lanes from each of the 38,152 files with one: 225,031 tangent lanes with W 0 or 3, 9,642 normal lanes). **Alternative:** decode the sentinel as an exact zero; then the GLB writer refuses every primitive that references one ("Referenced zero tangent directions ... cannot produce a strict tangent basis") and 37,775 files carry sentinel tangents. |
| D7 | The probe rule | The bounded structural walk of section 4.1, NotAModel on any violated check, with **the normal-W tail rule**: a complete file that ends at the tail boundary while its first normal's W is 1 is Unsupported (truncated). The rule rests on a two-way equality over the whole corpus (6,470 files have normal W 0 and no tail; 714,487 have W 1 and the tail; no other combination). The reader also throws on W 1 without a tail and records W 0 with a tail as a diagnostic (0 retail files each). Bounds: `weightsPerVertex` at most 8 and `lodCount` at most 8 (measured maxima 8 and 3; the controls separate identically at 8/8 and 32/32). |
| D8 | Versions 0 and 1 | **Supported, from the reference layout, synthetic tests only.** Retail ships only version 2 (720,957 of 720,957). The layouts differ only in whether the LOD section is present (version 0 has none), per NifSkope's BSD-3 `MeshFile.cpp` as `StarfieldMeshFile` records (recalled through that file). The format metadata says "no retail sample" on those variants. **Alternative:** Unsupported with the reason "no retail sample". |
| D9 | Cover depth | **The pairwise MILP joint cover:** 60 files cover every value and every observed pair of values of (version, weights, LODs, colors, UV1, meshlet bucket) plus 19 file-level tags, then 7 edge files and 16 decline controls (section 8). For comparison, measured on the same cells: a single-value joint cover is 9 files, a three-way cover 156, and one file per observed cell 295. Pairwise is the smallest cover in which the reader's interacting branches (LOD layer sets sharing weight and color streams, the tail rule with weights, UV1 with colors) meet at least once. |
| D10 | The placeholder material | One material `starfield.mesh.placeholder`: base color (1, 1, 1, 1), opaque, single-sided, lit, `LightingModel.MetallicRoughness`, metallic 0, roughness 1, no textures, plus a document diagnostic `bmt.starfield.mesh.material-in-nif` ("materials are named by the referencing NIF and resolved through Starfield's material database; a lone .mesh carries none"). The same neutral surface cut 1c's legacy XnGine export uses (metallic 0, roughness 1). |
| D11 | Order against cut 1c | Slices 0 to 5 touch only new files, `StarfieldMeshFile.cs`, its test and the disk-cache constant, so they can run beside cut-1c slices 5 to 13. Slice 6 edits `BethesdaModelRegistration.cs`, `BethesdaModelWorkflow.cs` and `MeshCommand.cs`, which cut-1c slice 8 also edits: it goes after cut-1c slice 8. Gate 2 (mesh) runs after gate 1c. |
| D12 | `--game` on a `.mesh` | Absent, `auto` or `starfield`: the Starfield row. Any other game throws, as the XnGine readers do under a NIF-era game: the user asserted a game the file cannot belong to. |

---

## 0 Baseline facts

### 0.1 BMT today

**The decoder** (`BMT/Core/Formats/Nif/Rendering/Geometry/StarfieldMeshFile.cs`)
- Accepts versions 0 to 2 (`:25`), trims a non-multiple-of-3 index count silently (`:99`), rejects zero vertices (`:108`).
- Positions: `(short)q * (scale / 32767f)`, two float32 roundings (`:117-127`). Normals and tangents: `v / 511.5f - 1f`, two float32 roundings; tangent W maps code 0 to -1 and every other code to +1 (`:286`).
- Skips the weights (`:141`), the LOD lists (`:146`) and the meshlet and cull sections (`:157-158`) without exposing them, and requires the meshlet and cull counts, so a stream that ends after the LOD section returns null.
- Its doc comment and memory `starfield_mesh_format_and_cell_units` say the layout was verified on 3,003 files. That sample was drawn from `Starfield - Meshes01.ba2` only (the note samples "all 288,231", which is that archive's count), where every file has the tail.

**Its one caller.** `NifSubmeshExtractor.ExtractBsGeometry` (`BMT/Core/Formats/Nif/Rendering/Geometry/NifSubmeshExtractor.cs:1243-1302`), reached from the D3D12 viewer's `ReferenceMeshDecoder12`. A null parse is reported as a decode failure and the empty model is cached; `ReferenceDecodedMeshDiskCache12` persists negatives, so any decode change must bump `DecoderVersion` (`:130`, now 99).

**How a `.mesh` is referenced.** A Starfield `BSGeometry` block carries four LOD slots, each naming its own `.mesh` by path (`BMT/Core/Formats/Nif/Parser/NifSceneGraphBlockReader.cs:11-12, 572-654`); the viewer takes the first. So a `.mesh` is one level of one shape, and the internal LOD lists measured below are a second, rarer mechanism inside one file. Skinning comes from the block's `SkinRef` (BSSkin::Instance), materials from its shader property and Starfield's material database.

**Model plumbing.** The composition root registers only `NifModelReader` (`BMT/Core/Modeling/BethesdaModelRegistration.cs:63-66`). `.ba2` is already an archive input (`BethesdaModelWorkflow.cs:605-610`). The memory estimate uses the NIF bound when the entry length is unknown (`:94-101`). `--game` accepts `fnv`, `fo3` or `auto` in its help text (`CLI/Commands/Mesh/MeshCommand.cs:128-135`). Starfield's unit row exists: 1.0 m per unit, Assumed, with mesh-bounds evidence (`BMT/Core/Games/GameProfiles.cs:155-166`). The NIF basis is `NifModelUnits.Basis` (+Z up, +Y forward, right-handed, Assumed; `Core/Modeling/Nif/NifModelUnits.cs`).

**Tests.** `T/Core/Formats/Nif/Rendering/Geometry/StarfieldMeshFileTests.cs` is synthetic (one 6-vertex blob with the tail). No retail `.mesh` test exists.

### 0.2 Corpus facts (measured)

**Population** (`census-summary.json`, `plan_measure_census.json`)
- 720,957 `.mesh` entries in 11 GNRL archives: `Starfield - Meshes01` 288,231, `MeshesPatch` 324,491, `Meshes02` 62,685, `FaceMeshes` 4,832, `ShatteredSpace - Main01` 23,570, `SFBGS050 - Main` 12,923, `SFBGS00D` 3,460, `SFBGS003` 474, `SFBGS004` 150, `SFBGS047` 79, `SFBGS008` 62. Every one lives under `geometries\`, and no archive holds a `.mesh` elsewhere.
- 399,638 distinct payloads. Later archives (in name order) repeat 321,319 paths of earlier ones, always with the same bytes (0 differ).
- Sizes: 350,722 at most 4 KiB; 318,631 more at most 64 KiB; 49,900 up to 1 MiB; 1,704 above; the largest 3,690,290 bytes. 51,604 files exceed the 64 KiB probe prefix.

**Layout** (every file decodes and consumes its last byte exactly: 720,957 of 720,957 under the optional-tail rule; 0 decode failures)
- **Version 2 only** (720,957). Versions 0 and 1 have no retail file.
- Index count is a multiple of 3 on every file; no index is out of range; no triangle repeats a vertex; 44 files have unused vertices. Largest vertex count 65,080; largest index count 195,120.
- UV0, normals and tangents are present on every file with count equal to the vertex count. UV1 on 257,992 files, colors on 343,029; when present, the count equals the vertex count.
- **The meshlet and cull tail is optional**: absent on 6,470 files (all 4,832 of `FaceMeshes`, 788 of `ShatteredSpace - Main01`, 359 of `SFBGS00D`, 292 of `SFBGS003`, 199 of `SFBGS050`), present on 714,487.
- **Normal W equals tail presence**, two ways: normal W is 0 on exactly the 6,470 tail-less files and 1 on exactly the 714,487 others; it is uniform within a file; codes 2 and 3 never occur.
- Tangent W uses only codes 0 and 3: {0} on 350,631 files, {3} on 40,065, both on 330,261.
- **Scale** is a power of two on 720,953 files (1 to 262,144; 4.0 on 196,274, 8.0 on 132,297, 2.0 on 111,327, 1.0 on 111,205); the exceptions are 8.1203661 (2 files), 25 and 50. The largest |q| equals 32,767 on 6,946 files, so the scale is a power-of-two bound, not the exact extent.
- **Weights** on 17,544 files (12,874 distinct): `weightsPerVertex` 1 (6,507), 2 (741), 3 (631), 4 (1,111), 5 (1,160), 6 (2,472), 7 (1,093), 8 (3,829); the weight count always equals vertices x `weightsPerVertex`. The pair order is (bone, weight): per-vertex weight sums are 65,535 exactly on every vertex of 7,783 files and within 3 (4.6e-5) on the rest, while the swapped reading sums to 65,535 on none. 8,889 files repeat a bone with nonzero weight in one vertex; the two inspected (`geometries/00228164d71ff7a3f980/37b72ca8391b78605a1f.mesh`, `geometries/0080a2f6c9f256542d68/967a148cc70adddb2fc9.mesh`) put a remainder `(0, 1)` in the last slot. Largest bone index 142.
- **LOD lists** on 1,744 files: 1 list on 317, 2 on 725, 3 on 702. Every list is a multiple of 3, in range, no longer than the one before, and the first no longer than the main list.
- **Meshlets** on the 714,487 tailed files (up to 1,749 per file; at most 96 vertices and 128 triangles per meshlet). Record = (vertexCount, vertexOffset, triangleCount, triangleOffset). Triangle counts sum to the main list on every file. `vertexOffset` is the running sum of `vertexCount` on every file. `triangleOffset` is the byte offset of 3-byte local triangles with each meshlet's block padded to 4 bytes on every file; the index-unit reading (3 x the running triangle count) holds on only 339,403 (the control). The vertex counts sum to more than the vertex count on 376,695 files, equal it on 337,778, and fall short on 14, and a meshlet's triangles stay inside `[vertexOffset, vertexOffset + vertexCount)` on only 310,947 files: the meshlet-vertex list those offsets index is not in the file (inferred: the engine builds it).
- **Cull records**: count equals the meshlet count on every file. Read as center + extent, each box contains its meshlet's triangles on 713,595 of 714,487 files; read as min/max, on 4 (the control). The other 892 carry all-zero records or boxes smaller than their triangles (for example meshlet 3 of `geometries/000f6d8518d56c3bd9cd/fbe5af2f5fc956fc5549.mesh` is all zeros; `geometries/cd22d82db116cd23be14/2c419da859fbcaa5f714.mesh`, scale 131,072, sits near z = 100 km).
- **Non-finite UVs** in 493 files (31,640 UV0 components in 85 files, 82,359 UV1 components in 441 files).
- **Colors**: fully saturated (every RGB channel 0 or 255) on 271,275 of 343,029 files; alpha below 255 somewhere on 112,699.

**Frames and routes** (`frames.json`, `plan_measure_dec4.json`; 3,604 sampled files, 2,932,672 vertices, 2,874,534 with a usable UV frame)
- The stored tangent points along the UV-derived tangent (dot > 0) on 2,870,081 vertices (99.85%).
- W read as code 3 = +1 and code 0 = -1 makes `cross(N, T) * w` agree with the UV-derived bitangent +dP/dv (raw UVs, no V flip) on 2,709,115 vertices (94.25%); the opposite reading agrees on 165,395 (5.75%). That is the engine's DirectX frame. glTF's bitangent (tangent-space +Y, the top of the image for its top-left UV origin) runs along -dP/dv, so the same measurement says glTF's w is the stored sign negated: -1 for code 3, +1 for code 0 (review amendment, 2026-09-28; the cover's expectations agree on 301,980 of 314,182 UV-framed vertices).
- One-rounding positions recover q exactly on all 8,798,016 sampled components; the legacy two-rounding product differs from them on 526,616 (5.99%).
- Dec4: the binary64-then-float32 route equals the correctly rounded value on all 1,024 codes; the legacy float32 route differs on 556.
- Near-zero vectors (length below 0.01) are all code (511, 511, 511) among the lanes collected (up to 64 per file from every file with one): tangent lanes with W 0 (157,072) and W 3 (67,959), normal lanes with W 1 (9,642). 37,775 files carry near-zero tangents and 653 near-zero normals.

**Probe** (the rule of section 4.1, before the normal-W rule was added, which changes no complete retail file)
- Every `.mesh` is Supported: 669,353 Confirmed (665,644 with the tail, 3,709 tail-less), 51,604 Tentative (their 64 KiB prefix ends mid-file; 9,799 of them before the vertex count).
- The 180,350 controls are all NotAModel at both bound settings: 178,776 at the version dword, and 1,574 (they pass it) at deeper checks: vertex count 1,246, scale 144, index count against the declared length 129, `weightsPerVertex` 38, index count not a multiple of 3 16, too short 1. A probe that stopped after the version, the multiple-of-3 test and the length test would claim 1,428 of them. Extensions probed: `.nif` 83,384, `.ffxanim` 54,385, `.wem` 28,246, `.btd` 2,211, `.pex` 1,965, `.txt` 1,858, `.af` 1,428, `.afx` 1,414, `.dat` 1,357, `.dds` 1,067, `.biom` 1,014 and 22 more.

### 0.3 Contract facts at 2e7af70 that bind the reader

- **Primitives draw together.** `SceneMesh.Primitives` are "Ordered draw primitives" (`SH/Slfx77.Multitool.Core/Models/SceneMesh.cs:23`); alternatives are node groups (`SceneLayerSet`, exclusive group, at most one default-on per group, `SceneLayerSetValidation.cs`).
- **Buffer sharing.** `ScenePrimitive.WithTriangleIndices` shares every vertex and attribute buffer (`ScenePrimitive.cs:90`).
- **Skin influences presuppose a palette** (`SceneSkinInfluences.cs:8`). `SceneSkinValidation` checks every primitive's influences whether or not a node binds a skin, and the legacy interpretation rejects repeated nonzero joints (`SceneSkinValidation.cs:27-44, 150`). GLB: `NormalizeWeights` runs for any primitive with influences and refuses a duplicate positive joint (`ModelGltfGeometry.cs:399-462`, `ModelGltfSignedLanes.cs:57`); `AddInfluences` writes JOINTS/WEIGHTS for any primitive with influences (`SceneGltfSkinBuilder.cs:70`). Blender: admission rows for any primitive with influences (`BlendAdmission.cs:651-660`); the importer writes weights only in `_bind_skin` (`import_model.py:3772, 4363-4364`).
- **Attribute streams.** No half-float component type (`SceneAttributeComponentType.cs`), so half UVs become float32 (exact). Blender maps (UInt16, 1) to INT and (UInt16, 2) to INT32_2D, Converted, and (UInt8, 4) normalized to BYTE_COLOR, Exact; other shapes are Degraded (`BlendAdmission.cs:678-690`). GLB drops every unselected stream with `geometry.source-attribute-storage-omitted` (`ModelGltfGeometry.cs:649-652`).
- **Primary color.** Selected only by `PrimaryColorAttributeIndex` (`ScenePrimitive.cs:131`); `COLOR_0` is always written from `SceneVertex.Color` (`SceneGltfBuilder.cs:457`); a vertex-color use other than base modulation is blocked in GLB (`ModelGltfMaterials.cs:345-353`).
- **Tangents.** GLB requires w of exactly +1 or -1, normalizes non-unit directions (Approximated) and refuses a referenced zero direction (`ModelGltfGeometry.cs:290-360`). Blender carries authored tangents as `mt_source_tangent`, Metadata.
- **Native payloads.** One row holds at most 1,048,576 characters of JSON and 64 MiB of raw bytes, raw bytes only with `NativeDetail.Full` (`SceneNativeState.cs:9-11`); the GLB writer drops them with `source.opaque-payloads-omitted` (`ModelGltfAdmission.cs:229-230`).
- **`mesh info` counts per mesh** (`Inspection/ModelInfoDocument.cs:24-35`): a LOD mesh sharing the main primitive's vertices counts them again.
- **Probe budget.** 64 KiB, content only, `candidate.IsComplete` and `candidate.Length` (`Sources/ModelSourceCandidate.cs`).

### 0.4 Where existing text needs correcting

- `StarfieldMeshFile`'s doc comment and memory `starfield_mesh_format_and_cell_units` state that the tail is always present and the layout was verified on 3,003 files; the tail is optional (6,470 retail files) and normal W says which. Slice 0 corrects the comment; the memory note should be amended by the main session.
- The design lists "Starfield `.mesh`" among the later readers and cut 2 without detail; its unit row (1.0, Assumed, `WorldUnitsPerMetre = 1`) stands and needs no change.

---

## 1 Decoder strategy

**Reuse `StarfieldMeshFile` with additive members; build the document in new `Core/Modeling/Starfield` code.** The Python probe stays the independent decoder and never consumes C# results.

| Member (all additive) | Content | Why |
|---|---|---|
| `Version` | the u32 | Header native state, format metadata |
| `QuantizedPositions` (`short[]`), `Scale` (`float`) | raw q and the scale | One-rounding positions (D5); quantization native state |
| `Uv0Bits`, `Uv1Bits` (`ushort[]` or null) | raw half bits | The non-finite UV rule |
| `ColorBytes` (BGRA as stored) | raw | The color stream (D4); the legacy RGBA `VertexColors` stays |
| `NormalCodes`, `TangentCodes` (`uint[]`) | raw Dec4 words | Correctly rounded channels (D5), W codes, sentinel census (D6), normal-W tail rule (D7) |
| `WeightsPerVertex` (exists), `WeightPairs` (`ushort[]`, bone and weight interleaved) | raw | The weight streams (D3) |
| `LodIndexLists` (`ushort[][]`) | raw | LOD alternatives (D1) |
| `HasMeshletTail`, `Meshlets` (`uint[]`, 4 per record), `CullRecords` (`float[]`, 6 per record) | raw | Native state |
| `IndexCount` | the declared count | The reader throws when it is not a multiple of 3 (legacy trimming stays) |

**The one behavioral change (D2):** after the LOD section, end of data means the tail is absent; everything else about `Parse` (null on bad input, never throws, the legacy float routes) is unchanged. Two static helpers carry the exact routes for the reader, so the formula lives once: `PositionMeters(short q, float scale)` (binary64, one rounding) and `Dec4Channel(uint code)` (a 1,024-entry table built from `(2v - 1023) / 1023.0`, pinned against the Python exact table).

| Component | Verdict |
|---|---|
| `StarfieldMeshFile.Parse` | Reuse; additive members and the tail fix above |
| `NifSubmeshExtractor.ExtractBsGeometry` | Unchanged (it gains the tail-less files through the fix) |
| `StarfieldMaterialColorPolicy` | Not used: a lone `.mesh` has no material |
| `NifModelUnits.Basis`, `GameProfiles` Starfield row | Reuse as they are |
| `P` (`starfield_mesh_probe.py`) | Seed of hops A1, A2 and A3; its BA2 reader serves the gate resolver |

---

## 2 File blueprint

All files are BMT-owned. No Shared file changes under the recommendations.

`BMT/Core/Modeling/Starfield/`:
- **`StarfieldMeshModelReader.cs`:** `IModelSourceReader` plus `IModelSourceFormatMetadataProvider`. `FormatId` `bmt.starfield.mesh`. `SupportsInspectionWithoutPixelDecoding` true (the reader decodes no images). `MaximumSourceBytes` 16 MiB (assumed; the largest retail file is 3,690,290 bytes). `Read`: check the item and context name the same occurrence, read the bytes under the bound, hash them (SHA-256), parse with `StarfieldMeshFile`, apply the reader checks of section 3.2 (throw `InvalidDataException` with the offset and field), build geometry, layers, material, units, basis, native state, diagnostics and coverage, and return `new ModelDocument("bmt.starfield.mesh", ...) { SourceProvenance = new SceneSourceProvenance(path, sha) }`.
- **`StarfieldMeshModelProbe.cs`:** the rule of section 4.1.
- **`StarfieldMeshModelGeometry.cs`:** vertices, triangles, tangents, UV1, the color, weight and raw-UV streams, the LOD primitives.
- **`StarfieldMeshModelLayers.cs`:** LOD nodes and the exclusive layer-set group.
- **`StarfieldMeshModelCoverage.cs`:** the section census (section 3.4), built from the parse's section list, independent of what the geometry builder consumed.
- **`StarfieldMeshModelNativeState.cs`:** kind constants and bounded payloads (section 3.5).
- **`StarfieldMeshModelUnits.cs`:** units from the Starfield `GameProfiles` row, the NIF basis, the `--game` check (D12).
- **`StarfieldMeshModelFormatMetadata.cs`:** the catalog description (variants: "Starfield .mesh v2 with meshlet tail", "v2 without meshlet tail", "v0 (no retail sample)", "v1 (no retail sample)"; the unit row; the admission rules of this plan).

**Changed files**
- `BMT/Core/Formats/Nif/Rendering/Geometry/StarfieldMeshFile.cs`: section 1.
- `BMT/Core/Formats/Nif/Rendering/Gpu/D3D12/ReferenceDecodedMeshDiskCache12.cs`: `DecoderVersion` 99 to 100, with its two ratchet tests (`NifMaterialDiffusePolicyTests.DiskCache_DecoderVersion_*`, `ReferenceDecodedMeshDiskCache12Tests`).
- `BMT/Core/Modeling/BethesdaModelRegistration.cs`: the one-line registration (applied by the main session, `IMPLEMENTATION.md`).
- `BMT/Core/Modeling/BethesdaModelWorkflow.cs`: the per-format memory estimate (source length, no companion allowance for `.mesh`, the writer workspace).
- `BMT/CLI/Commands/Mesh/MeshCommand.cs`: `--game` help names `starfield`.

**Tests**
- `T/Core/Formats/Nif/Rendering/Geometry/StarfieldMeshFileTests.cs`: tail-less parse; the tail-boundary truncation (a 6-vertex blob cut at its tail parses as tail-less, the legacy contract; the model reader refuses it by the W rule); raw accessors equal the written values; the legacy outputs of a tailed blob unchanged (bit pins).
- `T/Helpers/StarfieldMeshTestBuilder.cs`: an independent writer of versions 0 to 2, with or without the tail, LODs, weights, UV1, colors, sentinel codes and non-finite halves (the existing test's private builder moves here).
- `T/Core/Modeling/Starfield/*`: probe, units, geometry, layers, coverage, native state, format metadata.
- `T/Core/Modeling/RealAsset/Cut2Mesh*.cs`: Bucket-B tests with `BucketBTestGuard`, the `Category` trait and `[Collection(SequentialIntegrationGroup.Name)]`, resolving Starfield through `RealAssetPaths.SteamGameDirectory("Starfield", "Data")`.
- `T/Core/Modeling/Samples/cut2-starfield-mesh-cover-manifest.json`: the cover (section 8), in the cut-1c manifest shape (`source` as `<SteamLibrary>/Starfield/Data/<archive>`).

**Oracles** (never part of the build)
- `tools/scripts/gate2/starfield_mesh_probe.py` (staged now): decoder, probe rule, census, cover, verify, selfcheck, frames, dump.
- `tools/scripts/gate2/run_gate2_mesh.py` and `produce_gate2_mesh.ps1` (slice 7): A1, A2 and A3 comparators over `mesh dump`, then `gate1a/hop_b.py`, `hop_c.py` and `hop_e.py` as they are.

---

## 3 Mapping

### 3.1 Document

- `ModelDocument("bmt.starfield.mesh", <entry stem>, ...)`, one scene. Node 0 `mesh` (identity TRS, `SceneNodeRole.Transform`) carries mesh 0 (the main list). With LOD lists, nodes 1..k carry meshes 1..k (section 3.3). The scene's roots are all of them.
- `Units`: `new SceneUnits(1.0, Assumed, <GameProfiles Starfield evidence>; "a .mesh belongs only to Starfield")`. `SourceBasis`: `NifModelUnits.Basis` (a `.mesh` is in its `BSGeometry`'s local frame, which is the NIF frame).
- `SourceProvenance`: the relative entry path and the SHA-256 of the bytes read.
- One material (D10). No images, samplers, skins or animations.

### 3.2 Geometry rules (primitive 0 of mesh 0)

- **Vertices** in stored order: position by D5; normal = the correctly rounded Dec4 channels (unnormalized, sentinel kept, D6); `TexCoord` = UV0 halves as float32 (a non-finite component reads 0, see below); color (1, 1, 1, 1).
- **Triangles** = the main index list as stored, as `int`. The reader throws when the count is not a multiple of 3 or an index is not below the vertex count (0 retail files each); unused vertices stay (44 files); no triangle is dropped (none repeats a vertex).
- `NormalMode` Vertex; `NormalProvenance` Authored.
- **Tangents** = `SceneTangents` of the correctly rounded channels with w = glTF's handedness, -1 for code 3 and +1 for code 0 (the stored bitangent sign negated; review amendment, owner ruling pending with RE-13); codes 1 and 2 throw (0 retail). The stored codes stay in the header row's `tangentW` census.
- **UV1** = `AdditionalTextureCoordinates[0]` when present.
- **Non-finite halves** (493 files): the portable coordinate reads 0 and the set's exact bits go into `starfield.uv{n}.raw` (UInt16 x2, Vertex domain), with a diagnostic: the NIF reader's rule (`Core/Modeling/Nif/NifModelGeometryData.cs:299-314`), in half bits.
- **Colors** (D4): `starfield.color`, UInt8 x4 normalized RGBA, `ColorSpace.Unknown`, provenance Unknown, evidence "the referencing NIF's material decides whether vertex color tints (Starfield material database); 79% of retail color streams are saturated masks". Not primary.
- **Weights** (D3): `starfield.bone.{k}` and `starfield.weight.{k}` for k below `weightsPerVertex`, in slot order, zero slots and repeated bones kept.
- No `Faces`, no `PointIndices` (a triangle list with no split points), `Purpose` Render.
- **Tail rule** (D7): normal W 1 without a tail throws "truncated at the meshlet tail"; W 0 with a tail is a diagnostic.

### 3.3 LODs (D1)

For each LOD list k (1-based): `SceneMesh("lod{k}", [primary.WithTriangleIndices(list_k)])` on node `lod{k}`. Layer sets, one exclusive group `starfield.mesh.lod`: `lod0` (members [0], default-on), `lod1..lodk` (default-off), source kind `bmt.starfield.mesh.lod`. `mesh info` then counts every LOD's shared vertices again (section 0.3); on 1,744 files this is the price of reusing the contract's LOD vocabulary, and it is listed as SA-M2.

### 3.4 Coverage elements

One element per non-empty section, kind `starfield.mesh.section`, identity as below. The census comes from the parse's section list; a count of zero is no element.

| Identity | Classification | Reason (NativeOnly) |
|---|---|---|
| `indices` | Typed | |
| `positions` (with the scale) | Typed | |
| `uv0`, `uv1` | Typed | |
| `colors` | Typed (attribute stream) | |
| `normals`, `tangents` | Typed | |
| `weights` (with `weightsPerVertex`) | Typed (attribute streams) | |
| `lod:{k}` | Typed (layer-set alternative) | |
| `meshlets` | NativeOnly | "GPU meshlet partition of the main triangle list; no carrier in either writer" |
| `cull` | NativeOnly | "per-meshlet center and extent culling bounds; no carrier in either writer" |

Nothing is Dropped. The design's gate rule (Dropped only with later-cut reasons) is therefore met by the reader; the writers' own Dropped rows are section 7's allowed reasons.

### 3.5 Native state and diagnostics

| Kind (version 1) | Target | Payload (raw bytes only with `NativeDetail.Full`) |
|---|---|---|
| `bmt.starfield.mesh.header` | Document | version, section offsets and counts, scale (value and bits), `weightsPerVertex`, tail present, normal W code, tangent W codes used |
| `bmt.starfield.mesh.quantization` | Primitive (0, 0) | scale, largest q, the position route of D5 |
| `bmt.starfield.mesh.meshlets` | Primitive (0, 0) | the records as four arrays, and the measured offset rules as evidence; raw section bytes |
| `bmt.starfield.mesh.cull` | Primitive (0, 0) | the six floats per record (round-trip strings), "center + extent" as evidence; raw section bytes |
| `bmt.starfield.mesh.dec4-sentinels` | Primitive (0, 0) | counts of `(511, 511, 511)` normals and tangents by W |

Payload bound: 1,749 meshlets is under 50 KB of JSON; the cull payload under 200 KB. Diagnostics (document): `bmt.starfield.mesh.material-in-nif` (always), `bmt.starfield.mesh.skin-palette-in-nif` (weights present), `bmt.starfield.mesh.color-interpretation` (colors present), `bmt.starfield.mesh.dec4-sentinel`, `bmt.starfield.mesh.uv-nonfinite`, `bmt.starfield.mesh.tail-without-normal-w`.

---

## 4 Probe, units and basis

### 4.1 Probe (`bmt.starfield.mesh`)

There is no magic, so recognition is a bounded structural walk over the prefix; each check applies only to bytes inside it, in this order:

1. Fewer than 8 bytes: NotAModel. Version above 2: NotAModel.
2. Index count not a multiple of 3, or `8 + 2 x count + 46` (42 for version 0: the smallest tail-less layout, ten or nine dwords and one vertex; review amendment, was 50) above `candidate.Length` when known: NotAModel.
3. The indices inside the prefix: their maximum is kept.
4. Scale not finite and positive; `weightsPerVertex` above 8; vertex count 0 or above 65,536; the kept maximum index not below the vertex count; `6 x vertices` beyond the declared length: NotAModel.
5. Each of UV0, UV1, colors, normals, tangents: count other than 0 or the vertex count is NotAModel. The first normal's W is read when inside the prefix.
6. Weight count other than vertices x `weightsPerVertex`: NotAModel.
7. Version above 0: `lodCount` above 8, a LOD count not a multiple of 3, or a LOD index not below the vertex count: NotAModel.
8. **End of a complete file here:** Supported, Confirmed ("no meshlet tail") when normal W is 0; **Unsupported, Tentative ("truncated")** when it is 1 (D7).
9. Cull count other than the meshlet count: NotAModel.
10. A complete file that ends exactly after the cull records: Supported, Confirmed; bytes after them: NotAModel.

When the prefix runs out: a complete file is NotAModel before the vertex count validated and Unsupported ("truncated") after; an incomplete prefix is Supported, Tentative, and the read decides. The evidence text is, for example, `Starfield .mesh v2, 4 vertices, 6 indices, meshlet tail` (the reader-free facts the walk reached).

The probe claims no NIF (ASCII header, version dword far above 2), no XnGine `.3D` (`v2.x` tag) and none of the 180,350 controls (section 0.2); no other BMT reader claims a `.mesh`.

### 4.2 Units and basis

Section 3.1. The Blender writer's rotation is identity for the Z-up NIF basis and the GLB writer's is the NIF one (design section 4); both scale by 1.0, Converted. The basis test is the NIF basis test (the reader returns the same object; a pin checks reference equality, with a control that a Y-up basis fails it).

---

## 5 Shell and workflow (slice 6)

- The workflow already opens `.ba2`; `mesh convert "Starfield - Meshes01.ba2" --all` probes 320,483 entries, and the 31,058 NIFs there stay Unsupported under the NIF reader ("other version": Starfield's BS 170 and above is outside its keys, `Core/Modeling/Nif/NifModelProbe.cs:269-295`) and NotAModel under this one.
- Memory estimate: the source length (16 MiB when unknown) plus the writer workspace; no companion allowance.
- `--game starfield`, and the D12 rule.
- `mesh formats` lists the reader with its variants and rules.

---

## 6 Ordered slices

The owner builds and runs every slice; agents never build, test or launch Blender (R19).

| # | Slice | Files | Verification (each control must fail) | Bucket-B |
|---|---|---|---|---|
| 0 | Parser: raw members, optional tail, exact-route helpers | `StarfieldMeshFile.cs`, its tests, `T/Helpers/StarfieldMeshTestBuilder.cs`, the disk-cache constant and its two ratchet tests | Existing `StarfieldMeshFileTests` pass unchanged. A tail-less blob parses (control: the old rule returns null). Raw members equal the builder's values (control: one weight byte changed in the builder output is seen). `Dec4Channel` equals the checked-in exact table on all 1,024 codes (control: the legacy float route differs on 556). Legacy `Positions`, `Normals`, `Tangents`, `Uvs` of a tailed blob bit-identical to today (pinned constants) | Every cover file parses; the 8 tail-less cover files are non-null (they are null today) |
| 1 | Cover manifest and resolver | `Samples/cut2-starfield-mesh-cover-manifest.json`, `RealAsset/Cut2MeshCoverManifest.cs`, `Cut2MeshFixtureResolver.cs` | Schema; one altered SHA-256 digit fails; a row naming the path in another archive with different bytes fails (no such pair exists in retail, so the control is synthetic) | Every row resolves: size and SHA-256 (as `P verify` did, 79 of 79) |
| 2 | Oracles | `gate2/starfield_mesh_probe.py` (staged), `gate2/run_gate2_mesh.py` A1 to A3 comparators | `P selfcheck` (48 checks). The comparators' own controls: one flipped index, one moved vertex, the swapped W reading, the legacy two-rounding position route | Receipts per cover file |
| 3 | Units, basis, format metadata, probe | `StarfieldMeshModelUnits.cs`, `StarfieldMeshModelFormatMetadata.cs`, `StarfieldMeshModelProbe.cs` | Literal pin of 1.0 and the evidence; `--game fnv` on a `.mesh` throws (control: `starfield` does not). Probe: every class of section 4.1 on builder blobs; the tail-boundary cut refused with W 1 (control: the same cut with W 0 is Confirmed); every truncation of a builder blob refused | The cover's probe verdicts equal `P`'s (manifest `probe` field); the 12 retail NotAModel controls are NotAModel, 6 of them past the version dword |
| 4 | Reader: geometry, streams, material, coverage, native state | `StarfieldMeshModelReader.cs`, `StarfieldMeshModelGeometry.cs`, `StarfieldMeshModelCoverage.cs`, `StarfieldMeshModelNativeState.cs` | Positions equal `PositionMeters` (control: the legacy product differs on a builder blob chosen to differ). W code 3 is glTF's -1 and code 0 its +1, each agreeing with glTF's -dP/dv frame (control: the second builder quad, V reversed with code 0, fails a constant w or the engine map). Weights as streams with repeated bones kept (control: merging duplicates fails). Colors not primary, `ColorSpace.Unknown` (control: a primary index fails the pin). Non-finite half kept in `.raw` (control). Index count not a multiple of 3, out-of-range index, W code 1, W 1 without a tail: each throws. Coverage equals an independent section list (control: one section dropped). `ValidateStructure` passes | A1, A2, A3 on the cover |
| 5 | LODs | `StarfieldMeshModelLayers.cs` | One exclusive group, LOD 0 default-on (control: two default-on sets fail `ValidateStructure`); LOD primitives share buffers (reference equality); LOD triangles equal the lists | A1 on the 26 manifest files with LOD lists (25 cover, 1 edge) |
| 6 | Registration and shell | `BethesdaModelRegistration.cs` (main session), `BethesdaModelWorkflow.cs`, `MeshCommand.cs` | The registry resolves a `.mesh` entry to exactly one reader (control: a NIF resolves to the NIF reader); the estimate text names the `.mesh` bound | `mesh info` and `mesh convert` on two cover entries |
| 7 | Gate-2 harness | `gate2/produce_gate2_mesh.ps1`, `gate2/run_gate2_mesh.py` | Driver self-checks on hand-built receipts, as in 1a | Hops A1 to A3, B, C, E and basis on the cover |
| 8 | Gate 2 (mesh), owner run, after gate 1c | none | Section 7 | Full cover plus decline controls |

---

## 7 Oracles and gate 2 (mesh)

| Hop | Oracle | Shared premise | Control that must fail |
|---|---|---|---|
| A1 fields | `P dump --arrays` against `mesh dump`: header fields, counts, positions (Python binary64 rounded once to float32, bit for bit), indices, UV halves as float32, Dec4 channels by the exact route, tangent w by code, the color, weight and raw-UV streams byte for byte, LOD lists, native meshlet and cull values | None | The legacy two-rounding positions, applied on every cover file where `P` finds the routes differ (5.99% of sampled components differ; the receipt counts the files), and one index byte flipped in a copy |
| A2 coverage | The section list from `P`'s walk (non-empty sections, LOD lists one by one) against the reader's census | None | One section removed from the reader's census |
| A3 geometry | Per vertex: position, normal, tangent (w included), UV0, UV1, and the triangle list of every LOD mesh, against `P`, exact | None | One vertex moved; the W map swapped (disagrees with the UV frame on 94% of vertices) |
| B document to GLB | `gate1a/hop_b.py`: validator 0 errors, accessors after the declared conversions (normals and tangents normalized, TEXCOORD_1, COLOR_0 white, no JOINTS/WEIGHTS), one node drawn by default, the layer rows | As in 1a | As in 1a |
| B' Blender imports the GLB | As in 1a (owner-run) | As in 1a | As in 1a |
| C document to package | `gate1a/hop_c.py`: every stream (INT-widened weights, BYTE_COLOR colors, raw UV streams, UV maps, custom normals, `mt_source_tangent`) against `mesh dump` | As in 1a | One stream value flipped in the package |
| D package to .blend (owner-run) | Readback: attributes, UV maps, custom normals, LOD collections hidden except LOD 0 | None | One attribute value changed in the package |
| E writer against writer | `gate1a/hop_e.py`: positions, indices, UVs after each writer's conversions | Consistency only | One UV swapped in the package only |
| Basis | The NIF basis object (section 4.2) | None | A Y-up basis |

Hop F does not apply: a lone `.mesh` has no source material, blend or texture.

**Allowed Dropped reasons for a `.mesh` item** (the gate scope, as cut 1c's SA3 ruling scopes them): `geometry.source-attribute-storage-omitted` for the color, weight and raw-UV streams (A1 pins each stream against `P`), and `source.opaque-payloads-omitted` for the native state (A1 pins the meshlet and cull values). Any other Dropped row fails the gate, and an injected Faces-Dropped row on a `.mesh` item must fail it (control).

**Gate 2 (mesh):** every hop passes on the cover and every control fails; Dropped only with the reasons above; the legacy renderer's parse differs only on the tail-less cover files (null before, geometry after); peak memory recorded per item.

---

## 8 Cover (measured; `cover-manifest.json`)

**Rule.** Group the 720,957 records by (cell, file-level tag set), where the cell is (version, `weightsPerVertex`, `lodCount`, colors present, UV1 present, meshlet-count bucket 1 / 2-4 / 5-16 / 17-64 / 65-256 / 257+ / absent) and the tags are the 19 file-level values `P secondary_tags` observed (normal W 0 and 1; tangent W sets {0}, {3}, {0, 3}; sentinel normals; sentinel tangents; non-finite UV0; non-finite UV1; weight sums not all 65,535; repeated bones; cull overshoot; unused vertices; alpha below 255; unsaturated colors; largest |q| = 32,767; scale not a power of two; Tentative probe before, and after, the vertex count). Each group's representative is its smallest file, then SHA-256 order. An exact MILP (HiGHS through `scipy.optimize.milp`) chooses the fewest groups covering every value and every observed pair of values of the cell dimensions plus every tag, then the fewest bytes among those: **1,842 groups, 266 items, 60 files, both stages optimal, nothing uncovered.**

**Cover:** 60 files, 7,858,502 bytes: 35 from `Meshes01`, 8 from `SFBGS050`, 6 from `ShatteredSpace - Main01`, 5 from `FaceMeshes`, 5 from `Meshes02`, 1 from `SFBGS004` (36 of them also live elsewhere, listed under `alsoIn`). `weightsPerVertex` 0 to 8 all present, LOD counts 0 to 3 (25 files with LOD lists), 8 tail-less files, 24 files whose probe is Tentative (8 before the vertex count). For example the smallest is `Starfield - Meshes01.ba2 :: geometries/89b837abb1a99f8a2ea9/92d5215f898a30a1198b.mesh` (180 bytes, 0a7baa58...), a 50 m quad at scale 25.

**Edge files (7):** smallest (156 bytes, `ShatteredSpace - Main01 :: geometries/3878e5280427dcb19e3f/76445de4d7d501f809cc.mesh`), largest (3,690,290 bytes, `geometries/36e8ec79ff07f8ac2100/4843808dac53b440baf6.mesh`), most vertices (65,080, `Meshes02 :: geometries/f281c48bb045337de5d7/530f1aad0ab0a16a30c0.mesh`), most indices (195,120, `Meshes01 :: geometries/265c27b4be015a859efe/513885baa923ff30d4b6.mesh`), most meshlets (1,749, `Meshes01 :: geometries/af47b4200eb165833822/92b5c171ab12fd69140d.mesh`), largest bone index (142, `ShatteredSpace - Main01 :: geometries/ed1262cb8808085bd92b/307e0e029be389186bbf.mesh`), loosest cull (the 100 km file). The most-LODs edge is already a cover file.

**Decline controls (16):**
- Retail NotAModel by extension: `.af`, `.btd`, `.cdb`, `.dat`, `.nif`, `.rig` (6).
- Retail NotAModel past the version dword, one per deeper check: index count not a multiple of 3 (`Constellation - Localization :: strings/constellation_en.dlstrings`), index count against the length, scale, too short, vertex count, `weightsPerVertex` (6).
- Synthetic: truncated (every cut of the three smallest cover files: never Supported; the tail-boundary cut is refused only by the normal-W rule, which is its control), version 3, one trailing byte, one index equal to the vertex count (4).

**Alternatives for D9** (same cells, measured): single-value joint cover 9 files; three-way 156; one file per observed cell 295 (the census has all 295 cells).

**Verification:** `P verify` re-read all 79 retail rows (67 files and 12 controls): 0 size or SHA-256 mismatches; the altered-digit control detected.

---

## 9 Shared asks

None under the recommendations. Conditional or optional:

| Ask | Exact contract | When |
|---|---|---|
| SA-M1 | Writer rules for a primitive whose influences no node binds: GLB omits JOINTS/WEIGHTS with a stated row instead of writing them or refusing duplicate positive joints; Blender's admission and importer agree (vertex groups named by joint index without an armature, or a stated omission) | Only if the owner picks D3's alternative |
| SA-M2 | `ModelInfoDocument` counts a vertex buffer shared by layer-set alternatives once (for example a document-scoped `SourceDomainId`) | Optional; today 1,744 files print vertices x (1 + LOD count) |

---

## 10 Open questions

The decisions D1 to D12 above, plus:
- **Legacy numerics.** Should the renderer later switch to the correctly rounded routes (1-ulp changes on 5.99% of position components and on 556 Dec4 codes, one more disk-cache bump)? Recommendation: not in cut 2; decide when the Starfield NIF reader lands.
- **Cull overshoot.** 892 files carry all-zero or too-small cull records. They stay native state exactly; nothing draws from them. Recommendation: record, do not investigate further unless a renderer uses them.
- **Normal W semantics.** The bit tracks the tail on every retail file, but what the engine uses it for is unknown. Recommendation: keep it a probe and reader rule backed by the census, not a typed field.
- **Tangent handedness against the normal-map convention.** 94.25% of UV-framed vertices agree with w = +1 for code 3 under raw (DirectX) UVs against +dP/dv, which is glTF's w = -1 for code 3 against -dP/dv. Review amendment (2026-09-28): `SceneTangents` w is glTF's by contract, and the design flips a Down (-Y) normal map's green to glTF's +Y, so the engine sign would invert relief twice once the Starfield NIF reader binds a normal map; the reader therefore types the stored sign negated. Owner ruling pending with RE-13.
- **Units provenance.** 1.0 stays Assumed (design row); RE-1 has not read Starfield's executable.

## 11 Measured versus assumed

- **Measured:** population, sizes, duplicates; version 2 only; exact tiling of all 720,957 files; the optional tail and its two-way equality with normal W; tangent W codes; scale values; weights layout, sums and repeated bones; LOD lists; meshlet offset rules and their controls; cull center + extent and its control; non-finite UVs; color saturation; Dec4 sentinel codes; tangent direction and W against the UV frame; position and Dec4 routes; the probe on every `.mesh` and 180,350 controls; the cover and its verification.
- **Assumed:** 1.0 m per unit; the NIF basis; the 16 MiB source bound; probe bounds 8 and 8; the versions 0 and 1 layouts (from the reference, no retail sample); the placeholder material.
- **Inferred:** the meshlet-vertex list is built by the engine; a shader decoding the sentinel sees the same tiny vector; the repeated-bone pattern is an exporter rounding remainder (seen on the two files inspected).
- **Recalled:** the version 0 and 1 layouts through NifSkope's `MeshFile.cpp`, as `StarfieldMeshFile` cites it.

### Critical files

- `src/BethesdaMultitool/Core/Formats/Nif/Rendering/Geometry/StarfieldMeshFile.cs` (with `NifSubmeshExtractor.cs:1243-1302` and `ReferenceDecodedMeshDiskCache12.cs:130`)
- `shared/Multitool.Shared/src/Slfx77.Multitool.Core/Models/ScenePrimitive.cs`, `SceneMesh.cs`, `SceneLayerSet.cs`, `SceneSkinInfluences.cs`, `SceneAttributeStream.cs`
- `shared/Multitool.Shared/src/Slfx77.Multitool.Media/Models/ModelGltfGeometry.cs`, `ModelGltfSignedLanes.cs`, `ModelGltfMaterials.cs`, `ModelGltfLayers.cs`; `shared/Multitool.Shared/src/Slfx77.Multitool.Media.Blender/BlendAdmission.cs`, `Python/import_model.py`
- `src/BethesdaMultitool/Core/Modeling/Nif/NifModelUnits.cs`, `NifModelLayerReader.cs`, `NifModelGeometryData.cs` (the patterns this reader mirrors)
- `tools/scripts/gate2/starfield_mesh_probe.py`, the seed of A1 to A3
