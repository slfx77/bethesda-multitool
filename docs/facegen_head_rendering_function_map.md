# FaceGen Head Rendering Function Map

Last updated: 2026-04-04

This is the maintained function-level reference for the FaceGen investigation.
It exists to answer:

- which functions have already been meaningfully investigated
- what role each function currently has
- which interpretations are stable vs provisional
- which branches are demoted
- what the next exact targets are

Use this document as the working map.

Read order:

1. [facegen_head_rendering_current_status.md](/c:/Users/mmc99/source/repos/Xbox360MemoryCarver/docs/facegen_head_rendering_current_status.md)
2. this file
3. [facegen_head_rendering_pipeline.md](/c:/Users/mmc99/source/repos/Xbox360MemoryCarver/docs/facegen_head_rendering_pipeline.md) only for chronology and supporting history

## Scope

This map is about the shipped/editor `_0` texture-parity branch first.

It is not trying to be a full runtime FaceGen spec.
Runtime-only findings are included only when they help separate false leads from
the shipped `_0` branch.

## Confidence levels

- `High`: directly supported by a recovered function body or repeated converging artifacts
- `Medium`: structurally strong, but still partly interpretive
- `Low`: active hypothesis; not stable enough to rely on

## Working model

The current shipped `_0` branch is best read as:

1. authored/imported FaceGen state is parsed into owner/source structures
2. source/default selection chooses whole objects or descriptor families
3. export builds a temporary object and descriptor family
4. bake consumers use that staged state plus loaded `FREGT003`
5. the remaining mismatch is still not closed

The strongest surviving seams are importer-side and provenance-side, not the
late UI refresh layers we chased more recently.

## Stable function groups

### 0. Writer-family table mapping

- Writer-family symmetry
  Confidence: `High`
  Current role:
  The `0x00D551xx` current-source and `0x00D56Bxx` race/default neighborhoods
  are now confirmed function tables with mirrored importer/writer/updater slots.
  Stable takeaways:
  - current-source side:
    - `0x00D55184 -> FUN_00575D70` importer
    - `0x00D55190 -> FUN_00572360` writer
    - `0x00D551AC -> FUN_005704E0` trailing updater
  - race/default side:
    - `0x00D56B7C -> FUN_00588520` importer
    - `0x00D56B88 -> FUN_00586000` writer
    - `0x00D56BA4 -> FUN_00584180` trailing updater
  - this materially strengthens `FUN_00586000` as the mirrored race/default
    writer-slot peer to `FUN_00572360`
  Not this:
  - the race-only extra leading/trailing slots are not, by themselves, the
    strongest remaining write-side `_0` seam

- `FUN_00573C50`
  Confidence: `High`
  Current role:
  current-source wrapper/destructor slot
  Stable takeaways:
  - body is just `FUN_005739D0(); if (param_1 & 1) free(this);`
  Not this:
  - not a writer
  - not a provenance seam

- `FUN_005875D0`
  Confidence: `High`
  Current role:
  race/default wrapper/destructor slot
  Stable takeaways:
  - body is just `FUN_005873D0(); if (param_1 & 1) free(this);`
  Not this:
  - not a serializer
  - not the race/default float-bank writer

- `FUN_00585820`
  Confidence: `High`
  Current role:
  race/default family destructor/release helper
  Stable takeaways:
  - releases the per-sex `+0xCC/+0x30C` provider families
  - releases the auxiliary `+0x4CC/+0x574/+0x64C` families
  Not this:
  - not a float-bank write stage

- `FUN_00584180`
  Confidence: `High`
  Current role:
  race/default trailing updater / validator
  Stable takeaways:
  - validates and resolves male/female `BGSVoiceType`
  - validates and resolves male/female default `TESHair`
  - validates additional race-owned default form links
  - is the structural race/default peer to current-side `FUN_005704E0`
  Not this:
  - not the missing explicit `FGGS/FGGA/FGTS` writer

