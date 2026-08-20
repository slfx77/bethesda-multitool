using System.Globalization;
using System.Text;
using BethesdaMultitool.Core.Formats.Bsa.Extraction;
using BethesdaMultitool.Core.Formats.Bsa.Parsing;
using BethesdaMultitool.Core.Formats.Nif.Conversion;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Particles;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Particles;

/// <summary>
///     FO3 EmitterActive particle census (a census VEHICLE, not a gate). Enumerates every NIF in the
///     FO3 PC Final meshes BSA, prefilters on the raw <c>NiPSysEmitterCtlr</c> type string, parses each
///     particle system, and writes one CSV row per birth-rate controller that carries an EmitterActive
///     bool binding (constant or NiBoolData keys) to
///     <c>TestOutput/fo3-parity-2026-08/census/fo3-emitteractive-census.csv</c>. Rows with verdict
///     <c>gated-to-zero</c> are the emitters the EmitterActive gate newly silences in FO3 — they need
///     human eyeballing. The test passes whenever the census completes (asserts only that parsing
///     yielded at least one particle system). Sample-gated: Bucket B + FO3 meshes BSA present.
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
public sealed class Fo3EmitterActiveCensusTests
{
    private static readonly string Fo3MeshesBsa = SampleBsaLocator.ResolveFo3MeshesBsa();

    private static readonly string FnvMeshesBsa = SampleBsaLocator.ResolveFnvMeshesBsa();

    private static readonly byte[] EmitterCtlrNeedle = Encoding.ASCII.GetBytes("NiPSysEmitterCtlr");

    private readonly ITestOutputHelper _output;

    public Fo3EmitterActiveCensusTests(ITestOutputHelper output)
    {
        _output = output;
        BucketBTestGuard.SkipUnlessEnabled();
    }

    [Fact]
    public void Census_Fo3MeshesBsa_EmitterActiveBindings()
    {
        Assert.SkipUnless(File.Exists(Fo3MeshesBsa), "FO3 PC Final meshes BSA not present (dev-machine-only asset).");
        RunCensus(Fo3MeshesBsa, "fo3-emitteractive-retriage.csv", "FO3");
    }

    /// <summary>
    ///     The FNV twin. FortHowitzer (the asset that started this) lives here, so the same
    ///     instrument that re-triaged FO3 also has to agree that its idle smoke is gated off —
    ///     otherwise the census and the shipped renderer are measuring different things.
    /// </summary>
    [Fact]
    public void Census_FnvMeshesBsa_EmitterActiveBindings()
    {
        Assert.SkipUnless(File.Exists(FnvMeshesBsa), "FNV PC Final meshes BSA not present (dev-machine-only asset).");
        RunCensus(FnvMeshesBsa, "fnv-emitteractive-retriage.csv", "FNV");
    }

