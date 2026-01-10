# Xbox 360 Memory Carver

## Project Overview

.NET 10.0 application for Xbox 360 memory dump analysis, file carving, and format conversion. Features both a **WinUI 3 GUI** (Windows only) and a **cross-platform CLI** for batch processing.

## Architecture

### Single Unified Project

The project uses multi-targeting to produce both GUI and CLI builds from a single codebase:

- **`net10.0`** - Cross-platform CLI (Windows, Linux, macOS)
- **`net10.0-windows10.0.19041.0`** - Windows GUI with WinUI 3

### Key Components

- `MemoryCarver` - Main file carving engine with Aho-Corasick multi-pattern matching
- `SignatureMatcher` - Aho-Corasick algorithm for efficient multi-signature scanning
- `FormatRegistry` - Auto-discovers and registers format modules from `Core/Formats/`
- `IFileFormat` - Interface for self-contained format modules (parsing, conversion, repair)
- `MinidumpParser` - Parses Xbox 360 minidump structures for module extraction
- `DumpAnalyzer` - Comprehensive dump analysis with build detection and ESM record extraction
- `ScriptExtractor` - Extracts and groups compiled scripts (SCDA) by quest name
- `NifEndianConverter` - Converts Xbox 360 NIF models (big-endian) to PC format (little-endian)
- `HexViewerControl` / `HexMinimapControl` - Interactive hex viewing with VS Code-style minimap

### Submodules

- **DDXConv** - DDX to DDS texture conversion tool
- **microsoft-pdb** - Microsoft PDB tools for symbol analysis

### Analysis Tools

- **NifAnalyzer** (`tools/NifAnalyzer/`) - **Always use this tool for NIF file analysis**. Commands:
  - `dotnet run --project tools/NifAnalyzer -- blocks <file>` - List all blocks with offsets and sizes
  - `dotnet run --project tools/NifAnalyzer -- havok <file>` - Parse hkPackedNiTriStripsData blocks
  - `dotnet run --project tools/NifAnalyzer -- hex <file> <offset> <length>` - Hex dump at offset
  - `dotnet run --project tools/NifAnalyzer -- compare <file1> <file2>` - Compare two NIF files

> **IMPORTANT**: When analyzing NIF files, always use NifAnalyzer instead of manual PowerShell byte parsing. The tool handles endianness, block parsing, and structure interpretation correctly.

## Research Documentation

See [docs/Memory_Dump_Research.md](../docs/Memory_Dump_Research.md) for ongoing research into:

- Xbox 360 memory dump structure and layout
- PDB symbol analysis for understanding game structures
- File type region mapping
- Opcode table construction from executable analysis

### Reference Materials (in project)

Located in `Fallout New Vegas (July 21, 2010)/FalloutNV/`:

- PDB symbol files for debug, release beta, and memory debug builds
- Corresponding executables (both .exe and .xex formats)

## GUI Features (WinUI 3, Windows only)

- **Hex Viewer**: Virtual-scrolling hex editor supporting 200MB+ files
- **Minimap**: VS Code-style overview with file type region coloring
- **Analysis Tab**: File signature detection with filtering and statistics
- **Extraction**: Carve and export detected files with DDX→DDS conversion

## CLI Mode (Cross-platform)

```bash
# Linux/macOS - Carve files from memory dump
./Xbox360MemoryCarver dump.dmp -o output_dir -v

# Windows (force CLI mode)
Xbox360MemoryCarver.exe --no-gui dump.dmp -o output_dir -v

# Analyze dump structure
Xbox360MemoryCarver analyze dump.dmp              # Console summary
Xbox360MemoryCarver analyze dump.dmp -f md        # Markdown report
Xbox360MemoryCarver analyze dump.dmp -f md -o report.md  # Save report

# List loaded modules
Xbox360MemoryCarver modules dump.dmp              # Text output
Xbox360MemoryCarver modules dump.dmp -f md        # Markdown table
Xbox360MemoryCarver modules dump.dmp -f csv       # CSV export

# Convert Xbox 360 NIF models to PC format
Xbox360MemoryCarver convert-nif model.nif -o output_dir
Xbox360MemoryCarver convert-nif meshes_folder/ -r -v    # Recursive, verbose
Xbox360MemoryCarver convert-nif meshes/ -o converted/ --overwrite
```

