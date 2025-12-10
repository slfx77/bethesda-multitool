"""
Report Generator for Xbox 360 Memory Dump Extraction

Generates a comprehensive extraction report summarizing all carved files,
extracted strings, and coverage statistics.
"""

import json
import logging
from dataclasses import dataclass, field, asdict
from datetime import datetime
from pathlib import Path
from typing import Any, Dict, List, Optional

logger = logging.getLogger(__name__)


@dataclass
class FileTypeStats:
    """Statistics for a single file type."""

    count: int = 0
    total_bytes: int = 0
    files: List[str] = field(default_factory=list)


@dataclass
class ExtractionReport:
    """Complete extraction report."""

    # Metadata
    dump_file: str = ""
    dump_size: int = 0
    extraction_time: str = ""
    version: str = "1.0.0"

    # File carving results
    total_files_carved: int = 0
    total_bytes_carved: int = 0
    files_by_type: Dict[str, FileTypeStats] = field(default_factory=dict)

    # String extraction results
    total_strings: int = 0
    strings_excluded_carved: int = 0
    strings_by_category: Dict[str, int] = field(default_factory=dict)
    strings_by_encoding: Dict[str, int] = field(default_factory=dict)

    # Coverage
    coverage_percent: float = 0.0
    identified_bytes: int = 0
    unknown_bytes: int = 0


