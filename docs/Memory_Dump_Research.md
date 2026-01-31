# Xbox 360 Memory Dump Research

This is a living document tracking research into Xbox 360 memory dump structure, with a focus on Fallout: New Vegas.

## Available Resources

### PDB Symbol Files

Located in `Fallout New Vegas (July 21, 2010)/FalloutNV/`:

- `Fallout.pdb` - Debug build symbols
- `Fallout_Release_Beta.pdb` - Release beta symbols
- `Fallout_Release_MemDebug.pdb` - Release memory debug symbols

### Executables

- `Fallout.exe` / `default.xex` - Debug build
- `Fallout_Release_Beta.exe` / `Fallout_Release_Beta.xex` - Release beta
- `Fallout_Release_MemDebug.exe` / `Fallout_Release_MemDebug.xex` - Memory debug release

### PDB Tools

The `microsoft-pdb` submodule contains tools for PDB analysis:

- `cvdump` - Dump PDB contents
- `pdbdump` - PDB file dumper

### Extracted Symbols (in `tools/`)

- `pdb_types_full.txt` - Complete type definitions from debug PDB
- `pdb_publics.txt` - Public symbols
- `pdb_globals.txt` - Global symbols
- `script_function_constants.txt` - FUNCTION\_\* opcode constants
- `script_param_constants.txt` - SCRIPT*PARAM*\* type constants
- `script_block_constants.txt` - SCRIPT*BLOCK*\* type constants
- `opcode_table.json` - Generated opcode table (JSON format)
- `opcode_table.csv` - Generated opcode table (CSV format)

## Runtime TESForm Structures (PDB-Derived)

This section documents the empirically verified runtime object layouts for Fallout: New Vegas on Xbox 360. These were derived from the debug PDB (`Fallout.pdb`) type definitions and validated against live memory dumps. All multi-byte fields are **big-endian** on Xbox 360.

**Critical insight**: TESForm is **40 bytes** at runtime, not the 24 bytes declared in the PDB. The extra 16 bytes at offsets 24-39 are runtime-only fields absent from PDB type info. This means all PDB-derived field offsets for TESForm subclasses must be shifted **+16** from PDB values to get the actual runtime offset.

### TESForm Base Class (40 bytes at runtime)

| Offset | Size | Type      | Field         | Notes                            |
| ------ | ---- | --------- | ------------- | -------------------------------- |
| 0      | 4    | void\*    | vfptr         | Virtual function table pointer   |
| 4      | 4    | uint32    | m_uiRefCount  |                                  |
| 8      | 1    | uint8     | m_ucFormType  | See FormType enum below          |
| 9      | 3    | —         | padding       |                                  |
| 12     | 4    | uint32    | m_uiFormFlags |                                  |
| 16     | 4    | uint32    | m_uiFormID    |                                  |
| 20     | 4    | TESFile\* | m_kModIndex   | Pointer to owning module         |
| 24     | 4    | uint32    | (runtime)     | Always 0x00000000                |
| 28     | 4    | uint32    | (runtime)     | Flags/indices (e.g. 0x00CBCB14)  |
| 32     | 4    | void\*    | (runtime)     | Shared pointer (e.g. 0x40BFBA90) |
| 36     | 4    | uint32    | (runtime)     | Always 0x00000000                |

Offsets 24-39 are populated by the engine at load time and are not part of the PDB-defined TESForm layout. Their exact meaning is not fully understood, but they appear consistently across all dump files.

### BSStringT\<char\> (8 bytes)

Used by TESFullName.cFullName for display names and TESTopicInfo.cPrompt for dialogue lines.

| Offset | Size | Type   | Field   | Notes                                |
| ------ | ---- | ------ | ------- | ------------------------------------ |
| 0      | 4    | char\* | pString | BE pointer to null-terminated string |
| 4      | 2    | uint16 | sLen    | String length (BE)                   |
| 6      | 2    | uint16 | sMaxLen | Buffer capacity (BE)                 |

### EditorID Hash Table (BSTCaseInsensitiveStringMap / NiTMapBase)

The game maintains a global hash table mapping EditorID strings to TESForm pointers. This table is the primary source of EditorID-to-FormID mappings in memory dumps (48,619 entries in a typical dump vs ~700 from ESM EDID subrecords).

**NiTMapBase header (16 bytes):**

| Offset | Size | Type     | Field        | Notes                                      |
| ------ | ---- | -------- | ------------ | ------------------------------------------ |
| 0      | 4    | void\*   | vfptr        | Virtual function table pointer             |
| 4      | 4    | uint32   | m_uiHashSize | Non-power-of-2 (e.g. 131,213)              |
| 8      | 4    | void\*\* | m_ppkBuckets | Pointer to bucket array (hashSize entries) |
| 12     | 4    | uint32   | m_uiCount    | Total number of entries in table           |

