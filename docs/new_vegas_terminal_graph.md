# Terminal graph inspection

`esm terminal-graph` follows the stored TNAM links between terminal menus. It exports each inspected terminal's menu items in stored, one-based order, including their complete conditions, result text, note links and embedded action text.

```powershell
.\EsmAnalyzer.exe esm terminal-graph .\DeadMoney.esm NVDLC01VaultMainInfoDownloadTerminal
.\EsmAnalyzer.exe esm terminal-graph .\DeadMoney.esm 0x0100DAD2 --format dot --max-depth 8 --max-nodes 200 --output .\vault.dot
.\EsmAnalyzer.exe esm terminal-graph .\DeadMoney.esm DeadMoney.esm:0x0100DAD2 --load-order .\FalloutNV.esm --output .\vault.json
```

The source may be an ESM/ESP or memory dump. A single-source numeric selector is hexadecimal, with an optional `0x` prefix. An EditorID must be unique. `--load-order` accepts the shared ordered plugin list; the input plugin is appended when absent. In this mode, unqualified numbers are global load-order IDs and a selector such as `DeadMoney.esm:0x0100DAD2` uses the named plugin's file-local ID. Missing masters are rejected unless `--allow-missing-masters` is supplied; reserved slots preserve their identity when allowed.

The default is JSON. Both JSON and DOT own stdout, with progress and errors on stderr. `--output` creates a new file and refuses to overwrite an existing one. JSON is written explicitly, without reflection serialization.

Traversal is breadth-first, visiting each terminal once. The root has depth zero. Defaults are `--max-depth 32` and `--max-nodes 1000`; allowed ranges are 0–1024 and 1–10000. These bounds limit inspected terminal records. Their outgoing edges are retained, so boundary references can outnumber inspected nodes. Cycle and shared-child edges remain, with `AlreadyDiscovered` distinguishing an already queued/visited target; that label alone does not claim an edge is a cycle.

JSON reports separate target availability and traversal status. Availability distinguishes a resolved target, missing record, unloaded master, deleted winner, conflicting record types, wrong record type, existing but unparsed TERM, or data not present in a partial capture. Depth/node limits are explicit, and `truncated` is true when either prevents inspection. Load-order JSON preserves the winning plugin and complete record-version history for nodes and edge targets. Single-source JSON carries raw record flags and offsets where available.

Action text is not truncated. Stored SCTX and BMT's SCDA decompilation are separate fields, using the shared provenance labels. Reconstructed text is never presented as original source; captured but withheld source has an explicit reason. Local variables, references, byte order and incomplete executable-bundle diagnostics are also available in JSON. Text retains its source input's ID namespace; it is not rewritten when structured references are mapped to load-order IDs. Use each node's winning-plugin provenance to interpret IDs in its text. Both formats explicitly label this distinction. DOT includes full displayed conditions and source/decompiled bodies in escaped labels, plus source path, load-order summary, winning records, bounds, truncation and partial-capture limitations.

This is a structural graph. Conditions are described, not evaluated. Script commands such as `ForceTerminalBack` are displayed but do not create inferred edges. The graph therefore does not prove that a player can reach a menu, or that an action will run. Absent data in a memory dump is explicitly absence from the capture, not from the historical build. No runtime simulator, script control-flow interpreter or condition satisfiability evaluator is provided.
