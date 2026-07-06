"""
Extract Oblivion (TES4) script command definitions from the game executable.

Walks the engine's CommandInfo array (the same table that drives both script compilation
and CTDA condition functions: opcode = 0x1000 | functionIndex) and emits a JSON dump plus,
optionally, the C# table for the decompiler.

Two modes:
  classic     x86 retail Oblivion.exe — the DATA AUTHORITY (it compiled Oblivion.esm's bytecode).
              CommandInfo is the OBSE-documented 0x28-byte layout; ParamInfo is 12 bytes with an
              engine-authored human-readable type string per parameter.
  remastered  x64 OblivionRemastered-Win64-Shipping.exe — engine-accurate NAME cross-check
              (the Gamebryo sim survives under UE5). Entry stride is probed automatically.

Usage:
    python tools/extract_tes4_script_functions.py classic [exe] [-o out.json]
    python tools/extract_tes4_script_functions.py remastered [exe] [-o out.json]
    python tools/extract_tes4_script_functions.py csharp <classic.json> [-o Generated.cs]
"""

import json
import os
import struct
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).parent.parent
CLASSIC_EXE = r"E:\SteamLibrary\SteamApps\common\Oblivion\Oblivion.exe"
REMASTERED_EXE = str(REPO_ROOT / "Sample" / "Oblivion Remastered" / "OblivionRemastered-Win64-Shipping.exe")

GAME_OPCODE_BASE = 0x1000
CONSOLE_OPCODE_BASE = 0x0100


class PeImage:
    """Minimal PE reader: sections + VA->file-offset + string reads (x86 and x64)."""

    def __init__(self, path):
        self.data = open(path, "rb").read()
        pe_off = struct.unpack_from("<I", self.data, 0x3C)[0]
        machine, num_sections = struct.unpack_from("<HH", self.data, pe_off + 4)
        opt_size = struct.unpack_from("<H", self.data, pe_off + 20)[0]
        opt_off = pe_off + 24
        magic = struct.unpack_from("<H", self.data, opt_off)[0]
        self.is64 = magic == 0x20B
        self.image_base = (
            struct.unpack_from("<Q", self.data, opt_off + 24)[0]
            if self.is64
            else struct.unpack_from("<I", self.data, opt_off + 28)[0]
        )
        self.sections = []
        sec_off = opt_off + opt_size
        for i in range(num_sections):
            off = sec_off + i * 40
            name = self.data[off : off + 8].rstrip(b"\x00").decode("ascii", "replace")
            vsize, vaddr, rawsize, rawptr = struct.unpack_from("<IIII", self.data, off + 8)
            self.sections.append((name, vaddr, vsize, rawptr, rawsize))

    def va_to_off(self, va):
        rva = va - self.image_base
        for _, vaddr, vsize, rawptr, _ in self.sections:
            if vaddr <= rva < vaddr + vsize:
                return rawptr + (rva - vaddr)
        return None

    def read_ptr(self, off):
        return (
            struct.unpack_from("<Q", self.data, off)[0]
            if self.is64
            else struct.unpack_from("<I", self.data, off)[0]
        )

    def read_cstr(self, va, max_len=256):
        if va == 0:
            return ""
        off = self.va_to_off(va)
        if off is None:
            return ""
        end = self.data.find(b"\x00", off, off + max_len)
        if end < 0:
            end = off + max_len
        chunk = self.data[off:end]
        if any(b < 0x20 or b > 0x7E for b in chunk):
            return ""
        return chunk.decode("ascii", "replace")

    def section(self, name):
        for s in self.sections:
            if s[0] == name:
                return s
        return None


