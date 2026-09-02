using System.IO.MemoryMappedFiles;
using BethesdaMultitool.Core.Analysis;
using BethesdaMultitool.Core.Coverage;
using BethesdaMultitool.Core.FileFormat;
using BethesdaMultitool.Core.Formats.Esm.Analysis.FileAnalysis;
using BethesdaMultitool.Core.Formats.Esm.Land;
using BethesdaMultitool.Core.Formats.Esm.Localization;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Records;
using BethesdaMultitool.Core.Formats.Esm.Runtime;
using BethesdaMultitool.Core.Minidump;
using BethesdaMultitool.Core.Recovery;

namespace BethesdaMultitool.Core.Semantic;

/// <summary>
///     Canonical semantic loading path for ESM/ESP and DMP sources.
/// </summary>
internal static class SemanticFileLoader
{
    /// <summary>Detects (or validates the override of) the semantic file type, rejecting unknown and save-file formats.</summary>
    internal static AnalysisFileType ResolveSemanticFileType(
        string filePath,
        AnalysisFileType? fileTypeOverride = null)
    {
        var fileType = fileTypeOverride ?? FileTypeDetector.Detect(filePath);
        if (fileType == AnalysisFileType.Unknown)
        {
            throw new InvalidOperationException(
                $"Unrecognized file format: {Path.GetFileName(filePath)}. " +
                "Supported semantic formats: ESM/ESP (TES4/4SET) and DMP (MDMP).");
        }

        if (fileType == AnalysisFileType.SaveFile)
        {
            throw new NotSupportedException(
                "Save file analysis is not supported by the semantic loader. Use the save-specific pipeline instead.");
        }

        return fileType;
    }

    /// <summary>
    ///     Analyzes the file and parses it into a disposable <see cref="UnifiedAnalysisResult" /> with records and a
    ///     FormID resolver.
    /// </summary>
    internal static async Task<UnifiedAnalysisResult> LoadAsync(
        string filePath,
        SemanticFileLoadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new SemanticFileLoadOptions();

        var fileType = ResolveSemanticFileType(filePath, options.FileType);
        if (fileType == AnalysisFileType.ClassicGameData)
        {
            // A classic source is an install, not a record stream — no scan result, no memory map
            // of a single plugin. The classic analyzer owns the whole path.
            return await Formats.Classic.ClassicGameAnalyzer.LoadAsync(filePath, cancellationToken);
        }

        var analysisResult = await AnalyzeOnlyAsync(filePath, options, cancellationToken);
        return LoadFromAnalysisResult(filePath, analysisResult, fileType, options);
    }

    /// <summary>
    ///     Runs only the format-specific analysis phase (no semantic parse), returning the raw
    ///     <see cref="AnalysisResult" />.
    /// </summary>
    internal static async Task<AnalysisResult> AnalyzeOnlyAsync(
        string filePath,
        SemanticFileLoadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new SemanticFileLoadOptions();

        var fileType = ResolveSemanticFileType(filePath, options.FileType);
        return await AnalyzeAsync(filePath, fileType, options, cancellationToken);
    }

    /// <summary>Parses an already-computed <see cref="AnalysisResult" /> into a <see cref="UnifiedAnalysisResult" />.</summary>
    internal static UnifiedAnalysisResult LoadFromAnalysisResult(
        string filePath,
        AnalysisResult analysisResult,
        AnalysisFileType fileType,
        IProgress<(int percent, string phase)>? parseProgress = null)
    {
        return LoadFromAnalysisResult(
            filePath,
            analysisResult,
            fileType,
            new SemanticFileLoadOptions
            {
                FileType = fileType,
                ParseProgress = parseProgress
            });
    }

