# Xbox 360 ESM to PC Conversion Transforms

This document tracks all identified structural and data transformations required to convert Xbox 360 ESM files to PC-compatible format.

---

## Status Key

- ✅ **Implemented** - Transformation is coded and working
- ⚠️ **Partial** - Partially implemented or has known issues
- ❌ **Not Implemented** - Identified but not yet coded
- 🔍 **Needs Investigation** - Suspected but not confirmed

---

## 1. Endianness Transformations

### 1.1 Record/Subrecord Signatures (4-byte reversal)

**Status:** ✅ Implemented

Xbox 360 uses big-endian byte order, so 4-character signatures are byte-reversed:

| PC (Little-endian) | Xbox 360 (Big-endian) | Hex (Xbox)    |
| ------------------ | --------------------- | ------------- |
| `TES4`             | `4SET`                | `34 53 45 54` |
| `GRUP`             | `PURG`                | `50 55 52 47` |
| `INFO`             | `OFNI`                | `4F 46 4E 49` |
| `LAND`             | `DNAL`                | `44 4E 41 4C` |
| `CELL`             | `LLEC`                | `4C 4C 45 43` |
| `WRLD`             | `DLRW`                | `44 4C 52 57` |
| `HEDR`             | `RDEH`                | `52 44 45 48` |
| `GMST`             | `TSMG`                | `54 53 4D 47` |
| `TRDT`             | `TDRT`                | `54 44 52 54` |
| `NAM1`             | `1MAN`                | `31 4D 41 4E` |

### 1.2 Multi-byte Field Swapping

**Status:** ✅ Implemented

All multi-byte numeric fields require endian swapping:

| Field Type | Size | Swap Pattern  |
| ---------- | ---- | ------------- |
| `uint16`   | 2    | Bytes 0↔1     |
| `int16`    | 2    | Bytes 0↔1     |
| `uint32`   | 4    | Bytes 0↔1↔2↔3 |
| `int32`    | 4    | Bytes 0↔1↔2↔3 |
| `float`    | 4    | Bytes 0↔1↔2↔3 |
| `FormID`   | 4    | Bytes 0↔1↔2↔3 |

### 1.3 Byte Arrays (No Swap)

**Status:** ✅ Implemented

These data types do NOT require swapping:

- Vertex normals (`VNML`) - byte[33×33×3]
- Vertex colors (`VCLR`) - byte[33×33×3]
- Height gradients in `VHGT` (after first float)
- ASCII/UTF-8 strings
- Raw byte flags

---

## 2. GRUP Structure Transformations

### 2.1 Interior Cell GRUP Hierarchy Reconstruction

**Status:** ✅ Implemented

**Problem:** Xbox 360 stores Cell Temporary Children GRUPs (type 9) at the top level. PC expects them nested inside proper Cell Block/Sub-block hierarchy under the top-level CELL GRUP.

**Xbox 360 Structure:**

```
GRUP Top "CELL"
  [empty or minimal]
...
GRUP Cell Temporary Children (FormID 0x00012345)  <- TOP LEVEL (wrong!)
GRUP Cell Temporary Children (FormID 0x00012346)  <- TOP LEVEL (wrong!)
```

**PC Expected Structure:**

```
GRUP Top "CELL"
  GRUP Interior Cell Block 0
    GRUP Interior Cell Sub-Block 0
      CELL record 0x00012345
      GRUP Cell Temporary Children (FormID 0x00012345)  <- Nested correctly
    GRUP Interior Cell Sub-Block 1
      CELL record 0x00012346
      GRUP Cell Temporary Children (FormID 0x00012346)
  GRUP Interior Cell Block 1
    ...
```

**Transform Logic:**

- Block number = `(FormID & 0xFFF) % 10`
- Sub-block number = `FormID % 10`
- Collect all interior cells during index pass
- Skip top-level Cell Temporary GRUPs during write
- Reconstruct proper hierarchy inside CELL GRUP

### 2.2 Exterior Cell GRUP Hierarchy (WRLD Children)

**Status:** ✅ Implemented

**Problem:** Similar to interior cells - Xbox stores exterior cell child groups at top level.

**Transform Logic:**

- Exterior cells have XCLC subrecord with grid coordinates (X, Y)
- Block number = `(X/32 << 16) | (Y/32 & 0xFFFF)` (signed division)
- Sub-block number = `(X/8 << 16) | (Y/8 & 0xFFFF)` (signed division)
- Cells grouped under WRLD → Block → Sub-Block hierarchy

### 2.3 Top-Level GRUP Ordering

**Status:** 🔍 Needs Investigation

PC ESM files may expect top-level GRUPs in a specific order. Current implementation preserves Xbox order.

---

## 3. Record Splitting/Merging Transformations

### 3.1 INFO Record Merging

**Status:** ✅ Implemented

**Problem:** Xbox 360 splits INFO (dialogue) records into TWO separate records with the SAME FormID:

**Xbox 360 (Split):**

```
INFO FormID: 0x000A471E @ offset1 (217 bytes)
  DATA, QSTI, PNAM, NAM3×3, CTDA×4, TCLT×3

INFO FormID: 0x000A471E @ offset2 (362 bytes)
  TRDT, NAM1, NAM2, TRDT, NAM1, NAM2, TRDT, NAM1, NAM2, NEXT
```

**PC (Merged):**

