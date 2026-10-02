using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using BethesdaMultitool.Core.Formats.Esm.Inspection;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using Spectre.Console;

namespace BethesdaMultitool.CLI.Commands.Esm;

internal sealed record CellObjectsReport(string SourcePath, string CellQuery, uint CellFormId, long CellOffset,
    string? CellEditorId, string? CellName, uint? WorldspaceFormId, int? GridX, int? GridY,
    bool IncludePersistent, string? TypeFilter, int Limit, int TotalCount, int ReturnedCount, bool Truncated,
    IReadOnlyList<PlacementOccurrence> Objects)
{
    public IReadOnlyList<string> LoadOrder { get; init; } = [];
    public IReadOnlyList<string> MissingMasters { get; init; } = [];
    public IReadOnlyList<PlacementOccurrence> SelectionIssues { get; init; } = [];
    public string? CellSourcePath { get; init; }
    public uint? FileLocalCellFormId { get; init; }
    public int ExcludedCount => SelectionIssues.Count;
    public int SchemaVersion => 1;
    public string CoordinateUnits => "game units; rotations in radians";
    public string IdentifierScope => LoadOrder.Count == 0 ? "file-local FormIDs; offsets are physical file offsets" :
        "load-order FormIDs; fileLocal fields and offsets identify physical source records";
}

internal static class CellObjectsOutput
{
    internal static CellObjectsReport Create(string sourcePath, string cellQuery, CellRecord cell,
        bool includePersistent, string? typeFilter, int limit, IReadOnlyList<PlacementOccurrence> rows)
    {
        var selected = limit == 0 ? rows : rows.Take(limit).ToArray();
        return new CellObjectsReport(Path.GetFullPath(sourcePath), cellQuery, cell.FormId, cell.Offset, cell.EditorId,
            cell.FullName, cell.WorldspaceFormId, cell.GridX, cell.GridY, includePersistent, typeFilter, limit,
            rows.Count, selected.Count, selected.Count < rows.Count, selected);
    }

