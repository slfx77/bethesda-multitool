# Fallout Xbox 360 Utils - AI Assistant Instructions

## Project Overview

.NET 10.0 application for Xbox 360 memory dump analysis, file carving, and format conversion. Features WinUI 3 GUI (Windows) and cross-platform CLI.

## Critical Rules

### Tool Usage - NEVER use PowerShell for binary operations

- **NIF files**: Use `dotnet run --project tools/NifAnalyzer -f net10.0 -- <command> <file>`
- **ESM files**: Use `dotnet run --project tools/EsmAnalyzer -c Release -- <command> <file>`
- **Never use** `2>&1` in PowerShell - breaks Spectre.Console ANSI output

### EsmAnalyzer Commands

```bash
# Analysis
stats <file>                    # Record type statistics
dump <file> <type>              # Dump records of type
trace <file> -o <offset>        # Trace structure at offset
locate <file> <formid>          # Find record by FormID

# Comparison
compare land <file1> <file2>    # Compare land records
compare cells <file1> <file2>   # Compare cell records
compare heightmaps <f1> <f2>    # Compare heightmap data

# Diff (unified command - provide 2 or 3 files)
diff --xbox <file> --converted <file> --pc <file> -t <type> --semantic
semdiff <file1> <file2> ...     # Semantic field-by-field diff (most useful!)

# Conversion
convert <file>                  # Convert Xbox 360 ESM to PC format
```

### Semantic Diff (semdiff) - Primary debugging tool

```bash
# Compare specific FormID between converted and PC reference
semdiff <converted.esm> <pc_reference.esm> -f 0x0017B37C

# Compare all records of a type
semdiff <converted.esm> <pc_reference.esm> -t PROJ --limit 50

# Show all fields, not just differences
semdiff <file1> <file2> -f 0x12345678 --all
```

## Xbox 360 ESM Conversion

### DO NOT RE-INVESTIGATE

- **Split INFO records**: Xbox has MORE INFO records than PC (37,525 vs 23,247). This is expected - the converter merges them.

### Hybrid Endianness

Xbox 360 ESM uses mixed endianness:

- Record/subrecord headers: Big-endian
- Most data: Big-endian (FormIDs, floats, integers)
- Some fields: Already little-endian (e.g., INDX quest stage indices)

The `SubrecordSchemaRegistry` defines field types:

- `UInt16` / `UInt32` / `Float` - Big-endian, byte-swapped
- `UInt16LittleEndian` / `FormIdLittleEndian` - Preserved as-is

### Platform-Specific Subrecords

- **PNAM** in INFO records: Present on Xbox, stripped during conversion

### Known Content Differences (NOT conversion bugs)

Many records differ between Xbox and PC due to genuine content differences, not conversion issues:

- **LVLO padding bytes**: Xbox has `FA 06`, PC has `15 06` - both are valid, semantically equivalent
- **AIDT unused bytes**: Xbox has zeros, PC has non-zero values - likely PC-only data
- **Various counts**: Xbox has more/fewer records in some categories (REFR +2369, etc.)

When debugging, focus on fields showing **DIFF** in semantic comparison, not just byte differences in padding.

## Key Files for ESM Conversion

```
src/FalloutXbox360Utils/Core/Formats/Esm/Conversion/
├── EsmConverter.cs                     # Main conversion orchestrator
├── EsmConverterConstants.cs            # Conversion constants
├── EsmEndianHelpers.cs                 # Endian swap utilities
├── EsmHelpers.cs                       # Compression, general utilities
├── Indexing/
│   ├── EsmConversionIndexBuilder.cs    # Pre-scan index for merging
│   └── EsmConversionStats.cs           # Conversion statistics
├── Processing/
│   ├── EsmGrupWriter.cs               # GRUP record writing
│   ├── EsmInfoMerger.cs               # Split INFO record merging
│   ├── EsmRecordWriter.cs             # Record writing
│   ├── EsmSubrecordConverter.cs        # Subrecord byte-swapping
│   └── EsmSubrecordConverter.Helpers.cs
└── Schema/
    ├── SubrecordSchemaRegistry.cs      # Field type definitions (+ partial files)
    ├── SubrecordSchema.cs              # Schema structures
    ├── SubrecordSchemaProcessor.cs     # Schema application logic
    └── SubrecordFieldType.cs           # Field type enum

tools/EsmAnalyzer/                      # Thin CLI wrapper over the above
├── Commands/                           # CLI commands (38 files)
│   ├── ConvertCommands.cs              # Convert command entry point
│   ├── SemanticDiffCommands.cs         # semdiff implementation
│   ├── DiffCommands*.cs               # Diff commands (Unified, ThreeWay, Header, Records)
│   └── CompareCommands*.cs             # Compare commands (Land, Cells, Heightmap)
└── Helpers/
    └── DiffHelpers.cs                  # Diff display utilities
```