    private void RunCensus(string meshesBsa, string csvName, string label)
    {
        var repoRoot = FindRepoRoot();
        Assert.SkipWhen(repoRoot is null, "Repo root (Sample + src) not found from test base directory.");

        var csvDir = Path.Combine(repoRoot!, "TestOutput", "fo3-parity-2026-08", "census");
        Directory.CreateDirectory(csvDir);
        // New file: the 2026-08-10 census stays frozen beside it as the "before" for diffing.
        var csvPath = Path.Combine(csvDir, csvName);

        using var extractor = new BsaExtractor(meshesBsa);
        var archive = BsaParser.Parse(meshesBsa);

        var totalNifs = 0;
        var prefiltered = 0;
        var parsed = 0;
        var extractFailures = 0;
        var parseFailures = 0;
        var totalSystems = 0;
        var noRateController = 0;
        var noBoolBinding = 0;
        var rendersAtShipped = 0;
        var pulsesInvisible = 0;
        var silentEverywhere = 0;
        var legacyGatedToZero = 0;
        // shipped x warm-up confusion matrix: the blast radius of moving the default snapshot.
        var shippedYesWarmYes = 0;
        var shippedYesWarmNo = 0;
        var shippedNoWarmYes = 0;
        var shippedNoWarmNo = 0;

        var rows = new List<string>
        {
            "nifPath,systemIndex,capacity,lifeSpan,boolBinding,rateBinding,dormantTriggeredFx,"
            + "authoredRate,restRate0,restRate2_5,legacyVerdict,timingMode,bakeWindowSeconds,"
            + "bakedAtShipped,maxRateOverWindow,firstActiveTime,dutyFraction,bestSnapshot,"
            + "bakedAtBest,bakedAtWarmup,verdict,silenceCause"
        };
        var gatedRows = new List<string>();

        foreach (var file in archive.AllFiles)
        {
            if (!file.FullPath.EndsWith(".nif", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            totalNifs++;

            byte[] data;
            try
            {
                data = extractor.ExtractFile(file);
            }
            catch (Exception)
            {
                extractFailures++;
                continue;
            }

            // Cheap prefilter: only NIFs whose raw bytes name the NiPSysEmitterCtlr type can bind
            // an EmitterActive bool.
            if (data.AsSpan().IndexOf(EmitterCtlrNeedle) < 0)
            {
                continue;
            }

            prefiltered++;

            NifInfo? nif;
            try
            {
                nif = NifParser.Parse(data);
                if (nif is not null && nif.IsBigEndian)
                {
                    var converted = NifConverter.Convert(data);
                    if (converted.Success && converted.OutputData is not null)
                    {
                        data = converted.OutputData;
                        nif = NifParser.Parse(data);
                    }
                }
            }
            catch (Exception)
            {
                parseFailures++;
                continue;
            }

            if (nif is null)
            {
                parseFailures++;
                continue;
            }

            parsed++;

            for (var blockIndex = 0; blockIndex < nif.Blocks.Count; blockIndex++)
            {
                if (!NifParticleSystemParser.IsParticleSystem(nif.Blocks[blockIndex].TypeName))
                {
                    continue;
                }

                var system = NifParticleSystemParser.Parse(data, nif, blockIndex);
                if (system is null)
                {
                    continue;
                }

                totalSystems++;

                if (system.Emitter?.BirthRateController is not ParticleRateControllerDefinition rate)
                {
                    noRateController++;
                    continue;
                }

                var hasBoolBinding = rate.EmitterActiveConstant is not null || rate.EmitterActiveKeys.Count > 0;
                if (!hasBoolBinding)
                {
                    noBoolBinding++;
                    continue;
                }

                var constantBinding = rate.EmitterActiveConstant is { } c && c ? "true" : "false";
                var boolBinding = rate.EmitterActiveKeys.Count > 0
                    ? $"keys:{rate.EmitterActiveKeys.Count}"
                    : $"const:{constantBinding}";

                var authoredRate = rate.ConstantValue
                                   ?? (rate.Keys.Count > 0 ? rate.Keys.Max(k => k.Value) : 0f);
                var restRate0 = rate.Sample(0f);
                var restRate25 = rate.Sample(2.5f);

                // THE verdict: what the shipped static viewer actually bakes. Everything else on
                // this row is diagnostic. NifParticleSystemExtractor calls Bake(def) with default
                // options, so this is byte-for-byte the renderer's own answer — unlike the two
                // positive Sample() instants above, which the baker never evaluates (its window
                // runs BACKWARDS from the snapshot).
                var profile = ParticleActivityWindow.Profile(system);
                var bakedAtShipped = NifParticleBaker.Bake(system).Count;
                var bakedAtBest = NifParticleBaker.Bake(
                    system, new ParticleBakeOptions { SnapshotTimeSeconds = profile.BestSnapshot }).Count;
                var bakedAtWarmup = NifParticleBaker.Bake(
                    system, new ParticleBakeOptions { SnapshotTimeSeconds = profile.BakeWindowSeconds }).Count;

                string verdict;
                if (bakedAtShipped > 0)
                {
                    verdict = "renders-at-shipped";
                    rendersAtShipped++;
                }
                else if (bakedAtBest > 0)
                {
                    verdict = "pulses-invisible";
                    pulsesInvisible++;
                }
                else
                {
                    verdict = "silent-everywhere";
                    silentEverywhere++;
                }

                // Why it is silent, when it is — so bursts (correctly silent) separate from
                // ambient loops (a snapshot artifact) without re-reading every NIF by hand.
                string silenceCause;
                if (verdict == "renders-at-shipped")
                {
                    silenceCause = "";
                }
                else if (rate.DormantTriggeredFx)
                {
                    silenceCause = "dormant-triggered";
                }
                else if (rate.EmitterActiveConstant is false)
                {
                    silenceCause = "const-false";
                }
                else if (authoredRate <= 0f)
                {
                    silenceCause = "zero-rate";
                }
                else if (profile.MaxRate <= 0f)
                {
                    silenceCause = "all-keys-false";
                }
                else
                {
                    silenceCause = profile.Plan.Mode == ParticleSweepMode.Identity
                        ? "identity-pinned-key0"
                        : "phase-miss";
                }

                // Legacy verdict retained so the new CSV diffs cleanly against the frozen original.
                string legacyVerdict;
#pragma warning disable S1244
                if (authoredRate > 0f && restRate0 == 0f && restRate25 == 0f)
                {
                    legacyVerdict = "gated-to-zero";
                }
                else
                {
                    legacyVerdict = restRate0 > 0f || restRate25 > 0f
                        ? "active-rate"
                        : "zero-authored";
                }
#pragma warning restore S1244
                if (legacyVerdict == "gated-to-zero") legacyGatedToZero++;

                var row = string.Join(",",
                    file.FullPath,
                    blockIndex.ToString(CultureInfo.InvariantCulture),
                    system.Capacity.ToString(CultureInfo.InvariantCulture),
                    (system.Emitter?.LifeSpan ?? 0f).ToString("0.####", CultureInfo.InvariantCulture),
                    boolBinding,
                    rate.Keys.Count > 0 ? $"keys:{rate.Keys.Count}" : "pose",
                    rate.DormantTriggeredFx ? "true" : "false",
                    authoredRate.ToString("0.####", CultureInfo.InvariantCulture),
                    restRate0.ToString("0.####", CultureInfo.InvariantCulture),
                    restRate25.ToString("0.####", CultureInfo.InvariantCulture),
                    legacyVerdict,
                    profile.Plan.Mode.ToString(),
                    profile.BakeWindowSeconds.ToString("0.###", CultureInfo.InvariantCulture),
                    bakedAtShipped.ToString(CultureInfo.InvariantCulture),
                    profile.MaxRate.ToString("0.####", CultureInfo.InvariantCulture),
                    profile.FirstActiveTime?.ToString("0.###", CultureInfo.InvariantCulture) ?? "",
                    profile.DutyFraction.ToString("0.###", CultureInfo.InvariantCulture),
                    profile.BestSnapshot.ToString("0.###", CultureInfo.InvariantCulture),
                    bakedAtBest.ToString(CultureInfo.InvariantCulture),
                    bakedAtWarmup.ToString(CultureInfo.InvariantCulture),
                    verdict,
                    silenceCause);
                rows.Add(row);
                if (verdict != "renders-at-shipped")
                {
                    gatedRows.Add(row);
                }

                if (bakedAtShipped > 0 && bakedAtWarmup > 0) shippedYesWarmYes++;
                else if (bakedAtShipped > 0) shippedYesWarmNo++;
                else if (bakedAtWarmup > 0) shippedNoWarmYes++;
                else shippedNoWarmNo++;
            }
        }

        File.WriteAllLines(csvPath, rows);

        _output.WriteLine($"{label} EmitterActive census -> {csvPath}");
        _output.WriteLine($"NIFs scanned:        {totalNifs}");
        _output.WriteLine($"Prefiltered (ctlr):  {prefiltered}");
        _output.WriteLine(
            $"Parsed:              {parsed} (extract failures {extractFailures}, parse failures {parseFailures})");
        _output.WriteLine($"Particle systems:    {totalSystems}");
        _output.WriteLine($"  no rate ctrl:      {noRateController}");
        _output.WriteLine($"  no bool binding:   {noBoolBinding}");
        _output.WriteLine($"  bool-bound rows:   {rows.Count - 1}");
        _output.WriteLine(
            $"    renders-at-shipped: {rendersAtShipped}  (the 2026-08-10 census called many of these gated)");
        _output.WriteLine($"    pulses-invisible:   {pulsesInvisible}  (authored to emit, but not at snapshot 0)");
        _output.WriteLine($"    silent-everywhere:  {silentEverywhere}");
        _output.WriteLine($"  legacy gated-to-zero: {legacyGatedToZero} (old two-instant verdict, for diffing)");
        _output.WriteLine("  shipped x warm-up snapshot matrix:");
        _output.WriteLine($"    both render:        {shippedYesWarmYes}");
        _output.WriteLine($"    shipped only:       {shippedYesWarmNo}");
        _output.WriteLine($"    warm-up only:       {shippedNoWarmYes}  (would APPEAR if the default snapshot moved)");
        _output.WriteLine($"    neither:            {shippedNoWarmNo}");
        foreach (var row in gatedRows)
        {
            _output.WriteLine($"GATED {row}");
        }

        // Census vehicle, not a gate: pass whenever the sweep actually saw particle systems.
        Assert.True(totalSystems > 0, $"{label} census parsed no particle systems — enumeration or parsing is broken.");
    }

    private static string? FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 12 && dir is not null; i++)
        {
            if (Directory.Exists(Path.Combine(dir, "Sample")) && Directory.Exists(Path.Combine(dir, "src")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        return null;
    }
}