# Xbox 360 Memory Carver

## Project Overview
High-performance .NET 8.0 console application for converting Xbox 360 DDX texture files (3XDO and 3XDR formats) to DDS format and carving files from memory dumps.

## Key Features
- XMemCompress/XMemDecompress support via XCompression library (xcompress64.dll or XnaNative.dll)
- Xbox 360 texture untiling/unswizzling
- Support for both 3XDO and 3XDR formats
- Command-line interface for batch processing
- Memory dump carving for DDS, DDX, XMA, NIF, scripts, and more

## Technical Notes
- 3XDO: Standard compressed format (working)
- 3XDR: Uses different tiling pattern (experimental support)
- Uses XnaNative.dll from XNA Framework for decompression
- Texture data is Morton-order tiled on Xbox 360

## Build Instructions
```bash
dotnet build -c Release
dotnet run -- <input.dmp> -o <output_dir>
dotnet run -- --ddx <input.ddx> -o <output.dds>
```

## Dependencies
- .NET 8.0 or later
- Spectre.Console (CLI output)
- System.CommandLine (argument parsing)
- Optional: xcompress64.dll or XnaNative.dll for Xbox compression
