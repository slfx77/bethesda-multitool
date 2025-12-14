"""
Xbox 360 Memory Dump File Carver

A tool for extracting and analyzing data from Xbox 360 memory dumps,
particularly optimized for Fallout: New Vegas prototype builds.

Features:
- File carving (textures, audio, models, scripts)
"""

__version__ = "2.0.0"
__author__ = "Your Name"

from .carver import MemoryCarver
from .file_signatures import FILE_SIGNATURES, get_signature_info, get_all_signatures
from .report import ReportGenerator, ExtractionReport
from .minidump_extractor import MinidumpExtractor

__all__ = [
    # Core carving
    "MemoryCarver",
    "FILE_SIGNATURES",
    "get_signature_info",
    "get_all_signatures",
    # Reports
    "ReportGenerator",
    "ExtractionReport",
    # Minidump extraction
    "MinidumpExtractor",
]
