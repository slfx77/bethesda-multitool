"""
Extract Oblivion (TES4) script command definitions from the game executable.

Walks the engine's CommandInfo array and emits a JSON dump plus, optionally, the C# table
for the decompiler. TES4 condition functions are an independently keyed subset: retail command-backed
entries have a non-null ``eval`` callback, while 31 xOBSE extension rows have no retail CommandInfo.

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
    python tools/extract_tes4_script_functions.py --verify-only
"""

import hashlib
import json
import os
import re
import struct
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).parent.parent
CLASSIC_EXE = r"E:\SteamLibrary\SteamApps\common\Oblivion\Oblivion.exe"
REMASTERED_EXE = str(REPO_ROOT / "Sample" / "Oblivion Remastered" / "OblivionRemastered-Win64-Shipping.exe")
XEDIT_PAS = REPO_ROOT / "Sample" / "Reference_Code" / "TES5Edit" / "Core" / "wbDefinitionsTES4.pas"
GENERATED_OUTPUT = (
    REPO_ROOT / "src" / "BethesdaMultitool" / "Core" / "Formats" / "Esm" / "Script"
    / "OblivionScriptFunctionTable.Generated.cs"
)

OFFICIAL_EXE_SHA256 = "74DB80316ADE529DF4D70942FCF8D9E40660C9CA69683E595F0448FEBEB018EA"
XEDIT_SHA256 = "D461214EDBD7648FB9960826902403BA5E70798B3C56FF046D3AC7C10AF8372A"
XEDIT_SOURCE_COMMIT = "e0e529a2d473756520f2d41f72c24dea0cf5ee0d"

EXPECTED_RETAIL_COMMAND_COUNT = 501
EXPECTED_ENGINE_CONDITION_COUNT = 169
EXPECTED_XOBSE_EXTENSION_COUNT = 31
EXPECTED_XOBSE_COMMAND_COUNT = EXPECTED_XOBSE_EXTENSION_COUNT
EXPECTED_FUNCTION_COUNT = EXPECTED_RETAIL_COMMAND_COUNT + EXPECTED_XOBSE_COMMAND_COUNT
EXPECTED_CONDITION_COUNT = EXPECTED_ENGINE_CONDITION_COUNT + EXPECTED_XOBSE_EXTENSION_COUNT

GAME_OPCODE_BASE = 0x1000
CONSOLE_OPCODE_BASE = 0x0100


