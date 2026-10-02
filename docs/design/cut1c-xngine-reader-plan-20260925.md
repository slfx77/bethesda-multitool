<!-- Implementation plan for design row 14 (cut 1c). Drafted 2026-09-25 by a read-only planning pass over the worktree at BMT f3ecc8f2 with Shared pinned at 591d083. Corrected the same day after an adversarial review: 12 of its 13 findings were confirmed and applied, and the 14th verified the cover with no defect. The review receipts are in R/review/ and the new correction receipts in R/plan_fix_triangulation.*. The design (docs/design/model-document-design-20260923.md) stays authoritative; where this plan disagrees with it, section 0.4 says why. Nothing was built, tested or launched. -->

# Cut-1c XnGine `.3D` and Redguard `.3DC` model readers: implementation plan

Amended 2026-09-28 from the premise re-check at 0152179. Amended again later that day after the foundation delivered SA1 (`04ba005`), SA4 (`585dd1f`) and SA6 (`c674b2a`, verified `cbdbf3d`) and ruled on SA3; the pin is canonical Shared main `1fac23d` and the contract text is Shared `docs/xngine-shared-contracts.md`. Slices 10 to 12 collapse onto the shipped contracts (section 8).

I read the design, the cut-1a plan, the three survey results and the code they cite. I changed nothing under src, tests, tools or docs. New read-only measurements (Python, one process at a time, Sample data only) are in the receipts below.

- **Worktree:** `C:/dev/Multitool-worktrees/bmt-material-preparation-20260912`, HEAD f3ecc8f2. The Shared submodule is at 591d0834 (clean).
- **Canonical Shared at the 2026-09-28 re-check:** 0152179. It adds `SceneAnimationTiming` (`SceneAnimationTiming.cs:15-16`; `SceneAnimation.cs:30`), still has no palette type, keeps the G2 rows (`ModelGltfGeometry.cs:623-663`) and the opaque-payload row (`ModelGltfAdmission.cs:226-227`), keeps the duration limitation for non-clocked keyed clips (`ModelGltfAdmission.cs:217-222`), keeps (Int16, 2) Degraded (`BlendAdmission.cs:689`), and **carries a Step morph-weight clip natively in Blender** (`BlendAdmission.cs:1108-1117`; `BlendAnimationAdmission.cs:31-75`) — SA5 is satisfied.
- **Path prefixes:** `BMT/` = `src/BethesdaMultitool/`, `SH/` = `shared/Multitool.Shared/src/`, `T/` = `tests/BethesdaMultitool.Tests/`, `R/` = `TestOutput/cut1c-prep-20260925/`.
- **Labels:**
  - **(measured):** backed by a receipt in R/.
  - **(assumed):** a chosen value awaiting RE or an owner ruling.
  - **(inferred):** reasoning not checked against data.
  - **(recalled):** memory of external software, unverified.

## Receipts (R/)

| File | Holds |
|---|---|
| `census_xngine.py`, which writes `census-xngine.json`, `census-xngine-records.json` and `census-xngine-summary.json` | Every XnGine record in the Daggerfall, Battlespire and Redguard builds and Steam installs; the cover proposal with SHA-256 per payload and container |
| `survey1-*.json`, `survey2_*.json` (scripts saved as `.py.txt`) | Sizes against the 64 KiB probe prefix, face census, packed-UV unfold census, Newell sign, header dwords, .3DC header offsets |
| `survey3-shared-contracts.json` | What exists and what is missing in Shared at the pin |
| `plan_measure_xngine.py`, which writes `plan_measure_xngine.json` | Authored normals, UVs at corners 3 and up, collinear corners, coordinate range, .3DC per-frame normal blocks, the MENU.ROB segments whose header +16 is non-zero. Its fan counts start from corner 0 before the collinear drop, which is not the reference triangulation (superseded by `plan_fix_triangulation.json`). |
| `plan_measure_uv_readings.py`, which writes `.json` | Which reading of the first three UVs agrees with the stored later corners. Daggerfall ids below 905 are the control population. |
| `plan_measure_3dc_uv_explore.py`, `plan_measure_3dc_normals_explore.py`, and their `.json` | 16 UV readings on the .3DC keyframes; candidate decodings of the per-frame normal blocks |
| `plan_fix_triangulation.py.txt`, which writes `plan_fix_triangulation.json` (new, correction pass) | The reference triangulation (kept corners in the order c1..c(n-1), c0, a fan from the first kept corner) per population: n-gons with fewer than 3 kept corners, zero-area and folded triangles, planes that yield no triangle under D5, texture keys left with no plane, cover point count times texture keys, and .3DC n-gons whose kept corners depend on the pose |
| `review/review_verify_cover.py(.json)`, `review/review_census2.py(.json)`, `review/review_empty_primitive.py(.json)`, `review/review_df_vs_rg.py(.json)` (reviewer's, independent code) | Cover SHAs and containers, census counts, header +16/+20 discriminators, zero-normal planes and empty primitives |

## Decisions this plan asks the owner for (D10 before slice 0, the rest before slice 5)

| # | Decision | Recommendation |
|---|---|---|
| D1 | What `xngine.uv16` holds | The stored int16 values, before the packed-UV unfold and before accumulation. The portable UV follows the reference rule (section 3.2). |
| D2 | Packed-UV unfold | Apply it only where the reference does (Daggerfall ids below 905), marked Assumed, with a diagnostic. Never apply it to Redguard or Battlespire. The stored later corners contradict it even on Daggerfall (section 0.2), so the owner may drop it altogether. |
| D3 | .3DC texturing | Untextured in 1c, as the legacy export is (`BMT/CLI/Commands/Classic/ClassicCommand.cs:1108-1121`). No UV reading fits (section 0.2). Keep the texture keys and `xngine.uv16`. |
| D4 | .3DC normals | **Flat for both widths.** Flat is Exact in both writers (`ModelGltfGeometry.cs:236-238`; `BlendAdmission.cs:556`). The wide per-pose normal blocks go to native state. The cost is that Flat shading derives directions from each pose's faces, which differ from the authored wide normals on 32,277 of 1,030,183 plane-frames at cos < 0.9999, 15 of them pointing the opposite way. **Alternative (the draft's):** wide files as Authored with per-pose normal deltas. Then the GLB writer normalizes the mostly non-unit base (only 128,961 plane-frames are unit within ±0.002) and emits a Degraded row for every target that carries NormalDeltas (`ModelGltfGeometry.cs:434-437`). Blender marks every such target Degraded too (`BlendAdmission.cs:584-587`), for example 69 Degraded target rows per writer on BMANA001. Under R5 (Exact preferred), the owner weighs these two costs. Confirmed at 0152179: `SceneValidation.cs:377` makes "Flat primitives cannot author normal morph deltas" an explicit validation failure, so the recommendation (Flat, no deltas) is what validation demands, and the alternative (Vertex/Authored with per-pose NormalDeltas) remains legal since it is not Flat. |
| D5 | Triangulation | **Replicate the reference exactly.** A 3-corner plane is its own triangle. An n-gon keeps the corners the 0.001 rad test keeps, in the reference order c1, c2, ..., c(n-1), c0, and is fanned from the first kept corner (`XnGineMeshDecomposer.cs:129-185, 322-327`). Omit a triangle that covers no area when the plane's authored normal is zero. **A plane left with no triangle is omitted from the primitive geometry** and carried in native state; this covers planes with fewer than 3 kept corners and zero-normal planes with no area. **A texture key left with no plane gets no primitive.** On .3DC files a corner is kept when any pose keeps it (section 4). |
| D6 | Duplicate ARCH3D ids | Name the k-th later duplicate `<id>~<k>` in the XnGine BSA listing. This is visible in `archive list/extract`. |
| D7 | Game identification | The chain in section 6.2 with its evidence recorded. The install walk-up runs in the shell, container facts come from the item's BMT source, and the content step uses header +20. |
| D8 | Redguard 3dfx meshes (fxart v4.0/v5.0) | The probe declines them as Unsupported with a later-cut reason. |
| D9 | Image work during inspection | **Contract-conforming default.** Under `ModelReadPurpose.Inspection` the reader fills each `SceneImageSource` from record headers only: descriptor, Original bytes and SHA, container, location. It builds no StandardPayload and decodes no RLE, frame or index data. The Shared contract forbids pixel decoding during inspection (`IModelSourceReader.cs:27-29`), and the owner's 2026-09-24 ruling covered only the DDX block relayout (`BMT/Core/Modeling/Nif/INifTextureCodec.cs:6-9`). `mesh info` needs only the descriptor (`ModelTextureInspector.cs:26-31`). This needs header-only paths in `DaggerfallTextureFile` (its `Parse` decodes every record eagerly, `DaggerfallTextureFile.cs:86-150`) and `BsiFile` (`Parse`, `BsiFile.cs:125`). **Alternative:** the owner extends the ruling to lossless index decoding, and inspection builds the indexed PNG exactly as conversion does. `SupportsInspectionWithoutPixelDecoding` is true under either ruling, and only once the chosen path is implemented. |
| D10 | Order against gate 1b | The design orders row 12, then row 13 (gate 1b), then row 14 (gate 1c) (`model-document-design-20260923.md:766`). **Recommendation:** run slices 0 to 9 beside row 13, because they touch no Shared file. Slice 0's pin bump brings `SceneAnimationTiming`, a row-13 type (`:751`), in early. Hold slice 13 (gate 1c) until gate 1b passes, as the design orders, unless the owner rules otherwise. The .3DC clip's Blender row is `AnimationNativeCurves` (carried): SA5 was satisfied by the cut-1b animation path (verified at 0152179). |

---

## 0 Baseline facts

### 0.1 BMT plumbing today (all shaped for NIF)

**Registration and workflow**
- **Registration.** The composition root registers only `NifModelReader` (`BMT/Core/Modeling/BethesdaModelRegistration.cs:32-35`), and its cache factory returns `NifModelReadCache` (`:38-41`). Nothing exists under `Core/Modeling/Xngine`, `Core/Modeling/Redguard` or `Core/Modeling/Units`.
- **Archive recognition.** `BethesdaModelWorkflow.IsArchive` accepts only `.bsa` and `.ba2` (`BMT/Core/Modeling/BethesdaModelWorkflow.cs:559-564`). ARCH3D.BSA and 3D.BSA pass by extension; `.BS6` and `.ROB` fall into the single-file path. The archive layer already opens all four: ROB at `BMT/Core/Formats/Archives/ArchiveProbe.cs:45-48` and the XnGine BSA at `:106-112`.
- **Memory estimate.** `EstimateMemory` assumes the NIF bound and a 256 MiB companion allowance (`BethesdaModelWorkflow.cs:87-94`).
- **App options.** They are built once per run by `CreateAppOptions(game, gameEvidence, platform)` (`:104-119`), which already carries a game-evidence key (`BethesdaModelRegistration.cs:20`). The NIF reader reads its game only from these options, and throws on a value that names no NIF-era game (`BMT/Core/Modeling/Nif/NifModelUnits.cs:44-58`).
- **Companion inference.** It walks up to a `textures` folder (`BMT/Core/Modeling/BethesdaTextureCompanions.cs:136-143`), which no classic install has. An archive input uses the archive's folder as its data root (`BethesdaModelWorkflow.cs:544-549`).

