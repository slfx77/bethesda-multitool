# Xbox 360 Memory Dump File Carver - Technical Documentation

## Architecture Overview

The tool is organized into modular components for maintainability and extensibility:

```
xbox360-memory-carver/
├── src/
│   ├── __init__.py          # Package initialization
│   ├── file_signatures.py   # File signature definitions
│   ├── parsers.py           # Format-specific header parsers
│   ├── carver.py            # Main carving engine
│   └── utils.py             # Utility functions
├── main.py                  # CLI entry point
├── docs/                    # Documentation
├── output/                  # Default output directory
└── requirements.txt         # Python dependencies
```

## Component Details

### file_signatures.py

Defines signatures (magic bytes) for all supported file formats. Each signature includes:

- `magic`: Byte pattern to search for
- `extension`: File extension for carved files
- `description`: Human-readable format description
- `min_size` / `max_size`: Size bounds for validation

### parsers.py

Format-specific parsers that extract header information:

- **DDSParser**: Handles DDS texture headers with Xbox 360 (big-endian) and PC (little-endian) support
- **XMAParser**: Parses RIFF/XMA audio headers
- **NIFParser**: Extracts NetImmerse/Gamebryo model headers
- **ScriptParser**: Identifies Bethesda script boundaries

### carver.py

The main `MemoryCarver` class implements chunked processing:

1. **Chunked Reading**: Reads dump in configurable chunks (default 10MB)
2. **Overlap Handling**: Uses 2KB overlap to catch signatures at boundaries
3. **Signature Search**: Scans each chunk for all enabled file signatures
4. **Header Parsing**: Validates and determines file sizes
5. **Extraction**: Writes carved files to organized output directories
6. **Statistics**: Tracks and reports carved file counts

### utils.py

Helper functions for:

- Endianness conversion (little/big endian reads)
- Text detection (ASCII printability checks)
- Filename sanitization
- Size formatting
- Pattern searching

## Memory Efficiency

### Problem: Large Dumps Crash System

Memory dumps can be hundreds of MB to several GB. Loading entirely into memory causes:

- Out of memory errors
- System freezes
- IDE crashes

### Solution: Chunked Processing

```python
chunk_size = 10 * 1024 * 1024  # 10MB chunks
overlap_size = 2048             # Overlap for boundary signatures

while offset < file_size:
    # Read chunk with overlap
    chunk = file.read(chunk_size + overlap_size)

    # Process chunk
    search_signatures_in_chunk(chunk, offset)

    # Move to next chunk
    offset += chunk_size
```

Benefits:

- Constant memory usage regardless of dump size
- Prevents crashes
- Allows processing multi-GB dumps
- Configurable chunk size for different systems

### Overlap Handling

Without overlap, signatures at chunk boundaries would be missed:

```
Chunk 1: [...data... DDS ]
Chunk 2: [ header data...]
         ^
         Signature split across boundary!
```

With 2KB overlap:

```
Chunk 1: [...data... DDS header...]
Chunk 2: [DS header... data...]
         ^
         Already processed, skip in overlap region
```

## File Size Determination

Accurate file size determination is critical for clean extraction:

### DDS Textures

Calculate from header information:

```python
size = width * height * bytes_per_pixel

# Adjust for compression
if format == 'DXT1':
    bytes_per_pixel = 0.5
elif format in ('DXT3', 'DXT5'):
    bytes_per_pixel = 1

# Add mipmaps (~33% more data)
if mipmap_count > 1:
    size *= 1.33
```

### XMA Audio

Size stored in RIFF header:

```python
# RIFF header format:
# 'RIFF' (4 bytes)
# file_size - 8 (4 bytes, little-endian)
# 'WAVE' or 'XMA2' (4 bytes)

file_size = read_uint32_le(data, 4) + 8
```

### Scripts and Text

Scan until non-printable data:

```python
while is_printable_text(data[offset:offset+100]):
    offset += 100
return offset - start_offset
```

### Conservative Fallbacks

