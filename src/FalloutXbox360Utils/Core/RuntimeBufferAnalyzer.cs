using System.IO.MemoryMappedFiles;
using FalloutXbox360Utils.Core.Formats.EsmRecord;
using FalloutXbox360Utils.Core.Minidump;

namespace FalloutXbox360Utils.Core;

/// <summary>
///     Analyzes unmatched gaps in memory dumps to identify runtime buffers,
///     string pools, and data structures.
/// </summary>
internal sealed partial class RuntimeBufferAnalyzer
{
    #region Constants

    private const int MinStringLength = 4;

    private const int MaxStringLength = 512;

    private const int MaxSampleStrings = 20;

    private const int SignatureScanBytes = 512;

    private static readonly HashSet<string> KnownFileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".nif", ".dds", ".ddx", ".kf", ".wav", ".lip", ".psc", ".txt",
        ".esm", ".esp", ".bsa", ".xml", ".ini", ".fuz", ".xwm", ".bik",
        ".mp3", ".ogg", ".xur", ".xui", ".scda"
    };

    private static readonly HashSet<string> GameAssetPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "meshes", "textures", "sound", "interface", "menus", "scripts",
        "shaders", "music", "video", "strings", "grass", "trees",
        "landscape", "actors", "characters", "creatures", "effects",
        "clutter", "architecture", "weapons", "armor", "lodsettings",
        "data", "bsa", "esm", "esp"
    };

    #endregion

    #region Fields

    private readonly MemoryMappedViewAccessor _accessor;

    private readonly CoverageResult _coverage;

    private readonly long _fileSize;

    private readonly MinidumpInfo _minidumpInfo;

    private readonly uint _moduleEnd;

    private readonly uint _moduleStart;

    private readonly PdbAnalysisResult? _pdbAnalysis;

    #endregion

    #region Constructor

    public RuntimeBufferAnalyzer(
        MemoryMappedViewAccessor accessor,
        long fileSize,
        MinidumpInfo minidumpInfo,
        CoverageResult coverage,
        PdbAnalysisResult? pdbAnalysis)
    {
        _accessor = accessor;
        _fileSize = fileSize;
        _minidumpInfo = minidumpInfo;
        _coverage = coverage;
        _pdbAnalysis = pdbAnalysis;

        var gameModule = MemoryDumpAnalyzer.FindGameModule(minidumpInfo);
        if (gameModule != null)
        {
            _moduleStart = gameModule.BaseAddress32;
            _moduleEnd = (uint)(gameModule.BaseAddress + gameModule.Size);
        }
    }

    #endregion

    #region Public API

    /// <summary>
    ///     Perform full buffer exploration analysis.
    /// </summary>
    public BufferExplorationResult Analyze()
    {
        var result = new BufferExplorationResult();

        if (_pdbAnalysis != null)
        {
            RunManagerWalk(result);
        }

        RunStringPoolExtraction(result);
        RunBinarySignatureScan(result);
        RunPointerGraphAnalysis(result);

        return result;
    }

    /// <summary>
    ///     Run only the string pool extraction pass (no PDB required).
    ///     Used by the analyze command to enrich ESM reconstruction output.
    /// </summary>
    public StringPoolSummary ExtractStringPoolOnly()
    {
        var result = new BufferExplorationResult();
        RunStringPoolExtraction(result);
        return result.StringPools!;
    }

    /// <summary>
    ///     Cross-reference string pool file paths with carved files from analysis.
    /// </summary>
    public static void CrossReferenceWithCarvedFiles(
        StringPoolSummary summary,
        IReadOnlyList<CarvedFileInfo> carvedFiles)
    {
        if (summary.AllFilePaths.Count == 0 || carvedFiles.Count == 0)
        {
            return;
        }

        // Build a set of carved file name suffixes for fast lookup
        var carvedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var carved in carvedFiles)
        {
            var name = carved.FileName;
            if (!string.IsNullOrEmpty(name))
            {
                carvedNames.Add(Path.GetFileName(name));
            }
        }

        var matched = 0;
        foreach (var path in summary.AllFilePaths)
        {
            var fileName = path;
            var lastSep = path.LastIndexOfAny(['\\', '/']);
            if (lastSep >= 0 && lastSep < path.Length - 1)
            {
                fileName = path[(lastSep + 1)..];
            }

            if (carvedNames.Contains(fileName))
            {
                matched++;
            }
        }

        summary.MatchedToCarvedFiles = matched;
        summary.UnmatchedFilePaths = summary.AllFilePaths.Count - matched;
    }

    #endregion
}