**Archive layer**
- **Numbered entries.** A numbered XnGine BSA names each entry by its id (`BMT/Core/Formats/Xngine/Bsa/XnGineBsaParser.cs:161-163`).
- **Battlespire compression.** LZSS is undone during extraction (`BMT/Core/Formats/Archives/XnGineBsaBackend.cs:74-76`). The listing reports the stored (compressed) size (`:34-52`).
- **ROB segments.** They list as `NAME.3D`, and their extracted bytes exclude the 80-byte segment header (`RedguardRobBackend.cs`, `ListFiles` and `Extract`).
- **Path lookup.** It is last-wins (`BMT/Core/Formats/Bsa/Index/ArchiveReader.cs:249-257`).
- **What a reader sees of an archive entry.**
  - `AssetEntry.Provenance` is only the archive path: the file system's Label (`BMT/Core/Vfs/ArchiveFileSystem.cs:33, :56, :273-279`; `BMT/Core/AssetBrowse/BethesdaBrowseSource.cs:62`). It carries no record kind (numbered or named) and no LZSS flag.
  - `AssetEntry.Length` is the stored size, although the contract documents it as "the known decoded length" (`SH/Slfx77.Multitool.Core/Assets/AssetEntry.cs:5`). For example, ARMOR.3D in 3D.BSA is stored at 13,999 B and decodes to 34,556 B (review_verify_cover.json).
  - The reader's `OpenReadAsync` returns decompressed bytes.
  - The reader does receive the `IAssetSource` (`ModelSourceItem.cs:27`), which for archives is BMT's own `BethesdaBrowseSource`.

**Legacy route (unchanged until sampled parity)**
- `classic mesh info/export` (`ClassicCommand.cs:1055-1121`).
- The layout comes from an install walk-up (`:1069-1074`).
- Export goes through `XnGineMeshDecomposer` and `XnGineMeshGlbExporter`.

### 0.2 Corpus facts (measured unless labeled)

**Populations** (census-xngine-summary.json; re-derived independently in review_census2.json)
- **Daggerfall ARCH3D.BSA:** 10,251 records (10,109 v2.7, 134 v2.6, 8 v2.5). All walk with the 8-byte plane header; 20 also walk with the 10-byte header.
- **Battlespire:**
  - 245 loose files, all v2.7, all walking with the 10-byte header.
  - 3D.BSA: 2,400 entries, of which 2,395 are meshes plus 5 non-mesh strays (tags "", 0x01 twice, "4352", "MZ", the last being ARCH3.EXE with +16 = 1008); 2,398 entries are LZSS.
  - 3D.BS6: 2,115 entries, all LZSS, all meshes.
  - None of the 4,755 Battlespire meshes walks with the 8-byte header.
- **Redguard 3dart:**
  - 52 loose .3D files, all v2.7.
  - 41 ROBs with 5,870 segments: 4,667 meshes (217 v2.6, 4,450 v2.7) and 1,203 empty segments.
  - 147 .3DC files (120 v2.6, 27 v2.7), all tiling: 110 narrow (3-dword records) and 37 wide (4-dword records); 9,190 frames; at most 228 poses.
  - None of the 4,719 static meshes walks with the 10-byte header.
  - All 147 .3DC files also pass the .3D walk, and none of the 362 loose static files parses as a .3DC.
- **Redguard fxart (Disc 1 only), a population the design does not list:**
  - 204 .3DC files tagged v4.0.
  - 4,761 ROB segments (285 v4.0, 3,560 v5.0, 916 empty).
  - 65 loose files (26 v4.0, 39 v5.0).
  - Every one of them fails the 3dart walk, and every one walks with a 10-byte plane header and point indices.
  - None is byte-identical to its 3dart namesake (199 shared names).
- **MENU.ROB:** four segments (MENUA001, MB_PG01, MB_PG02, MB_PG03; type 256) have header +16 = 9 and +20 = 22,608 or 13,576. They walk as static .3D but fail the .3DC frame-table shape ("record of 42 dwords" and "not whole records"; plan_measure_xngine.json).
- **Header +20:**
  - Non-zero on static meshes: 4,352 of 4,667 ROB meshes, 52 of 52 loose Redguard files, 223 of 245 loose Battlespire files, 1,837 of 2,396 3D.BSA entries, 1,586 of 2,115 3D.BS6 entries, and **0 of 10,251 ARCH3D records**. That makes it a one-way Daggerfall discriminator: non-zero means "not ARCH3D" (review_census2.json, review_df_vs_rg.json).
  - Equal to 64 on no static mesh; equal to 64 on 147 of 147 .3DC files.
  - Header +44 is non-zero only on the 147 .3DC files.

**Sizes against the probe prefix**
- 121 of the 147 .3DC files exceed 64 KiB; the largest is 7,081,150 bytes.
- 4 ARCH3D entries exceed it (up to 81,394 bytes, cover file 451 among them), as does 1 3D.BSA entry (decoded size).
- Every loose .3D file fits in the prefix.

**Duplicate ARCH3D ids.** 10 ids carry 24 records; id 5090 alone has 6.
- Path lookup keeps the last record of each id, so 14 records cannot be reached by path.
- 13 of those 14 differ in bytes from the record that wins. Only 99800's pair is identical.

**Coordinates.** The largest |coordinate| in each population:

| Population | Largest |coordinate| |
|---|---|
| ARCH3D | 587,264 |
| 3D.BSA | 5,943,296 |
| 3D.BS6 | 2,359,296 |
| Battlespire loose | 671,744 |
| ROB | 1,179,648 |
| Redguard loose | 312,064 |
| Every .3DC pose | 524,288 |

All are below 2^24, so int-to-float32 conversion and int16 or int32 differences are exact.

**Authored plane normals**

| Population | Measurement |
|---|---|
| ARCH3D | Unit length within ±0.002 (in 1/256 units) on 201,075 of 204,479; cos ≥ 0.9999 with a Newell normal on 204,416 |
| 3D.BSA | 129 zero-length normals; 6 point opposite to Newell |
| 3D.BS6 | 240 zero-length normals; 6 point opposite to Newell |
| Battlespire loose | 1 points opposite to Newell |

Newell reproduces the authored sign on every Daggerfall and loose Redguard plane and on 245,507 of the 245,535 ROB planes (survey2_newell_sign.json).

**Faces under the reference triangulation** (plan_fix_triangulation.json; collinear counts from plan_measure_xngine.json)
- No plane has fewer than 3 points. ARCH3D polygons have 3 to 24 corners.
- The draft's figures (48,174 zero-area triangles in ARCH3D, and so on) fanned from corner 0 over all corners before the collinear drop. They do not describe the reference triangulation and are withdrawn.

| Population | n-gons with collinear corners (0.001 rad test) | Collinear corners | n-gons with fewer than 3 kept corners | Zero-area triangles | Folded triangles (against Newell) |
|---|---|---|---|---|---|
| ARCH3D | 33,283 of 181,896 | 137,911 | 0 | 0 | 1 |
| 3D.BSA | 2,850 | 5,866 | 24 | 168 (113 are zero-normal three-corner planes) | 399 |
| 3D.BS6 | 2,309 | 4,892 | 26 | 268 (223 are zero-normal three-corner planes) | 362 |
| Battlespire loose | 202 | 329 | 0 | 1 | 13 |
| Redguard loose | 8 | 10 | 0 | 0 | 0 |
| ROB | 5,570 of 93,094 | 10,739 | 1 | 13 | 123 |
| .3DC keyframes | 54 | 71 | 0 | not tallied | not tallied |

A folded triangle points against the plane normal, so the fan does not cover those polygons exactly.

**Planes that yield no triangle under D5, and empty primitives** (plan_fix_triangulation.json; the zero-normal part matches review_empty_primitive.json)

| Population | No-triangle planes | Of which zero-normal | Of which non-zero normal | Most in one mesh | Texture keys left with no plane |
|---|---|---|---|---|---|
| 3D.BSA | 137 | 129 | 8 | 36 | 4 (7PYLON1, 7PYLON2, EBATAXE, EJOUST) |
| 3D.BS6 | 249 | 240 | 9 | 66 (ESPEAR) | 4 (7PYLON1, 7PYLON2, ESPEAR, EJOUST) |
| ROB | 1 | 0 | 1 | 1 | 1 (ISLAND.ROB HBBLD01, key 0xAC) |
| ARCH3D, loose Battlespire, loose Redguard | 0 | 0 | 0 | 0 | 0 |

- Every zero-normal plane covers no area: 369 of 369, none with a non-zero-area fan triangle.
- The non-zero-normal no-triangle planes are n-gons whose corner test keeps fewer than 3 corners. The legacy decomposer drops those too (`XnGineMeshDecomposer.cs:97-100`).
- No mesh loses every texture key.

**Point domain against texture keys** (plan_fix_triangulation.json; the `mesh info` sum at `ModelInfoDocument.cs:30`)

| Cover file | Points | Keys | Points × keys |
|---|---|---|---|
| 44005 | 48 | 5 | 240 |
| 451 | 934 | 11 | 10,274 |
| 41509 | 178 | 14 | 2,492 |
| BARSTEP1 | 20 | 3 | 60 |
| CRAK0001 | 34 | 2 | 68 |
| GR_COMP | 17 | 2 | 34 |
| ARMOR | 339 | 1 | 339 |
| HUTVANE | 26 | 1 | 26 |

**.3DC corners depend on the pose** (plan_fix_triangulation.json)
- 45 of 3,898 n-gons in 32 of 147 files keep a different corner list in some pose than in the keyframe.
- 0 n-gons are degenerate (fewer than 3 kept corners) in the keyframe and not in some pose.

