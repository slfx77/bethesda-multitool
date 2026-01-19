# Xbox 360 Memory Dump Research

This is a living document tracking research into Xbox 360 memory dump structure, with a focus on Fallout: New Vegas.

## Related Documentation

- [Architecture Guide](Architecture.md) - Internal codebase architecture and extensibility
- [DDX/DDS Format Guide](DDX_DDS_Format_Guide.md) - Texture format documentation

## Overview

This document captures ongoing research into:

- Memory dump structure and layout
- File type region mapping
- Xbox 360/PowerPC architecture specifics
- PDB symbol analysis for understanding game structures

## Available Resources

### PDB Symbol Files

Located in `Fallout New Vegas (July 21, 2010)/FalloutNV/`:

- `Fallout.pdb` - Debug build symbols
- `Fallout_Release_Beta.pdb` - Release beta symbols
- `Fallout_Release_MemDebug.pdb` - Release memory debug symbols

### Executables

- `Fallout.exe` / `default.xex` - Debug build
- `Fallout_Release_Beta.exe` / `Fallout_Release_Beta.xex` - Release beta
- `Fallout_Release_MemDebug.exe` / `Fallout_Release_MemDebug.xex` - Memory debug release

### PDB Tools

The `microsoft-pdb` submodule contains tools for PDB analysis:

- `cvdump` - Dump PDB contents
- `pdbdump` - PDB file dumper

### Extracted Symbols (in `tools/`)

- `pdb_types_full.txt` - Complete type definitions from debug PDB
- `pdb_publics.txt` - Public symbols
- `pdb_globals.txt` - Global symbols
- `script_function_constants.txt` - FUNCTION\_\* opcode constants
- `script_param_constants.txt` - SCRIPT*PARAM*\* type constants
- `script_block_constants.txt` - SCRIPT*BLOCK*\* type constants
- `opcode_table.json` - Generated opcode table (JSON format)
- `opcode_table.csv` - Generated opcode table (CSV format)

---

## Script System Analysis

### Script Class Structure (84 bytes)

From PDB analysis, the `Script` class has these members:

| Offset | Type          | Name                         | Description                  |
| ------ | ------------- | ---------------------------- | ---------------------------- |
| 0      | (inherited)   | TESForm base                 | Base form data               |
| 24     | SCRIPT_HEADER | m_header                     | Script header (20 bytes)     |
| 44     | char\*        | m_text                       | Pointer to source text       |
| 48     | char\*        | m_data                       | Pointer to compiled bytecode |
| 52     | float         | fProfilerTimer               | Profiling data               |
| 56     | float         | fQuestScriptDelay            | Quest script delay           |
| 60     | float         | fQuestScriptGetSecondsBuffer | Quest timing buffer          |
| 64     | TESQuest\*    | pOwnerQuest                  | Owner quest reference        |
| 68     | BSSimpleList  | listRefObjects               | Referenced objects list      |
| 76     | BSSimpleList  | listVariables                | Variable list                |

### SCRIPT_FUNCTION Structure (40 bytes)

The command definition structure:

| Offset | Type                | Name                 | Description                |
| ------ | ------------------- | -------------------- | -------------------------- |
| 0      | char\*              | pFunctionName        | Full command name          |
| 4      | char\*              | pShortName           | Short alias                |
| 8      | enum                | eOutput              | Output type                |
| 12     | char\*              | pHelpString          | Help text                  |
| 16     | bool                | bReferenceFunction   | Is reference function      |
| 18     | unsigned short      | sParamCount          | Number of parameters       |
| 20     | SCRIPT_PARAMETER\*  | pParameters          | Parameter definitions      |
| 24     | ExecuteFunction\*   | pExecuteFunction     | Execute handler            |
| 28     | CompileFunction\*   | pCompileFunction     | Compile handler            |
| 32     | ConditionFunction\* | pConditionFunction   | Condition handler          |
| 36     | bool                | bEditorFilter        | Editor-only flag           |
| 37     | bool                | bInvalidatesCellList | Invalidates cell list flag |

### Opcode Table

**829 script commands** were extracted from the debug PDB symbols. Key findings:

- **Opcode range**: 256 (0x100) to 4719 (0x126F)
- **Gap at 302**: Single missing opcode
- **Gap at 461-623**: Range reserved/unused
- **Gap at 625-4095**: Jump to extended opcodes (4096+)

Commands are categorized:

- Getters (185): `GetLevel`, `GetHealth`, etc.
- Setters (116): `SetPos`, `SetAngle`, etc.
- UI (121): `ShowMessage`, `ToggleMenus`, etc.
- Conditions (76): `IsActorDetected`, `HasItem`, etc.
- Others: Inventory, Animation, Combat, Quest, etc.

See `tools/opcode_table.csv` for the complete list.

### Script Block Types (36 total)

Script blocks define execution contexts:

- `ONTRIGGERENTER`, `ONTRIGGEREXIT`
- `ONACTIVATE`, `ONOPEN`, `ONCLOSE`
- `ONSELL`, `ONPICKUP`, `ONDROP`
- etc.

---

## Memory Dump Structure

### Minidump Format

Xbox 360 memory dumps follow the Windows Minidump format with Xbox-specific extensions:

- Processor type 0x3 indicates PowerPC (Xbox 360)
- Memory64ListStream (stream type 9) contains memory region descriptors
- ModuleListStream (stream type 4) contains loaded module information

### Memory Region Analysis (Fallout: New Vegas)

Analysis performed on debug and release beta dumps using `tools/MemoryRegionMapper`:

#### Debug Build (Fallout_Debug.xex.dmp)

