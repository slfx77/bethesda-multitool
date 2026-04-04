# FaceGen Head Rendering Current Status

Last updated: 2026-04-04

This is the short maintained status page for the FaceGen investigation.

Recommended read order:

1. this file
2. [facegen_head_rendering_function_map.md](/c:/Users/mmc99/source/repos/Xbox360MemoryCarver/docs/facegen_head_rendering_function_map.md)
3. [facegen_head_rendering_pipeline.md](/c:/Users/mmc99/source/repos/Xbox360MemoryCarver/docs/facegen_head_rendering_pipeline.md) for chronology only

## Current state

- The broad FaceGen head pipeline is structurally understood at a useful level:
  - `FREGT003` schema
  - GECK/editor bake structure
  - runtime apply structure
  - shader slot binding and main FaceGen texture path
- End-to-end shipped `_0` parity is still not closed.
- The remaining shipped `_0` mismatch is not well explained by:
  - BC1/DDX/DDS encode noise alone
  - broad row-flip/parser-layout bugs
  - a simple NPC-only or race-only coefficient-source mistake

## Stable conclusions

- Relevant shipped head EGTs are first-span, full-frame files:
  - `256 x 256`
  - `sym=50`
  - `asym=0`
- The current/fallback source helper trio is now largely closed:
  - `FUN_00419C80` searches combo item-data for an existing handle
  - `FUN_00419CE0` returns combo item-data for an explicit index
  - `FUN_00419D10` returns combo item-data for the current selection
  - so these are generic combo helpers, not the concrete source-handle family
- The current/fallback source-handle branch is now structurally narrower than
  it looked earlier:
  - `+0x1EC` is the current selected hair-like source object
  - `+0x1F4` is the current selected eye-color-like source object
  - `FUN_0056A310` validates candidates against `owner->(+0x144)+0xA8` plus
    sex/index bits at `candidate + 0x78`
  - `FUN_00567420` is the eye-color sibling against `owner->(+0x144)+0xC4`
    plus bits at `candidate + 0x54`
  - this now looks more like hair/eye wrapper-family validation than the core
    remaining shipped `_0` FGTS seam
- The shared `FGGS / FGGA / FGTS` import block in `FUN_00575D70` is now largely
  closed:
  - resize/materialize span
  - raw read
  - endian swap if needed
  - direct float copy
- The structural bridge between imported primary race/default `FGTS` content
  and the loaded family-B control owner is now mostly closed:
  - `FUN_00588520` always writes the decoded primary race/default payload to
    `+0x1A8`
  - `+0x168/+0x188/+0x1A8/+0x1C8` are four consecutive `0x20` records in the
    same inline `0x80` descriptor family
  - ordinary export consumes that whole family through
    `FUN_0056F2E0 -> FUN_0056F390 -> FUN_0068EA20`
  - `FUN_0085AEB0` loads the `SI.CTL` family-B control owner at
    `DAT_00F05D54 + 0x118`, including payload rows at `+0x644`, scales at
    `+0x684`, and writeback coefficients at `+0xFCC`
  - so the remaining GECK-side gap is semantic pairing/alignment, not a
    missing structural bridge
- That semantic gap is narrower than it looked:
  - the shipped `SI.CTL` texture-symmetric family is a `33 x 50` control matrix
  - repo-generated `FaceGenTextureSymmetricData` already treats those rows as
    projections over 50 `FGTS` basis coefficients
  - `ComputeTextureSymmetric(...)` explicitly computes named control values as
    dot products against a 50-wide `FGTS` vector
  - so imported primary `+0x1A8` content and loaded family-B rows already look
    like the same 50-dimensional `FGTS` basis space
  - the remaining GECK-side question is now more specifically the upstream
    population of the sex-selected template families at `+0x694/+0x714` that
    `FUN_00586000` later serializes through temp buffers
- `MAN*` in `FUN_00575D70` is mostly ordinary `*NAM` schema and remap/fixup.
- `DATA / DNAM` in `FUN_00575D70` is generic schema/import compatibility, not a
  dedicated FaceGen provenance payload.
