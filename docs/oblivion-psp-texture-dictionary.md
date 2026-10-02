# Oblivion PSP source-scoped texture dictionaries

The September 20, 2026 continuation implements the texture-dictionary prerequisite
from [material admission](oblivion-psp-material-admission.md). It retains a selected
dictionary and resolves exact-name candidates within that dictionary. It does not
establish PSP material, normalized scene, rendering or export acceptance.

## Source and occurrence ownership

[RwPspTextureDictionaryReader](../src/BethesdaMultitool/Core/Formats/RenderWare/RwPspTextureDictionaryReader.cs)
accepts exactly one TXD root and an explicit encoded-byte limit. Its caller supplies
the source/opening identity, archive entry ordinal/name/absolute offset/length,
named-resource ordinal, root offset within the entry, and diagnostic authoring path.
The reader checks nonnegative, nonoverflowing containing ranges before copying the
root. It does not discover archives, authenticate a live source, parse the outer
named-resource wrapper, or infer dependency precedence from names or paths.

One owned root copy backs the retained chunk headers and bodies. Complete root and
native child streams must tile their parents, required Struct/Extension children
must be unique, and the declared low-word raster count must equal actual native
occurrences. Allocation follows complete source headers, never the declared count.
All original native ordinals, duplicates, raw metadata and opaque child bytes remain
inspectable. Malformed structural layout declines the complete dictionary instead
of returning a truncated subset.

The reader reuses `RwChunk`'s inherited `0x1300` size correction, including its
permissiveness for undersized declarations. Original size words are preserved;
this implementation does not claim to have tightened that existing policy.

## Exact candidates and selected decoding

Names use a byte-preserving Latin-1 representation through the first NUL in the
complete fixed 64-byte field, matching the CLUMP reader. Lookup is ordinal and
performs no case/path normalization, first/last election, or cross-entry fallback.
Results retain every known matching occurrence in source order:

| Status | Meaning |
| --- | --- |
| `Missing` | Every name is interpretable and none matches. |
| `Unique` | Every name is interpretable and exactly one occurrence matches. |
| `Ambiguous` | Every name is interpretable and multiple occurrences match. |
| `IncompleteNames` | An original name is truncated or unterminated; known candidates remain available, but cardinality is inconclusive. |

`Unique` establishes occurrence cardinality only. A candidate may still have an
explicit pixel-decoding restriction, and no result establishes material support.

Only a selected raster is decoded through the existing `RwPspTexture` implementation.
Before allocation, `TryDecode` checks the caller's decoded RGBA-byte budget against
`8 * Struct body length` using `long` arithmetic. This conservatively bounds the
largest supported expansion, 4-bit indexed texels to RGBA. Header, palette and row
padding bytes make the estimate conservative; it is not a process-memory budget.
All admitted authored mips are returned without generating replacements. Each call
owns fresh pixel arrays, but the existing `byte[]` pixel API is not deeply immutable.
The decoder's 4096-dimension and 12-inferred-level limits remain unchanged, and
selected decoding has no cancellation parameter.

Library word `0x1C020065` and upper declaration word `9` are observed admission
metadata, not newly interpreted platform or sampler semantics. Unknown dictionary
metadata, unknown direct dictionary children, or a nonempty dictionary extension
restrict decoding throughout that root. Corresponding native metadata/children
restrict only that native occurrence. Source bytes and valid names are retained in
both cases. Uninterpreted fields inside the admitted Struct remain raw; the existing
raster decoder continues to own its established field interpretation.

## Validation and remaining gates

The applied patch adds 18 synthetic `RwPspTextureDictionaryTests` cases and extends
the existing single `RwPspTextureMipRetailTests` case. The focused total is 19 when
the private fixture is enabled and available; skips remain incomplete validation.
Synthetic coverage includes duplicate/case/high-byte names, independent scopes,
incomplete names, opaque restriction scope, unsupported metadata, complete-stream
and count failures, admission limits, owned source bytes, fresh decoded arrays and
all authored mips.

The existing retail case reuses one bounded original January 11, 2007 `Hub_1` entry:
archive ordinal 87, offset 98,032,192, length 1,989,324. Resource ordinal 0 contains
the dictionary at entry offset 584, length 456,408, raw declaration `0x00090040`,
and 64 native occurrences. Selected `ob_Rock2` is ordinal 21; native/Struct headers
are dictionary offsets 124,652/124,664. All eight independently established RGBA
hashes are asserted through dictionary decoding and source-overwrite checks. No
additional private payload is staged.

The 18 dictionary cases and extended original eight-mip case now pass on Windows
and Ubuntu WSL, with zero skips, within the [152-case portable batch](validation/shadowkey-native-preview-20260920.md).
Five additional original April 27, 2007 material-candidate cases pass on both:
Ebony Arrow keeps its two exact raster assignments, Elven LongSword preserves 17
prelit colors across 298 vertices, IronShield preserves all-black RGB with opaque
alpha, and IronArrow retains its untextured gray slot beside two textured slots.
Goodies' empty dictionary remains Missing after explicitly selecting the same
name's Unique candidate in the separate ClothSack dictionary.

Those five cases make six bounded entry reads totaling 6,134,148 payload bytes,
plus bounded archive metadata, and decode 16 selected one-mip raster occurrences.
They pin full entry/root/raster/pixel hashes, per-material triangle counts and UV
and color arrays before and after candidate lookup. Original CLUMP sampler
`0x11102` and native raster word `0x1102` remain uninterpreted. A transcribed
ClothSack digest was independently corrected after a retained failing run; the
decoder did not change.

The [callable complexity review](complexity/psp-texture-dictionary-20260920.json)
records the earlier source review; compiled execution is recorded separately.
The original implementation artifact remains unchanged in ignored `TestOutput`, with patch SHA256
`5fc25ff30ef4ea2f9038c91ef5626fe700725662c172d3c7596494a4dba53d21`.

Cross-entry dependency rules, coordinate proof, sampler meaning, alpha/culling and
lighting policy remain open in the [material admission prerequisites](oblivion-psp-material-admission.md).
No material adapter, normalized dispatch, viewer/export path, Shared change or pin
change is part of this increment. Resource bounds are reviewed, not measured
performance or release acceptance.
