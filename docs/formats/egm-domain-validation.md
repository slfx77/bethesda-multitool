# EGM full-domain validation

The new bounded reader retains every little-endian `FREGM002` header field,
both principal-component families, original float scales, packed XYZ shorts
and exact source SHA-256. The geometry-basis version at byte 20 is an identity
field, followed by forty reserved bytes. The old renderer parser treated that
word as reserved; its APIs and behavior remain unchanged in this increment.

The implementation uses documented facts from the primary
[FaceGen format reference](https://facegen.com/dl/sdk/doc/manual/fileformats.html).
It includes no vendor or engine implementation. Encoded input is capped at
16 MiB, the domain at 500,000 vertices and each mode family at 200 entries,
matching the existing carving profile's bounds. Complete encoded length is
validated with wide arithmetic before mode allocation; non-finite scales and
scaled packed displacements fail explicitly. Unknown basis values remain
inspectable identities rather than being silently assigned a game profile.

## Explicit source and shape domain

`EgmShapeDomain.Create` requires the selected TRI and EGM documents, an
explicit expected basis key and exact coefficient family lengths. The EGM
vertex count must equal the selected TRI's V+K. Raw coefficient arrays carry
no self-identifying actor profile; callers remain responsible for supplying
coefficients from a verified matching basis. The function does not infer
that relationship from equal counts.

The result retains two separate full arrays: accumulated shape displacements
and TRI source coordinates plus those displacements. The latter is the
statistical target/reference domain. `ApplyBaseDisplacements` adds only the V
prefix to a copy of explicitly supplied unshaped NIF-local neutral positions.
Both are needed before statistical expressions: shaping the reference domain
alone would leave the displayed neutral mesh unshaped. The caller must supply
the original neutral positions, in the verified corresponding order and frame;
the method cannot identify a previous result or infer coordinate transforms.
It does not apply skinning.

Results own coefficient arrays and carry TRI/EGM hashes, the accepted basis
key and a coefficient hash. The canonical coefficient hash hashes ASCII
`EGMCOEF1`, little-endian uint32 basis, int32 family lengths, then original
float32 coefficient bits in symmetric/asymmetric source order. This retains
signed-zero identity without culture-dependent text formatting. The source
hashes remain separate parts of the provenance record.

Arithmetic multiplies mode scale by coefficient, then each packed component,
and accumulates symmetric then asymmetric modes in source order. It accepts
finite tiny/signed coefficients without epsilon suppression or clamping.
That is a deliberate diagnostic policy: existing `FaceGenMeshMorpher` skips
coefficients whose magnitude is below `1e-7` and may return null for nearly
zero accumulated results. Compatibility tests cover ordinary finite
coefficients, and a separate tiny-coefficient control demonstrates the known
difference. No renderer behavior is replaced under an equivalence claim.

## Paired corpus evidence

All 28 explicit loose FNV head-family TRI/EGM pairs have exact V+K agreement,
50 symmetric modes, 30 asymmetric modes and basis key `2001060901`. Examples:
headhuman has 1449 = 1211+238 vertices, eyes 245 = 49+196, female heads
2289 = 1211+1078. Full packed-mode hashes and nonzero suffix counts are in an
independent generated manifest; proprietary source bytes are not tracked.

The versioned shared `tools/validation/egm/inspect_pairs.py` reproduces those
checks from an explicit directory and records source/mode hashes. It reads
only immediate ordinary pairs, skips linked entries and reports missing or
malformed pairs as failures. The C# corpus test separately parses the full TRI
layout and compares all EGM mode hashes/counts with the manifest.

Private SHA-256 anchors:

| EGM source | SHA-256 |
| --- | --- |
| `headhuman.egm` | `c83a1bd794232edcc77fe5a6e630b1bda3fc16deb83137323b8860734e71fcd0` |
| `eyelefthuman.egm` | `2786dd9ee171cc50ff25714c02bec4031aa81b18b0e2914a478a82c4f02d5915` |
| `headfemale.egm` | `796e512c82240be529b5933c490f64862e432d3112a8c5e017bcb649a1aa0121` |

Set `RUN_BUCKET_B=1`, `BMT_EGM_PAIR_ROOT` and `BMT_EGM_PAIR_MANIFEST` to run
the explicit C# corpus check. Missing configuration is a skip; configured
missing/mismatched fixtures fail. The original `EgmParser` and existing
full-domain pre-skin displacement method are compared on these selected
inputs, including the statistical suffix, without changing them.

`NpcCompositionPlanner.BuildHeadPlan` already requests
`ComputeAccumulatedDeltas(..., egm.VertexCount)` and stores the full domain in
`HeadPreSkinMorphDeltas`; mesh extraction later consumes the visible prefix.
That is a future integration seam. It still requires source/basis validation,
explicit part binding and preservation of morph-before-skin behavior. No LIP
alias, audio origin, face-animation renderer hook or Xbox-endian format support
is introduced here.

## Authored method costs

Let B be encoded bytes, N=V+K, M the number of source modes and R the number
of nonzero selected coefficients. Counts and arithmetic are bounded before
allocation. These are source-level bounds rather than measured performance:

| Operation | Time | Additional memory |
| --- | --- | --- |
| Header validation and scalar/property access | O(1) | O(1) |
| `EgmReader.Read`, `ReadModes` | O(B) | O(B) retained mode arrays and fixed wrappers |
| `ReadFileAsync` | O(B) plus filesystem costs | O(B) encoded bytes in addition to parsed arrays |
| Document/mode/result constructors | O(1) wrapper work | Owned arrays retained; fixed wrappers |
| `EgmBasisMode.GetDelta` | O(1) | O(1) |
| `EgmShapeDomain.Create`, `Accumulate` | O(M+N+N×R) | O(M+N) coefficient/displacement/reference arrays |
| `ApplyBaseDisplacements` | O(V) | O(V) returned neutral-position copy |
| Coefficient validation/hashing | O(M) | O(1) hash scratch; coefficient arrays are already owned |
| Finite vector check | O(1) per vector | O(1) |

SHA-256 processing and file-stream I/O are delegated BCL operations; I/O waits
are not asserted bounded. Cancellation is checked before file opening and
through the authored parsing/accumulation/copy walks, with at most 4096 scalar
components or vertices between checks in the large inner loops. Canonical
coefficient hashing is synchronous over at most 400 coefficients. Failure
does not expose a partial domain or mutate caller positions.

Independent Python validation passed 28 pairs and all 2,240 mode records.
The initial portable C# build completed with no errors and exposed two new
EGM diagnostics: exact float-zero comparison and synchronous cancellation in an
async test. The former now has a local documented suppression because epsilon
comparison would change the diagnostic policy; the latter awaits cancellation.

The final combined portable build includes full API XML documentation, the
separate `EgmBasisMode.cs` file and those corrections. It completed in 52.81
seconds with zero warnings/errors using the fast `SkipAnalyzers=true` gate.
All eleven EGM tests passed with zero skips in 1.425 seconds, including the
complete paired corpus comparison. All seventeen TRI regression tests also
passed with zero skips in 1.530 seconds. Reports are in shared
`TestOutput/bmt-egm-tests-final.log`, `TestOutput/bmt-tri-tests-egm-final.log`
and the adjacent xUnit report directories. Full analyzer and platform
acceptance are separate gates; these results do not claim renderer adoption or
completed immersive playback.
