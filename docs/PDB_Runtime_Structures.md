# PDB Runtime Structure Reference

Comprehensive documentation of all C++ runtime structures identified from the Fallout: New Vegas Xbox 360 PDB symbols and implemented in the memory dump analyzer.

**PDB Sources:**

- **Proto Debug PDB:** `Sample/PDB/Fallout_Debug/types_full.txt` (Jul 2010, TESForm = 24 bytes) — used for base object types (NPC, WEAP, etc.)
- **Final Debug PDB:** `Sample/PDB/Final/Fallout_Debug_Final/types_full.txt` (TESForm = 40 bytes) — used for TESObjectREFR and related types
  **Key Files:** `RuntimeStructReader.cs`, `RecordParser.cs`, `Models/`

---

## Dump Shift Patterns

Xbox 360 crash dumps exhibit consistent offset shifts between PDB-defined offsets and actual dump offsets:

| Struct Category                                | PDB Size | Dump Size | Shift   | Cause                                                                           |
| ---------------------------------------------- | -------- | --------- | ------- | ------------------------------------------------------------------------------- |
| TESBoundObject-derived (NPC, WEAP, ARMO, etc.) | varies   | +16       | **+16** | Extra vtable/debug data at struct start                                         |
| TESTopicInfo (INFO, Release Beta/Final layout) | 96       | 96        | **0**   | Current runtime INFO reader uses the direct 96-byte Release Beta / Final layout |
| TESTopic (DIAL)                                | 72       | 88        | **+16** | Same as TESBoundObject pattern                                                  |
| TESForm base                                   | 24       | 24        | **0**   | No shift on base class                                                          |
| TESObjectREFR (Final PDB)                      | 120      | 120       | **0**   | Final PDB offsets = dump offsets directly                                       |

### VA-to-Offset Conversion

Virtual addresses (VA) from runtime pointers are converted to dump file offsets via `MinidumpInfo.VirtualAddressToFileOffset()`, which walks the minidump's `MINIDUMP_MEMORY_DESCRIPTOR` list to find the memory region containing the VA and computes the file offset within that region.

**FormID Extraction Rule:** For any followed pointer, the FormID lives at `fileOffset + 12` (the `iFormID` field of TESForm).

---

## Base Classes

### TESForm

The root class for all game objects. Every form has a type, flags, and unique FormID.

| PDB Offset | Dump Offset | Type   | Field        | Description                                             |
| ---------- | ----------- | ------ | ------------ | ------------------------------------------------------- |
| 0          | 0           | ptr    | vfptr        | Virtual function table pointer                          |
| 4          | 4           | uint8  | cFormType    | Form type identifier (e.g., 0x2A=NPC\_, 0x45=DIAL/INFO) |
| 8          | 8           | uint32 | iFormFlags   | Form flags bitmask                                      |
| 12         | 12          | uint32 | iFormID      | Unique FormID                                           |
| 16         | 16          | ptr    | pSourceFiles | Pointer to source ESM file list                         |

**PDB Type:** `0xC73D` — Size: 24 bytes — 78 virtual methods
**Verification:** FormID at +12 empirically verified across all struct types.

### TESBoundObject

Extends TESForm with 3D bounding box data. Base class for most placeable objects.

| PDB Offset | Dump Offset | Type            | Field     | Description               |
| ---------- | ----------- | --------------- | --------- | ------------------------- |
| 0-23       | 0-23        | —               | (TESForm) | Inherited base class      |
| 36         | 36          | BOUND_DATA (12) | BoundData | Min/max bounds (6× int16) |

**PDB Type:** `0x1696F` — Size: 48 bytes
**Note:** The +16 shift for derived types means fields defined in derived classes start 16 bytes later in the dump than the PDB offset suggests. TESForm fields at 0-23 are NOT shifted.

### BSStringT\<char\>

The engine's standard string type. Stores a pointer to character data and length/capacity info.

| Offset | Type   | Field  | Description                           |
| ------ | ------ | ------ | ------------------------------------- |
| 0      | ptr    | data   | Pointer to null-terminated char array |
| 4      | uint32 | length | String length / capacity (packed)     |

**PDB Type:** `0x13958` — Size: 8 bytes
**Reading:** Follow the data pointer (VA→offset), read null-terminated string.

### BSSimpleList\<T\>

Singly-linked list. Used for faction lists, spell lists, topic lists, etc.

| Offset | Type | Field     | Description                    |
| ------ | ---- | --------- | ------------------------------ |
| 0      | ptr  | head.data | Data of first node (or null)   |
| 4      | ptr  | head.next | Pointer to next node (or null) |

**Each subsequent node:**

| Offset | Type | Field | Description                     |
| ------ | ---- | ----- | ------------------------------- |
| 0      | T    | data  | Node data (typically a pointer) |
| 4      | ptr  | next  | Pointer to next node            |

**PDB Size:** 8 bytes
**Safety:** `RuntimeStructReader` limits traversal to 50 nodes (`MaxListItems`).

### BSSimpleArray\<T, MaxSize\>

Fixed-capacity array with dynamic count.

| Offset | Type   | Field      | Description                     |
| ------ | ------ | ---------- | ------------------------------- |
| 0      | ptr    | pBuffer    | Pointer to contiguous T[] array |
| 4      | uint32 | capacity   | Maximum number of elements      |
| 8      | uint32 | count      | Current number of elements      |
| 12     | uint32 | (reserved) | Padding / alignment             |

**PDB Size:** 16 bytes
**Notable instantiation:** `BSSimpleArray<INFO_LINK_ELEMENT, 1024>` — used in QUEST_INFO for INFO record linking.

### NiTLargeArray\<T, Allocator\>

Dynamic array with allocator interface. Used for large collections.

| Offset | Type   | Field     | Description                  |
| ------ | ------ | --------- | ---------------------------- |
| 0      | ptr    | pData     | Pointer to T[] array         |
| 4      | uint32 | maxSize   | Capacity                     |
| 8      | uint32 | count     | Used count                   |
| 12     | uint32 | growBy    | Growth factor                |
| 16-23  | —      | allocator | Allocator interface instance |

**PDB Size:** 24 bytes
**Notable instantiation:** `NiTLargeArray<TESTopicInfo*, NiTMallocInterface<TESTopicInfo*>>` — used in QUEST_INFO.infoArray.

---

## Character Structures

