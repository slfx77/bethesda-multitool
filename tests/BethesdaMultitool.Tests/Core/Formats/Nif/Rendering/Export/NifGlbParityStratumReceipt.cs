namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Export;

/// <summary>
///     Everything one run of a parity stratum measured, written as JSON under <c>TestOutput/nif-glb-parity/</c>
///     before any assertion so a failing run still leaves its evidence.
/// </summary>
/// <remarks>
///     The root session freezes <see cref="NifGlbParityRatchet" /> values from these receipts. Paths are relative to
///     the stratum root (or archive-internal), which is also the identity the SHA-256 selection order uses.
/// </remarks>
internal sealed class NifGlbParityStratumReceipt
{
    /// <summary>How many example paths are kept per decline reason or failing file.</summary>
    internal const int ExampleLimit = 8;

    /// <summary>The stratum identifier.</summary>
    public string StratumId { get; set; } = string.Empty;

    /// <summary>What the stratum covers.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>The scene assembly route under test.</summary>
    public string Route { get; set; } = string.Empty;

    /// <summary>The resolved corpus root.</summary>
    public string Root { get; set; } = string.Empty;

    /// <summary>The resolved supporting root (textures for SpeedTree), when the route needs one.</summary>
    public string? SupportRoot { get; set; }

    /// <summary>The sampled size N.</summary>
    public int SampleSize { get; set; }

    /// <summary>Whether <c>BMT_NIF_PARITY_FULL=1</c> selected every candidate.</summary>
    public bool FullStratum { get; set; }

    /// <summary>Candidates found under the root.</summary>
    public int Candidates { get; set; }

    /// <summary>Candidates selected by SHA-256 order.</summary>
    public int Selected { get; set; }

    /// <summary>Selected candidates actually processed.</summary>
    public int Inspected { get; set; }

    /// <summary>Named fixtures that were missing or did not match their pinned identity.</summary>
    public List<string> FixtureMismatches { get; set; } = [];

    /// <summary>Paths that failed to read, parse, convert or assemble.</summary>
    public List<string> ParseErrors { get; set; } = [];

