"""
Coverage Analysis Tool for Xbox 360 Memory Dumps

Analyzes a memory dump file to determine how much of the data can be
identified as known file types, and generates a detailed report.
"""

import os
import sys
import json
import logging
import argparse
from pathlib import Path
from typing import Dict, List, Tuple, Optional, Any
from dataclasses import dataclass, field, asdict
from collections import defaultdict

from src.carver import MemoryCarver
from src.file_signatures import FILE_SIGNATURES

logging.basicConfig(level=logging.INFO, format="%(message)s")
logger = logging.getLogger(__name__)


@dataclass
class FileMatch:
    """Represents a matched file in the dump."""

    file_type: str
    offset: int
    size: int
    end_offset: int
    filename: str


@dataclass
class UnknownRegion:
    """Represents an unidentified region in the dump."""

    start: int
    end: int
    size: int

    # Sample bytes for analysis
    sample_hex: str = ""
    sample_printable: str = ""

    # Pattern analysis
    is_zeros: bool = False
    is_repeating: bool = False
    pattern_byte: Optional[int] = None


@dataclass
class CoverageReport:
    """Complete coverage analysis report."""

    dump_file: str
    dump_size: int

    # Coverage statistics
    identified_bytes: int = 0  # Unique bytes covered (after removing overlaps)
    total_carved_bytes: int = 0  # Raw total of all carved file sizes
    unknown_bytes: int = 0
    coverage_percent: float = 0.0

    # File statistics
    files_by_type: Dict[str, int] = field(default_factory=dict)
    bytes_by_type: Dict[str, int] = field(default_factory=dict)

    # Matched files
    matched_files: List[FileMatch] = field(default_factory=list)

    # Unknown regions (gaps)
    unknown_regions: List[UnknownRegion] = field(default_factory=list)
    large_unknown_regions: List[UnknownRegion] = field(default_factory=list)

    # Pattern analysis
    zero_regions_bytes: int = 0
    repeating_pattern_bytes: int = 0


