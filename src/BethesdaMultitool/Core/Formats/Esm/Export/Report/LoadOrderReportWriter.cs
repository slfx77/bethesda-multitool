using System.Globalization;
using System.Text;
using System.Text.Json;
using BethesdaMultitool.Core.Formats.Esm.Export.Geck;
using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Semantic.LoadOrder;

namespace BethesdaMultitool.Core.Formats.Esm.Export.Report;

/// <summary>Documents the namespace and physical evidence behind a merged typed report set.</summary>
internal static class LoadOrderReportWriter
{
    private static readonly string[] Limitations =
    [
        "Static authored records only; no gameplay reachability, runtime state, or engine-calculated statistics are inferred.",
        "Each top-level record comes from its unique last physical winner. Deleted winners, conflicting signatures, and multiple physical winners are excluded; see record_provenance.csv.",
        "A winner absent from the typed parser never falls back to an older typed record. Included in the typed view does not mean every field has a dedicated report column.",
        "Typed FormID fields and supported references use the explicit load-order namespace. Stored/decompiled source text, raw bytes, opaque fields, and record offsets remain source-local evidence.",
        "Parser-derived annotations are not proof of authored fields or gameplay. Quest variable names are rebound to the winning linked SCPT; related NPC lists are rebuilt from selected INFO speaker attribution. AMMO projectile links use direct serialized data, without per-source weapon inference. Cached weapon projectile physics are omitted; inspect the winning PROJ reports instead.",
        "CELL/WRLD, LAND and placed children are selected independently. LAND attaches by physical parent only; multiple live LAND identities for one cell remain unresolved. Components and source offsets are in world_components.csv and selected_placements.csv. world_inheritance.csv records PNAM routes and lighting field selection. Water rendering and asset availability need separate validation.",
        "Dialogue trees are rebuilt from selected INFO/DIAL records. dialogue_links.csv distinguishes serialized, typed, inferred and observed edges. PNAM constraints guide display order; dangling links, forks, cycles and unconstrained ties are in dialogue_ordering.csv. Display order is not engine order or reachability.",
        "Missing masters have reserved slots and remain unresolved; absence from a supplied or parsed source is not proof of content absence."
    ];

    internal static Dictionary<string, string> Generate(LoadOrderReportView view, string toolVersion)
    {
        var reports = GeckReportGenerator.GenerateAllReports(new ReportDataSources(view.Records));
        var context = new StringBuilder();
        context.AppendLine("Reconstruction — explicit load-order FormIDs.");
        context.AppendLine("Sources and limitations: report_sources.json; physical records: record_provenance.csv.");
        context.AppendLine();
        // CSVs keep their machine-readable header as the first row. Every text report carries the
        // namespace notice, so an individually copied terminal/quest report remains interpretable.
        var contextText = context.ToString();
        foreach (var name in reports.Keys.Where(name => name.EndsWith(".txt", StringComparison.Ordinal)).ToArray())
        {
            reports[name] = contextText + reports[name];
        }
        foreach (var (name, content) in SelectedViewReportWriter.Generate(view.Selection)) { reports[name] = content; }
        reports["record_provenance.csv"] = WriteRecords(view);
        reports["report_sources.json"] = WriteManifest(view, toolVersion, reports.Keys.Append("report_sources.json"));
        return reports;
    }

    internal static Dictionary<string, string> GenerateProvenance(LoadOrderSelectionView selection, string toolVersion)
    {
        var view = new LoadOrderReportView(selection);
        var reports = SelectedViewReportWriter.Generate(selection);
        reports["record_provenance.csv"] = WriteRecords(view);
        reports["report_sources.json"] = WriteManifest(view, toolVersion, reports.Keys.Append("report_sources.json"));
        return reports;
    }

    private static string WriteRecords(LoadOrderReportView view)
    {
        var csv = new StringBuilder("LoadOrderFormID,OwnerPlugin,WinnerPlugin,Status,VersionPlugin,FileLocalFormID,Signature,EditorID,Flags,Offset,IsWinningPlugin,ClampedLocalIndex\n");
        foreach (var identity in view.Index.Records.Values.OrderBy(r => r.LoadOrderFormId))
        {
            foreach (var version in identity.Versions)
            {
                csv.AppendLine(string.Join(",", Fmt.FIdAlways(identity.LoadOrderFormId),
                    Fmt.CsvEscape(identity.OwnerPlugin), Fmt.CsvEscape(identity.Winner.Plugin), view.Status(identity),
                    Fmt.CsvEscape(version.Plugin), Fmt.FIdAlways(version.FileLocalFormId), version.Signature,
                    Fmt.CsvEscape(version.EditorId), $"0x{version.Flags:X8}", version.Offset.ToString(CultureInfo.InvariantCulture),
                    version.Plugin.Equals(identity.Winner.Plugin, StringComparison.OrdinalIgnoreCase) ? "true" : "false",
                    version.WasClamped ? "true" : "false"));
            }
        }
        return csv.ToString();
    }

    private static string WriteManifest(LoadOrderReportView view, string toolVersion, IEnumerable<string> files)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            json.WriteString("schema", "bethesda-multitool/load-order-reports");
            json.WriteNumber("schemaVersion", 2);
            json.WriteString("toolVersion", toolVersion);
            json.WriteString("formIdNamespace", "explicit-load-order");
            json.WriteString("recordSelection", "unique-live-physical-winners");
            json.WriteStartArray("loadOrder");
            foreach (var entry in view.Order.Entries)
            {
                json.WriteStartObject();
                json.WriteString("plugin", entry.Name);
                json.WriteString("path", entry.Path);
                json.WriteNumber("index", entry.Index);
                json.WriteStartArray("masters");
                foreach (var master in entry.Masters) { json.WriteStringValue(master); }
                json.WriteEndArray();
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteStartArray("missingMasters");
            for (var i = 0; i < view.Order.MissingMasters.Count; i++)
            {
                json.WriteStartObject();
                json.WriteString("plugin", view.Order.MissingMasters[i]);
                json.WriteNumber("reservedIndex", view.Order.Entries.Count + i);
                json.WriteBoolean("loaded", false);
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteStartObject("recordCounts");
            json.WriteNumber("physicalVersions", view.Index.Records.Values.Sum(r => r.Versions.Count));
            json.WriteNumber("identities", view.Index.Records.Count);
            foreach (var status in view.Index.Records.Values.GroupBy(view.Status).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                json.WriteNumber(status.Key, status.Count());
            }
            json.WriteEndObject();
            json.WriteStartArray("limitations");
            foreach (var limitation in Limitations) { json.WriteStringValue(limitation); }
            json.WriteEndArray();
            json.WriteStartArray("files");
            foreach (var file in files.Order(StringComparer.Ordinal)) { json.WriteStringValue(file); }
            json.WriteEndArray();
            json.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
