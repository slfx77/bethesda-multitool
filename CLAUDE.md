# Fallout Xbox 360 Utils - AI Assistant Instructions

## Project Overview

.NET 10.0 application for Xbox 360 memory dump analysis, file carving, and format conversion. Features WinUI 3 GUI (Windows), cross-platform CLI, and companion apps (FalloutAudioTranscriber). Includes 8+ standalone tool projects (EsmAnalyzer, NifAnalyzer, PdbAnalyzer, RttiScanner, etc.).

## Critical Rules

### Tool Usage - NEVER use PowerShell for binary operations

- **NIF files**: Use `dotnet run --project tools/NifAnalyzer -f net10.0 -- <command> <file>`
- **ESM files**: Use main app CLI or EsmAnalyzer (see command reference below)
- **Never use** `2>&1` in PowerShell - breaks Spectre.Console ANSI output

## DO NOT RE-INVESTIGATE

These have been thoroughly investigated. Do not spend time re-researching them:

- **Split INFO records**: Xbox has MORE INFO records than PC (37,525 vs 23,247). Expected — the converter merges them.
- **Use MemDebug XEX, not ReleaseBeta PE.** The ReleaseBeta PE (`PowerPC:BE:32:default`, 85K functions) produces `halt_baddata()` stubs due to VMX instructions lacking pcode semantics, overlapping functions, wrong SLEIGH spec.
- **The MemDebug project has NO PDB symbols loaded** (all functions are `Function_XXXXXXXX`). Name-based lookup won't work. Use address-based lookup with cvdump-extracted addresses.
- **PPC thunk detection**: `mfspr r12, LR` = bytes `7D 80 42 A6` (NOT `7C 6C 02 A6`). Use Ghidra instruction API (`mfspr` + `bl` mnemonic check).
- **globals.txt module offsets** (`S_PROCREF: module, offset`) do NOT linearly map to VAs. Use `cvdump -s` output (`S_GPROC32: [section:offset]`) instead.

## Main App CLI Commands (falloutu)

```bash
# Run main app
dotnet run --project src/FalloutXbox360Utils -f net10.0 -- <command> <args>

# Format-agnostic commands (auto-detect file type: ESM, DMP, ESP)
search text <file-or-dir> <pattern>    # Text search in any binary file
search hex <file-or-dir> <hex-pattern> # Hex byte search (e.g., "6B F8 11 00")
stats <file>                           # Record type statistics (categorized table)
list <file> [-t TYPE] [-f FILTER]      # Browse reconstructed records
show <file> <formid-or-editorid>       # Inspect record detail (NPC, quest, etc.)
diff <fileA> <fileB> [-t TYPE] [-f ID] # Compare records between any two files
analyze <file>                         # Analyze memory dump structure
compare <fileA> <fileB>                # Format-agnostic comparison
world <file>                           # World/terrain analysis

# ESM commands
esm <file>                      # Default ESM analysis
esm stats <file>                # Record type statistics
esm dump <file> <type>          # Dump records of type
esm trace <file> -o <offset>    # Trace structure at offset
esm convert <file>              # Convert Xbox 360 ESM to PC format
esm semdiff <f1> <f2>           # Semantic field-by-field diff
esm diff --xbox <f> --converted <f> --pc <f>  # Unified 2/3-way diff
esm cell objects <file> <cell>  # List placed objects in a cell
esm cell npc-trace <file> <id>  # Trace NPC from FormID to cell

# BSA commands
bsa list <file>                 # List files in BSA archive
bsa extract <file> -o <dir>     # Extract BSA contents
bsa info <file>                 # BSA archive statistics
bsa find <file> <pattern>       # Find files matching pattern
bsa convert <file>              # Convert/validate BSA format
bsa validate <file>             # Validate BSA structure
bsa debug rawdump <file> <off> <len>    # Raw hex dump at offset
bsa debug file-compare <bsa> <f> <e>    # Compare BSA entry vs extracted

# Render commands (output: PNG sprites)
render <path> -o <dir>                     # Render single NIF to PNG
render <dir> -o <dir>                      # Batch render all NIFs in directory
render <prefix> --bsa <bsa> -o <dir>       # Batch render NIFs from BSA by prefix
render npc <meshes-bsa> --esm <e> -o <dir>  # NPC head sprites (auto-detects texture BSAs)

# Export commands (output: GLB/glTF models)
export nif <path> -o <dir>                  # Export NIF model to GLB
export npc <meshes-bsa> --esm <e> -o <dir>  # Export NPC with FaceGen morphs + equipment

# DMP commands
dmp modules <file>              # List loaded modules
dmp regions <file>              # List memory regions
dmp va2offset <file> <address>  # Convert VA to file offset
dmp hexdump <file> <address>    # Hex dump at address
dmp analyze <directory>          # Unified DMP analysis (persistent refs, map markers, runtime structs)
dmp buffers <file>              # Memory buffer analysis
dmp coverage <file>             # Runtime structure coverage analysis
dmp compare <f1> <f2>           # Compare runtime structures between DMPs
dmp formtype-census <file>      # FormType distribution analysis
dmp rtti <file>                 # RTTI structure scanning

# Dialogue commands
dialogue stats <file>           # Dialogue record statistics
dialogue tree <file>            # Display dialogue tree structure
dialogue verify <file>          # Verify dialogue structure
dialogue debug <file>           # Debug dialogue parsing
dialogue provenance <file>      # Track dialogue record origins
dialogue unattributed <file>    # Find dialogue without speaker attribution

# Save game commands
save decode <file>              # Decode Xbox 360 save game file
save report <file>              # Generate save game analysis report

# Version tracking commands
version extract <file>          # Extract version identifiers from builds
version inventory <dir>         # Inventory of build versions
version report <dir>            # Build version comparison report
version track <dir>             # Track build history
```

