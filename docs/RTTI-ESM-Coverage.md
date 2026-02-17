# RTTI Census to ESM Record Type Coverage Analysis

This document maps the C++ RTTI classes found in Xbox 360 memory dumps to their corresponding ESM record type signatures, tracks which types are currently reconstructed in the data browser, and documents binary asset extraction capabilities.

Generated from RTTI census across all 50 DMP files (9.5 GB total).
Census results: `TestOutput/rtti_census_all.json`

## TESForm-Derived Classes — RTTI to ESM Mapping

### Currently Reconstructed (40 types)

| RTTI Class | ESM Type | Handler | Instances | Dumps |
|---|---|---|---|---|
| TESNPC | NPC_ | ActorRecordHandler | 188,270 | 50/50 |
| TESCreature | CREA | ActorRecordHandler | 24,063 | 14/50 |
| TESRace | RACE | ActorRecordHandler | 10,927 | 32/50 |
| TESFaction | FACT | ActorRecordHandler | 14,098 | 14/50 |
| TESClass | CLAS | MiscRecordHandler | 660 | 14/50 |
| TESQuest | QUST | DialogueRecordHandler | 47,268 | 46/50 |
| TESTopicInfo | INFO | DialogueRecordHandler | ~120,000 | 50/50 |
| TESTopic | DIAL | DialogueRecordHandler | ~30,000 | 50/50 |
| BGSNote | NOTE | TextRecordHandler | 10,000+ | 50/50 |
| TESObjectBOOK | BOOK | TextRecordHandler | ~5,000 | 50/50 |
| BGSTerminal | TERM | TextRecordHandler | ~8,000 | 50/50 |
| Script | SCPT | ScriptRecordHandler | ~5,000 | 50/50 |
| TESObjectWEAP | WEAP | ItemRecordHandler | 47,411 | 50/50 |
| TESObjectARMO | ARMO | ItemRecordHandler | 16,684 | 50/50 |
| TESAmmo | AMMO | ItemRecordHandler | 10,285 | 50/50 |
| AlchemyItem | ALCH | ItemRecordHandler | 9,766 | 50/50 |
| TESObjectMISC | MISC | ItemRecordHandler | ~5,000 | 50/50 |
| TESKey | KEYM | ItemRecordHandler | ~2,000 | 50/50 |
| TESObjectCONT | CONT | ItemRecordHandler | ~5,000 | 50/50 |
| BGSPerk | PERK | EffectRecordHandler | 15,752 | 32/50 |
| SpellItem | SPEL | EffectRecordHandler | ~5,000 | 50/50 |
| EnchantmentItem | ENCH | EffectRecordHandler | ~3,000 | 50/50 |
| EffectSetting | MGEF | EffectRecordHandler | ~2,000 | 50/50 |
| BGSProjectile | PROJ | EffectRecordHandler | ~3,000 | 50/50 |
| BGSExplosion | EXPL | EffectRecordHandler | ~2,000 | 50/50 |
| TESObjectCELL | CELL | WorldRecordHandler | ~10,000 | 50/50 |
| TESWorldSpace | WRLD | WorldRecordHandler | ~100 | 50/50 |
| TESObjectACTI | ACTI | MiscRecordHandler | ~8,000 | 50/50 |
| TESObjectLIGH | LIGH | MiscRecordHandler | ~3,000 | 50/50 |
| TESObjectDOOR | DOOR | MiscRecordHandler | ~1,500 | 50/50 |
| TESObjectSTAT | STAT | MiscRecordHandler | ~15,000 | 50/50 |
| TESFurniture | FURN | MiscRecordHandler | ~1,500 | 50/50 |
| BGSMessage | MESG | TextRecordHandler | ~2,000 | 50/50 |
| Setting / GameSetting | GMST | MiscRecordHandler | ~2,000 | 50/50 |
| TESGlobal | GLOB | MiscRecordHandler | ~800 | 50/50 |
| TESObjectIMOD | IMOD | MiscRecordHandler | ~300 | 50/50 |
| BGSListForm | FLST | MiscRecordHandler | ~2,000 | 50/50 |
| TESRecipe | RCPE | MiscRecordHandler | ~200 | 50/50 |
| TESChallenge | CHAL | MiscRecordHandler | ~100 | 50/50 |
| TESReputation | REPU | MiscRecordHandler | ~100 | 50/50 |
| PackageData (TESPackage) | PACK | AiRecordHandler | 113,814 | 50/50 |
| TESLevItem/TESLevCreature/TESLevCharacter | LVLI/LVLC/LVLN | MiscRecordHandler | ~5,000 | 50/50 |

