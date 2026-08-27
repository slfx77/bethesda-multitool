# Adversarial DMP recovery audit — 2026-08-25

Five-agent adversarial sweep answering: *is any decodable / compressed / usable data getting
skipped, missed, or misattributed by our code?* Dimensions: minidump structural coverage, carver
signature completeness, compression handling, silent drops & misattribution (verified against the
2026-08-02 corpus audit), and an empirical coverage probe (xex44, xex21, Debug xex2).

**Verdict: the "peak recovery" belief is close but not true yet.** The pipeline's foundations are
sound (streams, region stitching for pointer-chases, compressed-ESM handling, carve reassembly all
got clean bills), but the sweep found one empirically-proven scanner bug that blanks an entire
dump class, two carver truncation bugs, a statistically-certain boundary loss, ~15 record types
read-then-dropped with zero diagnostic, and an in-repo LZX decoder that recovery paths never call.

Empirical artifacts (coverage logs, gap-inventory CSVs, census CSVs) were saved to the session
scratchpad; regenerate with `dmp coverage` / `dmp gap-inventory` / `dmp formtype-census`.

---

## HIGH severity

### H1. Debug-dump gap scanner is parity-blind — file-offset vs VA alignment bug (EMPIRICALLY PROVEN)
`DmpGapRecoveryScanner.ScanRuntimeForms` (src/BethesdaMultitool/Core/Recovery/DmpGapRecoveryScanner.cs, ~line 325)
aligns its 4-byte-stride vtable probe to **file offsets**: `firstAligned = (4 - (gap.FileOffset & 3)) & 3`,
implicitly assuming file offset ≡ VA (mod 4). All three Debug dumps have Memory64List BaseRva ≡ 2 (mod 4)
(xex 0x21776, xex1 0x216A6, xex2 0x22916); every Release/MemDebug dump checked is ≡ 0. So on Debug the
scanner only ever probes VA ≡ 2 (mod 4) positions where real vtable pointers cannot sit.

Evidence chain (Debug xex2): one gap's first 4 KB has 301 heap-pointer words at VA-aligned parity vs 1 at
the probed parity; whole-heap census 1,110,377 game-module-pointer words at VA parity vs 430,998 straddle
noise at probed parity; `dmp rtti --census` resolves 394 classes / 178,055 instances / 236 TESForm-derived
classes in the same dump; `TesFormHeaderProbe` (which reads at VAs, not stride-scanned file offsets)
succeeds on all 53,373 Debug hash-table forms. Gap-inventory: 3 candidates on Debug (all byte-stride raw
TXST, immune to the bug) vs 18,063 on xex44. This RESOLVES the 08-02 audit's open anomaly
("gap scanner yields 0 on all Debug dumps — cause unverified"): it is explanation (a), a real recovery
gap — and Debug struct padding is NOT the cause. Fix: derive alignment from `gap.VirtualAddress`.
Debug dumps are the corpus's only script-text source, so this gap compounds.

### H2. Carver: PNG > ~64 KB never carved at all
`CarveExtractor.PrepareExtraction` hands `Parse` a 64 KB window for non-DDX formats
(Core/Carving/CarveExtractor.cs:30-34); `PngFormat.FindIendChunk` must find IEND inside that window or
Parse returns null (Core/Formats/Png/PngFormat.cs:44-48, 94-110 — its own 50 MB maxScan is unreachable).
Large PNGs are not truncated — they are silently dropped.

### H3. Carver: DDX > 512 KB structurally truncated to ~70%
DDX gets a 512 KB parse window (CarveExtractor.cs:30-31); `FindDdxBoundary`'s scan cannot see past it
(Core/Formats/Ddx/DdxFormat.cs:229-230), so any texture whose true end lies beyond 512 KB (1024² DXT1
≈ 700 KB, 2048² DXT5 ≈ 5.6 MB) always falls to the `headerSize + uncompressedSize*7/10` guess
(DdxFormat.cs:269) — guaranteed mip-chain loss. Caveat before acting: the 0.7 factor may be tuned to
packed-mip-tail reality; the 512 KB scan ceiling is not. Related: boundary-fallback truncation is never
flagged in the manifest (CarveExtractor.cs:100-116, CarveWriter.cs:55-66) so consumers can't tell.