- `FUN_004FA790`, `FUN_004FCC20`
  Confidence: `High`
  Current role:
  race-only leading UI/dialog slots
  Stable takeaways:
  - both take `HWND param_1`
  - they behave like dialog populate/apply handlers
  Not this:
  - weak shipped `_0` write-side candidates

- `FUN_004052D0`, `FUN_004F7AF0`, `FUN_004F9760`, `FUN_004F7B70`,
  `FUN_004FCA20`, `FUN_00405300`, `FUN_00405310`
  Confidence: `High`
  Current role:
  shared writer-tail interface/helpers after the mirrored writer slots
  Stable takeaways:
  - `FUN_004052D0` is only an indirect virtual dispatch wrapper:
    `(**(code **)(*ECX + 0x40))()`
  - `FUN_004F7AF0` is a guarded wrapper around
    `(**(code **)(*ECX + 0x48))(param_1)`, with temporary global/editor-state
    setup in one flag-driven branch
  - `FUN_004F9760` and `FUN_004F7B70` are typed order/compatibility predicates
  - `FUN_004FCA20` is metadata/copy-name helper logic, including
    `"COPY%04i"` formatting
  - `FUN_00405300` and `FUN_00405310` are race-only bit/flag accessors
  Not this:
  - not a recovered deferred `FGGS/FGGA/FGTS` emitter
  - if anything meaningful survives here, it would only be inside the concrete
    object-specific implementations behind the `vfunc + 0x40/+0x48` hooks, not
    in these shared wrappers themselves

### 1. NPC importer path

- `FUN_00575D70`
  Confidence: `High`
  Current role:
  NPC/current-source importer for `FGGS`, `FGGA`, `FGTS`, plus neighboring
  tagged record content.
  Stable takeaways:
  - `FGGS -> +0x168`
  - `FGGA -> +0x188`
  - `FGTS -> +0x1A8`
  - the common span path is resize/materialize, raw read, endian handling,
    direct float copy
  - `MAN*` here is mostly ordinary schema/remap/fixup, not a hidden texture
    provenance payload
  - `DATA / DNAM` here is generic schema/import compatibility, not a dedicated
    FaceGen texture-control channel
  Not this:
  - not a hidden bake-time weighting stage
  - not a strong direct explanation for the remaining `_0` residual by itself

- `FUN_00572360`
  Confidence: `High`
  Current role:
  concrete write-side serializer for the NPC/current-source FaceGen family
  Stable takeaways:
  - explicitly emits `FGGS`, `FGGA`, and `FGTS` through
    `thunk_FUN_004F9B70(...)`
  - serializes the neighboring current-source tags first:
    `MANC`, `MAND`, repeated `MANP`, `MANH`, `MANL`, `MANE`, `HCLR`,
    `MANZ`, `MAN4`
  - finishes with `MAN5`, `MAN6`, and `MAN7`
  - structurally matches the NPC/current-source importer branch, not the
    race/default paired-bank branch
  Not this:
  - not the write-side owner of `MAN2`, `MANM`, or `MANF`
  - not the race/default paired-bank serializer we are still missing

### 2. Race/default importer path

