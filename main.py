"""
Xbox 360 Memory Dump File Carver

A comprehensive tool for extracting usable data from Xbox 360 memory dumps.
Extracts files, strings, and generates detailed extraction reports.

Supports carving:
- DDS/DDX textures (Xbox 360 and PC formats)
- XMA audio files
- NIF/KF model and animation files
- Bethesda ObScript source files
- STFS/CON packages
- And many more formats

Output is organized into subdirectories by asset type with a comprehensive
extraction report at the top level.
"""

import logging
import os
import sys
from pathlib import Path
from typing import List, Optional

from src.carver import MemoryCarver
from src.file_signatures import FILE_SIGNATURES
from src.report import ReportGenerator
from src.string_extractor import StringExtractor

__version__ = "1.1.0"


def setup_logging(verbose: bool = False, log_file: Optional[str] = None) -> None:
    """Configure logging for the application."""
    level = logging.DEBUG if verbose else logging.INFO

    handlers: List[logging.Handler] = [logging.StreamHandler(sys.stdout)]
    if log_file:
        handlers.append(logging.FileHandler(log_file, mode="a"))

    logging.basicConfig(
        level=level,
        format="%(asctime)s - %(levelname)s - %(message)s",
        handlers=handlers,
    )


def find_dump_files(path: str) -> List[Path]:
    """Find all .dmp files in a path (file or directory)."""
    path_obj = Path(path)

    if path_obj.is_file() and path_obj.suffix.lower() == ".dmp":
        return [path_obj]

    if path_obj.is_dir():
        return sorted(path_obj.glob("*.dmp"))

    return []


def extract_dump(
    dump_path: Path,
    output_base: str,
    file_types: Optional[List[str]] = None,
    extract_strings: bool = True,
    chunk_size_mb: int = 10,
    max_files: int = 10000,
    verbose: bool = False,
) -> Path:
    """
    Extract all usable data from a single dump file.

    Args:
        dump_path: Path to the .dmp file
        output_base: Base output directory
        file_types: Optional list of file types to extract (None = all)
        extract_strings: Whether to extract text strings
        chunk_size_mb: Chunk size for processing
        max_files: Maximum files per type
        verbose: Enable verbose logging

    Returns:
        Path to the output directory for this dump.
    """
    logger = logging.getLogger(__name__)

    # Create output directory named after the dump
    dump_name = dump_path.stem
    output_dir = Path(output_base) / dump_name
    output_dir.mkdir(parents=True, exist_ok=True)

    dump_size = dump_path.stat().st_size

    logger.info(f"Processing: {dump_path.name}")
    logger.info(f"Dump size: {dump_size / 1024 / 1024:.2f} MB")
    logger.info(f"Output: {output_dir}")
    logger.info("-" * 60)

    # Initialize report generator
    report_gen = ReportGenerator(output_dir)
    report_gen.set_dump_info(str(dump_path), dump_size)

    # Step 1: Carve files
    logger.info("Step 1: Carving files...")
    chunk_size_bytes = chunk_size_mb * 1024 * 1024
    carver = MemoryCarver(
        output_dir=str(output_dir),
        chunk_size=chunk_size_bytes,
        max_files_per_type=max_files,
    )

    # output_subdir=False because we already created the dump-named directory
    carver.carve_dump(str(dump_path), file_types=file_types, output_subdir=False)

    # Get manifest entries for report
    manifest_entries = [
        {
            "file_type": e.file_type,
            "offset": e.offset,
            "size_in_dump": e.size_in_dump,
            "size_output": e.size_output,
            "filename": e.filename,
        }
        for e in carver.manifest
    ]
    report_gen.add_carved_files(manifest_entries)

    # Step 2: Extract strings (excluding carved regions)
    if extract_strings:
        logger.info("\nStep 2: Extracting strings...")
        string_extractor = StringExtractor()

        # Load carved regions to exclude
        manifest_path = output_dir / "carve_manifest.json"
        if manifest_path.exists():
            num_regions = string_extractor.load_carved_regions(str(manifest_path))
            logger.info(f"  Excluding {num_regions} carved regions from string search")

        string_result = string_extractor.extract(str(dump_path))

        logger.info(f"  Found {string_result.total_found:,} strings")
        logger.info(f"  Kept {string_result.total_kept:,} (excluded {string_result.skipped_carved:,} in carved regions)")

        # Save strings
        string_extractor.save_strings(string_result, output_dir)

        # Add to report
        report_gen.add_string_stats(
            total=string_result.total_kept,
            excluded=string_result.skipped_carved,
            by_category=string_result.by_category,
            by_encoding=string_result.by_encoding,
        )

    # Step 3: Generate report
    logger.info("\nStep 3: Generating report...")
    report_path = report_gen.save_report()
    logger.info(f"  Report saved to: {report_path}")

    return output_dir


