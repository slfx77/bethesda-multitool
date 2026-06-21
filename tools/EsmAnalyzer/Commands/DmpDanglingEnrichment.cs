using BethesdaMultitool.Core;
using BethesdaMultitool.Core.Formats.Esm.Export;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Semantic;
using Spectre.Console;

namespace EsmAnalyzer.Commands;

/// <summary>
///     Builds the FormID→<see cref="ReferenceEnrichment" /> index for the attribute-dangling
///     pipeline by walking each supplied Sample ESM's parsed placed references.
/// </summary>
internal static class DmpDanglingEnrichment
{
    /// <summary>
    ///     Walks each supplied Sample ESM via the semantic loader and builds a FormID→enrichment
    ///     index from every parsed <see cref="PlacedReference" /> (in each cell's PlacedObjects,
    ///     in worldspace cells, and the flat <see cref="RecordCollection.MapMarkers" /> list).
    ///     Earlier ESMs win on conflict — later builds only fill gaps.
    /// </summary>
    public static async Task<IReadOnlyDictionary<uint, ReferenceEnrichment>> BuildEnrichmentIndexAsync(
        string[] esmPaths, CancellationToken ct)
    {
        var enrich = new Dictionary<uint, ReferenceEnrichment>();
        if (esmPaths.Length == 0)
        {
            return enrich;
        }

        foreach (var esmPath in esmPaths)
        {
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(esmPath))
            {
                AnsiConsole.MarkupLine($"[yellow]ESM not found, skipping:[/] {Markup.Escape(esmPath)}");
                continue;
            }

            AnsiConsole.MarkupLine($"[blue]Loading ESM for enrichment:[/] {Markup.Escape(esmPath)}");
            try
            {
                using var loaded = await SemanticFileLoader.LoadAsync(
                    esmPath,
                    new SemanticFileLoadOptions { FileType = AnalysisFileType.EsmFile },
                    ct);

                var resolver = loaded.Resolver;
                var added = 0;

                foreach (var cell in loaded.Records.Cells)
                {
                    foreach (var pr in cell.PlacedObjects)
                    {
                        if (TryAddEnrichment(enrich, pr, resolver))
                        {
                            added++;
                        }
                    }
                }
                foreach (var ws in loaded.Records.Worldspaces)
                {
                    foreach (var cell in ws.Cells)
                    {
                        foreach (var pr in cell.PlacedObjects)
                        {
                            if (TryAddEnrichment(enrich, pr, resolver))
                            {
                                added++;
                            }
                        }
                    }
                }
                foreach (var pr in loaded.Records.MapMarkers)
                {
                    if (TryAddEnrichment(enrich, pr, resolver))
                    {
                        added++;
                    }
                }

                AnsiConsole.MarkupLine($"  +{added:N0} enrichment entries (total: {enrich.Count:N0})");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"  [red]ERROR: {Markup.Escape(ex.Message)}[/]");
            }
        }

        return enrich;
    }

    private static bool TryAddEnrichment(
        Dictionary<uint, ReferenceEnrichment> enrich,
        PlacedReference pr,
        FormIdResolver resolver)
    {
        if (pr.FormId == 0 || enrich.ContainsKey(pr.FormId))
        {
            return false;
        }

        enrich[pr.FormId] = new ReferenceEnrichment(
            EditorId: string.IsNullOrEmpty(pr.EditorId) ? null : pr.EditorId,
            BaseFormId: pr.BaseFormId,
            BaseEditorId: pr.BaseEditorId ?? resolver.GetEditorId(pr.BaseFormId),
            BaseFullName: resolver.GetDisplayName(pr.BaseFormId),
            ModelPath: string.IsNullOrEmpty(pr.ModelPath) ? null : pr.ModelPath,
            RecordType: pr.RecordType,
            IsMapMarker: pr.IsMapMarker,
            MarkerName: string.IsNullOrEmpty(pr.MarkerName) ? null : pr.MarkerName,
            MarkerType: pr.MarkerType.HasValue ? (ushort)pr.MarkerType.Value : null);
        return true;
    }
}
