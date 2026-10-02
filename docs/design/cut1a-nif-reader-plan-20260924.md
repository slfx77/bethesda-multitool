<!-- Implementation plan for design rows 10-11 (cut 1a). Drafted 2026-09-24 by a read-only planning pass over the
worktree at Shared ecc8928; the design (docs/design/model-document-design-20260923.md) stays authoritative. -->

## Decisions taken on this plan's open points (2026-09-24)

- **Pin:** BMT follows the tip of Shared main (owner policy, 2026-09-24); `1877ea0` at the time of writing.
- **DDX relayout during inspection is allowed.** LZX decompression plus untiling re-containerizes the compressed blocks
  losslessly; no pixel is decoded, which is what `ModelReadPurpose.Inspection` forbids.
- **Repeated-index triangles are dropped and counted in triangle lists as well as strips.** They cover no area, the
  Blender writer now reports any face repeating a vertex as Dropped (failing gate 1a), and the count stays in native
  state. How many real lists carry them is measured before gate 1a (plan section 7).
- **Billboards keep Front, Up and Roll null** until an RE item establishes the NIF convention; the Blender writer then
  reports them Degraded, which is honest for 1a.
- **Bind tolerance:** measured 2026-09-24 (TestOutput/bind-orthonormality-20260924): 99 of 90,509 bones exactly
  orthonormal, the rest within 5.96e-7, none beyond 1e-5; the foundation was asked to restore a 1e-5 tolerance with an
  Approximated row (mailbox). The plan's own bind census in section 7 is therefore already answered.
- **GLB gaps G1-G6** are the foundation's (their writer); relayed through the mailbox. G8 is the bind objection above.
  The foundation accepted it and is building an Approximated projection path (Shared `d29887c`); it asks the reader
  to keep the original inverse binds untouched.

### Measured 2026-09-24 over the cut-1a censuses (TestOutput/nif-reader-measurements-20260924)

59,341 distinct files at 20.2.0.7 / BS 14, 21, 26, 32, 34, both byte orders; every NiTriShapeData / NiTriStripsData
re-walk ended exactly at its block size.
- **Repeated-index list triangles are a landscape-LOD artifact.** FNV loose: 7,163,270 of 21,923,045 list triangles in
  3,580 files, of which 3,567 are `landscape/lod/**` (about half of each LOD list, e.g. 2,066 of 3,690); the other 13
  files (10 armor, 2 creatures, 1 DLC) carry a handful each, e.g. `(804, 805, 804)` in `armor/combatarmor/m/go.nif`.
  FO3 3,496,479 in 1,739 files, X360 4,662,812 in 2,282, PS3 none. Strips: 26,381,429 of 47,288,338 FNV strip
  triangles are stitching degenerates. Dropping and counting both keeps every writer clean and removes no area.
- **Vertex colors outside [0, 1]** (FNV loose, bs34 LE): 279 files with overbright components up to 16 (e.g. 1.05,
  2.0), 88 with small negatives, and 15 files (29 blocks) whose color arrays hold NaN or values near 1e30 although
  the block layout tiles exactly (e.g. `architecture/generic/nvbungalow01.nif`), i.e. garbage shipped in the file.
  GLB gap G4 therefore affects about 380 FNV files (1.5%). Default until the owner rules otherwise: finite values
  are kept raw; a block with a non-finite color keeps its exact bytes in a non-primary attribute plus native state,
  its portable vertex colors are neutral (1,1,1,1) because `SceneValidation` requires finite colors, and a
  diagnostic names the block.
- **NiSkinData overall transform is not the identity** on 3,274 of 5,265 FNV skins (828 files), typically a pure
  translation such as `(0, -0.80, -0.31)` on armor; FO3 1,299 of 1,855; X360 2,806 of 4,239; PS3 545 of 812. The
  reader must carry it (BMT's renderer ignores it today).
- **Has Vertex Weights = 0**: X360 4,231 of 4,239 skins and PS3 789 of 812, none on PC, so console weights come from
  the partitions (slice 10).

### Measured 2026-09-24: the console packed-stream layouts (supersedes the W-component and sentinel notes in section 7)

Census (`TestOutput/packed-layout-census-20260924/`, the probe over the retail X360 Final and PS3 Final Meshes BSAs):
12,307 of 18,840 X360 files and 12,068 of 18,735 PS3 files carry `BSPackedAdditionalGeometryData`, in exactly the
same SIX layouts on both platforms, keyed by the ordered (stream type, unit size) list and the stride. Semantics
(`TestOutput/packed-semantics-20260924/README.md`, `results.json`): 191 X360 files (1,088 shapes, 473,323 vertices)
and 35 PS3 files paired with the PC Steam Final file of the same path, shapes by ordinal, every stream scored against
every PC array (runner-up = the best score against any other array). All values big-endian.

| Layout | stride | offsets and encodings | X360 / PS3 blocks |
|---|---|---|---|
| L1 static + color | 40 | 0 position half4, 8 normal half4, 16 color D3DCOLOR, 20 uv half2, 24 bitangent half4, 32 tangent half4 | 33,682 / 33,474 |
| L2 static | 36 | 0 position, 8 normal, 16 uv, 20 bitangent, 28 tangent | 8,501 / 8,389 |
| L3 skinned | 48 | 0 position, 8 bone weights half4 (partition slots 0..3), 16 bone indices ubyte4, 20 normal, 28 uv, 32 bitangent, 40 tangent | 3,777 / 3,657 |
| L4 skinned + color | 52 | 0 position, 8 weights, 16 indices, 20 normal, 28 color, 32 uv, 36 bitangent, 44 tangent | 467 / 465 |
| L5 interface | 48 | 0 position half4, 8 normal float3, 20 uv half2, 24 bitangent float3, 36 tangent float3 | 27 / 27 |
| L6 interface + color | 52 | 0 position, 8 normal float3, 20 color, 24 uv, 28 bitangent float3, 40 tangent float3 | 22 / 22 |

- Positions, normals, UVs, bitangents and tangents: 1.00000 of vertices within one half ulp of the PC value on every
  layout and platform (positions are the exact half rounding of the PC float on 99.997-100%); runner-ups <= 0.0027;
  little-endian readings <= 0.15% with infinite errors; the swapped bitangent/tangent assignment scores <= 0.002.
- half4's fourth half is a constant 1.0 on 100% of vertices: NOT the bitangent sign (PC handedness is -1 on 27,474 of
  79,486 L1 vertices while it stays 1.0). W components carry nothing.
- Vertex color = bytes / 255. **X360 memory order A,R,G,B; PS3 A,G,B,R** (the only platform difference; both share the
  header key `20.2.0.7/uv11/bs34/BE`, so there is no in-file discriminator). On rows with R != B: X360 ARGB 12,982/12,982
  (L1) and 22,062/22,062 (L4), every other order <= 0.0032; PS3 AGBR 1,700/1,700 and 958/958, ARGB 0.0094.
- Bone indices: slot k = byte[3 - k] of the ubyte4, indexing the owning partition's bone list: 100% of 325,532 X360 and
  34,673 PS3 vertex slots; memory order scores 16.7-32.1%.
- Weights: four halves = partition slots 0..3, every used PC slot within one half ulp (514,846 of 514,846 X360;
  max 2.44e-4). **Sentinel:** a slot-3 value of exactly 1.0 whose three siblings sum to 1 (within 2e-3) reads as 0
  (901 + 110 X360 and 59 + 11 PS3 vertices; PC never uses slot 3 there). Reading it as 0 reproduces PC slot 3 within
  2^-11 on 325,532 / 325,532 X360 and 34,673 / 34,673 PS3; a genuine 4-slot vertex (6,188 + 485) keeps its fourth
  weight (equal to PC within 1e-3). Never renormalize. Slots are stored slot-for-slot: two retail vertices use slot 3
  with slots 1-2 empty. Best explanation of the 1.0 (not proven): a negative float32 residual `1 - (w0+w1+w2)` rounded
  to half; the small positive fourth halves equal that residual on 99.63%.
- Vertex order: static = identity (the console index buffers are byte-identical to PC: 340 + 3 / 143 + 1 / 27 / 22
  shapes); skinned = the concatenated NiSkinPartition vertex maps (console partitions equal PC's in count, vertex maps,
  bone lists and faces on 400/400 L3 and 152/152 L4 X360, 49/49 and 19/19 PS3; packed count == the partition sum).
- Format loss: positions above 8,192 have a half ulp of 8 (L2 max error 6.8), unrecoverable. 0.02-1.3% of halves are
  not the rounding of the PC float because PC snaps near-zero values; all within 2^-12.
- Open: the sentinel's cause; L6's color order is inferred from L1/L4 (every sampled L6 vertex is white); PS3 alpha
  (three distinct values in the sample); second UV sets (no layout has one); DLC-only files (none in the sample).

---

# Cut-1a NIF model reader and `mesh` command: implementation plan

I read everything and changed nothing. I did not build, run tests or launch Blender.
- **Worktree:** `C:/dev/Multitool-worktrees/bmt-material-preparation-20260912`, BMT HEAD 30bb432c. Its Shared submodule is checked out at ecc8928.
- **AWE reference:** `C:/dev/Multitool/AweMultitool`. `diff -rq` finds its Shared `Core/Models` byte-identical to ecc8928, so AWE's consumers use exactly the contracts below.
- **Path prefixes:** `SH/` = `shared/Multitool.Shared/src/`, `BMT/` = `src/BethesdaMultitool/`, `T/` = `tests/BethesdaMultitool.Tests/`, `AWE/` = `C:/dev/Multitool/AweMultitool/src/AweMultitool/`.

---

## 0. Baseline facts that shape the plan

### Worktree state
- **Pin bump not committed.** Shared is checked out at ecc8928, but BMT's committed gitlink is older (`git status`: "shared/Multitool.Shared (new commits)"). The SceneDocument→ModelDocument rename (design row 9) sits uncommitted in about 28 files (`git diff --stat`).
- **DDXConv counters not committed.** `src/DDXConv` is dirty, and row 10's three counters already exist uncommitted:
  - `DecodeDiagnostics.cs:47-68`
  - `DdxParser.cs:136` (`RecordDroppedTrailing`)
  - `DdxChunkProcessor.cs:986`
  - `DdxMipAtlasUnpacker.cs:466`

  They need a commit in the owner's fork (MIT, Kran's notice) and a gitlink bump.
- **Missing project reference.** BMT references Shared Core and Media only (`BethesdaMultitool.csproj:101-104`), not Media.Blender. Media.Blender ships `blender/import_model.py` as content (`SH/Slfx77.Multitool.Media.Blender/Slfx77.Multitool.Media.Blender.csproj:19-20`).
- **`mesh` is not a CLI root name.** `BMT/Program.cs:22-54` must list `mesh`, or the Windows build routes the verb to the GUI (`Program.cs:350-362`). Commands are registered at `Program.cs:175-213`.
- **Nothing exists yet** under `Core/Modeling` or `CLI/Commands/Mesh`.

