# Captured Cell Audit

Generated 2026-08-16 from `dmp cell-inventory` reports in `TestOutput/cell_capture_audit`, ordered by the PE `TimeDateStamp` of the game module inside each crash dump.

**Captured-cell criterion:** at least one non-persistent placement whose base record is `STAT`, `MSTT`, or `SCOL`. This deliberately excludes NPC/door/furniture-only evidence, so a cell the player merely stood in does not count as scenery-captured.

Authority inputs: PC final `FalloutNV.esm` plus `data/cell_worldspace_authority.json` (schema v2 — worldspace, interior/exterior class, grid, editor IDs, full names, and source-ESM ref→cell parentage where known).

Regeneration commands are in the header of `tools/scripts/cell_capture_audit.py`.

## Corpus

- Dumps scanned: **50**
- Captured scenery cells: **31**
- Scene-less captures: **1** — cells recovered, but the crash predates the cell grid streaming in, so there are no temporary refs to place
- Near-empty (parsed, negligible content): **3**
- Dataless pre-load captures: **15**

The classifier keys on **non-persistent placements**, not cell count. Scenery lives on temporary/streamed references; a capture taken before the grid loads still holds a full set of cells and persistent markers, so counting cells alone makes a scene-less capture look like a recovery gap. `xex23` is the worked example: 1,153 cells — 97.5% of its working neighbour `xex24` — and exactly one non-persistent ref in the whole dump.

Every dump appears below. The previous edition listed only the 35 that produced a report, which hid the distinction between a dump with nothing in it and a dump whose cells we fail to recover.

Worldspace classification is complete in this run — every captured cell resolved to a named worldspace, with no `Unlinked Exterior` or `Ambiguous Exterior` buckets. Earlier editions of this audit carried both.

**Richest captures** (reach for these first when you want scenery):

- `xex44` — 15,926 placements across 121 cells, build 2010-04-21
- `xex39` — 9,522 placements across 70 cells, build 2010-04-09
- `xex29` — 3,924 placements across 54 cells, build 2010-04-08
- `xex30` — 3,679 placements across 46 cells, build 2010-04-08
- `xex5` — 2,971 placements across 36 cells, build 2010-01-04

Note the filename ordinals do not track chronology: `xex39` builds on 2010-04-09, before `xex33`–`xex38`.

## All dumps

`Records` is the semantic parse total — the tell for whether a capture holds game state at all. `Build` is the PE timestamp (build identity); `Captured` is the dump's file mtime (when the crash was taken). They are different axes and can differ by days.

`Runtime cells` is the recovered cell population; `Temp refs` is the non-persistent placement count on them. A high cell count with zero temp refs is a scene-less capture, not a gap — that pair is the whole classification.

| Dump | Build (PE) | Captured | MB | Records | Runtime cells | Temp refs | WS | Cells | Placements | Status |
|---|---|---|---:|---:|---:|---:|---:|---:|---:|---|
| `xex` | 2009-11-17 | 2009-12-03 | 177 | 62,988 | 1,562 | 3,451 | 2 | 44 | 2,931 | **captured** |
| `xex1` | 2009-12-03 | 2009-12-03 | 204 | 62,929 | 1,540 | 2,446 | 2 | 31 | 2,132 | **captured** |
| `xex2` | 2009-12-03 | 2009-12-04 | 200 | 62,901 | 1,542 | 607 | 4 | 12 | 508 | **captured** |
| `xex3` | 2009-12-11 | 2009-12-11 | 200 | 63,433 | 833 | 1,785 | 1 | 33 | 1,278 | **captured** |
| `xex4` | 2009-12-15 | 2009-12-17 | 201 | 63,615 | 867 | 2,021 | 3 | 50 | 1,770 | **captured** |
| `xex5` | 2010-01-04 | 2010-01-06 | 182 | 64,949 | 1,041 | 3,704 | 1 | 36 | 2,971 | **captured** |
| `xex6` | 2010-01-07 | 2010-01-07 | 201 | 65,184 | 856 | 1,765 | 1 | 30 | 1,110 | **captured** |
| `xex7` | 2010-01-07 | 2010-01-08 | 182 | 65,327 | 858 | 2,116 | 1 | 32 | 1,324 | **captured** |
| `xex8` | 2010-01-07 | 2010-01-09 | 140 | 1 | — | — | — | — | — | dataless |
| `xex10` | 2010-01-08 | 2010-01-09 | 140 | 1 | — | — | — | — | — | dataless |
| `xex11` | 2010-01-08 | 2010-01-09 | 140 | 1 | — | — | — | — | — | dataless |
| `xex12` | 2010-01-08 | 2010-01-09 | 140 | 1 | — | — | — | — | — | dataless |
| `xex13` | 2010-01-08 | 2010-01-09 | 140 | 1 | — | — | — | — | — | dataless |
| `xex14` | 2010-01-08 | 2010-01-09 | 140 | 291 | 3 | 0 | 0 | 0 | — | near-empty |
| `xex15` | 2010-01-08 | 2010-01-09 | 140 | 1 | — | — | — | — | — | dataless |
| `xex16` | 2010-01-08 | 2010-01-09 | 140 | 127 | 5 | 0 | 0 | 0 | — | near-empty |
| `xex17` | 2010-01-08 | 2010-01-09 | 140 | 1 | — | — | — | — | — | dataless |
| `xex9` | 2010-01-08 | 2010-01-09 | 140 | 191 | 7 | 0 | 0 | 0 | — | near-empty |
| `xex18` | 2010-01-11 | 2010-01-11 | 204 | 65,672 | 976 | 2,660 | 1 | 25 | 2,345 | **captured** |
| `xex19` | 2010-01-11 | 2010-01-12 | 198 | 65,872 | 848 | 404 | 2 | 3 | 291 | **captured** |
| `Fallout_Debug.xex` | 2010-01-27 | 2010-01-30 | 147 | 68,888 | 898 | 438 | 1 | 1 | 214 | **captured** |
| `Fallout_Debug.xex1` | 2010-01-27 | 2010-01-30 | 146 | 68,888 | 898 | 438 | 1 | 1 | 214 | **captured** |
| `Fallout_Debug.xex2` | 2010-01-27 | 2010-02-01 | 165 | 68,983 | 906 | 524 | 2 | 3 | 281 | **captured** |
| `xex20` | 2010-02-03 | 2010-02-04 | 156 | 67,860 | 983 | 236 | 1 | 6 | 166 | **captured** |
| `xex21` | 2010-02-16 | 2010-02-17 | 236 | 71,776 | 1,058 | 2,964 | 1 | 40 | 2,640 | **captured** |
| `xex22` | 2010-03-10 | 2010-03-10 | 233 | 75,983 | 1,090 | 2,402 | 1 | 1 | 1,366 | **captured** |
| `xex23` | 2010-03-25 | 2010-03-25 | 109 | 79,270 | 1,153 | 0 | 0 | 0 | — | scene-less capture |
| `xex24` | 2010-03-26 | 2010-03-26 | 225 | 78,757 | 1,182 | 1,791 | 1 | 29 | 1,139 | **captured** |
| `xex25` | 2010-03-30 | 2010-03-30 | 189 | 79,414 | 1,215 | 107 | 1 | 11 | 90 | **captured** |
| `xex26` | 2010-03-31 | 2010-04-01 | 252 | 80,229 | 1,216 | 107 | 1 | 11 | 90 | **captured** |
| `xex27` | 2010-04-05 | 2010-04-05 | 213 | 80,362 | 1,220 | 3,268 | 1 | 54 | 2,404 | **captured** |
| `Fallout_Release_MemDebug.xex` | 2010-04-05 | 2010-04-05 | 212 | 81,260 | 1,201 | 2,774 | 1 | 38 | 2,055 | **captured** |
| `xex28` | 2010-04-06 | 2010-04-07 | 183 | 80,851 | 1,247 | 1,139 | 2 | 42 | 888 | **captured** |
| `xex29` | 2010-04-08 | 2010-04-08 | 174 | 81,343 | 1,232 | 5,509 | 2 | 54 | 3,924 | **captured** |
| `xex30` | 2010-04-08 | 2010-04-08 | 167 | 81,343 | 1,234 | 4,446 | 2 | 46 | 3,679 | **captured** |
| `xex31` | 2010-04-08 | 2010-04-08 | 189 | 82,173 | 1,242 | 1,523 | 1 | 30 | 1,292 | **captured** |
| `Jacobstown` | 2010-04-08 | 2010-04-08 | 176 | 82,127 | 1,206 | 1,728 | 2 | 22 | 1,548 | **captured** |
| `xex32` | 2010-04-09 | 2010-04-09 | 200 | 82,244 | 1,231 | 2,884 | 1 | 41 | 2,633 | **captured** |
| `xex39` | 2010-04-09 | 2010-04-17 | 210 | 82,296 | 1,217 | 13,434 | 2 | 70 | 9,522 | **captured** |
| `xex33` | 2010-04-09 | 2010-04-10 | 138 | 0 | — | — | — | — | — | dataless |
| `xex34` | 2010-04-09 | 2010-04-10 | 138 | 0 | — | — | — | — | — | dataless |
| `xex35` | 2010-04-09 | 2010-04-10 | 138 | 0 | — | — | — | — | — | dataless |
| `xex36` | 2010-04-09 | 2010-04-10 | 138 | 0 | — | — | — | — | — | dataless |
| `xex37` | 2010-04-12 | 2010-04-12 | 138 | 0 | — | — | — | — | — | dataless |
| `xex38` | 2010-04-12 | 2010-04-12 | 138 | 0 | — | — | — | — | — | dataless |
| `xex40` | 2010-04-14 | 2010-04-17 | 142 | 0 | — | — | — | — | — | dataless |
| `xex41` | 2010-04-14 | 2010-04-17 | 142 | 0 | — | — | — | — | — | dataless |
| `xex42` | 2010-04-19 | 2010-04-20 | 223 | 82,152 | 1,258 | 3,148 | 1 | 33 | 2,731 | **captured** |
| `xex43` | 2010-04-19 | 2010-04-20 | 229 | 82,276 | 1,251 | 2,426 | 1 | 38 | 1,858 | **captured** |
| `xex44` | 2010-04-21 | 2010-04-21 | 220 | 84,145 | 1,264 | 23,254 | 2 | 121 | 15,926 | **captured** |

