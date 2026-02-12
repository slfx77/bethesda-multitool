# Xbox 360 DDX Texture Format

Reference document for the DDX (DirectDraw Xbox) texture format used by Xbox 360 Fallout 3 and Fallout: New Vegas. Compiled from PDB symbol analysis (`Fallout_Debug`), DDXConv source code, Xenia GPU documentation, and empirical file analysis.

## Overview

DDX is the Xbox 360 counterpart to PC's DDS (DirectDraw Surface) format. Where PC games store textures as `.dds` files, the Xbox 360 versions store them as `.ddx` files within BSA archives. The format encapsulates GPU-ready texture data with Xbox 360-specific tiling, big-endian byte ordering, and LZX compression.

Key differences from DDS:

- **Magic**: `3XDO` (0x4F445833) or `3XDR` (0x52445833) instead of `DDS ` (0x20534444)
- **Byte order**: Big-endian (PowerPC) vs. little-endian (x86)
- **Tiling**: Morton/Z-order swizzling for GPU cache coherence (not linear row-major)
- **Compression**: XMemCompress (LZX) on the texture data, in addition to DXT block compression
- **MIP storage**: Packed mip atlases instead of sequential mip chain
- **Normal maps**: Stored as BC5 (2-channel) + separate BC4 (specular), merged to DXT5/BC3 for PC

## File Structure

### 3XDO Format (Standard)

This is the primary DDX format for textures stored in BSA archives.

```
Offset  Size  Field
------  ----  -----
0x00    4     Magic: "3XDO" (0x4F445833)
0x04    1     Priority Low byte
0x05    1     Priority Critical byte
0x06    1     Priority High byte
0x07    2     Version (uint16, LE) — must be >= 3
0x08    52    D3DTexture GPU Header (Xbox 360 hardware format)
0x3C    8     Padding / reserved
0x44    var   XMemCompress-compressed texture data (one or more LZX chunks)
```

### 3XDR Format (Engine-Tiled)

A simpler variant that uses 2×2 macro-block tiling instead of full Morton swizzling. Contains only mip level 0 (no mip atlas).

```
Same header layout as 3XDO through offset 0x44.
Data is NOT Morton-swizzled but uses 2×2 macro-block tiling.
Contains only the base mip level.
```

### Priority Bytes

Three priority bytes (Low, Critical, High) control texture streaming priority. Their exact semantics are determined by the engine's texture manager — higher values generally indicate textures that should be loaded earlier or retained longer in memory.

## D3DTexture GPU Header

The 52-byte structure at offset `0x08` is the Xbox 360 GPU texture fetch constant (`GPUTEXTURE_FETCH_CONSTANT` / `xe_gpu_texture_fetch_t`). This is the exact structure the GPU reads to configure texture sampling hardware.

### Structure Layout

From PDB: `NiXenonSourceTextureData` inherits from `NiXenonTextureData` (size = 136 bytes total), and the D3DTexture header is parsed in `InitializeFromD3DTexture`.

```
Header offset  Size  Description
-------------  ----  -----------
0x00-0x0F      16    D3DResource base (Common, RefCount, Fence, ReadFence, Identifier, BaseFlush)
0x10-0x13      4     MipFlush
0x14-0x2F      24    Format structure (xe_gpu_texture_fetch_t): 6 big-endian DWORDs
```

### Format DWORDs (xe_gpu_texture_fetch_t)

The 6 DWORDs at header offset `0x14` encode all GPU texture parameters. These are stored as big-endian and must be byte-swapped for parsing on little-endian systems.

```
DWORD  Bits       Field
-----  ----       -----
[0]    Various    Pitch, tiling mode, clamp modes (S/T/R)
[1]    0-5        Texture format (GPUTEXTUREFORMAT enum value)
       6-7        Endianness
       8-31       Base address (GPU physical)
[2]    0-12       Width - 1 (13 bits, max 8192)
       13-25      Height - 1 (13 bits, max 8192)
       26-31      Stack depth (for volume/array textures)
[3-5]  Various    Swizzle, MIP info, border color, aniso filter, etc.
```

**Dimension decoding** (from DWORD[2], stored big-endian at header offset `0x24`):

```csharp
// Read as big-endian, then:
width  = (dword2 & 0x1FFF) + 1;
height = ((dword2 >> 13) & 0x1FFF) + 1;
```