- `FUN_00588520`
  Confidence: `High`
  Current role:
  Race/default owner parser-materializer and the only recovered importer that
  can materialize paired `FGTS`-bank-like state.
  Stable takeaways:
  - it directly writes/populates the race/default template families at
    `+0x694/+0x714`
  - its `param_1` now looks closed as the generic tagged-form parser stream
    context rather than a hidden FaceGen provider object
  - `param_1 + 0x25C` is the current subrecord payload byte length
  - the owning dispatch family at `0x00D56B60-0x00D56BB8` still looks like the
    TESRace-backed sibling helper class to the TESNPC current-source family,
    not a detached generic parser node
  - the relevant direct writer-side bridge is now concrete:
    `FUN_00588520` sets record count/stride fields on those template-family
    records and calls `FUN_00573BA0`
  - `FUN_00573BA0` is only the generic float-span resizer, not a hidden
    semantic bridge
    - primary race/default `FGTS` bank is always written
    - sibling `+0x1C8` bank is conditionally mirrored from the same payload
  - mirror guard is currently best read as:
    `(local_20 == 0) || (local_9 != 0)`
  - the best behavioral read of that guard is now:
    - `MANM/MNAM` section always mirrors into `+0x1C8`
    - `MANF/FNAM` section mirrors only if `MAN2` did not clear the side flag
  - `local_20` is best read as `FNAM/MNAM` sex-section selection
  - `local_11` is the separate `MAN0/MAN1` family/mode flag for the later
    `INDX`-driven provider/object path
  - `MAN2` clears `local_9`, but `local_9` is now better read as companion-side
    / subsection-layout flag than as a pure mirror-enable bit because it also
    participates in the later `MAN0`/`INDX` remap
  - writer-side `MAN2` is now best read as effectively unconditional in the
    recovered `FUN_00586000` body and it clearly precedes the recovered
    `MNAM/FNAM` subsection loops
  - that remap dependence is now sharper:
    `local_9` only affects the later `INDX` remap through the
    `local_11 != 0` branch; otherwise `local_20` drives the subsection
    selection directly
  - the surrounding sideband tags now split into:
    - exact or near-exact write/read offset pairs:
      - `ONAM <-> +0x7A0`
      - `YNAM <-> +0x7A4`
      - `VTCK <-> +0x798/+0x79C`
      - `MAND <-> +0xB0/+0xB4`
      - `MANC <-> +0xB8/+0xB9`
    - exact or near-exact write/read offset pairs also now include:
      - `PNAM <-> +0xBC`
      - `UNAM <-> +0xC0`
  - historical note:
    older chronology often used the raw-immediate aliases
    `MANO/MANY/MANP/MANU`; the normalized little-endian subrecord names are
    `ONAM/YNAM/PNAM/UNAM`
  - so `ONAM/YNAM/VTCK/MAND/MANC` now look like ordinary selector/source
    state around `+0x798..+0x7A4` and `+0xB0..+0xB9`, not hidden bank
    selectors
  - importer-side `source + 0x1C8` still looks texture-bank-like:
    it is written from the same decoded float payload loop as `+0x1A8`
  - the paired-bank copy behavior itself is now effectively closed:
    `LAB_00588C06` decodes one float payload and writes it
    - to `+0x1A8` unconditionally
    - to `+0x1C8` only when `(local_20 == 0) || (local_9 != 0)`
  - do not conflate that with the later owner/provider
    `owner + sex * 0x120 + 0x1C8` eye-provider slot
  - `MAN0/MAN1` are no longer best read as bank selectors:
    they select between two serialized provider families that `INDX` then
    materializes directly
    - `MAN0 -> +0xCC/+0x30C`
    - `MAN1 -> +0x4CC/+0x574/+0x64C`
  - `INDX` is now the recovered parser bridge into those owner provider tables
  - `MANM/MANF` select the sex subsection used by those indexed provider writes
  - the post-copy remap destination surface is now concrete:
    - `local_11 == 0` reaches the auxiliary provider families through the
      `+0x574/+0x4CC` expressions
    - `local_11 != 0` reaches the generic provider families through the
      `+0xCC/+0x30C` expressions
  - the auxiliary `+0x4CC/+0x574/+0x64C` families are explicitly constructed,
    copied, and refreshed like the main provider families
  - they also have a real downstream export surface:
    export/bake artifacts read `+0x64C` through `vfunc + 0x34`, prepend
    `"Meshes\\"`, and use the resulting path for asset load/setup
  - so this auxiliary branch can influence ordinary export indirectly, but the
    influence surface still looks provider/path-oriented rather than
    float-bank-oriented
  - the remaining direct paired-bank route is upstream of those families:
    `+0x168/+0x188/+0x1A8/+0x1C8` are the same live-or-inline descriptor family
    ordinary export merges as a whole
  - no recovered later branch re-selects `+0x1A8` vs `+0x1C8` through the
    auxiliary families or through the later eye-provider pair
  - from the consumer side, there is still no recovered path where the
    companion record changes first loaded `FREGT003` span content after
    ordinary export chooses that active descriptor family
  - but that direct route is structurally weaker on actual shipped head EGTs:
    the fourth `0x20` descriptor record is the selector for the second loaded
    `FREGT003` span, and relevant shipped head EGTs are one-span files
    (`sym=50`, `asym=0`)
  Still open:
  - exact semantic meaning of the mirrored companion bank
  - exact shipped meaning of the `local_11 != 0` remap after the copy
  - how, or whether, that paired-bank state seeds later owner/provider slots
  - the exact pairing between the recovered raw `FUN_00586000` tail and the
    importer-side mirror/suppression conditions in `FUN_00588520`
  Current rerank:
  - stronger than any recovered later primary/companion selector
  - the concrete primary/companion divergence still only appears here, at
    import time
  - what gets copied is no longer the open question; the remaining ambiguity is
    the flag semantics around that copy, especially whether `MAN2/local_9`
    still matters directly through upstream descriptor population while the
    later `INDX` remap stays provider-family routing
  - but this whole `MAN2/local_9` branch is now below first-span
    `FREGT003` content/apply fidelity and below upstream provenance of active
    first-span descriptor content
  - and within the remaining GECK-side work, it is now below the alignment
    between always-written primary `+0x1A8` race/default content and the
    loaded `SI.CTL` family-B control owner
  - `FUN_004E1130` itself is no longer an unknown:
    it is only a generic 16-bit reader with optional endian swap
  - the switch-table decode is now exact enough to prove:
    `SNAM -> 0x00588964 -> FUN_004E1130(owner + 0x794)`
  - that demotes `SNAM` toward the same small sideband/discriminator family as
    the neighboring scalar tags rather than a hidden float-bank payload seam

