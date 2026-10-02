<!-- Implementation plan for the cut-2 Shadowkey model reader (design section 8, "Later cuts": Shadowkey meshes and zones). Drafted 2026-09-28 by a read-only planning pass over the worktree at BMT 25cd386f (its working tree already carries the unapplied cut-2 Starfield staging) with Shared at 2e7af70 (the gitlink, equal to canonical main). The design (docs/design/model-document-design-20260923.md) stays authoritative; where this plan disagrees with it or with the existing readers, section 0.4 says why. Nothing was built, tested or launched; every measurement is one read-only Python process at a time over the retail files. -->

# Cut-2 Shadowkey model reader: implementation plan

I read the design, the cut-1c and cut-2 Starfield plans, the Shared contracts at 2e7af70, every BMT Shadowkey reader and builder, and I measured the whole retail Shadowkey population (all 237 pack slots, all 21 zones, all 1,919 files of the application directory) with an independent Python decoder. I changed nothing under `src`, `tests` or Shared. The staged files are this plan, the probe (`tools/scripts/gate2/shadowkey_probe.py`) and its `.gitignore` allow-list line.

- **Worktree:** `C:/dev/Multitool-worktrees/bmt-material-preparation-20260912`, HEAD 25cd386f. Its working tree carries the Starfield staging (the `tools/scripts/gate2/` block of `.gitignore`, `Core/Modeling/Starfield`), and `staging.diff` is generated against that working tree. Shared is at 2e7af70, canonical main, so this cut needs no pin bump.
- **Data:** resolved as `RealAssetPaths.Travels.ShadowkeyRoot()` resolves it: the first build name (`... Shadowkey (N-Gage - Final)/.../6R51`) does not exist, the second does: `Sample/Builds/The Elder Scrolls Travels - Shadowkey (2004-10-27, N-Gage - Final)/The Elder Scrolls Travels - Shadowkey/system/apps/6r51`. The worktree has no `Sample` directory, so the runs set `BETHESDA_TEST_DATA_ROOT=C:/dev/Multitool/BethesdaMultitool` and the probe found the tree through `<root>/Sample/<relative>`, the order `SampleCandidates` uses.
- **Endianness and license:** everything here is little-endian (N-Gage, Symbian on ARM). The only reference is the retail bytes; the probe is clean-room code and reads no game code.
- **Path prefixes:** `BMT/` = `src/BethesdaMultitool/`, `SK/` = `BMT/Core/Formats/Travels/Shadowkey/`, `SH/` = `shared/Multitool.Shared/src/`, `T/` = `tests/BethesdaMultitool.Tests/`, `R/` = `TestOutput/cut2-shadowkey-20260928/receipts/`, `P` = `tools/scripts/gate2/shadowkey_probe.py` (staged).
- **Labels:** **(measured)** has a receipt in R/; **(assumed)** is a chosen value awaiting RE or an owner ruling; **(inferred)** is reasoning not checked against data.

## Receipts (R/)

| File | Holds |
|---|---|
| `census/census-summary.json`, `pack-slots.json`, `zones.json`, `files.json`, `census.log` | `P census`: every slot decoded with its facts and SHA-256, every zone file inflated, parsed and cross-checked, and every file of the tree under the three probe rules. 20 s, one process. |
| `measure/measurements.json`, `measure.log` | `P measure`: the sky texel encoding with its control, the mesh-to-zone orientation (10 hypotheses), floor contact (6), winding, sequence rates, the weapon records. |
| `plan_measure.py`, `.json` | Rate spread under both readings; the UV vertex domain; `ModelSceneComposer` budgets per zone for the D6 options; yaw classes. |
| `plan_measure_uv_domain.py`, `.json` | Every UV index owned by exactly one vertex (records and skies), with the transposed control. |
| `plan_measure_orientation.py`, `.json` | The full 16-map family: 2 chiralities x 4 fixed offsets x 2 yaw signs. |
| `plan_measure_chirality.py`, `.json` | The per-placement duel between the two leading maps. |
| `plan_measure_key.py`, `.json` | Whether the fog table pins 4:4:4 magenta, and whether the light table pins the palette key. |
| `plan_measure_cover_alternatives.py`, `.json` | Cover sizes at coverage order 1, 2 and 3, and one row per observed cell. |
| `plan_skins_png.py`, `skins/*.png` | Eight skins decoded top row first, read by eye for orientation. |
| `cover/cover-manifest.json`, `cover-report.txt` | `P cover`: the pairwise joint covers (exact MILP), edge rows, decline controls, SHA-256 pinned. |
| `verify/verify.json`, `selfcheck/selfcheck.json` | `P verify`: 168 pins re-read, 0 mismatches, the altered-digit control detected. `P selfcheck`: 19 synthetic checks, 0 failed. |
| `dump-slot175-arrow.json`, `dump-zone-raiders.json` | Sample A1 oracle output. |

## Decisions this plan asks the owner for (all before slice 3)

| # | Decision | Recommendation |
|---|---|---|
| D1 | Inputs and identity | **Three inputs.** (a) `models.huge` joins the archive probe chain (`.huge` name gate plus a sibling `models.idx` that tiles it exactly; it claims 1 of the 1,919 files), entries named `NNN_<models.txt name>` (for example `022_male_long_tunic.bin`, `NNN.bin` without `models.txt`), all 237 slots listed, the 11 empty ones as zero-length entries that classify NotAModel. (b) A loose `.bin` (an extracted slot) through the same mesh probe. (c) A zone by its `.zmp`, companions by stem plus the globals. Why: the slot index is the identity (7 names repeat over 14 slots; 9 slot pairs hold identical bytes), and `.ent` rows reach a slot through `entities.txt`, never through a name. **Alternative:** omit the empty slots from the listing. |
| D2 | Mesh vertex domain | **The UV list is the primitive's vertex domain.** Vertices in stored UV order; triangles are the stored UV index triples; `ScenePointIndices(vertexCount, owner vertex of each UV)` carries the vertex identity; positions are frame 0 of the owner, exact integers. Why: on 226 of 226 records every UV index belongs to exactly one vertex and every UV index is used (0 of 36,109 unused), while the transposed claim holds on 0 records (the control), so both source index spaces survive exactly with no welding by value and no invented identity. Unrolling to corners (the legacy `ToUnrolledTriangles`) would make 38,052 vertices and lose the sharing. |
| D3 | Skin images | **Original texels plus an indexed PNG, no `ScenePalette`.** `SceneImageSource` with Original = the exact texel block (u16 LE 0x0RGB), a 16 bpp Rgb descriptor with pixel format `bmt.shadowkey`/`rgb444-le`, and a StandardPayload: an 8-bit indexed PNG whose PLTE lists the skin's distinct texels in first-appearance order, each nibble x17. Why: lossless (top nibble clear on all 1,302,848 texels, at most 233 distinct per skin, x17 inverts exactly), and a direct-color source has no palette to declare; a fabricated one would claim a source object. **Alternative:** truecolor PNG. |
| D4 | Skin alternatives | **An exclusive layer-set group.** 19 records carry 2 to 19 skins (319 in all). One node and one mesh per skin, group `shadowkey.skin`, skin 0 default-on; the per-skin primitives are rebuilt but share the `SceneMorphTarget` objects. GLB writes the default-on member and lists the rest as Metadata (`SH/Slfx77.Multitool.Media/Models/ModelGltfLayers.cs:386-396`); Blender makes hidden collections. **Alternative:** skin 0 only, the rest as images plus native state; or ask SA-K1. |
| D5 | Animation | **All 33 animated records carry clips; 0 of the 33 legacy declines remain declines.** Morph targets are frames 1..N-1 as `AbsolutePositions` over the UV domain (exact integers). One `SceneAnimation` per sequence (209), one `SceneMorphTrack` per skin node, Step, key times i/rate plus a derived final key at length/rate, `DurationSeconds` null, `SceneAnimationTiming(fps = rate, rawRate = rate, rawRateUnit "shadowkey.sequence.rate", Assumed)`. Static records keep their single `(0, 1, 1)` or `(0, 1, 10)` sequence as native state. Section 3.5 names the six gaps that stay (rate unit, interpolation, loop policy, actor assembly, skin choice, two suspect rates). **Alternative:** decline argonian's two rate-1 sequences (16 s and 22 s under the fps reading). |
| D6 | Zone document route | **`ModelSceneComposer` over placement documents.** One terrain document, one sky document (`isSky` presentation), and per `.ent` row a placement of a static document for its slot (frame 0, no targets, no clips, at most 2 diagnostics), cached per slot so the composer shares meshes, materials, images and palettes by reference. Measured budgets: the worst zone (raiders) has 1,072 placements (limit 4,096) and 9,851 rows (limit 65,536); 2 diagnostics per placed document give 2,144 (limit 4,096; 4 would give 4,288). Why static: the zone authors no animation (an `.ent` row names an entity, not a sequence; scripts drive it), and clips per placement would put 1,582 clips into raiders. **Alternative:** clips per placement (fits: 11,024 rows). |
| D7 | Units and basis | **1/512 m per unit (0.5/256, exact in binary) for mesh, sky and zone, Assumed (RE-6).** The zone is expressed in mesh units, so every coordinate is an exact integer (tile corner x·256 and y·256, heights raw 8.8, placements raw 24.8). Mesh and sky: up +Y, forward +Z, right-handed, Assumed. Zone: up +Z, forward -Y, right-handed, Assumed. Placement = scale(RawScale/256), then (x, y, z) to (x, -z, y), then yaw by -2π·Angle2/65536, then translate(raw). Measured: the yaw sign, the binary-angle unit, mesh +Z to zone -Y and mesh +Y to zone +Z; **not measured:** the x mirror (section 4.4). |
| D8 | The sky | **The `.zsk` payload is a mesh record; one decode path.** `ShadowkeyMesh.Parse` gains a texture-header variant (counted `(1, 256, 256)` on the 12 outdoor skies, uncounted `(256, 256)` on the 9 interior ones); the image is **256x256 0x0RGB, not 512x256 indices through the `.pal`**; the "footer" is the sequence table. The sky document is a mesh document; the zone places it with `SceneNodePresentation(isSky: true)` through the basis map alone, at unit scale. |
| D9 | Tile texturing | **Untextured tiles in cut 2**, one neutral material per face kind, a diagnostic, and the texture bank carried as one material per `.sur` row bound to its `.ztx` image and referenced by no primitive (the GLB writer emits every image and material, `SH/Slfx77.Multitool.Media/Models/SceneGltfBuilder.cs:201-230`). Why: which surface a floor or ceiling uses is unsolved; the wall slots are measured (four edge directions x two bands, 0.94; BMT measurement, `SK/ShadowkeyCellPrototype.cs`) but their UV-window fields are hypotheses. **Later slice:** walls from slots, after its own A-hop. |
| D10 | Zone palettes and tables | **A typed `ScenePalette` per zone** (Rgb8, container `<stem>.pal`, the 768 bytes, entries with alpha 255, expansion rule `shadowkey.pal.rgb8` Authored), **transparent index 6 on the 13 zones whose palette has magenta** (rule `shadowkey.pal.light-table-key`, Assumed: the `.zlu` pins that entry to 0x0F0F at all 4 x 64 levels in 13 of 13), **the `.zlu` as its auxiliary table**, the `.zfg` as native state (it maps 4:4:4 colors, not palette entries). `.ztx` images: Original = the 16,384 stored index bytes (bottom row first), StandardPayload = an indexed PNG top row first with the zone palette in PLTE (tRNS only where the rule is declared), `PaletteIndex` bound. |
| D11 | Probes | `bmt.shadowkey.mesh` (a bounded structural walk, section 5.1) and `bmt.shadowkey.zone` (the `.zmp` envelope, name and grid arithmetic, section 5.2), plus the archive probe for `models.huge`. Measured: 205 slots Confirmed, 21 Tentative (larger than 64 KiB), 11 NotAModel (empty); every one of the 1,919 files NotAModel under the mesh probe; exactly the 21 `.zmp` files claimed by the zone probe. |
| D12 | Cover depth | **Pairwise joint covers:** 17 slots (91 requirements) plus 5 edge rows = 22 rows, 1,534,782 bytes; 7 zones (55 requirements) plus 2 edge zones = 9. Hops A1 to A3 run on the whole population (it is small); B, C and E run on the covers. For comparison: order 1 needs 11 slots and 6 zones, order 3 needs 20 and 8, one per observed cell 68 and 20. |
| D13 | Legacy corrections | **Fix the legacy sky decode and the legacy placement map in slice 0**, with their pinned tests re-derived (section 0.4). The viewer's sky today draws a 256-texel-wide 4:4:4 image as 512 palette indices, and its placements use (x, y, z) to (x, z, y) with +yaw, which scores 21,017 blocked vertices against 7,687. |
| D14 | Order against other tracks | Slices 0 to 8 touch only `SK/`, the new `BMT/Core/Modeling/Shadowkey/`, a new archive backend and their tests. Slice 9 edits `BethesdaModelRegistration.cs`, `BethesdaModelWorkflow.cs` and `MeshCommand.cs`, which cut-1c slice 8 and Starfield slice 6 also edit: it goes after both. Gate 2 (Shadowkey) runs after gate 1c. |

