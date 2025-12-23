# Xbox 360 Memory Carver

## Project Overview

.NET 10.0 application for Xbox 360 memory dump analysis, file carving, and DDX texture conversion. Features both a **WinUI 3 GUI** for interactive analysis and a **CLI mode** for batch processing.

## Architecture

### Projects
- **Xbox360MemoryCarver.App** - WinUI 3 desktop application with dual GUI/CLI mode
- **Xbox360MemoryCarver.Core** - Core library with carving logic, file signatures, and analysis
- **DDXConv** - Submodule for DDX to DDS texture conversion

### Key Components
- `MemoryCarver` - Main file carving engine with signature-based detection
- `FileSignatures` - Defines magic bytes and file type detection rules
- `MinidumpExtractor` - Parses Xbox 360 minidump structures for module extraction
- `HexViewerControl` / `HexMinimapControl` - Interactive hex viewing with VS Code-style minimap
- `FileTypeColors` - Centralized color palette for file type visualization

## GUI Features (WinUI 3)

- **Hex Viewer**: Virtual-scrolling hex editor supporting 200MB+ files
- **Minimap**: VS Code-style overview with file type region coloring
- **Analysis Tab**: File signature detection with filtering and statistics
- **Extraction**: Carve and export detected files with DDX→DDS conversion

## CLI Mode

Run with `--no-gui` or `-n` flag for headless operation:
```bash
Xbox360MemoryCarver.App.exe --no-gui <input.dmp> -o <output_dir> [-v]
Xbox360MemoryCarver.App.exe --no-gui --ddx <input.ddx> -o <output.dds>
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
- **Audio**: XMA (Xbox Media Audio)
- **Models**: NIF (NetImmerse/Gamebryo)
- **Executables**: XEX (Xbox Executable)
- **Data**: ESP/ESM (Bethesda plugins), XUI (Xbox UI), XDBF, LIP (lip sync)

## Build Instructions

```bash
# Build all projects
dotnet build -c Release

# Run GUI
dotnet run --project src/Xbox360MemoryCarver.App

# Run CLI mode
dotnet run --project src/Xbox360MemoryCarver.App -- --no-gui <input.dmp> -o <output_dir>
```

## Code Style & Quality

### Enforced Standards
- **Nullable Reference Types**: Enabled globally - handle nulls explicitly
- **File-scoped namespaces**: Use `namespace Foo;` not `namespace Foo { }`
- **Braces required**: Always use braces for if/for/while/etc.
- **Private field naming**: `_camelCase` with underscore prefix
- **Async methods**: Suffix with `Async` (e.g., `LoadFileAsync`)

### Analyzers (via Directory.Build.props)
- **Roslyn Analyzers**: Built-in .NET code analysis (latest-recommended)
- **SonarAnalyzer.CSharp**: Security, bugs, code smells
- **Roslynator**: 500+ additional analyzers

### WinUI 3 Best Practices
- Use `DispatcherQueue.TryEnqueue()` for UI thread access from background tasks
- Prefer `x:Bind` over `Binding` for compile-time checking and performance
- Implement `IDisposable` for controls holding native resources
- Use `async void` only for event handlers; prefer `async Task` elsewhere

### Patterns Used
- **MVVM-lite**: Code-behind for simpler controls, consider ViewModels for complex state
- **Async/await**: All I/O operations are async; never block UI thread
- **IProgress<T>**: Report progress from long-running operations
- **CancellationToken**: Support cancellation for all carving/analysis operations

## Dependencies

| Package | Version | Purpose |
|---------|---------|---------|
| Microsoft.WindowsAppSDK | 1.6.x | WinUI 3 framework |
| CommunityToolkit.WinUI | 7.1.x | Additional WinUI controls |
| System.CommandLine | 2.0.0-beta4 | CLI argument parsing |
| SonarAnalyzer.CSharp | 10.x | Code analysis |
| Roslynator.Analyzers | 4.x | Additional linting |

## Project Structure

```
src/
├── Xbox360MemoryCarver.App/     # WinUI 3 application
│   ├── Program.cs               # Entry point (CLI/GUI switch)
│   ├── MainWindow.xaml(.cs)     # Main window with tabs
│   ├── SingleFileTab.xaml(.cs)  # File analysis tab
│   ├── HexViewerControl.xaml    # Virtual hex viewer
│   ├── HexMinimapControl.xaml   # Minimap visualization
│   └── Converters/              # XAML value converters
├── Xbox360MemoryCarver.Core/    # Core library
│   ├── MemoryCarver.cs          # File carving engine
│   ├── FileSignatures.cs        # Magic byte definitions
│   ├── FileTypeColors.cs        # Color palette
│   └── Minidump/                # Minidump parsing
└── DDXConv/                     # DDX conversion submodule
Sample/
├── MemoryDump/                  # Test dump files
└── Texture/                     # Sample textures
```

## Common Tasks

### Adding a New File Signature
1. Add magic bytes to `FileSignatures.cs` in `AllSignatures` list
2. Add color mapping in `FileTypeColors.cs`
3. Update file type filter in `SingleFileTab.xaml.cs` if needed

### Debugging CLI Mode
Since it's a WinExe, run through cmd for console output:
```bash
cmd /c "Xbox360MemoryCarver.App.exe --no-gui <args> 2>&1"
```
