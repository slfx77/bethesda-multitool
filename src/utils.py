"""
Utility functions for file carving operations.
"""

import struct
import os
from typing import Optional


def read_uint32_le(data: bytes, offset: int = 0) -> int:
    """Read a 32-bit unsigned integer in little-endian format."""
    return struct.unpack_from("<I", data, offset)[0]


def read_uint32_be(data: bytes, offset: int = 0) -> int:
    """Read a 32-bit unsigned integer in big-endian format."""
    return struct.unpack_from(">I", data, offset)[0]


def read_uint16_le(data: bytes, offset: int = 0) -> int:
    """Read a 16-bit unsigned integer in little-endian format."""
    return struct.unpack_from("<H", data, offset)[0]


def read_uint16_be(data: bytes, offset: int = 0) -> int:
    """Read a 16-bit unsigned integer in big-endian format."""
    return struct.unpack_from(">H", data, offset)[0]


def is_printable_text(data: bytes, min_ratio: float = 0.8) -> bool:
    """
    Check if data contains mostly printable ASCII text.

    Args:
        data: Bytes to check
        min_ratio: Minimum ratio of printable characters (0.0-1.0)

    Returns:
        True if data appears to be text
    """
    if len(data) == 0:
        return False

    printable_count = sum(1 for b in data if 32 <= b < 127 or b in (9, 10, 13))
    return (printable_count / len(data)) >= min_ratio


def sanitize_filename(filename: str) -> str:
    """
    Sanitize filename by removing/replacing invalid characters.

    Args:
        filename: Original filename

    Returns:
        Sanitized filename safe for filesystem
    """
    invalid_chars = '<>:"|?*\\/\x00'
    for char in invalid_chars:
        filename = filename.replace(char, "_")
    return filename


def create_output_directory(base_path: str, dump_name: str) -> str:
    """
    Create output directory for carved files.

    Args:
        base_path: Base output directory
        dump_name: Name of the dump file being processed

    Returns:
        Path to created directory
    """
    output_dir = os.path.join(base_path, sanitize_filename(dump_name))
    os.makedirs(output_dir, exist_ok=True)
    return output_dir


def format_size(size_bytes: int) -> str:
    """
    Format byte size to human-readable string.

    Args:
        size_bytes: Size in bytes

    Returns:
        Formatted string (e.g., "1.5 MB")
    """
    size_float = float(size_bytes)
    for unit in ["B", "KB", "MB", "GB", "TB"]:
        if size_float < 1024.0:
            return f"{size_float:.2f} {unit}"
        size_float /= 1024.0
    return f"{size_float:.2f} PB"


def find_pattern(data: bytes, pattern: bytes, start: int = 0) -> int:
    """
    Find the next occurrence of a pattern in data.

    Args:
        data: Data to search in
        pattern: Pattern to search for
        start: Starting offset

    Returns:
        Offset of pattern or -1 if not found
    """
    try:
        return data.index(pattern, start)
    except ValueError:
        return -1


def extract_null_terminated_string(data: bytes, offset: int = 0, max_length: int = 256) -> Optional[str]:
    """
    Extract a null-terminated string from data.

    Args:
        data: Data containing string
        offset: Starting offset
        max_length: Maximum string length to extract

    Returns:
        Extracted string or None if invalid
    """
    try:
        end = data.index(b"\x00", offset, offset + max_length)
        string_bytes = data[offset:end]
        if is_printable_text(string_bytes, min_ratio=0.9):
            return string_bytes.decode("ascii", errors="ignore")
    except ValueError:
        pass
    return None


def align_offset(offset: int, alignment: int) -> int:
    """
    Align an offset to a specific boundary.

    Args:
        offset: Original offset
        alignment: Alignment boundary (e.g., 2048 for Xbox 360)

    Returns:
        Aligned offset
    """
    remainder = offset % alignment
    if remainder == 0:
        return offset
    return offset + (alignment - remainder)