### TESNPC (NPC\_ — FormType 0x2A)

The primary non-player character definition. Contains appearance, stats, AI, inventory, and faction data.

| PDB Offset | Dump Offset | Type        | Field            | Description                          |
| ---------- | ----------- | ----------- | ---------------- | ------------------------------------ |
| 12         | 12          | uint32      | iFormID          | NPC FormID                           |
| 52         | 68          | ACBS (24)   | actorBaseStats   | Actor base stats block               |
| 76         | 92          | ptr         | pDeathItem       | Death item (TESLeveledList\*)        |
| 80         | 96          | ptr         | pVoiceType       | Voice type (BGSVoiceType\*)          |
| 84         | 100         | ptr         | pTemplate        | Template NPC (TESNPC\*)              |
| 96         | 112         | ptr         | factionList.head | First faction membership             |
| 104        | 120         | ptr         | container.data   | Inventory list head data             |
| 108        | 124         | ptr         | container.next   | Inventory list next pointer          |
| 148        | 164         | AIData (16) | aiData           | TESAIForm AI behavior data           |
| 188        | 204         | uint8[7]    | cAttribute       | S.P.E.C.I.A.L. stats                 |
| 272        | 288         | ptr         | pRace            | Race (TESRace\*)                     |
| 276        | 292         | uint8[14]   | skills           | Skill values (14 skills)             |
| 304        | 320         | ptr         | pClass           | Class (TESClass\*)                   |
| 320        | 336         | ptr         | fggs.pData       | FaceGen geometry-symmetric data ptr  |
| 332        | 348         | uint32      | fggs.count       | Always 50 floats                     |
| 352        | 368         | ptr         | fgga.pData       | FaceGen geometry-asymmetric data ptr |
| 364        | 380         | uint32      | fgga.count       | Always 30 floats                     |
| 384        | 400         | ptr         | fgts.pData       | FaceGen texture-symmetric data ptr   |
| 396        | 412         | uint32      | fgts.count       | Always 50 floats                     |
| 440        | 456         | ptr         | pHair            | Hair form (TESHair\*)                |
| 444        | 460         | float       | hairLength       | Hair length (0.0-1.0)                |
| 448        | 464         | ptr         | pEyes            | Eyes form (TESEyes\*)                |
| 468        | 484         | ptr         | pCombatStyle     | Combat style (TESCombatStyle\*)      |

**PDB Type:** `0x0E14B` — PDB Size: 492 — Dump Size: 508 (+16 shift)
**Code:** `RuntimeStructReader.ReadRuntimeNpc()` → `Models/NpcRecord.cs`
**Called from:** `RecordParser.ReconstructNpcs()`

#### ACBS (Actor Base Stats) — Embedded at NPC +68

| Relative Offset | Type      | Field           | Description                     |
| --------------- | --------- | --------------- | ------------------------------- |
| +0              | uint32 BE | flags           | Actor flags bitmask             |
| +4              | uint16 BE | fatigueBase     | Base fatigue                    |
| +6              | uint16 BE | barterGold      | Barter gold amount              |
| +8              | int16 BE  | level           | Level (negative = level-offset) |
| +10             | uint16 BE | calcMin         | Calc min level                  |
| +12             | uint16 BE | calcMax         | Calc max level                  |
| +14             | uint16 BE | speedMultiplier | Speed multiplier                |
| +16             | float BE  | karmaAlignment  | Karma alignment value           |
| +20             | int16 BE  | dispositionBase | Base disposition                |
| +22             | uint16 BE | templateFlags   | Template flags                  |

**Size:** 24 bytes — Parsed by `RuntimeStructReader.ReadActorBaseStats()`

#### NPC AI Data — Embedded at NPC +164

| Relative Offset | Type      | Field              | Description                                                |
| --------------- | --------- | ------------------ | ---------------------------------------------------------- |
| +0              | uint8     | aggression         | 0=Unaggressive, 1=Aggressive, 2=VeryAggressive, 3=Frenzied |
| +4              | uint8     | mood               | Mood type                                                  |
| +8              | uint32 BE | buySellAndServices | Buy/sell service flags                                     |
| +14             | uint8     | assistance         | 0=Nobody, 1=Allies, 2=FriendsAndAllies                     |

**Parsed by:** `RuntimeStructReader.ReadNpcAiData()`

### TESCreature (CREA — FormType 0x2B)

Non-humanoid actors. Similar layout to NPC but with creature-specific fields.

| PDB Offset | Dump Offset | Type          | Field          | Description              |
| ---------- | ----------- | ------------- | -------------- | ------------------------ |
| 12         | 12          | uint32        | iFormID        | Creature FormID          |
| 8          | 24          | ACBS (24)     | actorBaseStats | Same ACBS as NPC         |
| 172        | 188         | BSStringT (8) | cModel         | Model path               |
| 204        | 220         | ptr           | pScript        | Script (Script\*)        |
| 212        | 228         | uint8         | combatSkill    | Combat skill             |
| 213        | 229         | uint8         | magicSkill     | Magic skill              |
| 214        | 230         | uint8         | stealthSkill   | Stealth skill            |
| 216        | 232         | int16 BE      | attackDamage   | Attack damage            |
| 220        | 236         | uint8         | creatureType   | Type (0=Animal..5=Robot) |

**PDB Type:** `0x0D868` — PDB Size: 352 — Dump Size: 440 (+16 shift, but larger due to additional data)
**Code:** `RuntimeStructReader.ReadRuntimeCreature()` → `Models/CreatureRecord.cs`

---

## Item Structures

### TESObjectWEAP (WEAP — FormType 0x18)

Weapon definitions with extensive combat data.

