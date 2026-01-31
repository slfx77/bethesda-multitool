# Research & Improvement Plan: Memory Dump Report Enhancements

## Overview

This plan covers: (A) diagnostic extraction results, (B) CSV conversion of all reports with multi-row RowType format, (C) runtime C++ struct reading via the hash table, (D) new data type implementations, and (E) heightmap improvements.

**Key architectural decision (resolved)**: The current ESM scanner finds only a fraction of game data as raw ESM records in memory (e.g., 7 NPC* records vs ~3000+ in the game). The game's `AllFormsByEditorID` hash table contains ~48K runtime C++ objects with exact file offsets. We will use a **two-track approach**: ESM scanning for types with good coverage (CELL, LAND, INFO, REFR) + runtime struct reading via hash table entries for types with poor ESM coverage (NPC*, WEAP, ARMO, QUST, etc.).

---

## PDB Research Summary (Completed)

### Runtime Object Layouts (for struct-based scanning)

Each TESForm-derived class has a vtable pointer at offset 0 and a FormID at a known offset. The game allocates these on the heap as C++ objects. Memory dumps capture the full heap.

**Scanning approach**: Find objects by validating FormID at known offsets + checking struct size patterns + validating cross-references (e.g., Race pointer in NPC should point to a valid TESRace).

| Record Type | ESM Sig | Runtime Class   | PDB Size     | Key Data Offsets                                     |
| ----------- | ------- | --------------- | ------------ | ---------------------------------------------------- |
| NPC\_       | NPC\_   | TESNPC          | 492 (508 rt) | FormID@16, ACBS stats, Race ptr, Factions list@0x1DC |
| WEAP        | WEAP    | TESObjectWEAP   | 908          | FormID@16, combat stats, ammo ptr, weapon type       |
| QUST        | QUST    | TESQuest        | 108          | FormID@16, flags@0x3C, priority@0x3D, stages@0x44    |
| CONT        | CONT    | TESObjectCONT   | 156          | FormID@16, TESContainer@0x04 with BSSimpleList       |
| FACT        | FACT    | TESFaction      | 76           | FormID@16, flags@0x34, ranks@0x3C                    |
| GMST        | GMST    | Setting         | 12           | Union value@0x00, key name@0x08                      |
| NOTE        | NOTE    | BGSNote         | 128          | FormID@16, note text, note type                      |
| BOOK        | BOOK    | TESObjectBOOK   | 196          | FormID@16, text content, value/weight                |
| TERM        | TERM    | BGSTerminal     | 168          | FormID@16, menu items (120 bytes each), header text  |
| INFO        | INFO    | TESTopicInfo    | 80           | FormID@16, quest@0x48, speaker@0x3C, conditions      |
| DIAL        | DIAL    | TESTopic        | 72           | FormID@16, topic type, quest link                    |
| MISC        | MISC    | TESObjectMISC   | 172          | FormID@16, value, weight, icon                       |
| ALCH        | ALCH    | AlchemyItem     | 216          | FormID@16, effects, addiction, value/weight          |
| AMMO        | AMMO    | TESAmmo         | 220          | FormID@16, projectile, speed, damage mod             |
| ARMO        | ARMO    | TESObjectARMO   | 400          | FormID@16, armor rating, value/weight                |
| KEYM        | KEYM    | TESKey          | 172          | FormID@16, value, weight                             |
| LVLI        | LVLI    | TESLevItem      | 76 (0x4C)    | TESLeveledList@0x30 with entries                     |
| LVLC        | LVLC    | TESLevCreature  | 112 (0x70)   | TESLeveledList@0x30, template@0x6C                   |
| LVLN        | LVLN    | TESLevCharacter | 112 (0x70)   | TESLeveledList@0x30, template@0x6C                   |
| LAND        | LAND    | TESObjectLAND   | 44 (60 rt)   | pLoadedData@+56 -> LoadedLandData(164 bytes)         |

### LoadedLandData (164 bytes / 0xA4)

```
Offset  Size  Type              Field
0       4     NiNode**          ppMesh
4       4     NiPoint3**        ppVertices
8       4     NiPoint3**        ppNormals
12      4     void*             ppColorsA
16      16    bool[16]          ppNormalsSet
20      4     NiLinesPtr        spBorder
24      8     NiPoint2          HeightExtents {min, max}
32      16    TESLandTexture*[4] pDefQuadTexture
48      16    Textures*[4]       pQuadTextureArray
64      16    float**[4]         ppPercentArrays
80      4     void*             pMoppCode
84      64    GrassMap[64]      pmGrassMap
148     4     void*             spLandRB
152     4     int32             iCellX        ** KEY FOR STITCHING **
156     4     int32             iCellY        ** KEY FOR STITCHING **
160     4     float32           fBaseHeight   ** KEY FOR STITCHING **
```

### TESLeveledList (at offset 0x30 in leveled list records)

```
Offset  Size  Type                     Field
0x00    4     void*                    vfptr
0x04    8     BSSimpleList<LEVELED_OBJECT*> leveledList
0x0C    1     uint8                    cChanceNone
0x0D    1     uint8                    cLLFlags
0x10    4     TESGlobal*               pChanceGlobal
```

### LEVELED_OBJECT_FILE (ESM LVLO subrecord, 12 bytes)

```
0x00    uint16   sLevel
0x02    uint16   (padding)
0x04    uint32   iFormID
0x08    uint16   sCount
0x0A    uint16   (padding)
```

### MapMarkerData (20 bytes)

```
0       12    BSStringT<char> LocationName
12      1     uint8           cFlags
13      1     uint8           cOriginalFlags
14      2     uint16          sType (MARKER_TYPE: 0=None..14=Vault)
16      4     TESForm*        pReputation
```

### MARKER_TYPE Enum

```
0=NONE, 1=CITY, 2=SETTLEMENT, 3=ENCAMPMENT, 4=NATURAL_LANDMARK,
5=CAVE, 6=FACTORY, 7=MONUMENT, 8=MILITARY, 9=OFFICE,
10=RUINS_TOWN, 11=RUINS_URBAN, 12=RUINS_SEWER, 13=METRO, 14=VAULT
```

---

## Part A: Diagnostic Results (Completed During Planning)

### A1. Dump File Examination Results

**Sample dump**: `Fallout_Release_Beta.xex3.dmp` (200.4 MB), one of 44 dumps (146-214 MB each).

**ESM record signatures found in dump** (raw 4-byte header scanning):
| Record Type | ESM Records Found | Notes |
|-------------|-------------------|-------|
| INFO | 682 | Dialog responses - abundant |
| REFR | 255 | Placed objects - abundant |
| CONT | 178 | Containers - good coverage |
| LAND | 84 | Landscape - good for heightmaps |
| CELL | 63 | Cells - good coverage |
| WEAP | 34 | Weapons - **low** (game has ~1200+) |
| NOTE | 24 | Notes - moderate |
| FACT | 16 | Factions - low |
| TERM | 16 | Terminals - low |
| NPC\_ | 7 | NPCs - **very low** (game has ~3000+) |
| QUST | 1 | Quests - **almost none** (game has ~300+) |
| LVLI | 1 | Leveled items - almost none |
| LVLC | 1 | Leveled creatures - almost none |

**Conclusion**: Most game data does NOT persist as raw ESM records in memory. After the game engine loads ESM data, it creates runtime C++ objects and likely frees the raw ESM buffers. The current ESM scanner catches only fragments that happen to remain in memory. **Runtime struct reading via the hash table is essential** for comprehensive data extraction.

---

## Part B: Convert All Reports to CSV (Multi-Row RowType Format)

### B1. CSV Architecture

All existing text reports (.txt) will be converted to CSV format. For records with nested sub-lists (factions, inventory, stages, etc.), use a `RowType` column to distinguish main records from sub-items.

**General CSV pattern**:

```csv
RowType,FormID,EditorID,Name,[type-specific columns...],[sub-item columns...]
NPC,0x00123456,VFSJosephBNash,Joseph B. Nash,15,200,100,...,,,,
FACTION,0x00123456,,,,,,,0x0001234,NCRFaction,0
INVENTORY,0x00123456,,,,,,,0x000ABCD,Weapon10mmPistol,1
SPELL,0x00123456,,,,,,,0x000EF01,AbPerkCowboyDamage,
```

Each RowType section:

- Main record row: All type-specific columns populated, sub-item columns empty
- Sub-item rows: FormID matches parent, type-specific columns empty, sub-item columns populated

### B2. CSV Report Definitions

**npcs.csv** (currently npcs.txt):

```
RowType,FormID,EditorID,Name,Level,FatigueBase,BarterGold,SpeedMult,Karma,Disposition,CalcMin,CalcMax,Flags,RaceFormID,RaceName,ClassFormID,ClassName,ScriptFormID,VoiceTypeFormID,TemplateFormID,Endianness,Offset,SubFormID,SubName,SubDetail
NPC,...
FACTION,...(FactionFormID,FactionName,Rank)
SPELL,...(SpellFormID,SpellName)
INVENTORY,...(ItemFormID,ItemName,Count)
PACKAGE,...(PackageFormID,PackageName)
```

**weapons.csv** (currently weapons.txt):

```
RowType,FormID,EditorID,Name,WeaponType,Damage,DPS,FireRate,ClipSize,MinRange,MaxRange,Spread,MinSpread,Drift,StrReq,SkillReq,CritDamage,CritChance,CritEffectFormID,Value,Weight,Health,AmmoFormID,AmmoName,APCost,ModelPath,IconPath,Endianness,Offset
```

(No sub-rows needed - all data is flat)

**quests.csv** (currently quests.txt):

```
RowType,FormID,EditorID,Name,Flags,Priority,ScriptFormID,Endianness,Offset,SubIndex,SubText,SubFlags,SubTargetStage
QUEST,...
STAGE,...(Index,LogEntry,StageFlags)
OBJECTIVE,...(Index,DisplayText,TargetStage)
DIALOGUE,...(TopicFormID,TopicName,SampleLine)
```

**cells.csv** (currently cell_info.csv):

```
RowType,CellFormID,CellEditorID,CellName,GridX,GridY,IsInterior,HasWater,WorldspaceEditorID,HasHeightmap,PlacedObjectCount,ObjectFormID,ObjectEditorID,ObjectRecordType,BaseFormID,BaseEditorID,X,Y,Z,RotX,RotY,RotZ,Scale,OwnerFormID
CELL,...
OBJ,...
```

**notes.csv** (currently notes.txt):

```
RowType,FormID,EditorID,Name,NoteType,NoteTypeName,Text,Endianness,Offset
```

(No sub-rows)

**terminals.csv** (currently terminals.txt):

```
RowType,FormID,EditorID,Name,Difficulty,HeaderText,Endianness,Offset,MenuItemText,MenuItemResultText,MenuItemSubTerminalFormID
TERMINAL,...
MENUITEM,...
```

**dialogue.csv** (currently dialogue.txt):

```
RowType,FormID,EditorID,TopicFormID,TopicName,QuestFormID,QuestName,SpeakerFormID,SpeakerName,Endianness,Offset,ResponseNumber,ResponseText,EmotionType,EmotionName,EmotionValue
DIALOGUE,...
RESPONSE,...
```

**factions.csv** (currently factions.txt):

```
RowType,FormID,EditorID,Name,Flags,IsHidden,AllowsEvil,AllowsSpecialCombat,Endianness,Offset,SubFormID,SubName,SubDetail
FACTION,...
RANK,...(RankIndex,RankName)
RELATION,...(FactionFormID,FactionName,Modifier)
```

**containers.csv** (currently containers.txt):

```
RowType,FormID,EditorID,Name,Respawns,ModelPath,ScriptFormID,Endianness,Offset,ItemFormID,ItemName,Count
CONTAINER,...
ITEM,...
```

**gamesettings.csv** (currently gamesettings.txt):

```
RowType,EditorID,FormID,ValueType,Value,Endianness,Offset
```

(No sub-rows)

**armor.csv, ammo.csv, consumables.csv, misc_items.csv, keys.csv**: Flat CSVs with type-specific stat columns + IconPath.

**leveled_lists.csv** (NEW):

```
RowType,FormID,EditorID,Name,ListType,ChanceNone,Flags,GlobalFormID,Endianness,Offset,EntryLevel,EntryFormID,EntryName,EntryCount
LEVELEDLIST,...
ENTRY,...
```

**map_markers.csv** (NEW):

```
RowType,FormID,EditorID,LocationName,MarkerType,MarkerTypeName,X,Y,Z,CellFormID,WorldspaceEditorID
```

(Flat CSV)

**Other existing reports** (creatures, races, perks, spells, books, dialog_topics, worldspaces): Convert similarly with appropriate columns.

### B3. Implementation Approach

1. Add a `CsvReportGenerator` class (or extend `GeckReportGenerator`) with CSV generation methods
2. Each method generates a `StringBuilder` with CSV header + data rows
3. Use the same `CsvEscape()` helper already in `EsmRecordExporter.cs`
4. Register new CSV filenames in `GenerateSplit()`, replacing .txt with .csv
5. Keep the existing text `Generate()` method (used by `-s` semantic report) unchanged

**Files**:

- `GeckReportGenerator.cs` -- add CSV generation methods, update `GenerateSplit()` to emit .csv files
- `EsmRecordExporter.cs` -- update `ExportCellInfoAsync()` with new RowType format, remove formid_map.csv

---

## Part C: Runtime Struct Scanning (Confirmed Feasible)

### C1. Investigation Results

**Conclusion: Runtime C++ objects ARE present and accessible in memory dumps.** The existing hash table walker (`ExtractFromHashTableCandidate` in `EsmRecordFormat.cs:3048`) already proves this by successfully extracting ~48K runtime objects.

### C2. How Runtime Objects Are Already Found

The game maintains a global `NiTMapBase<const char*, TESForm*>` hash table mapping EditorID strings to TESForm pointers. The code:

1. **Locates hash table** via PE section scanning → data section triple-pointer pattern (`ScanDataSectionForGlobalTriple`)
2. **Walks bucket chains** reading `NiTMapItem` structs (12 bytes: next_ptr + key_ptr + val_ptr)
3. **Reads EditorID** from `m_key` (char\* pointer)
4. **Reads TESForm** at `m_val` pointer — 24-byte header:
   ```
   Offset 0x00: vtable pointer (4 bytes BE)
   Offset 0x04: cFormType (1 byte)     ← identifies record type
   Offset 0x05: padding (3 bytes)
   Offset 0x08: iFormFlags (4 bytes BE)
   Offset 0x0C: iFormID (4 bytes BE)   ← the record's FormID
   Offset 0x10: additional (8 bytes)
   ```
5. **Reads display name** via `FullNameOffsetByFormType` dictionary — BSStringT strings at type-specific offsets
6. **Reads dialogue text** for INFO records via `InfoPromptOffset = 44`

**Key data stored per entry** (`RuntimeEditorIdEntry`):

- `EditorId` — the editor ID string
- `FormId` — uint32 from TESForm offset 0x0C
- `FormType` — byte from TESForm offset 0x04
- `TesFormOffset` — **exact file offset** where the full C++ struct begins
- `TesFormPointer` — virtual address of the TESForm object
- `DisplayName` — from TESFullName.cFullName at type-specific offset
- `DialogueLine` — from TESTopicInfo.cPrompt (INFO records only)

