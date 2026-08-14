"""Generate Fallout 76's raw-index CTDA condition-function table.

Fallout 76 has no pinned local engine symbol oracle for this metadata. The
generated table therefore treats the hash-pinned xEdit FO76 definitions as a
community source for condition names and coarse on-disk parameter kinds. It
does not expose the rows as a script-opcode table or claim engine membership.

Usage:
    python tools/extract_fo76_condition_functions.py
    python tools/extract_fo76_condition_functions.py --verify-only
"""

from __future__ import annotations

import argparse
import hashlib
import re
from dataclasses import dataclass
from pathlib import Path


REPO_ROOT = Path(__file__).resolve().parent.parent
DEFAULT_XEDIT = (
    REPO_ROOT
    / "Sample"
    / "Reference_Code"
    / "TES5Edit"
    / "Core"
    / "wbDefinitionsFO76.pas"
)
DEFAULT_OUTPUT = (
    REPO_ROOT
    / "src"
    / "BethesdaMultitool"
    / "Core"
    / "Formats"
    / "Esm"
    / "Script"
    / "Fallout76ConditionFunctionTable.Generated.cs"
)

EXPECTED_XEDIT_SHA256 = (
    "6DBB57FEF040413E4A2D4E5C2FB98E880D959F68A7ECF83CC922686A9A5887F9"
)
XEDIT_SOURCE_COMMIT = "e0e529a2d473756520f2d41f72c24dea0cf5ee0d"
CONDITION_FUNCTION_COUNT = 638
PARAMETER_TYPE_COUNT = 64
USED_PARAMETER_TYPE_COUNT = 62
MAX_CONDITION_INDEX = 12004
HIGH_INDEX_COUNT = 49
TYPE_OVERRIDE_ELIGIBLE_FUNCTION_COUNT = 68
TYPE_OVERRIDE_ELIGIBLE_SLOT_COUNT = 69

NON_FORM_PARAMETER_TYPES = (
    "ptNone",
    "ptFloat",
    "ptInteger",
    "ptString",
    "ptActorValue",
    "ptAlias",
    "ptAttackData",
    "ptEvent",
    "ptPackdata",
    "ptQuestStage1",
    "ptQuestStage2",
    "ptAlignment",
    "ptAxis",
    "ptCastingSource",
    "ptCrimeType",
    "ptCriticalStage",
    "ptFormType",
    "ptFurnitureAnim",
    "ptMiscStat",
    "ptSex",
    "ptWardState",
    "ptFurnitureEntry",
)

FORM_ID_PARAMETER_TYPES = (
    "ptAcousticSpace",
    "ptActor",
    "ptActorBase",
    "ptAssociationType",
    "ptBaseObject",
    "ptCell",
    "ptChallenge",
    "ptClass",
    "ptConditionForm",
    "ptConstructibleObject",
    "ptCurrency",
    "ptDailyContentGroup",
    "ptDamageType",
    "ptEffectItem",
    "ptEncounterZone",
    "ptEntitlement",
    "ptEquipType",
    "ptEventData",
    "ptFaction",
    "ptFactionNull",
    "ptFormList",
    "ptFurniture",
    "ptGlobal",
    "ptIdleForm",
    "ptInventoryObject",
    "ptKeyword",
    "ptLocation",
    "ptLocationRefType",
    "ptMagicEffect",
    "ptOwner",
    "ptPackage",
    "ptPerk",
    "ptPerkCard",
    "ptQuest",
    "ptRace",
    "ptReference",
    "ptRegion",
    "ptScene",
    "ptSpell",
    "ptVoiceType",
    "ptWeather",
    "ptWorldspace",
)

EXPECTED_PARAMETER_TYPES = NON_FORM_PARAMETER_TYPES + FORM_ID_PARAMETER_TYPES
NUMERIC_TYPES = {item.casefold() for item in NON_FORM_PARAMETER_TYPES[1:]}
FORM_ID_TYPES = {item.casefold() for item in FORM_ID_PARAMETER_TYPES}
TYPE_OVERRIDE_ELIGIBLE_TYPES = {"ptreference", "ptactor", "ptpackage"}

