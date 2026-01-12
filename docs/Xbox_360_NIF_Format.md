# Xbox 360 NIF Format Research

This document captures our current understanding of the Xbox 360 NIF (NetImmerse/Gamebryo) format as used in Fallout 3/New Vegas, and how it differs from the PC format.

> **Status**: This format is **partially understood**. Geometry conversion works for static rendering, but skeletal animation and physics are not yet fully implemented.

---

## Table of Contents

1. [Overview](#overview)
2. [Key Differences from PC](#key-differences-from-pc)
3. [BSPackedAdditionalGeometryData](#bspackedadditionalgeometrydata)
4. [Stream Layout Analysis](#stream-layout-analysis)
5. [Unknown/Unexplored Data](#unknownunexplored-data)
6. [NiSkinPartition Differences](#niskinpartition-differences)
7. [Havok Physics Data](#havok-physics-data)
8. [Conversion Status](#conversion-status)
9. [Research Methodology](#research-methodology)
10. [References](#references)

---

## Overview

Xbox 360 NIFs use the same basic NetImmerse/Gamebryo format as PC NIFs but with several platform-specific optimizations:

| Property           | Xbox 360                 | PC                        |
| ------------------ | ------------------------ | ------------------------- |
| Byte Order         | Big-endian               | Little-endian             |
| NIF Version        | 20.2.0.7                 | 20.2.0.7                  |
| User Version       | 11                       | 11                        |
| BS Version         | 34                       | 34                        |
| Geometry Storage   | Packed in separate block | Inline in geometry blocks |
| Half-float support | Extensive (half4/half2)  | Full floats only          |

### File Structure

```
[NIF Header]
  - Version string: "Gamebryo File Format, Version 20.2.0.7"
  - Endian flag (1 = big-endian for Xbox 360)
  - Block type list
  - Block size list

[Block Data]
  - Same block types as PC, but with Xbox-specific additions
  - BSPackedAdditionalGeometryData (Xbox 360 only)
  - hkPackedNiTriStripsData (Havok physics, Xbox-specific format)

[NIF Footer]
  - Root nodes list
```

---

## Key Differences from PC

### 1. Endianness

All multi-byte values are big-endian on Xbox 360:

- Integers (uint16, uint32, int32)
- Floats (IEEE 754, byte-swapped)
- Block references (Ref<T>)

### 2. Geometry Storage

PC stores geometry inline in `NiTriShapeData`/`NiTriStripsData` blocks. Xbox 360 stores geometry in a separate `BSPackedAdditionalGeometryData` block using half-precision floats.

### 3. Block References

The `NiTriShapeData` block on Xbox 360 has an `Additional Data` reference (Ref<BSPackedAdditionalGeometryData>) that points to the packed geometry. PC files have this reference set to -1.

### 4. Triangle Storage

- **PC**: Triangles stored in `NiTriShapeData` as triangle indices, or in `NiTriStripsData` as triangle strips
- **Xbox 360**: Triangles stored in `NiSkinPartition` as triangle strips (for skinned meshes), with indices requiring vertex map remapping

---

## BSPackedAdditionalGeometryData

This Xbox 360-specific block contains all vertex data in packed half-float format.

### Block Header

```
NumVertices:    ushort    - Number of vertices
NumBlockInfos:  uint32    - Number of data streams
[Stream Info × NumBlockInfos]
NumDataBlocks:  uint32    - Number of data blocks (usually 1)
[Data Block Info]
[Raw Vertex Data]
```

### Stream Info Structure (25 bytes each)

```
Type:        uint32    - Data type identifier
UnitSize:    uint32    - Size per element in bytes
TotalSize:   uint32    - Total size of stream
Stride:      uint32    - Bytes between vertices (typically 48)
BlockIndex:  uint32    - Which data block contains this stream
BlockOffset: uint32    - Offset within vertex stride
Flags:       byte      - Stream flags
```

### Data Type Identifiers

| Type | UnitSize | Meaning                         |
| ---- | -------- | ------------------------------- |
| 16   | 8        | half4 (4× half-floats, 8 bytes) |
| 14   | 4        | half2 (2× half-floats, 4 bytes) |
| 28   | 4        | ubyte4 (4× bytes)               |

---

## Stream Layout Analysis

The packed vertex data uses a variable stride depending on mesh type. Three stride formats have been identified:

### Common Layout (stride 36 bytes - non-skinned, no vertex colors)

| Offset | Size | Type  | Semantic      | Avg Length        | Notes                  |
| ------ | ---- | ----- | ------------- | ----------------- | ---------------------- |
| 0      | 8    | half4 | **Position**  | ~40 (model-scale) | XYZ + W=1              |
| 8      | 8    | half4 | **Normal**    | ~1.0 (unit)       | Unit-length normals    |
| 16     | 4    | half2 | **UV**        | N/A               | Texture coordinates    |
| 20     | 8    | half4 | **Tangent**   | ~1.0 (unit)       | Unit-length tangents   |
| 28     | 8    | half4 | **Bitangent** | ~1.0 (unit)       | Unit-length bitangents |

### Vertex Color Layout (stride 40 bytes - non-skinned, with vertex colors)

| Offset | Size | Type   | Semantic          | Avg Length        | Notes                  |
| ------ | ---- | ------ | ----------------- | ----------------- | ---------------------- |
| 0      | 8    | half4  | **Position**      | ~40 (model-scale) | XYZ + W=1              |
| 8      | 8    | half4  | **Normal**        | ~1.0 (unit)       | Unit-length normals    |
| 16     | 4    | ubyte4 | **Vertex Colors** | N/A               | RGBA vertex colors     |
| 20     | 4    | half2  | **UV**            | N/A               | Texture coordinates    |
| 24     | 8    | half4  | **Tangent**       | ~1.0 (unit)       | Unit-length tangents   |
| 32     | 8    | half4  | **Bitangent**     | ~1.0 (unit)       | Unit-length bitangents |

**Important**: This layout has normals at offset 8 (not offset 20 like other layouts). The converter detects this by checking if stride == 40 and uses the correct offsets.

### Skinned Layout (stride 48 bytes - skinned meshes)

| Offset | Size | Type   | Semantic         | Avg Length        | Notes                     |
| ------ | ---- | ------ | ---------------- | ----------------- | ------------------------- |
| 0      | 8    | half4  | **Position**     | ~40 (model-scale) | XYZ + W=1                 |
| 8      | 8    | half4  | **Unknown**      | ~0.82-0.90        | Purpose unknown           |
| 16     | 4    | ubyte4 | **Bone Indices** | N/A               | 4 bone indices per vertex |
| 20     | 8    | half4  | **Normal**       | ~1.0 (unit)       | Unit-length normals       |
| 28     | 4    | half2  | **UV**           | N/A               | Texture coordinates       |
| 32     | 8    | half4  | **Tangent**      | ~1.0 (unit)       | Unit-length tangents      |
| 40     | 8    | half4  | **Bitangent**    | ~1.0 (unit)       | Unit-length bitangents    |

### Stride-Based Detection

The converter uses **stride value alone** to determine mesh type:

| Stride | Mesh Type                       | ubyte4 at offset 16 |
| ------ | ------------------------------- | ------------------- |
| 36     | Non-skinned, no vertex colors   | Not present         |
| 40     | Non-skinned, with vertex colors | **Vertex Colors**   |
| 48     | Skinned                         | **Bone Indices**    |

> **Key Discovery**: Both stride 40 and stride 48 have `ubyte4` at offset 16, but they contain different data:
>
> - Stride 40: Vertex colors (RGBA)
> - Stride 48: Bone indices
>
> Using "ubyte4 presence" to detect skinned meshes caused false positives. Stride value is the reliable indicator.

### Detection Method

Unit-length vectors (normals, tangents, bitangents) are identified by computing the average vector length across sampled vertices:

- **Unit-length**: avg length ≈ 1.0 (within 0.9-1.1 tolerance)
- **Position**: avg length varies with model scale (typically 10-100+)
- **Skinned offset 8**: avg length ~0.82-0.90 (NOT unit-length, purpose unknown)

### Bitangent Computation

When the packed data has only 2 unit-length streams (normals and tangents), bitangents are computed during conversion:

```
Bitangent = cross(Normal, Tangent)
```

This produces correct results for meshes that store tangent space implicitly.

### Important Discovery: Skinned vs Non-Skinned Offset 8

The meaning of offset 8 depends on the stride:

| Stride | Offset 8 Content      | Avg Length  |
| ------ | --------------------- | ----------- |
| 36     | **Normals**           | ~1.0 (unit) |
| 40     | **Normals**           | ~1.0 (unit) |
| 48     | **Unknown/Auxiliary** | ~0.82-0.90  |

> **For skinned meshes (stride 48)**: Stream headers may label offset 8 as "Normal" based on stream order, but actual analysis shows avg vector length ~0.82-0.90 (NOT unit-length). The actual normals are at offset 20.
>
> **For non-skinned meshes (stride 36/40)**: Offset 8 contains actual unit-length normals.

---

## Unknown/Unexplored Data

### Offset 8 in Skinned Meshes (stride 48)

**Status**: Purpose unknown

**Observations**:

- Only applies to skinned meshes (stride 48)
- Stored as half4 (4× half-precision floats)
- Average vector length: 0.82-0.90 (NOT unit-length)
- Stream headers sometimes label this as "Normal" but it's not
- Values appear to be in a consistent range across vertices
- Could be: compressed normals (different encoding), blend shapes, secondary UV, or other auxiliary data

**Investigation needed**:

- Compare with known compressed normal formats (Oct16, Spheremap, etc.)
- Check if values correlate with any mesh properties
- Analyze across multiple mesh types (creatures, architecture, props)

### Bone Weights Location

**Status**: Uncertain

**Observations**:

- PC files store bone weights in `NiSkinPartition` blocks
- Xbox 360 packed geometry has bone indices at offset 16 (ubyte4)
- Offset 40 was suspected to contain bone weights, but analysis shows it's unit-length bitangent data
- Bone weights may be entirely in `NiSkinPartition`, not duplicated in packed geometry

**Investigation needed**:

- Parse Xbox 360 `NiSkinPartition` for bone weight data
- Compare weight values between PC and Xbox 360
- Determine if conversion needs to extract weights from different location

### Additional Streams

Some meshes may have additional streams beyond the standard 48-byte layout:

- Vertex colors (when present)
- Secondary UV sets
- Custom shader data

---

## NiSkinPartition Differences

`NiSkinPartition` blocks handle bone influences for skeletal meshes.

### PC Format

- Per-vertex bone weights stored inline
- Per-vertex bone indices stored inline
- `HasVertexWeights` and `HasBoneIndices` flags indicate presence

### Xbox 360 Format

- Triangle data stored as **triangle strips** (not explicit triangles)
- `NumStrips > 0` and `StripLengths` array define strip structure
- Bone weights/indices may be stored differently (investigation needed)

### Triangle Extraction

Xbox 360 triangle strips must be converted to explicit triangles:

```csharp
// Convert strip [0, 1, 2, 3, 4] to triangles:
// Triangle 0: [0, 1, 2]
// Triangle 1: [2, 1, 3]  (winding order flipped)
// Triangle 2: [2, 3, 4]
// Triangle 3: [4, 3, 5]  (winding order flipped)
// ... alternating winding for each subsequent triangle
```

---

## Havok Physics Data

### hkPackedNiTriStripsData

Xbox 360-specific Havok collision data block.

**Status**: Not yet implemented (currently stripped during conversion)

**Observations**:

- Contains collision geometry for physics simulation
- Different format from PC Havok data
- May reference packed geometry data

**Impact**: Converted models will not have collision detection until this is implemented.

---

## Conversion Status

### ✅ Fully Working

| Feature                   | Status | Notes                                                  |
| ------------------------- | ------ | ------------------------------------------------------ |
| Endian conversion         | ✅     | Schema-driven, all fields converted                    |
| Position extraction       | ✅     | half4 → float3                                         |
| Normal extraction         | ✅     | Stride-aware offsets (8 for stride 36/40, 20 for 48)   |
| Tangent extraction        | ✅     | Stride-aware offsets                                   |
| Bitangent computation     | ✅     | Computed as cross(N,T) when not in packed              |
| UV extraction             | ✅     | half2 → float2                                         |
| Triangle extraction       | ✅     | Strips converted to triangles                          |
| Block stripping           | ✅     | Xbox-specific blocks removed                           |
| Reference remapping       | ✅     | Ref<T> indices updated                                 |
| Rendering in NifSkope     | ✅     | Solid mode verified                                    |
| Non-skinned meshes        | ✅     | Stride 36 and 40 formats fully supported               |
| Vertex colors             | ✅     | Extracted from stride 40 meshes                        |
| Havok collision rendering | ✅     | HavokFilter Layer field correctly converted            |

### ⚠️ Partially Working

| Feature                | Status | Notes                                        |
| ---------------------- | ------ | -------------------------------------------- |
| Bone indices           | ⚠️     | Extracted from offset 16, may need remapping |
| Skinned mesh detection | ⚠️     | Based on stride == 48                        |

### ❌ Not Yet Implemented

| Feature                   | Status | Notes                                         |
| ------------------------- | ------ | --------------------------------------------- |
| Bone weights              | ❌     | Location uncertain, may be in NiSkinPartition |
| NiSkinPartition expansion | ❌     | Weights/indices not written to partition      |
| Skeletal animation        | ❌     | Requires bone weights/indices                 |
| Havok physics             | ❌     | hkPackedNiTriStripsData stripped              |
| Offset 8 data             | ❌     | Unknown purpose, currently discarded          |

---

## Research Methodology

### Tools Used

1. **NifAnalyzer** (`tools/NifAnalyzer/`)

   - Block listing and comparison
   - Stream analysis with semantic detection
   - Vertex data extraction and comparison
   - Normal/tangent/bitangent verification

2. **NifSkope** (external)
   - Visual verification of converted models
   - Reference for expected rendering results

### Verification Process

1. **Extract Xbox 360 geometry** using NifAnalyzer
2. **Compare vertex data** with PC reference file
3. **Calculate vector lengths** to determine semantics
4. **Visual verification** in NifSkope after conversion

### Example Commands

```bash
# Analyze packed geometry streams
dotnet run --project tools/NifAnalyzer -f net10.0 -- analyzestreams xbox.nif <block_index>

# Compare Xbox vs PC normals
dotnet run --project tools/NifAnalyzer -f net10.0 -- normalcompare xbox.nif pc.nif <xbox_block> <pc_block>

# Dump raw stream data
dotnet run --project tools/NifAnalyzer -f net10.0 -- streamdump xbox.nif <block_index>

# Compare converted vs PC file
dotnet run --project tools/NifAnalyzer -f net10.0 -- compare converted.nif reference.nif
```

---

## References

### Sample Files

| File            | Location                   | Purpose                       |
| --------------- | -------------------------- | ----------------------------- |
| Xbox 360 meshes | `Sample/meshes_360_final/` | Input files for conversion    |
| PC reference    | `Sample/meshes_pc/`        | Ground truth for verification |
| Test output     | `TestOutput/converted/`    | Conversion results            |

### Related Documentation

- [NifSkope nif.xml](https://github.com/niftools/nifxml) - NIF format schema
- [UESP NIF Format](https://en.uesp.net/wiki/Skyrim_Mod:Mod_File_Format/NIF) - Community documentation
- [Architecture.md](Architecture.md) - Project architecture overview
- [copilot-instructions.md](../.github/copilot-instructions.md) - Project guidelines

### Version History

| Date       | Change                                       |
| ---------- | -------------------------------------------- |
| 2026-01-11 | Initial document creation                    |
| 2026-01-11 | Documented offset 8 as unknown (not normals) |
| 2026-01-11 | Verified normal location at offset 20        |
| 2026-01-11 | Documented bone weight uncertainty           |

---

## Contributing

When investigating unknown data:

1. **Document observations** - What does the data look like?
2. **Note patterns** - Does it vary by mesh type?
3. **Compare with PC** - Is there a corresponding PC value?
4. **Test hypotheses** - Try interpreting the data different ways
5. **Update this document** - Record findings even if inconclusive

Do not dismiss unknown data as "garbage" - it likely serves a purpose we haven't yet discovered.
