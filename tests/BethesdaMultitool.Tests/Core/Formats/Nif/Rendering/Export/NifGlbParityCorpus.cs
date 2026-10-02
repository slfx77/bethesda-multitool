using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BethesdaMultitool.CLI.Rendering.Nif;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Export;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Viewer;
using BethesdaMultitool.Core.Formats.SpeedTree;
using BethesdaMultitool.Tests.Helpers;
using SharpGLTF.Schema2;
using Xunit;
using Xunit.Sdk;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Export;

/// <summary>
///     Drives the production parity corpus (plan section 3.3): stratum definitions, deterministic selection, the
///     production assembly routes, the forced normalized plan, both encodings, Layer A and Layer B, and the receipt.
/// </summary>
/// <remarks>
///     <para>
///         Production admits no family in this round, so every file is planned with the
///         <see cref="NifGlbWriterPreference.Normalized" /> preference through the admission seam of
///         <see cref="NifGlbExport.Plan(GlbScene, NifTextureResolver, NifGlbExportRequest, Func{NifExportFamily, bool, bool}, CancellationToken)" />
///         with every family admitted. A refused plan is a decline, except stage
///         <see cref="NifGlbDeclineStage.SharedValidation" />, which the gate counts as a failure (decision C2).
///     </para>
///     <para>
///         Selection sorts candidates by the SHA-256 of their lower-cased, forward-slash relative path and takes the
///         first N; <c>BMT_NIF_PARITY_FULL=1</c> takes every candidate (decision C6). The order is stable across runs
///         and machines, and unlike alphabetical order it does not sample one directory.
///     </para>
/// </remarks>
internal static class NifGlbParityCorpus
{
    /// <summary>Set to <c>1</c> to inspect every candidate in a stratum instead of the sampled N.</summary>
    internal const string FullStratumVariable = "BMT_NIF_PARITY_FULL";

    private const string FnvPcData = "Unpacked_Builds/PC_Final_Unpacked/Data";
    private const string FnvPcMeshes = FnvPcData + "/meshes";
    private const string FnvXboxMeshes = "Unpacked_Builds/360_July_Unpacked/FalloutNV/Data/meshes";
    private const string Fo3MeshesArchive = @"Builds\Fallout 3 (2026-2-15, Steam - Final)\Data\Fallout - Meshes.bsa";

    /// <summary>The ten named retail trees, the same set <c>SptNeutralSceneExportTests</c> pins.</summary>
    private static readonly string[] RetailTreeNames =
    [
        "euonymusbush01", "oasiselm01", "oasiselm02", "oasistreetop01", "pine01",
        "sugarmaple01", "sycamore01", "wastelandshrub01", "wastelandundergrowth01", "whiteoak01"
    ];

    /// <summary>The SpeedTree generation seeds each tree is exported at.</summary>
    private static readonly uint[] SpeedTreeSeeds = [1, 2, 7];

    /// <summary>
    ///     The fixed retail fixtures and their pinned SHA-256, the identities <c>NifNeutralSceneRetailCorpusTests</c>
    ///     pins. The skinned upper body is forced through the normalized preference for information only.
    /// </summary>
    private static readonly (string Path, string Hash, bool InformationOnly)[] Fixtures =
    [
        ("meshes/clutter/junk/tincan01.nif",
            "E928406FDE567A4C933A0F9CB5344B7F5E9F70E66B76A2C65B45FC4E62D79BF1", false),
        ("meshes/architecture/goodsprings/NV_ProspectorSaloon-Neon_Lights.NIF",
            "2DA30703F96B9C59F9135CA692F88D32B2DB94B4438E356805A4E59071A8BDED", false),
        ("meshes/characters/_male/upperbody.nif",
            "65064044AAA4B2EE40D720921E633E57E25E4B9118E01B3F90F94CE83B73AC1A", true)
    ];

    private static readonly JsonSerializerOptions ReceiptOptions = new() { WriteIndented = true };

    /// <summary>Whether this run inspects every candidate (<c>BMT_NIF_PARITY_FULL=1</c>).</summary>
    internal static bool FullStratum =>
        string.Equals(Environment.GetEnvironmentVariable(FullStratumVariable), "1", StringComparison.Ordinal);

    /// <summary>The admission seam's predicate: every family, in either conversion state, is admitted.</summary>
    internal static Func<NifExportFamily, bool, bool> AdmitEveryFamily { get; } = static (_, _) => true;

