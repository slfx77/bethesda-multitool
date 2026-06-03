# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Removed

- **Research / investigation docs**: pruned `docs/` to format-only documentation. Removed `Xbox_360_ESM_Conversion_Transforms.md`, `RTTI-ESM-Coverage.md`, `investigate-*.md` (3 files), `parity/` (SUMMARY + 2 baseline JSONs), `planner/migration-deltas.md`, `v3-scope.md`, plus the unused `runtime-refr-extra-baselines.json` and `runtime-world-cell-probe-baselines.json`.
- **In-repo memory notes**: removed `memory/bsa_mixed_archive_layout.md`, `memory/bsa_split_2gb_boundary.md`, `memory/dialogue_topic_link_remap.md` — investigation notes, not format docs.
- `MigrationDeltaMarkdownSyncTests.cs` — paired with the deleted `docs/planner/migration-deltas.md`; the C# `MigrationDeltaRegistry` is now the sole source of truth for delta entries.

### Changed

- **`runtime-parity-matrix.json` moved** from `docs/` to `tests/FalloutXbox360Utils.Tests/Resources/`. It's a load-bearing test fixture (consumed by `RuntimeParityMatrixTests`), not documentation.

## [3.0.0-alpha.1] - 2026-06-02

First alpha release of the 3.x line. The headline additions are the **DMP→ESP converter** (turn Xbox 360 memory dumps into PC-loadable plugins) and the **v3 worldspace 3D viewer** (real-time WinUI viewer with terrain, water, and placed references). Both ship as alpha-quality — known limitations are listed at the bottom of this entry. (Subsequent unreleased work prunes investigation/research notes that briefly lived in `docs/` and `memory/`; this entry's references to them point at the v3.0.0-alpha.1 tag snapshot.)

### Added

#### DMP→ESP converter

- **`dmp-to-esp` pipeline**: A complete two-pass plugin builder that reconstructs a vanilla-formatted PC ESP from an Xbox 360 minidump, optionally merged against the PC master ESM. The pipeline currently covers ~60 FormTypes with end-to-end (runtime read → encoder → emit) coverage and is opt-in via CLI flags or the new **DMP-to-ESP Converter GUI tab** (multi-DMP picker, dialogue CSV inputs, secondary-data overlays, live event log).
- **Encoder coverage**: end-to-end encoders for `ARMA`, `ARMO`, `BOOK`, `CARD`, `CCRD`, `CDCK`, `CELL`, `CMNY`, `COBJ`, `CONT`, `CPTH`, `CREA`, `DEBR`, `DIAL`, `DOOR`, `EXPL`, `GLOB`, `GMST`, `IDLE`, `IMAD`, `IMGS`, `IMOD`, `INFO`, `KEYM`, `LAND`, `LIGH`, `LSCT`, `LTEX`, `LVLI`/`LVLN`/`LVLC`, `MGEF`, `NAVI`, `NAVM`, `NOTE`, `NPC_`, `PACK`, `PERK`, `PROJ`, `PWAT`, `QUST`, `RACE`, `RCCT`, `RCPE`, `REFR`/`ACHR`/`ACRE`, `REGN`, `SCPT`, `SCOL`, `SOUN`, `SPEL`, `STAT`, `TERM`, `TREE`, `TXST`, `VTYP`, `WEAP`, `WRLD`.
- **Asset packing**: When emitting a plugin that references new mesh/texture/voice assets, the converter scans NIF subrecords for embedded texture paths, walks engine voice-file path conventions, and ingests dialogue CSV exports, then packs the required files into a co-emitted BSA whose layout matches per-content-category vanilla FNV archives (`BsaWriter.MatchVanillaLayout`).
- **Cell-authority data**: `data/cell_worldspace_authority.json` ships pre-computed cell → worldspace ownership for the four reference builds and is consumed by the converter to back-fill missing parent-cell links and to validate runtime-only cell recoveries.
- **Sanitizer pipeline**: post-emission scrubbers for NVEX dangling links, INFO CTDA dangling references (the "crucify on every NPC" idle bug), QUST/PERK condition lists, optional-FormID subrecords, and placed-ref FormIDs. Each sanitizer has synthetic in-memory tests under `tests/.../Plugin/`.

#### v3 Worldspace 3D Viewer

- **Phase 0**: GPU pipeline migrated from Veldrid to Vortice.Windows / D3D11; new `GpuDevice` + `GpuSwapChainSurface` host a `SwapChainPanel` inside WinUI 3.
- **Phase 1**: Camera (`CameraState` + `FlythroughCameraController`), cell-origin transforms, frustum culling.
- **Phase 2a**: Per-cell terrain meshes from runtime VHGT/VNML data (`TerrainMeshBuilder`, `TerrainHeightSampler`), water rendering, walk mode for ground-following navigation.
- **Phase 2b**: Per-cell landscape texturing via opacity-blended `terrain_textured` shaders, `TerrainOpacityTextureCache`, `TerrainTextureResolver` (LTEX → texture lookup with engine-default fallback); world-map 2D overlay system (`WorldMapLayerRenderer`, `WorldMapNavMeshOverlayRenderer`, `WorldSpatialIndex`, `WorldRenderCache`); multi-format Map Export dialog; per-stage frame profiling (env-flag gated `FALLOUT_VIEWER_PROFILE_LOG=1`).
- **Phase 2c**: Instanced 3D rendering of placed references (statics, doors, containers, lights) via `ReferenceRenderer` + `ReferenceMeshCache` with NIF → GPU buffer caching and frustum culling.

#### ESP Planner (two-pass pipeline)

- **Tier 0-7**: A from-scratch planner/encoder pipeline that walks DMP + master ESM in a catalog → disposition → reference-resolution → plan-write sequence. Replaces the prior single-pass converter on most record types; gated via `--planner-types` CLI flag. Covers cell-section orchestration (`PlanCellSectionBuilder`, `CellSectionPlanner`), reference walkers for SCPT/PACK/INFO/NPC_/CREA/PERK, and a `MigrationDelta` parity-harness foundation.
- **Specialized cell-child encoders**: LAND, NAVM, NAVI emission routed through planner-aware writers.
- **PGRE encoder + planner wrapper**: Tier 7a primitive for emitting placed grenade refs through the planner pipeline.

#### Runtime readers

- **PdbStructView abstraction**: data-driven runtime struct reading via PDB-derived field layouts; `PdbStructView.WithShift(owner, shift)` for offset adjustments. ~30 specialized readers migrated.
- **Typed runtime readers** for every remaining FormType the converter touches; coverage tracked in a parity-matrix JSON consumed by `RuntimeParityMatrixTests` (ratchet asserts the matrix matches `RecordCollection`).
- **`BsNavMeshStructuralValidator`** + **`RuntimeCellEnumerator`** + **`RuntimeNavMeshDiscovery`**: per-cell nav-mesh walk with NULL-parent + stale-pointer rejection.
- **`TesFormHeaderProbe`**: candidate-offset header probing unblocks MSTT/FLOR multi-inheritance reads.

#### New CLI / analysis commands

- `report validate` / `report consistency` — per-build field-domain sanity checks + cross-build agreement diffs (with `--from-html` to reuse a `dmp compare` output)
- `dmp analyze` / `dmp compare` / `dmp formtype-census` / `dmp scan-cell` — unified DMP analysis surface
- `dialogue player-lines` — per-quest player-line snapshots matching the GECK Topic browser
- `esm gameplay-audit` — cross-record gameplay-flag audit (ACBS UseTraits, QUST DATA, INFO Trespass/Aggro)
- `esm diagnose-scripts` / `esm script-provenance` — SCDA endian + SCHR layout + SCRO remap diagnostics
- EsmAnalyzer additions: `cell-textures`, `ltex-audit`, `navm-coverage`

#### Infrastructure

- **GitHub release pipeline**: `.github/workflows/build.yml` builds and publishes Windows GUI, Windows CLI, Linux CLI, and Audio Transcriber artifacts on `v*` tag push; alpha/beta/rc/pre tags auto-mark as prerelease.
- **Centralized package versions**: `Directory.Packages.props` (introduced in this cycle) is the single source of truth for NuGet versions.
- **Test discipline framework**: `SyntheticStructFactory` + `RuntimeReaderTestFixture` + `BucketBTestGuard` + offset-reader test helpers under `tests/.../Helpers/`. Snippet-based legacy harness retired.

### Changed

- **GLB exporter generalized**: `NpcGlbWriter` → `GlbWriter`; `NpcExportMeshPart`/`Node`/`NodeKind`/`Scene`/`SkinBinding` → `Glb*`. The NPC scene assemblers now emit through the same writer used by `MeshGlbExporter` and `TerrainGlbExporter`.
- **WindowsAppSDK 1.8 → 2.1.3** + WinUI ecosystem upgrade.
- **Dependency bumps**: Spectre.Console, System.CommandLine, Magick.NET, NAudio, SonarAnalyzer.CSharp (10.4 → 10.27), Roslynator (4.12.10 → 4.15.0), DDXConv submodule.
- **Plugin/ tree reorganized** into themed subfolders (`Output/`, `Pipeline/`, `Writers/Encoders/<RecordCategory>/`, `Nav/`, `AssetPacking/`, etc.).
- **Cross-dump comparison pipeline** rewritten in 10 phases as a projection-based streaming pipeline; old `CrossDumpComparisonPipeline` deleted.
- **`SchemaModelSerializer`** + **`SubrecordSchemaView`** replace the older `SubrecordDataReader.ReadFields` / `HasSchema` pattern (~45 encoder subrecords migrated; parser side migrated 16 handlers).
- **`SubrecordSchemaRegistry`**: opaque `ByteArray` schema fields replaced with named `UInt8` sequences for diff-friendliness.
- **Plugin builder v23**: full encoder coverage + validation + merge + nav-mesh emission. Versioned `v8` … `v55` smoke milestones rolled up into the planner pipeline.

### Fixed

- **SCDA bytecode endian** — script bytecode emitted to PC ESPs was the source's Xbox 360 big-endian bytes unswapped, so every converted quest's scripts dead-loaded. Decimal 7424 in 23K log errors = byte-reversed `ScriptName` opcode 0x001D. Fixed via `ScriptBytecodeEndianConverter` that reuses the decompiler as a structural walker.
- **DIAL QSTI remap missing** — `DialEncoder.EncodeNew` wrote QSTI verbatim; new DIALs referenced proto QUST FormIDs causing the engine to filter all their topics out (player only saw GOODBYE for Ulysses). Fixed by `SanitizeDialReferences` (mirrors `SanitizeInfoReferences`).
- **SCHR canonical layout** — `InfoEncoder` + `ScptEncoder` were emitting SCHR with the runtime `SCRIPT_HEADER` PDB layout, which diverges from the canonical fopdoc 20-byte layout at offsets 0, 12, and 16-19. Result: thousands of `SCRIPTS: Variable ID NNNNNNNN not found` errors. Now emit canonical layout (Padding/RefCount/CompiledSize/VariableCount/Type/Flags).
- **CellEncoder empty EDID** — empty EDID subrecord was emitted on every cell override; xEdit's `wbCELL` definition requires EDID to be optional and first-when-present. Now skipped when null/empty.
- **CellMerger binary policy** — persistent-only DMP captures merge into master, anything with non-persistent content authoritatively replaces it (no threshold mode).
- **QUST DATA override removal** — the runtime byte co-locates ESM-authored flags (StartGameEnabled, Allow*) with engine state bits (Started, Active, Completed). DMP-captured DATA either wiped master flags (Doc Mitchell's intro never starting) or carried runtime state (Sunny Smiles' quest appearing pre-started).
- **NPC actor merge policy** — retain only FormID identity fields (RNAM, SCRI, ZNAM, CNAM, ENAM, VTCK, HNAM, PNAM); discard everything else from DMP captures of master NPCs.
- **CREA encoding gap** — proto-only creatures (Speedy, Sleepy) were emitted as stubs because the encoder only modeled 9 of ~30 subrecords; expanded to OBND/PNAM/TNAM/BNAM/WNAM/NAM4/NAM5/VTCK/ZNAM/TPLT/CNTO/CSCR/CSDT*, plus the runtime reader and ESM parser extensions required to feed them.
- **MSTT / FLOR multi-inheritance** — `BGSMovableStatic` puts `TESFullName` + `BGSDestructibleObjectForm` BEFORE `TESForm`, so `EsmEditorIdExtractor` was reading garbage `cFormType`/`iFormID`. Fixed via `TesFormHeaderProbe` (candidate offsets `{+4/+12, +24/+32, +16/+24}`).
- **INFO CTDA Reference sanitizer** — sanitize CTDA `Reference` field on new INFOs to prevent the "crucify animation broadcast on every NPC" runtime symptom.
- **XCNT on ACHR** — placed-actor records carried `XCNT` from the DMP's live instance counter, causing the engine to append "(N)" to display names (e.g. "Ulysses (20770)"). Bethesda overloaded XCNT — stack count on REFR, runtime spawn counter on ACHR. RefrEncoder now emits XCNT only when `RecordType == "REFR"`.
- **NIF embedded asset gap** — asset packer's DMP scanner required null terminators; NIF `SizedString` texture paths slipped past it. `NifEmbeddedAssetCollector` pre-pass closes the gap. Was why Ulysses outfit + hair shipped without their textures.
- **NiAGDDataBlock.Data swap** — `Data` declared as `byte[][]` but holds packed 4-byte floats; the converter walked byte-by-byte and skipped the swap. Parity sweep across 14,854 Xbox/PC NIF pairs surfaced 2,282 affected LOD meshes; fix collapses that to 0.
- **BSPartFlag endian** — Xbox 360 stores `BSPartFlag` as a native byte pair, not a byte-swapped ushort. `BytePackedBitflagTypes` opt-out keeps the bytes verbatim.
- **Quest script brute-force scan** — `RuntimeQuestTerminalReader.ReadRuntimeQuest` was picking arbitrary `Script*` pointers from TESQuest memory when `pFormScript` was null. Caused Doc Mitchell "Finished" regression (CGTutorialSCRIPT was the wrong bind). Same antipattern in `RuntimeActorReader.BruteForceScanForScriptPointer` — removed entirely.
- **XCLW no-water sentinel** — `0x7F7FFFFF` (float.MaxValue) in XCLW / DNAM means "no water in this cell." `WorldHeightNormalizer` was coercing it to 0, flooding test/proto worldspaces. Now preserved through parse + render.

### Removed

- **OBJ mesh / terrain exporters** — `MeshObjExporter` + `TerrainObjExporter` replaced by `MeshGlbExporter` + `TerrainGlbExporter`. Anyone consuming OBJ output via the CLI needs to switch to GLB.
- **`NpcExport{MeshPart,Node,NodeKind,Scene,SkinBinding}.cs`** — renamed to `Glb*` as part of the GLB writer generalization.
- **`CrossDumpComparisonPipeline.cs`** — collapsed to a projection-pipeline shim and then deleted.
- **Brute-force script-pointer scans** in `RuntimeActorReader` and `RuntimeQuestTerminalReader`.

### Refactored

- **Plugin/ root** reorganized into themed subfolders (`Output/`, `Pipeline/`, `Writers/Encoders/<RecordCategory>/`, `Nav/`, `AssetPacking/`).
- **Encoders/** mirrored to Models/Records subfolders.
- **Test suite consolidation** — dedupe fixtures, parameterize Fact clusters, rename for SUT clarity. Snippet-based legacy harness retired.
- **`PdbStructView`** + **`SubrecordSchemaView`** + **`SchemaModelSerializer`** introduced as the data-driven runtime / schema-driven encoder primitives that displace the older hand-coded layouts.

### Known limitations (alpha)

- DMP→ESP master-cell NAVM augmentation gated off by default (`PluginBuildOptions.EmitMasterCellNavmAugmentation`); some new NAVM cells require an extended NAVI override that the planner doesn't yet emit.
- WastelandNV-specific crash under investigation (other worldspaces render fine).
- v3 viewer's reference renderer is in alpha — texture / material support limited to diffuse, no shader effects.
- Treat the alpha as "use, file issues, expect rough edges" — many edge cases are still under investigation.

## [2.4.0] - 2026-04-10

### Added

- **GUI Accessibility**: every interactive control in the WinUI 3 app now has an accessible name for screen readers (`AutomationProperties.Name` / `LabeledBy` / `x:Uid`). Page and section headers expose `HeadingLevel` for structure navigation. Icon-only buttons have both tooltips and accessible names. Color-coded status text is paired with the underlying text value so information isn't conveyed by color alone. A new xunit ratchet test (`XamlAccessibilityRatchetTests`) fails the build if a new control lands without accessible metadata.
- **Keyboard Shortcuts**: declared via XAML `KeyboardAccelerator` so tooltips auto-decorate with the shortcut hint. NIF Viewer adds Ctrl+O (open folder/BSA), Ctrl+E (export GLB), Ctrl+R (render PNG); HexViewer search wires Ctrl+F / F3 / Shift+F3 to XAML accelerators. F1 opens a new "Keyboard shortcuts" dialog listing every shortcut, grouped by area.
- **Report Validation Commands**: `report validate` and `report consistency` for per-build field-domain sanity checks and cross-build agreement diffs. `--from-html` reuses an existing `dmp compare` output; pattern-engine classifies unknown fields so the rule table grows from real data
- **MapMarker Cross-Dump Comparison**: Map markers now appear in `dmp compare` output — `compare_mapmarker.html` (and JSON/CSV equivalents); shared markers with drift in content are flagged, platform-metadata differences (endianness, offset) are demoted to "expected drift"
- **Non-Persistent Object Reports**: `esm reports` now emits `non_persistent_objects.csv` + `non_persistent_object_report.txt` alongside the persistent versions, covering XESP-gated placements and runtime-discovered refs
- **`InstanceEditorID` Column on REFR CSVs**: Both persistent and non-persistent CSVs include the per-REFR editor ID (sourced from the REFR's own EDID subrecord or runtime `ExtraEditorID`) — matches what the GUI explorer displays
- **NIF Tools: Viewer Tab**: The NIF panel is now a tabbed container with Batch Convert (original functionality) plus a new Viewer sub-tab — folder/BSA picker, NIF file tree, block-type inspector, elevation/perspective PNG render, GLB export. The parent nav item is renamed from "NIF Converter" to "NIF Tools" and the "Converters" subsection is renamed to "File Tools" to cover BSA Extractor and Repacker (neither actually a converter)
- **Resizable Hex / Files Column**: Single File tab gains a CommunityToolkit `GridSplitter` between the hex viewer and the files table; columns can be resized by the user
- **Unified Semantic Loader**: Core/Semantic `SemanticFileLoader` auto-detects file type (ESM, DMP, ESP) and produces a shared `RecordCollection` for all CLI commands and GUI tabs
- **Shared Record Detail Presenter**: `RecordDetailPresenter` in Core/Presentation builds structured detail models for NPC, creature, weapon, armor, quest, package, dialogue, cell, and worldspace records
- **NPC Composition Planners**: `NpcCompositionPlanner` and `CreatureCompositionPlanner` centralize render/export planning; `NpcCompositionExportAdapter` replaces direct scene builder calls
- **Cross-Dump Comparison Pipeline**: `CrossDumpAggregator` + `CrossDumpJsonHtmlWriter` generate interactive HTML reports with compressed JSON, chunked pages, and field-level browser-side diff
- **Unified `dmp analyze` Command**: Replaces `dmp-diag`; scans persistent refs, map markers, and runtime structures across dump directories
- **CellUtils Helper**: `CellUtils.WorldToCell()` replaces inline `floor(x/4096)` calculations
- **Weapon Projectile Enrichment**: Semantic loader enriches weapons with ESM projectile physics data (172/260 weapons, 80 PROJ records)
- **Music Type Parsing**: MUSC records now included in specialized semantic parsing (+20 records)
- **Persistent-Ref Redistribution**: Hoisted to a top-level pass for clearer pipeline ordering
- **File Size Exemptions Doc**: `docs/file-size-exemptions.md` documenting intentional 500-line guideline exemptions

### Changed

- **CLI Commands Unified**: `search`, `stats`, `list`, `show`, `diff`, `compare`, `world`, `analyze` commands wired onto the semantic loader for format-agnostic operation
- **GUI Tabs Refactored**: Extracted `LoadOrderDialogService`, `RecordDetailPropertyAdapter`, and property builders from `SingleFileTab` into standalone helpers
- **NPC Rendering**: Weapon resolution and mesh diagnostics integrated; render/export routed through composition plans
- **Runtime Readers Expanded**: Weapon sound probe, additional item/NPC field layouts, runtime worldspace maps exposed on `RecordCollection`
- **Cross-Dump HTML**: Replaced split-cell-pages approach with per-group chunked pages (single HTML with lazy-loaded compressed script tags)
- **ESM Parsing Reorganized**: Record parsing and runtime consumers restructured for clearer module boundaries
- **Show Renderers Split**: CLI show renderers split by record type (Actor, Item, Quest, Misc, Generic, Magic, WorldObject)

### Fixed

- **RACE ATTR/CNAM Endianness**: Xbox 360 stores these 2-byte RACE subrecords little-endian (not big-endian like other RACE fields); switched to pass-through instead of byte-swap, fixing attribute/color-index values after Xbox → PC conversion
- **CSV Display-Name Escaping**: `FormIdResolver.ResolveCsv` and `ResolveDisplayNameCsv` now actually CSV-escape returned strings; previously a display name containing a comma (e.g. "Tiny, Tiny Babies...") corrupted column alignment on every CSV writer
- **`dmp compare --format json` OOM**: `ReportJsonFormatter.WriteBatch` streams directly to the destination stream; previously materialized the full JSON as a single string and hit the .NET ~2 GB single-allocation cap on large record types (e.g., Cell across multiple builds)
- **Title Bar / Pane Interaction**: Redesigned `AppTitleBar` so the expanded NavigationView pane no longer claims the top strip; removed the `NavView_PaneOpened/Closed` margin-shift handlers
- **Build Errors**: Fixed incomplete namespace refactor (5 files), added `partial` to `TranscriptionJsonContext` for .NET 10 source gen compatibility
- **203 Analyzer Warnings Resolved**: Dead code removal (~450 lines), float equality fixes, null safety, nested ternary extraction, unused parameter cleanup, xUnit best practices
- **CS0675 Sign-Extended Bitwise-Or**: Fixed in `NpcBoundaryVertexStitcher` spatial hashing
- **CS8604 Nullable Warnings**: Fixed in `RecordDetailPresenter` TryFind pattern

### Removed

- Dead code from Phase C refactor: `GenerateSplitCellPages` + 7 helpers (225 lines), `BuildFullBodyScene`/`BuildHeadOnlyScene`/`LoadSkeletonContext` in `NpcExportSceneBuilder` (228 lines), `ApplyBodyEgtMorphs`, `ApplyHeadEgtMorphs`, `ResolveHairFilter`

### Refactored (500-line guideline)

- `PdbAnalyzer/Program.cs`: 1,213 → 557 LOC; extracted 7 command classes into `Commands/` + `PdbAnalyzerHelpers`
- `CellLinkageHandler`: 1,058 → 510 LOC; extracted `PersistentRefRedistributor` (578 LOC)
- `NpcFaceGenTextureVerifier`: 4,950 → 4,708 LOC; extracted `LinearAlgebraUtils` (187 LOC) + `MorphCorrectionHelpers` (160 LOC)

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
