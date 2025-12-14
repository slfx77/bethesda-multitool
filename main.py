"""
Xbox 360 Memory Dump File Carver

A comprehensive tool for extracting usable data from Xbox 360 memory dumps.
Extracts files from various formats.

Usage:
  # Extract assets from a dump
  python main.py Sample/Fallout_Debug.xex.dmp

  # Batch process all dumps
  python main.py Sample/

Supports carving:
- DDS/DDX textures (Xbox 360 and PC formats)
- XMA audio files
- NIF/KF model and animation files
- Bethesda ObScript source files
- STFS/CON packages
- And many more formats
"""

import logging
import subprocess
import sys
from pathlib import Path
from typing import List, Optional, Tuple

from src.carver import MemoryCarver
from src.file_signatures import FILE_SIGNATURES
from src.minidump_extractor import MinidumpExtractor
from src.report import ReportGenerator

__version__ = "2.0.0"


# Path to DDXConv.exe (in the root folder of the project)
DDXCONV_PATH = Path(__file__).parent / "DDXConv.exe"


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


def _convert_single_ddx(ddx_file: Path, dds_file: Path, logger: logging.Logger) -> bool:
    """
    Convert a single DDX file to DDS format.

    Returns:
        True if conversion succeeded, False otherwise.
    """
    try:
        result = subprocess.run(
            [str(DDXCONV_PATH), str(ddx_file), str(dds_file)],
            capture_output=True,
            timeout=30,
        )
        if result.returncode == 0 and dds_file.exists():
            logger.debug(f"  Converted: {ddx_file.name} -> {dds_file.name}")
            return True
        logger.debug(f"  Failed to convert: {ddx_file.name}")
        if result.stderr:
            logger.debug(f"    Error: {result.stderr.decode('utf-8', errors='ignore')}")
    except subprocess.TimeoutExpired:
        logger.debug(f"  Timeout converting: {ddx_file.name}")
    except Exception as e:
        logger.debug(f"  Error converting {ddx_file.name}: {e}")
    return False


def convert_ddx_to_dds(ddx_dir: Path, output_dir: Optional[Path] = None, logger: Optional[logging.Logger] = None) -> Tuple[int, int]:
    """
    Convert DDX files to DDS format using DDXConv.exe.

    Args:
        ddx_dir: Directory containing .ddx files
        output_dir: Output directory for .dds files (default: ddx_dir/../textures_converted)
        logger: Logger instance

    Returns:
        Tuple of (successful_conversions, failed_conversions)
    """
    if logger is None:
        logger = logging.getLogger(__name__)

    if not DDXCONV_PATH.exists():
        logger.warning(f"DDXConv.exe not found at {DDXCONV_PATH}")
        return 0, 0

    if not ddx_dir.exists():
        return 0, 0

    ddx_files = list(ddx_dir.glob("*.ddx"))
    if not ddx_files:
        return 0, 0

    if output_dir is None:
        output_dir = ddx_dir.parent / "textures_converted"
    output_dir.mkdir(parents=True, exist_ok=True)

    success_count = 0
    for ddx_file in ddx_files:
        dds_file = output_dir / ddx_file.with_suffix(".dds").name
        if _convert_single_ddx(ddx_file, dds_file, logger):
            success_count += 1

    fail_count = len(ddx_files) - success_count
    return success_count, fail_count


def find_dump_files(path: str) -> List[Path]:
    """Find all .dmp files in a path (file or directory)."""
    path_obj = Path(path)

    if path_obj.is_file() and path_obj.suffix.lower() == ".dmp":
        return [path_obj]

    if path_obj.is_dir():
        return sorted(path_obj.glob("*.dmp"))

    return []


def _convert_ddx_step(output_dir: Path, logger: logging.Logger) -> None:
    """Step 3: Convert DDX textures to DDS format."""
    logger.info("\nStep 3: Converting DDX textures to DDS...")
    ddx_dir = output_dir / "ddx"
    if not ddx_dir.exists() or not any(ddx_dir.glob("*.ddx")):
        logger.info("  No DDX files to convert")
        return
    if not DDXCONV_PATH.exists():
        logger.warning(f"  DDXConv.exe not found at {DDXCONV_PATH}")
        logger.info("  Download from: https://github.com/kran27/DDXConv")
        return
    converted_dir = output_dir / "textures_converted"
    success, failed = convert_ddx_to_dds(ddx_dir, converted_dir, logger)
    if success > 0 or failed > 0:
        logger.info(f"  Converted {success} DDX files to DDS ({failed} failed)")
        if failed > 0:
            logger.info("  Note: 3XDR format files may not convert (engine-tiled format)")


