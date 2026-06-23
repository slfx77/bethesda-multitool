"""
Locate and decompile SpeedTree generation candidates in the PC FalloutNV.exe
runtime image.

The Xbox 360 MemDebug binary has SpeedTree symbols; the PC final binary does not.
This script opens the existing analyzed PC runtime project and finds likely PC
counterparts by stable evidence:

  - .spt/SpeedTree strings and token parsing constants
  - leaf/blossom scalar defaults and hard constants
  - texture-coordinate, branch, and leaf record token clusters

Output is intentionally broad: ranked candidates plus decompiled C for the top
functions and focused known-entry candidates, so app fixes can be checked against
PC runtime behavior before code changes.
"""

from __future__ import annotations

import datetime
import os
import struct

os.environ.setdefault("GHIDRA_INSTALL_DIR", r"C:\Tools\ghidra_12.0.2_PUBLIC")

import pyghidra

pyghidra.start(verbose=True)

from ghidra.app.decompiler import DecompInterface, DecompileOptions
from ghidra.base.project import GhidraProject
from ghidra.program.flatapi import FlatProgramAPI
from ghidra.util.task import ConsoleTaskMonitor


TOOLS_DIR = os.path.dirname(os.path.abspath(__file__))
GHIDRA_DIR = os.path.join(TOOLS_DIR, "GhidraProject")
PROJECT_DIR = os.path.join(GHIDRA_DIR, "PEPCProject_FalloutNVRuntime")
PROJECT_NAME = "FalloutNV_RuntimeImage"
PROGRAM_PATH = "/"
PROGRAM_NAME = "FalloutNV_runtime_image.bin"
OUTPUT_PATH = os.path.join(GHIDRA_DIR, "speedtree_pc_decompiled.txt")

SEARCH_STRINGS = [
    "__IdvSpt_02_",
    "__IdvSpt",
    "SpeedTree",
    "CSpeedTreeRT",
    "CLeafGeometry",
    "SIdvLeaf",
    "BezierSpline",
    ".spt",
]

FLOAT_CONSTANTS = [
    ("float:0.01 bud min", struct.pack("<f", 0.01)),
    ("float:0.5 leaf spacing default", struct.pack("<f", 0.5)),
    ("float:0.75 blossom threshold default", struct.pack("<f", 0.75)),
    ("float:0.8 blossom probability default", struct.pack("<f", 0.8)),
    ("float:60 bud declination", struct.pack("<f", 60.0)),
    ("float:180 random spin", struct.pack("<f", 180.0)),
    ("float:255 color scale", struct.pack("<f", 255.0)),
]

INT_CONSTANTS = [
    ("token:10000 BeginTextureCoordInfo", 10000),
    ("token:10002 LeafTextureCoords", 10002),
    ("token:3000 LeafSize/blossom threshold", 3000),
    ("token:3001 BlossomDepth", 3001),
    ("token:3002 BlossomProbability", 3002),
    ("token:3007 RoomForLeafSpacing", 3007),
    ("token:3008 RoomForLeafMode", 3008),
    ("token:4000 LeafType", 4000),
    ("token:4005 LeafCorner1", 4005),
    ("token:6008 CrossSectionVerts", 6008),
    ("token:6009 RingCount", 6009),
]

FOCUSED_ENTRIES = [
    (0x00A0A410, "SpeedTree parser candidate: tokens 10000/3001/3002"),
    (0x00B09960, "SpeedTree texture-coordinate candidate: token 10002"),
    (0x00C95A80, "SpeedTree leaf candidate: token 4000 plus bud-min constant"),
    (0x00702990, "SpeedTree branch parser candidate: token 6009 cluster"),
    (0x00702DD0, "SpeedTree branch parser/helper candidate: token 6009 cluster"),
    (0x00B29FD0, "SpeedTree leaf-info default candidate: 0.5/0.8 constants"),
]

monitor = ConsoleTaskMonitor()
project = GhidraProject.openProject(PROJECT_DIR, PROJECT_NAME)


def iter_exec_blocks(program):
    for block in program.getMemory().getBlocks():
        if block.isExecute():
            yield block