    /// <summary>The message for each parse error.</summary>
    public SortedDictionary<string, string> ParseErrorMessages { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Paths that assembled no renderable scene.</summary>
    public List<string> NoScene { get; set; } = [];

    /// <summary>Files whose normalized plan reached comparison (Layer A, both encodings and Layer B).</summary>
    public int Compared { get; set; }

    /// <summary>The relative paths counted in <see cref="Compared" />, in selection order.</summary>
    public List<string> ComparedPaths { get; set; } = [];

    /// <summary>Compared files with no failure.</summary>
    public int Passed { get; set; }

    /// <summary>Declined files per <c>Stage: reason</c>.</summary>
    public SortedDictionary<string, int> Declines { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The first few declined paths per reason.</summary>
    public SortedDictionary<string, List<string>> DeclineExamples { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Files the shared validation or build rejected with invalid data; a failure, never a decline.</summary>
    public int SharedValidation { get; set; }

    /// <summary>The reason for each shared-validation fault.</summary>
    public SortedDictionary<string, string> SharedValidationFiles { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Files with at least one comparison failure (Layer A, Layer B, validator or an exception).</summary>
    public int ComparisonFailures { get; set; }

    /// <summary>The first failure messages per failing file.</summary>
    public SortedDictionary<string, List<string>> ComparisonFailureFiles { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Compared normalized primitive occurrences per feature class.</summary>
    public SortedDictionary<string, int> FeatureClasses { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Native COLOR_0 accessor types per drawn primitive occurrence (C9 measurement).</summary>
    public SortedDictionary<string, int> NativeColorAccessors { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Normalized COLOR_0 accessor types per drawn primitive occurrence.</summary>
    public SortedDictionary<string, int> SharedColorAccessors { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Paired triangles with diverging tangents per tangent class (C9 measurement).</summary>
    public SortedDictionary<string, int> TangentDivergences { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Normalized primitive occurrences whose native fallback tangents were proven inert.</summary>
    public int IgnoredFallbackTangentPrimitives { get; set; }

    /// <summary>Source triangles the native writer rejects for exactly repeated positions.</summary>
    public int RepeatedPositionDrops { get; set; }

    /// <summary>Material signatures with more normalized than native rows.</summary>
    public int MergedMaterialRowSignatures { get; set; }

    /// <summary>Normalized signatures referenced only by removed repeated-position triangles.</summary>
    public int RemovedOnlyMaterialSignatures { get; set; }

    /// <summary>Native triangles compared.</summary>
    public long NativeTriangles { get; set; }

    /// <summary>Normalized triangles compared, before repeated-position removal.</summary>
    public long SharedTriangles { get; set; }

    /// <summary>Total native GLB bytes.</summary>
    public long NativeBytes { get; set; }

    /// <summary>Total normalized GLB bytes.</summary>
    public long SharedBytes { get; set; }

    /// <summary>Planned files per source family.</summary>
    public SortedDictionary<string, int> Families { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Planned files converted from big-endian.</summary>
    public int ConvertedFromBigEndian { get; set; }

    /// <summary>Planned files whose family the production admission table admits (none in this round).</summary>
    public int ProductionAdmitted { get; set; }

    /// <summary>Files for which the production texture-source resolution found nothing.</summary>
    public int FilesWithoutTextureSources { get; set; }

    /// <summary>Files whose Starfield external geometry did not resolve completely.</summary>
    public int IncompleteExternalGeometry { get; set; }

    /// <summary>The validator executable, or <c>unavailable</c>.</summary>
    public string Validator { get; set; } = "unavailable";

    /// <summary>Normalized outputs the validator checked.</summary>
    public int ValidatedOutputs { get; set; }

    /// <summary>Validator errors across all outputs.</summary>
    public int ValidatorErrors { get; set; }

    /// <summary>Validator warnings across all outputs.</summary>
    public int ValidatorWarnings { get; set; }

    /// <summary>Information-only outcomes (the forced skinned fixture), never counted as declines or failures.</summary>
    public SortedDictionary<string, string> InformationOnly { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    ///     Every 25 inspected files: the live managed heap after a full compacting collection and the process's private
    ///     bytes, so a run that grows can be told apart as retained memory (live grows) or uncollected garbage (only
    ///     private grows).
    /// </summary>
    public List<string> MemoryTrace { get; set; } = [];

    /// <summary>Whether a frozen ratchet entry applied to this run.</summary>
    public string Ratchet { get; set; } = "empty";

    /// <summary>When the receipt was written.</summary>
    public DateTime GeneratedUtc { get; set; }

    /// <summary>Adds a report's tallies to this receipt.</summary>
    /// <param name="report">One file's oracle report.</param>
    internal void Absorb(NifGlbParityReport report)
    {
        Add(FeatureClasses, report.FeatureClasses);
        Add(NativeColorAccessors, report.NativeColorAccessors);
        Add(SharedColorAccessors, report.SharedColorAccessors);
        Add(TangentDivergences, report.TangentDivergences);
        IgnoredFallbackTangentPrimitives += report.IgnoredFallbackTangentPrimitives;
        MergedMaterialRowSignatures += report.MergedMaterialRowSignatures;
        RemovedOnlyMaterialSignatures += report.RemovedOnlyMaterialSignatures;
        NativeTriangles += report.NativeTriangles;
        SharedTriangles += report.SharedTriangles;
    }

    /// <summary>Records one failing file, keeping its first few messages.</summary>
    /// <param name="path">The failing relative path.</param>
    /// <param name="messages">What failed.</param>
    internal void RecordFailure(string path, IEnumerable<string> messages)
    {
        ComparisonFailures++;
        ComparisonFailureFiles[path] = messages.Take(ExampleLimit).ToList();
    }

    /// <summary>Records one declined file under its stage and reason.</summary>
    /// <param name="path">The declined relative path.</param>
    /// <param name="reason">The <c>Stage: reason</c> key.</param>
    internal void RecordDecline(string path, string reason)
    {
        Declines[reason] = Declines.GetValueOrDefault(reason) + 1;
        if (!DeclineExamples.TryGetValue(reason, out var examples))
        {
            examples = [];
            DeclineExamples.Add(reason, examples);
        }

        if (examples.Count < ExampleLimit)
        {
            examples.Add(path);
        }
    }

    /// <summary>Records one parse, conversion or assembly failure.</summary>
    /// <param name="path">The relative path.</param>
    /// <param name="message">What failed.</param>
    internal void RecordParseError(string path, string message)
    {
        ParseErrors.Add(path);
        ParseErrorMessages[path] = message;
    }

    /// <summary>Adds one tally into another.</summary>
    private static void Add(SortedDictionary<string, int> target, SortedDictionary<string, int> source)
    {
        foreach (var (key, value) in source)
        {
            target[key] = target.GetValueOrDefault(key) + value;
        }
    }
}