EXPECTED_HIGH_INDICES = frozenset(
    [*range(5000, 5008), 6000, *range(8000, 8008), *range(9000, 9006),
     *range(10000, 10021), *range(12000, 12005)]
)
EXPECTED_LEGACY_OR_COLLISIONS = (
    (904, 5000),
    (905, 5001),
    (906, 5002),
    (907, 5003),
    (908, 5004),
    (909, 5005),
    (910, 5006),
    (911, 5007),
)


@dataclass(frozen=True)
class ConditionFunction:
    index: int
    name: str
    param_types: tuple[str, str, str]


def require_xedit_hash(path: Path) -> None:
    if not path.is_file():
        raise SystemExit(f"missing xEdit input: {path}")
    actual = hashlib.sha256(path.read_bytes()).hexdigest().upper()
    if actual != EXPECTED_XEDIT_SHA256:
        raise SystemExit(
            f"unexpected xEdit SHA-256 for {path}: expected "
            f"{EXPECTED_XEDIT_SHA256}, found {actual}"
        )


def parse_xedit_conditions(path: Path) -> list[ConditionFunction]:
    text = path.read_text(encoding="utf-8-sig", errors="strict")
    if "Mozilla Public License" not in text or "v. 2.0" not in text:
        raise SystemExit("xEdit FO76 source no longer carries its MPL-2.0 notice")

    enum_start = text.index("TConditionParameterType = (")
    enum_end = text.index(");", enum_start)
    parameter_types = tuple(
        re.findall(r"\bpt[A-Za-z0-9_]+\b", text[enum_start:enum_end])
    )
    if parameter_types != EXPECTED_PARAMETER_TYPES:
        raise SystemExit(
            "xEdit FO76 condition-parameter taxonomy changed: expected "
            f"{EXPECTED_PARAMETER_TYPES}, found {parameter_types}"
        )

    table_start = text.index("wbConditionFunctions : array[0..637]")
    table_end = text.index("function wbConditionDescFromIndex", table_start)
    table = text[table_start:table_end]
    entry_pattern = re.compile(
        r"\(Index:\s*(\d+);\s*Name:\s*'((?:''|[^'])+)'(?P<body>[^\r\n]*)\)",
        re.IGNORECASE,
    )
    parameter_pattern = re.compile(
        r"ParamType([123])\s*:\s*(pt[A-Za-z0-9_]+)", re.IGNORECASE
    )
    conditions: list[ConditionFunction] = []
    for match in entry_pattern.finditer(table):
        declared = ["ptNone", "ptNone", "ptNone"]
        for parameter_match in parameter_pattern.finditer(match.group("body")):
            declared[int(parameter_match.group(1)) - 1] = parameter_match.group(2)
        conditions.append(
            ConditionFunction(
                int(match.group(1)),
                match.group(2).replace("''", "'"),
                tuple(declared),
            )
        )

    if len(conditions) != CONDITION_FUNCTION_COUNT:
        raise SystemExit(
            f"expected {CONDITION_FUNCTION_COUNT} xEdit conditions, found {len(conditions)}"
        )
    if len({condition.index for condition in conditions}) != len(conditions):
        raise SystemExit("xEdit FO76 conditions contain duplicate raw indices")
    if len({condition.name.casefold() for condition in conditions}) != len(conditions):
        raise SystemExit("xEdit FO76 conditions contain duplicate names")
    if conditions != sorted(conditions, key=lambda item: item.index):
        raise SystemExit("xEdit FO76 conditions are not sorted by raw index")
    if max(condition.index for condition in conditions) != MAX_CONDITION_INDEX:
        raise SystemExit("unexpected maximum FO76 condition index")

    used_types = {
        item.casefold() for condition in conditions for item in condition.param_types
    }
    expected_types = {item.casefold() for item in EXPECTED_PARAMETER_TYPES}
    if len(used_types) != USED_PARAMETER_TYPE_COUNT or not used_types.issubset(expected_types):
        raise SystemExit(f"unexpected used FO76 parameter types: {sorted(used_types)}")

    high_indices = {condition.index for condition in conditions if condition.index >= 0x1000}
    if high_indices != EXPECTED_HIGH_INDICES or len(high_indices) != HIGH_INDEX_COUNT:
        raise SystemExit(f"unexpected high FO76 condition indices: {sorted(high_indices)}")

    by_legacy_key: dict[int, list[int]] = {}
    for condition in conditions:
        by_legacy_key.setdefault(0x1000 | condition.index, []).append(condition.index)
    collisions = tuple(
        tuple(indices)
        for _, indices in sorted(by_legacy_key.items())
        if len(indices) > 1
    )
    if collisions != EXPECTED_LEGACY_OR_COLLISIONS:
        raise SystemExit(
            f"unexpected legacy OR-key collision pairs: expected "
            f"{EXPECTED_LEGACY_OR_COLLISIONS}, found {collisions}"
        )

    eligible_functions = sum(
        any(item.casefold() in TYPE_OVERRIDE_ELIGIBLE_TYPES for item in condition.param_types[:2])
        for condition in conditions
    )
    eligible_slots = sum(
        item.casefold() in TYPE_OVERRIDE_ELIGIBLE_TYPES
        for condition in conditions
        for item in condition.param_types[:2]
    )
    if eligible_functions != TYPE_OVERRIDE_ELIGIBLE_FUNCTION_COUNT:
        raise SystemExit(
            f"expected {TYPE_OVERRIDE_ELIGIBLE_FUNCTION_COUNT} Type-override functions, "
            f"found {eligible_functions}"
        )
    if eligible_slots != TYPE_OVERRIDE_ELIGIBLE_SLOT_COUNT:
        raise SystemExit(
            f"expected {TYPE_OVERRIDE_ELIGIBLE_SLOT_COUNT} Type-override slots, "
            f"found {eligible_slots}"
        )

    by_index = {condition.index: condition for condition in conditions}
    expected_controls = {
        161: ("GetIsCurrentPackage", ("ptPackage", "ptNone", "ptNone")),
        407: ("GetVATSValue", ("ptInteger", "ptInteger", "ptNone")),
        893: ("IsPreviousMeleeAttackEvent", ("ptAttackData", "ptNone", "ptNone")),
        904: ("CHAL_IsTargetWorkshopRecipe", ("ptConstructibleObject", "ptNone", "ptNone")),
        5000: ("IsInAirOrFloating", ("ptNone", "ptNone", "ptNone")),
        5004: ("PlayerHasQuest", ("ptQuest", "ptNone", "ptNone")),
        12004: ("GetEquippedWeaponHealthPercent", ("ptNone", "ptNone", "ptNone")),
    }
    actual_controls = {
        index: (by_index[index].name, by_index[index].param_types)
        for index in expected_controls
    }
    if actual_controls != expected_controls:
        raise SystemExit(
            f"unexpected FO76 condition controls: expected {expected_controls}, "
            f"found {actual_controls}"
        )
    return conditions


