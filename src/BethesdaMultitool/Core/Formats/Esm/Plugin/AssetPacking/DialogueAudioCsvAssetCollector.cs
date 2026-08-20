using System.Globalization;
using BethesdaMultitool.Core.Analysis;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Reporting;
using BethesdaMultitool.Core.Semantic;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.AssetPacking;

internal sealed record DialogueAudioCsvCollectionResult(
    IReadOnlySet<string> Paths,
    int CsvFilesRead,
    int RowsRead,
    int RowsMatched,
    int PathsAdded,
    int PathsRewrittenForNewEsp = 0,
    IReadOnlyDictionary<string, string>? PackPathRenames = null,
    int PathsRewrittenViaTriple = 0,
    int PathsRewrittenViaPrefix = 0,
    int RetailOverlayFallbacks = 0);

/// <summary>
///     Imports Bethesda Audio Transcriber CSV rows as dialogue voice asset requests.
///     The CSV carries prototype INFO FormIDs and concrete voice file paths; the converted
///     ESP may carry newly allocated INFO IDs, so source-DMP INFO IDs are included when
///     a DMP path is available.
/// </summary>
internal static class DialogueAudioCsvAssetCollector
{
    private static DialogueAudioCsvCollectionResult Empty { get; } =
        new(new HashSet<string>(StringComparer.OrdinalIgnoreCase), 0, 0, 0, 0);

