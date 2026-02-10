# Xbox 360 ESM Format Research

This document captures our current understanding of the Xbox 360 ESM (Elder Scrolls Master) format as used in Fallout: New Vegas, and how it differs from the PC format.

## PDB Analysis: Game Internal Structures

The following structures were extracted from the Xbox 360 debug build PDB (`Fallout.pdb`) using the `PdbAnalyzer` tool. The game has **219 structures with `Endian()` methods** - these are the critical ones that need byte-swapping for conversion.

### Core ESM Structures

#### FORM Structure (24 bytes)

The record header structure, found at PDB type 0x1570e:

```c
struct FORM {
    uint32_t form;              // Offset 0: Record type (signature as uint32)
    uint32_t length;            // Offset 4: Data size
    uint32_t flags;             // Offset 8: Record flags
    uint32_t iFormID;           // Offset 12: Form ID
    uint32_t iVersionControl;   // Offset 16: Version control info
    uint16_t sFormVersion;      // Offset 20: Form version
    uint16_t sVCVersion;        // Offset 22: VC version

    void Endian();              // Built-in endian conversion method!
};
```

#### CHUNK Structure (6 bytes)

The subrecord header structure, found at PDB type 0x14dbc:

```c
struct CHUNK {
    uint32_t chunk;             // Offset 0: Subrecord type (signature as uint32)
    uint16_t length;            // Offset 4: Data size

    void Endian();              // Built-in endian conversion method!
};
```

#### FILE_HEADER Structure (12 bytes)

The TES4 header data (HEDR subrecord), found at PDB type 0x1ed43:

```c
struct FILE_HEADER {
    float    fVersion;          // Offset 0: ESM version (1.32 Xbox, 1.34 PC)
    uint32_t iFormCount;        // Offset 4: Total form count
    uint32_t iNextFormID;       // Offset 8: Next available FormID

    void Endian();              // Built-in endian conversion method!
};
```

### Weapon Data Structures

#### OBJ_WEAP Structure (204 bytes)

The weapon data subrecord (DNAM), found at PDB type 0x1739c. Contains 58 fields:

```c
struct OBJ_WEAP {
    int8_t   eType;                         // Offset 0: Weapon type
    float    fSpeed;                        // Offset 4: Attack speed
    float    fReach;                        // Offset 8: Weapon reach
    uint8_t  cFlags;                        // Offset 12: Flags
    uint8_t  cHandGripAnim;                 // Offset 13: Hand grip animation
    uint8_t  cAmmoPerShot;                  // Offset 14: Ammo consumed per shot
    uint8_t  cReloadAnim;                   // Offset 15: Reload animation
    float    fMinSpread;                    // Offset 16: Min spread
    float    fSpread;                       // Offset 20: Spread
    float    fDrift;                        // Offset 24: Drift (sway)
    float    fIronFOV;                      // Offset 28: Iron sights FOV
    uint8_t  cConditionLevel;               // Offset 32: Condition degradation
    uint32_t pProjectile;                   // Offset 36: Projectile FormID
    uint8_t  cVATSToHitChance;              // Offset 40: VATS to-hit bonus
    uint8_t  cAttackAnim;                   // Offset 41: Attack animation
    uint8_t  cNumProjectiles;               // Offset 42: Projectiles per shot
    uint8_t  cEmbeddedConditionValue;       // Offset 43: Embedded condition value
    float    fMinRange;                     // Offset 44: Min range
    float    fMaxRange;                     // Offset 48: Max range
    uint32_t eHitBehavior;                  // Offset 52: On-hit behavior
    uint32_t iFlagsEx;                      // Offset 56: Extended flags
    float    fAttackMult;                   // Offset 60: Attack multiplier
    float    fShotsPerSec;                  // Offset 64: Fire rate
    float    fActionPoints;                 // Offset 68: AP cost
    float    fFiringRumbleLeftMotorStrength;  // Offset 72
    float    fFiringRumbleRightMotorStrength; // Offset 76
    float    fFiringRumbleDuration;         // Offset 80
    float    fDamageToWeaponMult;           // Offset 84: Weapon degradation mult
    float    fAnimShotsPerSecond;           // Offset 88: Animation fire rate
    float    fAnimReloadTime;               // Offset 92: Reload animation time
    float    fAnimJamTime;                  // Offset 96: Jam animation time
    float    fAimArc;                       // Offset 100: Aim arc
    uint32_t eSkill;                        // Offset 104: Required skill
    uint32_t eRumblePattern;                // Offset 108: Rumble pattern
    float    fRumbleWavelength;             // Offset 112: Rumble wavelength
    float    fLimbDamageMult;               // Offset 116: Limb damage multiplier
    uint32_t eResistance;                   // Offset 120: Resistance type
    float    fIronSightUseMult;             // Offset 124: Iron sight use mult
    float    fSemiAutomaticFireDelayMin;    // Offset 128: Semi-auto delay min
    float    fSemiAutomaticFireDelayMax;    // Offset 132: Semi-auto delay max
    float    fCookTimer;                    // Offset 136: Grenade cook timer
    uint32_t eModActionOne;                 // Offset 140: Mod 1 action type
    uint32_t eModActionTwo;                 // Offset 144: Mod 2 action type
    uint32_t eModActionThree;               // Offset 148: Mod 3 action type
    float    fModActionOneValue;            // Offset 152: Mod 1 value
    float    fModActionTwoValue;            // Offset 156: Mod 2 value
    float    fModActionThreeValue;          // Offset 160: Mod 3 value
    uint8_t  cPowerAttackOverrideAnim;      // Offset 164: Power attack anim
    uint32_t iStrengthRequirement;          // Offset 168: STR requirement
    int8_t   iModReloadClipAnimation;       // Offset 172: Mod reload clip anim
    int8_t   iModFireAnimation;             // Offset 173: Mod fire animation
    float    fAmmoRegenRate;                // Offset 176: Ammo regen rate
    float    fKillImpulse;                  // Offset 180: Kill impulse
    float    fModActionOneValueTwo;         // Offset 184: Mod 1 value 2
    float    fModActionTwoValueTwo;         // Offset 188: Mod 2 value 2
    float    fModActionThreeValueTwo;       // Offset 192: Mod 3 value 2
    float    fKillImpulseDistance;          // Offset 196: Kill impulse distance
    uint32_t iSkillRequirement;             // Offset 200: Skill requirement

    void Endian();
};
```