## Scene-less captures

Full record complement and a normal cell population, but no temporary references — the crash was taken before the cell grid streamed in. Nothing is being dropped; there is no scenery in the dump to recover. Verified for `xex23` down to the allocator: its REFR pool has free slots in *captured, readable* pages, so the temporaries were never allocated rather than lost to a partial capture.

- **`xex23`** — 79,270 semantic records, 1,153 cells, 0 non-persistent placements, 109 MB, build 2010-03-25.

## Worldspace totals

| Worldspace | Distinct cells captured | Dumps contributing |
|---|---:|---:|
| WastelandNV | 354 | 26 |
| Interior | 142 | 12 |
| TheStripWorld | 52 | 3 |
| FreesideWorld | 11 | 4 |
| CampMcCarranWorld | 1 | 1 |

## Cell coverage

560 distinct cells were captured at least once across the corpus. `Best` is the highest single-dump placement count and the dump that achieved it — use that dump when you want the fullest version of a cell.

| Worldspace | Cell | Dumps | Best | Best dump |
|---|---|---:|---:|---|
| Interior | Gomorrah01 "Gomorrah Main Level" | 3 | 1,366 | `xex22` |
| Interior | TOPSCasino "The Tops Casino Main Floor" | 1 | 1,297 | `xex44` |
| Interior | RocketLabBasement "REPCONN Basement" | 1 | 968 | `xex39` |
| Interior | RocketLabMid "REPCONN Facility" | 2 | 937 | `xex29` |
| Interior | KitOffice | 1 | 815 | `xex39` |
| Interior | Vault34a "Vault 34 - 1st Floor" | 1 | 795 | `xex44` |
| Interior | Lucky38ControlRoom "Lucky 38 Floor B1 Basement" | 1 | 669 | `xex44` |
| Interior | HooverDamIntPowerPlant04 "Hoover Dam Power Plant 04" | 1 | 654 | `xex44` |
| Interior | KnobHillMineInterior "Techatticup Mine" | 1 | 592 | `xex44` |
| Interior | OVWestSewers03 "The Thorn" | 1 | 539 | `xex44` |
| Interior | VikkiAndVance "The Vikki and Vance Casino" | 1 | 524 | `xex44` |
| Interior | Lucky38BasementFloorB2 "Lucky 38 Floor B1 Basement" | 1 | 513 | `xex30` |
| Interior | SSHQ01 "Factory Floor" | 3 | 478 | `xex30` |
| Interior | ULCasino "Ultra-Luxe Casino Floor" | 1 | 471 | `xex44` |
| Interior | 2EOMichaelAngelo "Michael Angelo's Workshop" | 1 | 445 | `xex39` |
| Interior | Vault11b "Vault 11 Living Quarters" | 1 | 429 | `xex39` |
| Interior | CampMCTermInt02 "Camp McCarran Terminal Building" | 2 | 399 | `xex39` |
| Interior | HELIOSOnePlant "HELIOS One Power Plant" | 1 | 392 | `xex44` |
| Interior | HiddenValley01 "Hidden Valley Bunker L1" | 1 | 384 | `xex44` |
| Interior | Vault19c "Sulfur Cave" | 1 | 352 | `xex39` |
| Interior | LakeMeadCave "Lake Mead Cave" | 2 | 348 | `xex39` |
| Interior | OVWestSewers02 "Northwest Sewers" | 2 | 341 | `xex44` |
| Interior | OVCentralSewers01 "Central Sewers" | 1 | 325 | `xex44` |
| Interior | Gomorrah03 "Gomorrah Top Floor" | 1 | 305 | `xex44` |
| Interior | 2ETopsBennysFloor "The Tops 13th Floor" | 1 | 298 | `xex44` |
| Interior | TestQANavMesh "QA Testing Cell" | 1 | 294 | `xex44` |
| Interior | Vault3a "Vault 3 - Recreation Area" | 1 | 277 | `xex44` |
| Interior | MojaveOutpostBarracks01 "Mojave Outpost Barracks" | 1 | 271 | `xex30` |
| Interior | RocketLabTop "REPCONN Research Labs" | 1 | 262 | `xex44` |
| TheStripWorld | [Virtual 1,-2 TheStripWorld] [1,-2] | 1 | 259 | `xex21` |
| WastelandNV | (no EDID) [-27,23] | 4 | 243 | `xex29` |
| Interior | SecuritronVault "Securitron Vault" | 1 | 234 | `xex44` |
| Interior | Vault22e "Vault 22 - Pest Control" | 1 | 234 | `xex44` |
| Interior | HELIOSOneDeck "HELIOS One Observation Level" | 2 | 231 | `xex19` |
| Interior | Lucky38SuiteFloor22 "Lucky 38 Floor 22 Suite" | 1 | 230 | `xex44` |
| Interior | GunRunnerHQInterior "Gun Runner Headquarters" | 1 | 221 | `xex44` |
| WastelandNV | (no EDID) [0,19] | 1 | 219 | `xex32` |
| Interior | GSDocMitchellHouse "Doc Mitchell's House" | 3 | 214 | `Fallout_Debug.xex` |
| WastelandNV | (no EDID) [12,4] | 1 | 211 | `xex1` |
| Interior | Vault3c "Vault 3 - Maintenance Wing" | 1 | 205 | `xex44` |
| TheStripWorld | (no EDID) [1,-3] | 1 | 204 | `xex21` |
| WastelandNV | (no EDID) [13,3] | 1 | 196 | `xex1` |
| Interior | HooverDamIntIntakeTower04 "Hoover Dam Intake Tower 04" | 1 | 194 | `xex39` |
| WastelandNV | (no EDID) [-18,-1] | 6 | 185 | `xex` |
| WastelandNV | BoulderCity [12,5] | 1 | 182 | `xex1` |
| Interior | TestTilesetNVUshomeExamples "Tileset NV Ushome Examples" | 1 | 179 | `xex44` |
| Interior | ULMembersOnly "White Glove Members Only Section" | 2 | 178 | `xex30` |
| WastelandNV | PrimmCentral [-14,-13] | 1 | 175 | `xex43` |
| WastelandNV | (no EDID) [12,3] | 1 | 174 | `xex1` |
| Interior | Vault22d "Vault 22 - Common Areas" | 1 | 171 | `xex44` |
| Interior | 2ENCREmbassy01 "NCR Embassy" | 1 | 168 | `xex30` |
| WastelandNV | (no EDID) [-19,-2] | 6 | 168 | `xex` |
| WastelandNV | (no EDID) [7,17] | 1 | 167 | `xex31` |
| WastelandNV | (no EDID) [13,5] | 1 | 165 | `xex1` |
| WastelandNV | (no EDID) [-17,-1] | 6 | 159 | `xex` |
| WastelandNV | (no EDID) [6,18] | 1 | 157 | `xex32` |
| Interior | NellisGenerator "Nellis Array Generators" | 1 | 155 | `xex39` |
| Interior | Vault34b "Vault 34 - Reactor" | 1 | 154 | `xex39` |
| Interior | Vault74a "Vault 74" | 1 | 153 | `xex44` |
| WastelandNV | (no EDID) [11,5] | 1 | 149 | `xex1` |
| WastelandNV | Primm [-14,-14] | 1 | 149 | `xex43` |
| WastelandNV | (no EDID) [13,4] | 1 | 146 | `xex1` |
| WastelandNV | BlackMountainVillage2 [0,3] | 2 | 146 | `xex27` |
| WastelandNV | Goodsprings [-18,0] | 6 | 145 | `xex` |
| Interior | JacobstownNightstalkerCave "Nightstalker Lair" | 1 | 138 | `xex39` |
| WastelandNV | (no EDID) [-17,1] | 6 | 138 | `xex` |
| WastelandNV | GibsonScrapyard [5,-5] | 4 | 137 | `xex3` |
| WastelandNV | GoodspringsSource [-16,-5] | 1 | 135 | `xex5` |
| Interior | Lucky38CasinoFloor01 "Lucky 38 Floor 01 Casino" | 1 | 134 | `xex39` |
| WastelandNV | (no EDID) [-19,-1] | 6 | 133 | `xex` |
| Interior | NellisHangar1b "Hangar" | 2 | 132 | `xex29` |
| WastelandNV | (no EDID) [-28,22] | 3 | 132 | `xex29` |
| WastelandNV | BlackMountainSummit [0,1] | 2 | 132 | `xex27` |
| WastelandNV | (no EDID) [-13,-14] | 1 | 124 | `xex43` |
| WastelandNV | Jacobstown [-27,22] | 4 | 124 | `xex29` |
| Interior | 2ELVBStation "Las Vegas Boulevard Station" | 1 | 122 | `xex44` |
| WastelandNV | (no EDID) [5,-6] | 4 | 122 | `xex3` |
| Interior | NiptonHouse7Tinker "Nipton House" | 1 | 121 | `xex44` |
| TheStripWorld | (no EDID) [0,3] | 1 | 121 | `xex21` |
| WastelandNV | (no EDID) [-18,1] | 6 | 121 | `xex` |
| Interior | RepconHQ01 "REPCONN Office Main Floor" | 1 | 120 | `xex44` |
| WastelandNV | (no EDID) [6,16] | 3 | 120 | `xex29` |
| WastelandNV | (no EDID) [-17,0] | 6 | 119 | `xex` |
| WastelandNV | (no EDID) [-19,0] | 6 | 119 | `xex` |
| Interior | BisonSteve02 "The Bison Steve Hotel" | 1 | 116 | `xex39` |
| Interior | NewVegasMedicalClinicInterior "New Vegas Medical Clinic" | 1 | 116 | `xex44` |
| Interior | ULKitchen "Ultra-Luxe Kitchen" | 3 | 114 | `xex2` |
| Interior | HiddenValleyBunker3 "Hidden Valley Bunker" | 1 | 113 | `xex39` |
| Interior | ULPenthouse "Ultra-Luxe Penthouse" | 1 | 113 | `xex30` |
| WastelandNV | (no EDID) [-18,-2] | 6 | 111 | `xex` |
| WastelandNV | (no EDID) [12,2] | 1 | 111 | `xex1` |
| WastelandNV | (no EDID) [-18,-3] | 2 | 110 | `xex5` |
| Interior | FreesideMickandRalphs "Mick & Ralph's" | 1 | 109 | `xex39` |
| WastelandNV | (no EDID) [-26,22] | 4 | 109 | `xex29` |
| Interior | NiptonHouse8Paranoid "Nipton House" | 1 | 108 | `xex39` |
| WastelandNV | (no EDID) [-17,2] | 5 | 108 | `xex18` |
| WastelandNV | (no EDID) [-4,0] | 1 | 108 | `Fallout_Release_MemDebug.xex` |
| TheStripWorld | (no EDID) [1,-1] | 1 | 107 | `xex21` |
| WastelandNV | (no EDID) [-27,24] | 3 | 107 | `xex29` |
| WastelandNV | (no EDID) [-5,3] | 2 | 106 | `xex27` |
| Interior | TrainStationInterior "Train Station" | 1 | 104 | `xex39` |
| TheStripWorld | (no EDID) [-1,-1] | 1 | 104 | `xex21` |
| WastelandNV | (no EDID) [-15,-5] | 1 | 104 | `xex5` |
| WastelandNV | (no EDID) [-16,-4] | 1 | 103 | `xex5` |
| TheStripWorld | (no EDID) [-1,2] | 1 | 102 | `xex21` |
| WastelandNV | (no EDID) [11,4] | 1 | 102 | `xex1` |
| WastelandNV | (no EDID) [7,16] | 3 | 101 | `xex29` |
| WastelandNV | GibsonScrapYard [6,-5] | 4 | 100 | `xex3` |
| TheStripWorld | (no EDID) [0,-3] | 1 | 96 | `xex2` |
| WastelandNV | (no EDID) [-17,-4] | 1 | 96 | `xex5` |
| WastelandNV | (no EDID) [0,20] | 1 | 96 | `xex32` |
| TheStripWorld | (no EDID) [-1,1] | 1 | 95 | `xex21` |
| WastelandNV | (no EDID) [-16,0] | 6 | 94 | `xex` |
| WastelandNV | (no EDID) [-26,23] | 4 | 93 | `xex29` |
| Interior | CampMCTermInt04 "Camp McCarran Supply Shack" | 1 | 92 | `xex44` |
| TheStripWorld | (no EDID) [2,0] | 1 | 92 | `xex21` |
| WastelandNV | (no EDID) [-8,4] | 1 | 92 | `xex4` |
| WastelandNV | GriffithPeak [-24,22] | 5 | 92 | `xex32` |
| WastelandNV | (no EDID) [-10,-12] | 1 | 91 | `xex43` |
| FreesideWorld | (no EDID) [0,1] | 3 | 90 | `xex2` |
| WastelandNV | (no EDID) [-25,22] | 4 | 90 | `xex29` |
| WastelandNV | PrimmParkingLot [-14,-12] | 1 | 90 | `xex43` |
| Interior | GypsumAbandonedBuilding "Abandoned Building" | 1 | 89 | `xex44` |
| WastelandNV | (no EDID) [-1,4] | 2 | 89 | `xex27` |
| WastelandNV | (no EDID) [-28,24] | 3 | 89 | `xex29` |
| Interior | LegateCampTent "Legate's War Tent" | 1 | 88 | `xex44` |
| TheStripWorld | GomorrahTSW [-1,3] | 1 | 88 | `xex21` |
| Interior | FreesideAtomicWrangler2 "Atomic Wrangler" | 1 | 87 | `xex44` |
| Interior | Vault22b "Vault 22 - Oxygen Recycling" | 1 | 87 | `xex39` |
| TheStripWorld | StationTSW [1,0] | 1 | 87 | `xex21` |
| TheStripWorld | [Virtual -2,-1 TheStripWorld] [-2,-1] | 1 | 87 | `xex4` |
| WastelandNV | NellisDeadPaladins [7,30] | 2 | 87 | `xex27` |
| TheStripWorld | (no EDID) [1,3] | 1 | 85 | `xex21` |
| TheStripWorld | TheTopsTSW [-2,1] | 1 | 85 | `xex21` |
| WastelandNV | (no EDID) [-16,2] | 6 | 85 | `xex39` |
| WastelandNV | PrimmBisonHotel [-13,-13] | 1 | 85 | `xex43` |
| TheStripWorld | Vault21TSW [-2,2] | 1 | 84 | `xex21` |
| WastelandNV | (no EDID) [-26,24] | 3 | 84 | `xex29` |
| WastelandNV | (no EDID) [0,2] | 2 | 84 | `xex27` |
| FreesideWorld | FreesideNorthGateIntersection [0,0] | 3 | 83 | `xex` |
| Interior | BlackMountainRadio "Broadcast Building, 1st Floor" | 1 | 83 | `xex44` |
| Interior | TestAnddy "Anddy's Test Map" | 1 | 83 | `xex30` |
| Interior | TestAnddyExt "Anddy's Test Map (fake exterior)" | 1 | 83 | `xex44` |
| WastelandNV | (no EDID) [-5,0] | 1 | 83 | `Fallout_Release_MemDebug.xex` |
| WastelandNV | BlackMountainVillage1 [0,4] | 3 | 83 | `xex27` |
| Interior | TestQAItems | 1 | 82 | `xex44` |
| WastelandNV | BlackRockMountain [-1,3] | 2 | 81 | `xex27` |
| Interior | EDSubstation "Power Substation" | 1 | 80 | `xex44` |
| Interior | HooverDamIntLowerLevel "Hoover Dam Lower Level" | 1 | 80 | `xex30` |
| WastelandNV | (no EDID) [6,0] | 1 | 80 | `xex28` |
| WastelandNV | (no EDID) [6,19] | 1 | 80 | `xex32` |
| Interior | CampSearchlightFireStation02 "Searchlight Fire Station" | 1 | 79 | `xex44` |
| Interior | PrimmGenericHouse04 "Primm House" | 1 | 79 | `xex44` |
| Interior | TestTilesetMinesExamples "Tileset Mines Examples" | 1 | 79 | `xex44` |
| TheStripWorld | (no EDID) [0,0] | 1 | 79 | `xex21` |
| WastelandNV | (no EDID) [7,29] | 2 | 79 | `xex27` |
| Interior | NCRPrisonBlockA "Cell Block A" | 1 | 78 | `xex44` |
| Interior | PrimmGenericHouse03 "Primm House" | 1 | 78 | `xex44` |
| TheStripWorld | (no EDID) [-2,-1] | 1 | 78 | `xex21` |
| WastelandNV | (no EDID) [4,17] | 5 | 77 | `xex30` |
| WastelandNV | (no EDID) [5,18] | 1 | 77 | `xex32` |
| WastelandNV | GSCemetery [-16,3] | 1 | 77 | `xex` |
| TheStripWorld | (no EDID) [-1,0] | 1 | 76 | `xex21` |
| TheStripWorld | (no EDID) [-3,-2] | 1 | 76 | `xex4` |
| TheStripWorld | (no EDID) [1,1] | 1 | 76 | `xex21` |
| WastelandNV | (no EDID) [-13,-12] | 1 | 76 | `xex43` |
| Interior | FreesideAtomicWrangler "Atomic Wrangler" | 1 | 75 | `xex44` |
| TheStripWorld | (no EDID) [1,-3] | 1 | 75 | `xex2` |
| WastelandNV | (no EDID) [-25,24] | 3 | 75 | `xex29` |
| WastelandNV | (no EDID) [-1,23] | 1 | 74 | `xex4` |
| WastelandNV | (no EDID) [-15,1] | 6 | 74 | `xex5` |
| WastelandNV | (no EDID) [-28,23] | 3 | 74 | `xex29` |
| WastelandNV | (no EDID) [-9,4] | 1 | 74 | `xex4` |
| WastelandNV | (no EDID) [6,-6] | 2 | 74 | `xex3` |
| WastelandNV | (no EDID) [-17,-5] | 1 | 73 | `xex5` |
| Interior | 3CBSCave2 "Cave" | 1 | 72 | `xex44` |
| TheStripWorld | (no EDID) [0,4] | 1 | 72 | `xex21` |
| WastelandNV | (no EDID) [-1,7] | 2 | 72 | `xex20` |
| WastelandNV | (no EDID) [-10,2] | 1 | 72 | `xex4` |
| WastelandNV | (no EDID) [-18,2] | 5 | 72 | `xex` |
| WastelandNV | (no EDID) [-19,1] | 6 | 72 | `xex` |
| Interior | BlackMountainTreasure "Storage Building" | 1 | 71 | `xex44` |
| Interior | NellisHangar2 "Mess Hall & Munitions Storage" | 1 | 71 | `xex44` |
| Interior | TestDance "Mentats Test Level" | 2 | 71 | `xex39` |
| TheStripWorld | (no EDID) [2,2] | 1 | 71 | `xex21` |
| WastelandNV | (no EDID) [6,17] | 1 | 71 | `xex31` |
| WastelandNV | (no EDID) [7,31] | 4 | 71 | `xex29` |
| WastelandNV | BlackMountainRS [-2,4] | 2 | 71 | `xex27` |
| WastelandNV | GSCemetery [-16,1] | 6 | 71 | `xex` |
| TheStripWorld | (no EDID) [-3,-1] | 1 | 70 | `xex4` |
| TheStripWorld | (no EDID) [0,-1] | 1 | 70 | `xex21` |
| WastelandNV | (no EDID) [-15,2] | 9 | 70 | `xex5` |
| Interior | CampSearchlightChurchNCR "Searchlight NCR Storage" | 1 | 69 | `xex44` |
| Interior | GomorrahJoanaRoom "Joana's Room" | 1 | 69 | `xex44` |
| Interior | TestMentats "Mentats Test Level" | 1 | 69 | `xex44` |
| WastelandNV | (no EDID) [-8,7] | 1 | 69 | `xex4` |
| Interior | 1EUsonianHome01 "New Vegas Home" | 1 | 68 | `xex44` |
| TheStripWorld | (no EDID) [-1,4] | 1 | 68 | `xex21` |
| TheStripWorld | (no EDID) [0,1] | 1 | 68 | `xex21` |
| WastelandNV | (no EDID) [-18,-4] | 1 | 68 | `xex5` |
| WastelandNV | (no EDID) [13,2] | 1 | 68 | `xex1` |
| WastelandNV | CallvilleBay [16,16] | 1 | 68 | `xex42` |
| WastelandNV | (no EDID) [12,6] | 1 | 67 | `xex1` |
| WastelandNV | (no EDID) [5,19] | 1 | 67 | `xex32` |
| WastelandNV | Goodsprings [-17,-2] | 6 | 67 | `xex` |
| Interior | NellisWorkshop "Nellis Workshop" | 1 | 66 | `xex39` |
| TheStripWorld | (no EDID) [-2,3] | 1 | 66 | `xex21` |
| WastelandNV | (no EDID) [11,3] | 1 | 66 | `xex1` |
| WastelandNV | Sloan [-8,1] | 1 | 66 | `xex4` |
| TheStripWorld | (no EDID) [2,3] | 1 | 65 | `xex21` |
| WastelandNV | (no EDID) [-20,-2] | 1 | 65 | `xex` |
| WastelandNV | (no EDID) [-1,0] | 2 | 64 | `xex27` |
| WastelandNV | (no EDID) [-2,2] | 2 | 64 | `xex27` |
| WastelandNV | BlackMountain [1,4] | 1 | 64 | `xex24` |
| WastelandNV | HeliosOne [5,-3] | 4 | 64 | `xex24` |
| WastelandNV | RedRockDrugLab [-20,19] | 1 | 64 | `xex31` |
| Interior | AbandonedUsonianHome01 "Abandoned Home" | 1 | 63 | `xex44` |
| WastelandNV | (no EDID) [-15,-12] | 1 | 63 | `xex43` |
| WastelandNV | (no EDID) [4,-3] | 4 | 63 | `xex3` |
| WastelandNV | (no EDID) [11,2] | 1 | 62 | `xex1` |
| WastelandNV | (no EDID) [-1,5] | 2 | 61 | `xex27` |
| TheStripWorld | TheStripNorthGate [1,4] | 1 | 60 | `xex21` |
| WastelandNV | (no EDID) [-15,-13] | 1 | 60 | `xex43` |
| WastelandNV | (no EDID) [-15,0] | 6 | 60 | `xex` |
| WastelandNV | (no EDID) [-2,20] | 1 | 60 | `xex32` |
| WastelandNV | (no EDID) [-3,4] | 2 | 60 | `xex27` |
| WastelandNV | (no EDID) [3,17] | 2 | 60 | `xex30` |
| WastelandNV | SLGoodspringsCave [-15,-2] | 6 | 60 | `xex18` |
| WastelandNV | (no EDID) [-1,2] | 2 | 59 | `xex27` |
| WastelandNV | (no EDID) [-16,-1] | 6 | 59 | `xex5` |
| WastelandNV | BlackMountainStart [-3,3] | 2 | 59 | `xex27` |
| Interior | 3CBSRecreationOffice "Bitter Springs Recreation Office" | 1 | 58 | `xex39` |
| WastelandNV | (no EDID) [-17,-3] | 2 | 58 | `xex42` |
| WastelandNV | (no EDID) [-20,-1] | 1 | 58 | `xex` |
| WastelandNV | (no EDID) [-7,2] | 1 | 58 | `xex4` |
| Interior | RSFoxtrotInterior "Ranger Station Foxtrot" | 1 | 57 | `xex44` |
| Interior | TestJoshWeapons | 1 | 57 | `xex39` |
| TheStripWorld | (no EDID) [2,1] | 1 | 57 | `xex21` |
| WastelandNV | (no EDID) [-15,-1] | 6 | 57 | `xex` |
| WastelandNV | HELIOSOne [5,-1] | 4 | 57 | `xex3` |
| WastelandNV | SLPowderGangerCampSouth [-10,-10] | 1 | 57 | `xex43` |
| TheStripWorld | (no EDID) [2,-1] | 1 | 56 | `xex21` |
| WastelandNV | (no EDID) [-12,-14] | 1 | 56 | `xex43` |
| WastelandNV | (no EDID) [-15,-14] | 1 | 56 | `xex43` |
| WastelandNV | (no EDID) [-19,2] | 5 | 56 | `xex` |
| WastelandNV | (no EDID) [0,17] | 2 | 56 | `xex29` |
| FreesideWorld | (no EDID) [0,-3] | 1 | 55 | `xex4` |
| Interior | NovacMotelLobby "Dino Dee-lite Front Desk" | 1 | 55 | `xex44` |
| TheStripWorld | Lucky38TSW [1,2] | 1 | 55 | `xex21` |
| WastelandNV | (no EDID) [-1,1] | 2 | 55 | `xex27` |
| Interior | GSShackVictorInt "Goodsprings Victor's Shack Int" | 1 | 54 | `xex44` |
| WastelandNV | (no EDID) [-10,4] | 1 | 54 | `xex4` |
| WastelandNV | BlackMountainScenic [0,5] | 3 | 54 | `xex6` |
| Interior | NelsonHouse03 "Nelson House" | 1 | 53 | `xex44` |
| Interior | TestQANavMeshSmall | 1 | 53 | `xex44` |
| TheStripWorld | VStreetScriptStarter [0,2] | 1 | 53 | `xex21` |
| WastelandNV | (no EDID) [-12,-12] | 1 | 53 | `xex43` |
| WastelandNV | (no EDID) [-2,5] | 2 | 53 | `xex27` |
| WastelandNV | (no EDID) [-4,3] | 2 | 53 | `xex27` |
| WastelandNV | (no EDID) [-7,1] | 1 | 53 | `xex4` |
| WastelandNV | (no EDID) [4,19] | 1 | 53 | `xex32` |
| Interior | AudioTestLevel "Audio Test Level" | 2 | 52 | `xex39` |
| Interior | CampForlornHopeMedCenter "Camp Forlorn Hope Medical Center" | 1 | 52 | `xex44` |
| Interior | CottonwoodCoveStorage "Cottonwood Cove Storage" | 1 | 52 | `xex39` |
| WastelandNV | (no EDID) [-8,3] | 1 | 52 | `xex4` |
| WastelandNV | (no EDID) [3,-6] | 2 | 52 | `xex3` |
| WastelandNV | (no EDID) [3,16] | 4 | 52 | `xex28` |
| Interior | RSCharlieNVDESTROYED "Ranger Station Charlie" | 1 | 51 | `xex44` |
| WastelandNV | (no EDID) [-13,-11] | 1 | 51 | `xex43` |
| WastelandNV | (no EDID) [-15,-3] | 2 | 51 | `xex5` |
| WastelandNV | (no EDID) [7,-7] | 2 | 51 | `xex3` |
| WastelandNV | (no EDID) [-15,-11] | 1 | 50 | `xex43` |
| WastelandNV | (no EDID) [-5,1] | 2 | 50 | `xex27` |
| WastelandNV | (no EDID) [1,16] | 5 | 50 | `xex27` |
| WastelandNV | (no EDID) [4,16] | 5 | 50 | `xex27` |
| WastelandNV | (no EDID) [7,18] | 1 | 50 | `xex32` |
| Interior | 1ECisternA "North Cistern" | 1 | 49 | `xex44` |
| Interior | HooverDamIntIntakeTower02 "Hoover Dam Intake Tower 02" | 1 | 49 | `xex30` |
| WastelandNV | (no EDID) [-25,23] | 4 | 49 | `xex29` |
| WastelandNV | (no EDID) [2,19] | 1 | 49 | `xex32` |
| Interior | 3CBSCave3 "Cave" | 1 | 48 | `xex44` |
| Interior | GibsonScrapYardInterior "Gibson Garage" | 1 | 48 | `xex44` |
| WastelandNV | (no EDID) [-3,2] | 2 | 48 | `xex27` |
| WastelandNV | (no EDID) [2,17] | 2 | 48 | `xex29` |
| WastelandNV | (no EDID) [4,-5] | 4 | 48 | `xex3` |
| WastelandNV | (no EDID) [-16,-2] | 6 | 47 | `xex5` |
| WastelandNV | (no EDID) [-2,3] | 2 | 47 | `xex27` |
| WastelandNV | (no EDID) [-9,5] | 1 | 47 | `xex4` |
| WastelandNV | (no EDID) [2,18] | 1 | 47 | `xex31` |
| Interior | Usonianhome02Int "Usonianhome02int Template" | 1 | 45 | `xex44` |
| WastelandNV | (no EDID) [-11,1] | 1 | 45 | `xex4` |
| WastelandNV | (no EDID) [-2,1] | 2 | 45 | `xex27` |
| WastelandNV | (no EDID) [-26,21] | 4 | 45 | `xex29` |
| WastelandNV | (no EDID) [-9,1] | 3 | 45 | `xex4` |
| WastelandNV | (no EDID) [4,-2] | 4 | 45 | `xex6` |
| Interior | BCSaloonInterior "Big Horn Saloon" | 1 | 44 | `xex39` |
| Interior | NovacGenericHouse01 "Novac House" | 1 | 44 | `xex39` |
| WastelandNV | (no EDID) [-10,-11] | 1 | 44 | `xex43` |
| Interior | FFEVendorChests "FFE vendor chests" | 1 | 43 | `xex44` |
| WastelandNV | (no EDID) [-9,2] | 1 | 43 | `xex4` |
| WastelandNV | (no EDID) [3,-5] | 4 | 43 | `xex3` |
| WastelandNV | (no EDID) [3,8] | 1 | 43 | `xex44` |
| WastelandNV | (no EDID) [7,0] | 1 | 43 | `xex19` |
| Interior | NovacAbandonedShack "Abandoned Shack" | 1 | 42 | `xex44` |
| Interior | Vault19b "Vault 19 - Living Quarters" | 2 | 42 | `xex39` |
| WastelandNV | (no EDID) [-10,-13] | 1 | 42 | `xex43` |
| WastelandNV | (no EDID) [-27,21] | 4 | 42 | `xex29` |
| WastelandNV | (no EDID) [-3,5] | 2 | 42 | `xex27` |
| WastelandNV | (no EDID) [-4,4] | 2 | 42 | `xex27` |
| WastelandNV | (no EDID) [-5,5] | 2 | 42 | `xex27` |
| WastelandNV | (no EDID) [0,6] | 1 | 42 | `xex27` |
| WastelandNV | (no EDID) [3,-1] | 4 | 42 | `xex24` |
| WastelandNV | (no EDID) [4,8] | 1 | 42 | `xex44` |
| WastelandNV | (no EDID) [-12,-10] | 1 | 41 | `xex43` |
| WastelandNV | (no EDID) [3,-2] | 4 | 41 | `xex6` |
| WastelandNV | (no EDID) [7,-1] | 4 | 41 | `xex6` |
| WastelandNV | (no EDID) [2,4] | 1 | 40 | `xex24` |
| WastelandNV | (no EDID) [5,16] | 5 | 40 | `xex27` |
| WastelandNV | NellisAFBEntrance [6,29] | 2 | 40 | `xex27` |
| Interior | 1EEastPumpStation "East Pump Station" | 1 | 39 | `xex29` |
| WastelandNV | (no EDID) [-11,3] | 1 | 39 | `xex4` |
| WastelandNV | (no EDID) [-12,-11] | 1 | 39 | `xex43` |
| WastelandNV | (no EDID) [1,5] | 2 | 39 | `xex6` |
| WastelandNV | (no EDID) [11,6] | 1 | 39 | `xex1` |
| WastelandNV | (no EDID) [4,-6] | 2 | 39 | `xex3` |
| Interior | NelsonBarracks01 "Nelson Barracks" | 1 | 38 | `xex44` |
| WastelandNV | (no EDID) [-10,3] | 1 | 38 | `xex4` |
| WastelandNV | (no EDID) [-11,4] | 1 | 38 | `xex4` |
| WastelandNV | (no EDID) [-12,-13] | 1 | 38 | `xex43` |
| WastelandNV | (no EDID) [-21,1] | 1 | 38 | `xex` |
| WastelandNV | (no EDID) [7,-5] | 4 | 38 | `xex3` |
| WastelandNV | (no EDID) [-11,-11] | 1 | 37 | `xex43` |
| WastelandNV | (no EDID) [-16,-3] | 3 | 37 | `xex5` |
| WastelandNV | (no EDID) [-7,3] | 1 | 37 | `xex4` |
| WastelandNV | (no EDID) [-8,2] | 1 | 37 | `xex4` |
| Interior | NiptonHotel "Nipton Hotel" | 1 | 36 | `xex44` |
| Interior | NovacGenericHouse02 "Novac House" | 1 | 36 | `xex39` |
| WastelandNV | (no EDID) [-14,-11] | 1 | 36 | `xex43` |
| WastelandNV | (no EDID) [-20,0] | 1 | 36 | `xex` |
| WastelandNV | (no EDID) [14,3] | 1 | 36 | `xex1` |
| WastelandNV | (no EDID) [6,30] | 1 | 36 | `xex28` |
| WastelandNV | (no EDID) [7,4] | 1 | 36 | `Fallout_Release_MemDebug.xex` |
| WastelandNV | QuarryJunction [-9,3] | 1 | 36 | `xex4` |
| WastelandNV | (no EDID) [10,4] | 1 | 35 | `xex1` |
| WastelandNV | (no EDID) [7,-2] | 4 | 35 | `xex6` |
| WastelandNV | SafehouseNCR [-1,6] | 1 | 35 | `xex27` |
| FreesideWorld | (no EDID) [1,-4] | 1 | 34 | `xex4` |
| WastelandNV | (no EDID) [-11,2] | 1 | 34 | `xex4` |
| WastelandNV | (no EDID) [-19,-3] | 2 | 34 | `xex5` |
| WastelandNV | (no EDID) [-10,1] | 1 | 33 | `xex4` |
| WastelandNV | (no EDID) [-24,23] | 5 | 33 | `xex32` |
| WastelandNV | (no EDID) [-7,4] | 1 | 33 | `xex4` |
| WastelandNV | (no EDID) [1,17] | 2 | 33 | `xex29` |
| FreesideWorld | (no EDID) [0,-2] | 1 | 32 | `xex4` |
| Interior | NovacJeannieMayHouse "Jeannie May Crawford's House" | 1 | 32 | `xex44` |
| WastelandNV | (no EDID) [-13,-10] | 1 | 32 | `xex43` |
| WastelandNV | (no EDID) [-13,-9] | 1 | 32 | `xex43` |
| WastelandNV | (no EDID) [-15,-10] | 1 | 32 | `xex43` |
| WastelandNV | (no EDID) [2,16] | 5 | 32 | `xex27` |
| WastelandNV | (no EDID) [2,5] | 1 | 32 | `xex6` |
| WastelandNV | (no EDID) [5,8] | 1 | 32 | `xex44` |
| WastelandNV | (no EDID) [6,-2] | 4 | 32 | `xex6` |
| WastelandNV | (no EDID) [-11,-10] | 1 | 31 | `xex43` |
| WastelandNV | (no EDID) [-12,-9] | 1 | 31 | `xex43` |
| WastelandNV | (no EDID) [-14,-9] | 1 | 31 | `xex43` |
| WastelandNV | (no EDID) [-15,-4] | 2 | 31 | `xex5` |
| WastelandNV | (no EDID) [5,29] | 2 | 31 | `xex27` |
| WastelandNV | (no EDID) [-20,2] | 1 | 30 | `xex` |
| WastelandNV | (no EDID) [5,-4] | 4 | 30 | `xex3` |
| WastelandNV | (no EDID) [7,-6] | 2 | 30 | `xex7` |
| WastelandNV | (no EDID) [2,1] | 1 | 29 | `xex28` |
| Interior | TestQAHairM | 1 | 28 | `xex44` |
| WastelandNV | (no EDID) [-10,-9] | 1 | 28 | `xex43` |
| WastelandNV | (no EDID) [-15,-9] | 1 | 28 | `xex43` |
| WastelandNV | (no EDID) [1,20] | 1 | 28 | `xex32` |
| WastelandNV | (no EDID) [6,-1] | 4 | 28 | `xex6` |
| WastelandNV | (no EDID) [6,8] | 1 | 28 | `xex44` |
| WastelandNV | MojaveOutpost [-21,-25] | 3 | 28 | `xex25` |
| Interior | RSDeltaInterior "Ranger Station Delta" | 1 | 27 | `xex39` |
| TheStripWorld | (no EDID) [-2,0] | 1 | 27 | `xex21` |
| WastelandNV | (no EDID) [-28,21] | 3 | 27 | `xex29` |
| Interior | HooverDamIntArizonaSpillway "Arizona Spillway Tunnel" | 1 | 26 | `xex44` |
| Interior | NovacMotelRoomQueen1 "Motel Room" | 1 | 26 | `xex44` |
| WastelandNV | (no EDID) [-14,-4] | 1 | 26 | `xex29` |
| WastelandNV | (no EDID) [-19,19] | 1 | 26 | `xex31` |
| WastelandNV | (no EDID) [10,3] | 1 | 26 | `xex1` |
| WastelandNV | (no EDID) [9,6] | 1 | 26 | `xex1` |
| Interior | NovacMotelRoomTwin1 "Motel Room" | 1 | 25 | `xex39` |
| Interior | RanchHouseInterior03 "RanchHouseInterior03 Template" | 1 | 25 | `xex44` |
| Interior | TestTilesetVaultRuinedHallsPieces "Tileset Vault Ruined Halls Pieces" | 1 | 25 | `xex44` |
| WastelandNV | (no EDID) [-2,6] | 1 | 25 | `xex27` |
| WastelandNV | (no EDID) [10,5] | 1 | 25 | `xex1` |
| WastelandNV | (no EDID) [-21,2] | 1 | 24 | `xex` |
| WastelandNV | (no EDID) [-7,6] | 1 | 24 | `xex4` |
| WastelandNV | (no EDID) [-21,-26] | 3 | 23 | `xex28` |
| WastelandNV | (no EDID) [-4,2] | 2 | 23 | `xex27` |
| WastelandNV | (no EDID) [-9,7] | 5 | 23 | `xex4` |
| WastelandNV | (no EDID) [1,19] | 1 | 23 | `xex32` |
| Interior | GSHouseInterior03 "Goodsprings Home" | 1 | 22 | `xex44` |
| WastelandNV | (no EDID) [-11,-9] | 1 | 22 | `xex43` |
| WastelandNV | (no EDID) [-14,-10] | 1 | 22 | `xex43` |
| WastelandNV | (no EDID) [-4,1] | 2 | 22 | `xex27` |
| WastelandNV | (no EDID) [13,6] | 1 | 22 | `xex1` |
| WastelandNV | (no EDID) [6,-3] | 4 | 22 | `xex6` |
| WastelandNV | (no EDID) [7,-4] | 4 | 22 | `xex3` |
| WastelandNV | (no EDID) [-4,6] | 1 | 21 | `xex27` |
| WastelandNV | (no EDID) [-5,2] | 2 | 21 | `xex27` |
| WastelandNV | (no EDID) [-5,4] | 2 | 21 | `xex27` |
| WastelandNV | (no EDID) [6,-4] | 4 | 21 | `xex3` |
| Interior | NellisBarracks03 "Nellis Women's Barracks" | 1 | 20 | `xex44` |
| WastelandNV | (no EDID) [-7,7] | 1 | 20 | `xex4` |
| WastelandNV | (no EDID) [1,6] | 1 | 20 | `xex27` |
| WastelandNV | (no EDID) [-11,5] | 1 | 19 | `xex4` |
| WastelandNV | (no EDID) [10,6] | 1 | 19 | `xex1` |
| WastelandNV | (no EDID) [4,0] | 1 | 19 | `xex28` |
| WastelandNV | (no EDID) [5,-2] | 4 | 19 | `xex3` |
| Interior | NellisBarracks05 "Nellis Women's Barracks" | 1 | 18 | `xex39` |
| Interior | TestArmorFaction "Hoover Dam Battle Spawner" | 1 | 18 | `xex39` |
| WastelandNV | (no EDID) [6,31] | 3 | 18 | `xex28` |
| WastelandNV | MountainTest [-21,0] | 1 | 18 | `xex` |
| WastelandNV | (no EDID) [-8,6] | 1 | 17 | `xex4` |
| WastelandNV | (no EDID) [-9,6] | 1 | 17 | `xex4` |
| WastelandNV | (no EDID) [7,19] | 2 | 17 | `xex32` |
| WastelandNV | (no EDID) [7,1] | 1 | 17 | `xex19` |
| FreesideWorld | FreesideKingsCorner [0,-1] | 4 | 16 | `xex4` |
| FreesideWorld | [Virtual 0,1 FreesideWorld] [0,1] | 1 | 16 | `xex1` |
| TheStripWorld | (no EDID) [0,-4] | 1 | 16 | `xex2` |
| WastelandNV | (no EDID) [-1,19] | 1 | 16 | `xex32` |
| WastelandNV | (no EDID) [-2,19] | 1 | 16 | `xex32` |
| WastelandNV | (no EDID) [-4,5] | 2 | 16 | `xex27` |
| WastelandNV | (no EDID) [-8,5] | 1 | 16 | `xex4` |
| WastelandNV | (no EDID) [7,-3] | 4 | 16 | `xex6` |
| Interior | PrimmNashResidence "Nash Residence" | 1 | 15 | `xex44` |
| TheStripWorld | (no EDID) [1,-4] | 1 | 15 | `xex2` |
| WastelandNV | (no EDID) [-11,-12] | 1 | 15 | `xex43` |
| WastelandNV | (no EDID) [-15,3] | 1 | 15 | `xex` |
| WastelandNV | (no EDID) [-22,-26] | 3 | 15 | `xex25` |
| WastelandNV | (no EDID) [3,-3] | 4 | 15 | `xex6` |
| Interior | CraftsmanHomesInterior01 "Craftsman Home 01 Template" | 1 | 14 | `xex44` |
| Interior | NovacVargasRoom "Manny Vargas' Room" | 1 | 14 | `xex44` |
| Interior | TestTilesetCaveBalconyPieces "Tileset Cave Balcony Pieces" | 1 | 14 | `xex44` |
| WastelandNV | (no EDID) [-10,5] | 1 | 14 | `xex4` |
| WastelandNV | (no EDID) [-10,7] | 1 | 14 | `xex4` |
| WastelandNV | (no EDID) [14,4] | 1 | 14 | `xex1` |
| WastelandNV | (no EDID) [7,5] | 1 | 14 | `xex27` |
| WastelandNV | RSFoxtrot [-17,22] | 2 | 14 | `xex20` |
| Interior | NellisBarracks04 "Nellis Children's Barracks" | 1 | 13 | `xex30` |
| Interior | NiptonHouse3 "Nipton House" | 1 | 13 | `xex39` |
| WastelandNV | (no EDID) [-3,6] | 1 | 13 | `xex27` |
| WastelandNV | (no EDID) [-7,8] | 1 | 13 | `xex4` |
| WastelandNV | (no EDID) [0,8] | 1 | 13 | `xex28` |
| Interior | NellisHangar1 "Hangar" | 1 | 12 | `xex44` |
| Interior | TestTilesetFacilityHallBigPieces "Tileset Facility HallBig Pieces" | 1 | 12 | `xex44` |
| WastelandNV | (no EDID) [-14,2] | 6 | 12 | `xex44` |
| WastelandNV | (no EDID) [-18,-5] | 1 | 12 | `xex5` |
| WastelandNV | (no EDID) [-24,24] | 4 | 12 | `xex29` |
| WastelandNV | (no EDID) [-3,0] | 1 | 12 | `Fallout_Release_MemDebug.xex` |
| WastelandNV | (no EDID) [-8,8] | 1 | 12 | `xex4` |
| WastelandNV | (no EDID) [3,-4] | 4 | 12 | `xex3` |
| WastelandNV | [Virtual 6,15 WastelandNV] [6,15] | 1 | 12 | `xex28` |
| FreesideWorld | [Virtual 0,1 FreesideWorld] [0,1] | 1 | 11 | `xex` |
| Interior | HELIOSOneTower "Solar Collection Tower" | 1 | 11 | `xex29` |
| Interior | QJBarracks "Worker Barracks" | 1 | 11 | `xex39` |
| WastelandNV | (no EDID) [-11,-13] | 1 | 11 | `xex43` |
| WastelandNV | (no EDID) [-11,-15] | 1 | 11 | `xex43` |
| WastelandNV | (no EDID) [10,2] | 1 | 11 | `xex1` |
| WastelandNV | (no EDID) [3,5] | 1 | 11 | `xex6` |
| WastelandNV | CaravanSacked03 [2,23] | 1 | 11 | `xex28` |
| WastelandNV | (no EDID) [-11,-14] | 1 | 10 | `xex43` |
| WastelandNV | (no EDID) [3,29] | 1 | 10 | `xex28` |
| WastelandNV | (no EDID) [4,-1] | 4 | 10 | `xex3` |
| WastelandNV | (no EDID) [4,-4] | 4 | 10 | `xex3` |
| WastelandNV | SLAbandonedShack03 [4,29] | 2 | 10 | `xex28` |
| Interior | QJOffice "Mining Office" | 1 | 9 | `xex44` |
| WastelandNV | (no EDID) [-2,7] | 2 | 9 | `xex20` |
| WastelandNV | (no EDID) [-7,5] | 1 | 9 | `xex4` |
| WastelandNV | (no EDID) [18,19] | 1 | 9 | `xex42` |
| Interior | NellisSchool "Nellis Schoolhouse" | 1 | 8 | `xex39` |
| TheStripWorld | (no EDID) [-1,5] | 1 | 8 | `xex21` |
| TheStripWorld | (no EDID) [-4,-1] | 1 | 8 | `xex4` |
| TheStripWorld | (no EDID) [2,-4] | 1 | 8 | `xex2` |
| WastelandNV | (no EDID) [-2,0] | 1 | 8 | `Fallout_Release_MemDebug.xex` |
| WastelandNV | (no EDID) [-20,-25] | 3 | 8 | `xex25` |
| WastelandNV | (no EDID) [-20,1] | 1 | 8 | `xex` |
| WastelandNV | (no EDID) [-20,20] | 1 | 8 | `xex31` |
| WastelandNV | (no EDID) [-21,19] | 1 | 8 | `xex31` |
| WastelandNV | (no EDID) [-9,-9] | 1 | 8 | `xex43` |
| WastelandNV | (no EDID) [0,16] | 5 | 8 | `xex31` |
| Interior | HooverDam "Hoover Dam Global Dummy Cell" | 1 | 7 | `xex39` |
| WastelandNV | (no EDID) [-13,-4] | 1 | 7 | `xex29` |
| WastelandNV | (no EDID) [-6,8] | 1 | 7 | `xex4` |
| Interior | 4BRRCPapasCabin "Great Khan Longhouse" | 1 | 6 | `xex44` |
| Interior | NiptonHouse2 "Nipton House" | 1 | 6 | `xex39` |
| WastelandNV | (no EDID) [-10,6] | 1 | 6 | `xex4` |
| WastelandNV | (no EDID) [-20,-26] | 3 | 6 | `xex28` |
| WastelandNV | (no EDID) [-21,-1] | 1 | 6 | `xex` |
| WastelandNV | (no EDID) [-3,1] | 2 | 6 | `xex27` |
| WastelandNV | (no EDID) [14,2] | 1 | 6 | `xex1` |
| WastelandNV | (no EDID) [7,-8] | 2 | 6 | `xex7` |
| WastelandNV | RuinsOfJean [-14,-5] | 1 | 6 | `xex5` |
| FreesideWorld | (no EDID) [0,2] | 1 | 5 | `xex2` |
| WastelandNV | (no EDID) [-1,20] | 1 | 5 | `xex32` |
| WastelandNV | (no EDID) [5,31] | 3 | 5 | `xex30` |
| WastelandNV | [Virtual -14,2 WastelandNV] [-14,2] | 2 | 5 | `xex28` |
| FreesideWorld | [Virtual 1,-3] [1,-3] | 1 | 4 | `xex4` |
| TheStripWorld | (no EDID) [-4,-2] | 1 | 4 | `xex4` |
| TheStripWorld | (no EDID) [3,-1] | 1 | 4 | `xex21` |
| TheStripWorld | (no EDID) [3,0] | 2 | 4 | `xex21` |
| TheStripWorld | (no EDID) [3,2] | 1 | 4 | `xex21` |
| WastelandNV | (no EDID) [-19,20] | 1 | 4 | `xex31` |
| WastelandNV | (no EDID) [-22,23] | 2 | 4 | `xex30` |
| WastelandNV | (no EDID) [-23,23] | 2 | 4 | `xex30` |
| WastelandNV | (no EDID) [0,30] | 2 | 4 | `xex27` |
| WastelandNV | (no EDID) [3,0] | 1 | 4 | `xex28` |
| WastelandNV | GriffithPeak [-25,21] | 4 | 4 | `xex29` |
| Interior | UnderpassStClair "Carlyle St. Clair's House" | 1 | 3 | `xex39` |
| WastelandNV | (no EDID) [-18,-24] | 3 | 3 | `xex25` |
| WastelandNV | (no EDID) [-19,-24] | 3 | 3 | `xex25` |
| WastelandNV | (no EDID) [2,30] | 1 | 3 | `xex27` |
| WastelandNV | (no EDID) [4,31] | 1 | 3 | `xex29` |
| WastelandNV | GS15 [-14,-3] | 1 | 3 | `xex42` |
| CampMcCarranWorld | (no EDID) [5,0] | 1 | 2 | `xex2` |
| Interior | HooverDamIntIntakeTower03 "Hoover Dam Intake Tower 03" | 1 | 2 | `xex29` |
| TheStripWorld | (no EDID) [-4,-3] | 1 | 2 | `xex4` |
| TheStripWorld | (no EDID) [2,4] | 1 | 2 | `xex21` |
| TheStripWorld | (no EDID) [3,-3] | 1 | 2 | `xex21` |
| TheStripWorld | (no EDID) [3,1] | 1 | 2 | `xex21` |
| TheStripWorld | (no EDID) [3,3] | 1 | 2 | `xex21` |
| WastelandNV | (no EDID) [-17,-24] | 3 | 2 | `xex25` |
| WastelandNV | (no EDID) [-18,-23] | 3 | 2 | `xex25` |
| WastelandNV | (no EDID) [-22,-25] | 3 | 2 | `xex25` |
| WastelandNV | (no EDID) [-23,-27] | 1 | 2 | `xex28` |
| WastelandNV | (no EDID) [-24,21] | 5 | 2 | `xex29` |
| WastelandNV | (no EDID) [0,0] | 6 | 2 | `xex27` |
| WastelandNV | (no EDID) [1,30] | 2 | 2 | `xex27` |
| WastelandNV | (no EDID) [5,17] | 2 | 2 | `xex29` |
| WastelandNV | (no EDID) [8,7] | 1 | 2 | `xex1` |
| WastelandNV | 188TradingPost [7,7] | 1 | 2 | `xex1` |
| FreesideWorld | (no EDID) [-1,-3] | 1 | 1 | `xex4` |
| Interior | FreesideAtomicWranglerRoom "Atomic Wrangler" | 1 | 1 | `xex44` |
| Interior | HiddenValleyBunker2 "Hidden Valley Bunker" | 1 | 1 | `xex29` |
| Interior | Vault22a "Vault 22 - Entrance Hall" | 1 | 1 | `xex39` |
| TheStripWorld | (no EDID) [-6,-3] | 1 | 1 | `xex4` |
| TheStripWorld | (no EDID) [3,-2] | 1 | 1 | `xex21` |
| TheStripWorld | (no EDID) [3,4] | 1 | 1 | `xex21` |
| WastelandNV | (no EDID) [-19,-25] | 3 | 1 | `xex25` |
| WastelandNV | (no EDID) [-21,24] | 1 | 1 | `xex31` |
| WastelandNV | (no EDID) [-22,-27] | 1 | 1 | `xex28` |
| WastelandNV | (no EDID) [-23,-26] | 1 | 1 | `xex28` |
| WastelandNV | (no EDID) [-23,21] | 2 | 1 | `xex30` |
| WastelandNV | (no EDID) [-6,-26] | 1 | 1 | `xex44` |
| WastelandNV | (no EDID) [4,30] | 1 | 1 | `xex27` |
| WastelandNV | (no EDID) [6,34] | 1 | 1 | `xex44` |
| WastelandNV | (no EDID) [7,11] | 1 | 1 | `xex44` |
| WastelandNV | (no EDID) [7,8] | 1 | 1 | `xex1` |
| WastelandNV | (no EDID) [8,-7] | 1 | 1 | `xex3` |
| WastelandNV | NCRSharecropperFarms [-3,20] | 1 | 1 | `xex32` |
| WastelandNV | PrimmHouses2 [-13,-15] | 1 | 1 | `xex43` |
| WastelandNV | PumpStationEast [1,21] | 1 | 1 | `xex32` |
| WastelandNV | SunsetSarsaparillaHeadquarters [-10,20] | 1 | 1 | `xex44` |

