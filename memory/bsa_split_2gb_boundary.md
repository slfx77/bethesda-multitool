# BSA Split for Signed 2 GB Runtime Boundary

Date: 2026-05-24

The monolithic `v50-xex43.bsa` was 3,839,500,447 bytes. Ulysses resources that failed in game
were stored past the signed 2 GB boundary:

- `textures\armor\ulysses\ulysses_n.dds` at `0xD3161A75`
- Ulysses voice OGG/LIP entries around `0xB6DD636B`

Loose-file extraction worked, and the bytes at those offsets were valid (`DDS ` / `OggS`), so the
common failure mode is likely FNV's runtime archive reader/resource layer mishandling offsets above
`0x7FFFFFFF`.

The packer now treats `--pack-assets <plugin>.bsa` as a base name when multiple asset classes are
present and writes plugin sidecars:

- `<plugin> - Main.bsa`
- `<plugin> - Textures.bsa`
- `<plugin> - Sounds.bsa`
- `<plugin> - Voices.bsa`

It also chunks a category into numbered sidecars (`Textures2`, `Sounds2`, etc.) if the estimated BSA
size would exceed the safety limit.

This should keep meshes, textures, general sounds, and dialogue voice/lip files below the signed
2 GB boundary and also matches the way FNV's vanilla/DLC archives are generally organized.
