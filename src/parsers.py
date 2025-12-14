"""
File parsers for different formats found in memory dumps.
Handles Xbox 360 and PC-specific format variations.
"""

import struct
from typing import Optional, Dict, Any, Tuple
from .utils import read_uint32_le, read_uint32_be


class DDSParser:
    """Parser for DDS (DirectDraw Surface) texture files."""

    @staticmethod
    def parse_script(data: bytes, offset: int = 0, max_size: int = 100000) -> Optional[Dict[str, Any]]:
        """Not used for DDS - use parse_header instead."""
        return None

    @staticmethod
    def try_decompress(data: bytes, offset: int = 0, max_compressed_size: int = 1048576) -> Optional[Dict[str, Any]]:
        """Not used for DDS."""
        return None

    @staticmethod
    def _get_bytes_per_block(fourcc_str: str) -> int:
        """Get bytes per block for the given FourCC format."""
        if fourcc_str == "DXT1":
            return 8
        if fourcc_str in ("DXT2", "DXT3", "DXT4", "DXT5"):
            return 16
        if fourcc_str in ("ATI1", "BC4U", "BC4S"):
            return 8
        if fourcc_str in ("ATI2", "BC5U", "BC5S"):
            return 16
        return 16  # Default for uncompressed

    @staticmethod
    def _calculate_mipmap_size(width: int, height: int, mipmap_count: int, bytes_per_block: int) -> int:
        """Calculate total size including mipmaps for compressed formats."""
        blocks_wide = (width + 3) // 4
        blocks_high = (height + 3) // 4
        estimated_size = blocks_wide * blocks_high * bytes_per_block

        if mipmap_count > 1:
            mip_width, mip_height = width, height
            for _ in range(1, min(mipmap_count, 16)):
                mip_width = max(1, mip_width // 2)
                mip_height = max(1, mip_height // 2)
                mip_blocks_wide = max(1, (mip_width + 3) // 4)
                mip_blocks_high = max(1, (mip_height + 3) // 4)
                estimated_size += mip_blocks_wide * mip_blocks_high * bytes_per_block

        return estimated_size

    @staticmethod
    def _handle_uncompressed(height: int, pitch: int, mipmap_count: int, bytes_per_block: int) -> int:
        """Calculate size for uncompressed formats using pitch."""
        estimated_size = pitch * height
        if mipmap_count > 1:
            mip_size = estimated_size
            for _ in range(1, min(mipmap_count, 16)):
                mip_size //= 4
                estimated_size += max(mip_size, bytes_per_block)
        return estimated_size

    @staticmethod
    def parse_header(data: bytes, offset: int = 0) -> Optional[Dict[str, Any]]:
        """
        Parse DDS header from data.

        Args:
            data: Data containing DDS header
            offset: Starting offset of DDS header

        Returns:
            Dictionary with header information or None if invalid
        """
        if len(data) < offset + 128:
            return None

        header_data = data[offset : offset + 128]

        try:
            magic = header_data[0:4]
            if magic != b"DDS ":
                return None

            header_size = read_uint32_le(header_data, 4)
            height = read_uint32_le(header_data, 12)
            width = read_uint32_le(header_data, 16)
            pitch_or_linear_size = read_uint32_le(header_data, 20)
            mipmap_count = read_uint32_le(header_data, 28)
            fourcc = header_data[84:88]
            endianness = "little"

            # Check if values are reasonable for little-endian, try big-endian if not
            if height > 16384 or width > 16384 or header_size != 124:
                height = read_uint32_be(header_data, 12)
                width = read_uint32_be(header_data, 16)
                pitch_or_linear_size = read_uint32_be(header_data, 20)
                mipmap_count = read_uint32_be(header_data, 28)
                endianness = "big"

            if height == 0 or width == 0 or height > 16384 or width > 16384:
                return None

            fourcc_str = fourcc.decode("ascii", errors="ignore").strip("\x00")
            bytes_per_block = DDSParser._get_bytes_per_block(fourcc_str)

            # Handle uncompressed formats with pitch
            if fourcc_str not in ("DXT1", "DXT2", "DXT3", "DXT4", "DXT5", "ATI1", "BC4U", "BC4S", "ATI2", "BC5U", "BC5S"):
                if pitch_or_linear_size > 0:
                    estimated_size = DDSParser._handle_uncompressed(height, pitch_or_linear_size, mipmap_count, bytes_per_block)
                    return {
                        "width": width,
                        "height": height,
                        "mipmap_count": mipmap_count,
                        "fourcc": fourcc_str,
                        "endianness": endianness,
                        "estimated_size": estimated_size + 128,
                        "pitch": pitch_or_linear_size,
                        "is_xbox360": endianness == "big",
                    }

            estimated_size = DDSParser._calculate_mipmap_size(width, height, mipmap_count, bytes_per_block)

            return {
                "width": width,
                "height": height,
                "mipmap_count": mipmap_count,
                "fourcc": fourcc_str,
                "endianness": endianness,
                "estimated_size": estimated_size + 128,
                "pitch": pitch_or_linear_size,
                "is_xbox360": endianness == "big",
            }
        except (struct.error, IndexError):
            return None


class XMAParser:
    """Parser for Xbox Media Audio (XMA) files."""

    XMA_FORMAT_CODES = (0x0165, 0x0166)

    @staticmethod
    def parse_script(data: bytes, offset: int = 0, max_size: int = 100000) -> Optional[Dict[str, Any]]:
        """Not used for XMA - use parse_header instead."""
        return None

    @staticmethod
    def try_decompress(data: bytes, offset: int = 0, max_compressed_size: int = 1048576) -> Optional[Dict[str, Any]]:
        """Not used for XMA."""
        return None

    @staticmethod
    def _is_xma_chunk(data: bytes, search_offset: int) -> bool:
        """Check if the chunk at the given offset indicates XMA format."""
        chunk_id = data[search_offset : search_offset + 4]

        if chunk_id == b"XMA2":
            return True

        if chunk_id == b"fmt " and len(data) >= search_offset + 10:
            format_tag = read_uint32_le(data, search_offset + 8) & 0xFFFF
            if format_tag in XMAParser.XMA_FORMAT_CODES:
                return True

        return False

    @staticmethod
    def parse_header(data: bytes, offset: int = 0) -> Optional[Dict[str, Any]]:
        """
        Parse XMA/RIFF header from data.

        Args:
            data: Data containing XMA header
            offset: Starting offset of XMA header

        Returns:
            Dictionary with header information or None if invalid
        """
        if len(data) < offset + 12:
            return None

        try:
            riff_magic = data[offset : offset + 4]
            if riff_magic != b"RIFF":
                return None

            file_size = read_uint32_le(data, offset + 4) + 8
            format_type = data[offset + 8 : offset + 12]

            if format_type != b"WAVE":
                return None

            # Search through chunks to find XMA format information
            search_offset = offset + 12
            while search_offset < min(offset + 200, len(data) - 8):
                if len(data) < search_offset + 8:
                    break

                if XMAParser._is_xma_chunk(data, search_offset):
                    return {"format": "XMA", "file_size": file_size, "is_xma": True}

                chunk_size = read_uint32_le(data, search_offset + 4)
                search_offset += 8 + ((chunk_size + 1) & ~1)

            return None
        except (struct.error, IndexError):
            return None


class NIFParser:
    """Parser for NetImmerse/Gamebryo (NIF) model files."""

    @staticmethod
    def parse_script(data: bytes, offset: int = 0, max_size: int = 100000) -> Optional[Dict[str, Any]]:
        """Not used for NIF - use parse_header instead."""
        return None

    @staticmethod
    def try_decompress(data: bytes, offset: int = 0, max_compressed_size: int = 1048576) -> Optional[Dict[str, Any]]:
        """Not used for NIF."""
        return None

    @staticmethod
    def parse_header(data: bytes, offset: int = 0) -> Optional[Dict[str, Any]]:
        """
        Parse NIF header from data.

        Args:
            data: Data containing NIF header
            offset: Starting offset of NIF header

        Returns:
            Dictionary with header information or None if invalid
        """
        if len(data) < offset + 64:
            return None

        try:
            header_magic = data[offset : offset + 20]
            if header_magic != b"Gamebryo File Format":
                return None

            # NIF files have version info after the magic
            version_offset = offset + 22

            # Look for null terminator after header
            null_pos = data.find(b"\x00", version_offset, version_offset + 40)
            if null_pos == -1:
                return None

            version_string = data[version_offset:null_pos].decode("ascii", errors="ignore")

            # Try to parse the header structure for a better size estimate
            # NIF structure (after version string): endianness, user_version, num_blocks, etc.
            estimated_size = 50000  # Default fallback

            try:
                # Skip past version string and additional nulls
                parse_offset = null_pos + 1

                # Skip past user version string (may be present)
                # Look for endianness byte (0x01 for little endian)
                # This varies by NIF version, so we'll use heuristics

                # For NIF 20.x (Oblivion, FO3, FNV), structure is more predictable
                # Version string includes ", Version " prefix
                if "20." in version_string:
                    # Try to read block count (usually within first 100 bytes of header)
                    if len(data) >= offset + 100:
                        # Scan for reasonable block count (typically 1-10000)
                        for test_offset in range(parse_offset, min(parse_offset + 60, len(data) - 4), 4):
                            potential_blocks = read_uint32_le(data, test_offset)
                            if 1 <= potential_blocks <= 10000:
                                # Estimate: average 500 bytes per block (conservative)
                                estimated_size = min(potential_blocks * 500 + 1000, 20 * 1024 * 1024)
                                break

            except (struct.error, IndexError):
                pass  # Use default estimate

            return {"format": "NIF", "version": version_string, "estimated_size": estimated_size}
        except (struct.error, IndexError):
            return None


class ScriptParser:
    """Parser for Bethesda script files."""

    # Script headers that indicate the start of a new script
    SCRIPT_HEADERS = [b"scn ", b"Scn ", b"SCN ", b"ScriptName ", b"scriptname ", b"SCRIPTNAME "]

    @staticmethod
    def parse_header(data: bytes, offset: int = 0) -> Optional[Dict[str, Any]]:
        """Not used for scripts - use parse_script instead."""
        return None

    @staticmethod
    def try_decompress(data: bytes, offset: int = 0, max_compressed_size: int = 1048576) -> Optional[Dict[str, Any]]:
        """Not used for scripts."""
        return None

    @staticmethod
    def _extract_script_name(first_line: str) -> Optional[str]:
        """Extract and validate script name from the first line."""
        script_name = ""
        lower_line = first_line.lower()

        if lower_line.startswith("scn "):
            script_name = first_line[4:].strip()
        elif lower_line.startswith("scriptname "):
            script_name = first_line[11:].strip()
        else:
            return None

        # Remove any trailing comments or invalid characters
        for char in [";", "\r", "\t", " "]:
            if char in script_name:
                script_name = script_name.split(char)[0]

        # Validate script name - should be a single word (alphanumeric + underscore)
        if not script_name or not all(c.isalnum() or c == "_" for c in script_name):
            return None

        return script_name

    @staticmethod
    def _find_script_end(script_data: bytes, first_line_end: int) -> int:
        """Find where the script ends using multiple heuristics."""
        end_pos = len(script_data)
        search_start = first_line_end + 1

        # Heuristic 1: Find next script header
        for header in ScriptParser.SCRIPT_HEADERS:
            next_script = script_data.find(header, search_start)
            if next_script != -1:
                boundary = script_data.rfind(b"\n", 0, next_script)
                end_pos = min(end_pos, boundary if boundary != -1 else next_script)

        # Heuristic 2: Stop at garbage (null bytes or non-printable chars)
        for i in range(len(script_data[:end_pos])):
            byte = script_data[i]
            if byte == 0 or (byte < 32 and byte not in (9, 10, 13)) or byte > 126:
                end_pos = min(end_pos, i)
                break

        # Trim trailing whitespace
        while end_pos > 0 and script_data[end_pos - 1] in (9, 10, 13, 32):
            end_pos -= 1

        return end_pos

    @staticmethod
    def parse_script(data: bytes, offset: int = 0, max_size: int = 100000) -> Optional[Dict[str, Any]]:
        """
        Parse Bethesda script from data.

        Scripts may be truncated/corrupted in memory dumps. We extract until:
        1. We hit garbage (null bytes or non-printable chars)
        2. We find the next script header (scripts are often stored sequentially)

        Args:
            data: Data containing script
            offset: Starting offset of script
            max_size: Maximum size to extract

        Returns:
            Dictionary with script information or None if invalid
        """
        if len(data) < offset + 10:
            return None

        try:
            max_end = min(offset + max_size, len(data))
            script_data = data[offset:max_end]

            # Find first line
            first_line_end = script_data.find(b"\n")
            if first_line_end == -1:
                return None

            first_line = script_data[:first_line_end].decode("ascii", errors="ignore").strip()

            # Extract and validate script name
            script_name = ScriptParser._extract_script_name(first_line)
            if script_name is None:
                return None

            # Validate there's meaningful content after header
            remaining = script_data[first_line_end + 1 :]
            if len(remaining) < 5 or remaining.find(b"\n") == -1:
                return None

            # Sanitize script name for filename
            safe_name = "".join(c for c in script_name if c.isalnum() or c in "_-") or "unknown"

            # Find script end
            end_pos = ScriptParser._find_script_end(script_data, first_line_end)
            if end_pos < 10:
                return None

            # Check if script is complete
            script_text = script_data[:end_pos].decode("ascii", errors="ignore").lower()
            is_complete = "\nend" in script_text or script_text.rstrip().endswith("end")

            return {"type": "script", "name": safe_name, "size": end_pos, "is_complete": is_complete}
        except UnicodeDecodeError:
            return None


class LIPParser:
    """Parser for LIP (lip-sync) files."""

    @staticmethod
    def parse_script(data: bytes, offset: int = 0, max_size: int = 100000) -> Optional[Dict[str, Any]]:
        """Not used for LIP - use parse_header instead."""
        return None

    @staticmethod
    def try_decompress(data: bytes, offset: int = 0, max_compressed_size: int = 1048576) -> Optional[Dict[str, Any]]:
        """Not used for LIP."""
        return None

    @staticmethod
    def parse_header(data: bytes, offset: int = 0) -> Optional[Dict[str, Any]]:
        """Parse LIP header from data."""
        if len(data) < offset + 8:
            return None

        try:
            magic = data[offset : offset + 4]
            if magic != b"LIPS":
                return None

            # LIP files have size info but it's embedded
            # Use conservative estimate
            return {"format": "LIP", "estimated_size": 10000}
        except (struct.error, IndexError):
            return None


class BIKParser:
    """Parser for Bink video files."""

    @staticmethod
    def parse_script(data: bytes, offset: int = 0, max_size: int = 100000) -> Optional[Dict[str, Any]]:
        """Not used for BIK - use parse_header instead."""
        return None

    @staticmethod
    def try_decompress(data: bytes, offset: int = 0, max_compressed_size: int = 1048576) -> Optional[Dict[str, Any]]:
        """Not used for BIK."""
        return None

    @staticmethod
    def parse_header(data: bytes, offset: int = 0) -> Optional[Dict[str, Any]]:
        """Parse BIK header from data."""
        if len(data) < offset + 8:
            return None

        try:
            magic = data[offset : offset + 4]
            if magic != b"BIKi":
                return None

            # Bink has file size at offset 4
            file_size = read_uint32_le(data, offset + 4)

            if file_size < 20 or file_size > 500 * 1024 * 1024:
                return None

            return {"format": "BIK", "file_size": file_size + 8}  # Add header size
        except (struct.error, IndexError):
            return None


class ZlibParser:
    """Parser for zlib compressed data."""

    ZLIB_HEADERS = (b"\x78\x9c", b"\x78\xda", b"\x78\x01", b"\x78\x5e")
    TEST_SIZES = [1024, 4096, 16384, 65536, 256 * 1024, 1024 * 1024]

    @staticmethod
    def _find_actual_size(data: bytes, offset: int, test_size: int, decompressed: bytes) -> Optional[Dict[str, Any]]:
        """Find actual compressed size by looking for zlib stream end."""
        import zlib

        decompressor = zlib.decompressobj()
        for i in range(offset, offset + test_size):
            try:
                decompressor.decompress(data[offset : i + 1])
                if decompressor.eof:
                    actual_size = i - offset + 1
                    return {
                        "format": "zlib",
                        "compressed_size": actual_size,
                        "decompressed_size": len(decompressed),
                        "estimated_size": actual_size,
                        "compression_ratio": len(decompressed) / actual_size if actual_size > 0 else 0,
                    }
            except Exception:
                continue
        return None

    @staticmethod
    def parse_header(data: bytes, offset: int = 0) -> Optional[Dict[str, Any]]:
        """
        Parse zlib header and attempt decompression to determine size.

        Args:
            data: Data containing zlib stream
            offset: Starting offset

        Returns:
            Dictionary with header info or None if invalid
        """
        if len(data) < offset + 6:
            return None

        try:
            import zlib

            header = data[offset : offset + 2]
            if header not in ZlibParser.ZLIB_HEADERS:
                return None

            max_test_size = min(len(data) - offset, 10 * 1024 * 1024)

            for test_size in ZlibParser.TEST_SIZES + [max_test_size]:
                test_size = min(test_size, max_test_size)

                try:
                    compressed_data = data[offset : offset + test_size]
                    decompressed = zlib.decompress(compressed_data)

                    result = ZlibParser._find_actual_size(data, offset, test_size, decompressed)
                    if result:
                        return result

                    return {"format": "zlib", "estimated_size": test_size, "decompressed_size": len(decompressed)}
                except zlib.error:
                    if test_size >= max_test_size:
                        return None
                    continue

            return None
        except Exception:
            return None


class PEParser:
    """Parser for PE (Portable Executable) files - EXE/DLL."""

    MACHINE_TYPES = {
        0x01F2: "Xbox 360 (PowerPC)",
        0x014C: "x86",
        0x8664: "x64",
    }

    @staticmethod
    def _get_machine_type(machine: int) -> str:
        """Get machine type string from machine code."""
        return PEParser.MACHINE_TYPES.get(machine, "Unknown")

    @staticmethod
    def _calculate_size_from_sections(data: bytes, section_table_offset: int, num_sections: int) -> int:
        """Calculate file size from PE sections."""
        max_offset = 0
        for i in range(min(num_sections, 96)):
            section_offset = section_table_offset + (i * 40)
            if section_offset + 40 > len(data):
                break

            raw_data_ptr = read_uint32_le(data, section_offset + 20)
            raw_data_size = read_uint32_le(data, section_offset + 16)
            section_end = raw_data_ptr + raw_data_size

            if section_end > max_offset and section_end < 100 * 1024 * 1024:
                max_offset = section_end

        return max_offset if max_offset > 0 else 4096

    @staticmethod
    def parse_header(data: bytes, offset: int = 0) -> Optional[Dict[str, Any]]:
        """
        Parse PE header from data.

        Args:
            data: Data containing PE header
            offset: Starting offset of PE header

        Returns:
            Dictionary with header information or None if invalid
        """
        if len(data) < offset + 64:
            return None

        try:
            magic = data[offset : offset + 2]
            if magic != b"MZ":
                return None

            if len(data) < offset + 0x3C + 4:
                return None

            pe_offset = read_uint32_le(data, offset + 0x3C)

            # Validate PE offset
            if pe_offset > len(data) - 24 or pe_offset > 1024:
                return {"estimated_size": 512, "format": "PE/DOS"}

            if len(data) < offset + pe_offset + 24:
                return {"estimated_size": 512, "format": "PE/DOS"}

            pe_sig = data[offset + pe_offset : offset + pe_offset + 4]
            if pe_sig != b"PE\x00\x00":
                return {"estimated_size": 512, "format": "DOS"}

            # Read COFF header
            machine = read_uint32_le(data, offset + pe_offset + 4) & 0xFFFF
            num_sections = read_uint32_le(data, offset + pe_offset + 6) & 0xFFFF
            size_of_optional_header = read_uint32_le(data, offset + pe_offset + 20) & 0xFFFF
            machine_type = PEParser._get_machine_type(machine)

            section_table_offset = offset + pe_offset + 24 + size_of_optional_header

            if len(data) < section_table_offset + (num_sections * 40):
                return {"estimated_size": 4096, "format": "PE", "machine": machine_type}

            estimated_size = PEParser._calculate_size_from_sections(data, section_table_offset, num_sections)

            return {
                "format": "PE",
                "machine": machine_type,
                "sections": num_sections,
                "estimated_size": estimated_size,
            }
        except (struct.error, IndexError):
            return None


class DDXParser:
    """Parser for Xbox 360 DDX texture files.

    DDX files are Xbox 360-specific texture containers that store compressed,
    tiled texture data. They use magic bytes '3XDO' (standard) or '3XDR' (engine-tiled).

    For conversion to standard DDS files, use DDXConv: https://github.com/kran27/DDXConv

    DDX Header Structure (based on DDXConv analysis):
    - Offset 0x00: Magic (4 bytes) - '3XDO' or '3XDR'
    - Offset 0x04: Priority bytes (3 bytes) - priorityL, priorityC, priorityH
    - Offset 0x07: Version (2 bytes) - Must be >= 3
    - Offset 0x08: D3DTexture header (52 bytes) - Contains format and dimension info
    - Offset 0x3C: Additional header data (8 bytes)
    - Offset 0x44: XMemCompress compressed texture data
    """

    MAGIC_3XDO = b"3XDO"  # 0x4F445833
    MAGIC_3XDR = b"3XDR"  # 0x52445833

    @staticmethod
    def parse_script(data: bytes, offset: int = 0, max_size: int = 100000) -> Optional[Dict[str, Any]]:
        """Not used for DDX - use parse_header instead."""
        return None

    @staticmethod
    def try_decompress(data: bytes, offset: int = 0, max_compressed_size: int = 1048576) -> Optional[Dict[str, Any]]:
        """Not used for DDX."""
        return None

    @staticmethod
    def parse_header(data: bytes, offset: int = 0) -> Optional[Dict[str, Any]]:
        """
        Parse DDX header from data.

        Args:
            data: Data containing DDX header
            offset: Starting offset of DDX header

        Returns:
            Dictionary with header information or None if invalid
        """
        if len(data) < offset + 68:  # Minimum size: magic + header
            return None

        try:
            magic = data[offset : offset + 4]

            if magic not in (DDXParser.MAGIC_3XDO, DDXParser.MAGIC_3XDR):
                return None

            format_type = "3XDO" if magic == DDXParser.MAGIC_3XDO else "3XDR"

            # Read version at offset 0x07 (little-endian uint16)
            version = struct.unpack("<H", data[offset + 7 : offset + 9])[0]

            if version < 3:
                return None  # Unsupported version

            # D3DTexture header starts at offset 0x08
            # Format dwords start at offset 0x18 (16 bytes into D3D header)
            # Dimension info is in dword5 at offset 0x08 + 36 = 0x2C

            # Read dimension dword (stored big-endian on Xbox 360)
            dword5_bytes = data[offset + 0x08 + 36 : offset + 0x08 + 40]
            dword5 = struct.unpack(">I", dword5_bytes)[0]  # Big-endian

            # Decode size_2d structure:
            # Bits 0-12: width - 1
            # Bits 13-25: height - 1
            width = (dword5 & 0x1FFF) + 1
            height = ((dword5 >> 13) & 0x1FFF) + 1

            # Validate dimensions
            if width == 0 or height == 0 or width > 4096 or height > 4096:
                return None

            # Read format info from dword3 and dword4
            # Format dwords start at file offset 0x18
            dword3 = struct.unpack("<I", data[offset + 0x18 + 12 : offset + 0x18 + 16])[0]
            dword4 = struct.unpack("<I", data[offset + 0x18 + 16 : offset + 0x18 + 20])[0]

            data_format = dword3 & 0xFF
            actual_format = (dword4 >> 24) & 0xFF
            if actual_format == 0:
                actual_format = data_format

            # Map format to DXT type
            format_name = DDXParser._get_format_name(actual_format)

            # Calculate expected compressed size (rough estimate)
            # DDX files use XMemCompress, so actual size varies
            # Estimate based on uncompressed DXT size with ~50% compression ratio
            block_size = 8 if format_name in ("DXT1", "ATI1", "BC4") else 16
            blocks_w = (width + 3) // 4
            blocks_h = (height + 3) // 4
            uncompressed_size = blocks_w * blocks_h * block_size

            # Rough estimate: compressed is typically 30-70% of uncompressed
            estimated_compressed = int(uncompressed_size * 0.7)
            estimated_size = 68 + estimated_compressed  # Header + compressed data

            return {
                "format_type": format_type,
                "version": version,
                "width": width,
                "height": height,
                "data_format": data_format,
                "actual_format": actual_format,
                "format_name": format_name,
                "estimated_size": estimated_size,
                "is_xbox360": True,
            }

        except (struct.error, IndexError):
            return None

    @staticmethod
    def _get_format_name(format_code: int) -> str:
        """Map Xbox 360 GPU format code to format name."""
        format_map = {
            0x12: "DXT1",
            0x13: "DXT3",
            0x14: "DXT5",
            0x52: "DXT1",
            0x53: "DXT3",
            0x54: "DXT5",
            0x71: "ATI2",  # Normal maps
            0x7B: "ATI1",  # Specular maps
            0x82: "DXT1",
            0x86: "DXT1",
            0x88: "DXT5",
        }
        return format_map.get(format_code, f"Unknown(0x{format_code:02X})")


class PNGParser:
    """Parser for PNG image files."""

    PNG_MAGIC = b"\x89PNG\r\n\x1a\n"
    IEND_CHUNK = b"IEND"

    @staticmethod
    def parse_script(data: bytes, offset: int = 0, max_size: int = 100000) -> Optional[Dict[str, Any]]:
        """Not used for PNG - use parse_header instead."""
        return None

    @staticmethod
    def try_decompress(data: bytes, offset: int = 0, max_compressed_size: int = 1048576) -> Optional[Dict[str, Any]]:
        """Not used for PNG."""
        return None

    @staticmethod
    def parse_header(data: bytes, offset: int = 0) -> Optional[Dict[str, Any]]:
        """
        Parse PNG file from data, finding the complete file size.

        PNG files consist of chunks, ending with IEND chunk.

        Args:
            data: Data containing PNG
            offset: Starting offset of PNG

        Returns:
            Dictionary with file information or None if invalid
        """
        if len(data) < offset + 33:  # Minimum PNG: header(8) + IHDR(25) = 33
            return None

        try:
            # Check PNG magic
            if data[offset : offset + 8] != PNGParser.PNG_MAGIC:
                return None

            # Parse chunks to find IEND
            chunk_offset = offset + 8  # Skip PNG signature
            file_size = 8  # Start with signature size

            while chunk_offset < len(data) - 12:
                # Chunk structure: length(4) + type(4) + data(length) + CRC(4)
                chunk_length = struct.unpack(">I", data[chunk_offset : chunk_offset + 4])[0]
                chunk_type = data[chunk_offset + 4 : chunk_offset + 8]

                # Sanity check chunk size
                if chunk_length > 50 * 1024 * 1024:  # 50MB max per chunk
                    break

                chunk_total = 4 + 4 + chunk_length + 4  # length + type + data + CRC
                file_size += chunk_total

                # Check if this is IEND (end of PNG)
                if chunk_type == PNGParser.IEND_CHUNK:
                    break

                chunk_offset += chunk_total

                # Safety limit
                if file_size > 50 * 1024 * 1024:
                    break

            # Get image dimensions from IHDR chunk (first chunk after signature)
            width = 0
            height = 0
            if len(data) >= offset + 24:
                ihdr_offset = offset + 8  # After PNG signature
                ihdr_type = data[ihdr_offset + 4 : ihdr_offset + 8]
                if ihdr_type == b"IHDR":
                    width = struct.unpack(">I", data[ihdr_offset + 8 : ihdr_offset + 12])[0]
                    height = struct.unpack(">I", data[ihdr_offset + 12 : ihdr_offset + 16])[0]

            return {
                "format": "PNG",
                "estimated_size": file_size,
                "width": width,
                "height": height,
            }
        except (struct.error, IndexError):
            return None


class ZlibStreamParser:
    """
    Parser for zlib-compressed data streams.

    Bethesda games (Fallout 3/NV, Skyrim) use zlib compression for:
    - Plugin data records (DATA/VNML heightmap data)
    - Embedded NIF models
    - Various binary data
    """

    # zlib header signatures (CMF + FLG bytes)
    # First byte (CMF): 0x78 = deflate compression, 32K window
    # Second byte (FLG): varies by compression level
    ZLIB_LOW = b"\x78\x01"  # No/low compression
    ZLIB_DEFAULT = b"\x78\x9c"  # Default compression
    ZLIB_BEST = b"\x78\xda"  # Best compression

    # Known decompressed content signatures
    NIF_MAGIC = b"Gamebryo File Format"
    DATA_CHUNK = b"DATA"

    @staticmethod
    def parse_header(data: bytes, offset: int = 0) -> Optional[Dict[str, Any]]:
        """Not used for zlib - use try_decompress instead."""
        return None

    @staticmethod
    def parse_script(data: bytes, offset: int = 0, max_size: int = 100000) -> Optional[Dict[str, Any]]:
        """Not used for zlib - use try_decompress instead."""
        return None

    # Mapping of file signatures to (content_type, extension)
    _SIGNATURE_MAP: Dict[bytes, Tuple[str, str]] = {
        b"DDS ": ("dds", ".dds"),
        b"3XDO": ("ddx", ".ddx"),
        b"3XDR": ("ddx", ".ddx"),
        b"TES4": ("esp", ".esp"),
        b"BSA\x00": ("bsa", ".bsa"),
        b"OggS": ("ogg", ".ogg"),
        b"\x89PNG": ("png", ".png"),
        b"BIKi": ("bik", ".bik"),
        b"LIPS": ("lip", ".lip"),
        b"XEX2": ("xex", ".xex"),
        b"XDBF": ("xdbf", ".xdbf"),
        b"XUIS": ("xui", ".xui"),
        b"XUIB": ("xui", ".xui"),
    }

    @staticmethod
    def _check_signature_match(decompressed: bytes) -> Optional[Tuple[str, str]]:
        """Check if data starts with a known signature."""
        for sig, (ctype, ext) in ZlibStreamParser._SIGNATURE_MAP.items():
            if decompressed.startswith(sig):
                return ctype, ext
        return None

    @staticmethod
    def _check_riff_type(decompressed: bytes) -> Tuple[str, str]:
        """Determine RIFF subtype."""
        if b"XMA2" in decompressed[:100] or b"fmt " in decompressed[:100]:
            return "xma", ".xma"
        return "riff", ".riff"

    @staticmethod
    def _check_xml_content(decompressed: bytes) -> Optional[Tuple[str, str]]:
        """Check if data is XML content."""
        try:
            text_sample = decompressed[:500].decode("utf-8", errors="strict")
            if "<" in text_sample and ">" in text_sample:
                return "xml", ".xml"
        except UnicodeDecodeError:
            pass
        return None

    @staticmethod
    def _check_text_content(decompressed: bytes) -> Optional[Tuple[str, str]]:
        """Check if data is plain text."""
        if len(decompressed) <= 20:
            return None
        try:
            text_sample = decompressed[: min(500, len(decompressed))]
            printable_count = sum(1 for b in text_sample if 32 <= b < 127 or b in (9, 10, 13))
            if printable_count / len(text_sample) > 0.85:
                return "text", ".txt"
        except Exception:
            pass
        return None

    @staticmethod
    def _determine_content_type(decompressed: bytes) -> Tuple[str, str, Optional[str]]:
        """Determine content type and extension from decompressed data."""
        name_hint: Optional[str] = None

        # Check NIF format (has special handling for author extraction)
        if decompressed.startswith(ZlibStreamParser.NIF_MAGIC):
            return "nif", ".nif", ZlibStreamParser._extract_nif_author(decompressed)

        # Check DATA chunk format
        if decompressed.startswith(ZlibStreamParser.DATA_CHUNK):
            if b"VNML" in decompressed[:20]:
                return "vnml", ".vnml", None
            return "data_chunk", ".data", None

        # Check simple signature matches
        sig_match = ZlibStreamParser._check_signature_match(decompressed)
        if sig_match:
            return sig_match[0], sig_match[1], None

        # Check RIFF format
        if decompressed.startswith(b"RIFF"):
            ctype, ext = ZlibStreamParser._check_riff_type(decompressed)
            return ctype, ext, None

        # Check script formats
        if decompressed[:3] in (b"scn", b"Scn", b"SCN") or decompressed.startswith(b"ScriptName"):
            return "script", ".txt", None

        # Check XML content
        if decompressed.startswith(b"<?xml") or decompressed.startswith(b"<"):
            xml_match = ZlibStreamParser._check_xml_content(decompressed)
            if xml_match:
                return xml_match[0], xml_match[1], None

        # Check plain text
        text_match = ZlibStreamParser._check_text_content(decompressed)
        if text_match:
            return text_match[0], text_match[1], None

        return "binary", ".bin", name_hint

    @staticmethod
    def _extract_nif_author(decompressed: bytes) -> Optional[str]:
        """Extract author name from NIF data."""
        try:
            text = decompressed[:500].decode("utf-8", errors="ignore")
            if "Do Nothing" in text:
                parts = text.split("Do Nothing")[0].strip().split()
                if parts:
                    return parts[-1]
        except (UnicodeDecodeError, AttributeError, IndexError):
            pass
        return None

    @staticmethod
    def try_decompress(data: bytes, offset: int = 0, max_compressed_size: int = 1048576) -> Optional[Dict[str, Any]]:
        """
        Try to decompress zlib data at the given offset.

        Args:
            data: Data buffer containing potential zlib stream
            offset: Starting offset of zlib header
            max_compressed_size: Maximum compressed size to try

        Returns:
            Dictionary with decompressed data info or None if invalid
        """
        import zlib

        if len(data) < offset + 10:
            return None

        # Verify zlib header
        header = data[offset : offset + 2]
        if header not in [ZlibStreamParser.ZLIB_LOW, ZlibStreamParser.ZLIB_DEFAULT, ZlibStreamParser.ZLIB_BEST]:
            return None

        # Try different chunk sizes to find complete zlib stream
        for try_size in [1024, 4096, 16384, 65536, 262144, max_compressed_size]:
            chunk_end = min(offset + try_size, len(data))
            chunk = data[offset:chunk_end]

            try:
                decompressed = zlib.decompress(chunk)
                if len(decompressed) < 20:
                    continue

                content_type, extension, name_hint = ZlibStreamParser._determine_content_type(decompressed)

                # Calculate actual compressed size
                dobj = zlib.decompressobj()
                dobj.decompress(chunk)
                actual_compressed_size = len(chunk) - len(dobj.unused_data)

                return {
                    "format": "zlib",
                    "content_type": content_type,
                    "extension": extension,
                    "compressed_size": actual_compressed_size,
                    "decompressed_size": len(decompressed),
                    "decompressed_data": decompressed,
                    "name_hint": name_hint,
                    "compression_ratio": len(decompressed) / actual_compressed_size if actual_compressed_size > 0 else 0,
                }
            except zlib.error:
                continue

        return None

    @staticmethod
    def is_valid_zlib_header(data: bytes, offset: int = 0) -> bool:
        """Check if data at offset has a valid zlib header."""
        if len(data) < offset + 2:
            return False
        header = data[offset : offset + 2]
        return header in [ZlibStreamParser.ZLIB_LOW, ZlibStreamParser.ZLIB_DEFAULT, ZlibStreamParser.ZLIB_BEST]


class XEXParser:
    """Parser for Xbox 360 Executable (XEX) files."""

    @staticmethod
    def parse_header(data: bytes, offset: int = 0) -> Optional[Dict[str, Any]]:
        """Parse XEX2 header to determine file size."""
        if len(data) < offset + 24:
            return None

        try:
            magic = data[offset : offset + 4]
            if magic != b"XEX2":
                return None

            # XEX2 header structure:
            # 0x00: Magic "XEX2"
            # 0x04: Module flags
            # 0x08: Data offset (where the PE image starts)
            # 0x0C: Reserved
            # 0x10: Header size
            # 0x14: Image size (size of the executable in memory)

            header_size = struct.unpack(">I", data[offset + 0x10 : offset + 0x14])[0]
            image_size = struct.unpack(">I", data[offset + 0x14 : offset + 0x18])[0]

            # The file size is approximately header_size + compressed image
            # Use header_size as minimum, cap at reasonable max
            estimated_size = max(header_size, 4096)
            if image_size > 0 and image_size < 100 * 1024 * 1024:
                estimated_size = min(image_size, 50 * 1024 * 1024)

            return {
                "format": "XEX2",
                "header_size": header_size,
                "image_size": image_size,
                "estimated_size": estimated_size,
            }
        except (struct.error, IndexError):
            return None

    @staticmethod
    def parse_script(data: bytes, offset: int = 0, max_size: int = 100000) -> Optional[Dict[str, Any]]:
        return None

    @staticmethod
    def try_decompress(data: bytes, offset: int = 0, max_compressed_size: int = 1048576) -> Optional[Dict[str, Any]]:
        return None


class XDBFParser:
    """Parser for Xbox Dashboard Files (XDBF) - achievements, title data."""

    @staticmethod
    def parse_header(data: bytes, offset: int = 0) -> Optional[Dict[str, Any]]:
        """Parse XDBF header to determine file size."""
        if len(data) < offset + 24:
            return None

        try:
            magic = data[offset : offset + 4]
            if magic != b"XDBF":
                return None

            # XDBF header:
            # 0x00: Magic "XDBF"
            # 0x04: Version
            # 0x08: Entry table length
            # 0x0C: Entry count
            # 0x10: Free space table length
            # 0x14: Free space table count

            version = struct.unpack(">I", data[offset + 4 : offset + 8])[0]
            entry_table_len = struct.unpack(">I", data[offset + 8 : offset + 12])[0]
            entry_count = struct.unpack(">I", data[offset + 12 : offset + 16])[0]
            free_table_len = struct.unpack(">I", data[offset + 16 : offset + 20])[0]

            # Header is 24 bytes, followed by entry table and free table
            # Then the actual data entries
            header_and_tables = 24 + (entry_table_len * 18) + (free_table_len * 8)

            # Estimate based on entry count (average ~1KB per entry)
            estimated_size = header_and_tables + (entry_count * 1024)
            estimated_size = min(estimated_size, 10 * 1024 * 1024)

            return {
                "format": "XDBF",
                "version": version,
                "entry_count": entry_count,
                "estimated_size": estimated_size,
            }
        except (struct.error, IndexError):
            return None

    @staticmethod
    def parse_script(data: bytes, offset: int = 0, max_size: int = 100000) -> Optional[Dict[str, Any]]:
        return None

    @staticmethod
    def try_decompress(data: bytes, offset: int = 0, max_compressed_size: int = 1048576) -> Optional[Dict[str, Any]]:
        return None


class XUIParser:
    """Parser for Xbox UI files (XUIS scenes and XUIB binaries)."""

    @staticmethod
    def parse_header(data: bytes, offset: int = 0) -> Optional[Dict[str, Any]]:
        """Parse XUI header to determine file size."""
        if len(data) < offset + 16:
            return None

        try:
            magic = data[offset : offset + 4]
            if magic not in (b"XUIS", b"XUIB"):
                return None

            format_type = "XUIS" if magic == b"XUIS" else "XUIB"

            # XUI files have size info in header
            # Structure varies but typically:
            # 0x00: Magic
            # 0x04: Version/flags
            # 0x08: Size or offset info

            if format_type == "XUIB":
                # XUIB: Binary compiled UI
                # Try to read size from header
                if len(data) >= offset + 12:
                    size_field = struct.unpack(">I", data[offset + 8 : offset + 12])[0]
                    if 16 < size_field < 10 * 1024 * 1024:
                        return {"format": format_type, "estimated_size": size_field}

            # For XUIS or if XUIB size not found, scan for end marker or next file
            # Use conservative estimate
            estimated_size = 50000  # 50KB default for UI files

            return {
                "format": format_type,
                "estimated_size": estimated_size,
            }
        except (struct.error, IndexError):
            return None

    @staticmethod
    def parse_script(data: bytes, offset: int = 0, max_size: int = 100000) -> Optional[Dict[str, Any]]:
        return None

    @staticmethod
    def try_decompress(data: bytes, offset: int = 0, max_compressed_size: int = 1048576) -> Optional[Dict[str, Any]]:
        return None


class STFSParser:
    """Parser for Xbox LIVE content packages (PIRS, CON, LIVE)."""

    @staticmethod
    def parse_header(data: bytes, offset: int = 0) -> Optional[Dict[str, Any]]:
        """Parse STFS package header to determine file size."""
        if len(data) < offset + 0x200:
            return None

        try:
            magic = data[offset : offset + 4]
            if magic not in (b"PIRS", b"CON ", b"LIVE"):
                return None

            format_type = magic.decode("ascii").strip()

            # STFS packages have a complex structure
            # The content size is at offset 0x344 (big-endian)
            # But we need at least the header to be valid

            if len(data) >= offset + 0x348:
                content_size = struct.unpack(">I", data[offset + 0x344 : offset + 0x348])[0]
                if content_size > 0 and content_size < 100 * 1024 * 1024:
                    # Add header size (~0x1000 for metadata)
                    return {
                        "format": format_type,
                        "content_size": content_size,
                        "estimated_size": content_size + 0x1000,
                    }

            # Default estimate for packages
            return {
                "format": format_type,
                "estimated_size": 100000,
            }
        except (struct.error, IndexError):
            return None

    @staticmethod
    def parse_script(data: bytes, offset: int = 0, max_size: int = 100000) -> Optional[Dict[str, Any]]:
        return None

    @staticmethod
    def try_decompress(data: bytes, offset: int = 0, max_compressed_size: int = 1048576) -> Optional[Dict[str, Any]]:
        return None