### H4. Runtime struct scanners lose boundary-straddling structs in every dump
`RuntimeObjectScanner.ScanRegionGroup` iterates per region, and `ScanRegion` stops candidate tests
`minStructSize-1` bytes before each region end (Core/Formats/Esm/Runtime/RuntimeObjectScanner.cs:85-147,
scanLimit at :131) — even when the next region is VA-adjacent in the same contiguous group and
`ReadBytesAtVa` could stitch. Same pattern in RuntimeRefrHeapSweep.cs:85-106,197 and
RuntimeAnimationScanner.cs:80-121. Corpus regions average ~56 KB with ~3,100 VA-adjacent boundaries per
dump → expected loss ≈ structSize/56 KB per object class (~0.15% of meshes, ~0.4% of NiTriShape nodes;
tens of objects per dump, every dump). Fix: extend each region's candidate window into its VA-adjacent
successor, or scan per contiguous group.

## MEDIUM-HIGH

### M1. ~15 FormTypes read from dumps, then dropped with zero diagnostic
The runtime generic sweep decodes every FormType with a PDB layout and no specialized reader into
`RecordCollection.GenericRecords` (Parsing/RecordParserContext.cs:394-433), but the extractor/yield
filters pass only FLOR/MSTT/ANIO/TACT/ASPC/ADDN (Planner/Catalog/DmpRecordSource.cs:132-137;
Plugin/Pipeline/PluginConversionPipeline.cs:1939-2087). Read-then-vanished with no warning/counter:
**LSCR, EFSH, MSET, CAMS, IDLM, CHIP, CSNO, IPDS, RGDL, DOBJ, SKIL, CLOT, LVSP, AMEF, TLOD** (plus
scanner-side PMIS/PGRD with no consumer). The 08-02 "silent drop before the No-encoder warning"
mechanism is unchanged (PluginConversionPipeline.cs:207-229) — only the yield set grew.
`PlannerRoutingConsistencyTests` guards planned⊆extractor⊆yield but NOT read→yield-or-diagnostic.
Most of these remain decode-blocked by `RuntimeGenericReader.ReadEmbeddedStruct` still returning
"[Type, NB]" descriptor strings for structs > 8 B (Runtime/Readers/Generic/RuntimeGenericReader.cs:456-463).

## MEDIUM

### M2. In-repo LZX decoder never reachable from BSA/VFS/dump recovery
`src/DDXConv/DDXConv/Compression/LzxDecompressor.cs` (managed XMemCompress/LZX, round-trip tested) is
called only for DDX payloads. `BsaExtractor.ExtractFile` (Core/Formats/Bsa/Extraction/BsaExtractor.cs:452-519)
handles zlib + LZ4 only and never checks `BsaArchiveFlags.XMemCodec`; an XMem entry throws a generic
zlib error, and `ArchiveFileSystem.TryReadAllBytes` (Core/Vfs/ArchiveFileSystem.cs:93-99) swallows it to
null — on a single-source mount the file silently reads as "missing". Matches the known VFS "XMem/LZX"
gap; FNV 360 retail BSAs are zlib so scope is other-era/flagged archives.

### M3. BSA headers and zlib streams inside dumps: detected, labeled, never recovered
`RuntimeBufferScanner` detects `BSA\0` headers (Core/RuntimeBuffer/RuntimeBufferScanner.cs:294-307) and
zlib magics 78 01/9C/DA (:323-351) in dump buffers, but the only consumer is `dmp buffers` display.
No carve format for BSA (FormatRegistry.cs:275-294), no entry-table walk, no inflate-unclaimed-regions
pass anywhere. Untapped vector for proto builds whose BSAs no longer exist on disk.

### M4. FaceGen signatures absent from the carver, parsers already in-repo
FREGM002 (EgmParser.cs:15), FREGT003 (EgtParser.cs:16), FRTRI003 (TriParser.cs:18), FRCTL001
(GenFaceGenCommands.cs:49) — 8-byte magics, near-zero FP cost, plausibly resident (the repo already
extracts runtime FaceGen state via `render npc --dmp`). Cheapest genuine carver win found.

