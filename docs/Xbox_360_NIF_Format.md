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
| 8      | 8    | half4  | **Bone Weights** | Sum ≈ 1.0         | 4 weights (sum to 1.0)    |
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

| Stride | Offset 8 Content | Avg Length  |
| ------ | ---------------- | ----------- |
| 36     | **Normals**      | ~1.0 (unit) |
| 40     | **Normals**      | ~1.0 (unit) |
| 48     | **Bone Weights** | Sum ≈ 1.0   |

> **For skinned meshes (stride 48)**: Offset 8 contains bone weights as half4, where the 4 values sum to approximately 1.0 (weight normalization). Normals are at offset 20.
>
> **For non-skinned meshes (stride 36/40)**: Offset 8 contains actual unit-length normals.

---

## Resolved: Bone Weights and Indices

### Bone Weights at Offset 8 (stride 48)

**Status**: ✅ RESOLVED

The half4 at offset 8 in stride 48 meshes contains **bone weights**, not unknown data:

- **Location**: Offset 8 in packed geometry (stride 48 meshes only)
- **Format**: 4× half-precision floats
- **Values**: Sum to approximately 1.0 (normalized blend weights)
- **Previous confusion**: Average vector length ~0.82-0.90 seemed anomalous because we were computing magnitude of a weight vector, not checking for unit-length

**Discovery**: The key was recognizing that bone weights are _not_ unit-length vectors - they are blend weights that sum to 1.0, so treating them as vectors and computing magnitude gives values around 0.5-1.0 depending on weight distribution.

### Bone Indices at Offset 16

**Status**: ✅ RESOLVED

- **Location**: Offset 16 in packed geometry (stride 48 meshes only)
- **Format**: ubyte4 (4× unsigned bytes)
- **Values**: Indices into the bone palette (0-255)

### Complete Stride 48 Layout (Skinned Meshes)

| Offset | Size | Type   | Semantic     | Notes                  |
| ------ | ---- | ------ | ------------ | ---------------------- |
| 0      | 8    | half4  | Position     | XYZ + W=1              |
| 8      | 8    | half4  | Bone Weights | 4 weights, sum ≈ 1.0   |
| 16     | 4    | ubyte4 | Bone Indices | 4 bone indices (0-255) |
| 20     | 8    | half4  | Normal       | Unit-length            |
| 28     | 4    | half2  | UV           | Texture coordinates    |
| 32     | 8    | half4  | Tangent      | Unit-length            |
| 40     | 8    | half4  | Bitangent    | Unit-length            |

### Additional Streams

Some meshes may have additional streams beyond the standard layouts:

- Vertex colors (stride 40 meshes)
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

| Feature                   | Status | Notes                                                |
| ------------------------- | ------ | ---------------------------------------------------- |
| Endian conversion         | ✅     | Schema-driven, all fields converted                  |
| Position extraction       | ✅     | half4 → float3                                       |
| Normal extraction         | ✅     | Stride-aware offsets (8 for stride 36/40, 20 for 48) |
| Tangent extraction        | ✅     | Stride-aware offsets                                 |
| Bitangent computation     | ✅     | Computed as cross(N,T) when not in packed            |
| UV extraction             | ✅     | half2 → float2                                       |
| Triangle extraction       | ✅     | Strips converted to triangles                        |
| Block stripping           | ✅     | Xbox-specific blocks removed                         |
| Reference remapping       | ✅     | Ref<T> indices updated                               |
| Rendering in NifSkope     | ✅     | Solid mode verified                                  |
| Non-skinned meshes        | ✅     | Stride 36 and 40 formats fully supported             |
| Vertex colors             | ✅     | Extracted from stride 40 meshes                      |
| Havok collision rendering | ✅     | HavokFilter Layer field correctly converted          |
| Bone weights extraction   | ✅     | Extracted from offset 8 in stride 48 meshes          |
| Bone indices extraction   | ✅     | Extracted from offset 16 in stride 48 meshes         |
| NiSkinPartition expansion | ✅     | HasVertexWeights=1, HasBoneIndices=1 written         |
| Skinned mesh detection    | ✅     | Based on stride == 48                                |

### ❌ Not Yet Implemented

| Feature       | Status | Notes                            |
| ------------- | ------ | -------------------------------- |
| Havok physics | ❌     | hkPackedNiTriStripsData stripped |

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

| Date       | Change                                                     |
| ---------- | ---------------------------------------------------------- |
| 2026-01-11 | Initial document creation                                  |
| 2026-01-11 | Documented offset 8 as unknown (not normals)               |
| 2026-01-11 | Verified normal location at offset 20                      |
| 2026-01-11 | Documented bone weight uncertainty                         |
| 2026-01-12 | RESOLVED: Offset 8 = bone weights, NiSkinPartition working |

---

## Contributing

When investigating unknown data:

1. **Document observations** - What does the data look like?
2. **Note patterns** - Does it vary by mesh type?
3. **Compare with PC** - Is there a corresponding PC value?
4. **Test hypotheses** - Try interpreting the data different ways
5. **Update this document** - Record findings even if inconclusive

Do not dismiss unknown data as "garbage" - it likely serves a purpose we haven't yet discovered.
