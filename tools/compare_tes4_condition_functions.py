"""
Optional research cross-check of the extracted TES4 retail command table against (a) the
Remastered exe's table and (b) xEdit's wbConditionFunctions (wbDefinitionsTES4.pas).

The generator itself now hash-pins its retail/xEdit inputs, reads classic CommandInfo.eval,
and validates the exact 31-row xOBSE extension block including parameters. This script is
only the separate Remastered name-drift report, not the generation/provenance gate.

Usage:
    python tools/compare_tes4_condition_functions.py \
        [TestOutput/tes4_command_table.classic.json] [TestOutput/tes4_command_table.remastered.json]
"""

import json
import re
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).parent.parent
XEDIT_PAS = REPO_ROOT / "Sample" / "Reference_Code" / "TES5Edit" / "Core" / "wbDefinitionsTES4.pas"

ENTRY_RE = re.compile(
    r"\(Index:\s*(\d+);\s*Name:\s*'([^']+)'"
    r"(?:;\s*ParamType1:\s*(\w+))?"
    r"(?:;\s*ParamType2:\s*(\w+))?\)")


def load_xedit_condition_functions():
    text = XEDIT_PAS.read_text(encoding="utf-8", errors="replace")
    start = text.find("wbConditionFunctions")
    if start < 0:
        raise SystemExit("wbConditionFunctions not found in wbDefinitionsTES4.pas")
    # The array literal ends at the next ');' after the last entry; scope the regex to a window.
    window = text[start:start + 40000]
    entries = {}
    for m in ENTRY_RE.finditer(window):
        idx, name, pt1, pt2 = int(m.group(1)), m.group(2), m.group(3), m.group(4)
        entries[idx] = {"name": name, "pt1": pt1, "pt2": pt2}
    return entries


def main():
    classic_path = sys.argv[1] if len(sys.argv) > 1 else str(REPO_ROOT / "TestOutput" / "tes4_command_table.classic.json")
    remastered_path = sys.argv[2] if len(sys.argv) > 2 else str(REPO_ROOT / "TestOutput" / "tes4_command_table.remastered.json")

    classic = json.load(open(classic_path, encoding="utf-8"))
    remastered = json.load(open(remastered_path, encoding="utf-8"))

    classic_by_op = {f["opcode"]: f for f in classic["game"]}
    remastered_by_op = {f["opcode"]: f for f in remastered["game"]}

    print("=== classic vs remastered (engine name drift) ===")
    drift = 0
    for op, cf in sorted(classic_by_op.items()):
        rf = remastered_by_op.get(op)
        if rf is None:
            print(f"  0x{op:04X} {cf['name']}: MISSING in remastered")
            drift += 1
        elif cf["name"].lower() != rf["name"].lower():
            print(f"  0x{op:04X}: classic '{cf['name']}' vs remastered '{rf['name']}'")
            drift += 1
    extra = sorted(set(remastered_by_op) - set(classic_by_op))
    for op in extra:
        print(f"  0x{op:04X} {remastered_by_op[op]['name']}: remastered-only (excluded from table)")
    print(f"  drift: {drift} renames/removals, {len(extra)} remastered-only additions")

    print("\n=== classic vs xEdit wbConditionFunctions ===")
    xedit = load_xedit_condition_functions()
    print(f"  xEdit entries: {len(xedit)}")
    mismatches = 0
    xobse_extensions = 0
    for idx, xe in sorted(xedit.items()):
        op = 0x1000 | idx
        cf = classic_by_op.get(op)
        if cf is None:
            if idx >= 370:
                # The pinned xEdit array labels this exact trailing block "Added by (x)OBSE".
                # It is absent from retail CommandInfo but retained by the runtime table as
                # explicitly attributed community extension commands/conditions.
                xobse_extensions += 1
            else:
                print(f"  index {idx} (0x{op:04X}) '{xe['name']}': NOT in classic table")
                mismatches += 1
        elif cf["name"].lower() != xe["name"].lower():
            print(f"  index {idx} (0x{op:04X}): classic '{cf['name']}' vs xEdit '{xe['name']}'")
            mismatches += 1
    print(f"  name mismatches: {mismatches}; xOBSE extension entries: {xobse_extensions}")

    # The classic 0x1171 placeholder terminator ("ADD NEW FUNCTIONS BEFORE THIS ONE!!!") is the
    # only expected engine drift; anything else needs review.
    unexplained_drift = drift - (1 if 0x1171 in classic_by_op else 0)
    print("\nREPORT:", "CONSISTENT" if mismatches == 0 and unexplained_drift <= 0 else "REVIEW REQUIRED")


if __name__ == "__main__":
    main()
