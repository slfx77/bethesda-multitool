# Xbox 360 Memory Carver (C#)

A high-performance C# application for extracting usable data from Xbox 360 memory dumps and converting DDX texture files to DDS format.

## Features

- **High Performance**: Native C# implementation with parallel processing support
- **XCompression Support**: Native Xbox 360 XMemCompress/XMemDecompress via xcompress64.dll
- **Multiple File Format Support**: Carves DDS textures, XMA audio, NIF models, Bethesda scripts, and more
- **Xbox 360 Optimized**: Handles Xbox 360-specific formats including big-endian byte order and swizzled textures
- **Memory Efficient**: Processes large dumps in chunks to prevent excessive memory usage
- **Progress Tracking**: Real-time progress bars using Spectre.Console
- **Batch Processing**: Process multiple dump files at once

## Supported File Types

### Textures

| Type       | Extension | Description                               |
| ---------- | --------- | ----------------------------------------- |
| `dds`      | .dds      | DirectDraw Surface textures               |
| `ddx_3xdo` | .ddx      | Xbox 360 DDX textures (3XDO format)       |
| `ddx_3xdr` | .ddx      | Xbox 360 DDX textures (3XDR engine-tiled) |

### 3D Models & Animations

| Type  | Extension | Description              |
| ----- | --------- | ------------------------ |
| `nif` | .nif      | Gamebryo 3D models       |
| `kf`  | .kf       | Gamebryo animation files |
| `egm` | .egm      | FaceGen morph files      |
| `egt` | .egt      | FaceGen tint files       |

### Audio

| Type  | Extension | Description              |
| ----- | --------- | ------------------------ |
| `xma` | .xma      | Xbox Media Audio files   |
| `ogg` | .ogg      | Ogg Vorbis audio files   |
| `lip` | .lip      | Lip-sync animation files |

### Scripts

| Type         | Extension | Description                               |
| ------------ | --------- | ----------------------------------------- |
| `script_scn` | .txt      | Bethesda script files (scn format)        |
| `script_sn`  | .txt      | Bethesda script files (ScriptName format) |

### Xbox 360 System Formats

| Type   | Extension | Description         |
| ------ | --------- | ------------------- |
| `xex`  | .xex      | Xbox 360 Executable |
| `xdbf` | .xdbf     | Xbox Dashboard File |
| `xuis` | .xuis     | Xbox UI Scene/Skin  |

## Requirements

- .NET 8.0 or later
- Windows (for XCompression native library support)
- Optional: xcompress64.dll (for XMemDecompress support)

## Building

```bash
dotnet build -c Release
```

## Usage

### Basic Usage - Carve Files from Dump

```bash
dotnet run -- Sample/Fallout_Debug.xex.dmp
```

### Process All Dumps in Directory

```bash
dotnet run -- Sample/
```

### Convert DDX Files to DDS

```bash
dotnet run -- --ddx path/to/ddx/files/
```

### Quick Sample DDX Batch Runs

Convert the bundled sample texture DDX files:

```bash
dotnet run -- --ddx Sample/Texture -o Output/SampleTextureDDS
```

Convert carved DDX files found in the sample memory dumps:

```bash
dotnet run -- --ddx Sample/MemoryDump -o Output/SampleDumpDDS
```

### Options

```
Arguments:
  <input>              Path to memory dump file (.dmp) or directory; or DDX file/directory when using --ddx

Options:
  -o, --output <path>  Output directory (default: "output")
  --ddx                Convert DDX textures to DDS instead of carving dumps
  --convert-ddx        Automatically convert carved DDX textures to DDS during carving (default: true)
  -t, --types <types>  File types to extract (e.g., -t dds ddx xma)
  -v, --verbose        Enable verbose output
  --chunk-size <size>  Chunk size in bytes (default: 10485760)
  --max-files <count>  Maximum files per type (default: 10000)
  --help               Show help information
  --version            Show version information
```

## XCompression Setup

For full Xbox 360 compressed texture support, place one of these DLLs in the application directory:

1. **xcompress64.dll** - From various Xbox 360 development tools
2. **XnaNative.dll** - From Microsoft XNA Framework 4.0

The application will work without these DLLs, but some compressed DDX files may not convert correctly.

## Project Structure

```
├── Xbox360MemoryCarver.csproj # Project file
├── Program.cs                 # Entry point and CLI
├── Carving/
│   └── MemoryCarver.cs       # Main carving engine
├── Compression/
│   └── XCompression.cs       # Xbox 360 decompression
├── Converters/
│   └── DdxConverter.cs       # DDX to DDS conversion
├── Minidump/
│   └── MinidumpExtractor.cs  # PE extraction from dumps
├── Models/
│   ├── Models.cs             # Data models
│   └── FileSignatures.cs     # File signature definitions
├── Parsers/
│   └── FileParsers.cs        # Format-specific parsers
├── Reporting/
│   └── ReportGenerator.cs    # Extraction reports
└── Utils/
    └── BinaryUtils.cs        # Binary helper functions
```

## Technical Notes

### DDX Format

- **3XDO**: Standard compressed format - fully supported
- **3XDR**: Engine-tiled format - experimental support (uses different tiling pattern)

### Texture Processing

- Xbox 360 textures use Morton/Z-order (swizzled) memory layout
- Big-endian byte order requires swapping for PC compatibility
- Block-compressed formats (DXT1/3/5, ATI1/2) require special handling

### Performance Tips

- Use SSD storage for large dump files
- Increase chunk size for better throughput on systems with more RAM
- Filter to specific file types if you only need certain formats

## License

MIT License - See LICENSE file for details.

## Credits

Based on algorithms from:

- [DDXConv](https://github.com/kran27/DDXConv)
- [Xenia Xbox 360 Emulator](https://github.com/xenia-project/xenia)
- [XCompression](https://github.com/gibbed/XCompression)