### ecc8928 contract facts (these bind the reader; several differ from the design)

**Reading contract**
1. **Synchronous read, async resolver.** `Read` is synchronous (`SH/Slfx77.Multitool.Core/Models/Sources/IModelSourceReader.cs:33`) but `ModelCompanionResolver` is async. AWE blocks on it with `.AsTask().GetAwaiter().GetResult()` (`AWE/Core/Formats/Mesh/Sources/AseModelTextures.cs:30`).
2. **The default resolver takes basenames only.** It throws on `textures/…` paths (`SH/Slfx77.Multitool.Core/Assets/CompanionResolver.cs:18-19`, used by `ModelReadContext.cs:33,52`). BMT must supply its own resolver.
3. **Inspection is opt-in.** `SupportsInspectionWithoutPixelDecoding` defaults to false (`IModelSourceReader.cs:15`); `ModelInfoOperation.cs:69` returns Unsupported without it.
4. **The lifetime checks every read.** It verifies coverage identity and then runs `ValidateStructure` (`ModelReadLifetime.cs:67-70`).
5. **No operation returns the document.** `ModelInfoResult` keeps only a `ModelInfoDocument`; `ModelConvertResult` is compact; `ModelReadLifetime` is internal. So `dump`, `fidelity`, `validate` and `package` need a BMT-side read path. (Superseded at 6c94992 by `ModelDumpOperation` / `ModelPackageOperation`, which own the read lifetime; see section 5.)
6. **Structural rules the reader must meet:**
   - one parent per node (`SceneValidation.cs:118`)
   - non-empty triangle primitives (`SceneValidation.cs:260`)
   - no repeated joint (`SceneSkinValidation.cs:104`)
   - joints share a root (`SceneSkinValidation.cs:113`)
   - unit quaternions within 1e-4 (`SceneValidation.cs:112-113`)

**GLB writer**

7. **Any limitation makes the whole item NotSupported** (`SH/Slfx77.Multitool.Media/Models/ModelGlbWriter.cs:80-83`). Limitations include:
   - an image with no PNG or DDS original and no StandardPayload, including an original DDX that carries a recovery Derivation (`ModelGltfImages.cs:128-158`)
   - a local matrix that cannot be decomposed to TRS (`ModelGltfAdmission.cs:99-106`)
   - a primary color stream whose space is Unknown, or with any component outside [0,1] (`ModelGltfVertexColors.cs:52-61`)
8. **GLB reports Dropped for every source attribute stream,** including the primary color stream, and for `Faces`, `PointIndices` and a non-Render `Purpose` (`ModelGltfGeometry.cs:351-367`).
9. **GLB never merges the Xbox specular companion.** `ModelGltfMaterials.cs:88` calls `ModelGltfNormalMaps.Plan` without its `specular` argument.

**Blender writer**

10. **Admission rules:**
    - faces that repeat a vertex are Dropped (`SH/Slfx77.Multitool.Media.Blender/BlendAdmission.cs:522-531`)
    - a billboard is exact only with Front and Up declared plus aim-specific rules (`BlendAdmission.cs:797-817`)
    - a bind is rigid only when its Gram matrix is exactly the identity (`BlendBindAnalysis.cs:16, 92-104`)
    - an original DDX with a recovery Derivation is packed and classed Degraded (`BlendAdmission.cs:244-249`)

**Formatting**

11. **`mesh info` format line.** It prints `document.SourceFormat | FormatEvidence` (`ModelInfoTextFormatter.cs:27`). The probe evidence text should therefore read `NIF 20.2.0.7, user 11, BS 34, little-endian`.

---

## 1. Decoder strategy

### Recommendation: a hybrid, schema-first decoder
- **One component reads bytes:** a new, non-mutating, schema-driven value decoder.
- **Thin typed views on top:** hand-written, but they interpret decoded values (bitfields, enums, semantics) and never compute offsets.
- **A small explicit quirk table** for Xbox conventions and known nif.xml errata.
- **What is reused:** definitions (`NifSchema`), expression evaluators (behind strict wrappers), `NifParser`'s header and block table, and a few pure helpers.
- **What is not reused:** every existing hand reader and the byte-swapping walker.

**Why:**
- **The hand readers each re-derive offsets and swallow failures,** and at least one is wrong for an in-scope key. `NifRenderPropertyReader.cs:226` reads Emit Mult only when `BsVersion > 26`, but nif.xml gates it on `#BSVER# #GT# 21` (`BMT/Core/Formats/Nif/nif.xml:10904-10905`). The probe also uses `bs > 21` (`tools/scripts/nif_feature_probe.py:1157`). Every bs26 material would silently read Emit Mult as absent: 4 unread bytes inside the block, and no error.
- **The existing schema walker is tied to in-place swapping.** `NifValueConverter` reads stored values back as little-endian after swapping (`NifValueConverter.cs:521-529`). It is also permissive by design:
  - stops at the block end (`:44-47`)
  - skips arrays longer than the remaining bytes (`:306-312`)
  - skips arrays whose length cannot be evaluated (`:285-289`)
  - caps recursion depth (`:36-40`)

  That is correct for conversion but becomes silent truncation in a reader.
- **Schema-driven field presence tracks nif.xml** across BS 14, 21, 26, 32 and 34 and both endiannesses, which is exactly where hand readers drift. The Python probe was hand-transcribed from nif.xml and shares no code (`nif_feature_probe.py:12-17`), so it stays an independent oracle.
- **Typed views are needed regardless:** `NifSchema` stores only the storage type of enums and bitfields, not their members or options (`NifSchema.cs:104-121`).

### Decoder design (new `BMT/Core/Formats/Nif/Decoding/`)

**Input and output**
- **Input:** the whole file as `ReadOnlyMemory<byte>`, the `NifInfo` from `NifParser.Parse` (`NifParser.cs:20`), a `NifVersionContext`, and the endian flag.
- **Output:** `NifDecodedBlock(Index, Type, Offset, Size, NifStruct Root, IReadOnlyList<NifFieldSpan> Spans)`, an immutable value tree:
  - scalars and floats keep their exact bits
  - string index plus the raw bytes it resolves to
  - SizedString as raw bytes
  - Ref/Ptr as an int
  - structs keep ordered fields; duplicate names are disambiguated by ordinal (nif.xml repeats names, e.g. "Num Vertices" at `nif.xml:9943-9950`)
  - arrays; fixed-size basic element types (Vector3, Color4, TexCoord, Triangle, ushort, float) become bulk typed arrays

**Field filtering** follows the walker's order, with these sources:
- onlyT and excludeT through `NifSchema.Inherits` (`NifSchema.cs:279-285`)
- since and until through `NifSchemaConverter.ParseVersionString` (`NifSchemaConverter.cs:115-133`)
- vercond through `NifVersionExpr`
- cond through `NifConditionExpr`

**Scoping replaces the flattened dictionary.** Each struct instance gets its own scope, chained to its parent, plus `#ARG#` and the `#T#` template.
- A name declared in the current definition but excluded by version evaluates to 0. nif.xml relies on this: `(Data Flags #BITOR# BS Data Flags)` at `nif.xml:9981-9990`, where one side is always absent.
- A name that is undeclared anywhere is an error.

**Strict expressions (additive API).** Today both evaluators fail open:
- `NifConditionExpr` compiles a parse error to "always true" (`NifConditionExpr.cs:56-60`)
- `FieldNode` returns 0 for an unknown name (`Conditions/FieldNode.cs:24-25`)
- `NifVersionExpr` also falls back to true (`NifVersionExpr.cs:132, 144-147`), and its macros are hard-coded (`:31-64`)

Add `TryCompileStrict` to both, plus a `GatherFields` check. The converter keeps the current behavior.

**Sizes** come from `NifSchema.GetTypeSize` (`NifSchema.cs:228-249`) and `NifScalarConverter.EffectiveTypeSize` (`NifScalarConverter.cs:43-46`). `bool` is forced to 1 byte (`NifSchema.cs:90-93`), which is correct at 20.2.0.7.

**Endianness**
- These header fields stay little-endian even in big-endian files:
  - the version, user version and block count (`NifParser.cs:453-498`)
  - the BS version (`NifParser.cs:515`)

  The probe docstring agrees (lines 24-26), as does `T/Helpers/BigEndianNifBuilder.cs:17-22`.
- Everything from Num Block Types on is big-endian when the endian byte is 0: the block-type table (`NifParser.cs:81`), the string table and every block body.
- The decoder reads each scalar in file order and never mutates bytes. Floats are taken from their bits.

**Quirk table.** Each entry is explicit, cited and pinned by a synthetic test:
- **`BSPartFlag` is little-endian inside big-endian files.** Source: `NifScalarConverter.cs:26-36`; the builder reproduces it at `BigEndianNifBuilder.cs:36-40`.
- **Fixed-size structs of single-byte fields** (ByteColor4 and similar) are stored as one big-endian unit. Read the unit, then split it (`NifScalarConverter.cs:160-200`).
- **`NiAGDDataBlock.Data` is Block Size bytes long,** not Num Data × Block Size as nif.xml says (`nif.xml:16457-16466`). The probe measured this on retail files (`nif_feature_probe.py:1036-1041`). With arg 1, Shader Index and Total Size follow.
- **HavokFilter's byte order depends on where it sits** (`NifValueConverter.cs:602-623`). Typed data never needs it; bhk blocks are decoded tolerantly for native payloads only.
- **Packed console vertex payloads** (half floats, ubyte4, D3DCOLOR) are decoded by `NifPackedGeometryDecoder`, not the schema walker (see §3).

**Strings**
- At 20.2.0.7 (≥ 20.1.0.3), `string` is an int32 index into the header strings, with -1 meaning none.
- SizedString is inline, with its length in file byte order. These carry texture names in `BSShaderTextureSet` and the NoLighting, Sky, Tile and TallGrass "File Name" fields.
- `NifParser` decodes the header string table with `Encoding.ASCII` (`NifParser.cs:685`), which turns every byte ≥ 0x80 into `?`. That is lossy. The decoder re-reads the table as raw bytes: Latin-1 for display, with the raw bytes kept in native state whenever a byte ≥ 0x80 occurs.

**Footer.** `NifParser` never reads the footer (`NifParser.cs:104-111`). Add `NifFooterReader`: Num Roots, then the root refs, read immediately after the last block.

**Typed views** are records per block type that read named fields out of the tree. Examples:
- `NifAlphaPropertyView`, with bitfield accessors per `nif.xml:5449-5470`
- `NifStencilView`, per `nif.xml:5482-5497`

  That entry's Test Func mask (0xF000) contradicts its declared width of 3, so decode by width and pin it in a test.

### Reuse audit

