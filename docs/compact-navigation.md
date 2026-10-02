# Compact Explore navigation

The native left navigation contains Data, Maps, and Assets under Explore. One
source toolbar serves the workspace. Native Records, Dialogue, Actors, Reports
and both map renderers remain mounted; Recovery tools retain independent inputs.
Settings is a separate destination. Background source updates do not select it
or another destination on the user's behalf.

The embedded asset gallery removes its duplicate source header and tab strip.
At the measured desktop size, the asset tree begins at 82 pixels instead of 198,
recovering 116 vertical pixels. Source controls fit at 850 by 650 pixels and export
commands wrap. Empty feedback is collapsed; results and errors explicitly update
both text and visibility so an unrealized text box can show its first message.

The raw carving-results type filter remains a checkbox dropdown. Records retains
search and sorting. Preview focus and recursive export selection remain separate.

The isolated implementation pins shared Core/WinUI packages to the immutable
`0.1.0-preview.20260909.10` version, with generated transitive locks. The final
Windows build has zero warnings/errors with application analyzers disabled.
Seven native checks pass: native selection controls, one source toolbar, retained
source and export selection, independent Recovery input, Settings return,
keyboard Enter, compact export controls and visible empty-selection feedback.
The check list groups related assertions; it is not full GUI coverage.

The preceding navigation bundle passes 46 focused published CLI commands and
the actor seed/level/inventory/source-replacement GUI suite. The subsequent
source change affects only export feedback presentation. Its first unsuccessful
native feedback check is retained, together with the corrected screenshot and
passing receipt. The final published bundle also passes all eight native
navigation/export checks, including byte-identical extraction of exactly one
checked PNG through the native destination picker, excluding unchecked assets.

The same final source passes 247 focused Linux tests and all 46 checks through
the self-contained published Linux binary. Locked restore and publish leave all
four dependency lock files unchanged. The Linux test build retains 70 existing
analyzer warnings. The immutable acceptance label is
`acceptance-20260909-bmt-navigation-v10-final`; the Linux executable SHA-256 is
`78e8a184db3b0a27dc70b08ba3b8d8c423d3b5f955180f18e309e902394890fb`.

Evidence is under the sibling shared repository's
`TestOutput/pending-navigation/`, including `bmt-native-feedback-v10/receipt.json`,
the wide/narrow/error screenshots, build logs, actor receipt, package locks and
published-command results. Existing source is preserved through conflict-checked
integration and backups. No public release is implied. Full game/GUI/platform,
localization, analyzer and immersive-dialogue coverage remains outstanding.