- Write-side symmetry around `FUN_00586000` is now partially pinned down:
  - it serializes the same broad owner/provider neighborhood as
    `FUN_00588520` reads from
  - the late recovered `FGGS/FGGA/FGTS` tail is now sharper:
    it allocates temp float buffers, copies from template-family spans, emits
    the tag, and frees the temp buffer again
  - the raw copy sources match the sex-selected template-family record layout:
    - `FGGS` from `+0x6A0/+0x6B0`
    - `FGGA` from `+0x6C0/+0x6D0`
    - `FGTS` from `+0x6E0/+0x6F0`
  - those offsets are the exact first/second/third `0x20` records under the
    race/default template family rooted at `+0x694`, with subsection selection
    also showing the matching `+0x714` half through counts like
    `+0x74C/+0x76C`
  - there is still no recovered slot-6/7-specific asymmetry inside that
    write-side path
- The template-family population ladder is now clearer too:
  - `FUN_00585630` is still only reset/default seed for `+0x694/+0x714`
  - `FUN_00588520` is the direct parser/materializer into those template
    families, using `FUN_00573BA0` only as a span-resize helper
    and `param_1` there now looks closed as the generic tagged-form parser
    stream context, with `+0x25C` as the current subrecord payload byte length
    while the owning `0x00D56B60-0x00D56BB8` table still looks like a
    TESRace-backed helper-family slice rather than a hidden FaceGen-only
    provider layer
  - `FUN_005875F0` is the first recovered targeted overwrite/copy path for the
    active sex-selected half:
    it copies one selected family from `+0x7AC` into `+0x694` or `+0x714`
    through `FUN_0068E960`
    but the only recovered caller is `FUN_00589F50`, a dialog/UI owner already
    demoted elsewhere, and the stash starts zeroed in `FUN_00587670`
  - `FUN_00586740` is the broader donor copy path that copies both template
    halves wholesale from a donor owner
  - so the next exact upstream GECK question is no longer whether
    `FUN_00588520` directly materializes `+0x694/+0x714`; it does
  - the `+0x7AC/+0x7B2` stash branch now looks weaker as an ordinary export
    seam unless a non-UI filler is recovered
  - no stronger recovered non-UI overwrite of `+0x694/+0x714` has surfaced
    after `FUN_00588520`
- The surrounding race/default sideband tags are now more sharply split:
  - exact or near-exact write/read offset pairs:
    - `ONAM <-> +0x7A0`
    - `YNAM <-> +0x7A4`
    - `VTCK <-> +0x798/+0x79C`
    - `MAND <-> +0xB0/+0xB4`
    - `MANC <-> +0xB8/+0xB9`
  - exact or near-exact write/read offset pairs now also include:
    - `PNAM <-> +0xBC`
    - `UNAM <-> +0xC0`
- Historical note:
  older chronology often used the raw-immediate aliases
  `MANO/MANY/MANP/MANU`; the normalized little-endian subrecord names are
  `ONAM/YNAM/PNAM/UNAM`.
- So the `ONAM/YNAM/VTCK/MAND/MANC` cluster is now best read as ordinary
  source/provider sideband state around the importer neighborhood, not another
  hidden float-bank family. `PNAM/UNAM/SNAM` now all look like ordinary small
  sideband/discriminator scalars rather than hidden post-bank float content.
- The leading helper cluster at the top of `FUN_00586000` is now mostly
  demoted as generic serializer framing:
  - `FUN_004FB090` = object-header/save-data preamble plus `EDID`-like output
  - `FUN_004FD890` = `FULL` string writer
  - `FUN_004F6A40` = tiny generic wrapper, not a recovered bank emitter
  - `FUN_0050AD00` = repeated `SPLO` list writer
  - `FUN_00508C60` = `MANX` sideband triple writer
  - `FUN_004F8820` = generic `DATA` payload emitter
- So that top helper cluster does not contain the missing race/default
  float-bank writer.
- A concrete explicit `FGGS/FGGA/FGTS` writer is now recovered:
  - `FUN_00572360` emits `FGGS`, `FGGA`, and `FGTS` through
    `thunk_FUN_004F9B70(...)`
  - but it writes the NPC/current-source family, not the race/default paired
    bank path
  - it also emits the neighboring current-source tags `MANC`, `MAND`,
    repeated `MANP`, `MANH`, `MANL`, `MANE`, `HCLR`, `MANZ`, `MAN4`,
    followed by `MAN5`, `MAN6`, and `MAN7`
  - importantly, it does not emit `MAN2`, `MANM`, or `MANF`