- `FUN_00586000`
  Confidence: `High`
  Current role:
  tagged write/serialize counterpart for the same broad owner/provider record
  family that `FUN_00588520` later parses
  Stable takeaways:
  - serializes `ONAM`, `YNAM`, `MAN2`, `VTCK`, `MAND`, `MANC`, `PNAM`,
    `UNAM`, and `ATTR`
  - the scalar/sideband correspondence is now sharper:
    - `ONAM <-> +0x7A0`
    - `YNAM <-> +0x7A4`
    - `VTCK <-> +0x798/+0x79C`
    - `MAND <-> +0xB0/+0xB4`
    - `MANC <-> +0xB8/+0xB9`
    - `PNAM <-> +0xBC`
    - `UNAM <-> +0xC0`
  - the leading helper cluster at the top of `FUN_00586000` is now mostly
    demoted as generic serializer framing:
    - `FUN_004FB090`: object-header/save-data preamble plus `EDID`-like write
    - `FUN_004FD890`: `FULL` string writer
    - `FUN_004F6A40`: tiny generic wrapper, not a recovered bank emitter
    - `FUN_0050AD00`: repeated `SPLO` list writer
    - `FUN_00508C60`: `MANX` sideband triple writer
    - `FUN_004F8820`: generic `DATA` payload emitter, but not a race/default
      float-bank emitter
  - serializes `MAN0`, then per-sex `MANM/MANF` loops over `+0x30C` with
    `INDX`, `MODL`, `MODT`, `MODD`, and `ICON`
  - serializes `MAN1`, then per-sex `MANM/MANF` loops over `+0x4CC` with
    `INDX`, `ICON`, `MODL`, and `MODT`
  - serializes the linked `+0xA8` family into `MANH`
  - local raw PE recheck now confirms the Ghidra decompile truncates early:
    `FUN_00586000` continues after `MANH` and emits:
    - `ENAM`
    - `MNAM/FNAM`
    - `FGGS`
    - `FGGA`
    - `FGTS`
    - `SNAM`
  - that recovered writer tail now matches the parser-side order-sensitive
    window in `FUN_00588520`:
    - `ENAM` = linked eye-list importer
    - `MNAM/FNAM` = subsection/sex selector state (`local_20`)
    - `FGGS/FGGA/FGTS` = bank payload materialization at `LAB_00588C06`
    - `SNAM` = post-bank 16-bit scalar written to `owner + 0x794`
  - the late writer tail is now also resolved as a staged serializer:
    temp float buffer allocation, copy from writer-side memory, tag emit,
    temp free
  - the copied source spans line up directly with the sex-selected race/default
    template families:
    - `FGGS` copy from `+0x6A0` with stride `+0x6B0`
    - `FGGA` copy from `+0x6C0` with stride `+0x6D0`
    - `FGTS` copy from `+0x6E0` with stride `+0x6F0`
  - those are the exact first/second/third record span fields under the
    family rooted at `+0x694`
  - subsection selection also exposes the matching `+0x714` half through count
    reads like `+0x74C` and `+0x76C`
  Why it matters:
  - together with `FUN_00588520`, this closes most of the “what does the
    writer tail actually serialize?” question
  - the remaining write-side question is no longer hidden emit mechanics; it is
    the earlier path that populates the `+0x694/+0x714` template families
  Not this:
  - the leading helper cluster does not contain a separate hidden
    race/default float-bank writer; the recovered raw tail of
    `FUN_00586000` itself emits the banks
  - no visible slot-6/7-specific asymmetry inside its `+0x30C` loop
  - not an earlier slot-6/7-specific seed writer
  Current rerank:
  - now confirmed as the explicit mirrored race/default `FGGS/FGGA/FGTS`
    writer-slot peer to `FUN_00572360`
  - the shared tail slots after `FUN_00586000` / `FUN_00572360` now rank as
    generic interface/helpers, not the missing continuation
  - the next best exact target is therefore earlier than the writer tail:
    the populate/copy path for the race/default template families at
    `+0x694/+0x714`, especially the third `0x20` record later serialized as
    `FGTS`