| PDB Offset | Dump Offset | Type          | Field         | Description                          |
| ---------- | ----------- | ------------- | ------------- | ------------------------------------ |
| 12         | 12          | uint32        | iFormID       | Weapon FormID                        |
| 64         | 80          | BSStringT (8) | cModel        | Model path                           |
| 136        | 152         | int32 BE      | iValue        | Item value (caps)                    |
| 144        | 160         | float BE      | fWeight       | Item weight                          |
| 152        | 168         | int32 BE      | iHealth       | Weapon health/condition              |
| 160        | 176         | uint16 BE     | sAttackDamage | Base damage                          |
| 168        | 184         | ptr           | pFormAmmo     | Default ammo (TESObjectAMMO\*)       |
| 176        | 192         | uint8         | cClipRounds   | Clip/magazine size                   |
| 244        | 260         | uint8         | animType      | Animation type (first byte only)     |
| 248        | 264         | float BE      | speed         | Animation speed multiplier           |
| 252        | 268         | float BE      | reach         | Melee reach                          |
| 260+16     | 276         | float BE      | minSpread     | Minimum spread                       |
| 260+20     | 280         | float BE      | spread        | Spread                               |
| 260+36     | 296         | ptr           | projectile    | Projectile (BGSProjectile\*)         |
| 260+40     | 300         | uint8         | vatsChance    | VATS to-hit chance                   |
| 260+44     | 304         | float BE      | minRange      | Minimum range                        |
| 260+48     | 308         | float BE      | maxRange      | Maximum range                        |
| 260+68     | 328         | float BE      | actionPoints  | AP cost                              |
| 260+88     | 348         | float BE      | shotsPerSec   | Rate of fire                         |
| —          | 456         | int16 BE      | critDamage    | Critical damage                      |
| —          | 460         | float BE      | critChance    | Critical chance multiplier           |
| 236        | 252         | ptr           | pickupSound   | Pickup sound (TESSound\*)            |
| 240        | 256         | ptr           | putdownSound  | Putdown sound (TESSound\*)           |
| —          | 548         | ptr           | fireSound3D   | 3D fire sound                        |
| —          | 552         | ptr           | fireSoundDist | Distant fire sound                   |
| —          | 556         | ptr           | fireSound2D   | 2D fire sound                        |
| —          | 564         | ptr           | dryFireSound  | Dry fire sound                       |
| —          | 572         | ptr           | idleSound     | Idle loop sound                      |
| —          | 576         | ptr           | equipSound    | Equip sound                          |
| —          | 580         | ptr           | unequipSound  | Unequip sound                        |
| —          | 584         | ptr           | impactDataSet | Impact data set (BGSImpactDataSet\*) |

**PDB Type:** `0x1FF61` — PDB Size: 908 — Dump Size: 924 (+16 shift)
**Code:** `RuntimeStructReader.ReadRuntimeWeapon()` → `Models/WeaponRecord.cs`

### TESObjectARMO (ARMO — FormType 0x1A)

Armor and clothing items.

| PDB Offset | Dump Offset | Type      | Field       | Description                          |
| ---------- | ----------- | --------- | ----------- | ------------------------------------ |
| 12         | 12          | uint32    | iFormID     | Armor FormID                         |
| 92         | 108         | int32 BE  | iValue      | Item value (caps)                    |
| 100        | 116         | float BE  | fWeight     | Item weight                          |
| 108        | 124         | int32 BE  | iHealth     | Armor health/condition               |
| 376        | 392         | uint16 BE | armorRating | AR (×100, divide by 100 for display) |

**PDB Type:** `0x144F3` — PDB Size: 400 — Dump Size: 416 (+16 shift)
**Code:** `RuntimeStructReader.ReadRuntimeArmor()` → `Models/ArmorRecord.cs`

### TESObjectAMMO (AMMO — FormType 0x29)

Ammunition items.

| PDB Offset | Dump Offset | Type     | Field   | Description       |
| ---------- | ----------- | -------- | ------- | ----------------- |
| 12         | 12          | uint32   | iFormID | Ammo FormID       |
| 124        | 140         | int32 BE | iValue  | Item value (caps) |

**Dump Size:** 236 (+16 shift)
**Code:** `RuntimeStructReader.ReadRuntimeAmmo()` → `Models/AmmoRecord.cs`

### TESObjectALCH (ALCH — FormType 0x2F)

Consumable items (food, chems, medicine).

| PDB Offset | Dump Offset | Type     | Field   | Description       |
| ---------- | ----------- | -------- | ------- | ----------------- |
| 12         | 12          | uint32   | iFormID | Item FormID       |
| 152        | 168         | float BE | fWeight | Item weight       |
| 184        | 200         | int32 BE | iValue  | Item value (caps) |

**Dump Size:** 232 (+16 shift)
**Code:** `RuntimeStructReader.ReadRuntimeConsumable()` → `Models/ConsumableRecord.cs`

### TESObjectMISC / TESKey (MISC 0x1F / KEYM)

Miscellaneous items and keys share the same struct layout.

| PDB Offset | Dump Offset | Type     | Field   | Description       |
| ---------- | ----------- | -------- | ------- | ----------------- |
| 12         | 12          | uint32   | iFormID | Item FormID       |
| 120        | 136         | int32 BE | iValue  | Item value (caps) |
| 128        | 144         | float BE | fWeight | Item weight       |

**PDB Type (MISC):** `0x1657A` — Size: 172 — Dump Size: 188 (+16 shift)
**PDB Type (Key):** `0x1BC5E` — Size: 172 — Dump Size: 188 (+16 shift)
**Code:** `RuntimeStructReader.ReadRuntimeMiscItem()` / `ReadRuntimeKey()` → `Models/MiscItemRecord.cs` / `KeyRecord.cs`

### BGSNote (NOTE)

Holotape and paper note items.

| PDB Offset | Dump Offset | Type          | Field     | Description                              |
| ---------- | ----------- | ------------- | --------- | ---------------------------------------- |
| 12         | 12          | uint32        | iFormID   | Note FormID                              |
| 52         | 68          | BSStringT (8) | cModel    | Model path                               |
| 76         | 92          | BSStringT (8) | cFullName | Display name                             |
| 124        | 140         | uint8         | cNoteType | Type (0=Sound, 1=Text, 2=Image, 3=Voice) |

**PDB Type:** `0x27E40` — Size: 128 — Dump Size: 160 (+16 shift, with additional data)
**Code:** `RuntimeStructReader.ReadRuntimeNote()` → `Models/NoteRecord.cs`

---

## World Structures

### TESObjectCONT (CONT — FormType 0x1B)

Container objects (desks, lockers, safes).

| PDB Offset | Dump Offset | Type          | Field               | Description                               |
| ---------- | ----------- | ------------- | ------------------- | ----------------------------------------- |
| 12         | 12          | uint32        | iFormID             | Container FormID                          |
| 48         | 64          | ptr           | TESContainer vtable | TESContainer base class                   |
| 52         | 68          | ptr           | objectList.data     | tList first node data (ContainerObject\*) |
| 56         | 72          | ptr           | objectList.next     | tList first node next                     |
| 64         | 80          | BSStringT (8) | cModel              | Model path                                |
| 100        | 116         | ptr           | pScript             | Script (Script\*)                         |
| 124        | 140         | uint8         | flags               | Container flags (bit1=Respawn)            |

