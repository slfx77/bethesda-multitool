# NIF lookup and head-shape routing: Linux source validation

This batch follows BMT `862d3add` with Shared still pinned at `88326240`. It applies
bounded case-insensitive loose-texture lookup, optional exact pre-skin head-shape
routing, bounded archive proof reads, and readable framework notice sidecars.

## Retained first run

The coordinated `20260920-nif-egm` run compiled the current checkout with Microsoft
.NET SDK 10.0.400 on Ubuntu 24.04 WSL. The source tree and private corpus remain
on NTFS/DrvFS; synthetic temporary files use `/tmp`, verified as ext4. Locked
restore and the analyzer-enabled source build passed. Compilation took 14:22.01
and reported 97 inherited warnings, no application warnings, no warnings in the
affected test files, and zero errors. Input manifests remained unchanged through
the entire attempt.

The focused run executed all 291 expected cases: 290 passed, one failed, zero
skipped. All 20 new texture-directory cases, ten new shape-target cases, and the
30 material / 17 skin / 22 scene-adapter cases passed. The three added bounded
archive cases also passed. The failure was the existing
`MeshArchiveSetTests.FuzzyEnabled_ResolvesRenamedMesh_AndReportsResolvedPath_WhileExactOnlyMisses`.
Publish, accessibility execution and compiler PDB audits were not reached in this
attempt. Its failed log, JUnit report and unchanged-input receipts are retained.

## Portability repair and retry

The archive index and fuzzy resolver called `Path.GetFileName` on game virtual
paths normalized with backslashes. On Linux, that host-filesystem API retained
the complete virtual path, so the basename index could not match a renamed mesh
from another directory. `AssetPathRules.GetVirtualFileName` now splits either
game separator independently of the host. Index construction and all five fuzzy
lookup sites use it. The existing loose-basename and global-character skeleton
classification use the same helper before removing the extension. Physical
archive paths retain their host filesystem handling; donor order, exact lookup,
collision preference and fuzzy tie rules are unchanged.

The helper scans at most the path length and returns at most one component-sized
string. It adds no filesystem work or retained state. Existing resolver behavior
tests cover renamed basenames, source ordering, ambiguity and fallback; existing
asset-path collector tests cover the skeleton classification. No source-text
assertion was added to mirror the implementation.

Fresh retry helpers preserve the original runners and expand the focused batch
to 321 cases: the original 291 plus 20 `DataFolderResolverTests` and ten
`AssetPathCollectorTests`. Two accessibility cases, Linux publish, actual published
CLI execution, compiler/PDB correspondence, physical notice delivery, Windows
execution of the Linux-built managed binaries and candidate lookup measurements
remain pending at this note's creation. Separate GUI, retail actor and release
acceptance gaps remain explicit in the implementation notes.

## Retry execution

The `20260921-nif-egm-resolver` retry passed locked restore and actual Linux source
compilation in 16:43.59, with the same 97 inherited warnings and zero errors.
There are no application warnings or warnings in the changed NIF/archive test
files. All 321 focused cases passed, including the previously failing fuzzy
archive lookup and the additional resolver/asset-path coverage. Both accessibility
cases passed. Both runs had zero failures, errors or skips. The synthetic
case-collision fixtures used `/tmp` on ext4; source and corpus remained on
NTFS/DrvFS.

