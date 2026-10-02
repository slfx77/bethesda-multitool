# FaceGen TRI source inspection and geometry binding

The strict reader in `Core/Formats/FaceGen/Tri/` preserves the complete
documented little-endian `FRTRI003` representation. The older carving and
heuristic inspection APIs remain available while their adapters are evaluated.

```text
btool tri inspect <file.tri>
btool tri inspect <file.tri> --json
btool tri inspect <file.tri> --data > <inspection.json>
```

`--data` adds original coordinates, facet/UV indices, labels and packed
differential values. Statistical records retain their own family, indices
and consecutive target ranges. JSON does not silently convert target
coordinates to deltas. A failed parse writes a concise diagnostic to stderr
and returns nonzero before opening stdout for JSON.

## Evidence and representation

The [vendor format reference](https://facegen.com/dl/sdk/doc/manual/fileformats.html)
defines the header and ordered sections. This implementation uses those
format facts; it includes no vendor implementation. Original bytes remain
identifiable by SHA-256. Both indexed/per-vertex UVs, quads, vertex labels,
wide surface labels and reserved bytes are retained. Morph names require
their documented terminal NUL. Their byte strings use a lossless Latin-1
display convention; wide surface strings use UTF-16LE. Raw bytes remain
available independently of display decoding. Unknown extension bits fail
explicitly, and trailing bytes are rejected.

The [vendor's morph explanation](https://facegen.com/man_add_morphs.htm)
distinguishes relative difference shapes from statistical targets that
participate in face shaping. The actual source record family is authoritative:
the observed FNV `Th` record is differential despite a generic vendor example
discussing that name as a statistical target.

An independent authored Python probe consumed all 49 TRI files extracted from
the FNV PC `Fallout - Meshes.bsa`, with no rejected files. Its versioned copy is
in the shared repository at `tools/validation/tri/inspect_corpus.py`; explicit
input instructions are beside it. The probe independently records byte hashes,
scale/nonzero counts and statistical ranges. Optional labelled vertex/surface
sections do not occur in that corpus and are covered by synthetic fixtures.

Private anchors (SHA-256; source bytes are not tracked):

| Source | Base/target vertices | SHA-256 |
| --- | --- | --- |
| `headhuman.tri` | 1211 / 238 | `6262171ec744cf58ee7cf4e1fbe18482a8d589b948e6c968ff6caaa8712c227c` |
| `eyelefthuman.tri` | 49 / 196 | `ab68e44b6227585116093e461562812a444466b1f648f192fa1612263a10019b` |
| `mouthhuman.tri` | 27 / 0 | `898b57abb60a4706da6c706b3950bfcecf91f5d6597ccc612cced158c67cd744` |

The head has 38 differential records and eight statistical Blink/Squint/Look
records. Eyes have statistical Look records; mouth/teeth/tongue expose other
subsets. Applying every head weight to every part cannot preserve that data.
The verified LIP/PDB track `Eee` differs from the observed TRI label `Ee`.
`ResolveMorph` therefore uses exact case-sensitive labels and rejects missing
or ambiguous matches; there is no automatic spelling alias.

## Binding and neutral-pose operations

`TriNifBinding.Bind` takes an explicit NIF source name, complete source bytes,
geometry-data block index, TRI document and caller-selected statistical
reference domain. It computes the NIF hash and extracts that exact block in
its local coordinate frame. The initial adapter admits little-endian
`NiTriShapeData` and `NiTriStripsData`; other geometry families are explicitly
unsupported. Caller-owned source bytes must remain stable during this
synchronous operation.

The binding requires equal base-domain lengths and identical oriented indexed
triangles. Comparison permits facet reordering and cyclic corner rotation;
it preserves winding and duplicate facet multiplicity. Only repeated-index
degenerate facets are excluded, with counts retained in `TriTopologyMatch`.
It does not invent vertex correspondence from proximity or equal counts.
Quads remain inspectable but are not admitted by this NIF adapter.

The original NIF neutral positions are retained separately from the TRI base
coordinates. Those coordinates demonstrably differ in the FNV head pair.
Every `Evaluate` call starts from its owned NIF neutral positions, adds the
explicitly selected differential displacements, and adds each statistical
target-minus-base displacement from the explicit V+K reference domain.
The domain must already contain any intended actor-specific shape statistics.
Passing the concatenated raw TRI arrays is an explicit unshaped diagnostic
choice, not an NPC shape reconstruction.

Private matching Aug MemDebug/PDB inspection of
`BSFaceGenMorphDifferential::ApplyMorph` (section 4, offset `0x2442f8`,
length 832) and `BSFaceGenMorphStatistical::ApplyMorph` (section 4,
offset `0x2446a8`, length 1036) corroborates the separate current-position
and reference-domain arithmetic. The latter subtracts the affected base
domain vertex from its corresponding target domain vertex before adding to
the current geometry. Its engine weight admission is separately restricted
to positive values through one. The new evaluator accepts finite signed
diagnostic weights and does not claim to reproduce that admission policy.

Duplicate morph selections, invalid references, non-finite inputs or
non-finite evaluated coordinates fail. Result arrays and optional duplicate
destination maps are separate objects, so failed/cancelled calls cannot
change persistent neutral state. `WithDestinationMap` requires an explicit
destination-to-source mapping that covers every source vertex. It never
truncates to the shorter array.

This increment exposes source inspection and position evaluation. It does
not update actor renderer geometry, recompute normals/tangents, apply skinning,
select LIP identities or establish an audio origin. The existing viewer
morph-plus-skin path requires separate work: skin scratch currently captures
its own neutral arrays and would overwrite a simply inserted morph result.
FO3 corpus binding and Xbox-endian TRI input are not claimed by FNV evidence.

## Bounds and method review

Limits are explicit reader policy, not format maxima: encoded TRI ≤16 MiB,
combined V+K ≤500,000, total morph records ≤4,096, labels ≤4,096 stored units;
the NIF adapter accepts at most 64 MiB and a destination map at most 2,000,000
vertices. Header arithmetic uses wide checked
sizes before allocation, all coordinates/scales/decoded products must be
finite, and statistical affected counts must sum exactly to K.

For encoded bytes B, vertex count V, statistical target count K, facet count
F, selected differential records R, selected statistical index occurrences L,
selected weight count M and destination count D:

| Authored operation | Time | Additional memory and assumptions |
| --- | --- | --- |
| `TriReader.Read` and sequential cursor helpers | O(B) | O(B) owned arrays/labels; fixed header work O(1); scalar reads/checks O(1) |
| `ReadFileAsync` | O(B) plus filesystem costs | O(B) encoded bytes in addition to parsed arrays; bounded source stream |
| Document/model construction | O(1) wrapper work | Parser-owned arrays retained without copying; fixed number of wrappers |
| `GetDelta`, generated property access, destination count | O(1) | O(1) |
| `ResolveMorph` | Sum of examined label comparisons | O(1) successful lookup; exception/message allocation depends on supplied label length |
| `TriGeometryBinding.Create`, `FacetKeys` | O(V+K+F log F) | O(V+K+F), including sorted keys and copied neutral/reference domains |
| `TriNifBinding.Bind` | NIF parse/extract costs + O(NIF bytes+V+F log F+K) | NIF dependency allocations + O(V+F+K); existing NIF parser/extractor costs are delegated, not asserted constant |
| `WithDestinationMap` | O(V+D) | O(V+D) coverage bitmap and copied map; retained immutable arrays are shared |
| `Evaluate` | O(V+D+V×R+L+M) expected | O(V+D+M); hash set operations expected amortized O(1), pathological collisions can add O(M²) |
| JSON data export | O(emitted values + label bytes) plus output costs | Periodic flushing bounds large numeric-array buffering; caller stream costs remain external |

Evaluation follows caller selection order, including float32 rounding. It
does not clamp values or merge repeated identities. Finite validation costs
O(number of coordinates) and publishes no partial result. Cancellation is
checked throughout the authored walks and I/O. Existing NIF parsing, hashing
and built-in array sorting are synchronous delegated operations; cancellation
is observed at the surrounding boundaries rather than asserted inside them.

Synthetic tests cover each retained section, dense-vs-statistical behavior,
truncation, counts, flags, indices, labels, finite arithmetic, exact identities,
JSON representation/stream ownership, topology, explicit shape domains,
neutral reset and duplicate destination mapping. Corpus tests require
`RUN_BUCKET_B=1`, `BMT_TRI_CORPUS_MANIFEST` and, for the selected NIF pairs,
`BMT_TRI_NIF_ROOT`. Unconfigured inputs are explicit skips; configured missing
or mismatched files are failures. Build/corpus acceptance results are recorded
after the final implementation checks, separately from the independent probe.

The final portable combined build's TRI run passed 17 tests with zero failures
or skips. This includes all 49 independent manifest entries and explicit
`headhuman` block 6 / `eyelefthuman` block 7 NIF bindings, with independently
observed neutral-coordinate checks. Six actual CLI process checks also passed:
complete head/eye/mouth JSON data and truncated/trailing/unknown-flag failures
with nonzero status, empty stdout and concise diagnostics. Evidence is in the
shared workspace at `TestOutput/bmt-tri-tests-final.log`,
`TestOutput/bmt-tri-tests-final/bmt-tri-tests.xml` and
`TestOutput/lip-research/tri-cli-final-checks.json`.

The Windows GUI build also passed the same six hidden CLI process checks,
using bare `tri inspect` without the compatibility GUI flag. Its evidence is
`TestOutput/lip-research/tri-cli-windows-checks.json`. The versioned shared
`tools/validation/tri-published-smoke.py` separately passed seventeen synthetic
commands on the portable Windows binary, recording executable/managed-payload
hashes and per-case outputs under `TestOutput/tri-published-smoke-portable/`.
These checks verify the format/CLI boundary and do not claim actor renderer
integration.