#### OBJ_WEAP_CRITICAL Structure (16 bytes)

Critical hit data for weapons:

```c
struct OBJ_WEAP_CRITICAL {
    uint16_t sCriticalDamage;   // Offset 0: Critical damage
    float    fCriticalChanceMult; // Offset 4: Critical chance multiplier
    uint8_t  bEffectOnDeath;    // Offset 8: Effect on death flag
    uint32_t pEffect;           // Offset 12: Effect FormID

    void Endian();
};
```

#### OBJ_WEAP_VATS_SPECIAL Structure (20 bytes)

VATS special attack data:

```c
struct OBJ_WEAP_VATS_SPECIAL {
    uint32_t pVATSSpecialEffect;      // Offset 0: Effect FormID
    float    fVATSSpecialAP;          // Offset 4: AP cost
    float    fVATSSpecialMultiplier;  // Offset 8: Damage multiplier
    float    fVATSSkillRequired;      // Offset 12: Skill requirement
    uint8_t  bSilent;                 // Offset 16: Silent flag
    uint8_t  bModRequired;            // Offset 17: Mod required flag
    uint8_t  cFlags;                  // Offset 18: Additional flags

    void Endian();
};
```

### NPC Data Structures

#### NPC_DATA Structure (28 bytes)

```c
struct NPC_DATA {
    int32_t  iBaseHealthPoints;  // Offset 0: Base HP
    uint8_t  Stats[7];           // Offset 4: S.P.E.C.I.A.L. stats
    // ... additional fields ...

    void Endian();
};
```

### Object Data Structures

#### OBJ_ARMO Structure (12 bytes)

Armor data (DATA subrecord):

```c
struct OBJ_ARMO {
    int32_t  iValue;    // Offset 0: Value
    int32_t  iHealth;   // Offset 4: Health/durability
    float    fWeight;   // Offset 8: Weight

    void Endian();
};
```

#### OBJ_LIGH Structure (24 bytes)

Light data:

```c
struct OBJ_LIGH {
    int32_t  iTime;     // Offset 0: Duration
    uint32_t iRadius;   // Offset 4: Light radius
    uint32_t iColor;    // Offset 8: RGBA color
    uint32_t iFlags;    // Offset 12: Flags
    float    fFalloff;  // Offset 16: Falloff exponent
    float    fFOV;      // Offset 20: Spotlight FOV

    void Endian();
};
```

### NavMesh Structures

