using System.Globalization;
using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Inspection;
using static BethesdaMultitool.Core.Formats.Esm.Presentation.RecordDetailHelpers;

namespace BethesdaMultitool.Core.Formats.Esm.Presentation;

internal static class PlacementDetailBuilder
{
    internal static RecordDetailModel Build(PlacementOccurrence row, FormIdResolver resolver) => Model(
        row.RecordType, row.FormId, row.EditorId, row.BaseName,
        [
            Section("Placement", [
                Scalar("Instance EditorID", row.EditorId), Link("Base object", row.BaseFormId, resolver),
                Scalar("Base EditorID", row.BaseEditorId), Link("Parent cell", row.ParentCellFormId, resolver),
                Scalar("Parent cell offset", row.ParentCellOffset.HasValue ? $"0x{row.ParentCellOffset:X}" : "unresolved"),
                Link("Worldspace", row.WorldspaceFormId, resolver),
                Scalar("Position", Vector(row.X, row.Y, row.Z)),
                Scalar("Rotation (radians)", Vector(row.RotX, row.RotY, row.RotZ)), Scalar("Scale", Number(row.Scale)),
                Scalar("Raw flags", $"0x{row.Flags:X8}"), Scalar("Persistent", row.IsPersistent.ToString()),
                Scalar("Initially disabled", row.IsInitiallyDisabled.ToString()), Scalar("Deleted", row.IsDeleted.ToString()),
                Link("Enable parent", row.EnableParentFormId, resolver),
                Scalar("Enable parent flags", row.EnableParentFlags.HasValue ? $"0x{row.EnableParentFlags:X2}" : "absent/unrecovered"),
                Scalar("Opposite enable-parent state", row.OppositeEnableParent?.ToString() ?? "unknown"),
                Scalar("Runtime enabled state", "Unavailable")]),
            Section("Provenance", [Scalar("Source", row.SourcePath), Scalar("Source kind", row.SourceKind),
                Scalar("File offset", $"0x{row.Offset:X}"), Scalar("Byte order", row.IsBigEndian ? "big-endian" : "little-endian"),
                Scalar("Cell association", row.AssignmentSource), Scalar("Payload", row.PayloadStatus)])
        ]);

    private static string Number(float? value) => value?.ToString("R", CultureInfo.InvariantCulture) ?? "unknown";
    private static string Vector(float? x, float? y, float? z) => $"({Number(x)}, {Number(y)}, {Number(z)})";
}