### C3. FormType Byte → Record Type Mapping (Complete)

From `EsmRecordTypes.cs` (verified against xEdit `wbFormTypeEnum`):

| Byte | Record | C++ Class       | PDB Size | FullName Offset |
| ---- | ------ | --------------- | -------- | --------------- |
| 0x08 | FACT   | TESFaction      | 76       | 44              |
| 0x0C | RACE   | TESRace         | -        | 44              |
| 0x17 | TERM   | BGSTerminal     | 168      | -               |
| 0x18 | ARMO   | TESObjectARMO   | 400      | 68              |
| 0x19 | BOOK   | TESObjectBOOK   | 196      | 68              |
| 0x1B | CONT   | TESObjectCONT   | 156      | 80              |
| 0x1C | DOOR   | TESObjectDOOR   | -        | 68              |
| 0x1F | MISC   | TESObjectMISC   | 172      | 68              |
| 0x28 | WEAP   | TESObjectWEAP   | 908      | 68              |
| 0x29 | AMMO   | TESAmmo         | 220      | 68              |
| 0x2A | NPC\_  | TESNPC          | 492      | 228             |
| 0x2B | CREA   | TESCreature     | -        | -               |
| 0x2C | LVLC   | TESLevCreature  | 112      | -               |
| 0x2D | LVLN   | TESLevCharacter | 112      | -               |
| 0x2E | KEYM   | TESKey          | 172      | 68              |
| 0x2F | ALCH   | AlchemyItem     | 216      | 68              |
| 0x31 | NOTE   | BGSNote         | 128      | -               |
| 0x34 | LVLI   | TESLevItem      | 76       | -               |
| 0x39 | CELL   | TESObjectCELL   | 176      | -               |
| 0x45 | LAND   | TESObjectLAND   | 44       | -               |
| 0x46 | INFO   | TESTopicInfo    | 80       | 44 (cPrompt)    |
| 0x47 | QUST   | TESQuest        | 108      | -               |
| 0x56 | PERK   | BGSPerk         | -        | -               |

FullName offsets are **empirically verified** against actual dump data (see `EsmRecordFormat.cs:2958`).

### C4. Runtime Struct Reading Strategy

**No searching needed** — `TesFormOffset` gives the exact file position of each C++ object. To extract extended data:

1. Filter `scanResult.RuntimeEditorIds` by FormType byte
2. Read N bytes at `TesFormOffset` (e.g., 492 bytes for TESNPC, 908 for TESObjectWEAP)
3. Parse fields at PDB-defined offsets with big-endian byte swapping
4. Create/enrich `Reconstructed*` models with the extracted data

**PDB version caveat**: PDB is from July 2010, dumps are Dec 2009 – Apr 2010. The existing display name offsets are empirically verified and work. New field offsets from the PDB are likely close but may need ±offset adjustments. Validation approach:

- For numeric fields (stats, damage): check values are in reasonable ranges
- For pointer fields (race, faction list): validate pointer is in captured memory range
- For string fields: use existing `ReadBSStringT()` with validation

### C5. Implementation — Runtime Struct Reader Methods

Add to `EsmRecordFormat.cs` or a new `RuntimeStructReader.cs`:

```csharp
// For each type, a method that reads extended struct fields from TesFormOffset
public static ReconstructedNpc? ReadRuntimeNpc(
    MemoryMappedViewAccessor accessor, long fileSize,
    MinidumpInfo minidumpInfo, RuntimeEditorIdEntry entry) { ... }

public static ReconstructedWeapon? ReadRuntimeWeapon(...) { ... }
// etc. for ARMO, AMMO, ALCH, MISC, KEYM, CONT, NOTE, TERM, FACT, QUST
```

Each method:

1. Reads the full struct at `entry.TesFormOffset`
2. Parses fields at PDB offsets (with validation)
3. Follows pointers (race, ammo, faction list) using `IsValidPointerInDump()` + VA-to-file-offset conversion
4. Returns a reconstructed model or null if validation fails

### C6. Two-Track Data Extraction Architecture

| Source                      | Best For                                                                          | Coverage                         | Data Richness       |
| --------------------------- | --------------------------------------------------------------------------------- | -------------------------------- | ------------------- |
| ESM scanning                | CELL, LAND, INFO, REFR                                                            | Good (records persist in memory) | Full subrecord data |
| Hash table + struct reading | NPC\_, WEAP, ARMO, AMMO, ALCH, MISC, KEYM, CONT, NOTE, TERM, FACT, QUST, LVLI/C/N | Comprehensive (~48K objects)     | PDB-defined fields  |

**Merge strategy**: For each record type, use BOTH sources. ESM records provide subrecord detail; runtime structs provide data for objects not found as ESM records. Match by FormID to avoid duplicates.

---

## Part D: Per-Data-Type Tasks

Each is a separate implementation task. All produce CSV output.

### D1. Notes

**ESM status**: Likely found (NOTE records). **Runtime**: BGSNote (128 bytes).
**CSV columns**: FormID, EditorID, Name, NoteType, NoteTypeName, Text
**Current model**: `ReconstructedNote` - has all needed fields.
**Action**: Convert notes.txt to notes.csv. No model changes.

### D2. Items (Weight, Name, Inventory Icon)

**ESM status**: Various item types (WEAP, ARMO, AMMO, ALCH, MISC, KEYM).
**Missing**: ICON/MICO paths not extracted for items (only for perks).
**Action**: Add IconPath to item models. Extract ICON/MICO in reconstruction. Include in CSV.
**Files**: `SemanticModels.cs`, `SemanticReconstructor.cs`

### D3. Quests (Stages, Objectives, Dialogues)

**ESM status**: QUST records found. **Runtime**: TESQuest (108 bytes), stages at offset 0x44.
**Missing**: Dialogue cross-referencing.
**Action**: Quest CSV includes STAGE/OBJECTIVE sub-rows. Add DIALOGUE sub-rows by cross-referencing DialogTopics and Dialogues by QuestFormId.
**Files**: `GeckReportGenerator.cs`

### D4. Terminal Entries

**ESM status**: TERM records found. **Runtime**: BGSTerminal (168 bytes), menu items 120 bytes each.
**CSV columns**: FormID, EditorID, Name, Difficulty, HeaderText + MENUITEM sub-rows.
**Current model**: `ReconstructedTerminal` - has all needed fields.
**Action**: Convert terminals.txt to terminals.csv with MENUITEM sub-rows.

### D5. Response Data / Combat Barks

**ESM status**: INFO records found. **Runtime**: TESTopicInfo (80 bytes).
**CSV columns**: FormID, TopicFormID, QuestFormID, SpeakerFormID + RESPONSE sub-rows with text, emotion data.
**Current model**: `ReconstructedDialogue` - has all needed fields including emotion types.
**Action**: Convert dialogue.txt to dialogue.csv with RESPONSE sub-rows.

### D6. Location Names / Map Markers -- NEW

**ESM gap**: REFR records ARE extracted but XMRK/TNAM/FULL not parsed.
**Runtime**: MapMarkerData (20 bytes) with LocationName, sType, cFlags.
**ESM subrecords**: XMRK (presence flag), TNAM (2 bytes: marker type uint16), FULL (name string).
**Implementation**:

1. Extend `ExtractedRefrRecord`: add `IsMapMarker`, `MapMarkerType`, `FullName`
2. Extend `ExtractRefrFromBuffer()`: add XMRK/TNAM/FULL cases
3. Extend `PlacedReference`: add `IsMapMarker`, `MapMarkerType`, `MapMarkerTypeName`
4. Add `map_markers.csv` report extracting all marker PlacedReferences from cells
   **Files**: `EsmRecordModels.cs`, `EsmRecordFormat.cs`, `SemanticModels.cs`, `SemanticReconstructor.cs`, `GeckReportGenerator.cs`

### D7. NPCs

**ESM status**: Only 7 NPC\_ ESM records found in sample dump. **Runtime**: TESNPC (492 bytes), FormType 0x2A. Hash table has thousands of NPC entries.
**Data source**: Primarily runtime struct reading at `TesFormOffset`. ESM records merged when found.
**Runtime fields to extract** (PDB offsets, all BE):

- ACBS stats block (level, fatigue, barter gold, speed mult, karma, disposition, calc min/max, flags)
- Race pointer → follow to read FormID of TESRace
- Faction list (BSSimpleList) → iterate entries for faction FormIDs + ranks
- Inventory list → iterate for item FormIDs + counts
- Spell list → iterate for spell FormIDs
- Package list → iterate for package FormIDs
- Display name already extracted at offset 228
  **CSV**: Multi-row with FACTION/INVENTORY/SPELL/PACKAGE sub-rows.
  **Action**: Add `ReadRuntimeNpc()` to RuntimeStructReader. Convert npcs.txt to npcs.csv. Merge ESM + runtime sources by FormID.

### D8. Weapons

**ESM status**: Only 34 WEAP ESM records found. **Runtime**: TESObjectWEAP (908 bytes), FormType 0x28. Hash table has hundreds of weapon entries.
**Data source**: Primarily runtime struct reading. ESM records merged when found.
**Runtime fields to extract** (PDB offsets, all BE):

- Weapon type enum, damage, fire rate, clip size, min/max range
- Spread, min spread, drift, str/skill requirements
- Critical damage, critical chance, critical effect FormID
- Value, weight, health, AP cost
- Ammo pointer → follow to read FormID of TESAmmo
- Model path (BSStringT), Icon path (BSStringT)
- Display name already extracted at offset 68
  **CSV**: Flat with all combat stats, ammo, criticals + icon path.
  **Action**: Add `ReadRuntimeWeapon()` to RuntimeStructReader. Convert weapons.txt to weapons.csv.

### D9. Containers

**ESM status**: CONT records.
**Runtime**: TESObjectCONT (156 bytes) with TESContainer@0x04.
**CSV**: Multi-row with ITEM sub-rows (FormID, Name, Count).
**Current model**: `ReconstructedContainer` - has contents list.
**Action**: Convert containers.txt to containers.csv with ITEM sub-rows.

### D10. Item Lists / Leveled Lists -- NEW

**ESM gap**: LVLI/LVLC/LVLN NOT in `RuntimeRecordTypes` array.
**Runtime**: TESLevItem (76), TESLevCreature (112), TESLevCharacter (112).
**ESM subrecords**: EDID, LVLD (1 byte), LVLF (1 byte), LVLG (4 bytes), LVLO (12 bytes each).
**Implementation**:

1. Add LVLI/LVLC/LVLN to `RuntimeRecordTypes`
2. Add `ReconstructedLeveledList` + `LeveledEntry` models
3. Add `ReconstructLeveledLists()` to SemanticReconstructor
4. Add `leveled_lists.csv` with ENTRY sub-rows (Level, FormID, Name, Count)
   **Files**: `EsmRecordFormat.cs`, `SemanticModels.cs`, `SemanticReconstructor.cs`, `GeckReportGenerator.cs`

### D11. Factions

**ESM status**: FACT records. **Runtime**: TESFaction (76 bytes).
**CSV**: Multi-row with RANK and RELATION sub-rows.
**Current model**: `ReconstructedFaction` - has ranks and relations.
**Action**: Convert factions.txt to factions.csv.

### D12. Game Settings (GMST)

**ESM status**: GMST subrecords found. **Runtime**: Setting (12 bytes) with typed union.
**CSV**: Flat with EditorID, ValueType, Value.
**Current model**: `ReconstructedGameSetting` - has typed values.
**Action**: Convert gamesettings.txt to gamesettings.csv.

### D13. Leveled Creature/NPC Lists -- COVERED BY D10

Same implementation handles LVLI/LVLC/LVLN with ListType discriminator.

---

## Part E: Heightmap Full Implementation

### E1. LoadedLandData Runtime Scanning

**Scanning strategy**:

1. For each LAND main record, check for `pLoadedData` pointer at runtime offset +56
2. If pointer is in valid Xbox 360 heap range (0x40000000-0x7FFFFFFF), read 164 bytes
3. Validate: `iCellX`@+152 in [-100,100], `iCellY`@+156 in [-100,100], `fBaseHeight`@+160 is normal float
4. Use runtime coordinates for heightmap stitching

**Alternative**: Scan for 12-byte tail pattern (int32 pair in range + float) if pointer following fails.

**Implementation**:

1. Add `RuntimeLoadedLandData` model to `EsmRecordModels.cs`
2. Add `ExtractRuntimeLandData()` to `EsmRecordFormat.cs`
3. Add `RuntimeLandData` to `EsmRecordScanResult`
4. Update `HeightmapPngExporter.ExportCompositeWorldmapAsync()` to prefer runtime coordinates
5. Document in `docs/Memory_Dump_Research.md`

---

## Part F: Cleanup

### F1. Remove Redundant Editor ID Reports

Remove `editorids.txt` from `GenerateSplit()` and `formid_map.csv` from `EsmRecordExporter`. Keep only `runtime_editorids.csv`.

---

## Completion Status

### Phase 1: Core Architecture ✅ COMPLETE

- CSV infrastructure (CsvEscape, RowType pattern)
- Removed redundant editorids.txt, formid_map.csv
- Baseline extraction counts established

### Phase 2: Runtime Struct Reader Infrastructure ✅ COMPLETE

- `RuntimeStructReader.cs` with ReadBSStringT(), pointer following, struct validation
- NPC\_ reader: ACBS stats, Race/Class/VoiceType pointers, Gender from flags
- WEAP reader: All combat stats (Damage, ClipSize, FireRate, APCost, AmmoFormId, ModelPath)
- Cross-dump layout verification (struct offsets stable across Dec 2009 – Apr 2010 builds)
- Two-track merge in SemanticReconstructor (ESM + runtime by FormID)

### Phase 3: CSV Conversion ✅ COMPLETE

- All 23 record-type reports converted to CSV with RowType sub-rows
- Dialog topics deduplication added

### Phase 4: Item Runtime Struct Readers ✅ COMPLETE

- Empirical hex dump analysis verified field offsets for all 5 item types
- **RuntimeStructReader.cs**: Added ReadRuntimeArmor(), ReadRuntimeAmmo(), ReadRuntimeConsumable(), ReadRuntimeMiscItem(), ReadRuntimeKey()
- **SemanticReconstructor.cs**: Added runtime merge loops for all 5 types
- Empirically verified offsets:
  - ARMO: Value@+108, Weight@+116, Health@+124, ArmorRating@+392 (uint16, ×100)
  - AMMO: Value@+140 (no Weight/Health in class hierarchy)
  - ALCH: Value@+200 (direct member, not TESValueForm), Weight@+168
  - MISC: Value@+136, Weight@+144
  - KEYM: Value@+136, Weight@+144 (inherits TESObjectMISC)
- Added TesFormOffset column to runtime_editorids.csv for diagnostics
- 12 CSV files now generated (was 7)

### Current Extraction Counts (xex3 dump, after Phase 4)

