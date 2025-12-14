"""
File signature definitions for carving various file types from memory dumps.
Supports Xbox 360 and PC formats including DDS textures, XMA audio, NIF models, and scripts.

For DDX texture conversion, see: https://github.com/kran27/DDXConv
"""

from typing import Dict, Union

# Type alias for signature info
SignatureInfo = Dict[str, Union[bytes, str, int]]
SignaturesDict = Dict[str, SignatureInfo]

# Common magic bytes
GAMEBRYO_MAGIC = b"Gamebryo File Format"
RIFF_MAGIC = b"RIFF"
TES4_MAGIC = b"TES4"
PNG_MAGIC = b"\x89PNG\r\n\x1a\n"

# Zlib compression headers (CMF + FLG)
# CMF 0x78 = deflate compression with 32K window
ZLIB_LOW = b"\x78\x01"  # No/low compression
ZLIB_DEFAULT = b"\x78\x9c"  # Default compression
ZLIB_BEST = b"\x78\xda"  # Best compression

# Xbox 360 DDX texture magic bytes (stored as little-endian in files)
DDX_3XDO_MAGIC = b"3XDO"  # 0x4F445833 - Standard DDX format
DDX_3XDR_MAGIC = b"3XDR"  # 0x52445833 - Engine-tiled DDX format (not fully supported by DDXConv)

# Xbox 360 specific formats
XEX2_MAGIC = b"XEX2"  # Xbox 360 executable
XDBF_MAGIC = b"XDBF"  # Xbox Dashboard File (achievements, title data)
XUIS_MAGIC = b"XUIS"  # Xbox UI Scene/Skin
XUIB_MAGIC = b"XUIB"  # Xbox UI Binary
PIRS_MAGIC = b"PIRS"  # Xbox LIVE signed content
CON_MAGIC = b"CON "  # Xbox LIVE content package

