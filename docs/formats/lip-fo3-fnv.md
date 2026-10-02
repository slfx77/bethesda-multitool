# Fallout 3 / New Vegas LIP inspection

`lip inspect voice.lip --json --samples` decodes the verified revision-one,
compressed, little-endian profile. Without `--samples`, JSON contains metadata
and track identities; without `--json`, the command prints a short text report.
Malformed or unsupported inputs return a nonzero exit code and a diagnostic on
stderr. The inspector never writes a game file.

In the Windows dialogue audio pane, select an original voice source and choose
**Inspect LIP**. The dialog reports missing or ambiguous companions explicitly.
For a unique readable companion it shows source provenance, metadata and a
frame selector with the thirty-three stored weights. Reads retain the Explore
snapshot and compare the returned path, source and size with the catalog;
replacing the source closes the dialog. Only one frame's UI rows are created,
so long files do not produce a control for every sample.

This is a source-weight timeline. It retains signed values and values above one,
and gives each row a time relative to the first sample at 30 Hz. It does not
apply an actor's facial morphs, engine interpolation, head modifier settings,
or audio synchronization. FO4, Skyrim, big-endian, uncompressed and legacy
headerless variants are not claimed by this implementation.

## Verified layout

The 12-byte header contains little-endian uint32 revision, declared size, and
flags. The accepted values are revision `1` and flags `1` (compressed). The
remaining bytes form a byte stream: a nonzero byte is literal; a zero byte is
followed by a little-endian uint16 count of zero bytes. Runs may end inside a
float. In particular, `00 11 00` means **seventeen zero bytes**, not a float
marker or a curve tag. Compression starts at byte 12, including the frame
count and starting-frame fields.

The expanded body begins with a uint32 frame count and an int32 starting frame.
Each row then contains sixteen phoneme float32 values and seventeen modifier
float32 values, all little endian. All 19,405 inspected files satisfy:

```text
expanded body bytes = 8 + frameCount * 33 * 4
declared size       = expanded body bytes + 16
```

The declared size is not the compressed file length or a time quantity. Its
sixteen-byte bias is retained as an observed profile constraint, not given an
invented semantic name. The reader validates both equalities, consumes exactly
one complete input, rejects non-finite weights and bounds encoded/expanded
allocation at 5 MiB / 64 MiB. Unknown profiles fail explicitly. Signature
scanning remains disabled because there is no unique file magic.

| Indices | Group | Names in source order |
| --- | --- | --- |
| 0–15 | Phoneme | Aah, BigAah, BMP, ChJSh, DST, Eee, Eh, FV, I, K, N, Oh, OohQ, R, Th, W |
| 16–32 | Modifier | BlinkLeft, BlinkRight, BrowDownLeft, BrowDownRight, BrowInLeft, BrowInRight, BrowUpLeft, BrowUpRight, LookDown, LookLeft, LookRight, LookUp, SquintLeft, SquintRight, HeadPitch, HeadRoll, HeadYaw |

## Evidence and limits

