using BethesdaMultitool.Core.Carving;

namespace BethesdaMultitool.Core.Extraction;

/// <summary>
///     Summary of extraction results.
/// </summary>
public class ExtractionSummary
{
    public int TotalExtracted { get; init; }
    public int DdxConverted { get; init; }
    public int DdxFailed { get; init; }
    public int ModulesExtracted { get; init; }
    public int ScriptsExtracted { get; init; }
    public int ScriptQuestsGrouped { get; init; }
    public Dictionary<string, int> TypeCounts { get; init; } = [];
    public HashSet<long> ExtractedOffsets { get; init; } = [];

    /// <summary>
    ///     Offsets of files that failed conversion (DDX -> DDS, XMA -> WAV, etc.).
    ///     These files were extracted but conversion failed.
    /// </summary>
    public HashSet<long> FailedConversionOffsets { get; init; } = [];

    /// <summary>
    ///     File offsets of extracted modules from minidump metadata.
    /// </summary>
    public HashSet<long> ExtractedModuleOffsets { get; init; } = [];

    /// <summary>
    ///     Whether an ESM semantic report was generated.
    /// </summary>
    public bool EsmReportGenerated { get; init; }

    /// <summary>
    ///     Number of heightmap PNG images exported.
    /// </summary>
    public int HeightmapsExported { get; init; }

    /// <summary>
    ///     Number of runtime in-memory textures exported as DDS.
    /// </summary>
    public int RuntimeTexturesExported { get; init; }

    /// <summary>
    ///     Number of runtime in-memory meshes exported as OBJ.
    /// </summary>
    public int RuntimeMeshesExported { get; init; }

    /// <summary>
    ///     Residency of the carved files. The carver has always measured this, but the entries it
    ///     measured it on were dropped before reaching the CLI, so nothing could report it.
    /// </summary>
    public CarveResidencySummary Residency { get; init; } = new();
}

/// <summary>
///     How much of the carved output was actually present in the dump. Aggregated from the manifest
///     entries so a run can say "23 of 4,100 files are incomplete, 4 of them structurally" instead
///     of leaving that only in <c>manifest.json</c>.
/// </summary>
public sealed class CarveResidencySummary
{
    /// <summary>Files with any missing bytes.</summary>
    public int PartialFiles { get; init; }

    /// <summary>Files missing only their tail — usually trailing detail, still usable.</summary>
    public int TailTruncatedFiles { get; init; }

    /// <summary>Files with at least one hole that is not the tail.</summary>
    public int InteriorHoleFiles { get; init; }

    /// <summary>Files whose gap landed in bytes their format needs to be structurally valid.</summary>
    public int CriticalRangeFiles { get; init; }

    /// <summary>Lowest coverage seen across the partial files, or 1.0 when none were partial.</summary>
    public double WorstCoverage { get; init; } = 1.0;
}
