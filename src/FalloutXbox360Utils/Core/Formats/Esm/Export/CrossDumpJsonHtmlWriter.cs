using System.Text;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

/// <summary>
///     Generates static HTML comparison pages with embedded compressed JSON data,
///     rendered client-side by JavaScript. Consumes <see cref="CrossDumpRecordIndex.StructuredRecords" />
///     instead of pre-formatted text, enabling field-level diff computed in the browser.
/// </summary>
internal static class CrossDumpJsonHtmlWriter
{
    private const int DefaultMaxInlineCompressedPayloadLength = 8 * 1024 * 1024;
    private const int DefaultMaxCellJsonPayloadLength = 96 * 1024 * 1024;
    private const int DefaultChunkedRecordCountThreshold = 10_000;
    private const int DefaultUngroupedChunkedRecordCountThreshold = 5_000;
    private const int PayloadChunkSize = 512 * 1024;
    private const string ExternalChunkGlobal = "__comparisonExternalChunks";

    /// <summary>
    ///     Generate all HTML files: one per record type (with embedded JSON) plus an index page.
    /// </summary>
    internal static Dictionary<string, string> GenerateAll(
        CrossDumpRecordIndex index,
        int maxInlineCompressedPayloadLength = DefaultMaxInlineCompressedPayloadLength,
        int maxCellJsonPayloadLength = DefaultMaxCellJsonPayloadLength)
    {
        return GenerateFiles(index, maxInlineCompressedPayloadLength, maxCellJsonPayloadLength)
            .ToDictionary(file => file.Filename, file => file.Html, StringComparer.OrdinalIgnoreCase);
    }

    internal static IEnumerable<(string Filename, string Html)> GenerateFiles(
        CrossDumpRecordIndex index,
        int maxInlineCompressedPayloadLength = DefaultMaxInlineCompressedPayloadLength,
        int maxCellJsonPayloadLength = DefaultMaxCellJsonPayloadLength)
    {
        foreach (var context in CrossDumpRecordTypePageContext.Enumerate(index))
        {
            foreach (var (filename, html) in GenerateRecordTypeFiles(
                         context.RecordType,
                         context.FormIdMap,
                         index.Dumps,
                         context.Groups,
                         context.AlternateGroups,
                         context.DefaultGroupMode,
                         context.Metadata,
                         context.CellGridCoords,
                         maxInlineCompressedPayloadLength,
                         maxCellJsonPayloadLength))
            {
                yield return (filename, html);
            }
        }

        yield return ("index.html", CrossDumpComparisonIndexPageBuilder.Generate(index));
    }

    internal static Task<IReadOnlyList<string>> WriteFilesAsync(
        CrossDumpRecordIndex index,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        return WriteFilesAsync(
            index,
            outputPath,
            DefaultMaxInlineCompressedPayloadLength,
            DefaultMaxCellJsonPayloadLength,
            cancellationToken);
    }

    internal static async Task<IReadOnlyList<string>> WriteFilesAsync(
        CrossDumpRecordIndex index,
        string outputPath,
        int maxInlineCompressedPayloadLength = DefaultMaxInlineCompressedPayloadLength,
        int maxCellJsonPayloadLength = DefaultMaxCellJsonPayloadLength,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(outputPath);

        var writtenFiles = new List<string>();
        foreach (var context in CrossDumpRecordTypePageContext.Enumerate(index))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var outputFile = Path.Combine(outputPath, context.OutputFilename);
            await WriteRecordTypeFileAsync(
                outputFile,
                context.RecordType,
                context.FormIdMap,
                index.Dumps,
                context.Groups,
                context.AlternateGroups,
                context.DefaultGroupMode,
                context.Metadata,
                context.CellGridCoords,
                maxInlineCompressedPayloadLength,
                maxCellJsonPayloadLength,
                cancellationToken);
            writtenFiles.Add(outputFile);
        }

        var indexFile = Path.Combine(outputPath, "index.html");
        await File.WriteAllTextAsync(indexFile, CrossDumpComparisonIndexPageBuilder.Generate(index), cancellationToken);
        writtenFiles.Add(indexFile);