- `FUN_00501A30`, `FUN_00502CC0`, `FUN_0050B2B0`, `FUN_0050C0C0`,
  `FUN_0050C9E0`, `FUN_0051CD20`
  Confidence: `High`
  Current role:
  generic helper cluster under the `MAN0/MAN1/INDX` parser branch
  Stable takeaways:
  - `FUN_00501A30` is only the `MODL/MODD/MODS/MODT` tag predicate
  - `FUN_00502CC0` is generic model/path/object materialization
  - `FUN_0050B2B0` is generic sideband object import
  - `FUN_0050C0C0` is the `ICON` predicate
  - `FUN_0050C9E0` is generic string/path metadata import into the `+0x30C`
    peer table
  - `FUN_0051CD20` is tiny linked-list append glue
  Not this:
  - not a hidden FaceGen-specific bank-selector layer
  - not a strong remaining shipped `_0` seam

### 2b. Loaded control-family owner

- `FUN_0085AEB0`
  Confidence: `High`
  Current role:
  loader for the global `SI.CTL` family-B control owner
  Stable takeaways:
  - loads `FACEGEN\\SI.CTL`
  - populates the global control-family owner at `DAT_00F05D54 + 0x118`
  - payload rows are rooted at `+0x644`
  - normalization scales are rooted at `+0x684`
  - writeback coefficients are rooted at `+0xFCC`
  - those families are later consumed by:
    - `FUN_0085CD50`
    - `FUN_0085C110`
    - `FUN_0085CEE0`
  - on the descriptor side, imported `FGTS -> +0x1A8` already lives in the
    same inline `0x80` family that ordinary export merges through
    `FUN_0056F2E0 -> FUN_0056F390 -> FUN_0068EA20`
  Why it matters:
  - this closes the loader side structurally
  - the remaining GECK-side seam is no longer broad basis-space compatibility:
    repo-visible control definitions already treat the shipped family-B rows as
    projections over 50 `FGTS` basis coefficients
  - so the remaining GECK-side seam is narrower:
    upstream population of the sex-selected template-family records that
    `FUN_00586000` later serializes as `FGTS`
  Not this:
  - not a missing owner layer
  - not a late bank selector
  - not a new first-span asymmetry by itself

