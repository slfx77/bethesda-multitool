# Handling Incomplete Files from RAM Dumps

## Overview

When extracting files from RAM dumps, it's common to encounter incomplete or partial files. This is **normal and expected** because:

1. **Paging** - Not all memory is resident in RAM
2. **Fragmentation** - Files may be split across non-contiguous memory regions
3. **Overwriting** - Memory may be reused, corrupting file data
4. **Dump boundaries** - Files may start or end outside the dumped region

This guide helps you understand and work with partial files.

---

## Types of Incomplete Files

### 1. Header-Only Files

**Characteristics:**

- Valid file header present
- Little or no data payload
- Size mismatch between header and actual data

**Usability:** ⚠️ Limited

- Can extract metadata (dimensions, format, etc.)
- Not usable as actual asset

**Example (DDS):**

```
dds_0045_off_A4B2C000.ddx
- Header: Valid, 1024×1024, DXT5, 4 mipmaps
- Data: Only 512 bytes (expected: ~700KB)
- Status: Header metadata extractable, texture not viewable
```

### 2. Truncated Files

**Characteristics:**

- Valid header
- Partial data payload
- Abrupt end (no footer/terminator)

**Usability:** ⭐ Partial to Good

- May be usable depending on format
- Textures: Top portion may render
- Audio: First few seconds may play
- Models: Some geometry may load

**Example (XMA Audio):**

```
xma_0012_off_B5C3D000.xma
- Header: Valid, 44.1kHz, stereo
- Data: 1.2 MB (expected: 3.5 MB)
- Status: First ~20 seconds playable
```

### 3. Fragmented Files

**Characteristics:**

- Valid header
- Data contains corruption/gaps
- May have valid sections interspersed with garbage

**Usability:** ⚠️ Poor

- Unpredictable results
- May crash viewers/editors
- Sometimes partially recoverable with hex editing

**Example (NIF Model):**

```
nif_0089_off_C6D4E000.nif
- Header: Valid, Gamebryo 20.2.0.7
- Block 1-5: Valid geometry
- Block 6-10: Corrupted (overwritten)
- Block 11-15: Valid again
- Status: Model crashes viewer due to invalid block chain
```

### 4. Complete Files

**Characteristics:**

- Valid header
- Complete data payload
- Proper termination/footer (if format requires)

**Usability:** ✅ Excellent

- Fully usable
- Identical to original file

---

## Identifying File Completeness

### Manual Inspection

#### DDS Textures

1. Check header dimensions and format
2. Calculate expected size:
   ```
   For DXT1: size = (width/4) × (height/4) × 8
   For DXT5: size = (width/4) × (height/4) × 16
   Add ~33% if mipmaps present
   ```
3. Compare to actual file size
4. Try opening in texture viewer

**Tools:**

- NVIDIA Texture Tools
- AMD Compressonator
- DDS Viewer (for Windows)

#### XMA Audio

1. Check RIFF chunk size in header (bytes 4-7)
2. Compare to actual file size
3. Try playing in audio tool

**Tools:**

- Xbox 360 Audio Tools
- VLC media player (may support XMA)
- FFmpeg (with XMA codec)

#### NIF Models

1. Check for clean "Gamebryo File Format" header
2. Look for reasonable block count
3. Try opening in NifSkope

**Tools:**

- NifSkope (best NIF viewer)
- Blender with NIF plugin

#### Scripts

1. Check for BEGIN/END pairs
2. Verify mostly printable ASCII
3. Look for complete syntax

**Completeness Indicators:**

```
✅ Good: BEGIN/END matched, clean syntax
⚠️  Partial: Has BEGIN but no END
❌ Bad: Non-printable characters, no structure
```

---

## Working with Partial Files

### Textures (DDS/DDX)

#### Truncated Textures

**If you have header + partial data:**

1. **Viewing Options:**

   - Some viewers render what's available
   - Top portion of image may be visible
   - Bottom portion will be black/corrupted