## Standard File Paths

### ESM Conversion Testing

- **Xbox 360 source**: `Sample/ESM/360_final/FalloutNV.esm`
- **Converted output**: `TestOutput/FalloutNV.pc.esm` (standard location, overwritten during testing)
- **PC reference**: `Sample/ESM/pc_final/FalloutNV.esm`
- **Game install**: `E:\SteamLibrary\SteamApps\common\Fallout New Vegas\Data\FalloutNV.esm`

### Three-Way Diff (Primary Debugging Tool)

```bash
# Compare all three files for a record type
diff --xbox "Sample/ESM/360_final/FalloutNV.esm" \
     --converted "TestOutput/FalloutNV.pc.esm" \
     --pc "Sample/ESM/pc_final/FalloutNV.esm" \
     -t ALCH --semantic -l 5

# Compare specific FormID across all three
diff --xbox ... --converted ... --pc ... -f 0x0017B37C --semantic
```

### Reference Materials

- **PDB symbols**: `Sample/PDB/`
- **MemDebug PDB**: `tools/GhidraProject/Fallout_Release_MemDebug.pdb` (100 MB, loaded into Ghidra)
- **Decompiled output**: `tools/GhidraProject/savegame_decompiled.txt` (save game functions)

## Ghidra Decompilation

### Setup

- **Ghidra**: `C:/Tools/ghidra_12.0.2_PUBLIC`
- **Project**: `tools/GhidraProject/XEX360Project/FalloutNV_MemDebug` (the MemDebug XEX)
- **Binary**: `Fallout_Release_MemDebug.xex`
- **Language**: `PowerPC:BE:64:A2ALT-32addr` (Xbox 360 Xenon, better VMX128 support)
- **TEXT_BASE**: `0x82250000` (MemDebug .text section base)

### DO NOT RE-INVESTIGATE

- **Use MemDebug XEX, not ReleaseBeta PE.** The ReleaseBeta PE (`PowerPC:BE:32:default`, 85K functions) produces `halt_baddata()` stubs due to: VMX instructions lacking pcode semantics, overlapping auto-analyzed functions, wrong SLEIGH spec.
- **The MemDebug project has NO PDB symbols loaded** (all functions are `Function_XXXXXXXX`). Name-based lookup won't work. Use address-based lookup with cvdump-extracted addresses.
- **PPC thunk detection**: `mfspr r12, LR` = bytes `7D 80 42 A6` (NOT `7C 6C 02 A6`). Use Ghidra instruction API (`mfspr` + `bl` mnemonic check) for reliability.
- **globals.txt module offsets** (`S_PROCREF: module, offset`) do NOT linearly map to VAs. Use `cvdump -s` output (`S_GPROC32: [section:offset]`) instead.

### Running Decompilation

```bash
# Extract function addresses from MemDebug PDB
tools/microsoft-pdb/cvdump/cvdump.exe -s tools/GhidraProject/Fallout_Release_MemDebug.pdb | grep S_GPROC32

# Run Ghidra headless (from tools/GhidraProject/)
"C:/Tools/ghidra_12.0.2_PUBLIC/support/analyzeHeadless.bat" \
    XEX360Project FalloutNV_MemDebug \
    -process Fallout_Release_MemDebug.xex \
    -noanalysis \
    -postScript DecompileSaveTargets.java \
    -scriptPath .
```

### Adding New Decompilation Targets

1. Find the function in the PDB: `cvdump -s Fallout_Release_MemDebug.pdb | grep "FunctionName"`
2. Extract `[0004:XXXXXXXX], Cb: YYYYYYYY` → section 4 offset and size
3. Add to `TARGETS` array in `DecompileSaveTargets.java`: `{0xOFFSET, 0xSIZE, "Class::Method", tier}`
4. VA = `TEXT_BASE (0x82250000) + offset`
5. Exclude tiny stubs (Cb <= 0x10) — those are vtable redirectors

### Key Scripts

| Script | Purpose |
|---|---|
| `DecompileSaveTargets.java` | Save game function decompilation (45 functions, 5 tiers) |
| `DecompileFaceGenMemDebug.java` | FaceGen function decompilation (28 functions) |
| `CreatePdbFunctions.java` | Pre-analysis function creation (ReleaseBeta PE only) |

## Code Style

- File-scoped namespaces: `namespace Foo;`
- Private fields: `_camelCase`
- Nullable reference types: Enabled
- Prefer braces for control flow
- Async methods: suffix with `Async`

## Build Commands

```bash
# Build EsmAnalyzer
dotnet build tools/EsmAnalyzer -c Release

# Run EsmAnalyzer
dotnet run --project tools/EsmAnalyzer -c Release -- <command> <args>

# Build main project
dotnet build -c Release

# Run tests (coverage collection hangs - must disable it)
dotnet test -p:CollectCoverage=false
```