| Type                  | Count | Offset Range            | Notes                               |
| --------------------- | ----: | ----------------------- | ----------------------------------- |
| xma                   |    32 | 0x0016C9A9 - 0x0914D93C | Audio scattered throughout          |
| xex                   |     5 | 0x0035E776 - 0x0901D776 | Executable modules                  |
| png                   |   249 | 0x00389776 - 0x09164551 | UI images, wide distribution        |
| nif                   |    36 | 0x006B1C46 - 0x06F5435A | Model data, clustered               |
| ddx_3xdo              |    21 | 0x0086D113 - 0x0375C5B0 | Compressed textures                 |
| **script_scn**        |   963 | 0x01EB47C6 - 0x059D8E57 | **Uncompiled scripts (debug only)** |
| **script_scriptname** |   156 | 0x01EB4CF6 - 0x05996E13 | Script name variants                |
| xui_scene             |   429 | 0x06E4D406 - 0x092A9776 | UI definitions, large region        |

**Script Region Details (Debug):**

- Total scripts: 1,172 files
- Region span: ~59 MB (mostly gaps between scripts)
- Average script size: 905 bytes
- Gaps between scripts: 1,169 (58 MB of non-script data interspersed)

#### Release Beta Build (Fallout_Release_Beta.xex.dmp)

| Type         | Count | Offset Range            | Notes                                     |
| ------------ | ----: | ----------------------- | ----------------------------------------- |
| png          |   250 | 0x0017658E - 0x0AFF2AAF | Similar to debug                          |
| xex          |     5 | 0x0037ACD4 - 0x0AEA7CD4 | Executable modules                        |
| nif          |    42 | 0x006F4464 - 0x09ACB208 | More models loaded                        |
| **ddx_3xdo** |   163 | 0x007850D0 - 0x0550CEC1 | **7.85 MB of textures (vs 1.2 MB debug)** |
| xma          |    40 | 0x00941E99 - 0x0AFDBE9A | More audio loaded                         |
| xui_scene    |   429 | 0x099FB964 - 0x0B137CD4 | UI definitions                            |

**Key Observation:** Release build has **no script_scn/script_scriptname** types detected - scripts are compiled bytecode that doesn't match text signatures.

#### Memory Layout Visualization

```
Debug Build (146 MB):
0x00000000                                                           0x092A9776
|------------------------------------------------------------------------------|
|A         A                       A                                       A   | xma (scattered)
| E                                                                       E    | xex (modules)
| PP                                                                      PP   | png (UI images)
|  NNNNNNNNNNNNNNNNNNNN                                                        | nif (models)
|   DDD                                                                        | ddx (textures)
|              SSSSSSSSSSSSSSS                                                 | scripts (DEBUG ONLY)
|                                                          UUUUUUUUUUUUUUUUUUUU| xui_scene

Release Build (177 MB):
0x00000000                                                           0x0B137CD4
|------------------------------------------------------------------------------|
| PP                                                                      PPPP | png
| E                                                                       E    | xex
|  NNN  NNN       NNN                NNNNNN                                    | nif
|   DDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDD                             | ddx (MUCH MORE)
|A      A            A                   A                        AAAA         | xma
|                                                         UUUUUUUUUUUUUUUUUUUUU| xui_scene
|              [NO SCRIPTS - COMPILED BYTECODE]                                |
```

### Type Adjacency Patterns

Common file type clustering observed:

- Scripts cluster together (848 consecutive script_scn pairs in debug)
- XUI scenes form large contiguous blocks (420+ consecutive)
- Textures (DDX) often adjacent to models (NIF)
- Audio (XMA) scattered throughout, often near images

See `tools/debug_memory_map.md` and `tools/release_beta_memory_map.md` for detailed analysis.

### Unidentified Regions Analysis

The memory dump contains significant regions not matched by file signature carvers. Analysis using `tools/RegionAnalyzer/` reveals these categories:

#### XEX Code Section (~32 MB gap at 0x06F5435A - 0x08F29776)

The largest unidentified region contains **PowerPC executable code** from the game's XEX module:

- Common instruction patterns: `7D 88 02 A6` (mflr r12), `4E 80 00 20` (blr), `48 xx xx xx` (branch)
- Mixed with wide-character (UTF-16) debug strings: source file paths like `D:\_Fallout3\Platforms\Common\Code\...`
- Debug builds retain source paths for error reporting

#### INI Settings Region (~35 KB at 0x06EBD91E)

Contains game INI setting names with embedded default values:

```
fStarsRotateDays, fSunMinimumGlareScale, fSneakBaseValue...
```

Setting structure (`SettingT<*>`) is 12 bytes per PDB analysis:

- 4 bytes: pointer to name string (char\*)
- 4 bytes: value (int/float union, big-endian)
- 4 bytes: collection pointer

#### ESM Record Data (scattered throughout)

String regions contain ESM plugin data loaded into memory:

| Region     | Content                          | Size    |
| ---------- | -------------------------------- | ------- |
| 0x01B674D6 | Audio asset paths (`.xma` files) | ~107 KB |
| 0x03CD9DF2 | Dialogue strings, quest names    | ~64 KB  |
| 0x041368CE | DLC content names                | ~77 KB  |
| 0x03B29686 | Interior cell names              | ~42 KB  |
| 0x0452B7D6 | Quest objectives text            | ~59 KB  |

Record markers found (indicating ESM data in memory):

- STAT (299), INFO (299), CONT (280), FORM (219), DIAL (189), GRUP (173)

#### Sparse Data Regions

Large gaps contain mostly zeros with occasional index values - likely:

- Uninitialized memory pools
- Sparse arrays/hash tables
- Memory-mapped but uncommitted pages

---

## Script Handling

### Build Type Differences

The game's memory dump structure differs significantly between debug and release builds:

| Build Type  | Script Format                                       | Status                                     |
| ----------- | --------------------------------------------------- | ------------------------------------------ |
| **Debug**   | Uncompiled text scripts (`scn`/`ScriptName` format) | ✅ Supported via `ScriptParser.cs`         |
| **Release** | Compiled bytecode (ESM SCDA records)                | ✅ Supported via `ScriptDisassembler` tool |

### Uncompiled Scripts (Debug Builds)

Debug builds contain source script text in memory. These are successfully detected and extracted by the existing `ScriptParser` which looks for `scn`/`ScriptName` prefixes.

**Script Data Structure in Debug Memory:**

```
Offset -16: [4 bytes] Identifier (CRC/FormID?) e.g., 43 EC AF 70
Offset -12: [4 bytes] Script size (little-endian) e.g., 00 00 00 F0 = 240 bytes
Offset  -8: [8 bytes] Padding/flags (zeros)
Offset   0: Script text begins ("scn ScriptName...")
```

### Compiled Scripts (Release Builds)

Release builds contain compiled bytecode, which requires understanding:

- The bytecode instruction set (opcodes)
- Variable/reference encoding
- Control flow structures

**Key Finding**: The memory region where debug scripts reside (`0x01EB47C6` - `0x059D8E57`) contains **different data** in release builds - appears to be texture/model data (sequential index values), **not** compiled script bytecode.

**Virtual Address Analysis:**

- Debug script at file offset `0x01EB47C6` maps to **VA `0x43ECB050`**
- The release dump has this same VA captured (region 1487)
- But the content at `0x43ECB050` in release is floating-point vertex data, not scripts
- **Conclusion**: The game uses this memory region differently in debug vs release builds

**Investigation with BytecodeExplorer tool revealed:**

- No SCPT records with actual script data found in release dumps
- The script region in release dumps has 0% opcode-range matches
- Scripts in release builds are likely:
  1. Loaded from ESM/ESP files on-demand when scripts execute
  2. Not resident in memory at crash time (only actively running scripts would be loaded)
  3. Stored in a different memory region than debug builds allocate for source text

**Minidump Limitations:**

- Minidumps only capture memory that was allocated at crash time
- If scripts weren't actively executing, their bytecode may not be in memory
- Debug builds keep source text for error reporting; release builds don't need it
- The 177 MB captured represents only a snapshot, not the game's full address space

**Previous attempt** using heuristic-based decoding was unreliable. A proper solution requires:

1. Extracting opcode definitions from the executable/PDB ✅ (829 opcodes extracted)
2. Understanding the `Script` and `ScriptInfo` structures from symbols ✅ (documented)
3. Locating where compiled bytecode actually resides in release dumps ⏳
4. Building a proper disassembler based on actual game code

**Realistic Assessment**: Without a full memory dump or the actual ESM file, we may not be able to extract compiled scripts from these minidumps. The scripts live in the ESM and are only loaded into memory when executed.

---

## File Type Signatures

Currently detected signatures (see `FileTypeRegistry.cs`):

### Textures

- **DDX (3XDO)**: `3XDO` magic - Xbox 360 compressed textures
- **DDX (3XDR)**: `3XDR` magic - Alternate tiling (experimental)
- **DDS**: `DDS ` magic - DirectDraw Surface

### Audio

- **XMA**: Xbox Media Audio headers
- **LIP**: Lip sync data

### Models

- **NIF**: `NIF\0` / `NiF\0` magic - NetImmerse/Gamebryo models

### Data Files

- **ESP/ESM**: `TES4` magic - Bethesda plugin files
- **XUI**: Xbox UI definitions
- **XDBF**: Xbox Dashboard file format

### Executables

- **XEX**: `XEX2` magic - Xbox executable

---

## PowerPC Architecture Notes

### Endianness

- Xbox 360 uses big-endian byte order
- All multi-byte values must be byte-swapped when reading
- Affects: integers, floats, pointers

### Memory Layout

- Virtual address space mapping differs from PC
- Module base addresses from minidump provide mapping hints

---

## PDB Symbol Research

### Approach

1. Use `cvdump` to extract symbols from PDB files
2. Identify script-related structures (Script, ScriptInfo, etc.)
3. Map function signatures to understand bytecode format
4. Build opcode table from actual game implementation

### Key Structures to Find

- `Script` - Base script class
- `ScriptInfo` - Script metadata
- `ScriptEffect` - Magic effect scripts
- `CommandInfo` - Script command definitions
- `CommandTable` - Opcode to function mapping

### Known Function Patterns

From `pdb_script_functions.txt`:

- `Script::Execute`
- `Script::Compile`
- `Script::SetVariable`
- Command handlers: `Cmd_*_Execute`

---

## Research Tasks

### Completed ✅

- [x] Run cvdump on available PDB files
- [x] Extract complete type information for script structures
- [x] Build authoritative opcode table from PDB analysis (829 opcodes)
- [x] Document Script and SCRIPT_FUNCTION structures
- [x] Map virtual addresses to memory dump regions
- [x] Analyze memory dump to identify file type clustering
- [x] Document memory region boundaries for each file type
- [x] Create visual map of memory dump layout
- [x] Identify where compiled script bytecode lives in release dumps (ESM SCDA records)
- [x] Build compiled script bytecode disassembler
- [x] Build complete bytecode decompiler with control flow analysis
- [x] Correlate SCRO FormIDs with bytecode reference indices
- [x] Enhance RegionAnalyzer with script/string/gap analysis modes
- [x] Analyze release dump (xex19) - 98.3% carve rate, ESM records in gaps
- [x] Document debug vs release dump differences

### Immediate