def sha256(path):
    digest = hashlib.sha256()
    with open(path, "rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def require_hash(path, expected, label):
    path = Path(path)
    if not path.is_file():
        raise SystemExit(f"missing {label} input: {path}")
    actual = sha256(path)
    if actual != expected:
        raise SystemExit(
            f"unsupported {label} input hash for {path}:\n"
            f"  expected {expected}\n  actual   {actual}"
        )


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
    parse(1Ch) eval(20h) flags(24h) — 0x28 bytes; ParamInfo = typeStr(0) typeID(4)
    isOptional(8) — 12 bytes."""
    require_hash(exe_path, OFFICIAL_EXE_SHA256, "official Oblivion.exe")
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
            eval_va = pe.read_ptr(pos + 0x20)
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
                # Preserve the actual pointer as extraction evidence. Generation uses only its
                # zero/nonzero state; addresses are never emitted into runtime source.
                "eval": eval_va,
            })
            pos += 0x28
            expected += 1

        print(f"{label}: {len(funcs)} commands at file+0x{start:X} (opcodes 0x{base:04X}-0x{expected - 1:04X})")
        results[label] = funcs

    results["provenance"] = {"officialExeSha256": OFFICIAL_EXE_SHA256}
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


# Condition-only extension rows with no retail CommandInfo entry. The pinned xEdit source labels this
# exact trailing block "Added by (x)OBSE"; names and parameter declarations therefore have community
# xEdit/xOBSE provenance, not retail-engine or Construction-Set provenance. pt* maps to the matching
# classic ObScriptParamType raw id where one exists.
XEDIT_PT_TO_RAW = {
    "ptInteger": 1, "ptInventoryObject": 3, "ptActorValue": 5, "ptSpell": 7,
    "ptClass": 16, "ptPackage": 29,
}
XOBSE_EXTENSION_FUNCTIONS = [
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

XEDIT_CONDITION_ENTRY_RE = re.compile(
    r"\(Index:\s*(\d+);\s*Name:\s*'([^']+)'"
    r"(?:;\s*ParamType1:\s*(\w+))?"
    r"(?:;\s*ParamType2:\s*(\w+))?\)"
)


def load_xedit_condition_functions(xedit_path):
    """Read the pinned TES4 condition array without treating it as a script-opcode table."""
    require_hash(xedit_path, XEDIT_SHA256, "xEdit wbDefinitionsTES4.pas")
    text = Path(xedit_path).read_text(encoding="utf-8", errors="replace")
    start = text.find("wbConditionFunctions")
    if start < 0:
        raise SystemExit("wbConditionFunctions not found in wbDefinitionsTES4.pas")

    # The pinned array is well below this bound. The exact expected key set below makes an
    # accidentally truncated or over-broad match fail instead of silently changing generation.
    window = text[start:start + 40000]
    extension_marker = window.find("// Added by (x)OBSE:")
    if extension_marker < 0:
        raise SystemExit("xEdit TES4 condition array lacks the expected Added by (x)OBSE block")
    entries = {}
    for match in XEDIT_CONDITION_ENTRY_RE.finditer(window):
        index = int(match.group(1))
        if index in entries:
            raise SystemExit(f"duplicate xEdit condition index {index}")
        entries[index] = (match.group(2), match.group(3), match.group(4))

    labeled_extension_indices = {
        int(match.group(1))
        for match in XEDIT_CONDITION_ENTRY_RE.finditer(window[extension_marker:])
    }
    expected_extension_indices = {item[0] for item in XOBSE_EXTENSION_FUNCTIONS}
    if labeled_extension_indices != expected_extension_indices:
        raise SystemExit(
            "xEdit Added by (x)OBSE block does not match the pinned 31-row extension set"
        )
    return entries


def validate_generation_inputs(table, xedit_entries):
    provenance = table.get("provenance", {})
    if provenance.get("officialExeSha256") != OFFICIAL_EXE_SHA256:
        raise SystemExit(
            "classic JSON lacks the pinned official Oblivion.exe provenance; "
            "rerun this extractor in classic mode"
        )

    console = table.get("console", [])
    game = table.get("game", [])
    if any("eval" not in item for item in console + game):
        raise SystemExit(
            "classic JSON predates CommandInfo.eval extraction; rerun this extractor in classic mode"
        )

    # Preserve the existing command-table scope: all sequential console/game definitions except
    # the game-array terminator. (The console terminator remains part of that pre-existing scope.)
    all_commands = console + game
    all_commands = [item for item in all_commands if not item["name"].startswith("ADD NEW FUNCTIONS")]
    if len(all_commands) != EXPECTED_RETAIL_COMMAND_COUNT:
        raise SystemExit(
            f"unexpected TES4 retail command count: expected {EXPECTED_RETAIL_COMMAND_COUNT}, "
            f"got {len(all_commands)}"
        )

    if any(item["eval"] for item in console):
        raise SystemExit("unexpected non-null CommandInfo.eval pointer in the TES4 console table")

    engine_conditions = [item for item in game if item["eval"]]
    if len(engine_conditions) != EXPECTED_ENGINE_CONDITION_COUNT:
        raise SystemExit(
            f"unexpected TES4 engine condition count: expected {EXPECTED_ENGINE_CONDITION_COUNT}, "
            f"got {len(engine_conditions)}"
        )

    engine_by_index = {item["opcode"] - GAME_OPCODE_BASE: item for item in engine_conditions}
    if len(engine_by_index) != len(engine_conditions) or any(
        item["opcode"] < GAME_OPCODE_BASE for item in engine_conditions
    ):
        raise SystemExit("invalid or duplicate raw condition indices in the TES4 game table")

    expected_xobse_extensions = {
        index: (name, param1, param2)
        for index, name, param1, param2 in XOBSE_EXTENSION_FUNCTIONS
    }
    if len(expected_xobse_extensions) != EXPECTED_XOBSE_EXTENSION_COUNT:
        raise SystemExit("duplicate or incomplete hardcoded TES4 xOBSE extension definitions")

    actual_xobse_extensions = set(xedit_entries) - set(engine_by_index)
    if actual_xobse_extensions != set(expected_xobse_extensions):
        missing = sorted(set(expected_xobse_extensions) - actual_xobse_extensions)
        extra = sorted(actual_xobse_extensions - set(expected_xobse_extensions))
        raise SystemExit(
            "TES4 xOBSE extension key drift: "
            f"missing hardcoded={missing}, unexpected extension={extra}"
        )

    # These 31 names and parameter declarations are community data. Validate every hardcoded
    # tuple exactly against the hash-pinned source instead of relying on a name-only PASS banner.
    for index, expected in expected_xobse_extensions.items():
        actual = xedit_entries.get(index)
        if actual != expected:
            raise SystemExit(
                f"TES4 xOBSE extension definition drift at raw index {index}: "
                f"expected {expected!r}, got {actual!r}"
            )

    if set(xedit_entries) != set(engine_by_index) | set(expected_xobse_extensions):
        raise SystemExit("TES4 condition key set does not equal the 169 retail + 31 xOBSE rows")
    if len(xedit_entries) != EXPECTED_CONDITION_COUNT:
        raise SystemExit(
            f"unexpected xEdit TES4 condition count: expected {EXPECTED_CONDITION_COUNT}, "
            f"got {len(xedit_entries)}"
        )

    for index, item in engine_by_index.items():
        xedit_name = xedit_entries[index][0]
        if item["name"] != xedit_name:
            raise SystemExit(
                f"TES4 engine/xEdit condition name mismatch at raw index {index}: "
                f"engine={item['name']!r}, xEdit={xedit_name!r}"
            )

    return all_commands, engine_conditions


def generate_csharp(table, xedit_entries):
    """Render the script-opcode table and independently keyed raw CTDA subset."""
    all_funcs, engine_conditions = validate_generation_inputs(table, xedit_entries)

    lines = [
        "// <auto-generated>",
        "// Generated by tools/extract_tes4_script_functions.py.",
        f"// Official retail Oblivion.exe SHA-256: {OFFICIAL_EXE_SHA256}",
        f"// Retail-engine provenance: {len(all_funcs)} script definitions from the x86 CommandInfo arrays;",
        f"// {len(engine_conditions)} game entries have a non-null eval callback and are CTDA-capable.",
        "// CommandInfo layout is the classic 0x28-byte layout; parameter types and display strings",
        "// for those engine definitions come from the executable's 12-byte ParamInfo entries.",
        f"// xEdit source commit: {XEDIT_SOURCE_COMMIT}",
        f"// xEdit wbDefinitionsTES4.pas SHA-256: {XEDIT_SHA256}",
        f"// Community provenance (MPL-2.0): {len(XOBSE_EXTENSION_FUNCTIONS)} xOBSE command/condition definitions",
        "// absent from retail CommandInfo are from xEdit's block labeled Added by (x)OBSE. They are",
        "// retained in the opcode table for xOBSE ecosystem compatibility, not attributed to vanilla.",
        f"// The raw-index map is exactly {EXPECTED_CONDITION_COUNT} rows: "
        f"{EXPECTED_ENGINE_CONDITION_COUNT} retail engine + {EXPECTED_XOBSE_EXTENSION_COUNT} xOBSE.",
        "// All extension indices, names, and parameter declarations are generator-validated.",
        "// </auto-generated>",
        "",
        "namespace BethesdaMultitool.Core.Formats.Esm.Script;",
        "",
        "internal static class OblivionScriptFunctionTable",
        "{",
        f"    internal const int RetailCommandCount = {EXPECTED_RETAIL_COMMAND_COUNT};",
        f"    internal const int XObseCommandCount = {EXPECTED_XOBSE_COMMAND_COUNT};",
        f"    internal const int FunctionCount = {EXPECTED_FUNCTION_COUNT};",
        f"    internal const int EngineConditionFunctionCount = {EXPECTED_ENGINE_CONDITION_COUNT};",
        f"    internal const int XObseExtensionCount = {EXPECTED_XOBSE_EXTENSION_COUNT};",
        f"    internal const int ConditionFunctionCount = {EXPECTED_CONDITION_COUNT};",
        "",
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
            f'"{esc(func.get("shortName") or "")}", {is_ref}, [{params}], '
            f'{"true" if func.get("eval") else "false"}),'
        )

    lines.append("")
    lines.append("        // xOBSE extension commands (community xEdit provenance; absent from retail CommandInfo):")
    for idx, name, pt1, pt2 in XOBSE_EXTENSION_FUNCTIONS:
        params = ", ".join(
            f'new("{pt[2:]}", (ObScriptParamType){XEDIT_PT_TO_RAW[pt]}, false)'
            for pt in (pt1, pt2) if pt
        )
        opcode = GAME_OPCODE_BASE + idx
        lines.append(f'        [0x{opcode:04X}] = new("{name}", "", false, [{params}], true),')

    lines += [
        "    };",
        "",
        "    // Raw CTDA index -> definition. All rows reuse the exact object from the independently",
        "    // keyed opcode table; provenance remains retail eval-backed versus xOBSE extension.",
        "    internal static readonly Dictionary<ushort, ScriptFunctionDef> ConditionFunctions = new()",
        "    {",
    ]
    for func in engine_conditions:
        index = func["opcode"] - GAME_OPCODE_BASE
        lines.append(f"        [0x{index:04X}] = Functions[0x{func['opcode']:04X}],")
    lines.append("")
    lines.append("        // xOBSE extension conditions (community xEdit provenance; see header):")
    for idx, _, _, _ in XOBSE_EXTENSION_FUNCTIONS:
        opcode = GAME_OPCODE_BASE + idx
        lines.append(f"        [0x{idx:04X}] = Functions[0x{opcode:04X}],")
    lines += ["    };", "}", ""]
    return "\n".join(lines)


def write_generated(output_path, generated):
    with open(output_path, "w", encoding="utf-8", newline="\n") as stream:
        stream.write(generated)
    print(
        f"Generated {output_path} ({EXPECTED_RETAIL_COMMAND_COUNT} retail + "
        f"{EXPECTED_XOBSE_COMMAND_COUNT} xOBSE commands; "
        f"{EXPECTED_ENGINE_CONDITION_COUNT} engine + "
        f"{EXPECTED_XOBSE_EXTENSION_COUNT} xOBSE extension conditions)"
    )


def main():
    if len(sys.argv) < 2 or sys.argv[1] not in ("classic", "remastered", "csharp", "--verify-only"):
        print(__doc__)
        sys.exit(1)

    mode = sys.argv[1]
    args = sys.argv[2:]
    out = None
    if "-o" in args:
        i = args.index("-o")
        out = args[i + 1]
        args = args[:i] + args[i + 2:]

    xedit_path = XEDIT_PAS
    if "--xedit" in args:
        i = args.index("--xedit")
        xedit_path = Path(args[i + 1])
        args = args[:i] + args[i + 2:]

    if mode == "--verify-only":
        exe_path = Path(args[0]) if args else Path(CLASSIC_EXE)
        output_path = Path(out) if out else GENERATED_OUTPUT
        table = extract_classic(exe_path)
        xedit_entries = load_xedit_condition_functions(xedit_path)
        generated = generate_csharp(table, xedit_entries)
        if not output_path.is_file() or output_path.read_text(encoding="utf-8") != generated:
            raise SystemExit(f"generated output is stale: {output_path}")
        print(
            f"PASS: {EXPECTED_RETAIL_COMMAND_COUNT} retail + "
            f"{EXPECTED_XOBSE_COMMAND_COUNT} xOBSE commands; "
            f"{EXPECTED_ENGINE_CONDITION_COUNT} engine + "
            f"{EXPECTED_XOBSE_EXTENSION_COUNT} xOBSE extension conditions"
        )
        return

    if mode == "csharp":
        if not args:
            raise SystemExit("csharp mode requires a classic JSON input")
        with open(args[0], encoding="utf-8") as stream:
            table = json.load(stream)
        xedit_entries = load_xedit_condition_functions(xedit_path)
        generated = generate_csharp(table, xedit_entries)
        write_generated(Path(out) if out else GENERATED_OUTPUT, generated)
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
