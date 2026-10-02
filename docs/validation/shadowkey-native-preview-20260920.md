# Shadowkey native preview and PSP dictionaries â€” September 20, 2026

This increment follows BMT `f6290ffe6da503078242b8cc7cb4ce25de739ff3` and retains
Shared `88326240d9603a2092886934eed6d63275fdbcf7`. It adds source-owned Shadowkey
pack selection, direct normalized native preview, an offscreen diagnostic command,
and explicit PSP texture-dictionary candidates. The implementation notes describe
[browser ownership](../shadowkey-pack-native-preview.md),
[capture publication](../shadowkey-native-capture.md), and
[dictionary admission](../oblivion-psp-texture-dictionary.md).

## First portable checkpoint

The pinned build wrapper completed a locked Release/net10.0 test build in 21:05,
with one MSBuild node, isolated compilation, ordinary analyzers, serial compiler
analysis and the coordinator's default 4 GiB admission. It reported zero errors
and 91 test warnings: 71 inherited warnings and 20 new test diagnostics. The
application compilation reported no warnings. The new test diagnostics were
subsequently corrected; this first build does not validate that correction.

All 31 changed source/project/resource inputs matched the pre-build LF-normalized
hash manifest. PE/PDB identity and available document checks verified 2,788
application documents and 1,532 test documents. The application's 93 virtual
generated documents are recorded separately; the test PDB has none. Ten matching
producer/consumer DLL and PDB files cover BMT, Shared Core/Media, SharpGLTF and DDX.

| First build artifact | SHA256 |
| --- | --- |
| Portable application DLL | `2ff9e2c9f7fb3de4402d2390438f8a96dde34da03080b8c1cfa8e06a12ea8bdb` |
| Test DLL | `a45bd74af7445bc5cf5f2c9f57148fb5a68c8c83c9357794e801c8a12c0576b2` |
| Source freeze manifest | `2e2d11ba8ab7857696834bd73331c58292b6696b5c9eb2558496b28e773dbfc0` |

| Execution | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| Initial Windows run, fixture root omitted | 143 | 4 | 0 |
| Windows with explicit original fixture root | 147 | 0 | 0 |
| Ubuntu WSL, same portable managed payload | 147 | 0 | 0 |
| Windows accessibility ratchet | 2 | 0 | 0 |

The first Windows failure is retained: four private-fixture cases failed through
`--fail-skips on` because the launcher omitted `BETHESDA_TEST_DATA_ROOT`.
The corrected launcher points to the existing primary corpus without copying
payloads. All runs use one test thread and a positive expected minimum. Ubuntu
executes Windows-built portable binaries over NTFS/DrvFS; this is not Linux source
compilation, publish or case-sensitive filesystem evidence.

The 147 cases comprise catalog ownership and cancellation (20), native project
topology (1), localized resource contracts (2), existing renderer labels (2),
source ownership (2), tree selection (8), Explore planning (7), Shadowkey scene
fidelity (19), fixture resolution (7), PSP decoding/mips (28), dictionary behavior
(18), capture CLI/options (19), and launch routing (14).

## Corrected portable checkpoint

Reviewed fixes forward test cancellation without changing the deliberately
synchronous replacement barrier, and require a nonnull dictionary diagnostic.
Nine Windows application warnings are corrected with equivalent validation and
ordering; the synchronous dialogue cancellation and proof-gated window lifetime
have narrow documented analyzer exceptions. Five additional original PSP binding
cases pin CLUMP material slots, selected texture occurrences, prelit colors and an
untextured gray material. All 152 focused cases pass on Windows and Ubuntu WSL,
with zero skips; the two accessibility cases also pass again on Windows. The
corrected build completed in 2:58 with zero errors and only the 71 inherited test
warnings. Application and dependency binaries remain identical to the first
portable checkpoint. Its 36 changed inputs match the separate freeze, and all
1,533 available test PDB documents match current source.

| Corrected artifact | SHA256 |
| --- | --- |
| Test DLL | `ec1e504541a472dde14dacc6a2efe315b985341834b070ea0d2639110a7089bc` |
| Test PDB | `ae911cfd6d95a8941536ff63e28b1e5d119001bcaa3f28eae1ac0290a8c12305` |
| Source freeze manifest | `7e851afd3f146b9ed31cd2249b3ab03bc0b21f13e44baef3fd581583cf2f150d` |

An intervening 152-case Windows run passed 151 and failed one because the new
ClothSack dictionary hash had been transcribed with 65 characters. An independent
11,040-byte read of the original archive entry confirmed its entry identity and
the dictionary's correct raw SHA256:
`a9bfcbf170cceb3c8d638368085df186c4f5fbbe77182f94c8f9007ec43bdae1`.
The test pin was corrected without changing the decoder or other fixture values.
Two test helper naming warnings were also removed. The failed report and original
patch remain retained; the successful labels include `final-corrected`.

## Windows native capture checkpoint

The Windows Release/net10.0-windows10.0.19041.0/win-x64 application build used for
these captures completed successfully in 11:15.20 with zero errors and six
warnings: three Win2D platform diagnostics from Shared project builds and three
application analyzer diagnostics (one S3626 and two S6966). This is compilation
of the applied native composition and capture command, not a publish receipt.
The capture checkpoint predates the follow-on analyzer and publish-notice edits;
later sources/binaries require their own correspondence and validation.