---

## 0 Baseline facts

### 0.1 BMT today

**Readers** (`SK/`, all measured-layout readers from 2026-09-05 to 09-08)
- `ShadowkeyMesh` (record walk; `DecodeSkin` builds a first-appearance palette; `ToUnrolledTriangles`), `ShadowkeyModelPack` (idx tiling, `models.txt`), `ShadowkeyTextTables` (`entities.txt`, model lists, `Resolve`).
- Zone: `ShadowkeyCompressedFile` (envelope), `ShadowkeyZoneMap` (`.zmp`), `ShadowkeyCellPrototypes` (`.zcp`), `ShadowkeyZoneFiles` (`.zon .stn .pth .sur .ent .sta`), `ShadowkeyTextureBank` (`.ztx`, rows reversed), `ShadowkeyZonePalette` (`.pal`), `ShadowkeyLightTable` (`.zlu`, `Synthesize` reproduces it), `ShadowkeyFogTable` (`.zfg`), `ShadowkeySkybox` (`.zsk`).
- Measured constants in their docs that this plan keeps: `.zcp` corner order (slot 0 = (x, y+1), 1 = (x+1, y+1), 2 = (x+1, y), 3 = (x, y), 98.13% against 58.78%), mesh unit = 1/256 tile, sky +Y up, sky UV = value/65536, `.ztx` rows bottom-up, the raiders sky UV table (35 pairs against 44).

**Builders and adapters**
- `ShadowkeySceneBuilder` (viewer, `ShadowkeyAxisConvention.ZUp` maps (x, y, z) to (x, z, y)), `ShadowkeyZoneSceneBuilder` (floors, ceilings, walls, risers and downstands by rule; placements baked with the same swap and a yaw of +Angle2), `ShadowkeySkySceneBuilder` (512x256 image through the zone palette, shell scaled to enclose the zone), `ShadowkeyTileMaterial` (the debug resolver; texturing "NOT solved").
- `ShadowkeyNeutralSceneAdapter`: one frame of one mesh as a `ModelDocument` ("shadowkey"), unrolled corners, flat normals, Repeat sampler, unlit, opaque unless the caller opts into the magenta key; it declines every record with more than one frame and a sequence (the "33 animation declines").

**Model plumbing.** No Shadowkey reader is registered (`BMT/Core/Modeling/BethesdaModelRegistration.cs`). `BethesdaModelWorkflow.IsArchive` knows `.bsa` and `.ba2` (`:605-610`; cut 1c adds `.bs6` and `.rob`). The Shadowkey game profile's units are the viewer's (`BMT/Core/Games/GameProfiles.cs:537-553`), not a design row. Classic unit rows live in `BMT/Core/Modeling/Units/ClassicModelUnits.cs` (a cut-1c file this plan does not stage).

**Tests.** `T/Core/Formats/Travels/Shadowkey*Tests.cs` (pack, zone map, zone files, zone scene, sky scene, each with a retail twin), `T/Core/Formats/Travels/Shadowkey/ShadowkeyNeutralSceneAdapterTests.cs`, `T/Core/AssetBrowse/ShadowkeyPackPreviewSourceTests.cs`, `T/Core/Formats/Classic/ShadowkeyRecordSource*Tests.cs`.

### 0.2 Corpus facts (measured)

