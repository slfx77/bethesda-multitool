# Quick Start Guide

Get started with the Xbox 360 Memory Dump File Carver in minutes!

## Installation

1. **Install Python** (if not already installed)

   - Download from [python.org](https://www.python.org/downloads/)
   - Requires Python 3.8 or higher

2. **Install Dependencies**
   ```bash
   cd xbox360-memory-carver
   pip install -r requirements.txt
   ```

## Basic Usage

### Carve a Single Dump

```bash
python main.py path/to/your_dump.dmp
```

Files will be extracted to `./output/your_dump/`

### Carve All Dumps in Current Directory

```bash
python main.py --all
```

### Carve Only Textures (DDS)

```bash
python main.py your_dump.dmp --types dds
```

### Carve Multiple Specific Types

```bash
python main.py your_dump.dmp --types dds xma nif
```

## Understanding Output

### Output Directory Structure

```
output/
└── your_dump/
    ├── dds_0001_off_00123456.dds
    ├── dds_0002_off_00234567.dds
    ├── xma_0001_off_00345678.xma
    └── ...
```

### Filename Format

`{type}_{number}_off_{hex_offset}.{extension}`

- **type**: File type (dds, xma, nif, etc.)
- **number**: Sequential number (0001, 0002, ...)
- **hex_offset**: Location in dump file (hexadecimal)
- **extension**: File extension

Example: `dds_0042_off_00AB12CD.dds`

- 42nd DDS texture found
- Located at offset 0xAB12CD in the dump

## Viewing Carved Files

### DDS Textures

**Windows:**

- Install [Paint.NET](https://www.getpaint.net/) with DDS plugin
- Or use [DirectXTex texconv](https://github.com/Microsoft/DirectXTex/releases)

**Convert to PNG:**

```bash
texconv -ft png *.dds
```

**Linux/Mac:**

- Use [GIMP](https://www.gimp.org/) with DDS plugin

### XMA Audio

Xbox 360 audio needs conversion:

```bash
# Using xma_test (recommended)
xma_test input.xma output.wav

# Using FFmpeg (if supported)
ffmpeg -i input.xma output.wav
```

### NIF Models

- Download [NifSkope](https://github.com/niftools/nifskope/releases)
- Open .nif files directly

## Common Issues

### "No module named 'tqdm'"

Install dependencies:

```bash
pip install -r requirements.txt
```

### "Permission denied"

Run as administrator (Windows) or with sudo (Linux/Mac):

```bash
sudo python main.py your_dump.dmp
```

### "Out of memory"

Reduce chunk size:

```bash
python main.py your_dump.dmp --chunk-size 5
```

### Process seems stuck

Use verbose mode to see progress:

```bash
python main.py your_dump.dmp --verbose
```

### Too many false positives

Limit extraction count:

```bash
python main.py your_dump.dmp --max-files 1000
```

## Tips

### 1. Start Small

Test with one dump and one file type:

```bash
python main.py test.dmp --types dds --max-files 100
```

### 2. Check Logs

View detailed log file:

```bash
type carver.log     # Windows
cat carver.log      # Linux/Mac
```

### 3. Batch Processing

Process multiple dumps overnight:

```bash
python main.py --all --verbose > output.log 2>&1
```

### 4. Focus on What You Need

Only carve file types you're interested in:

```bash
# Just textures
python main.py dump.dmp --types dds

# Just audio
python main.py dump.dmp --types xma ogg wav mp3

# Just game data
python main.py dump.dmp --types esp bsa script_begin script_scriptname
```

## Next Steps

- Read the full [README.md](../README.md) for all options
- Check [TECHNICAL.md](TECHNICAL.md) for architecture details
- Adjust settings in [config.ini](../config.ini)

## Getting Help

If you encounter issues:

1. Check the log file (`carver.log`)
2. Try with `--verbose` flag
3. Reduce `--chunk-size` and `--max-files`
4. Test with a smaller dump file first

## Example Workflow

```bash
# 1. Install dependencies
pip install -r requirements.txt

# 2. Test with one dump, one type
python main.py Fallout_Debug.xex.dmp --types dds --max-files 50 --verbose

# 3. Check output
cd output/Fallout_Debug.xex
dir  # or 'ls' on Linux/Mac

# 4. If successful, process all dumps
cd ../..
python main.py --all

# 5. View results
cd output
dir  # or 'ls' on Linux/Mac
```

## Performance Guide

### Small dumps (<100 MB)

```bash
python main.py dump.dmp --chunk-size 20
```

### Medium dumps (100-500 MB)

```bash
python main.py dump.dmp --chunk-size 10
```

### Large dumps (>500 MB)

```bash
python main.py dump.dmp --chunk-size 5
```

### Huge dumps (>2 GB)

```bash
python main.py dump.dmp --chunk-size 2 --max-files 5000
```

Adjust based on your system's RAM!