    /// <summary>
    ///     Reads the transcriber CSV rows and collects the voice-file asset paths they reference,
    ///     rewriting prototype INFO paths to the converted ESP's newly allocated FormIDs where needed.
    /// </summary>
    public static async Task<DialogueAudioCsvCollectionResult> CollectAsync(
        RecordCollection convertedRecords,
        string? dmpPath,
        IReadOnlyList<string> csvPaths,
        IConversionProgressSink sink,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<uint, uint>? newRecordSourceToAllocated = null,
        string? outputEspFileName = null,
        IReadOnlyList<EmittedDialogueAudioBinding>? audioBindings = null)
    {
        if (csvPaths.Count == 0)
        {
            var fallbacks = CountRetailOverlayFallbacks(
                audioBindings, new HashSet<(uint FormId, byte ResponseNumber)>());
            ReportRetailOverlayFallbacks(sink, fallbacks);
            return Empty with { RetailOverlayFallbacks = fallbacks };
        }

        var dialogueFormIds = BuildDialogueFormIdSet(convertedRecords);
        var convertedInfoCount = dialogueFormIds.Count;
        var dmpInfoCount = 0;

        if (!string.IsNullOrWhiteSpace(dmpPath) && File.Exists(dmpPath))
        {
            try
            {
                using var dmpResult = await SemanticFileLoader
                    .LoadAsync(
                        dmpPath,
                        new SemanticFileLoadOptions
                        {
                            FileType = AnalysisFileType.Minidump,
                            ApplyDefaultCellWorldspaceAuthority = false
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

                foreach (var info in dmpResult.Records.Dialogues)
                {
                    if (info.FormId != 0 && dialogueFormIds.Add(info.FormId))
                    {
                        dmpInfoCount++;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                sink.Warn("AssetCollect",
                    $"Dialogue audio CSV matching could not load source DMP FormIDs: {ex.Message}");
            }
        }

        // Also match source FormIDs that the converter remapped to allocated IDs — these
        // CSV rows still describe the same dialogue line, just renamed by our allocator.
        if (newRecordSourceToAllocated is not null)
        {
            foreach (var sourceId in newRecordSourceToAllocated.Keys)
            {
                if (sourceId != 0)
                {
                    dialogueFormIds.Add(sourceId);
                }
            }
        }

        // Build triple-key index from audio bindings: (voicetype_edid, topic_edid, resp_num)
        // → allocated INFO FormID. Used as a fallback when CSV's FormID column doesn't
        // match any source FormID in newRecordSourceToAllocated (build-era drift).
        var bindingsByTriple = BuildAudioBindingTripleIndex(audioBindings);
        // Build a SECOND index keyed on the topic-EDID prefix truncated to the engine's
        // 15-char cap. CSV paths from older builds (e.g. July 2010) carry truncated topic
        // stems, so full-string matches against the triple index miss. The prefix index
        // returns a CANDIDATE LIST (multiple INFOs can share a 15-char prefix); the
        // collector picks the best one via response-text similarity against the CSV row.
        var bindingsByPrefix = BuildAudioBindingPrefixIndex(audioBindings);
        // Binding lookup keyed on (allocatedFormId, responseNumber). Used after a FormID
        // match resolves to an allocated INFO — we still want to recompute the filename
        // through EngineVoicePathBuilder rather than copy the CSV's stem verbatim, because
        // CSV stems were generated by an older-engine truncation policy that no longer
        // matches what the runtime constructs.
        var bindingsByAllocated = BuildAudioBindingByAllocatedIndex(audioBindings);
        // Source identity survives retail overlays and cut-dialogue rehoming. It is the
        // decisive lookup when a CSV row names a shared retail INFO but its response was
        // emitted under a newly allocated top-level INFO.
        var bindingsBySource = BuildAudioBindingBySourceIndex(audioBindings);
        var outputEspName = outputEspFileName ?? string.Empty;
        var preferredTexts = BuildPreferredSourceTexts(audioBindings);
        var csvCatalog = DialogueCsvCatalog.Load(csvPaths, preferredTexts: preferredTexts);
        var selectedSourceRows = csvCatalog.SelectedAudioRows
            .Select(static row => (row.FormId, row.ResponseNumber))
            .ToHashSet();
        var retailOverlayFallbacks = CountRetailOverlayFallbacks(audioBindings, selectedSourceRows);
        ReportRetailOverlayFallbacks(sink, retailOverlayFallbacks);

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var renames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var csvFilesRead = 0;
        var rowsRead = 0;
        var rowsMatched = 0;
        var rewritten = 0;
        var rewrittenViaTriple = 0;
        var rewrittenViaPrefix = 0;

        foreach (var csvPath in csvPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(csvPath))
            {
                continue;
            }

            if (!File.Exists(csvPath))
            {
                sink.Warn("AssetCollect", $"Dialogue audio CSV not found: {csvPath}");
                continue;
            }

            var result = CollectFromCsv(
                csvPath, dialogueFormIds, paths,
                newRecordSourceToAllocated, outputEspName, renames,
                bindingsByTriple, bindingsByPrefix, bindingsByAllocated,
                bindingsBySource,
                csvCatalog.SelectedRowOrdinalsByCsvPath.GetValueOrDefault(csvPath));
            csvFilesRead++;
            rowsRead += result.RowsRead;
            rowsMatched += result.RowsMatched;
            rewritten += result.PathsRewrittenForNewEsp;
            rewrittenViaTriple += result.PathsRewrittenViaTriple;
            rewrittenViaPrefix += result.PathsRewrittenViaPrefix;
        }

        sink.Info("AssetCollect",
            $"Dialogue audio CSV contributed {paths.Count:N0} asset path(s) from " +
            $"{rowsMatched:N0}/{rowsRead:N0} matched row(s) " +
            $"({convertedInfoCount:N0} ESP INFO IDs, {dmpInfoCount:N0} source-DMP-only INFO IDs, " +
            $"{rewritten:N0} rewritten onto new ESP path, " +
            $"{rewrittenViaTriple:N0} via triple-key fallback, " +
            $"{rewrittenViaPrefix:N0} via prefix-key fallback).");

        return new DialogueAudioCsvCollectionResult(
            paths,
            csvFilesRead,
            rowsRead,
            rowsMatched,
            paths.Count,
            rewritten,
            renames,
            rewrittenViaTriple,
            rewrittenViaPrefix,
            retailOverlayFallbacks);
    }

    private static Dictionary<(uint FormId, byte ResponseNumber), string> BuildPreferredSourceTexts(
        IReadOnlyList<EmittedDialogueAudioBinding>? bindings)
    {
        var result = new Dictionary<(uint FormId, byte ResponseNumber), string>();
        if (bindings is null)
        {
            return result;
        }

        foreach (var binding in bindings)
        {
            var sourceResponse = binding.SourceResponseNumber != 0
                ? binding.SourceResponseNumber
                : binding.ResponseNumber;
            if (binding.SourceInfoFormId == 0
                || sourceResponse == 0
                || string.IsNullOrWhiteSpace(binding.ResponseText)
                || string.Equals(
                    binding.ResponseText,
                    DialogueTextBackfill.PlaceholderText,
                    StringComparison.Ordinal))
            {
                continue;
            }

            result.TryAdd((binding.SourceInfoFormId, sourceResponse), binding.ResponseText);
        }

        return result;
    }

    internal static DialogueAudioCsvCollectionResult CollectFromCsv(
        string csvPath,
        IReadOnlySet<uint> dialogueFormIds,
        HashSet<string> paths,
        IReadOnlyDictionary<uint, uint>? newRecordSourceToAllocated = null,
        string? outputEspFileName = null,
        Dictionary<string, string>? packPathRenames = null,
        IReadOnlyDictionary<(string Voice, string Topic, byte Resp), uint>? bindingsByTriple = null,
        IReadOnlyDictionary<(string Voice, string TopicPrefix, byte Resp), List<EmittedDialogueAudioBinding>>?
            bindingsByPrefix = null,
        IReadOnlyDictionary<(uint AllocFid, byte Resp), EmittedDialogueAudioBinding>?
            bindingsByAllocated = null,
        IReadOnlyDictionary<(uint SourceFid, byte SourceResp), EmittedDialogueAudioBinding>?
            bindingsBySource = null,
        IReadOnlySet<int>? selectedRowOrdinals = null)
    {
        using var reader = new StreamReader(csvPath);
        var headerFields = DialogueAudioCsvReader.ReadCsvRecord(reader);
        if (headerFields.Count == 0)
        {
            return new DialogueAudioCsvCollectionResult(paths, 1, 0, 0, 0);
        }

        var fileIndex = DialogueAudioCsvReader.FindColumn(headerFields, "File");
        var formIdIndex = DialogueAudioCsvReader.FindColumn(headerFields, "FormID");
        if (fileIndex < 0 || formIdIndex < 0)
        {
            return new DialogueAudioCsvCollectionResult(paths, 1, 0, 0, 0);
        }

        // Text column is optional but required for prefix-fallback disambiguation. Without
        // it we can still attempt prefix matching but can't pick between candidates that
        // share a (voice, topic-prefix, resp) bucket.
        var textIndex = DialogueAudioCsvReader.FindColumn(headerFields, "Text");

        var rowsRead = 0;
        var rowsMatched = 0;
        var rewritten = 0;
        var rewrittenViaTriple = 0;
        var rewrittenViaPrefix = 0;
        var initialPathCount = paths.Count;

        while (!reader.EndOfStream)
        {
            var fields = DialogueAudioCsvReader.ReadCsvRecord(reader);
            if (fields.Count == 0)
            {
                continue;
            }

            rowsRead++;
            if (selectedRowOrdinals is not null && !selectedRowOrdinals.Contains(rowsRead))
            {
                continue;
            }

            if (fields.Count <= Math.Max(fileIndex, formIdIndex))
            {
                continue;
            }

            if (!DialogueAudioCsvReader.TryParseFormId(fields[formIdIndex], out var formId))
            {
                continue;
            }

            var filePath = fields[fileIndex];
            if (string.IsNullOrWhiteSpace(filePath))
            {
                continue;
            }

            uint? allocatedFormId = null;
            EmittedDialogueAudioBinding? matchedBinding = null;
            var matchedViaTriple = false;
            var matchedViaPrefix = false;
            var sourceResponseNumber = ExtractResponseNumberFromPath(filePath);
            if (sourceResponseNumber.HasValue
                && bindingsBySource is not null
                && bindingsBySource.TryGetValue(
                    (formId, sourceResponseNumber.Value), out var sourceBinding))
            {
                matchedBinding = sourceBinding;
                allocatedFormId = sourceBinding.AllocatedInfoFormId;
            }
            else if (dialogueFormIds.Contains(formId))
            {
                // FormID-first match: same INFO between CSV and converter source.
                if (newRecordSourceToAllocated is not null
                    && newRecordSourceToAllocated.TryGetValue(formId, out var allocated))
                {
                    allocatedFormId = allocated;
                }
            }
            else if (bindingsByTriple is not null
                     && TryExtractTripleFromPath(filePath, out var triple)
                     && bindingsByTriple.TryGetValue(triple, out var allocatedFromTriple))
            {
                // FormID drift across build eras: the CSV's FormID doesn't match anything
                // we emitted, but the same (voicetype, topic, response) line was emitted
                // under a different FormID. Use the binding's allocated FormID so the pack
                // path matches the engine's runtime lookup.
                allocatedFormId = allocatedFromTriple;
                matchedViaTriple = true;
            }
            else if (bindingsByPrefix is not null
                     && TryExtractTripleFromPath(filePath, out var prefixTriple))
            {
                // Older-build CSV paths carry a TRUNCATED topic stem (e.g. July 2010 used a
                // 30-char policy: tempvdialogueu_vdialogueulysse_*). The runtime engine now
                // uses a 26-char policy and constructs different filenames; the triple-key
                // lookup misses because our binding stores the FULL topic EDID. Try matching
                // by topic-stem prefix and disambiguate using the CSV's response text.
                var prefixKey = (
                    prefixTriple.Voice,
                    TruncateTopicForPrefixIndex(prefixTriple.Topic),
                    prefixTriple.Resp);
                if (bindingsByPrefix.TryGetValue(prefixKey, out var candidates) && candidates.Count > 0)
                {
                    var csvText = textIndex >= 0 && textIndex < fields.Count
                        ? fields[textIndex]
                        : string.Empty;
                    matchedBinding = PickBestPrefixCandidate(candidates, csvText);
                    if (matchedBinding is not null)
                    {
                        allocatedFormId = matchedBinding.AllocatedInfoFormId;
                        matchedViaPrefix = true;
                    }
                }

                if (matchedBinding is null)
                {
                    continue;
                }
            }
            else
            {
                continue;
            }

            // If we matched via FormID and have an allocated FormID, look up the binding by
            // (allocatedFormId, responseNumber) so the path rewrite can use the engine-shape
            // builder rather than just copying the CSV's stem (which was generated under an
            // older-build truncation policy). The response number comes from the filename's
            // trailing _N segment.
            if (matchedBinding is null && allocatedFormId.HasValue && bindingsByAllocated is not null)
            {
                var resp = ExtractResponseNumberFromPath(filePath);
                if (resp.HasValue
                    && bindingsByAllocated.TryGetValue((allocatedFormId.Value, resp.Value), out var b))
                {
                    matchedBinding = b;
                }
            }

            // Master-override case: the CSV's FormID hit our INFO set but we didn't allocate
            // a new ID for it (it's a master override, FormID preserved). The proto-shape CSV
            // path (proto voice type + temp-prefixed topic stem) doesn't resolve anywhere —
            // FNV vanilla ships the canonical engine-shape path under falloutnv.esm. Pull the
            // binding by (formId, resp) so ExpandDialogueAudioRequests can emit the engine-
            // shape master-baseline path instead of the proto-shape, eliminating thousands of
            // "missing" noise entries that never affected runtime (engine falls back to the
            // master file's voice folder when ours doesn't ship the audio).
            if (matchedBinding is null && !allocatedFormId.HasValue
                                       && bindingsByAllocated is not null
                                       && dialogueFormIds.Contains(formId))
            {
                var resp = ExtractResponseNumberFromPath(filePath);
                if (resp.HasValue
                    && bindingsByAllocated.TryGetValue((formId, resp.Value), out var b))
                {
                    matchedBinding = b;
                    allocatedFormId = formId;
                }
            }

            var matchedPath = false;
            foreach (var (resolveAs, packAs) in ExpandDialogueAudioRequests(
                         filePath, formId, allocatedFormId, outputEspFileName,
                         matchedBinding))
            {
                matchedPath |= paths.Add(resolveAs);
                if (packPathRenames is not null
                    && !string.Equals(resolveAs, packAs, StringComparison.OrdinalIgnoreCase))
                {
                    packPathRenames[resolveAs] = packAs;
                }
            }

            if (matchedPath)
            {
                rowsMatched++;
                if (allocatedFormId.HasValue)
                {
                    rewritten++;
                    if (matchedViaTriple)
                    {
                        rewrittenViaTriple++;
                    }

                    if (matchedViaPrefix)
                    {
                        rewrittenViaPrefix++;
                    }
                }
            }
        }

        return new DialogueAudioCsvCollectionResult(
            paths,
            1,
            rowsRead,
            rowsMatched,
            paths.Count - initialPathCount,
            rewritten,
            packPathRenames,
            rewrittenViaTriple,
            rewrittenViaPrefix);
    }

    /// <summary>
    ///     Build a fast triple-key lookup from the per-INFO bindings emitted by
    ///     <c>DialogGrupBuilder</c>. Skips bindings without a voice type (no triple to
    ///     index on). Ties are resolved by the first binding to populate a key — multiple
    ///     INFOs with the same (voicetype, topic, resp) shouldn't normally occur, but if
    ///     they do we treat the first allocator-issued FormID as authoritative.
    /// </summary>
    internal static Dictionary<(string Voice, string Topic, byte Resp), uint> BuildAudioBindingTripleIndex(
        IReadOnlyList<EmittedDialogueAudioBinding>? bindings)
    {
        var index = new Dictionary<(string Voice, string Topic, byte Resp), uint>();
        if (bindings is null || bindings.Count == 0)
        {
            return index;
        }

        foreach (var b in bindings)
        {
            if (string.IsNullOrEmpty(b.VoiceTypeEditorId)
                || string.IsNullOrEmpty(b.ParentDialEditorId)
                || b.ResponseNumber == 0)
            {
                continue;
            }

            var key = (
                b.VoiceTypeEditorId.ToLowerInvariant(),
                b.ParentDialEditorId.ToLowerInvariant(),
                b.ResponseNumber);
            index.TryAdd(key, b.AllocatedInfoFormId);
        }

        return index;
    }

    /// <summary>
    ///     Build a (voice, topic-prefix-15, resp) → candidate-list index from emitted bindings.
    ///     Multiple INFOs commonly share a 15-char topic-EDID prefix
    ///     (<c>VDialogueUlyssesUlyssesTopic000/001/002/...</c> all collapse to
    ///     <c>VDialogueUlysse</c>), so each bucket can carry many candidates. The collector
    ///     disambiguates by matching the CSV row's response Text against each candidate's
    ///     <see cref="EmittedDialogueAudioBinding.ResponseText" />.
    /// </summary>
    internal static Dictionary<(string Voice, string TopicPrefix, byte Resp), List<EmittedDialogueAudioBinding>>
        BuildAudioBindingPrefixIndex(IReadOnlyList<EmittedDialogueAudioBinding>? bindings)
    {
        var index = new Dictionary<(string Voice, string TopicPrefix, byte Resp), List<EmittedDialogueAudioBinding>>();
        if (bindings is null || bindings.Count == 0)
        {
            return index;
        }

        foreach (var b in bindings)
        {
            if (string.IsNullOrEmpty(b.VoiceTypeEditorId)
                || string.IsNullOrEmpty(b.ParentDialEditorId)
                || string.IsNullOrEmpty(b.QuestEditorId)
                || b.ResponseNumber == 0)
            {
                continue;
            }

            var key = (
                b.VoiceTypeEditorId.ToLowerInvariant(),
                TruncateTopicForPrefixIndex(b.ParentDialEditorId),
                b.ResponseNumber);
            if (!index.TryGetValue(key, out var list))
            {
                list = [];
                index[key] = list;
            }

            list.Add(b);
        }

        return index;
    }

    /// <summary>
    ///     Build an (allocFid, responseNumber) → binding index. Used after a FormID-first
    ///     match has resolved an allocated INFO so the path rewriter can still pull the
    ///     binding's quest+topic+voice EDIDs and pass them to <see cref="EngineVoicePathBuilder" />
    ///     — that way the BSA path matches what the runtime constructs even when the source
    ///     CSV came from an older engine with a different truncation policy.
    /// </summary>
    internal static Dictionary<(uint AllocFid, byte Resp), EmittedDialogueAudioBinding>
        BuildAudioBindingByAllocatedIndex(IReadOnlyList<EmittedDialogueAudioBinding>? bindings)
    {
        var index = new Dictionary<(uint AllocFid, byte Resp), EmittedDialogueAudioBinding>();
        if (bindings is null || bindings.Count == 0)
        {
            return index;
        }

        foreach (var b in bindings)
        {
            if (b.AllocatedInfoFormId == 0 || b.ResponseNumber == 0)
            {
                continue;
            }

            index.TryAdd((b.AllocatedInfoFormId, b.ResponseNumber), b);
        }

        return index;
    }

    /// <summary>
    ///     Build the prototype source identity index. Unlike allocated-FormID lookup, this
    ///     remains stable when an unmatched response is rehomed from a shared retail INFO
    ///     onto a fresh plugin INFO.
    /// </summary>
    internal static Dictionary<(uint SourceFid, byte SourceResp), EmittedDialogueAudioBinding>
        BuildAudioBindingBySourceIndex(IReadOnlyList<EmittedDialogueAudioBinding>? bindings)
    {
        var index = new Dictionary<(uint SourceFid, byte SourceResp), EmittedDialogueAudioBinding>();
        if (bindings is null || bindings.Count == 0)
        {
            return index;
        }

        foreach (var binding in bindings)
        {
            var response = binding.SourceResponseNumber != 0
                ? binding.SourceResponseNumber
                : binding.ResponseNumber;
            if (binding.SourceInfoFormId == 0 || response == 0)
            {
                continue;
            }

            index.TryAdd((binding.SourceInfoFormId, response), binding);
        }

        return index;
    }

    /// <summary>
    ///     Extract the trailing _N response number from a CSV voice path. Returns null when
    ///     the filename doesn't follow the expected <c>stem_FORMID_RESP.ext</c> shape.
    /// </summary>
    internal static byte? ExtractResponseNumberFromPath(string filePath)
    {
        var normalized = AssetPathCollector.TryNormalizeRequestPath(filePath);
        if (normalized is null)
        {
            return null;
        }

        var lastSep = normalized.LastIndexOf('\\');
        var fileName = lastSep >= 0 ? normalized[(lastSep + 1)..] : normalized;
        var dot = fileName.LastIndexOf('.');
        if (dot < 0)
        {
            return null;
        }

        var stem = fileName[..dot];
        var underscoreBeforeResp = stem.LastIndexOf('_');
        if (underscoreBeforeResp < 0)
        {
            return null;
        }

        var respText = stem[(underscoreBeforeResp + 1)..];
        if (!byte.TryParse(respText, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var resp)
            || resp == 0)
        {
            return null;
        }

        return resp;
    }

    /// <summary>Lowercase + truncate the topic EDID to the FNV engine's 15-char filename cap.</summary>
    internal static string TruncateTopicForPrefixIndex(string topicEditorId)
    {
        var lower = topicEditorId.ToLowerInvariant();
        return lower.Length > EngineVoicePathBuilder.MaxTopicStem
            ? lower[..EngineVoicePathBuilder.MaxTopicStem]
            : lower;
    }

    /// <summary>
    ///     Pick the candidate whose <see cref="EmittedDialogueAudioBinding.ResponseText" />
    ///     best matches the CSV row's Text. Exact match (case-insensitive, trimmed) wins.
    ///     Otherwise fall back to the candidate whose normalized text shares the longest
    ///     leading common substring — this tolerates the minor typo / punctuation drift
    ///     between build eras (e.g. April had <c>"that.."</c> versus July's <c>"that..."</c>).
    ///     Returns null if no candidate has any text overlap.
    /// </summary>
    internal static EmittedDialogueAudioBinding? PickBestPrefixCandidate(
        IReadOnlyList<EmittedDialogueAudioBinding> candidates,
        string csvText)
    {
        if (candidates.Count == 1)
        {
            return candidates[0];
        }

        var normCsv = DialogueAudioTextMatcher.NormalizeText(csvText);
        if (normCsv.Length == 0)
        {
            // No text to disambiguate — return null rather than guessing. Caller logs a miss.
            return null;
        }

        EmittedDialogueAudioBinding? exact = null;
        EmittedDialogueAudioBinding? best = null;
        var bestPrefixLen = 0;

        foreach (var c in candidates)
        {
            var normC = DialogueAudioTextMatcher.NormalizeText(c.ResponseText ?? string.Empty);
            if (normC.Length == 0)
            {
                continue;
            }

            if (string.Equals(normC, normCsv, StringComparison.Ordinal))
            {
                exact = c;
                break;
            }

            var commonLen = DialogueAudioTextMatcher.CommonPrefixLength(normC, normCsv);
            if (commonLen > bestPrefixLen)
            {
                bestPrefixLen = commonLen;
                best = c;
            }
        }

        if (exact is not null)
        {
            return exact;
        }

        // Require at least a 16-char common prefix (about one short sentence) to claim a
        // fuzzy match. Below that the two texts could be different lines that happen to
        // start the same way ("I can't" / "I will" / etc.), and a wrong match would lay
        // down audio at the wrong INFO.
        return bestPrefixLen >= 16 ? best : null;
    }

    /// <summary>
    ///     Extract <c>(voicetype_edid, topic_edid, response_num)</c> from a CSV file path
    ///     of the canonical shape
    ///     <c>sound\voice\&lt;esm&gt;\&lt;voicetype&gt;\&lt;topic_stem&gt;_&lt;fid8&gt;_&lt;resp&gt;.&lt;ext&gt;</c>.
    ///     Returns false when the path doesn't match the expected shape.
    /// </summary>
    internal static bool TryExtractTripleFromPath(
        string filePath,
        out (string Voice, string Topic, byte Resp) triple)
    {
        triple = default;
        var normalized = AssetPathCollector.TryNormalizeRequestPath(filePath);
        if (normalized is null
            || !normalized.StartsWith("sound\\voice\\", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Split the path: sound\voice\<esm>\<voicetype>\<filename>
        var parts = normalized.Split('\\');
        if (parts.Length < 5)
        {
            return false;
        }

        var voiceTypeEdid = parts[3];
        var fileName = parts[^1];

        var dot = fileName.LastIndexOf('.');
        var stem = dot >= 0 ? fileName[..dot] : fileName;

        var underscoreBeforeResp = stem.LastIndexOf('_');
        if (underscoreBeforeResp < 0)
        {
            return false;
        }

        var respText = stem[(underscoreBeforeResp + 1)..];
        if (!byte.TryParse(respText, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var resp)
            || resp == 0)
        {
            return false;
        }

        var stemMinusResp = stem[..underscoreBeforeResp];
        var underscoreBeforeFid = stemMinusResp.LastIndexOf('_');
        if (underscoreBeforeFid < 0)
        {
            return false;
        }

        var topicEdid = stemMinusResp[..underscoreBeforeFid];
        if (string.IsNullOrEmpty(voiceTypeEdid) || string.IsNullOrEmpty(topicEdid))
        {
            return false;
        }

        triple = (voiceTypeEdid.ToLowerInvariant(), topicEdid.ToLowerInvariant(), resp);
        return true;
    }

    private static HashSet<uint> BuildDialogueFormIdSet(RecordCollection records)
    {
        var result = new HashSet<uint>();
        foreach (var info in records.Dialogues)
        {
            if (info.FormId != 0)
            {
                result.Add(info.FormId);
            }
        }

        return result;
    }

    /// <summary>
    ///     Emit (resolveAs, packAs) pairs for one CSV row. resolveAs is the master-shaped
    ///     path the data-folder index actually contains; packAs is the engine's runtime
    ///     lookup path. For master overrides (no remap) the two are identical and the
    ///     packer's existing identity-mapped flow handles it. For new INFOs the converter
    ///     remapped, packAs swaps the ESM directory token for the new ESP's filename and
    ///     the source-FormID hex in the filename for the allocated FormID's bottom 24 bits
    ///     — exactly what the engine constructs at runtime.
    /// </summary>
    internal static IEnumerable<(string ResolveAs, string PackAs)> ExpandDialogueAudioRequests(
        string filePath,
        uint sourceFormId = 0,
        uint? allocatedFormId = null,
        string? outputEspFileName = null,
        EmittedDialogueAudioBinding? matchedBinding = null)
    {
        var normalized = AssetPathCollector.TryNormalizeRequestPath(filePath);
        if (normalized is null ||
            !normalized.StartsWith("sound\\voice\\", StringComparison.OrdinalIgnoreCase))
        {
            yield break;
        }

        string packPath;
        // When set, masterResolvePath is the engine-shape path under falloutnv.esm — the
        // canonical location vanilla ships master-override voice files. Substituting it as
        // resolveAs lets the resolver hit baseline (AlreadyInBaseline) and skip packing
        // entirely; the runtime engine falls back to the master file's voice folder when
        // our ESP doesn't ship audio, so the line plays normally.
        string? masterResolvePath = null;
        if (matchedBinding is not null
            && !string.IsNullOrWhiteSpace(outputEspFileName)
            && !string.IsNullOrEmpty(matchedBinding.QuestEditorId)
            && !string.IsNullOrEmpty(matchedBinding.ParentDialEditorId)
            && !string.IsNullOrEmpty(matchedBinding.VoiceTypeEditorId)
            && allocatedFormId.HasValue)
        {
            // Always rebuild the path through EngineVoicePathBuilder when we have a binding —
            // the CSV's stem was produced by whichever engine version generated the source
            // BSA (April / July / final), and the runtime's truncation policy may have moved
            // since (FNV PC final uses a 26-char total-stem cap; July 2010 disk dump used 30).
            // Recomputing from quest+topic+resp gives the engine exactly what it'll ask for.
            var ext0 = Path.GetExtension(normalized);
            packPath = EngineVoicePathBuilder.Build(
                outputEspFileName!,
                matchedBinding.VoiceTypeEditorId!,
                matchedBinding.QuestEditorId!,
                matchedBinding.ParentDialEditorId,
                allocatedFormId.Value,
                matchedBinding.ResponseNumber,
                ext0);

            // Master-override INFOs (FormID preserved through conversion) live at the
            // engine-shape master path in FNV vanilla. Compute that path here so the
            // caller can request it instead of the proto-shape CSV path — drops the
            // proto-shape from the missing report without touching runtime behavior.
            // Only the FNV master is checked; FO3-derived INFOs are out of scope.
            if (sourceFormId != 0
                && sourceFormId == allocatedFormId.Value
                && !matchedBinding.IsRetailInfoOverlay)
            {
                masterResolvePath = EngineVoicePathBuilder.Build(
                    "falloutnv.esm",
                    matchedBinding.VoiceTypeEditorId!,
                    matchedBinding.QuestEditorId!,
                    matchedBinding.ParentDialEditorId,
                    allocatedFormId.Value,
                    matchedBinding.ResponseNumber,
                    ext0);
            }
        }
        else if (allocatedFormId.HasValue && !string.IsNullOrWhiteSpace(outputEspFileName))
        {
            packPath = RewritePathForNewEsp(normalized, sourceFormId, allocatedFormId.Value, outputEspFileName!);
        }
        else
        {
            packPath = normalized;
        }

        // For master overrides with a known engine-shape path, emit only the master-baseline
        // resolveAs — the proto-shape never resolves anywhere and just adds noise to the
        // missing report. The packAs stays at the engine-shape under our ESP token so that
        // IF baseline somehow misses (FO3-routed bindings, mis-merged voice types), the
        // packer still routes bytes to the right runtime location.
        var resolveBasePath = masterResolvePath ?? normalized;
        var ext = Path.GetExtension(normalized);
        if (ext.Equals(".lip", StringComparison.OrdinalIgnoreCase))
        {
            yield return (resolveBasePath, packPath);
            yield break;
        }

        // PC FNV voice playback expects paired OGG audio and LIP sync assets. April CSV
        // rows usually name Xbox XMA files, so request the runtime OGG path and let
        // resolution extension-swap back to XMA in a secondary 360 source.
        yield return (Path.ChangeExtension(resolveBasePath, ".ogg"), Path.ChangeExtension(packPath, ".ogg"));
        yield return (Path.ChangeExtension(resolveBasePath, ".lip"), Path.ChangeExtension(packPath, ".lip"));
    }

    /// <summary>
    ///     Rewrite a master-shaped voice path (<c>sound\voice\falloutnv.esm\voicetype\stem_SSSSSSSS_n.ext</c>)
    ///     onto an emitted-ESP shape (<c>sound\voice\&lt;esp&gt;\voicetype\stem_AAAAAA_n.ext</c>) where
    ///     <c>SSSSSSSS</c> is the source FormID hex and <c>AAAAAA</c> is the bottom 24 bits of the
    ///     allocated FormID — what the FNV engine actually opens at runtime.
    /// </summary>
    internal static string RewritePathForNewEsp(
        string normalizedPath,
        uint sourceFormId,
        uint allocatedFormId,
        string outputEspFileName)
    {
        // Replace the second path segment (the ESM/ESP token) with the new ESP filename.
        // Path layout: sound\voice\<esm_or_esp>\<voicetype>\<filename>
        const string voicePrefix = "sound\\voice\\";
        if (!normalizedPath.StartsWith(voicePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return normalizedPath;
        }

        var afterPrefix = normalizedPath[voicePrefix.Length..];
        var nextSep = afterPrefix.IndexOf('\\');
        if (nextSep < 0)
        {
            return normalizedPath;
        }

        var tail = afterPrefix[nextSep..]; // includes the leading '\\'
        var newEspToken = outputEspFileName.ToLowerInvariant();

        // The filename embeds the source FormID as 8 lowercase hex chars; replace it with
        // the allocated FormID's bottom 24 bits zero-padded to 8 hex chars (engine convention).
        var allocatedHex = (allocatedFormId & 0x00FFFFFFu).ToString("x8");
        var sourceHex = sourceFormId.ToString("x8");
        var rewrittenTail = DialogueAudioPathRewriter.ReplaceSourceFormIdInFilename(tail, sourceHex, allocatedHex);

        return voicePrefix + newEspToken + rewrittenTail;
    }

    private static int CountRetailOverlayFallbacks(
        IReadOnlyList<EmittedDialogueAudioBinding>? bindings,
        HashSet<(uint FormId, byte ResponseNumber)> selectedSourceRows)
    {
        if (bindings is null)
        {
            return 0;
        }

        return bindings
            .Where(static binding => binding.IsRetailInfoOverlay)
            .Select(binding => (
                binding.SourceInfoFormId,
                binding.SourceResponseNumber != 0
                    ? binding.SourceResponseNumber
                    : binding.ResponseNumber))
            .Distinct()
            .Count(key => !selectedSourceRows.Contains(key));
    }

    private static void ReportRetailOverlayFallbacks(
        IConversionProgressSink sink,
        int fallbackCount)
    {
        if (fallbackCount == 0)
        {
            return;
        }

        sink.Warn("AssetCollect",
            $"{fallbackCount:N0} retail INFO overlay response(s) have no selected prototype audio row; " +
            "subtitle overrides remain active and the engine will fall back to vanilla master audio.",
            code: "dialog.audio.retail-fallback");
    }
}
