# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- **FaceGen Format Detection**: New format module for Bethesda FaceGen files
  - Detects EGM (face morphs), EGT (face tints), and TRI (triangle morphs) files
  - Magic signatures: `FREGM`, `FREGT`, `FRTRI`
  - Used for character face customization data
- **Bink Video Format Detection**: New format module for RAD Game Tools Bink video
  - Detects Bink 2.x video files (BIKi, BIKh, BIKb variants)
  - Validates dimensions, frame count, and file size from header
  - Used for pre-rendered cinematics and logo videos
- **Video File Category**: New category for video file formats (purple-pink color)
- Tests for DDX GPU format validation covering unknown format rejection and known format acceptance
- Tests for new FaceGen and Bink signature detection

### Changed

- **DDX Format Validation**: Stricter GPU format validation to reduce false positives during memory dump carving
  - DDX headers with unknown/invalid GPU format bytes are now rejected
  - Only known Xbox 360 texture formats (DXT1, DXT3, DXT5, ATI1, ATI2) are accepted
  - Prevents random data matching "3XDO"/"3XDR" magic from being incorrectly identified as textures

### Fixed

- **LIP Format Detection Removed**: The "LIPS" signature was matching asset path strings in memory
  (e.g., "sound/voice/.../filename.lip"), not actual LIP files
  - Real LIP files have no magic header - they start with version/data bytes
  - Across 50+ crash dumps analyzed, 0 valid LIP files were found
  - LIP scanning is now disabled (`EnableSignatureScanning = false`)
  - The format module remains for potential future use with known file offsets

## [1.0.0] - 2026-01-17

### Added

- **Application Icon**: Embedded application icon for the executable
- **JSON Source Generation**: Added partial class for trim-compatible JSON serialization
- **Logger System**: Comprehensive logging with verbosity levels (None, Error, Warn, Info, Debug, Trace)

### Changed

- **NIF Converter Refactoring**: Modularized NIF converter into specialized components
  - `NifParser` - Header and block structure parsing
  - `NifConverter` - Conversion orchestration with partial class files (.Writers, .GeometryWriter, etc.)
  - `NifPackedDataExtractor` - BSPackedAdditionalGeometryData extraction
  - `NifSchemaConverter` - Schema-driven endian conversion
  - `NifSkinPartitionParser` - NiSkinPartition parsing for triangles/bones
  - `NifSkinPartitionExpander` - Expands bone weights/indices for PC format
  - `NifEndianUtils` - Low-level byte-swapping utilities
  - `NifTypes` - Shared type definitions
- Added JSON source generation contexts for AOT compatibility
- Improved code style consistency with enforced curly braces on all control flow statements
- Updated documentation for NIF conversion status (all features now implemented)

### Removed

- `NifEndianConverter.cs` - Replaced by modular components
- `NifXmlSchema.cs` - No longer needed with new parsing approach

### Fixed

- **NIF Converter: HavokFilter endian conversion** - Fixed packed struct conversion for Havok collision blocks
  - Structs with `size="2"`, `size="4"`, or `size="8"` (like `HavokFilter`) are now bulk-swapped as single units
  - Previously, individual fields within packed structs were swapped separately, corrupting the data
  - This fixes collision wireframe colors in NifSkope (Layer field now correctly shows Red instead of Green)
- **NIF Converter: Stride-based skinned mesh detection** - Fixed false positive detection for skinned meshes
  - Changed detection from "ubyte4 at offset 16" to "stride == 48" as the sole skinned indicator
  - Non-skinned meshes with stride 40 now correctly extract vertex colors instead of fake bone indices
  - Affected meshes like `nv_prospectorsaloon.nif` now render with correct normals and vertex colors
- Build warnings for missing curly braces in foreach/for loops (S3973)

## [0.2.0-alpha.1] - 2026-01-07

### Added

- **NIF Converter**: Convert Xbox 360 NIF models (big-endian) to PC format (little-endian)
  - GUI tab for batch NIF conversion with drag-and-drop support
  - CLI command `convert-nif` for scripted/batch processing
  - Strips Xbox 360-specific `BSPackedAdditionalGeometryData` blocks
  - Handles geometry data byte-swapping for vertices, normals, UVs, and triangles
- **Dump Analysis**: New `analyze` CLI command for comprehensive dump reports
  - Build type detection (Debug, Release Beta, Release MemDebug)
  - SCDA compiled script scanning with source text extraction
  - ESM record extraction (EDID, GMST, SCTX, SCRO)
  - FormID to EditorID correlation mapping
- **Module Listing**: New `modules` CLI command to list loaded modules from minidumps
  - Supports text, markdown, and CSV output formats
- **Script Extraction**: Extract and group compiled scripts (SCDA) by quest name
- **XUIHelper Integration**: XUR to XUI conversion support via submodule

### Changed

- Reorganized CLI commands into dedicated `CLI/` folder
- Improved NIF format validation with regex version checking to reduce false positives
- Updated copilot-instructions.md with NIF conversion documentation

### Fixed

- NIF signature detection now validates version format to prevent false positives

## [0.1.0-alpha.1] - 2025-12-15

### Added

- Initial release
- **Memory Carving Engine**: Aho-Corasick multi-pattern signature matching
- **WinUI 3 GUI** (Windows only):
  - Hex viewer with virtual scrolling for 200MB+ files
  - VS Code-style minimap with file type region coloring
  - Analysis tab with file signature detection, filtering, and statistics
  - File extraction with DDX -> DDS conversion
- **Cross-platform CLI**:
  - Batch file carving from memory dumps
  - Verbose progress reporting
- **Supported File Types**:
  - Textures: DDX (3XDO/3XDR), DDS, PNG
  - Audio: XMA (Xbox Media Audio), LIP (lip sync)
  - Models: NIF (NetImmerse/Gamebryo)
  - Scripts: ObScript (uncompiled), SCDA (compiled bytecode)
  - Executables: XEX (Xbox Executable)
  - Data: ESP/ESM (Bethesda plugins), XUI (Xbox UI), XDBF
- **DDX Conversion**: Xbox 360 DDX textures to standard DDS format
- **Minidump Parsing**: Extract module information from Xbox 360 minidumps

[1.0.0]: https://github.com/slfx77/xbox-360-minidump-extractor/compare/v0.2.0-alpha.1...v1.0.0
[0.2.0-alpha.1]: https://github.com/slfx77/xbox-360-minidump-extractor/compare/v0.1.0-alpha.1...v0.2.0-alpha.1
[0.1.0-alpha.1]: https://github.com/slfx77/xbox-360-minidump-extractor/releases/tag/v0.1.0-alpha.1