**Note:** TESContainer is at PDB offset 48 in TESObjectCONT, but at PDB offset 100 in TESNPC/TESActorBase.
The objectList tList is at TESContainer+4, so CONT uses dump+68 while NPC uses dump+120.

**PDB Type:** `0x1D9E4` — Size: 156 — Dump Size: 172 (+16 shift)
**Code:** `RuntimeStructReader.ReadRuntimeContainer()` → `Models/ContainerRecord.cs`

### BGSTerminal (TERM — FormType 0x17)

Computer terminal objects.

| PDB Offset | Dump Offset | Type          | Field      | Description                              |
| ---------- | ----------- | ------------- | ---------- | ---------------------------------------- |
| 12         | 12          | uint32        | iFormID    | Terminal FormID                          |
| 116        | 132         | uint8         | difficulty | Lock difficulty (0=VeryEasy..4=VeryHard) |
| 117        | 133         | uint8         | flags      | Terminal flags                           |
| 120        | 136         | BSStringT (8) | password   | Terminal password                        |

**PDB Type:** `0x28035` — Size: 168 — Dump Size: 184 (+16 shift)
**Code:** `RuntimeStructReader.ReadRuntimeTerminal()` → `Models/TerminalRecord.cs`

### BGSProjectile (PROJ)

Projectile physics and effect definitions.

| PDB Offset | Dump Offset | Type     | Field           | Description                        |
| ---------- | ----------- | -------- | --------------- | ---------------------------------- |
| 12         | 12          | uint32   | iFormID         | Projectile FormID                  |
| 96         | 112         | —        | (data base)     | BGSProjectileData start            |
| 100        | 116         | float BE | gravity         | Gravity multiplier                 |
| 104        | 120         | float BE | speed           | Projectile speed                   |
| 108        | 124         | float BE | range           | Maximum range                      |
| 132        | 148         | ptr      | explosion       | Explosion effect (TESObjectEXPL\*) |
| 136        | 152         | ptr      | activeSound     | Active sound (TESSound\*)          |
| 140        | 156         | float BE | muzzleFlashDur  | Muzzle flash duration              |
| 148        | 164         | float BE | force           | Impact force                       |
| 152        | 168         | ptr      | countdownSound  | Countdown sound                    |
| 156        | 172         | ptr      | deactivateSound | Deactivation sound                 |

**PDB Type:** `0x24B56` — PDB Size: 208 — Dump Size: 224 (+16 shift)
**Code:** `RuntimeStructReader.ReadProjectilePhysics()`

### TESObjectLAND (LAND)

Landscape/terrain cell data.

| PDB Offset | Dump Offset | Type   | Field       | Description               |
| ---------- | ----------- | ------ | ----------- | ------------------------- |
| 12         | 12          | uint32 | iFormID     | Land FormID               |
| —          | 56          | ptr    | pLoadedData | Pointer to LoadedLandData |

**PDB Type:** `0x1EE87` — Dump Size: 60

#### LoadedLandData — Pointed to by LAND +56

| Offset | Type  | Field                      | Description            |
| ------ | ----- | -------------------------- | ---------------------- |
| 0-151  | —     | (heightmap, normals, etc.) | Terrain mesh data      |
| 152    | int32 | iCellX                     | Cell grid X coordinate |
| 156    | int32 | iCellY                     | Cell grid Y coordinate |
| 160    | float | fBaseHeight                | Base terrain height    |

**PDB Type:** `0x1EE99` — Size: 164 bytes
**Code:** `RuntimeStructReader.ReadRuntimeLandData()` → `Models/RuntimeLoadedLandData.cs`

---

## Dialogue Structures

### TESTopic / DIAL (FormType 0x45)

Dialog topic containers. Each topic belongs to a quest and contains INFO records.

| PDB Offset | Dump Offset | Type              | Field           | Description                   |
| ---------- | ----------- | ----------------- | --------------- | ----------------------------- |
| 0          | 0           | TESForm (24)      | (base)          | Inherited TESForm             |
| 24         | 24          | (TESFullName)     | (base)          | Full name component           |
| 28+0       | 44          | BSStringT (8)     | cFullName       | Topic display name            |
| 36         | 52          | DIALOGUE_DATA (2) | m_Data          | Topic type + flags            |
| 40         | 56          | float             | m_fPriority     | Topic priority (default 50.0) |
| 44         | 60          | BSSimpleList (8)  | m_listQuestInfo | List of QUEST_INFO entries    |
| 52         | 68          | BSStringT (8)     | cDummyPrompt    | Fallback prompt text          |
| 60         | 76          | int32             | m_iJournalIndex | Journal index                 |
| 64         | 80          | BSStringT (8)     | cFormEditorID   | Editor ID string              |
| 68         | 84          | uint32            | m_uiTopicCount  | Number of INFO records        |

**PDB Type:** `0x1887F` — PDB Size: 72 — Dump Size: 88 (+16 shift)
**Code:** `RuntimeStructReader.ReadRuntimeDialogTopic()` → `Models/RuntimeDialogTopicInfo.cs`
**Called from:** `RecordParser.MergeRuntimeDialogTopicData()` and `DialogueRuntimeMerger.MergeRuntimeDialogueTopicLinks()`

Topic-level default speaker (`TNAM` in ESM) is not represented in the validated `TESTopic` runtime layout above. Current DMP parity for `DIAL` therefore treats topic metadata and `QUEST_INFO` linkage as runtime-backed, while topic default speaker remains ESM-authored unless new runtime evidence is found.

#### DIALOGUE_DATA — Embedded at TESTopic +52 (dump)

| Offset | Type  | Field  | Description                                                                              |
| ------ | ----- | ------ | ---------------------------------------------------------------------------------------- |
| +0     | uint8 | type   | 0=Topic, 1=Conversation, 2=Combat, 3=Persuasion, 4=Detection, 5=Service, 6=Misc, 7=Radio |
| +1     | uint8 | cFlags | bit0=Rumors, bit1=TopLevel                                                               |

**PDB Type:** `0x1DF18` — Size: 2 bytes