For formats with unreliable size info, use minimums to avoid extracting garbage:

```python
return sig_info.get('min_size', 1024)
```

## Xbox 360 Specifics

### Endianness

Xbox 360 uses big-endian, PC uses little-endian. The tool:

1. Tries little-endian first (most common)
2. Validates values (dimensions < 16384, header_size == 124)
3. If invalid, tries big-endian
4. Picks interpretation with valid values

### DDS Texture Formats

Xbox 360 uses standard DXT compression but with big-endian headers:

| Format   | FourCC     | Bytes per Pixel |
| -------- | ---------- | --------------- |
| DXT1     | 0x44585431 | 0.5             |
| DXT3     | 0x44585433 | 1.0             |
| DXT5     | 0x44585435 | 1.0             |
| ATI2/BC5 | 0x41544932 | 1.0             |

### Swizzled Textures

Xbox 360 textures are often "swizzled" (reordered) for GPU efficiency. This tool extracts the raw data; deswizzling requires additional processing.

## Performance Considerations

### Chunk Size Tuning

| Chunk Size | Pros               | Cons                        |
| ---------- | ------------------ | --------------------------- |
| 1-5 MB     | Lower memory usage | More I/O operations, slower |
| 10-20 MB   | Good balance       | Default recommended         |
| 50+ MB     | Faster processing  | Higher memory usage         |

### Max Files Limit

Prevents runaway extraction from false positives:

```python
max_files_per_type = 10000  # Stop after 10k files per type
```

Adjust based on:

- Expected file counts
- Storage space
- Processing time tolerance

### I/O Optimization

- Uses buffered file reading
- Seeks only when necessary
- Writes files atomically

## Extensibility

### Adding New File Formats

1. **Add signature** to `file_signatures.py`:

```python
'new_format': {
    'magic': b'MAGIC',
    'extension': '.ext',
    'description': 'New Format',
    'min_size': 100,
    'max_size': 10 * 1024 * 1024
}
```

2. **Create parser** in `parsers.py`:

```python
class NewFormatParser:
    @staticmethod
    def parse_header(data: bytes, offset: int = 0) -> Optional[Dict[str, Any]]:
        # Parse header, return size info
        pass
```

3. **Register parser** in `carver.py`:

```python
self.parsers = {
    'new_format': NewFormatParser(),
    # ... other parsers
}
```

4. **Add size logic** to `_determine_file_size()` in `carver.py`

### Future Improvements

- **Parallel Processing**: Process multiple dumps simultaneously
- **File Validation**: Verify carved files with format validators
- **Deswizzling**: Automatic Xbox 360 texture deswizzling
- **Database**: Store metadata about carved files
- **GUI**: Visual interface for non-technical users
- **Recovery**: Handle partially corrupted file headers

## Testing

### Unit Tests

Test individual components:

```bash
python -m pytest tests/
```

### Integration Tests

Test on sample dumps:

```bash
python main.py test_dumps/small_sample.dmp --verbose
```

### Validation

Check carved files:

```bash
# DDS files
texconv carved_dds/*.dds

# XMA files
xma_test carved_xma/*.xma

# NIF files
nifskope carved_nif/*.nif
```

## Troubleshooting Development

### Import Errors

Ensure you're running from project root:

```bash
python main.py  # Correct
python src/carver.py  # Wrong - import issues
```

### Debugging

Enable verbose mode:

```bash
python main.py dump.dmp --verbose
```

Check log file:

```bash
tail -f carver.log
```

### Performance Profiling

```python
import cProfile
cProfile.run('carver.carve_dump("dump.dmp")', 'profile.stats')

# Analyze
python -m pstats profile.stats
```

## Contributing Guidelines

1. Follow PEP 8 style guidelines
2. Add docstrings to all functions/classes
3. Include type hints where possible
4. Test with sample dumps before committing
5. Update documentation for new features
6. Keep commits atomic and well-described

## License

MIT License - See LICENSE file for details
