"""
Build Timeline Extractor for Xbox 360 Minidumps

Extracts build timestamps from the module list stream in minidump files
to identify which dumps came from the same executable build.

Usage:
    python -m src.build_timeline Sample/
    python -m src.build_timeline Sample/*.dmp --json output.json
"""

import struct
import json
import argparse
from pathlib import Path
from datetime import datetime
from collections import defaultdict
from typing import Any, Dict, List, Optional


# Type aliases for clarity
ModuleInfo = Dict[str, Any]
DumpInfo = Dict[str, Any]
BuildCluster = Dict[str, Any]


class BuildTimelineExtractor:
    """Extract and analyze build timestamps from minidump module info."""

    # Minidump stream types
    STREAM_TYPE_MODULE_LIST = 4

    def __init__(self) -> None:
        self.dumps: List[DumpInfo] = []
        self.builds: Dict[int, List[DumpInfo]] = defaultdict(list)

    def parse_module_stream(self, filepath: Path) -> Optional[List[ModuleInfo]]:
        """
        Parse the ModuleListStream from a minidump file.

        Returns list of module info dicts, or None if parsing fails.
        """
        try:
            with open(filepath, "rb") as f:
                # Read MDMP header
                magic = f.read(4)
                if magic != b"MDMP":
                    return None

                # Skip version
                f.read(4)

                # Number of streams and directory offset
                num_streams = struct.unpack("<I", f.read(4))[0]
                stream_dir_rva = struct.unpack("<I", f.read(4))[0]

                # Find module list stream
                f.seek(stream_dir_rva)
                module_stream = None

                for _ in range(num_streams):
                    stream_type = struct.unpack("<I", f.read(4))[0]
                    data_size = struct.unpack("<I", f.read(4))[0]
                    rva = struct.unpack("<I", f.read(4))[0]

                    if stream_type == self.STREAM_TYPE_MODULE_LIST:
                        module_stream = {"rva": rva, "size": data_size}
                        break

                if not module_stream:
                    return None

                # Parse modules
                f.seek(module_stream["rva"])
                num_modules = struct.unpack("<I", f.read(4))[0]

                modules: List[ModuleInfo] = []
                for _ in range(num_modules):
                    base_addr = struct.unpack("<Q", f.read(8))[0]
                    size = struct.unpack("<I", f.read(4))[0]
                    checksum = struct.unpack("<I", f.read(4))[0]
                    timestamp = struct.unpack("<I", f.read(4))[0]
                    name_rva = struct.unpack("<I", f.read(4))[0]

                    # Skip version info and reserved fields
                    # VS_FIXEDFILEINFO (52 bytes) + CV/Misc info
                    f.read(4 + 20 + 60)  # Skip remaining 84 bytes of MINIDUMP_MODULE

                    modules.append({"base_addr": base_addr, "size": size, "checksum": checksum, "timestamp": timestamp, "name_rva": name_rva})

                # Read module names
                for mod in modules:
                    f.seek(mod["name_rva"])
                    name_len = struct.unpack("<I", f.read(4))[0]
                    name_bytes = f.read(name_len)
                    mod["name"] = name_bytes.decode("utf-16-le").rstrip("\x00")
                    del mod["name_rva"]  # Clean up

                return modules

        except Exception as e:
            print(f"Error parsing {filepath}: {e}")
            return None

    def get_main_module(self, modules: List[ModuleInfo]) -> Optional[ModuleInfo]:
        """Find the main executable module (typically the XEX)."""
        # Look for Fallout/game module first
        for mod in modules:
            name_lower: str = mod["name"].lower()
            if "fallout" in name_lower or ".xex" in name_lower:
                return mod

        # Fall back to first module
        return modules[0] if modules else None

    def analyze_dump(self, filepath: Path) -> Optional[DumpInfo]:
        """
        Analyze a single dump file and extract build info.

        Returns dict with dump info or None if parsing fails.
        """
        modules = self.parse_module_stream(filepath)
        if not modules:
            return None

        main_mod = self.get_main_module(modules)
        if not main_mod:
            return None

        file_size = filepath.stat().st_size

        try:
            build_date = datetime.fromtimestamp(main_mod["timestamp"])
            build_date_str = build_date.strftime("%Y-%m-%d %H:%M:%S")
        except (ValueError, OSError):
            build_date_str = "Invalid timestamp"

        return {
            "filename": filepath.name,
            "filepath": str(filepath),
            "file_size_mb": round(file_size / 1024 / 1024, 1),
            "timestamp": main_mod["timestamp"],
            "build_date": build_date_str,
            "module_name": main_mod["name"],
            "module_base": hex(main_mod["base_addr"]),
            "module_size": main_mod["size"],
            "checksum": hex(main_mod["checksum"]),
            "total_modules": len(modules),
        }

    def analyze_directory(self, directory: Path, pattern: str = "*.dmp") -> None:
        """Analyze all dump files in a directory."""
        dump_files = sorted(directory.glob(pattern))

        for filepath in dump_files:
            info = self.analyze_dump(filepath)
            if info:
                self.dumps.append(info)
                self.builds[info["timestamp"]].append(info)

    def get_build_clusters(self) -> List[BuildCluster]:
        """
        Get list of builds sorted by timestamp with their associated dumps.

        Returns list of build info dicts.
        """
        clusters: List[BuildCluster] = []
        for timestamp in sorted(self.builds.keys()):
            dumps = self.builds[timestamp]
            sizes: List[float] = [d["file_size_mb"] for d in dumps]

            try:
                build_date = datetime.fromtimestamp(timestamp)
                date_str = build_date.strftime("%Y-%m-%d %H:%M:%S")
            except (ValueError, OSError):
                date_str = "Invalid"

            clusters.append(
                {
                    "timestamp": timestamp,
                    "build_date": date_str,
                    "dump_count": len(dumps),
                    "avg_size_mb": round(sum(sizes) / len(sizes), 1),
                    "min_size_mb": round(min(sizes), 1),
                    "max_size_mb": round(max(sizes), 1),
                    "dumps": [d["filename"] for d in dumps],
                }
            )

        return clusters

    def print_timeline(self) -> None:
        """Print the build timeline to console."""
        clusters = self.get_build_clusters()

        print("=" * 80)
        print("BUILD TIMELINE FROM MINIDUMP MODULE TIMESTAMPS")
        print("=" * 80)
        print()

        for cluster in clusters:
            print(f"BUILD: {cluster['build_date']} (timestamp: {cluster['timestamp']})")
            print(f"       {cluster['dump_count']} dumps, avg size: {cluster['avg_size_mb']} MB")

            for filename in sorted(cluster["dumps"]):
                dump_info = next(d for d in self.dumps if d["filename"] == filename)
                print(f"         - {filename} ({dump_info['file_size_mb']} MB)")
            print()

        print("=" * 80)
        print(f"SUMMARY: {len(self.dumps)} total dumps across {len(clusters)} unique builds")
        print("=" * 80)

        # Highlight best clusters for analysis
        multi_dump_clusters = [c for c in clusters if c["dump_count"] > 1]
        if multi_dump_clusters:
            print()
            print("BEST CLUSTERS FOR SAME-BUILD ANALYSIS:")
            for c in sorted(multi_dump_clusters, key=lambda x: -x["dump_count"]):
                print(f"  - {c['build_date']}: {c['dump_count']} dumps ({', '.join(c['dumps'][:5])}{'...' if len(c['dumps']) > 5 else ''})")

    def export_json(self, filepath: Path) -> None:
        """Export build timeline to JSON file."""
        data: Dict[str, Any] = {
            "generated": datetime.now().isoformat(),
            "total_dumps": len(self.dumps),
            "unique_builds": len(self.builds),
            "builds": self.get_build_clusters(),
            "dumps": self.dumps,
        }

        with open(filepath, "w") as f:
            json.dump(data, f, indent=2)

        print(f"Exported to {filepath}")


def main() -> None:
    parser = argparse.ArgumentParser(description="Extract build timeline from Xbox 360 minidump files")
    parser.add_argument("path", type=Path, help="Directory containing .dmp files or glob pattern")
    parser.add_argument("--json", "-j", type=Path, help="Export results to JSON file")
    parser.add_argument("--pattern", "-p", default="*.dmp", help="Glob pattern for dump files (default: *.dmp)")

    args = parser.parse_args()

    extractor = BuildTimelineExtractor()

    if args.path.is_dir():
        extractor.analyze_directory(args.path, args.pattern)
    else:
        # Single file
        info = extractor.analyze_dump(args.path)
        if info:
            extractor.dumps.append(info)
            extractor.builds[info["timestamp"]].append(info)

    extractor.print_timeline()

    if args.json:
        extractor.export_json(args.json)


if __name__ == "__main__":
    main()