**Format detection** (DDXConv approach):

- Primary format from DWORD[1] bits 0-5, read at header+0x14 as a byte
- For format `0x82` (base format), the actual compression type is disambiguated using DWORD[2] high byte: `0` = DXT1, non-zero = DXT5

## GPU Texture Formats (GPUTEXTUREFORMAT)

Complete enum from PDB symbols (type 0x1005). Values used by Fallout DDX files are marked with \*.

| Value     | Name                                       | DDS Equivalent    | Block Size     | Notes                           |
| --------- | ------------------------------------------ | ----------------- | -------------- | ------------------------------- |
| 0         | GPUTEXTUREFORMAT_1_REVERSE                 | —                 | —              | 1bpp                            |
| 1         | GPUTEXTUREFORMAT_1                         | —                 | —              | 1bpp                            |
| 2         | GPUTEXTUREFORMAT_8                         | L8                | 1 B/px         | Single channel                  |
| 3         | GPUTEXTUREFORMAT_1_5_5_5                   | A1R5G5B5          | 2 B/px         |                                 |
| 4 \*      | GPUTEXTUREFORMAT_5_6_5                     | R5G6B5            | 2 B/px         | 16bpp uncompressed              |
| 5         | GPUTEXTUREFORMAT_6_5_5                     | —                 | 2 B/px         |                                 |
| 6 \*      | GPUTEXTUREFORMAT_8_8_8_8                   | A8R8G8B8          | 4 B/px         | 32bpp uncompressed              |
| 7         | GPUTEXTUREFORMAT_2_10_10_10                | A2R10G10B10       | 4 B/px         | HDR                             |
| 8         | GPUTEXTUREFORMAT_8_A                       | A8                | 1 B/px         | Alpha only                      |
| 9         | GPUTEXTUREFORMAT_8_B                       | —                 | 1 B/px         |                                 |
| 10        | GPUTEXTUREFORMAT_8_8                       | —                 | 2 B/px         |                                 |
| 11        | GPUTEXTUREFORMAT_Cr_Y1_Cb_Y0_REP           | —                 | —              | YCbCr                           |
| 12        | GPUTEXTUREFORMAT_Y1_Cr_Y0_Cb_REP           | —                 | —              | YCbCr                           |
| 13        | GPUTEXTUREFORMAT_16_16_EDRAM               | —                 | —              | EDRAM only                      |
| 14        | GPUTEXTUREFORMAT_8_8_8_8_A                 | —                 | 4 B/px         |                                 |
| 15        | GPUTEXTUREFORMAT_4_4_4_4                   | A4R4G4B4          | 2 B/px         |                                 |
| 16        | GPUTEXTUREFORMAT_10_11_11                  | —                 | 4 B/px         |                                 |
| 17        | GPUTEXTUREFORMAT_11_11_10                  | —                 | 4 B/px         |                                 |
| **18** \* | **GPUTEXTUREFORMAT_DXT1**                  | **DXT1/BC1**      | **8 B/block**  | **4:1 compression**             |
| **19** \* | **GPUTEXTUREFORMAT_DXT2_3**                | **DXT3/BC2**      | **16 B/block** | **4:1 with explicit alpha**     |
| **20** \* | **GPUTEXTUREFORMAT_DXT4_5**                | **DXT5/BC3**      | **16 B/block** | **4:1 with interpolated alpha** |
| 21        | GPUTEXTUREFORMAT_16_16_16_16_EDRAM         | —                 | —              | EDRAM only                      |
| 22        | GPUTEXTUREFORMAT_24_8                      | D24S8             | 4 B/px         | Depth+stencil                   |
| 23        | GPUTEXTUREFORMAT_24_8_FLOAT                | —                 | 4 B/px         |                                 |
| 24        | GPUTEXTUREFORMAT_16                        | L16               | 2 B/px         |                                 |
| 25        | GPUTEXTUREFORMAT_16_16                     | G16R16            | 4 B/px         |                                 |
| 26        | GPUTEXTUREFORMAT_16_16_16_16               | —                 | 8 B/px         |                                 |
| 27        | GPUTEXTUREFORMAT_16_EXPAND                 | —                 | 2 B/px         |                                 |
| 28        | GPUTEXTUREFORMAT_16_16_EXPAND              | —                 | 4 B/px         |                                 |
| 29        | GPUTEXTUREFORMAT_16_16_16_16_EXPAND        | —                 | 8 B/px         |                                 |
| 30        | GPUTEXTUREFORMAT_16_FLOAT                  | R16F              | 2 B/px         |                                 |
| 31        | GPUTEXTUREFORMAT_16_16_FLOAT               | G16R16F           | 4 B/px         |                                 |
| 32        | GPUTEXTUREFORMAT_16_16_16_16_FLOAT         | —                 | 8 B/px         |                                 |
| 33        | GPUTEXTUREFORMAT_32                        | —                 | 4 B/px         |                                 |
| 34        | GPUTEXTUREFORMAT_32_32                     | —                 | 8 B/px         |                                 |
| 35        | GPUTEXTUREFORMAT_32_32_32_32               | —                 | 16 B/px        |                                 |
| 36        | GPUTEXTUREFORMAT_32_FLOAT                  | R32F              | 4 B/px         |                                 |
| 37        | GPUTEXTUREFORMAT_32_32_FLOAT               | G32R32F           | 8 B/px         |                                 |
| 38        | GPUTEXTUREFORMAT_32_32_32_32_FLOAT         | —                 | 16 B/px        |                                 |
| 39        | GPUTEXTUREFORMAT_32_AS_8                   | —                 | —              |                                 |
| 40        | GPUTEXTUREFORMAT_32_AS_8_8                 | —                 | —              |                                 |
| 41        | GPUTEXTUREFORMAT_16_MPEG                   | —                 | —              | Video                           |
| 42        | GPUTEXTUREFORMAT_16_16_MPEG                | —                 | —              | Video                           |
| 43        | GPUTEXTUREFORMAT_8_INTERLACED              | —                 | —              | Video                           |
| 44        | GPUTEXTUREFORMAT_32_AS_8_INTERLACED        | —                 | —              | Video                           |
| 45        | GPUTEXTUREFORMAT_32_AS_8_8_INTERLACED      | —                 | —              | Video                           |
| 46        | GPUTEXTUREFORMAT_16_INTERLACED             | —                 | —              | Video                           |
| 47        | GPUTEXTUREFORMAT_16_MPEG_INTERLACED        | —                 | —              | Video                           |
| 48        | GPUTEXTUREFORMAT_16_16_MPEG_INTERLACED     | —                 | —              | Video                           |
| **49** \* | **GPUTEXTUREFORMAT_DXN**                   | **ATI2/BC5**      | **16 B/block** | **Xbox 360 normal maps**        |
| 50        | GPUTEXTUREFORMAT_8_8_8_8_AS_16_16_16_16    | —                 | —              |                                 |
| 51        | GPUTEXTUREFORMAT_DXT1_AS_16_16_16_16       | —                 | —              |                                 |
| 52        | GPUTEXTUREFORMAT_DXT2_3_AS_16_16_16_16     | —                 | —              |                                 |
| 53        | GPUTEXTUREFORMAT_DXT4_5_AS_16_16_16_16     | —                 | —              |                                 |
| 54        | GPUTEXTUREFORMAT_2_10_10_10_AS_16_16_16_16 | —                 | —              |                                 |
| 55        | GPUTEXTUREFORMAT_10_11_11_AS_16_16_16_16   | —                 | —              |                                 |
| 56        | GPUTEXTUREFORMAT_11_11_10_AS_16_16_16_16   | —                 | —              |                                 |
| 57        | GPUTEXTUREFORMAT_32_32_32_FLOAT            | —                 | 12 B/px        |                                 |
| **58** \* | **GPUTEXTUREFORMAT_DXT3A**                 | **ATI1/BC4**      | **8 B/block**  | **Specular maps**               |
| **59** \* | **GPUTEXTUREFORMAT_DXT5A**                 | **(BC4 variant)** | **8 B/block**  | **Single-channel alpha**        |
| **60** \* | **GPUTEXTUREFORMAT_CTX1**                  | **—**             | **8 B/block**  | **Xbox 360-only 2-channel**     |
| 61        | GPUTEXTUREFORMAT_DXT3A_AS_1_1_1_1          | —                 | —              |                                 |
| 62        | GPUTEXTUREFORMAT_8_8_8_8_GAMMA_EDRAM       | —                 | 4 B/px         | EDRAM gamma                     |
| 63        | GPUTEXTUREFORMAT_2_10_10_10_FLOAT_EDRAM    | —                 | 4 B/px         | EDRAM float                     |