### EsmAnalyzer Commands (niche debugging)

```bash
# Run EsmAnalyzer (fast build, niche commands)
dotnet run --project tools/EsmAnalyzer -c Release -- <command> <args>

# Structure analysis
grups <file>                    # GRUP structure, nesting, duplicates
toft <file>                     # Xbox 360 TOFT streaming cache

# Land/export
land-summary <file>             # LAND subrecord summary
export-land <file>              # Export LAND as images/JSON
worldmap <file>                 # Generate worldspace heightmap

# Comparison
compare land <f1> <f2>          # Compare land records
compare cells <f1> <f2>         # Compare cell records
compare heightmaps <f1> <f2>    # Compare heightmap data

# Search/validate
search text <file> <pattern>    # ASCII text search
search hex <file> <offset>      # Hex dump at offset
search locate <file> <offset>   # Locate record at offset
validate structure <file>       # Validate record structure
validate deep <file>            # Deep record validation

# DMP (niche — scripts, rendering, module extraction)
dmp scripts list <file>         # List scripts in DMP
dmp scripts show <file> <id>    # Show script details
dmp scripts compare <file>      # Compare SCTX vs SCDA
dmp scripts crossrefs <file>    # Cross-reference chain diagnostics
dmp render-map <directory>      # Render map marker overlay PNGs
dmp extract-module <file>       # Extract game exe for Ghidra

# Other niche
gen-facegen <ctl-file>          # Generate C# code from si.ctl
worldmap-diag <file>            # World map category diagnostics
category-audit <file>           # Map category audit
orphan-refs <file>              # Find orphaned FormID references
```

### Semantic Diff (semdiff) - Primary debugging tool

```bash
# Compare specific FormID between converted and PC reference
dotnet run --project src/FalloutXbox360Utils -f net10.0 -- esm semdiff <converted.esm> <pc_reference.esm> -f 0x0017B37C

# Compare all records of a type
dotnet run --project src/FalloutXbox360Utils -f net10.0 -- esm semdiff <converted.esm> <pc_reference.esm> -t PROJ --limit 50

# Show all fields, not just differences
dotnet run --project src/FalloutXbox360Utils -f net10.0 -- esm semdiff <file1> <file2> -f 0x12345678 --all
```

