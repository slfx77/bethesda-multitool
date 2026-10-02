# DDX conversion losslessness, measured 2026-09-23

Question: is DDXConv's DDX-to-DDS conversion lossless for the DDX files the Xbox 360 New Vegas builds ship, and where do
its heuristic decode paths apply? The owner's position: lossless for the files inside the builds' `Fallout - Textures.bsa`
archives; the heuristics serve incomplete in-memory DDX carved from the prototype memory dumps. A design review had
called `DdxParser.ConvertDdxToDds` "a recovery pipeline, not a pure relayout".

Evidence lives outside the repository (`TestOutput/` is ignored): `TestOutput/ddx-lossless-20260923/` in this worktree.
Read `run1/report-20260923-155340/summary.md` (stages 0-2 and the controls) and `stage3-merged-173459.json` (the whole
population, merged over the resumed attempts `run1`, `run1s3c`, `run1s3d`, `run1s3r1`). The scripts (a PowerShell driver
that takes the 4 GiB build slot and runs every child under a memory watchdog, a Python BSA reader, the block oracle, the
controls) are in the session scratchpad `ddx-lossless/measure/`; nothing was built.

## Population

| Archive | Size | `.ddx` entries | Note |
|---|---:|---:|---|
| Final (2010-8-22) | 1,373,844,448 | 22,616 | SHA-256 `3f61c3a1…`; byte-identical to the 2010-8-22 and 2011-2-15 prototype archives |
| July 2010 prototype | 1,756,111,577 | 26,123 | one zero-byte entry (`machete_s.ddx`) |
| April 2010 prototype (partial) | 1,339,515,565 | 19,453 | 3,243 live; 16,210 all-zero placeholders; `bosunderarmor\outfitm.ddx` cut short |
| undated "X360 - Prototype" | 711,619,145 | 0 | holds 11,085 PC DDS files |

Entries were extracted raw (`archive extract -f .ddx`, never `-c`, which merges normal and specular maps) and every
extracted file matched the archive bytes by length and SHA-256.

## Results

- **DDXConv's per-file loss counters** (skipped, unwritten and duplicate destination blocks; padding; truncated reads),
  over every entry: zero on every complete block-compressed 3XDO entry. Nonzero only on the 10 (Final) and 13 (July)
  strips with format byte 0x43 or 0x86, and on April's cut entry (50,454 of 65,536 blocks unwritten). 3XDR rows report
  `n=0` because `Convert3Xdr` records nothing.
- **Independent oracle** (a Python transcription of the decompiled XGraphics tiling, sharing no code with DDXConv) over
  all 51,982 entries: no misplaced block, no output block absent from the decoded source, no non-zero block in the
  cropped tile padding, two-stream storage order coherent on every discriminating file, and the route each file took
  equal to the header prediction file for file.

| Archive | Exact relayout | 3XDR, drops stored mips | 3XDR, nothing to drop | Fabricated zero levels | Wrong-format strips | Other |
|---|---:|---:|---:|---:|---:|---|
| Final | 21,688 | 712 | 154 | 52 | 10 | — |
| July | 22,096 | 3,806 | 155 | 52 | 13 | 1 zero-byte |
| April | 2,977 | 265 | 0 | 0 | 0 | 1 cut entry |

- **Heuristic routes** (`UnswizzleDxtTextureHeuristic`, the atlas unpack, the double-size and large-linear routes, the
  full-atlas fallback): reached by 0 complete entries. Of 249 DDX carved from two prototype memory dumps, 164 were
  incomplete, took a damage or heuristic route, or raised counters; 62 decoded as exact relayouts.
- **Controls that had to fail did:** nine truncated copies plus the natural April one raised counters; five corrupted
  copies were caught by the oracle against their pristine twins (the counters cannot see them: LZX has no checksum);
  16 zero placeholders were rejected; the DDS-side flip and swap probes failed the intended tiers.

## Conclusion

For every complete block-compressed 3XDO entry the conversion is an exact relayout (endian swap, untile, crop of the tile
padding), and the heuristic code serves damaged or carved data only. Three losses on complete entries are real and are
counted by the ModelDocument design's DDX gate (docs/design/model-document-design-20260923.md §5.1): 3XDR entries decode
mip 0 only and silently drop their stored mip chain; the 23 strips are decoded under DXT1, the wrong format; about 52 UI
textures per build receive fabricated all-zero mip levels, which lose nothing. Not settled here: LZX decode correctness
(no independent decoder exists), the true format of the 23 strips, and the layout of the dropped 3XDR data.

DDXConv is Kran's MIT-licensed project (submodule `src/DDXConv`, the owner's fork); its notice is kept wherever the code
is used.
