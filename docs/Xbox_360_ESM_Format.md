# Xbox 360 ESM Format Research

This document captures our current understanding of the Xbox 360 ESM (Elder Scrolls Master) format as used in Fallout: New Vegas, and how it differs from the PC format.

> **Status**: This format is **well understood**. Key differences are endianness, signature reversal, and some platform-specific flags.

---

## Table of Contents

1. [Overview](#overview)
2. [Key Differences from PC](#key-differences-from-pc)
3. [Header Conversion](#header-conversion)
4. [Record Structure](#record-structure)
5. [Subrecord Conversion Rules](#subrecord-conversion-rules)
6. [LAND Record Details](#land-record-details)
7. [Special Cases](#special-cases)
8. [Conversion Status](#conversion-status)
9. [Analysis Tool Usage](#analysis-tool-usage)
10. [References](#references)

---

## Overview

Xbox 360 ESM files use the same basic Bethesda plugin format as PC ESM files but with platform-specific byte ordering:

| Property         | Xbox 360        | PC            |
| ---------------- | --------------- | ------------- |
| Byte Order       | Big-endian      | Little-endian |
| Signature Format | Reversed 4-char | Normal 4-char |
| Record Count     | Varies          | Varies        |
| File Size        | ~246 MB         | ~245 MB       |
| Compression      | zlib (same)     | zlib (same)   |

### File Comparison Summary

| Field          | Xbox 360                    | PC                          |
| -------------- | --------------------------- | --------------------------- |
| TES4 Signature | `4SET` (bytes: 34 53 45 54) | `TES4` (bytes: 54 45 53 34) |
| HEDR Version   | 1.32                        | 1.34                        |
| NumRecords     | 544,224                     | 542,016                     |
| Flags          | 0x11 (ESM + Xbox flag)      | 0x01 (ESM only)             |
| Total Records  | 278,606                     | 465,234                     |

---

## Key Differences from PC

### 1. Signature Reversal

All 4-character record and subrecord signatures are byte-reversed:

| PC Signature | Xbox 360 Signature | Hex (PC)      | Hex (Xbox)    |
| ------------ | ------------------ | ------------- | ------------- |
| `TES4`       | `4SET`             | `54 45 53 34` | `34 53 45 54` |
| `GRUP`       | `PURG`             | `47 52 55 50` | `50 55 52 47` |
| `HEDR`       | `RDEH`             | `48 45 44 52` | `52 44 45 48` |
| `LAND`       | `DNAL`             | `4C 41 4E 44` | `44 4E 41 4C` |
| `GMST`       | `TSMG`             | `47 4D 53 54` | `54 53 4D 47` |
| `NPC_`       | `_CPN`             | `4E 50 43 5F` | `5F 43 50 4E` |
| `REFR`       | `RFER`             | `52 45 46 52` | `52 46 45 52` |
| `CELL`       | `LLEC`             | `43 45 4C 4C` | `4C 4C 45 43` |
| `WEAP`       | `PAEW`             | `57 45 41 50` | `50 41 45 57` |
| `EDID`       | `DIDE`             | `45 44 49 44` | `44 49 44 45` |
| `DATA`       | `ATAD`             | `44 41 54 41` | `41 54 41 44` |

### 2. Numeric Field Endianness

All multi-byte numeric fields are big-endian on Xbox 360:

| Field Type | Size    | Conversion              |
| ---------- | ------- | ----------------------- |
| uint16     | 2 bytes | Swap bytes              |
| int16      | 2 bytes | Swap bytes              |
| uint32     | 4 bytes | Swap bytes              |
| int32      | 4 bytes | Swap bytes              |
| FormID     | 4 bytes | Swap bytes              |
| float      | 4 bytes | Swap bytes (IEEE 754)   |
| double     | 8 bytes | Swap bytes (if present) |

### 3. String Data

**Strings are NOT endian-swapped** - they are identical between platforms:

| Subrecord Type | Content                | Conversion |
| -------------- | ---------------------- | ---------- |
| EDID           | Editor ID (string)     | **NONE**   |
| FULL           | Display name (string)  | **NONE**   |
| MODL           | Model path (string)    | **NONE**   |
| ICON/MICO      | Icon paths (string)    | **NONE**   |
| TX00/TX01/etc. | Texture paths (string) | **NONE**   |
| MWD1/MWD2/etc. | Weapon paths (string)  | **NONE**   |

### 4. Flags Differences

The TES4 header flags differ:

| Bit   | Name           | PC Value | Xbox 360 Value |
| ----- | -------------- | -------- | -------------- |
| Bit 0 | ESM flag       | 1        | 1              |
| Bit 4 | Xbox-specific? | 0        | 1              |

**Xbox 360 has flags = 0x11, PC has flags = 0x01**

---

## Header Conversion

### TES4 Record Header (24 bytes)

| Offset | Size | Field     | Xbox 360    | PC            | Conversion         |
| ------ | ---- | --------- | ----------- | ------------- | ------------------ |
| 0      | 4    | Signature | `4SET` (BE) | `TES4` (LE)   | Reverse bytes      |
| 4      | 4    | DataSize  | Big-endian  | Little-endian | Swap 4 bytes       |
| 8      | 4    | Flags     | 0x00000011  | 0x00000001    | Swap + clear bit 4 |
| 12     | 4    | FormID    | Big-endian  | Little-endian | Swap 4 bytes       |
| 16     | 4    | Timestamp | Big-endian  | Little-endian | Swap 4 bytes       |
| 20     | 2    | VCSInfo   | Big-endian  | Little-endian | Swap 2 bytes       |
| 22     | 2    | Version   | 15 (Xbox)   | 2 (PC)        | **Convert to 2?**  |

### HEDR Subrecord (12 bytes)

| Offset | Size | Field        | Xbox 360   | PC         | Conversion    |
| ------ | ---- | ------------ | ---------- | ---------- | ------------- |
| 0      | 4    | Version      | 1.32 (BE)  | 1.34 (LE)  | Swap + Update |
| 4      | 4    | NumRecords   | 544,224    | 542,016    | Swap + Update |
| 8      | 4    | NextObjectId | 0x0017B71C | 0xFF17BA48 | Swap + Update |

---

## Record Structure

### Generic Record Header (24 bytes)

```
[0-3]   Signature     - 4 bytes, reversed on Xbox
[4-7]   DataSize      - uint32, big-endian on Xbox
[8-11]  Flags         - uint32, big-endian on Xbox
[12-15] FormID        - uint32, big-endian on Xbox
[16-19] Timestamp     - uint32, big-endian on Xbox
[20-21] VCSInfo       - uint16, big-endian on Xbox
[22-23] Version       - uint16, big-endian on Xbox
```

### Subrecord Header (6 bytes)

```
[0-3]   Signature     - 4 bytes, reversed on Xbox
[4-5]   DataSize      - uint16, big-endian on Xbox
[6+]    Data          - varies by subrecord type
```

---

## Subrecord Conversion Rules

Based on analysis of actual records, here are the conversion rules by subrecord type:

### Simple Conversions (IDENTICAL)

These subrecords are **completely identical** between platforms:

| Subrecord | Description                 | Why Identical        |
| --------- | --------------------------- | -------------------- |
| EDID      | Editor ID                   | String data          |
| FULL      | Display name                | String data          |
| MODL      | Model path                  | String data          |
| ICON      | Icon path                   | String data          |
| MICO      | Small icon path             | String data          |
| TX00-TX07 | Texture paths               | String data          |
| MWD1-MWD7 | Weapon model paths          | String data          |
| VANM      | VATS attack name            | String data          |
| DNAM      | (some) Description/data     | String or byte array |
| FGGA      | Face morph geometry         | Byte array           |
| VNML      | Vertex normals (3267 bytes) | Byte array (33×33×3) |
| VCLR      | Vertex colors (3267 bytes)  | Byte array (33×33×3) |

### Simple Endian Swaps (4-byte)

These 4-byte subrecords just need a simple byte swap:

| Subrecord | Description        | Content       |
| --------- | ------------------ | ------------- |
| NAME      | Base object FormID | FormID        |
| CNAM      | Class FormID       | FormID        |
| RNAM      | Race FormID        | FormID        |
| TPLT      | Template FormID    | FormID        |
| VTCK      | Voice type FormID  | FormID        |
| HNAM      | Hair FormID        | FormID        |
| LNAM      | Lighting template  | FormID        |
| LTMP      | Light template     | FormID        |
| INAM      | Idle FormID        | FormID        |
| ENAM      | Eyes FormID        | FormID        |
| REPL      | Repair list FormID | FormID        |
| UNAM      | Use sound FormID   | FormID        |
| WNAM      | World FormID       | FormID        |
| YNAM      | Sound (pick up)    | FormID        |
| ZNAM      | Sound (put down)   | FormID        |
| XOWN      | Owner FormID       | FormID        |
| XEZN      | Encounter zone     | FormID        |
| XCAS      | Acoustic space     | FormID        |
| XCIM      | Image space        | FormID        |
| XCMO      | Music type         | FormID        |
| XCWT      | Water type         | FormID        |
| PKID      | Package FormID     | FormID        |
| PNAM      | Previous perk      | FormID        |
| NAM0      | FormID reference   | FormID        |
| NAM4      | FormID reference   | FormID        |
| NAM6      | FormID reference   | FormID        |
| NAM7      | FormID reference   | FormID        |
| NAM8      | FormID reference   | FormID        |
| NAM9      | FormID reference   | FormID        |
| HCLR      | Hair color         | uint32        |
| ETYP      | Equipment type     | uint32        |
| VNAM      | Voice type         | uint32/FormID |
| SNAM      | Sound (weapon)     | FormID        |
| WMI1-3    | Weapon mod FormIDs | FormID        |
| WMS1-2    | Weapon mod sounds  | FormID        |
| WNM1-7    | Weapon mod names   | FormID        |
| XNAM      | Faction rank       | FormID        |
| TNAM      | Target FormID      | FormID        |

### Simple Endian Swaps (2-byte)

| Subrecord | Description     | Content |
| --------- | --------------- | ------- |
| NAM5      | Short value     | uint16  |
| EAMT      | Enchantment amt | uint16  |

### Complex Subrecords (Mixed Fields)

These subrecords contain multiple fields with different types. Pattern notation:

- `NB same` = N identical bytes
- `NB swap` = N-byte endian swap
- `N×MB swap` = N consecutive M-byte swaps

### DATA Subrecord Patterns by Record Type

The DATA subrecord has different structures depending on the parent record type:

| Record | Size | Pattern                            | Description                    |
| ------ | ---- | ---------------------------------- | ------------------------------ |
| NPC\_  | 11   | `4B swap + 7B same`                | Base health + 7 stat bytes     |
| WEAP   | 15   | `3×4B swap + 2B swap + 1B same`    | Value, health, weight + flags  |
| AMMO   | 13   | `4B swap + 4B same + 4B swap + 1B` | Value, flags, weight, AR       |
| QUST   | 8    | `4B same + 4B swap`                | Flags + float priority         |
| REFR   | 24   | `6×4B swap`                        | Position + rotation (6 floats) |
| ACHR   | 24   | `6×4B swap`                        | Position + rotation (6 floats) |
| CELL   | 1    | `1B same`                          | Single flags byte              |
| CREA   | 17   | `4B same + 4B swap + 2B swap + 7B` | Type, combat, magic, stealth   |
| MGEF   | 72   | `5×4B swap + 8B same + 11×4B swap` | Complex effect data            |
| RACE   | 36   | `16B same + 5×4B swap`             | Skill bytes + floats           |
| ARMO   | 12   | `3×4B swap`                        | Value, health, weight          |
| ALCH   | 4    | `4B swap`                          | Value (int32)                  |
| PERK   | 3-5  | `NB same`                          | Small flag bytes (identical)   |

### Other Common Subrecord Patterns

| Subrecord | Record | Size | Pattern                        | Description               |
| --------- | ------ | ---- | ------------------------------ | ------------------------- |
| ENIT      | ENCH   | 16   | `3×4B swap + 4B same`          | Type, charge, cost, flags |
| ENIT      | ALCH   | 20   | `4B swap + 12B same + 4B swap` | Value, flags, weight      |
| EFIT      | any    | 20   | `5×4B swap` (full swap)        | Effect magnitude/area/dur |
| EFID      | any    | 4    | `4B swap`                      | Effect FormID             |
| NIFT      | CREA   | var  | `4B swap + (N-4)B same`        | Count + face morph data   |
| EPFD      | PERK   | 4    | `4B swap`                      | Entry point data (float)  |

#### ACBS (24 bytes) - Actor Base Stats

Pattern: `4B same + 10×2B swap` or `4B swap + 6×2B swap + 4B swap + 2×2B swap`

```
Xbox: 00 00 00 00 00 32 00 00 00 01 00 00 00 00 00 64 00 00 00 00 00 23 01 D7
PC:   00 00 00 00 32 00 00 00 01 00 00 00 00 00 64 00 00 00 00 00 23 00 D7 01
```

#### DATA (NPC\_ - 11 bytes)

Pattern: `4B swap + 7B same`

```
Xbox: 00 00 00 32 05 05 05 05 05 05 05
PC:   32 00 00 00 05 05 05 05 05 05 05
      |--int32-| |---7 stat bytes---|
```

#### DATA (QUST - 8 bytes)

Pattern: `4B same + 4B swap`

```
Xbox: 00 37 61 6B 3D CC CC CD
PC:   00 37 61 6B CD CC CC 3D
      |--flags-| |---float---|
```

#### DATA (AMMO - 13 bytes)

Pattern: `4B swap + 4B same + 4B swap + 1B same`

#### DATA (WEAP - 15 bytes)

Pattern: `3×4B swap + 2B swap + 1B same`

```
Xbox: 00 00 00 64 00 00 02 58 40 C0 00 00 00 1E 0C
PC:   64 00 00 00 B0 04 00 00 00 00 C0 40 32 00 0C
      |--int32-| |--int32-| |--float-| |i16| |b|
```

#### DATA (REFR - 24 bytes)

Pattern: `6×4B swap` (position XYZ + rotation XYZ as floats)

#### CNTO (8 bytes) - Container Item

Pattern: `2×4B swap`

```
Bytes 0-3: FormID (swap 4)
Bytes 4-7: Count int32 (swap 4)
```

#### SNAM (NPC\_ - 8 bytes) - Sound/Faction

Pattern: `4B swap + 4B same`

```
Xbox: 00 13 F8 A4 00 4F 44 4C
PC:   A4 F8 13 00 00 4F 44 4C
      |--FormID-| |--flags--|
```

#### DNAM (WEAP - 204 bytes) - Weapon Stats

Pattern: `4B same + 2×4B swap + 4B same + 5×4B swap + 8B same + 30×4B swap + 4B same + 9×4B swap`

- Complex mix of flags (identical) and floats/ints (swapped)

#### XCLL (40 bytes) - Cell Lighting

Pattern: `10×4B swap`

- All floats/colors, each 4-byte group swapped

#### VATS (20 bytes) - VATS Data

Pattern: `5×4B swap` (varies - sometimes identical if zeros)

#### DAT2 (AMMO - 20 bytes)

Pattern: `8B same + 4B swap + 4B same + 4B swap`

#### CRDT (16 bytes) - Critical Data

Pattern: `4B swap + 4B swap + 8B same`

```
Xbox: 00 16 00 00 3F 80 00 00 01 00 00 00 00 00 00 00
PC:   16 00 00 00 00 00 80 3F 01 00 00 00 00 00 00 00
      |--int32-| |--float--| |------padding------|
```

#### OBND (12 bytes) - Object Bounds

Pattern: `6×2B swap`

```
Xbox: FF FB FF C8 00 00 00 05 00 38 00 A6
PC:   FB FF C8 FF 00 00 05 00 38 00 A6 00
      |X1--|  |Y1--|  |Z1--|  |X2--|  |Y2--|  |Z2--|
```

- 6× int16, each needs 2-byte endian swap
- Represents bounding box: (X1,Y1,Z1) to (X2,Y2,Z2)

#### DATA (MGEF - 72 bytes) - Magic Effect

Pattern: `5×4B swap + 8B same + 11×4B swap`

- Complex structure with FormIDs and floats interspersed with flags

#### DATA (RACE - 36 bytes)

Pattern: `16B same + 5×4B swap`

- First 16 bytes are flags/byte arrays, remaining 20 bytes are 5 floats

#### BMDT (ARMO - 8 bytes) - Biped Model

Pattern: `4B swap + 4B same`

- First 4 bytes: biped flags (uint32 swap)
- Last 4 bytes: general flags (identical)

#### MO\*S (ARMO - variable) - Model Shader Data

Pattern: `4B swap + (N-4)B same`

- First 4 bytes: count or flags (uint32 swap)
- Remaining bytes: identical (hashes or string data)

Example (MO2S, MO3S, MO4S - 28 bytes):

```
Xbox: 00 00 00 02 04 00 00 00 47 4F 3A 30 ...
PC:   02 00 00 00 04 00 00 00 47 4F 3A 30 ...
      |--count-| |--------string data--------|
```

**Warning**: Some ARMO records have significantly different MO\*S content - may require deep inspection.

#### CTDA (28 bytes) - Condition Data

Multiple patterns observed depending on condition type:

**Pattern A**: `4B same + 4B swap + 2×2B swap + 4×4B swap`

```
Type[4] | CompValue[4,swap] | Type[2,swap] | Func[2,swap] | Params[4×4,swap]
```

**Pattern B**: `8B same + 2×2B swap + 4×4B swap`

```
8 bytes identical (type+flags zeros) | Type[2,swap] | Func[2,swap] | Params[4×4,swap]
```

**Pattern C**: `8B same + 6×2B swap + 2×4B swap`

- Alternate layout for certain function types

Structure breakdown:

```
Offset  0-3:  Type flags (uint32) - sometimes same, sometimes swap
Offset  4-7:  Comparison value (float) - 4-byte swap (or zeros = same)
Offset  8-9:  Comparison type - 2-byte swap
Offset 10-11: Function index (uint16) - 2-byte swap
Offset 12-15: Parameter 1 (uint32/FormID) - 4-byte swap
Offset 16-19: Parameter 2 (uint32) - 4-byte swap
Offset 20-23: Run-on (uint32) - 4-byte swap
Offset 24-27: Reference (uint32) - 4-byte swap
```

**Note**: CTDA patterns vary based on whether initial bytes are zero. When both type flags and comparison value are zero, they appear identical.

---

## LAND Record Details

LAND records contain terrain data and have specific conversion requirements:

### LAND Subrecords

| Subrecord | Size       | Content              | Conversion                         |
| --------- | ---------- | -------------------- | ---------------------------------- |
| DATA      | 4 bytes    | Flags                | 4-byte endian swap                 |
| VNML      | 3267 bytes | Vertex normals       | **NONE** (byte array)              |
| VCLR      | 3267 bytes | Vertex colors        | **NONE** (byte array)              |
| VHGT      | 1096 bytes | Height data          | Float swap + zero trailing 3 bytes |
| ATXT      | 8 bytes    | Alpha texture        | Complex (see below)                |
| BTXT      | 8 bytes    | Base texture         | Same as ATXT                       |
| VTXT      | 8×N bytes  | Vertex texture blend | Complex (see below)                |

### ATXT/BTXT Structure (8 bytes)

```
Offset 0-3: FormID (texture reference) - 4-byte endian swap
Offset 4:   Quadrant (0-3) - NO SWAP
Offset 5:   Platform byte - Xbox=0x00, PC=0x88 (unnamed in PDB)
Offset 6-7: Layer (uint16) - 2-byte endian swap
```

**PDB Research Finding**: Extensive analysis of `Fallout.pdb` found **no named structure** for the ATXT file format. The game reads ATXT data directly by offset without a formal struct definition. xEdit labels this byte as `wbUnused(1)`. The byte consistently differs between platforms (Xbox=0x00, PC=0x88) but its purpose is unknown. For conversion, we set it to 0x88 to match PC files.

### VTXT Entry Structure (8 bytes each)

```
Offset 0-1: Position index (uint16) - 2-byte endian swap
Offset 2-3: Unknown flags - NO SWAP (always FF FF)
Offset 4-7: Opacity (float) - 4-byte endian swap
```

### VHGT Structure (1096 bytes)

```
Offset 0-3:      Height offset (float) - 4-byte endian swap
Offset 4-1092:   Gradient data (1089 signed bytes, 33×33 grid) - NO SWAP
Offset 1093-1095: Padding (3 bytes) - Zero out for PC format
```

**Note**: The trailing 3 bytes at offset 1093-1095 are platform-specific. Xbox 360 has arbitrary data here. PC has varying values - some records have `[0, 39, 0]`, others have `[0, 0, 0]`. For conversion, we zero them as some PC records have all zeros anyway.

---

## Special Cases

### AIDT Subrecord Differences

Some AIDT (AI Data) subrecords have different content beyond just endianness:

```
Xbox: 00 02 32 32 00 24 20 A2 00 00 00 00 00 00 01 00 00 00 00 00
PC:   00 02 32 32 00 00 00 00 00 00 00 00 00 00 01 00 00 00 00 00
                    ^^^^^^^^^^^
```

Bytes 5-7 differ - may be Xbox-specific data that should be zeroed

### Records Only in One Version

Some records exist only in PC or Xbox versions:

- **EITM** (Enchantment): Sometimes only in PC (e.g., WEAP FormID 0x00176E5D)

### FGGS/FGTS (Face Geometry)

For some NPCs, these 200-byte face morphing subrecords are:

- IDENTICAL for simple NPCs
- ENDIAN-SWAPPED for complex NPCs

This suggests they may contain floats that need conditional handling.

### OFST Subrecord (WRLD Cell Offset Table)

WRLD (Worldspace) records contain an OFST subrecord that provides file offsets to exterior cells for fast random access.

**Structure:**

```
OFST: Array of uint32 file offsets
      Each index maps to a cell grid location
      Index = (Y + halfHeight) * gridWidth + (X + halfWidth)
```

**Size Example (Mojave Wasteland - FormID 0x000DA726):**

- **177,112 bytes** = 44,278 entries × 4 bytes
- 16,396 non-zero entries (actual cell offsets)
- Remaining zeros = grid positions without exterior cells

**Conversion Handling:**

The OFST offsets point to file positions of CELL records. After conversion:

1. File structure changes (signature sizes, data reordering)
2. CELL records move to different file offsets
3. Original OFST values become invalid

**Current Implementation: Strip OFST**

The converter **strips OFST subrecords** from WRLD records during conversion:

- 14 WRLD records × varying OFST sizes = ~251 KB stripped
- The game works correctly without OFST (uses cell scanning instead)
- This is simpler than regenerating OFST with new file offsets

**Alternative: Regenerate OFST (Not Implemented)**

A more complete conversion would regenerate OFST:

1. Track file offset of each exterior CELL as it's written
2. Extract grid coordinates (X, Y) from XCLC subrecord
3. Build new OFST array with converted file offsets
4. Write OFST subrecord with new data

This requires a two-pass conversion or offset tracking during the single pass.

---

## Conversion Status

### ✅ Fully Understood

| Aspect             | Status | Notes                                   |
| ------------------ | ------ | --------------------------------------- |
| Signature reversal | ✅     | All 4-byte signatures reversed          |
| Header conversion  | ✅     | 24-byte record headers                  |
| Subrecord headers  | ✅     | 6-byte with size swap                   |
| String subrecords  | ✅     | IDENTICAL, no conversion                |
| FormID subrecords  | ✅     | Simple 4-byte swap                      |
| LAND subrecords    | ✅     | Mixed handling per subrecord type       |
| CELL records       | ✅     | Mostly simple FormID swaps + XCLL       |
| REFR records       | ✅     | DATA (positions) + NAME (FormID)        |
| ACHR records       | ✅     | Same as REFR                            |
| TXST records       | ✅     | IDENTICAL (all strings)                 |
| GMST records       | ✅     | Strings identical, numeric data swapped |
| NPC\_ records      | ✅     | ACBS, SNAM, DATA patterns documented    |
| CREA records       | ✅     | Similar to NPC\_ with DATA variant      |
| WEAP records       | ✅     | DATA, DNAM complex patterns documented  |
| ARMO records       | ✅     | BMDT, DATA, MO\*S patterns documented   |
| AMMO records       | ✅     | DATA, DAT2 patterns documented          |
| STAT records       | ✅     | Simple OBND + string data               |
| RACE records       | ✅     | DATA, FGGS/FGTS patterns documented     |
| ALCH records       | ✅     | ENIT, EFIT, CTDA patterns documented    |
| ENCH records       | ✅     | ENIT pattern documented                 |
| PERK records       | ✅     | CTDA, EPFD patterns documented          |
| MGEF records       | ✅     | DATA complex pattern documented         |
| CTDA (conditions)  | ✅     | Multiple patterns documented            |
| OBND (bounds)      | ✅     | `6×2B swap` pattern                     |

### ⚠️ Partially Understood

| Aspect               | Status | Notes                                       |
| -------------------- | ------ | ------------------------------------------- |
| TES4 flags bit 4     | ⚠️     | Xbox=0x11, PC=0x01 - clear on conversion?   |
| ATXT byte 5          | ⚠️     | Xbox=0x00, PC=0x88 - platform-specific flag |
| AIDT bytes 5-7       | ⚠️     | Extra data on Xbox, zeroed on PC            |
| Header version field | ⚠️     | Xbox=15, PC=2 - relationship unclear        |
| SNAM (CREA)          | ⚠️     | Some have content diffs beyond endianness   |

### ❌ Not Yet Analyzed

| Aspect                  | Status | Notes                                        |
| ----------------------- | ------ | -------------------------------------------- |
| Dialogue records (INFO) | ⚠️     | Xbox has 2x count due to TOFT duplication    |
| TOFT record internals   | ⚠️     | Skipped during conversion (streaming cache)  |
| Quest records (QUST)    | ⚠️     | Xbox has 431, PC has 436                     |
| Script records (SCPT)   | ❌     | Compiled bytecode differences                |
| Navigation mesh (NAVM)  | ⚠️     | PDB structures known, need full validation   |
| INFO-only subrecords    | ❌     | NAM1/TRDT/SCHR only on PC; PNAM only on Xbox |
| DOOR records            | ❌     | Count mismatch (320 vs 1) - structure issue  |

### 🔍 Record Count Anomalies

| Record Type | Xbox 360 | PC       | Ratio  | Notes                            |
| ----------- | -------- | -------- | ------ | -------------------------------- |
| INFO        | 46,494   | 23,247   | 2.00x  | TOFT streaming cache (see below) |
| TOFT        | 23,644   | 0        | N/A    | Xbox-only streaming cache        |
| DOOR        | 320      | 1        | 320x   | Major structural difference      |
| STAT        | 6,765    | 6,785    | 0.997x | Slight difference                |
| QUST        | 431      | 436      | 0.988x | Minor difference                 |
| ENCH        | 142      | 145      | 0.979x | 3 extra on PC                    |
| MGEF        | 287      | 289      | 0.993x | 2 extra on PC                    |
| ACHR        | 3,390    | 3,386    | 1.001x | 4 extra on Xbox                  |
| NPC\_       | 3,816    | 3,816    | 1.00x  | Exact match                      |
| REFR        | 100,000+ | 100,000+ | ~1.00x | Large count, match               |
| LAND        | 29,363   | 29,363   | 1.00x  | Exact match                      |
| CELL        | 30,497   | 30,497   | 1.00x  | Exact match                      |
| WEAP        | 260      | 261      | 0.996x | One less on Xbox                 |

### TOFT - Xbox 360 Streaming Cache Record (Xbox 360 Only)

The Xbox 360 ESM contains a special **TOFT** record type that does not exist on PC.

**PDB Evidence:**

- `TOFT_ID = 80` in the `ENUM_FORM_ID` enumeration (alongside all other record types like TES4_ID, GRUP_ID, INFO_ID, etc.)
- No `TESToft`, `OBJ_TOFT`, or similar class definition exists in the PDB
- No structure with `Endian()` method for TOFT
- This suggests TOFT is a **container/wrapper record** rather than a data record with its own fields

#### Two Types of TOFT Records

Analysis of FalloutNV.esm reveals **23,644 TOFT records** that fall into two categories:

| Type               | Count  | Size        | Location                                                 |
| ------------------ | ------ | ----------- | -------------------------------------------------------- |
| **Marker TOFT**    | 23,643 | 0 bytes     | End of Cell Persistent groups (depth > 0)                |
| **Container TOFT** | 1      | 1,244,443 B | Top-level at `0x01AB5A3D`, followed by bare INFO records |

**Marker TOFTs (size=0):**

- Appear at the **end of Cell Persistent groups** as 24-byte headers with no data
- Example: TOFT at `0x00D8ECDF` with FormID `0x01FB9CDC`, size 0
- FormID appears to reference a streaming TOC entry for that cell
- These are interspersed throughout the CELL region (`0x00D8E7A0` to `0x01BE5770`)

**Container TOFT (size=1.2 MB):**

- Single record at offset `0x01AB5A3D` with FormID `0xFFFFFFFE`
- Contains **pre-serialized INFO records** using standard record header format
- Data inside: `OFNI` (INFO), subrecords like `TDRT` (TRDT), `1MAN` (NAM1 with dialogue text)
- Example embedded dialogue: "Shut up and keep going.", "Hello again. Remember to stop by..."

**Bare INFO Records (after Container TOFT):**

- Following the Container TOFT, ~14,278 INFO records appear at depth 0 (outside any GRUP)
- These span from `0x01BE5788` to `0x01F9AC28`
- They are **duplicates** of INFO records already present in DIAL groups
- This explains the 2x INFO count: 23,247 in DIAL groups + 14,278 duplicates = 37,525 total

**Purpose (Inferred):**

- Pre-serialized streaming cache for dialogue system on Xbox 360
- Marker TOFTs may provide random-access indices into cell dialogue
- Container TOFT + bare INFO records allow fast sequential streaming without parsing DIAL group hierarchy
- The name "Topic Offset File Table" was previously assumed but not confirmed by PDB

**Conversion Handling:**

- **Marker TOFTs (size=0):** Skip individual records during conversion (they serve no purpose on PC)
- **Container TOFT + bare INFO region:** When TOFT appears at top-level (`grupStack.Count == 0`):
  - Skip all records until the next `GRUP` signature is found
  - This skips ~5.1 MB of streaming cache data
  - Resume normal conversion at the next GRUP (Cell Temporary groups containing LAND/NAVM data)
- The duplicated INFO records serve no purpose on PC (streaming optimization only)

---

## Analysis Tool Usage

### EsmAnalyzer Commands

```powershell
# Compare TES4 headers byte-by-byte
dotnet run -- diff-header xbox.esm pc.esm

# Compare specific record type (limit 5 records)
dotnet run -- diff xbox.esm pc.esm -t LAND -l 5
dotnet run -- diff xbox.esm pc.esm -t NPC_ -l 3
dotnet run -- diff xbox.esm pc.esm -t WEAP -l 3

# Compare specific FormID
dotnet run -- diff xbox.esm pc.esm -f 0x000D7055

# Get statistics
dotnet run -- stats esm_file.esm

# Compare overall statistics
dotnet run -- compare xbox.esm pc.esm
```

### Sample Paths

```powershell
$xbox = "Sample/ESM/360_final/FalloutNV.esm"
$pc = "Sample/ESM/pc_final/FalloutNV.esm"
```

---

## Conversion Algorithm

### High-Level Steps

1. **Read Xbox 360 ESM** (big-endian)
2. **Process TES4 header**:
   - Reverse signature: `4SET` → `TES4`
   - Swap DataSize, FormID, Timestamp
   - Clear flag bit 4: `0x11` → `0x01`
   - Update version fields as needed
3. **For each GRUP**:
   - Reverse signature: `PURG` → `GRUP`
   - Swap size and metadata fields
4. **For each Record**:
   - Reverse signature
   - Swap header fields (DataSize, Flags, FormID, etc.)
   - Decompress if compressed (zlib - same algorithm)
   - Process each subrecord
5. **For each Subrecord**:
   - Reverse signature
   - Swap size field
   - Apply type-specific data conversion (see rules above)
6. **Write PC ESM** (little-endian)

### Pseudo-code

```csharp
void ConvertRecord(Record xbox, BinaryWriter pc)
{
    // Write header
    pc.Write(ReverseSignature(xbox.Signature));
    pc.Write(SwapEndian(xbox.DataSize));
    pc.Write(SwapEndian(xbox.Flags) & ~0x10);  // Clear Xbox flag
    pc.Write(SwapEndian(xbox.FormID));
    pc.Write(SwapEndian(xbox.Timestamp));
    pc.Write(SwapEndian(xbox.VCSInfo));
    pc.Write(SwapEndian(xbox.Version));

    // Process subrecords
    byte[] data = xbox.IsCompressed
        ? Decompress(xbox.Data)
        : xbox.Data;

    foreach (var sub in ParseSubrecords(data, bigEndian: true))
    {
        pc.Write(ReverseSignature(sub.Signature));
        pc.Write(SwapEndian(sub.Size));
        pc.Write(ConvertSubrecordData(sub));
    }
}
```

---

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

};

````

**Key Discovery**: The `FORM` struct has an **`Endian()` method**, confirming the game has built-in endian conversion for these fields.

### CHUNK Structure (6 bytes)

The subrecord header structure, found at PDB type 0x14dbc:

```c
struct CHUNK {
    uint32_t chunk;             // Offset 0: Subrecord type (signature as uint32)
    uint16_t length;            // Offset 4: Data size

    void Endian();              // Built-in endian conversion method!
};
````

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

### Version History

| Date       | Change                                                                   |
| ---------- | ------------------------------------------------------------------------ |
| 2026-01-19 | Added PDB structure definitions for OBJ_WEAP, NavMesh, etc.              |
| 2026-01-19 | Documented TOFT streaming cache and INFO record duplication              |
| 2026-01-XX | Initial document creation from diff analysis                             |
| 2026-01-XX | Added structured pattern detection to diff tool                          |
| 2026-01-XX | Comprehensive analysis: NPC\_, WEAP, AMMO, ARMO, CREA, ALCH, ENCH, PERK  |
| 2026-01-XX | Documented DATA variants by record type, CTDA patterns, OBND, ENIT, etc. |

---

## Contributing

When analyzing new record types:

1. **Use the diff command** to compare Xbox vs PC versions
2. **Document field layouts** - offsets, sizes, types
3. **Note IDENTICAL vs ENDIAN-SWAPPED** patterns
4. **Use structured pattern notation** - `NB same`, `NB swap`, `N×MB swap`
5. **Flag any anomalies** - values that differ beyond endianness
6. **Update this document** with findings
