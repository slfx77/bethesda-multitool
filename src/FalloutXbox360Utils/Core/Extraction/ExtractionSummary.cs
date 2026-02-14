namespace FalloutXbox360Utils.Core.Extraction;

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
}