    internal static void Write(TextWriter writer, string format, CellObjectsReport report)
    {
        if (format == "json")
        {
            writer.WriteLine(JsonSerializer.Serialize(report, CellObjectsJsonContext.Default.CellObjectsReport));
            return;
        }
        if (format == "csv") { WriteCsv(writer, report); return; }
        var console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(writer), Ansi = AnsiSupport.No });
        console.WriteLine($"Cell 0x{report.CellFormId:X8} at 0x{report.CellOffset:X}: {report.CellEditorId} {report.CellName}");
        console.WriteLine($"Source: {report.CellSourcePath ?? report.SourcePath}");
        if (report.LoadOrder.Count > 0)
        {
            console.WriteLine($"Load order: {string.Join("; ", report.LoadOrder)}");
            console.WriteLine(report.IdentifierScope);
        }
        console.WriteLine($"Total: {report.TotalCount}; returned: {report.ReturnedCount}; truncated: {report.Truncated}");
        var table = new Table().Border(TableBorder.Rounded).AddColumn("Type").AddColumn("FormID")
            .AddColumn("Offset").AddColumn("Base").AddColumn("Instance / base EditorID")
            .AddColumn("Category").AddColumn("Position (X, Y, Z)").AddColumn("Flags");
        if (report.LoadOrder.Count > 0) table.AddColumn("Source");
        foreach (var row in report.Objects)
        {
            string[] values = [row.RecordType, Hex(row.FormId), $"0x{row.Offset:X}", Hex(row.BaseFormId),
                Markup.Escape($"{row.EditorId ?? "(none)"} / {row.BaseEditorId ?? "(unresolved)"}"),
                row.Category, $"{Number(row.X)}, {Number(row.Y)}, {Number(row.Z)}", $"0x{row.Flags:X8}{(row.IsPersistentOverlay ? " overlay" : "")}"];
            table.AddRow(report.LoadOrder.Count == 0 ? values : [.. values, Markup.Escape(Path.GetFileName(row.SourcePath))]);
        }
        console.Write(table);
        foreach (var issue in report.SelectionIssues)
            console.WriteLine($"{issue.SelectionStatus}: {Hex(issue.FormId)} {Path.GetFileName(issue.SourcePath)} at 0x{issue.Offset:X}");
        if (report.Truncated) { console.WriteLine("Use --limit 0 for all occurrences. JSON/CSV include rotation, enable-parent and provenance fields."); }
    }

    private static void WriteCsv(TextWriter writer, CellObjectsReport report)
    {
        writer.WriteLine("SourcePath,SourceKind,CellQuery,SelectedCellFormId,SelectedCellOffset,TotalCount,ReturnedCount,Truncated,IncludePersistent,TypeFilter,Limit,RecordType,FormId,Offset,IsBigEndian,Flags,EditorId,BaseFormId,BaseEditorId,BaseName,Category,ParentCellFormId,ParentCellOffset,WorldspaceFormId,AssignmentSource,X,Y,Z,RotX,RotY,RotZ,Scale,EnableParentFormId,EnableParentFlags,OppositeEnableParent,IsPersistent,IsInitiallyDisabled,IsDeleted,IsPersistentOverlay,PayloadStatus,SelectionStatus,FileLocalFormId,FileLocalBaseFormId,FileLocalParentCellFormId,FileLocalWorldspaceFormId,FileLocalEnableParentFormId,SelectedCellSourcePath,FileLocalSelectedCellFormId,ExcludedCount,IdentifierScope,LoadOrder,MissingMasters");
        foreach (var row in report.Objects.Concat(report.SelectionIssues))
        {
            string?[] values = [row.SourcePath, row.SourceKind, report.CellQuery, Hex(report.CellFormId),
                report.CellOffset.ToString(CultureInfo.InvariantCulture), report.TotalCount.ToString(CultureInfo.InvariantCulture),
                report.ReturnedCount.ToString(CultureInfo.InvariantCulture), report.Truncated.ToString(), report.IncludePersistent.ToString(),
                report.TypeFilter, report.Limit.ToString(CultureInfo.InvariantCulture), row.RecordType, Hex(row.FormId),
                row.Offset.ToString(CultureInfo.InvariantCulture), row.IsBigEndian.ToString(), $"0x{row.Flags:X8}", row.EditorId,
                Hex(row.BaseFormId), row.BaseEditorId, row.BaseName, row.Category, Hex(row.ParentCellFormId),
                row.ParentCellOffset?.ToString(CultureInfo.InvariantCulture), Hex(row.WorldspaceFormId), row.AssignmentSource,
                Number(row.X), Number(row.Y), Number(row.Z), Number(row.RotX), Number(row.RotY), Number(row.RotZ), Number(row.Scale),
                Hex(row.EnableParentFormId), row.EnableParentFlags.HasValue ? $"0x{row.EnableParentFlags:X2}" : null,
                row.OppositeEnableParent?.ToString(), row.IsPersistent.ToString(), row.IsInitiallyDisabled.ToString(), row.IsDeleted.ToString(),
                row.IsPersistentOverlay.ToString(), row.PayloadStatus, row.SelectionStatus, Hex(row.FileLocalFormId),
                Hex(row.FileLocalBaseFormId), Hex(row.FileLocalParentCellFormId), Hex(row.FileLocalWorldspaceFormId),
                Hex(row.FileLocalEnableParentFormId), report.CellSourcePath, Hex(report.FileLocalCellFormId),
                report.ExcludedCount.ToString(CultureInfo.InvariantCulture), report.IdentifierScope,
                string.Join(';', report.LoadOrder), string.Join(';', report.MissingMasters)];
            writer.WriteLine(string.Join(',', values.Select(EscapeCsv)));
        }
    }

    private static string Number(float? value) => value?.ToString("R", CultureInfo.InvariantCulture) ?? "";
    private static string Hex(uint? value) => value.HasValue ? $"0x{value:X8}" : "";
    private static string EscapeCsv(string? value) => value is null ? "" : value.IndexOfAny([',', '"', '\r', '\n']) < 0
        ? value : '"' + value.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
}

// The shipped CLI disables reflection-based serialization. Nonfinite captured floats are explicit
// JSON strings (NaN/Infinity), not fabricated zeroes; ordinary coordinates remain numeric.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true,
    NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals)]
[JsonSerializable(typeof(CellObjectsReport))]
internal partial class CellObjectsJsonContext : JsonSerializerContext;