def find_array(pe, opcode_base, stride, opcode_off, min_run=8):
    """Find the CommandInfo array start: a run of entries with sequential opcodes."""
    for name, vaddr, vsize, rawptr, rawsize in pe.sections:
        if name not in (".data", ".rdata"):
            continue
        limit = rawptr + min(vsize, rawsize) - stride * min_run
        for off in range(rawptr, limit, 4):
            ok = True
            for k in range(min_run):
                if struct.unpack_from("<I", pe.data, off + k * stride + opcode_off)[0] != opcode_base + k:
                    ok = False
                    break
            if ok:
                return off
    return None


def probe_stride(pe, opcode_base):
    """Probe (stride, opcode_off) combos for the x64 Remastered layout."""
    for stride in range(0x28, 0x69, 8):
        for opcode_off in (0x10, 0x14, 0x18):
            off = find_array(pe, opcode_base, stride, opcode_off)
            if off is not None:
                return off, stride, opcode_off
    return None, None, None


def extract_classic(exe_path):
    """Classic x86 layout (OBSE CommandTable.h): longName(0) shortName(4) opcode(8)
    helpText(C) needsParent u16(10) numParams u16(12) params(14h) execute(18h)
    parse(1Ch) unused(20h) flags(24h) — 0x28 bytes; ParamInfo = typeStr(0) typeID(4)
    isOptional(8) — 12 bytes."""
    pe = PeImage(exe_path)
    if pe.is64:
        raise SystemExit("classic mode expects the x86 retail exe")

    results = {}
    for label, base in (("console", CONSOLE_OPCODE_BASE), ("game", GAME_OPCODE_BASE)):
        start = find_array(pe, base, 0x28, 8)
        if start is None:
            print(f"WARN: {label} array (base 0x{base:04X}) not found")
            results[label] = []
            continue

        funcs = []
        pos = start
        expected = base
        while pos + 0x28 <= len(pe.data):
            opcode = struct.unpack_from("<I", pe.data, pos + 8)[0]
            if opcode != expected:
                break
            name = pe.read_cstr(pe.read_ptr(pos))
            short = pe.read_cstr(pe.read_ptr(pos + 4))
            help_text = pe.read_cstr(pe.read_ptr(pos + 0xC), 512)
            needs_parent = struct.unpack_from("<H", pe.data, pos + 0x10)[0]
            num_params = struct.unpack_from("<H", pe.data, pos + 0x12)[0]
            params_va = pe.read_ptr(pos + 0x14)
            if not name:
                break

            params = []
            if params_va and 0 < num_params <= 24:
                poff = pe.va_to_off(params_va)
                if poff is not None:
                    for i in range(num_params):
                        type_str = pe.read_cstr(pe.read_ptr(poff + i * 12))
                        type_id = struct.unpack_from("<I", pe.data, poff + i * 12 + 4)[0]
                        optional = struct.unpack_from("<I", pe.data, poff + i * 12 + 8)[0]
                        params.append({"name": type_str, "type": type_id, "optional": optional != 0})

            funcs.append({
                "opcode": opcode,
                "name": name,
                "shortName": short,
                "needsParent": needs_parent != 0,
                "help": help_text,
                "params": params,
            })
            pos += 0x28
            expected += 1

        print(f"{label}: {len(funcs)} commands at file+0x{start:X} (opcodes 0x{base:04X}-0x{expected - 1:04X})")
        results[label] = funcs

    return results


def extract_remastered(exe_path):
    """Remastered x64: stride probed; names for the per-opcode cross-check."""
    pe = PeImage(exe_path)
    if not pe.is64:
        raise SystemExit("remastered mode expects the x64 exe")

    start, stride, opcode_off = probe_stride(pe, GAME_OPCODE_BASE)
    if start is None:
        raise SystemExit("Remastered CommandInfo array not found (no stride matched)")
    print(f"game array at file+0x{start:X}, stride 0x{stride:X}, opcode at +0x{opcode_off:X}")

    funcs = []
    pos = start
    expected = GAME_OPCODE_BASE
    while pos + stride <= len(pe.data):
        opcode = struct.unpack_from("<I", pe.data, pos + opcode_off)[0]
        if opcode != expected:
            break
        name = pe.read_cstr(pe.read_ptr(pos))
        short = pe.read_cstr(pe.read_ptr(pos + 8))
        if not name:
            break
        funcs.append({"opcode": opcode, "name": name, "shortName": short})
        pos += stride
        expected += 1

    print(f"game: {len(funcs)} commands (0x1000-0x{expected - 1:04X})")
    return {"game": funcs}