def extract_dump(
    dump_path: Path,
    output_base: str,
    file_types: Optional[List[str]] = None,
    extract_modules: bool = True,
    convert_ddx: bool = True,
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
        extract_modules: Whether to extract PE modules (EXE/DLL) from minidump
        convert_ddx: Whether to convert DDX files to DDS using DDXConv.exe
        chunk_size_mb: Chunk size for processing
        max_files: Maximum files per type
        verbose: Enable verbose logging

    Returns:
        Path to the output directory for this dump.
    """
    logger = logging.getLogger(__name__)

    dump_name = dump_path.stem
    output_dir = Path(output_base) / dump_name
    output_dir.mkdir(parents=True, exist_ok=True)

    dump_size = dump_path.stat().st_size

    logger.info(f"Processing: {dump_path.name}")
    logger.info(f"Dump size: {dump_size / 1024 / 1024:.2f} MB")
    logger.info(f"Output: {output_dir}")
    logger.info("-" * 60)

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
    carver.carve_dump(str(dump_path), file_types=file_types, output_subdir=False)

    manifest_entries: List[dict[str, object]] = [
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

    if extract_modules:
        _extract_modules_step_with_path(dump_path, output_dir, logger)

    if convert_ddx:
        _convert_ddx_step(output_dir, logger)

    # Step 3: Generate report
    logger.info("\nStep 3: Generating report...")
    report_path = report_gen.save_report()
    logger.info(f"  Report saved to: {report_path}")

    return output_dir


def _extract_modules_step_with_path(dump_path: Path, output_dir: Path, logger: logging.Logger) -> None:
    """Step 2: Extract PE modules (EXE/DLL) from minidump structure."""
    logger.info("\nStep 2: Extracting modules from minidump...")
    modules_dir = output_dir / "modules"
    try:
        module_extractor = MinidumpExtractor(str(modules_dir))
        extracted_modules = module_extractor.extract_modules(str(dump_path))
        if extracted_modules:
            logger.info(f"  Extracted {len(extracted_modules)} modules")
            for mod in extracted_modules:
                logger.debug("    - %s (%s, %.1f%% coverage)", mod["name"], mod["machine"], mod["coverage"])
        else:
            logger.info("  No modules found in minidump")
    except Exception as e:
        logger.warning(f"  Module extraction failed: {e}")


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
  python main.py dump.dmp --types dds ddx_3xdo xma script_scn
  
  # Skip module extraction from minidump
  python main.py dump.dmp --no-modules
  
Output Structure:
  output/<dump_name>/
    ├── extraction_report.txt    # Human-readable summary
    ├── extraction_report.json   # Machine-readable report
    ├── carve_manifest.json      # Detailed file manifest
    ├── modules/                 # Extracted PE modules (EXE/DLL)
    ├── textures/                # DDS textures (PC format)
    ├── ddx/                     # DDX textures (Xbox 360 format)
    ├── textures_converted/      # DDX converted to DDS (via DDXConv.exe)
    ├── audio/                   # XMA and other audio files
    ├── scripts/                 # ObScript source files
    ├── zlib_nif/                # Decompressed NIF models
    ├── zlib_dds/                # Decompressed textures
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
        "--no-modules",
        action="store_true",
        help="Skip extracting PE modules (EXE/DLL) from minidump structure",
    )
    parser.add_argument(
        "--no-convert",
        action="store_true",
        help="Skip converting DDX textures to DDS format",
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

    # Process each dump for asset extraction
    for i, dump_path in enumerate(dumps, 1):
        logger.info(f"\n{'=' * 60}")
        logger.info(f"Processing dump {i}/{len(dumps)}")
        logger.info("=" * 60)

        try:
            extract_dump(
                dump_path=dump_path,
                output_base=args.output,
                file_types=args.types,
                extract_modules=not args.no_modules,
                convert_ddx=not args.no_convert,
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
