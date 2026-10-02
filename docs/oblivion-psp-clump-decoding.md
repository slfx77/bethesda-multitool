# Oblivion PSP CLUMP decoding

This application-owned increment implements the geometry-decoding prerequisite
of M2.1. It does not enable normalized export or viewing. Shared remains pinned
to `2d498be3`; existing format dispatch is unchanged.

`RwClumpReader` assembles generic RenderWare geometry into its original graph:
frames and parent edges, geometry and material slots, atomic draw occurrences,
texture and mask names, sampler words, and camera/light frame associations. It
retains original coordinates, material fields and flags. Opaque extension bodies
refer to an owned copy of the source and carry explicit unevaluated diagnostics.
Skin, HAnim, camera/light and other plugin semantics are not interpreted.

The reader requires exact chunk bounds and count agreement, finite source
numbers, acyclic frame parents, valid material reuse and atomic references, and
valid geometry indices. Generic triangles and BinMesh draw splits must agree as
oriented triangle multisets including material assignment and multiplicity.
Repeated-index degenerate strip connectors are excluded only from that comparison;
authored geometry remains intact. Unsupported core layouts return a diagnostic
instead of a partially decoded graph.

The bounded private corpus comprises six dated builds. Independent byte-level
inspection found 963 CLUMPs, 14,795 frames and 993 generic geometries/atomics. All
993 geometry/BinMesh pairs agree after excluding 236 repeated-index degenerate
triangles. There are 502 CLUMPs without a Skin plugin; this number is not a count
of renderable or normalized-adapter-eligible objects. The remaining features still
require semantic admission and fidelity checks.

A subsequent independent scan of every decoded floating-point source field found
one authored invalid CLUMP. Successful finite graph decoding therefore covers
962 CLUMPs, 14,764 frames and 992 geometries/atomics; the inspected corpus totals
above remain unchanged. The acceptance test requires this one exact decline and
rejects any additional failure.

| Build | Inspected CLUMPs / frames / geometries | Decoded CLUMPs / frames / geometries | Invalid-source declines |
| --- | --- | --- | --- |
| 2006-6-9 | 5 / 93 / 11 | 5 / 93 / 11 | 0 |
| 2006-11-21 | 153 / 1,928 / 153 | 152 / 1,897 / 152 | 1 |
| 2007-1-11 | 214 / 3,388 / 222 | 214 / 3,388 / 222 | 0 |
| 2007-1-31 | 240 / 3,788 / 248 | 240 / 3,788 / 248 | 0 |
| 2007-2-1 | 240 / 3,788 / 248 | 240 / 3,788 / 248 | 0 |
| 2007-4-27 | 111 / 1,810 / 111 | 111 / 1,810 / 111 | 0 |

Atomic counts equal geometry counts in both columns. The rejected resource is
`2006-11-21/GR.ARC`, entry `CEnemyBehaviourLoot.Villager2`, named-resource ordinal
50, entry-relative CLUMP offset 2,556,688, length 82,006 bytes. Its SHA256 is
`12B8F3B6E3C202DFCF0CA60980BFA07FC74A569B9B75692EA8A7BC518141B1CB`.
It contains 31 frames and one skin-bearing geometry/atomic, with 995 vertices and
1,030 triangles. All three components of normal vectors 69–80 and 656–687 contain
the literal little-endian word `0xFFC00000`: 132 NaN scalars in 44 vectors. The
normal array begins at CLUMP byte 36,340; the first invalid scalar is at 37,168.
The exact required reader diagnostic is `Nonfinite geometry normal.` No value is
sanitized and the finite-value check is unchanged. The retail test pins the full
resource hash, every affected normal word and the finite status of every other
normal in that resource.

The Python census read GR.ARC tables and nested source bytes directly, without BMT
readers. Across all 963 CLUMPs it inspected 177,540 frame components, 1,205,020 UV
components, 3,972 sphere components, 1,810,482 position components, 1,798,350 normal
components and 17,151 material surface components. The 132 normal scalars above
were the only nonfinite values in those fields; opaque plugins remain outside
this numeric interpretation.

The authored acceptance batch contains 48 synthetic cases and 10 opt-in retail
cases. Synthetic cases cover hierarchy, source independence, material reuse,
draw occurrences, attachment preservation and malformed bounds/counts/indices.
Retail cases pin all six corpus counts and four SHA256-identified resources: a
textured arrow, a translated rigid object, a skin-bearing object and a camera
attachment. The first formal run passed all 48 synthetic cases and nine retail
cases; the authored NaNs exposed the original census test's incorrect universal
success expectation. The exact-decline correction passes all 58 cases on Windows,
without skips. The 48 synthetic cases and 61 existing leaf-reader cases also pass
on Ubuntu using the same portable binaries. The first Linux retail attempt failed
its fail-skips gate because the dated-build locator embedded a Windows backslash;
all six build folders and their GR.ARC files are present under the configured Linux
corpus root. The locator now uses platform-native path construction, with two
synthetic resolver cases covering a corpus root and its Sample child. The focused
Linux rerun passes all 208 cases, including all ten PSP retail cases. It executes
the Windows-built portable DLL in WSL over NTFS; this is Linux runtime evidence,
not a Linux source build or publish. Private payloads stay external.

Further work must establish coordinate basis, texture resolution and material
semantics, then preserve original frames, geometry occurrences and provenance in
the normalized contract. Skin/HAnim and other fidelity cannot be silently dropped.
WORLD decoding, native viewing, animation and normalized export acceptance remain
separate implementation and validation work.