## Build Commands

```bash
# Build all targets
dotnet build -c Release

# Run GUI (Windows only)
dotnet run -f net10.0-windows10.0.19041.0

# Run CLI (any platform)
dotnet run -f net10.0 -- dump.dmp -o output

# Publish cross-platform CLI
dotnet publish -c Release -f net10.0 -r linux-x64 --self-contained

# Publish Windows GUI
dotnet publish -c Release -f net10.0-windows10.0.19041.0 -r win-x64 --self-contained
```

## Technical Notes

### Xbox 360 Specifics

- **DDX Formats**: 3XDO (standard compressed), 3XDR (alternate tiling - experimental)
- **NIF Models**: Xbox 360 NIFs use big-endian byte order; conversion strips platform-specific blocks
- **Architecture**: PowerPC (processor type 0x3 in minidumps)
- **Texture Tiling**: Morton-order (Z-order) swizzling on Xbox 360 GPU
- **Compression**: XMemCompress/XMemDecompress via XnaNative.dll or xcompress64.dll

### Minidump Structure

- Stream type 4: MINIDUMP_MODULE_LIST - Contains loaded module info with accurate sizes
- Stream type 9: MINIDUMP_MEMORY64_LIST - Memory region descriptors
- Modules extracted from metadata have correct sizes; signature carving uses heuristics

### Supported File Types

- **Textures**: DDX (3XDO/3XDR), DDS, PNG
- **Audio**: XMA (Xbox Media Audio), LIP (lip sync)
- **Models**: NIF (NetImmerse/Gamebryo) - with BE→LE conversion
- **Executables**: XEX (Xbox Executable)
- **Scripts**: Uncompiled ObScript (`scn`/`ScriptName` format) - debug builds; Compiled bytecode (SCDA) - release builds
- **Data**: ESP/ESM (Bethesda plugins), XUI (Xbox UI), XDBF

### NIF Conversion Notes

Xbox 360 NIF files use big-endian byte order and may contain platform-specific blocks:

- **BSPackedAdditionalGeometryData** - Xbox 360-specific geometry blocks (stripped during conversion)
- Header fields are converted to little-endian
- Block data byte order is swapped where possible
- For best results, use [Noesis](https://richwhitehouse.com/index.php?content=inc_projects.php&showproject=91) or [NifSkope](https://github.com/niftools/nifskope) for further processing

## Code Style & Quality

### Enforced Standards

- **Nullable Reference Types**: Enabled globally - handle nulls explicitly
- **File-scoped namespaces**: Use `namespace Foo;` not `namespace Foo { }`
- **Braces required**: Always use braces for if/for/while/etc.
- **Private field naming**: `_camelCase` with underscore prefix
- **Async methods**: Suffix with `Async` (e.g., `LoadFileAsync`)

### Conditional Compilation

- `#if WINDOWS_GUI` - Code only included in Windows GUI build
- GUI files are excluded from `net10.0` build via csproj conditions

### WinUI 3 Best Practices

- Use `DispatcherQueue.TryEnqueue()` for UI thread access from background tasks
- Prefer `x:Bind` over `Binding` for compile-time checking and performance
- Implement `IDisposable` for controls holding native resources
- Use `async void` only for event handlers; prefer `async Task` elsewhere

### Patterns Used

- **MVVM-lite**: Code-behind for simpler controls
- **Async/await**: All I/O operations are async; never block UI thread
- **IProgress<T>**: Report progress from long-running operations
- **CancellationToken**: Support cancellation for all carving/analysis operations

## Dependencies

| Package                 | Version     | Purpose                                  |
| ----------------------- | ----------- | ---------------------------------------- |
| Microsoft.WindowsAppSDK | 1.6.x       | WinUI 3 framework (Windows only)         |
| CommunityToolkit.WinUI  | 8.x         | Additional WinUI controls (Windows only) |
| System.CommandLine      | 2.0.0-beta4 | CLI argument parsing                     |
| Spectre.Console         | 0.49.x      | Rich CLI output and progress bars        |

## Project Structure

```
src/Xbox360MemoryCarver/
├── CLI/                         # Command-line interface commands
│   ├── AnalyzeCommand.cs        # Dump analysis command
│   ├── CarveCommand.cs          # File extraction command
│   ├── ConvertNifCommand.cs     # NIF conversion command
│   └── ModulesCommand.cs        # Module listing command
├── Core/                        # Cross-platform carving logic
│   ├── Analysis/                # DumpAnalyzer - build detection, ESM extraction
│   ├── Carving/                 # MemoryCarver, CarveManifest
│   ├── Converters/              # DDX subprocess converter, NIF endian converter
│   ├── Extractors/              # ScriptExtractor - SCDA grouping by quest
│   ├── Formats/                 # Self-contained format modules
│   │   ├── FormatRegistry.cs    # Auto-discovers format modules
│   │   ├── IFileFormat.cs       # Base interface for formats
│   │   ├── Dds/DdsFormat.cs     # DDS texture format
│   │   ├── Ddx/DdxFormat.cs     # DDX Xbox 360 texture (w/ conversion)
│   │   ├── EsmRecord/           # ESM record scanning
│   │   ├── Esp/EspFormat.cs     # ESP/ESM plugin format
│   │   ├── Lip/LipFormat.cs     # Lip sync format
│   │   ├── Nif/NifFormat.cs     # NetImmerse model format (w/ conversion)
│   │   ├── Png/PngFormat.cs     # PNG image format
│   │   ├── Scda/ScdaFormat.cs   # Compiled script bytecode
│   │   ├── Script/ScriptFormat.cs # Uncompiled script source
│   │   ├── Xdbf/XdbfFormat.cs   # Xbox Dashboard format
│   │   ├── Xex/XexFormat.cs     # Xbox executable format
│   │   ├── Xma/XmaFormat.cs     # Xbox Media Audio (w/ repair)
│   │   └── Xui/XuiFormat.cs     # Xbox UI format
│   ├── Minidump/                # MinidumpParser, MinidumpInfo
│   └── Utils/                   # BinaryUtils, SignatureBoundaryScanner
├── App/                         # WinUI 3 GUI (Windows only)
├── SignatureMatcher.cs          # Aho-Corasick multi-pattern search
├── Program.cs                   # Entry point (CLI/GUI switch)
├── GuiEntryPoint.cs             # GUI bootstrap (Windows only)
└── Xbox360MemoryCarver.csproj   # Multi-target project file

tests/Xbox360MemoryCarver.Tests/
├── Core/
│   ├── Converters/              # NifEndianConverterTests
│   ├── Formats/                 # NifFormatTests
│   ├── Parsers/                 # Parser tests (DDS, DDX, PNG, XMA, etc.)
│   └── Utils/                   # BinaryUtilsTests
└── Xbox360MemoryCarver.Tests.csproj

src/DDXConv/                     # DDX conversion submodule
```

## Common Tasks

### Adding a New File Format

1. Create a new folder `Core/Formats/NewFormat/`
2. Create `NewFormatFormat.cs` extending `FileFormatBase`:

```csharp
public sealed class NewFormatFormat : FileFormatBase
{
    public override string FormatId => "newformat";
    public override string DisplayName => "NEWF";
    public override string Extension => ".newf";
    public override FileCategory Category => FileCategory.Data;
    public override string OutputFolder => "newformat";
    public override int MinSize => 16;
    public override int MaxSize => 10 * 1024 * 1024;

    public override IReadOnlyList<FormatSignature> Signatures { get; } =
    [
        new FormatSignature
        {
            Id = "newformat",
            MagicBytes = "NEWF"u8.ToArray(),
            Description = "New Format File"
        }
    ];

    public override ParseResult? Parse(ReadOnlySpan<byte> data, int offset = 0)
    {
        // Parse and return file info
    }
}
```

3. The format is auto-discovered by `FormatRegistry` via reflection
4. Color is automatically derived from category

See [docs/Architecture.md](../docs/Architecture.md) for detailed extensibility guide.

### Testing CLI on Windows

The Windows build defaults to GUI mode. Force CLI with `--no-gui`:

```bash
Xbox360MemoryCarver.exe --no-gui dump.dmp -o output -v
```