**NiTMapItem (12 bytes per bucket chain entry):**

| Offset | Size | Type         | Field    | Notes                           |
| ------ | ---- | ------------ | -------- | ------------------------------- |
| 0      | 4    | NiTMapItem\* | m_pkNext | Next item in chain, or 0 (NULL) |
| 4      | 4    | const char\* | m_key    | Pointer to EditorID string      |
| 8      | 4    | TESForm\*    | m_val    | Pointer to TESForm object       |

Each bucket in the array is a 4-byte pointer to the first NiTMapItem in the chain (or 0 for empty buckets). The hash function is case-insensitive.

### TESFullName.cFullName Offsets by FormType

The `cFullName` field is a BSStringT\<char\> embedded in subclasses of TESForm at different offsets depending on the class hierarchy. The PDB offset is the value from PDB type info; the runtime offset is the actual offset in memory (+16 from PDB due to runtime-only TESForm fields).

| FormType | Hex  | Record | Class         | PDB Offset | Runtime Offset |
| -------- | ---- | ------ | ------------- | ---------- | -------------- |
| FACT     | 0x08 | FACT   | TESFaction    | 28         | 44             |
| RACE     | 0x0C | RACE   | TESRace       | 28         | 44             |
| ACTI     | 0x15 | ACTI   | TESObjectACTI | 52         | 68             |
| ARMO     | 0x18 | ARMO   | TESObjectARMO | 52         | 68             |
| BOOK     | 0x19 | BOOK   | TESObjectBOOK | 52         | 68             |
| CONT     | 0x1B | CONT   | TESObjectCONT | 64         | 80             |
| DOOR     | 0x1C | DOOR   | TESObjectDOOR | 52         | 68             |
| MISC     | 0x1F | MISC   | TESObjectMISC | 52         | 68             |
| WEAP     | 0x28 | WEAP   | TESObjectWEAP | 52         | 68             |
| AMMO     | 0x29 | AMMO   | TESAmmo       | 52         | 68             |
| NPC\_    | 0x2A | NPC\_  | TESNPC        | 212        | 228            |
| KEYM     | 0x2E | KEYM   | TESKey        | 52         | 68             |
| ALCH     | 0x2F | ALCH   | AlchemyItem   | 52         | 68             |

Most item-like objects (ACTI, ARMO, BOOK, DOOR, MISC, WEAP, AMMO, KEYM, ALCH) share offset 68 because they inherit TESFullName through the same chain: `TESBoundObject` → `TESFullName`. CONT has an extra intermediate class (offset 80). NPC\_ has a deep hierarchy (offset 228). FACT/RACE inherit TESFullName early (offset 44).

### TESTopicInfo Dialogue Line Offset (FormType 0x46)

| Field   | PDB Offset | Runtime Offset | Notes                                                 |
| ------- | ---------- | -------------- | ----------------------------------------------------- |
| cPrompt | 40         | 44             | Only +4 shift (not +16) — different base class layout |

INFO records show a smaller offset shift than other TESForm subclasses. This may indicate that TESTopicInfo does not inherit the same 16 runtime-only bytes, or has a different virtual table arrangement.

### FormType Enum (Runtime Byte Values)

These are the `m_ucFormType` byte values at TESForm offset 8, as observed in memory dumps:

```
0x04=TXST  0x06=GLOB  0x07=CLAS  0x08=FACT  0x09=HDPT
0x0A=HAIR  0x0B=EYES  0x0C=RACE  0x0D=SOUN  0x0E=ASPC
0x10=MGEF  0x11=SCPT  0x12=LTEX  0x13=ENCH  0x14=SPEL
0x15=ACTI  0x16=TACT  0x17=TERM  0x18=ARMO  0x19=BOOK
0x1A=CLOT  0x1B=CONT  0x1C=DOOR  0x1D=INGR  0x1E=LIGH
0x1F=MISC  0x20=STAT  0x24=GRAS  0x25=TREE  0x26=FLOR
0x27=FURN  0x28=WEAP  0x29=AMMO  0x2A=NPC_  0x2B=CREA
0x2E=KEYM  0x2F=ALCH  0x31=NOTE  0x33=PROJ  0x39=CELL
0x3A=REFR  0x44=WRLD  0x45=LAND  0x46=INFO  0x47=QUST
0x48=IDLE  0x49=PACK  0x4A=CSTY  0x4B=LSCR  0x4C=ANIO
0x4D=WATR  0x4E=EFSH  0x51=EXPL  0x52=DEBR  0x53=IMGS
0x54=IMAD  0x55=FLST  0x56=PERK  0x58=AVIF  0x5B=MESG
0x5E=CAMS  0x62=CHIP  0x63=CSNO  0x64=LSCT  0x65=MSET
0x66=RCPE  0x67=RCCT  0x69=REPU  0x6A=HUNG  0x6B=SLPD
0x6C=CHAL  0x6D=AMEF  0x6E=CCRD  0x6F=CMNY  0x70=CDCK
0x71=DEHY  0x72=IMOD
```