### DDXConv Format Code Mapping

DDXConv internally maps Xbox GPU format codes (which appear in DDX file headers) to DDS FourCC codes:

| DDX Code   | GPU Format | DDS FourCC   | Block Size | Notes                              |
| ---------- | ---------- | ------------ | ---------- | ---------------------------------- |
| 0x12 (18)  | DXT1       | `DXT1` (BC1) | 8          | Standard DXT1                      |
| 0x13 (19)  | DXT2_3     | `DXT3` (BC2) | 16         | Standard DXT3                      |
| 0x14 (20)  | DXT4_5     | `DXT5` (BC3) | 16         | Standard DXT5                      |
| 0x52 (82)  | —          | `DXT1` (BC1) | 8          | Alternate DXT1 code                |
| 0x53 (83)  | —          | `DXT3` (BC2) | 16         | Alternate DXT3 code                |
| 0x54 (84)  | —          | `DXT5` (BC3) | 16         | Alternate DXT5 code                |
| 0x71 (113) | DXN (49)   | `ATI2` (BC5) | 16         | 2-channel normal maps              |
| 0x7B (123) | DXT3A (58) | `ATI1` (BC4) | 8          | Single-channel specular            |
| 0x82 (130) | —          | `DXT1` (BC1) | 8          | Base format (actual from DWORD[4]) |
| 0x86 (134) | —          | `DXT1` (BC1) | 8          | DXT1 variant                       |
| 0x88 (136) | —          | `DXT5` (BC3) | 16         | DXT5 variant                       |
| 0x04 (4)   | 5_6_5      | R5G6B5       | 2 B/px     | Uncompressed 16bpp                 |
| 0x06 (6)   | 8_8_8_8    | A8R8G8B8     | 4 B/px     | Uncompressed 32bpp                 |