### 3. Current-source state lifecycle

- `FUN_005721B0`
  Confidence: `High`
  Current role:
  hard reset/reseed of current-source state and inline descriptor

- `FUN_00571CC0`
  Confidence: `High`
  Current role:
  copy/import helper for current-source triplet and active descriptor
  Stable takeaways:
  - copies `+0x1EC/+0x1F0/+0x1F4/+0x20C`
  - clones linked family at `+0x210`
  - copies active descriptor via `FUN_0068E960`
  Not this:
  - does not copy the owner `+0xCC/+0x30C` provider family
  - therefore does not directly explain the later eye-provider pair

- `FUN_005711F0`
  Confidence: `High`
  Current role:
  clamp current `+0x1EC` to the first compatible source candidate

- `FUN_00575290`
  Confidence: `High`
  Current role:
  donor selector/import owner for current-source maintenance
  Stable takeaways:
  - chooses between current selection, explicit donor, or fallback
  - gate `6` eventually calls `FUN_00571CC0`
  Not this:
  - gate `6` is generic donor-side component import, not a texture-control seam

### 4. Descriptor selection and export staging

- `FUN_0056F2E0`
  Confidence: `High`
  Current role:
  choose active source descriptor: live `+0x1E8` else inline `+0x168`

- `FUN_0056F390`
  Confidence: `High`
  Current role:
  merge template + active source family through `FUN_0068EA20`

- `FUN_0068EA20`
  Confidence: `High`
  Current role:
  merge helper for the shared `2 x 2` family
  Not this:
  - not a hidden late selector/bank switch
  - not a per-lane hidden weighting table on the ordinary export path

- `FUN_00586EA0`
  Confidence: `High`
  Current role:
  ordinary export-side staging builder
  Stable takeaways:
  - chooses current `+0x1EC` or fallback `+0xB0/+0xB8`
  - forwards owner sex-slice slot addresses into temp staging
  - `temp + 0xD8 = owner + sex * 0x120 + 0x1A4`
  - `temp + 0xDC = owner + sex * 0x120 + 0x1C8`
  Not this:
  - not a direct `+0x1A8` vs `+0x1C8` importer-bank selector
  - later flow currently looks like forwarding, not primary/companion
    texture-bank choice

### 5. Later owner/provider families

- `FUN_00585630`
  Confidence: `High`
  Current role:
  owner-state reset/default seeder
  Stable takeaways:
  - seeds the per-sex `+0xCC` model-provider family uniformly
  - seeds the paired `+0x30C` family uniformly
  - seeds the race/default template families at `+0x714` and `+0x694` only by
    copying the global default family through `FUN_00690240 -> FUN_0068E960`
    and then calling `FUN_0068E8F0`
  - its visible hardcoded default-path work is concentrated in the separate
    `0x4CC/0x574/0x64C` auxiliary families, not a recovered slot-6/7 special case
  Not this:
  - not the first meaningful population step for `FGGS/FGGA/FGTS`

- `FUN_005875F0`
  Confidence: `High`
  Current role:
  selected-family copy bridge for the active sex-selected template half
  Stable takeaways:
  - reads a selected family entry from the `+0x7AC` list using selector
    `+0x7B2`
  - chooses destination half `+0x714` or `+0x694`
  - copies that selected family into the active sex-selected template half via
    `FUN_0068E960`
  - then updates selection bookkeeping through `FUN_005703B0`
  - the only recovered caller is `FUN_00589F50`, a dialog/UI owner branch
    already demoted elsewhere
  - `FUN_00587670` zero-initializes `+0x7B2` and stores the backing pointer at
    `+0x7AC`, so this stash/pop path starts empty during construction
  Why it matters:
  - this is still the cleanest recovered overwrite path for only the active
    sex-selected template half, but it is now weaker as an ordinary export seam
    until a non-UI stash producer is recovered
  Not this:
  - not the original parser/materializer for the template families
  Still open:
  - whether any non-UI path populates the `+0x7AC` list
  - what exact semantic family `+0x7B2` is selecting if that branch matters