Note: Not all FormType values are contiguous — gaps exist (e.g. 0x21-0x23, 0x2C-0x2D, 0x34-0x38). These gaps correspond to record types removed or never implemented in Fallout: New Vegas.

### FormType Enum Build Variance

The `m_ucFormType` values are **not stable** across game builds. The enum shifted between development builds, with values above 0x45 (LAND) diverging by +1 in the baseline build:

| Type      | Baseline (Release Beta) | Other Builds | Notes                        |
| --------- | ----------------------- | ------------ | ---------------------------- |
| GLOB–ALCH | 0x06–0x2F               | 0x06–0x2F    | **Stable** across all builds |
| CELL      | 0x39                    | 0x39         | Stable                       |
| REFR      | 0x3A                    | 0x3A         | Stable                       |
| WRLD      | 0x44                    | 0x44         | Stable                       |
| LAND      | 0x45                    | 0x45         | Stable                       |
| **INFO**  | **0x46**                | **0x45**     | Baseline +1 vs others        |
| **QUST**  | **0x48**                | **0x47**     | Baseline +1 vs others        |
| **FLST**  | **0x56**                | **0x55**     | Baseline +1 vs others        |
| **PERK**  | **0x57**                | **0x56**     | Baseline +1 vs others        |

**Affected builds:**

- `Fallout_Release_Beta.xex.dmp` (baseline): Higher values above INFO (INFO=0x46)
- `Fallout_Debug.xex.dmp`, `Fallout_Release_MemDebug.xex.dmp`, `*.xex19.dmp`, `*.xex43.dmp`: Standard values (INFO=0x45)

**Root cause:** An extra enum entry was likely inserted or removed between LAND (0x45) and INFO in one build, shifting all subsequent values by +1.

**Impact:** Any code using hardcoded FormType values above 0x45 will produce incorrect results on different builds. The codebase now uses auto-detection: `DetectInfoFormType()` calibrates the INFO FormType from EditorID patterns ("Topic" substring in EditorIDs), and `FullNameOffsetByFormType` only uses stable values (0x06–0x2F range).

The FormType enum values listed above in the main table are from the baseline build (`Fallout_Release_Beta.xex.dmp`).

### BSSimpleList / NiTListItem (Linked List Infrastructure)

The game engine uses `BSSimpleList<T>` for dynamic collections (inventory, factions, spells, etc.). These are doubly-linked lists stored within runtime objects.

**BSSimpleList structure (12 bytes):**

| Offset | Size | Type          | Field  | Notes                    |
| ------ | ---- | ------------- | ------ | ------------------------ |
| 0      | 4    | void\*        | vfptr  | Virtual function table   |
| 4      | 4    | NiTListItem\* | pHead  | Pointer to first item    |
| 8      | 4    | uint32/void   | (data) | Count or additional data |

**NiTListItem structure (12+ bytes):**

| Offset | Size | Type          | Field | Notes                         |
| ------ | ---- | ------------- | ----- | ----------------------------- |
| 0      | 4    | NiTListItem\* | pNext | Next item (NULL = end)        |
| 4      | 4    | NiTListItem\* | pPrev | Previous item                 |
| 8      | N    | T             | data  | Embedded data or pointer to T |

### ContainerObject (8 bytes)

Used in TESContainer's BSSimpleList for inventory items.

| Offset | Size | Type      | Field  | Notes                   |
| ------ | ---- | --------- | ------ | ----------------------- |
| 0      | 4    | int32     | iCount | Item count (BE)         |
| 4      | 4    | TESForm\* | pItem  | Pointer to item TESForm |

### TESNPC (508 bytes at runtime, PDB 492)

The NPC struct is complex with many inherited base classes. Key offsets (empirically verified):

