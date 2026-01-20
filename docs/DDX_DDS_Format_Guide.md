# DDX and DDS Texture Format Guide

## Overview

This document explains the DDX (Xbox 360 texture) and DDS (DirectDraw Surface) texture formats, their relationship, and the challenges in converting between them. It is intended to help understand why certain DDX files (specifically 3XDR format) are difficult to convert.

---

## Table of Contents

1. [What Are Textures?](#what-are-textures)
2. [DDS Format (Target Format)](#dds-format-target-format)
3. [DDX Format (Xbox 360 Texture)](#ddx-format-xbox-360-texture)
4. [3XDO vs 3XDR: The Two DDX Variants](#3xdo-vs-3xdr-the-two-ddx-variants)
5. [Key Technical Concepts](#key-technical-concepts)
6. [Why 3XDR Conversion Fails](#why-3xdr-conversion-fails)
7. [Format Comparison Table](#format-comparison-table)
8. [References](#references)

---

## What Are Textures?

In video games, **textures** are 2D images applied to 3D surfaces to give them color, detail, and visual complexity. A brick wall, a character's face, or a weapon's metal surface all use textures.

### Key Texture Concepts

| Concept              | Description                                                        |
| -------------------- | ------------------------------------------------------------------ |
| **Resolution**       | The dimensions in pixels (e.g., 512x512, 1024x1024)                |
| **Mip Maps**         | Pre-calculated smaller versions of the texture for distant objects |
| **Compression**      | Algorithms to reduce file size while maintaining visual quality    |
| **Tiling/Swizzling** | How pixel data is arranged in memory for GPU efficiency            |

---

## DDS Format (Target Format)

**DDS (DirectDraw Surface)** is a Microsoft texture format widely used in PC games and by DirectX applications.

### DDS File Structure

```
┌─────────────────────────────────────────┐
│ Magic Number: "DDS " (0x20534444)       │  4 bytes
├─────────────────────────────────────────┤
│ DDS_HEADER (124 bytes)                  │
│   - Size, flags, dimensions             │
│   - Mip map count                       │
│   - Pixel format (DXT1/DXT3/DXT5/etc)   │
│   - Capability flags                    │
├─────────────────────────────────────────┤
│ Texture Data                            │
│   - Main surface (level 0)              │
│   - Mip level 1 (half size)             │
│   - Mip level 2 (quarter size)          │
│   - ... (continues to smallest mip)     │
└─────────────────────────────────────────┘
```

### DDS Pixel Formats (Compression)

DDS supports various compression formats. The most common are **DXT** (S3 Texture Compression):

| Format   | Also Known As | Block Size     | Description                                                        |
| -------- | ------------- | -------------- | ------------------------------------------------------------------ |
| **DXT1** | BC1           | 8 bytes/block  | 4:1 compression, 1-bit alpha. Best for opaque textures             |
| **DXT3** | BC2           | 16 bytes/block | 4:1 compression, explicit 4-bit alpha per pixel                    |
| **DXT5** | BC3           | 16 bytes/block | 4:1 compression, interpolated alpha. Best for gradual transparency |
| **ATI1** | BC4           | 8 bytes/block  | Single-channel (grayscale). Used for height/specular maps          |
| **ATI2** | BC5           | 16 bytes/block | Two-channel. Used for normal maps (XY directions)                  |

### How DXT Block Compression Works

DXT compresses textures by dividing them into **4x4 pixel blocks**:

```
┌────┬────┬────┬────┐
│    │    │    │    │  Each 4x4 block
│  4x4 Block (16 px)│  is compressed to
│    │    │    │    │  8 or 16 bytes
└────┴────┴────┴────┘

Original: 16 pixels × 4 bytes/pixel = 64 bytes
DXT1:     8 bytes (8:1 compression)
DXT5:     16 bytes (4:1 compression)
```

### DDS Data Layout (Linear)

DDS stores texture data **linearly** - blocks are arranged left-to-right, top-to-bottom:

```
Block Order in DDS (for 8x8 texture = 2x2 blocks):

┌───┬───┐
│ 0 │ 1 │    Memory: [Block0][Block1][Block2][Block3]
├───┼───┤
│ 2 │ 3 │    Simple sequential order
└───┴───┘
```

---

## DDX Format (Xbox 360 Texture)

**DDX** is Bethesda's Xbox 360 texture format used in games like Fallout: New Vegas. It wraps Xbox 360 GPU-ready texture data with a custom header.

### DDX File Structure

```
┌─────────────────────────────────────────┐
│ Magic Number: "3XDO" or "3XDR"          │  4 bytes (0x4F445833 or 0x52445833)
├─────────────────────────────────────────┤
│ Priority Bytes (Low, Common, High)      │  3 bytes - streaming priority
├─────────────────────────────────────────┤
│ Version (typically 3+)                  │  2 bytes
├─────────────────────────────────────────┤
│ D3DTexture Header (52 bytes)            │
│   - D3DResource fields                  │
│   - GPU Texture Fetch structure         │
│   - Format, dimensions, tiling info     │
├─────────────────────────────────────────┤
│ Compressed Texture Data                 │
│   - XMemCompress compressed chunks      │
│   - May be 1 or 2 chunks                │
└─────────────────────────────────────────┘
```

### Xbox 360 D3DTexture Header (from Xenia's xenos.h)

The texture header contains GPU-specific information packed into 6 DWORDs (24 bytes):

```
Format Structure (xe_gpu_texture_fetch_t):
  DWORD[0]: pitch, tiling mode, clamp modes
  DWORD[1]: format (bits 0-5), endianness, base_address
  DWORD[2]: size_2d { width:13, height:13, stack_depth:6 }
  DWORD[3-5]: swizzle, mip info, etc.

IMPORTANT: Xbox 360 is BIG-ENDIAN, so these must be byte-swapped!
```

### Dimension Encoding

Dimensions are stored as (size - 1) in 13 bits each:

```
DWORD[5] bits:
  Bits 0-12:  width - 1   (allows 1 to 8192 pixels)
  Bits 13-25: height - 1  (allows 1 to 8192 pixels)
  Bits 26-31: stack_depth (for texture arrays)
```

### DDX Compression

DDX files use **XMemCompress** (Xbox 360's LZX-based compression):

- Texture data is split into 1-2 compressed chunks
- Chunk 1: Mip atlas (smaller mip levels packed together)
- Chunk 2: Main surface (largest mip level)

The decompression is handled via Microsoft's XNA Framework or xcompress64.dll.

---

## 3XDO vs 3XDR: The Two DDX Variants

DDX files come in two variants, identified by their magic number:

| Variant  | Magic  | Hex Value  | Tiling Method                    | Support Status |
| -------- | ------ | ---------- | -------------------------------- | -------------- |
| **3XDO** | "3XDO" | 0x4F445833 | Standard Xbox 360 GPU tiling     | ✅ Supported   |
| **3XDR** | "3XDR" | 0x52445833 | Engine-specific alternate tiling | ❌ Not working |

### 3XDO: Standard GPU Tiling

3XDO uses the standard Xbox 360 GPU tiling algorithm (Morton order / Z-order curve):

```
Xbox 360 Morton Order Tiling:

Linear order:        Tiled order (Morton/Z-curve):
┌───┬───┬───┬───┐    ┌───┬───┬───┬───┐
│ 0 │ 1 │ 2 │ 3 │    │ 0 │ 1 │ 4 │ 5 │
├───┼───┼───┼───┤    ├───┼───┼───┼───┤
│ 4 │ 5 │ 6 │ 7 │ => │ 2 │ 3 │ 6 │ 7 │
├───┼───┼───┼───┤    ├───┼───┼───┼───┤
│ 8 │ 9 │10 │11 │    │ 8 │ 9 │12 │13 │
├───┼───┼───┼───┤    ├───┼───┼───┼───┤
│12 │13 │14 │15 │    │10 │11 │14 │15 │
└───┴───┴───┴───┘    └───┴───┴───┴───┘
```

The conversion process:

1. Decompress XMemCompress data
2. Apply inverse Morton tiling to "unswizzle" the data
3. Swap byte order (big-endian to little-endian)
4. Extract mips from the atlas
5. Write DDS header + linear data

### 3XDR: Engine-Specific Tiling

3XDR uses an **alternate tiling pattern** that was custom-implemented by Bethesda/Gamebryo. The code references this as coming from `NiXenonSourceTextureData::CreateFromDDXFile`.

The code contains extracted tiling tables from the engine:

```csharp
// Engine tiling tables extracted from the native CreateFromDDX implementation
private static readonly byte[,] _pFirstTilingRectsA = new byte[4, 3]
{
    { 0x00, 0x02, 0x03 }, { 0x12, 0x16, 0x18 },
    { 0x04, 0x0C, 0x10 }, { 0x00, 0x02, 0x03 }
};

private static readonly TextureTileRectDef[] _pTextureTileRectsA = [
    new() { cByteOffsetX = 0, cLineOffsetY = 0, cBytesWide = 128, cLinesHigh = 8 },
    new() { cByteOffsetX = 128, cLineOffsetY = 16, cBytesWide = 128, cLinesHigh = 8 },
    // ... more rectangle definitions
];
```

These tables define rectangular regions that get copied in a specific order, but **the exact algorithm hasn't been fully reverse-engineered**.

---

## Key Technical Concepts

### What is Tiling/Swizzling?

**Tiling** (also called swizzling) rearranges pixel data for optimal GPU memory access:

- **Linear layout**: Simple row-by-row order (good for CPUs, disk storage)
- **Tiled layout**: Optimized for GPU's 2D cache locality (good for rendering)

The Xbox 360 GPU performs best when textures are stored in Morton order because:

1. Adjacent pixels in 2D are also adjacent in memory
2. This matches the GPU's cache structure
3. Reduces memory bandwidth during texture sampling

### Mip Maps and Atlases

**Mip maps** are pre-calculated smaller versions of a texture:

```
Mip Chain for 256x256 texture:
┌──────────────────────┐
│      Level 0         │  256x256 (main surface)
│      256x256         │
└──────────────────────┘
┌──────────┐
│ Level 1  │  128x128
│ 128x128  │
└──────────┘
┌────┐
│ L2 │ 64x64
└────┘
┌──┐
│L3│ 32x32
└──┘
... down to 4x4 or 1x1
```

The **mip atlas** packs multiple mip levels into a single tiled texture:

```
Mip Atlas Layout (for 128x128 base texture in 256x256 atlas):

┌─────────────────┬─────────┐
│                 │         │
│   128x128       │  64x64  │ <- Mip 1
│   (base)        │         │
│                 ├────┬────┤
├────┬────┬───────┤32x32│16 │ <- Mips 2, 3, 4...
│ 32 │ 16 │ 8│4│2 │    │ 8 │
└────┴────┴───────┴────┴────┘
```

### Endianness

- **Xbox 360**: Big-endian (most significant byte first)
- **PC/x86**: Little-endian (least significant byte first)

Every 16-bit word in the texture data must be byte-swapped during conversion:

```
Xbox 360 (Big-Endian):    0xABCD stored as [AB][CD]
PC (Little-Endian):       0xABCD stored as [CD][AB]
```

---

## Why 3XDR Conversion Fails

Based on the code analysis, here are the identified issues with 3XDR conversion:

### 1. Unknown Tiling Algorithm

The code contains a comment admitting the limitation:

```csharp
// Attempt to reverse-engineer the engine's 3XDR tiling pattern,
// just gives different wrong output.
private static byte[] ApplyEngineTilingFor3xdr(...)
```

The `ApplyEngineTilingFor3xdr` function tries to use the extracted tiling tables, but the mapping between:

- Data format -> table row selection
- Table entries -> actual byte positions

...is not fully understood.

### 2. Table-Based vs Algorithm-Based

| 3XDO                                         | 3XDR                                        |
| -------------------------------------------- | ------------------------------------------- |
| Uses a mathematical algorithm (Morton order) | Uses lookup tables with rectangular regions |
| Can be computed for any texture size         | Tables may be size-specific                 |
| Well-documented (Xenia emulator)             | Proprietary Gamebryo implementation         |

### 3. Incomplete Table Data

The extracted tables may be:

- Missing entries for certain texture sizes
- Specific to certain texture formats
- Only partial (the full table set wasn't captured)

### 4. Format-to-Table Mapping Unknown

```csharp
private static int MapDataFormatToTmbRow(int dataFormatIndex)
{
    // Heuristic mapping: group formats into 4 rows
    // This is a GUESS, not reverse-engineered
    return dataFormatIndex switch
    {
        0x52 or 0x82 or 0x86 or 0x12 => 0,  // DXT1-like
        0x53 or 0x54 or 0x13 or 0x14 or 0x88 or 0x71 => 1,  // DXT5/DXT3-like
        0x7B => 2,  // BC4/BC5-like
        _ => 3  // fallback
    };
}
```

This mapping is a **heuristic guess**, not based on actual reverse engineering.

### 5. Potential Investigation Approaches

To fix 3XDR support, one would need to:

1. **Analyze the original engine code** (if available) to understand `NiXenonSourceTextureData::CreateFromDDXFile`
2. **Compare known inputs and outputs** - find 3XDR textures that also exist as other formats
3. **Study Gamebryo SDK documentation** (if accessible)
4. **Examine Xbox 360 GPU documentation** for alternate tiling modes
5. **Test with various texture sizes** to find patterns in the table usage

---

## Format Comparison Table

| Feature         | DDS (PC)        | DDX 3XDO           | DDX 3XDR           |
| --------------- | --------------- | ------------------ | ------------------ |
| **Magic**       | "DDS "          | "3XDO"             | "3XDR"             |
| **Endianness**  | Little          | Big                | Big                |
| **Data Layout** | Linear          | Morton-tiled       | Engine-tiled       |
| **Compression** | None (DXT only) | XMemCompress + DXT | XMemCompress + DXT |
| **Mip Storage** | Sequential      | Packed atlas       | Packed atlas       |
| **Conversion**  | N/A (target)    | ✅ Supported       | ❌ Unknown tiling  |

### Texture Format Codes

| Code | Format                  | Block Size | Usage                      |
| ---- | ----------------------- | ---------- | -------------------------- |
| 0x52 | DXT1                    | 8 bytes    | Diffuse textures           |
| 0x53 | DXT3                    | 16 bytes   | Textures with sharp alpha  |
| 0x54 | DXT5                    | 16 bytes   | Textures with smooth alpha |
| 0x71 | ATI2/BC5                | 16 bytes   | Normal maps (XY)           |
| 0x7B | ATI1/BC4                | 8 bytes    | Specular/height maps       |
| 0x82 | DXT1 variant            | 8 bytes    | Alternative DXT1 encoding  |
| 0x86 | DXT1 variant            | 8 bytes    | Alternative DXT1 encoding  |
| 0x88 | DXT5 variant            | 16 bytes   | Alternative DXT5 encoding  |
| 0x12 | GPUTEXTUREFORMAT_DXT1   | 8 bytes    | Xenos GPU format           |
| 0x13 | GPUTEXTUREFORMAT_DXT2/3 | 16 bytes   | Xenos GPU format           |
| 0x14 | GPUTEXTUREFORMAT_DXT4/5 | 16 bytes   | Xenos GPU format           |

---

## References

### Code Sources

- **Xenia Emulator**: Xbox 360 tiling algorithms
  - https://github.com/xenia-project/xenia/blob/master/src/xenia/gpu/texture_conversion.cc
- **XCompression**: Xbox 360 decompression library
  - https://github.com/gibbed/XCompression

### Format Documentation

- **DDS File Format**: Microsoft DirectX documentation
- **Xbox 360 Xenos GPU**: Technical documentation from Xbox development kits
- **Gamebryo Engine**: NiXenonSourceTextureData implementation (proprietary)

### Statistics (from July 2010 Fallout: New Vegas prototype)

- Total DDX files: 26,123
- Successfully converted (3XDO): 22,148 (~85%)
- Unsupported (3XDR): 3,961 (~15%)
- Other failures: 13 (very low resolution textures)

---

## Glossary

| Term             | Definition                                             |
| ---------------- | ------------------------------------------------------ |
| **Block**        | A 4x4 pixel group in DXT compression (8 or 16 bytes)   |
| **DXT**          | DirectX Texture Compression (S3TC)                     |
| **Endianness**   | Byte order in multi-byte values (big vs little)        |
| **FourCC**       | Four-character code identifying formats (e.g., "DXT1") |
| **GPU**          | Graphics Processing Unit                               |
| **Mip Map**      | Pre-calculated lower-resolution texture versions       |
| **Morton Order** | Z-curve pattern for 2D-to-1D mapping                   |
| **Swizzle**      | Rearranging data for GPU-optimal access patterns       |
| **Tiling**       | Memory layout optimization for GPU cache efficiency    |
| **XMemCompress** | Xbox 360's LZX-based compression                       |
