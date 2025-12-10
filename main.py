"""
Xbox 360 Memory Dump File Carver

A tool for extracting files from Xbox 360 memory dumps, specifically designed for
Fallout: New Vegas prototype builds but applicable to other Xbox 360 games.

Supports carving:
- DDS textures (Xbox 360 and PC formats)
- XMA audio files
- NIF/KF model and animation files
- Bethesda script files
- ESP plugin files
- BSA archive files
- Common audio formats (MP3, OGG, WAV)
"""

import os
import sys
import logging
import argparse
from pathlib import Path
from typing import List, Optional

from src.carver import MemoryCarver
from src.file_signatures import FILE_SIGNATURES
from src.integrity import generate_integrity_report
from src.minidump_extractor import MinidumpExtractor

__version__ = "1.0.0"


def setup_logging(verbose: bool = False):
    """Configure logging for the application."""
    level = logging.DEBUG if verbose else logging.INFO
    logging.basicConfig(level=level, format="%(asctime)s - %(levelname)s - %(message)s", handlers=[logging.StreamHandler(sys.stdout), logging.FileHandler("carver.log", mode="a")])


def find_dump_files(directory: str) -> List[str]:
    """Find all .dmp files in a directory."""
    dump_files = []
    for file in Path(directory).glob("*.dmp"):
        if file.is_file():
            dump_files.append(str(file))
    return sorted(dump_files)


def main():
    """Main entry point for the file carver."""
    parser = argparse.ArgumentParser(
        description="Carve files from Xbox 360 memory dumps",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="""
Examples:
  # Carve all file types from a single dump
  python main.py Fallout_Debug.xex.dmp
  
  # Carve only DDS textures from all dumps in current directory
  python main.py --types dds --all
  
  # Carve DDS and XMA files with verbose output
  python main.py Fallout_Release_Beta.xex.dmp --types dds xma --verbose
  
  # Process all dumps with custom output directory
  python main.py --all --output ./extracted_files
  
Supported file types:
  """
        + ", ".join(sorted(FILE_SIGNATURES.keys())),
    )

    parser.add_argument("dump_files", nargs="*", help="Path(s) to .dmp file(s) to process")

    parser.add_argument("--all", action="store_true", help="Process all .dmp files in the current directory")

    parser.add_argument("--types", nargs="+", choices=list(FILE_SIGNATURES.keys()), help="Specific file types to carve (default: all types)")

    parser.add_argument("--output", default="./output", help="Output directory for carved files (default: ./output)")

    parser.add_argument("--chunk-size", type=int, default=10, help="Chunk size in MB for processing (default: 10)")

    parser.add_argument("--max-files", type=int, default=10000, help="Maximum files to carve per type (default: 10000)")

    parser.add_argument("--verbose", action="store_true", help="Enable verbose logging")

    parser.add_argument("--check-integrity", action="store_true", help="Run integrity check on carved files after extraction")

    parser.add_argument("--extract-modules", action="store_true", help="Extract loaded modules (EXE/DLL) from minidump using proper parsing")

    parser.add_argument("--version", action="version", version=f"Xbox 360 Memory Carver v{__version__}")

    args = parser.parse_args()

    # Setup logging
    setup_logging(args.verbose)
    logger = logging.getLogger(__name__)

    logger.info(f"Xbox 360 Memory Dump File Carver v{__version__}")
    logger.info("=" * 60)

    # Determine which dumps to process
    dumps_to_process = []

    if args.all:
        dumps_to_process = find_dump_files(".")
        if not dumps_to_process:
            logger.error("No .dmp files found in current directory")
            return 1
        logger.info(f"Found {len(dumps_to_process)} dump file(s) to process")
    elif args.dump_files:
        dumps_to_process = args.dump_files
    else:
        parser.print_help()
        return 1

    # Validate dump files exist
    for dump in dumps_to_process:
        if not os.path.exists(dump):
            logger.error(f"Dump file not found: {dump}")
            return 1

    # Create output directory
    os.makedirs(args.output, exist_ok=True)

    # Initialize carver
    chunk_size_bytes = args.chunk_size * 1024 * 1024
    carver = MemoryCarver(output_dir=args.output, chunk_size=chunk_size_bytes, max_files_per_type=args.max_files)

    # Extract modules if requested (using proper minidump parsing)
    if args.extract_modules:
        logger.info("\nExtracting modules from minidumps using minidump library...")
        for i, dump_path in enumerate(dumps_to_process, 1):
            try:
                dump_name = Path(dump_path).stem
                module_output = os.path.join(args.output, dump_name, "modules")
                extractor = MinidumpExtractor(module_output)
                modules = extractor.extract_modules(dump_path)
                if modules:
                    logger.info(f"Extracted {len(modules)} modules from {dump_name}")
                    for mod in modules:
                        logger.debug(f"  - {mod['name']} ({mod['size']} bytes)")
            except Exception as e:
                logger.error(f"Error extracting modules from {dump_path}: {e}", exc_info=args.verbose)
                continue

    # Process each dump for file carving
    for i, dump_path in enumerate(dumps_to_process, 1):
        logger.info(f"\nProcessing dump {i}/{len(dumps_to_process)}")
        try:
            carver.carve_dump(dump_path, file_types=args.types)
        except KeyboardInterrupt:
            logger.warning("\nCarving interrupted by user")
            return 130
        except Exception as e:
            logger.error(f"Error processing {dump_path}: {e}", exc_info=args.verbose)
            continue

    # Print final statistics
    logger.info("\n" + "=" * 60)
    logger.info("Carving complete!")
    stats = carver.get_statistics()
    total = sum(stats.values())
    logger.info(f"Total files carved: {total}")

    # Run integrity check if requested
    if args.check_integrity and total > 0:
        logger.info("\n" + "=" * 60)
        logger.info("Running integrity check...")
        try:
            report_path = generate_integrity_report(args.output, args.types)
            logger.info(f"Integrity report saved to: {report_path}")
        except Exception as e:
            logger.error(f"Error generating integrity report: {e}", exc_info=args.verbose)

    return 0


if __name__ == "__main__":
    sys.exit(main())