The primary public header reference is
[Nukem9 FaceFXWrapper, revision 85a0f2a](https://github.com/Nukem9/FaceFXWrapper/blob/85a0f2ac041f55653afbd7c70614f62c1b2bc1a4/FFXW32/LipSynchAnim.h).
Its wrapper source is MIT-labelled, while its bundled Creation Kit resources
have separate Bethesda terms. Neither source code nor bundled engine resources
were imported. The header facts were independently confirmed in the private
New Vegas PDB. The
[OpenFaceFX experimental writer, revision 7333dc2](https://github.com/OpenFaceFX/OpenFaceFX/blob/7333dc2ffa844e8b3efdecdcfdc813162aea673a/src/openfacefx/export_lip.py)
was investigated under its
[MIT license](https://github.com/OpenFaceFX/OpenFaceFX/blob/7333dc2ffa844e8b3efdecdcfdc813162aea673a/LICENSE).
Its float-token and provisional slot hypotheses were not adopted: the byte
codec above handles non-word-sized runs present in the Fallout corpus.

The matching private July 2010 New Vegas PDB names `LipSynchAnim.iNumFrames`,
`iStartingFrame`, `ppPhonemes`, and `ppModifiers` in type `0x1f8ac` / field list
`0x1f8ab`. Its phoneme and modifier enums (`0xbb9e` and `0xbba0`) supply the
indexed names. The matching executable's `Scatter` routine (section 4,
offset `0x11e1f0`, length `0x1f0`) loads the first sixteen floats into the
phoneme row and the next seventeen into the modifier row. This establishes
ordering independently of similarities between curves. It also applies
settings to the final three head modifiers; the inspector preserves their
original stored values.

`ApplyToNode` (section 4, offset `0x11dff8`, length `0x1a4`) submits both rows
through `AddPhonemeKeyframe` / `AddModifiersKeyframe` using the float32 constant
`0x3d088889` (1/30 second) at virtual address `0x82016180`. A negative starting
frame is separately used to derive a reset delay, capped at 0.2 seconds in
that routine. The raw signed starting frame is therefore preserved, and
reported relative sample times deliberately do not assert an audio origin.
One example has 124 rows while its OGG lasts 2.618958 seconds; deriving the
frame rate from audio duration would be wrong. Actual actor playback and
retargeting remain separate work.

Private research input hashes (SHA-256; assets and disassembly are not tracked):

| Input | SHA-256 |
| --- | --- |
| July 2010 `Fallout.pdb`, 124808192 bytes | `c2893c7b5377d76d7d5c7fe6b05d9b1cbe1084b63b9d3b695db92473a55c1d6b` |
| Matching unpacked `Fallout.exe`, 18388480 bytes | `f3579c4fd04f7e6432d0154fcc5bebac6854a8c3445977ba7c2e0ccd046faf52` |
| FNV `radionewvegas_rnvnewsintro_0014dfb7_1.lip`, 4412 bytes | `96812756df0ae03c6cbdbbbd094ed97c7792011865b6ca95ee817e79f4ab5586` |
| FO3 `audioholot_ffraidercamp07h_00029f5f_1.lip` | `f3932429e7cf7839dde522edcdda48167850abf92ac1c95aed00c5e823aad322` |

An independent Python byte decoder validated 181 FNV MrNewVegas companions
and 19,224 FO3 MenuVoices companions. Every file used revision/flags `1/1`,
expanded to the exact shape above, and contained finite values. Combined
sample range was -0.07256102 to 1.2409476; frame count reached 817 and signed
starting frame ranged from -37 to 23. This verifies codec and stored layout
coverage, not every game release, actor morph application or in-game playback.
Local evidence is in shared `TestOutput/lip-research/`; proprietary fixtures
are not part of the test suite or packages.

The exact names and engine timebase are verified against New Vegas symbols and
code. Applying those identities to FO3 is an inference from its matching stored
layout; this pass did not independently inspect a FO3 engine symbol set.

The shared repository's dependency-free `tools/validation/lip/inspect_corpus.py` reproduces the
independent byte decoder from explicit `--root`, `--directory` and `--output`
arguments. It skips linked directories and files and reports rejected inputs
as failures. Its generated manifest includes every input hash. Set
`BMT_LIP_CORPUS_ROOT` and semicolon-separated `BMT_LIP_CORPUS_MANIFESTS` to run
`LipCorpusTests.MatchesIndependentManifest` against those exact files; absent
explicit inputs are reported as a skip, never a successful corpus check.

## Complexity and verification

For C compressed bytes and D expanded bytes, decode takes O(C + D) time and
O(D) memory, with explicit allocation bounds. Random sample access and time
lookup take O(1) time and space. File reading adds filesystem I/O costs and
O(C) storage. JSON sample output takes O(frameCount × 33) time plus stream I/O;
the writer flushes periodically to bound its buffer. The fixed track table
has constant cost. These are source-level bounds, not empirical throughput
claims. Cancellation is checked while expanding, interpreting samples and
writing sample rows. Synthetic tests cover partial-float zero runs, source
identities, signed/above-one values, truncation, trailing bytes, malformed
runs, allocation bounds, row counts, non-finite values, unsupported profiles,
encoded-length accounting, cancellation and JSON stream ownership.

The September 9 validation run passed eleven synthetic tests plus the explicit
19,405-file C# differential corpus test (12 passed, zero failed or skipped).
The portable and combined Windows app builds passed with zero warnings or errors. Process-scoped
UI Automation verified the real FNV companion's 124 rows, 33 tracks, 30 Hz,
starting frame -5, 4412 encoded bytes and declared size 16392; setting frame
index 30 displayed 1.000000 seconds and thirty-three weight rows. Dialog
closure and subsequent source replacement passed. Local Windows evidence is
`TestOutput/actor-explore-smoke/dialogue-lip-ui.log` and
`dialogue-lip-windows-build.log` in the app worktree; the shared workspace's
`TestOutput/bmt-lip-tests/` contains the test report. FO3 and FNV real-file CLI
JSON was also checked independently. Unsupported profiles and malformed
input were checked on both binaries for nonzero exit status, empty stdout and
concise diagnostics without unhandled exception stacks. Final portable evidence
is in shared `TestOutput/bmt-lip-cli-build-final.log` and
`TestOutput/lip-research/final-portable-cli-checks.json`.
