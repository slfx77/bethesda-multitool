"""
Main file carving engine with memory-efficient processing.
"""

import os
import json
import logging
import struct
from typing import Dict, List, Optional, Union, Any, BinaryIO, Tuple
from dataclasses import dataclass, asdict

try:
    from tqdm import tqdm

    _tqdm_available = True
except ImportError:
    tqdm = None  # type: ignore
    _tqdm_available = False

TQDM_AVAILABLE: bool = _tqdm_available

from .file_signatures import FILE_SIGNATURES, SignatureInfo
from .parsers import DDSParser, DDXParser, XMAParser, NIFParser, LIPParser, BIKParser, ScriptParser, PNGParser, ZlibStreamParser, XEXParser, XDBFParser, XUIParser, STFSParser

# Type alias for any parser type
ParserType = Union[DDSParser, DDXParser, XMAParser, NIFParser, LIPParser, BIKParser, ScriptParser, PNGParser, ZlibStreamParser, XEXParser, XDBFParser, XUIParser, STFSParser]

from .utils import create_output_directory, format_size

logger = logging.getLogger(__name__)


@dataclass
class CarveEntry:
    """Record of a carved file with original dump location info."""

    file_type: str
    offset: int  # Offset in dump file
    size_in_dump: int  # Size of data in original dump (compressed size for zlib)
    size_output: int  # Size of output file (decompressed size for zlib)
    filename: str
    is_compressed: bool = False
    content_type: str = ""  # For zlib: nif, binary, etc.


