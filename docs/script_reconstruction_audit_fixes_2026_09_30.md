# Script reconstruction audit fixes (2026-09-30)

Status: implemented. The parent-coordinated first build passed; all 47 focused script cases passed (14 symbol resolution, 18 provenance, 15 export). The CTRF receipt is tests/BethesdaMultitool.Tests/bin/development/Release/net10.0/TestResults/first-batch.json. Broader integration checks remain coordinated by the parent session.

## Scope

- P2-T03: standalone SCPT and embedded INFO/TERM/PACK reconstructions share an explicit owner-link resolver. It follows serialized SCRI, runtime owner links and placed-reference base links. Quest mentions in SCRO and EditorID suffixes do not establish ownership. Missing, incomplete or conflicting ownership/local tables retain numeric slots.
- Owner scripts are parsed before dialogue/terminal decompilation. A final embedded-block pass supplies symbols to runtime-merged blocks. Direct parser entry points prepare symbols too.
- Each encountered external operand retains owner FormID, variable index, resolution status, candidate SCPTs and the resolved owner chain in shared models. Script export manifests and detail/report output expose the result. These are static binding observations, not execution traces.
- P2-T11: emitted reconstruction declarations consult SLSD/SCVR plus SCRV, never dot-use in reconstructed text. Reference and float locals sharing storage flag zero remain distinct. Raw flags and captured source are preserved.
- P2-T08: short reconstruction/source labels and banners. Machine provenance tokens, legacy emitted-banner recognition, and .gek naming remain compatible.

## Focused verification prepared

ScriptSymbolResolutionTests has 14 parameterized cases covering full/direct parser ordering, runtime INFO/TERM/PACK enrichment, both byte orders, owner present/missing/conflicting, and incomplete/conflicting local metadata.

ScriptSourceProvenanceTests checks three generations of reconstruction banners and stable tokens. ScriptExportWriterTests checks SCRV declarations beside unchanged stored .gek source, raw type flags, and external-variable manifest identity.

Existing detail/report/export tests have updated human-label expectations. Targeted real-corpus checks should revisit July INFO 000E7939 and its explicit owner script. No live execution claim is made by these changes.