- The writer-family tables themselves are now concrete:
  - current-source family:
    - importer slot `0x00D55184 -> FUN_00575D70`
    - writer slot `0x00D55190 -> FUN_00572360`
    - trailing updater slot `0x00D551AC -> FUN_005704E0`
  - race/default family:
    - importer slot `0x00D56B7C -> FUN_00588520`
    - writer slot `0x00D56B88 -> FUN_00586000`
    - trailing updater slot `0x00D56BA4 -> FUN_00584180`
  - newly resolved non-writer slots:
    - `FUN_00573C50` is the current-source wrapper/destructor around
      `FUN_005739D0`
    - `FUN_005875D0` is the race/default wrapper/destructor around
      `FUN_005873D0`
    - `FUN_00585820` is the race/default provider-family destructor/release
      helper, not a float-bank writer
    - `FUN_00584180` validates race defaults like male/female voice type and
      default hair, rather than writing `FGGS/FGGA/FGTS`
    - `FUN_004FA790` and `FUN_004FCC20` are race-only leading dialog
      populate/apply slots, not strong shipped `_0` write-side candidates
- So the write-side branch is narrower now:
  - the project has an explicit current-source `FGGS/FGGA/FGTS` writer
  - the race/default writer sibling is now also explicit:
    a local raw PE recheck confirms `FUN_00586000` continues past the
    truncated Ghidra tail and emits `ENAM`, `MNAM/FNAM`, `FGGS`, `FGGA`,
    `FGTS`, then `SNAM`
  - that late writer tail now lines up directly with the parser-side
    order-sensitive window in `FUN_00588520`:
    - `ENAM` is the linked eye-list importer
    - `MNAM/FNAM` set the subsection selector `local_20`
    - `FGGS/FGGA/FGTS` all route into `LAB_00588C06`
    - `SNAM` is the recovered post-bank 16-bit scalar at `owner + 0x794`
  - the shared tail slots after `FUN_00572360` / `FUN_00586000` are now
    ranked as generic shared object-interface helpers, not deferred
    `FGGS/FGGA/FGTS` continuation:
    - `FUN_004052D0` and `FUN_004F7AF0` are only weak indirect virtual hooks
      (`vfunc + 0x40` / `vfunc + 0x48`)
    - `FUN_004F9760` and `FUN_004F7B70` are typed order/compatibility
      predicates
    - `FUN_004FCA20` is metadata/copy-name helper logic
    - `FUN_00405300` and `FUN_00405310` are race-only bit/flag accessors
  - so the strongest remaining write-side seam is no longer “find the missing
    race/default writer” or “follow the shared tail slots”
  - it is importer-side paired-bank population/selection around
    `FUN_00588520`, and only secondarily any concrete object-specific
    implementations behind the shared virtual hooks if that branch has to be
    reopened
- The `MAN0/MAN1/INDX` helper cluster is now also mostly closed and generic:
  - `FUN_00501A30` is only the `MODL/MODD/MODS/MODT` tag predicate
  - `FUN_00502CC0` is generic model/path/object materialization
  - `FUN_0050B2B0` is generic sideband object import
  - `FUN_0050C0C0` is the `ICON` predicate
  - `FUN_0050C9E0` is generic string/path metadata import into the `+0x30C`
    peer table
  - `FUN_0051CD20` is tiny linked-list append glue
- So the generic provider-table helper branch is now a weak shipped `_0`
  suspect; the stronger remaining tags on this symmetry branch are still
  `MAN2`, then the small scalar sideband family only as a weaker follow-up.

## Strongest remaining shipped `_0` seams

- NPC importer vs race/default importer state split:
  - NPC/current-source path imports only the primary active `FGTS` bank
  - race/default sibling is the only recovered importer that can materialize
    paired banks at `+0x1A8` and `+0x1C8`
