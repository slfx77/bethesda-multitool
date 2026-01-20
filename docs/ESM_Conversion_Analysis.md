# ESM Conversion Analysis Summary

## Key Finding (Revised): **Endianness + Structural Differences**

Analysis of Xbox 360 vs PC ESM files shows **endianness conversion is necessary but not sufficient**.
The Xbox 360 ESM uses a **different GRUP organization** (flat/streaming-oriented) than PC, and
simply swapping bytes does not always produce a GECK/xEdit-stable file.

- Xbox 360: Big-endian (PowerPC)
- PC: Little-endian (x86/x64)

### What Needs Conversion

| Data Type                        | Size     | Conversion Required               |
| -------------------------------- | -------- | --------------------------------- |
| Strings (EDID, FULL, DESC, etc.) | Variable | **None** - byte-by-byte identical |
| uint16 (flags, counts)           | 2 bytes  | Byte swap                         |
| uint32 (FormIDs, sizes, flags)   | 4 bytes  | Byte swap                         |
| int32 (signed integers)          | 4 bytes  | Byte swap                         |
| float (IEEE 754)                 | 4 bytes  | Byte swap                         |
| uint64 / double                  | 8 bytes  | Byte swap                         |

### Subrecord-Level Analysis

From hex dump comparisons:

**SNDD (Sound Data):**

```
Xbox 360: 03 F5 00 00  →  PC: F5 03 00 00  (uint16 swap)
Xbox 360: 00 00 10 10  →  PC: 10 10 00 00  (uint32 swap)
```

**SNAM (Sound FormID Reference):**

```
Xbox 360: 00 12 01 8E  →  PC: 8E 01 12 00  (uint32 FormID swap)
```

**DNAM (Weapon Data with floats):**

```
Xbox 360: 3F 80 00 00  →  PC: 00 00 80 3F  (float 1.0 swap)
Xbox 360: 3F 4C CC CD  →  PC: CD CC 4C 3F  (float 0.8 swap)
```

### Record Type Coverage

From 33,277 matching FormIDs compared:

| Status                           | Count  | Record Types                              |
| -------------------------------- | ------ | ----------------------------------------- |
| Identical (string-only records)  | 9,526  | KEYM, FURN, GLOB, HAIR, MICN, EYES, etc.  |
| Size different (version-related) | 8,135  | NPC\_, STAT, SCPT (PC patches added data) |
| Content different (endianness)   | 15,616 | SOUN, LVLI, SCPT, CONT, CREA, etc.        |

### Current Blocker: GECK/xEdit Reference Initialization Crash

After full-size conversion (byte swapping + TOFT stripping), GECK still crashes during
reference initialization. xEdit logs show hundreds of errors like:

```
GRUP Cell Temporary Children of [001206D8] <Error: Could not be resolved>
```

This strongly suggests **GRUP hierarchy reconstruction is required**:

- Xbox 360 uses a **flat** layout for Cell Temporary children and world blocks.
- PC tools expect nested structure: WRLD → World Children → Exterior Block → Exterior Sub-Block → Cell Children → Cell Temp.
- If the nesting and labels do not match PC expectations, FormID resolution fails and GECK crashes.

### Implications for BSA Repacker

1. **ESM Conversion is Required**
   - Cannot simply use Xbox 360 ESM with converted BSA assets
   - The game would crash reading big-endian values

2. **Conversion is Straightforward**
   - No structural changes needed
   - Just endianness byte-swapping per field
   - Need type information per subrecord (from UESP wiki or game code)

3. **Version Mismatch**
   - Xbox 360: v1.32, PC: v1.34
   - PC has patch data that Xbox 360 doesn't (131 new records)
   - For repacking: use PC ESM as base, convert assets only

### Recommended Approach for BSA Repacking

1. **Use PC ESM unmodified** (already little-endian, has patches)
2. **Convert BSA assets only**:
   - DDX → DDS textures ✅ (already implemented)
   - XMA → WAV/OGG audio ✅ (already implemented)
   - NIF → NIF (big-endian → little-endian) ✅ (already implemented)
   - XUI → PC-compatible (needs investigation)
3. **Update ESM paths if needed** (e.g., sound\fx\ambient\*.xma → \*.wav)

### Files Compared

| File                               | Size      | Endian | Version |
| ---------------------------------- | --------- | ------ | ------- |
| Sample\ESM\360_final\FalloutNV.esm | 234.78 MB | Big    | 1.32    |
| Sample\ESM\pc_final\FalloutNV.esm  | 234.27 MB | Little | 1.34    |

### Conclusion (Updated)

ESM conversion between Xbox 360 and PC requires **two layers**:

1. **Endianness conversion** (still required and well understood)
2. **Structure reconstruction** (GRUP nesting, world/cell hierarchy, OFST regeneration)

Going forward, the conversion must be informed by **Xbox 360 runtime parsing** from the PDB
and validated against **PC structure expectations** from xEdit/GECK:

- Use PDB symbols to understand how the Xbox 360 engine reads/organizes GRUPs.
- Use xEdit structure as the target layout for PC compatibility.
- Rebuild WRLD/Cell hierarchy during conversion (not just byte swapping).

Until that structure reconstruction is implemented, **byte-swapped output may load in xEdit but still crash GECK**.
