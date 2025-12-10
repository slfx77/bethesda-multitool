"""
Minidump (.dmp) file parser and PE extractor.
Properly handles Xbox 360 memory dumps with fragmented memory regions.
"""

import struct
import logging
from pathlib import Path
from typing import List, Dict, Optional, Any, BinaryIO, Tuple

logger = logging.getLogger(__name__)

# Type aliases
ModuleInfo = Dict[str, Any]
MemoryRange = Dict[str, Any]
StreamInfo = Dict[str, int]


class MinidumpExtractor:
    """Extracts PE files (EXE/DLL) from Windows/Xbox 360 minidump files."""

    def __init__(self, output_dir: str):
        """
        Initialize the minidump extractor.

        Args:
            output_dir: Directory to save extracted modules
        """
        self.output_dir = Path(output_dir)
        self.output_dir.mkdir(parents=True, exist_ok=True)

    def _parse_header(self, f: BinaryIO) -> Tuple[int, int]:
        """Parse MDMP header and return num_streams and stream_dir_rva."""
        magic = f.read(4)
        if magic != b"MDMP":
            raise ValueError(f"Not a valid minidump file: {magic}")

        f.read(4)  # Skip version
        num_streams = struct.unpack("<I", f.read(4))[0]
        stream_dir_rva = struct.unpack("<I", f.read(4))[0]
        return num_streams, stream_dir_rva

    def _find_streams(self, f: BinaryIO, stream_dir_rva: int, num_streams: int) -> Tuple[Optional[StreamInfo], Optional[StreamInfo]]:
        """Find module stream and memory64 stream."""
        f.seek(stream_dir_rva)
        module_stream: Optional[StreamInfo] = None
        memory64_stream: Optional[StreamInfo] = None

        for _ in range(num_streams):
            stream_type = struct.unpack("<I", f.read(4))[0]
            data_size = struct.unpack("<I", f.read(4))[0]
            rva = struct.unpack("<I", f.read(4))[0]

            if stream_type == 4:  # ModuleListStream
                module_stream = {"rva": rva, "size": data_size}
            elif stream_type == 9:  # Memory64ListStream
                memory64_stream = {"rva": rva, "size": data_size}

        return module_stream, memory64_stream

    def _parse_modules(self, f: BinaryIO, module_stream: StreamInfo) -> List[ModuleInfo]:
        """Parse module list from dump."""
        f.seek(module_stream["rva"])
        num_modules = struct.unpack("<I", f.read(4))[0]
        logger.info(f"Found {num_modules} modules in dump")

        modules: List[ModuleInfo] = []
        for _ in range(num_modules):
            base_addr = struct.unpack("<Q", f.read(8))[0]
            size = struct.unpack("<I", f.read(4))[0]
            _checksum = struct.unpack("<I", f.read(4))[0]
            timestamp = struct.unpack("<I", f.read(4))[0]
            name_rva = struct.unpack("<I", f.read(4))[0]
            f.read(108 - 24)  # Skip rest of MINIDUMP_MODULE

            modules.append({"base": base_addr, "size": size, "name_rva": name_rva, "timestamp": timestamp})

        # Read module names
        for mod in modules:
            f.seek(mod["name_rva"])
            name_len = struct.unpack("<I", f.read(4))[0]
            name_bytes = f.read(name_len)
            mod["name"] = name_bytes.decode("utf-16-le").rstrip("\x00")

        return modules

    def _parse_memory_ranges(self, f: BinaryIO, memory64_stream: StreamInfo) -> List[MemoryRange]:
        """Parse memory64 list from dump."""
        f.seek(memory64_stream["rva"])
        num_ranges = struct.unpack("<Q", f.read(8))[0]
        base_rva = struct.unpack("<Q", f.read(8))[0]

        memory_ranges: List[MemoryRange] = []
        for _ in range(num_ranges):
            start_va = struct.unpack("<Q", f.read(8))[0]
            data_size = struct.unpack("<Q", f.read(8))[0]
            memory_ranges.append({"va": start_va, "size": data_size})

        # Calculate file offsets
        current_offset: int = base_rva
        for r in memory_ranges:
            r["file_offset"] = current_offset
            current_offset += r["size"]

        return memory_ranges

    def extract_modules(self, dump_path: str) -> List[ModuleInfo]:
        """
        Extract all loaded modules (EXE/DLL) from a minidump file.
        Handles Xbox 360 PowerPC dumps with fragmented memory regions.

        Args:
            dump_path: Path to the .dmp file

        Returns:
            List of dictionaries with module information
        """
        extracted_modules: List[ModuleInfo] = []

        try:
            logger.info(f"Parsing minidump: {dump_path}")

            with open(dump_path, "rb") as f:
                num_streams, stream_dir_rva = self._parse_header(f)
                module_stream, memory64_stream = self._find_streams(f, stream_dir_rva, num_streams)

                if not module_stream:
                    logger.warning("No ModuleListStream found")
                    return []

                modules = self._parse_modules(f, module_stream)
                memory_ranges = self._parse_memory_ranges(f, memory64_stream) if memory64_stream else []

                # Extract each module
                for idx, mod in enumerate(modules):
                    try:
                        module_info = self._extract_module_from_ranges(f, mod, memory_ranges, idx)
                        if module_info:
                            extracted_modules.append(module_info)
                    except Exception as e:
                        logger.debug(f"Error extracting module {mod['name']}: {e}")

                self._save_module_list(dump_path, modules)

            logger.info(f"Successfully extracted {len(extracted_modules)} modules")

        except Exception as e:
            logger.error(f"Error parsing minidump {dump_path}: {e}")

        return extracted_modules

    def _extract_module_from_ranges(self, f: BinaryIO, mod: ModuleInfo, memory_ranges: List[MemoryRange], idx: int) -> Optional[ModuleInfo]:
        """
        Extract a module by collecting all memory ranges within its address space.
        """
        mod_start = mod["base"]
        mod_end = mod["base"] + mod["size"]
        mod_name = mod["name"]

        # Find all memory ranges that fall within this module's address space
        module_ranges: List[MemoryRange] = []
        for r in memory_ranges:
            r_start: int = r["va"]
            r_end: int = r["va"] + r["size"]

            # Check if this range overlaps with the module (start or end within bounds)
            if (r_start >= mod_start and r_start < mod_end) or (r_end > mod_start and r_end <= mod_end):
                module_ranges.append(r)

        if not module_ranges:
            logger.debug(f"No memory regions found for {mod_name}")
            return None

        # Sort ranges by VA
        module_ranges.sort(key=lambda x: x["va"])

        # Create buffer for the full module
        module_data = bytearray(mod["size"])

        # Fill in data from each range
        bytes_filled = 0
        for r in module_ranges:
            offset_in_module = r["va"] - mod_start
            if offset_in_module < 0 or offset_in_module >= mod["size"]:
                continue

            f.seek(r["file_offset"])
            data = f.read(r["size"])

            copy_size = min(len(data), mod["size"] - offset_in_module)
            module_data[offset_in_module : offset_in_module + copy_size] = data[:copy_size]
            bytes_filled += copy_size

        # Check header
        header = bytes(module_data[:4])
        if header[:2] != b"MZ":
            logger.debug(f"Module {mod_name} doesn't have MZ header")
            return None

        # Verify PE signature
        pe_offset = struct.unpack("<I", module_data[0x3C:0x40])[0]
        if pe_offset >= len(module_data) - 4:
            logger.debug(f"Invalid PE offset for {mod_name}")
            return None

        pe_sig = bytes(module_data[pe_offset : pe_offset + 4])
        if pe_sig != b"PE\x00\x00":
            logger.debug(f"Invalid PE signature for {mod_name}")
            return None

        # Get machine type
        machine = struct.unpack("<H", module_data[pe_offset + 4 : pe_offset + 6])[0]
        machine_names = {0x14C: "i386", 0x8664: "AMD64", 0x1F0: "PowerPC", 0x1F1: "PowerPC FP", 0x1F2: "PowerPC BE"}
        machine_name = machine_names.get(machine, f"0x{machine:X}")

        # Use original filename
        safe_name = Path(mod_name).name
        if not safe_name.lower().endswith((".exe", ".dll", ".xex")):
            safe_name += ".dll"

        # Save module with original name
        output_path = self.output_dir / safe_name
        with open(output_path, "wb") as out:
            out.write(module_data)

        coverage = (bytes_filled / mod["size"]) * 100
        logger.info(f"Extracted {safe_name}: {machine_name}, {bytes_filled:,}/{mod['size']:,} bytes ({coverage:.1f}%)")

        return {
            "name": safe_name,
            "path": str(output_path),
            "base_address": mod["base"],
            "size": len(module_data),
            "bytes_filled": bytes_filled,
            "coverage": coverage,
            "machine": machine_name,
            "index": idx,
        }

    def _save_module_list(self, dump_path: str, modules: List[ModuleInfo]) -> None:
        """Save module list to a text file."""
        module_list_path = self.output_dir / "module_list.txt"
        with open(module_list_path, "w") as f:
            f.write(f"Modules found in {Path(dump_path).name}:\n")
            f.write("=" * 80 + "\n\n")
            for i, mod in enumerate(modules, 1):
                f.write(f"{i}. {mod['name']}\n")
                f.write(f"   Base Address: 0x{mod['base']:016X}\n")
                f.write(f"   Size: {mod['size']:,} bytes ({mod['size'] / 1024 / 1024:.2f} MB)\n")
                f.write(f"   Timestamp: {mod['timestamp']}\n")
                f.write("\n")
        logger.debug(f"Module list saved to: {module_list_path}")
