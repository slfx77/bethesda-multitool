# Fallout Xbox 360 Utils

A .NET 10.0 toolkit for Xbox 360 memory dump analysis, ESM/NIF format conversion, file carving, and game data exploration. Features a **WinUI 3 GUI** on Windows, a **cross-platform CLI** for batch processing, and a standalone **Audio Transcriber** for voice file transcription.

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
| `save` | Inspect Xbox 360 Fallout NV save game files |
| `compare` | Compare ESM files (converted vs. PC reference) |
| `modules` | List loaded modules from memory dumps |
| `coverage` | Analyze memory region coverage |

### Audio Transcriber (Windows)

A standalone companion app for transcribing Fallout: New Vegas voice files using [Whisper](https://github.com/openai/whisper) speech-to-text. See the [Audio Transcriber](#audio-transcriber) section below for details.

### Format Support

| Category | Formats |
| --- | --- |
| Game Data | ESM/ESP (Xbox 360 and PC, with full conversion), FOS (save games) |
| Models | NIF (Xbox 360 to PC conversion with geometry expansion) |
| Archives | BSA (Bethesda Softworks Archive) |
| Textures | DDX (3XDO/3XDR), DDS, PNG |
| Audio | XMA (Xbox Media Audio), WAV, LIP (lip sync) |
| Scripts | ObScript bytecode (decompilation + comparison) |
| Executables | XEX (Xbox Executable) |
| UI | XDBF (Xbox Dashboard) |
| Crash dumps | Xbox 360 minidumps with PDB-aware struct reading |

## Installation

### Pre-built Releases

Download from [Releases](https://github.com/slfx77/fallout-xbox-360-utils/releases):

| Platform | Download |
| --- | --- |
| Windows GUI | `FalloutXbox360Utils-Windows-GUI-x64.zip` |
| Windows CLI | `FalloutXbox360Utils-Windows-CLI-x64.zip` |
| Linux CLI | `FalloutXbox360Utils-Linux-CLI-x64.tar.gz` |
| Audio Transcriber | `FalloutAudioTranscriber-Windows-x64.zip` |

### Build from Source

Requires [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```bash
# Clone with submodules
git clone --recursive https://github.com/slfx77/fallout-xbox-360-utils.git
cd fallout-xbox-360-utils

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

# Inspect a save game
FalloutXbox360Utils save info savegame.fos

# Force CLI mode on Windows (otherwise defaults to GUI)
FalloutXbox360Utils --no-gui dump.dmp -o output
```

## Audio Transcriber

The **Fallout Audio Transcriber** is a standalone WinUI 3 application for browsing and transcribing Fallout: New Vegas voice files. It is provided as a precompiled download in [Releases](https://github.com/slfx77/fallout-xbox-360-utils/releases).

### What it does

- Loads voice audio files (XMA, WAV) from Bethesda BSA archives
- Plays back voice lines with an integrated audio player
- Transcribes speech to text using [Whisper.net](https://github.com/sandrohanea/whisper.net) (OpenAI Whisper, runs locally)
- Cross-references voice files against the ESM to display speaker names, quest context, and existing subtitle text
- Saves transcription projects for incremental work across sessions

### Getting started

1. Download and extract `FalloutAudioTranscriber-Windows-x64.zip` from [Releases](https://github.com/slfx77/fallout-xbox-360-utils/releases)
2. Launch `FalloutAudioTranscriber.exe`
3. Point it at a Fallout: New Vegas `Data` directory containing voice BSA files (e.g., `Fallout - Voices1.bsa`)
4. The app parses all voice BSAs, cross-references with `FalloutNV.esm` if present, and presents a browsable playlist

### Transcription

- On first use, the Whisper model (`ggml-base.en`, ~148 MB) is automatically downloaded to `%LocalAppData%\FalloutAudioTranscriber\models\`
- Audio is resampled to 16kHz mono before transcription
- Transcriptions are saved alongside the Data directory and persist across sessions
- Voice files with existing ESM subtitles (NAM1) are shown alongside Whisper transcriptions for comparison

### Requirements

- Windows 10 (build 17763+) or later
- No additional dependencies required (self-contained build with Whisper runtime included)

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

## Developer Tools

Standalone CLI tools for format analysis and debugging. These are not included in precompiled releases -- build from source with `dotnet run --project tools/<name>`.

| Tool | Description |
| --- | --- |
| `tools/EsmAnalyzer` | ESM analysis, comparison, semantic diff, format conversion, WRLD OFST streaming analysis, worldmap visualization |
| `tools/NifAnalyzer` | NIF mesh structure inspection, vertex/geometry comparison, skin partition and Havok physics debugging |
| `tools/TextureAnalyzer` | DDX/DDS texture analysis, decompression, block map visualization, format conversion |
| `tools/MinidumpAnalyzer` | Xbox 360 minidump memory region analysis, module enumeration, FaceGen extraction, script analysis |
| `tools/BsaAnalyzer` | BSA archive inspection, file search by pattern, entry comparison, file type statistics |
| `tools/PdbAnalyzer` | PDB symbol analysis and function extraction |
| `tools/TerrainAnalyzer` | Terrain and heightmap analysis and visualization |
| `tools/SignatureScanner` | File signature scanning utilities |
| `tools/LzxVerify` | LZX compression verification |

```bash
# ESM analysis and comparison
dotnet run --project tools/EsmAnalyzer -c Release -- stats FalloutNV.esm
dotnet run --project tools/EsmAnalyzer -c Release -- semdiff converted.esm pc_reference.esm -t NPC_

# Memory dump script analysis
dotnet run --project tools/MinidumpAnalyzer -- scripts dump.dmp

# NIF structure analysis
dotnet run --project tools/NifAnalyzer -- info mesh.nif

# Texture analysis
dotnet run --project tools/TextureAnalyzer -- info texture.ddx

# BSA file search
dotnet run --project tools/BsaAnalyzer -- find archive.bsa "*.nif"
```

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
│   │   ├── Nif/             #   NIF mesh parsing and conversion
│   │   └── SaveGame/        #   Xbox 360 save game (FOS/STFS) parsing
│   ├── Minidump/            #   Xbox 360 minidump parsing
│   └── Utils/               #   Binary utilities
└── Repack/                  # Memory region repacking

src/FalloutAudioTranscriber/  # Whisper-based voice file transcriber (WinUI 3)
src/DDXConv/                  # DDX conversion library (submodule)

tools/
├── EsmAnalyzer/             # ESM comparison, semantic diff, conversion
├── NifAnalyzer/             # NIF structure inspection and comparison
├── TextureAnalyzer/         # DDX/DDS texture analysis
├── MinidumpAnalyzer/        # Runtime memory analysis, script extraction
├── BsaAnalyzer/             # BSA archive inspection
├── PdbAnalyzer/             # PDB symbol analysis
├── TerrainAnalyzer/         # Terrain/heightmap analysis
├── SignatureScanner/        # File signature scanning
├── LzxVerify/               # LZX compression verification
└── Shared/                  # Shared CLI strings library
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
- [RTTI to ESM Coverage](docs/RTTI-ESM-Coverage.md) - C++ RTTI class to ESM record type mapping

## License

MIT License - See [LICENSE](LICENSE) for details.

### Third-Party Components (included in repository)

| Component | License | Usage |
| --- | --- | --- |
| [DDXConv](https://github.com/GamesPastOrg/DDXConv) | [MIT](https://github.com/GamesPastOrg/DDXConv/blob/master/LICENSE) | DDX to DDS texture conversion (forked, built-in) |
| [NifSkope nif.xml](https://github.com/fo76utils/nifskope) | [BSD-3-Clause](https://github.com/fo76utils/nifskope/blob/develop/LICENSE.md) | NIF format schema (embedded) |
| [Xenia](https://github.com/xenia-project/xenia) | [BSD-3-Clause](https://github.com/xenia-project/xenia/blob/master/LICENSE) | Xbox 360 texture tiling code (in DDXConv) |

## Acknowledgments

### Tools & Libraries

- [Veldrid](https://github.com/veldrid/veldrid) - GPU rendering abstraction (MIT)
- [Spectre.Console](https://github.com/spectreconsole/spectre.console) - CLI output formatting (MIT)
- [System.CommandLine](https://github.com/dotnet/command-line-api) - CLI argument parsing (MIT)
- [Magick.NET](https://github.com/dlemstra/Magick.NET) - Image processing (Apache-2.0)
- [SixLabors.ImageSharp](https://github.com/SixLabors/ImageSharp) - Image processing (Apache-2.0)
- [BCnEncoder.Net](https://github.com/Nominom/BCnEncoder.NET) - Block compression encoding (MIT)
- [Whisper.net](https://github.com/sandrohanea/whisper.net) - Speech-to-text transcription (MIT)
- [NAudio](https://github.com/naudio/NAudio) - Audio playback and resampling (MIT)
- [xunit](https://github.com/xunit/xunit) - Unit testing (Apache-2.0)
- [microsoft/microsoft-pdb](https://github.com/microsoft/microsoft-pdb) - PDB format and cvdump tool (MIT)
- [wbenny/pdbex](https://github.com/wbenny/pdbex) - PDB struct layout extraction (MIT)
- [0dinD/ghidra](https://github.com/0dinD/ghidra) - VMX128 PowerPC SLEIGH definitions for Ghidra

### Format References

- [xEdit / TES5Edit](https://github.com/TES5Edit) - ESM format documentation
- [AlexxEG/BSA_Browser](https://github.com/AlexxEG/BSA_Browser) - BSA format reference
- [fo76utils/NifSkope](https://github.com/fo76utils/nifskope) - NIF format documentation (BSD-3-Clause)
- [GamesPastOrg/DDXConv](https://github.com/GamesPastOrg/DDXConv) - DDX texture conversion (MIT, Copyright 2026 Kran)