2. **Recovery Attempts:**

   ```python
   # Pad file to expected size with zeros
   expected_size = calculated_from_header
   actual_size = os.path.getsize(file)
   padding_needed = expected_size - actual_size

   with open(file, 'ab') as f:
       f.write(b'\x00' * padding_needed)
   ```

3. **Conversion:**
   - Convert partial texture to PNG/TGA
   - Save usable portion
   - Discard corrupted area

#### Xbox 360 Deswizzling

Xbox 360 textures are "swizzled" (tiled) for GPU efficiency.

**Deswizzling Tools:**

- Noesis (supports many game formats)
- Xbox 360 Texture Converter
- Custom Python scripts using texture libraries

**Basic Process:**

```python
# Pseudocode
def deswizzle_xbox360_dds(input_file, output_file):
    # Read DDS header
    width, height, format = parse_dds_header(input_file)

    # Read swizzled data
    swizzled_data = read_data(input_file)

    # Deswizzle based on block size
    linear_data = untile_texture(swizzled_data, width, height)

    # Write PC-format DDS
    write_dds(output_file, linear_data, width, height, format)
```

### Audio (XMA)

#### Truncated Audio

**Playback:**

- Most audio players skip unreadable portions
- You'll hear beginning of file until truncation point
- No audio beyond that point

**Conversion:**

```bash
# Convert XMA to WAV using FFmpeg
ffmpeg -i file.xma -t 30 output.wav
# -t limits duration (useful for truncated files)
```

**Recovery:**

- Truncated audio often still useful
- Voice lines may be complete even if file is truncated
- Background music may have usable loops

### Models (NIF/KF)

#### Partial Models

**NifSkope Behavior:**

- May load partial model
- Will error on corrupted blocks
- Some geometry may be visible

**Recovery Attempts:**

1. Open in NifSkope
2. Remove corrupted blocks
3. Save as new file
4. May preserve usable geometry

#### Animation Files (KF)

- Usually smaller, more likely to be complete
- Truncation less common
- If truncated, animation plays partially

### Scripts

#### Partial Scripts

**Useful Scenarios:**

- Header/metadata extraction (quest IDs, NPC refs)
- Learning script patterns
- Reconstructing logic

**Example Partial Script:**

```
ScriptName MyQuestScript

; Complete section
BEGIN GameMode
    if GetQuestCompleted MyQuest == 1
        ; Script continues...

; TRUNCATED - rest is missing or corrupted
```

**What You Can Do:**

- Extract quest references
- Understand script logic (even partial)
- Combine with other partial scripts
- May help with game research

---

## Integrity Checking

### Current Implementation

The tool's integrity checker validates files:

```python
result = integrity_checker.check_file(file_path, file_type)
# Returns: {'valid': bool, 'issues': [], 'info': {}}
```

### Interpretation

#### "Valid" Files

✅ **Meaning:** File passes basic validation

- Header is correct
- Size matches expectations
- No obvious corruption

#### "Invalid" Files

❌ **Meaning:** File has issues

- Common issues:
  - Size mismatch
  - Corrupted header
  - Missing data

**Note:** "Invalid" doesn't mean "useless"! Many "invalid" files are partially usable.

### Enhanced Validation (Future)

Planned improvements:

```python
result = {
    'completeness': 0.75,  # 75% complete
    'usability': 'partial',  # full, partial, header_only, corrupted
    'missing_bytes': 250000,
    'has_complete_header': True,
    'recoverable': True
}
```

---

## Best Practices

### 1. Don't Delete "Invalid" Files Immediately

- Review manually first
- Check if partially usable
- May contain valuable metadata

### 2. Organize by Completeness

```
output/
├── complete/          # Fully extracted, valid
├── partial_usable/    # Incomplete but useful
├── header_only/       # Metadata only
└── corrupted/         # Severely damaged
```

### 3. Document Findings

Keep notes on partial files:

```
partial_files.txt:
- dds_0045: 1024×1024 DXT5, only 30% data, but enough to see character face
- xma_0012: Music track, first 20 seconds usable for identification
- nif_0089: Corrupted but has valid UV coordinates in first 5 blocks
```

### 4. Cross-Reference Multiple Dumps

If you have multiple dumps:

- Same file may be complete in different dump
- Combine fragments from multiple sources
- Build more complete picture

### 5. Use Hex Editors

**Recommended Tools:**

- HxD (Windows)
- 010 Editor (cross-platform, has templates)
- ImHex (modern, cross-platform)

**What to Look For:**

- Valid signatures beyond first occurrence
- Repeating patterns (may indicate structure)
- Embedded strings/metadata

---

## Format-Specific Tips

### DDS/DDX Textures

**Key Offsets:**

- 0x00: "DDS " magic
- 0x04: Header size (should be 124)
- 0x0C: Height
- 0x10: Width
- 0x1C: Pitch/linear size
- 0x54: FourCC code
- 0x80: Pixel data starts

**Quick Hex Check:**

```
Good DDS:
00000000: 44 44 53 20 7C 00 00 00  07 10 00 00 00 04 00 00  |DDS |...........|
00000010: 00 04 00 00 00 00 04 00  00 00 00 00 0A 00 00 00  |................|
```

### XMA Audio

**Key Chunks:**

- "RIFF" + size + "WAVE"
- "XMA2" chunk (Xbox 360)
- "fmt " chunk (format details)
- "data" chunk (audio data)

**Quick Check:**

```bash
# Check if XMA2 chunk present
grep -ao "XMA2" file.xma
```

### NIF Models

**Key Sections:**

- Header: "Gamebryo File Format, Version X.X.X.X"
- Block type strings (at varying offset)
- Block data

**NifSkope Debug:**

1. Open file
2. Check "Block Details" panel
3. Look for read errors
4. Note which blocks are valid

---

## Recovery Tools

### Recommended Software

#### General

- **HxD** - Hex editor
- **010 Editor** - Hex editor with templates
- **Binwalk** - File carving tool

#### Textures

- **NVIDIA Texture Tools** - DDS conversion
- **AMD Compressonator** - Texture analysis
- **Noesis** - Multi-format viewer

#### Models

- **NifSkope** - NIF viewer/editor
- **Blender** - 3D modeling (with NIF plugin)

#### Audio

- **FFmpeg** - Audio conversion
- **Audacity** - Audio editing
- **VLC** - Media player

### Scripting Your Own Tools

Python libraries for file analysis:

```python
# DDS parsing
from struct import unpack

def parse_dds_header(data):
    magic = data[0:4]
    if magic != b'DDS ':
        return None

    header_size = unpack('<I', data[4:8])[0]
    height = unpack('<I', data[12:16])[0]
    width = unpack('<I', data[16:20])[0]

    return {'width': width, 'height': height}
```

---

## Conclusion

**Key Takeaways:**

1. ✅ **Incomplete files are normal** in RAM dumps
2. ✅ **Many partial files are still useful** for research/analysis
3. ✅ **"Invalid" ≠ "Useless"** - always inspect before deleting
4. ✅ **Multiple dumps = better recovery** - cross-reference when possible
5. ✅ **Document your findings** - partial data still has value

**Remember:** The goal is preservation and research. Even partial files provide insights into game development and cut content.

---

## Additional Resources

### Documentation

- `ANALYSIS_REPORT.md` - Technical implementation details
- `CHANGES.md` - Recent improvements
- `README.md` - Usage instructions
- `docs/TECHNICAL.md` - Format specifications

### External Resources

- **NifTools Wiki** - NIF format documentation
- **Microsoft DDS Documentation** - Texture format specs
- **Xbox 360 Development** - Console-specific formats
- **Gamebryo SDK Docs** - (if available) Engine documentation

---

## Support

If you discover new patterns in incomplete files or develop recovery techniques, please share your findings!