**Note**: Codes 0x52-0x88 are not direct GPUTEXTUREFORMAT values but rather encoded format identifiers that DDXConv encountered in DDX file headers. The exact mapping between these and the GPU enum likely involves additional bit fields from the texture fetch constant.

## Bethesda Texture Type System (BSTEXTURE_TYPE)

From PDB: `NiXenonSourceTextureData::BSTEXTURE_TYPE` enum controls how the engine interprets and processes textures:

| Value | Name                     | Description                           |
| ----- | ------------------------ | ------------------------------------- |
| 0     | `BSTT_STANDARD`          | Diffuse/color texture                 |
| 1     | `BSTT_NORMAL_MAP`        | Object normal maps (BC5/ATI2 on Xbox) |
| 2     | `BSTT_LANDSCAPE_NORMALS` | Terrain normal maps                   |
| 3     | `BSTT_FACE_NORMALS`      | Character face normal maps            |
| 4     | `BSTT_HEIGHT_MAP`        | Parallax height maps                  |
| 5     | `BSTT_GLOW_MAP`          | Emissive/glow textures                |
| 6     | `BSTT_SPECULAR`          | Specular maps (BC4/ATI1 on Xbox)      |
| 7     | `BSTT_LUMINENCE`         | Luminance textures                    |
| 8     | `BSTT_HAIR`              | Hair textures                         |
| 9     | `BSTT_HAIR_LAYER`        | Hair layer textures                   |
| 10    | `BSST_CUBE_MAP`          | Cube map textures                     |
| 11    | `NUM_BSTT_TYPES`         | Sentinel (count)                      |

### Normal Map Pipeline (Xbox → PC)

On Xbox 360, normal maps are stored as two separate textures:

- **`*_n.ddx`**: BC5/ATI2 (2-channel: R=X, G=Y normal)
- **`*_s.ddx`**: BC4/ATI1 (1-channel: specular intensity)

On PC, these are combined into a single DXT5/BC3 texture:

- R = Normal X, G = Normal Y, B = reconstructed Normal Z, A = Specular

The Z component is reconstructed: `Z = sqrt(1 - X² - Y²)`