    /// <summary>Every stratum, one theory row each.</summary>
    internal static IReadOnlyList<NifGlbParityStratum> Strata { get; } =
    [
        new("S1", "FNV PC loose meshes, textured", NifGlbParityRoute.CliAssembly, 600,
            $"RealAssetPaths.SampleDirectory(\"{FnvPcMeshes}\")",
            static () => RealAssetPaths.SampleDirectory(FnvPcMeshes)) { RequiresTextureSources = true },
        new("S2", "FNV PC loose meshes through the Mesh Viewer", NifGlbParityRoute.GuiAssembly, 150,
            $"RealAssetPaths.SampleDirectory(\"{FnvPcMeshes}\") via NifBrowserService.CreateFromDirectory",
            static () => RealAssetPaths.SampleDirectory(FnvPcMeshes)),
        new("S3", "FNV Xbox 360 July 2010 loose meshes, big-endian", NifGlbParityRoute.CliAssembly, 100,
            $"RealAssetPaths.SampleDirectory(\"{FnvXboxMeshes}\")",
            static () => RealAssetPaths.SampleDirectory(FnvXboxMeshes))
        {
            RequiresBigEndianConversion = true,
            RequiresTextureSources = true
        },
        new("S4", "Oblivion", NifGlbParityRoute.GuiAssembly, 150,
            "RealAssetPaths.SteamGameFile(\"Oblivion\", \"Data/Oblivion - Meshes.bsa\") via CreateFromBsa",
            static () => RealAssetPaths.SteamGameFile("Oblivion", @"Data\Oblivion - Meshes.bsa")),
        new("S5", "Fallout 3", NifGlbParityRoute.GuiAssembly, 150,
            "RealAssetPaths.SampleFile(the Fallout 3 build's Fallout - Meshes.bsa), then Steam \"Fallout 3 goty\"",
            static () => RealAssetPaths.SampleFile(Fo3MeshesArchive) ??
                         RealAssetPaths.SteamGameFile("Fallout 3 goty", @"Data\Fallout - Meshes.bsa")),
        new("S6-Skyrim", "Skyrim", NifGlbParityRoute.GuiAssembly, 100,
            "RealAssetPaths.SteamGameFile(\"Skyrim\", \"Data/Skyrim - Meshes.bsa\") via CreateFromBsa",
            static () => RealAssetPaths.SteamGameFile("Skyrim", @"Data\Skyrim - Meshes.bsa")),
        new("S6-SkyrimSE", "Skyrim Special Edition", NifGlbParityRoute.GuiAssembly, 100,
            "RealAssetPaths.SteamGameFile(\"Skyrim Special Edition\", \"Data/Skyrim - Meshes0.bsa\") via CreateFromBsa",
            static () => RealAssetPaths.SteamGameFile("Skyrim Special Edition", @"Data\Skyrim - Meshes0.bsa")),
        new("S7-FO4", "Fallout 4", NifGlbParityRoute.GuiAssembly, 100,
            "RealAssetPaths.SteamGameFile(\"Fallout 4\", \"Data/Fallout4 - Meshes.ba2\") via CreateFromBsa",
            static () => RealAssetPaths.SteamGameFile("Fallout 4", @"Data\Fallout4 - Meshes.ba2")),
        new("S7-FO76", "Fallout 76", NifGlbParityRoute.GuiAssembly, 100,
            "RealAssetPaths.SteamGameFile(\"Fallout76\", \"Data/SeventySix - Meshes.ba2\") via CreateFromBsa",
            static () => RealAssetPaths.SteamGameFile("Fallout76", @"Data\SeventySix - Meshes.ba2")),
        new("S7-Starfield", "Starfield", NifGlbParityRoute.GuiAssembly, 100,
            "RealAssetPaths.SteamGameFile(\"Starfield\", \"Data/Starfield - Meshes01.ba2\") via CreateFromBsa",
            static () => RealAssetPaths.SteamGameFile("Starfield", @"Data\Starfield - Meshes01.ba2")),
        new("S8", "SpeedTree retail trees at seeds 1, 2 and 7", NifGlbParityRoute.SpeedTree,
            RetailTreeNames.Length * SpeedTreeSeeds.Length,
            $"TestOutput/fnv_spt/trees under {RealAssetPaths.RootVariable} or this checkout",
            SpeedTreeRoot)
        {
            ResolveSupportRoot = static () => RealAssetPaths.SampleDirectory(FnvPcData),
            SupportDescription = $"RealAssetPaths.SampleDirectory(\"{FnvPcData}\") for textures"
        },
        new("S9", "Fixed FNV retail fixtures", NifGlbParityRoute.FixedFixtures, Fixtures.Length,
            $"RealAssetPaths.SampleDirectory(\"{FnvPcData}\")",
            static () => RealAssetPaths.SampleDirectory(FnvPcData))
        {
            RequiredComparedPaths = [.. Fixtures.Where(static fixture => !fixture.InformationOnly)
                .Select(static fixture => fixture.Path)]
        }
    ];