    /// <summary>
    ///     Parses an already-computed <see cref="AnalysisResult" /> into a <see cref="UnifiedAnalysisResult" /> using the
    ///     given options.
    /// </summary>
    internal static UnifiedAnalysisResult LoadFromAnalysisResult(
        string filePath,
        AnalysisResult analysisResult,
        AnalysisFileType fileType,
        SemanticFileLoadOptions? options)
    {
        options ??= new SemanticFileLoadOptions();
        fileType = ResolveSemanticFileType(filePath, fileType);
        if (analysisResult.EsmRecords == null)
        {
            throw new InvalidOperationException(
                $"No records found in {Path.GetFileName(filePath)}. The file may be empty or corrupted.");
        }

        var fileInfo = new FileInfo(filePath);
        var mmf = MemoryMappedFile.CreateFromFile(filePath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        var accessor = mmf.CreateViewAccessor(0, fileInfo.Length, MemoryMappedFileAccess.Read);

        try
        {
            var result = LoadFromAnalysisResult(
                filePath,
                analysisResult,
                fileType,
                options,
                new MmfMemoryAccessor(accessor),
                fileInfo.Length);
            result.SetDisposables(mmf, accessor);
            return result;
        }
        catch
        {
            accessor.Dispose();
            mmf.Dispose();
            throw;
        }
    }

    internal static UnifiedAnalysisResult LoadFromAnalysisResult(
        string filePath,
        AnalysisResult analysisResult,
        AnalysisFileType fileType,
        SemanticFileLoadOptions? options,
        IMemoryAccessor accessor,
        long fileSize)
    {
        options ??= new SemanticFileLoadOptions();
        fileType = ResolveSemanticFileType(filePath, fileType);
        if (fileType == AnalysisFileType.ClassicGameData)
        {
            throw new InvalidOperationException(
                "Classic game sources have no ESM scan result — load them via SemanticFileLoader.LoadAsync, " +
                "which routes to ClassicGameAnalyzer.");
        }

        if (analysisResult.EsmRecords == null)
        {
            throw new InvalidOperationException(
                $"No records found in {Path.GetFileName(filePath)}. The file may be empty or corrupted.");
        }

        ApplyDmpGapRecoveryPromotionsIfNeeded(analysisResult, options);

        // NOTE: an earlier revision wrapped the string-table + terrain mounts below in
        // ArchiveHandleRegistry.Shared.KeepWarm() to avoid re-parsing archives across the pair. That is
        // wrong here on both counts. It buys nothing — the string-table lookup stops at the first
        // layer that hits, and "<Game> - Localization.ba2" sorts before "<Game> - Terrain*.ba2", so the
        // terrain archives are never opened by that pass — and KeepWarm is process-global, so it parks
        // handles released by *any* concurrent caller, holding their files open past the point those
        // callers expect to be able to delete them.

        // Localized plugins (Skyrim/FO4/Starfield) store FULL/DESC/dialogue text as string-table
        // IDs, not inline text. Load the tables (loose Data\Strings or inside a BSA) so the handlers
        // resolve real names instead of the first byte of the ID. Null for non-localized plugins.
        var localizedStrings = fileType == AnalysisFileType.EsmFile
            ? LocalizedStringTables.TryLoad(filePath)
            : null;
        var context = new RecordParserContext(
            analysisResult.EsmRecords,
            analysisResult.FormIdMap,
            accessor,
            fileSize,
            analysisResult.MinidumpInfo,
            localizedStrings);
        var parser = new RecordParser(context);
        var records = parser.ParseAll(options.ParseProgress, options.ResidentRecoveryMasterFormIds);
        ApplyCellWorldspaceAuthorityIfNeeded(records, analysisResult.EsmRecords, fileType, options);

        // Fallout 76 and Starfield store terrain heights in external .btd files, not in-record VHGT
        // (Starfield has no LAND record at all). Attach them to exterior cells so the 2D/3D terrain
        // renderers (which read through the shared height abstraction) light up. No-op for every
        // other game. Large worldspaces (Appalachia) get LAZY per-cell height providers over a
        // kept-open BTD — the injection object owns those sources and rides on the result, disposed
        // with it (the same lifetime the ESM's own memory map already has).
        BtdTerrainInjection? terrain = null;
        if (fileType == AnalysisFileType.EsmFile)
        {
            ReleaseEsmScanIntermediates(analysisResult.EsmRecords);
            terrain = BtdTerrainInjector.Inject(records, filePath);
        }

        try
        {
            var resolver = records.CreateResolver(analysisResult.FormIdMap);

            var result = new UnifiedAnalysisResult
            {
                FileType = fileType,
                Records = records,
                Resolver = resolver,
                RawResult = analysisResult,
                FilePath = filePath
            };
            result.SetTerrainInjection(terrain);
            return result;
        }
        catch
        {
            terrain?.Dispose();
            throw;
        }
    }

    /// <summary>
    ///     Releases ESM scan-time intermediates the record parser has fully consumed into the semantic
    ///     <c>RecordCollection</c>: <c>RefrRecords</c> (the per-REFR <c>ExtractedRefrRecord</c>s plus
    ///     the <c>PositionSubrecord</c>s they uniquely own) and <c>NameReferences</c>. Neither has any
    ///     reader after <c>RecordParser.ParseAll</c> returns on a plain ESM load — the last in-parse
    ///     reader is <c>WorldRecordHandler.ExtractMapMarkers</c>, so this must run AFTER ParseAll, never
    ///     inside it. For Fallout 76's SeventySix.esm (5.1M REFRs) this frees ~2 GB / ~10M objects and
    ///     shortens every later GC pause (pause time scales with live-object count). ESM-only: DMP
    ///     runtime extraction has post-parse consumers of both lists. KEPT alive: MainRecords
    ///     (hex-viewer overlay), LandRecords (heightmap viewer), the GRUP maps, Positions
    ///     (dangling-ref attribution), and the runtime lists. Idempotent.
    /// </summary>
    private static void ReleaseEsmScanIntermediates(EsmRecordScanResult scan)
    {
        scan.RefrRecords.Clear();
        scan.RefrRecords.TrimExcess();
        scan.NameReferences.Clear();
        scan.NameReferences.TrimExcess();
    }

    private static async Task<AnalysisResult> AnalyzeAsync(
        string filePath,
        AnalysisFileType fileType,
        SemanticFileLoadOptions options,
        CancellationToken cancellationToken)
    {
        var result = fileType switch
        {
            AnalysisFileType.EsmFile => await EsmFileAnalyzer.AnalyzeAsync(
                filePath,
                options.AnalysisProgress,
                options.VerboseEsmAnalysis,
                cancellationToken),
            AnalysisFileType.Minidump => await new MinidumpAnalyzer().AnalyzeAsync(
                filePath,
                options.AnalysisProgress,
                options.IncludeMetadata,
                options.VerboseMinidumpAnalysis,
                cancellationToken),
            AnalysisFileType.ClassicGameData => throw new NotSupportedException(
                "Classic game sources have no analyze-only phase — load them via SemanticFileLoader.LoadAsync, " +
                "which routes to ClassicGameAnalyzer."),
            _ => throw new InvalidOperationException($"Unsupported semantic file type: {fileType}")
        };

        if (fileType == AnalysisFileType.Minidump &&
            (options.GapRecovery.DiscoverCandidates || options.GapRecovery.AnyPromotion))
        {
            DiscoverDmpGapRecoveryCandidates(filePath, result, options.GapRecovery, cancellationToken);
        }

        return result;
    }

    private static void DiscoverDmpGapRecoveryCandidates(
        string filePath,
        AnalysisResult result,
        DmpGapRecoveryOptions options,
        CancellationToken cancellationToken)
    {
        if (result.EsmRecords == null || result.MinidumpInfo is not { IsValid: true })
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();

        using var mmf = MemoryMappedFile.CreateFromFile(
            filePath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        using var accessor = mmf.CreateViewAccessor(0, result.FileSize, MemoryMappedFileAccess.Read);
        var coverage = CoverageAnalyzer.Analyze(result, accessor);
        if (coverage.Error != null)
        {
            return;
        }

        using var stream = File.OpenRead(filePath);
        var rttiReader = new RttiReader(result.MinidumpInfo, stream);
        var recovery = DmpGapRecoveryScanner.Scan(
            result,
            coverage,
            new MmfMemoryAccessor(accessor),
            rttiReader,
            options);

        result.RecoverableGapCandidates.Clear();
        result.RecoverableGapCandidates.AddRange(recovery.Candidates);
        result.RecoverableGapSummary = recovery.Summary;
    }

    private static void ApplyDmpGapRecoveryPromotionsIfNeeded(
        AnalysisResult analysisResult,
        SemanticFileLoadOptions options)
    {
        if (analysisResult.EsmRecords == null ||
            !options.GapRecovery.AnyPromotion ||
            analysisResult.RecoverableGapCandidates.Count == 0)
        {
            return;
        }

        analysisResult.RecoverableGapPromotion = DmpGapRecoveryPromoter.Apply(
            analysisResult.EsmRecords,
            analysisResult.RecoverableGapCandidates,
            options.GapRecovery);
    }

    private static void ApplyCellWorldspaceAuthorityIfNeeded(
        RecordCollection records,
        EsmRecordScanResult scanResult,
        AnalysisFileType fileType,
        SemanticFileLoadOptions options)
    {
        if (fileType != AnalysisFileType.Minidump)
        {
            return;
        }

        var authority = options.CellWorldspaceAuthority;
        var metadata = options.CellMetadataAuthority;
        var refToCell = options.CellReferenceParentAuthority;
        var refWindows = options.CellReferenceParentWindows;
        var worldspaceNames = options.CellWorldspaceAuthorityWorldspaceNames;
        if (authority is null &&
            metadata is null &&
            refToCell is null &&
            refWindows is null &&
            options.ApplyDefaultCellWorldspaceAuthority)
        {
            var load = CellWorldspaceAuthorityJson.Load(options.CellWorldspaceAuthorityPath);
            authority = load.CellToWorldspace;
            metadata = load.Cells;
            refToCell = load.RefToCell;
            refWindows = load.RefWindows;
            worldspaceNames = load.WorldspaceNames;
        }

        CellWorldspaceAuthorityApplier.Apply(
            records,
            authority,
            worldspaceNames,
            scanResult,
            metadata,
            refToCell,
            refWindows);
    }
}
