# PDB Runtime Structure Reference

Comprehensive documentation of all C++ runtime structures identified from the Fallout: New Vegas Xbox 360 PDB symbols (`Sample/PDB/Fallout_Debug/types_full.txt`) and implemented in the memory dump analyzer.

**PDB Source:** Microsoft (R) Debugging Information Dumper — Fallout New Vegas Xbox 360 executable
**Key Files:** `RuntimeStructReader.cs`, `RecordParser.cs`, `Models/`

---

## Dump Shift Patterns

Xbox 360 crash dumps exhibit consistent offset shifts between PDB-defined offsets and actual dump offsets:

| Struct Category                                | PDB Size | Dump Size | Shift                            | Cause                                                  |
| ---------------------------------------------- | -------- | --------- | -------------------------------- | ------------------------------------------------------ |
| TESBoundObject-derived (NPC, WEAP, ARMO, etc.) | varies   | +16       | **+16**                          | Extra vtable/debug data at struct start                |
| TESTopicInfo (INFO)                            | 80       | 80        | **+4** on fields after offset 24 | TESForm base is 24 bytes but field offsets shift by +4 |
| TESTopic (DIAL)                                | 72       | 88        | **+16**                          | Same as TESBoundObject pattern                         |
| TESForm base                                   | 24       | 24        | **0**                            | No shift on base class                                 |

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
**Called from:** `RecordParser.MergeRuntimeDialogTopicData()`

#### DIALOGUE_DATA — Embedded at TESTopic +52 (dump)

| Offset | Type  | Field  | Description                                                                              |
| ------ | ----- | ------ | ---------------------------------------------------------------------------------------- |
| +0     | uint8 | type   | 0=Topic, 1=Conversation, 2=Combat, 3=Persuasion, 4=Detection, 5=Service, 6=Misc, 7=Radio |
| +1     | uint8 | cFlags | bit0=Rumors, bit1=TopLevel                                                               |

**PDB Type:** `0x1DF18` — Size: 2 bytes

### TESTopicInfo / INFO (FormType 0x45)

Individual dialogue response entries. Each belongs to a TESTopic.

| PDB Offset | Dump Offset | Type                 | Field               | Description                              |
| ---------- | ----------- | -------------------- | ------------------- | ---------------------------------------- |
| 0-23       | 0-23        | TESForm (24)         | (base)              | Inherited TESForm (no shift)             |
| 24         | 28          | TESConditionItem (8) | objConditions       | Condition list                           |
| 32         | 36          | uint16               | iInfoIndex          | Ordering index within topic              |
| 34         | 38          | bool                 | bSaidOnce           | Said-once state flag                     |
| 35         | 39          | TOPIC_INFO_DATA (4)  | m_Data              | Type, speaker, flags                     |
| 40         | 44          | BSStringT (8)        | cPrompt             | Player-visible prompt text               |
| 48         | 52          | BSSimpleList (8)     | m_listAddTopics     | Additional topics list                   |
| 56         | 60          | ptr                  | m_pConversationData | Conversation data (always null in dumps) |
| 60         | 64          | ptr                  | pSpeaker            | Speaker NPC (TESActorBase\*)             |
| 64         | 68          | ptr                  | pPerkSkillStat      | Perk/skill stat pointer                  |
| 68         | 72          | uint32               | eDifficulty         | Speech challenge difficulty (0-5)        |
| 72         | 76          | ptr                  | pOwnerQuest         | Parent quest (TESQuest\*)                |
| 76         | 80          | uint32               | iFileOffset         | File offset (mostly zero or VA in dumps) |

**PDB Type:** `0x214D7` — Size: 80 bytes
**Shift Note:** Fields after TESForm base (+24) are shifted by +4 in the dump, not +16.
**Code:** `RuntimeStructReader.ReadRuntimeDialogueInfo()` / `ReadRuntimeDialogueInfoFromVA()` → `Models/RuntimeDialogueInfo.cs`
**Called from:** `RecordParser.MergeRuntimeDialogueData()`, `MergeRuntimeDialogueTopicLinks()`

#### TOPIC_INFO_DATA — Embedded at TESTopicInfo +39 (dump)

| Offset | Type  | Field       | Description                                                                                    |
| ------ | ----- | ----------- | ---------------------------------------------------------------------------------------------- |
| +0     | uint8 | type        | Topic type (mirrors parent topic)                                                              |
| +1     | uint8 | nextSpeaker | Next speaker enum                                                                              |
| +2     | uint8 | flags       | Info flags: Goodbye(0x01), Random(0x02), RandomEnd(0x04), SayOnce(0x10), SpeechChallenge(0x80) |
| +3     | uint8 | flagsExt    | Extended flags: SayOnceADay(0x01), AlwaysDarkened(0x02)                                        |

**PDB Type:** `0x1BBC9` — Size: 4 bytes

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
**Status:** Pointer at TESTopicInfo +60 (dump). Always null/invalid in analyzed crash dumps.

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

---

## Implementation Cross-Reference

| PDB Struct    | PDB Size | Dump Size | RuntimeStructReader Method  | Model File                                          | RecordParser Integration   |
| ------------- | -------- | --------- | --------------------------- | --------------------------------------------------- | ----------------------------------- |
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
| TESTopic      | 72       | 88        | `ReadRuntimeDialogTopic()`  | `RuntimeDialogTopicInfo.cs`                         | `MergeRuntimeDialogTopicData()`     |
| TESTopicInfo  | 80       | 80        | `ReadRuntimeDialogueInfo()` | `RuntimeDialogueInfo.cs`                            | `MergeRuntimeDialogueData()`        |
| TESObjectLAND | 44       | 60        | `ReadRuntimeLandData()`     | `RuntimeLoadedLandData.cs`                          | `ReadAllRuntimeLandData()`          |

---

## Unimplemented / Partially Explored

### TESResponse (PDB: 44 bytes)

Found in PDB with full field definitions. Contains `cResponseText` (BSStringT at +24) which holds NPC dialogue text (the NAM1 equivalent). However, investigation confirmed that TESResponse structs are **not instantiated in runtime heap memory** — the game reads response text on-demand from the ESM file. Only 0.5-1.1% of ESM NAM1 texts were found anywhere in crash dumps, and zero TESResponse struct signatures were detected near those text occurrences.

### TESConversationData (PDB: 24 bytes)

Pointer at TESTopicInfo dump+60 (`m_pConversationData`). In all analyzed crash dumps, this pointer is null or points to invalid memory. The struct contains `m_listLinkFrom`, `m_listLinkTo`, and `m_listFollowUpInfos` — all BSSimpleList containers for dialogue flow linking.

### m_listAddTopics (BSSimpleList at TESTopicInfo dump+52)

Present in the PDB at TESTopicInfo +48 (dump +52). Not currently walked. Would contain additional topics triggered by choosing this dialogue option. Listed in dialogue CSV as `AddTopics` column (currently 0% populated from runtime data).

### iFileOffset (uint32 at TESTopicInfo dump+80)

PDB field at TESTopicInfo +76 (dump +80). Investigation showed 91% are zero, remainder are VA-range pointers — NOT ESM file offsets as the field name suggests. Not useful for response text recovery.

### TNAM Speaker Propagation (ESM DIAL subrecord)

The game stores speaker NPC FormID at the DIAL topic level via the TNAM subrecord (4-byte FormID). Code to parse TNAM and propagate to INFO records is implemented but has zero practical impact on current crash dumps: the carved DIAL ESM fragments do not contain TNAM subrecords. TNAM exists abundantly in the full ESM file (17,500+ entries) but the crash dumps capture only tiny ESM fragments without TNAM data.
