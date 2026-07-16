# Bethesda Multitool

A .NET 10.0 toolkit for analyzing and converting Bethesda game data across The Elder Scrolls and Fallout titles (Morrowind through Starfield), on PC and console. It handles Xbox 360 memory dump analysis, ESM/NIF/BSA format conversion, file carving, and game data exploration, with a **WinUI 3 GUI** on Windows, a **cross-platform CLI** for batch processing, and a standalone **Audio Transcriber** for voice file transcription.

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
- **NIF Tools**: Batch Convert (Xbox 360 → PC NIF mesh conversion with geometry expansion + endian conversion) + Viewer (browse a folder / BSA, inspect blocks, render PNG, export GLB)
- **DDX Converter**: Batch DDX to DDS texture conversion
- **Repacker**: Rebuild Xbox 360 memory regions with modified assets

### CLI (Cross-platform)

| Command | Description |
| --- | --- |
| *(default)* | Carve files from a memory dump or DDX by type (`-t dds ddx xma nif`), with optional conversion |
| `analyze` | Analyze a memory dump's structure; optionally extract ESM records and GECK-style reports |
| `esm` | Analyze ESM/ESP plugins and convert Xbox 360 → PC (GECK compatible) |
| `convert-nif` | Convert Xbox 360 NIF meshes to PC format |
| `convert-ddx` | Convert DDX textures to DDS |
| `bsa` / `ba2` | Inspect and extract BSA / BA2 archives |
| `dialogue` | Browse and export NPC dialogue trees |
| `world` | Explore worldspace data, heightmaps, and placed objects |
| `render` / `export` | Render NIF/NPC models to PNG, or export to GLB |
| `save` | Inspect Fallout 3/NV save game files |
| `dmp` | Memory dump analysis (modules, regions, coverage, cross-build compare, …) plus `dmp to-esm` — rebuild a loadable ESM/ESP plugin from a dump |
| `search` / `stats` / `list` / `show` / `diff` | Format-agnostic inspection of any ESM/ESP/DMP file |
| `version-track` | Track game data changes across development builds |

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
| Scripts | ObScript bytecode + Papyrus (`.pex`) — decompilation + comparison |
| Executables | XEX (Xbox Executable) |
| UI | XDBF (Xbox Dashboard) |
| Crash dumps | Xbox 360 minidumps with PDB-aware struct reading |

## Installation

### Pre-built Releases