DDXConv's `DdsPostProcessor.MergeNormalSpecularMaps()` handles this merge, re-encoding to BC3 with the `KRAN` signature at DDS offset `0x44` (`dwReserved1[9]`). This is branding from **Kran27**, the original DDXConv author — the same reserved field that NVIDIA tools use for `NVTT`. The game engine ignores this field entirely; it is not a Bethesda convention.

## Tiling / Swizzling

Xbox 360 GPU textures are stored in a tiled (swizzled) memory layout optimized for cache coherence during rasterization. There are two tiling schemes used in DDX files.

### Morton / Z-Order Tiling (3XDO)

The primary tiling algorithm for 3XDO format textures. Operations are performed at the DXT block level (4×4 pixel blocks), not individual pixels.

**Algorithm** (from Xenia `texture_conversion.cc`, used in DDXConv):

```
TiledOffset2DRow(y, width, log2Bpp):
  macro = ((y >> 5) * ((width >> 5) << log2Bpp)) << 11
  micro = ((y & 6) >> 1) << log2Bpp << 7
  return macro + ((micro + ((y & 8) << (7 + log2Bpp))) ^ ((y & 1) << 4))

TiledOffset2DColumn(x, y, log2Bpp, rowOffset):
  macro = (x >> 5) << log2Bpp << 11
  micro = ((x & 7) + ((x & 8) << 1)) << log2Bpp
  offset = macro + (micro ^ (((y & 8) << 3) + ((y & 1) << 4)))
  return ((rowOffset + offset) << log2Bpp) >> log2Bpp
```

Where `log2Bpp` is derived from the DXT block size:

- 8-byte blocks (DXT1, BC4): `log2Bpp = 2`
- 16-byte blocks (DXT5, BC5): `log2Bpp = 3`

### 2×2 Macro Block Tiling (3XDR)

3XDR uses a simpler tiling pattern operating on 8×2 groups of DXT blocks. Within each group:

```
Xbox row 0 (Y=0 within group):
  Xbox X: 0  1  2  3  4  5  6  7
  PC X:   0  1  0  1  2  3  2  3
  PC Y:   0  0  1  1  0  0  1  1

Xbox row 1 (Y=1 within group):
  Xbox X: 0  1  2  3  4  5  6  7
  PC X:   4  5  4  5  6  7  6  7
  PC Y:   0  0  1  1  0  0  1  1
```

**Formula**:

```
pcLocalX = localY * 4 + (localX / 4) * 2 + (localX % 2)
pcLocalY = (localX / 2) % 2
```

This is a bit permutation (swizzle) within the 8×2 group — equivalent to:

```
pcLocalX = (localY << 2) | ((localX >> 2) << 1) | (localX & 1)
pcLocalY = (localX >> 1) & 1
```

### Engine Tiling Tables (from PDB)

The `NiXenonSourceTextureData` class contains static tables used by the engine's texture tiling system:

| Static Member                | Type              | Description                                    |
| ---------------------------- | ----------------- | ---------------------------------------------- |
| `pTextureTileRectsA`         | TextureTileRect[] | Tile rectangle definitions (12 entries)        |
| `pFirstTilingRectsA`         | byte[4,3]         | Index into pTextureTileRectsA per format/size  |
| `pTilingRectCountsA`         | byte[4,3]         | Number of rects per format/size                |
| `pTilingStridesA`            | uint[]            | Row strides per tiling configuration           |
| `pTiledTextureLevelOffsetsA` | uint[]            | Byte offsets to each mip level in tiled layout |
| `pBitsPerPixel`              | byte[]            | Bits-per-pixel lookup indexed by GPU format    |

The `TextureTileRect` structure (8 bytes):

```c
struct TextureTileRect {
    uint16_t cByteOffsetX;   // Byte offset in X
    uint16_t cLineOffsetY;   // Line offset in Y
    uint16_t cBytesWide;     // Width in bytes
    uint16_t cLinesHigh;     // Height in lines
};
```

These tables define how the engine reassembles mip atlases from the engine's tiling layout — they describe rectangular regions within the tiled atlas that correspond to individual mip levels.

## MIP Chain Storage

Unlike PC DDS where mip levels are stored sequentially (largest to smallest), DDX files use packed mip atlases. The exact layout depends on texture size and the number of LZX-compressed chunks.

