# Bethesda Multitool

A .NET 10.0 toolkit for exploring, rendering, and converting Bethesda game data. It reads the plugin, archive, mesh, texture, and audio formats used across The Elder Scrolls and Fallout — Morrowind through Starfield, on PC and Xbox 360 — through a **WinUI 3 GUI** with a real-time Direct3D 12 worldspace viewer, a **cross-platform CLI** for batch work, and a standalone **Audio Transcriber** for voice files.

Its deepest support is for Fallout: New Vegas on Xbox 360: converting retail and prototype console builds to PC format, and reconstructing loadable plugins out of console crash dumps.

![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4)
![Platform](https://img.shields.io/badge/Platform-Windows%20%7C%20Linux-blue)
![License](https://img.shields.io/badge/License-0BSD-green)

## Features

### GUI (Windows)

A navigation sidebar with these destinations:

- **Data Explorer**: Open a plugin, memory dump, or save game and inspect it across eight views —
  - *Records*: browse records with search, property display, and GECK-style flag decoding
  - *Dialogue*: NPC dialogue trees by speaker, quest, and topic
  - *World*: switch between a 2D map and a real-time **3D View** (below)
  - *Actors*: searchable NPC/creature list with a live 3D model viewer and Full Body / Armor / Weapon / Idle Pose toggles
  - *Reports*: GECK-style reports with search and export
  - *Raw View*: virtual-scrolling hex editor supporting 200MB+ files with minimap overview
  - *Overview* and *Coverage Gaps*: file/record-type summary, and a table of bytes the parser could not account for
- **3D Worldspace Viewer** (the Data Explorer's World → 3D View): real-time Direct3D 12 renderer for any worldspace or interior — streamed terrain, sky/cloud/weather layers, cascaded sun shadows, per-game water, grass and SpeedTree wind, particles, placed lights with day/night gating, HDR tonemapping and bloom, Havok-collision walk mode, nav-mesh / collision / FormID-heatmap overlays, and orthographic/isometric tiled PNG export
- **Archive Browser**: Browse and extract BSA and BA2 archives, with Xbox 360 to PC conversion for BSA
- **Model Tools**: Batch Convert (Xbox 360 → PC NIF mesh conversion with geometry expansion + endian conversion) + Viewer (browse a folder / archive, inspect blocks, render PNG, export GLB)
- **Texture Tools**: Batch DDX to DDS texture conversion
- **Game Repacker**: Convert a whole Xbox 360 Fallout: New Vegas *installation* into a playable PC install — BIK video, XMA → MP3 music, BSA extract/convert/repack, menu unpacking, ESM/ESP endian conversion, and a hybrid INI
- **DMP to ESM Converter**: Rebuild a loadable ESM/ESP plugin from an Xbox 360 crash dump, optionally packing missing assets into a BSA
- **Batch Dump Analysis**: Carve files from memory dumps in bulk — signature detection, extraction, and DDX/XMA conversion
- **Diagnostics**: Live cache, queue, and memory statistics

Record browsing, the world map, and the 3D viewer are game-detected and work across the supported titles; the Game Repacker is Fallout: New Vegas only.

### CLI (Cross-platform)

| Command | Description |
| --- | --- |
| *(default)* | Carve files from a memory dump or DDX by type (`-t dds ddx xma nif`), with optional conversion |
| `analyze` | Analyze a memory dump's structure; optionally extract ESM records and GECK-style reports |
| `esm` | Analyze plugins and convert Xbox 360 → PC (GECK compatible) |
| `dmp` | Memory dump analysis (modules, regions, coverage, cross-build compare, …) plus `dmp to-esm` — rebuild a loadable ESM/ESP plugin from a dump |
| `repack` | Convert a whole Xbox 360 Fallout: New Vegas installation to a PC install (video, music, BSAs, menus, ESM/ESP) |
| `archive` | Inspect, extract, and convert BSA / BA2 archives (`bsa` and `ba2` are deprecated aliases) |
| `convert-nif` | Convert Xbox 360 NIF meshes to PC format |
| `convert-ddx` | Convert DDX textures to DDS |
| `render` / `export` | Render NIF/NPC models to PNG, or export NIFs, NPCs and creatures to GLB |
| `world` | Explore worldspace data, heightmaps, map markers, and placed objects |
| `btd` | Inspect and render Bethesda Terrain Data (`.btd`) heightmaps (Fallout 76 / Starfield) |
| `dialogue` | Browse and export NPC dialogue trees |
| `papyrus` | Inspect, disassemble, decompile, and extract Papyrus (`.pex`) compiled scripts |
| `save` | Inspect Fallout 3/NV save games — header, changed forms, player state, stats, STFS containers |
| `rtti` | Resolve C++ class names from vtable addresses via MSVC RTTI (also available as `dmp rtti`) |
| `search` / `stats` / `list` / `show` / `diff` | Format-agnostic inspection of any ESM/ESP/DMP file |
| `report` | Validate generated report fields and cross-check report consistency across builds |
| `version-track` | Track game data changes across development builds |

### Audio Transcriber (Windows)

A standalone companion app for transcribing Fallout: New Vegas voice files using [Whisper](https://github.com/openai/whisper) speech-to-text. See the [Audio Transcriber](#audio-transcriber) section below for details.

### Format Support

| Category | Formats |
| --- | --- |
| Game Data | ESM/ESP — schema-driven read for Morrowind (TES3), Oblivion, Fallout 3, New Vegas, Skyrim, Fallout 4, and Fallout 76; Xbox 360 → PC conversion for Fallout 3 / New Vegas. Starfield is supported for terrain, meshes, materials, and archives, but has no record schema yet |
| Save games | FOS / FXS (Fallout 3 / New Vegas), including STFS containers |
| Models | NIF (Xbox 360 to PC conversion with geometry expansion), Starfield `.mesh` + `.cdb` materials; GLB/glTF export |
| Terrain | BTD (Bethesda Terrain Data — Fallout 76 / Starfield) |
| Archives | BSA (Bethesda Softworks Archive) — read all eras, write v104 (Fallout 3 / New Vegas); BA2 (Fallout 4 / 76 / Starfield) — read |
| Textures | DDX (3XDO/3XDR), DDS, PNG |
| Audio | XMA (Xbox Media Audio), WAV, MP3, OGG, LIP (lip sync) |
| Video | BIK (Bink) |
| Scripts | ObScript bytecode + Papyrus (`.pex`) — decompilation + comparison |
| UI | XDBF (Xbox Dashboard), XUI/XUR, `final_master_xml.dat` menu archives |
| Text | Subtitle indexes |
| Crash dumps | Xbox 360 minidumps with PDB-aware struct reading |

## Installation

### Pre-built Releases

Download from [Releases](https://github.com/slfx77/bethesda-multitool/releases):

| Platform | Download |
| --- | --- |
| Windows GUI | `BethesdaMultitool-Windows-GUI-x64.zip` |
| Windows CLI | `BethesdaMultitool-Windows-CLI-x64.zip` |
| Linux CLI | `BethesdaMultitool-Linux-CLI-x64.tar.gz` |
| Audio Transcriber | `BethesdaAudioTranscriber-Windows-x64.zip` |

### Build from Source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) selected by
`global.json` (10.0.401 or a later patch in the 10.0.4xx feature band), and PowerShell 7
for the coordinated build wrapper.

The current development branch references **Multitool.Shared (Foundation) as a pinned private
Git submodule** under `shared/Multitool.Shared`. It is not yet distributed as a NuGet package.
Building this branch requires access to `slfx77/Multitool.Shared`; a public clone alone is not
sufficient. DDXConv remains a separate public submodule. Keep both at the commits recorded by
this repository. The intended public dependency is the future Foundation NuGet package.

```bash
# Authenticate with an account that can read Multitool.Shared, then clone both pinned dependencies.
git clone --recursive https://github.com/slfx77/bethesda-multitool.git
cd bethesda-multitool
# Existing clones: git submodule update --init --recursive
```

On **Windows**, build everything in `BethesdaMultitool.slnx` — GUI, CLI, companion apps, and tests:

```powershell
# Full selects all GUI targets; AnalyzerProfile Full runs the CI analysis gate.
pwsh -NoProfile -File tools/scripts/build.ps1 -Full -AnalyzerProfile Full
dotnet test --project tests/BethesdaMultitool.Tests/BethesdaMultitool.Tests.csproj -c Release -p:AnalyzerProfile=Full -p:Platform=x64 --no-build

# Run the GUI
dotnet run --project src/BethesdaMultitool -f net10.0-windows10.0.19041.0

# Run the CLI
dotnet run --project src/BethesdaMultitool -f net10.0 -- --help
```

On **Linux / macOS**, build the CLI only. The solution contains WinUI 3 projects whose
`net10.0-windows…` target MSBuild cannot evaluate off Windows (`NETSDK1100`), and `-f net10.0` alone
does not stop it trying — so scope to the project and pass `-p:BuildTestsOnly=true`, which collapses
the target frameworks to `net10.0`:

```bash
dotnet build src/BethesdaMultitool/BethesdaMultitool.csproj -c Release -p:BuildTestsOnly=true -p:AnalyzerProfile=Full -f net10.0
dotnet run --project src/BethesdaMultitool -c Release -p:BuildTestsOnly=true -f net10.0 -- --help
```

For normal local iteration, `pwsh -NoProfile -File tools/scripts/build.ps1` uses the
`Development` analyzer profile and omits the GUI. `-AnalyzerProfile Fast` explicitly skips
build analysis. Keep the same profile, configuration, and platform for restore, build, and
`--no-build` test commands; each analyzer profile has its own output tree. CI and publishing
require `Full`. See [analyzer profiles](shared/Multitool.Shared/docs/analyzer-profiles.md).

The native runtime bridge fixtures run separately with
`pwsh -NoProfile -File tools/scripts/test-runtime-bridges.ps1`. They require Visual Studio C++
build tools and use synthetic inputs only. Pass `-PlatformToolset v143` for Visual Studio 2022;
the projects otherwise use v145. CI runs these five fixtures alongside the managed tests.

CI currently runs only for trusted `main`/`develop` pushes or manual runs on those branches.
Before a hosted run, publish the exact recorded DDXConv and Foundation commits to their
respective repositories, then configure this repository's Actions secret
`MULTITOOL_SHARED_SSH_KEY` with the private half of a **read-only SSH deploy key registered
on `slfx77/Multitool.Shared`**. The ordinary Actions `GITHUB_TOKEN` cannot read a different
private repository. The checkout action uses the deploy key only for Foundation and does
not persist it in Git configuration. Missing
credentials or unpublished pins fail the build; they do not turn the quality gate into a skip.

Pull requests do not receive private Foundation source or run these workflows. After a
maintainer reviews and integrates a change into a trusted branch, CI builds the Windows GUI,
CLI, companion apps and tests, and checks the Linux CLI. The publish-layout workflow also
validates packaging, but does not upload application binaries or create releases. Tagged
releases and binary distribution remain disabled until the Foundation package is available
and a release is deliberately prepared.

## Usage

### GUI Mode (Windows)

Launch without arguments for the GUI, or auto-load a file:

```bash
BethesdaMultitool.exe
BethesdaMultitool.exe path/to/dump.dmp
```

**Explore** shares one source across Data, Maps, and Assets. The sidebar also retains Archive Browser and Model Tools, independent Recovery tools (File Carver, Game Repacker, DDX conversion, and DMP to ESM), and Batch Dump Analysis. Diagnostics and Settings are in the footer. See [shared Explore adoption and acceptance](docs/shared-explore-adoption.md) for source-selection behavior, verification commands, and current limits.

### CLI Mode

```bash
# Carve files from a memory dump (the default command — no subcommand)
BethesdaMultitool dump.dmp -o output -t ddx xma nif

# Analyze a memory dump's structure (-s also writes a GECK-style semantic report)
BethesdaMultitool analyze dump.dmp -o report.txt -s geck-report.txt

# Convert Xbox 360 ESM to PC format
BethesdaMultitool esm convert Sample/ESM/360_final/FalloutNV.esm -o FalloutNV.pc.esm

# Rebuild a loadable plugin from an Xbox 360 crash dump (overlays a PC master)
BethesdaMultitool dmp to-esm dump.dmp --pc-esm FalloutNV.esm -o Recovered.esp

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

The **Bethesda Audio Transcriber** is a standalone WinUI 3 application for browsing and transcribing Fallout: New Vegas voice files. It is provided as a precompiled download in [Releases](https://github.com/slfx77/bethesda-multitool/releases).

### What it does

- Loads voice audio files (XMA, WAV) from Bethesda BSA and BA2 archives
- Plays back voice lines with an integrated audio player
- Transcribes speech to text using [Whisper.net](https://github.com/sandrohanea/whisper.net) (OpenAI Whisper, runs locally), one line at a time or as a multi-threaded batch run with live progress
- Cross-references voice files against the ESM to display speaker names, quest context, and existing subtitle text
- Offers a review pass over Whisper output — Approve, Reject (revert to the ESM subtitle), or Dismiss each line, with suspected-typo flags loaded from a `.fnvreview.json` sidecar
- Exports finished transcriptions to CSV or to a plain-text report grouped by quest and speaker
- Saves transcription projects for incremental work across sessions

### Getting started

1. Download and extract `BethesdaAudioTranscriber-Windows-x64.zip` from [Releases](https://github.com/slfx77/bethesda-multitool/releases)
2. Launch `BethesdaAudioTranscriber.exe`
3. Point it at a Fallout: New Vegas `Data` directory containing voice BSA files (e.g., `Fallout - Voices1.bsa`)
4. The app parses all voice archives, cross-references with `FalloutNV.esm` if present, and presents a browsable playlist

### Transcription

- On first use, the Whisper model (`ggml-base.en`, ~148 MB) is automatically downloaded to `%LocalAppData%\BethesdaAudioTranscriber\models\`
- Audio is resampled to 16kHz mono before transcription
- Transcriptions persist across sessions as `.fnvtranscript.json`, saved outside the game: nothing is ever written into the Data directory. By default they go under `%LOCALAPPDATA%\BethesdaAudioTranscriber\Transcripts\`, mirroring the Data directory's full path (drive letter first); set `BETHESDA_TRANSCRIPT_STORE` to use another root. A file that an earlier version left in a Data directory is still read, and is never changed or deleted — move it to the new location to keep using it
- Voice files with existing ESM subtitles (NAM1) are shown alongside Whisper transcriptions for comparison

### Requirements

- Windows 10 (build 17763+) or later
- No .NET runtime needed — self-contained build with the Whisper runtime included
- **FFmpeg is required for Xbox 360 `.xma` voice files**, which need decoding before playback or transcription. Install it on PATH or at `C:\ffmpeg\bin\` (see [External Dependencies](#ffmpeg-xma-audio-conversion)); the app warns on load if a build contains XMA and FFmpeg is missing. PC builds ship WAV and do not need it.
- An internet connection on first run, to download the Whisper model

## ESM Conversion

The ESM converter handles Xbox 360 to PC format conversion for Fallout 3 / New Vegas plugins:

- **Endian conversion**: Record/subrecord headers and data fields (hybrid big/little-endian)
- **Split INFO merging**: Xbox 360's split dialogue records merged to match PC format
- **Schema-driven**: Field types defined in `SubrecordSchemaRegistry` for correct byte-swapping
- **GECK compatible**: Output loads in the Garden of Eden Creation Kit

Conversion is built on the 24-byte TES4-era record/GRUP framing, so it covers the Fallout 3 / New Vegas generation. Reading — as opposed to converting — is much broader; see [Format Support](#format-support).

## DMP → ESM Reconstruction

`dmp to-esm` rebuilds a loadable ESM/ESP plugin out of an Xbox 360 crash dump, recovering game data from a build that may never have shipped a plugin you can read directly. A two-pass planner settles every decision before a byte is written: per-record dispositions and cell-child verdicts, actor merge/move policy, duplicate-actor merging, override-door cloning, persistent-cell reparenting, dialogue exit-topic relinking, navmesh NVCI/NVEX reconstruction, and leveled-spawn recovery. `report validate` and `report consistency` sanity-check the result field by field and across builds.

## Script Decompiler

Decompiles game script bytecode back to readable source across engines:

- **ObScript** (SCDA subrecords): full New Vegas opcode coverage, reused for Fallout 3 (which adds its own retail-derived condition map), plus Oblivion
- **Papyrus** (`.pex`): Skyrim, Fallout 4, and Fallout 76 compiled-script decompilation and disassembly
- Cross-script variable resolution via SCRO/SCRV reference chains
- FormID to EditorID resolution for human-readable output
- Semantic comparison between original SCTX source and decompiled output

## Developer Tools

Standalone CLI tools for format analysis and debugging. These are not included in precompiled releases — build from source with `dotnet run --project tools/<name>`.

| Tool | Description |
| --- | --- |
| `tools/EsmAnalyzer` | Niche ESM/DMP debugging: GRUP structure, WRLD OFST streaming, land/heightmap export, worldmap visualization, dump script analysis |
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
# ESM structure and comparison (main app)
dotnet run --project src/BethesdaMultitool -f net10.0 -- esm stats FalloutNV.esm
dotnet run --project src/BethesdaMultitool -f net10.0 -- esm semdiff converted.esm pc_reference.esm -t NPC_
dotnet run --project src/BethesdaMultitool -f net10.0 -- archive find archive.bsa "*.nif"

# Niche ESM/DMP debugging
dotnet run --project tools/EsmAnalyzer -c Release -- grups FalloutNV.esm
dotnet run --project tools/EsmAnalyzer -c Release -- dmp scripts list dump.dmp

# NIF structure analysis
dotnet run --project tools/NifAnalyzer -f net10.0 -- info mesh.nif

# Texture analysis
dotnet run --project tools/TextureAnalyzer -- info texture.ddx
```

## Project Structure

Abridged — only the larger subsystems are shown.

```
src/BethesdaMultitool/
├── App/                     # WinUI 3 GUI (Windows only)
│   ├── Controls/            #   WorldView3D (Direct3D 12 viewer), WorldMap, markers, cell lists
│   ├── Dialogs/             #   Shared dialogs (shortcuts, dependencies, load order)
│   ├── HexViewer/           #   Virtual-scrolling hex editor + minimap
│   ├── Helpers/             #   Tree builders, display helpers
│   ├── Models/              #   Session state, view models
│   └── Tabs/                #   SingleFile (Data Explorer), BsaExtractor, NifConverter,
│                            #   DdxConverter, Repacker, DmpToEsmConverter, BatchMode, Diagnostics
├── CLI/                     # Cross-platform CLI commands, render/export pipelines
├── Core/                    # Format and analysis libraries
│   ├── Carving/             #   File signature detection and extraction
│   ├── Formats/
│   │   ├── Bsa/             #   BSA + BA2 archive parsing and extraction
│   │   ├── Ddx/ Dds/ Png/   #   Texture parsing and conversion
│   │   ├── Esm/             #   Plugin parsing, conversion, planning/writing, runtime readers
│   │   ├── Nif/             #   Mesh parsing, conversion, and the Direct3D 12 render stack
│   │   ├── Papyrus/         #   PEX parsing, disassembly, decompilation
│   │   ├── SaveGame/        #   Fallout 3/NV save game (FOS/FXS/STFS) parsing
│   │   ├── SpeedTree/       #   SpeedTree (.spt) geometry and wind
│   │   ├── Tes3/            #   Morrowind's flat record stream
│   │   ├── Xma/ Lip/ Bik/   #   Audio, lip sync, and video
│   │   └── Menus/ Xui/ Xdbf/ Subtitles/
│   ├── Games/               #   Per-game profiles and detection
│   ├── Minidump/            #   Xbox 360 minidump parsing and RTTI
│   ├── Pdb/                 #   PDB symbol resolution
│   ├── Repack/              #   Xbox 360 install → PC install repacking
│   ├── RuntimeBuffer/       #   Runtime string/pointer analysis
│   ├── Semantic/            #   Format-agnostic ESM/DMP/ESP loading
│   ├── VersionTracking/     #   Cross-build change tracking
│   ├── Vfs/                 #   Layered loose-file + archive virtual filesystem
│   ├── WorldData/           #   Worldspace, terrain, and map data
│   └── Utils/               #   Binary utilities

src/BethesdaAudioTranscriber/  # Whisper-based voice file transcriber (WinUI 3)
src/BethesdaRendererProfiler/  # 3D renderer profiling harness (WinUI 3)
src/BethesdaMap2DProfiler/     # 2D map profiling harness (WinUI 3)
src/DDXConv/                   # DDX conversion library (submodule)

tools/
├── EsmAnalyzer/             # Niche ESM/DMP debugging
├── NifAnalyzer/             # NIF structure inspection and comparison
├── TextureAnalyzer/         # DDX/DDS texture analysis
├── EgtAnalyzer/             # FaceGen EGT texture analysis
├── PdbAnalyzer/             # PDB symbol analysis and struct layout generation
├── RttiScanner/             # RTTI / operator-new extraction
├── TerrainAnalyzer/         # Terrain/heightmap analysis
├── SignatureScanner/        # File signature scanning
├── EsmSchemaGen/            # Per-game record schema generation from xEdit definitions
├── ShaderProbe/             # FNV shaderpackage.sdp extraction and probing
└── Shared/                  # Shared CLI strings library
```

## External Dependencies

Some features require external tools. The GUI shows a notification on startup if any are missing.

### FFmpeg (XMA audio conversion)

XMA to WAV conversion requires [FFmpeg](https://www.ffmpeg.org/download.html) on PATH or at `C:\ffmpeg\bin\`. Without it, XMA files are extracted but not converted to WAV. The Game Repacker also drives FFmpeg for MP3 and OGG encoding, so use a build with `libmp3lame` and `libvorbis`.

### WebView2 Runtime (3D model viewer)

The Actors and Model Tools 3D model viewers host [@google/model-viewer](https://github.com/google/model-viewer) in a WebView2 control, which needs the machine-wide Microsoft Edge WebView2 Runtime. It ships with Windows 11 and current Windows 10, so this is normally already present.

## Documentation

- [Xbox 360 ESM Format](docs/Xbox_360_ESM_Format.md) - ESM binary format and hybrid endianness
- [DDX Format](docs/Xbox_360_DDX_Format.md) - DDX texture format documentation
- [PDB Runtime Structures](docs/PDB_Runtime_Structures.md) - Gamebryo runtime struct layouts
- [Script Bytecode Format](docs/PDB_Script_Bytecode_Format.md) - ObScript SCDA bytecode format
- [Acronyms](ACRONYMS.md) - Glossary of the four-character record types and abbreviations used throughout
- [Changelog](CHANGELOG.md)

## License

Zero-Clause BSD (0BSD) - See [LICENSE](LICENSE) for details.

### Third-Party Components (included in repository)

| Component | License | Usage |
| --- | --- | --- |
| [DDXConv](https://github.com/GamesPastOrg/DDXConv) | [MIT](https://github.com/GamesPastOrg/DDXConv/blob/master/LICENSE) | DDX to DDS texture conversion (forked, built-in) |
| [NifSkope nif.xml](https://github.com/fo76utils/nifskope) | [BSD-3-Clause](https://github.com/fo76utils/nifskope/blob/develop/LICENSE.md) | NIF format schema (embedded) |
| [Xenia](https://github.com/xenia-project/xenia) | [BSD-3-Clause](https://github.com/xenia-project/xenia/blob/master/LICENSE) | Xbox 360 texture tiling code (in DDXConv) |
| [fo76utils](https://github.com/fo76utils/fo76utils) | [MIT](https://github.com/fo76utils/fo76utils/blob/master/LICENSE) | BA2 archive parser + BTD terrain reader (re-implemented) |
| [@google/model-viewer](https://github.com/google/model-viewer) | [BSD-3-Clause](https://github.com/google/model-viewer/blob/master/LICENSE) | 3D NPC model viewer (bundled in GUI) |
| [OpenTESArena](https://github.com/afritz1/OpenTESArena) | [MIT](https://github.com/afritz1/OpenTESArena/blob/master/LICENSE.txt) | Arena codecs, image/animation/map decoders and FLIC (ported) |
| [daggerfall-unity](https://github.com/Interkarma/daggerfall-unity) | [MIT](https://github.com/Interkarma/daggerfall-unity/blob/master/LICENSE) | Daggerfall format decoders via DaggerfallConnect (ported) |
| [falltergeist/dat-unpacker](https://github.com/falltergeist/dat-unpacker) | MIT | Fallout DAT1 LZSS decompression (ported) |
| [RGUnity/redguard-file-exporter](https://github.com/RGUnity/redguard-file-exporter) | [MIT](https://github.com/RGUnity/redguard-file-exporter/blob/master/LICENSE) | Redguard ROB archive structure (ported) |
| [twogood/unshield](https://github.com/twogood/unshield) | [MIT](https://github.com/twogood/unshield/blob/master/LICENSE) | InstallShield 5 cabinet reading (ported) |
| [libacm](https://github.com/markokr/libacm) | ISC | Interplay ACM audio decoder for Fallout 1/2 (ported) |
| [zlib contrib/blast.c](https://github.com/madler/zlib/blob/develop/contrib/blast/blast.c) | [zlib](https://github.com/madler/zlib/blob/develop/LICENSE) | PKWARE DCL "implode" decoder for Daggerfall's CD `PACKED.DAT` (ported) |
| [Rasetsuu/blendergranny](https://github.com/Rasetsuu/blendergranny), [Stitchuuuu/granny-ro-js](https://github.com/Stitchuuuu/granny-ro-js) | MIT | Granny 2 Oodle0 decompression (via AweMultitool) |
| [carbonenginejs/format-gr2](https://github.com/carbonenginejs/format-gr2) | MIT | Granny 2 Oodle1 decompression (via AweMultitool) |
| [Arbos/nwn2mdk](https://github.com/Arbos/nwn2mdk) | Boost 1.0 | The Oodle1 algorithm format-gr2 ports (`gr2_decompress.cpp`) |

## Acknowledgments

### Tools & Libraries

- [Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows) - Direct3D 12 + DXGI bindings for GPU rendering (MIT)
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

### Classic-format references

Support for the pre-Morrowind and spin-off catalog (Arena, Daggerfall, Battlespire,
Redguard, Fallout, Fallout 2, Fallout Tactics, the cancelled Van Buren, and Fallout:
Brotherhood of Steel) builds on the following community work. Code is
ported only from permissively-licensed projects, with the upstream source named in a
header comment on every ported file and the license text in
[THIRD_PARTY_LICENSES](THIRD_PARTY_LICENSES). Everything else is used strictly as
written documentation — no code from those projects is present here.

The Asset Browser also borrows two UI-framework-independent helpers from this project's
sister app [JimmyPCTool / AweMultitool](https://github.com/slfx77/JimmyPCTool):
its latest-only job guard and its gallery thumbnail scaler. The map pane's 2D-level
seam, the CD-image reader (`archive` on .iso / .cue+.bin, with Redbook tracks as WAV) and the
XDVDFS reader (the original Xbox and Xbox 360 disc filesystem, which mounts Fallout: Brotherhood of
Steel's Xbox release and a full Xbox 360 redump) come from the other sister app,
[NeversoftMultitool](https://github.com/slfx77/NeversoftMultitool). The Granny 2 reader that
decodes Van Buren's character meshes is ported from AweMultitool too, with its own upstream chain
(blendergranny + granny-ro-js for Oodle0, CarbonEngineJS format-gr2 and nwn2mdk for Oodle1 — MIT and
Boost 1.0, every notice reproduced in the ported files).

**Ported (permissive):**

- [OpenTESArena](https://github.com/afritz1/OpenTESArena) - Arena compression codecs and image decoders (MIT)
- [daggerfall-unity](https://github.com/Interkarma/daggerfall-unity) - Daggerfall decoders via its DaggerfallConnect API layer (MIT)
- [falltergeist/dat-unpacker](https://github.com/falltergeist/dat-unpacker) - Fallout DAT1 LZSS (MIT)
- [RGUnity/redguard-file-exporter](https://github.com/RGUnity/redguard-file-exporter) - Redguard ROB archives and TEXBSI textures (MIT)
- [twogood/unshield](https://github.com/twogood/unshield) - InstallShield 5 cabinet reading (MIT)
- [kaitai_struct_formats](https://github.com/kaitai-io/kaitai_struct_formats) - `game/fallout_dat.ksy` structure cross-check (CC0-1.0)
- [libacm](https://github.com/markokr/libacm) - Interplay ACM audio (Fallout 1/2 speech, effects and music) (ISC)
- [zlib contrib/blast.c](https://github.com/madler/zlib/blob/develop/contrib/blast/blast.c) - PKWARE DCL "implode", the codec of Daggerfall's CD `PACKED.DAT` (zlib)
- [JimmyPCTool / AweMultitool](https://github.com/slfx77/JimmyPCTool) - Granny 2 (.gr2) container, type tree and model reader (MIT), with
  [Rasetsuu/blendergranny](https://github.com/Rasetsuu/blendergranny) + [Stitchuuuu/granny-ro-js](https://github.com/Stitchuuuu/granny-ro-js) (Oodle0, MIT)
  and [carbonenginejs/format-gr2](https://github.com/carbonenginejs/format-gr2) (MIT) / [Arbos/nwn2mdk](https://github.com/Arbos/nwn2mdk) (Boost 1.0) (Oodle1)

**Documentation only (not ported):**

- [ariscop/battlespire-tools](https://github.com/ariscop/battlespire-tools) - XnGine BSA and Battlespire format notes
- [fodev.net](https://fodev.net) - Fallout FRM, PRO, MAP and palette documentation
- [UESP](https://en.uesp.net) - Daggerfall, Battlespire and Redguard format articles
- Creative Voice File (`.VOC`) specification - the published block-type table and time-constant
  formula; the decoder here is clean-roomed from it

Fallout Tactics support is clean-roomed from prose format specifications: every
public Tactics tool is GPL-licensed and therefore incompatible with this project's
MIT license, so no Tactics code is derived from them.

**Original reverse engineering (no reference used):**

Three of these titles have no permissively-licensed reference at all, so their formats
were measured directly from the shipped data and every reader here is original work:

- **Fallout Tactics** - the `.mis` mission container, the self-describing `.ent`/`.chr`
  property bags, the `.zar` / `.til` / `.spr` art and the `.sav` save archive. The art and save
  layouts are read off our own decompilation of the game's `BOS.exe`; `kran27/FalloutTacticsTools`
  and the other public tools are GPL and were not consulted for code.
- **Van Buren** (cancelled Fallout 3, Dec 2003 prototype) - the `.grp` archive, `EEN2` entity
  records, `.stf` string tables and `B3D` mesh identification. `kran27/VanBurenTools` is GPL
  and was used as a behaviour reference only, never transliterated.
- **Fallout: Brotherhood of Steel** (2004, PS2 and Xbox) - the `.SDB` string database, the `.DDF`
  record store, the `.CLP` clump container, the VAG sound banks and the PS2 GS texture layout,
  decoded from the retail discs and our own decompilation of the game's executables
  (`SLUS_205.39`, `default.xbe`) with no reference of any kind. The sibling Snowblind engine
  tools do not describe this container family.
- **Bink video** (`.bik`, RAD Game Tools; Fallout Tactics and the Xbox Brotherhood of Steel disc) - an original clean-room
  decoder written from our own disassembly of the shipped `binkw32.dll` and verified byte-exact
  against ffmpeg's output frames; no FFmpeg code was read or ported.

