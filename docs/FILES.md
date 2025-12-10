# Project File Overview

This document explains what each file in the project does.

## Core Files

### `main.py`

The main entry point for the application. Run this to carve files from dumps.

**Usage:**

```bash
python main.py your_dump.dmp
python main.py --help  # See all options
```

### `requirements.txt`

Lists Python package dependencies. Install with:

```bash
pip install -r requirements.txt
```

### `config.ini`

Configuration file for adjusting carver settings (optional - command-line args override these).

### `test_installation.py`

Validates that the installation is working correctly. Run with:

```bash
python test_installation.py
```

## Source Code (`src/`)

### `src/__init__.py`

Package initialization file that exports main classes.

### `src/file_signatures.py`

Defines magic byte signatures for all supported file formats:

- DDS textures
- XMA audio
- NIF models
- Scripts
- Archives
- etc.

### `src/parsers.py`

Format-specific parsers that read file headers:

- `DDSParser` - Parses DDS texture headers (PC and Xbox 360)
- `XMAParser` - Parses XMA audio headers
- `NIFParser` - Parses NIF model headers
- `ScriptParser` - Identifies script boundaries

### `src/utils.py`

Utility functions used throughout the codebase:

- Byte order conversion (little/big endian)
- Text detection
- Filename sanitization
- Size formatting
- Pattern searching

### `src/carver.py`

The main carving engine (`MemoryCarver` class):

- Chunked file processing to prevent crashes
- Signature scanning
- File extraction
- Statistics tracking

## Documentation (`docs/`)

### `docs/QUICKSTART.md`

Quick start guide for new users. Start here!

### `docs/TECHNICAL.md`

Detailed technical documentation:

- Architecture overview
- Algorithm explanations
- Xbox 360 specifics
- Performance tuning
- How to extend the tool

## Other Files

### `LICENSE`

MIT License for the project.

### `.gitignore`

Tells git which files to ignore (e.g., outputs, logs, cache files).

### `README.md`

Main project README with comprehensive documentation.

## Output Directory (`output/`)

Where carved files are saved, organized by dump name:

```
output/
├── Fallout_Debug.xex/
│   ├── dds_0001_off_00123456.dds
│   ├── xma_0001_off_00234567.xma
│   └── ...
└── Fallout_Release_Beta.xex/
    └── ...
```

## Quick Reference

| Want to...             | Do this...                                   |
| ---------------------- | -------------------------------------------- |
| Start using the tool   | Read `docs/QUICKSTART.md`                    |
| Run the carver         | `python main.py dump.dmp`                    |
| See all options        | `python main.py --help`                      |
| Test installation      | `python test_installation.py`                |
| Understand internals   | Read `docs/TECHNICAL.md`                     |
| Modify file signatures | Edit `src/file_signatures.py`                |
| Add new file format    | Edit all `src/*.py` files (see TECHNICAL.md) |
| Change settings        | Use command-line args or edit `config.ini`   |
| Report issues          | Check log file `carver.log`                  |