| Offset | Size | Type               | Field           | Notes                                          |
| ------ | ---- | ------------------ | --------------- | ---------------------------------------------- |
| 0-63   | 64   | TESBoundAnimObject | (base)          | +16 vs PDB (48 in PDB, 64 at runtime)          |
| 68     | 24   | TESActorBaseData   | actorData       | ACBS stats (level, health, etc.)               |
| 92     | 4    | TESLevItem\*       | pDeathItem      | Pointer                                        |
| 96     | 4    | BGSVoiceType\*     | pVoiceType      | Pointer                                        |
| 100    | 4    | TESForm\*          | pTemplateForm   | Pointer                                        |
| 116    | 12   | TESContainer       | container       | BSSimpleList of ContainerObject\* (inventory)  |
| 128    | 12   | TESSpellList       | spellList       | BSSimpleList of TESForm\* (spells)             |
| 160    | 16   | TESAIForm          | aiForm          | AI data                                        |
| 164    | 4    | uint8[4]           | AIData          | aggression, confidence, energy, responsibility |
| 172    | 4    | uint32             | buySellServices | AI flags (BE)                                  |
| 178    | 1    | uint8              | assistance      | AI assistance value                            |
| 204    | 7    | uint8[7]           | SPECIAL         | ST, PE, EN, CH, IN, AG, LK                     |
| 224    | 4    | void\*             | TESFullName vt  | vtable pointer                                 |
| 228    | 8    | BSStringT\<char\>  | cFullName       | Display name (empirically verified)            |
| 288    | 4    | TESRace\*          | pRace           | Pointer to race                                |
| 292    | 14   | uint8[14]          | skills          | 14 skill values                                |
| 320    | 4    | TESClass\*         | pClass          | Pointer to class                               |
| 336    | 32   | FaceGen struct     | FGGS            | ptr + count (50 floats)                        |
| 368    | 32   | FaceGen struct     | FGGA            | ptr + count (30 floats)                        |
| 400    | 32   | FaceGen struct     | FGTS            | ptr + count (50 floats)                        |
| 456    | 4    | TESHair\*          | pHair           | Pointer to hair                                |
| 460    | 4    | float              | fHairLength     | Hair length (0.0-1.0)                          |
| 464    | 4    | TESEyes\*          | pEyes           | Pointer to eyes                                |
| 484    | 4    | TESCombatStyle\*   | pCombatStyle    | Pointer to combat style (FormType=74)          |

### TESObjectCONT (172 bytes at runtime, PDB 156)

Container object with inventory stored as BSSimpleList.

| Offset | Size | Type               | Field     | Notes                             |
| ------ | ---- | ------------------ | --------- | --------------------------------- |
| 0-63   | 64   | TESBoundAnimObject | (base)    | +16 vs PDB                        |
| 68     | 12   | TESFullName        | fullName  | Display name at +80               |
| 80     | 8    | BSStringT\<char\>  | cFullName | Name string                       |
| 88     | 24   | TESModel           | model     | Model path                        |
| 112    | 8    | TESWeightForm      | weight    |                                   |
| 120    | 12   | TESScriptableForm  | script    |                                   |
| 132    | 12   | TESContainer       | container | BSSimpleList of ContainerObject\* |
| 144    | 4    | uint8              | flags     | Container flags                   |

### BGSTerminal (184 bytes at runtime, PDB 168)

Terminal with menu items stored as BSSimpleList.

| Offset | Size | Type              | Field      | Notes                              |
| ------ | ---- | ----------------- | ---------- | ---------------------------------- |
| 68     | 8    | BSStringT\<char\> | headerText | Terminal header text               |
| 76     | 1    | uint8             | difficulty | Difficulty (0-3: Very Easy-Hard)   |
| ~80    | 12   | BSSimpleList      | menuItems  | BSSimpleList of MenuItemData (120) |

**MenuItemData (120 bytes):**

| Offset | Size | Type              | Field        | Notes                    |
| ------ | ---- | ----------------- | ------------ | ------------------------ |
| 0      | 8    | BSStringT\<char\> | menuText     | Menu item label          |
| 8      | 8    | BSStringT\<char\> | resultText   | Text shown when selected |
| 16     | 4    | BGSTerminal\*     | pSubTerminal | Pointer to sub-terminal  |

### TESLeveledList (embedded in leveled list types)

Located at offset 0x30 (dump offset +64 with runtime shift) in TESLevItem, TESLevCreature, TESLevCharacter.

| Offset | Size | Type                           | Field         | Notes                      |
| ------ | ---- | ------------------------------ | ------------- | -------------------------- |
| 0x00   | 4    | void\*                         | vfptr         | vtable pointer             |
| 0x04   | 8    | BSSimpleList<LEVELED_OBJECT\*> | leveledList   | Entries linked list        |
| 0x0C   | 1    | uint8                          | cChanceNone   | % chance of no spawn       |
| 0x0D   | 1    | uint8                          | cLLFlags      | Flags                      |
| 0x10   | 4    | TESGlobal\*                    | pChanceGlobal | Global variable for chance |

