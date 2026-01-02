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
│  │  Carving/   │ │  Parsers/   │ │  Analysis/  │ │    FileTypes/         │ │
│  │MemoryCarver │ │ 16 parsers  │ │DumpAnalyzer │ │ FileTypeRegistry      │ │
│  │CarveManifest│ │ IFileParser │ │             │ │ FileTypeDefinition    │ │
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
│   ├── Analysis/           # Dump analysis and reporting
│   │   └── DumpAnalyzer.cs
│   ├── Carving/            # File carving engine
│   │   ├── CarveManifest.cs
│   │   └── MemoryCarver.cs
│   ├── Converters/         # DDX to DDS conversion
│   │   ├── DdxConversionResult.cs
│   │   └── DdxSubprocessConverter.cs
│   ├── Extractors/         # Specialized extraction logic
│   │   └── ScriptExtractor.cs
│   ├── FileTypes/          # File type registry system
│   │   ├── FileTypeDefinition.cs
│   │   ├── FileTypeRegistry.cs
│   │   ├── ParserRegistry.cs
│   │   └── TextureFormats.cs
│   ├── Minidump/           # Minidump format parsing
│   │   ├── MinidumpInfo.cs
│   │   ├── MinidumpModels.cs
│   │   └── MinidumpParser.cs
│   ├── Parsers/            # File format parsers
│   │   ├── DdsParser.cs
│   │   ├── DdxParser.cs
│   │   ├── EsmRecordParser.cs
│   │   ├── EspParser.cs
│   │   ├── IFileParser.cs
│   │   ├── LipParser.cs
│   │   ├── NifParser.cs
│   │   ├── PngParser.cs
│   │   ├── ScdaParser.cs
│   │   ├── ScriptParser.cs
│   │   ├── SignatureBoundaryScanner.cs
│   │   ├── TexturePathExtractor.cs
│   │   ├── XdbfParser.cs
│   │   ├── XexParser.cs
│   │   ├── XmaParser.cs
│   │   └── XuiParser.cs
│   ├── Utils/              # Utility classes
│   │   └── BinaryUtils.cs
│   ├── SignatureMatcher.cs # Aho-Corasick multi-pattern search
│   └── Models.cs           # Shared model types
├── *.xaml / *.xaml.cs      # WinUI 3 GUI components
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

### FileTypeRegistry

Central registry of all supported file types. Provides:

- Type definitions with signatures, extensions, and size constraints
- Category-based organization and coloring
- Parser type resolution
- Signature ID normalization

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

### FileTypeDefinition

Defines a single file type with all its variants.

```csharp
FileTypeDefinition
├── TypeId: string          // Unique identifier (e.g., "ddx")
├── DisplayName: string     // UI display name (e.g., "DDX")
├── Extension: string       // Output file extension
├── Category: FileCategory  // Category for coloring
├── OutputFolder: string    // Subdirectory for output
├── MinSize / MaxSize: int  // Size constraints
├── DisplayPriority: int    // Overlap resolution priority
├── ParserType: Type        // Associated IFileParser implementation
└── Signatures: FileSignature[]
    ├── Id: string          // Signature ID (e.g., "ddx_3xdo")
    ├── MagicBytes: byte[]  // Magic bytes to match
    └── Description: string // Human-readable description
```

---

## Parser Architecture

All parsers implement the `IFileParser` interface:

```csharp
public interface IFileParser
{
    ParseResult? ParseHeader(ReadOnlySpan<byte> data, int offset = 0);
}

public record ParseResult
{
    public required string Format { get; init; }
    public required int EstimatedSize { get; init; }
    public string? FileName { get; init; }
    public Dictionary<string, object> Metadata { get; init; } = [];
}
```

### Registered Parsers

