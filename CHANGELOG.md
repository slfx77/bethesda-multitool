# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [2.3.0] - 2026-03-22

### Added

- **Dialogue Viewer**: Full dialogue tree visualization in the GUI
  - Conversation builder with topic merging and runtime dialogue info
  - Dialogue tree renderer with condition display formatting
  - Dialogue picker tree builder for browsing quest dialogue
  - Dialogue condition parser with expanded operator and function support
  - Dialogue runtime merger for combining split Xbox 360 INFO records
- **NPC Browser**: Interactive NPC browsing with 3D model preview
  - BSA discovery for automatic mesh/texture archive detection
  - NPC list with filtering and selection
  - WebView2-based model viewer with embedded viewer HTML
- **Memory-Mapped File Access**: `IMemoryAccessor` abstraction with `MmfMemoryAccessor` implementation
  - Enables efficient random-access reads of large DMP files without loading into memory
  - `RecordingMemoryAccessor` and `SparseMemoryAccessor` test helpers for unit testing
- **Data-Driven Field Probes**: Runtime DMP struct reading via PDB-derived field layouts
  - Typed runtime readers for 14+ record types
  - Runtime probe consistency tests with baseline verification
  - Runtime world cell auto-detection and probing
  - BSStringT diagnostics for string field validation
- **NPC Export Pipeline**: Full-body NPC scene assembly with equipment resolution
  - `NpcExportSceneBuilder` for compositing head, body, and equipment meshes
  - `NpcEquipmentResolver` for armor addon and weapon mesh lookup
  - `NpcRecordScanner` for batch NPC appearance extraction
  - glTF export with alpha texture packing and material tuning
- **DMP Diagnostics**: `dmp dmp-diag` command for scanning persistent refs and map markers across dump directories
- **World Map Control**: Interactive world map display in the GUI
- **ESM Record Expansion**: Eyes (EYES), Hair (HAIR), and expanded effect data parsing
- **DMP Snippet Test Framework**: `DmpSnippetExtractor`, `DmpSnippetReader`, and `EsmTestFileBuilder` for self-contained test data

### Changed

- **NIF Rendering Pipeline**: Refactored sprite renderer, FaceGen mesh morpher, and NPC appearance factory
  - Deduplicated lighting constants across rendering code
  - Split large rendering files for maintainability
- **Runtime Struct Readers**: Expanded for broader DMP analysis coverage
  - Enhanced `RuntimeGenericReader` and `RuntimePdbFieldAccessor` for more field types
  - Expanded `RuntimeBuildOffsets` with additional build-specific offset tables
  - Updated `RuntimeMemoryContext` for memory-mapped accessor support
- **App UI**: Updated analysis views, record data display, and world map integration
  - Expanded `PropertyPanelBuilder` and `EsmBrowserTreeBuilder` for new record types
  - `FormUsageIndex` expanded for cross-reference tracking
- **Analysis Infrastructure**: Refactored `AnalysisExtractionHelper` and reporting layers
  - Semantic formatting added to export, report, and CLI output
- **ESM Parsing**: Expanded `RecordParser` and `RecordParserContext` for dialogue and new record types
  - `SubrecordSchemaRegistry.Dialogue` extended with new field definitions
- **DDXConv Submodule**: Updated with partial decompression, sequential mips, DDS header fixes, and 3XDR block-size-dependent untiling fix
- **Test Suite**: Refactored for expanded record types, runtime parity baselines, and DMP serialization
  - Improved test performance
  - Updated probe and regression baselines

### Fixed

- **NPC Head Attachment**: Fixed eye transform positioning for correct NPC head rendering
- **NPC Eye Rendering**: Stabilized eye mesh placement and orientation
- **Xbox NPC Attachment Posing**: Fixed holster and weapon attachment transforms
- **Holstered Baseball Bat**: Corrected attachment pose for baseball bat holster position

### Removed

- EGT verification and runtime capture comparison pipeline (superseded by probe-based testing)
- Obsolete test files: `PackageComparisonTests`, `XboxNpcGlbExportRegressionTests`, `XboxNpcRenderRegressionTests`, `ScriptDecompilerMemoryDumpTests`, `ScriptEnableDisablePatternTests`, `DumpAnalysisHelper`

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

[Unreleased]: https://github.com/slfx77/fallout-xbox-360-utils/compare/v2.3.0...HEAD
[2.3.0]: https://github.com/slfx77/fallout-xbox-360-utils/compare/v2.2.0-pre1...v2.3.0
[1.0.0]: https://github.com/slfx77/fallout-xbox-360-utils/compare/v0.2.0-alpha.1...v1.0.0
[0.2.0-alpha.1]: https://github.com/slfx77/fallout-xbox-360-utils/compare/v0.1.0-alpha.1...v0.2.0-alpha.1
[0.1.0-alpha.1]: https://github.com/slfx77/fallout-xbox-360-utils/releases/tag/v0.1.0-alpha.1