- [x] Build ESM record extractor (GMST/GRUP/EDID/SCTX from gaps) ✅ `tools/ESMRecordExtractor/`
- [x] Fix NIF signature false positives (22K detected vs 171 valid) ✅ Added regex version validation
- [x] Create FormID→Name correlation tool across multiple dumps ✅ `tools/FormIdCorrelator/`
- [x] Correlate minidump module info with region analysis ✅ `tools/ModuleCorrelator/`
- [x] Classify Jacobstown.dmp (debug, release, or memdebug) ✅ Release MemDebug

### Short-term

- [ ] Understand Xbox 360 texture tiling patterns
- [x] Scan all 50 dumps for unique scripts (45 release, 3 debug, 2 memdebug) ✅ 597 SCDA, 570 with source
- [ ] Extract SCTX source text from all release dump ESM records

### Long-term

- [ ] Document any game-specific memory structures
- [ ] Build script comparison tools (bytecode vs source)

---

## Tools and Commands

### Main CLI Commands (Integrated)

```powershell
# Carve files from dump
Xbox360MemoryCarver dump.dmp -o output -v

# Analyze dump structure (text summary)
Xbox360MemoryCarver analyze dump.dmp

# Analyze dump structure (markdown report)
Xbox360MemoryCarver analyze dump.dmp -f md -o report.md

# List loaded modules
Xbox360MemoryCarver modules dump.dmp
Xbox360MemoryCarver modules dump.dmp -f md   # Markdown table
Xbox360MemoryCarver modules dump.dmp -f csv  # CSV export
```

### Extracting PDB Information

```powershell
# Dump all symbols from PDB
.\microsoft-pdb\cvdump\cvdump.exe "Fallout New Vegas (July 21, 2010)\FalloutNV\Fallout.pdb" > pdb_dump.txt

# Extract specific sections
.\microsoft-pdb\cvdump\cvdump.exe -p "Fallout New Vegas (July 21, 2010)\FalloutNV\Fallout.pdb"
```

### Research Tools (in `tools/`)

```powershell
# Map memory regions from manifest
dotnet run --project tools/MemoryRegionMapper -- Sample/MemoryDump/analysis_output/Fallout_Debug.xex/manifest.json tools/debug_memory_map.md

# Scan for compiled script bytecode (SCDA records)
dotnet run --project tools/ScriptDisassembler -- dump.dmp --scan

# Disassemble all scripts in a dump
dotnet run --project tools/ScriptDisassembler -- dump.dmp --opcodes tools/opcode_table.csv --disasm

# Extract bytecode and source to files
dotnet run --project tools/ScriptDisassembler -- dump.dmp --extract output_dir

# Analyze unidentified regions (strings, gaps, patterns)
dotnet run --project tools/RegionAnalyzer -- dump.dmp --strings
dotnet run --project tools/RegionAnalyzer -- dump.dmp manifest.json --gaps
dotnet run --project tools/RegionAnalyzer -- dump.dmp --sample 0x06EBD91E 512
```

### Generated Analysis Files

| File                               | Description                                    |
| ---------------------------------- | ---------------------------------------------- |
| `tools/debug_memory_map.md`        | Region analysis for debug build                |
| `tools/release_beta_memory_map.md` | Region analysis for release beta build         |
| `tools/opcode_table.csv`           | Complete opcode table (829 commands)           |
| `tools/opcode_table.json`          | Opcode table in JSON format                    |
| `tools/pdb_types_full.txt`         | Complete type definitions from debug PDB       |
| `tools/ScriptDisassembler/`        | Tool to extract and disassemble SCDA bytecode  |
| `tools/RegionAnalyzer/`            | Tool to analyze unidentified dump regions      |
| `tools/ESMRecordExtractor/`        | Tool to extract GMST/EDID/SCTX/SCRO records    |
| `tools/FormIdCorrelator/`          | Tool to correlate FormID↔EditorID across dumps |
| `tools/ModuleCorrelator/`          | Tool to analyze loaded modules from minidumps  |
| `tools/dump_script_scan.csv`       | Script scan results for all 50 dumps           |

---

## References