class CoverageAnalyzer:
    """Analyzes memory dump coverage by file type identification."""

    def __init__(self, output_dir: str = "./coverage_analysis"):
        self.output_dir = Path(output_dir)
        self.output_dir.mkdir(parents=True, exist_ok=True)

    def analyze_dump(self, dump_path: str, file_types: Optional[List[str]] = None) -> CoverageReport:
        """
        Analyze a dump file and generate a coverage report.

        Args:
            dump_path: Path to the .dmp file
            file_types: Optional list of file types to search for

        Returns:
            CoverageReport with detailed analysis
        """
        dump_path = Path(dump_path)
        dump_size = dump_path.stat().st_size

        logger.info(f"Analyzing: {dump_path.name}")
        logger.info(f"Dump size: {self._format_size(dump_size)}")
        logger.info("-" * 60)

        report = CoverageReport(dump_file=str(dump_path), dump_size=dump_size)

        # Create temporary output for carving (clear if exists to avoid duplicates)
        temp_output = self.output_dir / dump_path.stem / "carved"
        if temp_output.exists():
            import shutil

            shutil.rmtree(temp_output)
        temp_output.mkdir(parents=True, exist_ok=True)

        # Run carver to identify files
        carver = MemoryCarver(output_dir=str(temp_output), chunk_size=50 * 1024 * 1024, max_files_per_type=50000)  # 50MB chunks for speed

        logger.info("Scanning for known file signatures...")
        carver.carve_dump(str(dump_path), file_types=file_types)

        # Collect carved files info from manifest (uses correct sizes for compressed data)
        self._collect_carved_files_from_manifest(report, temp_output, carver)

        # Analyze unknown regions
        logger.info("\nAnalyzing unknown regions...")
        self._analyze_unknown_regions(report, dump_path)

        # Calculate final statistics
        self._calculate_statistics(report)

        return report

    def _collect_carved_files_from_manifest(self, report: CoverageReport, carved_dir: Path, carver: MemoryCarver) -> None:
        """Collect information about carved files using manifest data for accurate sizes."""
        files_by_type: Dict[str, int] = defaultdict(int)
        bytes_by_type: Dict[str, int] = defaultdict(int)  # Uses size_in_dump for accurate coverage
        bytes_output_by_type: Dict[str, int] = defaultdict(int)  # Actual output sizes
        matched_files: List[FileMatch] = []

        for entry in carver.manifest:
            file_type = entry.file_type
            files_by_type[file_type] += 1
            bytes_by_type[file_type] += entry.size_in_dump  # Original size in dump
            bytes_output_by_type[file_type] += entry.size_output  # Output file size

            # Create FileMatch with size_in_dump for coverage calculation
            matched_files.append(
                FileMatch(
                    file_type=file_type,
                    offset=entry.offset,
                    size=entry.size_in_dump,  # Use dump size for coverage
                    end_offset=entry.offset + entry.size_in_dump,
                    filename=entry.filename,
                )
            )

        report.files_by_type = dict(files_by_type)
        report.bytes_by_type = dict(bytes_by_type)  # Now reflects actual dump bytes
        report.matched_files = sorted(matched_files, key=lambda x: x.offset)
        report.total_carved_bytes = sum(bytes_output_by_type.values())  # Total output (decompressed)

    def _collect_carved_files(self, report: CoverageReport, carved_dir: Path) -> None:
        """Legacy: Collect information about carved files from output directory."""
        files_by_type: Dict[str, int] = defaultdict(int)
        bytes_by_type: Dict[str, int] = defaultdict(int)
        matched_files: List[FileMatch] = []

        for type_dir in carved_dir.iterdir():
            if type_dir.is_dir():
                file_type = type_dir.name
                for file_path in type_dir.glob("*"):
                    if file_path.is_file():
                        size = file_path.stat().st_size
                        files_by_type[file_type] += 1
                        bytes_by_type[file_type] += size

                        # Try to extract offset from filename
                        offset = self._extract_offset_from_filename(file_path.name)
                        if offset is not None:
                            matched_files.append(FileMatch(file_type=file_type, offset=offset, size=size, end_offset=offset + size, filename=file_path.name))

        report.files_by_type = dict(files_by_type)
        report.bytes_by_type = dict(bytes_by_type)
        report.matched_files = sorted(matched_files, key=lambda x: x.offset)
        report.total_carved_bytes = sum(bytes_by_type.values())

    def _extract_offset_from_filename(self, filename: str) -> Optional[int]:
        """Extract offset from carved filename (format: type_offset_hash.ext)."""
        try:
            # Format is typically: prefix_offset_hash.ext
            parts = filename.rsplit(".", 1)[0].split("_")
            for part in parts:
                if part.startswith("0x") or (len(part) >= 6 and part.isalnum()):
                    try:
                        return int(part, 16)
                    except ValueError:
                        continue
                # Try decimal
                if part.isdigit() and len(part) >= 6:
                    return int(part)
        except Exception:
            pass
        return None

    def _analyze_unknown_regions(self, report: CoverageReport, dump_path: Path) -> None:
        """Analyze regions not covered by identified files."""
        # Build coverage map
        coverage_map: List[Tuple[int, int]] = []
        for match in report.matched_files:
            coverage_map.append((match.offset, match.end_offset))

        # Merge overlapping regions
        coverage_map.sort()
        merged: List[Tuple[int, int]] = []
        for start, end in coverage_map:
            if merged and start <= merged[-1][1]:
                merged[-1] = (merged[-1][0], max(merged[-1][1], end))
            else:
                merged.append((start, end))

        # Find gaps (unknown regions)
        unknown_regions: List[UnknownRegion] = []
        prev_end = 0

        for start, end in merged:
            if start > prev_end:
                region = UnknownRegion(start=prev_end, end=start, size=start - prev_end)
                unknown_regions.append(region)
            prev_end = max(prev_end, end)

        # Add final region if dump extends past last match
        if prev_end < report.dump_size:
            unknown_regions.append(UnknownRegion(start=prev_end, end=report.dump_size, size=report.dump_size - prev_end))

        # Analyze content of unknown regions
        with open(dump_path, "rb") as f:
            for region in unknown_regions:
                self._analyze_region_content(f, region)

        report.unknown_regions = unknown_regions
        report.large_unknown_regions = [r for r in unknown_regions if r.size >= 1024 * 1024]  # >= 1MB
        report.unknown_bytes = sum(r.size for r in unknown_regions)
        report.zero_regions_bytes = sum(r.size for r in unknown_regions if r.is_zeros)
        report.repeating_pattern_bytes = sum(r.size for r in unknown_regions if r.is_repeating)

    def _analyze_region_content(self, f, region: UnknownRegion) -> None:
        """Analyze the content of an unknown region."""
        f.seek(region.start)

        # Read sample (first 64 bytes)
        sample_size = min(64, region.size)
        sample = f.read(sample_size)

        region.sample_hex = sample[:32].hex()
        region.sample_printable = "".join(chr(b) if 32 <= b < 127 else "." for b in sample[:32])

        # Check for patterns
        if len(sample) >= 4:
            # Check if all zeros
            if all(b == 0 for b in sample):
                # Verify with larger sample
                if region.size > sample_size:
                    f.seek(region.start)
                    larger_sample = f.read(min(4096, region.size))
                    if all(b == 0 for b in larger_sample):
                        region.is_zeros = True
                        region.pattern_byte = 0
                else:
                    region.is_zeros = True
                    region.pattern_byte = 0

            # Check for repeating single byte
            elif len(set(sample)) == 1:
                region.is_repeating = True
                region.pattern_byte = sample[0]

            # Check for repeating 4-byte pattern
            elif len(sample) >= 8:
                pattern = sample[:4]
                is_repeating = True
                for i in range(4, len(sample) - 3, 4):
                    if sample[i : i + 4] != pattern:
                        is_repeating = False
                        break
                if is_repeating:
                    region.is_repeating = True

    def _calculate_statistics(self, report: CoverageReport) -> None:
        """Calculate final coverage statistics accounting for overlaps."""
        if report.dump_size > 0:
            # Calculate unique covered bytes by merging all matched file ranges
            coverage_map: List[Tuple[int, int]] = []
            for match in report.matched_files:
                coverage_map.append((match.offset, match.end_offset))

            # Merge overlapping regions
            coverage_map.sort()
            merged: List[Tuple[int, int]] = []
            for start, end in coverage_map:
                if merged and start <= merged[-1][1]:
                    merged[-1] = (merged[-1][0], max(merged[-1][1], end))
                else:
                    merged.append((start, end))

            # Calculate unique identified bytes (non-overlapping)
            unique_identified = sum(end - start for start, end in merged)
            report.identified_bytes = unique_identified
            report.unknown_bytes = report.dump_size - unique_identified
            report.coverage_percent = (unique_identified / report.dump_size) * 100

    def generate_report(self, report: CoverageReport) -> str:
        """Generate a human-readable report."""
        lines = []
        lines.append("=" * 70)
        lines.append("MEMORY DUMP COVERAGE ANALYSIS REPORT")
        lines.append("=" * 70)
        lines.append("")

        # Basic info
        lines.append(f"Dump File: {Path(report.dump_file).name}")
        lines.append(f"Dump Size: {self._format_size(report.dump_size)}")
        lines.append("")

        # Coverage summary
        lines.append("-" * 70)
        lines.append("COVERAGE SUMMARY")
        lines.append("-" * 70)
        lines.append(f"Unique Identified:  {self._format_size(report.identified_bytes):>15} ({report.coverage_percent:.2f}%)")
        lines.append(f"Total Carved:       {self._format_size(report.total_carved_bytes):>15} (includes overlaps)")
        lines.append(f"Unknown Data:       {self._format_size(report.unknown_bytes):>15} ({100 - report.coverage_percent:.2f}%)")
        lines.append(f"  - Zero-filled:    {self._format_size(report.zero_regions_bytes):>15}")
        lines.append(f"  - Repeating:      {self._format_size(report.repeating_pattern_bytes):>15}")
        actual_unknown = report.unknown_bytes - report.zero_regions_bytes - report.repeating_pattern_bytes
        lines.append(f"  - Actual unknown: {self._format_size(actual_unknown):>15}")
        lines.append("")

        # Files by type
        lines.append("-" * 70)
        lines.append("FILES BY TYPE")
        lines.append("-" * 70)
        lines.append(f"{'Type':<20} {'Count':>10} {'Size':>15} {'% of Dump':>12}")
        lines.append("-" * 70)

        sorted_types = sorted(report.bytes_by_type.items(), key=lambda x: -x[1])
        for file_type, byte_count in sorted_types:
            count = report.files_by_type.get(file_type, 0)
            pct = (byte_count / report.dump_size) * 100 if report.dump_size > 0 else 0
            lines.append(f"{file_type:<20} {count:>10,} {self._format_size(byte_count):>15} {pct:>11.2f}%")

        lines.append("-" * 70)
        total_files = sum(report.files_by_type.values())
        lines.append(f"{'TOTAL':<20} {total_files:>10,} {self._format_size(report.identified_bytes):>15} {report.coverage_percent:>11.2f}%")
        lines.append("")

        # Large unknown regions
        if report.large_unknown_regions:
            lines.append("-" * 70)
            lines.append(f"LARGE UNKNOWN REGIONS (>= 1 MB): {len(report.large_unknown_regions)}")
            lines.append("-" * 70)
            lines.append(f"{'Offset':<18} {'Size':>15} {'Type':<15} {'Sample (hex)'}")
            lines.append("-" * 70)

            for region in report.large_unknown_regions[:20]:  # Top 20
                region_type = "zeros" if region.is_zeros else "repeating" if region.is_repeating else "data"
                lines.append(f"0x{region.start:012X}   {self._format_size(region.size):>15} {region_type:<15} {region.sample_hex[:32]}...")

            if len(report.large_unknown_regions) > 20:
                lines.append(f"... and {len(report.large_unknown_regions) - 20} more regions")

        lines.append("")

        # Unknown data samples (potential signatures to add)
        interesting_regions = [r for r in report.unknown_regions if not r.is_zeros and not r.is_repeating and r.size >= 1024]

        if interesting_regions:
            lines.append("-" * 70)
            lines.append("POTENTIAL UNIDENTIFIED FILE SIGNATURES")
            lines.append("-" * 70)
            lines.append("These regions contain data that might be recognizable file formats:")
            lines.append("")

            # Group by first 4 bytes (potential magic numbers)
            magic_groups: Dict[str, List[UnknownRegion]] = defaultdict(list)
            for region in interesting_regions:
                magic = region.sample_hex[:8]  # First 4 bytes
                magic_groups[magic].append(region)

            # Show most common potential signatures
            sorted_magics = sorted(magic_groups.items(), key=lambda x: -len(x[1]))[:15]

            lines.append(f"{'Magic (hex)':<20} {'Count':>8} {'Total Size':>15} {'ASCII'}")
            lines.append("-" * 70)
            for magic, regions in sorted_magics:
                total_size = sum(r.size for r in regions)
                ascii_repr = "".join(chr(int(magic[i : i + 2], 16)) if 32 <= int(magic[i : i + 2], 16) < 127 else "." for i in range(0, len(magic), 2))
                lines.append(f"{magic:<20} {len(regions):>8} {self._format_size(total_size):>15} {ascii_repr}")

        lines.append("")
        lines.append("=" * 70)

        return "\n".join(lines)

    def save_report(self, report: CoverageReport, output_path: Optional[str] = None) -> Tuple[str, str]:
        """Save the report to text and JSON files."""
        dump_name = Path(report.dump_file).stem

        if output_path is None:
            output_path = self.output_dir / dump_name
        else:
            output_path = Path(output_path)

        output_path.mkdir(parents=True, exist_ok=True)

        # Save text report
        text_path = output_path / "coverage_report.txt"
        text_content = self.generate_report(report)
        with open(text_path, "w") as f:
            f.write(text_content)

        # Save JSON report (for programmatic access)
        json_path = output_path / "coverage_report.json"

        # Convert to JSON-serializable format
        json_data = {
            "dump_file": report.dump_file,
            "dump_size": report.dump_size,
            "identified_bytes": report.identified_bytes,
            "unknown_bytes": report.unknown_bytes,
            "coverage_percent": report.coverage_percent,
            "files_by_type": report.files_by_type,
            "bytes_by_type": report.bytes_by_type,
            "zero_regions_bytes": report.zero_regions_bytes,
            "repeating_pattern_bytes": report.repeating_pattern_bytes,
            "large_unknown_regions_count": len(report.large_unknown_regions),
            "total_unknown_regions": len(report.unknown_regions),
        }

        with open(json_path, "w") as f:
            json.dump(json_data, f, indent=2)

        return str(text_path), str(json_path)

    @staticmethod
    def _format_size(size: int) -> str:
        """Format byte size in human-readable format."""
        for unit in ["B", "KB", "MB", "GB"]:
            if size < 1024:
                return f"{size:.2f} {unit}"
            size /= 1024
        return f"{size:.2f} TB"