### Two-Chunk Format

Most DDX files decompress to two separate chunks:

| Chunk       | Content                                              | Tiling          |
| ----------- | ---------------------------------------------------- | --------------- |
| 1 (smaller) | Mip atlas — mip levels 1+ packed in a tiled 2D atlas | Morton swizzled |
| 2 (larger)  | Main surface — mip level 0 at full resolution        | Morton swizzled |

### Mip Atlas Layout

The mip atlas packs smaller mip levels into a single tiled texture. The atlas dimensions depend on the main texture size:

- **Square textures** (e.g., 1024×1024): Atlas is same dimensions as main
- **Wide textures** (W > H): Atlas width = W × 5/8, height = H
- **Tall textures** (H > W): Atlas width = W, height = H × 5/8
- **Small textures** (≤256): Atlas may be same size or larger (e.g., 128×128 texture → 256×256 atlas)

Within the atlas, mip levels are arranged as:

1. **Largest mip** (W/2 × H/2) in the top-left quadrant
2. **Smaller mips** stacked vertically to the right or below, each half the previous size

For textures ≤256×256, the atlas may use a different layout where the largest mip is split into top/bottom halves placed side by side, with smaller mips below.

### Single-Chunk Format

Some textures decompress to a single chunk containing either:

- **Main surface only**: Just mip level 0, requiring no atlas unpacking
- **Main + sequential mips**: For large textures (≥512), mip data follows main surface linearly (each level also Morton-swizzled independently)
- **Main + mip atlas**: Single chunk with 2× main surface size — first half is main, second half is atlas

### Memory Dump Textures

Textures carved from Xbox 360 memory dumps may have additional layout patterns:

- **Packed mip atlas for half-size base**: A WxH tiled space containing a W/2×H/2 base texture with mips
- **Atlas-only data**: Missing the main surface, containing only the mip atlas portion
- **Partial data**: Truncated textures from incomplete memory regions

## XMemCompress / LZX Decompression

DDX texture data is compressed using Microsoft's XMemCompress, which is a chunked variant of the LZX (Lempel-Ziv Extended) algorithm also used in `.cab` files and Xbox LIVE content packages.

### Chunk Format

The compressed data consists of a sequence of independently-decompressible chunks:

```
For each chunk:
  If first_byte == 0xFF:
    Offset+0: 0xFF (marker byte)
    Offset+1: Uncompressed size high byte
    Offset+2: Uncompressed size low byte
    Offset+3: Compressed size high byte
    Offset+4: Compressed size low byte
    Offset+5: LZX compressed data
  Else:
    Offset+0: Compressed size high byte
    Offset+1: Compressed size low byte
    (Uncompressed size defaults to 0x8000 = 32,768 bytes)
    Offset+2: LZX compressed data
```

### LZX Parameters

| Parameter      | Value                                                   |
| -------------- | ------------------------------------------------------- |
| Window size    | 131,072 bytes (128 KB)                                  |
| Chunk size     | 524,288 bytes (512 KB) max uncompressed                 |
| Block types    | Verbatim (1), Aligned (2), Uncompressed (3)             |
| E8 translation | Intel CALL instruction fixup (standard LZX feature)     |
| Bit ordering   | MSB-first (big-endian bit reading from LE 16-bit words) |

### Huffman Trees

Each LZX block contains up to three Huffman trees:

- **Main tree**: Literal bytes (0-255) + match symbols (256+), up to `256 + numPositionSlots * 8` symbols
- **Length tree**: 249 symbols for extended match lengths
- **Aligned tree**: 8 symbols (aligned block type only), 3-bit codes
- **Pretree**: 20 symbols, used to delta-encode main/length tree code lengths

Position slots are computed from the window size: `numPositionSlots = log2(windowSize) * 2 = 34` for 128 KB windows.

## Endian Conversion

All DXT block-compressed texture data on Xbox 360 is stored in big-endian (PowerPC) byte order. Conversion to PC requires swapping every 16-bit word:

```csharp
for (int i = 0; i < data.Length - 1; i += 2)
{
    byte tmp = data[i];
    data[i] = data[i + 1];
    data[i + 1] = tmp;
}
```

