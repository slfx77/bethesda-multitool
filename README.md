# Xbox 360 Memory Dump File Carver

A specialized tool for extracting (carving) files from Xbox 360 memory dumps. Originally developed for preserving data from Fallout: New Vegas prototype builds, this tool can be used with any Xbox 360 game memory dumps.

## Features

- **Multiple File Format Support**: Carves DDS textures, XMA audio, NIF models, Bethesda scripts, and more
- **Xbox 360 Optimized**: Handles Xbox 360-specific formats including big-endian byte order and swizzled textures
- **Memory Efficient**: Processes large dumps in chunks to prevent crashes and excessive memory usage
- **Progress Tracking**: Real-time progress bars and detailed logging
- **Configurable**: Control what file types to extract, chunk sizes, and output limits
- **Batch Processing**: Process multiple dump files at once

## Supported File Types

### Textures

| Type       | Extension | Description                                   |
| ---------- | --------- | --------------------------------------------- |
| `dds`      | .dds      | DirectDraw Surface textures (Xbox 360 and PC) |
| `ddx_3xdo` | .ddx      | Xbox 360 DDX textures (3XDO format)           |
| `ddx_3xdr` | .ddx      | Xbox 360 DDX textures (3XDR engine-tiled)     |
| `tex`      | .tex      | Texture info files                            |

