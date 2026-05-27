# BSA mixed-archive texture failure

2026-05-24: Ulysses outfit textures failed from `v50-xex43.bsa` even though loose extracted DDS/NIF files rendered correctly. The BSA hash implementation matched vanilla `Fallout - Textures.bsa` for `textures\characters\male\upperbodymale_n.dds`, so hash lookup was not the root cause.

Root cause found: the generated all-in-one plugin BSA was using the texture-only archive shape (`archive_flags=0x107`: compressed + embedded filenames). Vanilla FNV mixed-content archives that contain DDS + NIF + voice data, such as DLC `*- Main.bsa` and `Update.bsa`, use `archive_flags=0x083`: include directory/file names + `RetainStringsDuringStartup`, with no zlib archive compression and no embedded filename prefixes. After changing `BsaWriter.CreateWithAutoFlags` to use the DLC Main layout for mixed texture archives, rebuilt `TestOutput/v50-xex43.bsa` has `archive_flags=0x083`, `file_flags=0x1b`, and Ulysses texture data begins directly with `DDS `.