| Component | Verdict | Audit findings (bakes and lossy behavior) |
|---|---|---|
| `NifParser.Parse` (`Parser/NifParser.cs:20`) | **Reuse** (header, block-type table, sizes, strings) | No bakes. It does not check block extents against file length (`BuildBlockList` at `:619-636`), does not read the footer, and its ASCII string decoding is lossy (`:685`). The reader adds those checks. |
| `NifParser.ProbeVersionInfo` (`:329`) | **Reuse** in `Probe` | Header only. The probe also needs the block-type table and type indices, which the reader parses inside the 64 KiB prefix. |
| `NifSchema` (`Schema/NifSchema.cs`) | **Reuse** definitions | Bitfield members and enum options are not parsed (`:104-121`); `bool` is forced to 1 byte (`:90-93`). |
| `NifConditionExpr` / `NifVersionExpr` | **Reuse** behind a strict compile | Fail-open as described above. |
| `NifSchemaConverter.ParseVersionString`, `NifScalarConverter.EffectiveTypeSize` | **Reuse** (pure) | None. |
| `NifValueConverter`, `NifSchemaConverter.TryConvert` / `MeasureBlock` | **Do not reuse** | Mutates bytes, reads values back after swapping, and truncates silently as listed above. It also captures strip lengths only for fields named `* Lengths` stored as ushort (`:372-375`). |
| `NifBinaryCursor` (`Parser/NifBinaryCursor.cs`) | **Do not reuse** | Caps the extra-data count at 100 (`:82`, `:123`); `ReadSizedString` returns null for empty, whitespace or > 512-byte strings (`:147-156`). |
| `NifRenderPropertyReader` | **Do not reuse** | Emissive pre-multiplied by Emit Mult (`:258-260`). Emit Mult gated on BS > 26 (`:226`), which is wrong. Silent defaults on truncation (`:60-71`, `:148`). Drops No Sorter, Clone Unique and editor bits (`:108-114`). Double-sided means draw mode 3 only (`:44-46`). Animated emissive takes the first key or a diffuse target (`:316-417`). |
| `NifObjectBlockReader` | **Do not reuse** | Unknown billboard modes (8) become RotateAboutUp (`:27-34`). Transform multiplies scale into the rotation (`:130-146`) and returns Identity on failure (`:99, 109, 116, 122`). Hidden is false on failure. |
| `NifSceneGraphBlockReader` | **Do not reuse** | Children capped at 500; null refs dropped, losing ordinals (`:270-285`). The switch ordinal is compared against the filtered list (`:362-421`). |
| `NifGeometryDataReader` (`Rendering/Geometry`) | **Do not reuse** | Clamps and truncates colors to bytes (`:50-53`). Positions and UVs are raw but trivial to re-read. |
| `NifSubmeshExtractor` | **Oracle only** (hop A3) | Bakes world transform and skinning (`:514`); truncated colors (`:392`); UV set 0 only (`:401`); recomputes missing normals (`:534`); silently skips short tangents (`:349`); treats zero vertices as absent (`:283`). |
| `NifTriStripExtractor.ConvertStripsToTriangles` (`GeometryAnalysis/NifTriStripExtractor.cs:60-92`) | **Rule reference only** | This, not the Morrowind path the design cites (`NifSubmeshExtractor.cs:751-812`), is the 20.2.0.7 rule: repeated-index triangles are skipped at `:75`, parity is flipped at `:81-89`. Re-implement it independently so A3 is not comparing a function with itself. |
| `NifPackedDataExtractor` | **Do not reuse** | Stride-based skinned detection (`:236-244`). Stream semantics guessed from average vector length (`FindUnitLengthStreams` at `:320`, assignment at `:357-404`). Invented bitangents (`ComputeMissingBitangents` at `:409`). Its frame-stream naming was also swapped against retail PC until 2026-09-28, so the converter wrote the lower-offset (+dP/du) stream as the first array: fixed there (lower = "Bitangents", higher = "Tangents", a sample-rejected partner read from its channel, the invented partner now `ComputeMissingFrameArray`), measured over every X360 packed shape against its PC twin (TestOutput/nif-tangent-frame-20260928/converter). Weight "sentinel" zeroing and renormalization (`NormalizeDecodedBoneWeights` at `:541`). Drops the W component. Truncates silently. |
| `Rendering/Skinning/*`, `Skinning/*` | **Do not reuse** (reference only) | `NifSkinBlockParser` multiplies scale into the rotation (`ParseNiTransform` at `:15-47`) and caps bones at 500 (`:84`, `:132`). `NifShapeSkinningDataBuilder` ignores the NiSkinData overall transform (`:146-147`). `NifSubmeshSkinExporter` keeps the top 4 weights and renormalizes (`:92`). The partition expander documents that packed vertices are stored in partition order (`NifSkinPartitionExpander.cs:489-490`). |
| `NifTextureResolver`, `NifTextureLoader`, `INifTextureSource` | **Do not reuse** | Decodes pixels into a global cache. `ConvertDdxIfNeeded` swallows exceptions and returns DDX bytes (`Textures/NifTextureLoader.cs:58-66`). Merges BC5 and BC4 (`:104-119`). `NifTextureArchiveSource.TryLoadRaw` swallows errors (`:43-60`). |
| `NormalMapMerge.IsNormalMapPath` / `ComputeSpecularPath` (`BMT/Core/Formats/Ddx/NormalMapMerge.cs:25-47`), `NifTexturePathUtility.Normalize` (`Textures/NifTexturePathUtility.cs:56-118`) | **Reuse** (pure) | Normalization is used only as the lookup key; the authored string stays in the document. |
| `GameFileSystem` / `IGameFileSystem` (`BMT/Core/Vfs`) | **Reuse** for texture lookup | Loose files shadow archives (`GameFileSystem.cs:35-54`). `TryReadAllBytes` falls through to the next layer when an entry is unreadable (`IGameFileSystem.cs:57-63`), so record the provenance from `TryReadAllBytesBounded`'s `GameFileReadResult.Entry` (`:65-71`). |
| DDXConv `DdxParser.ConvertDdxToDds` (`src/DDXConv/DDXConv/DdxParser.cs:42`) and `DecodeDiagnostics` | **Reuse** for the gate | `IsLossless` covers only the permutation invariant (`DecodeDiagnostics.cs:70-77`). The three extra counters must be read separately. The header parser is internal (`DdxHeaderWriter.cs:6,14`); add a public `DdxParser.ReadHeader` in the fork. |
| `DdsImageDecoder.Inspect`, `Bc1BlockInspector` (`SH/Slfx77.Multitool.Media/Images`) | **Reuse** for DDS descriptors | Inspect runs the BC1 transparent-selector scan without decoding. It rejects cubes (`DdsImageDecoder.cs:39`); the reader needs its own minimal DDS header path for cubes. |
| `GameProfiles.For(game).Units` (`BMT/Core/Games/GameProfiles.cs:682`; FNV `:92-105`, FO3 `:107-117`) | **Reuse** | None. |
| `NifNeutralSceneAdapter` (`Rendering/Export`) | **Learn from, do not reuse** | Stores each image once, keyed by content (`:460-479`). Its source is `RenderableSubmesh`. |
| `NifGeometryExtractor.Extract(..., bindPoseOnly: true)` (`Rendering/NifGeometryExtractor.cs:153-160, 369-385`) | **A3 oracle** | Returns shape-local vertices with no transforms or skinning, so no new internal option is needed. It drops hidden shapes (`:214-219`) and NiVisController-hidden subtrees (`:202`). |

### Self-checks, and what they can and cannot discriminate

**Checks**
1. **Consumed bytes equal the header's Block Size.**
   - Typed block type → `InvalidDataException` naming block, field and offset.
   - NativeOnly type → diagnostic, raw bytes kept, still classified NativeOnly.
2. **Layout:** the first block starts at the end of the header, and the sum of block sizes plus the footer equals the file length.
3. **Footer:** it ends exactly at EOF and every root is in range.
4. **Refs and strings:**
   - every Ref/Ptr is in [-1, N)
   - its target matches the field's template (e.g. NiAVObject.Properties must point at a NiProperty subclass), checked with `NifSchema.Inherits`
   - string indices are in range
5. **Bounded allocation:** count × element size ≤ remaining bytes before anything is allocated.
6. **Cross-block consistency:**
   - NiTriShape points at NiTriShapeData, NiTriStrips at NiTriStripsData
   - triangle and strip indices < Num Vertices
   - NiSkinData vertex indices < Num Vertices
   - NiSkinInstance bone count equals NiSkinData bone count
   - partition vertex-map values < Num Vertices
   - NiMorphData Num Vertices equals the geometry's
7. **Graph:** acyclic, and every root is an NiAVObject.
8. **Schema lint** (a test): every cond, vercond, length, width and arg reachable from the in-scope types compiles strictly and names only declared fields; every `#MACRO#` used is known to `NifVersionExpr` (`:31-64`).
9. **Non-finite values** are reported with their block and offset before `ValidateStructure` rejects them.

**Can discriminate:** desynchronization in any single block (an extra or missing field, a wrong version gate, a wrong array length, a wrong type width), because 20.2.0.7 headers carry a size for every block. Also truncated files and broken refs.

**Cannot discriminate:**
- **Mis-valued fields of the same width:** byte order of one field (BSPartFlag), enum or bitfield meaning, Tangents vs Bitangents order, two u8 read as one u16, float vs int.
- **Errors in the header's own sizes,** which are the reference.
- **Wrong semantics in a correct layout,** such as clamp axes or transform methods.

CLAUDE.md's rule applies: exact tiling is a consistency check, not a falsifier. Report "desynchronized" and "silently mis-valued" as separate numbers. A1 covers the probe-tagged fields, A3 covers geometry, and synthetic tests pin every quirk with a value. Add a **negative control** that asserts the size check does *not* catch a byte-swapped BSPartFlag, so nobody mistakes it for a falsifier.

---

## 2. File blueprint

Namespaces: `BethesdaMultitool.Core.Modeling` and `…Modeling.Nif` (design §2.3). No local ModelDocument type exists in BMT.

### Decoder (new; not in row 10's list — add it to the row-10 claim; BMT-owned)
`BMT/Core/Formats/Nif/Decoding/`:
- `NifBlockDecoder.cs` — `internal sealed class NifBlockDecoder(NifSchema schema, NifInfo info, ReadOnlyMemory<byte> file)`, with `NifDecodedBlock Decode(int block, NifDecodeMode mode)`. Modes: Strict for typed types, Tolerant for NativeOnly types.
- `NifValue.cs`, `NifDecodedBlock.cs`, `NifFieldSpan.cs` — the value tree.
- `NifDecodeQuirks.cs` — the quirk table.
- `NifStrictExpressions.cs` — the strict compile cache, or additive methods on the existing expression classes.
- `NifFooterReader.cs`.