#### NavMeshTriangle Structure (16 bytes)

```c
struct NavMeshTriangle {
    uint16_t Vertices[3];    // Offset 0: Vertex indices
    uint16_t Triangles[3];   // Offset 6: Adjacent triangle indices
    uint32_t Flags;          // Offset 12: Navigation flags

    void Endian();
};
```

#### NavMeshVertex Structure (12 bytes)

```c
struct NavMeshVertex {
    float X, Y, Z;  // Position coordinates

    void Endian();
};
```

### Complete Structure List (25 ESM-Critical with Endian)

From PDB analysis, these structures have `Endian()` methods and map to ESM subrecords:

| Structure               | Size | Description                |
| ----------------------- | ---- | -------------------------- |
| FORM                    | 24   | Record header              |
| CHUNK                   | 6    | Subrecord header           |
| FILE_HEADER             | 12   | HEDR subrecord data        |
| OBJ_WEAP                | 204  | DNAM weapon data           |
| OBJ_WEAP_CRITICAL       | 16   | CRDT critical data         |
| OBJ_WEAP_VATS_SPECIAL   | 20   | VATS special attack        |
| OBJ_ARMO                | 12   | Armor DATA                 |
| OBJ_BOOK                | 2    | Book DATA                  |
| OBJ_LAND                | 4    | Land DATA                  |
| OBJ_LIGH                | 24   | Light DATA                 |
| OBJ_TREE                | 32   | Tree DATA                  |
| NPC_DATA                | 28   | NPC DATA                   |
| NavMeshTriangle         | 16   | NAVM triangle data         |
| NavMeshVertex           | 12   | NAVM vertex data           |
| NAVMESH_PORTAL          | 8    | NAVM portal data           |
| NavMeshSaveStruct       | 24   | NAVM save data             |
| BGSExplosionData        | 52   | Explosion DATA             |
| BGSProjectileData       | 84   | Projectile DATA            |
| BGSImpactData_DATA      | 24   | Impact DATA                |
| BGSSaveLoadFormHeader   | 9    | Save game form header      |
| CellData structures     | var  | Cell DATA variants         |
| ExteriorCellData        | var  | Exterior cell data         |
| InteriorCellInitialData | var  | Interior cell initial data |

**Full list**: See `tools/EsmStructures.cs` for all 217 structures exported as C# code.

    void Endian();              // Built-in endian conversion method!

**Key Discovery**: The `FORM` struct has an **`Endian()` method**, confirming the game has built-in endian conversion for these fields.

### CHUNK Structure (6 bytes)

The subrecord header structure, found at PDB type 0x14dbc:

```c
struct CHUNK {
    uint32_t chunk;             // Offset 0: Subrecord type (signature as uint32)
    uint16_t length;            // Offset 4: Data size

    void Endian();              // Built-in endian conversion method!
};
```

### FILE_HEADER Structure (12 bytes)

The TES4 header data (HEDR subrecord), found at PDB type 0x1ed43:

```c
struct FILE_HEADER {
    float    fVersion;          // Offset 0: ESM version (1.32 Xbox, 1.34 PC)
    uint32_t iFormCount;        // Offset 4: Total form count
    uint32_t iNextFormID;       // Offset 8: Next available FormID

    void Endian();              // Built-in endian conversion method!
};
```

### TESFile Class (1068 bytes)

The main file handler class at PDB type 0x1d29d. Key fields relevant to conversion:

```c
class TESFile {
    // ... other fields ...

    FORM     m_currentform;             // Offset 576: Current record being processed
    uint32_t m_currentchunkID;          // Offset 600: Current subrecord type
    uint32_t m_actualChunkSize;         // Offset 604: Current subrecord size
    FILE_HEADER fileHeaderInfo;         // Offset 988: HEDR data (12 bytes)
    uint32_t m_Flags;                   // Offset 1000: File flags
    bool     bMustEndianConvert;        // Offset 665: Endian conversion flag!

    // Key methods:
    void SetLittleEndian(bool);         // Set endian mode
    bool GetLittleEndian();             // Query endian mode
    bool QEndian();                     // Query if endian swap needed
    void InitEndian();                  // Initialize endianness
    bool ReadFormHeader();              // Read record header
    bool ReadChunkHeader();             // Read subrecord header
};
```

**Key Discovery**: The `bMustEndianConvert` flag (offset 665) determines whether endian conversion is applied when reading.

### Implications for Conversion

