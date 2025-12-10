"""Xbox 360 Memory Dump File Carver package."""

__version__ = "1.1.0"
__author__ = "Your Name"
__description__ = "A tool for extracting files from Xbox 360 memory dumps"

from .carver import MemoryCarver
from .file_signatures import FILE_SIGNATURES, get_signature_info, get_all_signatures
from .string_extractor import StringExtractor, ExtractedString, StringExtractionResult
from .report import ReportGenerator, ExtractionReport

__all__ = [
    "MemoryCarver",
    "FILE_SIGNATURES",
    "get_signature_info",
    "get_all_signatures",
    "StringExtractor",
    "ExtractedString",
    "StringExtractionResult",
    "ReportGenerator",
    "ExtractionReport",
]
