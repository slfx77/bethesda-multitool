# Xbox 360 Memory Carver

A high-performance WinUI 3 desktop application for extracting usable data from Xbox 360 memory dumps and converting DDX texture files to DDS format.

## Features

-   **Modern GUI**: WinUI 3 desktop application with hex viewer and minimap
-   **High Performance**: Native C# implementation with parallel processing support
-   **XCompression Support**: Native Xbox 360 XMemCompress/XMemDecompress via XnaNative.dll from XNA Framework
-   **Multiple File Format Support**: Carves DDS textures, XMA audio, NIF models, Bethesda scripts, and more
-   **Xbox 360 Optimized**: Handles Xbox 360-specific formats including big-endian byte order and swizzled textures
-   **Memory Efficient**: Processes large dumps in chunks to prevent excessive memory usage
-   **Batch Processing**: Process multiple dump files at once

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

-   .NET 10.0 or later
-   Windows 10/11 (WinUI 3 requires Windows 10 version 1809 or later)
-   Microsoft XNA Framework 4.0 (for XnaNative.dll decompression support)

## Building

```bash
dotnet build -c Release
```

Or build from solution:

```bash
dotnet build Xbox360MemoryCarver.slnx -c Release
```

## Running the Application

```bash
dotnet run --project src/Xbox360MemoryCarver.App
```

Or run the built executable directly:

```bash
.\src\Xbox360MemoryCarver.App\bin\Release\net10.0-windows10.0.22621.0\win-x64\Xbox360MemoryCarver.App.exe
```

## DDXConv Command-Line Tool

The DDXConv command-line tool can also be used standalone for batch DDX to DDS conversion:

```bash
# Single file conversion
dotnet run --project src/DDXConv/DDXConv -- path/to/texture.ddx output.dds

# Batch conversion
dotnet run --project src/DDXConv/DDXConv -- path/to/ddx/folder output/folder
```

### DDXConv Options

```
Standard Options:
  --pc-friendly, -pc   Produce PC-ready normal maps (batch conversion only)
  --regen-mips, -g     Regenerate mip levels from top level

Memory Dump Options:
  --memory, -m         Use memory texture parser (handles memory dump layouts)
  --atlas, -a          Save full untiled atlas as separate DDS file

Developer Options:
  --verbose, -v        Enable verbose output
```

Options:
-o, --output <path> Output directory (default: "output")
--ddx Convert DDX textures to DDS instead of carving dumps
--convert-ddx Automatically convert carved DDX textures to DDS during carving (default: true)
-t, --types <types> File types to extract (e.g., -t dds ddx xma)
-v, --verbose Enable verbose output
--chunk-size <size> Chunk size in bytes (default: 10485760)
--max-files <count> Maximum files per type (default: 10000)
--help Show help information
--version Show version information

```

## XCompression Setup

For full Xbox 360 compressed texture support, install Microsoft XNA Framework 4.0. The application uses XnaNative.dll from the XNA Framework for LZX decompression.

If XNA Framework is not installed, DDX files cannot be converted (the converter subprocess will fail).

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
```
