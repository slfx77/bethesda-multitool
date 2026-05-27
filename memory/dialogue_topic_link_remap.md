# Dialogue Topic Link Remap Root Cause

Date: 2026-05-24

The flat dialogue menu failure was not a missing DIAL/INFO nesting problem. New DIAL and INFO
records were nested in the ESP, but INFO topic-edge subrecords still referenced source DIAL
FormIDs from the dump:

- `TCLT` / `TCLF` topic links
- INFO `NAME` add-topic references
- related topic references passed through `InfoEncoder`

`DialogGrupBuilder` allocated new FormIDs for new DIAL records, but that source-DIAL to
allocated-DIAL map was not merged into the sanitizer/encoder remap table. The sanitizer treated
those source topic references as dangling and dropped them, leaving only broad synthetic GREETING
links. In game this surfaced as a flat list of possible topics instead of the intended dialogue
tree.

Fix summary:

- Merge the local DIAL allocation map into the dialog remap table before sanitizing INFO links.
- Pass the merged remap table through `SanitizeDialReferences`, `SanitizeInfoReferences`, and
  `InfoEncoder.EncodeNew`.
- Narrow synthetic GREETING entry links to inferred root topics for each `(speaker, quest)` pair
  instead of linking every emitted topic under the quest.

Verification on `TestOutput/dialogue-fix-xex43.esp`:

- Ulysses synthetic GREETING links dropped from 97 broad links to 23 inferred roots.
- Ulysses real topic INFOs now retain topic edges: 30 INFOs with `TCLT`, 98 total `TCLT` links.
- Focused `DialogGrupBuilderTests` pass.