### M5. Silent structural caps in MinidumpParser
`numberOfRanges > 10000` → ParseMemory64List returns ZERO regions silently (Core/Minidump/MinidumpParser.cs:152-155);
dump stays IsValid, all VA-based analysis dies, and the file-offset fallback scan's 16-byte stepping is
wrong for non-16-aligned BaseRva (near-total runtime-object loss). Corpus max 4,236 ranges (xex21);
a 512 MB devkit dump extrapolates to ~8,800 — within 15% of the cap. Same pattern: >1000 modules → zero
modules (:107); >100 streams → IsValid=false (:47). Also: declared regions past EOF never clamped, and
`MinidumpFileScanner.cs:178` ignores ReadArray's return count (stale-buffer phantom matches on truncated
dumps); RuntimeObjectScanner fails closed but drops the whole 4 MB chunk including its valid prefix (:120-128).

### M6. Attribution residue (post the three interior fix rounds — which held)
- Last CELL in offset order has no next-cell cap → up to 500 KB of trailing REFRs claimed with no
  spatial check (Parsing/Handlers/CellRecordHandler.cs:333-373). A uniquely-attributed wrong-cell NEW
  ref is never spatially re-checked anywhere.
- XCLC grid attach by `FirstOrDefault(|offset diff| < 200)` both directions can steal the previous
  cell's grid coords → propagates into grid-keyed terrain attachment (CellRecordHandler.cs:599-600, 531-553).
- `RecordCatalog.cs:77-80` duplicate (type,FormId) captures: first-wins by enumeration order, loser
  silently discarded, no freshness comparison; same for multi-proto→one-master aliases (:121-124).
- `LandOverrideBuilder.cs:37-47, 78-86` bare catches silently abandon captured runtime terrain
  heightmaps/vertex colors (no stat, no log).
- `TryResolveOrphanByGrid` ambiguity fallback = "first match found", dictionary order
  (PersistentRefRedistributor.cs:714-736).
- `PersistentCellReparenting.cs:266-311`: allocator-less overload can strip a movable child from its
  source cell and add it nowhere (:308) — reachable only off the production path, but a genuine
  attached-to-neither path.
- Verdict drops without DropReason: CellChildVerdictPlanner.cs:262-267, 382-386.

### M7. RTTI census heap-window opt-out is dead
`RttiReader` filters to [0x40000000, 0x50000000) unless `includeAllRegions: true` (Core/Minidump/RttiReader.cs:111-131);
its own comment concedes SpeedTree pools live outside; both CLI callers use the default
(RttiCommand.cs:200,332; AnimationsCommand.cs:56) — `dmp rtti` census structurally never sees
out-of-window instances.

## LOW / tooling

- `EsmStringDetector` 4 MB chunks have zero overlap → boundary-straddling asset paths lost
  (Core/Formats/Esm/Records/EsmStringDetector.cs:29,47,92).
- PCM WAV found-then-rejected: RIFF/WAVE with fmt tag ∉ {0x165,0x166} discarded entirely (XmaParser.cs:11,44-47).
- XDBF capped ~64 KB by the same parse-window math despite 10 MB maxScan (XdbfFormat.cs:54-62); NIF/XUI
  fallbacks similarly window-capped; NIF 10 MB internal clamp vs 20 MB MaxSize (NifFormat.cs:235,295 vs :67).
- DDS size math assumes block compression (uncompressed A8R8G8B8 carved at 25%; cubemap/volume ignored)
  (DdsFormat.cs:124-142). Boundary scanners accept unvalidated 4-byte tokens as terminators (random
  collision in high-entropy payload truncates early); "LIPS" isn't even a real magic.
- tools/SignatureScanner (standalone): every match offset after the first 64 MB chunk shifted by
  +(maxPatternLen−1) (Program.cs:314-339, :531-550); first-boundary overlap carry uninitialized
  (:329-334); Aho-Corasick single-output trie drops duplicate patterns — .nif/.kf share a 16-byte
  header so one reports zero (AhoCorasick.cs:25). Main-app SignatureMatcher is correct.
- `EsmRecordCompression.cs:49-57` converter fallback: on decompress failure emits the unconverted
  big-endian zlib bytes still flagged compressed — no stat, no log (silent corruption if it ever fires).
- `SemdiffRecordParser.cs:52-69` skips zlib-failed compressed records from diffs with no counter.
- LAND FormType auto-calibration (<3 known-LAND matches → all runtime LAND skipped, Debug-log only)
  (Records/EditorIdLookupTables.cs:202-212).
- `dmp rtti <dump>` with no args crashes: unescaped Spectre markup `<va2>` in the usage string
  (CLI/Commands/Dmp/RttiCommand.cs:122). `dmp hexdump` with an out-of-range bare offset prints nothing.
