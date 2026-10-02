# PSP authored mip preservation

`RwPspTexture` retains every level admitted by the existing encoded-length check.
Each level is decoded from its own padded source rows into owned RGBA bytes;
`ToDecodedTexture` carries the same ordered levels without generating replacements.
The existing `Rgba`, `Width` and `Height` accessors continue to describe level zero.
Four-bit rows now round a final half-byte upward before aligning the pitch: a
33-pixel row needs 17 bytes and occupies 32 bytes, rather than overlapping its next row.

The six-format synthetic fixture gives every authored level a different color,
uses non-power-of-two dimensions and sentinel padding, and verifies all decoded
pixels. Separate checks cover the final four-bit nibble and a truncated last mip.
The eight synthetic cases and one bounded retail case pass on Windows and Ubuntu
WSL alongside all 19 existing decoder checks. The coordinated locked build passes
with ordinary analyzers. The [validation receipt](validation/travels-fidelity-20260920.md)
records exact binaries and the separate Linux runtime boundary. No native viewer,
normalized adapter or export dispatch
acceptance follows from this decoder change.

The retail fixture is `ob_Rock2` in the original January 11, 2007 PSP `GR.ARC`:
entry 87 (`Hub_1`), resource zero, native raster 21. Only the 1,989,324-byte entry
is read. Exact entry, dictionary and raster hashes bind its identity. The 11,484-byte
raster body has SHA256
`4D24000295AF419E4F10AB4DB42E2FD5B3BD5E690EC1947B4E29FC4A50E24C75`.
Independent raw palette lookup establishes all eight RGBA hashes, from 128×128
through 1×1, pinned in `RwPspTextureMipRetailTests`. Both source nonmutation and
decoded ownership after overwriting the caller's input buffer are checked.

The [source review](complexity/psp-authored-mips-20260920.json) covers the changed
callables and their decoding dependencies. Time and retained pixels grow with the
sum of authored level sizes. The existing dimension and twelve-level limits bound
owned RGBA at 89,478,480 bytes; this is an arithmetic bound, not a measured memory
benchmark. Mip count still comes from exact encoded length and the inherited
twelve-level search. No texture resolution, sampler policy, coordinate basis or
cross-entry precedence is inferred here; those remain in the
[PSP material prerequisites](oblivion-psp-material-admission.md).