**Pack** (`census-summary.json`, `pack-slots.json`)
- `models.idx` 1,900 B (237 slots) tiles `models.huge` 4,907,880 B exactly; 11 empty slots (19, 24, 26, 28, 29, 54, 57, 133, 221, 227, 236); 226 records, all walking to their last byte.
- 193 static (1 frame), 33 animated (2 to 200 frames, 3,574 frames in all). Record sizes 678 B (`arrow.bin`) to 257,784 B (`male_long_tunic.bin`); 21 exceed the 64 KiB probe budget.
- Textures: 64x64 165, 32x32 31, 16x16 13, 128x128 12, 32x64 2, 128x64 1, 64x32 1, 8x8 1. 19 records carry several skins (319 skins in all, at most 19). All 1,302,848 texels have a clear top nibble; at most 233 distinct colors per skin; 44 records contain magenta 0x0F0F.
- **Every record carries at least one sequence** (402): the 193 static records carry exactly one, `(0, 1, 1)` on 185 and `(0, 1, 10)` on 8. The 33 animated records carry 209 (195 multi-frame, 14 single-frame); sequences never overlap and cover every frame (0 frames outside a sequence).
- Rates: 1 (187), 3 (26), 4, 5 (3), 6, 7, 10 (176), 11 (2), 14, 15 (2), 17, 29. On the 195 multi-frame sequences, **8 have rate equal to their length** (desk, fortifyingcrystal, portcullis, sarcophagus and all four of spider's), which is exactly 1 s under a frames-per-second reading and length² ticks under a ticks-per-frame reading. Durations under fps: median 2.1 s, p90/p10 4.58; under ticks-per-frame: p90/p10 9.70. Two sequences (argonian `[119, 135)` and `[135, 157)`, rate 1) run 16 s and 22 s under fps.
- **The humanoid table.** 12 records share one 11-sequence table (`(0,1,10) (1,22,10) ... (138,144,10)`) or its first 9 entries: the four tunic bodies, delfran, orc_ice_warrior, and the six weapon records (sword, mace, two daggers, bow, ax), which are 144-frame keyframe records that play in lockstep with a body.
- Geometry: every UV index belongs to one vertex (226/226; control 0/226); 3 records have unused vertices; 8 have triangles with repeated positions (`arrow.bin` among them), 9 duplicate triangles, 0 repeated-index triangles; 22 records have UVs past the texture size (up to 7.875x); 46 UV values are not multiples of 1/8 texel. Largest |coordinate| 5,590; largest frame-to-frame delta from frame 0 1,873.
- **Winding:** 79 records are closed; 75 have positive signed volume under a right-handed reading with (v0, v1, v2) order, that is counter-clockwise when seen from outside; the other 4 are `door_5units_right`, `door_7units_right`, `woodenbucket`, `ax`.

**Zones** (`zones.json`)
- 21 zones, 20 at 128x128 and ffarena at 64x64; every `.zmp` name equals its stem; every compressed file inflates to its declared length with nothing after the stream (126/126).
- Prototypes 99 (raiders) to 11,828 (azra), all referenced; 0 surface slots outside the `.sur` range; in 11 zones a slot value reaches or passes the texture count (so slots are not texture indices).
- 326 textures (6 to 22 per zone), tiling exactly; the `.sur` texture index is the last byte (max = count - 1 in 21/21).
- Palettes: 7 distinct; magenta at index 6 in 13 zones; 7 distinct light tables, 5 distinct fog tables (black in 14 zones, blue-grays in 7).
- Placements: 8,258 (41 to 1,072 per zone), all resolving to a resident slot; 3 outside the grid (delfhide), 17 on a blocked cell (cell = floor(raw/256)); 1,611 resolve to an animated record (at most 167 per zone); Angle2 non-zero on 3,975 (4,348 exact quarter turns including 0); Angle0 non-zero on 232 and Angle1 on 204 (384 placements with either); RawScale other than 256 on 958; 41 names fill all 8 bytes without a NUL; the alignment hole is 0xCCCC on all.
- 256 trigger rectangles, 7 paths (86 points), 59 lock rows, azra's 201 `.sta` records.
- Terrain under the builder's rule: 248,001 floors, 248,001 ceilings, 50,446 full walls, 19,724 risers, 10,931 downstands: 577,103 quads, at most 35,672 in one zone (dstar_w).

| Zone | Grid | Open | Protos | Tex/Sur | Placed (animated) | Sky | Key | Fog | Quads |
|---|---|---|---|---|---|---|---|---|---|
| azra | 128x128 | 15,659 | 11,828 | 21/23 | 282 (68) | out, painted, rate 10 | 6 | black | 32,800 |
| broken1 | 128x128 | 11,890 | 448 | 19/21 | 278 (80) | in | 6 | black | 27,630 |
| broken2 | 128x128 | 8,733 | 209 | 18/18 | 162 (41) | in | 6 | black | 19,953 |
| crypt1 | 128x128 | 12,610 | 1,350 | 17/17 | 438 (70) | in | 6 | black | 30,914 |
| crypt2 | 128x128 | 11,009 | 341 | 16/18 | 292 (89) | in | 6 | black | 26,482 |
| crypt3 | 128x128 | 3,299 | 270 | 18/17 | 129 (32) | in | 6 | black | 8,504 |
| delfhide | 128x128 | 10,744 | 1,799 | 18/21 | 760 (97) | out | 6 | black | 27,198 |
| drgnfld | 128x128 | 15,453 | 4,725 | 15/15 | 321 (38) | out, painted | 6 | (10,10,12) | 31,880 |
| dstar_e | 128x128 | 13,325 | 897 | 22/21 | 715 (167) | out, painted | none | (10,10,12) | 35,309 |
| dstar_w | 128x128 | 13,196 | 1,946 | 19/21 | 1,045 (130) | out, painted | none | (10,10,12) | 35,672 |
| erthcave | 128x128 | 8,144 | 5,782 | 15/14 | 237 (79) | in | 6 | black | 19,760 |
| fearfrst | 128x128 | 15,472 | 6,982 | 10/14 | 221 (99) | out | none | black | 35,120 |
| ffarena | 64x64 | 1,876 | 965 | 7/7 | 41 (10) | in | none | black | 4,450 |
| ghstpass | 128x128 | 15,324 | 10,303 | 19/20 | 315 (32) | out, painted | 6 | (10,10,11) | 31,675 |
| glaciercrawl | 128x128 | 15,049 | 182 | 6/7 | 210 (73) | out, painted, rate 10 | none | (10,10,11) | 33,189 |
| lakvan | 128x128 | 12,971 | 1,057 | 12/15 | 755 (100) | out | none | black | 33,449 |
| lothcav | 128x128 | 11,776 | 2,703 | 8/9 | 375 (139) | in | none | black | 27,556 |
| raiders | 128x128 | 12,445 | 99 | 12/12 | 1,072 (137) | out | none | black | 31,297 |
| snowline | 128x128 | 15,346 | 6,243 | 19/27 | 108 (37) | out, painted | 6 | (9,9,11) | 33,233 |
| stouttp | 128x128 | 15,296 | 8,245 | 14/14 | 81 (28) | out, painted | 6 | (8,8,9) | 31,405 |
| twilite | 128x128 | 8,384 | 455 | 21/20 | 421 (65) | in | 6 | black | 19,627 |

**The sky** (`measurements.json` "sky_encoding", `zones.json`)
- An inflated `.zsk` walks as a mesh record: 12 outdoor payloads (30 vertices, 168 corners, 56 faces) carry the texture header `(1, 256, 256)`; 9 interior payloads (98, 145, 192) carry only `(256, 256)`; both end on a one-row sequence table `(0, 1, 1)` (19) or `(0, 1, 10)` (azra, glaciercrawl). Every corner index belongs to one vertex on 21/21.
- **The image is 256x256 0x0RGB.** On all 8 painted skies the top nibble of every u16 is clear (65,536 of 65,536 each), the odd bytes never exceed 15, and horizontally adjacent pixels differ by 1.27 to 3.07 per channel under 4:4:4 against 6.45 to 72.50 through the zone palette at 512 wide. Control: the 326 `.ztx` textures, which are 8-bit indices, read as u16 have a clear top nibble on 0.001% to 30.0% (dstar_w the highest), never 100%.
- Consequently the corner UVs are 8.8 texels of a 256-wide texture, the mesh convention: value/256/256 = value/65536, the number BMT already uses.

**Orientation, contact and keys** (`measurements.json`, `plan_measure_*.json`)
- Blocked vertices (frame-0 vertices of every placed mesh landing in a blocked or off-grid cell, over 8,258 placements; lower is better): (x, -z, y) with -yaw 7,687; (-x, -z, y) with -yaw 8,431; the next map 13,819; the legacy (x, z, y) with +yaw 21,017; no yaw 21,285 and 22,199; the degrees-x-256 angle reading loses to the binary reading in every family (10,236 against 7,687 for the leader).
- The two leaders differ only by mirroring mesh x. On the 499 placements where they disagree they win 248 (proper) against 251 (mirror); by object `weaponrack` favors the proper map 85:1 and `torch` the mirror 150:48. **The x mirror is not settled.**
- Floor contact within 1/8 tile (mesh base at factor f along +Y or -Y, plus the placement height, against the cell's floor band): f = 1, +Y 2,025 of 8,255; every other factor and sign at most 1,070.
- The fog table follows its blend formula for 0x0F0F at all 16 levels in 21/21 zones (it does not pin magenta), while the light table pins the palette's magenta entry to 0x0F0F at every bank and level in 13/13 zones.

**Probe** (`files.json`, the census's probe columns)
- Slots: 205 Supported Confirmed, 21 Supported Tentative, 11 NotAModel (zero bytes).
- Files (1,919: 1,535 `.s`, 70 `.txt`, 38 `.wav`, 21 of each zone family, the executable, the packs): the mesh probe refuses all 1,919 (header constants 1,889, shorter than 14 bytes 29, and 1 on the sequence count against the length, which is `models.huge` read as one record). The zone probe claims the 21 `.zmp` files, Confirmed, and refuses the other 105 compressed zone files (name field 101, grid size 2, grid arithmetic 2). The archive probe claims `models.huge` only. The outdoor sky payload, once inflated, is Tentative under the mesh probe and the interior one NotAModel; no file on disk is either.

### 0.3 Contract facts at 2e7af70 that bind the readers

- **Palettes (SA1).** `ScenePalette` (`SH/Slfx77.Multitool.Core/Models/ScenePalette.cs`): encoding {Unknown, Vga6, Rgb8, X555, Rgba8}, original bytes with a container, expanded entries, an expansion rule and a transparency rule (`ScenePaletteRule`: id, provenance, evidence), ordered auxiliary tables (`ScenePaletteTable`). Images bind through `SceneImageSource.PaletteIndex` (`SceneImageSource.cs:19-29, 119-120`), validated in range. Both writers emit one Dropped row `source.palette-provenance-omitted` per palette (`SH/Slfx77.Multitool.Core/Models/Export/ScenePaletteFidelity.cs`; `ModelGltfAdmission.cs:228`; `BlendAdmission.cs:57`).
- **Point domains (SA6).** `ScenePointIndices(pointCount, values, sourceDomainId)` (`ScenePointIndices.cs`): unused points stay in `PointCount`; a domain id shares points only within one mesh. GLB drops the domain with `geometry.source-point-domain-omitted` (`ModelGltfGeometry.cs:628-630`).
- **Morphs and clips.** `SceneMorphTarget.AbsolutePositions` (`SceneMorphTarget.cs`); GLB lowers absolute targets to relative, Converted when the difference is an exact integer (`ModelGltfGeometry.cs:561-600`); Blender writes them Exact (`BlendAdmission.cs:810-812`). `SceneMorphTrack` supports Step; Blender's native animation path accepts Step on `MorphWeights` (`BlendAnimationAdmission.cs:54-68`, native rate 32 fps at `:12`). `SceneAnimationTiming` records fps, raw rate, raw unit and provenance without rescaling seconds. GLB refuses a clip whose authored duration differs from its last key (`ModelGltfAdmission.cs:205-224`), hence the derived final key and a null duration, as cut 1c's `.3DC` clip does.
- **Composition.** `ModelSceneComposer.Compose` (`Composition/ModelSceneComposer.cs:11-15`: 4,096 placements, 65,536 new rows, 16 Mi references) shares meshes, materials, images, samplers and palettes per `ModelDocument` reference and copies nodes, one helper root per source root, scenes, clips, layer sets, native states and **diagnostics per placement** (`ModelCompositionBuilder.cs:46-120`); a document holds at most 4,096 diagnostics (`ModelDocument.cs`) and 256 MiB of native payload. The output units and basis are the caller's; placement transforms must carry any unit or basis change.
- **Sky presentation.** `SceneNodePresentation(isSky: true)`; GLB reports `presentation.static-source-pose`, Degraded (`ModelGltfAdmission.cs:118-121`); Blender keeps it as `mt_presentation` (`BlendAdmission.cs:940`).
- **Layer sets.** One default-on per exclusive group; GLB draws the default-on member and records the rest as Metadata (`ModelGltfLayers.cs:386-396`).
- **Buffers.** `ScenePrimitive.WithMaterialIndex` is internal (`ScenePrimitive.cs:80`), so BMT cannot share one primitive's buffers across materials; `WithTriangleIndices` is public (`:90`).
- **Images.** The GLB builder writes every document image and material in source order, referenced or not (`SceneGltfBuilder.cs:201-230`).
- **Provenance vocabulary.** `SceneValueProvenance` is {Unknown, Authored, ReverseEngineered, Assumed}; there is no "measured", so measured-but-not-RE'd facts are Assumed with the measurement in the evidence text.
- **Probe budget.** 64 KiB, content only, `IsComplete` and the declared `Length` (`Sources/ModelSourceCandidate.cs`).

### 0.4 Where existing text or code needs correcting

- **The sky image.** `ShadowkeySkybox` reads a 512x256 8-bit image through the zone `.pal` and calls the 4- or 6-byte gap "opaque". Both are wrong (section 0.2): the gap is the mesh texture header and the image is 256x256 0x0RGB. `ShadowkeySkySceneBuilder` therefore draws noise at double width, and the retail test that "proves" 512 columns (`T/Core/Formats/Travels/ShadowkeySkySceneRetailTests.cs:142-186`, "the right half differs from the left") cannot discriminate: the two halves of a 512-byte row are the two halves of a 256-texel row under either reading. The UV conclusion (value/65536) survives; its reason changes. The memory note `shadowkey_viewer_geometry_constants` item 4 should be amended by the main session.
- **The placement map.** `ShadowkeyZoneSceneBuilder.PlacementTransform` and `ShadowkeySceneBuilder.Orient` map (x, y, z) to (x, z, y), a reflection, and yaw by +Angle2. The yaw sign and the forward axis are now measured (-yaw, mesh +Z to zone -Y); the doc comment's "SIGN is a display choice with no evidence" is superseded.
- **The 33 declines.** `ShadowkeyNeutralSceneAdapter`'s "sequence rates are in unrecorded units, so a clip cannot be timed" becomes an Assumed timing with evidence (D5); the adapter retires once the reader carries its callers (slice 4).
- **The `ShadowkeyMapCell` doc** says placements sit on blocked cells "12 times in 8,255"; under cell = floor(raw/256) the census counts 17 (1 to 4 in 7 zones). Re-derive the rule before quoting either number.
- **The design's units row** stands (0.5/256, Assumed, RE-6) and gains the floor-contact evidence; the timing row stands and gains the rate-equals-length evidence.

---

## 1 Decoder strategy

**Reuse the `SK/` readers with additive members; build documents in new `BMT/Core/Modeling/Shadowkey/` code.** The Python probe stays the independent decoder and never consumes C# results.

| Component | Verdict |
|---|---|
| `ShadowkeyMesh.Parse` | Reuse. Add a `ShadowkeyTextureHeader` argument {Counted, Uncounted} (default Counted) and the section offsets (for coverage and native state). |
| `ShadowkeySkybox` | Re-express on `ShadowkeyMesh.Parse` (the variant chosen by the payload length, as `P sky_variant` does); keep its public surface where callers need it; the image becomes a 256x256 skin. |
| `ShadowkeyModelPack`, `ShadowkeyTextTables`, `ShadowkeyZoneMap`, `ShadowkeyCellPrototypes`, `ShadowkeyZoneFiles`, `ShadowkeyTextureBank`, `ShadowkeyZonePalette`, `ShadowkeyLightTable`, `ShadowkeyFogTable`, `ShadowkeyCompressedFile` | Reuse unchanged. |
| `ShadowkeyZoneSceneBuilder` | The face rule (floor, ceiling, wall, riser, downstand, corner offsets) moves into a shared static helper both the viewer and the terrain document call, so the rule lives once; the placement transform moves to `ShadowkeyModelUnits.PlacementMatrix` and the viewer calls it. |
| `ShadowkeyNeutralSceneAdapter` | Retire after slice 4 (its callers move to the reader). |
| `P` | Seed of hops A1, A2 and A3 for both formats; its tile rule is an independent re-implementation, as cut 1c re-implemented triangulation. |

---

## 2 File blueprint

All files are BMT-owned. No Shared file changes under the recommendations.

`BMT/Core/Modeling/Shadowkey/`:
- **`ShadowkeyMeshModelReader.cs`:** `IModelSourceReader` plus `IModelSourceFormatMetadataProvider`, `FormatId` `bmt.shadowkey.mesh`, `SupportsInspectionWithoutPixelDecoding` true (PNG encoding happens only on conversion reads), `MaximumSourceBytes` 1 MiB (assumed; the largest record is 257,784 B).
- **`ShadowkeyMeshModelProbe.cs`**, **`ShadowkeyZoneModelProbe.cs`:** sections 5.1 and 5.2.
- **`ShadowkeyMeshDocumentBuilder.cs`:** one builder, three shapes (`ShadowkeyMeshDocumentShape` {Standalone, Placement, Sky}): Standalone carries skins, targets and clips; Placement is frame 0 with no targets, no clips and at most 2 diagnostics; Sky is Standalone without clips.
- **`ShadowkeyMeshModelGeometry.cs`** (UV domain, point indices, targets), **`ShadowkeyMeshModelImages.cs`** (skins, `.ztx`, zone palettes), **`ShadowkeyMeshModelAnimation.cs`** (clips), **`ShadowkeyMeshModelLayers.cs`** (skin alternatives).
- **`ShadowkeyZoneModelReader.cs`** (`bmt.shadowkey.zone`), **`ShadowkeyZoneTerrain.cs`** (the terrain document), **`ShadowkeyZoneComposition.cs`** (placements through `ModelSceneComposer`).
- **`ShadowkeyModelCoverage.cs`**, **`ShadowkeyModelNativeState.cs`**, **`ShadowkeyModelDiagnostics.cs`**.
- **`ShadowkeyModelUnits.cs`:** the unit row (1/512, Assumed, evidence), the two bases, the placement matrix, the `--game` check. Owned here rather than in `Core/Modeling/Units/ClassicModelUnits.cs` so this track does not touch a cut-1c file; the row can move there after cut 1c lands.
- **`ShadowkeyModelFormatMetadata.cs`:** catalog entries for both formats (variants: static record, animated record, multi-skin record, sky payload counted and uncounted; the unit row; the admission rules of this plan).

`BMT/Core/Formats/Archives/ShadowkeyPackBackend.cs` plus its `ArchiveProbe` entry (D1).

**Changed files:** `SK/ShadowkeyMesh.cs`, `SK/ShadowkeySkybox.cs`, `SK/ShadowkeySkySceneBuilder.cs`, `SK/ShadowkeyZoneSceneBuilder.cs`, `SK/ShadowkeySceneBuilder.cs` (slice 0); `BethesdaModelRegistration.cs` (main session, `IMPLEMENTATION.md`), `BethesdaModelWorkflow.cs` (`.huge` as an archive, the memory estimate), `CLI/Commands/Mesh/MeshCommand.cs` (`--game shadowkey`) in slice 9.

**Tests**
- `T/Helpers/ShadowkeyTestBuilder.cs`: an independent writer of records (static, animated, multi-skin, counted and uncounted texture headers), packs, `.zmp`/`.zcp`/`.ent`/`.pal`/`.zsk` sets.
- `T/Core/Modeling/Shadowkey/*`: probe, units, geometry, images, palettes, layers, animation, terrain, composition, coverage, native state, format metadata.
- `T/Core/Modeling/RealAsset/Cut2Shadowkey*.cs`: Bucket-B tests with `BucketBTestGuard.SkipUnlessEnabled()`, `[Trait("Category", BucketBTestGuard.Category)]` (as `ShadowkeyPackRetailTests` does) and `[Collection(SequentialIntegrationGroup.Name)]`, resolving through `RealAssetPaths.Travels.ShadowkeyRoot()`.
- `T/Core/Modeling/Samples/cut2-shadowkey-cover-manifest.json`: the cover (section 9), in the cut-1c manifest shape.

**Oracles** (never part of the build): `tools/scripts/gate2/shadowkey_probe.py` (staged now) and, in slice 10, `tools/scripts/gate2/shadowkey_gate2.py` (A1 to A3 comparators and the driver) and `shadowkey_produce_gate2.ps1`, each with its `.gitignore` line.

---

## 3 Mesh mapping

### 3.1 Document

- `ModelDocument("bmt.shadowkey.mesh", "<NNN> <name>", ...)`, one scene. `SourceIdentity` `models.huge#<NNN>`; `SourceProvenance` = the entry path and the SHA-256 of the slot bytes.
- **One skin:** node 0 `mesh` carries mesh 0. **Several skins (D4):** node 0 is a Transform root whose children are `skin00` to `skinNN`, each carrying its own mesh; layer sets `skin00`.. in exclusive group `shadowkey.skin`, `skin00` default-on, source kind `bmt.shadowkey.skin`.
- `Units`: `SceneUnits(1.0 / 512, Assumed, evidence)` (section 5.3). `SourceBasis`: up (0, 1, 0), forward (0, 0, 1), right-handed, Assumed.
- One sampler, Repeat on both axes (UVs reach 7.875x the texture). One material per skin: base color (1, 1, 1, 1), the skin image, unlit (no normals are stored; the engine lights through the zone light table, inferred), opaque, single-sided.

### 3.2 Geometry (D2)

- **Vertices:** one per UV index, stored order. Position = frame 0 of the owning vertex, exact integers as float32. `TexCoord` = (u / 256 / width, v / 256 / height), exact (a u16 over a power of two). Color (1, 1, 1, 1). Normal (0, 0, 0) with `NormalMode.Flat` and `NormalProvenance(Flat)`, the 1c rule for absent normals.
- **Triangles:** the stored UV index triples, as stored; nothing dropped (the 8 records with repeated-position triangles keep them; `arrow.bin` has six).
- **`PointIndices`:** `ScenePointIndices(vertexCount, owner vertex per UV index)`; the 3 records with unused vertices keep them in the domain. No `Faces` (the source is triangles).
- **Morph targets (animated records):** frame f in 1..N-1 as `SceneMorphTarget("frame NNN", [], absolutePositions: owner positions of frame f)`, exact integers. The per-skin primitives of a multi-skin record share these objects.
- **Reader checks** (throw `InvalidDataException` with the offset): every check `ShadowkeyMesh.Parse` makes, plus "a UV index owned by two vertices" (0 retail), which is what makes the UV domain well defined.

### 3.3 Skin images (D3)

- One `SceneImage` per skin: `SceneImageSource` container `models.huge`, Location = the slot offset plus the texel block range, Original = the exact block, descriptor (width, height, 16 bpp, `Rgb`, `bmt.shadowkey`/`rgb444-le`, sRGB Assumed, alpha none), origin SourceReference.
- StandardPayload: an 8-bit indexed PNG, PLTE = distinct texels in first-appearance order with each nibble x17, no tRNS; note "0x0RGB 4:4:4 texels, nibble x17, indexed by first appearance"; evidence "top nibble clear on every texel; at most 256 distinct colors; x17 is invertible". A skin with more than 256 colors (0 retail) gets a truecolor PNG instead.
- **Magenta:** opaque. The 44 records with 0x0F0F texels carry the diagnostic `bmt.shadowkey.mesh.magenta-opaque` ("0x0F0F texels drawn opaque; that the engine keys them is a hypothesis: the fog table does not pin 0x0F0F"). Retiring the neutral adapter's opt-in keeps "no flags".

### 3.4 Skin alternatives (D4)

19 records; `male_long_tunic.bin` has 19 skins, the other 18 between 2 and 16. Each alternative mesh copies the vertex and index arrays (small: at most 1,038 UV-domain vertices) and shares the target objects; each clip carries one morph track per skin node with the same key arrays. The diagnostic `bmt.shadowkey.mesh.skin-selection` states that the entity table or a script picks the skin and skin 0 is shown by default.

### 3.5 Animation (D5)

- **Clips:** for every sequence k of an animated record, `SceneAnimation("seq{k:00}", tracks, timing, extras {start, end, rate})`. Keys at t_i = i / rate for i = 0..length-1 (float32, 0.5 ulp), weights one-hot on target (start + i - 1), all zero when start + i = 0, Step; plus one derived final key at length / rate repeating the last weights, so the clip spans length frame periods; `DurationSeconds` null. 209 clips over 33 records (195 multi-frame, 14 single-frame poses).
- **Timing:** `SceneAnimationTiming(framesPerSecond: rate, rawRate: rate, rawRateUnit: "shadowkey.sequence.rate", Assumed, evidence)`. Evidence: "rate read as frames per second (design section 4.2, RE-6); 8 of 195 multi-frame sequences have rate equal to length (1.0 s); durations median 2.1 s, p90/p10 4.6, against 9.7 as ticks per frame".
- **Static records:** their one sequence goes to native state; nothing moves.
- **Named gaps (they stay; none is a decline):**

| Gap | Reason | Carrier |
|---|---|---|
| G1 Rate unit | Nothing in the pack states it; RE-6 reads `6r51.app` | Assumed timing, native `bmt.shadowkey.mesh.sequences` |
| G2 Interpolation | Whole-frame keyframes; whether the engine blends them is unknown | Step, Assumed, diagnostic |
| G3 Loop or once | No per-sequence flag exists | Not declared (no clock is invented) |
| G4 Actor assembly | The six weapon records play the humanoid table in lockstep with a body; which weapon and which body is script data | Diagnostic on the weapon records; no composition |
| G5 Skin choice | Entity table or script | D4 default skin 0 |
| G6 Suspect rates | argonian `[119, 135)` and `[135, 157)` at rate 1 run 16 s and 22 s under fps | Carried, diagnostic `bmt.shadowkey.mesh.rate-suspect` |

### 3.6 Coverage elements

Kind `shadowkey.mesh.section`, one element per non-empty section:

| Identity | Classification | Reason (NativeOnly) |
|---|---|---|
| `header` | Typed | |
| `positions:0` | Typed | |
| `positions:{f}` (f = 1..N-1) | Typed (morph targets) | |
| `uvs`, `faces`, `texture-header` | Typed | |
| `skin:{k}` | Typed (images) | |
| `sequence:{k}` | Typed (clip) on animated records; NativeOnly on static ones | "one frame: nothing moves; the rate is retained" |

Nothing is Dropped.

### 3.7 Native state and diagnostics

| Kind (version 1) | Target | Payload (raw bytes only with `NativeDetail.Full`) |
|---|---|---|
| `bmt.shadowkey.mesh.header` | Document | the seven words, the section offsets, the slot index, the `models.txt` flag/width/height columns (the footprint descriptor, uninterpreted) |
| `bmt.shadowkey.mesh.sequences` | Document | every (start, end, rate) |
| `bmt.shadowkey.mesh.frames` | Primitive (0, 0) | frame count and vertex count; raw position block with Full |

Diagnostics (document; at most 3 in Standalone, at most 2 in Placement): `magenta-opaque`, `skin-selection`, `rate-suspect` (Standalone only), `actor-assembly` (weapon records).

---

## 4 Zone mapping

### 4.1 Route (D6)

`ShadowkeyZoneModelReader.Read` parses the zone set once, builds three kinds of document and composes them with `ModelSceneComposer.Compose(name, placements, units, basis, identity)`:
1. **Terrain** (placement `terrain`, identity transform): section 4.2.
2. **Sky** (placement `sky`, transform M alone (section 4.4: the Y-up shell into the Z-up zone, no scale, yaw or translation), `SceneNodePresentation(isSky: true)`): the `.zsk` mesh document in the Sky shape (section 4.5).
3. **Entities** (placement `ent:{index}`): the Placement-shape document of the resolved slot, one cached instance per slot, transform per section 4.4.

The composed document is `ModelDocument("bmt.shadowkey.zone", <zone name>)` with the zone's units and basis; every placed document is Y-up in the same unit, so each placement transform carries M (the composer requires transforms to include any basis change). Composition metadata keeps each placement's identity map.

### 4.2 Terrain

- One primitive per face kind (floor, ceiling, wall, riser, downstand), 4 vertices per quad, the builder's rule and corner offsets; at most 142,688 vertices in one zone (int indices, no 16-bit split).
- Positions in mesh units: (x·256 + dx·256, y·256 + dy·256, raw height), exact integers.
- `Faces` = one 4-corner face per quad; triangles (0, 1, 2) and (0, 2, 3) of it, the diagonal Assumed (four independent corner heights make many quads non-planar; the builder notes 1.9% of shared edges disagree); `NormalMode.Flat`.
- Materials: one neutral unlit material per face kind, plus one material per `.sur` row bound to its `.ztx` image, used by no primitive (D9); diagnostic `bmt.shadowkey.zone.tile-texturing-unresolved`.

### 4.3 Palettes and textures (D10)

- `ScenePalette("<stem>.pal", Rgb8, "<stem>.pal", 768 bytes, entries (r, g, b, 255), ScenePaletteRule("shadowkey.pal.rgb8", Authored, "8-bit components; the maximum component is 255 in 21/21"), location)`; transparent index 6 with `ScenePaletteRule("shadowkey.pal.light-table-key", Assumed, "the .zlu maps entry 6 to 0x0F0F at all 4 banks x 64 levels; entry 6 is the only magenta entry")` on the 13 zones that have it; auxiliary table `ScenePaletteTable("zlu", "u16le[4][64][256] 0x0RGB", "<stem>.zlu", <inflated 131,072 bytes>, interpretation ScenePaletteRule("shadowkey.zlu.light-ramp", Assumed, "banks white/red/green/blue x 64 levels; ShadowkeyLightTable.Synthesize reproduces it"))`.
- `.ztx` texture n: `SceneImageSource` container `<stem>.ztx`, Original = its 16,384 index bytes as stored (bottom row first), descriptor 128x128 8 bpp Indexed, StandardPayload = indexed PNG top row first with the 256-entry PLTE (tRNS alpha 0 on index 6 only where the rule is declared), note "rows reversed: stored bottom row first", `PaletteIndex` = the zone palette.
- `.zfg`: native state `bmt.shadowkey.zone.fog` (fog color, raw 131,072 bytes with Full).

### 4.4 Placements (D7)

- Transform (row vectors, applied left to right): scale(RawScale / 256), then M: (x, y, z) to (x, -z, y), then rotation about +Z by -2π·Angle2 / 65536, then translation (RawX, RawY, RawZ). All in mesh units.
- Quarter turns (4,348 placements, 0 included) have exact matrix entries; the other 3,910 are float32-rounded (writers report their usual transform rows).
- **What is measured:** the yaw sign and unit, mesh +Z to zone -Y, mesh +Y to zone +Z, the 1/256 unit (floor contact). **What is not:** the x mirror (248 against 251 wins). The proper map is chosen because it keeps every placed mesh congruent to its own document; the reflection would mirror each one. Diagnostic `bmt.shadowkey.zone.chirality-assumed`.
- Angle0 and Angle1 (384 placements) are not applied: native state per placement, diagnostic `bmt.shadowkey.zone.pitch-roll-unapplied` with the count.
- The 3 placements outside the grid (delfhide) and the 17 on blocked cells are placed as stored.
- Node names: the instance name (cut at the NUL, or 8 bytes) plus `#index`; extras: entity id, `entities.txt` kind and name, script field.

### 4.5 Sky (D8)

The `.zsk` payload through `ShadowkeyMesh.Parse` with its variant, built in the Sky shape: 256x256 skin image (4:4:4, indexed PNG), unlit, Repeat; units and basis as a mesh. Placed with M alone (unit scale) and `isSky`: the shell's size has no established relation to the grid, and a camera-centered sky needs none. The 9 interior skies (all-zero image, a closed box) are placed the same way. Diagnostic `bmt.shadowkey.zone.sky-texture-header` on interior skies ("texture header without a skin count").

### 4.6 Coverage, native state and diagnostics

| Identity | Classification | Reason (NativeOnly) |
|---|---|---|
| `zmp:header` | NativeOnly | "name, author and description strings" (also in extras) |
| `zmp:cells` | Typed (terrain) | |
| `zcp:heights` | Typed (terrain) | |
| `zcp:slots-edges-extra-shade` | NativeOnly | "surface assignment and edge bytes unresolved" |
| `sur` | NativeOnly | "tile-to-surface mapping unresolved" |
| `ztx:{n}` | Typed (images) | |
| `pal`, `zlu` | Typed (palette, table) | |
| `zfg` | NativeOnly | "no fog contract" |
| `zsk` | Typed (sky) | |
| `ent:{i}` | Typed (placement) | |
| `ent:angles-0-1` | NativeOnly | "axis and unit not established" |
| `zon`, `pth`, `stn`, `sta` | NativeOnly | "markers wait for the cut-2 marker contract" |
| `models-list` | Typed (residency resolves every placement) | |

Native kinds: `bmt.shadowkey.zone.header`, `.cells` (flags, flags2, raw u16 per cell; raw with Full), `.prototypes` (the non-height fields), `.surfaces`, `.fog`, `.triggers`, `.paths`, `.locks`, `.sta`, `.placement` (per placement: raw angles, scale, fill, entity id, name, script). The largest `.prototypes` payload (azra, 11,828 records) is about 0.7 M characters of JSON (inferred, about 60 per record), under the 1 Mi-character row limit (`SceneNativeState.MaximumPayloadCharacters`); slice 7 pins the measured size and splits by record range if it is ever exceeded.

Zone diagnostics: `tile-texturing-unresolved`, `chirality-assumed`, `pitch-roll-unapplied`, `static-placements` ("1,611 placements resolve to animated records and are shown at frame 0; their clips are in the mesh documents"), `sky-texture-header` (interior).

---

## 5 Probes, units and basis

### 5.1 `bmt.shadowkey.mesh`

Applied only to bytes inside the prefix, in this order (`P probe_mesh`):
1. Fewer than 14 bytes; tag other than 7; trailer other than 1; coordinate count other than 3 x vertices: NotAModel.
2. A zero frame, vertex, UV or face count: NotAModel.
3. `14 + 6·frames·vertices + 4·uvs + 12·faces + 8` above the declared length: NotAModel.
4. A face index out of range, for every face inside the prefix: NotAModel.
5. The texture header inside the prefix: zero skins or a side outside 1..1024: NotAModel.
6. With a declared length: the remainder after the texels minus the sequence count is not a positive multiple of 6: NotAModel.
7. The sequence table inside the prefix: count 0, count against the length, a range outside [0, frames], a zero rate: NotAModel; bytes after the table: NotAModel.
8. A complete record: Supported, Confirmed; a prefix that ends first: Supported, Tentative (evidence names where).

Evidence text, for example `Shadowkey mesh record: 1 frames, 11 vertices, 19 faces, 1 skin(s) 8x8, 1 sequence(s)`.

### 5.2 `bmt.shadowkey.zone`

On a `.zmp` candidate (`P probe_zone`): the envelope (u32 length, zlib header at +4), the first 132 inflated bytes, a NUL-terminated printable name within 32 bytes, width and height 1..4096, `132 + 6·width·height` equal to the declared inflated length; a complete file must inflate exactly with nothing after the stream (Confirmed), a prefix is Tentative. The name equalling the file stem only enriches the evidence. No other retail file passes (section 0.2).

### 5.3 Units and basis

- **Row:** `SceneUnits(1.0 / 512, Assumed, "0.5 m per tile, 256 mesh units per tile: 8 of the 10 door meshes are 1,036 units, 4.047 tiles, against the 4.0-tile standard room height of 43,082 of 66,829 prototypes (BMT measurement, SK/ShadowkeyZoneSceneBuilder.cs:88-98); with that unit 2,025 of 8,255 placements rest within 1/8 tile of their cell's floor, against at most 1,070 at half or double the unit or with the axis flipped; RE-6 (6r51.app) pending")`. The factor is exact in binary, so the GLB root scale is exact.
- **Bases:** mesh and sky up (0, 1, 0), forward (0, 0, 1), right-handed; zone up (0, 0, 1), forward (0, -1, 0), right-handed; all Assumed, with the evidence of section 4.4 and the winding count (75 of 79 closed records are counter-clockwise from outside when read right-handed, so no winding change is needed for either writer).
- **Writers:** GLB rotates the zone -90 degrees about X (Z-up to Y-up) and leaves the Y-up mesh as is; Blender rotates the mesh +90 degrees about X and leaves the zone. The basis test has a control that omits the rotation (as 1a's).
- **`--game`:** absent, `auto` or `shadowkey` selects these rows; any other game throws for a Shadowkey input.

---

## 6 Shell and workflow (slice 9)

- `models.huge` opens as an archive (D1); `mesh convert models.huge --all` converts 226 records and lists 11 NotAModel rows; `mesh info models.huge -e 175_arrow.bin` inspects one.
- A `.zmp` input converts one zone; `mesh convert <6r51> --all` finds the 21 zones plus `models.huge`.
- Memory estimate: the source length plus the writer workspace for a mesh; for a zone, the inflated zone set (at most about 1.3 MB, azra), the pack (4.9 MB) and the terrain buffers (at most 142,688 vertices).
- `mesh formats` lists both readers with their variants and rules.

---

## 7 Ordered slices

The owner builds and runs every slice; agents never build, test or launch Blender.

| # | Slice | Files | Verification (each control must fail) | Bucket-B |
|---|---|---|---|---|
| 0 | Legacy corrections and the one decode path | `SK/ShadowkeyMesh.cs` (texture-header variant, offsets), `SK/ShadowkeySkybox.cs`, `SK/ShadowkeySkySceneBuilder.cs`, `SK/ShadowkeyZoneSceneBuilder.cs`, `SK/ShadowkeySceneBuilder.cs`, their tests, `T/Helpers/ShadowkeyTestBuilder.cs` | A counted and an uncounted builder sky parse (control: the uncounted one fails the counted walk). The sky image is 256x256 (control: a 4:4:4 builder image read as 512 indices fails the top-nibble pin). The placement matrix of a builder placement equals the literal (control: the swap map with +yaw fails). Existing record tests pass unchanged. | All 21 skies parse; the 8 painted ones have a clear top nibble on 65,536 of 65,536; the sky retail test's half-width assertion is replaced by the texel-encoding pin (control: the `.ztx` bank read as u16 stays below 31%) |
| 1 | Pack archive backend | `ShadowkeyPackBackend.cs`, `ArchiveProbe` | Tiling and naming on a builder pack; one moved offset fails; a `.huge` without an `.idx` is not claimed | 237 entries; `archive list` names; 1 claim among the 1,919 files |
| 2 | Cover manifest, resolver, oracles | `Samples/cut2-shadowkey-cover-manifest.json`, `RealAsset/Cut2ShadowkeyCoverManifest.cs`, `gate2/shadowkey_probe.py` | Schema; one altered SHA-256 digit fails; `P selfcheck` (19 checks) | Every row resolves (as `P verify`, 168 of 168) |
| 3 | Units, bases, format metadata, probes | `ShadowkeyModelUnits.cs`, `ShadowkeyModelFormatMetadata.cs`, both probes | Literal pins of 1/512 and the evidence; every probe class on builder blobs; every truncation refused (control: the prefix of a longer entry is Tentative, not NotAModel) | The cover's probe verdicts equal `P`'s; the 13 file controls NotAModel |
| 4 | Mesh reader: geometry, images, coverage, native state | `ShadowkeyMeshModelReader.cs`, `...Geometry.cs`, `...Images.cs`, `ShadowkeyModelCoverage.cs`, `ShadowkeyModelNativeState.cs`, `ShadowkeyMeshDocumentBuilder.cs` | Vertex domain = UV list (control: an unrolled domain fails the vertex-count pin); PointIndices equal the owners (control: a swapped owner fails); UVs exact (control: a float32 division by 256 then by the size, rounded twice, differs on a chosen builder value); the PNG decodes to the texels (control: x16 instead of x17 fails); a UV owned by two vertices throws; coverage equals an independent section list (control: one section dropped); `ValidateStructure` passes | A1, A2, A3 over all 226 records |
| 5 | Skin alternatives | `ShadowkeyMeshModelLayers.cs` | One exclusive group, skin 0 default-on (control: two default-on sets fail validation); targets shared by reference | A1 on the 19 multi-skin records |
| 6 | Animation | `ShadowkeyMeshModelAnimation.cs` | One clip per sequence with the derived final key (control: without it the end pin fails; with `DurationSeconds` set the GLB plan refuses); one-hot Step weights (control: Linear fails); absolute targets exact (control: frame-to-frame accumulation fails) | A1 and A3 over the 33 animated records |
| 7 | Zone reader: terrain, palettes, textures, sky | `ShadowkeyZoneModelReader.cs`, `ShadowkeyZoneTerrain.cs`, `ShadowkeyMeshModelImages.cs` | The face rule on a builder grid equals `P`'s quad list (control: the rotated corner order fails the shared-edge pin); palette entries, key and table pinned (control: `<< 2` expansion fails); the `.ztx` PNG is top row first (control: unflipped fails) | A1 to A3 over all 21 zones (terrain, palettes, images, sky) |
| 8 | Zone placements | `ShadowkeyZoneComposition.cs` | Placement matrix literal on quarter turns (control: +yaw fails); budgets checked before composing (a synthetic 4,097-placement zone refuses) | A1 and A3 over all 8,258 placements; the blocked-vertex total equals `P`'s 7,687 (control: the legacy map gives 21,017) |
| 9 | Registration and shell | `BethesdaModelRegistration.cs` (main session), `BethesdaModelWorkflow.cs`, `MeshCommand.cs` | A slot resolves to exactly one reader (control: a NIF resolves to the NIF reader); a `.zmp` to the zone reader | `mesh info` and `mesh convert` on two cover slots and one cover zone |
| 10 | Gate-2 harness | `gate2/shadowkey_gate2.py`, `gate2/shadowkey_produce_gate2.ps1` | Driver self-checks on hand-built receipts, as in 1a | Hops A1 to A3, B, C, E and basis on the covers |
| 11 | Gate 2 (Shadowkey), owner run, after gate 1c | none | Section 8 | Full covers plus decline controls |

---

## 8 Oracles and gate 2 (Shadowkey)

| Hop | Oracle | Control that must fail |
|---|---|---|
| A1 fields | `P dump --arrays` against `mesh dump`: record header, counts, positions of every frame, UVs (raw and normalized), faces, skins (SHA-256 of the texel block and of the PNG's decoded indices), sequences and clip keys; zone header, grid, prototypes, surfaces, palette bytes and rules, table SHA-256, sky record, placements (raw fields and the matrix) | One position word changed in a copy; the swapped yaw sign |
| A2 coverage | `P`'s section list against the reader's census | One element removed |
| A3 geometry | Per vertex: position, UV and point index; triangles; per target the absolute positions; terrain quads by the independent rule; placed frame-0 vertices in zone space | One vertex moved; the legacy placement map; the rotated corner order |
| B document to GLB | `gate1a/hop_b.py` (and `hop_b_anim.py` for clips): validator 0 errors, accessors after the declared conversions, the default-on skin only, morph targets lowered relative and exact, one animation per clip ending at length/rate | As in 1a and 1b |
| C document to package | `gate1a/hop_c.py` and `hop_c_anim.py`: vertices, UVs, shape keys, actions, skin collections, the unused surface materials present | One shape-key coordinate flipped in the package |
| D, B' (owner-run) | As in 1a | As in 1a |
| E writer against writer | `gate1a/hop_e.py`, `hop_e_anim.py` | One UV swapped in the package only |
| F (owner-run) | Orientation of an asymmetric skin (`coatarms.bin`: the shield's point down, the sword along the top), chirality (a placed `weaponrack` and a placed `torch` against their walls), palette colors against an independent expansion, the 256x256 sky | A mirrored export; a sky drawn at 512 wide |
| Basis | The two bases of section 5.3 | The rotation omitted |

**Allowed Dropped and Degraded rows for a Shadowkey item** (scoped as cut 1c's enumerated allowance): `geometry.source-point-domain-omitted` (A1 pins the point indices), `source.palette-provenance-omitted` (A1 pins the palette), `source.opaque-payloads-omitted` (A1 pins native state), and on the zone's sky node `presentation.static-source-pose` (Degraded). An injected Faces-Dropped row on a mesh item must fail the gate (control).

**Gate 2 (Shadowkey):** every hop passes on the covers and every control fails; A1 to A3 pass on the whole population; the legacy viewer's parse differs only in the sky image and the placement map (slice 0).

---

## 9 Cover (measured; `cover/cover-manifest.json`)

**Rule.** Slots: byte-identical payloads collapse to one representative (217 of 226); each representative's cell is (frames class 1 / 2-15 / 16-127 / 128+, skins class 1 / 2-4 / 5+, texture size, sequences class 1 / 2-4 / 5+) plus the true tags among 18 (magenta, UV past the texture, repeated-position triangle, repeated-index triangle, duplicate triangle, unused vertices, unused UVs, closed, closed with negative volume, rate equal to length, rate 1 on several frames, the humanoid table, probe Tentative, duplicate payload, duplicate name, non-square texture, static rate 10, frames outside every sequence). Zones: (sky counted or uncounted, painted, palette key, fog black or colored, grid size) plus 12 tags (sky rate 10, the raiders UV table, `.sta`, paths, locks, placements outside the grid, on blocked cells, unterminated names, author or description not default, pitch or roll angles, slot values at or past the texture count). An exact MILP (HiGHS through `scipy.optimize.milp`) takes the fewest rows covering every value, every observed pair of cell values and every tag, then the fewest bytes: **slots 17 over 91 requirements, zones 7 over 55, both stages optimal.**

**Slot rows (22, 1,534,782 B):** cover 25 `dagger`, 56 `wormmouth`, 59 `argonian`, 63 `orc_ice_warrior`, 69 `zombie`, 70 `portcullis`, 75 `startooth`, 77 `glasscase`, 78 `sarcophagus`, 80 `charnelflame`, 123 `umbrastat`, 142 `spiketrap`, 152 `burnedwall`, 175 `arrow` (smallest), 176 `throw_dagger`, 208 `woodenbucket`, 233 `azra`; edges 22 `male_long_tunic` (largest, most skins), 230 `Pergan02` (most frames), 67 `umbra` (most vertices and faces), 215 `windowbox` (widest UV wrap), 20 `female_long_tunic` (most sequences); 70 `portcullis` is also the largest frame delta.

**Zones (9):** cover azra (most prototypes), crypt2, delfhide, ffarena (fewest placements), glaciercrawl, raiders (most placements, fewest prototypes), stouttp; edges dstar_w (most terrain) and dstar_e (most animated placements). Each row pins every file of the zone; the manifest also pins `models.idx`, `models.huge`, `models.txt` and `entities.txt`.

**Decline controls:** the 11 empty slots (NotAModel); 13 tree files, one of each family and every compressed zone family (`6r51.app`, `ffarena.ent`, `models.idx`, `battle2.ogg`, a zero-byte `.s`, `global.spr`, `menu_sprites.txt`, a `.wav`, `raiders.zcp`, `azra.zfg`, `lothcav.zlu`, `raiders.zsk`, `glaciercrawl.ztx`); 12 synthetic (every truncation of the three smallest cover records, one trailing byte, tag 6, trailer 2, coordinate count 3V + 1, a vertex index equal to the count, a UV index equal to the count, a sequence past the frames, a zero rate, a `.zmp` one cell short, a `.zmp` name without a NUL, an interior sky read with the counted header).

**Alternatives for D12** (same cells): order 1: 11 slots, 6 zones; order 3: 20 and 8; one per observed cell: 68 and 20.

**Verification:** `P verify` re-read all 168 pins with 0 mismatches and detected the altered digit.

---

## 10 Shared asks

None under the recommendations. Optional:

| Ask | Exact contract | When |
|---|---|---|
| SA-K1 | A public way to share one primitive's vertex, index and target buffers under another material (today `ScenePrimitive.WithMaterialIndex` is internal), or a material-alternative contract | Only if D4's copies prove costly; they are small (at most 1,038 vertices per skin) |
| SA-K2 | `ModelSceneComposer` retains a diagnostic repeated by many placements once, with a count | Optional; the 2-per-document bound makes it unnecessary for Shadowkey |
| (dependency) | The cut-2 marker contract, for triggers, paths and spawn points | Until then they are native state |

---

## 11 Open questions for the owner

| # | Question | Recommendation |
|---|---|---|
| Q1 | The Rate unit | Keep fps Assumed with the rate-equals-length evidence; RE-6 reads `6r51.app` for the sequence tick (the design's RE backlog). |
| Q2 | Interpolation between keyframes | Step, Assumed; Linear is one enum away if RE-6 finds blending. |
| Q3 | The x mirror between mesh and zone frames | The proper rotation (congruent placements), a diagnostic, and hop F on `weaponrack` and `torch`, the two objects that disagree most; RE-6 can read the placement code. |
| Q4 | Skin selection | Exclusive layer set, skin 0 default (D4). |
| Q5 | Tile texturing | Untextured now (D9); a later slice textures walls from the measured slots after reproducing the 0.94 score in `P`; floors and ceilings stay open. |
| Q6 | The 4:4:4 magenta key on skins and skies | Opaque (the fog table gives no evidence of a key); the palette key on `.ztx` is declared because the light table pins it. |
| Q7 | Clips on placed entities | Static placements (D6); the per-mesh documents carry the clips. |
| Q8 | The interior sky's missing skin-count word | Parse it as the uncounted variant and record it; it may be a second record version (RE-6). |
| Q9 | Sky size | Unit scale under `isSky`; no enclosure scaling (that was a viewer display choice). |
| Q10 | Angle0 and Angle1 | Native state until an axis and unit are measured (384 placements). |
| Q11 | Empty slots in `archive list` and `mesh convert --all` | Listed as zero-length entries (D1). |
| Q12 | Entry naming | `NNN_name.bin`; slot identity survives duplicate names. |
| Q13 | Skin PNG form | Indexed (D3). |
| Q14 | Terrain quads | Keep `Faces` quads with the (0, 2) diagonal Assumed; the engine's split is unknown. |
| Q15 | The legacy viewer | Fix its sky and placement map in slice 0 (D13), so the viewer and the documents agree. |

## 12 Measured versus assumed

- **Measured:** pack tiling and slot census; record layout on 226/226; the UV vertex domain (226/226, 21/21 skies, control 0/226); the sky as a mesh record with a 256x256 4:4:4 texture (8/8 painted, control at most 30%); static records' single sequences; the humanoid table and the weapon lockstep; rate equals length on 8 sequences and the two duration spreads; winding (75/79); the zone census (grids, prototypes, surfaces, textures, palettes, tables, placements, triggers, paths, locks, terrain faces); the yaw sign and unit, the forward and up axes, the floor-contact unit; the palette key pinned by the light table and not by the fog table; composition budgets; probe verdicts on every slot and file; the covers and their verification.
- **Assumed:** 1/512 m per unit (RE-6); fps timing, Step, no loop policy; right-handed bases and the proper placement map; forward +Z for meshes; unlit materials; sRGB; skin 0 default; opaque 4:4:4 magenta; the palette transparency rule; the terrain diagonal; sky at identity scale; 1 MiB source bound; the probe's 1..1024 texture-side bound.
- **Inferred:** the engine lights meshes through the zone light table; scripts choose skins, sequences and weapons.

### Critical files

- `src/BethesdaMultitool/Core/Formats/Travels/Shadowkey/ShadowkeyMesh.cs`, `ShadowkeySkybox.cs`, `ShadowkeySkySceneBuilder.cs`, `ShadowkeyZoneSceneBuilder.cs`, `ShadowkeySceneBuilder.cs`, `ShadowkeyNeutralSceneAdapter.cs`
- `shared/Multitool.Shared/src/Slfx77.Multitool.Core/Models/ScenePalette.cs`, `ScenePointIndices.cs`, `SceneMorphTarget.cs`, `SceneAnimationTiming.cs`, `Composition/ModelSceneComposer.cs`, `Composition/ModelCompositionBuilder.cs`
- `shared/Multitool.Shared/src/Slfx77.Multitool.Media/Models/ModelGltfAdmission.cs`, `ModelGltfGeometry.cs`, `ModelGltfLayers.cs`, `SceneGltfBuilder.cs`; `shared/Multitool.Shared/src/Slfx77.Multitool.Media.Blender/BlendAdmission.cs`, `BlendAnimationAdmission.cs`
- `tools/scripts/gate2/shadowkey_probe.py`, the seed of A1 to A3

---

## Review corrections (2026-09-28)

The staged implementation's review found these points of the plan wrong or incomplete; the staging follows the
corrections below.

- **D4 and section 0.3 (skin alternatives in GLB).** Layer sets are not the only GLB rule that binds the skin
  alternatives. At 2e7af70 `ModelGltfDrawSelection.Plan` (`SH/Slfx77.Multitool.Media/Models/ModelGltfDrawSelection.cs`,
  lines 97-110) marks every morph track whose node's mesh the layer selection leaves undrawn
  (`draw.morph-target-suppressed`), and `ModelGlbWriter` then throws. With one track per skin node, every multi-skin
  record (all 19 are animated) was unwritable in Default mode, and the shared Transform root made All mode unwritable as
  well (`layers.shared-hierarchy-unsupported`). Staged, pending the owner's ruling (option (a)): every skin node is a
  scene root, as the Starfield reader's LOD nodes are, and the clips drive skin 0's node only, so the alternatives hold
  frame 0; Default and All write, an explicit selection of another skin is still refused. The alternatives are (b) skin 0
  as the only mesh with the other skins as images, unreferenced materials and native state, and (c) a Shared ask that
  lets draw selection drop the tracks of unselected exclusive-group members with a row.
- **Section 3.1.** "Node 0 is a Transform root whose children are `skin00` to `skinNN`" is superseded: node k is
  `<record>.skinNN`, carries mesh k and is a scene root.
- **Sections 3.2 and 3.6 (unused vertices).** The UV vertex domain carries no position of a record vertex no face names
  (umbra 22 over 157 frames, jelly 4, umbrastat 1). Their indices and per-frame positions travel in native rows at every
  detail, and the census gives them their own element, `unused-vertices`, NativeOnly.
- **Section 4.6 (cells and prototypes).** The cells row carries each cell's prototype index and the prototypes row every
  corner height, because the terrain draws only the prototypes an open cell uses (raiders 34 of 99, crypt2 118 of 341,
  dstar_w 669 of 1,946 are used by none). The census gains `zcp:heights-unplaced`, NativeOnly, when there are such
  prototypes.
- **Section 2 (PNG encoding on conversion reads only).** Not followed: an inspection read builds the standard PNG
  payloads too, because Shared's GLB planner plans an image only from a PNG, DDS or TGA representation and `mesh info`
  runs the writer plans. Recorded as a deviation in both readers' remarks.
- **Winding.** Materials stay single-sided (Assumed), and the 4 closed records wound clockwise seen from outside
  (door_5units_right, door_7units_right, woodenbucket, ax) carry `bmt.shadowkey.mesh.reversed-winding`, placed ones
  `bmt.shadowkey.zone.placed-reversed-winding`; single against double-sided is an owner decision.
- **Sky locations.** Every sky location's element is `inflated:<stem>.zsk:<part>`, so it names the file whose inflated
  payload its offsets index.