def condition_kind(parameter_type: str) -> str | None:
    normalized = parameter_type.casefold()
    if normalized == "ptnone":
        return None
    if normalized in NUMERIC_TYPES:
        return "ConditionParamKind.Numeric"
    if normalized in FORM_ID_TYPES:
        return "ConditionParamKind.FormId"
    raise ValueError(parameter_type)


def csharp_string(value: str) -> str:
    return (
        value.replace("\\", "\\\\")
        .replace('"', '\\"')
        .replace("\r", "\\r")
        .replace("\n", "\\n")
    )


def generate_csharp(conditions: list[ConditionFunction]) -> str:
    lines = [
        "// <auto-generated>",
        "// Generated by tools/extract_fo76_condition_functions.py from a pinned community oracle.",
        f"// xEdit source commit: {XEDIT_SOURCE_COMMIT}",
        f"// xEdit wbDefinitionsFO76.pas SHA-256: {EXPECTED_XEDIT_SHA256}",
        "// Provenance: xEdit source under MPL-2.0. These 638 rows provide community condition",
        "// names and coarse CTDA parameter-storage kinds; no engine command/callback identity is claimed.",
        "// Forty-nine raw indices are >= 0x1000. Eight low/high pairs collide under the legacy",
        "// bitwise-OR opcode projection, so this table is keyed only by the exact raw CTDA index.",
        "// This is not a Fallout 76 script-opcode or Papyrus command table.",
        "// </auto-generated>",
        "",
        "using BethesdaMultitool.Core.Formats.Esm.Script.Conditions;",
        "",
        "namespace BethesdaMultitool.Core.Formats.Esm.Script;",
        "",
        "internal static class Fallout76ConditionFunctionTable",
        "{",
        f"    internal const int ConditionFunctionCount = {CONDITION_FUNCTION_COUNT};",
        f"    internal const int ParameterTypeCount = {PARAMETER_TYPE_COUNT};",
        f"    internal const int UsedParameterTypeCount = {USED_PARAMETER_TYPE_COUNT};",
        f"    internal const int HighRawIndexCount = {HIGH_INDEX_COUNT};",
        f"    internal const int LegacyOrCollisionPairCount = {len(EXPECTED_LEGACY_OR_COLLISIONS)};",
        f"    internal const int TypeOverrideEligibleFunctionCount = {TYPE_OVERRIDE_ELIGIBLE_FUNCTION_COUNT};",
        f"    internal const int TypeOverrideEligibleSlotCount = {TYPE_OVERRIDE_ELIGIBLE_SLOT_COUNT};",
        f"    internal const ushort MaximumRawIndex = {MAX_CONDITION_INDEX};",
        "",
        "    // Raw CTDA index -> xEdit-facing condition definition. Script-call metadata remains empty",
        "    // because this source does not prove a Fallout 76 command table or engine callback subset.",
        "    internal static readonly Dictionary<ushort, ScriptFunctionDef> ConditionFunctions = new()",
        "    {",
    ]
    for condition in conditions:
        lines.append(
            f'        [0x{condition.index:04X}] = new("{csharp_string(condition.name)}", "", '
            "false, [], true),"
        )
    lines.extend(
        [
            "    };",
            "",
            "    // Base storage kinds from xEdit. Null/omitted slots fail closed as raw values.",
            "    internal static readonly Dictionary<ushort, ConditionParamKind?[]> ConditionParamKinds = new()",
            "    {",
        ]
    )
    for condition in conditions:
        kinds = [condition_kind(item) for item in condition.param_types[:2]]
        last = max((index for index, item in enumerate(kinds) if item is not None), default=-1)
        rendered = ", ".join(item or "null" for item in kinds[: last + 1])
        lines.append(f"        [0x{condition.index:04X}] = [{rendered}],")
    lines.extend(
        [
            "    };",
            "",
            "    // FO76 xEdit applies Type.UseAliases/UsePackdata only to declared Reference, Actor,",
            "    // or Package slots. This raw-index metadata is separate from the base FormID kind.",
            "    internal static readonly Dictionary<ushort, bool[]> ConditionTypeOverrideEligibility = new()",
            "    {",
        ]
    )
    for condition in conditions:
        eligible = [
            item.casefold() in TYPE_OVERRIDE_ELIGIBLE_TYPES
            for item in condition.param_types[:2]
        ]
        if not any(eligible):
            continue
        last = max(index for index, item in enumerate(eligible) if item)
        rendered = ", ".join("true" if item else "false" for item in eligible[: last + 1])
        lines.append(f"        [0x{condition.index:04X}] = [{rendered}],")
    lines.extend(["    };", "}", ""])
    return "\n".join(lines)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--xedit", type=Path, default=DEFAULT_XEDIT)
    parser.add_argument("-o", "--output", type=Path, default=DEFAULT_OUTPUT)
    parser.add_argument("--verify-only", action="store_true")
    args = parser.parse_args()

    require_xedit_hash(args.xedit)
    conditions = parse_xedit_conditions(args.xedit)
    generated = generate_csharp(conditions)

    if args.verify_only:
        if not args.output.is_file():
            raise SystemExit(f"generated output is missing: {args.output}")
        actual = args.output.read_text(encoding="utf-8")
        if actual != generated:
            raise SystemExit(f"generated output is stale: {args.output}")
    else:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(generated, encoding="utf-8", newline="\n")

    print(
        "PASS: 638 xEdit FO76 condition rows; 49 high raw indices; "
        "8 legacy OR-key collision pairs"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