### TESTopicInfo / INFO (FormType 0x45)

Individual dialogue response entries. Each belongs to a TESTopic.

| PDB Offset | Dump Offset | Type                | Field               | Description                                                   |
| ---------- | ----------- | ------------------- | ------------------- | ------------------------------------------------------------- |
| 0-39       | 0-39        | TESForm (40)        | (base)              | Inherited TESForm including `cFormEditorID` in Release builds |
| 40         | 40          | TESCondition (8)    | objConditions       | Condition list                                                |
| 48         | 48          | uint16              | iInfoIndex          | Ordering index within topic                                   |
| 50         | 50          | bool                | bSaidOnce           | Said-once state flag                                          |
| 51         | 51          | TOPIC_INFO_DATA (4) | m_Data              | Type, next speaker, flags                                     |
| 56         | 56          | BSStringT (8)       | cPrompt             | Player-visible prompt text                                    |
| 64         | 64          | BSSimpleList (8)    | m_listAddTopics     | Additional topics list                                        |
| 72         | 72          | ptr                 | m_pConversationData | Conversation link data                                        |
| 76         | 76          | ptr                 | pSpeaker            | Speaker NPC (TESActorBase\*)                                  |
| 80         | 80          | ptr                 | pPerkSkillStat      | Perk/skill stat pointer                                       |
| 84         | 84          | uint32              | eDifficulty         | Speech challenge difficulty (0-5)                             |
| 88         | 88          | ptr                 | pOwnerQuest         | Parent quest (TESQuest\*)                                     |
| 92         | 92          | uint32              | iFileOffset         | Temp/source offset metadata; not a reliable ESM file offset   |

**PDB Type:** `0x0001E2F9` — Size: 96 bytes
**Layout Note:** Current runtime INFO reading uses the direct 96-byte Release Beta / Final layout. The older Proto Debug `80 -> 84` shifted layout remains historical reference only.
**Code:** `RuntimeStructReader.ReadRuntimeDialogueInfo()` / `ReadRuntimeDialogueInfoFromVA()` → `Models/RuntimeDialogueInfo.cs`
**Called from:** `RecordParser.MergeRuntimeDialogueData()`, `MergeRuntimeDialogueTopicLinks()`

On real dumps, direct INFO hash-table hits can be sparse or absent. The semantic INFO path therefore also relies on `QUEST_INFO` topic walking plus `ReadRuntimeDialogueInfoFromVA()` to enrich existing dialogues when only the topic graph exposes the runtime INFO structs.

#### TOPIC_INFO_DATA — Embedded at TESTopicInfo +51

| Offset | Type  | Field       | Description                                                                                    |
| ------ | ----- | ----------- | ---------------------------------------------------------------------------------------------- |
| +0     | uint8 | type        | Topic type (mirrors parent topic)                                                              |
| +1     | uint8 | nextSpeaker | Next speaker enum                                                                              |
| +2     | uint8 | flags       | Info flags: Goodbye(0x01), Random(0x02), RandomEnd(0x04), SayOnce(0x10), SpeechChallenge(0x80) |
| +3     | uint8 | flagsExt    | Extended flags: SayOnceADay(0x01), AlwaysDarkened(0x02)                                        |

**PDB Type:** `0x1BBC9` — Size: 4 bytes

**Crash dump caveat:** `TOPIC_INFO_DATA` is often uninitialized in dumps and can carry heap-fill patterns instead of live dialogue state. `RuntimeStructReader` validates these fields (`nextSpeaker ≤ 2`, `type ≤ 7`) and zeros them when invalid to prevent garbage flags from propagating.

### QUEST_INFO — Embedded in TESTopic.m_listQuestInfo

Links a quest to its INFO records within a topic. Walked via pointer following from TESTopic.

| PDB Offset | Type               | Field         | Description                                |
| ---------- | ------------------ | ------------- | ------------------------------------------ |
| 0          | ptr                | pQuest        | Quest pointer (TESQuest\*) → FormID at +12 |
| 4          | NiTLargeArray (24) | infoArray     | Array of TESTopicInfo\* pointers           |
| 28         | BSSimpleArray (16) | infoLinkArray | Array of INFO_LINK_ELEMENT                 |
| 44         | ptr                | pRemovedQuest | Removed quest pointer                      |
| 48         | bool               | bInitialized  | Whether entry is initialized               |

**PDB Type:** `0x135BD` — Size: 52 bytes
**Walking:** The m_listQuestInfo BSSimpleList is traversed node-by-node. For each QUEST_INFO node, the infoArray (NiTLargeArray) buffer pointer at +4 is followed, and each TESTopicInfo\* entry's FormID is read at +12.
**Code:** `RuntimeStructReader` via `RecordParser.MergeRuntimeDialogueTopicLinks()`

### INFO_LINK_ELEMENT — In QUEST_INFO.infoLinkArray

| Offset | Type   | Field         | Description            |
| ------ | ------ | ------------- | ---------------------- |
| 0      | uint32 | nID           | Link identifier        |
| 4      | ptr    | pInfo         | TESTopicInfo\* pointer |
| 8      | int32  | nDesiredIndex | Desired ordering index |

**PDB Type:** `0x1A00B` — Size: 12 bytes

### TESResponse — Linked list of dialogue responses

| PDB Offset | Type               | Field         | Description                             |
| ---------- | ------------------ | ------------- | --------------------------------------- |
| 0          | RESPONSE_DATA (24) | Data          | Response metadata                       |
| 24         | BSStringT (8)      | cResponseText | NPC's spoken text (NAM1 equivalent)     |
| 32         | ptr                | pSpeakerIdle  | Speaker idle animation (TESIdleForm\*)  |
| 36         | ptr                | pListenerIdle | Listener idle animation (TESIdleForm\*) |
| 40         | ptr                | pNext         | Next TESResponse in linked list         |

**PDB Type:** `0x1C7F3` — Size: 44 bytes
**Status:** Identified in PDB but NOT found instantiated in crash dumps. The game loads response text on-demand from the ESM file; it is not kept in the runtime heap.

#### RESPONSE_DATA — Embedded at TESResponse +0