**LEVELED_OBJECT (runtime, 8 bytes):**

| Offset | Size | Type      | Field  | Notes                  |
| ------ | ---- | --------- | ------ | ---------------------- |
| 0x00   | 2    | uint16    | sLevel | Level requirement (BE) |
| 0x02   | 2    | uint16    | sCount | Spawn count (BE)       |
| 0x04   | 4    | TESForm\* | pForm  | Pointer to item/NPC    |

**LEVELED_OBJECT_FILE (ESM LVLO subrecord, 12 bytes):**

| Offset | Size | Type   | Field   | Notes                  |
| ------ | ---- | ------ | ------- | ---------------------- |
| 0x00   | 2    | uint16 | sLevel  | Level requirement (BE) |
| 0x02   | 2    | uint16 | (pad)   | Padding                |
| 0x04   | 4    | uint32 | iFormID | Form ID (BE)           |
| 0x08   | 2    | uint16 | sCount  | Spawn count (BE)       |
| 0x0A   | 2    | uint16 | (pad)   | Padding                |

### MapMarkerData (20 bytes)

Data for map markers attached to REFR records.

| Offset | Size | Type              | Field          | Notes                           |
| ------ | ---- | ----------------- | -------------- | ------------------------------- |
| 0      | 12   | BSStringT\<char\> | LocationName   | Location name string            |
| 12     | 1    | uint8             | cFlags         | Marker flags                    |
| 13     | 1    | uint8             | cOriginalFlags | Original flags                  |
| 14     | 2    | uint16            | sType          | MARKER_TYPE enum (0-14)         |
| 16     | 4    | TESForm\*         | pReputation    | Associated reputation (pointer) |

**MARKER_TYPE Enum:**

| Value | Name             | Description           |
| ----- | ---------------- | --------------------- |
| 0     | NONE             | No marker             |
| 1     | CITY             | City                  |
| 2     | SETTLEMENT       | Settlement            |
| 3     | ENCAMPMENT       | Encampment            |
| 4     | NATURAL_LANDMARK | Natural landmark      |
| 5     | CAVE             | Cave                  |
| 6     | FACTORY          | Factory               |
| 7     | MONUMENT         | Monument              |
| 8     | MILITARY         | Military installation |
| 9     | OFFICE           | Office building       |
| 10    | RUINS_TOWN       | Town ruins            |
| 11    | RUINS_URBAN      | Urban ruins           |
| 12    | RUINS_SEWER      | Sewer/Underground     |
| 13    | METRO            | Metro station         |
| 14    | VAULT            | Vault                 |

### LoadedLandData (164 bytes / 0xA4)

Pointed to by TESObjectLAND's pLoadedData field at runtime offset +56. Contains heightmap cell coordinates for stitching.

| Offset | Size | Type                | Field             | Notes                       |
| ------ | ---- | ------------------- | ----------------- | --------------------------- |
| 0      | 4    | NiNode\*\*          | ppMesh            | Mesh pointer                |
| 4      | 4    | NiPoint3\*\*        | ppVertices        | Vertices pointer            |
| 8      | 4    | NiPoint3\*\*        | ppNormals         | Normals pointer             |
| 12     | 4    | void\*              | ppColorsA         | Colors pointer              |
| 16     | 16   | bool[16]            | ppNormalsSet      | Normals set flags           |
| 20     | 4    | NiLinesPtr          | spBorder          | Border lines                |
| 24     | 8    | NiPoint2            | HeightExtents     | {min, max}                  |
| 32     | 16   | TESLandTexture\*[4] | pDefQuadTexture   | Default quad textures       |
| 48     | 16   | Textures\*[4]       | pQuadTextureArray | Quad texture arrays         |
| 64     | 16   | float\*\*[4]        | ppPercentArrays   | Percent arrays              |
| 80     | 4    | void\*              | pMoppCode         | MOPP collision code         |
| 84     | 64   | GrassMap[64]        | pmGrassMap        | Grass map                   |
| 148    | 4    | void\*              | spLandRB          | Land rigid body             |
| 152    | 4    | int32               | iCellX            | **Cell X coordinate** (key) |
| 156    | 4    | int32               | iCellY            | **Cell Y coordinate** (key) |
| 160    | 4    | float32             | fBaseHeight       | **Base height** (key)       |

### Additional Runtime Struct Sizes

Summary of verified PDB vs runtime sizes (+16 due to runtime-only TESForm fields):