### Row 10: `BMT/Core/Modeling/Nif/`
- **`NifModelReader.cs`** — `public sealed class NifModelReader : IModelSourceReader`.
  - `FormatId => "bmt.nif"`; `SupportsInspectionWithoutPixelDecoding => true`
  - `Probe(ModelSourceCandidate)` and `Read(ModelSourceItem, ModelReadContext, CancellationToken)`
  - `MaximumSourceBytes` of about 256 MiB
  - `Read` steps:
    1. Check that the item and context name the same occurrence (AWE pattern: `AseModelSourceReader.cs:48-52`) and require a `NifModelReadCache`.
    2. Read the bytes with a bound and hash them with SHA-256.
    3. Parse with `NifParser`, check the key is in scope, decode the blocks.
    4. Run the node, geometry, skin, material/texture and layer readers.
    5. Add units and basis, native state, then coverage.
    6. Build `new ModelDocument("bmt.nif", …) { SourceProvenance = new SceneSourceProvenance(item.Reference.Path, sha) }`.

    It shares an internal mutable builder, `NifModelReadState`, with the sub-readers.
- **`NifModelNodeReader.cs`** (addition) — walks from the footer roots:
  - one node occurrence per parent edge; cycles throw
  - applies the TRS rule, names (including palette names), and the Joint role for bones
  - builds `SceneBillboard`s and the inherited-property context
- **`NifModelTransform.cs`** (addition, pure) — `Resolve(Vector3 t, ReadOnlySpan<float> rowMajor9, float s)` returns TRS or a matrix, plus `RotError` and `Determinant`.
- **`NifModelGeometryReader.cs`** — NiTriShape, NiTriStrips, BSSegmentedTriShape and their data; dispatches to packed data; NiMorphData. Returns a `SceneMesh` plus native primitive facts.
- **`NifStripTriangulator.cs`** (addition, pure).
- **`NifPackedGeometryDecoder.cs`** (addition) — the BSPackedAdditionalGeometryData layout table.
- **`NifModelMaterialReader.cs`** — resolves the effective property set, builds `SceneMaterialSource`, `SceneRenderState`, layers and samplers, then calls `SceneMaterial.FromSource` (`SH/…/Models/SceneMaterial.cs:74-76`). It never sets portable fields directly.
- **`NifModelSkinReader.cs`** — skins, joints, inverse binds, influences, partitions, the dismember face stream, and the Xbox partition-order domain.
- **`NifModelLayerReader.cs`** — NiSwitchNode, NiLODNode (+ NiRangeLODData) and the hidden flag, as `SceneLayerSet`s.
- **`NifModelTextureSource.cs`** — `SceneTextureBinding Bind(string authoredPath, SceneTextureLayerRole role, int sampler, int uvSet)`.
  - resolves through `context.CompanionResolver`
  - builds DDS and DDX descriptors, runs the DDX gate and the specular companion rule
  - deduplicates by SHA-256
- **`NifDdxGate.cs`** (addition, pure) — the gate predicate.
- **`NifModelCoverage.cs`** — builds the census from `NifInfo.Blocks`, takes the disposition table plus consumption marks, and returns `ModelSourceCoverage`.
- **`NifModelNativeState.cs`** (addition) — kind constants and bounded payload builders.
- **`NifModelReadCache.cs`** (addition) — `IModelReadCacheScope` holding encoded texture bytes and DDX outputs under a byte budget. It retains nothing across items.
- **`NifModelUnits.cs`** (addition) — maps the game to `SceneUnits`, and returns the NIF `SceneSourceBasis`.

`BMT/Core/Modeling/`:
- **`BethesdaModelRegistration.cs`** — the composition root:
  - `ModelSourceRegistry CreateReaders() => new([new NifModelReader()])`
  - `ModelWriterRegistry CreateWriters(ExternalToolSettings? s = null) => new([new ModelGlbWriter(), new ModelBlendWriter(s)])`
  - `ModelMemoryEstimate EstimateMemory(ModelInputItem)`: source length (or `MaximumSourceBytes`), plus a 256 MiB companion allowance, plus DDX working space, with evidence text
  - `IModelReadCacheScope CreateCache(ModelInputItem)`
  - app-option key constants: `bmt.game`, `bmt.game-evidence`
- **`BethesdaModelWorkflow.cs`** (addition; the analog of `AWE/Core/Formats/Mesh/Sources/ModelWorkflow.cs`) — owns sources and builds requests:
  - input kinds: file, directory, archive
  - builds `ModelInfoRequest` and `ModelConvertRequest`
  - runs `ModelInfoOperation` / `ModelConvertOperation` with `ModelProcessMemoryObserver.CreateGate()` (`SH/Slfx77.Multitool.Media.Processing/Models/ModelProcessMemoryObserver.cs:13`)
  - retires returned resources (AWE `ModelWorkflow.cs:124-134`)
  - no BMT-side read path: `DumpAsync` and `PackageAsync` run the Shared `ModelDumpOperation` / `ModelPackageOperation`, which own the read lifetime (section 5)
- **`BethesdaTextureCompanions.cs`** (addition) — a `ModelCompanionResolver` over a layered VFS, exposed as an `IAssetSource`. It can reuse `BethesdaBrowseSource` (`BMT/Core/AssetBrowse/BethesdaBrowseSource.cs`, whose `OpenReadAsync` is at `:69-78`).
- **`ModelDocumentDumper.cs`** (retired at 6c94992): the text dumper this plan added at ecc8928, when Shared had no dumper (`SceneJson` only serialized node graphs), is superseded by the Shared `ModelDumpOperation` / `ModelDocumentJsonWriter` (see section 5) and is deleted.

### Row 11
- **`BMT/CLI/Commands/Mesh/`:**
  - `MeshCommand.cs` (`internal static Command Create()`)
  - `MeshInfoCommand.cs`, `MeshConvertCommand.cs`
  - `MeshDebugCommands.cs` (dump, fidelity, validate, package, formats)
  - `MeshCommandOptions.cs` (shared options)
- **`Program.cs`:** add `MeshCommand.Create()` among `:185-213`, and add `"mesh"` to `:22-54`.
- **csproj:** add a ProjectReference to Media.Blender, and add it to the slnx.
- **Tests:** `T/Core/Modeling/*`, `T/Core/Modeling/RealAsset/*`, and a new `T/Helpers/NifTestFileBuilder.cs`. It writes little- or big-endian headers for BS 14, 21, 26, 32 or 34, hand-laid blocks and the footer, extending the Writer pattern of `BigEndianNifBuilder.cs:385-460`, and stays independent of `NifSchema`.

### Design name → ecc8928 type (the code wins)

**Model types**

| Design | Type at ecc8928 |
|---|---|
| Units, Basis | `SceneUnits(metersPerUnit, SceneValueProvenance, evidence)`; `SceneSourceBasis(up, forward, handedness, normalizedByReader, provenance, evidence, right)` |
| Native state | `SceneNativeState(SceneElementRef, kind, version, payloadJson, SceneSourceLocation?, rawBytes?)`. Payload ≤ 1 Mi characters, raw ≤ 64 MiB (`SceneNativeState.cs:9-11`); document total ≤ 256 MiB and 65,536 rows. |
| Source provenance | `SceneSourceProvenance(relativePath, sha256)`, set through `ModelDocument.SourceProvenance` (not named in the design) |
| Blend | `SceneBlendState(colorEquation, alphaEquation, enabled)` with `SceneBlendEquation(src, dst, op, clamp)` and `SceneBlendTerm(constant, scale, SceneBlendInput)` |
| AlphaTest | `SceneAlphaTest(compare, double reference, rawReference, enabled)` |
| Depth | `SceneDepthState(test, write, compare, constantBias, slopeBias)` |
| Stencil | `SceneStencilState(SceneStencilDrawMode, SceneStencilTest?)` |
| DrawOrder | `SceneDrawOrder(priority, SceneDrawSort)` |
| VertexColorUse | `SceneVertexColorUse(source, lighting, neutralScale, useAlphaForOpacity)` |
| ViewAngleOpacity | `SceneViewAngleOpacity(startCosine, stopCosine, startOpacity, stopOpacity, absoluteDot, enabled, …, provenance, evidence)` |
| Layer | `SceneTextureLayer(role, binding, SceneTextureSwizzle, colorOp, alphaOp, resultScale, constant, clampResult)`. Swizzle was not in the design. Roles also include SpecularColor, LightMap, Occlusion and MetallicRoughness. |
| NormalGreen | `SceneNormalGreenConvention(SceneNormalGreen, provenance, evidence)` |
| Material exact form | `SceneMaterialSource` + `RenderState` via `SceneMaterial.FromSource` |
| Image source | `SceneImageSource(container, descriptor, original, location, standardPayload, displayTransforms, derivation, origin, missingReason)` |
| StandardPayload | `SceneStandardImagePayload(payload, descriptor, note, evidence)` |
| Derivation | `SceneImageDerivation(recipe, inputs, detailsJson, reason, payload, descriptor)` |
| BC5 Z rule | `SceneNormalZReconstruction` (the only display transform the GLB writer accepts: `ModelGltfImages.cs:186-192`) |
| Faces, points, attributes | `SceneFaceList`, `ScenePointIndices`; `SceneAttributeStream` plus `ScenePrimitive.PrimaryColorAttributeIndex` (the index is not in the design) |
| NormalProvenance | `SceneNormalProvenance(kind, ruleId)` |
| Billboard | `SceneBillboard(aim?, lockedAxis?, rigid?, pivot, anchor, rawMode?, front?, up?, roll?)`. Front, Up and Roll are not in the design, but Blender exactness needs them. |
| Layer set | `SceneLayerSet(id, label, members, defaultOn, sourceKind, exclusiveGroup)` |
| Bind mode, absolute morphs | `SceneSkin.BindMode`; `SceneMorphTarget(…, absolutePositions)` |
| Diagnostic | `SceneDiagnostic(code, DocumentText)` |

**Operation types**

| Design | Type at ecc8928 |
|---|---|
| Writer contract | `IModelWriter.PreflightAsync` / `Plan` / `WriteAsync` (the design said Preflight, Plan, Write) |
| Convert options | `ModelConvertOptions(format, scale, overwrite, ModelLayerSelection, writerOptions)` |
| Inputs | `ModelInputItem.File` / `DirectoryFile` / `ContainerEntry`; `ModelInputExpansion.DirectoryAsync` / `ContainerAsync` / `CreateRequestAsync` |
| Convert | `ModelOutputRequest`; `ModelOutputPlanner(readers, writers)`; `ModelConvertRequest(output, createCache, estimateMemory, nativeDetail, appOptions, companionResolver, progress)` |
| Info | `ModelInfoRequest(input, createCache, estimateMemory, writers, nativeDetail, appOptions, companionResolver)` |
| Debug helpers | `ModelBlendWriter.ResolvePlan` / `WritePackageAsync` (static; `ModelBlendWriter.cs:149-180`) |
| Blender option keys | `blender`, `blender-memory`, `blender-timeout` (`BlendWriterOptions.cs:16-22`) |

---

## 3. Mapping table