    /// <summary>Finds a stratum by identifier.</summary>
    /// <param name="id">The stratum identifier.</param>
    /// <returns>The stratum.</returns>
    internal static NifGlbParityStratum Stratum(string id) => Strata.Single(stratum => stratum.Id == id);

    /// <summary>The selection key: SHA-256 of the lower-cased relative path with forward slashes.</summary>
    /// <param name="relativePath">A path relative to the stratum root, or archive-internal.</param>
    /// <returns>An uppercase hexadecimal digest.</returns>
    internal static string OrderingKey(string relativePath) =>
        Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(relativePath.Replace('\\', '/').ToLowerInvariant())));

    /// <summary>Orders candidates by <see cref="OrderingKey" /> and takes the first N, or all of them.</summary>
    /// <typeparam name="T">The candidate type.</typeparam>
    /// <param name="candidates">Every candidate.</param>
    /// <param name="relativePath">Each candidate's relative path.</param>
    /// <param name="sampleSize">N.</param>
    /// <param name="full">Whether to take every candidate.</param>
    /// <returns>The selected candidates in selection order.</returns>
    internal static List<T> Select<T>(IEnumerable<T> candidates, Func<T, string> relativePath, int sampleSize,
        bool full)
    {
        var ordered = candidates
            .Select(candidate => (Candidate: candidate, Path: relativePath(candidate)))
            .Select(entry => (entry.Candidate, entry.Path, Key: OrderingKey(entry.Path)))
            .OrderBy(static entry => entry.Key, StringComparer.Ordinal)
            .ThenBy(static entry => entry.Path, StringComparer.Ordinal)
            .Select(static entry => entry.Candidate);
        return (full ? ordered : ordered.Take(sampleSize)).ToList();
    }

    /// <summary>Plans a scene with the normalized preference, admitting every family through the test seam.</summary>
    /// <param name="scene">The assembled scene; planning never modifies it.</param>
    /// <param name="resolver">The resolver both writers use.</param>
    /// <param name="request">The request; its preference is replaced by Normalized.</param>
    /// <param name="cancellationToken">Cancels planning.</param>
    /// <returns>The plan.</returns>
    internal static NifGlbExportPlan PlanNormalized(GlbScene scene, NifTextureResolver resolver,
        NifGlbExportRequest request, CancellationToken cancellationToken) =>
        NifGlbExport.Plan(scene, resolver, request with { Preference = NifGlbWriterPreference.Normalized },
            AdmitEveryFamily, cancellationToken);

    /// <summary>Plans, proves Layer A and encodes both writers, failing the test if the plan is refused.</summary>
    /// <param name="scene">The assembled scene; the native writer normalizes its winding last.</param>
    /// <param name="resolver">The resolver both writers use.</param>
    /// <param name="request">The request; its preference is replaced by Normalized.</param>
    /// <param name="cancellationToken">Cancels planning and encoding.</param>
    /// <returns>Both encodings and the Layer A mapping.</returns>
    internal static NifGlbParityPair EncodePair(GlbScene scene, NifTextureResolver resolver,
        NifGlbExportRequest request, CancellationToken cancellationToken)
    {
        var snapshot = new NifCorpusSourceSnapshot(scene);
        var plan = PlanNormalized(scene, resolver, request, cancellationToken);
        Assert.False(plan.Decision.Refused, $"{plan.Decision.Stage}: {plan.Decision.Reason}");
        return EncodePlanned(scene, resolver, plan, snapshot, cancellationToken);
    }

    /// <summary>
    ///     Proves Layer A on a normalized plan, then encodes the normalized bytes and, last, the native bytes, because
    ///     the native writer normalizes winding on the source in place (plan finding 8).
    /// </summary>
    /// <param name="scene">The planned scene.</param>
    /// <param name="resolver">The resolver both writers use.</param>
    /// <param name="plan">A normalized plan for this scene.</param>
    /// <param name="snapshot">
    ///     The source snapshot taken before planning, or null when the source graph could not be serialized (the
    ///     caller records that as a failure of its own).
    /// </param>
    /// <param name="cancellationToken">Cancels Layer A and encoding.</param>
    /// <returns>Both encodings and the Layer A mapping.</returns>
    internal static NifGlbParityPair EncodePlanned(GlbScene scene, NifTextureResolver resolver,
        NifGlbExportPlan plan, NifCorpusSourceSnapshot? snapshot, CancellationToken cancellationToken)
    {
        Assert.True(plan.Decision.IsNormalized, $"The plan took the {plan.Decision.Route} route.");
        snapshot?.AssertUnchanged(scene);
        var mapping = NifGlbSourceParity.Check(scene, plan.ModelDocument!, resolver, cancellationToken);
        snapshot?.AssertUnchanged(scene);
        var repeated = NifCorpusLegacyTriangles.RepeatedPositions(scene, resolver);
        var sharedBytes = NifGlbExport.WriteToBytes(plan, scene, resolver, cancellationToken);
        var nativeBytes = GlbWriter.WriteToBytes(scene, resolver);
        return new NifGlbParityPair(plan, mapping, repeated, nativeBytes, sharedBytes);
    }

    /// <summary>Runs one stratum and returns its measured receipt without asserting anything.</summary>
    /// <param name="stratum">The stratum.</param>
    /// <param name="root">Its resolved root.</param>
    /// <param name="supportRoot">Its resolved supporting root, when the route needs one.</param>
    /// <param name="validator">The Khronos validator executable, or null when unavailable.</param>
    /// <param name="full">Whether to inspect every candidate.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>The receipt.</returns>
    internal static NifGlbParityStratumReceipt Run(NifGlbParityStratum stratum, string root, string? supportRoot,
        string? validator, bool full, CancellationToken cancellationToken)
    {
        var receipt = new NifGlbParityStratumReceipt
        {
            StratumId = stratum.Id,
            Description = stratum.Description,
            Route = stratum.Route.ToString(),
            Root = root,
            SupportRoot = supportRoot,
            SampleSize = stratum.SampleSize,
            FullStratum = full,
            Validator = validator ?? "unavailable"
        };
        switch (stratum.Route)
        {
            case NifGlbParityRoute.CliAssembly:
                RunCli(stratum, root, receipt, validator, full, cancellationToken);
                break;
            case NifGlbParityRoute.GuiAssembly:
                RunGui(stratum, root, receipt, validator, full, cancellationToken);
                break;
            case NifGlbParityRoute.SpeedTree:
                RunSpeedTree(stratum, root, supportRoot!, receipt, validator, full, cancellationToken);
                break;
            case NifGlbParityRoute.FixedFixtures:
                RunFixtures(root, receipt, validator, cancellationToken);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(stratum), stratum.Route, "Unknown parity route.");
        }

        receipt.GeneratedUtc = DateTime.UtcNow;
        return receipt;
    }

    /// <summary>
    ///     Every 25 inspected files, compacts the heap and records the live managed bytes and the private bytes; the
    ///     collection also returns uncollected large-object garbage to the machine during a long stratum.
    /// </summary>
    /// <param name="receipt">The stratum's receipt.</param>
    private static void TraceMemory(NifGlbParityStratumReceipt receipt)
    {
        if (receipt.Inspected % 25 != 0) return;
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        var live = GC.GetTotalMemory(forceFullCollection: true);
        var info = GC.GetGCMemoryInfo(GCKind.FullBlocking);
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        receipt.MemoryTrace.Add(string.Create(CultureInfo.InvariantCulture,
            $"{receipt.Inspected}: live {live / 1048576} MiB, GC committed {info.TotalCommittedBytes / 1048576} MiB, " +
            $"private {process.PrivateMemorySize64 / 1048576} MiB, handles {process.HandleCount}"));
    }

    /// <summary>Writes a receipt under <c>TestOutput/nif-glb-parity/</c> and returns its path.</summary>
    /// <param name="receipt">The measured receipt.</param>
    /// <returns>The written file.</returns>
    internal static string WriteReceipt(NifGlbParityStratumReceipt receipt)
    {
        var directory = Path.Combine(SourceContract.RepoRoot, "TestOutput", "nif-glb-parity");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory,
            receipt.StratumId + (receipt.FullStratum ? ".full" : string.Empty) + ".receipt.json");
        File.WriteAllText(path, JsonSerializer.Serialize(receipt, ReceiptOptions));
        return path;
    }

    /// <summary>Every rule the receipt breaks: the unconditional ones, the stratum's own, then the ratchet's.</summary>
    /// <param name="stratum">The stratum.</param>
    /// <param name="receipt">Its measured receipt; <see cref="NifGlbParityStratumReceipt.Ratchet" /> is updated.</param>
    /// <param name="entry">The frozen ratchet entry, or null when the stratum is not frozen.</param>
    /// <returns>The violations; empty when the stratum passes.</returns>
    internal static List<string> Violations(NifGlbParityStratum stratum, NifGlbParityStratumReceipt receipt,
        NifGlbParityRatchetEntry? entry)
    {
        var violations = new List<string>();
        if (receipt.ComparisonFailures != 0)
        {
            violations.Add($"{receipt.ComparisonFailures} compared file(s) failed: " +
                           string.Join(", ", receipt.ComparisonFailureFiles.Keys.Take(5)));
        }

        if (receipt.SharedValidation != 0)
        {
            violations.Add($"{receipt.SharedValidation} file(s) reached stage SharedValidation, which counts as a " +
                           "failure: " + string.Join(", ", receipt.SharedValidationFiles.Keys.Take(5)));
        }

        if (receipt.FixtureMismatches.Count != 0)
        {
            violations.Add("Named fixtures missing or changed: " + string.Join("; ", receipt.FixtureMismatches));
        }

        if (!receipt.FullStratum && receipt.Candidates < stratum.SampleSize)
        {
            violations.Add($"Only {receipt.Candidates} candidate(s) exist, fewer than N = {stratum.SampleSize}.");
        }

        if (receipt.Inspected != receipt.Selected)
        {
            violations.Add($"Inspected {receipt.Inspected} of {receipt.Selected} selected candidates.");
        }

        if (stratum.RequiresBigEndianConversion && receipt.ConvertedFromBigEndian == 0)
        {
            violations.Add("No inspected file was converted from big-endian, so the stratum did not exercise it.");
        }

        if (stratum.RequiresTextureSources && receipt.FilesWithoutTextureSources != 0)
        {
            violations.Add($"{receipt.FilesWithoutTextureSources} file(s) resolved no texture sources.");
        }

        foreach (var path in stratum.RequiredComparedPaths)
        {
            if (!receipt.ComparedPaths.Contains(path, StringComparer.Ordinal))
            {
                violations.Add($"Required fixture '{path}' was not compared.");
            }
        }

        if (entry is null)
        {
            receipt.Ratchet = "empty";
            return violations;
        }

        if (receipt.FullStratum)
        {
            receipt.Ratchet = "frozen for the sampled mode; not applied in full-stratum mode";
            return violations;
        }

        receipt.Ratchet = "frozen";
        AddSetViolation(violations, "parse-error", entry.ParseErrors, receipt.ParseErrors);
        AddSetViolation(violations, "no-scene", entry.NoScene, receipt.NoScene);
        if (entry.ComparedFloor is { } floor && receipt.Compared < floor)
        {
            violations.Add($"Compared {receipt.Compared} file(s), below the frozen floor of {floor}.");
        }

        if (entry.DeclineCeilings is { } ceilings)
        {
            foreach (var (reason, count) in receipt.Declines)
            {
                if (!ceilings.TryGetValue(reason, out var ceiling))
                {
                    violations.Add($"Decline reason is not on the allowlist: {reason} ({count})");
                }
                else if (count > ceiling)
                {
                    violations.Add($"Decline reason exceeds its ceiling of {ceiling}: {reason} ({count})");
                }
            }
        }

        foreach (var featureClass in entry.RequiredFeatureClasses ?? [])
        {
            if (receipt.FeatureClasses.GetValueOrDefault(featureClass) < 1)
            {
                violations.Add($"No compared primitive covers feature class {featureClass}.");
            }
        }

        return violations;
    }

    /// <summary>Resolves the extracted SpeedTree fixtures exactly as <c>SptNeutralSceneExportTests</c> does.</summary>
    private static string? SpeedTreeRoot()
    {
        var external = Environment.GetEnvironmentVariable(RealAssetPaths.RootVariable);
        if (!string.IsNullOrWhiteSpace(external))
        {
            var nested = Path.Combine(external, "TestOutput", "fnv_spt", "trees");
            if (Directory.Exists(nested))
            {
                return nested;
            }

            if (Directory.Exists(external) && Directory.EnumerateFiles(external, "*.spt").Any())
            {
                return external;
            }
        }

        var local = Path.Combine(SourceContract.RepoRoot, "TestOutput", "fnv_spt", "trees");
        return Directory.Exists(local) ? local : null;
    }

    /// <summary>The CLI route over loose files.</summary>
    private static void RunCli(NifGlbParityStratum stratum, string root, NifGlbParityStratumReceipt receipt,
        string? validator, bool full, CancellationToken cancellationToken)
    {
        var candidates = Directory.EnumerateFiles(root, "*.nif", SearchOption.AllDirectories)
            .Select(fullPath =>
                (FullPath: fullPath, Relative: Path.GetRelativePath(root, fullPath).Replace('\\', '/')))
            .ToList();
        receipt.Candidates = candidates.Count;
        var selected = Select(candidates, static candidate => candidate.Relative, stratum.SampleSize, full);
        receipt.Selected = selected.Count;
        foreach (var (fullPath, relative) in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            receipt.Inspected++;
            TraceMemory(receipt);
            RunCliFile(receipt, fullPath, relative, validator, false, cancellationToken);
        }
    }

    /// <summary>One file through the CLI <c>export nif</c> assembly and its production texture sources.</summary>
    private static void RunCliFile(NifGlbParityStratumReceipt receipt, string fullPath, string relative,
        string? validator, bool informationOnly, CancellationToken cancellationToken)
    {
        byte[] data;
        NifInfo? nif;
        bool converted;
        try
        {
            var raw = File.ReadAllBytes(fullPath);
            if (!NifExportSceneAssembly.TryParseForExport(raw, out data, out nif, out converted, out var error))
            {
                receipt.RecordParseError(relative, error);
                return;
            }
        }
        catch (Exception exception) when (Recordable(exception))
        {
            receipt.RecordParseError(relative, Describe(exception));
            return;
        }

        var sources = NifExportPathResolver.ResolveTextureSourcePaths(fullPath, null, null, out _) ?? [];
        if (sources.Length == 0)
        {
            receipt.FilesWithoutTextureSources++;
        }

        using var resolver = sources.Length > 0 ? new NifTextureResolver(sources) : new NifTextureResolver();
        GlbScene? scene;
        try
        {
            scene = NifExportSceneAssembly.BuildForExport(data, nif, fullPath, resolver);
        }
        catch (Exception exception) when (Recordable(exception))
        {
            receipt.RecordParseError(relative, "CLI assembly threw " + Describe(exception));
            return;
        }

        if (scene is null || scene.MeshParts.Count == 0)
        {
            receipt.NoScene.Add(relative);
            return;
        }

        Compare(receipt, relative, scene, resolver,
            new NifGlbExportRequest(Path.GetFileNameWithoutExtension(fullPath), NifExportFamilies.FromHeader(nif),
                converted, NifGlbWriterPreference.Normalized), validator, informationOnly, cancellationToken);
    }

    /// <summary>The GUI Mesh Viewer route over a loose directory or a mesh archive.</summary>
    private static void RunGui(NifGlbParityStratum stratum, string root, NifGlbParityStratumReceipt receipt,
        string? validator, bool full, CancellationToken cancellationToken)
    {
        var archive = File.Exists(root);
        var service = archive
            ? NifBrowserService.CreateFromBsa(root)
            : NifBrowserService.CreateFromDirectory(root);
        try
        {
            var candidates = Flatten(service.ListNifFiles(null, cancellationToken))
                .Select(entry => (Entry: entry,
                    Relative: (archive ? entry : Path.GetRelativePath(root, entry)).Replace('\\', '/')))
                .ToList();
            receipt.Candidates = candidates.Count;
            var selected = Select(candidates, static candidate => candidate.Relative, stratum.SampleSize, full);
            receipt.Selected = selected.Count;

            foreach (var (entry, relative) in selected)
            {
                cancellationToken.ThrowIfCancellationRequested();
                receipt.Inspected++;
                TraceMemory(receipt);
                // The service's own texture resolver keeps every texture it has decoded for its lifetime (only the
                // GUI's memory coordinator trims it), so a long stratum renews the service every ServiceRenewal files.
                if (receipt.Inspected % ServiceRenewal == 0)
                {
                    service.Dispose();
                    service = archive ? NifBrowserService.CreateFromBsa(root) : NifBrowserService.CreateFromDirectory(root);
                }

                // The service builds its own lazy resolver from exactly these paths; the export mirrors that
                // construction, one resolver per file as the CLI export does, so decoded textures never accumulate.
                var sources = service.TexturePaths;
                using var resolver = sources.Length > 0 ? new NifTextureResolver(sources) : new NifTextureResolver();
                RunGuiFile(receipt, service, resolver, entry, relative, validator, cancellationToken);
            }
        }
        finally
        {
            service.Dispose();
        }
    }

    /// <summary>How many GUI-route files one browser service serves before it is renewed to release its texture cache.</summary>
    private const int ServiceRenewal = 25;

    /// <summary>One entry through <c>BuildViewerSceneWithDiagnostics</c> and <c>ToGlbScene</c>, as the GUI export does.</summary>
    private static void RunGuiFile(NifGlbParityStratumReceipt receipt, NifBrowserService service,
        NifTextureResolver resolver, string entry, string relative, string? validator,
        CancellationToken cancellationToken)
    {
        byte[]? raw;
        NifInfo? header;
        GlbScene? scene;
        try
        {
            raw = service.ReadNifData(entry);
            header = raw is null ? null : NifParser.Parse(raw);
            if (raw is null || header is null)
            {
                receipt.RecordParseError(relative, raw is null ? "The entry could not be read." :
                    "NifParser.Parse returned no model.");
                return;
            }

            var build = service.BuildViewerSceneWithDiagnostics(raw, entry);
            if (!build.ExternalGeometry.IsComplete)
            {
                receipt.IncompleteExternalGeometry++;
            }

            scene = build.Scene is null ? null : BethesdaViewerSceneGlbAdapter.ToGlbScene(build.Scene);
        }
        catch (Exception exception) when (Recordable(exception))
        {
            receipt.RecordParseError(relative, "GUI assembly threw " + Describe(exception));
            return;
        }

        if (scene is null || scene.MeshParts.Count == 0)
        {
            receipt.NoScene.Add(relative);
            return;
        }

        Compare(receipt, relative, scene, resolver,
            new NifGlbExportRequest(Path.GetFileNameWithoutExtension(entry), NifExportFamilies.FromHeader(header),
                header.IsBigEndian, NifGlbWriterPreference.Normalized), validator, false, cancellationToken);
    }

    /// <summary>The SpeedTree route: every named tree at every seed, textured from the FNV data directory.</summary>
    private static void RunSpeedTree(NifGlbParityStratum stratum, string root, string textureRoot,
        NifGlbParityStratumReceipt receipt, string? validator, bool full, CancellationToken cancellationToken)
    {
        var candidates = new List<(string Name, uint Seed, string Relative)>();
        foreach (var name in RetailTreeNames)
        {
            if (!File.Exists(Path.Combine(root, name + ".spt")))
            {
                receipt.FixtureMismatches.Add($"{name}.spt: missing");
                continue;
            }

            candidates.AddRange(SpeedTreeSeeds.Select(seed =>
                (name, seed, string.Create(CultureInfo.InvariantCulture, $"{name}.spt#seed{seed}"))));
        }

        receipt.Candidates = candidates.Count;
        var selected = Select(candidates, static candidate => candidate.Relative, stratum.SampleSize, full);
        receipt.Selected = selected.Count;
        using var resolver = new NifTextureResolver(textureRoot);
        foreach (var (name, seed, relative) in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            receipt.Inspected++;
            TraceMemory(receipt);
            GlbScene? scene;
            try
            {
                var model = SptFile.Parse(File.ReadAllBytes(Path.Combine(root, name + ".spt")));
                scene = NifExportSceneBuilder.BuildRenderableModel(SptGeometryBuilder.Build(model, seed), name);
            }
            catch (Exception exception) when (Recordable(exception))
            {
                receipt.RecordParseError(relative, Describe(exception));
                continue;
            }

            if (scene is null || scene.MeshParts.Count == 0)
            {
                receipt.NoScene.Add(relative);
                continue;
            }

            Compare(receipt, relative, scene, resolver,
                new NifGlbExportRequest(name, NifExportFamily.SpeedTree, false, NifGlbWriterPreference.Normalized),
                validator, false, cancellationToken);
        }
    }

    /// <summary>The named, hash-pinned fixtures through the CLI assembly.</summary>
    private static void RunFixtures(string root, NifGlbParityStratumReceipt receipt, string? validator,
        CancellationToken cancellationToken)
    {
        receipt.Candidates = Fixtures.Length;
        receipt.Selected = Fixtures.Length;
        foreach (var (relative, hash, informationOnly) in Fixtures)
        {
            cancellationToken.ThrowIfCancellationRequested();
            receipt.Inspected++;
            TraceMemory(receipt);
            var path = Path.Combine(root, relative.ToLowerInvariant());
            if (!File.Exists(path))
            {
                receipt.FixtureMismatches.Add($"{relative}: missing");
                continue;
            }

            var actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
            if (!string.Equals(actual, hash, StringComparison.Ordinal))
            {
                receipt.FixtureMismatches.Add($"{relative}: SHA-256 {actual}, pinned {hash}");
                continue;
            }

            RunCliFile(receipt, path, relative, validator, informationOnly, cancellationToken);
        }
    }

    /// <summary>Plans one assembled scene with the normalized preference and compares both encodings.</summary>
    private static void Compare(NifGlbParityStratumReceipt receipt, string relative, GlbScene scene,
        NifTextureResolver resolver, NifGlbExportRequest request, string? validator, bool informationOnly,
        CancellationToken cancellationToken)
    {
        var family = request.Family.ToString();
        receipt.Families[family] = receipt.Families.GetValueOrDefault(family) + 1;
        if (request.ConvertedFromBigEndian)
        {
            receipt.ConvertedFromBigEndian++;
        }

        if (NifGlbNormalizedAdmission.IsAdmitted(request.Family, request.ConvertedFromBigEndian))
        {
            receipt.ProductionAdmitted++;
        }

        NifCorpusSourceSnapshot? snapshot = null;
        string? snapshotFailure = null;
        try
        {
            snapshot = new NifCorpusSourceSnapshot(scene);
        }
        catch (Exception exception) when (Recordable(exception))
        {
            // Still measure the pair: the receipt keeps the comparison evidence, and the missing proof that
            // planning left the source unchanged is recorded as a failure below.
            snapshotFailure = "The source snapshot could not be taken: " + Describe(exception);
        }

        NifGlbExportPlan plan;
        try
        {
            plan = PlanNormalized(scene, resolver, request, cancellationToken);
        }
        catch (Exception exception) when (Recordable(exception))
        {
            Record(receipt, relative, informationOnly, ["Planning threw " + Describe(exception)]);
            return;
        }

        if (plan.Decision.Refused)
        {
            var reason = $"{plan.Decision.Stage}: {plan.Decision.Reason}";
            if (informationOnly)
            {
                receipt.InformationOnly[relative] = "refused at " + reason;
            }
            else if (plan.Decision.Stage == NifGlbDeclineStage.SharedValidation)
            {
                receipt.SharedValidation++;
                receipt.SharedValidationFiles[relative] = plan.Decision.Reason ?? string.Empty;
            }
            else
            {
                receipt.RecordDecline(relative, reason);
            }

            return;
        }

        if (!informationOnly)
        {
            receipt.Compared++;
            receipt.ComparedPaths.Add(relative);
        }

        try
        {
            var pair = EncodePlanned(scene, resolver, plan, snapshot, cancellationToken);
            var report = NifGlbParityOracle.Compare(ModelRoot.ParseGLB(pair.NativeBytes),
                ModelRoot.ParseGLB(pair.SharedBytes), pair.Expectations, cancellationToken);
            var messages = report.Failures.ToList();
            if (report.FailureCount > messages.Count)
            {
                messages.Add($"... and {report.FailureCount - messages.Count} more oracle failure(s).");
            }

            if (snapshotFailure is not null)
            {
                messages.Insert(0, snapshotFailure);
            }

            if (validator is not null)
            {
                var validation = NifGlbKhronosValidator.Validate(validator, pair.SharedBytes, cancellationToken);
                receipt.ValidatedOutputs++;
                receipt.ValidatorErrors += validation.NumErrors;
                receipt.ValidatorWarnings += validation.NumWarnings;
                if (validation.NumErrors + validation.NumWarnings > 0)
                {
                    messages.Add($"Khronos validator: {validation.NumErrors} error(s), {validation.NumWarnings} " +
                                 "warning(s): " + string.Join("; ", validation.Messages.Take(3)));
                }
            }

            if (informationOnly)
            {
                receipt.InformationOnly[relative] = messages.Count == 0
                    ? "compared: equivalent"
                    : "compared: " + messages[0];
                return;
            }

            receipt.RepeatedPositionDrops += pair.RepeatedPositions;
            receipt.NativeBytes += pair.NativeBytes.Length;
            receipt.SharedBytes += pair.SharedBytes.Length;
            receipt.Absorb(report);
            if (messages.Count == 0)
            {
                receipt.Passed++;
            }
            else
            {
                receipt.RecordFailure(relative, messages);
            }
        }
        catch (XunitException exception)
        {
            Record(receipt, relative, informationOnly,
                ["Source parity (Layer A) or source snapshot: " + exception.Message]);
        }
        catch (Exception exception) when (Recordable(exception))
        {
            Record(receipt, relative, informationOnly, [Describe(exception)]);
        }
    }

    /// <summary>Records a failure, or an information-only note for the forced fixture.</summary>
    private static void Record(NifGlbParityStratumReceipt receipt, string relative, bool informationOnly,
        IReadOnlyList<string> messages)
    {
        if (informationOnly)
        {
            receipt.InformationOnly[relative] = "failed: " + messages[0];
            return;
        }

        receipt.RecordFailure(relative, messages);
    }

    /// <summary>Flattens the browser's grouped tree into its file entries.</summary>
    private static IEnumerable<string> Flatten(IEnumerable<NifTreeEntry> entries)
    {
        foreach (var entry in entries)
        {
            if (!entry.IsDirectory)
            {
                yield return entry.FullPath;
            }

            foreach (var child in Flatten(entry.Children))
            {
                yield return child;
            }
        }
    }

    /// <summary>Requires two path sets to be equal, reporting both differences.</summary>
    private static void AddSetViolation(List<string> violations, string kind, List<string>? frozen,
        List<string> measured)
    {
        if (frozen is null)
        {
            return;
        }

        var missing = frozen.Except(measured, StringComparer.Ordinal).ToArray();
        var added = measured.Except(frozen, StringComparer.Ordinal).ToArray();
        if (missing.Length > 0 || added.Length > 0)
        {
            violations.Add($"The {kind} list differs from the frozen list: new [{string.Join(", ", added)}], " +
                           $"gone [{string.Join(", ", missing)}].");
        }
    }

    /// <summary>Whether an exception is a per-file outcome to record rather than a run-ending condition.</summary>
    private static bool Recordable(Exception exception) =>
        exception is not OperationCanceledException and not OutOfMemoryException;

    /// <summary>A one-line description of an exception.</summary>
    private static string Describe(Exception exception) => $"{exception.GetType().Name}: {exception.Message}";
}
