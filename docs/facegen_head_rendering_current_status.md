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
  - the focused write-site scan still only found:
    `FUN_00587670` constructor/reset init, `FUN_005875F0` stash/pop consume,
    and the already-demoted UI/dialog caller `FUN_00589F50`
  - no stronger recovered non-UI overwrite of `+0x694/+0x714` has surfaced
    after `FUN_00588520`
  - a direct stash write-site reread still only shows constructor/reset plus
    the UI/dialog stash-pop path
  - the focused stash-fill scan in this pass still only shows constructor
    zeroing plus the UI stash/pop consumer, so the remaining GECK branch is
    weaker again
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
- A direct repo-side accumulator probe just demoted one nearby first-span
  sub-branch:
  - an experimental per-morph fixed-point weight truncation mode was tested
    against `0x0001816A` and `0x000181D2`
  - it worsened `0x0001816A`, produced only noise-level raw improvement on
    `0x000181D2`, and worsened the real trunc-encoded path overall
  - so early native-delta truncation near the current `EngineTruncated256`
    accumulator is now a weaker candidate than per-basis morph
    content/materialization fidelity or subtler coefficient provenance within
    the same late hotspot family
- A direct same-race/same-sex authored provenance-family scan just demoted the
  broad static provenance branch again:
  - new `verify-egt --raw-fit-prov-family` results:
    - `0x0001816A`: `currentRawMAE=3.3268`, `familyRawMAE=3.3258`,
      `rawFitRawMAE=2.2061`, explained share `0.1%`
    - `0x000181D2`: `currentRawMAE=2.9462`, `familyRawMAE=2.9538`,
      `rawFitRawMAE=1.9213`, explained share `-0.7%`
  - on both anchors, the best same-family candidate leaves the late hotspot
    rows `[35,36,37,38,39,40,41,42,43,45,46,49]` unchanged relative to the
    current merged coefficients, while unrestricted raw fit still wants large
    movement there
  - that makes static same-race/same-sex authored FGTS provenance a weak
    explanation for the anchor mismatch
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

Direct rerank before the numbered list:

- do not prioritize more near-neighbor early truncation variants in the repo
  native-delta accumulator
- if returning to repo-side code first, prioritize:
  - first-span per-basis morph content/materialization fidelity in the late
    hotspot family
  - then only subtler coefficient/source-state provenance inside that same
    family after the stronger content/materialization checks

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
    now mostly closed:
    no non-UI stash producer for `+0x7AC/+0x7B2` was recovered in the focused
    write-site scan, so the stronger remaining branch shifts back to first-span
    `FREGT003` content/apply fidelity
  - exact effect of `MAN2`, `MNAM/FNAM`, and the mirror guard in
    `FUN_00588520` now ranks as a narrower secondary branch
  - highest-value next target:
    first-span `FREGT003` content/apply fidelity
  - if staying on GECK anyway:
    raw xref/write-site work for a still-unrecovered non-UI overwrite of
    `+0x694/+0x714`, but that is now a secondary branch
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
    alignment remains the surviving GECK-side seam, but is weaker than the
    confirmed first-span row-content/materialization branch
  - `MAN2/local_9` remains real, but only as a narrower upstream
    descriptor-population check
  - first-span `FREGT003` row-content/materialization fidelity still remains
    the stronger competing branch overall
- A direct repo-side probe also now rules out one nearby arithmetic sub-branch:
  - experimental per-morph combined-weight truncation in the native delta
    accumulator failed on the anchors
  - so the stronger repo-side branch is no longer “try another nearby early
    truncation mode”
  - it is first-span per-basis morph content/materialization fidelity, with
    subtler coefficient provenance still secondary inside the same late hotspot
    family
- A follow-up repo-side oracle-fit discriminator now demotes coefficient
  quantization itself:
  - the new unquantized joint-RGB first-span oracle fit barely improves over
    quantized `RAWFIT` on either anchor
  - `0x0001816A`: `fitRawMAE 2.2061 -> 2.2051` while encode-loss stays `0.4701`
  - `0x000181D2`: `fitRawMAE 1.9213 -> 1.9204` while encode-loss stays `0.4957`
  - encoded RGB movement is also noise-level:
    `1.0925 -> 1.0939` on `0x0001816A`, `0.9369 -> 0.9348` on `0x000181D2`
  - so coefficient rounding/selection inside the current joint-RGB fit is now
    materially weaker than first-span per-basis content/materialization
    fidelity
  - short note:
    [codex_egt_float_oracle_rerank_resolution.txt](/c:/Users/mmc99/source/repos/Xbox360MemoryCarver/research/facegen_research_artifacts/notes/egt/codex_egt_float_oracle_rerank_resolution.txt)