| Record | C++ Class       | PDB Size | Runtime Size |
| ------ | --------------- | -------- | ------------ |
| NPC\_  | TESNPC          | 492      | 508          |
| WEAP   | TESObjectWEAP   | 908      | 924          |
| ARMO   | TESObjectARMO   | 400      | 416          |
| AMMO   | TESAmmo         | 220      | 236          |
| ALCH   | AlchemyItem     | 216      | 232          |
| MISC   | TESObjectMISC   | 172      | 188          |
| KEYM   | TESKey          | 172      | 188          |
| CONT   | TESObjectCONT   | 156      | 172          |
| TERM   | BGSTerminal     | 168      | 184          |
| QUST   | TESQuest        | 108      | 124          |
| NOTE   | BGSNote         | 128      | 144          |
| FACT   | TESFaction      | 76       | 92           |
| LVLI   | TESLevItem      | 76       | 92           |
| LVLC   | TESLevCreature  | 112      | 128          |
| LVLN   | TESLevCharacter | 112      | 128          |
| LAND   | TESObjectLAND   | 44       | 60           |

### TESNPC Detailed Field Map (Empirically Verified)

The TESNPC struct has been extensively mapped through PDB analysis and cross-NPC hex dump comparison. All offsets below are runtime offsets (PDB + 16).

**AI Data (TESAIForm at +160):**

| Offset | Size | Type   | Field          | Notes                                             |
| ------ | ---- | ------ | -------------- | ------------------------------------------------- |
| 164    | 1    | uint8  | aggression     | 0=Unaggressive, 1=Aggressive, 2=Very, 3=Frenzied  |
| 165    | 1    | uint8  | confidence     | 0=Cowardly, 1=Cautious, 2=Average, 3=Brave, 4=Foolhardy |
| 166    | 1    | uint8  | energyLevel    | Energy level (0-100)                               |
| 167    | 1    | uint8  | responsibility | 0=Any crime, 1=Violence, 2=Property, 3=No crime   |
| 168    | 1    | uint8  | mood           | 0=Neutral, 1=Afraid, 2=Annoyed, 3=Cocky, 4=Drugged, 5=Pleasant, 6=Angry, 7=Sad |
| 172    | 4    | uint32 | buySellAndServices | AI flags (BE)                                  |
| 178    | 1    | uint8  | assistance     | 0=Nobody, 1=Allies, 2=Friends and Allies          |

**SPECIAL Stats (TESAttributes at +204):**

| Offset | Size | Type     | Field   | Notes                              |
| ------ | ---- | -------- | ------- | ---------------------------------- |
| 204    | 1    | uint8    | ST      | Strength                           |
| 205    | 1    | uint8    | PE      | Perception                         |
| 206    | 1    | uint8    | EN      | Endurance                          |
| 207    | 1    | uint8    | CH      | Charisma                           |
| 208    | 1    | uint8    | IN      | Intelligence                       |
| 209    | 1    | uint8    | AG      | Agility                            |
| 210    | 1    | uint8    | LK      | Luck                               |

Verified against GECK values for GSSunnySmiles (6,5,4,4,4,6,4) and CraigBoone (6,8,5,3,3,6,5).

**Skills (TESNPCData at +292):**

| Offset | Size | Type      | Field | Notes                              |
| ------ | ---- | --------- | ----- | ---------------------------------- |
| 292    | 14   | uint8[14] | skills | 14 skill slots: Barter, BigGuns(unused), EnergyWeapons, Explosives, Lockpick, Medicine, MeleeWeapons, Repair, Science, Guns, Sneak, Speech, Survival, Unarmed |

**Derived Stats (computed from SPECIAL + Level):**

| Stat              | Formula                    | Sunny (Level 2, EN=4, ST=6, LK=4) |
| ----------------- | -------------------------- | ---------------------------------- |
| Base Health       | END × 5 + 50               | 70                                 |
| Calculated Health | BaseHP + Level × 10        | 90                                 |
| Calculated Fatigue| Fatigue + (STR+END) × 10   | 150                                |
| Critical Chance   | Luck                        | 4.0                                |
| Melee Damage      | STR × 0.5                  | 3.0                                |
| Unarmed Damage    | 0.5 + STR × 0.1            | 1.1                                |
| Poison Resistance | (END - 1) × 5              | 15.0                               |
| Rad Resistance    | (END - 1) × 2              | 6.0                                |

**FaceGen Morph Pointers (+336, +368, +400):**

| Offset | Size | Type     | Field | Notes                               |
| ------ | ---- | -------- | ----- | ----------------------------------- |
| 336    | 4    | float\*  | pFGGS | Pointer to 50 Geometry-Symmetric floats |
| 340    | 4    | uint32   | nFGGS | Count (typically 50)                |
| 368    | 4    | float\*  | pFGGA | Pointer to 30 Geometry-Asymmetric floats |
| 372    | 4    | uint32   | nFGGA | Count (typically 30)                |
| 400    | 4    | float\*  | pFGTS | Pointer to 50 Texture-Symmetric floats |
| 404    | 4    | uint32   | nFGTS | Count (typically 50)                |