### Common to every block
- **Native state:** every census block gets one `bmt.nif.block` (version 1) row. It targets the element the block fed, or the Document for NativeOnly blocks. Payload: type, index, offset, size, name, and decoded fields. Arrays over 64 elements are summarized as count plus SHA-256. Raw bytes are kept only with `ModelNativeDetail.Full`.
- **Coverage:** identity `block:{i}`, kind = type name. Evidence: `NIF header block table: N blocks, T types (20.2.0.7/11/34, BE)`.
- **Typed-capable blocks** that feed nothing are NativeOnly, with reason "not reachable from footer roots" or "unreferenced by reachable geometry".
- **The reader emits no Dropped rows.** This keeps the gate's "Dropped only with later-cut reasons" rule satisfied on the reader side.

### Document and header (not a census element)
- **Document fields:**
  - `SourceFormat "bmt.nif"`, Name = file stem, `SourceIdentity = item.Reference.ToString()` (AWE pattern), and `SourceProvenance`
  - one `SceneDefinition` whose roots are the occurrences of the footer roots
- **Native state** `bmt.nif.header`: header string, version, user and BS, endianness, BS stream strings, block-type table, raw string table, groups, footer roots.
- **Units:** `GameProfiles.For(game).Units` mapped to `SceneUnits`; `UnitProvenance.ReverseEngineered` becomes `SceneValueProvenance.ReverseEngineered`.
  - The game comes from the `bmt.game` app option (from `--game` or detection of the ESM under the data root).
  - Otherwise use FNV's 1/69.99125 as Assumed, with evidence: "BS 14–34 ship in FNV and FO3; game not established; FO3's 1/69.9904 differs by 0.0012%".
- **Basis:** `SceneSourceBasis(up: +Z, forward: +Y, RightHanded, normalizedByReader: false, Assumed, "Gamebryo Z-up right-handed; BMT GLB exporter maps (x,y,z)→(x,z,−y)")`. The GLB writer requires cardinal up and forward (`ModelGltfCoordinates.cs:125-141`).

### Nodes
| Block | ModelDocument | Coverage | Rules |
|---|---|---|---|
| NiNode, BSFadeNode, BSValueNode, BSOrderedNode, BSMultiBoundNode, BSBlastNode / BSDamageStage / BSDebrisNode (BSRangeNode), BSMasterParticleSystem | `SceneNode` per occurrence: Name; `LocalTrs` or `LocalTransform`; Children = non-null refs in order; Role Transform, or Joint for skin bones | Typed | Name comes from the string table. For null Xbox names, use the `NiDefaultAVObjectPalette` name; record `nameSource` in native state. Native state keeps: all NiAVObject Flags bits, null-child ordinals, extra-data / controller / collision / effects refs, and subclass fields (BSValueNode value and flags, BSOrderedNode alpha-sort bound and static flag, multibound ref, range min/max/current, psys list). |
| NiBillboardNode | the above + `SceneBillboard` | Typed | Mode mapping below. |
| NiSwitchNode | the above + one `SceneLayerSet` per child ordinal | Typed | Id `switch:{block}:{ordinal}`; Members = that child's occurrence; DefaultOn = (ordinal == Index); ExclusiveGroup `switch:{block}`; SourceKind `bmt.nif.NiSwitchNode`. Switch flags are native. |
| NiLODNode, NiRangeLODData / NiScreenLODData | the above + layer sets per level | Typed | ExclusiveGroup `lod:{block}`. DefaultOn = the level with the smallest Near extent (ties: lowest ordinal). Center and ranges are native. |
| Hidden flag (Flags bit 0) on any NiAVObject | `SceneLayerSet hidden:{block}`, Members = [occurrence], DefaultOn false, SourceKind `bmt.nif.hidden` | — | — |
| NiTriShape, NiTriStrips, BSSegmentedTriShape | `SceneNode` with MeshIndex (+ SkinIndex) | Typed | NiGeometry material-data and segments are native. |
| NiCamera, NiPointLight, NiAmbientLight (NiSpot/Directional) | `SceneNode` Role Helper, transform kept | NativeOnly "later-cut(2): cameras and lights" | Parameters in `bmt.nif.block`. |
| NiParticleSystem, BSStripParticleSystem | `SceneNode` Role Helper, no mesh | NativeOnly "particles: no typed vocabulary" | — |

**TRS rule.** nif.xml stores Matrix33 in row-major order (`nif.xml:5932-5934`) for column vectors. The document's row-vector matrix is its transpose, as `NifObjectBlockReader.cs:130-146` already does.
- Let E = max|R·Rᵀ − I|, computed in double.
- **If E ≤ 1e-5 and det > 0:** q = quaternion of Rᵀ, Scale = (s,s,s).
- **If E ≤ 1e-5 and det < 0:** q from (−R)ᵀ, Scale = (−s,−s,−s). This is exact: (−s)(−R) = sR.
- **Otherwise:** a matrix node. Compose S·Rᵀ·T in double and round each element once. Elements are exact copies when s = 1; otherwise Converted within 0.5 ulp.
- **Always:** native state keeps t, R, s, E and the determinant. The per-node quaternion reconstruction error is recorded, since TRS is within the 1e-5 threshold, not bit-exact.
- NiSkinData bone transforms get the same analysis, recorded only (see Skin).
- **Root transforms are kept as authored.** Add a diagnostic when the root is not the identity: FO3+ placement replaces it in game (`BMT/Core/Formats/Nif/Parser/NifVersions.cs` IsTes4Era remarks).

**Billboard modes** (nif.xml options at `nif.xml:4195-4228`). Pivot = Anchor = 0 (Assumed). Front, Up and Roll stay null, because BMT's own front detection is a winding heuristic (`BMT/Core/Formats/Nif/Rendering/NifBillboardFacing.cs:7-40`) that the contract excludes. Blender therefore classes every NIF billboard Degraded until a new RE item (an extension of RE-14) establishes the convention.

| Mode | SceneBillboard |
|---|---|
| 0 | CameraPlane, Rigid false |
| 1, 5, 9 | CameraPosition, LockedAxis = the world image of local +Z, Rigid null (open question on the axis frame) |
| 2 | CameraPlane, Rigid true, Roll Camera |
| 3 | CameraPosition, Rigid false |
| 4 | CameraPosition, Rigid true |
| 8 and anything else | Aim null, RawMode kept (RE-14) |

### Geometry
| Block | ModelDocument | Coverage | Rules |
|---|---|---|---|
| NiTriShapeData | One `SceneMesh` with one `ScenePrimitive` per geometry and effective-material key | Typed | See the field list after this table. |
| NiTriStripsData | same | Typed | Strips triangulated as below. Strip lengths, dropped count and indices are native (the design says this). |
| BSPackedAdditionalGeometryData (Xbox) | the geometry streams | Typed if every used stream is mapped; unmapped streams native | Layout table below. |
| NiAdditionalGeometryData (PC LOD, e.g. `landscape/lod/.../nvdlc04road01world.level8.x-3.y-3.nif` in the manifest) | — | NativeOnly "additional geometry streams: semantics not established" | — |
| NiMorphData (reached through NiGeomMorpherController) | `SceneMorphTarget` per morph ≥ 1: Name = Frame Name. Relative Targets = 1 → PositionDeltas; 0 → AbsolutePositions. | Typed (keys native) | Morph 0 is compared bit for bit with the base (diagnostic on mismatch). Rest weights null (implicit zero, Assumed). |
| NiGeomMorpherController | — | NativeOnly "later-cut(1b)" | — |

**Primitive fields (NiTriShapeData and the strips data):**
- **Positions:** raw.
- **Normals:** raw when authored, with `NormalProvenance(Authored)`. When absent: `(0,0,0)`, `NormalMode.Flat`, `NormalProvenance(Flat)` (the two must agree: `SceneSourceGeometryValidation.cs:42-44`), plus a native flag.
- **UVs:** set 0 in `TexCoord`, raw with no V flip. NIF, the document and DDS are all top-left-origin; Blender flips V once itself (`BlendAdmission.cs:532-535`). Sets 1..n go to `AdditionalTextureCoordinates`.
- **Colors:** Color4 floats are kept raw, uncapped, in `SceneVertex.Color` and in primary attribute 0: `"nif.vertexColor"`, Vertex domain, Float32×4, Srgb, Assumed (RE-11), `ColorEncoding.FloatingPoint`. With no colors: `Color = (1,1,1,1)`, because the legacy builder always writes COLOR_0 (`SceneGltfBuilder.cs:451`).
- **Tangents** (corrected 2026-09-28, TestOutput/nif-tangent-frame-20260928/MEASURE.md): `SceneTangents` xyz = the nif **"Bitangents"** array, raw, because it is the one that runs along +dP/du (99.91% of scored PC cover vertices; the stored "Tangents" runs along +dP/dv and along +dP/du on 0.12%). w = sign(dot(cross(N, Bitangents), -Tangents)), which is glTF's handedness for top-left UVs (glTF's bitangent cross(N, xyz) * w runs up the image, -dP/dv), and +1 when undetermined, with native counts (Assumed). The plan's first reading (xyz = "Tangents", w = sign(dot(cross(N,T),B))) typed the V axis as TANGENT; its w is algebraically the same. Console packed layouts: the lower-offset Bitangent channel is the xyz, exactly as the PC array it reproduces. Exception pending an owner ruling: terrain LOD (shader flags 2 bit 2) and seven FO3 SCOL files store Tangents = cross(N, Bitangents), so this w reproduces a constant-handed stored frame there while the UVs mirror.
- **Bitangents:** glTF has no bitangent attribute, so the stored V-axis array ("Tangents", or the packed Tangent channel) is not typed; both stored arrays go to native state under their nif.xml names (`storedTangentFrame` in the primitive row, the values in the block row), not to an attribute stream; see §7 gap G2. The consumer's rebuilt bitangent differs from the stored one by more than 1 degree on 7.35% (PC) and 8.35% (console) of vertices (skewed UV mappings), a loss inherent to glTF.
- **Triangles:** triangles with a repeated index are dropped and counted, including in NiTriShapeData lists (open question: the design names strips only). Zero-area triangles with distinct indices are kept.
- **Native** `bmt.nif.primitive`: Group ID, keep and compress flags, BS Data Flags, consistency flags, bounding sphere, match groups, additional-data ref, dropped-triangle facts.
- **Empty geometry** (no triangles left, or no vertices): no primitive is emitted (ValidateStructure would reject it), and the block is NativeOnly "no drawable triangles".

**Strip rule:** for each strip, for i in 0..L−3, take (a,b,c) = (p[i], p[i+1], p[i+2]).
- If any two are equal, drop the triangle and count it.
- Else emit (a,b,c) when i is even, (a,c,b) when i is odd.
- Parity counts from the strip start, including skipped triangles.