## Tool Projects

| Tool | Purpose | Run command |
|---|---|---|
| EsmAnalyzer | Niche ESM/DMP debugging (~60 commands) | `dotnet run --project tools/EsmAnalyzer -c Release -- <cmd>` |
| NifAnalyzer | NIF structure analysis (blocks, geometry, skin, havok) | `dotnet run --project tools/NifAnalyzer -f net10.0 -- <cmd>` |
| PdbAnalyzer | PDB symbol extraction, struct layout generation | `dotnet run --project tools/PdbAnalyzer -- <cmd>` |
| RttiScanner | RTTI + operator new extraction from raw binaries | `dotnet run --project tools/RttiScanner -- <cmd>` |
| EgtAnalyzer | FaceGen EGT texture analysis | `dotnet run --project tools/EgtAnalyzer -- <cmd>` |
| TextureAnalyzer | DDX/DDS texture analysis and conversion | `dotnet run --project tools/TextureAnalyzer -- <cmd>` |
| SignatureScanner | File signature matching in memory dumps | `dotnet run --project tools/SignatureScanner -- <cmd>` |
| TerrainAnalyzer | Heightmap/terrain analysis | `dotnet run --project tools/TerrainAnalyzer -- <cmd>` |

## Key Source Directories

```
src/FalloutXbox360Utils/
├── CLI/
│   ├── Commands/
│   │   ├── Analysis/       # search, stats, list, show, diff, compare, world, analyze
│   │   ├── Esm/            # esm convert/stats/dump/diff(5 variants)/semdiff/cell
│   │   ├── Bsa/            # bsa list/extract/convert/find/validate/debug
│   │   ├── Dmp/            # dmp modules/regions/buffers/coverage/compare/rtti/formtype-census
│   │   ├── Dialogue/       # dialogue stats/tree/verify/debug/provenance/unattributed
│   │   ├── Export/         # export nif/npc (GLB model export)
│   │   ├── Save/           # save decode/report
│   │   └── Version/        # version extract/inventory/report/track
│   ├── Rendering/          # Pipeline implementations for render/export commands
│   │   ├── Nif/            # NifExportPipeline.cs (NIF→PNG/GLB)
│   │   ├── Npc/            # NpcRenderPipeline.cs + NpcExportPipeline.cs (head+body+equipment)
│   │   └── Gltf/           # GLB validation
│   ├── Formatters/         # Semdiff formatting, diff resolution
│   ├── Show/               # Record display renderers (Actor, Item, Quest, Misc, Generic, Magic, WorldObject)
│   └── Shared/             # CLI helpers, progress bars, table builders
├── Core/
│   ├── Semantic/           # SemanticFileLoader (format-agnostic ESM/DMP/ESP loading)
│   ├── Formats/
│   │   ├── Esm/            # ESM parsing, conversion, runtime reading, export
│   │   │   ├── Conversion/ # Xbox→PC converter engine (see ESM Conversion section)
│   │   │   ├── Runtime/    # PDB-based DMP struct readers (Readers/Generic/ + 24 specialized)
│   │   │   ├── Export/     # CSV, GECK reports, cross-dump comparison
│   │   │   ├── Parsing/    # ESM record/subrecord parsing
│   │   │   ├── Analysis/   # Semantic analysis
│   │   │   ├── FaceGen/    # FaceGen coefficient handling
│   │   │   ├── Script/     # Script bytecode parsing
│   │   │   ├── Records/    # Per-record-type models (split from Models/)
│   │   │   ├── Subrecords/ # Per-subrecord parsers
│   │   │   ├── Presentation/ # Display/formatting helpers
│   │   │   ├── Enums/      # ESM enum definitions
│   │   │   └── Models/     # Shared record data models
│   │   ├── Nif/            # NIF mesh format
│   │   │   ├── Rendering/  # Rasterizer, FaceGen morpher, GPU sprites, NPC assembly
│   │   │   │   └── Npc/Composition/  # NPC + Creature render/export composition planners
│   │   │   ├── Skinning/   # Skin data/partition parsing + LBS/DQS
│   │   │   ├── Geometry/   # Packed geometry, topology
│   │   │   ├── Conversion/ # NIF endian conversion
│   │   │   └── Schema/     # NIF format schema (nif.xml)
│   │   ├── SaveGame/       # Changed form decoder (ACHR, ACRE, REFR, QUEST, etc.)
│   │   ├── Bsa/            # BSA archive parsing + extraction
│   │   └── ...             # Dds, Ddx, Bik, Lip, Xma, Xdbf, Png, Subtitles
│   ├── Minidump/           # DMP parser, RTTI reader, FormType census, module extraction
│   ├── RuntimeBuffer/      # Runtime string extraction, pointer analysis, ownership
│   ├── Pdb/                # PDB global resolver
│   ├── Carving/            # Memory carver (file signature extraction)
│   ├── Coverage/           # DMP structure coverage analysis
│   └── Utils/              # General utilities
├── App/                    # WinUI 3 GUI (net10.0-windows TFM only)
│   ├── Controls/           # XAML user controls (WorldMapControl, etc.)
│   ├── Tabs/               # GUI tabs (SingleFileTab, BatchModeTab)
│   ├── Helpers/            # EsmBrowserTreeBuilder, EsmPropertyFormatter
│   └── HexViewer/          # Virtual-scrolling hex editor
└── Repack/                 # Memory region repacking

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

tools/EsmAnalyzer/                      # Niche debugging commands (fast build)
├── Commands/                           # ~56 command files
└── GlobalUsings.cs                     # References main project namespaces
```

