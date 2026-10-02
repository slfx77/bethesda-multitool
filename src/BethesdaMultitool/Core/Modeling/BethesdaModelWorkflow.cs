using BethesdaMultitool.Core.AssetBrowse;
using BethesdaMultitool.Core.Modeling.Nif;
using Slfx77.Multitool.Core.Assets;
using Slfx77.Multitool.Core.Models.Catalog;
using Slfx77.Multitool.Core.Models.Export;
using Slfx77.Multitool.Core.Models.Inspection;
using Slfx77.Multitool.Core.Models.Inspection.Dump;
using Slfx77.Multitool.Core.Models.Sources;
using Slfx77.Multitool.Core.Operations;
using Slfx77.Multitool.Core.Settings;
using Slfx77.Multitool.Media.Blender;
using Slfx77.Multitool.Media.Blender.Operations;
using Slfx77.Multitool.Media.Models;
using Slfx77.Multitool.Media.Processing.Models;

namespace BethesdaMultitool.Core.Modeling;

/// <summary>
///     Composes BMT's model readers with the Shared inspection, dump, package and conversion operations, the GLB and
///     Blender writers and process memory admission (design section 3; plan section 5). It owns every input source it
///     opens until the operation and any returned unretired handles are finished, and never routes through a legacy
///     exporter.
/// </summary>
/// <remarks>
///     <para>
///         Inputs: a loose model file (its folder is the source), a directory of loose files (conversion only), or a
///         Bethesda archive (<c>.bsa</c>/<c>.ba2</c>) with an entry path; an archive without an entry converts every
///         model entry the readers recognize.
///     </para>
///     <para>
///         Texture companions (plan section 4, "Resolution"): the given data roots, else a Data root inferred by walking
///         up from the input to a folder holding <c>textures</c> (an archive input uses the archive's own folder), are
///         mounted as <see cref="BethesdaTextureCompanions" /> (loose files over archives) and handed to the operation as
///         its companion resolver. Without a data root the Shared default resolver applies (basenames within the input's
///         own source). The workflow owns that source and disposes it after the operation and every returned handle.
///     </para>
///     <para>
///         Skeletons (cut-1b slice 10): a <c>--skeleton</c> file is opened as a <see cref="BethesdaSkeletonCompanion" />,
///         its full path becomes the <see cref="BethesdaModelRegistration.SkeletonOption" /> app option, and its resolver
///         is composed over the texture resolver, so a <c>.kf</c> read finds exactly that file; without it the reader walks
///         up to the nearest ancestor <c>skeleton.nif</c> through the texture resolver. The workflow owns the companion as
///         it owns the texture source.
///     </para>
///     <para>
///         Debug commands (plan section 5, "Debug commands"): <see cref="ValidateAsync" /> and
///         <see cref="FidelityAsync" /> go through the Shared inspection operation like <see cref="InfoAsync" />;
///         <see cref="DumpAsync" /> goes through the Shared <see cref="ModelDumpOperation" /> (the exact schema-version-1 JSON of
///         <c>docs/model-dump-json.md</c>) and <see cref="PackageAsync" /> through the Shared
///         <see cref="ModelPackageOperation" />, whose package-only preflight
///         (<see cref="ModelBlendWriter.PreflightPackage" />) neither locates nor runs Blender, so no host Blender state
///         can fail it. BMT keeps no read path of its own: the Shared read lifetime is internal to those operations.
///     </para>
/// </remarks>
public static class BethesdaModelWorkflow
{
    /// <summary>The companion allowance per item: encoded textures and DDX relayout buffers (slice 6).</summary>
    public const long CompanionAllowanceBytes = 256L * 1024 * 1024;

    /// <summary>The writer and preparation workspace reserved per item on top of the source and companions.</summary>
    public const long WriterWorkspaceBytes = 512L * 1024 * 1024;

    /// <summary>The registered Blender writer format.</summary>
    private const string BlendFormat = "blend";

    /// <summary>Creates the registry of BMT model readers.</summary>
    public static ModelSourceRegistry CreateReaders()
    {
        return BethesdaModelRegistration.CreateReaders();
    }

    /// <summary>Registers the Shared GLB and Blender writers; Blender is located through the machine tool settings.</summary>
    /// <param name="settings">The machine-level external tool settings, or null for the default store.</param>
    /// <returns>The writer registry used by both inspection and conversion.</returns>
    public static ModelWriterRegistry CreateWriters(ExternalToolSettings? settings = null)
    {
        return new ModelWriterRegistry([new ModelGlbWriter(), new ModelBlendWriter(settings)]);
    }

    /// <summary>
    ///     Captures the registered readers' and writers' static descriptions for <c>mesh formats</c>, without probing,
    ///     reading, preflighting or locating any tool.
    /// </summary>
    /// <param name="cancellationToken">Cancels between registrations.</param>
    /// <returns>The Shared catalog in registration order.</returns>
    /// <exception cref="InvalidDataException">A registration changed identity or the catalog exceeds its text budget.</exception>
    public static ModelFormatCatalog CreateFormatCatalog(CancellationToken cancellationToken = default)
    {
        return ModelFormatCatalog.Capture(CreateReaders(), CreateWriters(), cancellationToken);
    }