This applies to DXT1, DXT3, DXT5, BC4, BC5, and CTX1 block data. For uncompressed formats (A8R8G8B8, R5G6B5), the swap width matches the pixel component size.

**Important**: The endian swap is performed AFTER untiling. The tiling algorithm operates on block indices, not on the byte contents of blocks, so the order is: decompress → untile → endian swap.

## DDX Loading in the Game Engine

### Control Flow (from PDB)

The texture loading pipeline in the Xbox 360 Fallout engine:

1. **`NiXenonSourceTextureData::LoadTextureFile`** — Entry point, determines file type
2. **`NiXenonSourceTextureData::CreateFromDDXFile`** — DDX-specific loading path
3. **`NiXenonSourceTextureData::InitializeFromD3DTexture`** — Configures GPU texture fetch constant
4. **`NiXenonSourceTextureData::CopyDataToSurface`** → **`CopyDataToSurfaceLevel`** — Copies decompressed data to GPU memory
5. **`NiXenonSourceTextureData::PostProcessTexture`** — Post-processing (format conversion, filtering)
6. **`NiXenonSourceTextureData::Upgrade`** / **`Degrade`** — MIP level streaming (load higher/lower detail)

### Global State

| Symbol                    | Type   | Description                                     |
| ------------------------- | ------ | ----------------------------------------------- |
| `bUseDDXTextures`         | bool   | Whether to load DDX (true on Xbox, false on PC) |
| `pCompressedBuffer`       | char\* | Static buffer for compressed data               |
| `iCompressedBufferSize`   | uint   | Size of compressed buffer                       |
| `pUncompressedBuffer`     | char\* | Static buffer for decompressed data             |
| `iUncompressedBufferSize` | uint   | Size of uncompressed buffer                     |
| `ms_uiSkipLevels`         | uint   | Number of MIP levels to skip (LOD bias)         |
| `ulLoadedTextureMemory`   | ulong  | Total GPU memory used by loaded textures        |
| `ulTextureMemoryOverhead` | ulong  | Texture management overhead                     |

The engine uses static shared buffers for decompression, meaning textures are loaded sequentially (not concurrently decompressed). The `ms_uiSkipLevels` value controls the global MIP skip level — when GPU memory is under pressure, the engine can increase this to load only lower-resolution mip levels.

### D3DIOP_DDX

The PDB contains a constant `D3DIOP_DDX = 16` in what appears to be a D3D instruction/operation enum. This may represent a GPU instruction or render state related to DDX texture operations, though its exact role is unclear.

## DDX-to-DDS Conversion Pipeline

The full conversion pipeline implemented by DDXConv:

```
1. Read DDX header (magic, priority, version, D3DTexture header)
2. Parse dimensions and format from GPU texture fetch constant
3. Read remaining file data (compressed)
4. Decompress XMemCompress/LZX chunks
5. Determine layout (one-chunk vs two-chunk, atlas dimensions)
6. Untile/unswizzle each data region:
   a. Morton deswizzle (3XDO) or 2×2 macro untile (3XDR)
   b. Operate at DXT block granularity
7. Unpack mip atlas → extract individual mip levels
8. Endian swap (16-bit word swap on all block data)
9. Write DDS header + linear mip chain
10. (Optional) Merge normal + specular maps for PC compatibility
11. (Optional) Regenerate MIP levels from base using BCnEncoder
```

### Known Conversion Issues

- **Specular intensity**: BC5→BC3 re-encoding introduces quality loss from the additional encode step
- **MIP regeneration**: Re-encoded MIPs don't match the original Xbox encoder's output exactly
- **Atlas dimension heuristics**: Some unusual texture sizes may require manual atlas dimension specification
- **CTX1 format**: Xbox 360-exclusive 2-channel compressed format with no direct DDS equivalent; must be transcoded

## References

- **Xenia emulator**: `src/xenia/gpu/texture_conversion.cc` — Tiling/swizzling algorithms
- **BSArchPro**: `wbBSArchive.pas` — BSA hash algorithm and file flags
- **Xbox 360 SDK**: `xe_gpu_texture_fetch_t` structure documentation
- **BCnEncoder.NET**: BC1-BC7 encode/decode for MIP regeneration and normal map processing
- **XnaNative.dll v4.0.30901.0**: Original Microsoft LZX implementation (decompiled to `LzxDecompressor.cs`)