- Same-offset different-signature matches: `DistinctBy(Offset)` drops one nondeterministically
  (MinidumpFileScanner.cs:99,150; MemoryCarver.cs:225) — no current magic pair collides.
- Analysis-path signature scan (not extraction) clamps at region ends → ~1-2 missed matches/dump,
  and `dmp analyze` results can disagree with what extraction actually carves (MinidumpFileScanner.cs:158-202).
- Latent: `ChunkOverlap = 256` vs max struct 240 — nothing asserts the margin (RuntimeObjectScanner.cs:22,131).

## Clean bills (verified correct)

- **Minidump structure**: corpus dumps carry streams {3,4,6,7,9,15}; payload == EOF exactly, descriptors
  VA-sorted, no overlaps/zero-size/slack — nothing recoverable hides in unparsed streams. Thread contexts
  + the Exception stream (crash PC, faulting address) are unused *analysis signal*, not lost data.
- Extraction-path signature scan: full-file 0→EOF, correct chunk overlap, byte-by-byte unaligned,
  nested carves not suppressed. `ReadBytesAtVa` stitching, `IsVaRangeCaptured`, VA→offset (no off-by-one,
  fail-closed). Carve reassembly across regions with zero-fill + coverage tracking.
- Compressed ESM records: both dump scanners are compression-aware (flag-exempt first-subrecord probe,
  RFC-1950 extent proof); parse does strict zlib → raw-deflate → dump-only partial salvage with tracked
  FormIDs; TOFT, LAND, INFO merge, converter recompression all decompress correctly.
- DDX LZX decode end-to-end, with raw-file fallback so no carved bytes are discarded. Saves/STFS
  correctly have nothing to decompress. LIP/ESM/XEX carver omissions deliberate and justified.
- Fixed since 08-02 and verified: PWAT/TREE end-to-end; GRAS/IMGS/CLMT/FLOR/MSTT/ANIO/TACT/ASPC/ADDN
  + ~12 more types reachable; runtime LAND live via calibration; SCTX superseded by runtime
  `Script::m_text` pointer chase (RuntimeScriptReader.cs:239-345); orphan-bucket deaths named;
  interior accretion hardening (both-side agreement, 2× ratio, 2 KB step cap, frozen boxes) intact;
  proximity window capped at next CELL (except the last-cell tail, M6); 360-endianness handled in every
  registered parser; drop-stats good on the named paths (VerdictPlacedRefEncoder, DialogGrupBuilder, census).

## Empirical baseline (2026-08-25 binaries)

| Dump | Recognized | Uncovered | Gap makeup (top) |
|---|---|---|---|
| xex44 | 58.2 MB = 25.2% | 172.8 MB = 74.8% (44,813 gaps, all ≤64 KB) | StringPool 36.1% · BinaryData 24.3% · PointerDense 23.2% |
| xex21 | 70.0 MB = 28.3% | 177.2 MB = 71.7% | PointerDense 32.8% · StringPool 26.9% · BinaryData 26.3% |
| Debug xex2 | 65.2 MB = 37.7% | 107.6 MB = 62.3% | BinaryData 27.1% · StringPool 27.0% · PointerDense 25.2% |

Gap inventory: xex44 → 18,063 candidates (17,794 dialogue); Debug xex2 → 3 (see H1). FormType census
xex44: 92 types, all mapped, no drift, no unknown bytes. BinaryData spot-check (xex44, VA 0x40190000):
sorted 16-byte hash→size→offset table — in-memory asset/archive index, not carvable payload; much of the
42 MB BinaryData class is likely similar.

## Ranked by expected recovered data

1. H1 gap-scanner VA-parity fix (unlocks the entire Debug gap class; compounds with script text).
2. H3 DDX 512 KB ceiling (+ verify the 0.7 factor) and H2 PNG 64 KB drop.
3. H4 region-boundary struct windows (small % but every dump, every scanner).
4. M1 read-then-dropped FormTypes (needs the ReadEmbeddedStruct raw-bytes widening + yield rows;
   LSCR/EFSH/MSET/CAMS/IDLM/CHIP/CSNO are real proto content).
5. M4 FaceGen carver signatures (cheapest).
6. M3 zlib/BSA-in-dump recovery pass (new capability, unknown but nonzero yield).
7. M2 LZX→BSA wiring (only matters for XMem-flagged archives).

Per the present-data-not-solutions ruling: nothing was changed; every item above is a finding, not a fix.