The capture build verified all 36 frozen inputs against manifest
`7e851afd3f146b9ed31cd2249b3ab03bc0b21f13e44baef3fd581583cf2f150d`.
Its PE/PDB receipt verifies 3,090 available application source documents and
records 143 virtual generated documents separately. Ten producer/consumer hash
groups cover BMT and Shared Core/Media/WinUI/Direct3D12 DLL/PDB pairs.

| Capture-build artifact | SHA256 |
| --- | --- |
| Application DLL | `73822d9bef3b17c1db74def2d1ea52373579e9cee47d879bd81620318596bb7a` |
| Application EXE | `4fc9887bbcfddb7b2e187478636403019a8ab43417e0567bb5ff98ef8a55918d` |
| Application PDB | `fd4e6bc773af6d7016d639085163db565c8a6f18c7ebf271790287ca4f7c14f1` |
| Shader pack | `adaaebbbdb0c56073c7533bc3174073eab5bae032d4ec4afa181d2752dc5921c` |

The six original-path command invocations all returned their expected statuses:

| Original selection / operation | Exit | Observed result |
| --- | ---: | --- |
| `fern.bin`, slot 0, frame/skin 0, opaque | 0 | New 512Ã—512 PNG and receipt; native disposal succeeded. |
| `fern.bin`, slot 0, frame/skin 0, explicit magenta key | 0 | New 512Ã—512 PNG and receipt; native disposal succeeded. |
| `arrow.bin`, slot 175, frame/skin 0, opaque | 0 | New 512Ã—512 PNG and receipt; native disposal succeeded. |
| Empty slot 19 | 2 | Explicit empty-slot decline; no PNG or receipt. |
| Animated slot 18 | 2 | Unrecorded timing-units decline; no PNG or receipt. |
| Repeat opaque fern destination | 1 | Existing PNG and receipt refused and unchanged. |

The runner checked all three original input hashes before and after the batch,
and the application DLL/EXE hashes remained unchanged. Each successful receipt
also records unchanged inputs after native disposal. Independent receipt review
rehashed every output and decoded all three PNGs: their 512Ã—512 RGBA buffers
exactly match their respective receipt hashes. The repeat output hashes match
the first successful opaque fern capture. These are three successful renders,
two expected declines and one expected refusal, not six renders.

The independent fern oracle used the previously fixed script
`5dab4bd215a3e6ec4fa10023744870f2507478b6e6f58abfa4a10497b16c16c0`
and read 11,093 original bytes (index, names and the selected fern record). It
independently checked record/geometry/UV/pixel hashes; it did not reread the full
pack, whose established hash is checked against the capture receipt. It sampled
a fixed 8-pixel grid at offset 4, excluded source/camera/alpha/depth-boundary
cases, and retained every mismatch above the preset four-sRGB-byte tolerance.

| Fern capture | Stable image samples | Visible | Key-discard/background | Repeat-vs-clamp discriminators | Excluded geometry samples | Maximum channel error |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Opaque | 111 | 111 | 0 | 88 | 57 | 3.1385181496 |
| Explicit key | 90 | 66 | 24 | 54 | 78 | 3.1385181496 |

Both checks passed with zero mismatches above tolerance; both repeat tests reject
clamped sampling. The 201 total image-sample checks are not 201 distinct source
locations or whole-image parity. Background color is measured from four equal
capture corners independently shown to miss geometry, so the oracle does not
independently prove clear-color encoding. Raster/alpha edges, excluded depth ties,
other cameras, arrow pixel fidelity and original-engine alpha semantics are
outside this evidence. Explicit magenta keying remains a hypothetical user choice.

Exact evidence is retained under
`TestOutput/shadowkey-native-preview-20260920`: `windows-capture-build.log`,
`windows-capture-build-correspondence.json`, `windows-capture-app-pdb.json`,
`native-shadowkey/command-receipt.json`, the three `.png.capture.json` sidecars,
and `fern-pixel-comparison.json`. The earlier
[native neon capture](native-neon-20260920.md) remains separate route evidence.

## Remaining gates

The capture-build Windows compilation and bounded offscreen Shadowkey pixels now
have the evidence above. A subsequent [Windows publish checkpoint](publish-20260920.md)
compiles the warning and notice changes and passes six commands through its actual
single-file executable. Both fern captures retain exact PNG and RGBA correspondence.
Its separate notice-distribution finding, release acceptance and interactive
workflow checks remain open. The computer-use native pipe is unavailable; source and
offscreen checks do not establish GUI replacement, cancellation, shutdown, layout,
localization or injected cleanup-failure behavior. The six commands did not test
cancellation or terminal native disposal failure. Subsequent source changes are
not retroactively covered by the capture-build hashes.

Legacy production NIF dispatch remains. PSP normalized material adoption, Shared
media consumer adoption, M2.1, the complete M1â€“M6/TB1â€“TB6 program and release gates
remain open. Detailed local logs, PDB/source correspondence and XML reports are in
`TestOutput/shadowkey-native-preview-20260920` and the adjacent labeled JUnit files.