# Condition-ONLY functions (no CommandInfo entry; evaluated by index in the game's condition
# dispatch; names exist only in the Construction Set). Sourced from xEdit wbDefinitionsTES4
# wbConditionFunctions — community provenance. pt* → engine ObScriptParamType raw id.
XEDIT_PT_TO_RAW = {
    "ptInteger": 1, "ptInventoryObject": 3, "ptActorValue": 5, "ptSpell": 7,
    "ptClass": 16, "ptPackage": 29,
}
XEDIT_CONDITION_ONLY = [
    (1107, "IsAmmo", "ptInventoryObject", None),
    (1122, "HasSpell", "ptSpell", None),
    (1124, "IsClassSkill", "ptActorValue", "ptClass"),
    (1254, "GetActorLightAmount", None, None),
    (1884, "GetPCTrainingSessionsUsed", None, None),
    (2213, "GetPackageOffersServices", "ptPackage", None),
    (2214, "GetPackageMustReachLocation", "ptPackage", None),
    (2215, "GetPackageMustComplete", "ptPackage", None),
    (2216, "GetPackageLockDoorsAtStart", "ptPackage", None),
    (2217, "GetPackageLockDoorsAtEnd", "ptPackage", None),
    (2218, "GetPackageLockDoorsAtLocation", "ptPackage", None),
    (2219, "GetPackageUnlockDoorsAtStart", "ptPackage", None),
    (2220, "GetPackageUnlockDoorsAtEnd", "ptPackage", None),
    (2221, "GetPackageUnlockDoorsAtLocation", "ptPackage", None),
    (2222, "GetPackageContinueIfPCNear", "ptPackage", None),
    (2223, "GetPackageOncePerDay", "ptPackage", None),
    (2224, "GetPackageSkipFalloutBehavior", "ptPackage", None),
    (2225, "GetPackageAlwaysRun", "ptPackage", None),
    (2226, "GetPackageAlwaysSneak", "ptPackage", None),
    (2227, "GetPackageAllowSwimming", "ptPackage", None),
    (2228, "GetPackageAllowFalls", "ptPackage", None),
    (2229, "GetPackageArmorUnequipped", "ptPackage", None),
    (2230, "GetPackageWeaponsUnequipped", "ptPackage", None),
    (2231, "GetPackageDefensiveCombat", "ptPackage", None),
    (2232, "GetPackageUseHorse", "ptPackage", None),
    (2233, "GetPackageNoIdleAnims", "ptPackage", None),
    (2571, "GetBaseAV3", "ptActorValue", None),
    (2572, "GetBaseAV3C", "ptInteger", None),
    (2573, "IsNaked", "ptInteger", None),
    (2577, "IsMajorRef", "ptActorValue", None),
    (2578, "IsDiseased", None, None),
]