- `FUN_00586740`
  Confidence: `High`
  Current role:
  whole-owner copy/import helper
  Stable takeaways:
  - copies the full `+0xCC/+0x30C` provider family uniformly
  - this family includes the later eye-provider slots ordinary export consumes
  - it RTTI-casts the donor to `TESRace`
  - the previously-attributed addresses `0x00586A2C/0x00586ABA/0x00586B8A/0x00586BF5`
    are internal calls inside `FUN_00586740`, not recovered external callers
  - `0x00586A2C/0x00586ABA` are the stronger internal metadata-holder/path-copy
    subpath through `FUN_00405B40`
  - `0x00586B8A/0x00586BF5` are the weaker linked-family allocation subpath
    through `FUN_008540A0(8)`
  Why it matters:
  - unlike `FUN_00571CC0`, this one really can move the table containing the
    later `+0x1A4/+0x1C8` pair
  Still open:
  - the earlier seed path that fills the copied late `+0xCC` slots before this
    whole-owner copy runs

- `FUN_00584700`
  Confidence: `High`
  Current role:
  live owner-state updater
  Stable takeaways:
  - refreshes `+0xCC/+0x30C` uniformly
  - saves fallback selector state at `+0xB0/+0xB8`
  - the only recovered slot-leaning distinction nearby is late UI/provider
    typing, not an earlier slot-6/7 seed path
  Not this:
  - not the missing importer-to-eye-slot bridge

- `FUN_00584680`
  Confidence: `Medium`
  Current role:
  late provider-type to edit-control mapper
  Stable takeaways:
  - provider type `6 -> 0x646`
  - provider type `7 -> 0x645`
  Interpretation:
  - this is a late UI/provider distinction, not yet an earlier copy/seed
    distinction for the eye-provider pair
  - no recovered slot-6/7-specific writer in its provider-table loop
  - after the `FUN_00586000 <-> FUN_00588520` symmetry pass, slots `6/7` are
    best read as ordinary `MAN0` provider slots that only diverge later in UI
    typing and downstream eye consumption

- `FUN_00589420`
  Confidence: `High`
  Current role:
  late slot-id and UI/provider refresh helper
  Stable takeaways:
  - stamps ids like `0x9D5/0x9D6` and `0x9E5/0x9E6`
  - refreshes existing slot objects through `vfunc + 0x24`
  - repopulates controls
  Not this:
  - not the importer-to-owner copy we were hoping for
  - late scaffolding, not root provenance

- `FUN_00589050`
  Confidence: `High`
  Current role:
  immediate refresh helper around child `0x87F` and `FUN_005880A0`

### 6. Downstream eye-provider consumers

- `FUN_00589F50`
  Confidence: `High`
  Current role:
  large owner-span consumer / dialog owner
  Stable takeaways:
  - `0x9D6 -> owner + sex * 0x120 + 0x1A4`
  - `0x9E6 -> owner + sex * 0x120 + 0x1C8`
  - both dispatch through the same slot-object message interface
  Supporting clue:
  - `0x9D6` has a recovered transient/provider twin in `0x574`
  - `0x9E6` currently does not

- `FUN_00690FF0`
  Confidence: `High`
  Current role:
  post-loop consumer of the staged eye-provider pair
  Stable takeaways:
  - `temp + 0xD8 -> FaceGenEyeLeft`
  - `temp + 0xDC -> FaceGenEyeRight`
  Interpretation:
  - the later owner sex-slice pair is an eye-provider pair, not direct evidence
    about raw importer-side `FGTS` bank semantics

## Demoted branches

These were worth checking, but should no longer drive the search.