| Offset | Type   | Field              | Description                      |
| ------ | ------ | ------------------ | -------------------------------- |
| 0      | uint32 | eEmotion           | Emotion enum                     |
| 4      | uint32 | iEmotionValue      | Emotion intensity (0-100)        |
| 8      | ptr    | pConversationTopic | Conversation topic (TESTopic\*)  |
| 12     | uint8  | ucResponseID       | Response ordering index          |
| 16     | ptr    | pVoiceSound        | Voice sound file (TESSound\*)    |
| 20     | bool   | bUseEmotion        | Whether to use emotion animation |

**PDB Type:** `0x1D00C` — Size: 24 bytes

### TESConversationData — Conversation linking data

| PDB Offset | Type             | Field               | Description            |
| ---------- | ---------------- | ------------------- | ---------------------- |
| 0          | BSSimpleList (8) | m_listLinkFrom      | Incoming links         |
| 8          | BSSimpleList (8) | m_listLinkTo        | Outgoing links         |
| 16         | BSSimpleList (8) | m_listFollowUpInfos | Follow-up INFO records |

**PDB Type:** `0x17098` — Size: 24 bytes
**Status:** This pointer is not generally resident, but it is not universally null. Current sample-dump evidence is:

- `m_listFollowUpInfos` has positive real-dump hits on `Fallout_Release_MemDebug.xex.dmp`.
- `m_listLinkFrom` / `m_listLinkTo` are still unproven on the validated sample dumps.

---

## Organization Structures

### TESFaction (FACT)

Faction definitions with reputation and service flags.

| PDB Offset | Dump Offset | Type          | Field     | Description           |
| ---------- | ----------- | ------------- | --------- | --------------------- |
| 12         | 12          | uint32        | iFormID   | Faction FormID        |
| 28         | 44          | BSStringT (8) | cFullName | Faction display name  |
| 52         | 68          | uint32 BE     | flags     | Faction flags bitmask |

**PDB Type:** `0x2236C` — PDB Size: 76 — Dump Size: 108 (+16 shift, but struct appears larger in practice)
**Code:** `RuntimeStructReader.ReadRuntimeFaction()` → `Models/FactionRecord.cs`

### TESQuest (QUST)

Quest definitions with stages and objectives.

| PDB Offset | Dump Offset | Type          | Field     | Description                                    |
| ---------- | ----------- | ------------- | --------- | ---------------------------------------------- |
| 12         | 12          | uint32        | iFormID   | Quest FormID                                   |
| 52         | 68          | BSStringT (8) | cFullName | Quest display name                             |
| 60         | 76          | uint8         | flags     | Quest flags (active, started, completed, etc.) |
| 61         | 77          | uint8         | priority  | Quest priority (0-255)                         |

**PDB Type:** `0x13B82` — PDB Size: 108 — Dump Size: 140 (+16 shift)
**Code:** `RuntimeStructReader.ReadRuntimeQuest()` → `Models/QuestRecord.cs`

#### TESQuestStageItem

Runtime stage-item helper hanging off `TESQuest.m_listStages -> TESQuestStage -> BSSimpleList<TESQuestStageItem*>`.

| PDB Offset | Type         | Field          | Description                                        |
| ---------- | ------------ | -------------- | -------------------------------------------------- |
| 0          | struct(1)    | m_Data         | Stage-item flags block (`QUEST_STAGE_ITEM_DATA`)   |
| 4          | TESCondition | objConditions  | Stage-item conditions                              |
| 12         | Script\*     | cResultScript  | Stage-item result script                           |
| 112        | uint32       | m_fileOffset   | Source file offset metadata                        |
| 116        | uint8        | ucIndex        | Stage item index                                   |
| 117        | bool         | m_bHasLogEntry | Whether the stage item has an associated log entry |
| 120        | Date\*       | m_pLogDate     | Optional log date                                  |
| 124        | TESQuest\*   | m_pOwner       | Owning quest                                       |
| 128        | TESQuest\*   | m_pNextQuest   | Linked quest pointer                               |

Relevant methods from the Final Debug PDB:

- `GetLogEntry(TESForm*) -> char*`
- `GetFileOffset() -> uint32`
- `HasLogEntry() -> bool`
- `GetOwner() -> TESQuest*`
- `SetLogEntry(bool)` / `Resolve(bool)`

Current parity conclusion:

- The PDB shows no inline runtime string field for quest stage log text.
- `GetLogEntry` requires a `TESForm*` argument, so the text appears to be resolved contextually rather than stored directly on `TESQuestStageItem`.
- `m_fileOffset` and `m_bHasLogEntry` are useful evidence, but they are not enough on their own to reconstruct `CNAM` stage log text safely from DMP memory.
- `TESQuest::SaveGame[_v2]` / `LoadGame[_v2]` also support that boundary: the save stream carries stage index, stage flags, per-log index, a `hasNote` byte, and optional 4-byte note payloads, but not stage log text.
- Runtime parity should therefore treat quest stage log entries as `ESM-only` until decompilation or dump-backed evidence proves a stable resolution path.

---

## Placed Reference Structures

> **PDB Source:** Final Debug PDB (`Sample/PDB/Final/Fallout_Debug_Final/types_full.txt`)
> Final PDB offsets match dump offsets directly — no shift needed.
> All offsets verified against Ghidra decompilation of `TESObjectREFR::SaveGame_v2` / `LoadGame_v2`.

### TESObjectREFR (REFR/ACHR/ACRE — FormType 0x40)

The placed reference type for all objects in the game world. Every placed NPC, creature, item, activator, map marker, door, etc. is a TESObjectREFR instance (or a subclass: Character for ACHR, Creature for ACRE). This is the critical type for the world map — it holds position, rotation, scale, parent cell, base object, and the ExtraDataList which contains map marker data, persistence flags, etc.

**Inheritance:** TESForm (offset 0, 40 bytes) + TESChildCell (offset 40, 4 bytes)