- The paired-bank routing is now mostly concrete inside `FUN_00588520`:
  - primary race/default `FGTS` record is always populated
  - the sibling `+0x1C8` record is mirrored only when the importer-local guard
    `(local_20 == 0) || (local_9 != 0)` holds
  - that guard is now best read behaviorally as:
    - `MANM/MNAM` section always mirrors into `+0x1C8`
    - `MANF/FNAM` section mirrors only if `MAN2` did not clear the side flag
  - the sibling is now best read as an optional mirrored companion bank from
    the same race/default source state, not an independently authored second
    `FGTS` payload
  - `local_20` is now best read as the `FNAM/MNAM` sex-section selector, not a
    generic bank-mode flag
  - `MAN2` clears `local_9`, but `local_9` now looks broader than a pure
    mirror-enable bit:
    it also participates in the later `MAN0`/`INDX` remap, so the safest
    current reading is companion-side / subsection-layout flag with a direct
    mirror-suppression effect on subsection `MANF`
  - that remap split is now tighter:
    - `local_11` is the separate `MAN0/MAN1` family/mode flag
    - `local_9` only leaks into the later `INDX` remap through the
      `local_11 != 0` branch
    - the destination surface is now concrete:
      - `local_11 == 0` lands in the auxiliary provider families
        `+0x4CC/+0x574`
      - `local_11 != 0` lands in the generic provider families
        `+0xCC/+0x30C`
  - what gets copied is now effectively closed:
    `LAB_00588C06` decodes one float payload and writes it to the primary bank
    unconditionally and to the companion bank conditionally
  - `MAN2` remains the strongest still-open tag on this symmetry branch:
    its behavioral effect is clear, but its exact semantic label is not
  - writer-side `MAN2` now also looks effectively unconditional in the
    recovered `FUN_00586000` body and it precedes the recovered
    `MNAM/FNAM` subsection work, which strengthens the default read that
    subsection-1 mirror suppression is the normal race/default case
  - the small post-bank scalar sideband family is now mostly closed:
    `PNAM <-> +0xBC`, `UNAM <-> +0xC0`, and `SNAM -> +0x794`
  - `ENAM` is now structurally resolved enough to treat as orthogonal:
    it imports the eye list/family and does not currently look like the
    bank-routing seam
  - `SNAM` is now exact enough to stop treating its destination as open:
    the switch dispatch at `0x005887E0` maps
    `0x4D414E53 ('SNAM') -> idx 7 -> 0x00588964`, and that handler does the
    direct-field `FUN_004E1130(owner + 0x794)` read
  - `FUN_004E1130` itself is now structurally resolved:
    it is only a generic 2-byte payload reader with optional endian swap,
    so it no longer hides any meaningful `SNAM` semantics by itself
  - `SNAM -> +0x794` now looks like another small owner sideband /
    discriminator scalar, because `+0x794` is already known elsewhere as
    copied/reset/compared owner state rather than lane-bearing float content
  - `MAN0 / MAN1` flip a separate `local_11` flag used later in the `INDX`
    sideband branch, which now looks like provider/object-family routing rather
    than a texture-bank selector
  - importer-side `source + 0x1C8` should still be treated as texture-bank-like
    companion float state from the same decoded payload loop
  - do not conflate that with the later owner/provider slot
    `owner + sex * 0x120 + 0x1C8`, which stays on the eye-provider branch
