# Xbox 360 Memory Carver - AI Assistant Instructions

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

# Comparison (key for conversion work)
compare <file1> <file2>         # Compare two ESM files
diff <file1> <file2> -t <type>  # Diff by record type
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
tools/EsmAnalyzer/
├── Commands/
│   ├── ConvertCommands.cs          # Convert command entry point
│   ├── SemanticDiffCommands.cs     # semdiff implementation
│   ├── DiffCommands*.cs            # Various diff commands
│   └── CompareCommands*.cs         # Comparison utilities
├── Conversion/
│   ├── EsmConverter.cs             # Main conversion logic
│   ├── EsmSubrecordConverter.cs    # Subrecord byte-swapping
│   ├── EsmInfoMerger.cs            # Split INFO record merging
│   └── Schema/
│       ├── SubrecordSchemaRegistry.cs  # Field type definitions
│       ├── SubrecordSchema.cs          # Schema structures
│       └── SubrecordFieldType.cs       # Field type enum
└── Helpers/
    ├── EsmHelpers.cs               # Compression, utilities
    └── DiffHelpers.cs              # Diff utilities
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
- **TES5Edit source**: `TES5Edit/` at repo root (for ESM research)
- **PDB symbols**: `tools/pdb_types_full.txt`, `tools/pdb_globals.txt`

## Code Style

- File-scoped namespaces: `namespace Foo;`
- Private fields: `_camelCase`
- Nullable reference types: Enabled
- Always use braces for control flow
- Async methods: suffix with `Async`

## Build Commands

```bash
# Build EsmAnalyzer
dotnet build tools/EsmAnalyzer -c Release

# Run EsmAnalyzer
dotnet run --project tools/EsmAnalyzer -c Release -- <command> <args>

# Build main project
dotnet build -c Release

# Run tests
dotnet test
```