        return writtenFiles;
    }

    internal static async Task<string?> WriteRecordTypeFileAsync(
        CrossDumpRecordIndex index,
        string recordType,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        return await WriteRecordTypeFileAsync(
            index,
            recordType,
            outputPath,
            DefaultMaxInlineCompressedPayloadLength,
            DefaultMaxCellJsonPayloadLength,
            cancellationToken);
    }

    internal static async Task<string?> WriteRecordTypeFileAsync(
        CrossDumpRecordIndex index,
        string recordType,
        string outputPath,
        int maxInlineCompressedPayloadLength = DefaultMaxInlineCompressedPayloadLength,
        int maxCellJsonPayloadLength = DefaultMaxCellJsonPayloadLength,
        CancellationToken cancellationToken = default)
    {
        var context = CrossDumpRecordTypePageContext.TryCreate(index, recordType);
        if (context == null)
        {
            return null;
        }

        Directory.CreateDirectory(outputPath);

        var outputFile = Path.Combine(outputPath, context.OutputFilename);
        await WriteRecordTypeFileAsync(
            outputFile,
            context.RecordType,
            context.FormIdMap,
            index.Dumps,
            context.Groups,
            context.AlternateGroups,
            context.DefaultGroupMode,
            context.Metadata,
            context.CellGridCoords,
            maxInlineCompressedPayloadLength,
            maxCellJsonPayloadLength,
            cancellationToken);

        return outputFile;
    }

    internal static async Task<string> WriteIndexPageAsync(
        IReadOnlyList<DumpSnapshot> dumps,
        IReadOnlyList<CrossDumpRecordTypeSummary> recordTypes,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(outputPath);

        var indexFile = Path.Combine(outputPath, "index.html");
        await File.WriteAllTextAsync(
            indexFile,
            CrossDumpComparisonIndexPageBuilder.Generate(dumps, recordTypes),
            cancellationToken);
        return indexFile;
    }

    private static Dictionary<string, string> GenerateRecordTypeFiles(
        string recordType,
        Dictionary<uint, Dictionary<int, RecordReport>> formIdMap,
        List<DumpSnapshot> dumps,
        Dictionary<uint, string>? groups,
        Dictionary<uint, string>? alternateGroups,
        string? defaultGroupMode,
        Dictionary<uint, Dictionary<string, string>>? metadata,
        Dictionary<uint, (int X, int Y)>? cellGridCoords,
        int maxInlineCompressedPayloadLength,
        int maxCellJsonPayloadLength)
    {
        var files = new Dictionary<string, string>();
        var chunkGroups = groups;
        var forceChunkedPage = false;
        if (chunkGroups == null && ShouldUseUngroupedChunkedPageBeforePayload(formIdMap))
        {
            chunkGroups = BuildSingleChunkGroup(formIdMap, "All Records");
            forceChunkedPage = true;
        }

        if (forceChunkedPage || ShouldUseChunkedPageBeforePayload(chunkGroups, formIdMap))
        {
            files[$"compare_{recordType.ToLowerInvariant()}.html"] = GenerateChunkedPage(
                recordType, formIdMap, dumps, chunkGroups!, cellGridCoords, metadata);
            return files;
        }

        var payload = BuildCompressedPayload(
            formIdMap,
            dumps,
            recordType,
            groups,
            alternateGroups,
            defaultGroupMode,
            metadata,
            cellGridCoords);

        if (ShouldUseChunkedPage(groups, payload,
                maxInlineCompressedPayloadLength, maxCellJsonPayloadLength))
        {
            files[$"compare_{recordType.ToLowerInvariant()}.html"] = GenerateChunkedPage(
                recordType, formIdMap, dumps, groups!, cellGridCoords, metadata);
            return files;
        }

        files[$"compare_{recordType.ToLowerInvariant()}.html"] = GenerateRecordTypePage(
            recordType,
            formIdMap,
            dumps,
            alternateGroups,
            defaultGroupMode,
            payload.CompressedPayload);
        return files;
    }

    private static async Task WriteRecordTypeFileAsync(
        string outputFile,
        string recordType,
        Dictionary<uint, Dictionary<int, RecordReport>> formIdMap,
        List<DumpSnapshot> dumps,
        Dictionary<uint, string>? groups,
        Dictionary<uint, string>? alternateGroups,
        string? defaultGroupMode,
        Dictionary<uint, Dictionary<string, string>>? metadata,
        Dictionary<uint, (int X, int Y)>? cellGridCoords,
        int maxInlineCompressedPayloadLength,
        int maxCellJsonPayloadLength,
        CancellationToken cancellationToken)
    {
        var chunkGroups = groups;
        var forceChunkedPage = false;
        if (chunkGroups == null && ShouldUseUngroupedChunkedPageBeforePayload(formIdMap))
        {
            chunkGroups = BuildSingleChunkGroup(formIdMap, "All Records");
            forceChunkedPage = true;
        }

        if (forceChunkedPage || ShouldUseChunkedPageBeforePayload(chunkGroups, formIdMap))
        {
            await WriteChunkedPageAsync(
                outputFile,
                recordType,
                formIdMap,
                dumps,
                chunkGroups!,
                cellGridCoords,
                metadata,
                cancellationToken);
            return;
        }

        var payload = BuildCompressedPayload(
            formIdMap,
            dumps,
            recordType,
            groups,
            alternateGroups,
            defaultGroupMode,
            metadata,
            cellGridCoords);

        if (ShouldUseChunkedPage(groups, payload,
                maxInlineCompressedPayloadLength, maxCellJsonPayloadLength))
        {
            await WriteChunkedPageAsync(
                outputFile,
                recordType,
                formIdMap,
                dumps,
                groups!,
                cellGridCoords,
                metadata,
                cancellationToken);
            return;
        }

        await WriteRecordTypePageAsync(
            outputFile,
            recordType,
            formIdMap,
            dumps,
            alternateGroups,
            defaultGroupMode,
            payload.CompressedPayload,
            cancellationToken);
    }

    private static string GenerateRecordTypePage(
        string recordType,
        Dictionary<uint, Dictionary<int, RecordReport>> formIdMap,
        List<DumpSnapshot> dumps,
        Dictionary<uint, string>? alternateGroups,
        string? defaultGroupMode,
        string compressedPayload)
    {
        var sb = new StringBuilder();
        var pageTitle = $"{recordType} \u2014 Cross-Build Comparison";
        var pageSummary = $"{dumps.Count} builds, {formIdMap.Count:N0} records";

        ComparisonHtmlHelpers.AppendHtmlHeader(sb, pageTitle);

        // Initial column visibility — hide columns 3+ until nav buttons change it
        if (dumps.Count > 3)
        {
            sb.Append("  <style id=\"build-col-style\">");
            for (var i = 3; i < dumps.Count; i++)
                sb.Append($".build-col-{i}{{display:none !important}}");
            sb.AppendLine("</style>");
        }

        sb.AppendLine(
            $"  <h1>{ComparisonHtmlHelpers.Esc(pageTitle)} </h1>");
        sb.AppendLine($"  <p class=\"summary\">{ComparisonHtmlHelpers.Esc(pageSummary)}</p>");

        // Navigation + controls
        sb.AppendLine("  <div class=\"controls\">");
        sb.AppendLine("    <a href=\"index.html\">&larr; Back to index</a>");
        sb.AppendLine(
            "    <input type=\"text\" id=\"search\" placeholder=\"Search by FormID, EditorID, or name...\" oninput=\"filterRows()\">");
        sb.AppendLine("    <button onclick=\"expandAll()\">Expand All</button>");
        sb.AppendLine("    <button onclick=\"collapseAll()\">Collapse All</button>");
        sb.AppendLine("    <span id=\"matchCount\" class=\"match-count\"></span>");

        // Dialogue-specific: group-mode radio buttons
        if (alternateGroups != null && defaultGroupMode != null)
        {
            sb.AppendLine("    <div class=\"group-mode-selector\">");
            sb.AppendLine("      <label>Group by:</label>");
            sb.AppendLine(
                "      <label><input type=\"radio\" name=\"groupMode\" value=\"Quest\" checked onchange=\"switchGroupMode(this.value)\"> Quest</label>");
            sb.AppendLine(
                "      <label><input type=\"radio\" name=\"groupMode\" value=\"NPC\" onchange=\"switchGroupMode(this.value)\"> NPC</label>");
            sb.AppendLine("    </div>");
        }

        if (dumps.Count > 3)
        {
            sb.AppendLine(
                $"    <div class=\"build-nav\" data-total=\"{dumps.Count}\" data-start=\"0\" data-size=\"3\">");
            sb.AppendLine("      <button onclick=\"navBuilds('first')\">&laquo; First</button>");
            sb.AppendLine("      <button onclick=\"navBuilds('prev3')\">&lsaquo; 3</button>");
            sb.AppendLine("      <button onclick=\"navBuilds('prev1')\">&lsaquo; 1</button>");
            sb.AppendLine(
                $"      <span class=\"build-nav-label\">Builds 1\u20133 of {dumps.Count}</span>");
            sb.AppendLine("      <button onclick=\"navBuilds('next1')\">1 &rsaquo;</button>");
            sb.AppendLine("      <button onclick=\"navBuilds('next3')\">3 &rsaquo;</button>");
            sb.AppendLine("      <button onclick=\"navBuilds('last')\">&raquo; Last</button>");
            sb.AppendLine("    </div>");
        }

        sb.AppendLine("  </div>");

        // Loading indicator (shown until JS hydration completes)
        sb.AppendLine("  <div id=\"loading\">Loading records...</div>");

        // Table skeleton — JS will populate the tbody
        sb.AppendLine("  <div id=\"tables-container\"></div>");

        // Embed compressed JSON data in chunked script text blocks rather than a giant
        // HTML attribute. Large attribute values can be truncated by browsers/DOM parsing,
        // which surfaces later as "Unexpected end of JSON input" during hydration.
        AppendPayloadScripts(sb, compressedPayload);

        // JavaScript renderer
        sb.AppendLine($"  <script>{ComparisonJsRenderer.Script}</script>");
        ComparisonHtmlHelpers.AppendHtmlFooter(sb);
        return sb.ToString();
    }

    private static async Task WriteRecordTypePageAsync(
        string outputFile,
        string recordType,
        Dictionary<uint, Dictionary<int, RecordReport>> formIdMap,
        List<DumpSnapshot> dumps,
        Dictionary<uint, string>? alternateGroups,
        string? defaultGroupMode,
        string compressedPayload,
        CancellationToken cancellationToken)
    {
        await using var stream = CreateHtmlFileStream(outputFile);
        await using var writer = CreateHtmlWriter(stream);

        var pageTitle = $"{recordType} \u2014 Cross-Build Comparison";
        var pageSummary = $"{dumps.Count} builds, {formIdMap.Count:N0} records";

        await WriteHtmlHeaderAsync(writer, pageTitle, cancellationToken);
        await WriteBuildColumnStyleAsync(writer, dumps.Count, cancellationToken);

        await WriteLineAsync(
            writer,
            $"  <h1>{ComparisonHtmlHelpers.Esc(pageTitle)} </h1>",
            cancellationToken);
        await WriteLineAsync(
            writer,
            $"  <p class=\"summary\">{ComparisonHtmlHelpers.Esc(pageSummary)}</p>",
            cancellationToken);

        await WriteControlsAsync(
            writer,
            dumps.Count,
            alternateGroups != null && defaultGroupMode != null,
            cancellationToken);

        await WriteLineAsync(writer, "  <div id=\"loading\">Loading records...</div>", cancellationToken);
        await WriteLineAsync(writer, "  <div id=\"tables-container\"></div>", cancellationToken);

        await WritePayloadScriptsAsync(writer, compressedPayload, cancellationToken);

        await WriteLineAsync(writer, $"  <script>{ComparisonJsRenderer.Script}</script>", cancellationToken);
        await WriteHtmlFooterAsync(writer, cancellationToken);
    }

    private static bool ShouldUseChunkedPage(
        Dictionary<uint, string>? groups,
        PayloadBundle payload,
        int maxInlineCompressedPayloadLength,
        int maxCellJsonPayloadLength)
    {
        return groups is { Count: > 0 }
               && (payload.CompressedPayload.Length > maxInlineCompressedPayloadLength
                   || payload.JsonLength > maxCellJsonPayloadLength);
    }

    private static bool ShouldUseChunkedPageBeforePayload(
        Dictionary<uint, string>? groups,
        Dictionary<uint, Dictionary<int, RecordReport>> formIdMap)
    {
        return groups != null && formIdMap.Count >= DefaultChunkedRecordCountThreshold;
    }

    private static bool ShouldUseUngroupedChunkedPageBeforePayload(
        Dictionary<uint, Dictionary<int, RecordReport>> formIdMap)
    {
        return formIdMap.Count >= DefaultUngroupedChunkedRecordCountThreshold;
    }

    private static Dictionary<uint, string> BuildSingleChunkGroup(
        Dictionary<uint, Dictionary<int, RecordReport>> formIdMap,
        string label)
    {
        var groups = new Dictionary<uint, string>(formIdMap.Count);
        foreach (var formId in formIdMap.Keys)
            groups[formId] = label;

        return groups;
    }

    private static PayloadBundle BuildCompressedPayload(
        Dictionary<uint, Dictionary<int, RecordReport>> formIdMap,
        List<DumpSnapshot> dumps,
        string recordType,
        Dictionary<uint, string>? groups,
        Dictionary<uint, string>? alternateGroups,
        string? defaultGroupMode,
        Dictionary<uint, Dictionary<string, string>>? metadata,
        Dictionary<uint, (int X, int Y)>? cellGridCoords)
    {
        var jsonBlob = ComparisonJsonBlobBuilder.Build(
            formIdMap,
            dumps,
            recordType,
            groups,
            alternateGroups,
            defaultGroupMode,
            metadata,
            cellGridCoords);
        return new PayloadBundle(
            ComparisonHtmlHelpers.CompressToBase64(jsonBlob),
            Encoding.UTF8.GetByteCount(jsonBlob));
    }

    /// <summary>
    ///     Generate a single HTML page with per-group chunked data.
    ///     Each group's records are in a separate compressed script tag, loaded on demand
    ///     when the group is expanded. This keeps any single JSON parse under 5MB.
    /// </summary>
    private static string GenerateChunkedPage(
        string recordType,
        Dictionary<uint, Dictionary<int, RecordReport>> formIdMap,
        List<DumpSnapshot> dumps,
        Dictionary<uint, string> groups,
        Dictionary<uint, (int X, int Y)>? cellGridCoords,
        Dictionary<uint, Dictionary<string, string>>? metadata)
    {
        // Build compressed index and per-group chunks without retaining raw JSON blobs.
        var (compressedIndexPayload, chunks) = ComparisonJsonBlobBuilder.BuildChunked(
            formIdMap, dumps, recordType, groups, cellGridCoords, metadata);

        var sb = new StringBuilder();
        var pageTitle = $"{recordType} \u2014 Cross-Build Comparison";
        ComparisonHtmlHelpers.AppendHtmlHeader(sb, pageTitle);

        if (dumps.Count > 3)
        {
            sb.Append("  <style id=\"build-col-style\">");
            for (var i = 3; i < dumps.Count; i++)
                sb.Append($".build-col-{i}{{display:none !important}}");
            sb.AppendLine("</style>");
        }

        sb.AppendLine($"  <h1>{ComparisonHtmlHelpers.Esc(pageTitle)}</h1>");
        var chunkSummary = groups.Values.All(value => string.Equals(value, "All Records", StringComparison.Ordinal))
            ? "chunked"
            : "chunked by group";
        sb.AppendLine(
            $"  <p class=\"summary\">{dumps.Count} builds, {formIdMap.Count:N0} records ({chunkSummary})</p>");

        // Controls
        sb.AppendLine("  <div class=\"controls\">");
        sb.AppendLine("    <a href=\"index.html\">&larr; Back to index</a>");
        sb.AppendLine(
            "    <input type=\"text\" id=\"search\" placeholder=\"Search by FormID, EditorID, or name...\" oninput=\"filterRows()\">");
        sb.AppendLine("    <button onclick=\"expandAll()\">Expand All</button>");
        sb.AppendLine("    <button onclick=\"collapseAll()\">Collapse All</button>");
        sb.AppendLine("    <span id=\"matchCount\" class=\"match-count\"></span>");

        if (dumps.Count > 3)
        {
            sb.AppendLine(
                $"    <div class=\"build-nav\" data-total=\"{dumps.Count}\" data-start=\"0\" data-size=\"3\">");
            sb.AppendLine("      <button onclick=\"navBuilds('first')\">&laquo; First</button>");
            sb.AppendLine("      <button onclick=\"navBuilds('prev3')\">&lsaquo; 3</button>");
            sb.AppendLine("      <button onclick=\"navBuilds('prev1')\">&lsaquo; 1</button>");
            sb.AppendLine(
                $"      <span class=\"build-nav-label\">Builds 1\u20133 of {dumps.Count}</span>");
            sb.AppendLine("      <button onclick=\"navBuilds('next1')\">1 &rsaquo;</button>");
            sb.AppendLine("      <button onclick=\"navBuilds('next3')\">3 &rsaquo;</button>");
            sb.AppendLine("      <button onclick=\"navBuilds('last')\">&raquo; Last</button>");
            sb.AppendLine("    </div>");
        }

        sb.AppendLine("  </div>");

        sb.AppendLine("  <div id=\"loading\">Loading index...</div>");
        sb.AppendLine("  <div id=\"tables-container\"></div>");

        // Index blob (small — just metadata, groups, gridCoords)
        AppendPayloadScripts(sb, compressedIndexPayload);

        // Per-group chunk blobs
        for (var i = 0; i < chunks.Count; i++)
        {
            var (groupKey, compressedChunkPayload) = chunks[i];
            var chunkId = $"chunk-{i}";
            sb.AppendLine(
                $"  <script type=\"application/json\" id=\"{chunkId}\" " +
                $"data-group=\"{ComparisonHtmlHelpers.Esc(groupKey)}\" " +
                $"data-z=\"{compressedChunkPayload}\"></script>");
        }

        sb.AppendLine($"  <script>{ComparisonJsRenderer.Script}</script>");
        ComparisonHtmlHelpers.AppendHtmlFooter(sb);
        return sb.ToString();
    }

    private static async Task WriteChunkedPageAsync(
        string outputFile,
        string recordType,
        Dictionary<uint, Dictionary<int, RecordReport>> formIdMap,
        List<DumpSnapshot> dumps,
        Dictionary<uint, string> groups,
        Dictionary<uint, (int X, int Y)>? cellGridCoords,
        Dictionary<uint, Dictionary<string, string>>? metadata,
        CancellationToken cancellationToken)
    {
        await using var stream = CreateHtmlFileStream(outputFile);
        await using var writer = CreateHtmlWriter(stream);
        var chunkDirectoryName = $"{Path.GetFileNameWithoutExtension(outputFile)}_chunks";
        var chunkDirectory = Path.Combine(Path.GetDirectoryName(outputFile) ?? ".", chunkDirectoryName);
        Directory.CreateDirectory(chunkDirectory);

        var compressedIndexPayload = ComparisonJsonBlobBuilder.BuildChunkedIndex(
            formIdMap, dumps, recordType, groups, cellGridCoords);

        var pageTitle = $"{recordType} \u2014 Cross-Build Comparison";
        await WriteHtmlHeaderAsync(writer, pageTitle, cancellationToken);
        await WriteBuildColumnStyleAsync(writer, dumps.Count, cancellationToken);

        await WriteLineAsync(
            writer,
            $"  <h1>{ComparisonHtmlHelpers.Esc(pageTitle)}</h1>",
            cancellationToken);
        var chunkSummary = groups.Values.All(value => string.Equals(value, "All Records", StringComparison.Ordinal))
            ? "chunked"
            : "chunked by group";
        await WriteLineAsync(
            writer,
            $"  <p class=\"summary\">{dumps.Count} builds, {formIdMap.Count:N0} records ({chunkSummary})</p>",
            cancellationToken);

        await WriteControlsAsync(writer, dumps.Count, false, cancellationToken);

        await WriteLineAsync(writer, "  <div id=\"loading\">Loading index...</div>", cancellationToken);
        await WriteLineAsync(writer, "  <div id=\"tables-container\"></div>", cancellationToken);

        await WritePayloadScriptsAsync(writer, compressedIndexPayload, cancellationToken);

        var chunkIndex = 0;
        foreach (var (groupKey, compressedChunkPayload) in ComparisonJsonBlobBuilder.BuildChunkPayloads(
                     formIdMap, recordType, groups, metadata))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var chunkId = $"chunk-{chunkIndex++}";
            var chunkFileName = $"{chunkId}.js";
            var chunkRelativePath = $"{chunkDirectoryName}/{chunkFileName}";
            await WriteExternalChunkScriptAsync(
                Path.Combine(chunkDirectory, chunkFileName),
                chunkId,
                compressedChunkPayload,
                cancellationToken);

            await WriteLineAsync(
                writer,
                $"  <script type=\"application/json\" id=\"{chunkId}\" " +
                $"data-group=\"{ComparisonHtmlHelpers.Esc(groupKey)}\" " +
                $"data-external-key=\"{chunkId}\" " +
                $"data-src=\"{chunkRelativePath}\"></script>",
                cancellationToken);
            await WriteLineAsync(
                writer,
                $"  <script src=\"{chunkRelativePath}\"></script>",
                cancellationToken);
        }

        await WriteLineAsync(writer, $"  <script>{ComparisonJsRenderer.Script}</script>", cancellationToken);
        await WriteHtmlFooterAsync(writer, cancellationToken);
    }

    private static async Task WriteExternalChunkScriptAsync(
        string outputFile,
        string chunkId,
        string compressedChunkPayload,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            outputFile,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            1024 * 1024,
            true);
        await using var writer = CreateHtmlWriter(stream);
        await WriteAsync(
            writer,
            $"window.{ExternalChunkGlobal}=window.{ExternalChunkGlobal}||{{}};window.{ExternalChunkGlobal}[\"{chunkId}\"]=\"",
            cancellationToken);
        await writer.WriteAsync(compressedChunkPayload.AsMemory(), cancellationToken);
        await WriteLineAsync(writer, "\";", cancellationToken);
    }

    internal static CrossDumpRecordTypeSummary BuildRecordTypeSummary(
        string recordType,
        Dictionary<uint, Dictionary<int, RecordReport>> formIdMap,
        int dumpCount)
    {
        return CrossDumpComparisonIndexPageBuilder.BuildRecordTypeSummary(recordType, formIdMap, dumpCount);
    }

    private static void AppendPayloadScripts(StringBuilder sb, string compressed)
    {
        if (compressed.Length <= PayloadChunkSize)
        {
            sb.AppendLine(
                $"  <script type=\"application/octet-stream\" id=\"record-data\">{compressed}</script>");
            return;
        }

        sb.AppendLine("  <div id=\"record-data\" hidden>");
        for (var offset = 0; offset < compressed.Length; offset += PayloadChunkSize)
        {
            var length = Math.Min(PayloadChunkSize, compressed.Length - offset);
            var chunk = compressed.Substring(offset, length);
            sb.AppendLine(
                $"    <script type=\"application/octet-stream\" class=\"record-data-chunk\">{chunk}</script>");
        }

        sb.AppendLine("  </div>");
    }

    private static FileStream CreateHtmlFileStream(string outputFile)
    {
        return new FileStream(
            outputFile,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            1024 * 1024,
            true);
    }

    private static StreamWriter CreateHtmlWriter(Stream stream)
    {
        return new StreamWriter(stream, new UTF8Encoding(false), 1024 * 1024);
    }

    private static async Task WriteHtmlHeaderAsync(
        TextWriter writer,
        string title,
        CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        ComparisonHtmlHelpers.AppendHtmlHeader(sb, title);
        await WriteAsync(writer, sb.ToString(), cancellationToken);
    }

    private static async Task WriteHtmlFooterAsync(TextWriter writer, CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        ComparisonHtmlHelpers.AppendHtmlFooter(sb);
        await WriteAsync(writer, sb.ToString(), cancellationToken);
    }

    private static async Task WriteBuildColumnStyleAsync(
        TextWriter writer,
        int dumpCount,
        CancellationToken cancellationToken)
    {
        if (dumpCount <= 3)
        {
            return;
        }

        await WriteAsync(writer, "  <style id=\"build-col-style\">", cancellationToken);
        for (var i = 3; i < dumpCount; i++)
        {
            await WriteAsync(writer, $".build-col-{i}{{display:none !important}}", cancellationToken);
        }

        await WriteLineAsync(writer, "</style>", cancellationToken);
    }

    private static async Task WriteControlsAsync(
        TextWriter writer,
        int dumpCount,
        bool hasDialogueGroupSelector,
        CancellationToken cancellationToken)
    {
        await WriteLineAsync(writer, "  <div class=\"controls\">", cancellationToken);
        await WriteLineAsync(writer, "    <a href=\"index.html\">&larr; Back to index</a>", cancellationToken);
        await WriteLineAsync(
            writer,
            "    <input type=\"text\" id=\"search\" placeholder=\"Search by FormID, EditorID, or name...\" oninput=\"filterRows()\">",
            cancellationToken);
        await WriteLineAsync(writer, "    <button onclick=\"expandAll()\">Expand All</button>", cancellationToken);
        await WriteLineAsync(writer, "    <button onclick=\"collapseAll()\">Collapse All</button>", cancellationToken);
        await WriteLineAsync(writer, "    <span id=\"matchCount\" class=\"match-count\"></span>", cancellationToken);

        if (hasDialogueGroupSelector)
        {
            await WriteLineAsync(writer, "    <div class=\"group-mode-selector\">", cancellationToken);
            await WriteLineAsync(writer, "      <label>Group by:</label>", cancellationToken);
            await WriteLineAsync(
                writer,
                "      <label><input type=\"radio\" name=\"groupMode\" value=\"Quest\" checked onchange=\"switchGroupMode(this.value)\"> Quest</label>",
                cancellationToken);
            await WriteLineAsync(
                writer,
                "      <label><input type=\"radio\" name=\"groupMode\" value=\"NPC\" onchange=\"switchGroupMode(this.value)\"> NPC</label>",
                cancellationToken);
            await WriteLineAsync(writer, "    </div>", cancellationToken);
        }

        if (dumpCount > 3)
        {
            await WriteLineAsync(
                writer,
                $"    <div class=\"build-nav\" data-total=\"{dumpCount}\" data-start=\"0\" data-size=\"3\">",
                cancellationToken);
            await WriteLineAsync(writer, "      <button onclick=\"navBuilds('first')\">&laquo; First</button>",
                cancellationToken);
            await WriteLineAsync(writer, "      <button onclick=\"navBuilds('prev3')\">&lsaquo; 3</button>",
                cancellationToken);
            await WriteLineAsync(writer, "      <button onclick=\"navBuilds('prev1')\">&lsaquo; 1</button>",
                cancellationToken);
            await WriteLineAsync(
                writer,
                $"      <span class=\"build-nav-label\">Builds 1\u20133 of {dumpCount}</span>",
                cancellationToken);
            await WriteLineAsync(writer, "      <button onclick=\"navBuilds('next1')\">1 &rsaquo;</button>",
                cancellationToken);
            await WriteLineAsync(writer, "      <button onclick=\"navBuilds('next3')\">3 &rsaquo;</button>",
                cancellationToken);
            await WriteLineAsync(writer, "      <button onclick=\"navBuilds('last')\">&raquo; Last</button>",
                cancellationToken);
            await WriteLineAsync(writer, "    </div>", cancellationToken);
        }

        await WriteLineAsync(writer, "  </div>", cancellationToken);
    }

    private static async Task WritePayloadScriptsAsync(
        TextWriter writer,
        string compressed,
        CancellationToken cancellationToken)
    {
        if (compressed.Length <= PayloadChunkSize)
        {
            await WriteLineAsync(
                writer,
                $"  <script type=\"application/octet-stream\" id=\"record-data\">{compressed}</script>",
                cancellationToken);
            return;
        }

        await WriteLineAsync(writer, "  <div id=\"record-data\" hidden>", cancellationToken);
        for (var offset = 0; offset < compressed.Length; offset += PayloadChunkSize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var length = Math.Min(PayloadChunkSize, compressed.Length - offset);
            await WriteAsync(
                writer,
                "    <script type=\"application/octet-stream\" class=\"record-data-chunk\">",
                cancellationToken);
            await writer.WriteAsync(compressed.AsMemory(offset, length), cancellationToken);
            await WriteLineAsync(writer, "</script>", cancellationToken);
        }

        await WriteLineAsync(writer, "  </div>", cancellationToken);
    }

    private static async Task WriteLineAsync(
        TextWriter writer,
        string text,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await writer.WriteLineAsync(text);
    }

    private static async Task WriteAsync(
        TextWriter writer,
        string text,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await writer.WriteAsync(text);
    }

    private sealed record PayloadBundle(string CompressedPayload, int JsonLength);
}