### Missing — To Be Added (26 types)

#### Phase 2 — Specialized Records (10 types with rich data)

| RTTI Class | ESM Type | Instances | Dumps | Key Subrecords |
|---|---|---|---|---|
| TESSound | SOUN | 29,978 | 14/50 | EDID, OBND, FNAM, SNDD(36 bytes) |
| BGSTextureSet | TXST | 14,333 | 32/50 | EDID, OBND, TX00-TX05, DNAM(2 bytes) |
| TESObjectARMA | ARMA | 3,621 | 32/50 | EDID, FULL, OBND, MODL/MOD2/MOD3/MOD4, DATA(12), DNAM(12) |
| TESWaterForm | WATR | 1,934 | 32/50 | EDID, FULL, NNAM, ANAM, FNAM, DNAM(196), GNAM(12), SNAM, DATA(2) |
| BGSBodyPartData | BPTD | 1,435 | 32/50 | EDID, MODL, BPTN/BPNN/BPNT/BPNI, NAM1/NAM4/NAM5, BPND |
| ActorValueInfo | AVIF | 2,380 | 32/50 | EDID, FULL, DESC, ICON, ANAM |
| TESCombatStyle | CSTY | 1,943 | 32/50 | EDID, CSTD, CSAD, CSSD |
| BGSLightingTemplate | LGTM | 722 | 32/50 | EDID, DATA(40 bytes) |
| NavMesh | NAVM | 1,067 | 28/50 | EDID, DATA(20/24), NVVX, NVTR, NVDP, NVGD |
| TESWeather | WTHR | 1,308 | 32/50 | EDID, IAD_, DNAM/CNAM/ANAM/BNAM, FNAM, PNAM, NAM0, INAM, SNAM, DATA |

#### Phase 1 — Generic Records (16 simple types)

| RTTI Class | ESM Type | Instances | Dumps | Key Subrecords |
|---|---|---|---|---|
| BGSMovableStatic | MSTT | 10,006 | 32/50 | EDID, FULL, MODL, OBND, DATA(1), SNAM |
| BGSCameraShot | CAMS | 7,453 | 32/50 | EDID, MODL, DATA(40) |
| TESImageSpaceModifier | IMAD | 5,575 | 32/50 | EDID, DNAM(244), IAD subrecords |
| TESObjectANIO | ANIO | 4,736 | 32/50 | EDID, MODL, DATA(FormId) |
| BGSImpactDataSet | IPDS | 3,460 | 32/50 | EDID, DATA(48) |
| TESLoadScreen | LSCR | 2,487 | 16/50 | EDID, ICON, DESC, LNAM, ONAM, XNAM |
| BGSTalkingActivator | TACT | 2,214 | 32/50 | EDID, FULL, MODL, OBND, SCRI, SNAM, VNAM |
| BGSRagdoll | RGDL | 1,149 | 32/50 | EDID, NVER, DATA(14), RAFD, RAPS, ANAM |
| TESEffectShader | EFSH | 1,020 | 32/50 | EDID, ICON, ICO2, NAM7, DATA(200-308) |
| TESObjectTREE | TREE | 305 | 32/50 | EDID, OBND, MODL, CNAM(32), BNAM(8), SNAM |
| MediaSet | MSET | 237 | 3/50 | EDID, FULL, NAM1-NAM9, HNAM, DATA |
| BGSAcousticSpace | ASPC | 192 | 3/50 | EDID, OBND, SNAM |
| TESCasinoChips | CHIP | 40 | 16/50 | EDID, FULL, OBND, MODL, ICON |
| TESCasino | CSNO | 40 | 16/50 | EDID, FULL, MODL, ICON, DATA(56) |
| BGSDefaultObjectManager | DOBJ | 2 | 1/50 | EDID, DATA(136) |
| BGSAddonNode | ADDN | 2 | 1/50 | EDID, OBND, MODL, DATA(4), DNAM, SNAM |