- Ordinary export is now structurally demoted as a direct bank selector:
  - `FUN_0056F2E0` selects a whole source descriptor (`+0x1E8` else `+0x168`)
  - `FUN_0056F390` and `FUN_0068EA20` merge the full `2 x 2` family, not one bank
  - `FUN_00586EA0` chooses either current source `+0x1EC` or the sex-selected
    fallback handle from race-owned slots `+0xB0/+0xB8`
  - the paired staging pointers are now better read as owner sex-slice
    slot objects:
    `temp + 0xD8 = owner + sex * 0x120 + 0x1A4`
    `temp + 0xDC = owner + sex * 0x120 + 0x1C8`
  - `FUN_00589F50` now shows those offsets are peer members of an eight-slot
    per-sex owner slice, all dispatched through the same `vfunc + 0x1C`
    message interface
  - this means export is forwarding owner-carried slot-object addresses from
    the race/default sex slice directly
  - the shared post-loop consumer `FUN_00690FF0` then consumes:
    `temp + 0xD8 -> FaceGenEyeLeft`
    `temp + 0xDC -> FaceGenEyeRight`
  - so the owner sex-slice `+0x1A4/+0x1C8` pair is now best read as the
    left-eye / right-eye provider pair, not as direct evidence about raw
    `FGTS` bank semantics
  - the earlier `FUN_00589F50` asymmetry still matters as supporting
    structure:
    `0x9D6` participates in the transient `0x574` provider-family branch,
    while `0x9E6` currently does not
  - current rerank:
    importer-side companion-bank population in `FUN_00588520` is stronger than
    any recovered later primary/companion selector, because the later flow
    still looks like forwarding into the eye-provider pair rather than a bank
    choice
- The late eye-provider family is now also structurally cleaner:
  - the per-sex `+0xCC` model-provider family is seeded, copied, and refreshed
    uniformly across all eight slots in `FUN_00585630`, `FUN_00586740`, and
    `FUN_00584700`
  - `FUN_00586740` itself is now cleaner too:
    the previously-attributed addresses `0x00586A2C/0x00586ABA/0x00586B8A/0x00586BF5`
    are internal calls inside `FUN_00586740`, not recovered external callers
  - of those internal sites:
    - `0x00586A2C/0x00586ABA` are the stronger metadata-holder/path-copy
      subpath through `FUN_00405B40`
    - `0x00586B8A/0x00586BF5` are the weaker linked-family allocation subpath
      through `FUN_008540A0(8)`
  - there is no recovered constructor/copy/update special case for slots `6`
    or `7` that explains the later `+0x1A4/+0x1C8` eye split by itself
  - the same negative result now also holds on the nearby seed/update helpers:
    - `FUN_00585630` is still a uniform default seeder, with its visible
      hardcoded path defaults concentrated in the separate
      `0x4CC/0x574/0x64C` auxiliary families
    - `FUN_00584700` still refreshes the whole `+0xCC/+0x30C` table through
      one uniform loop and writes `+0xB0/+0xB8`, but does not expose a
      slot-6/7-specific writer
  - there is still no recovered direct bridge from importer-side
    source-object `+0x1A8/+0x1C8` paired-bank state into the later owner
    eye-provider pair
  - `FUN_00589420` is now demoted from bridge candidate to late UI/provider
    refresh helper:
    it stamps control ids into the already-existing slot records and refreshes
    them through `vfunc + 0x24`, but it does not yet show the missing
    importer-to-provider copy
  - `FUN_00584680` adds only a late provider-type distinction here:
    type `6 -> 0x646`, type `7 -> 0x645`
    so the current slot-leaning difference is still UI/provider typing, not a
    recovered earlier slot-6/7 seed/copy split
  - the provider-family bridge is now mostly closed for the provider tables
    themselves:
    - `FUN_00586000` is the tagged write/serialize counterpart
    - `FUN_00588520`'s `MAN0/MAN1/INDX` path is the matching parser/materializer
  - specifically:
    - `MAN0 -> +0xCC/+0x30C`
    - `MAN1 -> +0x4CC/+0x574/+0x64C`
    - `MANM/MANF` select sex subsection
    - `INDX` instantiates the indexed slot objects and peer-table payloads
      directly, including `FUN_00405B40(...)` on the `+0x30C` peer table
  - the auxiliary `MAN1` families are no longer dead side state:
    - they are explicitly constructed, copied, and refreshed like the main
      provider families
    - export/bake-side artifacts also read `+0x64C` directly through
      `vfunc + 0x34`, prepend `"Meshes\\"`, and use the result as an asset path
    - so they can influence ordinary export indirectly through provider/path
      selection
    - but this still looks provider-side, not like a direct continuation of
      the paired `FGTS` bank semantics at `+0x1A8/+0x1C8`
  - the remaining direct paired-bank route, if it exists, is upstream of those
    provider families:
    - `+0x168/+0x188/+0x1A8/+0x1C8` are the same live-or-inline descriptor
      family ordinary export merges as a whole
    - no recovered later branch re-selects `+0x1A8` vs `+0x1C8` through
      `+0x4CC/+0x574/+0x64C` or through the later eye-provider pair
    - so `MAN2/local_9` remains plausible only as an upstream descriptor-
      population seam, not as a later provider-routing seam
    - and even there it is weaker than before:
      no recovered consumer-side path shows the companion record changing
      first loaded `FREGT003` span content after ordinary export chooses the
      active descriptor family
  - so the old “missing bridge into the late provider family” is now largely
    resolved for the provider tables
  - this demotes the slot-6/7 eye-provider search as the main shipped `_0`
    seam:
    slots `6/7` are now best read as ordinary members of the generic `MAN0`
    provider family, later distinguished by late type/UI routing and
    downstream eye consumption
  - the source-object `+0x1A8/+0x1C8` xref surface is also weaker now:
    in the recovered GECK/editor artifact set, the concrete writes are still
    dominated by `FUN_00588520`, while later export/consumer flow uses
    owner-carried slot objects instead
  - the companion-record branch is weaker again once the bake-visible selector
    role is combined with actual head EGT structure:
    - the fourth `0x20` descriptor record is the selector for the second loaded
      `FREGT003` span
    - relevant shipped head EGTs are one-span files (`sym=50`, `asym=0`)
    - so suppressing the companion `+0x1C8` record is now structurally weak as
      a direct shipped `_0` bake lever
    - this now demotes `MAN2/local_9` below first-span content/apply fidelity
      and below upstream provenance of active first-span descriptor content
  - `FUN_00571CC0` is also now a weaker direct candidate for that copy:
    it only copies current-source state and the active descriptor block
  - `FUN_00586740` is still the stronger recovered earlier whole-owner copy
    helper on this branch, because it copies the whole `+0xCC/+0x30C`
    provider family from a `TESRace` donor, and that family contains the later
    eye-provider slots
  - but the old “follow callers of `FUN_00586740`” rerank is now weakened,
    because the addresses we had been chasing turned out to be internal calls
    inside `FUN_00586740` itself