# File signatures for carving
FILE_SIGNATURES: SignaturesDict = {
    # Textures - all output to unified folders
    "dds": {"magic": b"DDS ", "extension": ".dds", "description": "DirectDraw Surface texture", "min_size": 128, "max_size": 50 * 1024 * 1024, "folder": "textures"},
    # Xbox 360 DDX textures - use DDXConv (https://github.com/kran27/DDXConv) for conversion to standard DDS
    # Both 3XDO and 3XDR formats output to unified 'ddx' folder (DDXConv converts them automatically)
    "ddx_3xdo": {"magic": DDX_3XDO_MAGIC, "extension": ".ddx", "description": "Xbox 360 DDX texture (3XDO format)", "min_size": 68, "max_size": 50 * 1024 * 1024, "folder": "ddx"},
    "ddx_3xdr": {
        "magic": DDX_3XDR_MAGIC,
        "extension": ".ddx",
        "description": "Xbox 360 DDX texture (3XDR engine-tiled format)",
        "min_size": 68,
        "max_size": 50 * 1024 * 1024,
        "folder": "ddx",
    },
    # 3D Models and Animations
    "nif": {"magic": GAMEBRYO_MAGIC, "extension": ".nif", "description": "NetImmerse/Gamebryo 3D model", "min_size": 100, "max_size": 20 * 1024 * 1024},
    "kf": {"magic": GAMEBRYO_MAGIC, "extension": ".kf", "description": "Gamebryo animation", "min_size": 100, "max_size": 10 * 1024 * 1024},
    "egm": {"magic": GAMEBRYO_MAGIC, "extension": ".egm", "description": "FaceGen Morph file", "min_size": 100, "max_size": 5 * 1024 * 1024},
    "egt": {"magic": GAMEBRYO_MAGIC, "extension": ".egt", "description": "FaceGen Tint file", "min_size": 100, "max_size": 1 * 1024 * 1024},
    # Audio
    # Xbox 360 uses XMA audio format (RIFF container with XMA2 or XMA fmt chunks)
    # Standard WAV files also use RIFF but are distinguished by fmt chunk format code
    "xma": {"magic": RIFF_MAGIC, "extension": ".xma", "description": "Xbox Media Audio (RIFF/XMA)", "min_size": 44, "max_size": 100 * 1024 * 1024, "folder": "audio"},
    "ogg": {"magic": b"OggS", "extension": ".ogg", "description": "Ogg Vorbis audio", "min_size": 58, "max_size": 50 * 1024 * 1024, "folder": "audio"},
    "lip": {"magic": b"LIPS", "extension": ".lip", "description": "Lip-sync animation", "min_size": 20, "max_size": 5 * 1024 * 1024},
    # Scripts - ObScript format (present in debug builds, stripped from release builds)
    # Uses "scn <name>" or "ScriptName <name>" header format
    # Both formats output to unified 'scripts' folder
    "script_scn": {"magic": b"scn ", "extension": ".txt", "description": "Bethesda ObScript (scn format)", "min_size": 20, "max_size": 100 * 1024, "folder": "scripts"},
    "script_sn": {
        "magic": b"ScriptName ",
        "extension": ".txt",
        "description": "Bethesda ObScript (ScriptName format)",
        "min_size": 20,
        "max_size": 100 * 1024,
        "folder": "scripts",
    },
    # Game Data Files
    "esp": {"magic": TES4_MAGIC, "extension": ".esp", "description": "Elder Scrolls Plugin", "min_size": 24, "max_size": 500 * 1024 * 1024},
    "esm": {"magic": TES4_MAGIC, "extension": ".esm", "description": "Elder Scrolls Master", "min_size": 24, "max_size": 500 * 1024 * 1024},
    "bsa": {"magic": b"BSA\x00", "extension": ".bsa", "description": "Bethesda Archive", "min_size": 36, "max_size": 2 * 1024 * 1024 * 1024},
    # Shaders
    "sdt": {"magic": b"SDAT", "extension": ".sdt", "description": "Shader Data", "min_size": 20, "max_size": 10 * 1024 * 1024},
    # Video
    "bik": {"magic": b"BIKi", "extension": ".bik", "description": "Bink Video", "min_size": 20, "max_size": 500 * 1024 * 1024},
    "tex": {"magic": b"TEXI", "extension": ".tex", "description": "Texture info", "min_size": 20, "max_size": 1 * 1024 * 1024},
    # Images
    "png": {"magic": PNG_MAGIC, "extension": ".png", "description": "PNG image", "min_size": 67, "max_size": 50 * 1024 * 1024},
    # Xbox 360 System Formats
    "xex": {"magic": XEX2_MAGIC, "extension": ".xex", "description": "Xbox 360 Executable", "min_size": 24, "max_size": 100 * 1024 * 1024},
    "xdbf": {"magic": XDBF_MAGIC, "extension": ".xdbf", "description": "Xbox Dashboard File (achievements/title data)", "min_size": 24, "max_size": 10 * 1024 * 1024},
    "xuis": {"magic": XUIS_MAGIC, "extension": ".xuis", "description": "Xbox UI Scene/Skin", "min_size": 16, "max_size": 10 * 1024 * 1024},
    "xuib": {"magic": XUIB_MAGIC, "extension": ".xuib", "description": "Xbox UI Binary", "min_size": 16, "max_size": 10 * 1024 * 1024},
    "zlib_default": {"magic": ZLIB_DEFAULT, "extension": ".zlib", "description": "Zlib compressed stream (default)", "min_size": 10, "max_size": 10 * 1024 * 1024},
    "zlib_best": {"magic": ZLIB_BEST, "extension": ".zlib", "description": "Zlib compressed stream (best)", "min_size": 10, "max_size": 10 * 1024 * 1024},
}

# Xbox 360 GPU texture formats (from DDXConv/Xenia)
# Used for identifying texture format in DDX headers
XBOX360_GPU_TEXTURE_FORMATS = {
    0x12: "DXT1",  # GPUTEXTUREFORMAT_DXT1
    0x13: "DXT3",  # GPUTEXTUREFORMAT_DXT2_3
    0x14: "DXT5",  # GPUTEXTUREFORMAT_DXT4_5
    0x52: "DXT1",  # DXT1 variant
    0x53: "DXT3",  # DXT3 variant
    0x54: "DXT5",  # DXT5 variant
    0x71: "ATI2",  # DXT5 variant for normal maps (ATI2/BC5)
    0x7B: "ATI1",  # ATI1/BC4 - Single channel (specular maps)
    0x82: "DXT1",  # DXT1 (default when format byte is 0)
    0x86: "DXT1",  # DXT1 variant
    0x88: "DXT5",  # DXT5 variant
}


def get_signature_info(file_type: str) -> SignatureInfo:
    """Get signature information for a specific file type."""
    return FILE_SIGNATURES.get(file_type, {})


def get_all_signatures() -> SignaturesDict:
    """Get all file signatures."""
    return FILE_SIGNATURES