| Parser            | File Types       | Key Features                                          |
| ----------------- | ---------------- | ----------------------------------------------------- |
| `DdsParser`       | DDS              | Reads DX9/DX10 headers, calculates mip chain size     |
| `DdxParser`       | DDX (3XDO, 3XDR) | Xbox 360 texture headers, chunk detection             |
| `PngParser`       | PNG              | IEND chunk detection for size                         |
| `XmaParser`       | XMA              | RIFF format, XMA2 codec validation                    |
| `LipParser`       | LIP              | Lip-sync animation files                              |
| `NifParser`       | NIF              | Gamebryo version validation, false positive filtering |
| `XexParser`       | XEX              | Xbox executable header parsing                        |
| `ScriptParser`    | ObScript         | Source text boundary detection                        |
| `ScdaParser`      | SCDA             | Compiled bytecode + associated SCTX                   |
| `EspParser`       | ESP/ESM          | TES4 plugin header                                    |
| `XdbfParser`      | XDBF             | Xbox Dashboard files                                  |
| `XuiParser`       | XUI              | Xbox UI (XUIS, XUIB variants)                         |
| `EsmRecordParser` | N/A (analysis)   | Extracts EDID, GMST, SCTX, SCRO fragments             |

### Adding a New Parser

1. Create `Core/Parsers/NewFormatParser.cs`:

```csharp
public class NewFormatParser : IFileParser
{
    private static readonly byte[] Signature = "NEWF"u8.ToArray();

    public ParseResult? ParseHeader(ReadOnlySpan<byte> data, int offset = 0)
    {
        if (data.Length < offset + 16) return null;
        var span = data[offset..];
        if (!span[..4].SequenceEqual(Signature)) return null;

        var size = BinaryUtils.ReadUInt32LE(span, 4);
        return new ParseResult
        {
            Format = "NEWF",
            EstimatedSize = (int)size,
            Metadata = new Dictionary<string, object>
            {
                ["version"] = BinaryUtils.ReadUInt16LE(span, 8)
            }
        };
    }
}
```

2. Register in `FileTypeRegistry.cs`:

```csharp
new FileTypeDefinition
{
    TypeId = "newformat",
    DisplayName = "NEWF",
    Extension = ".newf",
    Category = FileCategory.Data,
    OutputFolder = "newformat",
    MinSize = 16,
    MaxSize = 10 * 1024 * 1024,
    ParserType = typeof(NewFormatParser),
    Signatures = [
        new FileSignature
        {
            Id = "newformat",
            MagicBytes = Encoding.ASCII.GetBytes("NEWF"),
            Description = "New Format File"
        }
    ]
}
```

---

## Analysis Module

### DumpAnalyzer

Provides comprehensive dump analysis combining multiple data sources.

**Analysis Pipeline:**

1. Parse minidump header → Modules, memory regions
2. Detect build type (Debug, Release Beta, Release MemDebug)
3. Scan for SCDA records → Compiled scripts
4. Scan for ESM records → EDID, GMST, SCTX, SCRO
5. Correlate FormIDs to names

**Output Formats:**

| Format   | Command                    | Description             |
| -------- | -------------------------- | ----------------------- |
| Text     | `analyze dump.dmp`         | Console summary         |
| Markdown | `analyze dump.dmp -f md`   | Full report with tables |
| JSON     | `analyze dump.dmp -f json` | Machine-readable        |

### ScriptExtractor

Extracts and groups compiled script bytecode (SCDA records) by quest name.

**Grouping Algorithm:**

1. Scan dump for all SCDA records
2. Extract quest name from associated SCTX source
3. Group scripts with same quest prefix
4. Assign orphans by offset proximity
5. Output grouped files: `QuestName_stages.txt`

**Quest Name Patterns:**

- `VMS03.nPowerConfiguration` → Quest: `VMS03`
- `SetObjectiveDisplayed VFreeformCampGolf 10 1` → Quest: `VFreeformCampGolf`

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

### Adding a New File Type

See [Adding a New File Signature](../.github/copilot-instructions.md#adding-a-new-file-signature) in the Copilot instructions.

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

2. Integrate with `DumpAnalyzer.AnalyzeAsync()` if needed for unified reporting.

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