    /// <summary>Estimates one item's memory: its source length, the companion allowance and writer workspace.</summary>
    /// <param name="item">The planned input.</param>
    /// <returns>The estimate the Shared memory gate admits against.</returns>
    public static ModelMemoryEstimate EstimateMemory(ModelInputItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return new ModelMemoryEstimate(item.Source.Entry.Length ?? NifModelReader.MaximumSourceBytes,
            CompanionAllowanceBytes + WriterWorkspaceBytes,
            "Source length (256 MiB NIF bound when unknown), 256 MiB for encoded texture companions and DDX relayout, " +
            "and 512 MiB of writer and image preparation workspace.");
    }

    /// <summary>Builds the app options the BMT readers interpret.</summary>
    /// <param name="game">A game name (<c>fnv</c>, <c>fo3</c>, ...), <c>auto</c>, or null.</param>
    /// <param name="gameEvidence">How the game was established, quoted in the unit evidence.</param>
    /// <param name="platform">
    ///     The console a big-endian NIF was shipped for (<c>x360</c> or <c>ps3</c>), or null when not established (the
    ///     reader then assumes X360 and says so).
    /// </param>
    /// <param name="skeleton">
    ///     The skeleton path a <c>.kf</c> binds to (<see cref="BethesdaModelRegistration.SkeletonOption" />), exactly as
    ///     the companion resolver knows it, or null to walk up from the <c>.kf</c>.
    /// </param>
    /// <returns>The immutable option bag.</returns>
    public static IReadOnlyDictionary<string, string> CreateAppOptions(string? game, string? gameEvidence = null,
        string? platform = null, string? skeleton = null)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(game) && !string.Equals(game, "auto", StringComparison.OrdinalIgnoreCase))
        {
            options[BethesdaModelRegistration.GameOption] = game;
            options[BethesdaModelRegistration.GameEvidenceOption] = gameEvidence ?? "--game " + game;
        }

        if (!string.IsNullOrWhiteSpace(platform))
        {
            options[BethesdaModelRegistration.PlatformOption] = platform;
        }

        if (!string.IsNullOrWhiteSpace(skeleton))
        {
            options[BethesdaModelRegistration.SkeletonOption] = skeleton;
        }

        return options;
    }

    /// <summary>The inspection request <c>mesh info</c> uses: every registered writer planned with its defaults.</summary>
    /// <param name="item">The planned input; its source stays owned by the caller.</param>
    /// <param name="game">The game whose units apply, or null.</param>
    /// <param name="resolver">The companion resolver, or null for Shared's default.</param>
    /// <param name="platform">The console platform of a big-endian input, or null.</param>
    /// <param name="skeleton">The <see cref="BethesdaModelRegistration.SkeletonOption" /> value, or null.</param>
    /// <returns>A pixel-free inspection request.</returns>
    public static ModelInfoRequest CreateInspectionRequest(ModelInputItem item, string? game,
        ModelCompanionResolver? resolver = null, string? platform = null, string? skeleton = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        return new ModelInfoRequest(item, _ => BethesdaModelRegistration.CreateCache(), EstimateMemory,
            appOptions: CreateAppOptions(game, platform: platform, skeleton: skeleton), companionResolver: resolver);
    }

    /// <summary>
    ///     The request <c>mesh validate</c> uses: the same pixel-free read and structural validation, with no writer
    ///     planned (AWE's pattern).
    /// </summary>
    /// <param name="item">The planned input; its source stays owned by the caller.</param>
    /// <param name="game">The game whose units apply, or null.</param>
    /// <param name="resolver">The companion resolver, or null for Shared's default.</param>
    /// <param name="platform">The console platform of a big-endian input, or null.</param>
    /// <param name="skeleton">The <see cref="BethesdaModelRegistration.SkeletonOption" /> value, or null.</param>
    /// <returns>An inspection request with an empty writer list.</returns>
    /// <remarks>
    ///     Success validates the normalized document's structure, not that every source byte is supported (coverage says
    ///     that), that encoded pixels decode, or that a writer can preserve the model.
    /// </remarks>
    public static ModelInfoRequest CreateValidationRequest(ModelInputItem item, string? game,
        ModelCompanionResolver? resolver = null, string? platform = null, string? skeleton = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        return new ModelInfoRequest(item, _ => BethesdaModelRegistration.CreateCache(), EstimateMemory,
            writers: Array.Empty<ModelConvertOptions>(),
            appOptions: CreateAppOptions(game, platform: platform, skeleton: skeleton), companionResolver: resolver);
    }

    /// <summary>
    ///     The request <c>mesh fidelity</c> uses: one writer, with its plan resolved through the writer's own preparation
    ///     (the read runs with the conversion purpose; no model is written and no tool is started).
    /// </summary>
    /// <param name="item">The planned input; its source stays owned by the caller.</param>
    /// <param name="options">The selected writer and scale.</param>
    /// <param name="game">The game whose units apply, or null.</param>
    /// <param name="resolver">The companion resolver, or null for Shared's default.</param>
    /// <param name="platform">The console platform of a big-endian input, or null.</param>
    /// <param name="skeleton">The <see cref="BethesdaModelRegistration.SkeletonOption" /> value, or null.</param>
    /// <returns>A fidelity-resolving inspection request.</returns>
    public static ModelInfoRequest CreateFidelityRequest(ModelInputItem item, ModelConvertOptions options,
        string? game, ModelCompanionResolver? resolver = null, string? platform = null, string? skeleton = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(options);
        return new ModelInfoRequest(item, _ => BethesdaModelRegistration.CreateCache(), EstimateMemory,
            writers: [options], appOptions: CreateAppOptions(game, platform: platform, skeleton: skeleton),
            companionResolver: resolver, resolveFidelity: true);
    }

    /// <summary>
    ///     The request <c>mesh dump</c> uses (AWE's <c>CreateDumpRequest</c>): the same cache, memory estimate, app options
    ///     and companion resolver as inspection, with the borrowed UTF-8 destination the Shared operation writes the exact
    ///     schema-version-1 document to and never closes.
    /// </summary>
    /// <param name="item">The planned input; its source stays owned by the caller through execution and retirement.</param>
    /// <param name="output">The borrowed writable destination.</param>
    /// <param name="includeNative">
    ///     Whether the reader retains full native payloads and raw bytes (<c>--native</c>); otherwise metadata-only
    ///     native rows. Every retained row is serialized in either mode.
    /// </param>
    /// <param name="game">The game whose units apply, or null.</param>
    /// <param name="resolver">The companion resolver, or null for Shared's default.</param>
    /// <param name="platform">The console platform of a big-endian input, or null.</param>
    /// <param name="skeleton">The <see cref="BethesdaModelRegistration.SkeletonOption" /> value, or null.</param>
    /// <returns>A pixel-free dump request.</returns>
    /// <exception cref="ArgumentException">The destination is not writable.</exception>
    public static ModelDumpRequest CreateDumpRequest(ModelInputItem item, Stream output, bool includeNative,
        string? game, ModelCompanionResolver? resolver = null, string? platform = null, string? skeleton = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(output);
        return new ModelDumpRequest(item, output, _ => BethesdaModelRegistration.CreateCache(), EstimateMemory,
            includeNative, CreateAppOptions(game, platform: platform, skeleton: skeleton), resolver);
    }

    /// <summary>
    ///     The request <c>mesh package</c> uses (AWE's <c>CreatePackageRequest</c>): the same cache, memory estimate, app
    ///     options and companion resolver as conversion, the Blender writer options and the exact zip destination. It
    ///     neither discovers nor launches Blender.
    /// </summary>
    /// <param name="item">The planned input; its source stays owned by the caller through execution and retirement.</param>
    /// <param name="output">The zip to publish; resolved to a full path here.</param>
    /// <param name="options">The Shared Blender writer, scale and overwrite options.</param>
    /// <param name="game">The game whose units apply, or null.</param>
    /// <param name="resolver">The companion resolver, or null for Shared's default.</param>
    /// <param name="platform">The console platform of a big-endian input, or null.</param>
    /// <param name="skeleton">The <see cref="BethesdaModelRegistration.SkeletonOption" /> value, or null.</param>
    /// <returns>The Shared package request.</returns>
    /// <exception cref="ArgumentException">
    ///     The destination is not a <c>.zip</c>, aliases the physical input, or the options select another writer.
    /// </exception>
    public static ModelPackageRequest CreatePackageRequest(ModelInputItem item, string output,
        ModelConvertOptions options, string? game, ModelCompanionResolver? resolver = null, string? platform = null,
        string? skeleton = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(output);
        return new ModelPackageRequest(item, Path.GetFullPath(output), _ => BethesdaModelRegistration.CreateCache(),
            EstimateMemory, options, appOptions: CreateAppOptions(game, platform: platform, skeleton: skeleton),
            companionResolver: resolver);
    }

    /// <summary>Inspects one model: a loose file, or one entry of a Bethesda archive.</summary>
    /// <param name="input">The model file or archive.</param>
    /// <param name="entry">The archive entry path; required for archives, rejected for loose files.</param>
    /// <param name="game">The game whose units apply, or null.</param>
    /// <param name="platform">The console platform of a big-endian input (<c>x360</c> or <c>ps3</c>), or null.</param>
    /// <param name="dataRoots">Data folders to resolve textures from, earlier first; null or empty infers one.</param>
    /// <param name="memory">The memory admission gate; null uses the process observer's gate.</param>
    /// <param name="cancellationToken">Cancels probing, admission, reading and writer planning.</param>
    /// <param name="skeleton">The skeleton file a <c>.kf</c> input binds to (<c>--skeleton</c>), or null to walk up.</param>
    /// <returns>The Shared inspection result, with any unretired handles already retired.</returns>
    /// <exception cref="FileNotFoundException">The skeleton file does not exist.</exception>
    public static Task<ModelInfoResult> InfoAsync(string input, string? entry, string? game, string? platform,
        IReadOnlyList<string>? dataRoots = null, ModelMemoryGate? memory = null,
        CancellationToken cancellationToken = default, string? skeleton = null)
    {
        return InspectAsync(input, entry, dataRoots, memory, skeleton,
            (item, resolver, skeletonPath) => CreateInspectionRequest(item, game, resolver, platform, skeletonPath),
            cancellationToken);
    }

    /// <summary>Validates one model's normalized structure without planning any writer (<c>mesh validate</c>).</summary>
    /// <param name="input">The model file or archive.</param>
    /// <param name="entry">The archive entry path; required for archives, rejected for loose files.</param>
    /// <param name="game">The game whose units apply, or null.</param>
    /// <param name="platform">The console platform of a big-endian input (<c>x360</c> or <c>ps3</c>), or null.</param>
    /// <param name="dataRoots">Data folders to resolve textures from, earlier first; null or empty infers one.</param>
    /// <param name="memory">The memory admission gate; null uses the process observer's gate.</param>
    /// <param name="cancellationToken">Cancels probing, admission, reading and validation.</param>
    /// <param name="skeleton">The skeleton file a <c>.kf</c> input binds to (<c>--skeleton</c>), or null to walk up.</param>
    /// <returns>The Shared inspection result with no writer rows; its exit code is 0 or 1 (130 on cancellation).</returns>
    /// <exception cref="FileNotFoundException">The skeleton file does not exist.</exception>
    public static Task<ModelInfoResult> ValidateAsync(string input, string? entry, string? game, string? platform,
        IReadOnlyList<string>? dataRoots = null, ModelMemoryGate? memory = null,
        CancellationToken cancellationToken = default, string? skeleton = null)
    {
        return InspectAsync(input, entry, dataRoots, memory, skeleton,
            (item, resolver, skeletonPath) => CreateValidationRequest(item, game, resolver, platform, skeletonPath),
            cancellationToken);
    }

    /// <summary>Resolves one writer's complete fidelity without writing a model (<c>mesh fidelity</c>).</summary>
    /// <param name="input">The model file or archive.</param>
    /// <param name="entry">The archive entry path; required for archives, rejected for loose files.</param>
    /// <param name="format">The registered writer: <c>glb</c> or <c>blend</c>.</param>
    /// <param name="scale">The finite positive multiplier applied after conversion to meters.</param>
    /// <param name="game">The game whose units apply, or null.</param>
    /// <param name="platform">The console platform of a big-endian input (<c>x360</c> or <c>ps3</c>), or null.</param>
    /// <param name="dataRoots">Data folders to resolve textures from, earlier first; null or empty infers one.</param>
    /// <param name="memory">The memory admission gate; null uses the process observer's gate.</param>
    /// <param name="cancellationToken">Cancels probing, admission, reading, preparation and resolution.</param>
    /// <param name="skeleton">The skeleton file a <c>.kf</c> input binds to (<c>--skeleton</c>), or null to walk up.</param>
    /// <returns>The Shared inspection result carrying the one writer's resolved report.</returns>
    /// <exception cref="FileNotFoundException">The skeleton file does not exist.</exception>
    public static Task<ModelInfoResult> FidelityAsync(string input, string? entry, string format, double scale,
        string? game, string? platform, IReadOnlyList<string>? dataRoots = null, ModelMemoryGate? memory = null,
        CancellationToken cancellationToken = default, string? skeleton = null)
    {
        var options = new ModelConvertOptions(format, scale);
        return InspectAsync(input, entry, dataRoots, memory, skeleton,
            (item, resolver, skeletonPath) =>
                CreateFidelityRequest(item, options, game, resolver, platform, skeletonPath), cancellationToken);
    }

    /// <summary>
    ///     Streams one model's exact document as Shared JSON (<c>mesh dump</c>, AWE's <c>DumpAsync</c>): opens the input
    ///     and its companion source, runs the Shared <see cref="ModelDumpOperation" /> against the borrowed destination,
    ///     retires the returned handles, and closes the sources.
    /// </summary>
    /// <param name="input">The model file or archive.</param>
    /// <param name="entry">The archive entry path; required for archives, rejected for loose files.</param>
    /// <param name="output">The borrowed UTF-8 destination, left open on every outcome.</param>
    /// <param name="includeNative">Whether to retain full native payloads and raw bytes instead of metadata only.</param>
    /// <param name="game">The game whose units apply, or null.</param>
    /// <param name="platform">The console platform of a big-endian input (<c>x360</c> or <c>ps3</c>), or null.</param>
    /// <param name="dataRoots">Data folders to resolve textures from, earlier first; null or empty infers one.</param>
    /// <param name="memory">The memory admission gate; null uses the process observer's gate.</param>
    /// <param name="cancellationToken">Cancels probing, admission, reading and serialization.</param>
    /// <param name="skeleton">The skeleton file a <c>.kf</c> input binds to (<c>--skeleton</c>), or null to walk up.</param>
    /// <returns>The Shared terminal result (exit 0 complete, 1 failed, 2 ambiguous, 130 canceled), handles retired.</returns>
    /// <exception cref="FileNotFoundException">The skeleton file does not exist.</exception>
    /// <remarks>
    ///     The operation reads with the inspection purpose (no pixel decoding) and writes incrementally: a failed or
    ///     canceled dump may leave partial JSON in the destination. A caller needing an atomic file uses
    ///     <see cref="DumpToFileAsync" />.
    /// </remarks>
    public static async Task<ModelDumpResult> DumpAsync(string input, string? entry, Stream output,
        bool includeNative, string? game, string? platform, IReadOnlyList<string>? dataRoots = null,
        ModelMemoryGate? memory = null, CancellationToken cancellationToken = default, string? skeleton = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        await using var skeletonCompanion = BethesdaSkeletonCompanion.Open(skeleton);
        await using var opened = await OpenSingleAsync(input, entry, cancellationToken).ConfigureAwait(false);
        await using var companions = OpenTextureCompanions(input, dataRoots);
        var resolver = ComposeResolver(companions, skeletonCompanion);
        var request = CreateDumpRequest(opened.Item, output, includeNative, game, resolver, platform,
            skeletonCompanion?.Path);
        var result = await new ModelDumpOperation(CreateReaders(), memory ?? ModelProcessMemoryObserver.CreateGate())
            .ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
        await RetireReturnedResourcesAsync(result).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    ///     Dumps one model to a file (<c>mesh dump --output</c>, AWE's staged source-JSON export): the JSON is generated
    ///     into a sibling temporary file through <see cref="StagedStreamExport.WriteGeneratedAsync" /> and published only
    ///     when the Shared operation reports a complete envelope, so a failed dump never leaves a partial file.
    /// </summary>
    /// <param name="input">The model file or archive.</param>
    /// <param name="entry">The archive entry path; required for archives, rejected for loose files.</param>
    /// <param name="outputPath">The JSON file to publish.</param>
    /// <param name="overwrite">Whether an existing file may be replaced.</param>
    /// <param name="includeNative">Whether to retain full native payloads and raw bytes instead of metadata only.</param>
    /// <param name="game">The game whose units apply, or null.</param>
    /// <param name="platform">The console platform of a big-endian input (<c>x360</c> or <c>ps3</c>), or null.</param>
    /// <param name="dataRoots">Data folders to resolve textures from, earlier first; null or empty infers one.</param>
    /// <param name="memory">The memory admission gate; null uses the process observer's gate.</param>
    /// <param name="cancellationToken">Cancels probing, admission, reading, serialization and publication.</param>
    /// <param name="skeleton">The skeleton file a <c>.kf</c> input binds to (<c>--skeleton</c>), or null to walk up.</param>
    /// <returns>The completed Shared result (exit 0), handles retired.</returns>
    /// <exception cref="IOException">
    ///     The destination is the model input, a directory, or an existing file without <paramref name="overwrite" />
    ///     (checked before reading); or publication failed.
    /// </exception>
    /// <exception cref="InvalidDataException">The operation did not complete the envelope; nothing was published.</exception>
    /// <exception cref="OperationCanceledException">The dump was canceled; nothing was published.</exception>
    public static async Task<ModelDumpResult> DumpToFileAsync(string input, string? entry, string outputPath,
        bool overwrite, bool includeNative, string? game, string? platform, IReadOnlyList<string>? dataRoots = null,
        ModelMemoryGate? memory = null, CancellationToken cancellationToken = default, string? skeleton = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var destination = ModelPathPolicy.NormalizeAbsolute(outputPath);
        if (ModelPathPolicy.Comparer.Equals(destination, ModelPathPolicy.NormalizeAbsolute(input)))
        {
            throw new IOException($"The dump destination '{destination}' is the model input itself.");
        }

        if (Directory.Exists(destination))
        {
            throw new IOException($"The dump destination '{destination}' is a directory.");
        }

        if (!overwrite && File.Exists(destination))
        {
            throw new IOException($"The dump file '{destination}' already exists; pass --overwrite to replace it.");
        }

        ModelDumpResult? completed = null;
        await StagedStreamExport.WriteGeneratedAsync(destination, async (output, token) =>
        {
            var result = await DumpAsync(input, entry, output, includeNative, game, platform, dataRoots, memory, token,
                    skeleton)
                .ConfigureAwait(false);
            if (result.Status == ModelInfoStatus.Canceled)
            {
                throw new OperationCanceledException(result.Reason ?? "The model dump was canceled.", token);
            }

            if (!result.OutputComplete || result.ExitCode != 0)
            {
                throw new InvalidDataException($"The model dump did not complete ({result.Status}): " +
                                               (result.Reason ?? result.CleanupFailure ?? "the JSON output is incomplete."));
            }

            completed = result;
        }, overwrite, cancellationToken: cancellationToken).ConfigureAwait(false);
        return completed ?? throw new InvalidDataException("The model dump returned no result.");
    }

    /// <summary>
    ///     Writes the Blender package (<c>package.zip</c>: manifest, streams, images) for one model without running
    ///     Blender (<c>mesh package</c>, AWE's <c>PackageAsync</c>): opens the input and its companion source, runs the
    ///     Shared <see cref="ModelPackageOperation" />, retires the returned handles, and closes the sources.
    /// </summary>
    /// <param name="input">The model file or archive.</param>
    /// <param name="entry">The archive entry path; required for archives, rejected for loose files.</param>
    /// <param name="output">The zip to publish (a <c>.zip</c> name; resolved to a full path).</param>
    /// <param name="scale">The finite positive multiplier applied after conversion to meters.</param>
    /// <param name="overwrite">Whether an existing zip may be replaced; otherwise the item is Skipped (exit 0).</param>
    /// <param name="game">The game whose units apply, or null.</param>
    /// <param name="platform">The console platform of a big-endian input (<c>x360</c> or <c>ps3</c>), or null.</param>
    /// <param name="dataRoots">Data folders to resolve textures from, earlier first; null or empty infers one.</param>
    /// <param name="memory">The memory admission gate; null uses the process observer's gate.</param>
    /// <param name="cancellationToken">Cancels probing, admission, reading, planning and packaging.</param>
    /// <param name="skeleton">The skeleton file a <c>.kf</c> input binds to (<c>--skeleton</c>), or null to walk up.</param>
    /// <returns>The Shared result (Converted or Skipped exit 0, failure 1, canceled 130), handles retired.</returns>
    /// <exception cref="ArgumentException">The output is not a <c>.zip</c> name or aliases the physical input.</exception>
    /// <remarks>
    ///     The operation's preflight is <see cref="ModelBlendWriter.PreflightPackage" />: it parses the writer options and
    ///     locates no tool, so a missing, unknown or rejected host Blender cannot fail packaging. The package is staged
    ///     beside the output and moved into place, so a failed write never leaves a partial output; a destination that
    ///     appears during the run is reported as Skipped, never replaced without <paramref name="overwrite" />.
    /// </remarks>
    public static async Task<ModelPackageResult> PackageAsync(string input, string? entry, string output,
        double scale, bool overwrite, string? game, string? platform, IReadOnlyList<string>? dataRoots = null,
        ModelMemoryGate? memory = null, CancellationToken cancellationToken = default, string? skeleton = null)
    {
        await using var skeletonCompanion = BethesdaSkeletonCompanion.Open(skeleton);
        await using var opened = await OpenSingleAsync(input, entry, cancellationToken).ConfigureAwait(false);
        await using var companions = OpenTextureCompanions(input, dataRoots);
        var resolver = ComposeResolver(companions, skeletonCompanion);
        var request = CreatePackageRequest(opened.Item, output, new ModelConvertOptions(BlendFormat, scale, overwrite),
            game, resolver, platform, skeletonCompanion?.Path);
        var result = await new ModelPackageOperation(CreateReaders(), memory ?? ModelProcessMemoryObserver.CreateGate())
            .ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
        await RetireReturnedResourcesAsync(result).ConfigureAwait(false);
        return result;
    }

    /// <summary>Converts a model file, a directory of models, or archive entries through the Shared operation.</summary>
    /// <param name="input">A model file, a directory, or a Bethesda archive.</param>
    /// <param name="entry">For an archive, one entry path; null converts every recognized entry.</param>
    /// <param name="output">A destination directory, or an exact file name for a single input.</param>
    /// <param name="options">The Shared writer, scale and overwrite options.</param>
    /// <param name="game">The game whose units apply, or null.</param>
    /// <param name="platform">The console platform of a big-endian input (<c>x360</c> or <c>ps3</c>), or null.</param>
    /// <param name="dataRoots">Data folders to resolve textures from, earlier first; null or empty infers one.</param>
    /// <param name="memory">The memory admission gate; null uses the process observer's gate.</param>
    /// <param name="cancellationToken">Cancels planning, reading, writing and external execution.</param>
    /// <param name="skeleton">
    ///     The skeleton file every <c>.kf</c> input binds to (<c>--skeleton</c>), or null for each to walk up to its own
    ///     nearest ancestor <c>skeleton.nif</c>.
    /// </param>
    /// <returns>The Shared per-item results; returned handles are retired before the sources close.</returns>
    /// <exception cref="FileNotFoundException">The skeleton file does not exist.</exception>
    public static async Task<ModelConvertResult> ConvertAsync(string input, string? entry, string output,
        ModelConvertOptions options, string? game, string? platform, IReadOnlyList<string>? dataRoots = null,
        ModelMemoryGate? memory = null, CancellationToken cancellationToken = default, string? skeleton = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);
        ArgumentNullException.ThrowIfNull(options);
        await using var skeletonCompanion = BethesdaSkeletonCompanion.Open(skeleton);
        var full = Path.GetFullPath(input);
        IAssetSource source;
        IAsyncEnumerable<ModelInputItem> inputs;
        if (Directory.Exists(full))
        {
            if (entry is not null)
            {
                throw new ArgumentException("--entry applies to archive inputs only.", nameof(entry));
            }

            source = new FolderAssetSource(full);
            inputs = ModelInputExpansion.DirectoryAsync(source, full, cancellationToken: cancellationToken);
        }
        else if (IsArchive(full))
        {
            source = OpenArchive(full);
            var wanted = entry is null ? null : AssetPath.Normalize(entry.Replace('\\', '/'));
            inputs = ModelInputExpansion.ContainerAsync(source, full,
                wanted is null ? null : candidate => source.PathComparer.Equals(candidate.Reference.Path, wanted),
                explicitlyNamed: wanted is not null, cancellationToken: cancellationToken);
        }
        else
        {
            var opened = await OpenSingleAsync(full, entry, cancellationToken).ConfigureAwait(false);
            source = opened.Source;
            inputs = Single(opened.Item);
        }

        await using (source.ConfigureAwait(false))
        {
            await using var companions = OpenTextureCompanions(full, dataRoots);
            var resolver = ComposeResolver(companions, skeletonCompanion);
            var request = new ModelConvertRequest(
                await ModelInputExpansion.CreateRequestAsync(inputs, output, options, cancellationToken: cancellationToken)
                    .ConfigureAwait(false),
                _ => BethesdaModelRegistration.CreateCache(), EstimateMemory,
                appOptions: CreateAppOptions(game, platform: platform, skeleton: skeletonCompanion?.Path),
                companionResolver: resolver);
            var writers = CreateWriters();
            var result = await new ModelConvertOperation(new ModelOutputPlanner(CreateReaders(), writers), writers,
                memory ?? ModelProcessMemoryObserver.CreateGate()).ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
            await RetireReturnedResourcesAsync(result).ConfigureAwait(false);
            return result;
        }
    }

    /// <summary>Retires the input stream and cache a dump returned unretired (AWE's rule: retry before the source closes).</summary>
    /// <param name="result">The dump result; its cleanup failure stays reported after a successful retry.</param>
    /// <returns>Completion after the returned input and cache got another retirement attempt.</returns>
    public static ValueTask RetireReturnedResourcesAsync(ModelDumpResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return RetireAsync(result.UnretiredInput, result.UnretiredCache);
    }

    /// <summary>Retires the input stream and cache a package run returned unretired (AWE's rule).</summary>
    /// <param name="result">The package result; its cleanup failure stays reported after a successful retry.</param>
    /// <returns>Completion after the returned input and cache got another retirement attempt.</returns>
    public static ValueTask RetireReturnedResourcesAsync(ModelPackageResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return RetireAsync(result.UnretiredInput, result.UnretiredCache);
    }

    /// <summary>Retires every input stream and cache a conversion returned unretired, keeping the first failures.</summary>
    /// <param name="result">The conversion result.</param>
    /// <returns>Completion after every returned resource got a retirement attempt.</returns>
    /// <exception cref="AggregateException">A retirement failed.</exception>
    public static async ValueTask RetireReturnedResourcesAsync(ModelConvertResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        List<Exception>? failures = null;
        foreach (var item in result.Items)
        {
            try
            {
                await RetireAsync(item.UnretiredInput, item.UnretiredCache).ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                (failures ??= []).Add(failure);
            }
        }

        if (failures is not null)
        {
            throw new AggregateException("Model input retirement failed.", failures);
        }
    }

    /// <summary>
    ///     Opens the texture companion source for an input: the explicit data roots, else the inferred one (an archive's own
    ///     folder, or the nearest ancestor of a loose input holding a <c>textures</c> folder), else none.
    /// </summary>
    /// <param name="input">The model file, directory or archive.</param>
    /// <param name="dataRoots">Explicit data roots, or null or empty to infer.</param>
    /// <returns>The companion source the caller owns and disposes, or null when no data root applies.</returns>
    /// <exception cref="DirectoryNotFoundException">An explicit data root does not exist.</exception>
    public static BethesdaTextureCompanions? OpenTextureCompanions(string input, IReadOnlyList<string>? dataRoots)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);
        if (dataRoots is { Count: > 0 })
        {
            return BethesdaTextureCompanions.OpenDataRoots(dataRoots);
        }

        var full = Path.GetFullPath(input);
        if (IsArchive(full) && File.Exists(full))
        {
            return Path.GetDirectoryName(full) is { Length: > 0 } folder
                ? BethesdaTextureCompanions.OpenDataRoots([folder])
                : null;
        }

        return BethesdaTextureCompanions.TryInferDataRoot(full, out var root)
            ? BethesdaTextureCompanions.OpenDataRoots([root])
            : null;
    }

    /// <summary>Whether a path names a Bethesda archive by extension (the archive itself is opened by content).</summary>
    /// <param name="path">A file path.</param>
    /// <returns>True for <c>.bsa</c> and <c>.ba2</c>.</returns>
    public static bool IsArchive(string path)
    {
        var extension = Path.GetExtension(path);
        return string.Equals(extension, ".bsa", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(extension, ".ba2", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Runs one Shared inspection of a single explicitly named model: opens it and its companion source, builds the
    ///     request, executes it and retires the returned handles before the sources close.
    /// </summary>
    private static async Task<ModelInfoResult> InspectAsync(string input, string? entry,
        IReadOnlyList<string>? dataRoots, ModelMemoryGate? memory, string? skeleton,
        Func<ModelInputItem, ModelCompanionResolver?, string?, ModelInfoRequest> createRequest,
        CancellationToken cancellationToken)
    {
        await using var skeletonCompanion = BethesdaSkeletonCompanion.Open(skeleton);
        await using var opened = await OpenSingleAsync(input, entry, cancellationToken).ConfigureAwait(false);
        await using var companions = OpenTextureCompanions(input, dataRoots);
        var resolver = ComposeResolver(companions, skeletonCompanion);
        var request = createRequest(opened.Item, resolver, skeletonCompanion?.Path);
        var result = await new ModelInfoOperation(CreateReaders(), CreateWriters(), memory ?? ModelProcessMemoryObserver.CreateGate())
            .ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
        await RetireAsync(result.UnretiredInput, result.UnretiredCache).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    ///     The companion resolver a request carries: the texture companions' (or none, for Shared's default), with the
    ///     <c>--skeleton</c> companion composed over it when one was given.
    /// </summary>
    private static ModelCompanionResolver? ComposeResolver(BethesdaTextureCompanions? companions,
        BethesdaSkeletonCompanion? skeleton)
    {
        var inner = companions is null ? null : new ModelCompanionResolver(companions.ResolveAsync);
        return skeleton is null ? inner : skeleton.Compose(inner);
    }

    /// <summary>Opens one explicitly named model: a loose file in its folder, or one archive entry.</summary>
    private static async Task<OpenedModel> OpenSingleAsync(string input, string? entry, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);
        var full = Path.GetFullPath(input);
        if (!File.Exists(full))
        {
            throw new FileNotFoundException("The model input must be an existing file.", full);
        }

        if (!IsArchive(full))
        {
            if (entry is not null)
            {
                throw new ArgumentException("--entry applies to archive inputs only.", nameof(entry));
            }

            var folder = new FolderAssetSource(Path.GetDirectoryName(full)!);
            var reference = new AssetReference(folder.Id, Path.GetFileName(full));
            var item = ModelInputItem.File(new ModelSourceItem(folder,
                new AssetEntry(reference, new FileInfo(full).Length, Provenance: full)), full);
            return new OpenedModel(folder, item);
        }

        if (string.IsNullOrWhiteSpace(entry))
        {
            throw new ArgumentException("An archive input needs --entry naming the model inside it.", nameof(entry));
        }

        var archive = OpenArchive(full);
        try
        {
            var wanted = AssetPath.Normalize(entry.Replace('\\', '/'));
            var directory = wanted.Contains('/', StringComparison.Ordinal) ? wanted[..wanted.LastIndexOf('/')] : null;
            await foreach (var candidate in archive.EnumerateAsync(directory, cancellationToken).ConfigureAwait(false))
            {
                if (archive.PathComparer.Equals(candidate.Reference.Path, wanted))
                {
                    var item = ModelInputItem.ContainerEntry(new ModelSourceItem(archive, candidate), full,
                        candidate.Reference.Path, explicitlyNamed: true);
                    return new OpenedModel(archive, item);
                }
            }

            throw new FileNotFoundException($"The archive has no entry '{entry}'.", full);
        }
        catch
        {
            await archive.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Opens an archive as a leased asset source, preferring the game-aware archive session.</summary>
    private static BethesdaBrowseSource OpenArchive(string archivePath)
    {
        var session = AssetBrowseSession.TryOpenGameArchive(archivePath) ?? AssetBrowseSession.OpenArchive(archivePath);
        return new BethesdaBrowseSource(session);
    }

    /// <summary>Yields one already planned item.</summary>
    private static async IAsyncEnumerable<ModelInputItem> Single(ModelInputItem item)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        yield return item;
    }

    /// <summary>Retires a returned input stream and cache.</summary>
    private static async ValueTask RetireAsync(Stream? input, IModelReadCacheScope? cache)
    {
        try
        {
            if (input is not null)
            {
                await input.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            if (cache is not null)
            {
                await cache.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>An opened source and the one item planned from it; disposing it releases the source.</summary>
    private sealed record OpenedModel(IAssetSource Source, ModelInputItem Item) : IAsyncDisposable
    {
        /// <summary>Releases the source.</summary>
        public ValueTask DisposeAsync()
        {
            return Source.DisposeAsync();
        }
    }
}