## Xbox 360 ESM Conversion

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

## Standard File Paths

### Sample Directory Layout

```
Sample/
├── ESM/                    # Individual ESM files
│   ├── 360_final/          # Xbox 360 final ESM
│   ├── 360_proto/          # Xbox 360 prototype ESM
│   ├── fallout_3/          # Fallout 3 ESM
│   └── pc_final/           # PC final ESM
├── Full_Builds/            # Full game data (BSAs + ESMs + textures)
│   ├── Fallout New Vegas (360 Final)/Data/
│   ├── Fallout New Vegas (Aug 22, 2010)/Data/
│   ├── Fallout New Vegas (July 21, 2010)/Data/
│   └── Fallout New Vegas (PC Final)/Data/
├── Meshes/                 # Extracted Meshes BSAs
├── Textures/               # Extracted Texture BSAs
├── MemoryDump/             # Xbox 360 crash dumps
├── PDB/                    # Extracted PDB info from cvdump
├── Reference_Code/         # Source code from useful projects
├── Saves/                  # Save game files from 360 prototypes (for save decode testing)
├── TCRF/                   # Reference documentation for article writing
└── Unpacked_Builds/        # Full game data with BSAs extracted
    ├── 360_July_Unpacked/
    └── PC_Final_Unpacked/
```

### Full Game Builds (for rendering — needs BSAs + textures)

- **Xbox 360 final**: `Sample/Full_Builds/Fallout New Vegas (360 Final)/Data/`
- **Xbox 360 Aug 2010**: `Sample/Full_Builds/Fallout New Vegas (Aug 22, 2010)/Data/`
- **Xbox 360 July 2010**: `Sample/Full_Builds/Fallout New Vegas (July 21, 2010)/Data/`
- **PC final**: `Sample/Full_Builds/Fallout New Vegas (PC Final)/Data/`
- **PC install**: `E:\SteamLibrary\SteamApps\common\Fallout New Vegas\Data\`

### ESM Conversion Testing

- **Xbox 360 source**: `Sample/ESM/360_final/FalloutNV.esm`
- **Converted output**: `TestOutput/FalloutNV.pc.esm` (standard location, overwritten during testing)
- **PC reference**: `Sample/ESM/pc_final/FalloutNV.esm`

### Three-Way Diff (Primary Debugging Tool)

```bash
# Compare all three files for a record type (via main app)
dotnet run --project src/FalloutXbox360Utils -f net10.0 -- esm diff \
     --xbox "Sample/ESM/360_final/FalloutNV.esm" \
     --converted "TestOutput/FalloutNV.pc.esm" \
     --pc "Sample/ESM/pc_final/FalloutNV.esm" \
     -t ALCH --semantic -l 5