def main():
    parser = argparse.ArgumentParser(description="Analyze memory dump coverage by identified file types")
    parser.add_argument("dump_file", help="Path to .dmp file to analyze")
    parser.add_argument("--output", "-o", default="./coverage_analysis", help="Output directory for reports (default: ./coverage_analysis)")
    parser.add_argument("--types", nargs="+", choices=list(FILE_SIGNATURES.keys()), help="Specific file types to search for (default: all)")

    args = parser.parse_args()

    if not os.path.exists(args.dump_file):
        logger.error(f"File not found: {args.dump_file}")
        return 1

    analyzer = CoverageAnalyzer(output_dir=args.output)

    try:
        report = analyzer.analyze_dump(args.dump_file, file_types=args.types)

        # Print report to console
        print("\n" + analyzer.generate_report(report))

        # Save reports
        text_path, json_path = analyzer.save_report(report)
        logger.info(f"\nReports saved to:")
        logger.info(f"  Text: {text_path}")
        logger.info(f"  JSON: {json_path}")

    except KeyboardInterrupt:
        logger.info("\nAnalysis cancelled by user")
        return 130
    except Exception as e:
        logger.error(f"Error analyzing dump: {e}")
        import traceback

        traceback.print_exc()
        return 1

    return 0


if __name__ == "__main__":
    sys.exit(main())