class MemoryCarver:
    """Memory dump file carver with chunked processing to avoid crashes."""

    def __init__(self, output_dir: str, chunk_size: int = 10 * 1024 * 1024, max_files_per_type: int = 10000):
        """
        Initialize the memory carver.

        Args:
            output_dir: Base output directory for carved files
            chunk_size: Size of chunks to process at once (default 10MB)
            max_files_per_type: Maximum files to carve per type (prevents runaway extraction)
        """
        self.output_dir = output_dir
        self.chunk_size = chunk_size
        self.max_files_per_type = max_files_per_type
        self.stats: Dict[str, int] = dict.fromkeys(FILE_SIGNATURES.keys(), 0)
        self.parsers: Dict[str, ParserType] = {
            "dds": DDSParser(),
            "ddx_3xdo": DDXParser(),
            "ddx_3xdr": DDXParser(),
            "xma": XMAParser(),
            "nif": NIFParser(),
            "kf": NIFParser(),  # KF uses same Gamebryo format as NIF
            "egm": NIFParser(),  # EGM uses same Gamebryo format
            "egt": NIFParser(),  # EGT uses same Gamebryo format
            "lip": LIPParser(),
            "bik": BIKParser(),
            "script_scn": ScriptParser(),
            "script_sn": ScriptParser(),
            "png": PNGParser(),
            "zlib_default": ZlibStreamParser(),
            "zlib_best": ZlibStreamParser(),
            # Xbox 360 system formats
            "xex": XEXParser(),
            "xdbf": XDBFParser(),
            "xuis": XUIParser(),
            "xuib": XUIParser(),
            "pirs": STFSParser(),
            "con": STFSParser(),
        }
        # Manifest to track all carved files with their dump locations
        self.manifest: List[CarveEntry] = []
        self._current_output_path: str = ""

    def _get_signatures_to_search(self, file_types: Optional[List[str]]) -> Dict[str, SignatureInfo]:
        """Get the signatures to search based on file_types filter."""
        if file_types is None:
            return FILE_SIGNATURES  # type: ignore
        return {k: v for k, v in FILE_SIGNATURES.items() if k in file_types}  # type: ignore

    def _process_chunk(self, f: BinaryIO, chunk_data: bytes, chunk_start: int, signatures_to_search: Dict[str, SignatureInfo], output_path: str) -> None:
        """Process a single chunk searching for all signatures."""
        for sig_name, sig_info in signatures_to_search.items():
            if self.stats[sig_name] >= self.max_files_per_type:
                continue
            try:
                self._search_signature_in_chunk(chunk_data, chunk_start, sig_name, sig_info, output_path, f)
            except Exception as e:
                logger.warning(f"Error searching {sig_name} in chunk at offset {chunk_start:08X}: {e}")

    def carve_dump(self, dump_path: str, file_types: Optional[List[str]] = None, output_subdir: bool = True) -> None:
        """
        Carve files from a memory dump.

        Args:
            dump_path: Path to the .dmp file
            file_types: List of file types to carve (None = all types)
            output_subdir: If True, create a subdirectory named after the dump. If False, use output_dir directly.
        """
        dump_name = os.path.splitext(os.path.basename(dump_path))[0]

        if output_subdir:
            output_path = create_output_directory(self.output_dir, dump_name)
        else:
            output_path = self.output_dir
            os.makedirs(output_path, exist_ok=True)

        self._current_output_path = output_path

        # Clear manifest for new carve operation
        self.manifest.clear()

        logger.info(f"Processing: {dump_path}")
        logger.info(f"Output directory: {output_path}")

        file_size = os.path.getsize(dump_path)
        logger.info(f"Dump size: {format_size(file_size)}")

        signatures_to_search = self._get_signatures_to_search(file_types)
        overlap_size = 2048

        with open(dump_path, "rb") as f:
            pbar = tqdm(total=file_size, unit="B", unit_scale=True, desc=f"Carving {dump_name}") if TQDM_AVAILABLE and tqdm else None

            try:
                offset = 0
                while offset < file_size:
                    f.seek(max(0, offset - overlap_size))
                    chunk_data = f.read(self.chunk_size + overlap_size)
                    if not chunk_data:
                        break

                    self._process_chunk(f, chunk_data, max(0, offset - overlap_size), signatures_to_search, output_path)

                    offset += self.chunk_size
                    if pbar:
                        pbar.update(min(self.chunk_size, file_size - pbar.n))
            finally:
                if pbar:
                    pbar.close()

        # Save manifest
        self._save_manifest(output_path)
        self._print_statistics()

    def _search_signature_in_chunk(self, chunk: bytes, chunk_offset: int, sig_name: str, sig_info: SignatureInfo, output_path: str, file_handle: BinaryIO) -> None:
        """Search for a specific signature within a chunk."""
        magic = sig_info["magic"]
        if not isinstance(magic, bytes):
            return
        search_pos = 0

        while search_pos < len(chunk):
            # Find next occurrence of signature
            pos = chunk.find(magic, search_pos)
            if pos == -1:
                break

            global_offset = chunk_offset + pos

            # Skip if we've already processed this (in overlap region)
            if pos < 2048 and chunk_offset > 0:
                search_pos = pos + 1
                continue

            # Try to extract the file
            self._extract_file(file_handle, global_offset, sig_name, sig_info, output_path)

            search_pos = pos + len(magic)

    def _get_read_size(self, file_type: str, max_size: int) -> int:
        """Determine how much data to read for header analysis."""
        if file_type in ("script_scn", "script_sn"):
            return max_size
        elif file_type in ("png", "zlib_default", "zlib_best"):
            return min(max_size, 1024 * 1024)
        return 2048

    def _parse_size_result(self, size_result: Union[int, tuple[Any, ...], Dict[str, Any]]) -> tuple[Optional[int], bool, Optional[str], bool]:
        """Parse the result from _determine_file_size into components."""
        if isinstance(size_result, int):
            return size_result, False, None, True

        if not isinstance(size_result, tuple):
            return None, False, None, True

        # Extract values from tuple based on length
        tuple_len = len(size_result)
        file_size = int(size_result[0]) if size_result[0] is not None else None
        is_xbox360 = bool(size_result[1]) if tuple_len >= 2 else False
        script_name = str(size_result[2]) if tuple_len >= 3 and size_result[2] is not None else None
        is_complete = bool(size_result[3]) if tuple_len >= 4 else True

        return file_size, is_xbox360, script_name, is_complete

    def _generate_filename(self, file_type: str, extension: str, is_xbox360: bool, script_name: Optional[str], is_complete: bool, offset: int) -> str:
        """Generate appropriate filename for carved file."""
        if file_type == "dds" and is_xbox360:
            extension = ".ddx"

        if script_name and file_type in ("script_scn", "script_sn"):
            if not is_complete:
                return f"{script_name}_INCOMPLETE{extension}"
            return f"{script_name}{extension}"

        return f"{file_type}_{self.stats[file_type]:04d}_off_{offset:08X}{extension}"

    def _extract_file(self, file_handle: BinaryIO, offset: int, file_type: str, sig_info: SignatureInfo, output_path: str) -> None:
        """Extract a single file from the dump."""
        try:
            max_size = int(sig_info.get("max_size", 100000))
            min_size = int(sig_info.get("min_size", 0))
            extension = str(sig_info.get("extension", ".bin"))

            # Read header data
            file_handle.seek(offset)
            header_data = file_handle.read(self._get_read_size(file_type, max_size))

            # Parse header to determine file size
            size_result = self._determine_file_size(header_data, file_type, sig_info)
            if size_result is None:
                return

            # Handle zlib dict return
            if isinstance(size_result, dict):
                self._save_zlib_stream(size_result, offset, file_type, output_path)
                return

            # Parse size result into components
            file_size, is_xbox360, script_name, is_complete = self._parse_size_result(size_result)

            if file_size is None or file_size < min_size or file_size > max_size:
                return

            # Read and save file
            file_handle.seek(offset)
            file_data = file_handle.read(file_size)

            self.stats[file_type] += 1
            filename = self._generate_filename(file_type, extension, is_xbox360, script_name, is_complete, offset)

            # Organize by file type in subdirectories
            type_dir = os.path.join(output_path, file_type)
            os.makedirs(type_dir, exist_ok=True)
            output_file = os.path.join(type_dir, filename)

            # Handle duplicate filenames - skip if identical content exists
            if os.path.exists(output_file):
                with open(output_file, "rb") as existing:
                    if existing.read() == file_data:
                        logger.debug(f"Skipped duplicate: {filename} at offset {offset:08X}")
                        self.stats[file_type] -= 1  # Don't count duplicates
                        return
                # Different content, use offset suffix
                base, ext = os.path.splitext(filename)
                filename = f"{base}_off_{offset:08X}{ext}"
                output_file = os.path.join(type_dir, filename)

            with open(output_file, "wb") as out_f:
                out_f.write(file_data)

            # Add to manifest with relative path
            self.manifest.append(
                CarveEntry(file_type=file_type, offset=offset, size_in_dump=file_size, size_output=file_size, filename=f"{file_type}/{filename}", is_compressed=False)
            )

            logger.debug(f"Carved: {filename} ({format_size(file_size)})")

        except (IOError, OSError) as e:
            logger.warning(f"Failed to extract {file_type} at offset {offset:08X}: {e}")

    def _save_zlib_stream(self, zlib_info: Dict[str, Any], offset: int, file_type: str, output_path: str) -> None:
        """Save a decompressed zlib stream with appropriate naming."""
        try:
            content_type: str = zlib_info["content_type"]
            decompressed_data: bytes = zlib_info["decompressed_data"]
            extension: str = zlib_info["extension"]
            name_hint: Optional[str] = zlib_info.get("name_hint")
            compressed_size: int = zlib_info.get("compressed_size", 0)

            # Update statistics
            self.stats[file_type] += 1
            count = self.stats[file_type]

            # Create subdirectory for zlib content type
            subdir = os.path.join(output_path, f"zlib_{content_type}")
            os.makedirs(subdir, exist_ok=True)

            # Generate filename
            if name_hint:
                filename = f"{name_hint}_{count:04d}{extension}"
            else:
                filename = f"{content_type}_{count:04d}_off_{offset:08X}{extension}"

            output_file = os.path.join(subdir, filename)

            # Handle duplicates
            if os.path.exists(output_file):
                base, ext = os.path.splitext(filename)
                filename = f"{base}_off_{offset:08X}{ext}"
                output_file = os.path.join(subdir, filename)

            # Write decompressed data
            with open(output_file, "wb") as out_f:
                out_f.write(decompressed_data)

            # Add to manifest with compressed size for accurate coverage
            self.manifest.append(
                CarveEntry(
                    file_type=file_type,
                    offset=offset,
                    size_in_dump=compressed_size,  # Original compressed size in dump
                    size_output=len(decompressed_data),  # Decompressed output size
                    filename=f"zlib_{content_type}/{filename}",
                    is_compressed=True,
                    content_type=content_type,
                )
            )

            logger.debug(f"Carved zlib: {filename} ({format_size(len(decompressed_data))} decompressed, {zlib_info['compression_ratio']:.1f}x ratio)")

        except Exception as e:
            logger.warning(f"Failed to save zlib stream at offset {offset:08X}: {e}")

    def _determine_file_size(self, header_data: bytes, file_type: str, sig_info: SignatureInfo) -> Optional[Union[int, tuple[Any, ...], Dict[str, Any]]]:
        """Determine the size of a file based on its header.
        Returns:
            - int: file size
            - tuple[int, bool]: (file_size, is_xbox360) for DDS files
            - tuple[int, bool, str, bool]: (file_size, is_xbox360, script_name, is_complete) for scripts
            - dict: zlib info for decompressed streams
            - None: if invalid
        """
        parser = self.parsers.get(file_type)
        max_size = int(sig_info.get("max_size", 100000))
        min_size = int(sig_info.get("min_size", 0))

        # Route to appropriate handler based on file type
        if file_type == "dds":
            return self._handle_dds_size(parser, header_data, max_size)

        if file_type in ("ddx_3xdo", "ddx_3xdr", "nif", "kf", "egm", "egt", "lip", "png"):
            return self._handle_estimated_size(parser, header_data, max_size)

        if file_type in ("xma", "bik"):
            return self._handle_file_size(parser, header_data, max_size)

        if file_type in ("esp", "esm", "bsa"):
            return self._handle_game_data_size(header_data, min_size, max_size)

        if file_type in ("script_scn", "script_sn"):
            return self._handle_script_size(parser, header_data, max_size)

        if file_type in ("zlib_default", "zlib_best"):
            return self._handle_zlib_size(parser, header_data)

        # Xbox 360 system formats
        if file_type in ("xex", "xdbf", "xuis", "xuib", "pirs", "con"):
            return self._handle_estimated_size(parser, header_data, max_size)

        # Default: use a conservative estimate
        return min_size if min_size > 0 else 1024

    def _handle_dds_size(self, parser: Optional[ParserType], header_data: bytes, max_size: int) -> Optional[Tuple[int, bool]]:
        """Handle DDS file size determination."""
        if parser:
            header_info = parser.parse_header(header_data)
            if header_info:
                return (min(header_info["estimated_size"], max_size), header_info.get("is_xbox360", False))
        return None

    def _handle_estimated_size(self, parser: Optional[ParserType], header_data: bytes, max_size: int) -> Optional[int]:
        """Handle file types that use estimated_size from parse_header."""
        if parser:
            header_info = parser.parse_header(header_data)
            if header_info:
                return min(header_info["estimated_size"], max_size)
        return None

    def _handle_file_size(self, parser: Optional[ParserType], header_data: bytes, max_size: int) -> Optional[int]:
        """Handle file types that use file_size from parse_header."""
        if parser:
            header_info = parser.parse_header(header_data)
            if header_info:
                return min(header_info["file_size"], max_size)
        return None

    def _handle_game_data_size(self, header_data: bytes, min_size: int, max_size: int) -> Optional[int]:
        """Handle game data files with size in header at offset 4."""
        if len(header_data) >= 8:
            try:
                size = int.from_bytes(header_data[4:8], byteorder="little")
                if min_size <= size <= max_size:
                    return size
            except (ValueError, struct.error):
                pass
        return None

    def _handle_script_size(self, parser: Optional[ParserType], header_data: bytes, max_size: int) -> Optional[Tuple[int, bool, str, bool]]:
        """Handle Bethesda ObScript file size determination."""
        if parser:
            script_info = parser.parse_script(header_data)
            if script_info:
                return (min(script_info["size"], max_size), False, script_info["name"], script_info.get("is_complete", False))
        return None

    def _handle_zlib_size(self, parser: Optional[ParserType], header_data: bytes) -> Optional[Dict[str, Any]]:
        """Handle zlib compressed stream size determination."""
        if parser:
            result = parser.try_decompress(header_data)
            if result and result["decompressed_size"] > 50:
                return result
        return None

    def _save_manifest(self, output_path: str) -> None:
        """Save the carve manifest to a JSON file."""
        manifest_path = os.path.join(output_path, "carve_manifest.json")

        # Build by-type summary
        by_type: Dict[str, Dict[str, int]] = {}
        for entry in self.manifest:
            if entry.file_type not in by_type:
                by_type[entry.file_type] = {"count": 0, "bytes_in_dump": 0, "bytes_output": 0}
            by_type[entry.file_type]["count"] += 1
            by_type[entry.file_type]["bytes_in_dump"] += entry.size_in_dump
            by_type[entry.file_type]["bytes_output"] += entry.size_output

        manifest_data: Dict[str, Any] = {
            "entries": [asdict(entry) for entry in self.manifest],
            "summary": {
                "total_files": len(self.manifest),
                "total_bytes_in_dump": sum(e.size_in_dump for e in self.manifest),
                "total_bytes_output": sum(e.size_output for e in self.manifest),
                "by_type": by_type,
            },
        }

        with open(manifest_path, "w") as f:
            json.dump(manifest_data, f, indent=2)
        logger.debug(f"Saved manifest to {manifest_path}")

    def _print_statistics(self):
        """Print carving statistics."""
        logger.info("\n=== Carving Statistics ===")
        total_files = 0
        for file_type, count in sorted(self.stats.items()):
            if count > 0:
                logger.info(f"  {file_type:20s}: {count:5d} files")
                total_files += count
        logger.info(f"  {'Total':20s}: {total_files:5d} files")
        logger.info("=" * 40)

    def get_statistics(self) -> Dict[str, int]:
        """Get carving statistics."""
        return self.stats.copy()