**Note on pointer targets**: These pointers contain Xbox 360 virtual addresses (0x66xxxxxx range = module/code space). Converting VA to file offset requires the minidump MEMORY64_LIST region table. The pointed-to float arrays contain FaceGen morph coefficients in big-endian format.

**Height/Weight R&D result**: PDB predicts Height at +484 and Weight at +488. Exhaustive scanning of the full 508-byte NPC struct found neither Sunny's height (0.95) nor any consistent height-like float across multiple NPCs. These values may be stored in a secondary structure or computed at runtime. **Not extractable from the NPC struct.**

### Item Struct Layouts (Empirically Verified)

All item types share the base chain: TESBoundObject (64 bytes) → TESFullName (at offset 68). The `ReadBSStringT()` helper reads display names via the hash table's pre-extracted `DisplayName`. Model paths use TESModel.cModel BSStringT at offset +80.

**TESObjectARMO (416 bytes at runtime, PDB 400):**

| Offset | Size | Type    | Field       | Notes                              |
| ------ | ---- | ------- | ----------- | ---------------------------------- |
| 68     | 8    | BSStringT | cFullName | Display name                        |
| 108    | 4    | int32   | iValue      | Gold value (BE)                     |
| 116    | 4    | float   | fWeight     | Weight (BE float)                   |
| 124    | 4    | int32   | iHealth     | Armor health/condition (BE)         |
| 392    | 2    | uint16  | armorRating | Armor rating × 100 (BE)            |

**TESAmmo (236 bytes at runtime, PDB 220):**

| Offset | Size | Type    | Field     | Notes                     |
| ------ | ---- | ------- | --------- | ------------------------- |
| 68     | 8    | BSStringT | cFullName | Display name            |
| 80     | 8    | BSStringT | cModel  | World model path          |
| 140    | 4    | int32   | iValue    | Gold value (BE)           |

**AlchemyItem (232 bytes at runtime, PDB 216):**

| Offset | Size | Type    | Field   | Notes                               |
| ------ | ---- | ------- | ------- | ----------------------------------- |
| 68     | 8    | BSStringT | cFullName | Display name                    |
| 168    | 4    | float   | fWeight | Weight (BE float)                    |
| 200    | 4    | int32   | iValue  | Gold value (BE, direct member not TESValueForm) |

**TESObjectMISC / TESKey (188 bytes at runtime, PDB 172):**

| Offset | Size | Type    | Field     | Notes              |
| ------ | ---- | ------- | --------- | ------------------ |
| 68     | 8    | BSStringT | cFullName | Display name     |
| 80     | 8    | BSStringT | cModel  | World model path   |
| 136    | 4    | int32   | iValue    | Gold value (BE)    |
| 144    | 4    | float   | fWeight   | Weight (BE float)  |

### TESCreature (Runtime, Empirical)

| Offset | Size | Type     | Field           | Notes                    |
| ------ | ---- | -------- | --------------- | ------------------------ |
| 68     | 8    | BSStringT | cFullName      | Display name              |
| 80     | 8    | BSStringT | cModel         | World model path          |
| 128    | 1    | uint8    | creatureType    | 0=Animal, 1=MutatedAnimal, 2=MutatedInsect, 3=Abomination, 4=SuperMutant, 5=FeralGhoul, 6=Robot, 7=Giant |
| 152    | 2    | uint16   | attackDamage    | Base attack damage (BE)   |
| 156    | 1    | uint8    | combatSkill     | Combat skill (0-100)      |
| 157    | 1    | uint8    | magicSkill      | Magic skill (0-100)       |
| 158    | 1    | uint8    | stealthSkill    | Stealth skill (0-100)     |

### BGSNote (144 bytes at runtime, PDB 128)

| Offset | Size | Type     | Field    | Notes                    |
| ------ | ---- | -------- | -------- | ------------------------ |
| 76     | 8    | BSStringT | cFullName | Display name (via TESModel → TESFullName) |
| 108    | 1    | uint8    | noteType | 0=Sound, 1=Text, 2=Image, 3=Voice |
| 112    | 8    | BSStringT | noteText | Note text content        |

### TESFaction (92 bytes at runtime, PDB 76)

| Offset | Size | Type   | Field  | Notes                              |
| ------ | ---- | ------ | ------ | ---------------------------------- |
| 44     | 8    | BSStringT | cFullName | Faction display name          |
| 68     | 4    | uint32 | flags  | Faction flags (BE)                  |
| 72     | 1    | uint8  | isHidden | Hidden faction flag               |

