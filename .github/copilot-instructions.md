# Xbox 360 Memory Carver

## Project Overview

.NET 10.0 application for Xbox 360 memory dump analysis, file carving, and DDX texture conversion. Features both a **WinUI 3 GUI** (Windows only) and a **cross-platform CLI** for batch processing.

## Architecture

### Single Unified Project

The project uses multi-targeting to produce both GUI and CLI builds from a single codebase:

- **`net10.0`** - Cross-platform CLI (Windows, Linux, macOS)
- **`net10.0-windows10.0.19041.0`** - Windows GUI with WinUI 3

### Key Components

- `MemoryCarver` - Main file carving engine with signature-based detection
- `FileSignatures` - Defines magic bytes and file type detection rules
- `MinidumpParser` - Parses Xbox 360 minidump structures for module extraction
- `HexViewerControl` / `HexMinimapControl` - Interactive hex viewing with VS Code-style minimap
- `FileTypeColors` - Centralized color palette for file type visualization
- `FileTypeMetadata` - Single source of truth for file type categories and display names

### Submodules

- **DDXConv** - DDX to DDS texture conversion tool
- **microsoft-pdb** - Microsoft PDB tools for symbol analysis

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
# Linux/macOS - Carve files
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
- **Models**: NIF (NetImmerse/Gamebryo)
- **Executables**: XEX (Xbox Executable)
- **Scripts**: Uncompiled ObScript (`scn`/`ScriptName` format) - debug builds; Compiled bytecode (SCDA) - release builds via ScriptDisassembler tool
- **Data**: ESP/ESM (Bethesda plugins), XUI (Xbox UI), XDBF

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

## Project Structure

```
src/Xbox360MemoryCarver/
├── Core/                        # Cross-platform carving logic
│   ├── Carving/                 # MemoryCarver, CarveManifest
│   ├── Converters/              # DDX subprocess converter
│   ├── Minidump/                # Minidump parsing
│   ├── Models/                  # FileSignatures, FileTypeMetadata
│   ├── Parsers/                 # DDX, DDS, XMA, NIF, Script parsers
│   └── Utils/                   # BinaryUtils
├── *.xaml / *.xaml.cs           # WinUI 3 GUI (Windows only)
├── Program.cs                   # Entry point (CLI/GUI switch)
├── GuiEntryPoint.cs             # GUI bootstrap (Windows only)
├── FileTypeColors.cs            # UI color mappings (Windows only)
└── Xbox360MemoryCarver.csproj   # Multi-target project file

src/DDXConv/                     # DDX conversion submodule
```

## Common Tasks

### Adding a New File Signature

1. Add magic bytes to `Core/Models/FileSignatures.cs`
2. Add parser to `Core/Parsers/` and register in `ParserFactory.cs`
3. Add to `Core/Models/FileTypeMetadata.cs` with category and display name
4. Color is automatically derived from category in `FileTypeColors.cs`

### Testing CLI on Windows

The Windows build defaults to GUI mode. Force CLI with `--no-gui`:

```bash
Xbox360MemoryCarver.exe --no-gui dump.dmp -o output -v
```