**Packed-stream layout table.** The semantics are not stored in the file (`nif.xml:16434-16453`).
- **Key** each layout by (stride, per-stream type, unit size, offset), and record provenance and a layout id in native state.
- **Decoding:**
  - half → float32 exactly; NaN or ∞ throw
  - ubyte4 bone indices: a big-endian uint32, so components are reversed (`NifPackedDataExtractor.cs:592-606`)
  - D3DCOLOR: ARGB bytes reordered to RGBA as UInt8 normalized, Srgb Assumed (`:631-645`)
  - weights: half4 kept as authored, including any 1.0 "sentinel"; no renormalization
  - W components go to native state
- **Unknown layouts:** the block is NativeOnly "packed layout unknown", and the geometry is NativeOnly too. Never guess from vector length.
- **Skinned Xbox geometry:** the vertex domain is partition order (`NifSkinPartitionExpander.cs:489-490`). `ScenePointIndices(PointCount = NiTriShapeData.NumVertices, values = concatenated vertex maps)`. Triangles come from the partitions, offset by each partition's start. Morph deltas are indexed through the points. This is exact, not welded.

### Skin
| Block | ModelDocument | Coverage | Rules |
|---|---|---|---|
| NiSkinInstance, BSDismemberSkinInstance | `SceneSkin(name, joints, inverseBinds, skeletonRoot)`, BindMode InverseBind; node SkinIndex; bone nodes get Role Joint | Typed | A bone block with several occurrences resolves to the one under the skeleton root; if still ambiguous, throw. A repeated bone throws (Shared rejects it). |
| NiSkinData | IBM[i] = per-bone Rotation/Translation/Scale as S·Rᵀ·T | Typed | IBM elements are exact copies when s = 1. Influences come from per-bone vertex weights when Has Vertex Weights = 1: stride = the maximum count per vertex, padded with (0, 0.0), bone order, weights as authored. Overall transform is native, with a diagnostic when it is not the identity (BMT ignores it: `NifShapeSkinningDataBuilder.cs:146-147`). |
| NiSkinPartition | PC: native state (bones, vertex maps, weights, strips and triangles, bone indices) plus consistency checks; typed influences when NiSkinData has none. Xbox: vertex domain and triangles. | Typed | — |
| BSDismemberSkinInstance partitions | Face-domain streams `nif.dismember.bodyPart` / `partFlag` (UInt16) over `Faces` = the triangles | Typed | Open question G2 (GLB reports it Dropped). |

### Materials and properties
The effective property set is computed Gamebryo-style: ancestor properties are inherited and the nearest property of each type wins. A property on a node therefore produces a per-placement material key.

| Block | Typed state | Rules |
|---|---|---|
| NiAlphaProperty | `Blend`: SceneBlendState with the same factors for color and alpha, Add, clamp true (Assumed: 8-bit target), Enabled = bit 0. `AlphaTest`: Compare mapped, Reference = T/255 as double, RawReference = T, Enabled = bit 9. `DrawOrder(0, NoSorter ? Authored : BackToFront)` when blending. | Factors: ONE (1,0,Zero); ZERO (0,0,Zero); SRC_COLOR (0,1,SourceColor); INV_SRC_COLOR (1,−1,SourceColor); DEST_* uses DestinationColor/Alpha; SRC_ALPHA (0,1,SourceAlpha); INV_SRC_ALPHA (1,−1,SourceAlpha); SATURATE uses SourceAlphaSaturate. Test functions ALWAYS…NEVER map to `SceneCompareFunction`. a8 > T ⟺ a8/255 > T/255, so the test is exact. Clone Unique, Editor Alpha Threshold and the unknown short go to native state. |
| NiStencilProperty (20.2.0.7 Flags + ref + mask) | `Stencil(DrawMode, Test(compare, ref, mask, mask, fail/zfail/pass, enabled))` | DRAW_CCW → CounterClockwise; DRAW_CW → Clockwise; DRAW_BOTH → Both; DRAW_CCW_OR_BOTH (0) → CounterClockwise, Assumed, raw kept (`nif.xml:4300-4312`). |
| NiZBufferProperty | `Depth(test, write, compare)` | Not in the sample. Without it, depth comes from BSShader SF1 bit 31 (test) and SF2 bit 0 (write), with compare LessEqual Assumed (probe tag `shaderzbuf`). |
| NiVertexColorProperty | `VertexColorUse(source, lighting, (1,1,1,1), alpha)` | Not in the sample. FNV uses SF2 Vertex_Colors / SF1 Vertex_Alpha instead (Assumed; RE-11). |
| NiMaterialProperty | `SceneMaterialSource`: BaseColor = (diffuse when BS < 26, else (1,1,1)) with alpha; AmbientColor (BS < 26); SpecularColor; Glossiness; EmissiveColor; EmissiveMultiplier (BS > 21) kept separate; LightingModel BlinnPhong (Assumed) | Flags are native. |
| BSShaderPPLightingProperty, Lighting30ShaderProperty | Layers from texture-set slots 0–5; one sampler from Texture Clamp Mode (S → U, T → V) for every layer (Assumed); Environment layer constant = Env Map Scale; Unlit false | Slot roles: 0 BaseColor, 1 Normal (+ NormalGreen Assumed, RE-13), 1-alpha Specular (identity swizzle, only when SF1 bit 0 Specular is set: PC "normal alpha"), 2 Glow, 3 Parallax, 4 Environment (cube), 5 EnvironmentMask. Shader type and flags, refraction strength and fire period, parallax passes and scale are native. |
| BSShaderNoLightingProperty | Unlit true; File Name → BaseColor layer; Falloff → `SceneViewAngleOpacity(cos start, cos stop, opacity start, opacity stop, absoluteDot true, Assumed)` | — |
| SkyShaderProperty, TileShaderProperty, TallGrassShaderProperty | File Name → BaseColor layer | Typed; sky object type native. |
| WaterShaderProperty | — | NativeOnly "water shading: no typed vocabulary (later-cut 2)". The shape keeps a placeholder material plus a diagnostic. |
| BSShaderTextureSet | Layer bindings | Typed. Its own count is native. |
| NiTexturingProperty (FO3 effects) | Layers: Base→BaseColor, Dark, Detail, Gloss→Specular, Glow, Normal, Parallax, Decal n; Bump → native (no carrier) | Apply mode drives ColorOp (MODULATE → product). Clamp and filter become the sampler. UV set becomes `TextureCoordinateSet`. The transform becomes `SceneTextureTransform`, computed per transform method (Assumed; open question). |
| NiSourceTexture | `SceneImage` (Name = authored path) | Typed. Embedded NiPixelData → NativeOnly "later-cut(2)". Format prefs native. |
| NiFogProperty | — | NativeOnly "no typed vocabulary". |

The design's "tint" has no field in any BS 14–34 BSShader layout in nif.xml (`nif.xml:13966-14047`), so there is nothing to type.

### Everything else
All of the following are NativeOnly, each with the reason shown:

| Blocks | Reason |
|---|---|
| NiControllerManager, NiControllerSequence, NiTransformController, NiMultiTargetTransformController, NiVisController, NiAlphaController, NiMaterialColorController, NiTextureTransformController, BSMaterialEmittanceMultController, BSRefractionStrength/FirePeriodController, BSFrustumFOVController, NiLightColor/DimmerController, NiFloatExtraDataController, NiBSBoneLODController, every interpolator (NiTransform, NiFloat, NiPoint3, NiBool, NiBoolTimeline, NiBlend*, NiBSpline*, NiPath, NiLookAt, BSRotAccumTransf, BSTreadTransf) and every data block (NiTransformData, NiFloatData, NiPosData, NiBoolData, NiColorData, NiBSplineData, NiBSplineBasisData), NiTextKeyExtraData, BSAnimNotes | "later-cut(1b): animation" |
| NiPSys* (controllers, modifiers, emitters, colliders), NiPSysData, BSStripPSysData, BSPSys*, BSParentVelocityModifier, BSWindModifier | "particles" |
| bhk* (including bhkBlendController) and hkPackedNiTriStripsData | "Havok (later-cut 2)" |
| BSMultiBound, BSMultiBoundAABB, BSMultiBoundSphere | "multibound culling data" |
| BSXFlags, NiString/Integer/Float/BinaryExtraData, BSBound, BSDecalPlacementVectorExtraData, BSWArray | "other extra data" |
| BSFurnitureMarker | "markers (later-cut 2)" |
| NiDefaultAVObjectPalette | NativeOnly "later-cut(1b)", except **Typed** when it supplies node display names |

This covers all 167 block types in the manifest (my extraction). The design lists NiZBufferProperty, NiVertexColorProperty, NiSwitchNode and NiLODNode, but none of them appears in the sample: they can be tested only synthetically.

---

## 4. Textures

