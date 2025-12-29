# Xbox 360 Memory Carver

A cross-platform tool for Xbox 360 memory dump analysis, file carving, and DDX texture conversion. Features a **WinUI 3 GUI** on Windows and a **cross-platform CLI** for batch processing on Windows, Linux, and macOS.

![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4)
![Platform](https://img.shields.io/badge/Platform-Windows%20%7C%20Linux%20%7C%20macOS-blue)
![License](https://img.shields.io/badge/License-MIT-green)

## Features

### GUI (Windows only)
- **Hex Viewer**: Virtual-scrolling hex editor supporting 200MB+ files
- **Minimap**: VS Code-style overview with file type region coloring
- **Analysis Tab**: File signature detection with filtering and statistics
- **Extraction**: Carve and export detected files with DDX→DDS conversion

### CLI (Cross-platform)
- Batch processing of memory dumps
- Automatic file type detection and extraction
- DDX to DDS texture conversion
- Verbose progress reporting

### Supported File Types

| Category | Formats |
|----------|---------|
| Textures | DDX (3XDO/3XDR), DDS, PNG |
| Audio | XMA (Xbox Media Audio), LIP (lip sync) |
| Models | NIF (NetImmerse/Gamebryo) |
| Scripts | ObScript (Bethesda scripting) |
| Executables | XEX (Xbox Executable) |
| Data | ESP/ESM (Bethesda plugins), XUI (Xbox UI), XDBF |

## Installation

### Pre-built Releases

Download from [Releases](https://github.com/slfx77/xbox-360-minidump-extractor/releases):

| Platform | Download |
|----------|----------|
| Windows GUI | `Xbox360MemoryCarver-Windows-GUI-x64.zip` |
| Windows CLI | `Xbox360MemoryCarver-Windows-CLI-x64.zip` |
| Linux CLI | `Xbox360MemoryCarver-Linux-CLI-x64.tar.gz` |

### Build from Source

Requires [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```bash
# Clone with submodules
git clone --recursive https://github.com/slfx77/xbox-360-minidump-extractor.git
cd xbox-360-minidump-extractor

# Build all targets
dotnet build -c Release

# Run GUI (Windows only)
dotnet run --project src/Xbox360MemoryCarver -f net10.0-windows10.0.19041.0

# Run CLI (cross-platform)
dotnet run --project src/Xbox360MemoryCarver -f net10.0 -- --help
```

## Usage

### GUI Mode (Windows)

Run the application without arguments to launch the GUI:

```bash
Xbox360MemoryCarver.exe
```

Or auto-load a dump file:

```bash
Xbox360MemoryCarver.exe path/to/dump.dmp
```

### CLI Mode

```bash
# Basic extraction
Xbox360MemoryCarver dump.dmp -o output_folder

# With options
Xbox360MemoryCarver dump.dmp -o output -t ddx xma nif -v --convert-ddx

# Windows: Force CLI mode (otherwise defaults to GUI)
Xbox360MemoryCarver --no-gui dump.dmp -o output
```

#### CLI Options

| Option | Description |
|--------|-------------|
| `<input>` | Path to memory dump file (.dmp) |
| `-o, --output` | Output directory (default: `output`) |
| `-n, --no-gui` | Force CLI mode on Windows |
| `-t, --types` | File types to extract (e.g., `ddx xma nif`) |
| `--convert-ddx` | Convert DDX textures to DDS (default: true) |
| `-v, --verbose` | Enable verbose output |
| `--max-files` | Max files per type (default: 10000) |

## DDXConv Standalone Tool

The DDXConv command-line tool can also be used standalone for DDX to DDS conversion:

```bash
# Single file conversion
dotnet run --project src/DDXConv/DDXConv -- texture.ddx output.dds

# Batch conversion
dotnet run --project src/DDXConv/DDXConv -- ddx_folder/ output_folder/
```

## Technical Details

### Xbox 360 Specifics
- **DDX Formats**: 3XDO (standard compressed), 3XDR (alternate tiling - experimental)
- **Architecture**: PowerPC (processor type 0x3 in minidumps)
- **Texture Tiling**: Morton-order (Z-order) swizzling on Xbox 360 GPU
- **Compression**: XMemCompress/XMemDecompress via XnaNative.dll

### Minidump Structure
- Stream type 4: `MINIDUMP_MODULE_LIST` - Loaded module info with accurate sizes
- Stream type 9: `MINIDUMP_MEMORY64_LIST` - Memory region descriptors
- Modules extracted from metadata have correct sizes; signature carving uses heuristics

### XCompression Setup

For DDX texture conversion, install Microsoft XNA Framework 4.0. The application uses `XnaNative.dll` for LZX decompression. Without it, DDX conversion will fail.

## Project Structure

```
src/Xbox360MemoryCarver/
├── Core/                    # Cross-platform carving logic
│   ├── Carving/             # File carving engine
│   ├── Converters/          # DDX conversion
│   ├── Minidump/            # Minidump parsing
│   ├── Models/              # File signatures, metadata
│   ├── Parsers/             # File format parsers
│   └── Utils/               # Binary utilities
├── *.xaml / *.xaml.cs       # WinUI 3 GUI (Windows only)
├── Program.cs               # Entry point (CLI/GUI switch)
└── GuiEntryPoint.cs         # GUI bootstrap (Windows only)

src/DDXConv/                 # DDX conversion submodule
```

## Contributing

Contributions are welcome! Please ensure:

- Code follows existing style (file-scoped namespaces, `_camelCase` fields)
- All I/O operations are async
- New file types include parser, signature, and color mapping

## License

MIT License - See [LICENSE](LICENSE) file for details.

## Acknowledgments

- [DDXConv](https://github.com/kran27/DDXConv) - DDX texture conversion
- [Xenia Xbox 360 Emulator](https://github.com/xenia-project/xenia) - Format documentation
- [XCompression](https://github.com/gibbed/XCompression) - LZX decompression reference