- broad parser orientation / simple row-flip theories
- “just BC1/DDX noise” explanations
- `NPC-only` vs `race-only` coefficient source as the main shipped `_0` bug
- `FUN_0085CEE0` as the key upstream provenance seam
- `FUN_004E0740` as a meaningful shipped `_0` seam
- current/fallback hair/eye source-handle helpers as the main texture seam
- `FUN_00589420` as the leading importer-to-eye-provider bridge
- ordinary export helpers as a direct `+0x1A8` vs `+0x1C8` bank selector
- `MAN0/MAN1 -> INDX` as a direct texture-bank selector
- the generic `+0xCC/+0x30C` provider-table family as the main missing
  importer-to-owner bridge

## Runtime-only branch status

The runtime investigation produced useful separation, but it is not the current
highest-priority shipped `_0` branch.

Stable runtime takeaways:

- live runtime `FGGS/FGGA/FGTS` buffers are per-NPC, not shared by pointer identity
- runtime drift for the anchor NPCs is same-family drift, not exact donor-copy reuse
- runtime `LoadFaceGen` and `RandomizeFaceCoord` are real state materializers,
  but they are weaker explanations for the shipped editor/export `_0` mismatch

Keep runtime results as context, not the leading branch.

## Why the current branch still matters

The remaining shipped `_0` gap is not closed by better fitting alone.
That leaves two broad possibilities:

1. we still mis-handle first-span EGT content/apply fidelity
2. the editor/importer/source side is not feeding the same effective state into
   bake that we assume

The current decomp branch is aimed at option 2.
But within option 2, the late UI/provider layer is now mostly supporting
structure. The surviving meaningful earlier copy points are:

- `FUN_00588520` on the importer side
- `FUN_00586740` on the whole-owner provider-family copy side

## Best next targets

1. Stay on the GECK-side primary-bank provenance branch
   Goal:
   determine whether any non-UI overwrite path actually survives after direct
   materialization into the sex-selected race/default template families at
   `+0x694/+0x714`, especially the third `0x20` record later emitted as
   `FGTS`, now that the broad family-B basis-space alignment with
   `FUN_0085AEB0` is mostly closed and the upstream parser context is mostly
   identified.
   Why:
   - the structural bridge is already closed
   - the remaining GECK-side gap is upstream template-family provenance, not missing
     descriptor structure or generic control-row compatibility
   Exact next target:
   - any non-UI stash producer for `+0x7AC/+0x7B2`
   - or any other later non-UI overwrite that can still replace the active
     sex-selected half before `FUN_00586000`
   - otherwise, demote the remaining GECK branch below first-span
     `FREGT003` content/apply fidelity
     serializes it

2. Keep `FUN_00588520` as the upstream importer anchor, but narrow the open
   question
   Goal:
   preserve the paired-bank mirror semantics while treating `MAN2/local_9`
   only as a secondary upstream descriptor-population check.
   Secondary:
   keep `SNAM -> +0x794` and the `PNAM/UNAM` sideband family as resolved small
   scalar state, not the main open seam.

3. Revisit the auxiliary provider families only if the primary-bank/control-row
   alignment branch stalls
   Goal:
   confirm whether any remaining `+0x4CC/+0x574/+0x64C` influence matters for
   shipped `_0` beyond provider/object/path selection.
   Current result:
   this surface is real but weak as the main shipped `_0` seam.

4. If the GECK-side branch stops moving, step back to the stronger competing
   branch
   Goal:
   return to first-span `FREGT003` content/apply fidelity, which still outranks
   the local `MAN2/local_9` asymmetry window overall.

## What is still missing from our docs

We still do not have a single hand-cleaned pseudo-C/spec file for the critical
functions with stable field names.

Current maintained layers are:

- raw focused decompile artifacts in `tools/GhidraProject`
- short research notes in `research/facegen_research_artifacts`
- short maintained state in [facegen_head_rendering_current_status.md](/c:/Users/mmc99/source/repos/Xbox360MemoryCarver/docs/facegen_head_rendering_current_status.md)
- long chronology in [facegen_head_rendering_pipeline.md](/c:/Users/mmc99/source/repos/Xbox360MemoryCarver/docs/facegen_head_rendering_pipeline.md)

This file is intended to be the missing middle layer: a stable, function-level
map that stops the investigation from living only in chronology.
