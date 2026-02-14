# Fallout Xbox 360 Utils

A .NET 10.0 toolkit for Xbox 360 memory dump analysis, ESM/NIF format conversion, file carving, and game data exploration. Features a **WinUI 3 GUI** on Windows and a **cross-platform CLI** for batch processing.

![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4)
![Platform](https://img.shields.io/badge/Platform-Windows%20%7C%20Linux%20%7C%20macOS-blue)
![License](https://img.shields.io/badge/License-MIT-green)

## Features

### GUI (Windows)

- **ESM Data Browser**: Explore ESM records with search, property display, and GECK-style flag decoding
- **Dialogue Viewer**: Browse NPC dialogue trees by speaker, quest, and topic
- **World Map**: Interactive heightmap visualization with cell navigation and placed object overlay
- **Hex Viewer**: Virtual-scrolling hex editor supporting 200MB+ files with minimap overview
- **Memory Carver**: File signature detection, extraction, and DDX/XMA conversion
- **BSA Extractor**: Extract Bethesda archive files with Xbox 360 to PC conversion
- **NIF Converter**: Xbox 360 to PC NIF mesh conversion (geometry expansion, endian conversion)
- **DDX Converter**: Batch DDX to DDS texture conversion
- **Repacker**: Rebuild Xbox 360 memory regions with modified assets

### CLI (Cross-platform)

| Command | Description |
| --- | --- |
| `carve` | Extract files from memory dumps with type filtering |
| `analyze` | Full ESM semantic reconstruction with GECK-format CSV/text reports |
| `esm` | Convert Xbox 360 ESM to PC format (GECK compatible) |
| `nif` | Convert Xbox 360 NIF meshes to PC format |
| `bsa` | Extract BSA archives |
| `dialogue` | Browse and export NPC dialogue trees |
| `world` | Explore worldspace data, heightmaps, and placed objects |
| `compare` | Compare ESM files (converted vs. PC reference) |
| `modules` | List loaded modules from memory dumps |
| `coverage` | Analyze memory region coverage |

### Format Support

| Category | Formats |
| --- | --- |
| Game Data | ESM/ESP (Xbox 360 and PC, with full conversion) |
| Models | NIF (Xbox 360 to PC conversion with geometry expansion) |
| Archives | BSA (Bethesda Softworks Archive) |
| Textures | DDX (3XDO/3XDR), DDS, PNG |
| Audio | XMA (Xbox Media Audio), LIP (lip sync) |
| Scripts | ObScript bytecode (decompilation + comparison) |
| Executables | XEX (Xbox Executable) |
| UI | XDBF (Xbox Dashboard) |
| Crash dumps | Xbox 360 minidumps with PDB-aware struct reading |

## Installation

### Pre-built Releases

Download from [Releases](https://github.com/slfx77/xbox-360-minidump-extractor/releases):

| Platform | Download |
| --- | --- |
| Windows GUI | `FalloutXbox360Utils-Windows-GUI-x64.zip` |
| Windows CLI | `FalloutXbox360Utils-Windows-CLI-x64.zip` |
| Linux CLI | `FalloutXbox360Utils-Linux-CLI-x64.tar.gz` |

### Build from Source

Requires [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```bash
# Clone with submodules
git clone --recursive https://github.com/slfx77/xbox-360-minidump-extractor.git
cd xbox-360-minidump-extractor

# Build all targets
dotnet build -c Release

# Run GUI (Windows only)
dotnet run --project src/FalloutXbox360Utils -f net10.0-windows10.0.19041.0

# Run CLI (cross-platform)
dotnet run --project src/FalloutXbox360Utils -f net10.0 -- --help

# Run tests
dotnet test -p:CollectCoverage=false
```

## Usage

### GUI Mode (Windows)

Launch without arguments for the GUI, or auto-load a file:

```bash
FalloutXbox360Utils.exe
FalloutXbox360Utils.exe path/to/dump.dmp
```

Tabs: **Single File** (ESM browser, dialogue, world map, hex viewer) | **BSA Extractor** | **NIF Converter** | **DDX Converter** | **Repacker** | **Batch Mode**

### CLI Mode

```bash
# Extract files from a memory dump
FalloutXbox360Utils carve dump.dmp -o output -t ddx xma nif --convert-ddx

# Analyze ESM from a memory dump (generates GECK-format reports)
FalloutXbox360Utils analyze dump.dmp -o reports/

# Convert Xbox 360 ESM to PC format
FalloutXbox360Utils esm convert Sample/ESM/360_final/FalloutNV.esm -o FalloutNV.pc.esm

# Convert Xbox 360 NIF to PC format
FalloutXbox360Utils nif mesh.nif -o mesh_pc.nif

# Browse dialogue
FalloutXbox360Utils dialogue dump.dmp --npc CraigBoone

# Explore worldspace
FalloutXbox360Utils world dump.dmp --worldspace WastelandNV

# Force CLI mode on Windows (otherwise defaults to GUI)
FalloutXbox360Utils --no-gui dump.dmp -o output
```

### Developer Tools

Standalone analysis tools for development and debugging:

```bash
# ESM analysis and comparison
dotnet run --project tools/EsmAnalyzer -c Release -- stats FalloutNV.esm
dotnet run --project tools/EsmAnalyzer -c Release -- semdiff converted.esm pc_reference.esm -t NPC_

# Memory dump script analysis
dotnet run --project tools/MinidumpAnalyzer -- scripts dump.dmp

# NIF structure analysis
dotnet run --project tools/NifAnalyzer -f net10.0 -- info mesh.nif
```

## ESM Conversion

The ESM converter handles Xbox 360 to PC format conversion for Fallout: New Vegas master files:

- **Endian conversion**: Record/subrecord headers and data fields (hybrid big/little-endian)
- **Split INFO merging**: Xbox 360's split dialogue records merged to match PC format
- **Schema-driven**: Field types defined in `SubrecordSchemaRegistry` for correct byte-swapping
- **GECK compatible**: Output loads in the Garden of Eden Creation Kit

## Script Decompiler

Decompiles ObScript bytecode (SCDA subrecords) back to readable script source:

- Full opcode coverage for Fallout: New Vegas
- Cross-script variable resolution via SCRO/SCRV reference chains
- FormID to EditorID resolution for human-readable output
- Semantic comparison between original SCTX source and decompiled output

## Project Structure

```
src/FalloutXbox360Utils/
├── App/                     # WinUI 3 GUI (Windows only)
│   ├── Controls/            #   WorldMapControl
│   ├── Helpers/             #   Tree builders, display helpers
│   ├── Models/              #   Session state, view models
│   └── Tabs/                #   SingleFile, BSA, NIF, DDX, Repack, Batch
├── CLI/                     # Cross-platform CLI commands
├── Core/                    # Format libraries
│   ├── Carving/             #   File signature detection and extraction
│   ├── Formats/
│   │   ├── Bsa/             #   BSA archive extraction
│   │   ├── Ddx/             #   DDX texture parsing
│   │   ├── Esm/             #   ESM parsing, conversion, export, runtime readers
│   │   └── Nif/             #   NIF mesh parsing and conversion
│   ├── Minidump/            #   Xbox 360 minidump parsing
│   └── Utils/               #   Binary utilities
└── Repack/                  # Memory region repacking

src/DDXConv/                 # DDX conversion library (submodule)

tools/
├── EsmAnalyzer/             # ESM comparison, semantic diff, conversion
├── MinidumpAnalyzer/        # Runtime memory analysis, script extraction
├── NifAnalyzer/             # NIF structure inspection
└── ...                      # Additional analysis tools
```

## External Dependencies

Some features require external tools. The GUI shows a notification on startup if any are missing.

### FFmpeg (XMA audio conversion)

XMA to WAV conversion requires [FFmpeg](https://www.ffmpeg.org/download.html) on PATH or at `C:\ffmpeg\bin\`. Without it, XMA files are extracted but not converted to WAV.

## Documentation

- [Xbox 360 ESM Format](docs/Xbox_360_ESM_Format.md) - ESM binary format and hybrid endianness
- [ESM Conversion Transforms](docs/Xbox_360_ESM_Conversion_Transforms.md) - Conversion field mappings
- [DDX Format](docs/Xbox_360_DDX_Format.md) - DDX texture format documentation
- [PDB Runtime Structures](docs/PDB_Runtime_Structures.md) - Gamebryo runtime struct layouts
- [Script Bytecode Format](docs/PDB_Script_Bytecode_Format.md) - ObScript SCDA bytecode format

## License

MIT License - See [LICENSE](LICENSE) for details.

### Third-Party Components

| Component | License | Usage |
| --- | --- | --- |
| [DDXConv](https://github.com/GamesPastOrg/DDXConv) | [MIT](https://github.com/GamesPastOrg/DDXConv/blob/master/LICENSE) | DDX to DDS texture conversion (forked, built-in) |
| [NifSkope nif.xml](https://github.com/fo76utils/nifskope) | [BSD-3-Clause](https://github.com/fo76utils/nifskope/blob/develop/LICENSE.md) | NIF format schema (embedded) |

## Acknowledgments

- [AlexxEG/BSA_Browser](https://github.com/AlexxEG/BSA_Browser) - BSA format reference
- [GamesPastOrg/DDXConv](https://github.com/GamesPastOrg/DDXConv) - DDX texture conversion (MIT, Copyright 2026 Kran)
- [fo76utils/NifSkope](https://github.com/fo76utils/nifskope) - NIF format schema (BSD-3-Clause)
- [Xenia](https://github.com/xenia-project/xenia) - Format documentation and research
