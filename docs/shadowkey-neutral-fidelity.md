# Shadowkey neutral fidelity increment

This increment prepares the existing adapter for later adoption. It does not add production dispatch, pack-slot selection UI, native viewer integration, animation playback, or a new Shared contract.

[ShadowkeyNeutralSceneAdapter](../src/BethesdaMultitool/Core/Formats/Travels/Shadowkey/ShadowkeyNeutralSceneAdapter.cs) now requires an explicit caller choice to enable magenta color-keying. Its default decodes every source color as opaque and emits an opaque material. Opting in produces alpha zero for `0x0F0F` and a matching mask material with cutoff 0.5. The previous implementation implicitly used `DecodeSkin`'s keyed default while emitting an opaque material; its hidden alpha did not establish a transparent rendering policy. The decoder documents keying as a hypothesis, so this change does not declare it authentic for every asset.

The caller may supply an exact source occurrence identity, including pack and slot when known. The document retains that identity; both document and node extras retain it with selected frame, selected skin, source frame count, and the color-key choice. Missing identity stays null. Mesh labels remain labels. Positions retain the existing source basis and units; texel UVs are normalized by skin dimensions and remain unclamped with repeat sampling. Multi-frame records with sequence metadata still decline because the sequence timing units are unknown.

## Original corpus evidence

The independent raw scan read only the original files under the dated N-Gage final build through the existing corpus location. It found 237 slots, 226 structurally valid nonempty records, 11 empty slots, 193 records eligible at frame 0 / skin 0, and 33 animated records that must decline. All 193 eligible records have one skin; 41 contain magenta. Across all 319 skins, the maximum distinct color count is 233 and every texel has a clear upper nibble. These are raw fixture findings, separate from executing the adapter.

| File | Original size | SHA-256 |
| --- | ---: | --- |
| `models.idx` | 1,900 | `7b67e1d26b3d0037b748622fceb340fb4556bb638490b985b2c4b0f62bf0ae71` |
| `models.txt` | 6,433 | `a595b31863e96777083b6c1294c81c27d7ba4334a6ffb8332162246c96fffb24` |
| `models.huge` | 4,907,880 | `3b49517651b9edbf78e99660ef0a0f5f9d398146d11eecb7ee12df81c8fbd310` |

The [tests](../tests/BethesdaMultitool.Tests/Core/Formats/Travels/Shadowkey/ShadowkeyNeutralSceneAdapterTests.cs) pin two original records and independent little-endian position, normalized UV, and decoded RGBA hashes. Those goldens were derived from raw fields without the application parser, unroller, decoder, adapter, or GLB builder.

| Slot / label | Offset / bytes | Geometry and skin |
| --- | --- | --- |
| 0 / `fern.bin` | 0 / 2,760 | 24 triangles, 32×32 skin, 694 magenta texels; normalized V extends from 1.03125 to 2.0. No repeated-position triangles. |
| 175 / `arrow.bin` | 4,173,632 / 678 | 19 triangles, 8×8 skin, no magenta; six triangles repeat exact positions (ordinals 3, 5, 7, 9, 10, 11). |

The named tests require every source/neutral triangle, then compare the encoded triangle multiset including winding and UV association. Shared's direct `SceneGltfBuilder.AddIndices` retains repeated-position triangles; no Toolkit legacy filtering is accepted here. Both default opaque and explicit keyed PNG pixels and GLB alpha modes are checked. No private payload is written or staged. Corpus reads retain opt-in and sequential execution, with preallocation limits of 4 KiB index, 16 KiB names, and 5 MiB pack, plus exact file hashes.

## Validation boundary

The expanded class contains 19 cases: 16 portable and 3 opt-in retail. Synthetic cases cover independently indexed corners and selected nonzero frames, source basis/UV/winding/repeat preservation, exact default/keyed pixels and encoded alpha, occurrence metadata through GLB, cancellation before work, source object/buffer immutability, and owned image storage. The census now requires exact carried/declined/empty counts instead of allowing an empty or reduced scan to pass.

The locked portable build and all 19 cases now pass on Windows and Ubuntu WSL, within a 54-case focused batch on each platform. The [validation receipt](validation/travels-fidelity-20260920.md) records exact binaries and the original three failed sampler assertions. Those assertions incorrectly required an explicit all-default sampler; the corrected oracle follows the material's actual texture and checks effective repeat wrapping, including glTF defaults when the sampler is absent. No production code changed for that repair. No Khronos, image-render, GUI, release, Linux source-build, or non-rest animation acceptance is claimed. Cancellation during the format's synchronous decode is observed at its boundary, not inside the existing decoder.

The [complexity receipt](complexity/shadowkey-neutral-fidelity-20260920.json) hashes the changed declarations and records their incremental resource costs.