Download from [Releases](https://github.com/slfx77/fallout-xbox-360-utils/releases):

| Platform | Download |
| --- | --- |
| Windows GUI | `BethesdaMultitool-Windows-GUI-x64.zip` |
| Windows CLI | `BethesdaMultitool-Windows-CLI-x64.zip` |
| Linux CLI | `BethesdaMultitool-Linux-CLI-x64.tar.gz` |
| Audio Transcriber | `BethesdaAudioTranscriber-Windows-x64.zip` |

### Build from Source

Requires [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```bash
# Clone with submodules
git clone --recursive https://github.com/slfx77/fallout-xbox-360-utils.git
cd fallout-xbox-360-utils

# Build all targets
dotnet build -c Release

# Run GUI (Windows only)
dotnet run --project src/BethesdaMultitool -f net10.0-windows10.0.19041.0

# Run CLI (cross-platform)
dotnet run --project src/BethesdaMultitool -f net10.0 -- --help

# Run tests
dotnet test -p:CollectCoverage=false
```

## Usage

### GUI Mode (Windows)

Launch without arguments for the GUI, or auto-load a file:

```bash
BethesdaMultitool.exe
BethesdaMultitool.exe path/to/dump.dmp
```

Tabs: **Single File** (ESM browser, dialogue, world map, hex viewer) | **BSA Extractor** | **NIF Tools** (Batch Convert + Viewer) | **DDX Converter** | **Repacker** | **Batch Mode**

### CLI Mode

```bash
# Carve files from a memory dump (the default command — no subcommand)
BethesdaMultitool dump.dmp -o output -t ddx xma nif --convert-ddx

# Analyze a memory dump's structure (add -s to emit a GECK-style report)
BethesdaMultitool analyze dump.dmp -o report.txt

# Convert Xbox 360 ESM to PC format
BethesdaMultitool esm convert Sample/ESM/360_final/FalloutNV.esm -o FalloutNV.pc.esm

# Convert Xbox 360 NIF to PC format
BethesdaMultitool convert-nif mesh.nif -o output/

# Browse dialogue by NPC
BethesdaMultitool dialogue npc dump.dmp CraigBoone

# Render a worldspace heightmap
BethesdaMultitool world heightmap FalloutNV.esm -o map.png -w WastelandNV

# Inspect a save game
BethesdaMultitool save info savegame.fos

# Force CLI mode on Windows (otherwise defaults to GUI)
BethesdaMultitool --no-gui dump.dmp -o output
```

## Audio Transcriber

The **Bethesda Audio Transcriber** is a standalone WinUI 3 application for browsing and transcribing Fallout: New Vegas voice files. It is provided as a precompiled download in [Releases](https://github.com/slfx77/fallout-xbox-360-utils/releases).

### What it does

- Loads voice audio files (XMA, WAV) from Bethesda BSA archives
- Plays back voice lines with an integrated audio player
- Transcribes speech to text using [Whisper.net](https://github.com/sandrohanea/whisper.net) (OpenAI Whisper, runs locally)
- Cross-references voice files against the ESM to display speaker names, quest context, and existing subtitle text
- Saves transcription projects for incremental work across sessions

### Getting started

1. Download and extract `BethesdaAudioTranscriber-Windows-x64.zip` from [Releases](https://github.com/slfx77/fallout-xbox-360-utils/releases)
2. Launch `BethesdaAudioTranscriber.exe`
3. Point it at a Fallout: New Vegas `Data` directory containing voice BSA files (e.g., `Fallout - Voices1.bsa`)
4. The app parses all voice BSAs, cross-references with `FalloutNV.esm` if present, and presents a browsable playlist

### Transcription

- On first use, the Whisper model (`ggml-base.en`, ~148 MB) is automatically downloaded to `%LocalAppData%\BethesdaAudioTranscriber\models\`
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

Decompiles game script bytecode back to readable source across engines:

- **ObScript** (SCDA subrecords): full Fallout 3 / New Vegas opcode coverage, plus Oblivion
- **Papyrus** (`.pex`): Skyrim, Fallout 4, and Fallout 76 compiled-script decompilation
- Cross-script variable resolution via SCRO/SCRV reference chains
- FormID to EditorID resolution for human-readable output
- Semantic comparison between original SCTX source and decompiled output

## Developer Tools

Standalone CLI tools for format analysis and debugging. These are not included in precompiled releases -- build from source with `dotnet run --project tools/<name>`.

| Tool | Description |
| --- | --- |
| `tools/EsmAnalyzer` | ESM/DMP debugging: comparison, semantic diff, conversion, WRLD OFST streaming, worldmap visualization, dump script analysis |
| `tools/NifAnalyzer` | NIF mesh structure inspection, vertex/geometry comparison, skin partition and Havok physics debugging |
| `tools/TextureAnalyzer` | DDX/DDS texture analysis, decompression, block map visualization, format conversion |
| `tools/EgtAnalyzer` | FaceGen EGT texture analysis |
| `tools/PdbAnalyzer` | PDB symbol analysis, struct layout generation, function extraction |
| `tools/RttiScanner` | RTTI and operator-new extraction from raw binaries |
| `tools/TerrainAnalyzer` | Terrain and heightmap analysis and visualization |
| `tools/SignatureScanner` | File signature scanning utilities |
| `tools/EsmSchemaGen` | Generates per-game C# record schemas from xEdit `wbDefinitions*.pas` (feeds the multi-game reader) |
| `tools/ShaderProbe` | Extracts and probes the FNV `shaderpackage.sdp` for renderer-parity analysis |

```bash
# ESM analysis and comparison
dotnet run --project tools/EsmAnalyzer -c Release -- stats FalloutNV.esm
dotnet run --project tools/EsmAnalyzer -c Release -- semdiff converted.esm pc_reference.esm -t NPC_

# Memory dump script analysis
dotnet run --project tools/EsmAnalyzer -c Release -- dmp scripts list dump.dmp

# NIF structure analysis
dotnet run --project tools/NifAnalyzer -f net10.0 -- info mesh.nif

# Texture analysis
dotnet run --project tools/TextureAnalyzer -- info texture.ddx

# BSA file search (main app)
dotnet run --project src/BethesdaMultitool -f net10.0 -- bsa find archive.bsa "*.nif"
```

## Project Structure

```
src/BethesdaMultitool/
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

src/BethesdaAudioTranscriber/  # Whisper-based voice file transcriber (WinUI 3)
src/DDXConv/                  # DDX conversion library (submodule)

tools/
├── EsmAnalyzer/             # ESM comparison, semantic diff, conversion, dump script analysis
├── NifAnalyzer/             # NIF structure inspection and comparison
├── TextureAnalyzer/         # DDX/DDS texture analysis
├── EgtAnalyzer/             # FaceGen EGT texture analysis
├── PdbAnalyzer/             # PDB symbol analysis and struct layout generation
├── RttiScanner/             # RTTI / operator-new extraction
├── TerrainAnalyzer/         # Terrain/heightmap analysis
├── SignatureScanner/        # File signature scanning
└── Shared/                  # Shared CLI strings library
```

## External Dependencies

Some features require external tools. The GUI shows a notification on startup if any are missing.

### FFmpeg (XMA audio conversion)

XMA to WAV conversion requires [FFmpeg](https://www.ffmpeg.org/download.html) on PATH or at `C:\ffmpeg\bin\`. Without it, XMA files are extracted but not converted to WAV.

## Documentation

- [Xbox 360 ESM Format](docs/Xbox_360_ESM_Format.md) - ESM binary format and hybrid endianness
- [DDX Format](docs/Xbox_360_DDX_Format.md) - DDX texture format documentation
- [PDB Runtime Structures](docs/PDB_Runtime_Structures.md) - Gamebryo runtime struct layouts
- [Script Bytecode Format](docs/PDB_Script_Bytecode_Format.md) - ObScript SCDA bytecode format

## License

MIT License - See [LICENSE](LICENSE) for details.

### Third-Party Components (included in repository)

| Component | License | Usage |
| --- | --- | --- |
| [DDXConv](https://github.com/GamesPastOrg/DDXConv) | [MIT](https://github.com/GamesPastOrg/DDXConv/blob/master/LICENSE) | DDX to DDS texture conversion (forked, built-in) |
| [NifSkope nif.xml](https://github.com/fo76utils/nifskope) | [BSD-3-Clause](https://github.com/fo76utils/nifskope/blob/develop/LICENSE.md) | NIF format schema (embedded) |
| [Xenia](https://github.com/xenia-project/xenia) | [BSD-3-Clause](https://github.com/xenia-project/xenia/blob/master/LICENSE) | Xbox 360 texture tiling code (in DDXConv) |
| [fo76utils](https://github.com/fo76utils/fo76utils) | [MIT](https://github.com/fo76utils/fo76utils/blob/master/LICENSE) | BA2 archive parser + BTD terrain reader (re-implemented) |
| [@google/model-viewer](https://github.com/google/model-viewer) | [BSD-3-Clause](https://github.com/google/model-viewer/blob/master/LICENSE) | 3D NPC model viewer (bundled in GUI) |

## Acknowledgments

### Tools & Libraries

- [Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows) - Direct3D 11/12 + DXGI bindings for GPU rendering (MIT)
- [SharpGLTF](https://github.com/vpenades/SharpGLTF) - glTF/GLB model export (MIT)
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
- [fo76utils/NifSkope](https://github.com/fo76utils/nifskope) - NIF format documentation (BSD-3-Clause)
- [GamesPastOrg/DDXConv](https://github.com/GamesPastOrg/DDXConv) - DDX texture conversion (MIT, Copyright 2026 Kran)
