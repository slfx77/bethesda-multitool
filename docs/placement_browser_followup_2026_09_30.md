# Placement browsing follow-up

Actual July-build GUI search for `00152406` returned no matches while CLI inspection worked. The Records tree included base definitions but omitted physical REFR/ACHR/ACRE occurrences.

The Records tree now adds a Placed References category from retained scan headers. Names, FormIDs and source/offset labels are searchable without decoding placement payloads. Selecting an occurrence invokes the shared `PlacementQuery` and `PlacementDetailBuilder`. A short-lived read-only mapping closes after the selected payload is read; source size/time changes require reopening the source.

Single-source and captured duplicates retain separate offsets. Explicit plugin orders show occurrences from the selected source, including ambiguous and deleted winners. Displayed links use the selected load-order namespace; the file-local FormID and physical source remain visible. Raw-offset navigation is available only for the open primary file. Ambiguous FormID links show the occurrence list for selection.

The same tree correction registers the typed IMAD list under Graphics. It previously looked only for generic IMAD records.

Six parameterized cases added to `PlacementInspectionTests` cover all three placement signatures, deferred reads, duplicate offsets, deleted/ambiguous winners, source selection and rebased links. Parent-coordinated managed/Windows builds and actual July GUI replay are pending.