The run then stopped at publish restore with NU1004. The helper's `dotnet restore
-r linux-x64` narrowed the application's three-RID lock graph; global publication
properties also introduced runtime/ILLink requirements into dependency projects.
All source/config/resource/lock inputs remained unchanged. The failed overall
receipt is retained, and no publish or published CLI success is claimed.

Publication is being retried separately with the application's declared restore
graph and existing lock files. The completed source build and 323 passing cases
will be reused with explicit parent evidence, without repeating compilation or
tests for an invocation-only repair. The corrected ordinary portable restore
passed with all three declared runtime targets and unchanged committed locks;
its Linux publish compiled the application and completed trimming, then stopped
before bundling on MSB4012 in the ignored diagnostic import. That helper combined
a literal prefix with an MSBuild item list outside the item transform. The failed
attempt remains under `TestOutput/linux-publish-retry-20260921-nif-egm-resolver`;
its final manifest confirms unchanged source and helper inputs. A separate
trace-only correction is being prepared. No published artifact or notice-sidecar
acceptance is claimed from this attempt.

All eight test-output compiler PE/PDB checks passed. The application has 2,787
verified authored documents, three generated files and 93 virtual generated
documents. The test assembly has 1,530 authored documents, five generated files
and one package-supplied xUnit document. That exact external source matches both
the actual Linux cache file and the entry in the lock-verified package.

The original verifier refused the external package source. The first narrow
package extension incorrectly equated raw ZIP SHA512 with NuGet's signed-package
content hash and was retained as a failed recipe. The successful verifier,
`verify-linux-build-pdb-nuget-content.ps1`, has SHA256
`ba34c8a4c5f0b4cfcc116457ffe79f986f859da4ab899537a6d8197c57d0bedb`.
It uses installed NuGet 7.9's canonical package-content hash, checks the lock,
cache metadata and raw archive digest separately, and preserves the exact
compiled-source and PE/PDB checks. The receipt records the hashing implementation
assemblies; this establishes content correspondence, not publisher trust.

Native Windows execution of the same eight Linux-built managed pairs passed all
321 focused cases and both accessibility cases, zero failures or skips. The
existing Windows test apphost and all compiler DLL/PDB pairs stayed unchanged.
This is Windows runtime evidence, not a new Windows source compilation or GUI
acceptance. The bounded lookup measurements are now recorded in the
[lookup implementation note](../nif-directory-case-resolution.md); the larger
directory samples show a material scan cost, with no general performance claim.

Before changing the FLC reader, the source/PDB-verified application also produced
105 prior-implementation RGBA frame hashes for two original Arena files. Exact
header timing and source immutability passed; initial/final reads totaled
4,177,252 bytes under the 4 MiB cap and wrote no payload copies. The baseline
receipt is `TestOutput/flc-decoded-adapter-20260920/prior-legacy-baseline.json`,
SHA256 `cba89af048b8fd6099030e25bba47756b2ca685eb89f2d27ced506bf00eeda2d`.
It is a baseline for the next adapter tests, not validation of that new adapter.

Evidence is under `TestOutput/linux-current-checkout-20260920-nif-egm`; the fresh
runner derivation is `TestOutput/shadowkey-native-preview-20260920/resolver-retry-helper-review.json`.
The retry evidence is `TestOutput/linux-current-checkout-20260921-nif-egm-resolver`.

## Completed Linux publication

The diagnostic correction passed in the separate
`TestOutput/linux-publish-trace-retry-20260921-nif-egm-resolver` directory. It used
the completed locked restore and compiler output with `--no-build --no-restore`;
normal partial trimming, dependency resolution and bundling remained enabled.
The 6,009 frozen inputs, tested compiler pairs, RID application PE/PDB and all
106 preceding publish-input hashes remained unchanged. No tests were rebuilt or
rerun for this correction. Both earlier failed publication receipts remain failed.

The actual Linux executable ran CLI help with exit 0. Native Shadowkey capture
returned the expected unavailable exit 2 before creating any input/output path.
The ninth compiler PDB audit verified 2,787 authored application documents. The
payload audit matched all 91 bundle entries and 36 final sidecar inputs, including
the complete generated ELF host except its eight-byte bundle-offset field. All
five application/project assemblies in the linked directory matched their
compiler bytes; package trimming is recorded separately, without a general
semantic-equivalence claim.

All **20 applicable portable project notices** are readable and byte-identical:
two BMT notices, four Shared Core, eight Shared Media and six SharpGLTF files.
The separate pinned Magick notice also matches. This closes the Linux notice
delivery check; Windows still requires its fresh output and larger notice scope.

| Artifact | SHA256 |
| --- | --- |
| Published ELF, 25,390,458 bytes | `bcf86ad45043b2adedda120772cbec01bf4bb728322c0f614ac63d586974defc` |
| RID compiler application DLL | `299b3d6e56e3f9153f362786e9d9ac7d60458a38cd44558e103a25ae905d3ea7` |
| RID compiler application PDB | `da4f6babfc82cdfc0e7f3a50a91e6c584738a5087f4ea8755d50cc8eb8cdc247` |
| Build/payload correspondence receipt | `4ad1bb53d950e8fe507b84b5726cbc5e87f29f521bf149575b3dca8fdb85ab2e` |

The terminal launcher reports success, unchanged inputs and passed compiler,
payload and correspondence checks. These are current-source Linux compile,
runtime and publication results at Shared `88326240`, with source/corpus on
NTFS/DrvFS. They do not establish interactive GUI, macOS, whole-corpus fidelity or
release acceptance. Legacy NIF production export dispatch remains unchanged.