| Type  | ESM Records | Runtime Structs | Total   | Game Has |
| ----- | ----------- | --------------- | ------- | -------- |
| NPC\_ | 7           | 2,769           | 2,776   | ~3,000+  |
| WEAP  | 34          | 302             | 336     | ~1,200+  |
| ARMO  | 0           | **453**         | **453** | ~600+    |
| AMMO  | 0           | **45**          | **45**  | ~50+     |
| ALCH  | 0           | **116**         | **116** | ~100+    |
| MISC  | 0           | **302**         | **302** | ~200+    |
| KEYM  | 0           | **207**         | **207** | ~50+     |
| CONT  | 0           | 0               | 0       | ~500+    |
| FACT  | 0           | 0               | 0       | ~100+    |
| QUST  | 0           | 0               | 0       | ~300+    |
| NOTE  | 0           | 0               | 0       | ~100+    |
| TERM  | 0           | 0               | 0       | ~200+    |
| INFO  | 642         | 0               | 642     | ~23,000+ |
| DIAL  | 17          | 0               | 17      | ~1,000+  |
| CELL  | 6           | 0               | 6       | ~1,000+  |

**Total Reconstructed**: 5,485 records (was ~3,800 before Phase 4)

**Zero-count types with hash table entries** (candidates for runtime struct reading):

- Creatures, Races, Factions, Quests, Notes, Books, Terminals, Containers, Perks, Spells, Worldspaces
- These all have entries in the runtime EditorID hash table but no reader implemented yet

---

## Remaining Implementation Phases

### ~~Phase 4: Item Runtime Struct Readers~~ ✅ COMPLETE (see above)

### Phase 5: Model Paths, Simple Readers, and NPC Sub-Items

Phase 5 is broken into sub-phases that build on each other. Execute in order.

---

#### Phase 5A: Fix Empty Model Columns (QUICK WIN)

**Problem**: Only weapons have ModelPath populated. MISC, AMMO, and KEYM all have valid TESModel.cModel BSStringT at dump offset +80 (verified via hex dumps), but the runtime readers don't read it. ALCH has a different layout at +80 (not a valid BSStringT — sLen=20232). ARMO uses biped model slots, not TESModel, so model path is legitimately NULL.

**Implementation**:

1. In `ReadRuntimeAmmo()`: Add `ReadBSStringT(offset, 80)` call → set `ModelPath`
2. In `ReadRuntimeMiscItem()`: Add `ReadBSStringT(offset, 80)` call → set `ModelPath`
3. In `ReadRuntimeKey()`: Add `ReadBSStringT(offset, 80)` call → set `ModelPath`
4. Leave ALCH and ARMO as-is (model not at +80 for ALCH; ARMO uses biped models)

**Files modified**: `RuntimeStructReader.cs` only (3 one-line additions)

**Expected impact**: ~554 items gain ModelPath (45 AMMO + 302 MISC + 207 KEYM)

---

#### Phase 5B: Ammo Projectile Model Column (NEW USER REQUEST)

**Problem**: User wants a projectile model column in the ammo CSV report. Currently `ProjectileFormID` and `ProjectileName` columns exist but are always empty. Also, a new `ProjectileModelPath` column is needed.

**Background**: In Fallout 3, ammo doesn't directly reference projectiles in the ESM DATA subrecord. The projectile association is: WEAP → DNAM.pProjectile → BGSProjectile. But the runtime TESAmmo struct may have a direct pointer.

**BGSProjectile PDB layout** (Size 208, dump 224 with +16):

- +0: TESBoundObject (64 bytes in dump)
- +76: TESModel base (24 bytes) → cModel BSStringT at +80
- +96: BGSProjectileData (84 bytes)
- +180: MuzzleFlashModel (TESModel, 24 bytes)

**Implementation (two approaches, try in order)**:

**Approach 1: Cross-reference via weapons** (reliable, no hex dump needed)

1. Add `ProjectileModelPath` field to `ReconstructedAmmo`
2. After reconstructing both weapons and ammo, build reverse lookup:
   - Map: AmmoFormId → WeaponProjectileFormId (from `ReconstructedWeapon.AmmoFormId` → `ProjectileFormId`)
3. For each projectile FormID, look up in `RuntimeEditorIds` by FormId (FormType 0x33=PROJ)
4. Read model BSStringT at dump offset +80 from the BGSProjectile's TesFormOffset
5. Add `ProjectileModelPath` column to `GenerateAmmoCsv()`

**Approach 2: Direct pointer in TESAmmo** (fallback, needs empirical verification)

- If cross-reference yields no results, hex-dump a known ammo (e.g., 10mm Round) to find a BGSProjectile\* pointer within the TESAmmo struct
- The pointer would be somewhere in the `data` struct at dump offset ~184

**Files modified**:

- `SemanticModels.cs` — Add `ProjectileModelPath` to `ReconstructedAmmo`
- `SemanticReconstructor.cs` — Add cross-reference logic after ReconstructWeapons/ReconstructAmmo
- `RuntimeStructReader.cs` — Add `ReadProjectileModelPath(uint projectileFormId)` helper
- `CsvReportGenerator.cs` — Add ProjectileModelPath column to ammo CSV

---

#### Phase 5C: Simple Type Runtime Readers (NO LINKED LIST)

These types can be read with simple struct field extraction (no BSSimpleList traversal needed). Each adds a runtime merge loop following the established ARMO/AMMO/etc. pattern.

**NOTE** (BGSNote, PDB 128, dump 144, FormType 0x31, hash table: 668 entries):

- Note type byte: needs empirical offset verification
- Note text: BSStringT at some offset (needs hex dump of a known note)
- Implementation: `ReadRuntimeNote()` with empirically verified offsets

**FACT** (TESFaction, PDB 76, dump 92, FormType 0x08, hash table: 424 entries):

- Flags at PDB offset 0x34 (dump +68)
- No sub-item lists needed for basic faction record (ranks/relations require BSSimpleList — deferred to 5D)
- Implementation: `ReadRuntimeFaction()` — minimal reader with flags only; sub-items deferred

**QUST** (TESQuest, PDB 108, dump 124, FormType 0x47, hash table: 164 entries):

- Flags at PDB 0x3C (dump +76), Priority at PDB 0x3D (dump +77)
- Stages require BSSimpleList — deferred to 5D
- Implementation: `ReadRuntimeQuest()` — minimal reader with flags/priority; stages deferred

**Files modified**:

- `RuntimeStructReader.cs` — Add ReadRuntimeNote(), ReadRuntimeFaction(), ReadRuntimeQuest()
- `SemanticReconstructor.cs` — Add runtime merge loops for NOTE (0x31), FACT (0x08), QUST (0x47)

**Expected counts after 5C**: NOTE ~668, FACT ~424, QUST ~164

---

#### Phase 5D: BSSimpleList Infrastructure + NPC Sub-Items (HARD)

**Problem**: NPC records currently have ZERO faction, inventory, spell, and package sub-rows despite 2,776 NPCs being extracted. These sub-items are stored as BSSimpleList linked lists within the TESNPC struct. This is the "missing phase" — NPC inventories are NOT CONT records; they're embedded lists within each NPC's TESContainer base class.

**BSSimpleList structure (12 bytes, big-endian):**

```
+0: vfptr (4 bytes) — vtable pointer
+4: pHead (4 bytes) — pointer to first NiTListItem
+8: count or padding
```

**NiTListItem (12 bytes):**

```
+0: pNext (4 bytes) — next item pointer (NULL = end of list)
+4: pPrev (4 bytes) — previous item pointer
+8: data (4+ bytes) — embedded data or pointer to data
```

**TESNPC sub-item locations** (dump offsets, empirically verified):

- TESContainer at +116: contains BSSimpleList of ContainerObject\* (item FormID + count)
- TESSpellList at +128: contains BSSimpleList of TESForm\* (spell pointers)
- NPC-specific: AI package list (BSSimpleList of TESPackage\*)
- Faction list: BSSimpleList of TESActorBaseData::FactionInfo (faction FormID + rank)

**ContainerObject layout (8 bytes):**

```
+0: int32 count (BE)
+4: TESForm* pItem (pointer to item TESForm)
```

**Implementation**:

1. Add `FollowBSSimpleList()` generic helper to RuntimeStructReader:
   - Takes: buffer offset of BSSimpleList, max items (safety limit), callback per item
   - Follows pHead → NiTListItem chain via pNext pointers
   - Converts VA to file offset at each step, validates pointer in dump range
   - Returns list of raw data blobs (one per list item)

2. Add NPC sub-item extraction methods:
   - `ReadNpcFactions(buffer, offset)` → List<FactionMembership>
   - `ReadNpcInventory(buffer, offset)` → List<InventoryItem>
   - `ReadNpcSpells(buffer, offset)` → List<uint>
   - `ReadNpcPackages(buffer, offset)` → List<uint>

3. Integrate into `ReadRuntimeNpc()`:
   - After basic stats extraction, call sub-item methods
   - Populate Factions, Inventory, Spells, Packages lists on ReconstructedNpc

4. Empirical verification approach:
   - Hex dump Doc Mitchell (FormID 0x00104C0C) at known offset
   - Look for BSSimpleList vtable patterns (0x82xxxxxx) at +116, +128
   - Follow pHead pointer chain manually to verify structure
   - Cross-reference inventory items against known Doc Mitchell inventory

**Files modified**:

- `RuntimeStructReader.cs` — Add FollowBSSimpleList(), NPC sub-item extraction
- No model changes needed (ReconstructedNpc already has Factions, Inventory, Spells, Packages lists)

**Expected impact**: All 2,776 NPCs gain faction/inventory/spell/package sub-rows in npcs.csv

---

#### Phase 5E: Container & Terminal Readers (BSSimpleList)

Reuses the BSSimpleList infrastructure from 5D.

**CONT** (TESObjectCONT, PDB 156, dump 172, FormType 0x1B, hash table: 718 entries):

- TESContainer at dump offset ~80 (PDB: TESContainer@0x04 base, +16 shift → +20, BUT this is relative to TESBoundObject end... needs verification)
- Actually: TESObjectCONT inherits TESBoundAnimObject(64) + TESFullName(12=80) + TESModel(24=104) + TESWeight(8=112) + TESScriptable(12=124) + TESContainer(12=136)
- Container BSSimpleList at ~136 → follow for items
- Implementation: `ReadRuntimeContainer()` with BSSimpleList for contents

**TERM** (BGSTerminal, PDB 168, dump 184, FormType 0x17, hash table: 276 entries):

- Menu items via BSSimpleList (each MenuItem is 120 bytes with text, result, sub-terminal)
- Header text as BSStringT
- Difficulty byte
- Implementation: `ReadRuntimeTerminal()` — header text first, menu items via BSSimpleList

**Files modified**:

- `RuntimeStructReader.cs` — Add ReadRuntimeContainer(), ReadRuntimeTerminal()
- `SemanticReconstructor.cs` — Add runtime merge loops for CONT (0x1B), TERM (0x17)

**Expected counts**: CONT ~718, TERM ~276

---

#### Phase 5F: Creature Reader (NEW — not in original plan)

**Problem**: 802 creature entries in hash table, currently 0 count. CREA (TESCreature, FormType 0x2B/43) is similar to NPC\_ but for non-human entities.

**Implementation**: Similar to ReadRuntimeNpc but for TESCreature struct. Needs empirical offset verification.

**Files modified**:

- `RuntimeStructReader.cs` — Add ReadRuntimeCreature()
- `SemanticReconstructor.cs` — Add runtime merge for CREA (0x2B)

**Expected count**: ~802

---

### Phase 6: New Record Types (MEDIUM IMPACT)

#### 6A. Map Markers (REFR Extension)

Map markers are REFR records with XMRK/MMRK subrecords. Currently, REFR extraction only parses NAME, DATA, XSCL, XOWN.

**Add to REFR parsing:**

- XMRK (0 bytes): presence flag → `IsMapMarker = true`
- TNAM (2 bytes): marker type enum (0=None..14=Vault)
- FULL (variable): marker display name

**Models to extend:**

- `ExtractedRefrRecord` — add `IsMapMarker`, `MarkerType`, `MarkerName`
- `PlacedReference` — add same fields

**New output:** `map_markers.csv` with columns: FormID, LocationName, MarkerType, MarkerTypeName, X, Y, Z, CellFormID, WorldspaceEditorID

**Files modified:**

- `EsmRecordFormat.cs` — Add XMRK/TNAM/FULL cases in ExtractRefrFromBuffer()
- `EsmRecordModels.cs` — Extend ExtractedRefrRecord
- `SemanticModels.cs` — Extend PlacedReference, add MapMarkerType enum
- `SemanticReconstructor.cs` — Propagate marker fields to PlacedReference
- `CsvReportGenerator.cs` — Add GenerateMapMarkersCsv()
- `GeckReportGenerator.cs` — Add map_markers.csv to GenerateSplit()

#### 6B. Leveled Lists (New Type)

LVLI/LVLC/LVLN records are currently not in `RuntimeRecordTypes` and not reconstructed.

**ESM subrecords:** EDID, LVLD (1 byte chanceNone), LVLF (1 byte flags), LVLG (4 bytes global FormID), LVLO (12 bytes each: level u16 + pad u16 + FormID u32 + count u16 + pad u16)

**New models:**

```csharp
public record ReconstructedLeveledList { FormId, EditorId, ListType, ChanceNone, Flags, GlobalFormId, Entries[], Offset }
public record LeveledEntry(ushort Level, uint FormId, ushort Count)
```

**Files modified:**

- `EsmRecordFormat.cs` — Add LVLI/LVLC/LVLN to RuntimeRecordTypes array
- `SemanticModels.cs` — Add ReconstructedLeveledList, LeveledEntry
- `SemanticReconstructor.cs` — Add ReconstructLeveledLists()
- `CsvReportGenerator.cs` — Add GenerateLeveledListsCsv()
- `GeckReportGenerator.cs` — Add leveled_lists.csv to GenerateSplit()

### Phase 7: Heightmap Enhancement (SPECIALIZED)

**Goal**: Use LoadedLandData runtime scanning to get proper cell coordinates for heightmap stitching.

**LoadedLandData (164 bytes):**

```
+152: iCellX (int32) — cell grid X
+156: iCellY (int32) — cell grid Y
+160: fBaseHeight (float32) — base elevation
```

**Approach**: For each LAND record, check `pLoadedData` pointer at runtime offset +56. If valid, read 164 bytes at target and extract cell coordinates.

**Files modified:**

- `EsmRecordModels.cs` — Add RuntimeLoadedLandData model
- `EsmRecordFormat.cs` — Add ExtractRuntimeLandData()
- `HeightmapPngExporter.cs` — Prefer runtime coordinates for stitching

### Phase 8: Documentation

- Update `docs/Memory_Dump_Research.md` with PDB findings, runtime struct layouts, FormType mapping, verified offsets

---

## Recommended Execution Order

1. **Phase 5A** (Model Path Fixes) — Quick win, 3 one-line additions
2. **Phase 5B** (Ammo Projectile Model) — New user request, cross-reference approach
3. **Phase 5C** (Simple Type Readers) — NOTE/FACT/QUST basic readers, no linked lists
4. **Phase 5D** (BSSimpleList + NPC Sub-Items) — Core infrastructure + NPC enrichment
5. **Phase 5E** (Container + Terminal Readers) — Reuses BSSimpleList from 5D
6. **Phase 5F** (Creature Reader) — Similar to NPC reader
7. **Phase 6A** (Map Markers) — REFR extension
8. **Phase 6B** (Leveled Lists) — New record type
9. **Phase 7** (Heightmap) — Specialized
10. **Phase 8** (Documentation) — Final

