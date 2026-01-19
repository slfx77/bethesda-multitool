# Xbox 360 Memory Carver - Architecture Guide

This document describes the internal architecture of the Xbox 360 Memory Carver, including the core components, data flow, and extensibility points.

---

## Table of Contents

1. [High-Level Architecture](#high-level-architecture)
2. [Core Components](#core-components)
3. [File Type System](#file-type-system)
4. [Carving Pipeline](#carving-pipeline)
5. [Parser Architecture](#parser-architecture)
6. [Analysis Module](#analysis-module)
7. [GUI Architecture](#gui-architecture)
8. [Extensibility Guide](#extensibility-guide)

---

## High-Level Architecture

```
┌────────────────────────────────────────────────────────────────────────────┐
│                          Xbox 360 Memory Carver                            │
├──────────────────────────────────┬─────────────────────────────────────────┤
│           GUI Layer              │               CLI Layer                 │
│  (WinUI 3 - Windows only)        │        (Cross-platform .NET)            │
│  ┌─────────────────────────┐     │     ┌────────────────────────────────┐  │
│  │ MainWindow              │     │     │ Program.cs                     │  │
│  │ HexViewerControl        │     │     │ - carve command                │  │
│  │ HexMinimapControl       │     │     │ - analyze command              │  │
│  │ SingleFileTab           │     │     │ - modules command              │  │
│  │ BatchModeTab            │     │     └────────────────────────────────┘  │
│  └─────────────────────────┘     │                                         │
├──────────────────────────────────┴─────────────────────────────────────────┤
│                              Core Layer                                    │
│  ┌─────────────┐ ┌─────────────┐ ┌─────────────┐ ┌───────────────────────┐ │
│  │  Carving/   │ │  Formats/   │ │  Dump       │ │   FormatRegistry      │ │
│  │MemoryCarver │ │ 12 modules  │ │  Analyzer   │ │   IFileFormat         │ │
│  │CarveManifest│ │IFileFormat  │ │             │ │   FileFormatBase      │ │
│  └─────────────┘ └─────────────┘ └─────────────┘ └───────────────────────┘ │
│  ┌─────────────┐ ┌─────────────┐ ┌─────────────┐ ┌───────────────────────┐ │
│  │  Minidump/  │ │ Converters/ │ │ Extractors/ │ │ SignatureMatcher      │ │
│  │MinidumpParse│ │DdxConverter │ │ScriptExtract│ │ (Aho-Corasick)        │ │
│  └─────────────┘ └─────────────┘ └─────────────┘ └───────────────────────┘ │
└────────────────────────────────────────────────────────────────────────────┘
```

### Project Structure

```
src/Xbox360MemoryCarver/
├── Core/
│   ├── Carving/            # File carving engine
│   │   ├── CarveExtractor.cs
│   │   ├── CarveManifest.cs
│   │   ├── CarveWriter.cs
│   │   └── MemoryCarver.cs
│   ├── Converters/         # DDX/XUR conversion
│   │   ├── DdxConversionResult.cs
│   │   ├── DdxSubprocessConverter.cs
│   │   ├── XurConversionResult.cs
│   │   └── XurSubprocessConverter.cs
│   ├── Formats/            # Self-contained format modules
│   │   ├── FormatRegistry.cs   # Explicit format registration (no reflection)
│   │   ├── IFileFormat.cs      # Base interface + IFileConverter, IDumpScanner
│   │   ├── FileFormatBase.cs   # Base class with common implementation
│   │   ├── Dds/DdsFormat.cs
│   │   ├── Ddx/DdxFormat.cs    # Implements IFileConverter
│   │   ├── EsmRecord/EsmRecordFormat.cs  # Implements IDumpScanner
│   │   ├── Esp/EspFormat.cs
│   │   ├── Lip/LipFormat.cs
│   │   ├── Nif/NifFormat.cs
│   │   ├── Png/PngFormat.cs
│   │   ├── Scda/ScdaFormat.cs  # Implements IDumpScanner
│   │   ├── Script/ScriptFormat.cs
│   │   ├── Xdbf/XdbfFormat.cs
│   │   ├── Xma/XmaFormat.cs    # Implements IFileRepairer, IFileConverter
│   │   └── Xui/XuiFormat.cs    # Implements IFileConverter
│   ├── Minidump/           # Minidump format parsing
│   │   ├── MinidumpInfo.cs
│   │   ├── MinidumpModels.cs
│   │   └── MinidumpParser.cs
│   ├── Utils/              # Utility classes
│   │   ├── BinaryUtils.cs
│   │   ├── SignatureBoundaryScanner.cs
│   │   └── TexturePathExtractor.cs
│   ├── MemoryDumpAnalyzer.cs   # Build detection, ESM extraction
│   ├── SignatureMatcher.cs     # Aho-Corasick multi-pattern search
│   └── Models.cs               # Shared model types
├── App/                    # WinUI 3 GUI components (Windows only)
├── CLI/                    # CLI command implementations
├── Program.cs              # CLI entry point
└── GuiEntryPoint.cs        # GUI bootstrap (Windows only)
```

---

## Core Components

### SignatureMatcher (Aho-Corasick Algorithm)

The `SignatureMatcher` class implements the Aho-Corasick algorithm for efficient multi-pattern string matching. This enables scanning large memory dumps (200MB+) for multiple file signatures in a single pass.

**Key Features:**

- O(n + m) time complexity where n = data length, m = total matches
- Builds a trie with failure links for backtracking
- Returns all matches with their offsets

**Usage:**

```csharp
var matcher = new SignatureMatcher();
matcher.AddPattern("dds", Encoding.ASCII.GetBytes("DDS "));
matcher.AddPattern("ddx", Encoding.ASCII.GetBytes("3XDO"));
matcher.Build(); // Build failure links

var matches = matcher.Search(dumpData);
// Returns: [(name, pattern, offset), ...]
```

### MemoryCarver

The main carving engine that orchestrates the entire extraction process.

**Carving Pipeline:**

1. **Build Signature Matcher** - Load all registered signatures
2. **Scan Phase (0-50%)** - Find all signature matches via Aho-Corasick
3. **Parse Phase** - Use appropriate parser to determine file boundaries
4. **Extract Phase (50-100%)** - Write files to output directory
5. **Convert Phase** - Optional DDX→DDS conversion
6. **Manifest** - Save JSON manifest of all carved files

**Configuration Options:**

| Option            | Description                                |
| ----------------- | ------------------------------------------ |
| `outputDir`       | Base directory for extracted files         |
| `maxFilesPerType` | Limit per signature type (default: 10000)  |
| `convertDdxToDds` | Auto-convert DDX textures (default: true)  |
| `fileTypes`       | Filter to specific types (null = all)      |
| `verbose`         | Enable progress logging                    |
| `saveAtlas`       | Save intermediate atlas data for debugging |

### MinidumpParser

Parses Microsoft Minidump format files (`.dmp`) to extract structural information.

**Parsed Streams:**

| Stream Type        | ID  | Content                                        |
| ------------------ | --- | ---------------------------------------------- |
| SystemInfoStream   | 7   | Processor architecture (PowerPC = Xbox 360)    |
| ModuleListStream   | 4   | Loaded modules (exe, dll) with addresses/sizes |
| Memory64ListStream | 9   | Memory regions with virtual addresses          |

**Output Structure:**

```csharp
MinidumpInfo
├── IsValid: bool
├── ProcessorArchitecture: ushort (0x3 = PowerPC)
├── IsXbox360: bool
├── Modules: List<MinidumpModule>
│   ├── Name: string
│   ├── BaseAddress32: uint
│   ├── Size: uint
│   ├── Checksum: uint
│   └── TimeDateStamp: uint
└── MemoryRegions: List<MinidumpMemoryRegion>
    ├── VirtualAddress: long
    ├── Size: long
    └── FileOffset: long
```

---

## File Type System

### FormatRegistry

Central registry of all supported file formats. Uses explicit registration for trim/AOT compatibility.

**Key Features:**

- Explicit registration of `IFileFormat` implementations (no reflection)
- Type definitions with signatures, extensions, and size constraints
- Category-based organization and coloring
- Support for optional interfaces: `IFileConverter`, `IFileRepairer`, `IDumpScanner`

**File Categories:**

| Category | Color (ARGB)       | Description             |
| -------- | ------------------ | ----------------------- |
| Texture  | `#2ECC71` (Green)  | DDS, DDX texture files  |
| Image    | `#1ABC9C` (Teal)   | PNG images              |
| Audio    | `#E74C3C` (Red)    | XMA audio, LIP lip-sync |
| Model    | `#F1C40F` (Yellow) | NIF Gamebryo models     |
| Module   | `#9B59B6` (Purple) | XEX executables/DLLs    |
| Script   | `#E67E22` (Orange) | ObScript, SCDA bytecode |
| Xbox     | `#3498DB` (Blue)   | XDBF, XUI system files  |
| Plugin   | `#FF6B9D` (Pink)   | ESP/ESM plugin files    |

### IFileFormat Interface

Base interface for all format modules. Each format is self-contained with parsing, metadata, and optional capabilities.

```csharp
public interface IFileFormat
{
    string FormatId { get; }           // Unique identifier (e.g., "ddx")
    string DisplayName { get; }        // UI display name (e.g., "DDX")
    string Extension { get; }          // Output file extension
    FileCategory Category { get; }     // Category for coloring
    string OutputFolder { get; }       // Subdirectory for output
    int MinSize { get; }               // Minimum valid file size
    int MaxSize { get; }               // Maximum valid file size
    int DisplayPriority { get; }       // Overlap resolution priority
    IReadOnlyList<FormatSignature> Signatures { get; }  // Magic byte patterns

    ParseResult? Parse(ReadOnlySpan<byte> data, int offset = 0);
}
```

### Optional Capability Interfaces

Formats can implement additional interfaces for extended functionality:

```csharp
// For formats that support file conversion (e.g., DDX → DDS)
public interface IFileConverter
{
    string TargetExtension { get; }
    string TargetFolder { get; }
    bool IsInitialized { get; }
    int ConvertedCount { get; }
    int FailedCount { get; }

    bool Initialize(bool verbose = false, Dictionary<string, object>? options = null);
    bool CanConvert(string signatureId, IReadOnlyDictionary<string, object>? metadata);
    Task<DdxConversionResult> ConvertAsync(byte[] data, IReadOnlyDictionary<string, object>? metadata = null);
}

// For formats that support file repair (e.g., XMA seek table)
public interface IFileRepairer
{
    bool NeedsRepair(IReadOnlyDictionary<string, object>? metadata);
    byte[] Repair(byte[] data, IReadOnlyDictionary<string, object>? metadata);
}

// For formats that provide dump-wide scanning (e.g., SCDA, ESM records)
public interface IDumpScanner
{
    Task<object> ScanDumpAsync(string dumpPath, IProgress<double>? progress = null);
}
```

---

## Format Module Architecture

All format modules extend `FileFormatBase` and are self-contained in their own folders.

### Format Module Structure

| Format Module     | Location                  | Capabilities                                 |
| ----------------- | ------------------------- | -------------------------------------------- |
| `DdsFormat`       | `Core/Formats/Dds/`       | Parsing only                                 |
| `DdxFormat`       | `Core/Formats/Ddx/`       | Parsing + `IFileConverter`                   |
| `EsmRecordFormat` | `Core/Formats/EsmRecord/` | `IDumpScanner` (ESM record extraction)       |
| `EspFormat`       | `Core/Formats/Esp/`       | Parsing only                                 |
| `LipFormat`       | `Core/Formats/Lip/`       | Parsing only                                 |
| `NifFormat`       | `Core/Formats/Nif/`       | Parsing + BE→LE conversion                   |
| `PngFormat`       | `Core/Formats/Png/`       | Parsing only                                 |
| `ScdaFormat`      | `Core/Formats/Scda/`      | Parsing + `IDumpScanner`                     |
| `ScriptFormat`    | `Core/Formats/Script/`    | Parsing only                                 |
| `XdbfFormat`      | `Core/Formats/Xdbf/`      | Parsing only                                 |
| `XmaFormat`       | `Core/Formats/Xma/`       | Parsing + `IFileRepairer` + `IFileConverter` |
| `XuiFormat`       | `Core/Formats/Xui/`       | Parsing + `IFileConverter`                   |

### NIF Converter Module Structure

The NIF converter has been modularized into specialized components:

| Component                     | Purpose                                               |
| ----------------------------- | ----------------------------------------------------- |
| `NifFormat`                   | Main format module with signature and parsing         |
| `NifFormat.Converter`         | `IFileConverter` implementation                       |
| `NifParser`                   | Parses NIF header, block types, and block offsets     |
| `NifConverter`                | Orchestrates conversion from Xbox 360 to PC format    |
| `NifConverter.Writers`        | Writes converted header, blocks, and footer           |
| `NifConverter.GeometryWriter` | Writes expanded geometry blocks                       |
| `NifPackedDataExtractor`      | Extracts geometry from BSPackedAdditionalGeometryData |
| `NifSchemaConverter`          | Schema-driven endian conversion                       |
| `NifSkinPartitionParser`      | Parses NiSkinPartition for triangles/bones            |
| `NifSkinPartitionExpander`    | Expands bone weights/indices for PC format            |
| `NifEndianUtils`              | Low-level byte-swapping utilities                     |
| `NifTypes`                    | Shared types: NifInfo, BlockInfo, ConversionResult    |

### ParseResult

Common result type returned by all format parsers:

```csharp
public record ParseResult
{
    public required string Format { get; init; }
    public required int EstimatedSize { get; init; }
    public string? FileName { get; init; }
    public Dictionary<string, object> Metadata { get; init; } = [];
}
```

---

## Analysis Module

### MemoryDumpAnalyzer

Provides comprehensive dump analysis combining multiple data sources.

**Analysis Pipeline:**

1. Parse minidump header → Modules, memory regions
2. Detect build type (Debug, Release Beta, Release MemDebug)
3. Scan for SCDA records → Compiled scripts (via `ScdaFormat.IDumpScanner`)
4. Scan for ESM records → EDID, GMST, SCTX, SCRO (via `EsmRecordFormat.IDumpScanner`)
5. Correlate FormIDs to names

**Output Formats:**

| Format   | Command                    | Description             |
| -------- | -------------------------- | ----------------------- |
| Text     | `analyze dump.dmp`         | Console summary         |
| Markdown | `analyze dump.dmp -f md`   | Full report with tables |
| JSON     | `analyze dump.dmp -f json` | Machine-readable        |

---

## GUI Architecture

The WinUI 3 GUI is built with XAML and C# code-behind (MVVM-lite pattern).

### Main Components

| Component            | Purpose                                 |
| -------------------- | --------------------------------------- |
| `MainWindow`         | Main application window, tab navigation |
| `SingleFileTab`      | Single file analysis with hex viewer    |
| `BatchModeTab`       | Batch processing multiple dumps         |
| `HexViewerControl`   | Virtual-scrolling hex editor            |
| `HexMinimapControl`  | VS Code-style minimap overview          |
| `HexMinimapRenderer` | Bitmap rendering for minimap            |
| `HexRowRenderer`     | Row-level hex rendering                 |

### Threading Model

- **UI Thread**: All XAML updates via `DispatcherQueue.TryEnqueue()`
- **Background Tasks**: Carving, analysis, loading via `Task.Run()`
- **Progress Reporting**: `IProgress<T>` pattern for UI updates
- **Cancellation**: `CancellationToken` support throughout

### Key Patterns

```csharp
// Background operation with progress
await Task.Run(async () =>
{
    var progress = new Progress<double>(p =>
    {
        DispatcherQueue.TryEnqueue(() => ProgressBar.Value = p * 100);
    });

    await carver.CarveDumpAsync(path, progress);
});

// Cancellation support
private CancellationTokenSource? _cts;

private async void StartOperation()
{
    _cts = new CancellationTokenSource();
    try
    {
        await LongRunningOperationAsync(_cts.Token);
    }
    catch (OperationCanceledException) { }
}

private void CancelOperation() => _cts?.Cancel();
```

---

## Extensibility Guide

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
        if (data.Length < offset + 16) return null;
        if (!data.Slice(offset, 4).SequenceEqual("NEWF"u8)) return null;

        var size = BinaryUtils.ReadUInt32LE(data, offset + 4);
        return new ParseResult
        {
            Format = "NEWF",
            EstimatedSize = (int)size,
            Metadata = new Dictionary<string, object>
            {
                ["version"] = BinaryUtils.ReadUInt16LE(data, offset + 8)
            }
        };
    }
}
```

3. Register the format in `FormatRegistry.CreateFormats()` method
4. For conversion/repair capabilities, implement `IFileConverter` or `IFileRepairer`
5. For dump-wide scanning, implement `IDumpScanner`

### Adding a New CLI Command

1. Create command factory in `Program.cs`:

```csharp
private static Command CreateNewCommand()
{
    var command = new Command("newcmd", "Description of new command");

    var inputArg = new Argument<string>("input", "Input file path");
    var optionA = new Option<bool>(["-a", "--option-a"], "Option description");

    command.AddArgument(inputArg);
    command.AddOption(optionA);

    command.SetHandler((input, optionA) =>
    {
        // Implementation
    }, inputArg, optionA);

    return command;
}
```

2. Register in `RunCliAsync`:

```csharp
var newCommand = CreateNewCommand();
rootCommand.AddCommand(newCommand);
```

### Adding a New Analysis Module

1. Create analyzer in `Core/Analysis/`:

```csharp
public static class NewAnalyzer
{
    public record AnalysisResult { /* fields */ }

    public static AnalysisResult Analyze(byte[] data)
    {
        // Implementation
    }
}
```

2. Integrate with `MemoryDumpAnalyzer.AnalyzeAsync()` if needed for unified reporting.

---

## Performance Considerations

### Memory Management

- **Memory-mapped files**: Used for large dump access without full loading
- **ArrayPool**: Pooled buffers for chunk reading
- **Span<T>**: Zero-allocation slicing for parsing

### Scanning Optimization

- **Aho-Corasick**: Single-pass multi-pattern matching
- **Chunk-based reading**: 64 MB chunks with pattern overlap
- **Parallel extraction**: `Task.WhenAll` for independent file writes
- **Early termination**: Skip processing when `maxFilesPerType` reached

### GUI Virtualization

- **Virtual scrolling**: Only render visible hex rows
- **Bitmap caching**: Pre-render minimap regions
- **Debounced updates**: Throttle rapid scroll events

---

## Testing

### Unit Tests

Located in `tests/Xbox360MemoryCarver.Tests/`:

```bash
dotnet test
```

### Integration Testing

```bash
# Test CLI carving
dotnet run -f net10.0 -- Sample/MemoryDump/test.dmp -o TestOutput -v

# Test analysis command
dotnet run -f net10.0 -- analyze Sample/MemoryDump/test.dmp -f md
```

### Test Coverage

```bash
dotnet test --collect:"XPlat Code Coverage"
```