- The fallback/current-source handle side is now supporting context, not the
  lead `_0` seam:
  - `FUN_00586740` proves the donor for `+0xB0/+0xB8` can be `TESRace`
  - `+0xB0` is still the fallback provider/source-object handle slot
  - `+0xB8` is a companion sideband selector/variant byte
  - but this branch is now more strongly associated with hair/eye source-object
    validation than with the core FGTS bank discrepancy
- First-span hotspot-family residuals remain real even after improved fitting.
- The residual is localized and mixed:
  - mouth-heavy component
  - whole-face / eye-support component
- The writer-side ambiguity is now materially reduced:
  - `FUN_00586000` itself is the explicit race/default writer sibling and its
    raw tail already reaches `ENAM -> MNAM/FNAM -> FGGS -> FGGA -> FGTS -> SNAM`
  - so the strongest remaining seam is no longer another alternate writer hunt
  - the local parser-side flag-lifetime window inside `FUN_00588520`
    (`MAN2 -> MNAM/FNAM -> LAB_00588C06 -> SNAM`) is now a narrower secondary
    GECK-side branch, not the top remaining one

## Demoted branches

- `MAN*` as a hidden current-source `_0` channel
- `DATA / DNAM` as a FaceGen-specific texture-control payload
- `FUN_0085CEE0` as the best immediate provenance target
- `FUN_004E0740` as a meaningful shipped `_0` provenance seam
- ordinary export merge helpers as a direct `+0x1A8` vs `+0x1C8` bank selector
- `MAN0 / MAN1 -> INDX` as a likely direct texture-bank selector
- the generic `+0xCC/+0x30C` provider-table family as the main missing
  importer-to-owner bridge
- `FUN_00589420` as the leading importer-to-eye-provider bridge hypothesis
- broad parser orientation / row-stride theories
- “just compression noise” explanations
- the current/fallback source-handle family as the primary shipped `_0` seam

## Best next targets

