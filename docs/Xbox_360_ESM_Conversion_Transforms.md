# Xbox 360 ESM to PC Conversion Transforms

This document tracks all identified structural and data transformations required to convert Xbox 360 ESM files to PC-compatible format.

---

## Changelog

| Date       | Change                                                                                                |
| ---------- | ----------------------------------------------------------------------------------------------------- |
| 2026-01-20 | Initial document creation                                                                             |
| 2026-01-20 | Documented INFO split discovery (2 records per FormID)                                                |
| 2026-01-20 | Added GRUP hierarchy reconstruction status                                                            |
| 2026-01-20 | Added OFST/PNAM investigation notes                                                                   |
| 2026-02-09 | Added PDB-verified schema corrections (Section 5)                                                     |
| 2026-02-09 | Added known Xbox/PC content differences (Section 6)                                                   |
| 2026-02-09 | Updated all status markers to reflect current state                                                   |
| 2026-02-09 | Updated testing methodology with semdiff commands                                                     |
| 2026-02-09 | Fixed PKPT/PKDD/PKW3/DNAM TERM schema bugs (Sections 5.4-5.7)                                         |
| 2026-02-09 | Documented PACK_LOC_FILE vs PackageLocation discrepancy resolution (Section 5.8)                      |
| 2026-02-09 | Added PACK/QUST/CELL/NAVM content difference docs (Sections 6.4-6.7)                                  |
| 2026-02-09 | PDB final build validation: 219 structs with Endian(), no changes vs prototype                        |
| 2026-02-09 | Fixed NVDP padding swap bug (Section 6.7): NAVM diffs 329→303                                         |
| 2026-02-09 | TOFT streaming cache analysis (Section 6.8): confirmed redundant, correctly skipped                   |
| 2026-02-09 | Endian() coverage audit complete (Section 6.10): 214 structs, 0 gaps in FNV ESM                       |
| 2026-02-09 | End-to-end converter verification (Section 6B): NIF, KF, DDX, XMA all verified                        |
| 2026-02-09 | Extracted DEFAULT_OBJECT table (34 entries) and ENUM_FORM_ID (122 types) from PDB/EXE                 |
| 2026-02-09 | Fixed XLOC schema bug (Section 5.10): cFlags single byte treated as UInt32, door lock flags corrupted |
| 2026-02-09 | Cleared pending rewrite.                                                                              |