### TESQuest (124 bytes at runtime, PDB 108)

| Offset | Size | Type   | Field    | Notes                    |
| ------ | ---- | ------ | -------- | ------------------------ |
| 76     | 1    | uint8  | flags    | Quest flags               |
| 77     | 1    | uint8  | priority | Quest priority (0-255)    |
| 80     | 4    | uint32 | scriptFormId | Quest script FormID (BE) |

---

## Data Extraction Architecture

### Two-Track Approach

Memory dumps contain game data in two forms:

1. **Raw ESM records** — Fragment of the original ESM file data still in memory. The game loads ESM data, creates runtime C++ objects, and often frees the raw buffers. Only a fraction of records persist.

2. **Runtime C++ objects** — Live game objects allocated on the heap. The `AllFormsByEditorID` hash table provides access to ~48,000+ of these objects with their exact file offsets.

The extraction pipeline uses both sources:

```
Memory Dump → ESM Scanner (raw record signatures)    → SemanticReconstructor → CSV/Reports
           ↘ Hash Table Walker (EditorID → TESForm*) ↗
             → RuntimeStructReader (PDB-derived offsets)
```

For each record type, data from both sources is merged by FormID, with runtime struct data filling in any gaps.

### Coverage by Type

| Source | Best For | Coverage | Data Richness |
| ------ | -------- | -------- | ------------- |
| ESM scanning | CELL, LAND, INFO, REFR, CONT | Good for types that persist as raw records | Full subrecord detail |
| Runtime structs | NPC\_, WEAP, ARMO, AMMO, ALCH, MISC, KEYM, CREA, NOTE, FACT, QUST, TERM | Comprehensive (~48K objects) | PDB-defined fields |

### Typical Extraction Results (xex3 dump, 200 MB)

| Type       | ESM Records | Runtime Structs | Total  |
| ---------- | ----------- | --------------- | ------ |
| NPC\_      | 7           | 2,769           | 2,776  |
| Creatures  | 0           | 802             | 802    |
| Factions   | 0           | 424             | 424    |
| Quests     | 0           | 164             | 164    |
| Notes      | 0           | 668             | 668    |
| Terminals  | 0           | 276             | 276    |
| Containers | 0           | 718             | 718    |
| Weapons    | 34          | 302             | 336    |
| Armor      | 0           | 453             | 453    |
| Ammo       | 0           | 45              | 45     |
| Consumables| 0           | 116             | 116    |
| Misc Items | 0           | 302             | 302    |
| Keys       | 0           | 207             | 207    |
| Dialogue   | 642         | 0               | 642    |
| Dial Topics| 17          | 0               | 17     |
| Cells      | 6           | 0               | 6      |
| **Total**  |             |                 | **8,537** |

---

### Source Cross-References

- **PDB field offsets**: `tools/MinidumpAnalyzer/read_tesform_fields.py`
- **Runtime offset table (C#)**: `src/.../EsmRecord/EsmRecordFormat.cs` — `FullNameOffsetByFormType` dictionary
- **Hash table detection (C#)**: `src/.../EsmRecord/EsmRecordFormat.cs` — `ExtractFromHashTableCandidate()`
- **Hash table detection (Python)**: `tools/MinidumpAnalyzer/find_editorid_table.py`, `tools/MinidumpAnalyzer/search_hash_tables.py`
- **Runtime struct reader (C#)**: `src/.../EsmRecord/RuntimeStructReader.cs` — All `ReadRuntime*()` methods with empirically verified offsets
- **Semantic reconstruction (C#)**: `src/.../EsmRecord/SemanticReconstructor.cs` — Two-track ESM + runtime merging
- **Report generation (C#)**: `src/.../EsmRecord/Export/GeckReportGenerator.cs`, `src/.../EsmRecord/Export/CsvReportGenerator.cs`
- **FaceGen controls (C#)**: `src/.../EsmRecord/FaceGenControls.cs` — Auto-generated CTL coefficient data for morph slider names
- **Models (C#)**: `src/.../EsmRecord/Models/` — All `Reconstructed*` data transfer objects
- **Research plan document**: `docs/steady-wibbling-milner.md` — Complete PDB research and implementation plan
- **NPC struct scan (Python)**: `tools/MinidumpAnalyzer/npc_struct_scan.py` — Multi-NPC field comparison R&D
- **FaceGen distribution analysis (Python)**: `tools/MinidumpAnalyzer/ctl_distribution_scaling.py` — CTL transform R&D (inconclusive)