- [Microsoft Minidump Format](https://docs.microsoft.com/en-us/windows/win32/debug/minidump-files)
- [Xbox 360 XEX Format](https://free60.org/XEX)
- [DDS File Format](https://docs.microsoft.com/en-us/windows/win32/direct3ddds/dx-graphics-dds)
- [Bethesda ESM/ESP Format](https://en.uesp.net/wiki/Skyrim_Mod:Mod_File_Format) (similar to FNV)
- [Oblivion SCPT Format](https://en.uesp.net/wiki/Oblivion_Mod:Mod_File_Format/SCPT) (bytecode documentation)

---

## Changelog

### 2026-01-02 (Update 6) - Research Tools Integration

**Merged research tools into main carver codebase.**

#### New Core Parsers

| Parser            | Location                                    | Description                                                     |
| ----------------- | ------------------------------------------- | --------------------------------------------------------------- |
| `ScdaFormat`      | `Core/Formats/Scda/ScdaFormat.cs`           | Compiled script bytecode (SCDA) with `IDumpScanner`             |
| `EsmRecordFormat` | `Core/Formats/EsmRecord/EsmRecordFormat.cs` | ESM record extraction (EDID/GMST/SCTX/SCRO) with `IDumpScanner` |

#### New Analysis Module

| Class                | Location                     | Description                          |
| -------------------- | ---------------------------- | ------------------------------------ |
| `MemoryDumpAnalyzer` | `Core/MemoryDumpAnalyzer.cs` | Unified dump analysis with reporting |

#### New CLI Commands

The main Xbox360MemoryCarver CLI now includes:

```powershell
# Analyze dump structure and metadata
Xbox360MemoryCarver analyze dump.dmp              # Console summary
Xbox360MemoryCarver analyze dump.dmp -f md        # Markdown report
Xbox360MemoryCarver analyze dump.dmp -f md -o report.md  # Save report

# List loaded modules from minidump
Xbox360MemoryCarver modules dump.dmp              # Text output
Xbox360MemoryCarver modules dump.dmp -f md        # Markdown table
Xbox360MemoryCarver modules dump.dmp -f csv       # CSV export
```

#### Analysis Command Output

The `analyze` command provides:

- Build type detection (Debug, Release Beta, Release MemDebug)
- Module list with sizes
- SCDA record count and source availability
- ESM record statistics (EDID, GMST, SCTX, SCRO)
- FormID to EditorID correlation map

#### Research Tools Deprecation

The standalone tools in `tools/` remain for reference but functionality is now in the main codebase:

- `ModuleCorrelator` → `Xbox360MemoryCarver modules` command
- `ESMRecordExtractor` → `EsmRecordFormat` class (implements `IDumpScanner`)
- `FormIdCorrelator` → `MemoryDumpAnalyzer.CorrelateFormIdsToNames()`
- `ScriptDisassembler` → `ScdaFormat` class (implements `IDumpScanner`)

---

### 2026-01-02 (Update 5) - Complete Dump Analysis & Module Correlation

**Full analysis of all 50 dumps** completed with script scanning and module correlation.

#### Jacobstown Dump Classification

The unknown "Jacobstown.dmp" was classified as **Release MemDebug** build:

- Module list shows `Fallout_Release_MemDebug.exe`
- No script source text (0 scn files)
- Contains SCDA bytecode (1 record)
- 948 files carved, 175.71 MB

#### Script Scan Results (All 50 Dumps)

| Metric             | Value    |
| ------------------ | -------- |
| Total SCDA records | 597      |
| With source (SCTX) | 570      |
| Dumps with scripts | 29 of 50 |

**Top dumps by script count:**

| Dump              |     SCDA | With SCTX |
| ----------------- | -------: | --------: |
| Debug builds (×3) | 108 each |       108 |
| xex19.dmp         |      105 |       104 |
| xex20.dmp         |       71 |        68 |
| xex5.dmp          |       40 |        39 |
| xex42.dmp         |       11 |        10 |

Scripts are demand-loaded - most dumps have 0-1 SCDA records (21 dumps have none).

#### Module Correlation (`tools/ModuleCorrelator/`)

New tool to analyze loaded modules from minidump stream type 4:

```powershell
dotnet run --project tools/ModuleCorrelator -- dump.dmp --verbose
```

**Loaded Modules (common to all dumps):**

| Module        | Base Address |    Debug Size |  Release Size |
| ------------- | ------------ | ------------: | ------------: |
| xboxkrnld.exe | 0x80040000   |      1,536 KB |      1,536 KB |
| xamd.dll      | 0x81870000   |      7,338 KB |      7,338 KB |
| **Game EXE**  | 0x82000000   | **37,934 KB** | **23,216 KB** |
| party.dll     | 0x91260000   |        298 KB |        298 KB |
| hud.dll       | 0x913E0000   |        214 KB |        214 KB |
| XIMECORE.dll  | 0x91680000   |        179 KB |        179 KB |
| XMsgr.dll     | 0x91720000   |        313 KB |        313 KB |
| xbdm.dll      | 0x91F30000   |        682 KB |        682 KB |

**Key Finding**: Debug executable is ~15 MB larger than release (38 MB vs 23 MB) due to debug symbols and unoptimized code.

---

### 2026-01-02 (Update 4) - ESM Record Extractor, NIF Validation, FormID Correlator

**Three new tools added** to support ESM data extraction and FormID correlation.

#### ESM Record Extractor (`tools/ESMRecordExtractor/`)

New tool to scan memory dumps for ESM record fragments:

```powershell
dotnet run --project tools/ESMRecordExtractor -- dump.dmp --output report.md
```

**Results from xex19.dmp:**
| Record Type | Count | Description |
| ----------- | ------ | ----------------------------------- |
| GMST | 15,201 | Game settings (many duplicates) |
| EDID | 736 | Unique editor IDs |
| SCTX | 96 | Script source text fragments |
| SCRO | 110 | FormID references (34 unique) |

#### NIF Signature Validation Fix

Enhanced `NifParser.cs` to reduce false positives from 22,517 to 170 valid files:

- Added `GeneratedRegex` validation for version format: `^\d{1,2}\.\d{1,2}\.\d{1,2}\.\d{1,2}$`
- Checks for ", Version " string after "Gamebryo File Format"
- Binary version header validation (uint32 after newline)
- Major version range check (2.x - 30.x)

#### FormID Correlator (`tools/FormIdCorrelator/`)

New tool to map FormID hex values to editor ID names across multiple dumps:

```powershell
dotnet run --project tools/FormIdCorrelator -- dump1.dmp dump2.dmp --format md --output map.md
```

**Results from 3 dumps (xex19 + debug + xex5):**
| Metric | Value |
| ----------------------- | ----- |
| Total FormIDs with names| 1,219 |
| Total unique names | 1,226 |
| Single dump (xex19) | 732 |

Supports output formats: JSON, CSV, Markdown.

---

### 2026-01-02 (Update 3) - Release Dump Analysis & RegionAnalyzer Enhancement

**Comprehensive analysis of release dumps** - focused on the 50 available dumps (45 release, 3 debug, 1 memory debug, 1 unknown "Jacobstown").

#### Release Dump Statistics (xex19.dmp - 208 MB)

| Metric                | Value                      |
| --------------------- | -------------------------- |
| Total carved files    | 1,279                      |
| Carve coverage        | 98.3% (204.8 MB of 208 MB) |
| Script source (`scn`) | **0** (compiled only)      |
| FormID editor IDs     | 1,025 unique               |
| Quest/dialogue names  | 4,711 unique               |

#### File Type Distribution (Release)

| Type       | Count | Notes                        |
| ---------- | ----: | ---------------------------- |
| xui_scene  |   429 | UI definitions               |
| ddx_3xdo   |   262 | Textures                     |
| png        |   249 | UI images                    |
| nif        |   171 | Models                       |
| xma        |   100 | Audio                        |
| lip        |    35 | Lip sync                     |
| script_scn |     0 | No source scripts in release |

#### Gap Analysis

- **Largest gap**: 0x067B548A - 0x09C43874 (52.56 MB) - ESM GMST records
- **Total uncarved**: ~3.5 MB across 73 gaps
- **Categories**: 125.8 MB Mixed Binary, 6.5 MB Zero/Sparse, 0.53 MB String Tables

#### ESM Data in Release Dumps

Release dump gaps contain structured ESM record data:

| Record Type | Content Found                                            |
| ----------- | -------------------------------------------------------- |
| GMST        | Game settings (`sKarmicTitleEvil30`, `fStarsRotateDays`) |
| GRUP        | Record groups                                            |
| EDID        | Editor IDs (FormID names)                                |
| SCTX        | **Script source retained** for loaded scripts            |
| SCRO        | FormID references (4 bytes each)                         |

**Sample SCTX found at 0x067A675F:**

```
HeliosKlaxonREF.Enable
endif
; BEGIN DEMO SCRIPTING
; DEMO SCRIPTING END
```

Followed by SCRO FormIDs: `04 00 5D 2C 0E 00`, `04 00 2E EC 11 00`

#### String Tables Found

| Offset     | Count | Content                    |
| ---------- | ----: | -------------------------- |
| 0x00D08ABD | 3,690 | Audio asset paths (`.xma`) |
| 0x0B015150 |   689 | Shader parameter names     |
| 0x06E3ABF4 |   323 | UI element names           |
| 0x0A018C84 |   305 | FormID editor IDs          |

#### Key Findings: Debug vs Release

| Aspect                | Debug Build   | Release Build              |
| --------------------- | ------------- | -------------------------- |
| Script source (`scn`) | 1,168 scripts | 0 (compiled only)          |
| Carve coverage        | ~7%           | 98.3%                      |
| FormID names          | 1,148         | 1,025                      |
| Quest names           | 5,616         | 4,711                      |
| SCTX fragments        | N/A           | Present for loaded scripts |
| Texture data          | 1.2 MB        | 7.85 MB (more loaded)      |

#### Available Dumps (50 total)

| Build Type   | Count | Notes                                            |
| ------------ | ----: | ------------------------------------------------ |
| Release Beta |    45 | Most common, compiled scripts only               |
| Debug        |     3 | Contains source scripts                          |
| Memory Debug |     2 | Release with memory debugging (incl. Jacobstown) |

**Note**: Jacobstown.dmp was classified as Release MemDebug build based on module list showing `Fallout_Release_MemDebug.exe`.

#### RegionAnalyzer Tool Enhancements

New analysis modes added:

```powershell
dotnet run -- dump.dmp --scripts        # Find script names, variables, FormIDs
dotnet run -- dump.dmp --missed         # Scan for uncaptured file signatures
dotnet run -- dump.dmp --string-tables  # Analyze string table structures
dotnet run -- dump.dmp manifest.json --identify  # Categorize all uncarved regions
```

#### NIF False Positive Issue

The `--missed` scan reports 22,517 NIF signatures in release dumps but only 171 are actually valid. The NIF signature (`Gamebryo File Format`) needs additional validation (file size bounds, version check) to reduce false positives.

### 2026-01-02 (Update 2) - Unidentified Region Analysis

**Created RegionAnalyzer tool** to examine dump regions not matched by file carvers.

#### Key Findings

1. **XEX Code Section** (~32 MB): PowerPC executable code with debug strings
   - Contains function prologues (`mflr r12`), returns (`blr`), branches
   - UTF-16 source file paths embedded in debug builds

2. **INI Settings Table** (~35 KB at 0x06EBD91E):
   - Setting names: `fStarsRotateDays`, `fSneakBaseValue`, etc.
   - `SettingT<*>` structure is 12 bytes (name ptr + value + collection ptr)

3. **ESM Record Data** (scattered, ~400+ KB total):
   - DIAL/INFO dialogue strings with bytecode interleaved
   - Asset paths (.xma, .nif, .dds files)
   - Quest objectives, DLC content names, cell names
   - Record markers: STAT(299), INFO(299), CONT(280), FORM(219)

4. **Coverage Statistics** (Debug Build):
   - 1992 carved files, 1315 gaps between them
   - Largest gap: 32 MB (XEX code section)
   - Total gap size: ~145 MB (93% of dump)

#### RegionAnalyzer Tool

New tool at `tools/RegionAnalyzer/` with options:

- `--strings` - Find ASCII string regions
- `--gaps` - Show gaps between carved files
- `--patterns` - Scan for pointer tables, zero regions
- `--sample <offset> <len>` - Hex dump at offset

### 2026-01-02 - ScriptDisassembler Decompiler Complete

**Completed full decompilation** with human-readable output!

#### Decompiler Features

The ScriptDisassembler tool now produces properly formatted scripts:

- **Control flow indentation**: `if`/`else`/`elseif`/`endif` with proper nesting
- **Expression parsing**: Postfix notation decoded to infix with operators (`==`, `!=`, `&&`, `||`, `>=`, `<=`)
- **Function calls with parameters**: `AddScriptPackage(SCRO#2)`, `SetOpenState(1)`
- **Reference.variable patterns**: `SCRO#5.iLocal33` format for cross-script variable access
- **Source text integration**: SCTX preserved and formatted alongside bytecode decompilation
- **SCRO FormID mapping**: Header shows all FormID references for reverse lookup

#### Expression Encoding Details

Expressions use postfix (RPN) notation with a 0x20 Push prefix:

| Pattern                        | Meaning                | Example                   |
| ------------------------------ | ---------------------- | ------------------------- |
| `20 38`                        | Push literal '8'       | Integer value 8           |
| `20 3D 3D`                     | Push "==" operator     | Equality comparison       |
| `20 26 26`                     | Push "&&" operator     | Logical AND               |
| `72 XX XX 73 YY YY`            | `SCRO#XX.varYY`        | Reference.variable access |
| `58 [op] [len] [cnt] [params]` | Function in expression | `GetLinkedRef()`          |

#### Sample Output Format

```
; Script: VMS03 (Helios One Power Configuration)
; SCDA offset: 0x055406A0, size: 184 bytes
; SCTX offset: 0x055406F8, size: 216 bytes

; SCRO References (7):
;   SCRO#1 = FormID 0x000E2C5D
;   SCRO#2 = FormID 0x0011EC2E
;   ...

; === Original Source ===
if (VMS03.bRetargetComplete == 1 && VMS03.nPowerConfiguration != 5)
    VMS03SolarRetargetMarkerREF.Disable
endif

; === Bytecode Decompilation ===
if (SCRO#5.iLocal33 == 1 && SCRO#5.iLocal23 != 5)
    SetRef SCRO#1
    Disable()
endif
```

#### Extraction Results (xex19.dmp)

- **105 scripts** extracted to `.txt` files
- **105 bytecode** files saved as `.bin` for analysis
- Notable scripts include:
  - `VMS03` - Helios One power configuration
  - `VMS18` - Vault 21 Sarah quest
  - `VCG01MitchellHouseScript` - Doc Mitchell tutorial
  - `VFreeformQuarryJunction` - Quarry Junction deathclaw area
  - Terminal and NPC scripts with complex conditionals

#### Key Technical Findings

1. **Core opcodes vs FUNCTION opcodes**: Control flow uses 0x10-0x1F range; script commands use 0x100+ (FUNCTION\_\* constants)
2. **Little-endian encoding**: All values stored little-endian despite Xbox 360 being big-endian (game converts at load time)
3. **SCRO indices are 1-based**: First reference is SCRO#1, maps to first FormID in SCRO table
4. **Variable indices are script-local**: `varYY` refers to variable index within the referenced script
5. **Expression operators as ASCII**: `==` stored as bytes 0x3D 0x3D, `&&` as 0x26 0x26

### 2026-01-01 (Update 5) - ScriptDisassembler Tool

**Built working disassembler** for FNV compiled scripts!

#### Multi-Dump Analysis

Scanned 50+ memory dumps and found significant script data:

| Dump        | SCDA Count | Bytecode Size |
| ----------- | ---------- | ------------- |
| xex19.dmp   | 105        | 7,396 bytes   |
| xex20.dmp   | 71         | ~8,000 bytes  |
| xex5.dmp    | 40         | ~3,000 bytes  |
| Other dumps | 1-11 each  | varies        |

**Total: ~300+ unique SCDA records across all dumps**

#### ScriptDisassembler Tool

New tool at `tools/ScriptDisassembler/` that:

- Scans dumps for SCDA records
- Extracts bytecode + source text pairs
- Disassembles bytecode using opcode table
- Handles core opcodes (0x00-0x1F) and FUNCTION\_\* opcodes (0x100+)

Usage:

```powershell
# Scan dump for SCDA records
dotnet run --project tools/ScriptDisassembler -- dump.dmp --scan

# Disassemble all scripts
dotnet run --project tools/ScriptDisassembler -- dump.dmp --opcodes tools/opcode_table.csv --disasm

# Extract to files
dotnet run --project tools/ScriptDisassembler -- dump.dmp --extract output_dir
```

#### Sample Disassembly Output

```
[000] 0x001C SetRef SCRO#1
[004] 0x1097 AddScriptPackage (SCRO#2)
[013] 0x1191 ApplyImagespaceModifier (SCRO#3)
[022] 0x0015 Set SCRO#4.var4(float) (len=10)
```

Scripts successfully extracted include:

- VCG01 (Doc Mitchell's house tutorial)
- VMS03 (Helios One quest)
- VMS18 (Vault 21 quest)
- Various NPC scripts and terminal scripts

### 2026-01-01 (Update 4) - ESM Format Discovery

**BREAKTHROUGH**: Found compiled scripts ARE in memory, stored in **ESM plugin record format**.

#### ESM Script Subrecords Found in Release Dump

| Marker | Count | Size        | Description                                  |
| ------ | ----- | ----------- | -------------------------------------------- |
| SCHR   | ~35   | 20 bytes    | Script Header                                |
| SCTX   | ~8    | variable    | Script Source Text (retained for debugging!) |
| SCDA   | 3     | 14-60 bytes | **Compiled Script Data (bytecode)**          |
| SCRO   | 11    | 4 bytes     | Script Object Reference (FormID)             |

#### Sample Decoded (VMS18 Quest Stage)

**SCDA bytecode** (60 bytes at `0x0553DE3A`):

```
A3 11 0F 00 03 00 72 03 00 6E 3C 00 00 00 6E 01
00 00 00 1C 00 01 00 9E 10 05 00 01 00 72 04 00
1C 00 01 00 97 10 05 00 01 00 72 05 00 1C 00 02
00 DD 10 07 00 01 00 6E 01 00 00 00
```

**Corresponding SCTX source**:

```
SetObjectiveDisplayed VMS18 60 1;
VMS18AssassinREF.MoveTo VMS18AssassinWarpMarkerREF;
VMS18AssassinREF.AddScriptPackage VMS18AssassinKillChauncey;
VMS18SteamRoomDoorREF.SetOpenState 1
```

#### Key Discoveries

1. **Game loads ESM records directly into memory** - not just parsed bytecode, but full record structure with SCHR/SCTX/SCDA/SCRO subrecords
2. **Source text retained in release builds** - SCTX subrecords preserved, possibly for error messages
3. **Very few scripts loaded at crash time** - only 3 SCDA records in 177 MB dump
4. **Scripts are demand-loaded** - most scripts not resident unless actively executing
5. **Bytecode uses core opcodes below 0x100** - `Set` is opcode `0x15`, not in FUNCTION\_\* table (those start at 256)

#### Bytecode Format (Decoded!)

Using the [Oblivion SCPT documentation](https://en.uesp.net/wiki/Oblivion_Mod:Mod_File_Format/SCPT), which is nearly identical to FNV:

##### Core Opcodes (0x00-0xFF)

| Opcode | Name       | Format                                           |
| ------ | ---------- | ------------------------------------------------ |
| 0x10   | Begin      | `10 00 [ModeLen] [Mode] [BlkLen(4)]`             |
| 0x11   | End        | `11 00 00 00`                                    |
| 0x15   | Set        | `15 00 [Length] [VarRef] [ExpLen] [Expression]`  |
| 0x16   | If         | `16 00 [CompLen] [JmpOps] [ExpLen] [Expression]` |
| 0x17   | Else       | `17 00 02 00 [JmpOps]`                           |
| 0x18   | ElseIf     | `18 00 ...` (same as If)                         |
| 0x19   | EndIf      | `19 00 00 00`                                    |
| 0x1C   | SetRef     | `1C 00 [Index]` - Set current reference          |
| 0x1D   | ScriptName | `1D 00 00 00`                                    |
| 0x1E   | Return     | `1E 00 00 00`                                    |

##### Data Type Markers

| Marker | Type       | Format                                                |
| ------ | ---------- | ----------------------------------------------------- |
| 0x20   | Push       | Stack push for expression evaluation                  |
| 0x47   | Global     | `47 [Index(2)]` - SCRO reference to global            |
| 0x58   | ExprFunc   | `58 [FuncCode(2)] [ParamBytes] [ParamCount] [Params]` |
| 0x5A   | Reference  | `5A [Index(2)]` - Standalone reference                |
| 0x66   | FloatLocal | `66 [Index(2)]` - Float variable                      |
| 0x6E   | LongParam  | `6E [Value(4)]` - 32-bit integer parameter            |
| 0x72   | RefCall    | `72 [Index(2)]` - Reference + function/var            |
| 0x73   | IntLocal   | `73 [Index(2)]` - Short/Long variable                 |
| 0x7A   | FloatParam | `7A [Value(8)]` - Double-precision float              |

##### Sample Decode: `set VFreeformNellis.iJackLoveStory to 8`

```
Bytecode: 15 00 0A 00 72 01 00 73 16 00 02 00 20 38
         |-----|-----|--------|--------|-----|-----
         Set   Len=10 Ref#1    Var#22  ExpLen Push '8'
```

- `15 00` - Set opcode
- `0A 00` - Total length: 10 bytes
- `72 01 00` - Reference SCRO #1 (VFreeformNellis quest form)
- `73 16 00` - Integer variable index 22 in target script
- `02 00` - Expression length: 2 bytes
- `20 38` - Push (0x20) + ASCII '8' (0x38)

##### Sample Decode: VMS18 Quest Stage (60 bytes)

```
Bytecode: A3 11 0F 00 03 00 72 03 00 6E 3C 00 00 00 6E 01 00 00 00...
```

Partial decode:

- `A3 11` = Opcode 0x11A3 = `SetObjectiveDisplayed` (from opcode table!)
- `0F 00` = Parameter length: 15 bytes
- `03 00` = Parameter count: 3
- `72 03 00` = Reference SCRO #3 (VMS18 quest)
- `6E 3C 00 00 00` = Long param: 60 (0x3C = objective index)
- `6E 01 00 00 00` = Long param: 1 (display flag)

Then:

- `1C 00 01 00` = SetRef to SCRO #1 (VMS18AssassinREF)
- `9E 10` = Opcode 0x109E = `MoveTo`
- ...and so on

**Next Steps**:

- Build proper disassembler combining core opcodes (0x00-0xFF) with FUNCTION\_\* opcodes (0x100+)
- Correlate SCRO FormIDs with bytecode reference indices
- Handle expression parsing (postfix notation)

### 2026-01-01 (Update 3)

- Created BytecodeExplorer tool for bytecode pattern analysis
- Compared debug vs release dumps at script region:
  - Debug: Text scripts with 16-byte header (ID + size + padding)
  - Release: Different data entirely (sequential indices, texture/model data)
- Searched for SCPT records - found only form type table, no actual script data
- Key conclusion: Compiled scripts not stored in same memory region as debug scripts
- Updated research document with investigation findings

### 2026-01-01 (Update 2)

- Created MemoryRegionMapper tool for manifest analysis
- Analyzed debug and release beta dumps:
  - Debug: 1,992 files, 146 MB, includes 1,172 scripts
  - Release Beta: 977 files, 177 MB, no text scripts (compiled bytecode)
- Documented file type distribution by offset
- Created ASCII memory map visualizations
- Key finding: Scripts occupy 0x01EB47C6-0x059D8E57 range in debug build (~59 MB)
- Key finding: Release build has 7.85 MB of textures vs 1.2 MB in debug

### 2026-01-01

- Initial document creation
- Removed compiled script parsing code (unreliable)
- Beginning fresh research approach based on PDB symbols