- A fixed-coefficient late-hotspot row-backsolve probe strengthens the same
  branch one more step:
  - using `--inspect-morph [35,36,37,40,42,49]`, several single-row content
    corrections nearly eliminate raw residual while staying mostly inside legal
    signed-byte range
  - strongest rows:
    - `0x0001816A`: `[37]`, `[35]`, `[36]`, `[42]`
    - `0x000181D2`: `[37]`, `[40]`, `[35]`, `[42]`, `[49]`
  - examples:
    - `0x0001816A [37]`: `inRange=99.9%`, `rowClampRawMAE=0.0026`
    - `0x000181D2 [40]`: `inRange=99.2%`, `rowClampRawMAE=0.0270`
  - weaker single-row cases still exist:
    `0x0001816A [40]` and `0x000181D2 [36]`
  - this materially strengthens late first-span hotspot row
    content/materialization over coefficient quantization or broad static
    provenance
  - short note:
    [codex_egt_row_backsolve_rerank_resolution.txt](/c:/Users/mmc99/source/repos/Xbox360MemoryCarver/research/facegen_research_artifacts/notes/egt/codex_egt_row_backsolve_rerank_resolution.txt)
- A direct gain-only follow-up now sharpens that result:
  - constrain the same inspected morphs to `delta' = clamp(g * currentRow)`
    with one fitted scalar `g` per morph
  - gain-only barely improves the anchors while unconstrained row-backsolve
    still nearly annihilates the residual on the same rows
  - representative pairs:
    - `0x0001816A [37]`: `gainRawMAE=3.1858` vs `rowClampRawMAE=0.0026`
    - `0x0001816A [35]`: `3.2783` vs `0.0473`
    - `0x000181D2 [37]`: `2.7994` vs `0.0076`
    - `0x000181D2 [40]`: `2.6402` vs `0.0270`
  - so the late-hotspot branch now confirms wrong row
    content/materialization outright, not amplitude drift
  - short note:
    [codex_egt_row_gain_rerank_resolution.txt](/c:/Users/mmc99/source/repos/Xbox360MemoryCarver/research/facegen_research_artifacts/notes/egt/codex_egt_row_gain_rerank_resolution.txt)
- A direct affine follow-up now closes the remaining “shape but affine drift”
  escape hatch:
  - fit one affine row model per morph,
    `delta' = clamp(a * currentRow + b)`, on the same fixed-coefficient target
  - affine helps slightly more than gain-only on some rows, but still stays
    nowhere near the unconstrained free-row result
  - representative comparisons:
    - `0x0001816A [37]`: `affineRawMAE=3.0625` vs `rowClampRawMAE=0.0026`
    - `0x0001816A [35]`: `3.2281` vs `0.0473`
    - `0x000181D2 [37]`: `2.7102` vs `0.0076`
    - `0x000181D2 [40]`: `2.6262` vs `0.0270`
  - so the remaining late-hotspot branch is now best read as wrong row
    content/shape outright, or a row-level substitution process, not scalar or
    affine drift on the current parsed row
  - short note:
    [codex_egt_row_affine_rerank_resolution.txt](/c:/Users/mmc99/source/repos/Xbox360MemoryCarver/research/facegen_research_artifacts/notes/egt/codex_egt_row_affine_rerank_resolution.txt)
- A direct row-space similarity follow-up now closes the last amplitude-drift
  hedge:
  - `--inspect-morph` now compares the current parsed row against the
    unclamped free-row target directly in byte space with cosine, correlation,
    and best scalar/affine row-fit residuals
  - representative rows stay near zero or negative in row-space similarity,
    and scalar / affine row models explain little of the required row movement:
    - `0x0001816A [35]`: `cos=0.0598`, `corr=-0.0194`,
      `gainExpl=1.5%`, `affExpl=3.0%`
    - `0x0001816A [37]`: `cos=0.0088`, `corr=-0.0493`,
      `gainExpl=4.2%`, `affExpl=7.9%`
    - `0x000181D2 [40]`: `cos=-0.1970`, `corr=-0.1877`,
      `gainExpl=10.4%`, `affExpl=10.9%`
    - `0x000181D2 [42]`: `cos=-0.0093`, `corr=-0.0341`,
      `gainExpl=1.3%`, `affExpl=3.3%`
  - even the strongest support-side case stays far from a “same row, different
    amplitude” explanation
  - so the late-hotspot first-span branch is now best read as wrong
    row-content/materialization outright, or a row-level substitution process,
    not gain-only or affine drift on the current parsed row
  - short note:
    [codex_egt_row_similarity_rerank_resolution.txt](/c:/Users/mmc99/source/repos/Xbox360MemoryCarver/research/facegen_research_artifacts/notes/egt/codex_egt_row_similarity_rerank_resolution.txt)
