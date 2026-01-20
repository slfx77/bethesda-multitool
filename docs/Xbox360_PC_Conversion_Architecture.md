# Xbox 360 to PC Conversion Architecture

This document outlines the architecture for a comprehensive tool to convert Xbox 360 Fallout: New Vegas content to PC format, enabling "plug and play" use of Xbox 360 game assets on PC.

---

## Table of Contents

1. [Overview](#overview)
2. [File Types and Conversion Requirements](#file-types-and-conversion-requirements)
3. [Conversion Pipeline](#conversion-pipeline)
4. [BSA Archive Format](#bsa-archive-format)
5. [ESM/ESP Plugin Conversion](#esmesp-plugin-conversion)
6. [Implementation Plan](#implementation-plan)
7. [Current Status](#current-status)

---

## Overview

### Goal

Create a tool that can:

1. **Extract** files from Xbox 360 BSA archives
2. **Convert** platform-specific files (NIF, DDX, XMA, ESM) to PC format
3. **Repack** converted files into PC-compatible BSA archives
4. **Produce** a "drop-in" mod that replaces PC content with converted Xbox 360 content

### Use Cases

1. **July 2010 Prototype Content**: Access cut/changed content from the July 21, 2010 Xbox 360 prototype
2. **Platform Comparison**: Compare Xbox 360 vs PC versions of assets
3. **Restoration Mods**: Create mods that restore removed Xbox 360 content to PC

---

## File Types and Conversion Requirements

### File Categories

| Category          | Xbox 360 Format   | PC Format         | Conversion                       | Status     |
| ----------------- | ----------------- | ----------------- | -------------------------------- | ---------- |
| **Textures**      | DDX (3XDO/3XDR)   | DDS               | Untile + decompress              | ✅ Working |
| **Models**        | NIF (BE + packed) | NIF (LE + inline) | Endian swap + geometry expansion | ✅ Working |
| **Audio**         | XMA               | WAV/XWM           | XMA decode                       | ✅ Working |
| **Plugins**       | ESM/ESP (BE)      | ESM/ESP (LE)      | Endian swap all fields           | ⏳ Needed  |
| **UI**            | XUI (BE)          | XUI (LE)          | Conversion via XUIHelper         | ✅ Working |
| **Archives**      | BSA (BE)          | BSA (LE)          | Endian swap + extract/repack     | ⏳ Needed  |
| **Scripts**       | SCDA (compiled)   | SCDA (compiled)   | Endian swap                      | ⏳ Needed  |
| **Lip Sync**      | LIP               | LIP               | May need endian swap             | ❓ Unknown |
| **KF Animations** | KF (BE)           | KF (LE)           | Endian swap                      | ❓ Unknown |

### Conversion Details

#### Textures (DDX -> DDS)

- **Xbox 360**: Morton-order tiled, possibly XMemCompress'd
- **PC**: Linear DDS
- **Converter**: `DdxSubprocessConverter` using DDXConv tool
- **Status**: ✅ Fully working

#### Models (NIF BE -> NIF LE)

- **Xbox 360**: Big-endian, `BSPackedAdditionalGeometryData` for geometry
- **PC**: Little-endian, inline geometry in `NiTriShapeData`/`NiTriStripsData`
- **Converter**: `NifConverter` with schema-driven field conversion
- **Status**: ✅ Fully working (static + skinned meshes)

#### Audio (XMA -> WAV)

- **Xbox 360**: XMA compressed audio
- **PC**: PCM WAV or XWM (Ogg Vorbis variant)
- **Converter**: `xWMAEncode` or xma2wav
- **Status**: ✅ Working via external tools

#### Plugins (ESM/ESP)

- **Xbox 360**: Big-endian (TES4 reads as "4SET")
- **PC**: Little-endian
- **Converter**: Need to swap ALL multi-byte fields in ALL record types
- **Status**: ⏳ Parser working, converter needed

#### Archives (BSA)

- **Xbox 360**: Archive flag bit 7 set = big-endian
- **PC**: Little-endian (flag bit 7 clear)
- **Converter**: Extract -> Convert files -> Repack
- **Status**: ⏳ Not implemented

---

## Conversion Pipeline

### High-Level Flow

```
┌─────────────────────────────────────────────────────────────────────────┐
│                    Xbox 360 Content Directory                           │
│  ┌──────────────┐  ┌──────────────┐  ┌──────────────┐                   │
│  │ FalloutNV.esm│  │ Meshes.bsa   │  │ Textures.bsa │  ...              │
│  └──────┬───────┘  └──────┬───────┘  └──────┬───────┘                   │
└─────────┼──────────────────┼──────────────────┼─────────────────────────┘
          │                  │                  │
          ▼                  ▼                  ▼
   ┌──────────────┐   ┌──────────────┐   ┌──────────────┐
   │ ESM Converter │   │BSA Extractor │   │BSA Extractor │
   │ (Endian Swap) │   │ (Endian Swap)│   │ (Endian Swap)│
   └──────┬───────┘   └──────┬───────┘   └──────┬───────┘
          │                  │                  │
          │                  ▼                  ▼
          │           ┌──────────────┐   ┌──────────────┐
          │           │ NIF, KF, LIP │   │ DDX Textures │
          │           │   files      │   │              │
          │           └──────┬───────┘   └──────┬───────┘
          │                  │                  │
          │                  ▼                  ▼
          │           ┌──────────────┐   ┌──────────────┐
          │           │NIF Converter │   │DDX Converter │
          │           │ BE -> LE+Expand │   │ DDX -> DDS      │
          │           └──────┬───────┘   └──────┬───────┘
          │                  │                  │
          ▼                  ▼                  ▼
┌─────────────────────────────────────────────────────────────────────────┐
│                         Converted Files                                  │
│  ┌──────────────┐  ┌──────────────┐  ┌──────────────┐                   │
│  │FalloutNV.esm │  │ *.nif (LE)   │  │ *.dds (PC)   │                   │
│  │    (LE)      │  │              │  │              │                   │
│  └──────────────┘  └──────────────┘  └──────────────┘                   │
└─────────────────────────────────────────────────────────────────────────┘
                             │
                             ▼
                    ┌──────────────┐
                    │ BSA Repacker │
                    │ (PC Format)  │
                    └──────┬───────┘
                             │
                             ▼
┌─────────────────────────────────────────────────────────────────────────┐
│                    PC-Ready Content Directory                            │
│  ┌──────────────┐  ┌──────────────┐  ┌──────────────┐                   │
│  │FalloutNV.esm │  │ Meshes.bsa   │  │ Textures.bsa │                   │
│  │    (LE)      │  │    (LE)      │  │    (LE)      │                   │
│  └──────────────┘  └──────────────┘  └──────────────┘                   │
└─────────────────────────────────────────────────────────────────────────┘
```

### Detailed Steps

1. **BSA Extraction**
   - Parse BSA header (check flag bit 7 for endianness)
   - Read folder/file records with correct endianness
   - Extract files, decompressing if needed (zlib)
   - Track original paths for repacking

2. **File Conversion**
   - Identify file type by extension/signature
   - Route to appropriate converter:
     - `.nif`, `.kf` -> NIF Converter
     - `.ddx` -> DDX Converter
     - `.xma` -> XMA Converter
     - `.lip` -> LIP Converter (if needed)
   - Skip files that don't need conversion (`.txt`, `.xml`, etc.)

3. **ESM/ESP Conversion**
   - Read all records with big-endian parsing
   - Write all records with little-endian encoding
   - Preserve exact record structure and order

4. **BSA Repacking**
   - Recreate folder structure from extracted paths
   - Calculate hashes using PC (LE) method
   - Write BSA with flag bit 7 clear (little-endian)
   - Optionally compress files

---

## BSA Archive Format

### Header (36 bytes)

```
Offset  Size  Field               Notes
0x00    4     fileId              "BSA\0"
0x04    4     version             104 (0x68) for FO3/FNV/Skyrim
0x08    4     offset              36 (header size)
0x0C    4     archiveFlags        Bit 7 = big-endian if set
0x10    4     folderCount         Number of folders
0x14    4     fileCount           Number of files
0x18    4     totalFolderNameLen  Sum of folder name lengths
0x1C    4     totalFileNameLen    Sum of file name lengths
0x20    2     fileFlags           Content type flags
0x22    2     padding
```

### Archive Flags

| Bit | Meaning                   |
| --- | ------------------------- |
| 0   | Include directory names   |
| 1   | Include file names        |
| 2   | Compressed by default     |
| 6   | Xbox 360 archive (?)      |
| 7   | **Big-endian** (Xbox 360) |
| 8   | Embed file names in data  |

### Folder Record (16 bytes on 32-bit, 24 bytes on 64-bit)

```
Offset  Size  Field      Notes
0x00    8     nameHash   64-bit hash of folder path
0x08    4     count      Files in this folder
0x0C    4     offset     Offset to file records
```

### File Record (16 bytes)

```
Offset  Size  Field      Notes
0x00    8     nameHash   64-bit hash of filename
0x08    4     size       File size (bit 30 = compression toggle)
0x0C    4     offset     Offset to file data
```

### Hash Algorithm

```csharp
public static ulong CalculateHash(string path)
{
    // Lowercase, backslash separators
    path = path.ToLower().Replace('/', '\\');

    var ext = Path.GetExtension(path);
    var name = Path.GetFileNameWithoutExtension(path);

    // Extension contributes to low bytes
    var hash1 = (ulong)(name.Length > 0 ? name[name.Length - 1] : 0)
              | ((ulong)(name.Length > 1 ? name[name.Length - 2] : 0) << 8)
              | ((ulong)name.Length << 16)
              | ((ulong)(name.Length > 0 ? name[0] : 0) << 24);

    // Hash2 from rest of name
    ulong hash2 = 0;
    for (int i = 1; i < name.Length - 2; i++)
        hash2 = hash2 * 0x1003F + (ulong)name[i];

    // Extension hash
    ulong hash3 = 0;
    foreach (var c in ext)
        hash3 = hash3 * 0x1003F + (ulong)c;

    return (hash2 + hash3) << 32 | hash1;
}
```

---

## ESM/ESP Plugin Conversion

### Record Structure

All records use the same basic structure that needs endian conversion:

```
Main Record Header (24 bytes):
- Signature: 4 chars (no conversion needed)
- DataSize: uint32 (endian swap)
- Flags: uint32 (endian swap)
- FormID: uint32 (endian swap)
- VersionControl: 8 bytes (endian swap as 2x uint32 or 1x uint64)

Group Header (24 bytes):
- GRUP signature: 4 chars
- GroupSize: uint32 (endian swap)
- Label: 4 bytes (depends on group type)
- GroupType: int32 (endian swap)
- Stamp: uint16 (endian swap)
- Unknown: 6 bytes

Subrecord Header (6 bytes):
- Signature: 4 chars
- Size: uint16 (endian swap)
- Data: [Size] bytes (contents depend on field type)
```

### Field Type Conversion

| Field Type                 | Conversion Required   |
| -------------------------- | --------------------- |
| `int8/uint8`               | None                  |
| `int16/uint16`             | Endian swap (2 bytes) |
| `int32/uint32`             | Endian swap (4 bytes) |
| `int64/uint64`             | Endian swap (8 bytes) |
| `float`                    | Endian swap (4 bytes) |
| `FormID`                   | Endian swap (4 bytes) |
| `string` (null-terminated) | None                  |
| `lstring` (localized)      | Endian swap index     |
| `Vector3`                  | 3× float swaps        |
| `Color`                    | 4× uint8 (none)       |

### Subrecord-Specific Handling

Most subrecords can be converted generically by knowing their data type:

```csharp
// Generic converter based on data type
public byte[] ConvertSubrecordData(string signature, byte[] data, SubrecordDataType type)
{
    return type switch
    {
        SubrecordDataType.UInt32 => BinaryUtils.SwapEndian32(data),
        SubrecordDataType.Int32 => BinaryUtils.SwapEndian32(data),
        SubrecordDataType.Float => BinaryUtils.SwapEndian32(data),
        SubrecordDataType.UInt16 => BinaryUtils.SwapEndian16(data),
        SubrecordDataType.FormID => BinaryUtils.SwapEndian32(data),
        SubrecordDataType.String => data, // No conversion
        SubrecordDataType.ByteArray => data, // No conversion
        SubrecordDataType.Struct => ConvertStruct(signature, data),
        _ => data
    };
}
```

### Complex Struct Handling

Some subrecords contain complex structures that need field-by-field conversion:

```
OBND (Object Bounds) - 12 bytes:
  int16 X1, Y1, Z1, X2, Y2, Z2 - 6× endian swaps

DATA (varies by record type):
  - STAT: float (mass) - 1× swap
  - NPC_: complex struct - many swaps
  - WEAP: many fields - many swaps

XCLC (Cell Grid) - 12 bytes:
  int32 X, Y, uint32 Flags - 3× swaps
```

---

## Implementation Plan

### Phase 1: BSA Format Support

1. **BsaParser.cs** - Parse BSA header, folders, files
   - Handle both LE and BE based on flag bit 7
   - Read file/folder records
   - Extract files (with decompression)

2. **BsaExtractCommand.cs** - CLI command
   - `extract-bsa <input.bsa> -o <output_dir>`
   - Verbose mode shows file list
   - Option to convert files during extraction

### Phase 2: ESM Converter

1. **EsmConverter.cs** - Convert ESM BE -> LE
   - Read records with BE parser
   - Write records with LE encoding
   - Handle all subrecord types

2. **EsmConvertCommand.cs** - CLI command
   - `convert-esm <input.esm> -o <output.esm>`
   - Verify round-trip integrity

### Phase 3: Full Conversion Pipeline

1. **ConvertDirectoryCommand.cs** - Full conversion
   - `convert-xbox <input_dir> -o <output_dir>`
   - Auto-detect and convert all file types
   - Option to repack into BSAs

2. **BsaPacker.cs** - Create BSA from files
   - Calculate hashes correctly
   - Support compression options
   - Write PC-format BSA

### Phase 4: Validation & Testing

1. Test against July 2010 prototype
2. Test against Xbox 360 final build
3. Verify converted files work in PC game
4. Performance optimization for large archives

---

## Current Status

### ✅ Completed

| Component       | Status | Notes                                 |
| --------------- | ------ | ------------------------------------- |
| DDX -> DDS      | ✅     | Via DDXConv subprocess                |
| NIF BE -> LE    | ✅     | Schema-driven, skinned meshes working |
| XMA -> WAV      | ✅     | Via external tools                    |
| XUI -> XUI      | ✅     | Via XUIHelper                         |
| ESM Parser (BE) | ✅     | 70+ record types, endian-aware        |

### ⏳ In Progress

| Component     | Status | Notes                      |
| ------------- | ------ | -------------------------- |
| ESM Converter | ⏳     | Parser done, writer needed |
| BSA Parser    | ⏳     | Not started                |

### ❓ Unknown

| Component    | Status | Notes                     |
| ------------ | ------ | ------------------------- |
| KF Animation | ❓     | Likely just endian swap   |
| LIP Sync     | ❓     | May need special handling |
| Havok HKX    | ❓     | Physics data format       |

---

## CLI Commands (Proposed)

```bash
# Extract BSA archive
Xbox360MemoryCarver extract-bsa FalloutNV-Meshes.bsa -o extracted/

# Extract and convert files
Xbox360MemoryCarver extract-bsa FalloutNV-Meshes.bsa -o converted/ --convert

# Convert ESM file
Xbox360MemoryCarver convert-esm FalloutNV.esm -o FalloutNV_PC.esm

# Full directory conversion
Xbox360MemoryCarver convert-xbox "Xbox360Data/" -o "PCData/"

# Repack as BSA
Xbox360MemoryCarver pack-bsa "PCData/meshes/" -o "Meshes.bsa" --compress
```

---

## References

- [UESP BSA Format](https://en.uesp.net/wiki/Skyrim_Mod:Archive_File_Format)
- [UESP ESM Format](https://en.uesp.net/wiki/Skyrim_Mod:Mod_File_Format)
- [xEdit Source](https://github.com/TES5Edit/TES5Edit) - Reference for record definitions
- [NifSkope Source](https://github.com/niftools/nifskope) - NIF format reference