1. **219 structures have `Endian()` methods** - The game handles endianness at the struct level
2. **Headers are always endian-converted** - FORM, CHUNK, and FILE_HEADER all have Endian methods
3. **`bMustEndianConvert` flag** - The TESFile class tracks whether conversion is needed
4. **Subrecord data conversion** - Must be done per-subrecord type (game knows the internal structure)

---

## References

### Sample Files

| File           | Location                                                         | Purpose                    |
| -------------- | ---------------------------------------------------------------- | -------------------------- |
| Xbox 360 ESM   | `Sample/ESM/360_final/`                                          | Input for conversion       |
| PC ESM         | `Sample/ESM/pc_final/`                                           | Reference for verification |
| Xbox 360 proto | `Sample/ESM/360_proto/`                                          | Earlier Xbox build         |
| Debug PDB      | `Sample/Fallout New Vegas (July 21, 2010)/FalloutNV/Fallout.pdb` | Structure definitions      |
| PDB Structures | `tools/EsmStructures.cs`                                         | Exported C# structures     |

### Related Documentation

- [Memory_Dump_Research.md](Memory_Dump_Research.md) - Memory dump analysis
- [Architecture.md](Architecture.md) - Project architecture overview
- [UESP ESM Format](https://en.uesp.net/wiki/Skyrim_Mod:Mod_File_Format) - Community documentation (Skyrim, similar format)
- [FO3/FNV ESM Format](https://en.uesp.net/wiki/Tes5Mod:Mod_File_Format) - Fallout 3/NV specifics

### Tools

| Tool        | Location            | Purpose                                   |
| ----------- | ------------------- | ----------------------------------------- |
| EsmAnalyzer | `tools/EsmAnalyzer` | ESM comparison, conversion, diff analysis |
| PdbAnalyzer | `tools/PdbAnalyzer` | Extract structures from cvdump PDB output |

### PDB-Verified Subrecord Layouts

The following subrecord schemas have been cross-referenced against the Fallout New Vegas PDB debug symbols (`Sample/PDB/Fallout_Debug/types_full.txt`). These are authoritative — they come from the actual game engine source, not community reverse-engineering.

#### ACTOR_BASE_DATA (ACBS - 24 bytes)

PDB struct `ACTOR_BASE_DATA` (type 0x1B92E), embedded in `TESActorBaseData` at offset 4.

| Offset | Type      | PDB Field Name    | Schema Field    | Notes                                    |
| ------ | --------- | ----------------- | --------------- | ---------------------------------------- |
| 0      | int32     | iActorBaseFlags   | Flags (UInt32)  | Actor flags bitmask                      |
| 4      | uint16    | usFatigue         | Fatigue         |                                          |
| 6      | uint16    | usBartergold      | BarterGold      |                                          |
| 8      | uint16    | sLevel            | Level (Int16)   | `s` prefix suggests signed intent        |
| 10     | uint16    | usCalcLevelMin    | CalcMin         |                                          |
| 12     | uint16    | usCalcLevelMax    | CalcMax         |                                          |
| 14     | uint16    | usSpeedMult       | SpeedMultiplier |                                          |
| 16     | **float** | **fKarma**        | KarmaAlignment  | **Was incorrectly UInt32 TemplateFlags** |
| 20     | uint16    | usBaseDisposition | Disposition     |                                          |
| 22     | uint16    | sTemplateUseFlags | TemplateFlags   |                                          |

**Schema correction**: Offset 16 was previously `UInt32("TemplateFlags")`. PDB confirms it is `float fKarma` (karma alignment value). Offset 22 is the actual `TemplateFlags`.

#### AIDATA (AIDT - 20 bytes)

PDB struct `AIDATA` (type 0x1AA0D), embedded in `TESAIForm` at offset 4.

| Offset | Type   | PDB Field Name  | Schema Field        | Notes                                                   |
| ------ | ------ | --------------- | ------------------- | ------------------------------------------------------- |
| 0      | uint8  | cAggression     | Aggression          |                                                         |
| 1      | uint8  | cConfidence     | Confidence          |                                                         |
| 2      | uint8  | cEnergy         | Energy              |                                                         |
| 3      | uint8  | cResponsibility | Responsibility      |                                                         |
| 4      | uint8  | cMood           | Mood                |                                                         |
| 5-7    | —      | (padding)       | Padding(3)          |                                                         |
| 8      | uint32 | iServiceFlags   | ServiceFlags        |                                                         |
| 12     | uint8  | cTrainingSkill  | TrainingSkill       | **Was incorrectly Int32 — caused byte-swap corruption** |
| 13     | uint8  | cTrainingLevel  | TrainingLevel       |                                                         |
| 14     | uint8  | cAssistance     | Assistance          |                                                         |
| 15     | bool   | bAggroRadius    | AggroRadiusBehavior |                                                         |
| 16     | uint32 | uiAggroRadius   | AggroRadius         | **Was incorrectly Int32("TrainLevel")**                 |

**Schema correction**: Offsets 12-15 were previously a single `Int32("TrainSkill")`, which reversed all four uint8 fields during conversion. Now correctly four individual `UInt8` fields (no byte-swap needed for single bytes).

#### EffectItemData (EFIT - 20 bytes)

PDB struct `EffectItemData` (type 0x626172).

| Offset | Type      | PDB Field Name | Schema Field | Notes                                                     |
| ------ | --------- | -------------- | ------------ | --------------------------------------------------------- |
| 0      | **int32** | **iMagnitude** | Magnitude    | **Was incorrectly Float — same swap, different semantic** |
| 4      | int32     | iArea          | Area         |                                                           |
| 8      | int32     | iDuration      | Duration     |                                                           |
| 12     | int32     | iRange         | Type         | MagicSystem::Range enum                                   |
| 16     | int32     | iActorValue    | ActorValue   | ActorValue::Index enum                                    |

**Schema correction**: Magnitude was `Float` but PDB confirms all five fields are `T_INT4`. Byte-swap is identical (both 4 bytes), but semantic interpretation differs — value 50 should display as integer 50, not as denormalized float 7.0e-44.

#### BGSProjectileData (PROJ DATA - 84 bytes)

PDB struct `BGSProjectileData` (type 0x867895). Confirms `iFlags` at offset 0 is a **single UInt32**, not two independent UInt16s. The `BGSProjectile::BGSProjectileFlags` enum packs behavior flags in bits 0-11 and motion type in bits 16-20.

**Key insight**: Xbox stores this as one big-endian UInt32. A 4-byte swap produces the correct result. Two independent 2-byte swaps would produce wrong results (flags and type would be transposed).

#### Other PDB-Confirmed Schemas (No Corrections Needed)

| Subrecord | PDB Struct            | Size | Status                                            |
| --------- | --------------------- | ---- | ------------------------------------------------- |
| WEAP DNAM | OBJ_WEAP              | 204  | Perfect match                                     |
| WEAP CRDT | OBJ_WEAP_CRITICAL     | 16   | Perfect match                                     |
| WEAP VATS | OBJ_WEAP_VATS_SPECIAL | 20   | Perfect match                                     |
| ENCH ENIT | EnchantmentItemData   | 16   | Flags confirmed as UInt8+3pad                     |
| ALCH ENIT | AlchemyItemData       | 20   | Flags confirmed as UInt8+3pad                     |
| EXPL DATA | BGSExplosionData      | 52   | Perfect match                                     |
| RACE DATA | RACE_DATA             | 36   | SkillBoost[7] = 7×(char,char)                     |
| CLAS DATA | CLASS_DATA            | 28   | cClassFlags is 1 byte + 3 pad (swapped as UInt32) |

### Version History

| Date       | Change                                                                   |
| ---------- | ------------------------------------------------------------------------ |
| 2026-02-09 | PDB cross-reference: verified ACBS, AIDT, EFIT, PROJ, WEAP, EXPL, etc.   |
| 2026-02-09 | Fixed AIDT schema (offsets 12-15: four uint8s, not one int32)            |
| 2026-02-09 | Fixed EFIT Magnitude type (Int32, not Float) per PDB                     |
| 2026-02-09 | Fixed FACT DATA (UInt32 flags, not ByteArray)                            |
| 2026-01-19 | Added PDB structure definitions for OBJ_WEAP, NavMesh, etc.              |
| 2026-01-19 | Documented TOFT streaming cache and INFO record duplication              |
| 2026-01-XX | Initial document creation from diff analysis                             |
| 2026-01-XX | Added structured pattern detection to diff tool                          |
| 2026-01-XX | Comprehensive analysis: NPC\_, WEAP, AMMO, ARMO, CREA, ALCH, ENCH, PERK  |
| 2026-01-XX | Documented DATA variants by record type, CTDA patterns, OBND, ENIT, etc. |
| 2026-02-09 | Mostly blanked pending rewrite.                                          |
