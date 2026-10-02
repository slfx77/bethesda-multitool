# Placement inspection: Batch A

Implements P2-T01, P2-T02 and P2-T04 from the second research pass.

- `show <file> <FormID|EditorID> [--offset <decimal|0xhex>]` inspects REFR, ACHR and ACRE occurrences, including header-only deleted records. More than one physical occurrence returns status 2 and lists offsets. An unmatched offset returns status 1. Load-order inspection selects occurrences in the winning plugin and explicitly labels its file-local identifiers.
- `esm cell objects <file> <cell> --format table|json|csv [--output <new-file>] [--offset <cell-offset>]` exports full stored float precision. JSON and CSV include record and parent-cell offsets, instance/base IDs and names, cell/worldspace identity, rotations, flags, enable-parent polarity, source path/kind and payload status. JSON uses generated serialization metadata for the shipped CLI. Nonfinite captured floats are explicit named JSON strings.
- Cell lookup tries a quiet FormID parse, exact EditorID, then name/EditorID substrings. Ambiguous queries list candidates and return status 2; missing records return status 1. No output artifact is created for either case. Existing output files are never overwritten.

The shared query preserves `(signature, FormID, offset)` occurrences. Plugin parentage comes from physical cell-child GRUP framing rather than the semantic FormID map. Only small GRUP metadata is retained; the normal loader still releases its large REFR cache. Placement bodies use the central bounded/decompressing reader. Dump-only runtime recoveries are labelled separately; unresolved parent-cell occurrences stay unresolved. Stored flags do not establish current runtime enabled state.

`--limit` applies to every format (default 50; 0 means all). JSON has total, returned and truncated metadata; CSV repeats these fields per data row. Empty CSV has its schema header; the command always writes a count summary to stderr. Persistent overlays use the same worldspace and half-open exterior grid bounds and retain different occurrences sharing a FormID.

Focused validation class: `BethesdaMultitool.Tests.CLI.PlacementInspectionTests`. Its parameterized fixtures cover the three signatures, duplicate reference/cell IDs, offset selection, precise coordinate/rotation output, quoted CSV fields, successful supported identifiers, missing/ambiguous queries and shipped JSON serialization. Additional cases cover overlay boundaries, limit metadata, deleted records and refusal to overwrite files. All placement cases passed in the root's second integrated checkpoint. The initial run caught semantic logs contaminating structured stdout; the command now routes those logs to stderr. Builds and tests remain coordinated by the root session.

Shared presenter registration also includes the root lane's `ImageSpaceModifierDetailBuilder` for CLI lookup and GUI record selection.

Useful corpus rechecks after the integrated build: July prototype `show --full` for `00152406`, `00161493` and `0016576B`; OWB cell `NVDLC03X13StealthTest --format json --limit 0`. Preserve new receipts separately from historical research evidence.