> **Note**: DDX files are Xbox 360-specific compressed textures. Use [DDXConv](https://github.com/kran27/DDXConv) to convert them to standard DDS files.

### 3D Models & Animations

| Type  | Extension | Description              |
| ----- | --------- | ------------------------ |
| `nif` | .nif      | Gamebryo 3D models       |
| `kf`  | .kf       | Gamebryo animation files |
| `egm` | .egm      | FaceGen morph files      |
| `egt` | .egt      | FaceGen tint files       |

### Audio

| Type  | Extension | Description              |
| ----- | --------- | ------------------------ |
| `xma` | .xma      | Xbox Media Audio files   |
| `mp3` | .mp3      | MP3 audio files          |
| `ogg` | .ogg      | Ogg Vorbis audio files   |
| `wav` | .wav      | WAV audio files          |
| `lip` | .lip      | Lip-sync animation files |

### Scripts

| Type                | Extension | Description                               |
| ------------------- | --------- | ----------------------------------------- |
| `script_begin`      | .txt      | Bethesda script files (scn format)        |
| `script_scriptname` | .txt      | Bethesda script files (ScriptName format) |

### Game Data

| Type  | Extension | Description                |
| ----- | --------- | -------------------------- |
| `esp` | .esp      | Elder Scrolls Plugin files |
| `esm` | .esm      | Elder Scrolls Master files |
| `bsa` | .bsa      | Bethesda Archive files     |

### Other

| Type  | Extension | Description       |
| ----- | --------- | ----------------- |
| `sdt` | .sdt      | Shader data files |
| `bik` | .bik      | Bink video files  |

## Related Tools

- **[DDXConv](https://github.com/kran27/DDXConv)** - Converts Xbox 360 .ddx textures to standard .dds files usable in PC games
- **[Save Image Extractor](https://gist.github.com/kran27/291349139c769173a0f02e1eb2462975)** - Extracts screenshots from Fallout 3/NV save files

## Installation

### Requirements

- Python 3.8 or higher
- pip (Python package manager)

### Setup

1. Clone or download this repository
2. Navigate to the project directory
3. Install dependencies:

```bash
pip install -r requirements.txt
```

## Usage

### Basic Usage

Carve all file types from a single dump:

```bash
python main.py path/to/Fallout_Debug.xex.dmp
```

### Process All Dumps in Directory

```bash
python main.py Sample/
```

### Analyze Player State (Quests, Locations, etc.)

```bash
python main.py --analyze Sample/Fallout_Release_Beta.xex.dmp
```

### Carve Specific File Types

Extract only DDS textures:

```bash
python main.py Fallout_Release_Beta.xex.dmp --types dds
```

Extract DDS textures and XMA audio:

```bash
python main.py Fallout_Release_Beta.xex.dmp --types dds xma
```

### Custom Output Directory

```bash
python main.py Sample/ --output ./extracted_files
```

### Advanced Options

```bash
python main.py Fallout_Debug.xex.dmp \
  --types dds xma nif \
  --output ./output \
  --chunk-size 20 \
  --max-files 5000 \
  --verbose
```

### Command-Line Options

| Option         | Description                                    | Default   |
| -------------- | ---------------------------------------------- | --------- |
| `path`         | Path to .dmp file or directory of .dmp files   | -         |
| `--analyze`    | Run player state analysis instead of carving   | False     |
| `--types`      | Specific file types to carve (space-separated) | All types |
| `--output`     | Output directory for carved files              | ./output  |
| `--no-strings` | Skip string extraction                         | False     |
| `--no-modules` | Skip PE module extraction                      | False     |
| `--chunk-size` | Chunk size in MB for processing                | 10        |
| `--max-files`  | Maximum files to carve per type                | 10000     |
| `--verbose`    | Enable verbose logging                         | False     |
| `--version`    | Show version information                       | -         |

## Memory Snapshot Analysis (Preservation)

In addition to file carving, the repo includes analysis scripts that scan minidump memory ranges for _evidence-backed_ gameplay clues (quests, locations/cells, faction reputation strings, etc.).

Analyze one dump:

```bash
python main.py --analyze Sample/Fallout_Release_Beta.xex24.dmp
```

Or use the analyzer directly:

```bash
python -m src.player_state_analyzer Sample/Fallout_Release_Beta.xex24.dmp
```

Batch analyze all dumps in `Sample/`:

```bash
python -m src.batch_player_state --input Sample --output results/player_state_batch
```

## Output Structure

Carved files are organized by dump file name:

```
output/
├── Fallout_Debug.xex/
│   ├── dds_0001_off_00123456.dds
│   ├── dds_0002_off_00234567.dds
│   ├── xma_0001_off_00345678.xma
│   └── ...
├── Fallout_Release_Beta.xex/
│   ├── dds_0001_off_00456789.dds
│   └── ...
└── ...
```

Filenames include:

- File type
- Sequential number
- Hexadecimal offset in the dump file

## Technical Details

### Xbox 360 Considerations

Xbox 360 uses big-endian byte ordering, which differs from PC's little-endian. This tool automatically detects and handles both:

- **DDS Textures**: Checks header values in both endianness modes
- **Texture Formats**: Recognizes Xbox 360-specific formats (DXT1, DXT3, DXT5, ATI2/BC5)
- **Memory Alignment**: Considers Xbox 360's 2048-byte alignment

### Memory Efficiency

The tool processes dumps in configurable chunks (default 10MB) with overlap to catch signatures at chunk boundaries. This prevents:

- Out of memory errors on large dumps
- System crashes
- IDE freezes

### False Positives

Memory dumps may contain partial or corrupted data. The tool:

- Validates file headers before extraction
- Enforces min/max file sizes
- Limits extraction count per type (configurable)

Some carved files may be incomplete or corrupted - this is normal for memory dumps.

## Viewing Carved Files

### DDS Textures

Xbox 360 DDS files may need conversion for PC viewing:

- **Recommended**: [Texconv](https://github.com/Microsoft/DirectXTex/wiki/Texconv) (DirectXTex)
- **Alternative**: [Paint.NET](https://www.getpaint.net/) with DDS plugin
- **GIMP**: With DDS plugin

### XMA Audio

Xbox 360 XMA files can be converted using:

- [xma_test](https://github.com/kode54/foo_input_xbox) (foobar2000 component)
- [towav](https://github.com/xenia-project/xenia/tree/master/tools)

### NIF Models

- [NifSkope](https://github.com/niftools/nifskope) - View and edit NIF files

## Troubleshooting

### "No files carved"

- Memory dumps may not contain complete file headers
- Try carving different file types with `--types`
- Use `--verbose` to see detailed debug information

### "Process hangs or crashes"

- Reduce `--chunk-size` (try 5 or 2 MB)
- Reduce `--max-files` to stop earlier
- Process one dump at a time instead of `--all`

### "Carved files are corrupted"

- This is expected for memory dumps - not all data will be intact
- Xbox 360 textures may need special tools to view correctly
- Try different file types - some may be more intact than others

## Contributing

Contributions are welcome! Areas for improvement:

- Additional file format support
- Better file size estimation algorithms
- Xbox 360 texture deswizzling
- Automated file validation

## License

MIT License - See LICENSE file for details

## Acknowledgments

- Developed for preserving Fallout: New Vegas prototype builds
- Xbox 360 format information from various modding communities
- Inspired by general-purpose file carving tools like Foremost and Scalpel

## Disclaimer

This tool is for data preservation and research purposes. Ensure you have the legal right to access and process the memory dumps you're working with.