---

## Files Modified (Summary)

### Already Modified (Phases 1-4)

| File                       | Changes Done                                                                                                           |
| -------------------------- | ---------------------------------------------------------------------------------------------------------------------- |
| `RuntimeStructReader.cs`   | ReadRuntimeNpc, ReadRuntimeWeapon, ReadRuntimeArmor/Ammo/Consumable/MiscItem/Key, ReadBSStringT, FollowPointerToFormId |
| `SemanticReconstructor.cs` | Runtime merge loops for NPC\_/WEAP/ARMO/AMMO/ALCH/MISC/KEYM, dialog dedup                                              |
| `CsvReportGenerator.cs`    | All CSV generation methods, NPC gender column                                                                          |
| `GeckReportGenerator.cs`   | GenerateSplit() with 22 CSV files + summary.txt, TesFormOffset in runtime_editorids.csv                                |
| `SemanticModels.cs`        | All current models                                                                                                     |

### Remaining Changes (Phases 5-8)

| File                           | Remaining Changes                                                                                                                                                                                    |
| ------------------------------ | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `RuntimeStructReader.cs`       | 5A: model path for AMMO/MISC/KEYM; 5B: ReadProjectileModelPath; 5C: ReadRuntimeNote/Faction/Quest; 5D: FollowBSSimpleList, NPC sub-items; 5E: ReadRuntimeContainer/Terminal; 5F: ReadRuntimeCreature |
| `SemanticReconstructor.cs`     | 5B: ammo↔weapon↔projectile cross-reference; 5C-5F: runtime merge loops for NOTE/FACT/QUST/CONT/TERM/CREA                                                                                             |
| `SemanticModels.cs`            | 5B: ProjectileModelPath on ReconstructedAmmo; 6A: MapMarkerType enum, PlacedReference marker fields; 6B: ReconstructedLeveledList+LeveledEntry                                                       |
| `CsvReportGenerator.cs`        | 5B: ProjectileModelPath column in ammo CSV; 6A: GenerateMapMarkersCsv(); 6B: GenerateLeveledListsCsv()                                                                                               |
| `EsmRecordFormat.cs`           | 6A: XMRK/TNAM/FULL in REFR; 6B: LVLI/LVLC/LVLN in RuntimeRecordTypes; 7: runtime land data                                                                                                           |
| `EsmRecordModels.cs`           | 6A: ExtractedRefrRecord marker fields; 7: RuntimeLoadedLandData                                                                                                                                      |
| `GeckReportGenerator.cs`       | 6A: map_markers.csv; 6B: leveled_lists.csv in GenerateSplit()                                                                                                                                        |
| `HeightmapPngExporter.cs`      | 7: Runtime cell coordinates                                                                                                                                                                          |
| `docs/Memory_Dump_Research.md` | 8: All PDB findings                                                                                                                                                                                  |

---

## Phase 5G: Structured NPC Report with Display Names ✅ COMPLETE

Steps 1-3 implemented. `npc_report.txt` generated (3.8 MB xex3, 4.5 MB xex44).
Files modified: `SemanticModels.cs`, `SemanticReconstructor.cs`, `GeckReportGenerator.cs`.

---

## Phase 5H: Wiki-Matching NPC Data Enhancement

### Goal

Enhance NPC data extraction and report to match the Fallout Wiki format. Add SPECIAL stats, AI data, combat style, physical traits (hair/eyes/colors/height), and FaceGen morph slider values.

### Ulysses Discovery

The cut companion Ulysses IS present in the late dump (xex44):

- FormID: `0x00133FD4`, EditorID: `Ulysses`, Level 50
- Voice Type: `MaleUniqueUlysses (0x0014F3EB)`
- 97 dialogue topics (`VDialogueUlyssesTopic000-097`)
- Outfit: `OutfitUlysses` + `UlyssesHat`
- Script: `UlyssesScript`

### New TESNPC Fields (PDB offsets → dump offsets)

All offsets verified from PDB analysis (July 2010 PDB, +16 universal shift):

| Dump Offset | PDB Offset | Size | Type      | Field                                    |
| ----------- | ---------- | ---- | --------- | ---------------------------------------- |
| +44         | +28        | 1    | uint8     | Aggression (TESAIForm.AIData)            |
| +45         | +29        | 1    | uint8     | Confidence                               |
| +46         | +30        | 1    | uint8     | Energy Level                             |
| +47         | +31        | 1    | uint8     | Responsibility                           |
| +48         | +32        | 4    | uint32    | AI Flags                                 |
| +52         | +36        | 1    | uint8     | Teaches (skill index)                    |
| +53         | +37        | 1    | uint8     | Max Training Level                       |
| +54         | +38        | 2    | uint16    | Assistance                               |
| +292        | +276       | 28   | struct    | TESNPCData (SPECIAL + skills)            |
| +320        | +304       | 4    | ptr       | Combat Style (TESCombatStyle\*)          |
| +324        | +308       | 96   | float[24] | RaceFaceOffsetCoord (face morph sliders) |
| +424        | +408       | 4    | ptr       | Hair (TESHair\*)                         |
| +428        | +412       | 4    | float     | Hair Length                              |
| +432        | +416       | 4    | ptr       | Eyes (TESEyes\*)                         |
| +436        | +420       | 4    | uint32    | Hair Color (RGBA)                        |
| +440        | +424       | 4    | uint32    | Eye Color (RGBA)                         |
| +460        | +444       | 8    | list      | Head Parts (eyebrow, etc.)               |
| +484        | +468       | 4    | float     | Height                                   |
| +488        | +472       | 4    | float     | Weight                                   |

**AI Data location rationale**: TESForm ends at dump +40. TESAIForm (28 bytes PDB) has a 4-byte vtable + 20-byte AIData + 4-byte AIPackList = 28 bytes. This fits exactly in the gap between +40 and +68 (where ACBS starts). So AIData starts at dump +44.

**TESNPCData layout** (28 bytes at dump +292): Likely contains:

- Bytes 0-6: SPECIAL attributes (7 × uint8: ST, PE, EN, CH, IN, AG, LK)
- Bytes 7-20: Skill values (14 × uint8 for Fallout NV skills)
- Bytes 21-27: Padding or additional data
- **Needs empirical verification** — hex dump Sunny Smiles to match wiki SPECIAL: 6,5,4,4,4,6,4

### Implementation Steps

#### Step 0: Empirical Hex Dump Verification

Before implementing, hex dump GSSunnySmiles (late dump offset 22743140) at regions:

- +40 to +68: Verify AI data location (aggression=1, confidence=4 expected)
- +292 to +320: Verify SPECIAL stats (6,5,4,4,4,6,4 expected)
- +424 to +444: Verify hair/eye pointers and colors
- +484 to +492: Verify height (0.95 expected) and weight

Use RuntimeStructReader's existing infrastructure to read these bytes.

#### Step 1: Update ReconstructedNpc Model

**File: `SemanticModels.cs`**

Add new properties to `ReconstructedNpc`:

```csharp
// SPECIAL stats
public byte[]? SpecialStats { get; init; }  // 7 bytes: ST, PE, EN, CH, IN, AG, LK

// AI Data
public NpcAiData? AiData { get; init; }

// Physical Traits
public uint? Hair { get; init; }          // TESHair FormID
public uint? Eyes { get; init; }          // TESEyes FormID
public uint? HairColor { get; init; }     // RGBA uint32
public uint? EyeColor { get; init; }      // RGBA uint32
public float? Height { get; init; }
public float? Weight { get; init; }
public float? HairLength { get; init; }

// Combat
public uint? CombatStyle { get; init; }   // TESCombatStyle FormID

// FaceGen morph data (24 slider values for GECK recreation)
public float[]? FaceGenMorphs { get; init; }  // 24 floats from RaceFaceOffsetCoord
```

Add new record:

```csharp
public record NpcAiData(
    byte Aggression,    // 0=Unaggressive, 1=Aggressive, 2=Very Aggressive, 3=Frenzied
    byte Confidence,    // 0=Cowardly, 1=Cautious, 2=Average, 3=Brave, 4=Foolhardy
    byte EnergyLevel,
    byte Responsibility, // 0=Any crime, 1=Violence, 2=Property crime, 3=No crime
    uint Flags,
    byte TeachesSkill,
    byte MaxTrainingLevel,
    ushort Assistance);  // 0=Nobody, 1=Allies, 2=Friends and Allies
```

#### Step 2: Extend RuntimeStructReader.ReadRuntimeNpc()

**File: `RuntimeStructReader.cs`**

Add new offset constants:

```csharp
// AI Data (TESAIForm within TESActorBase, after TESForm)
private const int NpcAiDataOffset = 44;      // aggression, confidence, etc.
private const int NpcAiDataSize = 12;         // 8 core bytes + 4 padding

// TESNPCData (SPECIAL + skills)
private const int NpcDataOffset = 292;
private const int NpcDataSize = 28;

// Combat Style
private const int NpcCombatStylePtrOffset = 320;  // NOTE: was NpcClassPtrOffset — investigate overlap

// Face/Physical
private const int NpcFaceMorphOffset = 324;   // RaceFaceOffsetCoord (96 bytes = 24 floats)
private const int NpcFaceMorphSize = 96;
private const int NpcHairPtrOffset = 424;
private const int NpcHairLengthOffset = 428;
private const int NpcEyesPtrOffset = 432;
private const int NpcHairColorOffset = 436;
private const int NpcEyeColorOffset = 440;
private const int NpcHeightOffset = 484;
private const int NpcWeightOffset = 488;
```

Add reading logic to `ReadRuntimeNpc()`:

1. Read AI data: 12 bytes at +44, parse aggression/confidence/etc.
2. Read SPECIAL: 7 bytes at +292 (within TESNPCData)
3. Follow Hair/Eyes pointers at +424/+432 → FormIDs
4. Read hair/eye color uint32s at +436/+440
5. Read height/weight floats at +484/+488 (BE → swap)
6. Read face morphs: 24 big-endian floats at +324

**Class vs CombatStyle at +320**: The PDB says `pCl` (TESCombatStyle\*) is at PDB 304 → dump 320. We currently read this as `NpcClassPtrOffset` for Class. This may actually be CombatStyle. Need to verify: if the FormID at this pointer resolves to a CSTY (CombatStyle) EditorID in the lookup, it's CombatStyle. If it resolves to a CLAS (Class) EditorID, it's Class. They might be the same offset holding different things, or our current Class assignment might be incorrect.

#### Step 3: Update NPC Report Format

**File: `GeckReportGenerator.cs`**

Update `AppendNpcReportEntry()` to add new sections after the existing content:

```
================================================================================
                    NPC: GSSunnySmiles — Sunny Smiles
================================================================================
  FormID:         0x00104E84
  Editor ID:      GSSunnySmiles
  Display Name:   Sunny Smiles
  Gender:         Female

  ── Stats ──────────────────────────────────────────────────────────────────────
  Level:          5
  SPECIAL:        6 ST, 5 PE, 4 EN, 4 CH, 4 IN, 6 AG, 4 LK
  Karma:          900.00 (Very Good)
  Fatigue Base:   50
  Barter Gold:    0
  Speed Mult:     100

  ── Combat ─────────────────────────────────────────────────────────────────────
  Race:           Hispanic — Hispanic (0x000038E5)
  Class:          WastelandAdventurer (0x0001873D)
  Combat Style:   DefaultCombatRangedstyle (0x000ABCDE)

  Factions (6):
    EditorID                         Name                             Rank
    ──────────────────────────────── ──────────────────────────────── ────
    ...

  ── AI Data ────────────────────────────────────────────────────────────────────
  Aggression:     Aggressive (1)
  Confidence:     Foolhardy (4)
  Assistance:     Helps Friends and Allies (2)
  Energy Level:   50
  Responsibility: Any Crime (0)

  ── Physical Traits ────────────────────────────────────────────────────────────
  Height:         0.95
  Weight:         50.00
  Hairstyle:      WendyTheWelder — Wendy the Welder (0x00012345)
  Hair Color:     0xRRGGBBAA
  Hair Length:     1.00
  Eyes:           DarkBrownEyes — Dark Brown (0x00012346)
  Eye Color:      0xRRGGBBAA

  ── Inventory (2) ──────────────────────────────────────────────────────────────
    EditorID                         Name                               Qty
    ──────────────────────────────── ──────────────────────────────── ─────
    ArmorLeather                     Leather Armor                        1
    WithAmmoNVVarmintRifleLoot       (none)                               1

  ── FaceGen Morph Sliders (24) ─────────────────────────────────────────────────
    [ 0] 0.1234   [ 1] -0.0567   [ 2] 0.0000   [ 3] 0.2345
    [ 4] 0.0890   [ 5] -0.1234   [ 6] 0.0000   [ 7] 0.0456
    [ 8] ...      [ 9] ...      [10] ...      [11] ...
    [12] ...      [13] ...      [14] ...      [15] ...
    [16] ...      [17] ...      [18] ...      [19] ...
    [20] ...      [21] ...      [22] ...      [23] ...

  ── AI Packages (2) ────────────────────────────────────────────────────────────
    EditorID                         Name
    ──────────────────────────────── ────────────────────────────────
    ...
```

**Label mappings** to add:

- Aggression: 0→Unaggressive, 1→Aggressive, 2→Very Aggressive, 3→Frenzied
- Confidence: 0→Cowardly, 1→Cautious, 2→Average, 3→Brave, 4→Foolhardy
- Assistance: 0→Helps Nobody, 1→Helps Allies, 2→Helps Friends and Allies
- Responsibility: 0→Any Crime, 1→Violence, 2→Property Crime, 3→No Crime
- Karma labels: < -750=Very Evil, < -250=Evil, < 250=Neutral, < 750=Good, ≥750=Very Good

#### Step 4: Update npcs.csv with New Columns

**File: `CsvReportGenerator.cs`**

Add columns to the NPC CSV header:

- `SPECIAL_ST,SPECIAL_PE,SPECIAL_EN,SPECIAL_CH,SPECIAL_IN,SPECIAL_AG,SPECIAL_LK`
- `Aggression,Confidence,EnergyLevel,Responsibility,Assistance`
- `CombatStyleFormID,CombatStyleName`
- `Height,Weight`
- `HairFormID,HairName,EyesFormID,EyesName,HairColor,EyeColor`

### FaceGen Data Assessment

**What's in memory:**

- 96 bytes of RaceFaceOffsetCoord (24 floats) — face morph slider values relative to race defaults
- Hair style (TESHair pointer) — identifies the hairstyle mesh
- Eyes (TESEyes pointer) — identifies eye type
- Hair/eye color values (uint32, likely RGBA)
- Additional FaceGen files (.egm, .egt, .tri) already carved by FaceGenFormat.cs

**Can NIF Scope preview faces?**
Not directly from slider values. A full face preview would require:

1. The base head mesh (HeadHuman.nif or similar)
2. Race-specific morph targets (.egm file)
3. Application of the 24 morph coefficients to the base mesh
4. Hair/eye meshes overlaid

This is beyond current scope but the extracted slider values + hair/eye references are sufficient for manual GECK recreation.

**GECK import/export**: GECK doesn't have a direct NPC import. But with the slider values, hair style, eye type, and colors extracted, a modder could manually recreate any NPC's appearance in GECK by setting the 24 face sliders + selecting the correct hair/eyes. This is particularly valuable for cut content like Ulysses.

**Age**: Not a separate field. NPC "age" in Bethesda games is determined by:

- Race (e.g., "CaucasianOldAged" vs "Caucasian") — we already extract this
- Face morph slider values (wrinkle/aging morphs)
  So the race name IS the age indicator.

### Files Modified

| File                     | Changes                                                                                    |
| ------------------------ | ------------------------------------------------------------------------------------------ |
| `SemanticModels.cs`      | Add `NpcAiData` record, new NPC properties (SPECIAL, AI, physical, FaceGen)                |
| `RuntimeStructReader.cs` | Add new offset constants, extend `ReadRuntimeNpc()` with AI/SPECIAL/physical/FaceGen reads |
| `GeckReportGenerator.cs` | Update `AppendNpcReportEntry()` with new sections, add label mapping helpers               |
| `CsvReportGenerator.cs`  | Add new columns to `GenerateNpcsCsv()`                                                     |

### Verification

1. **Hex dump verification** (first!): Read GSSunnySmiles at dump offset 22743140, verify:
   - +44: aggression byte → expect 1 (Aggressive)
   - +45: confidence byte → expect 4 (Foolhardy)
   - +292-298: SPECIAL bytes → expect 6,5,4,4,4,6,4
   - +484: height float (BE) → expect 0.95
2. `dotnet build -c Release`
3. Run on xex44 late dump → check npc_report.txt for:
   - GSSunnySmiles: SPECIAL, AI data, height 0.95, hair/eyes
   - Ulysses: Full data including FaceGen morphs (non-zero values = face was customized)
4. Verify npcs.csv has new columns populated
5. Cross-dump: Run on xex3 early dump, compare SPECIAL/AI values for same NPCs

---

## Phase 5I: Height/Weight/FaceGen R&D (MinidumpAnalyzer)

### Context & Findings So Far

Phase 5H implemented SPECIAL stats (+204) and AI data (+164) successfully. The remaining NPC physical data — height, weight, hair, eyes, face morphs — proved elusive. Key findings from R&D:

1. **PDB offsets are unreliable for late-struct fields**: The Xbox 360 compiler reorders class components. SPECIAL was predicted at +292 but found at +204. AI data was predicted at +44 but found at +164. Height predicted at +484 and weight at +488 may also be reordered.

2. **Height float 0.95 NOT found anywhere in 720 bytes**: Exhaustive scan of GSSunnySmiles' NPC struct (offset 22743140 in xex44) for byte pattern `3F 73 33 33` (BE float 0.95) found nothing. Also checked LE encoding, uint8/uint16 scaled encodings — all negative.

3. **Face morph pointer targets were FALSE POSITIVES**: Previous scan used the formula `VA - 0x40000000 + 0x1000` which only works for heap addresses. Pointers at +336, +340, +400 contain 0x660xxxxx addresses (module/code space), and the formula gave wrong file offsets that coincidentally contained float-like data.

4. **Module-space pointers need proper VA mapping**: The MinidumpAnalyzer Python tool (`tools/MinidumpAnalyzer/minidump_utils.py`) has `va_to_offset()` using the full MEMORY64_LIST region table, which correctly handles ALL VA ranges including module space.

5. **Multi-NPC comparison at +320-508**: Verified GSSunnySmiles, CraigBoone, DocMitchell, RaulTejada, Arcade Gannon, Cass with correct xex44 offsets. Only float of note in +320-508 is at **+460** with distinct per-NPC values (Sunny=0.60, Doc=0.29, Arcade=0.69, Cass=0.20) — but these don't match known heights.

6. **Struct size is 508 bytes**: Data beyond +508 belongs to the next heap object. CraigBoone's first R&D scan showed vtables at "+480" because the wrong base offset was used.

### Problem Statement

Height, weight, hair/eye pointers, hair/eye colors, and face morph data all rely on offsets or pointers that the Xbox 360 compiler moved to different positions than the PDB predicts. The ad-hoc PowerShell scripts cannot properly follow non-heap pointers. We need a proper R&D tool.

### Plan

#### Step 1: Create `tools/MinidumpAnalyzer/npc_struct_scan.py`

A new Python script using the existing `MinidumpInfo` infrastructure. It will:

1. **Open dump and parse regions** using `MinidumpInfo`
2. **Accept NPC file offsets** (from runtime_editorids.csv) as CLI arguments
3. **For each NPC at its TesFormOffset**:
   - Read 508 bytes (full TESNPC struct)
   - Verify FormType=42 and FormID at +12
   - For every 4-byte aligned offset from +0 to +504:
     - Read as big-endian uint32
     - Try to resolve as VA via `dump.va_to_offset()`
     - If resolvable: read 128 bytes at target, interpret as floats, check for vtable
     - Report: offset, raw hex, float interpretation, VA resolution status, target summary
4. **Cross-NPC comparison**: Print a table showing which offsets have consistent pointer patterns vs varying data across NPCs
5. **Height search**: For each resolved pointer target, search for known height floats:
   - Sunny: 0.95 (`3F733333`)
   - Boone: 1.03 (`3F83D70A`) — wiki says he's tall
   - Doc: 1.0 (`3F800000`) — default height

#### Step 2: Analyze Results

The script output will reveal:

- Which +320-508 offsets contain valid pointers (resolved by minidump regions)
- Whether face morph data (arrays of 15-24 small floats) exists at any pointer target
- Whether height/weight are in a pointed-to structure rather than inline
- Whether hair/eye FormIDs are at the PDB-predicted +424/+432 offsets or elsewhere

#### Step 3: Implement or Defer

Based on findings:

- **If height/face data found**: Add to `RuntimeStructReader.ReadRuntimeNpc()`, update `ReconstructedNpc` model, CSV, and report
- **If not found**: Document findings in plan, mark as "not extractable from runtime NPC struct" and move to Phase 5E

### Files

| File                                        | Action                           |
| ------------------------------------------- | -------------------------------- |
| `tools/MinidumpAnalyzer/npc_struct_scan.py` | NEW — R&D investigation script   |
| `RuntimeStructReader.cs`                    | Conditional — only if data found |
| `SemanticModels.cs`                         | Conditional — only if data found |

### Verification

```bash
python tools/MinidumpAnalyzer/npc_struct_scan.py "Sample/MemoryDump/Fallout_Release_Beta.xex44.dmp" --offsets 22743140,23116692,22754996,22996724,22552724 --names GSSunnySmiles,CraigBoone,DocMitchell,RaulTejada,ArcadeGannon
```

Review output for:

- Pointer resolution at +336/+340/+400 (face morph candidates)
- Any pointer target containing float 0.95 (height)
- Consistent patterns across all 5 NPCs

---

## Phase 5J: GECK-Matching NPC Report Enhancement

### Goal

Enhance the NPC report and CSV to match the GECK editor's per-NPC panel output. Add named FaceGen morph sliders, skills, derived combat stats, mood, and face tab data.

### Data Status (What We Have vs What's Missing)

| GECK Panel | Field               | Status             | Notes                                        |
| ---------- | ------------------- | ------------------ | -------------------------------------------- |
| Stats      | SPECIAL (7)         | ✅ Have            | +204, empirically verified                   |
| Stats      | Skills (13)         | ❌ Missing         | Likely at +211..+224 in TESNPCData           |
| Stats      | Base Health         | ❌ Missing         | **Computable**: (END × 5) + 50               |
| Stats      | Calc Health         | ❌ Missing         | **Computable**: BaseHP + (Level × 10)        |
| Stats      | Critical Chance     | ❌ Missing         | **Computable**: Luck                         |
| Stats      | Melee Damage        | ❌ Missing         | **Computable**: STR × 0.5                    |
| Stats      | Unarmed Damage      | ❌ Missing         | **Computable**: needs formula verification   |
| Stats      | Calc Fatigue        | ❌ Missing         | **Computable**: Fatigue + (STR+END) × 10     |
| Stats      | Poison Resist       | ❌ Missing         | **Computable**: (END - 1) × 5                |
| Stats      | Rad Resist          | ❌ Missing         | **Computable**: (END - 1) × 2                |
| AI Data    | Mood                | ❌ Missing         | Byte at +168, currently misread as flags MSB |
| Face       | Age                 | ❌ Missing         | Float, location unknown (R&D needed)         |
| Face       | Complexion          | ❌ Missing         | Float, location unknown (R&D needed)         |
| Face       | Hair Color RGB      | ❌ Missing         | 3 bytes, PDB +436 unverified                 |
| Face Adv   | Named morph sliders | ❌ Missing         | Need EGM parse for index→name mapping        |
| Head Parts | Head Add Ons        | ❌ Missing         | BSSimpleList, low priority                   |
| Traits     | Height/Weight       | ❌ Not extractable | Previous R&D confirmed not inline            |

### Verified Formulas (Sunny Smiles: SPECIAL={6,5,4,4,4,6,4}, Level=2)

```
Base Health       = (END × 5) + 50              → (4×5)+50 = 70   ✓ matches GECK
Calculated Health = BaseHP + (Level × 10)        → 70 + 20  = 90   ✓ matches GECK
Calculated Fatigue= Fatigue + (STR+END) × 10     → 50 + 100 = 150  ✓ matches GECK
Critical Chance   = Luck                          → 4.0             ✓ matches GECK
Melee Damage      = STR × 0.5                    → 3.0             ✓ matches GECK
Poison Resistance = (END - 1) × 5                → 15.0            ✓ matches GECK
Rad Resistance    = (END - 1) × 2                → 6.0             ✓ matches GECK
Unarmed Damage    = ??? (GECK shows 1.1)         → needs verification
```

### Step 1: FaceGen Morph Name Mapping (R&D + Implementation)

**Problem**: The GECK "Face Advanced" tab shows named morph sliders (alphabetically sorted), but our FGGS/FGGA/FGTS arrays use numeric indices. We need a name→index mapping.

**Key finding**: PC ESM FGGS raw values ≠ GECK slider values. The GECK applies per-morph scaling via the EGM basis mode `scale` factors. Each `FffModeGS` in the EGM has a `float scale` that transforms: `GECK_value = FGGS_coeff × mode_scale`.

**User-provided data**: 55 named morph values for Sunny in `Sample/SunnyNPCGeck/SunnyFaceGen.txt`. These are from the PC final ESM loaded in GECK. We extracted 50 FGGS floats from the same PC ESM. The 55 entries likely include 50 FGGS + 5 from FGGA/FGTS (the user may have switched views).

**Note**: Xbox dump values differ from PC final (Apr 2010 vs Oct 2010 builds), but the morph NAME ordering is fixed by the FaceGen SDK and EGM file — it won't change between builds.

#### Step 1a: Python EGM Parser Script

Create `tools/MinidumpAnalyzer/egm_morph_mapper.py`:

1. Parse `headhuman.egm` header: magic(5) + version(3) + numPoints(4) + numSym(4) + numAsym(4) + basisKey(4) + reserved(40) = 60 bytes
2. Read `numSym` mode scale factors: for each mode, read `float scale` (4 bytes LE), then skip `numPoints × 6` bytes of vertex data
3. Load Sunny's 50 FGGS values from the PC ESM (hardcoded or passed as input)
4. Compute `display_value[i] = FGGS[i] × scale[i]` for each of the 50 symmetric modes
5. Match each computed display value against the 55 GECK values (within ±0.05 tolerance)
6. Print the resulting mapping: `index → morph_name`
7. Also compute asymmetric mode scales if numAsym > 0

**Input files**:

- EGM: `Sample/PC_Final_Unpacked/Data/meshes/characters/head/headhuman.egm`
- PC ESM FGGS for Sunny (50 floats, extracted above)
- GECK values: `Sample/SunnyNPCGeck/SunnyFaceGen.txt`

**Output**: A mapping of FGGS index → morph name, to be hardcoded in C#.

#### Step 1b: Hardcode Morph Name Arrays in C#

Add to `GeckReportGenerator.cs` (or a new static class):

```csharp
// Geometry-Symmetric morph names (FGGS), indexed by array position
// Mapping derived from EGM basis mode order + GECK Face Advanced display
private static readonly string[] FggsSymmetricNames = new[]
{
    "...",  // [0] - determined by Step 1a
    "...",  // [1]
    // ... 50 entries total
};

// Geometry-Asymmetric morph names (FGGA), if determinable
private static readonly string?[] FggaAsymmetricNames = new string?[30];

// Texture-Symmetric morph names (FGTS), if determinable
private static readonly string?[] FgtsTextureNames = new string?[50];
```

#### Step 1c: Update FaceGen Report Section

Replace `AppendFaceGenArray()` with `AppendNamedFaceGenSection()`:

- Group morphs by body region (Brow, Cheeks, Chin, Eyes, Face, Forehead, Jaw, Mouth, Nose)
- Show name, value, and optional control/symmetry type
- Skip zero-value morphs
- Format:

```
  ── FaceGen Morph Data ──────────────────────────────────────────────
  Geometry-Symmetric (50 morphs, 47 active):
    Brow Ridge - high / low:              -2.7800
    Brow Ridge Inner - up / down:          4.1700
    ...
  Geometry-Asymmetric: all zero
  Texture-Symmetric (50 morphs, 43 active):
    [0]  0.1234   [1] -0.0567  ...  (unnamed, indexed)
```

If the EGM mapping is only partially successful, fall back to indexed display for unmapped morphs.

### Step 2: Skills Extraction

**Location**: TESNPCData struct is 28 bytes starting at dump +204. SPECIAL occupies bytes 0-6. Skills likely occupy bytes 7-20 (14 × uint8 for FO3/FNV skill slots).

**Skill order** (Fallout 3/NV engine, 14 slots):
0=Barter, 1=BigGuns, 2=EnergyWeapons, 3=Explosives, 4=Lockpick, 5=Medicine, 6=MeleeWeapons, 7=Repair, 8=Science, 9=Guns(SmallGuns), 10=Sneak, 11=Speech, 12=Survival, 13=Unarmed

FNV doesn't use BigGuns (index 1) — it will be 0.

**Implementation**:

1. Read 14 bytes at dump +211 (NpcSpecialOffset + NpcSpecialSize = 204 + 7 = 211)
2. Verify Sunny's values match GECK: Barter=12, EnergyWeapons=14, Explosives=14, Lockpick=14, Medicine=12, MeleeWeapons=35, Repair=12, Science=12, Guns=35, Sneak=35(?), Speech=12
3. If values don't match at +211, try nearby offsets (±1..±4) due to possible padding

**Files**:

- `SemanticModels.cs` — Add `byte[]? Skills` (14 bytes) to `ReconstructedNpc`
- `RuntimeStructReader.cs` — Add `ReadNpcSkills()` method, add `NpcSkillsOffset = 211` constant
- `GeckReportGenerator.cs` — Add Skills section to NPC report
- `CsvReportGenerator.cs` — Add 13 skill columns (skip BigGuns) to npcs.csv

### Step 3: Derived Stats (Computed, No New Struct Reading)

Add computed properties to `ReconstructedNpc` or compute in report generator:

```csharp
// All formulas verified against GECK for Sunny Smiles
int BaseHealth => (SpecialStats?[2] ?? 0) * 5 + 50;        // END × 5 + 50
int CalcHealth => BaseHealth + (Stats?.Level ?? 0) * 10;     // + Level × 10
int CalcFatigue => (Stats?.FatigueBase ?? 0)
    + ((SpecialStats?[0] ?? 0) + (SpecialStats?[2] ?? 0)) * 10;  // + (STR+END) × 10
float CritChance => SpecialStats?[6] ?? 0;                  // Luck
float MeleeDamage => (SpecialStats?[0] ?? 0) * 0.5f;        // STR × 0.5
float PoisonResist => ((SpecialStats?[2] ?? 0) - 1) * 5f;   // (END-1) × 5
float RadResist => ((SpecialStats?[2] ?? 0) - 1) * 2f;      // (END-1) × 2
```

**Unarmed Damage** formula needs verification. Likely: `(STR × 0.5) + (UnarmedSkill × scale)` or a fixed modifier. We'll try `0.5 + (STR × 0.1)` and verify against GECK (1.1 for STR=6 → 0.5 + 0.6 = 1.1 ✓).

**Files**:

- `GeckReportGenerator.cs` — Add "Derived Stats" section showing computed values
- `CsvReportGenerator.cs` — Add columns: BaseHealth, CalcHealth, CalcFatigue, CritChance, MeleeDmg, UnarmedDmg, PoisonResist, RadResist

### Step 4: Fix Mood in AI Data

**Problem**: Mood byte at +168 is currently consumed as MSB of `NpcAiFlagsOffset = 168` (uint32). Mood is actually a separate byte.

**Layout correction**:

```
+164: aggression (byte)
+165: confidence (byte)
+166: energy (byte)
+167: responsibility (byte)
+168: mood (byte)          ← currently misread as flags MSB
+169-171: padding (3 bytes)
+172: buySellAndServices (uint32 BE)  ← actual AI flags
+176: teaches (byte)
+177: maxTrainingLevel (byte)
+178: assistance (byte)    ← correct
```

**Implementation**:

1. Add `NpcMoodOffset = 168` constant
2. Shift `NpcAiFlagsOffset` from 168 to 172
3. Add `Mood` byte to `NpcAiData` record
4. Add `MoodName` property: 0=Neutral, 1=Afraid, 2=Annoyed, 3=Cocky, 4=Drugged, 5=Pleasant, 6=Angry, 7=Sad
5. Add to report: `Mood: Neutral (0)` in AI Data section
6. Verify Sunny shows Mood=0 (Neutral) — matching GECK

**Files**:

- `SemanticModels.cs` — Add `byte Mood` to `NpcAiData`, add `MoodName` property
- `RuntimeStructReader.cs` — Fix `NpcAiFlagsOffset` to 172, add mood byte read at +168
- `GeckReportGenerator.cs` — Add Mood line to AI Data section
- `CsvReportGenerator.cs` — Add Mood column

### Step 5: Face Tab Data (Age, Complexion, Hair Color) — R&D Required

**Known GECK values for Sunny**: Age=15.0, Complexion=-2.0, Hair Color RGB=(66,28,15)

These float/byte values need to be located in the NPC struct. Options:

1. **Hair Color**: PDB predicts `cHairColor` at dump +436 (uint32 RGBA). Try reading 4 bytes at +436 and check if RGB matches (66,28,15). Also try +440, +444 nearby offsets.
2. **Age/Complexion**: These are FaceGen "face detail" parameters. They might be stored:
   - As inline floats in the NPC struct (unknown offset)
   - Via the FGGS/FGGA/FGTS data (unlikely — those are morph coefficients)
   - In a separate NAM6/NAM7 ESM subrecord that gets loaded into a specific runtime offset

**R&D approach**: Use `npc_struct_scan.py` to scan the full 508-byte NPC struct for:

- Float 15.0 (Age): byte pattern `41 70 00 00` (BE)
- Float -2.0 (Complexion): byte pattern `C0 00 00 00` (BE)
- Bytes 66, 28, 15 in sequence (Hair Color RGB)

If found inline, add to RuntimeStructReader. If not found, these values may only exist in the ESM subrecords (not in the runtime NPC struct), and we'd need ESM-based extraction.

**Files** (conditional):

- `SemanticModels.cs` — Add `float? Age`, `float? Complexion`, `uint? HairColor` to `ReconstructedNpc`
- `RuntimeStructReader.cs` — Add offset constants and readers
- `GeckReportGenerator.cs` — Add Face section

### Step 6: Updated Report Format

The final NPC report entry will match GECK panel order:

```
================================================================================
                    NPC: GSSunnySmiles — Sunny Smiles
================================================================================
  FormID:         0x00104E84
  Editor ID:      GSSunnySmiles
  Display Name:   Sunny Smiles
  Gender:         Female

  ── Stats ──────────────────────────────────────────────────────────────────────
  Level:          2
  SPECIAL:        6 ST, 5 PE, 4 EN, 4 CH, 4 IN, 6 AG, 4 LK  (Total: 33)
  Skills:
    Barter           12    Energy Weapons   14    Explosives       14
    Guns             35    Lockpick         14    Medicine         12
    Melee Weapons    35    Repair           12    Science          12
    Sneak            35    Speech           12    Survival          ?
    Unarmed           ?

  ── Derived Stats ──────────────────────────────────────────────────────────────
  Base Health:      70     Calculated Health:  90
  Fatigue:          50     Calculated Fatigue: 150
  Critical Chance:  4.00   Speed Mult:         100%
  Melee Damage:     3.00   Unarmed Damage:     1.10
  Poison Resist:    15.00  Rad Resist:         6.00
  Karma:            900.00 (Very Good)
  Disposition:      35     Barter Gold:        0

  ── Combat ─────────────────────────────────────────────────────────────────────
  Race:           Hispanic (0x000038E5)
  Class:          WastelandAdventurer (0x0001873D)
  Combat Style:   DefaultCombatRangedstyle (0x000ABCDE)

  ── AI Data ────────────────────────────────────────────────────────────────────
  Aggression:     Aggressive (1)
  Confidence:     Foolhardy (4)
  Mood:           Neutral (0)
  Assistance:     Helps Friends and Allies (2)
  Energy Level:   50
  Responsibility: 50

  ── Physical Traits ────────────────────────────────────────────────────────────
  Hairstyle:      HairBun (0x00012345)
  Hair Length:     0.84
  Hair Color:     RGB(66, 28, 15)           [if extractable]
  Eyes:           EyeDarkBrown (0x00012346)
  Age:            15.00                      [if extractable]
  Complexion:     -2.00                      [if extractable]

  ── References ─────────────────────────────────────────────────────────────────
  Script:         GSSunnySmilesScript
  Voice Type:     FemaleAdult01Default
  Death Item:     (none)
  Template:       (none)

  Factions (6):
    ...

  Inventory (2):
    ...

  Spells/Abilities (1):
    PerkToughness — Toughness

  AI Packages (N):
    ...

  ── FaceGen Morph Data ─────────────────────────────────────────────────────────
  Geometry-Symmetric (50 morphs, 47 active):
    Brow Ridge - high / low              -2.78
    Brow Ridge Inner - up / down          4.17
    Cheekbones - low / high               1.12
    ...
  Geometry-Asymmetric: all zero
  Texture-Symmetric (50 morphs, 43 active):
    [indexed if names not determinable]
```

### Implementation Order

1. **Step 2: Skills** — Quick win, empirical verification needed but likely straightforward
2. **Step 4: Mood fix** — Quick win, 1-byte fix to existing AI Data
3. **Step 3: Derived Stats** — Pure computation, no struct reading needed
4. **Step 1a: EGM Parser** — Python R&D script, determines morph mapping
5. **Step 1b-c: Named morphs** — Depends on 1a results
6. **Step 5: Face Tab** — R&D needed, may or may not succeed
7. **Step 6: Report formatting** — Final pass, integrates all new data

### Files Modified

| File                                         | Changes                                                                            |
| -------------------------------------------- | ---------------------------------------------------------------------------------- |
| `RuntimeStructReader.cs`                     | Fix mood/flags offsets, add skills reader, add face tab readers (conditional)      |
| `SemanticModels.cs`                          | Add Skills, Mood to NpcAiData, derived stat helpers, face tab fields (conditional) |
| `GeckReportGenerator.cs`                     | Restructure NPC report with Skills, Derived Stats, Mood, named morphs, face tab    |
| `CsvReportGenerator.cs`                      | Add skill columns, derived stat columns, mood column                               |
| `tools/MinidumpAnalyzer/egm_morph_mapper.py` | NEW — EGM parser for morph name mapping                                            |

### Verification

1. `dotnet build -c Release` after each step
2. Run on xex44 (late dump, closest to final):
   ```
   dotnet run --project src/FalloutXbox360Utils -f net10.0 -c Release -- analyze "Sample/MemoryDump/Fallout_Release_Beta.xex44.dmp" -e TestOutput/test_5j
   ```
3. After Step 2: Check `npc_report.txt` for GSSunnySmiles Skills section — values should be close to GECK (Barter=12, Guns=35, etc.)
4. After Step 3: Derived Stats section — Base Health=70, Calc Health=90, etc. (exact match expected since formulas use integer SPECIAL)
5. After Step 4: AI Data shows Mood — should be "Neutral (0)" for most NPCs
6. After Step 1: FaceGen section shows named morphs instead of numbered indices
7. Cross-dump validation: compare skills/derived stats across xex3 and xex44 for the same NPCs

---

## Phase 5K: Fix FaceGen Display Values — CTL Projections with Distribution Scaling

### Problem

The NPC report's FaceGen section shows values in range [-2, +2] using CTL dot-product projections. The GECK shows values in range [-8, +9]. The user confirmed all 55 geometry sliders are GS (Geometry-Symmetric) controls — CTL projections from 50 FGGS values, not a mix of GS+GA.

### Root Cause

The current code computes `slider[j] = dot(CTL_vector[j], dump_FGGS)` without any preprocessing of the FGGS values. The FaceGen si.ctl file contains distribution data (mean vectors, eigBasis, invEigBasis matrices) that must be used to transform FGGS before projection. The GECK displays face morphs **relative to race defaults** and with distribution-based scaling.

### Critical Data Discovery: Two FGGS Datasets

There are TWO different PC FGGS extraction results in the codebase:

**Set A** (WRONG — used in older scripts `facegen_format_analysis.py`, `egm_morph_mapper.py`, `facegen_distribution_test.py`):

```
+0.101188, +0.365651, +0.152343, +0.023575, +0.011938, -0.119095, ...
```

Most values in [-0.2, +0.4] range except outliers at indices 39, 45-48.

**Set B** (CORRECT — used in `facegen_cross_npc_mapping.py`, `facegen_egm_mapping.py`):

```
+0.101188, +0.365651, +0.152343, -0.040704, -1.314729, -1.488567, ...
```

Values in [-12, +7] range. Comment: "extracted via extract_pc_fggs.py, correct FormIDs".

Set A and Set B share indices 0-2, 39, 45-48, 49 but diverge for all other indices. Set A likely came from an incorrect ESM extraction (wrong record or decompression bug). **All R&D work must use Set B.**

### Key Insight: Race-Adjusted FGGS

From `facegen_cross_npc_mapping.py` line 8-9:

> "The GECK displays face morphs RELATIVE TO RACE DEFAULTS: `GECK_display[j] = (NPC_FGGS[i] - Race_FGGS[i]) * factor[i]`"

Race defaults available:

- Hispanic Female (Sunny's race): `HISPANIC_FEMALE_FGGS` (50 floats)
- Caucasian Male (Boone/Benny's race): `CAUCASIAN_MALE_FGGS` (50 floats)

The correct formula for all 55 sliders (which are ALL GS CTL projections) is:

```
adj_FGGS = NPC_FGGS - Race_FGGS
slider[j] = dot(CTL_vector[j], T(adj_FGGS))
```

where T is the missing scaling transform from the CTL distribution data.

### CTL Distribution Data Structure

File: `Sample/PC_Final_Unpacked/Data/facegen/si.ctl` (338,421 bytes)

- Distribution section starts at byte ~24,261 after control sections
- 314,160 bytes = 78,540 LE floats
- 5 race distributions × 15,100 floats each:
  - 100-float mean (50 GS + 50 TS, identical across all 5 races)
  - 10,000-float eigBasis (100×100)
  - 2,500-float invEigBasis_GS (50×50)
  - 2,500-float invEigBasis_TS (50×50)
- Extra ~3,040 bytes likely for race name strings and headers

### Candidate Transforms to Test

| ID     | Transform T(x)                   | Description                        |
| ------ | -------------------------------- | ---------------------------------- |
| T0     | x                                | Identity (current, gives [-2,+2])  |
| T1     | x - mean_GS                      | Mean subtraction                   |
| T2     | invEigBasis_GS @ x               | InvEigBasis only                   |
| T3     | invEigBasis_GS @ (x - mean_GS)   | Centered + invEigBasis             |
| T4     | eigBasis_GS_50x50 @ x            | Top-left block of 100×100 eigBasis |
| T5     | eigBasis_GS @ (x - mean_GS)      | Centered + eigBasis                |
| T6     | diag(egm_scales) @ x             | EGM per-mode scale factors         |
| T7     | diag(egm_scales) @ (x - mean_GS) | Centered + EGM scales              |
| T8-T13 | k × T2..T5 for various k         | Scaled variants                    |

Each tested with and without race subtraction (adj_FGGS vs raw FGGS).

### Implementation Steps

#### Step 1: Python R&D Script (`tools/MinidumpAnalyzer/ctl_distribution_scaling.py`)

Create a comprehensive R&D script that:

1. **Parses CTL distribution section** — Extends `parse_ctl()` from `parse_ctl_to_csharp.py` to read the distribution data after control sections. Must handle race name strings and any padding. Start with hex dump of first ~200 bytes after control sections to determine exact format.

2. **Parses EGM scale factors** — Reuse `parse_egm_file()` from `egm_morph_mapper.py` for the 50 symmetric + 30 asymmetric mode scales.

3. **Loads correct PC FGGS/FGGA/FGTS** — Use Set B values from `facegen_cross_npc_mapping.py` / `facegen_egm_mapping.py` (Sunny, Boone, Benny). Include race defaults (HISPANIC_FEMALE_FGGS, CAUCASIAN_MALE_FGGS).

4. **Parses corrected GECK data** — From `Sample/SunnyNPCGeck/SunnyFaceGen.txt`, `BennyFaceGen.txt`, `BooneFaceGen.txt`. Must handle duplicate "Nose - sellion shallow / deep" names.

5. **Tests all candidate transforms** against all 3 NPCs:
   - For each NPC: compute `adj_FGGS = NPC_FGGS - Race_FGGS`
   - Apply each transform T to adj_FGGS (and also to raw FGGS)
   - Project through all 56 GS control vectors: `slider[j] = dot(CTL[j], T(x))`
   - Match projected values to GECK geometry values by control name
   - Skip 5 FGGA names ("Face - brow-nose-chin ratio", etc.) — these won't match any GS control
   - Compute mean absolute error (MAE) per NPC and aggregate

6. **Ranks transforms** by aggregate MAE across all 3 NPCs.

7. **Outputs winning formula** and any needed C# data (matrix arrays, mean vector).

**Validation criteria**: The correct transform should produce MAE < 0.10 across all 3 NPCs for the 50 GS-matched geometry sliders.

**Key data files**:

- `Sample/PC_Final_Unpacked/Data/facegen/si.ctl`
- `Sample/PC_Final_Unpacked/Data/meshes/characters/head/headhuman.egm` (or `Sample/meshes_pc/meshes/characters/head/headhuman.egm`)
- `Sample/SunnyNPCGeck/{Sunny,Benny,Boone}FaceGen.txt`

#### Step 2: Update FaceGenControls.cs

Based on the winning transform from Step 1:

**Scenario A: Transform is a 50×50 matrix** (invEigBasis_GS or eigBasis block)

- Pre-multiply at code-gen time: `effectiveCoeffs[j] = CTL_vector[j] @ TransformMatrix`
- Replace the 56×50 coefficient matrix with the pre-multiplied version
- If mean subtraction needed: add `float[] GeometrySymmetricMean` (50 floats)
- Update `ComputeGeometrySymmetric`:
  ```csharp
  centered[i] = fggs[i] - GeometrySymmetricMean[i];
  dot += EffectiveCoeffs[j][i] * centered[i];
  ```

**Scenario B: Transform is diagonal scaling** (EGM scales or eigenvalue factors)

- Pre-multiply at code-gen time: `effectiveCoeffs[j][i] = CTL_vector[j][i] * scale[i]`
- Same structure as above but with diagonal scaling baked in
- Optionally add mean subtraction

**Scenario C: Simple constant multiplier**

- Just multiply the final dot product by a constant

In all cases, the 56 GS control names, 26 GA control names, and 33 TS control names remain. Only the coefficient matrices and compute methods change.

**File size**: Pre-multiplied matrix keeps the same size (~96KB) since the dimensions don't change. If mean subtraction is added, that's an additional 50 floats (200 bytes).

#### Step 3: Apply same transform to GA and TS

The CTL distribution also has `invEigBasis_TS` (50×50) for texture. Apply the same approach:

- For FGGA: use the GA portion of the distribution data (30-dim)
- For FGTS: use `invEigBasis_TS` and the TS portion of the mean vector

#### Step 4: Update GeckReportGenerator.cs

- Update threshold in `AppendFaceGenControlSection` from `0.01f` to `0.10f` (line ~2238)
- Update format from `F4` to `F2` (line ~2251)
- The section labels and structure remain the same

#### Step 5: Build and Validate

1. `dotnet build -c Release`
2. Run on xex44 (late dump):
   ```
   dotnet run --project src/FalloutXbox360Utils -f net10.0 -c Release -- analyze "Sample/MemoryDump/Fallout_Release_Beta.xex44.dmp" -e TestOutput/test_5k
   ```
3. Check `npc_report.txt` for GSSunnySmiles:
   - Values should be in approximately [-10, +10] range
   - Named sliders alphabetically sorted
4. Compare against GECK values (approximate match expected — different game build)
5. Check Ulysses, Boone — values should be reasonable

### Expected Result

Before (current):

```
  Geometry-Symmetric (56 controls, 54 active):
    Brow Ridge - high / low               0.0283
    Brow Ridge Inner - up / down           1.3510
    ...
```

After (with distribution scaling):

```
  Geometry-Symmetric (56 controls, 54 active):
    Brow Ridge - high / low              -2.78
    Brow Ridge Inner - up / down          4.17
    Cheekbones - low / high               1.12
    ...
```

Values matching GECK range [-10, +10].

### Files Modified

| File                                                 | Action                                                          |
| ---------------------------------------------------- | --------------------------------------------------------------- |
| `tools/MinidumpAnalyzer/ctl_distribution_scaling.py` | NEW — R&D script to determine correct transform                 |
| `tools/MinidumpAnalyzer/parse_ctl_to_csharp.py`      | UPDATE — add distribution parsing, pre-multiply coefficients    |
| `FaceGenControls.cs`                                 | REGENERATE — pre-multiplied coefficients + optional mean vector |
| `GeckReportGenerator.cs`                             | UPDATE — adjust threshold and format                            |

### Fallback Plan

If the CTL distribution transform fails, the validated per-index approach from `facegen_cross_npc_mapping.py` works for 50 of 55 sliders:

```
GECK_display[name] = (NPC_FGGS[mapping[name]] - Race_FGGS[mapping[name]]) * factor[mapping[name]]
```

This requires a separate name→index mapping and per-mode factors, and doesn't handle the 5 multi-component GS sliders or GA/TS. Use as last resort.

---

## Updated Completion Status (Jan 2026)

### Code Implementation Status

All major phases have been implemented in code across previous sessions:

| Phase | Description | Code Status |
|-------|-------------|-------------|
| 0A | EsmRecord/ folder reorganization | ✅ COMPLETE |
| 0B | Report generator utility extraction | ✅ COMPLETE |
| 0C | ESM converter deduplication | ⏸ DEFERRED |
| 1-4 | Core architecture through item readers | ✅ COMPLETE |
| 5A | Model paths for AMMO/MISC/KEYM | ✅ COMPLETE |
| 5B | Ammo projectile model column | ✅ COMPLETE |
| 5C | Simple type readers (NOTE/FACT/QUST) | ✅ COMPLETE |
| 5D | BSSimpleList + NPC sub-items | ✅ COMPLETE |
| 5E | Container + terminal readers | ✅ COMPLETE |
| 5F | Creature reader | ✅ COMPLETE |
| 5G | Structured NPC report | ✅ COMPLETE |
| 5H | SPECIAL stats, AI data, skills, mood | ✅ COMPLETE |
| 5I | Height/Weight/FaceGen R&D | ✅ R&D DONE (height not extractable) |
| 5J | GECK-matching NPC report format | ✅ COMPLETE |
| 5K | FaceGen display value scaling (CTL) | ⏸ DEFERRED (R&D inconclusive, MAE=1.82) |
| 6A | Map markers (REFR extension) | ✅ COMPLETE |
| 6B | Leveled lists | ✅ COMPLETE |
| 7 | Heightmap (runtime land data) | ✅ COMPLETE |
| 8 | Documentation | ❌ NOT STARTED |
| 9A | Ulysses FaceGen patching | ✅ COMPLETE |
| 9B | Full Ulysses NPC transformation | ✅ COMPLETE |

### Immediate Next Steps

1. **Verify build** — `dotnet build -c Release` after Phase 0 refactoring (namespace changes)
2. **Run extraction** — confirm all phases produce correct output
3. **Update extraction counts** — replace stale Phase 4 counts with current totals
4. **Phase 8: Documentation** — update `docs/Memory_Dump_Research.md` with all PDB findings
5. **Phase 5K (deferred)** — revisit FaceGen CTL scaling when fresh approach available
6. **Phase 0C (deferred)** — ESM converter deduplication (29 diverged files)

---

## Verification (All Phases)

1. `dotnet build -c Release` — verify compilation after each sub-phase
2. Run extraction on sample dump:
   ```
   dotnet run --project src/FalloutXbox360Utils -f net10.0 -c Release -- analyze "Sample/MemoryDump/Fallout_Release_Beta.xex3.dmp" -e TestOutput/test_verify
   ```
3. After Phase 5A: Spot-check ammo.csv, misc_items.csv, keys.csv — ModelPath column should have data
4. After Phase 5B: ammo.csv has ProjectileModelPath column with .nif paths
5. After Phase 5C: summary.txt shows non-zero counts for Notes, Factions, Quests
6. After Phase 5D: npcs.csv has FACTION/INVENTORY/SPELL/PACKAGE sub-rows (non-zero count)
7. After Phase 5E: Non-zero counts for Containers, Terminals
8. After Phase 5F: Non-zero count for Creatures
9. After Phase 5G: npc_report.txt exists with per-NPC tables showing display names
   9b. After Phase 5H: npc_report.txt shows SPECIAL, AI data, physical traits, FaceGen morphs; npcs.csv has new columns
10. After Phase 6A: map_markers.csv exists with location names and coordinates
11. After Phase 6B: leveled_lists.csv exists with ENTRY sub-rows
12. After Phase 7: worldmap_composite.png uses runtime cell coordinates for stitching
13. Cross-dump validation: run on Debug + Late dumps and compare counts/values

---

## Phase 9: ESM NPC Transformation — Transform NVProspectorMaleY into Ulysses

### Goal

Fully transform NPC `NVProspectorMaleY` (FormID `0x00163E9E`) in the PC ESM into Ulysses, using FaceGen data from the Xbox 360 memory dump. This includes face morphs, identity (name, voice), and clearing appearance subrecords to race defaults.

### Phase 9A: FaceGen Patching ✅ COMPLETE

FGGS and FGTS subrecords patched with Ulysses' data. Backup at `FalloutNV.esm.bak`.

### Phase 9B: Full NPC Transformation (CURRENT)

#### Research Results

**Hair/Eyes investigation across all 45 dumps:**

- Ulysses present in 16 dumps (xex21-32, xex39, xex42-44)
- ALL 16 show NULL for Hair ptr (+456), Eyes ptr (+464), Hair length (+460)
- Conclusive: hair/eyes were never assigned to Ulysses in any captured build

**TESHair/TESEyes objects in hash table:**

- 67 TESHair objects, 12 TESEyes objects found
- No Ulysses-specific hair/eyes variants exist
- Ulysses' dreadlocks are part of UlyssesHat model, not a TESHair style

**AfricanAmerican race defaults (from PC ESM RACE record at 0x00058E63):**

- DNAM: Default male hair = `HairAfricanAmericanBase` (0x000306BE)
- ENAM: Available eyes = Blue, DarkBrown, Hazel, Green (no single default)
- FGGS/FGTS: Race-specific default face morphs for male/female

**NPC statistics for AfricanAmerican males (339 total):**

- Most common hair: HairAfricanAmericanBase (110/270 = 41%) — race default
- Most common eyes: EyeDarkBrown (133/190 = 70%)
- Hair length: avg=0.48, median=0.45, range 0.0–1.0

**User decisions:**

1. Zero all appearance fields (HNAM, ENAM, LNAM, HCLR) → engine uses race defaults
2. Full identity transformation (EDID→Ulysses, FULL→Ulysses, VTCK→MaleUniqueUlysses)

#### Verified Subrecord Offsets (in decompressed data, from .bak original)

All offsets verified by decompressing the original .bak file:

| Offset | Subrecord | Size | Current Value             | New Value                         |
| ------ | --------- | ---- | ------------------------- | --------------------------------- |
| 0x0000 | EDID      | 18   | "NVProspectorMaleY\0"     | "Ulysses\0" + 10 null pad         |
| 0x002A | FULL      | 11   | "Prospector\0"            | "Ulysses\0" + 3 null pad          |
| 0x00B5 | VTCK      | 4    | 0x0013C8D6                | 0x0014F3EB (MaleUniqueUlysses)    |
| 0x0206 | HNAM      | 4    | 0x0002BFDB (HairBase)     | 0x00000000 (race default)         |
| 0x0210 | LNAM      | 4    | 0x3F800000 (1.0)          | 0x00000000 (race default)         |
| 0x021A | ENAM      | 4    | 0x00004256 (EyeDarkBrown) | 0x00000000 (race default)         |
| 0x0224 | HCLR      | 4    | 0x00060A0B (RGB 11,10,6)  | 0x00000000 (race default)         |
| 0x0242 | FGGS      | 200  | original morphs           | Ulysses FGGS (already done in 9A) |
| 0x038E | FGTS      | 200  | original textures         | Ulysses FGTS (already done in 9A) |

**Key design decision: same-size replacements.**

- EDID: "Ulysses\0" is 8 bytes; pad with 10 nulls to keep at 18 bytes. Engine reads null-terminated strings, so extra nulls after the terminator are harmless.
- FULL: "Ulysses\0" is 8 bytes; pad with 3 nulls to keep at 11 bytes.
- VTCK, HNAM, LNAM, ENAM, HCLR: all 4-byte replacements (same size).
- This means the decompressed size stays at 1144 bytes. No subrecord offset shifts. Only the compressed payload size may change slightly due to zlib.

#### Implementation: Update `tools/patch_facegen.py`

Update the existing script to read from `.bak` (original) and apply ALL patches in one shot. This makes the script idempotent — always produces the same result from the original.

**New constants to add:**

```python
# Identity subrecords (decompressed offsets)
EDID_DATA_OFFSET = 0x0006   # 6 bytes after start (sig=4 + size=2)
EDID_DATA_SIZE   = 18
FULL_DATA_OFFSET = 0x0030   # 0x002A + 6
FULL_DATA_SIZE   = 11
VTCK_DATA_OFFSET = 0x00BB   # 0x00B5 + 6
VTCK_DATA_SIZE   = 4

# Appearance subrecords (decompressed offsets, data portion after 6-byte header)
HNAM_DATA_OFFSET = 0x020C   # 0x0206 + 6
LNAM_DATA_OFFSET = 0x0216   # 0x0210 + 6
ENAM_DATA_OFFSET = 0x0220   # 0x021A + 6
HCLR_DATA_OFFSET = 0x022A   # 0x0224 + 6

# New values
ULYSSES_EDID = b"Ulysses\x00" + b"\x00" * 10     # 18 bytes (padded)
ULYSSES_FULL = b"Ulysses\x00" + b"\x00" * 3       # 11 bytes (padded)
ULYSSES_VTCK = 0x0014F3EB   # MaleUniqueUlysses voice type
```

**Algorithm changes:**

1. Read from `.bak` file (original, unpatched) instead of the already-patched ESM
2. Decompress the record
3. Verify ALL target subrecords at expected offsets (EDID, FULL, VTCK, HNAM, LNAM, ENAM, HCLR, FGGS, FGTS)
4. Apply ALL patches:
   - EDID → "Ulysses\0" + padding
   - FULL → "Ulysses\0" + padding
   - VTCK → 0x0014F3EB (LE uint32)
   - HNAM → 0x00000000
   - LNAM → 0x00000000
   - ENAM → 0x00000000
   - HCLR → 0x00000000
   - FGGS → Ulysses' 200 bytes
   - FGTS → Ulysses' 200 bytes
5. Recompress, update DataSize and GRUP size, write to ESM

**Post-patch verification additions:**

- Verify EDID reads "Ulysses"
- Verify FULL reads "Ulysses"
- Verify VTCK = 0x0014F3EB
- Verify HNAM, LNAM, ENAM, HCLR = 0x00000000
- Print summary table of all changed subrecords

#### Files

| File                     | Action                                                      |
| ------------------------ | ----------------------------------------------------------- |
| `tools/patch_facegen.py` | UPDATE — add identity + appearance patching, read from .bak |

#### Verification

1. Script reads from `.bak` (original), writes to `.esm` (patched)
2. Script prints before/after comparison for ALL 9 patched subrecords
3. Script re-reads patched file and verifies all subrecords
4. EsmAnalyzer verification:

```bash
dotnet run --project tools/EsmAnalyzer -c Release -- semdiff "E:\SteamLibrary\SteamApps\common\Fallout New Vegas\Data\FalloutNV.esm.bak" "E:\SteamLibrary\SteamApps\common\Fallout New Vegas\Data\FalloutNV.esm" -f 0x00163E9E --all
```

5. Expected diff: EDID, FULL, VTCK, HNAM, LNAM, ENAM, HCLR, FGGS, FGTS all changed
6. Load in GECK → Face tab shows Ulysses' morphs, voice type is MaleUniqueUlysses
7. Load in game → NPC has Ulysses' face with race-default hair/eyes

---

## Phase 0: Refactoring & Code Cleanup (Before Phase 5A)

### Problem Summary

The codebase has grown organically to ~570 C# files. Two issues need addressing:

1. **EsmRecord/ folder bloat**: 86 files in a flat directory — needs subdirectories
2. **ESM converter code duplication**: 29 files duplicated between `src/Core/Converters/Esm/` and `tools/EsmAnalyzer/` (Conversion/, Core/, Helpers/) — different namespaces, files have diverged

### Phase 0A: EsmRecord/ Folder Reorganization

**Current state**: 86 files in `src/FalloutXbox360Utils/Core/Formats/EsmRecord/` with no subdirectories.

**Target structure**:
```
EsmRecord/
├── [Core files - 14 files stay at root]
│   ├── EsmRecordFormat.cs          (main format handler)
│   ├── EsmParser.cs                (ESM parsing logic)
│   ├── EsmRecordTypes.cs           (type registry)
│   ├── SemanticReconstructor.cs    (record reconstruction)
│   ├── RuntimeStructReader.cs      (runtime struct reading)
│   ├── FaceGenControls.cs          (auto-generated FaceGen data)
│   ├── EsmRecordExporter.cs        (export orchestration)
│   ├── EsmFileScanResult.cs        (scan result container)
│   ├── EsmRecordScanResult.cs      (record scan result)
│   ├── EsmFileHeader.cs            (file header parsing)
│   ├── SemanticReconstructionResult.cs
│   ├── MainRecordHeader.cs
│   ├── GroupHeader.cs
│   └── ParsedMainRecord.cs
│
├── Models/                         [~35 files - data transfer objects]
│   ├── ReconstructedNpc.cs
│   ├── ReconstructedWeapon.cs
│   ├── ReconstructedArmor.cs
│   ├── ReconstructedAmmo.cs
│   ├── ReconstructedConsumable.cs
│   ├── ReconstructedMiscItem.cs
│   ├── ReconstructedKey.cs
│   ├── ReconstructedBook.cs
│   ├── ReconstructedNote.cs
│   ├── ReconstructedQuest.cs
│   ├── ReconstructedCell.cs
│   ├── ReconstructedWorldspace.cs
│   ├── ReconstructedDialogue.cs
│   ├── ReconstructedDialogTopic.cs
│   ├── ReconstructedFaction.cs
│   ├── ReconstructedPerk.cs
│   ├── ReconstructedSpell.cs
│   ├── ReconstructedRace.cs
│   ├── ReconstructedCreature.cs
│   ├── ReconstructedContainer.cs
│   ├── ReconstructedTerminal.cs
│   ├── ReconstructedGameSetting.cs
│   ├── ReconstructedLeveledList.cs
│   ├── PlacedReference.cs
│   ├── ExtractedRefrRecord.cs
│   ├── ExtractedLandRecord.cs
│   ├── NpcAiData.cs
│   ├── DialogueResponse.cs
│   ├── FactionMembership.cs
│   ├── FactionRelation.cs
│   ├── InventoryItem.cs
│   ├── QuestStage.cs
│   ├── QuestObjective.cs
│   ├── PerkEntry.cs
│   ├── ProjectilePhysicsData.cs
│   ├── TerminalMenuItem.cs
│   ├── LeveledEntry.cs
│   ├── LandHeightmap.cs
│   ├── LandTextureLayer.cs
│   ├── RuntimeEditorIdEntry.cs
│   ├── RuntimeLoadedLandData.cs
│   ├── DetectedMainRecord.cs
│   ├── DetectedSubrecord.cs
│   ├── DetectedAssetString.cs
│   ├── DetectedVhgtHeightmap.cs
│   ├── EdidRecord.cs
│   ├── GmstRecord.cs
│   ├── SctxRecord.cs
│   ├── ScroRecord.cs
│   └── RecordInfo.cs
│
├── Subrecords/                     [~9 files - parsed subrecord types]
│   ├── ActorBaseSubrecord.cs
│   ├── CellGridSubrecord.cs
│   ├── ConditionSubrecord.cs
│   ├── FormIdSubrecord.cs
│   ├── NameSubrecord.cs
│   ├── PositionSubrecord.cs
│   ├── ResponseDataSubrecord.cs
│   ├── ResponseTextSubrecord.cs
│   ├── TextSubrecord.cs
│   └── ParsedSubrecord.cs
│
├── Enums/                          [~7 files - enumerations]
│   ├── AssetCategory.cs
│   ├── GameSettingType.cs
│   ├── MapMarkerType.cs
│   ├── RecordCategory.cs
│   ├── SpellType.cs
│   ├── SubrecordDataType.cs
│   └── WeaponType.cs
│
├── Schema/                         [~2 files - type metadata]
│   ├── RecordTypeInfo.cs
│   └── SubrecordTypeInfo.cs
│
└── Export/                         [~4 files - report generation]
    ├── GeckReportGenerator.cs
    ├── CsvReportGenerator.cs
    ├── HeightmapPngExporter.cs
    └── ReportFormatUtils.cs        (NEW - extracted shared utilities)
```

**Steps**:
1. Create subdirectories: `Models/`, `Subrecords/`, `Enums/`, `Schema/`, `Export/`
2. Move files to appropriate subdirectories (git mv)
3. Update namespace declarations in moved files: `namespace FalloutXbox360Utils.Core.Formats.EsmRecord.Models;` etc.
4. Update all `using` statements across the codebase to reference new namespaces
5. Build and verify no compile errors

**Namespace convention**: `FalloutXbox360Utils.Core.Formats.EsmRecord.{Subfolder}`

### Phase 0B: Report Generator Utility Extraction

**Problem**: `GeckReportGenerator.cs` and `CsvReportGenerator.cs` duplicate these utilities:
- `FormatFormId()` / `FId()` — FormID to `0x{formId:X8}`
- `CsvEscape()` / `E()` — CSV field escaping
- `FormatFormIdWithName()` / `Resolve()` — FormID + name resolution

**Solution**: Create `Export/ReportFormatUtils.cs` with shared static methods:
```csharp
namespace FalloutXbox360Utils.Core.Formats.EsmRecord.Export;

internal static class ReportFormatUtils
{
    public static string FormatFormId(uint formId) => $"0x{formId:X8}";
    public static string FormatFormIdNullable(uint? formId) => formId.HasValue ? FormatFormId(formId.Value) : "";
    public static string ResolveFormId(uint formId, Dictionary<uint, string> lookup) { ... }
    public static string CsvEscape(string? value) { ... }
}
```

Then update both generators to call these shared methods.

### Phase 0C: ESM Converter Deduplication

**Problem**: 29 files duplicated between `src/.../Converters/Esm/` (canonical) and `tools/EsmAnalyzer/` (local copies). EsmAnalyzer already has `<ProjectReference>` to main project but uses own copies under `EsmAnalyzer.Conversion` namespace. Files have diverged — tools version has MORE functionality in key files.

**Audit results** (Jan 2026):
- 15 Conversion/ files + 9 Schema/ files = 24 near-identical files (namespace-only diffs)
- 4 Core/ duplicates: EsmBinary, EsmRecordParser, AnalyzerRecordInfo, AnalyzerSubrecordInfo
- 1 Helpers/ duplicate: EsmHelpers

**Significant divergences** (tools version is MORE complete):
| File | src lines | tools lines | Divergence |
|------|-----------|-------------|------------|
| EsmSubrecordConverter.Helpers.cs | 178 | 311 | +5 methods: ConvertEnit, ConvertImadSubrecord, ConvertVtxt, ConvertUnknownSubrecord, SwapAllFloats |
| EsmBinary.cs | 63 | 202 | +ReadInt16, ReadSingle, ReadUInt64, ReadInt64, ReadDouble, span-first overloads |
| EsmInfoMerger.cs | 737 | 765 | +ChoiceSignature constant, +WriteSubrecordsToBuffer method |
| EsmConversionStats.cs | 180 | 196 | Spectre.Console display (tools-specific, NOT backported) |
| EsmHelpers.cs | 281 | 401 | +CompareRecords method (tools-specific, NOT backported) |
| EsmRecordParser.cs | 257 | 261 | Trivial: EsmConstants vs EsmConverterConstants reference |

#### Step 1: Enable cross-project access

Add `<InternalsVisibleTo Include="EsmAnalyzer" />` to `src/.../FalloutXbox360Utils.csproj` (line 39, next to existing Tests entry). All converter types are `internal` — this lets EsmAnalyzer see them without changing visibility.

#### Step 2: Backport diverged functionality (tools → src)

**EsmSubrecordConverter.Helpers.cs** — Add 5 methods from tools version:
- `ConvertEnit(byte[] data, string recordType)` — ENIT handler for ENCH/ALCH
- `ConvertImadSubrecord(string signature, byte[] data)` — IMAD routing
- `ConvertVtxt(byte[] data)` — Vertex texture blend conversion
- `ConvertUnknownSubrecord(byte[] data, string signature, string recordType)` — Fallback handler
- `SwapAllFloats(byte[] data)` — Float array byte-swapping utility

**EsmBinary.cs** — Add missing overloads from tools version:
- ReadInt16 (span+offset, byte[]+offset)
- ReadSingle (span+offset, byte[]+offset)
- ReadUInt64 (span+offset, byte[]+offset)
- ReadInt64 (span+offset, byte[]+offset)
- ReadDouble (span+offset, byte[]+offset)
- Span-first overloads (8 methods reading from offset 0)

**EsmInfoMerger.cs** — Add from tools:
- `ChoiceSignature` constant (declared but currently unused)
- `WriteSubrecordsToBuffer()` method (for future use)

**NOT backported** (tools-specific):
- EsmConversionStats `PrintStats()` — Spectre.Console display, src has UI-agnostic `GetStatsSummary()`
- EsmHelpers `CompareRecords()` — references `RecordComparison`/`SubrecordDiff` types that stay in EsmAnalyzer
- EsmHelpers `HexDump` Spectre escaping — tools-specific formatting concern

#### Step 3: Create tools-specific extraction files

Before deleting the tools duplicates, extract tools-specific functionality into new files:

**`tools/EsmAnalyzer/Helpers/RecordComparisonHelpers.cs`** (NEW):
- Extract `CompareRecords()` from EsmHelpers.cs
- It calls canonical `EsmHelpers.GetRecordData()`/`ParseSubrecords()` from src
- Returns `RecordComparison` (stays in `EsmAnalyzer.Helpers`)

**`tools/EsmAnalyzer/Helpers/ConversionStatsFormatter.cs`** (NEW):
- Extension method `PrintWithSpectre(this EsmConversionStats stats, bool verbose)`
- Migrates the Spectre.Console Table formatting from tools' EsmConversionStats.cs

#### Step 4: Delete 29 duplicate files

**Conversion/ (15 files)**: CellEntry, ConversionIndex, EsmConversionIndexBuilder, EsmConversionStats, EsmConverter, EsmEndianHelpers, EsmGrupWriter, EsmInfoMerger, EsmRecordCompression, EsmRecordWriter, EsmSubrecordConverter, EsmSubrecordConverter.Helpers, GrupEntry, PcCellOrderGenerator, WorldEntry

**Conversion/Schema/ (9 files)**: ParsedGrupHeader, ParsedRecordHeader, RecordHeaderProcessor, RecordHeaderSchema, SubrecordField, SubrecordFieldType, SubrecordSchema, SubrecordSchemaProcessor, SubrecordSchemaRegistry

**Core/ (4 files)**: AnalyzerRecordInfo, AnalyzerSubrecordInfo, EsmBinary, EsmRecordParser

**Helpers/ (1 file)**: EsmHelpers

#### Step 5: Update using statements

**GlobalUsings.cs** — Add:
```
global using FalloutXbox360Utils.Core.Converters.Esm;
global using FalloutXbox360Utils.Core.Converters.Esm.Schema;
```

**All files with `using EsmAnalyzer.Conversion`** (2 files): ConvertCommands.cs, ToftCommands.cs → remove

**All files with `using EsmAnalyzer.Conversion.Schema`** (8 files): DiffHelpers.cs, ConvertCommands.cs, DiffCommands.ThreeWay.cs, FormIdAuditCommands.cs, FieldValueDecoder.cs, RecordSchemaCommands.cs, SemanticDiffCommands.cs → remove (covered by global using)

**All `using static EsmAnalyzer.Conversion.EsmEndianHelpers`** (5 files — all in deleted Conversion/) → no action needed

**`EsmConstants.SubrecordHeaderSize`** references in remaining Core/ files → change to `EsmConverterConstants.SubrecordHeaderSize` or keep using `EsmConstants` (same value, still exists in `EsmAnalyzer.Core` for LAND constants)

#### Verification

```bash
dotnet build src/FalloutXbox360Utils -c Release -f net10.0
dotnet build tools/EsmAnalyzer -c Release

# Conversion regression test
dotnet run --project tools/EsmAnalyzer -c Release -- convert "Sample/ESM/360_final/FalloutNV.esm" -o TestOutput/FalloutNV.pc.esm
dotnet run --project tools/EsmAnalyzer -c Release -- semdiff TestOutput/FalloutNV.pc.esm "Sample/ESM/pc_final/FalloutNV.esm" -t NPC_ --limit 5
```

#### Files modified/created/deleted summary

| Action | Files |
|--------|-------|
| Modified (src) | FalloutXbox360Utils.csproj, EsmSubrecordConverter.Helpers.cs, EsmBinary.cs, EsmInfoMerger.cs |
| Created (tools) | RecordComparisonHelpers.cs, ConversionStatsFormatter.cs |
| Deleted (tools) | 29 files across Conversion/, Conversion/Schema/, Core/, Helpers/ |
| Modified (tools) | GlobalUsings.cs, ConvertCommands.cs, ToftCommands.cs, + ~6 command files (remove using statements) |

### Verification

```bash
# After Phase 0A + 0B:
dotnet build src/FalloutXbox360Utils -c Release
dotnet build tools/EsmAnalyzer -c Release
dotnet build tools/NifAnalyzer -c Release
dotnet test

# Run extraction on a test dump to verify no runtime regression
dotnet run --project src/FalloutXbox360Utils -- analyze "Sample/dumps/test.bin"
```