| Offset | Type             | Size | Field                 | Description                                    |
| ------ | ---------------- | ---- | --------------------- | ---------------------------------------------- |
| 0      | ptr              | 4    | vfptr                 | TESForm virtual function table                 |
| 4      | uint8            | 1    | cFormType             | Form type (0x40 for REFR)                      |
| 8      | uint32           | 4    | iFormFlags            | Form flags (0x0400=Persistent, 0x0020=Deleted) |
| 12     | uint32           | 4    | iFormID               | Unique FormID                                  |
| 16     | BSFixedString    | 8    | cFormEditorID         | Editor ID string                               |
| 24     | uint32           | 4    | iVersionControl       | Version control info                           |
| 28     | uint8            | 1    | cVCVersion            | Version control version                        |
| 32     | ptr              | ~8   | pSourceFiles          | Source ESM file list                           |
| 40     | ptr              | 4    | (TESChildCell vfptr)  | TESChildCell vtable                            |
| 44     | ptr              | 4    | pRandomSound          | Random sound pointer                           |
| 48     | TESBoundObject\* | 4    | data.pObjectReference | Base object (the "template" form)              |
| 52     | NiPoint3         | 12   | data.Angle            | Rotation (X, Y, Z radians)                     |
| 64     | NiPoint3         | 12   | data.Location         | World position (X, Y, Z game units)            |
| 76     | float            | 4    | fRefScale             | Scale factor (1.0 = normal)                    |
| 80     | TESObjectCELL\*  | 4    | pParentCell           | Parent cell pointer                            |
| 84     | ExtraDataList    | 32   | m_Extra               | Extra data container (see below)               |
| 116    | ptr              | 4    | pLoadedData           | Loaded 3D data (null when unloaded)            |

**PDB Type:** `0x0001196D` (Final PDB) — Size: 120 bytes — 517 members
**RTTI Census:** Instances confirmed in all 50 crash dumps (class name `TESObjectREFR`).
**Ghidra Cross-Reference:**

- `r31+0x4C` (offset 76) confirmed as `fRefScale` — float load/store in SaveGame_v2
- `r31+0x54` (offset 84) confirmed as `m_Extra` — ExtraDataList Save/Load calls
- `r31+0x74` (offset 116) confirmed as `pLoadedData` — pointer dereference in SaveGame_v2
  **Validation:** TESForm base fields (+4/+8/+12) proven across 17 form types. TESObjectREFR-specific offsets validated by 3 independent Ghidra confirmations. Full DMP struct read validation pending for RuntimeRefrReader implementation.

#### OBJ_REFR — Embedded data sub-struct at offset +48

| Relative Offset | Type             | Size | Field            | Description                   |
| --------------- | ---------------- | ---- | ---------------- | ----------------------------- |
| +0              | TESBoundObject\* | 4    | pObjectReference | Base form (follow for FormID) |
| +4              | NiPoint3         | 12   | Angle            | Rotation (X, Y, Z as float32) |
| +16             | NiPoint3         | 12   | Location         | Position (X, Y, Z as float32) |

**PDB Type:** `0x00012387` — Size: 28 bytes

#### NiPoint3

| Relative Offset | Type    | Size | Field | Description         |
| --------------- | ------- | ---- | ----- | ------------------- |
| +0              | float32 | 4    | x     | X coordinate        |
| +4              | float32 | 4    | y     | Y coordinate        |
| +8              | float32 | 4    | z     | Z coordinate / axis |

**PDB Type:** `0x000159E6` — Size: 12 bytes

### ExtraDataList / BaseExtraList — Embedded at TESObjectREFR +84

Container for all extra data attached to a reference. Inherits from BaseExtraList. Stores a linked list of BSExtraData entries and a bitfield indicating which extra types are present.

| Offset (within REFR) | Type          | Size | Field  | Description                                |
| -------------------- | ------------- | ---- | ------ | ------------------------------------------ |
| +84                  | ptr           | 4    | vfptr  | ExtraDataList vtable                       |
| +88                  | BSExtraData\* | 4    | pHead  | Head of linked list of extra data entries  |
| +92                  | uint8[21]     | 21   | iFlags | Bitfield: bit N set = extra type N present |
| +113                 | —             | 3    | (pad)  | Alignment padding                          |

**PDB Type:** `0x00010B8F` (ExtraDataList, 32 bytes) inherits `0x000186E0` (BaseExtraList, 32 bytes)
**Usage:** To find a specific extra type, check `iFlags[type/8] & (1 << (type%8))`. If set, walk the linked list from `pHead` until `cEtype` matches.

### BSExtraData — Base class for all extra data entries

Each entry in the ExtraDataList linked list. Subclasses add type-specific data after the base fields.

| Offset | Type          | Size | Field  | Description                            |
| ------ | ------------- | ---- | ------ | -------------------------------------- |
| 0      | ptr           | 4    | vfptr  | Virtual function table                 |
| 4      | uint8         | 1    | cEtype | Extra data type code                   |
| 8      | BSExtraData\* | 4    | pNext  | Next entry in linked list (null = end) |

**PDB Type:** `0x00014279` — Size: 12 bytes

### ExtraMapMarker — BSExtraData subclass (type code 0x2C / 44)

Attached to REFR instances that are map markers. Contains a pointer to the MapMarkerData struct.

| Offset | Type            | Size | Field         | Description                   |
| ------ | --------------- | ---- | ------------- | ----------------------------- |
| 0-11   | —               | 12   | (BSExtraData) | Inherited base class          |
| 12     | MapMarkerData\* | 4    | pMapData      | Pointer to marker data struct |

**PDB Type:** `0x0001745A` — Size: 16 bytes
**Detection:** Check `cEtype == 0x2C` in the ExtraDataList linked list, or check bit 44 in `iFlags`.

### MapMarkerData — Pointed to by ExtraMapMarker

Contains the marker's display name, type icon, and visibility flags.

| Offset | Type        | Size | Field          | Description                                                     |
| ------ | ----------- | ---- | -------------- | --------------------------------------------------------------- |
| 0      | TESFullName | 12   | LocationName   | Marker display name (see below)                                 |
| 12     | uint8       | 1    | cFlags         | Visibility flags (bit 0=Visible, bit 1=CanTravel, bit 2=Hidden) |
| 13     | uint8       | 1    | cOriginalFlags | Original flags at creation                                      |
| 14     | uint16      | 2    | sType          | Marker type (icon index on game map)                            |
| 16     | ptr         | 4    | pReputation    | Reputation form pointer (or null)                               |

**PDB Type:** `0x0002C0AD` — Size: 20 bytes

#### TESFullName — Embedded at MapMarkerData +0

| Offset | Type          | Size | Field     | Description             |
| ------ | ------------- | ---- | --------- | ----------------------- |
| 0      | ptr           | 4    | vfptr     | TESFullName vtable      |
| 4      | BSFixedString | 8    | cFullName | The display name string |

**PDB Type:** `0x00015FAA` — Size: 12 bytes