def generate_csharp(classic_json_path, output_path):
    """Emit OblivionScriptFunctionTable.Generated.cs from the classic JSON dump + the
    xEdit-sourced condition-only entries."""
    with open(classic_json_path, encoding="utf-8") as f:
        table = json.load(f)

    all_funcs = table.get("console", []) + table.get("game", [])
    # Drop the end-of-table placeholder ("ADD NEW FUNCTIONS BEFORE THIS ONE!!!").
    all_funcs = [f for f in all_funcs if not f["name"].startswith("ADD NEW FUNCTIONS")]

    lines = [
        "// <auto-generated>",
        "// Generated by tools/extract_tes4_script_functions.py from retail Oblivion.exe (x86),",
        "// cross-checked per-opcode against the Oblivion Remastered x64 table and xEdit",
        "// wbDefinitionsTES4 (tools/compare_tes4_condition_functions.py — gate PASS).",
        "// CommandInfo layout per OBSE CommandTable.h; param types are the engine's raw",
        "// ObScriptParamType ids, each with the engine's own per-parameter display string.",
        f"// {len(all_funcs)} engine commands ({len(table.get('console', []))} console + game) plus",
        f"// {len(XEDIT_CONDITION_ONLY)} condition-only functions (no CommandInfo entry; names are",
        "// Construction-Set-side, sourced from xEdit — community provenance).",
        "// </auto-generated>",
        "",
        "namespace BethesdaMultitool.Core.Formats.Esm.Script;",
        "",
        "internal static class OblivionScriptFunctionTable",
        "{",
        "    internal static readonly Dictionary<ushort, ScriptFunctionDef> Functions = new()",
        "    {",
    ]

    def esc(s):
        return s.replace('"', '\\"')

    for func in all_funcs:
        params = ", ".join(
            f'new("{esc(p["name"])}", (ObScriptParamType){p["type"]}, '
            f'{"true" if p["optional"] else "false"})'
            for p in func.get("params", [])
        )
        is_ref = "true" if func.get("needsParent") else "false"
        lines.append(
            f'        [0x{func["opcode"]:04X}] = new("{esc(func["name"])}", '
            f'"{esc(func.get("shortName") or "")}", {is_ref}, [{params}]),'
        )

    lines.append("")
    lines.append("        // Condition-only (xEdit-sourced; see header):")
    for idx, name, pt1, pt2 in XEDIT_CONDITION_ONLY:
        params = ", ".join(
            f'new("{pt[2:]}", (ObScriptParamType){XEDIT_PT_TO_RAW[pt]}, false)'
            for pt in (pt1, pt2) if pt
        )
        lines.append(f'        [0x{0x1000 | idx:04X}] = new("{name}", "", false, [{params}]),')

    lines += ["    };", "}", ""]
    with open(output_path, "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(lines))
    print(f"Generated {output_path} ({len(all_funcs)} engine + {len(XEDIT_CONDITION_ONLY)} xEdit commands)")


def main():
    if len(sys.argv) < 2 or sys.argv[1] not in ("classic", "remastered", "csharp"):
        print(__doc__)
        sys.exit(1)

    mode = sys.argv[1]
    args = sys.argv[2:]
    out = None
    if "-o" in args:
        i = args.index("-o")
        out = args[i + 1]
        args = args[:i] + args[i + 2:]

    if mode == "csharp":
        generate_csharp(args[0], out or str(
            REPO_ROOT / "src" / "BethesdaMultitool" / "Core" / "Formats" / "Esm" / "Script"
            / "OblivionScriptFunctionTable.Generated.cs"))
        return

    exe = args[0] if args else (CLASSIC_EXE if mode == "classic" else REMASTERED_EXE)
    if not os.path.exists(exe):
        raise SystemExit(f"exe not found: {exe}")
    print(f"Reading: {exe}")

    table = extract_classic(exe) if mode == "classic" else extract_remastered(exe)

    out = out or str(REPO_ROOT / "TestOutput" / f"tes4_command_table.{mode}.json")
    os.makedirs(os.path.dirname(out), exist_ok=True)
    with open(out, "w", encoding="utf-8") as f:
        json.dump(table, f, indent=1)
    print(f"Wrote {out}")

    # Distinct param-type ids with the engine's own naming (classic only).
    if mode == "classic":
        type_names = {}
        for func in table.get("console", []) + table.get("game", []):
            for p in func.get("params", []):
                type_names.setdefault(p["type"], set()).add(p["name"])
        print(f"{len(type_names)} distinct param type ids:")
        for tid in sorted(type_names):
            print(f"  {tid}: {sorted(type_names[tid])}")


if __name__ == "__main__":
    main()
