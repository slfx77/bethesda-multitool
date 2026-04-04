# Investigation: Cell-to-Worldspace Grouping in ESM Parser

## Status: RESOLVED — Not a Bug

**Date investigated**: 2026-03-30

## Original Problem

When generating cross-dump HTML comparison pages for cells, several worldspaces had seemingly high cell counts:

| Worldspace | Cell Count | Originally Expected |
|---|---|---|
| Lucky38World | 4,097 | ~20-50 |
| TheStripWorldNew | 4,097 | ~200-400 |
| WastelandNVmini | 4,097 | small |
| WastelandNVStrip | 4,139 (Xbox only) | ~200-400 |
| WastelandNV | 16,397 | ~25,000+ |

## Finding: Cell Counts Are Correct

Verified by comparing Xbox 360 and PC reference ESMs — both produce **identical** cell-to-worldspace distributions:

| FormID | EditorID | Xbox Cells | PC Cells |
|---|---|---|---|
| 0x000DA726 | WastelandNV | 16,397 | 16,397 |
| 0x0013B308 | TheStripWorldNew | 4,097 | 4,097 |
| 0x00148C05 | WastelandNVmini | 4,097 | 4,097 |
| 0x0016D714 | Lucky38World | 4,097 | 4,097 |
| 0x0011375B | WastelandNVStrip | 4,139 | (not present) |

### Root Cause of Confusion: Bethesda Child Worldspaces

Lucky38World, WastelandNVmini, TheStripWorldNew, and WastelandNVStrip are **child worldspaces** of WastelandNV. In Bethesda's engine, child worldspaces inherit the parent's exterior cell grid. Each child genuinely has ~4,097 CELL records in the ESM — they share the same grid coordinate space with different content/LOD.

Cells at coordinates like `(23, 31)` exist in **both** WastelandNV and Lucky38World simultaneously. This is expected behavior, not misattribution.

### GRUP Structure Verification

- The `SortedIntervalMap` correctly maps cells to worldspaces via Type 1 GRUP containment
- All 40 Type 1 GRUPs (20 worldspaces × 2 GRUPs each: persistent + exterior) are non-overlapping
- Each worldspace has two Type 1 GRUPs: a small one early in the file (~100-17K bytes, persistent cell) and a large one later (~1KB-145MB, exterior cells)
- No endianness or parsing issues — the Xbox and PC ESMs produce identical results

### Potential HTML UX Improvement

While there's no parsing bug, the cross-dump HTML could be improved for child worldspaces:
- Consider visually grouping child worldspaces under their parent (WastelandNV)
- Or adding a note indicating these are child worldspaces with inherited cell grids
- The WRLD record's parent worldspace FormID (WNAM subrecord) can identify the relationship