### Key Relationships for World Map

```
TESObjectREFR (+120 bytes)
├── iFormFlags (+8)          → check 0x0400 for persistence
├── iFormID (+12)            → reference ID
├── data.pObjectReference (+48) → base form (follow ptr → +12 for base FormID)
├── data.Location (+64)      → world position (X, Y, Z floats)
├── data.Angle (+52)         → rotation
├── fRefScale (+76)          → scale
├── pParentCell (+80)        → cell pointer (follow → +12 for cell FormID)
└── m_Extra (+84)
    └── pHead (+88)          → linked list of BSExtraData
        └── BSExtraData (cEtype=0x2C)
            └── ExtraMapMarker.pMapData (+12)
                └── MapMarkerData
                    ├── LocationName.cFullName (+4) → display name
                    ├── cFlags (+12) → visible/hidden
                    └── sType (+14) → marker icon type
```

---

## Implementation Cross-Reference

| PDB Struct    | PDB Size | Dump Size | RuntimeStructReader Method  | Model File                                   | RecordParser Integration            |
| ------------- | -------- | --------- | --------------------------- | -------------------------------------------- | ----------------------------------- |
| TESNPC        | 492      | 508       | `ReadRuntimeNpc()`          | `NpcRecord.cs`                               | `ReconstructNpcs()`                 |
| TESCreature   | 352      | 440       | `ReadRuntimeCreature()`     | `CreatureRecord.cs`                          | `ReconstructCreatures()`            |
| TESObjectWEAP | 908      | 924       | `ReadRuntimeWeapon()`       | `WeaponRecord.cs`                            | `ReconstructWeapons()`              |
| TESObjectARMO | 400      | 416       | `ReadRuntimeArmor()`        | `ArmorRecord.cs`                             | `ReconstructArmor()`                |
| TESObjectAMMO | —        | 236       | `ReadRuntimeAmmo()`         | `AmmoRecord.cs`                              | `ReconstructAmmo()`                 |
| TESObjectALCH | —        | 232       | `ReadRuntimeConsumable()`   | `ConsumableRecord.cs`                        | `ReconstructConsumables()`          |
| TESObjectMISC | 172      | 188       | `ReadRuntimeMiscItem()`     | `MiscItemRecord.cs`                          | `ReconstructMiscItems()`            |
| TESKey        | 172      | 188       | `ReadRuntimeKey()`          | `KeyRecord.cs`                               | `ReconstructKeys()`                 |
| BGSNote       | 128      | 160       | `ReadRuntimeNote()`         | `NoteRecord.cs`                              | `ReconstructNotes()`                |
| TESObjectCONT | 156      | 172       | `ReadRuntimeContainer()`    | `ContainerRecord.cs`                         | `ReconstructContainers()`           |
| BGSTerminal   | 168      | 184       | `ReadRuntimeTerminal()`     | `TerminalRecord.cs`                          | `ReconstructTerminals()`            |
| BGSProjectile | 208      | 224       | `ReadProjectilePhysics()`   | `ProjectilePhysicsData` (in WeaponRecord.cs) | `EnrichWeaponsWithProjectileData()` |
| TESFaction    | 76       | 108       | `ReadRuntimeFaction()`      | `FactionRecord.cs`                           | `ReconstructFactions()`             |
| TESQuest      | 108      | 140       | `ReadRuntimeQuest()`        | `QuestRecord.cs`                             | `ReconstructQuests()`               |
| TESTopic      | 72       | 88        | `ReadRuntimeDialogTopic()`  | `RuntimeDialogTopicInfo.cs`                  | `MergeRuntimeDialogTopicData()`     |
| TESTopicInfo  | 96       | 96        | `ReadRuntimeDialogueInfo()` | `RuntimeDialogueInfo.cs`                     | `MergeRuntimeDialogueData()`        |
| TESObjectLAND | 44       | 60        | `ReadRuntimeLandData()`     | `RuntimeLoadedLandData.cs`                   | `ReadAllRuntimeLandData()`          |

---

## Unimplemented / Partially Explored

### TESResponse (PDB: 44 bytes)

Found in PDB with full field definitions. Contains `cResponseText` (BSStringT at +24) which holds NPC dialogue text (the NAM1 equivalent). However, investigation confirmed that TESResponse structs are **not instantiated in runtime heap memory** — the game reads response text on-demand from the ESM file. Only 0.5-1.1% of ESM NAM1 texts were found anywhere in crash dumps, and zero TESResponse struct signatures were detected near those text occurrences.

### TESConversationData (PDB: 24 bytes)

Pointer at TESTopicInfo +72 (`m_pConversationData`) in the current Release Beta / Final INFO layout. The runtime reader already walks the struct when the pointer survives. Current real-dump evidence is asymmetric: `m_listFollowUpInfos` has positive hits on `Fallout_Release_MemDebug.xex.dmp`, while `m_listLinkFrom` / `m_listLinkTo` do not yet have stable positive sample-dump decodes.

### m_listAddTopics (BSSimpleList at TESTopicInfo +64)

Present at TESTopicInfo +64 in the current Release Beta / Final INFO layout. This is already walked by `RuntimeDialogueReader`. Real sample-dump counts are positive on the validated families:

- `Fallout_Debug.xex.dmp`: `103`
- `Fallout_Release_Beta.xex4.dmp`: `104`
- `Fallout_Release_Beta.xex44.dmp`: `166`
- `Fallout_Release_MemDebug.xex.dmp`: `175`

Base `Fallout_Release_Beta.xex.dmp` did not yield valid runtime INFO reads in the same pass, so it is not treated as a negative signal for this field.

### iFileOffset (uint32 at TESTopicInfo +92)

PDB field at TESTopicInfo +92 in the current Release Beta / Final INFO layout. Investigation still shows that most values are zero and the remainder behave like transient VA-range metadata rather than stable ESM file offsets. It is not useful for response text recovery.

### TNAM Speaker Propagation (ESM DIAL subrecord)

The game stores speaker NPC FormID at the DIAL topic level via the TNAM subrecord (4-byte FormID). Code to parse TNAM and propagate to INFO records is implemented but has zero practical impact on current crash dumps: the carved DIAL ESM fragments do not contain TNAM subrecords. TNAM exists abundantly in the full ESM file (17,500+ entries) but the crash dumps capture only tiny ESM fragments without TNAM data.