def find_all_bytes(memory, addr_set, pattern, limit=200):
    hits = []
    addr = memory.findBytes(addr_set.getMinAddress(), pattern, None, True, monitor)
    while addr is not None and len(hits) < limit:
        hits.append(addr)
        try:
            addr = memory.findBytes(addr.add(1), pattern, None, True, monitor)
        except Exception:
            addr = None
    return hits


def search_code_immediates(memory, exec_blocks, value):
    pattern = struct.pack("<I", value & 0xFFFFFFFF)
    hits = []
    for block in exec_blocks:
        addr = memory.findBytes(block.getStart(), block.getEnd(), pattern, None, True, monitor)
        while addr is not None:
            hits.append(addr)
            try:
                next_addr = addr.add(1)
                if next_addr.compareTo(block.getEnd()) > 0:
                    break
                addr = memory.findBytes(next_addr, block.getEnd(), pattern, None, True, monitor)
            except Exception:
                break
    return hits


try:
    program = project.openProgram(PROGRAM_PATH, PROGRAM_NAME, False)
    memory = program.getMemory()
    listing = program.getListing()
    ref_mgr = program.getReferenceManager()
    func_mgr = program.getFunctionManager()
    flat = FlatProgramAPI(program, monitor)
    loaded_set = memory.getLoadedAndInitializedAddressSet()
    exec_blocks = list(iter_exec_blocks(program))

    candidates = {}

    def add_candidate(func, source, from_addr, extra=None):
        if func is None:
            return
        key = func.getEntryPoint().getOffset()
        entry = candidates.setdefault(key, {"function": func, "contexts": []})
        context = f"{source} via 0x{from_addr.getOffset():08X}"
        if extra:
            context += f" ({extra})"
        if context not in entry["contexts"]:
            entry["contexts"].append(context)

    def record_data_refs(label, hits):
        for hit in hits:
            block = memory.getBlock(hit)
            if block is not None and block.isExecute():
                add_candidate(func_mgr.getFunctionContaining(hit), f"{label}:inline", hit, block.getName())

            refs = ref_mgr.getReferencesTo(hit)
            while refs.hasNext():
                ref = refs.next()
                add_candidate(
                    func_mgr.getFunctionContaining(ref.getFromAddress()),
                    label,
                    ref.getFromAddress(),
                    f"data@0x{hit.getOffset():08X}",
                )

    def record_immediate_refs(label, value):
        for code_hit in search_code_immediates(memory, exec_blocks, value):
            add_candidate(func_mgr.getFunctionContaining(code_hit), label, code_hit, f"imm={value}")

    with open(OUTPUT_PATH, "w", encoding="utf-8") as out:
        out.write("=== SpeedTree PC Runtime Locator ===\n")
        out.write(f"Date: {datetime.datetime.now()}\n")
        out.write(f"Project: {PROJECT_DIR}\\{PROJECT_NAME}.gpr\n")
        out.write(f"Program: {program.getName()} {program.getLanguageID()}\n")
        out.write(f"Image base: 0x{program.getImageBase().getOffset():08X}\n\n")

        out.write("=== String Hits ===\n")
        for text in SEARCH_STRINGS:
            hits = find_all_bytes(memory, loaded_set, text.encode("ascii"), limit=80)
            out.write(f"{text!r}: {len(hits)} hits\n")
            for hit in hits[:40]:
                data = listing.getDefinedDataContaining(hit)
                block = memory.getBlock(hit)
                dtype = data.getDataType().getName() if data else "no-data"
                block_name = block.getName() if block else "?"
                out.write(f"  0x{hit.getOffset():08X} ({dtype}, block={block_name})\n")
            record_data_refs(f"string:{text}", hits)
            out.flush()
        out.write("\n")

        out.write("=== Float Constant Hits ===\n")
        for label, pattern in FLOAT_CONSTANTS:
            hits = find_all_bytes(memory, loaded_set, pattern, limit=80)
            out.write(f"{label}: {len(hits)} hits\n")
            for hit in hits[:40]:
                block = memory.getBlock(hit)
                out.write(f"  0x{hit.getOffset():08X} (block={block.getName() if block else '?'})\n")
            record_data_refs(label, hits)
            out.flush()
        out.write("\n")

        out.write("=== Integer Token Immediate Hits ===\n")
        for label, value in INT_CONSTANTS:
            hits = search_code_immediates(memory, exec_blocks, value)
            out.write(f"{label}: {len(hits)} code immediate hits\n")
            for hit in hits[:60]:
                func = func_mgr.getFunctionContaining(hit)
                fname = func.getName() if func else "(no function)"
                out.write(f"  0x{hit.getOffset():08X} {fname}\n")
            record_immediate_refs(label, value)
            out.flush()
        out.write("\n")

        ranked = sorted(
            candidates.values(),
            key=lambda e: (
                -len(e["contexts"]),
                -e["function"].getBody().getNumAddresses(),
                e["function"].getEntryPoint().getOffset(),
            ),
        )

        out.write("=== Ranked Candidate Functions ===\n")
        for entry in ranked[:100]:
            func = entry["function"]
            out.write(
                f"0x{func.getEntryPoint().getOffset():08X} size={func.getBody().getNumAddresses()} "
                f"name={func.getName()} contexts={len(entry['contexts'])}\n"
            )
            for context in entry["contexts"][:30]:
                out.write(f"  {context}\n")
        out.write("\n")

        decomp = DecompInterface()
        opts = DecompileOptions()
        opts.setMaxPayloadMBytes(64)
        decomp.setOptions(opts)
        decomp.openProgram(program)
        decomp.setSimplificationStyle("decompile")

        decompile_entries = []
        seen_entries = set()
        for entry in ranked[:12]:
            offset = entry["function"].getEntryPoint().getOffset()
            decompile_entries.append((entry, "ranked"))
            seen_entries.add(offset)

        for offset, why in FOCUSED_ENTRIES:
            if offset in seen_entries:
                continue
            func = func_mgr.getFunctionAt(flat.toAddr(offset))
            if func is None:
                func = func_mgr.getFunctionContaining(flat.toAddr(offset))
            if func is None:
                continue
            decompile_entries.append(({"function": func, "contexts": [why]}, "focused"))
            seen_entries.add(func.getEntryPoint().getOffset())

        out.write("=== Top Candidate Decompilation ===\n")
        for entry, source_kind in decompile_entries:
            func = entry["function"]
            out.write("=" * 88 + "\n")
            out.write(
                f"{func.getName()} entry=0x{func.getEntryPoint().getOffset():08X} "
                f"size={func.getBody().getNumAddresses()} contexts={len(entry['contexts'])} "
                f"source={source_kind}\n"
            )
            for context in entry["contexts"][:30]:
                out.write(f"  {context}\n")

            callees = []
            seen = set()
            inst_iter = listing.getInstructions(func.getBody(), True)
            while inst_iter.hasNext():
                inst = inst_iter.next()
                for ref in inst.getReferencesFrom():
                    if ref.getReferenceType().isCall():
                        target = ref.getToAddress()
                        if target.getOffset() in seen:
                            continue
                        seen.add(target.getOffset())
                        callee = func_mgr.getFunctionAt(target)
                        name = callee.getName() if callee else "(no func)"
                        callees.append(f"{name}@0x{target.getOffset():08X}")
            out.write("Callees: " + ", ".join(sorted(callees)[:80]) + "\n")
            out.write("-" * 88 + "\n")

            try:
                result = decomp.decompileFunction(func, 180, monitor)
                if result is not None and result.decompileCompleted():
                    out.write(result.getDecompiledFunction().getC())
                else:
                    out.write(f"DECOMPILE FAILED: {result.getErrorMessage() if result else 'no result'}\n")
            except Exception as ex:
                out.write(f"DECOMPILE ERROR: {ex}\n")
            out.write("\n\n")

        decomp.dispose()

    print(f"Wrote: {OUTPUT_PATH}")
finally:
    try:
        project.close(program)
    except Exception:
        pass
    project.close()