def main() -> int:
    """Main entry point."""
    import argparse

    parser = argparse.ArgumentParser(
        description="Extract usable data from Xbox 360 memory dumps",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="""
Examples:
  # Extract everything from a single dump
  python main.py Sample/Fallout_Debug.xex.dmp
  
  # Extract from all dumps in a directory
  python main.py Sample/
  
  # Extract only specific file types
  python main.py dump.dmp --types dds xma script_scn
  
  # Skip string extraction
  python main.py dump.dmp --no-strings
  
Output Structure:
  output/<dump_name>/
    ├── extraction_report.txt    # Human-readable summary
    ├── extraction_report.json   # Machine-readable report
    ├── carve_manifest.json      # Detailed file manifest
    ├── strings/                 # Extracted text strings
    │   ├── general.txt
    │   ├── filepath.txt
    │   └── ...
    ├── dds/                     # DDS textures
    ├── xma/                     # XMA audio
    ├── script_scn/              # ObScript source files
    └── ...                      # Other file types

Supported file types:
  """
        + ", ".join(sorted(FILE_SIGNATURES.keys())),
    )

    parser.add_argument(
        "path",
        help="Path to .dmp file or directory containing .dmp files",
    )
    parser.add_argument(
        "-o",
        "--output",
        default="./output",
        help="Base output directory (default: ./output)",
    )
    parser.add_argument(
        "--types",
        nargs="+",
        choices=list(FILE_SIGNATURES.keys()),
        help="Specific file types to extract (default: all)",
    )
    parser.add_argument(
        "--no-strings",
        action="store_true",
        help="Skip string extraction",
    )
    parser.add_argument(
        "--chunk-size",
        type=int,
        default=10,
        help="Chunk size in MB for processing (default: 10)",
    )
    parser.add_argument(
        "--max-files",
        type=int,
        default=10000,
        help="Maximum files to extract per type (default: 10000)",
    )
    parser.add_argument(
        "-v",
        "--verbose",
        action="store_true",
        help="Enable verbose logging",
    )
    parser.add_argument(
        "--version",
        action="version",
        version=f"Xbox 360 Memory Carver v{__version__}",
    )

    args = parser.parse_args()

    # Setup logging
    setup_logging(verbose=args.verbose)
    logger = logging.getLogger(__name__)

    logger.info(f"Xbox 360 Memory Dump Extractor v{__version__}")
    logger.info("=" * 60)

    # Find dump files
    dumps = find_dump_files(args.path)
    if not dumps:
        logger.error(f"No .dmp files found at: {args.path}")
        return 1

    logger.info(f"Found {len(dumps)} dump file(s) to process")

    # Process each dump
    for i, dump_path in enumerate(dumps, 1):
        logger.info(f"\n{'=' * 60}")
        logger.info(f"Processing dump {i}/{len(dumps)}")
        logger.info("=" * 60)

        try:
            extract_dump(
                dump_path=dump_path,
                output_base=args.output,
                file_types=args.types,
                extract_strings=not args.no_strings,
                chunk_size_mb=args.chunk_size,
                max_files=args.max_files,
                verbose=args.verbose,
            )
        except KeyboardInterrupt:
            logger.warning("\nExtraction interrupted by user")
            return 130
        except Exception as e:
            logger.error(f"Error processing {dump_path}: {e}")
            if args.verbose:
                import traceback

                traceback.print_exc()
            continue

    logger.info(f"\n{'=' * 60}")
    logger.info("Extraction complete!")
    logger.info(f"Output saved to: {args.output}")

    return 0


if __name__ == "__main__":
    sys.exit(main())