```
INFO FormID: 0x000A471E (587 bytes)
  DATA, QSTI
  TRDT, NAM1, NAM2, NAM3  <- Response group 1
  TRDT, NAM1, NAM2, NAM3  <- Response group 2
  TRDT, NAM1, NAM2, NAM3  <- Response group 3
  CTDA×3, TCLT×3          <- Conditions and choices
  SCHR, NEXT, SCHR        <- Script headers
```

**Evidence:**

- Xbox: 37,525 INFO records, avg 213 bytes
- PC: 23,247 INFO records, avg 425 bytes
- Ratio ≈ 1.61x (consistent with splitting)

**Required Transform:**

1. Build index of all INFO records by FormID
2. For each unique FormID with multiple records:
   - Find the "base" record (has DATA, QSTI, CTDA, TCLT, PNAM)
   - Find the "response" record (has TRDT, NAM1, NAM2)
   - Merge subrecords in correct order:
     - DATA, QSTI first
     - TRDT/NAM1/NAM2 groups (from response record) + NAM3 from base (one per response)
     - CTDA conditions
     - TCLT choices
     - SCHR/NEXT/SCTX/SCRO/SCDA script tails (if present)
     - Preserve other subrecords when possible
   - Drop PNAM (Xbox-only)
3. Write single merged record, skip response record in output

**Subrecord Order (PC):**

```
DATA → QSTI → [TRDT → NAM1 → NAM2 → NAM3]* → CTDA* → TCLT* → [SCHR → NEXT → SCHR]?
```

### 3.2 Other Potentially Split Records

**Status:** 🔍 Needs Investigation

Check if other record types are also split:

- DIAL (dialogue topics)?
- QUST (quests)?
- NPC\_ (actors)?

---

## 4. Xbox-Specific Records/Subrecords

### 4.1 OFST Subrecord (Offset Table)

**Status:** 🔍 Needs Investigation

**Location:** Found in WRLD records

**Observation:** Xbox 360 WRLD records contain `OFST` (or `TSFO` in big-endian) subrecords that appear to be file offset tables pointing to cell data.

**Hypothesis:** These may be pre-computed lookup tables for fast cell loading on Xbox 360. PC may not use them or may recompute them.

**Questions:**

- Is OFST required on PC?
- Do the offsets need recalculation after conversion?
- Should OFST be stripped or converted?

### 4.2 PNAM Subrecord in INFO

**Status:** 🔍 Needs Investigation

**Observation:** Xbox INFO records have `PNAM` (4 bytes, FormID) that PC records don't have.

**Questions:**

- What does PNAM reference?
- Should it be stripped or converted to something else?
- Is it redundant with data in the response record?

### 4.3 NAM3 Placement Difference

**Status:** ⚠️ Partial (part of INFO merge)

**Xbox:** NAM3 appears early in the base INFO record (after PNAM, before CTDA)
**PC:** NAM3 follows each NAM1/NAM2 pair as part of response data

This is handled as part of INFO record merging.

---

## 5. Compression Handling

### 5.1 Zlib Compression

**Status:** ✅ Implemented

Both platforms use zlib compression for large records (flag 0x00040000). The compression algorithm is identical - only the compressed data's multi-byte fields need endian swapping after decompression.

---

## 6. Data Validation

### 6.1 Record Count Verification

After conversion, verify record counts match expected values:

| Record Type | Xbox Count | PC Count | Relationship          |
| ----------- | ---------- | -------- | --------------------- |
| INFO        | 37,525     | 23,247   | ~1.6x (split records) |
| DIAL        | 18,215     | 18,215   | 1:1                   |
| CELL        | 30,497     | 30,497   | 1:1                   |
| LAND        | 29,363     | 29,363   | 1:1                   |

### 6.2 xEdit Compatibility Checks

Known xEdit errors to watch for:

| Error                                           | Cause                | Solution                   |
| ----------------------------------------------- | -------------------- | -------------------------- |
| "Cell Temporary Children could not be resolved" | GRUPs at wrong level | Reconstruct CELL hierarchy |
| "unexpected (or out of order) subrecord NAM3"   | INFO not merged      | Merge split INFO records   |
| "unexpected (or out of order) subrecord SCTX"   | SCTX without SCHR    | Add proper script headers  |

---

## 7. Implementation Priority

### Phase 1: Structure (Current Focus)

1. ✅ Endian conversion
2. ✅ GRUP hierarchy reconstruction (CELL, WRLD)
3. ❌ INFO record merging

### Phase 2: Data Integrity

4. 🔍 OFST handling
5. 🔍 PNAM handling
6. 🔍 Other split record types

### Phase 3: Validation

7. Record count verification
8. Full xEdit load test
9. In-game functionality test

---

## 8. Testing Methodology

### Quick Validation

```powershell
# Check record counts
dotnet run -- stats converted.esm

# Compare specific record
dotnet run -- find-formid converted.esm 0x000A471E

# Diff against PC reference
dotnet run -- diff converted.esm pc_reference.esm -t INFO -l 5
```

### xEdit Test

```powershell
# Load in xEdit and check for errors
.\FNVEdit.exe converted.esm
# Check log for "Error:" lines
```

---

## Changelog

| Date       | Change                                                 |
| ---------- | ------------------------------------------------------ |
| 2026-01-20 | Initial document creation                              |
| 2026-01-20 | Documented INFO split discovery (2 records per FormID) |
| 2026-01-20 | Added GRUP hierarchy reconstruction status             |
| 2026-01-20 | Added OFST/PNAM investigation notes                    |