### Resolution
- **The shell owns the texture source:** an `IAssetSource` over `GameFileSystem.OpenDataFolder(root)` (loose files first, then BSAs in order) for each `--data-root`, plus each `--textures-archive`. Without options, infer the Data root by walking up to a folder containing `textures` (`BMT/CLI/Rendering/Nif/NifExportPathResolver.cs:99-116`). For archive inputs, use the archive's own folder.
- **The shell's `ModelCompanionResolver`:**
  1. Normalizes the authored path with `NifTexturePathUtility.Normalize`.
  2. Returns the single winning entry, taking provenance from `TryReadAllBytesBounded`'s actual layer.
  3. Falls back from `.dds` to `.ddx` (the Xbox engine's spelling).
- **The reader** calls `context.CompanionResolver(context.Item, authoredPath, ct)` synchronously.
  - If it gets `ArgumentException` (the Shared default resolver), it retries with the basename.
  - Zero matches → a missing image with a reason; several matches → missing, "ambiguous".
- **The image records** Name = the authored string; `SceneSourceLocation` = the resolved `AssetReference`; `bmt.nif.texture` holds the normalization rule, the extension fallback and the layer.
- **Bytes** are read once into `NifModelReadCache` under a budget, and images are deduplicated by SHA-256.

### Descriptors (no pixel decoding)
- **DDS:** `DdsImageDecoder.Inspect` supplies dims, mips, bpp, channels (its BC1 scan yields RGB or RGBA per R23), compression and color space. The reader's own header path handles cubes (`IsCube = true`). BC5 images get `SceneNormalZReconstruction(false, true, Assumed)`.
- **DDX:** a new public `DdxParser.ReadHeader` in the DDXConv fork: magic, version, dims, format byte, declared mip count. PixelFormat is namespace "Xenos" with the format byte as code.

### The DDX gate
Take a fresh `DecodeDiagnostics` per image in `ConversionOptions.Diagnostics` (`ConversionOptions.cs:18`) and call `ConvertDdxToDds`. **Pass only if all of these hold:**
- `IsLossless`, TruncatedReads = 0 and PaddedBytes = 0
- DroppedTrailingDataBlocks = 0, FullAtlasFallbacks = 0 and FilledMipTailBlocks = 0. The class says these are read by the caller's own gate (`DecodeDiagnostics.cs:70-75`).
- the format byte is in {0x52, 0x53, 0x54, 0x71, 0x7B}
- output dims equal the header's
- the output mip count equals the declared count, after trimming fabricated all-zero levels beyond it (the trim is recorded)

**Outcomes**

| Case | Representation |
|---|---|
| Pass | Original DDX, plus `SceneStandardImagePayload(dds, descriptor from Inspect, "DDXConv relayout: untile and endian swap, no pixel change", evidence = DDXConv commit + counters + checks)` |
| Fail (including 3XDR with declared mips, 0x43, 0x86) | Original DDX, no StandardPayload, and `Derivation("bmt.ddx-recovery", [], counters JSON, reason, DDS output, descriptor)` per the design. Blender packs it Degraded; the GLB writer at ecc8928 rejects it (gap G1). |
| DDXConv throws | Original only, plus a diagnostic |
| No texture | Original null, with a missing reason |

### Xbox specular companion
Applies only when the slot-1 file resolved to a `.ddx` and `NormalMapMerge.IsNormalMapPath` is true (the same boundary as `NifTextureLoader.cs:97-104`).
- Resolve `ComputeSpecularPath`.
- If found, add its own image: Origin PlatformCompanion, Original BC4 DDX, gated.
- Bind it to a Specular layer (swizzle alpha ← Red; to be agreed with the foundation).
- Never apply the BC5+BC4 merge.
- PS3 and PC never take this path. The probe measured that the `*_s` convention is Xbox-only (`nif_feature_probe.py:66-74`).

### Inspection must not
- decode pixels, run S3TC, BC4 or BC5 decoders, or make PNGs
- merge normals
- touch `NifTextureResolver`

It may read headers, hash bytes and run the BC1 selector scan (design §3.2).

**Decision needed:** whether the DDX relayout counts as "pixel decoding". I recommend allowing it: it is LZX decompression and untiling, a lossless re-containerization. Without it, every bound DDX has no DDS representation, the GLB plan throws inside `mesh info`, and ModelInfoResult exits 1 (`ModelInfoResult.cs:57`).

---

## 5. The `mesh` shell (AWE is the pattern)

AWE's `ModelInfoCommand` and `ModelConvertCommand` apply almost verbatim (`AWE/CLI/ModelInfoCommand.cs:16-116`, `AWE/CLI/ModelConvertCommand.cs:1-83`). Replace AWE's `--source-meters-per-unit` with BMT's context options.

**Shared options:**
- `--data-root <dir>...`, `--textures-archive <file>...` (the same spelling as `export nif`: `ExportNifCommand.cs:25-34`)
- `--game fnv|fo3|auto`
- `-e/--entry <glob>...` and `--all` (required for BSA or BA2 inputs)
- `--native metadata|full`

### Subcommands
- **`mesh info <input> [-e] [-v] [--json]`**
  - Workflow: build a `ModelInputItem` (File, or ContainerEntry), then `ModelInfoRequest(item, CreateCache, EstimateMemory, writers: null, nativeDetail, appOptions, resolver)`, then `ModelInfoOperation(readers, writers, gate)`.
  - Output: `ModelInfoTextFormatter.Write`, or `ModelInfoJsonFormatter.Write` with `--json`.
  - Exit code: `result.ExitCode`.
  - Blender preflight only locates Blender; it never launches it.
- **`mesh convert <input> <output_path>`** with `--format glb|blend` (default glb), `--scale` (finite and > 0), `--overwrite`, `--layers default|all|ids`, `--report`, `--blender`, `--blender-memory`, `--blender-timeout`, and the context options.
  - Inputs: `ModelInputExpansion.DirectoryAsync` / `ContainerAsync` / `ModelInputItem.File`, then `CreateRequestAsync`.
  - Options: `ModelConvertOptions(format, scale, overwrite, ModelLayerSelection, writerOptions {blender, blender-memory, blender-timeout})`.
  - Execution: `ModelConvertOperation(new ModelOutputPlanner(readers, writers), writers, gate)`, then `ModelConvertTextFormatter.Write`, then `RetireReturnedResourcesAsync`.
  - Exit codes come from `ModelConvertResult.ExitCode`: 0, 1, 2 for plan failure (a missing Blender fails preflight for blend), 130.
  - `--json` on convert has no public Shared formatter: `ModelConvertReportWriter` is internal (`ModelConvertReportWriter.cs:9,24`). Use `--report` in 1a.
- **Debug commands** (updated for the Shared foundation at 6c94992): `validate` and `fidelity` go through `ModelInfoOperation` like `info`; `dump` and `package` go through the Shared `ModelDumpOperation` and `ModelPackageOperation`, which own the read lifetime (probe, cache, `ModelReadLifetime`, memory gate, retirement) so BMT keeps no read path of its own. The three-step BMT read path this section first described was retired when those operations shipped.

  | Command | Behavior |
  |---|---|
  | `mesh dump <input> [--native] [--output <file> [--overwrite]]` | Shared `ModelDumpOperation` (`SH/…/Models/Inspection/Dump/`): the exact schema-version-1 JSON of `ModelDocumentJsonWriter` (`SH/docs/model-dump-json.md`) streamed to standard output, or published through `StagedStreamExport.WriteGeneratedAsync` with `--output`; `--native` requests full native retention. Exit codes are the result's (0, 1, 2 ambiguous, 130). BMT's text `ModelDocumentDumper` is superseded and removed. |
  | `mesh validate <input>` | `ValidateStructure`; exit 0, or 1 with the message |
  | `mesh fidelity <input> --format glb\|blend` | Preflight + Plan. For blend, add `ModelBlendWriter.ResolvePlan`. GLB pending rows cannot be resolved without writing (no public API; gap G6): stage a temporary write and delete it, or print pending. |
  | `mesh package <input> <out.zip> [--scale] [--overwrite]` | Shared `ModelPackageOperation` (`SH/…/Media.Blender/Operations/`): `ModelBlendWriter.PreflightPackage` (package-only preflight, no tool discovery, so host Blender state cannot fail it), plan, staged `WritePackageAsync`, atomic publish; an existing zip without `--overwrite` is Skipped with exit 0 per `ModelPackageRequest`. Output is `ModelPackageTextFormatter`. |
  | `mesh formats` | Registrations, GameProfiles unit rows |

### Exit codes and cancellation
- **Usage errors → 2.** System.CommandLine's default parse-error exit code must be mapped to 2; check the 2.0.8 API. BMT invokes `rootCommand.Parse(args).Invoke()` (`Program.cs:215`).
- **Cancellation → 130** (AWE catches `OperationCanceledException`).
- **Ownership:** the shell owns every source (input source, texture source) until the operation and its returned unretired handles finish.

---

## 6. Ordered implementation slices

Every slice builds and tests on its own.

| # | Slice (≈ lines) | Files | Synthetic tests (each with a control that must fail) | Bucket-B |
|---|---|---|---|---|
| 0 | Adoption (≈ 300) | Commit the row-9 rename and the ecc8928 gitlink; commit the DDXConv counters in the fork and bump the gitlink; add the Media.Blender reference | Counter tests: a 3XDR with a non-zero tail → DroppedTrailingDataBlocks > 0 (control: all-zero tail → 0); a full-atlas fallback; a mip-tail fill | — |
| 1 | Decoder (≈ 1,100) | `Nif/Decoding/*`, strict-expression additions, `NifFooterReader`, `T/Helpers/NifTestFileBuilder.cs` | Little- and big-endian decode of NiNode, NiTriShapeData, NiAlphaProperty, BSShaderPPLighting, NiSkinData, NiMorphData, BSPacked (erratum), BSDismember (BSPartFlag). Controls: one appended byte fails the size check; a BS 26 NiMaterialProperty reads Emit Mult (a BS > 26 gate fails); a BS > 26-gated reader fails the Emit Mult value test. Negative control: a swapped BSPartFlag passes the size check but fails the value test. Schema lint over the in-scope types. | — |
| 2 | Reader skeleton (≈ 900) | `NifModelReader` (Probe + nodes), `NifModelNodeReader`, `NifModelTransform`, `NifModelCoverage`, `NifModelNativeState`, `NifModelReadCache`, `NifModelUnits`, `BethesdaModelRegistration.CreateReaders` | Probe: NotAModel; Unsupported for 20.0.0.4 ("cut 2"), 3.3.0.13 and 4.2.1.0 ("other version"), BS 24/25/27/28/30/31/33 ("cut 1b"), and a `.kf` root; Supported for BS 14–34 in both byte orders; Tentative for headers over 64 KiB. TRS: orthonormal, reflection and shear cases (control: a 1e-4 perturbation must route to a matrix node). Cycles throw. Multi-parent instancing. Coverage complete (control: drop one census block → mismatch against an independent header list). `ValidateStructure` passes. | A2 on the sample: census vs the probe's `details.blocks` (control: remove one block from the census) |
| 3 | Geometry and morphs (≈ 1,000) | `NifModelGeometryReader`, `NifStripTriangulator` | Strip parity (control: no odd flip fails). Repeated-index triangle dropped and counted (control: keeping it fails). Zero-area distinct-index triangle kept (control). Colors > 1 kept (control: a clamping implementation fails). UV not flipped (asymmetric fixture). Relative and absolute morphs. Morph-0 mismatch diagnostic. | A3 static LE: reader vs `NifGeometryExtractor(bindPoseOnly)` per `SourceBlockIndex`, bit-equal positions, normals (where authored), UVs and triangles, with the oracle's documented exclusions (control: one reader vertex moved) |
| 4 | CLI vertical slice (≈ 900) | `MeshCommand` (info, convert), `BethesdaModelWorkflow`, `Program.cs` | Parse errors exit 2. Convert a synthetic NIF to GLB: output exists; second run Skipped; `--overwrite` replaces. A directory with a non-NIF file is listed, not failed. An explicitly named non-model exits 1. `--format blend` with no Blender exits 2. | — |
| 5 | Materials (≈ 1,100) | `NifModelMaterialReader` plus property views | 11 blend pairs evaluated with `SceneBlendEquation.Evaluate` against analytic results (control: swapped src/dst fails). T/255 with RawReference (control: T/256). Emit Mult separate (control: a pre-multiplied implementation fails). Stencil modes. BS 21 vs 26 vs 34 field presence. Property inheritance override (control). | A1 materials, alpha, shaders, texture sets against checked-in probe JSON (control: the design's ONE/ONE → SRC_ALPHA/INV_SRC_ALPHA byte patch in `dlc05spacewindowcover01.nif`) |
| 6 | Textures (≈ 1,000) | `NifModelTextureSource`, `NifDdxGate`, `BethesdaTextureCompanions`, DDXConv `ReadHeader` | DXT1 with and without a transparent block → RGB vs RGBA (control). `SyntheticDdxPayload` (`T/Helpers`) passes the gate → StandardPayload; a padded or truncated one → Derivation (control). 3XDR with mips; format 0x43/0x86. Companion present or absent. Loose file shadows the archive. Inspection never reaches the pixel path (a relayout spy). | A4: original SHA-256 vs checked-in expectations from the Python BSA reader (control: a loose copy with one byte changed) |
| 7 | Skin, PC (≈ 900) | `NifModelSkinReader` | Weights as authored (control: normalizing fails). Stride and padding. Duplicate bones throw. Skeleton root. Overall-transform diagnostic. Dismember face stream. | A3 skinned in bind space; A1 skins (bones, partitions, body parts) |
| 8 | Layers and billboards (≈ 500) | `NifModelLayerReader`, billboard mapping | Exclusive switch groups; null-child ordinals; LOD finest level; hidden → default-off (control); modes 0–5 and 9; mode 8 raw with Aim null | A1 nodes (billboard mode, hidden) |
| 9 | Debug commands (≈ 700) | `MeshDebugCommands` | Deterministic dump is Shared's (ModelDumpOperationTests); BMT asserts the `--output` publish and exit codes. `validate` catches a broken reference. `package` produces a zip without Blender. | — |
| 10 | Xbox big-endian geometry and skin (≈ 1,100; after the layout measurement in §7) | `NifPackedGeometryDecoder`, Xbox branches in the geometry and skin readers | Extended `BigEndianNifBuilder` with packed streams: half exactness (control: a truncating half→float fails); D3DCOLOR order (control); partition domain with PointIndices (control: welding fails); an unknown layout → NativeOnly | A3-BE against the `NifConverter.Convert` output (`BMT/Core/Formats/Nif/Conversion/NifConverter.cs:17-80`). Shapes are mapped by ordinal, or by exposing the converter's block remap; points are compared through PointIndices. |
| 11 | Oracle harness (≈ 800) | `T/Core/Modeling/RealAsset/*` (manifest-driven, `[Collection(SequentialIntegrationGroup.Name)]`, BucketB guard plus trait), checked-in probe JSON per sample SHA | Every hop's control asserted | A1–A4 across the whole cut-1a manifest |

---

## 7. Risks, contract gaps, open questions, measurement plans

### Contract gaps at ecc8928 (raise with the foundation)
- **G1. Gate-failing DDX blocks the whole GLB item.** An original DDX with a recovery Derivation throws in `ModelGltfImages.PlanCore` (`:128-158`), so the item is Unsupported (`ModelGltfAdmission.cs:86`, `ModelGlbWriter.cs:83`). The design's §5.2 row ("DDX failing the gate → PNG of the recovery output, Degraded") needs a small Shared change: select `Derivation.Payload` with a Degraded row. Blender already does this (`BlendAdmission.cs:244-249`).
- **G2. GLB Dropped rows conflict with gate 1a.** The GLB writer emits Dropped rows, with non-later-cut reasons, for all source attributes, including the design-mandated primary color stream, and for Faces, PointIndices and non-Render purposes (`ModelGltfGeometry.cs:351-367`). This conflicts with gate 1a's "Dropped only with later-cut reasons" and blocks:
  - the §6.3 color-space rule
  - dismember face streams
  - the Xbox partition-order domain

  Ruling needed: reclassify these R12 omissions (Metadata, or an allowed reason code), or apply the gate's Dropped rule only to reader coverage.
- **G3. Non-TRS matrix nodes make the GLB item Unsupported** (`ModelGltfAdmission.cs:99-106`). The design promised "shear Degraded, matrix in extras". The sample contains `rot=nonorthonormal`.
- **G4. Vertex colors outside [0,1] make the GLB item Unsupported** (`ModelGltfVertexColors.cs:59-61`). The design's raw-color rule meets no Approximated-within-1/255 path.
- **G5. The Xbox specular companion is never passed to the normal-map plan** (`ModelGltfMaterials.cs:88`). Also, a Specular layer with a non-identity swizzle does not project in `SceneMaterialSummaryLayers` (`SceneMaterialSummary.cs:65-66`).
- **G6. No public way to resolve GLB pending rows without writing,** and no public convert JSON formatter.
- **G7. Decal depth bias has no typed vocabulary.** The engine's decal bias is a constant, not a NIF field, and SF1 Decal flags cannot reach writers because writers never read native state (design rule).
- **G8. Bind rigidity tolerance is 0** (`BlendBindAnalysis.cs:16`); see the measurement plan below.

### Design points the code contradicts or leaves open
- **Wrong strip citation.** The design's strip citation (`NifSubmeshExtractor.cs:751, 796`) is the Morrowind path; the 20.2.0.7 rule is `NifTriStripExtractor.cs:60-92`.
- **Triangles with repeated indices in NiTriShapeData lists.** Dropping them keeps Blender's gate clean (`BlendAdmission.cs:522-531`), but the design names strips only. Measure how many exist before ruling.
- **BE is not a simple byte swap.** Xbox skinned meshes store vertices in partition order, triangles live in the partitions, and weights include a "sentinel" whose meaning is unknown. The existing extractor normalizes and zeroes that sentinel (`NifPackedDataExtractor.cs:541`), which means it is an RE question, not a solved decode. Also, `NiSkinData.HasVertexWeights` is 0 on Xbox (`BMT/Core/Formats/Nif/Skinning/NifSkinDataExpander.cs:1-4`).
- **Names still to settle:** ~~Tangents vs Bitangents naming in FO3 data~~ settled 2026-09-28 by measurement (the nif.xml "Bitangents" runs along +dP/du on FNV/FO3 PC, the console Bitangent channel likewise, and the BS 14 NiBinaryExtraData tangent space stores +V first and +U second, which the cut-1a reader leaves native; see the Tangents bullet above); the NormalGreen convention (RE-13); the vertex-color source from shader flags when NiVertexColorProperty is absent (RE-11).
- **Texture transform formulas** per Transform Method (Maya/Max, with center) are recalled, not measured. Only 2 sample files carry `uvxform`.
- **Billboard facts:** Front, Up and Roll, and the frame of the rotate-about-up axis. Until settled, Blender marks all NIF billboards Degraded, where the design expected Approximated.
- **Editor markers:** BSXFlags' editor-marker bit plus an `EditorMarker`-named node are hidden in game (recalled). Typing that would be name-based inference; keep it NativeOnly in 1a.
- **`mesh info` on animated NIFs** reports `isAnimated: false` in 1a, because there are no clips until 1b. Add a document diagnostic with the controller count.
- **No samples for four design block types.** NiSwitchNode, NiLODNode, NiZBufferProperty and NiVertexColorProperty do not occur in the sample; only synthetic tests cover them.
- **Units for BS 34 and BS 32 Skyrim LE stragglers** (e.g. `architecture/farmhouse/fern01.nif`) stay Assumed unless `--game` or detection says otherwise.

### Measurement prerequisites
These are read-only runs by the owner, as extensions to the probe and cover tooling:
1. **Packed-layout census over the BE key:** (stride, types, unit sizes, offsets) × skinned or not × platform. The layout table is built from it. Control: one synthetic file per layout.
2. **Per-file counts:**
   - node-level properties (inheritance)
   - repeated-index list triangles
   - duplicate skin bones
   - a non-identity NiSkinData overall transform
   - PC files with `HasVertexWeights = 0`
   - out-of-range vertex colors
   - non-identity roots
   - NiNode properties that change the effective material per placement

### Bind-orthonormality measurement plan
- **Script:** `tools/scripts/nif_bind_census.py`. It is independent and reuses the probe's own `p_skindata` parser (`nif_feature_probe.py:1080-1100`). It runs over the FNV loose tree, the FO3 BSA and the X360 BSA.
- **What it computes** for each NiSkinData bone:
  - Build the document's inverse-bind linear part exactly as the reader will: the elements of sR rounded to float32. They are exact copies when s == 1.0f.
  - Compute Blender's quantity in float64: G = M·Mᵀ, error e = max|G − I|, and the determinant (`BlendBindAnalysis.IsRigid`, `:92-104`).
- **Buckets:** e == 0 (rigid in Blender); (0, 1e-7]; (1e-7, 1e-6]; (1e-6, 1e-5]; > 1e-5. Record s == 1 vs not, and det < 0.
- **Per-skin roll-up:** Blender classifies per skin (`BlendAdmission.cs:665-690`), so count skins rigid at tolerance 0, ≤ 1e-6 and ≤ 1e-5.
- **Residual bound:** for each bone, a rest-residual upper bound in meters = the norm of the axis-scale decomposition error × the NiSkinData bone bounding radius / 69.99125.
- **Same metric on joint NiAVObject rotations,** to cover the TRS rule.
- **Output:** per (game, platform) stratum, counts plus the 20 worst files.
- **Controls:**
  - synthetic axis-aligned bones must count as exact
  - a 30° rotation stored in float32 is reported, whichever bucket it lands in
  - s = 1.0001 is non-rigid at every tolerance
  - a sheared matrix lands above 1e-5
  - a one-ulp perturbation of a real rigid bone must change its bucket
- **Cross-check:** a Bucket-B C# test recomputes the buckets from the reader's `SceneSkin` on the sample and must match the Python counts.
- **Decision input:** I predict, but have not measured, that almost no non-axis-aligned bind is exactly orthonormal in float32. If most are within 1e-6, propose that the foundation adopt a float32-aware tolerance classed Approximated with the measured position residual, rather than Degraded.

### Other risks
- **Memory:** DDX relayout and duplicated Xbox vertices. Bound the cache; estimate = 8× source + 256 MiB.
- **The ASCII string decoding** in `NifParser`.
- **The fail-open expression evaluators** (strict wrappers are mandatory).
- **Budgets:** the shared coverage ceiling is 65,536 elements and the fidelity report has its own row limit (`ModelFidelityReport.MaximumRows`); a very large NIF may hit them.

---

### Critical Files for Implementation
- `C:/dev/Multitool-worktrees/bmt-material-preparation-20260912/src/BethesdaMultitool/Core/Formats/Nif/Parser/NifParser.cs`
- `C:/dev/Multitool-worktrees/bmt-material-preparation-20260912/src/BethesdaMultitool/Core/Formats/Nif/Schema/NifSchema.cs` (with `Conversion/NifValueConverter.cs` as the design reference for the new decoder)
- `C:/dev/Multitool-worktrees/bmt-material-preparation-20260912/shared/Multitool.Shared/src/Slfx77.Multitool.Core/Models/Sources/IModelSourceReader.cs` (with `ModelReadContext.cs` and `ModelSourceCoverage.cs`)
- `C:/dev/Multitool-worktrees/bmt-material-preparation-20260912/shared/Multitool.Shared/src/Slfx77.Multitool.Media/Models/ModelGltfImages.cs` (with `Media.Blender/BlendAdmission.cs`, the writer admission that constrains the reader's representation)
- `C:/dev/Multitool/AweMultitool/src/AweMultitool/Core/Formats/Mesh/Sources/ModelWorkflow.cs` (with `AseModelSourceReader.cs`, the composition-root and reader pattern)