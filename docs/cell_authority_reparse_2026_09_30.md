# Preserve captured placement recovery on reparse

The actual November GUI extraction repeated semantic parsing on a shared scan result. Its report contained 307 cells and zero placements, compared with 1,561 cells and 11,219 placements in the retained CLI report. All 945 scripts and the complete script report were identical. Evidence is preserved under `artifacts/prototype-feedback/bmt-gui-script-extraction-retry001/comparison/`.

Cell authority application had written partial inferred parent links into `CellToRefrMap` and `CellToWorldspaceMap`. Those scan dictionaries represent physical GRUP hierarchy. A later parser interpreted their nonempty state as complete structural mapping and skipped orphan recovery.

The applier now keeps inferred parentage on semantic cells and placed references, including their existing assignment-source labels. It preserves both physical scan maps and their occurrences. Terrain enrichment remains unchanged. No other callers or existing authority tests require the removed map writes.

Two parameterized `CellWorldspaceAuthorityTests` cases exercise parse, authority application and reparse on the same scan. They compare cell IDs plus placement IDs, parents, base IDs, signatures, offsets, positions and rotations; a physical-map case preserves its original worldspace mapping and repeated reference entries. Existing authority/terrain tests and `MinidumpScriptExportProvenanceTests` remain relevant.

Batch 9 passed 99 focused tests. The actual GUI extraction replay matches the fresh CLI export: 1,561 CELL rows and 11,219 OBJ rows, including duplicate occurrences, placement offsets, parentage and transforms at report precision. All 945 script files and full content/provenance manifests agree, and `script_report.txt` is byte-identical. Evidence: `artifacts/prototype-feedback/bmt-gui-script-extraction-retry002/comparison/validation.json`.

The GUI flow completed through UI Automation and closed normally; source and Fast9 assembly hashes remained unchanged. Foreground screenshot capture was unavailable. Its whole export independently validates 60 CSVs and 1,205,552 aligned rows; CLI parity assertions cover the selected placement and script outputs. The original failing export remains preserved.
