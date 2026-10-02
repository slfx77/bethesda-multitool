# NIF loose-texture path resolution

`NifTextureDirectorySource` now resolves each requested path segment against the actual immediate directory entries using `OrdinalIgnoreCase`. This lets a normalized game path find mixed-case directories and DDS/DDX filenames on a case-sensitive filesystem. It retains the enumerated physical spelling for raw reads and asset metadata.

A segment is accepted only after its siblings have been fully examined and exactly one name matches. Two case aliases are ambiguous, even if one matches the request's spelling exactly. An ambiguous segment, invalid path, enumeration failure or exhausted budget declines the request without selecting an alternate DDS/DDX file. There is no persistent directory index, so later requests observe filesystem changes.

Decoded loads and `Exists` retain the existing DDS/DDX extension fallback. Decoded loads can also try the alternate when the primary cannot be read or decoded. `TryLoadRaw` and `TryGetAssetMetadata` continue to describe only the requested extension. Metadata reports the actual physical path, length and last-write timestamp; this is a filesystem observation, not an atomic content snapshot.

Requests are root-relative. Both separators, repeated separators and `.` segments are accepted; rooted paths, drive prefixes, `..`, NUL, empty requests and requests longer than 32,767 characters are declined. The reviewed callers supply normalized game-relative paths; no active caller was found to require arbitrary rooted access. Legitimate symlinks and junctions continue to work. The explicit root is a lookup scope, not a new filesystem sandbox.

## Resource and compatibility limits

The default budget processes at most 65,536 sibling entries across all segments and the optional extension fallback. The iterator can yield one further entry before the zero-budget check refuses it. Work also includes path parsing and per-segment filesystem operations. Only the requested directories are enumerated; there is no recursive corpus scan. Complete uniqueness requires scanning siblings even on Windows, so this adds a concrete cost compared with the previous direct path lookup.

The budget limits name lookup, not texture payload size, decoder allocation, filesystem latency or cancellation delay. Existing whole-file reads and decoding remain unchanged. Streaming enumeration avoids retaining an index, but its per-entry path strings still allocate. Files can change between lookup and read; this change does not introduce filesystem snapshot semantics.

The source and callable hashes, bounds and delegated costs are recorded in [the scoped complexity review](complexity/nif-directory-case-resolution-20260920.json).

## Validation status at source review

The new `NifTextureDirectorySourceTests` contains 20 cases covering mixed-case directories and extensions, separator forms, DDS/DDX priority, primary-only raw/metadata behavior, live filesystem changes, ambiguity, invalid scope and shared-budget exhaustion. Synthetic DDS bytes stored under a DDX suffix test routing, not DDX codec fidelity. Collision cases branch on actual filesystem capability without skipping; the case-sensitive branch requires a filesystem that supports distinct aliases. Link and junction behavior has source review but no new runtime fixture.

The applied changes are included in the coordinated Linux `20260920-nif-egm` batch: 291 focused cases plus two accessibility cases, with temporary fixtures on `/tmp` ext4. That batch was running when this note and its source-review receipt were written; no passing result is asserted here. Windows execution of the changed lookup and current GUI texture workflows remains separate acceptance work.

The subsequent [Linux execution record](validation/nif-egm-linux-20260921.md) reports all 20 lookup cases passing on ext4 in both the initial run and the repaired 321-case batch. The latter also passed both accessibility checks. Its later publish-restore failure is recorded separately; it does not negate those test results or establish publication acceptance.

The bounded Windows measurements are retained at `TestOutput/nif-directory-case-resolution-20260920/baseline-cost.json` and `candidate-cost.json`. The baseline used the verified pre-change portable assembly `2ff9e2c9f7fb3de4402d2390438f8a96dde34da03080b8c1cfa8e06a12ea8bdb`; the candidate used the Linux-compiled, source/PDB-verified application `e834880eacf195a880f1e5d6962bf8bbb5a522001f9340ac995b3983490d3e7c`, executed in fresh Windows PowerShell. Both used the same Windows host, NTFS and .NET 10.0.12, three warmup requests, then one sample for each operation and directory size.

| Sibling entries | Operation | Previous lookup (ms) | Unique lookup (ms) |
| --- | --- | ---: | ---: |
| 100 | Primary probe | 3.0470 | 4.4692 |
| 100 | One-byte raw hit | 0.6518 | 1.0244 |
| 100 | DDX fallback probe | 0.2665 | 1.2128 |
| 10,000 | Primary probe | 0.1559 | 18.6744 |
| 10,000 | One-byte raw hit | 0.2774 | 12.7521 |
| 10,000 | DDX fallback probe | 0.2429 | 17.6475 |

All six requests returned the expected result, the compiled assembly stayed unchanged, and the helper removed only its owned temporary directory. The measurements show a material scan cost at large sibling counts. They include reflection and filesystem latency and are one sample per operation, with different compiler origins; they are not a cold-cache or statistical benchmark, a total texture-decode timing, or a GUI latency acceptance result. Complete uniqueness and live filesystem observations are retained; actual workload performance remains a separate acceptance check.
