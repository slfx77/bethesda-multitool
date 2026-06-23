using System.IO.MemoryMappedFiles;
using BethesdaMultitool.Core.Analysis;
using BethesdaMultitool.Core.Coverage;
using BethesdaMultitool.Core.FileFormat;
using BethesdaMultitool.Core.Formats.Esm.Analysis;
using BethesdaMultitool.Core.Formats.Esm;
using BethesdaMultitool.Core.Formats.Esm.Localization;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Runtime;
using BethesdaMultitool.Core.Formats.SaveGame.Models;
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

    /// <summary>Analyzes the file and parses it into a disposable <see cref="UnifiedAnalysisResult" /> with records and a FormID resolver.</summary>
    internal static async Task<UnifiedAnalysisResult> LoadAsync(
        string filePath,
        SemanticFileLoadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new SemanticFileLoadOptions();

        var fileType = ResolveSemanticFileType(filePath, options.FileType);
        var analysisResult = await AnalyzeOnlyAsync(filePath, options, cancellationToken);
        return LoadFromAnalysisResult(filePath, analysisResult, fileType, options);
    }

    /// <summary>Runs only the format-specific analysis phase (no semantic parse), returning the raw <see cref="AnalysisResult" />.</summary>
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

    /// <summary>Parses an already-computed <see cref="AnalysisResult" /> into a <see cref="UnifiedAnalysisResult" /> using the given options.</summary>
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
        if (analysisResult.EsmRecords == null)
        {
            throw new InvalidOperationException(
                $"No records found in {Path.GetFileName(filePath)}. The file may be empty or corrupted.");
        }

        ApplyDmpGapRecoveryPromotionsIfNeeded(analysisResult, options);

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

        // Fallout 76 stores terrain heights in external .btd files, not in-record VHGT. Attach them to
        // exterior cells so the 2D/3D terrain renderers (which read through the shared height
        // abstraction) light up. No-op for every other game.
        if (fileType == AnalysisFileType.EsmFile)
        {
            Formats.Esm.Land.Fo76TerrainInjector.Inject(records, filePath);
        }

        var resolver = records.CreateResolver(analysisResult.FormIdMap);

        return new UnifiedAnalysisResult
        {
            FileType = fileType,
            Records = records,
            Resolver = resolver,
            RawResult = analysisResult,
            FilePath = filePath
        };
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
        Formats.Esm.Models.RecordCollection records,
        Formats.Esm.Records.EsmRecordScanResult scanResult,
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