### Not in Memory Dumps (ESM-only types)

These ESM record types are defined in the engine but were not found in any of the 50 memory dumps:

| ESM Type | Description | Notes |
|---|---|---|
| EYES | Eyes | Freed after character gen |
| HAIR | Hair | Freed after character gen |
| HDPT | Head Part | Freed after character gen |
| IDLE | Idle Animation | May be freed or low count |
| IDLM | Idle Marker | May be freed or low count |
| IMGS | Image Space | Freed or no instances |
| IPCT | Impact | May be merged with IPDS |
| LAND | Landscape | Special handling via VHGT subrecords |
| NAVI | Navigation Info | Container record |
| PGRE | Placed Grenade | Reference, not base type |
| PMIS | Placed Missile | Reference, not base type |
| REFR | Placed Object | Reference, handled in WorldRecordHandler |
| ACHR | Placed NPC | Reference, handled in WorldRecordHandler |
| ACRE | Placed Creature | Reference, handled in WorldRecordHandler |
| REGN | Region | May be freed |
| SCOL | Static Collection | Unused in FNV |
| TXST | Texture Set | Present but handled in Phase 2 |

## Non-TESForm RTTI Classes (Gamebryo Engine)

The census also found 278 non-TESForm C++ classes (engine internals, Gamebryo/NIF runtime objects, etc). Key categories:

### Gamebryo Scene Graph (NiObject hierarchy)
- NiNode, NiTriShape, NiTriStrips, NiTriShapeData, NiTriStripsData
- NiTexture, NiSourceTexture, NiPixelData
- NiMaterialProperty, NiAlphaProperty, NiStencilProperty
- BSShaderPPLightingProperty, BSShaderNoLightingProperty
- ~80 total NiObject-derived classes

### Havok Physics
- bhkRigidBody, bhkCompressedMeshShape, bhkMoppBvTreeShape
- bhkSimpleShapePhantom, bhkConstraint variants
- ~30 total bhk* classes

### Engine Systems
- BSTaskManager, BSAudio, BSResource, BSStream
- TESDataHandler, TESObjectREFR (runtime refs)
- ~100 other internal classes

## Binary Asset Extraction Status

### Currently Working

| Asset Type | Scanner | Technique | Notes |
|---|---|---|---|
| Textures (DDS/DDX) | RuntimeTextureScanner | Heap scan for NiPixelData (116 bytes) + NiSourceTexture (72 bytes) | Extracts texture names + dimensions + format |
| Meshes (NIF geometry) | RuntimeGeometryScanner | Heap scan for NiTriShapeData (88 bytes) + NiTriStripsData (80 bytes) | Extracts vertex/triangle counts |
| Audio (XMA) | XmaParser + MemoryCarver | Signature-based carving of XMA2 chunks | Xbox 360 compressed audio |
| NIF files | MemoryCarver | NIF header magic bytes | Full NIF file carving |
| Lip sync (LIP) | MemoryCarver | Signature-based | Facial animation data |
| BIK video | MemoryCarver | Signature-based | Bink video chunks |

### Deferred Improvements

1. **Texture → TXST correlation**: Match carved textures to TextureSet records by path
2. **NIF → STAT/MSTT correlation**: Match carved NIF files to model paths from STAT/MSTT/ACTI records
3. **XMA → SOUN correlation**: Match carved audio to Sound records
4. **MSET → music file correlation**: Link MediaSet records to their audio tracks

## Census Summary

- **Total unique C++ classes**: 342
- **TESForm-derived classes**: 64
- **Total object instances**: 2,980,577 across 50 dumps
- **Build types**: 45 Release Beta, 3 Debug, 1 Release MemDebug, 1 Jacobstown
- **Currently reconstructed**: 40 ESM record types
- **Missing (to be added)**: 26 types (10 specialized + 16 generic)
- **After implementation**: Full ESM record parity for all TESForm types found in memory