1. Stay on the race/default importer side:
  - if staying on GECK decomp, prioritize upstream provenance of active
    first-span descriptor content over more consumer-side paired-bank routing
  - the sharper GECK-side target is now:
    the populate/copy path for the sex-selected race/default template families
    at `+0x694/+0x714`, especially the third `0x20` record later serialized as
    `FGTS`
  - the basis-space part of that question is now mostly closed:
    the next exact decomp target is no longer the raw emit tail after
    `MNAM/FNAM`; it is the earlier helper/copy path that populates the spans
    that tail serializes
  - the new strongest exact sub-target on that branch is:
    whether any non-UI stash producer for `+0x7AC/+0x7B2`, or any other later
    non-UI overwrite, actually exists; without that, the stronger remaining
    branch shifts back to first-span `FREGT003` content/apply fidelity
  - exact effect of `MAN2`, `MNAM/FNAM`, and the mirror guard in
    `FUN_00588520` now ranks as a narrower secondary branch
  - highest-value next target:
    prove a non-UI overwrite of `+0x694/+0x714` after `FUN_00588520`, or
    otherwise demote the remaining GECK branch below first-span `FREGT003`
    content/apply fidelity
  - `SNAM -> +0x794` is now effectively resolved and looks like a small
    sideband/discriminator scalar, not the strongest remaining `_0` seam
  - the sharpest remaining `MAN2/local_9` sub-question is now only:
    whether subsection-1 mirror suppression changes upstream descriptor
    population in a first-span-visible way despite the now-weak fourth-record
    route
  - the ordinary-export consumer surface for the auxiliary
    `+0x4CC/+0x574/+0x64C` families is now partially resolved:
    it can influence export indirectly through provider/object/path selection,
    but currently looks weak as the main shipped `_0` seam
  - the exact semantics of the flags around the importer-side paired-bank
    state at source-object `+0x1A8/+0x1C8`
  - treat the `PNAM/UNAM/SNAM` scalar sideband family as mostly resolved and
    not the first stop on this branch
  - the short note for this rerank is:
    [codex_geck_primary_bank_control_alignment_rerank_resolution.txt](/c:/Users/mmc99/source/repos/Xbox360MemoryCarver/research/facegen_research_artifacts/notes/geck/codex_geck_primary_bank_control_alignment_rerank_resolution.txt)
  - and the basis-space follow-up note is:
    [codex_geck_fgts_basis_space_alignment_resolution.txt](/c:/Users/mmc99/source/repos/Xbox360MemoryCarver/research/facegen_research_artifacts/notes/geck/codex_geck_fgts_basis_space_alignment_resolution.txt)
2. Recover the bridge, if any, from that importer-side paired-bank state into
   the later owner/provider eye-slot family:
   - `owner + sex * 0x120 + 0x1A4`
   - `owner + sex * 0x120 + 0x1C8`
   - but treat the generic provider-table bridge itself as mostly resolved via
     `FUN_00586000 <-> FUN_00588520`
   - and treat broad xref hunting on source-object `+0x1A8/+0x1C8` as a weaker
     branch unless a new direct consumer turns up
3. Recover the concrete semantic role of late `MAN0` slots `6/7`, not another
   generic provider-table seed/copy pass
4. Treat the owner sex-slice pair as resolved supporting structure:
  - `+0x1A4 -> temp + 0xD8 -> FaceGenEyeLeft`
  - `+0x1C8 -> temp + 0xDC -> FaceGenEyeRight`
5. Keep the transient `0x574` twin mismatch as a secondary structural clue:
  - `0x9D6` currently has a recovered transient/provider twin
  - `0x9E6` currently does not
6. Keep the hair/eye source-handle branch only as supporting context for
   current-source validation, not as the leading shipped `_0` hypothesis.

## Latest rerank

- Three independent subagent reads plus a local reread converged on the same
  update:
  - primary race/default `+0x1A8` provenance plus `SI.CTL` family-B control
    alignment is now the stronger GECK-side seam
  - `MAN2/local_9` remains real, but only as a narrower upstream
    descriptor-population check
  - first-span `FREGT003` content/apply fidelity still remains the stronger
    competing branch overall

## Scope note

- Shipped `_0` parity and loaded-runtime-NPC parity are related but not the same
  problem.
- The current highest-priority branch above is the shipped `_0` branch.