- A nearest-other-row follow-up now weakens simple whole-row substitution inside
  the currently parsed symmetric basis:
  - `--inspect-morph` now reports the best non-self symmetric row against the
    same free-row target
  - the best non-self candidate only improves modestly over the current row:
    - `0x0001816A [35] -> other [37]`: `vsSelf=10.0%`
    - `0x0001816A [37] -> other [01]`: `vsSelf=5.9%`
    - `0x000181D2 [40] -> other [37]`: `vsSelf=3.3%`
    - `0x000181D2 [49] -> other [40]`: `vsSelf=11.5%`
  - so a plain row-swap or row-index mismatch to another already-loaded
    symmetric row is now weak
  - the sharper next target is channel-level mixing or another external
    row-materialization path around the same late-hotspot family, not another
    whole-row swap theory
  - short note:
    [codex_egt_row_nearest_rerank_resolution.txt](/c:/Users/mmc99/source/repos/Xbox360MemoryCarver/research/facegen_research_artifacts/notes/egt/codex_egt_row_nearest_rerank_resolution.txt)
- A nearest-other-row follow-up now weakens simple row-substitution/index
  mismatch inside the existing symmetric basis:
  - `--inspect-morph` now also scans every other symmetric row against the same
    free-row target and reports the best non-self affine row-space match
  - the best non-self candidate is usually from the same late-hotspot family
    (`[37]` or `[40]`, with isolated `[00]/[01]` cases), but it only beats the
    current row modestly:
    - `0x0001816A [35] -> other [37]`: `vsSelf=10.0%`
    - `0x0001816A [37] -> other [01]`: `vsSelf=5.9%`
    - `0x000181D2 [40] -> other [37]`: `vsSelf=3.3%`
    - `0x000181D2 [49] -> other [40]`: `vsSelf=11.5%`
  - so a plain swap to another already-loaded symmetric row is now a weak
    explanation for the shipped `_0` hotspot mismatch
  - the stronger surviving read is an external row materialization /
    substitution path, or wrong row content outright, not a simple row-index
    mix-up within the current parsed first span
  - short note:
    [codex_egt_row_nearest_rerank_resolution.txt](/c:/Users/mmc99/source/repos/Xbox360MemoryCarver/research/facegen_research_artifacts/notes/egt/codex_egt_row_nearest_rerank_resolution.txt)
- A direct non-self nearest-row follow-up now weakens simple row-substitution /
  index-mismatch as the main explanation:
  - for each inspected hotspot, scan all other symmetric rows against the same
    free-row target and keep the best non-self affine row match
  - the best candidate usually stays inside the same late-hotspot family
    (`[37]` / `[40]`, with occasional `[01]` / `[00]`), but only improves
    modestly over the current row
  - representative improvements vs self:
    - `0x0001816A [35] -> other [37]`: `vsSelf=10.0%`
    - `0x000181D2 [35] -> other [40]`: `vsSelf=10.3%`
    - `0x000181D2 [49] -> other [40]`: `vsSelf=11.5%`
    - `0x000181D2 [40] -> other [37]`: `vsSelf=3.3%`
  - so the current row is not just a clean swap to another existing symmetric
    row; the stronger remaining branch is row-level materialization /
    substitution beyond a direct row index mix-up
  - short note:
    [codex_egt_row_neighbor_rerank_resolution.txt](/c:/Users/mmc99/source/repos/Xbox360MemoryCarver/research/facegen_research_artifacts/notes/egt/codex_egt_row_neighbor_rerank_resolution.txt)

## Scope note

- Shipped `_0` parity and loaded-runtime-NPC parity are related but not the same
  problem.
- The current highest-priority branch above is the shipped `_0` branch.
