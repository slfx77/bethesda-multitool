# File Size Exemptions (500-Line Guideline)

Files over 500 lines that are intentionally left unsplit, with justification.

## Embedded Content / Generated Code

| File | LOC | Reason |
|---|---|---|
| `Core/Formats/Esm/Export/ComparisonJsRenderer.cs` | 1,746 | Single raw JS string literal (client-side renderer). Cannot be split into C# types. |
| `Core/Formats/Esm/Script/ScriptFunctionTable.Generated.cs` | 842 | Generated lookup table. |
| `Core/Formats/Esm/FaceGen/FaceGenGeometrySymmetricData.cs` | 864 | Hard-coded symmetry data tables extracted from the game engine. |

## Algorithm Cohesion

| File | LOC | Reason |
|---|---|---|
| `DDXConv/Compression/LzxDecompressor.cs` | 978 | LZX decompression algorithm — splitting would scatter tightly coupled state machine logic. |
| `Core/RuntimeBuffer/SecondPassOwnershipResolver.cs` | 1,000 | Chain-of-responsibility ownership resolver; 20+ `TryResolve*` methods share instance fields. |
| `Core/Formats/Nif/Rendering/NifScanlineRasterizer.cs` | 936 | Scanline rasterization loop with shared edge/span state. |
| `Core/Formats/Nif/Skinning/NifSkinPartitionExpander.cs` | 816 | Partition expansion algorithm with recursive subdivision. |
| `Core/Formats/Nif/Skinning/NifSkinPartitionParser.cs` | 758 | Binary format parser — sequential read logic. |
| `Core/Formats/Nif/Rendering/FaceGenTextureMorpher.cs` | 757 | EGT texture morphing pipeline — each step feeds the next. |
| `Core/Formats/Nif/Conversion/NifSchemaConverter.cs` | 843 | NIF endian conversion — field-by-field schema application. |
| `Core/Formats/Nif/Conversion/NifGeometryWriter.cs` | 777 | Geometry binary writer — sequential output. |

## Instance Classes with Shared State

| File | LOC | Reason |
|---|---|---|
| `Core/Formats/Esm/Runtime/Readers/RuntimeDialogueReader.cs` | 961 | Instance class; all methods share `_context` and layout offsets. |
| `Core/Formats/Esm/Runtime/Readers/RuntimeCellReader.cs` | 767 | Instance class; all methods share runtime memory context. |
| `Core/Formats/Esm/Runtime/Readers/Specialized/RuntimeNpcFieldReader.cs` | 798 | Instance class; shared PDB layout + memory context. |
| `Core/Formats/Esm/Runtime/Readers/Specialized/RuntimeMagicReader.cs` | 730 | Instance class; shared PDB layout + memory context. |

## CLI Pipeline Orchestrators

| File | LOC | Reason |
|---|---|---|
| `CLI/Rendering/Npc/NpcRenderPipeline.cs` | 955 | NPC render orchestrator — already delegates to composition planner. |
| `tools/EgtAnalyzer/Commands/VerifyEgtCommand.cs` | 1,230 | Single CLI command handler with pipeline, reporting, and CSV output tightly coupled to command options. |

## GUI Controls

| File | LOC | Reason |
|---|---|---|
| `App/Controls/WorldMapControl.xaml.cs` | 912 | WinUI control code-behind with input handling, rendering, and state. |
| `App/Tabs/SingleFileTab.NpcBrowser.cs` | 900 | Tab code-behind with NPC browsing, rendering triggers, and UI state. |

## DDXConv Submodule (deferred)

| File | LOC | Reason |
|---|---|---|
| `DDXConv/DdxChunkProcessor.cs` | 1,163 | In DDXConv submodule; deferred to submodule release. |
| `DDXConv/DdxMipAtlasUnpacker.cs` | 767 | In DDXConv submodule; deferred to submodule release. |

## Parser / Handler Files (500-850 range)

These files are in the 500-850 range and follow the pattern of a single record type handler or format parser. Splitting them would create artificial boundaries within cohesive parsing logic.

| File | LOC | Pattern |
|---|---|---|
| `Parsing/Handlers/WeaponRecordHandler.cs` | 831 | Single record type handler |
| `Parsing/Handlers/DialogueConditionParser.cs` | 939 | Condition parser with function dispatch |
| `Parsing/Handlers/ActorRecordHandler.cs` | 711 | Single record type handler |
| `Parsing/RecordParserContext.cs` | 762 | Parser state + subrecord dispatch |
| `Parsing/RecordParser.cs` | 719 | Top-level parser |
| `Rendering/TriParser.cs` | 745 | Triangle strip/list parser |
| `NifFormat.cs` | 712 | NIF format definitions |
