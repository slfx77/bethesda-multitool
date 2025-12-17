# Xbox 360 Memory Carver

## Project Overview

High-performance .NET 10.0 console application for converting Xbox 360 DDX texture files (3XDO and 3XDR formats) to DDS format and carving files from memory dumps.

## Key Features

- XMemCompress/XMemDecompress support via XCompression library (xcompress64.dll or XnaNative.dll)
- Managed LZX decompression fallback when native DLLs unavailable (experimental, not fully working)
- Xbox 360 texture untiling/unswizzling
- Support for both 3XDO and 3XDR formats
- Command-line interface for batch processing
- Memory dump carving for DDS, DDX, XMA, NIF, scripts, and more
- Minidump analysis: module extraction, system info, crash details, thread list

## Technical Notes

- 3XDO: Standard compressed format (working)
- 3XDR: Uses different tiling pattern (experimental support)
- Uses XnaNative.dll from XNA Framework or managed LZX for decompression
- Texture data is Morton-order tiled on Xbox 360
- Minidumps from Xbox 360 devkits use PowerPC architecture (0x3)

## Build Instructions

```bash
dotnet build -c Release
dotnet run --project src/Xbox360MemoryCarver -- <input.dmp> -o <output_dir>
dotnet run --project src/Xbox360MemoryCarver -- --ddx <input.ddx> -o <output.dds>
```

## Project Structure

- src/Xbox360MemoryCarver/ - Main application with .csproj
- src/DDXConv/ - Reference submodule (DDXConv project)
- Sample/ - Sample data (memory dumps, textures)
- docs/ - Documentation

## Dependencies

- .NET 10.0 or later
- Spectre.Console (CLI output)
- System.CommandLine (argument parsing)
- Optional: xcompress64.dll or XnaNative.dll for native Xbox compression