# Compare specific FormID across all three
dotnet run --project src/FalloutXbox360Utils -f net10.0 -- esm diff \
     --xbox ... --converted ... --pc ... -f 0x0017B37C --semantic
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
- **Language**: `PowerPC:BE:64:Xenon` (VMX128 SLEIGH defs for Xbox 360 Xenon)
- **TEXT_BASE**: `0x82250000` (MemDebug .text section base)

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
3. Add to `TARGETS` array in the script: `{0xOFFSET, 0xSIZE, "Class::Method", tier}`
4. VA = `TEXT_BASE (0x82250000) + offset`
5. Exclude tiny stubs (Cb <= 0x10) — those are vtable redirectors

### Key Scripts

46 Java + ~190 Python scripts total. Most `run_decompile_*.py` are targeted single-function decompilations via PyGhidra.

| Script | Purpose | Status |
|---|---|---|
| `DecompileSaveTargets.java` | Save game functions | 58/58 GOOD |
| `DecompileSkeletonPipeline.java` | NIF loading, transforms, scene graph | 36/37 GOOD |
| `DecompileSkinningMemDebug.java` | Skinning/bone matrices | 20/20 GOOD |
| `DecompileWeaponAttachment.java` | BipedAnim weapon attachment | 8/11 GOOD |
| `DecompileAnimationPipeline.java` | Animation system | |
| `DecompileHairTint.java` | Hair tint rendering | |
| `run_decompile_facegen.py` | FaceGen morphing (PyGhidra) | 28/28 GOOD |
| `ExtractRttiStructSizes.java` | RTTI struct size extraction | |

## Build & CI

```bash
# Build EsmAnalyzer
dotnet build tools/EsmAnalyzer -c Release

# Run EsmAnalyzer
dotnet run --project tools/EsmAnalyzer -c Release -- <command> <args>

# Build main project (both CLI + Windows GUI TFMs, ~2:40)
dotnet build -c Release

# Fast build — CLI only, no analyzers (~25s)
dotnet build -c Release -p:BuildTestsOnly=true -p:SkipAnalyzers=true

# Run tests — fast (CLI-only build, no analyzers, ~1 min total)
dotnet test -p:CollectCoverage=false -p:BuildTestsOnly=true -p:SkipAnalyzers=true

# Run tests — full build (both TFMs, with analyzers)
dotnet test -p:CollectCoverage=false
```

### Build Flags

- `BuildTestsOnly=true` — Skips `net10.0-windows` TFM (WinUI 3 GUI). Saves ~2 min. Safe for test runs since the test project only targets `net10.0`.
- `SkipAnalyzers=true` — Disables SonarAnalyzer + Roslynator during build. Saves 5-15s. Use for fast iteration; omit for CI/lint passes.
- `CollectCoverage=false` — Required: coverage collection hangs without this flag.

### CI/CD

CI: `.github/workflows/build-and-test.yml` — builds Release + runs tests with code coverage on Windows, then cross-platform CLI build on Ubuntu.

## Code Style

- File-scoped namespaces: `namespace Foo;`
- Private fields: `_camelCase`
- Nullable reference types: Enabled
- Prefer braces for control flow
- Async methods: suffix with `Async`

## Key Dependencies

| Package | Purpose |
|---|---|
| `Veldrid` + `Veldrid.SPIRV` | GPU rendering (headless sprite generation) |
| `SharpGLTF.Toolkit` | GLB/glTF model export |
| `Magick.NET-Q16-AnyCPU` | Image processing (textures, sprites) |
| `System.CommandLine` | CLI argument parsing |
| `Spectre.Console` | Rich terminal output (tables, progress bars) |
| `BCnEncoder.Net.ImageSharp` | DDS/BC texture compression (DDXConv) |