class ReportGenerator:
    """Generate extraction reports."""

    # Human-readable names for file types
    TYPE_NAMES = {
        "dds": "DDS Textures",
        "ddx_3xdo": "Xbox 360 Textures (3XDO)",
        "ddx_3xdr": "Xbox 360 Textures (3XDR)",
        "xma": "XMA Audio",
        "nif": "NIF Models",
        "kf": "KF Animations",
        "egm": "EGM Morph Data",
        "egt": "EGT Texture Data",
        "lip": "LIP Lip-Sync",
        "bik": "Bink Video",
        "script_scn": "Scripts (SCN)",
        "script_sn": "Scripts (SN)",
        "png": "PNG Images",
        "wav": "WAV Audio",
        "zlib_default": "Zlib Streams (Default)",
        "zlib_best": "Zlib Streams (Best)",
        "xex": "Xbox Executables",
        "xdbf": "Xbox Data Files",
        "xuis": "XUI Skins",
        "xuib": "XUI Binary",
        "pirs": "PIRS Packages",
        "con": "CON Packages",
        "esm": "ESM Master Files",
        "esp": "ESP Plugin Files",
    }

    def __init__(self, output_dir: Path) -> None:
        self.output_dir = output_dir
        self.report = ExtractionReport()

    def set_dump_info(self, dump_path: str, dump_size: int) -> None:
        """Set basic dump information."""
        self.report.dump_file = Path(dump_path).name
        self.report.dump_size = dump_size
        self.report.extraction_time = datetime.now().isoformat()

    def add_carved_files(self, manifest_entries: List[Dict[str, Any]]) -> None:
        """Add carved file statistics from manifest entries."""
        for entry in manifest_entries:
            file_type = entry.get("file_type", "unknown")
            size = entry.get("size_in_dump", 0)
            filename = entry.get("filename", "")

            if file_type not in self.report.files_by_type:
                self.report.files_by_type[file_type] = FileTypeStats()

            stats = self.report.files_by_type[file_type]
            stats.count += 1
            stats.total_bytes += size
            stats.files.append(filename)

            self.report.total_files_carved += 1
            self.report.total_bytes_carved += size

    def add_string_stats(
        self,
        total: int,
        excluded: int,
        by_category: Dict[str, int],
        by_encoding: Dict[str, int],
    ) -> None:
        """Add string extraction statistics."""
        self.report.total_strings = total
        self.report.strings_excluded_carved = excluded
        self.report.strings_by_category = by_category
        self.report.strings_by_encoding = by_encoding

    def calculate_coverage(self) -> None:
        """Calculate coverage statistics."""
        if self.report.dump_size > 0:
            self.report.identified_bytes = self.report.total_bytes_carved
            self.report.unknown_bytes = self.report.dump_size - self.report.identified_bytes
            self.report.coverage_percent = self.report.identified_bytes / self.report.dump_size * 100

    def generate_text_report(self) -> str:
        """Generate a human-readable text report."""
        lines: List[str] = []

        # Header
        lines.append("=" * 70)
        lines.append("XBOX 360 MEMORY DUMP EXTRACTION REPORT")
        lines.append("=" * 70)
        lines.append("")
        lines.append(f"Dump File:    {self.report.dump_file}")
        lines.append(f"Dump Size:    {self._format_size(self.report.dump_size)}")
        lines.append(f"Extracted:    {self.report.extraction_time}")
        lines.append("")

        # Carved Files Summary
        lines.append("-" * 70)
        lines.append("CARVED FILES")
        lines.append("-" * 70)
        lines.append(f"Total Files:  {self.report.total_files_carved:,}")
        lines.append(f"Total Size:   {self._format_size(self.report.total_bytes_carved)}")
        lines.append("")

        if self.report.files_by_type:
            lines.append(f"{'Type':<25} {'Count':>10} {'Size':>15}")
            lines.append("-" * 52)

            for file_type, stats in sorted(self.report.files_by_type.items(), key=lambda x: -x[1].total_bytes):
                type_name = self.TYPE_NAMES.get(file_type, file_type)
                if len(type_name) > 24:
                    type_name = type_name[:21] + "..."
                lines.append(f"{type_name:<25} {stats.count:>10,} {self._format_size(stats.total_bytes):>15}")
            lines.append("")

        # String Extraction Summary
        if self.report.total_strings > 0:
            lines.append("-" * 70)
            lines.append("EXTRACTED STRINGS")
            lines.append("-" * 70)
            lines.append(f"Total Strings:     {self.report.total_strings:,}")
            if self.report.strings_excluded_carved > 0:
                lines.append(f"Excluded (carved): {self.report.strings_excluded_carved:,}")
            lines.append("")

            if self.report.strings_by_category:
                lines.append("By Category:")
                for cat, count in sorted(self.report.strings_by_category.items(), key=lambda x: -x[1]):
                    lines.append(f"  {cat:<20} {count:>10,}")
                lines.append("")

        # Coverage Summary
        lines.append("-" * 70)
        lines.append("COVERAGE ANALYSIS")
        lines.append("-" * 70)
        lines.append(f"Identified:   {self._format_size(self.report.identified_bytes)} ({self.report.coverage_percent:.1f}%)")
        lines.append(f"Unknown:      {self._format_size(self.report.unknown_bytes)} ({100 - self.report.coverage_percent:.1f}%)")
        lines.append("")
        lines.append("=" * 70)

        return "\n".join(lines)

    def save_report(self) -> Path:
        """Save the report to the output directory."""
        self.calculate_coverage()

        # Save text report
        text_path = self.output_dir / "extraction_report.txt"
        with open(text_path, "w", encoding="utf-8") as f:
            f.write(self.generate_text_report())

        # Save JSON report
        json_path = self.output_dir / "extraction_report.json"

        # Convert to serializable dict
        report_dict = {
            "dump_file": self.report.dump_file,
            "dump_size": self.report.dump_size,
            "extraction_time": self.report.extraction_time,
            "version": self.report.version,
            "total_files_carved": self.report.total_files_carved,
            "total_bytes_carved": self.report.total_bytes_carved,
            "files_by_type": {k: {"count": v.count, "total_bytes": v.total_bytes} for k, v in self.report.files_by_type.items()},
            "total_strings": self.report.total_strings,
            "strings_excluded_carved": self.report.strings_excluded_carved,
            "strings_by_category": self.report.strings_by_category,
            "strings_by_encoding": self.report.strings_by_encoding,
            "coverage_percent": self.report.coverage_percent,
            "identified_bytes": self.report.identified_bytes,
            "unknown_bytes": self.report.unknown_bytes,
        }

        with open(json_path, "w", encoding="utf-8") as f:
            json.dump(report_dict, f, indent=2)

        return text_path

    @staticmethod
    def _format_size(size: int) -> str:
        """Format size in human-readable form."""
        for unit in ["B", "KB", "MB", "GB"]:
            if abs(size) < 1024:
                return f"{size:.2f} {unit}" if unit != "B" else f"{size} {unit}"
            size /= 1024  # type: ignore
        return f"{size:.2f} TB"
