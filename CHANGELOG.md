# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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
  - File extraction with DDX→DDS conversion
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

[Unreleased]: https://github.com/slfx77/xbox-360-minidump-extractor/compare/v0.2.0-alpha.1...HEAD
[0.2.0-alpha.1]: https://github.com/slfx77/xbox-360-minidump-extractor/compare/v0.1.0-alpha.1...v0.2.0-alpha.1
[0.1.0-alpha.1]: https://github.com/slfx77/xbox-360-minidump-extractor/releases/tag/v0.1.0-alpha.1
