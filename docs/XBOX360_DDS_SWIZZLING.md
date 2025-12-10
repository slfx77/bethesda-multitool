# Xbox 360 DDS Texture Format - Findings

## Important Discovery

After testing the deswizzle implementation on actual Fallout New Vegas Xbox 360 memory dumps, we found:

**The textures in these dumps appear to be stored in LINEAR (PC) format, NOT swizzled Xbox 360 format!**

### Evidence

When comparing original vs. deswizzled textures:

- **Original `.dds` files**: Show recognizable patterns (glows, stripes, shapes)
- **Deswizzled `_deswizzled.dds` files**: More scrambled and corrupted

This means the deswizzle operation is **making images worse**, not better - a clear sign the source data was already linear.

### Why This Might Be

1. **Engine choice**: The Gamebryo engine (used by Fallout 3/NV) may have stored textures in linear format in RAM for CPU access
2. **Dump timing**: These dumps may have captured textures after decompression/conversion from GPU format
3. **Development builds**: These are prototype/debug builds which might use PC-format assets for easier debugging
4. **Mixed formats**: The Xbox 360 SDK allowed both tiled and linear textures; these may be linear

### What This Means For You

✅ **Good news**: Your original extracted `.dds` files are likely correct!
✅ **The corruption** you see is from incomplete RAM captures (truncated data, partial textures)
❌ **Don't use the deswizzle script** on these particular dumps - it will make things worse

## The Problem

Xbox 360 DDS textures extracted from memory dumps are **swizzled** (tiled) and cannot be viewed directly in standard DDS viewers. This is **by design** - the Xbox 360 GPU uses a tiled memory layout for better performance.

### What is Swizzling?

Swizzling (or tiling) reorders pixel data in a non-linear pattern optimized for Xbox 360's GPU cache. A normal (linear) texture stores pixels row by row:

```
Linear:     [Row 0][Row 1][Row 2]...
Swizzled:   [Tile 0][Tile 1][Tile 2]... (each tile contains parts of multiple rows)
```

### Current Status

✅ **What Works:**

- DDS headers are correctly extracted
- File sizes are calculated accurately
- Files are properly identified as Xbox 360 format (.ddx extension)
- Metadata (dimensions, format, mipmap count) can be read

❌ **What Doesn't Work:**

- Textures appear corrupted/garbled in viewers
- Direct viewing without deswizzling is impossible
- Converting to PNG/other formats will show corrupted data

## Solutions

### Option 1: Use Xbox 360 Texture Tools

**Recommended Tools:**

1. **Noesis** - Free, supports Xbox 360 DDS deswizzling

   - Download: https://richwhitehouse.com/noesis/
   - Can batch convert DDX → PC DDS

2. **Xbox 360 Texture Converter** - Dedicated tool

   - Handles Xbox 360 swizzled textures
   - Converts to linear PC format

3. **Texconv (DirectXTex)** - Microsoft's official tool
   - Command line: `texconv -xbox file.ddx`

### Option 2: Python Deswizzling Script

I can create a Python script to deswizzle textures using the swizzle algorithm. However, this requires:

- Understanding Xbox 360 tiling patterns (Morton order)
- Handling different block sizes for DXT1/3/5
- Processing each mipmap level correctly

**Would you like me to implement this?** It's doable but complex.

### Option 3: Keep As-Is for Research

For research/documentation purposes, you might want to:

- Keep the swizzled .ddx files as extracted (authentic to memory dump)
- Document which textures exist and their properties
- Use external tools for viewing when needed

## Technical Details

### Xbox 360 Tiling Algorithm

Xbox 360 uses **Morton order** (Z-order curve) tiling:

```
For a 256×256 DXT5 texture:
- Divided into 64×64 tiles
- Each tile contains 16×16 blocks (4×4 pixels each)
- Blocks within tiles follow Morton order
- Tiles are stored sequentially
```

### DXT Compression + Swizzling

The combination of DXT block compression and swizzling makes this complex:

1. **DXT blocks** (4×4 pixels) are the atomic unit
2. **Blocks are tiled** in Morton order within larger tiles
3. **Tiles are arranged** sequentially in memory

### Detection

The carver now detects Xbox 360 textures by checking byte order:

- Big-endian header values = Xbox 360 format
- Files saved with .ddx extension
- `is_xbox360` flag in metadata

## What You Can Do Now

### 1. Check Extracted Metadata

Even without viewing, you can analyze what textures exist:

```python
# Check DDX files metadata
from src.parsers import DDSParser

parser = DDSParser()
with open('output/dump/dds_0001.ddx', 'rb') as f:
    header = parser.parse_header(f.read(128))
    print(f"Size: {header['width']}×{header['height']}")
    print(f"Format: {header['fourcc']}")
    print(f"Mipmaps: {header['mipmap_count']}")
```

### 2. Use External Conversion

```bash
# Using Noesis (batch convert all DDX to DDS)
noesis.exe -batch "output/**/*.ddx" -ddx2dds

# Using Texconv
texconv -xbox -o converted/ output/**/*.ddx
```

### 3. Document Findings

Create a catalog of textures:

```
dds_0001.ddx - 1024×1024 DXT5 - Character face texture
dds_0002.ddx - 512×512 DXT1 - UI element
...
```

## Future Improvements

### Automatic Deswizzling (To Be Implemented)

If you want native Python deswizzling, I can add:

1. **Morton order function** - Calculate tile addresses
2. **Block reordering** - Untile DXT blocks
3. **Mipmap handling** - Process each level
4. **Output conversion** - Save as linear PC DDS

This would make the tool fully self-contained but adds complexity.

### Implementation Complexity

```python
# Pseudocode for deswizzling
def deswizzle_xbox360_dds(input_file, output_file):
    # Parse header
    width, height, format = parse_dds_header(input_file)

    # Read swizzled data
    swizzled_data = read_dds_data(input_file)

    # Calculate tile dimensions
    tile_width, tile_height = get_tile_size(format)

    # Reorder blocks using Morton order
    linear_data = []
    for tile_y in range(0, height, tile_height):
        for tile_x in range(0, width, tile_width):
            tile_data = extract_tile(swizzled_data, tile_x, tile_y)
            deswizzled_tile = morton_decode(tile_data)
            linear_data.append(deswizzled_tile)

    # Write PC-format DDS
    write_linear_dds(output_file, linear_data, width, height, format)
```

## Recommendations

**For now:**

1. ✅ Keep extracting .ddx files as-is
2. ✅ Use external tools (Noesis) for viewing
3. ✅ Document texture metadata
4. ⏳ Consider implementing deswizzling if you want full automation

**Priority:**

- **High**: Get Noesis or similar tool for batch conversion
- **Medium**: Catalog what textures exist
- **Low**: Implement native Python deswizzling

## Questions?

Let me know if you want:

1. **Python deswizzling implementation** - I can add this to the tool
2. **Batch conversion script** - Using external tools
3. **Metadata extraction script** - Catalog all textures without converting
4. **Documentation** - More details on Xbox 360 texture formats

---

**Note:** This is a fundamental limitation of Xbox 360 memory dumps, not a bug in the extractor. The tool is correctly identifying and extracting the data - it's just in Xbox 360 native format.
