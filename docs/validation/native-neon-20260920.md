# Native neon capture — September 20, 2026

The current-pin Windows application and renderer-profiler build passed after
`f6290ffe6da503078242b8cc7cb4ce25de739ff3`, with the first Shadowkey native-preview
source applied and Shared still at `88326240d9603a2092886934eed6d63275fdbcf7`.
The pinned wrapper used locked restore, one MSBuild node, isolated compilation,
ordinary analyzers, serial compiler analysis and the default 4 GiB admission.
It completed in 18:52 with 15 warnings and zero errors. Three new Shadowkey UI
warnings were subsequently corrected; this build does not validate those edits.
The other warnings comprise nine inherited application diagnostics and three
Win2D platform warnings. No AD0001 or copy-lock diagnostic occurred.

## Exact built inputs

| Artifact | SHA256 |
| --- | --- |
| Application DLL | `5d5cd3664555a6f259238d51ff19cf88c90f41203ea72b9f5f0ebb34561c3db5` |
| Application PDB | `96c93467b279362d516067914995bfe79ae4d702dbaaa2a6fc17cc8f611beb26` |
| Profiler DLL | `deb85700d7db8c8d4b24a43918aff44f4b29ec66296efab89b4bc8d9be75e5fd` |
| Profiler executable | `cdf4e070b4c2f90180138ea2da620d4cb644f4a1ff8dda187c92e618ea1f0a40` |
| Physical 124-permutation shader pack | `adaaebbbdb0c56073c7533bc3174073eab5bae032d4ec4afa181d2752dc5921c` |

PE debug identities match their PDBs. Source verification checked 3,080 available
application documents and 25 profiler documents. Their 143 and three virtual
generated documents remain explicitly distinguished from available source files.
All 13 frozen changed Windows inputs matched the pre-build hashes. Thirteen
DLL/PDB/shader groups match their recorded producer and consumer copies.

One dependency is recorded separately: application and profiler copies of
`Slfx77.Multitool.WinUI` differ. Both PE/PDB pairs verify all 110 available source
documents; 23 of 32 virtual WinRT-generated document hashes differ. Available
source hashes agree. A subsequent embedded-source comparison resolves all 29
generated type-to-vtable mappings to identical bodies, compares 52 identical
generic helper class bodies, and confirms the same 26 disjoint exact-name cases
in both lookup methods. Compiler-option, metadata-reference and SourceLink
records are byte-identical. For this exact pair, generated differences are
equivalent representative names and declaration/branch order. This is not a
general binary-equivalence waiver or generated-byte reproducibility claim.
The normal build log cannot explain the repeated project visit. No reference
property was changed, and no dependency was copied manually to force equality.
The exact profiler payload was used below.

## Actual renderer execution

Two sequential headless `ReferenceRenderer12` runs used the original Fallout:
New Vegas mesh and texture BSAs. The NIF is
`architecture\goodsprings\nv_prospectorsaloon-neon_lights.nif`; the existing
[emission receipt](emission-adoption-20260920.md) records byte identity between
that original archive entry, all seven texture entries and the bounded fixtures.
No raw game payload was copied for this run.

Both runs used 1920×1920 output, yaw 225, animation time zero and `--gui-shape`.
Inherited `FALLOUT_VIEWER_*` settings were removed, then tone mapping ACES,
exposure one and bloom zero were explicitly set. Only `--emissive-mult` changed
from zero to one. The two-slot coordinator retained its default 4 GiB gate.
Both processes exited zero, loaded the shipped shader pack, settled after seven
iterations and drew the sign's 11 submeshes.

| Capture | PNG SHA256 |
| --- | --- |
| Emission zero | `0702dc6a6427fefda842968a9319cde6f3f6a75119c2b3c59dee78685a623f12` |
| Emission one | `d2da36b208934d7ab5a26a04e779497132be9500fde60501c628bec6dff12906` |

Visual inspection confirms the complete sign and a brighter green O with
emission enabled. Independent full-resolution RGBA analysis finds 390,486
nonblack pixels in each image, silhouette IoU 1, identical alpha and 63,949
changed pixels confined to x=740–1035, y=689–1034. The largest channel difference
is 134. All 26,643 pixels selected by the emission-zero red mask
(`R > 200, G < 60, B < 60`) remain identical. That mask is a pixel criterion,
not a geometry-ownership assertion.

## Acceptance boundary

This supplies current-pin Windows compilation and an actual Bethesda native
framebuffer response. It does not compare the Shared native renderer, exercise
interactive viewport input or validate the Shadowkey GUI. Current source after
the three analyzer corrections still requires its next Windows build. Current
self-contained publish, interactive replacement/cancellation/shutdown and
localization checks remain pending. M2.1, M1–M6/TB1–TB6 and release acceptance
remain open; legacy production export dispatch stays active.

Local build/PDB/freeze/correspondence evidence is under
`TestOutput/shadowkey-native-preview-20260920`. Capture commands, process logs,
PNG files and `image-review.json` are under
`TestOutput/emission-adoption-20260920/native-neon`. Generated artifacts remain
outside committed source.

The pair-specific generated-source comparison is
`windows-shared-winui-generated-equivalence.json`, SHA256
`33777880e532f4acd2cbd898e42ae9da85687472b85c679a6b166790ce190031`.