**UVs at corners 3 and up** (non-zero stored values only; "agrees" means within 2 units, 1/8 texel, of the reference's plane fit through the first three corners; plan_measure_uv_readings.json)

| Population | Raw reading agrees | Unfolded reading agrees |
|---|---|---|
| Daggerfall ids ≥ 905 | 92,554 of 116,785 | same as raw (no values in the fold range) |
| Daggerfall ids < 905 (the control population) | 40,416 of 45,868 | same as raw (no values in the fold range) |
| Daggerfall ids < 905, polygons with values in the fold range | 331 of 411 | 6 of 411 |
| Daggerfall ids ≥ 905, polygons with values in the fold range | 127 of 179 | 57 of 179 |
| ROB | 60,612 of 100,501 (fold-range polygons: 789 of 2,865) | fold-range polygons: 72 of 2,865 |
| Redguard loose | 199 of 266 (fold-range polygons: 3 of 6) | fold-range polygons: 0 of 6 |

- Where the unfold makes no difference, the raw and unfolded readings agree equally. "Absolute first three" reaches at most 513 on any population, so the test separates the readings.
- On polygons with values in the fold range, the stored later corners agree with the raw reading and contradict the unfold. That holds even on Daggerfall ids below 905, where the reference applies it.
- **Daggerfall:** stored later corners are either zero (158,664 of 321,907) or absolute values on the fit.
- **Battlespire:** later corners continue the delta chain instead (within 2 of the fit on 32,134 of 74,398 in 3D.BSA, 29,461 of 65,356 in 3D.BS6 and 1,014 of 5,258 loose; plan_measure_xngine.json).
- **.3DC:** 0 of 3,810 non-zero later corners fit under any of 16 readings (plan_measure_3dc_uv_explore.json), and 113,062 of 324,616 keyframe UV values (34.8%) lie in the fold range. The .3DC UV encoding is undecoded.

**Current code's unfold on Redguard** (survey1, survey2)
- `XnGineMesh.cs:374-379` gates the unfold on layout == Daggerfall and object id < 905.
- Redguard parses with the Daggerfall layout and an id of 0 or the segment index (`RedguardRobMeshArchive.cs:109`, `Redguard3dcFile.cs:263`).
- As a result, the unfold rewrites 4,703 ROB corners in 273 meshes, 22 values in NCROCK.3D, and 89,509 of the 158,016 first-three corner values in the .3DC files.

**.3DC per-frame normal blocks** (plan_measure_xngine.json, plan_measure_3dc_normals_explore.json)
- **Wide (12 bytes per plane, int32 triples):**
  - The direction matches a Newell normal of the same pose at cos ≥ 0.9999 on 997,906 of 1,030,183 plane-frames. The other 32,277 fall below that, and 15 point the opposite way.
  - Lengths are at most 256 and usually shorter; only 128,961 lie within ±0.002 of 256.
  - These are authored, non-unit, per-pose plane normals.
- **Narrow (4 bytes per plane):** no decoding matches. I tried int8 triples at offset 0 and offset 1, 10:10:10, 11:11:10, 10:11:11, and a plane distance; the int8 reading reaches cos ≥ 0.9999 on only 201 of 2,840,925.
- **Header offsets.** Header +24/+48/+52 point at frame 1 only on the 37 wide files. On the 110 narrow files they point at no frame, and on 22 of them +24 lies past the end of the file (survey2_3dc_*.json).

**Companion sizes**
- Daggerfall TEXTURE.*: 472 files, the largest 983,853 bytes.
- Redguard 3dart TEXTURE.*: 418 files, the largest 1,097,216 bytes.
- fxart TEXBSI.*: 415 files, the largest 1,054,669 bytes.
- Battlespire BSI.BSA: one 73,113,573-byte archive whose entries are read one at a time. Its 2,599 entries yield 2,621 images, so some entries hold more than one image (`BsiFile.cs:80-90`).
- Redguard .COL: 18 files of 776 bytes.

### 0.3 Contract facts at 591d083 that bind 1c

**Faces, points and attributes**
- `SceneFaceList` requires at least 3 corners per face, and its corners index the primitive's own vertices (`SH/Slfx77.Multitool.Core/Models/SceneFaceList.cs:19-20, 69-70`).
- Vertices that share a source point must share a position (`SceneSourceGeometryValidation.cs:57-66`).
- Attribute counts must match the declared domain (`:68-82`).
- `NormalProvenance` Flat must agree with `NormalMode.Flat` (`:42-44`).
- `ScenePointIndices.PointCount` is "the explicit source point-domain size, including points unused by this primitive" (`ScenePointIndices.cs:36-37`). `mesh info` nevertheless adds it once per primitive (`Inspection/ModelInfoDocument.cs:30`).

**Validation**
- `ValidateStructure` runs on every read (`Sources/ModelReadLifetime.cs:70`).
- It validates every primitive (`SceneValidation.cs:41-67`), and a primitive with no vertices or no index triples fails ("Triangle primitives require vertices and complete nonempty index triples", `:280-281`).
- A mesh with no primitives fails (`:60`).

**Normals and morphs**
- `DerivedSmooth` is defined as "smooth directions using an identified rule" (`SceneNormalProvenanceKind.cs:8-9`).
- `SceneMorphTarget.AbsolutePositions` exists (`SceneMorphTarget.cs:13, 33-36`).
- `SceneMorphTrack` supports `Step` (`SceneMorphTrack.cs:7-8`, `SceneInterpolation.cs:10`).
- There is no timing type at the pin (it exists on canonical main; the slice-0 pin bump brings it — verified at 0152179).

**GLB writer**
- **Textures.** It copies a StandardPayload PNG byte for byte and classes it Converted ("reader-lossless-standard", `SH/Slfx77.Multitool.Media/Models/ModelGltfImages.cs:141-147, 199-208`). PNG color type 3 maps to `Indexed` (`:300`). So palettized textures reach GLB without any Shared change.
- **Coordinates.** It maps an up axis of -Y with a half-turn and reflects a LeftHanded declaration along forward (`ModelGltfCoordinates.cs:122-171`).
- **Normals.**
  - It sets `invalid` for any zero-length vertex normal outside Flat mode, whether or not a triangle references it (`ModelGltfGeometry.cs:176-183`).
  - It then reconstructs referenced zero normals and fills unreferenced ones, emitting Degraded rows "geometry.zero-normals-reconstructed" or "geometry.unreferenced-normals-filled", plus "geometry.unreferenced-normal-placeholders" (`:193-228`).
  - A referenced zero normal with no incident area makes the item unsupported (`:199-204`).
  - Flat mode is an Exact "geometry.normals-preserved" row (`:236-238`).
  - A normalized base makes every target with NormalDeltas Degraded (`:434-437`).
- **Dropped rows.** It reports `Faces`, `PointIndices` and unselected attributes as Dropped, with reasons that are not later-cut reasons (G2, `:506-531`). It also emits a document-level Dropped row "source.opaque-payloads-omitted" on every item (`ModelGltfAdmission.cs:170-171`).
- **Clip duration.** An authored clip duration that differs from the clip's last key adds a plan limitation (`ModelGltfAdmission.cs:159-167` at the pin, `:202-223` at 0152179). A limitation makes the plan unsupported (`ModelGlbPlan.cs:53`), and the writer then throws for the whole item (`ModelGlbWriter.cs:130`). Main relaxes this only for metadata-only clips, constant declared channels (:211-216), and **clocked clips, whose interval the clock stage owns (:202-203)**; a keyed morph track is none of these (`ModelGltfTransformChannels.HasKeyedPlayback`, :58-66).
- **Empty primitives.** It rejects a primitive without vertices or triangles (`ModelGltfGeometry.cs:122-123`).

**Blender writer**
- It packs the Original bytes unchanged (Exact) when Blender reads the container (dds/png/tga, `SH/Slfx77.Multitool.Media.Blender/BlendAdmission.cs:262-266`) and otherwise the StandardPayload, Converted (`ImageStandardPayload`, :267-271). XnGine containers are not Blender-readable, so every XnGine image takes the StandardPayload path.
- It degrades any LeftHanded basis (`:179-181`), so reader normalization stays.
- It creates one mesh datablock per document primitive, whose vertices are the used source points (`Python/import_model.py:79-82`).
- It writes AbsolutePositions exactly (`:639-641`).
- It classes Flat normals Exact (`:556`) and every target with NormalDeltas Degraded (`:584-587`).
- It classifies each clip through `BlendAnimationAdmission.Unsupported` (`BlendAdmission.cs:1108-1117`); a keyed Step morph-weight clip is carried natively (`AnimationNativeCurves`), and later-cut(1b) remains only for clips the admission refuses.
- It maps an attribute of type (Int16, 2 components) to Degraded (`:595-609`).

**Catalog, probe and inspection**
- `ModelSourceFormatMetadata` accepts `DefaultUnits` with Unknown provenance plus a unit-policy text (`Catalog/ModelSourceFormatMetadata.cs:12-19`; `SceneUnits.cs:3-5`).
- A probe sees 64 KiB (`Sources/ModelSourceCandidate.cs:9`) and the entry metadata, including `Provenance` (`:38`).
- `IsComplete` is set when the helper observed EOF within the budget, independent of the declared Length (`:48`).
- Two recognitions of one file are Ambiguous (`ModelSourceRegistry.cs`, per survey 1).
- Inspection reads "may read encoded image payloads and descriptors but must not decode image pixels" (`IModelSourceReader.cs:27-29`).
- `ModelTextureInfo.Palette` exists (`Inspection/ModelTextureInfo.cs:66`) and is printed (`ModelInfoTextFormatter.cs:118`), but nothing assigns it.

### 0.4 Where the design's cut-1c text needs correcting

**.3DC geometry**
- **Normals.** Newell gives one flat normal per plane (`BMT/Core/Formats/Redguard/Redguard3dcFile.cs:393-431`), which Shared calls `Flat`, not `DerivedSmooth` (see D4). The wide files also store authored per-pose normals, so the "4 or 12 bytes per plane" block is typed data on wide files, not only raw bytes.
- **Header offsets.** "Header +24/+48/+52 are frame 1's" (`Redguard3dcFile.cs:17-20`, CLAUDE.md) holds only on wide files. `KeyframeMesh.Planes[].PlaneData` is sliced from +24 (`XnGineMesh.cs:384-387`), so the new reader must not use it.
- **Corners.** A keyframe-only corner test would drop corners that are real in other poses (45 n-gons, section 0.2).

**UVs and textures**
- **Unfold.** The packed-UV unfold reaches Redguard only because of the object-id gate, and it contradicts the stored data even on Daggerfall (section 0.2).
- **.3DC UVs** are undecoded. "The raw accumulated UVs ... with the normalized UV as the portable form" cannot be satisfied for .3DC files.
- **Battlespire meshes are already textured** by name through BSI.BSA (`BattlespireTextureResolver.cs:34-224`). CLAUDE.md still calls them untextured.

**Inspection counts**
- The design's `mesh info` "vertices" rule (source points, `model-document-design-20260923.md:274`) is overcounted by the Shared sum whenever one point domain is split into per-texture primitives (section 0.2; SA6).

**Scope and sources**
- **Redguard fxart meshes** (v4.0/v5.0) are a second mesh family. The design does not list them.
- **`redguard_rgm_format.md`** exists only in the main checkout.

---

## 1 Decoder strategy

**Reuse the two parsers**, with additive changes that keep legacy output byte-identical. Build the document in new `Core/Modeling` code. Never use the decomposer or the exporters as a source, because they bake.

| Component | Verdict | Findings |
|---|---|---|
| `XnGineMesh.Parse` (`BMT/Core/Formats/Xngine/Mesh/XnGineMesh.cs:203-318`) | Reuse, adding a stored-UV mode | Unfolds inside the walk and keeps only the result (`:374-379`). Layout and object id come from the caller (`:203-212`). v2.5 offsets are multiplied by 3 (`:367`). PlaneData is taken from header +24 (`:384-387`). Add `XnGineUvHandling {Reference, Stored}` defaulting to Reference, so every legacy caller is unchanged. |
| `Redguard3DcFile.TryParse` (`Redguard3dcFile.cs:127-269`) | Reuse, adding accessors | Exact tiling (`:302-352`) is the acceptance rule. Today it exposes only the keyframe mesh, the frames' points, the width, the record dwords and the unaccounted length (`:83-102`). Add: frame-table records including the 4th dword, preamble dwords 0 to 5, per-frame normal and plane-data block offsets with raw spans, and header +44. |
| `XnGineMeshDecomposer` (`XnGineMeshDecomposer.cs:76-328`) | Rule reference and the A3x oracle only | It bakes /256 and /16 (`:317-319`), normalizes normals (`:310-314`), and drops collinear corners (`:147-185`). **Its kept-corner list starts at c1 and ends at c0** (`:153-170`), and it fans from that list's first entry (`:322-327`), so its apex is the first kept corner from c1, not c0. It skips a polygon with fewer than 3 kept corners (`:97-100`), regenerates later-corner UVs with int truncation (`:192-279`), and falls back to raw UVs when the fit is degenerate (`:132-133`). The reader re-implements the rule independently, as 1a did with strips. |
| `XnGineMeshGlbExporter` | Do not reuse | Flips Y per vertex (`:186`), swaps the second and third corner of every triangle (`:156-162`), skips empty submeshes (`:142-145`), replaces a zero normal with +Y (`:189-191`), writes double-sided, metallic 0, roughness 1 (`:172-174`), uses a 64-texel fallback (`:31, :150-151`) and conjugates placements (`:108-121`). |
| `ClassicMeshPreviewSource`, `XnGineViewerSceneAdapter`, `XnGineScenePreviewRenderer` | Leave alone | Viewer only. `ClassicMeshPreviewSource.cs:105` parses every Daggerfall mesh with id 0, so the GUI unfolds ids ≥ 905. That is a separate legacy bug. |
| `DaggerfallTextureFile` (`BMT/Core/Formats/Daggerfall/DaggerfallTextureFile.cs:86-383`) | Reuse for indices; add a header-only path (D9) | Frames come out as `IndexedBitmap` (`:393-398`). TEXTURE.000 and .001 are generated swatches whose record N is palette index N or 128+N (`:23-24, :109-147`). 215, 217 and 436 are refused (`:25`). `Parse` decodes every record eagerly. Add a record byte-range accessor (needed for Original) and a descriptor-only header read. |
| `BsiFile` (`BMT/Core/Formats/Battlespire/BsiFile.cs:13-78, 125, 187-194`) | Reuse; add a header-only path (D9) | Indexed frames, the CMAP palette, and raw HICL and HTBL. One entry can hold several images (`:80-90`). |
| `BattlespireTextureName` (`:60-200`); `BattlespireTextureResolver` (`:34-224`) | Reuse the key logic, not the PNG path | Base-40 stem; solid color at 0xFFF00000 and above (`:83-88`), x555 Assumed (`:183-186`). The resolver takes image 0, frame 0 (`:200, :207`) and produces RGBA, but the reader needs indices. |
| `RedguardTextureResolver` (`BMT/Core/Formats/Redguard/RedguardTextureResolver.cs:10-31`) | Reuse the (archive, record) to TEXTURE.nnn rule | Produces RGBA; the reader needs indices. |
| `RedguardLevelLoader.ResolvePalette` (`RedguardLevelLoader.cs:234-246`) | Lift the rule (it is private) | WORLD.INI's world palette, then `<stem>.COL`, then `art_pal.col`. |
| `Palette` (`BMT/Core/Imaging/Palette.cs`) | Reuse | `FromVga6Bit` uses `(v << 2) | (v >> 4)` (`:48-53, :201`). `LoadDaggerfallCol` reads full-range 8-bit (`:147-155`). Alpha is opt-in (`:9-10`). |
| `ClassicGameLocator.DetectRootForFile` (`BMT/Core/Games/ClassicGameLocator.cs:151`) | Reuse in the shell only (D7) | None |
| `census_xngine.py`, `plan_fix_triangulation.py.txt` (R/) | Seed of the A6, A7 and A3x oracles | Written from BMT's layouts but sharing no code with them, like the NIF probe's relation to nif.xml. |

## 2 File blueprint

All files are BMT-owned unless marked Shared.

`BMT/Core/Modeling/Xngine/`:
- **`XnGineModelReader.cs`:** `IModelSourceReader` plus `IModelSourceFormatMetadataProvider`.
  - `FormatId` is `bmt.xngine.3d`.
  - `SupportsInspectionWithoutPixelDecoding` is true once the D9 inspection path exists.
  - `MaximumSourceBytes` is 16 MiB (assumed; the largest measured entry is 81,394 bytes).
- **`XnGineModelProbe.cs`:** the bounded walk (8- or 10-byte headers), the `.3DC` shape test S (section 6.1), and fxart tag recognition.
- **`XnGineGameIdentity.cs`:** the game chain with evidence (section 6.2).
- **`XnGineModelGeometry.cs`:** split vertices, `Faces`, `PointIndices`, the winding rule, normals, and the `xngine.uv16` and `xngine.plane` streams.
- **`XnGineUvRule.cs`:** the portable UV, an independent re-implementation of the reference rule.
- **`XnGineTriangulation.cs`:** the reference corner rule and fan, and the zero-area, zero-normal and no-triangle-plane handling (D5).
- **`XnGineModelCoverage.cs`:** the element census plus a tiling of the byte areas, so every uncovered range is its own element.
- **`XnGineModelNativeState.cs`:** kind constants and bounded payloads (section 3.4).
- **`XnGineModelTextures.cs`, `XnGineModelMaterials.cs`:** per-game image sources and materials (section 5).
- **`XnGineModelReadCache.cs`:** parsed texture files and palettes, per item.
- **`XnGineModelFormatMetadata.cs`.**

`BMT/Core/Modeling/Redguard/`:
- `Redguard3DcModelReader.cs` (`FormatId` `bmt.redguard.3dc`).
- `Redguard3DcModelCoverage.cs`, `Redguard3DcModelNativeState.cs`, `Redguard3DcModelAnimation.cs`.

Elsewhere under `BMT/Core/`:
- `Modeling/Units/ClassicModelUnits.cs`: the section 4.1 rows (design row 14).
- `Modeling/ClassicContainerFacts.cs`: a BMT-internal query that the XnGine and .3DC readers ask of `item.Source`. It returns the container kind (numbered XnGine BSA, named XnGine BSA, ROB), entry index, stored size, compression flag, a stored-bytes read (for the stored SHA) and, for ROB, the 80-byte segment header. Implemented by `BethesdaBrowseSource`; absent for any other source.
- `Modeling/ClassicModelCompanions.cs`: an `IAssetSource` and resolver over a detected classic install, the archive folder, or `--data-root`.
- `Modeling/BethesdaModelReadCache.cs`: a composite per-item cache (NIF and classic), so one factory serves every reader.
- `Imaging/IndexedPngEncoder.cs`: color type 3 at 8 bits, with IHDR, PLTE, tRNS when declared, IDAT through ZLibStream, and IEND, each with CRC-32.

**Changed files**
- `BethesdaModelRegistration.cs`: register both readers; composite cache. (The `bmt.classic-game` option key and its evidence key landed with slice 3, as the constants the identity chain reads; slice 8 sets them from the shell and adds no key.)
- `BethesdaModelWorkflow.cs`: section 7.
- `AssetBrowse/BethesdaBrowseSource.cs`:
  - implements `ClassicContainerFacts`;
  - reports `Length: null` for an LZSS entry, whose decoded length the archive does not store (`AssetEntry.cs:5` permits null).
- `XnGineMesh.cs`: stored-UV mode.
- `Redguard3dcFile.cs`: accessors.
- `DaggerfallTextureFile.cs`: record ranges and a header-only read.
- `BsiFile.cs`: a header-only read.
- `XnGineBsaBackend.cs`: duplicate names (D6).
- `NifModelReader.cs`: accept the composite cache.
- `CLI/Commands/Mesh/MeshCommand.cs`: help text for the new `--game` values.

**Tests**
- `T/Core/Modeling/Xngine/*` and `T/Core/Modeling/Redguard/*`.
- `T/Helpers/XnGineTestMeshBuilder.cs`: an independent writer of v2.5, v2.6 and v2.7 records with 8- or 10-byte headers, narrow and wide .3DC stacks, TEXTURE records, BSI stubs (single- and multi-image) and COL palettes.
- `T/Core/Modeling/RealAsset/Cut1c*.cs`: Bucket-B tests, with the guard, the trait, and `[Collection(SequentialIntegrationGroup.Name)]`.
- `T/Core/Modeling/Samples/cut1c-cover-manifest.json`.

**Oracles** (never part of the build)
- `tools/scripts/gate1c/`: `xngine_probe.py` (A6), `rg3dc_probe.py` (A7), `texture_probe.py` (A4x) and `run_gate1c.py`.
- `run_gate1c.py` reuses `tools/scripts/gate1a/dump_reader.py`, `glb_reader.py`, `package_reader.py`, `hop_c.py` and `hop_e.py`.

## 3 `.3D` mapping

### 3.1 Document
- **Identity:** `SourceFormat` `bmt.xngine.3d`; Name is the entry stem, or the id for ARCH3D; `SourceIdentity = item.Reference.ToString()`.
- **Provenance:** `SourceProvenance(relative path, SHA-256 of the payload the reader parsed)`. For Battlespire that is the decompressed payload. The stored (compressed) size and SHA come from the container-facts query and go into native state.
- **Structure:** one `SceneDefinition`, one node (Role Transform, MeshIndex 0) and one `SceneMesh`.
- **Primitives:** one per distinct texture key that keeps at least one plane with a triangle (D5), in first-use order. That is the order of `XnGineMesh.UniqueTextures` (`XnGineMesh.cs:284-294`) with emptied keys removed, which is also the legacy grouping (`XnGineMeshDecomposer.cs:102-107`) after the legacy exporter skips empty submeshes.
  - 9 retail keys are removed this way (section 0.2), and no retail mesh loses every key.
  - A synthetic mesh that does is rejected with `InvalidDataException` naming the reason (`SceneValidation.cs:60` would reject a mesh with no primitives).

### 3.2 Geometry rules

**Planes that reach the geometry.** Every plane except a no-triangle plane (D5, next paragraph); no-triangle planes are carried only in native state (section 3.4) and classified NativeOnly (section 3.3).

**Vertices**
- **Splitting:** one vertex per (plane, corner) of every plane that reaches the geometry, grouped by primitive, then in plane order, then in the `Faces` corner order below.
- **Position:** `(X, -Y, Z)` of the source point as float32. This is exact, because every |coordinate| is below 2^24 (section 0.2).
- **Normal:** `(Nx, -Ny, Nz) / 256` of the plane's authored normal, on every corner. Dividing by a power of two is exact. `NormalMode` is Vertex and `NormalProvenance` is Authored.
  - Non-unit normals are kept as authored; the GLB writer normalizes them (an Approximated row, `ModelGltfGeometry.cs:230-234`).
  - A zero normal reaches the geometry only on a plane whose triangles cover area. No retail plane is like that (369 of 369 zero-normal planes cover no area), so no zero-normal slot reaches either writer on retail data. A synthetic one gets the GLB writer's reconstruction and its Degraded row.
- **Color:** `(1,1,1,1)`.

**Topology**
- **`Faces`:** sizes are the plane point counts; corner indices are sequential vertex ordinals.
- **Faces corner order:** `(c0, c(n-1), ..., c1)`, which reverses the stored orientation to compensate for the Y negation. The rule id goes into the basis evidence and into `bmt.xngine.uv-rule`.
- **`PointIndices`:** `(header point count, source point index per vertex)`. Blender welds n-gons through these; GLB ignores them.
  - Every primitive of the mesh declares the same header point domain, as `ScenePointIndices.PointCount` defines it (`ScenePointIndices.cs:36-37`).
  - `mesh info` sums that domain once per primitive (`ModelInfoDocument.cs:30`). Until SA6 it therefore prints points × texture keys (for example 240 for 44005's 48 points), and the gate's mesh-info check records that as a known Shared defect.

**Attributes**
- **`xngine.uv16`:** semantic `xngine.texel16`, FaceCorner domain, Int16 × 2. It holds the stored u and v per corner, before the unfold and before accumulation (D1), in `Faces` corner order. If Shared ask SA4 is declined, store Int32 × 2 instead (section 10).
- **`xngine.plane`:** Face domain, Int32. It holds the source plane ordinal, because primitives regroup planes by texture and omitted planes leave gaps.

**Triangles (`Indices`), the reference rule (D5)**
- A 3-corner plane gives `(c0, c2, c1)`: the reference triangle `(c0, c1, c2)` with its second and third corners exchanged, as the legacy exporter writes it (`XnGineMeshGlbExporter.cs:156-162`).
- **An n-gon:**
  - Let K be its kept corners in the reference order: the source corners c1, c2, ..., c(n-1), c0, minus every corner the 0.001 rad test drops (`XnGineMeshDecomposer.cs:147-185`).
  - For i = 1 to |K|-2, emit `(K0, K(i+1), K(i))`. This is exactly the legacy exporter's triangle stream for that plane, index for index (`:322-327` plus the swap).
  - The apex K0 is c1 whenever c1 is kept, not c0. The draft's "fan from corner 0" was wrong.
- **An n-gon with fewer than 3 kept corners** yields no triangle, as in the reference (`XnGineMeshDecomposer.cs:97-100`): 24 in 3D.BSA, 26 in 3D.BS6, 1 in ROB.
- **Zero-area triangles:** a triangle that covers exactly no area is omitted when the plane's authored normal is zero; otherwise zero-area triangles are kept, as in 1a. Omitted triangles are counted in a diagnostic.
- **A plane left with no triangle** (the two cases above) is omitted from the primitive geometry: no vertices, no `Faces` entry, no `PointIndices` entries. It is carried in `bmt.xngine.omitted-planes`. This keeps every primitive non-empty (`SceneValidation.cs:280-281`; `ModelGltfGeometry.cs:122-123`), and it keeps zero-normal slots away from the GLB writer's Degraded placeholder rows (`:193-228`). Retail count: 137 planes in 3D.BSA, 249 in 3D.BS6, 1 in ROB, at most 66 in one mesh.
- **Collinear corners** dropped from a plane that still yields triangles stay in `Faces`. Their vertices are simply unreferenced by triangles, which is legal, and their normals are non-zero.
- **Folded triangles** (399 in 3D.BSA, 362 in 3D.BS6, 13 loose Battlespire, 123 in ROB, 1 in ARCH3D) do not cover their polygon, so the GLB "Converted" triangulation row does not hold there (open question).

**Portable UVs**
- `TexCoord = reference rule / 16 / texture (width, height)`.
- Solid-color primitives divide by the legacy swatch size: 32 for Daggerfall and Redguard, 4 for Battlespire (`BattlespireTextureResolver.cs:37`).
- An unresolved texture divides by 64 (`XnGineMeshGlbExporter.cs:31`), Assumed, with a diagnostic.
- The reference rule:
  - Corners 1 and 2 accumulate as deltas (`XnGineMeshDecomposer.cs:121-126` for triangles, `:219-221` for n-gons).
  - Later corners come from the plane fit, with the reference's int truncation (`:212-217, :268-271`).
  - A degenerate fit uses the raw value of every corner (`:132-133`).
  - The unfold applies to corners 0 to 2 only for Daggerfall ids below 905 (D2).
- The rule is marked Assumed, with evidence quoting the section 0.2 measurements.

### 3.3 Coverage elements

The element list is taken from the header counts and a byte-area tiling, independent of what the reader builds. There are no Dropped rows.

| Element | Classification |
|---|---|
| `header` | Typed (counts, offsets); undecoded dwords go to native state |
| `points` | Typed |
| `normals` | Typed |
| `plane:{k}` for each k below the plane count | Typed (face, corners, UVs, texture key). **NativeOnly for a no-triangle plane**, with the reason "fewer than 3 corners survive the reference corner test" or "zero authored normal and no area; no drawable surface", carried by `bmt.xngine.omitted-planes`. |
| `plane-data` (if the offset is in range) | NativeOnly: "24-byte plane records undecoded" |
| `object-data` (if count > 0; 10,248 of 10,251 ARCH3D records) | NativeOnly: "object-data area undecoded" |
| `unclaimed:{start}-{end}` | NativeOnly: "bytes outside every declared area". The tiling of static .3D files has not been measured yet (M-T). |

The largest plane count seen (712, in cover file 451) is far below the 65,536-element ceiling.

### 3.4 Native state

| Kind | Target | Content |
|---|---|---|
| `bmt.xngine.header` v1 | Document | Tag; counts; radius; the dwords at +16, +20, +36, +40, +44 and +56; all offsets; object id; layout; game and its evidence |
| `bmt.xngine.planes` v1 | Mesh | Per plane: ordinal, point count, Unknown1, texture key (u16 or u32), header tail as hex. Arrays of more than 64 elements are summarized by count and SHA-256 unless `--native full` (the 1a rule). |
| `bmt.xngine.omitted-planes` v1 | Mesh | Always in full (at most 66 per retail mesh): per no-triangle plane, the ordinal, texture key, corner point indices, stored u and v, authored normal, and the reason. Omitted texture keys are listed too. |
| `bmt.xngine.plane-data`, `bmt.xngine.object-data`, `bmt.xngine.unclaimed` | Mesh or Document | Offsets and SHA always; raw bytes only with Full |
| `bmt.xngine.uv-rule` | Document | Rule id; unfold applied yes or no, and how many values it changed; degenerate fits; collinear corners dropped; omitted triangles and planes |
| `bmt.xngine.container` | Document | From the container-facts query: container kind, entry index, stored size and SHA for LZSS entries, ROB segment name and the 80-byte segment header. Absent when the source is not BMT's. |

**Diagnostics:**
- Header +16 non-zero on a static mesh (the 4 MENU.ROB segments).
- A texture that cannot be resolved, or resolves ambiguously.
- A game or layout fallback.
- Omitted planes and texture keys.

## 4 `.3DC` mapping

**Base and topology**
- The base primitive geometry is the keyframe (frame 0, int32 points).
- `Faces`, `PointIndices`, `xngine.uv16`, `xngine.plane` and the triangle rule follow section 3.2. Plane point offsets divide by 12 (`Redguard3dcFile.cs:433-437`).
- **The corner test is pose-independent:** a corner is kept when the 0.001 rad test keeps it in at least one pose. 45 n-gons in 32 files keep different corners in different poses. No keyframe-degenerate n-gon revives in another pose, and no .3DC keyframe n-gon has fewer than 3 kept corners (plan_fix_triangulation.json), so no .3DC plane is omitted.

**Morph targets:** frames 1 to N-1, named `pose NNN` after the source frame ordinal.
- **Narrow:** `PositionDeltas` per vertex are `(dx, -dy, dz)`, taken exactly from int16. They are added to the keyframe and never accumulated frame to frame; accumulating them makes the mesh drift on all 91 retail files with 10 or more frames (`Redguard3dcFile.cs:369-373`).
- **Wide:** `AbsolutePositions` per vertex are `(x, -y, z)`, exact int32 values. The GLB writer lowers them to relative targets, Converted below 2^24; Blender writes absolute shape keys exactly (`BlendAdmission.cs:639-646`).

**Normals (D4, recommended option)**
- **Both widths:** `NormalMode.Flat` with `NormalProvenance(Flat)`, vertex normals `(0,0,0)` (the 1a rule for absent normals), and no normal deltas (Flat forbids them). Both writers class this Exact (`ModelGltfGeometry.cs:236-238`; `BlendAdmission.cs:556`).
- **Wide normal blocks** go to native state (`bmt.redguard.3dc.frames`, raw with Full, count and SHA otherwise). Their coverage element is NativeOnly with the measured agreement quoted (section 0.2).
- **Alternative (owner's choice):** wide files as Vertex mode with Authored normals, base = frame-0 block / 256 with Y negated, `NormalDeltas` = `(frame i - frame 0) / 256` with Y negated. The cost is stated in D4.

**UVs (D3):** the portable `TexCoord` uses the .3D rule without the unfold. Materials stay untextured. Texture keys stay in native state, and a document diagnostic states that .3DC UVs are not decoded.

**Clip**
- One `SceneAnimation` named `poses`, holding one `SceneMorphTrack(node 0, N-1 targets)`.
- **Keys:**
  - key times i/15 s for i = 0 to N-1;
  - one-hot weights, all zero at frame 0;
  - interpolation Step;
  - **plus one derived final key at N/15 s repeating frame N-1's weights**, so the clip spans N frame periods.
- **Duration:** `DurationSeconds` stays null. The GLB writer refuses any clip whose authored duration differs from its last key (`ModelGltfAdmission.cs:159-167` at the pin, `:202-223` at 0152179; `ModelGlbPlan.cs:53`; `ModelGlbWriter.cs:130`). The draft's "duration N/15 with the last key at (N-1)/15" would have made every .3DC item fail hops B, C and E. That failure mode is now avoidable a second way — declaring a clock, whose interval the clock stage owns (:202-203) — which the plan still does not take; the recommendation (null `DurationSeconds` plus the derived final key) is unchanged.
- **Native record:** `bmt.redguard.3dc.clip` records the derived key ("holds the last pose for one frame period at the assumed rate").
- **Alternative:** no derived key and no duration; the clip then ends at (N-1)/15, and a looping player shows the last pose for zero time.
- **Rate and interpolation** are Assumed (design section 4.2; RE-3).
- **Timing record:** after the slice-0 pin bump, pass `timing: new SceneAnimationTiming(framesPerSecond: 15, provenance: Assumed, evidence)` (`SceneAnimation.cs:26` on main).
- **Blender:** the clip is written natively (32 native frames/s package timeline, `BlendAnimationAdmission.cs:12`; MorphWeights F-curves on the mesh's shape keys, `import_model.py:5867-5884`). Hop D verifies the readback at the 1/15 s key times.

**Units**
- `ClassicModelUnits.Redguard3Dc` is 1/20480, "Assumed (human actors only)".
- Every .3DC also carries the document diagnostic `bmt.redguard.3dc.actor-scale-unknown`: "actor scale unknown; geometry normalized to the int16 range".
- The design asks for a Degraded row. Readers cannot emit fidelity rows, so the diagnostic is the reader's carrier (open question).

**Coverage**

| Element | Classification |
|---|---|
| `header` | Typed |
| `frame-block` (preamble dwords 0 to 5) | NativeOnly |
| `frame-table` | Typed (frame offsets); the 4th dword NativeOnly |
| `plane:{k}` | Typed |
| `frame:{i}:points` | Typed |
| `frame:{i}:normals` | NativeOnly on both widths under the recommended D4. Wide: "authored per-pose plane normals; Flat shading derives directions from each pose". Narrow: "4-byte per-plane frame records undecoded". (Wide is Typed under the D4 alternative.) |
| `frame:{i}:plane-data` | NativeOnly |
| `unaccounted` (the one region the tiling leaves, `Redguard3dcFile.cs:344-348`) | NativeOnly |

The largest file has 228 poses, which yields fewer than 700 frame elements.

**Native state**
- `bmt.redguard.3dc.frames`: the table records, including the 4th dword; the block offsets; the width.
- `bmt.redguard.3dc.preamble`.
- `bmt.redguard.3dc.clip`: rate, interpolation, the derived final key.
- Header +24/+48/+52/+44, with the measured note that the offsets are frame 1's only on wide files.
- Raw frame blocks, only with Full.

## 5 Textures and palettes

Slice 7 builds typed palettes directly: SA1 shipped in Shared `04ba005` (`ScenePalette`, `ScenePaletteEntry`, `ScenePaletteRule`, `ScenePaletteTable`, `ModelDocument(palettes:)`, `SceneImageSource(paletteIndex:)`, `SceneElementKind.Palette`; limits are 65,536 entries, 64 auxiliary tables and 64 MiB per palette, 4,096 palettes per document). Slice 10 is folded into slice 7. Shared interprets no rule, modifies no alpha and infers no encoding from length: the reader supplies all three.

| Game | Plane key | Image (Original) | Palette | Encoding | Solid colors |
|---|---|---|---|---|---|
| Daggerfall | u16: archive = bits >> 7, record = bits & 0x7F (`XnGineMesh.cs:344-349`) | TEXTURE.nnn record r, next to ARCH3D.BSA in ARENA2 | ART_PAL.COL (`Palette.cs:147-155`) | Rgb8, 8-byte header | TEXTURE.000 and .001: palette index r or 128+r, used as the base color factor with no image |
| Redguard | archive·128+record (`RedguardTextureResolver.cs:10-17`) | 3dart TEXTURE.nnn. fxart TEXBSI is index-parallel on Disc 1 (open question). | The world .COL: WORLD.INI, then `<stem>.COL`, then `art_pal.col` (`RedguardLevelLoader.cs:234-246`). For a ROB, the ROB stem names the map; for a loose .3D or .3DC, see the open question. | Rgb8 (776-byte .COL) | Set 0 is the solid table |
| Battlespire | u32, a base-40 BSI stem (`XnGineMesh.cs:52-58`) | BSI.BSA entry `<stem>.BSI`, first image, frame 0 (as `BattlespireTextureResolver.cs:200-207` does today) | The BSI's own CMAP | Vga6, expanded by `(v << 2) | (v >> 4)`; Assumed (RE-10) | A key at 0xFFF00000 or above is a 15-bit color, x555 Assumed (`BattlespireTextureName.cs:83-88, 183-186`), used as the base color factor |

**One `SceneImage` per distinct (companion file, record or image)**
- **`SceneImageSource` fields:**
  - Container: `TEXTURE.nnn`, `BSI` or `TEXBSI`.
  - Descriptor: W×H, 1 mip, 8 bpp, `Channels.Indexed`, compression None, sRGB Assumed. It comes from record headers, so it needs no decode.
  - Original: the record's (or BSI entry's) exact byte range, whose SHA-256 is the dedup key (open question on the slice boundary).
  - Location: the resolved companion.
  - StandardPayload (conversion reads, and inspection reads only under the D9 alternative): an indexed PNG (color type 3, 8-bit, the expanded palette in PLTE, tRNS only when a transparency rule is declared). Its note reads "indices unchanged; palette expanded by `<rule>`".
- **When frame 0 alone is not lossless:** a TEXTURE record with several frames, a BSI entry holding more than one image (2,599 entries yield 2,621 images, `BsiFile.cs:80-90`), or a BSI image with several frames. The source then becomes `Derivation("bmt.xngine.frame0", reason "M images, N frames; image 0 frame 0 only", payload = indexed PNG)`, because StandardPayload promises a lossless re-containerization (`SceneImageSource.cs:14`). Both writers then report it Degraded. The image and frame counts go into native state.
- **Transparency:** index 0 stays opaque, as today (alpha is opt-in, `Palette.cs:9-10`); Assumed, open question.

**Palette record.** Each distinct palette (a .COL file; a BSI CMAP) becomes one `ScenePalette`: Encoding (Rgb8 for a 776-byte .COL, Vga6 for a CMAP), the original bytes with container and offset, the reader-expanded RGBA entries, the expansion rule id with its provenance (Assumed for Vga6 per RE-10), the transparent indices under the declared rule (none by default: index 0 stays opaque, M-I), and the auxiliary tables HTBL and HICL retained as bytes. Identity is the document ordinal, so the reader declares one palette per distinct companion file or BSI entry (dedup by SHA-256 of the original bytes within one container kind) and every image binds its palette by ordinal (`paletteIndex`); the PLTE in the indexed PNG carries the same expanded colors. No `bmt.xngine.palette` native row: a palette-targeted native row would move the Blender package to revision 8 for nothing the typed palette does not already carry. Both writers report `Dropped`, reason `source.palette-provenance-omitted`, feature `source/palette`, at the palette index; gate 1c allows exactly that reason on XnGine items, backed by A4x (the PLTE and the indices are pinned against a Python expansion of the original bytes), which is the reader pixel evidence the SA3 ruling requires. `mesh info -v` names the palette through Shared document-aware inspection (`ModelTextureInfo.Palette` is filled from the ordinal), and `mesh dump` carries the exact values and bytes.

**Companion resolution.** `ClassicModelCompanions` resolves:
- `TEXTURE.042`;
- `ART_PAL.COL`;
- `<STEM>.BSI`, inside BSI.BSA;
- `WORLD.INI`;
- `<MAP>.COL`;
- `TEXBSI.042`.

It chooses data roots from the shell-detected install, the archive folder, or `--data-root`, and the reader blocks on the async resolver as 1a does. The allowance is 64 MiB (the largest measured companion file is 1,097,216 bytes).

## 6 Probes, game identification, units and basis

### 6.1 Probes (exactly one reader recognizes each file)

**Shape test S(b):** header +16 > 0; the +20 block offset lies inside the prefix; the table offset (block dword 0) is at or before the plane-list offset (+60); and `(plane list - table)` divides into 3- or 4-dword records per frame (`Redguard3dcFile.cs:141-179`).
- S holds on 147 of 147 .3DC files and on no static mesh.
- It fails on the 4 MENU.ROB segments with +16 = 9.
- +16 is 0 on every other static mesh except one 3D.BSA non-mesh stray (ARCH3.EXE, +16 = 1008).

**Completeness:** both probes judge it by `candidate.IsComplete` (EOF observed within the budget, `ModelSourceCandidate.cs:48`), never by `Length`, which is the stored size for LZSS entries until slice 3 nulls it.

**`bmt.redguard.3dc`**
- Supported when S holds and the plane walk fits the prefix.
- Confirmed when the file is complete in the prefix and tiles (26 files); Tentative otherwise (121 files).

**`bmt.xngine.3d`**
- **Supported:** tag v2.5, v2.6 or v2.7, lists that fit, and a plane walk with 8- or 10-byte headers.
- **NotAModel when S holds**, so .3DC files go only to the other reader.
- **Unsupported** for tags v4.0 and v5.0, with the reason "Redguard 3dfx mesh (10-byte plane header, point indices) not decoded: later cut".
- **Tentative** when not complete: 4 ARCH3D entries, including 451, and 1 3D.BSA entry.
- **NotAModel:** non-mesh strays and empty ROB segments.
- **Evidence text:** for example "XnGine v2.7 mesh, 8-byte plane headers, 48 points, 34 planes".

### 6.2 Game identification (recorded as evidence)

The reader tries each step in order and stops at the first that answers. It touches no file system; everything it needs arrives through app options or the item's source.
1. **`--game`:** the `bmt.game` option, extended to `daggerfall`, `redguard` and `battlespire`. A NIF item under such a value throws (`NifModelUnits.cs:44-58`), which is intended: the user asserted a game.
2. **Container**, from the `ClassicContainerFacts` query on `item.Source` (`ModelSourceItem.cs:27`), because `Entry.Provenance` holds only the archive path:
   - a numbered XnGine BSA means Daggerfall, and the object id is the entry name;
   - a name-record XnGine BSA with LZSS entries means Battlespire;
   - a ROB means Redguard.
3. **Install:** the `bmt.classic-game` option and its evidence.
   - The shell sets it once per input path from `ClassicGameLocator.DetectRootForFile` (`ClassicGameLocator.cs:151`), unless `--game` was given.
   - It is a separate key from `bmt.game`, so a detected classic install never reaches `NifModelUnits`.
4. **Content:**
   - A mesh that walks only with 10-byte headers is Battlespire. Measured: 0 of 4,755 Battlespire meshes walk with 8-byte headers, and 0 of 4,719 Redguard static meshes walk with 10-byte headers.
   - A mesh that walks only with 8-byte headers **and has header +20 ≠ 0** is Redguard. Measured: +20 is 0 on 10,251 of 10,251 ARCH3D records and non-zero on 52 of 52 loose Redguard files and 4,352 of 4,667 ROB meshes.
   - **A mesh that walks only with 8-byte headers and has +20 = 0** is ambiguous between an extracted ARCH3D record (BMT's own `archive extract` writes them loose, named by id, and 10,231 walk only with 8-byte headers) and one of 315 Redguard ROB meshes. Its game is Unknown, with a diagnostic naming `--game`. The draft called it Redguard, which would have given an extracted Daggerfall record half its size (1/20480 instead of 1/10240) and skipped the unfold.
   - 20 ARCH3D records walk both ways; the container always settles them.

**Fallbacks and conflicts**
- If no step answers, the layout comes from content, the game is Unknown, the units are Unknown (the design's guard), and no unfold is applied.
- `--game` conflicting with the content (for example `battlespire` on an 8-byte-only mesh) throws.

### 6.3 Units (`ClassicModelUnits`)

| Row | Meters per native unit | Provenance |
|---|---|---|
| Daggerfall | 0.025/256 = 1/10240 (double literal `1.0 / 10240`) | Assumed (RE-2) |
| Redguard .3D and ROB | 1/20480 | Assumed (RE-3) |
| Redguard .3DC | 1/20480, plus the actor-scale diagnostic | Assumed (human actors only) |
| Battlespire | (1/64)/256 = 2^-14 (exact in binary) | Assumed, weak (RE-4) |
| Game unknown | 1.0 | Unknown (guard) |

The format metadata declares `DefaultUnits` as Unknown (1.0) plus a unit-policy text listing these rows (allowed by `ModelSourceFormatMetadata.cs:12-19`).

**DOGA001 control**
- DOGA001's keyframe spans 30,060 native units in Y and 40,706 in Z. At 1/20480 that is 1.47 m tall and 1.99 m long.
- A human actor (CYRSA001, Y 32,766) comes out at 1.60 m.
- The test pins the row and requires the diagnostic on both files. The receipt records the implied sizes.

### 6.4 Basis

**Declaration.** `SceneSourceBasis(up +Y, forward +Z, RightHanded, normalizedByReader: true, Assumed, evidence)`. The evidence reads: "XnGine stores Y down (the legacy export flips Y: `XnGineMeshGlbExporter.cs:108, :186`); the reader negated Y and reversed corner order; forward and chirality not established."

**Writer effect.** The GLB writer needs no rotation (up is already +Y). The Blender writer rotates +90 degrees about X (design section 4).

**Why not declare the raw basis?** The GLB writer could reflect a LeftHanded declaration itself (`ModelGltfCoordinates.cs:164-170`), but Blender degrades LeftHanded (`BlendAdmission.cs:179-181`).

**Chirality is open.** The legacy reflection assumes the native frame is left-handed. Daggerfall Unity negates Y into Unity's left-handed frame (recalled), which would instead make the native frame right-handed and the legacy output mirrored. Hop F adds a chirality check: a mesh whose texture carries legible text.

## 7 Shell and workflow (slice 8)

- **Archives:** `IsArchive` also accepts `.bs6` and `.rob` (`BethesdaModelWorkflow.cs:559-564`).
- **Cache:** `BethesdaModelReadCache` becomes the composite that replaces `CreateCache` (`BethesdaModelRegistration.cs:38-41`); `NifModelReader` accepts it.
- **Memory estimate:** per format. For XnGine it is the source length (16 MiB when unknown, which now includes every LZSS entry), plus the 64 MiB companion allowance, plus the writer workspace (`:87-94`).
- **`--game`:** adds `daggerfall`, `redguard` and `battlespire` (`CreateAppOptions`, `:104-119`).
- **Install detection:** without `--game`, the shell walks up from each input path with `ClassicGameLocator.DetectRootForFile` and sets `bmt.classic-game` plus its evidence (section 6.2, step 3). The same detection supplies the companion data root.
- **Companions:** `OpenTextureCompanions` falls back to `ClassicModelCompanions` when it finds no `textures` folder.
- **Duplicate names:** `XnGineBsaBackend.ListFiles` names the k-th later duplicate `<id>~<k>` (`XnGineBsaBackend.cs:34-52`; D6). `mesh convert ARCH3D.BSA --all` then plans without a collision (design section 3.5), and all 24 records can be addressed.
- **`mesh formats`:** prints both readers, their unit rows and their admission rules.

## 8 Ordered slices

Slices 0 to 9 need no Shared change; slices 10 to 12 need one. Per D10, slices 0 to 9 can run beside row 13, and slice 13 waits for gate 1b. The owner builds and runs every slice; agents never build, test or launch Blender (R19).

| # | Slice | Files | Verification (each control must fail) | Bucket-B |
|---|---|---|---|---|
| 0 | Pin bump to canonical Shared main **tip at bump time** (owner policy; 0152179 at the 2026-09-28 re-check, `1fac23d` at the second amendment) | gitlink; compile fixes only | Existing 1a synthetic suite green; a compile-only test that constructs `SceneAnimationTiming` and passes it through `SceneAnimation(..., timing:)`. The re-check at the new tip that the duration rule, the G2 rows, the opaque-payload row, the clip admission and the (Int16, 2) mapping are unchanged (or sections 4 and 10 amended) is done for 0152179 by the 2026-09-28 premise re-check; a later tip repeats it. Amendments C.1-C.13 of that re-check applied. | 1a gate smoke unchanged |
| 1 | Cover manifest with SHA pins | `T/.../Samples/cut1c-cover-manifest.json`; `RealAsset/Cut1cCoverManifest.cs`, plus a resolver for ARCH3D, 3D.BSA and BS6 (LZSS) and ROB entries | Synthetic: manifest schema. Controls: one altered SHA hex digit fails; naming the 3D.BSA namesake of BARSTEP1 (#438, different bytes, measured) fails | Every row resolves: size, payload SHA, stored SHA |
| 2 | Independent oracles A6, A7, A3x and A4x, plus pending measurements | `tools/scripts/gate1c/xngine_probe.py`, `rg3dc_probe.py`, `texture_probe.py`; receipts | Self-check against census counts (10,251 records walk, 147 files tile, 9,190 frames) and against plan_fix_triangulation.json (137/249/1 no-triangle planes, 9 emptied keys). Controls: a flipped UV delta byte is detected; a flipped narrow delta bit is detected; a .3DC walked as .3D yields the wrong points (the trap); a fan from c0 instead of the reference apex disagrees with the legacy triangle stream. Measurements M-T, M-Z, M-F, M-I, M-C (section 11) | Receipts per cover file |
| 3 | Unit rows, game identity and container facts | `Units/ClassicModelUnits.cs`, `Xngine/XnGineGameIdentity.cs`, `XnGineModelFormatMetadata.cs`, `ClassicContainerFacts.cs`, `BethesdaBrowseSource.cs` (facts; null Length for LZSS) | Literal pins for 1/10240, 1/20480 and 2^-14. Chain cases on synthetic sources: container kinds through the facts query, `--game`, `bmt.classic-game`, and content with +20 zero and non-zero. Controls: `--game battlespire` on an 8-byte-only mesh throws; an 8-byte-only mesh with +20 = 0 identified as Redguard fails; a Daggerfall factor applied to a Redguard document fails the pin; an LZSS entry reporting its stored size as Length fails | Identity and evidence for all 11 cover files; an extracted ARCH3D record read loose reports Unknown with the `--game` diagnostic |
| 4 | Parser additions, preserving legacy output | `XnGineMesh.cs` (stored-UV mode), `Redguard3dcFile.cs` (accessors), `DaggerfallTextureFile.cs` (record ranges, header-only read), `BsiFile.cs` (header-only read), area-tiling helper | Existing `XnGineMeshTests` and `Redguard3dcFileTests` unchanged. Stored mode keeps a value of 14336 or more (control: Reference mode unfolds it). The 4th frame dword is surfaced (control: a 3-dword record reports none). Header-only reads return the same W×H and frame counts as full parses (control: one width byte changed) | `classic mesh export` GLB SHA identical before and after, for every cover file |
| 5 | `.3D` reader, geometry only (placeholder materials per texture key, named `TEXTURE.aaa#r` as the legacy export does) | `Xngine/*` (sections 3 and 6), registration | Probe: NotAModel; .3DC declined; fxart Unsupported; Tentative when not complete; strays NotAModel. Controls: unreversed winding fails the outward-normal check; fanning from c0 fails the index-for-index comparison with the legacy triangle stream; welding by position fails when two points coincide; an unfolded `uv16` fails the stored-value pin; keeping a zero-normal, zero-area triangle fails; keeping a no-triangle plane's vertices fails (zero-normal slot reaches the writer); keeping a texture key with no triangles fails `ValidateStructure`; dropping one `plane:{k}` fails the independent element count; an omitted plane missing from `bmt.xngine.omitted-planes` fails; `ValidateStructure` passes | A6 on the cover; A2; A3x against the legacy decomposer (section 9); the 9 emptied-key meshes (7PYLON1 in both archives, EBATAXE, EJOUST, ESPEAR, HBBLD01) read and convert without an empty primitive |
| 6 | `.3DC` reader | `Redguard/*` | Synthetic narrow, wide and 2-frame stacks. Controls: frame-to-frame accumulation fails the delta pin; narrow marked Authored fails the Flat agreement; a clip with Linear instead of Step fails; **a clip whose DurationSeconds differs from its last key fails the GLB plan (NotSupported)**; a clip without the derived final key fails the N/15 end pin; the Blender fidelity row for the clip is `AnimationNativeCurves` (control: a clip the admission refuses reports later-cut(1b)); the .3DC package writes package version 4 (`BlenderPackageManifestBuilder.cs:102-112`) and a static .3D package writes version 2; a keyframe-only corner test fails on a synthetic stack whose corner is collinear only in the keyframe; missing actor-scale diagnostic fails; a non-tiling stack is rejected; the probe recognizes each file exactly once. Blender's native timeline is 32 frames/s (`BlendAnimationAdmission.cs:12`), so the 1/15 s keys land on fractional native frames — exact float scaling by a power of two; the hop-D receipt confirms the readback at those times | A7 on BMANA001, CVFTL001, DOGA001, BLOBA001, BEAMA001 and CV_SKUL3; the DOGA001 control; a GLB plan for each is supported; hop D's clip readback at the 1/15 s key times |
| 7 | Textures and palettes, with typed `ScenePalette` (SA1 shipped; slice 10 folded in) | `Imaging/IndexedPngEncoder.cs`, `ClassicModelCompanions.cs`, `XnGineModelTextures.cs`, `XnGineModelMaterials.cs`, `XnGineModelPalettes.cs` | Encoder round trip through `BMT/Core/Formats/Png/PngImageDecoder.cs` (control: one PLTE entry changed). Solid mapping (control: N instead of 128+N for .001). A multi-frame record and a multi-image BSI entry each become a Derivation (control: a StandardPayload variant fails). Under D9's default, an inspection spy sees no RLE, frame or index decode and no StandardPayload (control: the conversion path under Inspection fails the spy). Palettes: entries equal the expansion of the original bytes (control: one entry changed), an image's `paletteIndex` names the palette its PLTE came from (control: the two Battlespire palettes swapped), `ValidateStructure` rejects an out-of-range index (control), no native row targets a palette (control: one such row moves the Blender package to revision 8). Per-game synthetic textures | A4x: Original SHA against a Python record slicer, Pillow indices of the PNG against a Python decode of the record (control: one index flipped), and the PLTE plus `ScenePalette` entries against a Python expansion of the palette bytes (control: one channel off by one) |
| 8 | Workflow plumbing and archive naming | `BethesdaModelWorkflow.cs`, `BethesdaModelRegistration.cs`, `BethesdaModelReadCache.cs`, `XnGineBsaBackend.cs`, `MeshCommand.cs` | `.BS6` and `.ROB` inputs open as archives (control: a `.bs6` that is not an XnGine BSA falls back). The planner reports no collision for ARCH3D `--all` (control: without the suffix it reports the collision). The composite cache is accepted by both readers. The estimate text names the classic allowance. `bmt.classic-game` is set from a temp install tree and never reaches a NIF item (control: writing it to `bmt.game` makes a NIF read throw) | `mesh info ISLAND.ROB -e GR_COMP.3D`; `mesh convert 3D.BS6 -e BARSTEP1.3D` |
| 9 | Gate-1c harness, before any Shared change | `tools/scripts/gate1c/produce_gate1c.ps1`, `run_gate1c.py`; `RealAsset/Cut1c*Oracle*Tests.cs` | Driver self-checks on hand-built receipts, as in 1a | Hops A2, A3x, A4x, A6, A7, B, C, E and basis on the cover. Known blockers recorded: the G2 rows, the opaque-payload row (SA3) and the `mesh info` point overcount (SA6) |
| 10 | Folded into slice 7 (SA1 shipped in `04ba005`) | None | See slice 7 | See slice 7 |
| 11 | Source point domains (SA6 shipped `c674b2a`) and the G2 gate scope (SA3 ruling) | Reader: `ScenePointIndices(..., sourceDomainId:)` set to the mesh's point-list identity (container, record and point-list offset), shared by every material-split primitive of that mesh and never derived from equal sizes or positions (`XnGineModelGeometry.cs`). Gate: `run_gate1c.py` enumerates the allowed reasons `geometry.source-polygon-topology-omitted`, `geometry.source-point-domain-omitted`, `geometry.source-attribute-storage-omitted`, `source.opaque-payloads-omitted` and `source.palette-provenance-omitted`, each scoped to the rows an XnGine item produces and each named with the reader evidence that carries it (A6 pins the plane walk, triangulation and UV/plane interpretation against the independent probe; A3x pins the normalized triangles against the legacy extractor; A4x pins the pixels and palettes); no blanket Dropped exemption, every loss stays in the report | Two meshes with byte-equal point lists get distinct ids and a primitive built from another point list gets its own (controls); `mesh info` vertices equal the header point count (control: 44005 printing 240 fails); an XnGine `Dropped` row with any reason outside the enumeration fails the gate, and an injected Faces-Dropped row on a NIF item fails it (controls) | Gate receipts |
| 12 | Blender carrier (SA4 shipped `585dd1f`: Int16x2 and UInt16x2 widen to INT32_2D) | None in the reader: `uv16` stays (Int16, 2) with its normalization and signedness declarations | The package reports `uv16` Converted with `AttributeWidened` and the importer reads both components back exactly (control: a (Int16, 2) row classed Degraded fails the expectation) | Hops C and D |
| 13 | Gate 1c (owner run, after gate 1b per D10) | None | Every hop passes and every control fails; Dropped only with later-cut reasons; legacy outputs byte-identical; peak memory recorded per item | Full cover plus decline controls |

## 9 Oracles and gate 1c

| Hop | Oracle | Shared premise | Control that must fail |
|---|---|---|---|
| A6 `.3D` fields | `xngine_probe.py` (from `census_xngine.py`): points, normals, planes, stored u and v, texture keys, header tails, plane-data SHA, LZSS, ROB, ARCH3D, omitted planes; compared with `mesh dump` after the declared negation and winding rule | None | One UV delta byte flipped in a copy |
| A7 `.3DC` poses | `rg3dc_probe.py`: frame table, keyframe, narrow deltas, wide poses, tiling, the pose-independent corner lists; integer poses rebuilt exactly through `PointIndices` | None | One narrow delta bit flipped |
| A2 coverage | An element list from the header counts and tiling, built independently of the reader, with no-triangle planes predicted NativeOnly | None | One element removed from the reader's census |
| A3x geometry | The legacy `XnGineMeshDecomposer` on the same bytes (after /256, /16 and the size), compared **per plane, triangle for triangle**, against the legacy exporter's swapped stream. Exclusions, each documented and counted: the 113 + 223 zero-normal three-corner planes the legacy keeps and the reader omits; the Redguard values the legacy unfolds by accident; and, on .3DC, the n-gons whose pose-independent corner list differs from the keyframe list (at most 45) | Shares `XnGineMesh.Parse`: catches reader errors, not parser errors | One vertex moved; the fan apex moved to c0 |
| A4x textures | Original SHA against a Python TEXTURE.nnn record slicer and a Python BSI reader; Pillow indices of the payload PNG against a Python RLE decode of the record | None | One index changed in the payload |
| B, B', C, E | As in 1a (validator, Python GLB reader, package reader, writer against writer). Hop B also checks that each .3DC clip ends at N/15 and that no primitive is empty | As in 1a | As in 1a |
| D | Readback of welded n-gons, `uv16`, absolute shape keys and packed indexed PNGs, and the morph-weight Step clip at 1/15 s keys | None | One shape-key vertex moved in the package |
| F | Owner-run: orientation (an asymmetric texture), chirality (legible text), palette colors against an independent expansion | None | A deliberately flipped image; a mirrored export; a palette expanded with `<< 2` instead of the bit-replication rule |
| Basis | A synthetic Y-down XnGine record through both writers | None | The reader's negation omitted |

**Gate 1c** has the same shape as gate 1a:
- every hop passes on the cover and every control fails;
- Dropped appears only with later-cut reasons (after SA3, including the opaque-payload row);
- `mesh info` vertex counts equal source points (after SA6);
- `classic mesh export` output is byte-identical for the cover files;
- peak memory is recorded per item.

**Cover** (census-xngine-summary.json; re-derived independently in review_verify_cover.json)
- **Design members:**
  - Daggerfall 44005 (index 0; v2.7, 4,104 B, e72b7147…), 451 (index 5552; v2.6, 81,394 B, e5d2b24a…; takes the unfold and is Tentative) and 41509 (index 8205; v2.5, 21,874 B, c0314b18…);
  - Redguard CRAK0001.3D (4,964 B, 305b9100…; byte-identical to CAVERNS.ROB segment 259) and ISLAND.ROB GR_COMP (segment 0, 1,714 B, 2df3f935…);
  - Battlespire ARMOR.3D (34,556 B, 001e797f…; byte-identical in 3D.BSA #651 and 3D.BS6 #1900);
  - BMANA001.3DC (wide, 70 frames, 769,244 B, 49077836…);
  - DOGA001.3DC (narrow, 38 frames, 328,398 B, a8dddd86…).
- **Proposed:**
  - 3D.BS6 BARSTEP1.3D (#437; 1,540 B, e714112c…; LZSS 569 B);
  - 3D.BSA v2.6 HUTVANE.3D (#2050; 2,086 B, b661c9af…; LZSS);
  - narrow CVFTL001.3DC (v2.7, 13 frames, 8,266 B, f7463fed…).
- **Alternates:** BLOBA001 (2 frames), BEAMA001 (no unaccounted region), CV_SKUL3 (the smallest v2.7 wide file).
- **Decline and edge controls to add:**
  - a .3DC offered to the .3D reader;
  - an fxart v5.0 mesh;
  - the 3D.BSA "MZ" stray;
  - MENU.ROB MENUA001;
  - an empty ROB segment;
  - the id-5090 duplicate group;
  - an extracted ARCH3D record read loose (the +20 = 0 ambiguity);
  - 3D.BS6 ESPEAR (66 omitted planes, one emptied key) and ISLAND.ROB HBBLD01 (an emptied key with a non-zero normal);
  - a Battlespire mesh with a folded fan (picked by M-Z).
- **Billboards:** none (flats belong to cut 2).

## 10 Shared asks

**Status at the second amendment (2026-09-28):** SA1 delivered (`04ba005`), SA4 delivered (`585dd1f`), SA6 delivered (`c674b2a`, verified `cbdbf3d`; the reader passes `sourceDomainId` from real source identity), SA3 answered as a gate-scope ruling rather than a reclassification (the Dropped rows stay; the gate enumerates the reasons it allows, with reader evidence per reason, and exempts nothing else), SA2 not triggered (M-C found no cycling structure on any mesh-referenced container), SA5 satisfied. The foundation also added `SceneBillboard.SourceScale` (`853b6f1`) for the cut-1b scale-sign case, a NIF-reader follow-up outside this cut. The table is the record of what was asked.

| Ask | Exact contract | Why |
|---|---|---|
| SA1 (row 14) | `ScenePalette` (Core/Models) with Name, Encoding {Vga6, Rgb8, X555, Rgba8, ...}, the original bytes with container and offset, the expanded entries, expansion rule id + provenance + evidence, transparent indices + rule provenance, and named auxiliary tables (HTBL ramps, HICL, cycled ranges). Plus: `ModelDocument.Palettes`; an image-to-palette reference on `SceneImageSource` (or on the descriptor); `SceneElementKind.Palette`; `ValidateStructure` range rules; the dump schema; and inspection filling `ModelTextureInfo.Palette` | Design section 5.1. Neither writer needs it to draw, because PLTE carries the colors, but `mesh info -v`, `mesh dump`, RE-10 provenance and any later palette cycling do. Nothing exists at the pin or on main. |
| SA2 (conditional) | A palette-cycling `SceneImageDisplayTransform` (its base constructor is `private protected`) | Only if M-C finds cycled ranges on mesh textures |
| SA3 | A G2 ruling. Reclassify as Metadata or a not-carried class, or scope the gate's Dropped rule to reader coverage: (a) the GLB writer's Dropped rows for `Faces`, `PointIndices` and unselected attributes (`ModelGltfGeometry.cs:506-531` at the pin, `:623-663` at 0152179 — which adds two further Dropped rows, non-Render purpose :654-656 and `ExtrasJson` :660-662, both inert for XnGine); (b) its document-level "source.opaque-payloads-omitted" row (`ModelGltfAdmission.cs:170-171` at the pin, `:226-227` at 0152179) | Every XnGine primitive carries `Faces`, `PointIndices`, `xngine.uv16` and `xngine.plane`, and every XnGine document carries native state. Design section 6.1 allows only later-cut(1b\|1c\|2) Dropped reasons at a gate, so gate 1c cannot pass otherwise. |
| SA4 | Blender attribute mapping of (Int16, 2) and (UInt16, 2) to `INT32_2D` widened, Converted (`BlendAdmission.cs:595-609` at the pin, `:676-690` at 0152179, default arm :689) | `xngine.uv16` in its source encoding is otherwise Degraded. Fallback: BMT stores Int32×2. |
| SA6 (new) | `ModelInfoDocument` counts a mesh's shared point domain once: for the primitives of one mesh that carry `PointIndices`, add their common `PointCount` once, and count primitives without `PointIndices` by vertices as today (`ModelInfoDocument.cs:30`). A ruling that a mesh's `PointIndices` primitives share one domain, with the design's `vertices` wording (`model-document-design-20260923.md:274`) amended to match. | `ScenePointIndices.PointCount` already means the source domain including points a primitive does not use (`ScenePointIndices.cs:36-37`). The per-primitive sum prints points × texture keys for every multi-texture XnGine mesh (240 for 44005, 10,274 for 451, 2,492 for 41509). BMT has no clean fallback: compacting each primitive's domain would renumber source identities and still overcount seam points. |

SA5 (the 1b Blender animation path for morph-weight Step clips) is **satisfied at 0152179** and leaves the asks table: clips are classified per clip through `BlendAnimationAdmission.Unsupported` (`BlendAdmission.cs:1108-1117`; `BlendAnimationAdmission.cs:31-75`; `BlendAnimationCurves.cs:135-141`; `import_model.py:3949-3990, 5867-5884`), and the .3DC Step morph-weight clip is carried natively. The gate no longer accepts a Dropped clip row for .3DC — it requires the carried clip.

No other ask:
- `SceneAnimationTiming` needs only the slice-0 pin bump.
- Indexed PNG stays a BMT encoder; BMT can offer it to Shared later.
- The .3DC clip duration needs no Shared change: the reader's derived final key and a null `DurationSeconds` keep the GLB plan supported. (A clock declaration would also avoid the refusal — the clock stage owns the interval, `ModelGltfAdmission.cs:202-203` at 0152179 — and the plan does not take it.)
- Zero normals and empty primitives need no Shared change. The reader omits every plane that yields no triangle, so on retail data no zero-normal slot and no empty primitive reaches a writer. A synthetic zero-normal plane with area still gets the GLB writer's reconstruction and its Degraded row (`ModelGltfGeometry.cs:193-228`).

## 11 Open questions

The `openQuestions` field lists them all. The ones that change code shape are D1 to D10, plus:
- which palette a loose Redguard mesh binds;
- the chirality of the native frame;
- the definition of the Original payload (record, entry, or image range inside an LZSS entry);
- cull mode and the lighting row for XnGine materials;
- whether folded fans (section 0.2) need a triangulation other than the reference's for GLB's Converted row;
- whether no-triangle planes should also reach Blender as zero-area n-gons (the plan keeps them native-only in both writers).

**Measurements (slice 2, done 2026-09-28; receipts under `TestOutput/cut1c-20260928/slice12/slice2/`).** M-T: header, point list, normal list, plane list and plane data tile with zero overlaps on all 19,725 static meshes, one gap per mesh always at the end of the record, so `unclaimed:*` is the tiling residue and object-data records are variable-length. M-Z: folded-fan control = 3D.BSA CAT02.3D (its 3D.BS6 namesake differs by 67 bytes). M-F: 4 + 19 + 12 retail frame-0 Derivation triggers; no mesh references a multi-image BSI entry; `DaggerfallTextureFile`'s by-name refusal of TEXTURE.215/217 must be scoped to Daggerfall (Redguard's decode, 1,059 planes). M-I: index 0 is rare on mesh textures; the opaque default stands. M-C: no cycling structure; SA2 not triggered. The list below is what they were:
- **M-T:** tiling of static .3D byte areas (defines `unclaimed:*`).
- **M-Z:** per mesh, zero authored normals and folded fans; picks the folded-fan control. (The no-triangle-plane census is done: plan_fix_triangulation.json.)
- **M-F:** planes that reference multi-frame TEXTURE records, multi-image BSI entries, or animated BSI images.
- **M-I:** palette index 0 use on mesh textures (the transparency rule).
- **M-C:** a fold-range census on triangles, where there is no later-corner oracle, plus palette cycling.

## 12 Measured versus assumed

- **Measured (section 0.2):**
  - populations and layouts;
  - plane-header discrimination and the header +20 Daggerfall discriminator;
  - the .3DC shape test and the probe-prefix overflow;
  - duplicate ids and path shadowing;
  - coordinate range;
  - authored normals and zero normals;
  - collinear corners, the reference fan's zero-area and folded triangles, no-triangle planes and emptied texture keys;
  - pose-dependent .3DC corners;
  - the `mesh info` point overcount on the cover;
  - later-corner UV agreement, including its control;
  - .3DC UVs undecoded;
  - wide normal blocks authored, narrow blocks undecoded;
  - companion sizes.
- **Assumed:**
  - all four unit rows;
  - 15 fps with Step, and the derived final key that holds the last pose one period;
  - forward +Z;
  - the unfold (Daggerfall only);
  - the reference UV rule as the portable form;
  - the 64-texel fallback;
  - index 0 opaque;
  - Vga6 bit replication (RE-10);
  - Battlespire x555 solid colors;
  - `art_pal.col` for a standalone Redguard mesh;
  - the 16 MiB source bound and 64 MiB companion allowance.
- **Inferred:**
  - the MENU.ROB +16 value is a frame count;
  - pose-independent corners (any pose keeps it) are the right .3DC rule.
- **Recalled:** Daggerfall Unity's Y negation into Unity's frame.

### Critical files
- `src/BethesdaMultitool/Core/Formats/Xngine/Mesh/XnGineMesh.cs` (with `XnGineMeshDecomposer.cs` as the rule reference)
- `src/BethesdaMultitool/Core/Formats/Redguard/Redguard3dcFile.cs`
- `src/BethesdaMultitool/Core/Modeling/BethesdaModelWorkflow.cs` (with `BethesdaModelRegistration.cs`) and `src/BethesdaMultitool/Core/AssetBrowse/BethesdaBrowseSource.cs`
- `shared/Multitool.Shared/src/Slfx77.Multitool.Core/Models/SceneFaceList.cs` (with `SceneSourceGeometryValidation.cs`, `SceneValidation.cs`, `ScenePointIndices.cs` and `ScenePrimitive.cs`)
- `shared/Multitool.Shared/src/Slfx77.Multitool.Media.Blender/BlendAdmission.cs`, `.../Media/Models/ModelGltfGeometry.cs` and `.../Media/Models/ModelGltfAdmission.cs`, the writer rules that constrain the representation
- `TestOutput/cut1c-prep-20260925/census_xngine.py` and `plan_fix_triangulation.py.txt`, the seeds of A6, A7 and A3x
