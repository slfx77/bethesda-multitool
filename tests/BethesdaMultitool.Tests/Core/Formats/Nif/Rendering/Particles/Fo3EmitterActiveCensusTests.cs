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

        var repoRoot = FindRepoRoot();
        Assert.SkipWhen(repoRoot is null, "Repo root (Sample + src) not found from test base directory.");

        var csvDir = Path.Combine(repoRoot!, "TestOutput", "fo3-parity-2026-08", "census");
        Directory.CreateDirectory(csvDir);
        var csvPath = Path.Combine(csvDir, "fo3-emitteractive-census.csv");

        using var extractor = new BsaExtractor(Fo3MeshesBsa);
        var archive = BsaParser.Parse(Fo3MeshesBsa);

        var totalNifs = 0;
        var prefiltered = 0;
        var parsed = 0;
        var extractFailures = 0;
        var parseFailures = 0;
        var totalSystems = 0;
        var noRateController = 0;
        var noBoolBinding = 0;
        var gatedToZero = 0;
        var activeRate = 0;
        var zeroAuthored = 0;

        var rows = new List<string>
        {
            "nifPath,systemIndex,capacity,boolBinding,dormantTriggeredFx,authoredRate,restRate0,restRate2_5,verdict",
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

                string verdict;
                // Exact-zero is the point: "gated to zero" means the rest-state sample is the
                // literal 0f the emitter-active gate writes, not merely a small rate.
#pragma warning disable S1244
                if (authoredRate > 0f && restRate0 == 0f && restRate25 == 0f)
#pragma warning restore S1244
                {
                    verdict = "gated-to-zero";
                    gatedToZero++;
                }
                else if (restRate0 > 0f || restRate25 > 0f)
                {
                    verdict = "active-rate";
                    activeRate++;
                }
                else
                {
                    verdict = "zero-authored";
                    zeroAuthored++;
                }

                var row = string.Join(",",
                    file.FullPath,
                    blockIndex.ToString(CultureInfo.InvariantCulture),
                    system.Capacity.ToString(CultureInfo.InvariantCulture),
                    boolBinding,
                    rate.DormantTriggeredFx ? "true" : "false",
                    authoredRate.ToString("0.####", CultureInfo.InvariantCulture),
                    restRate0.ToString("0.####", CultureInfo.InvariantCulture),
                    restRate25.ToString("0.####", CultureInfo.InvariantCulture),
                    verdict);
                rows.Add(row);
                if (verdict == "gated-to-zero")
                {
                    gatedRows.Add(row);
                }
            }
        }

        File.WriteAllLines(csvPath, rows);

        _output.WriteLine($"FO3 EmitterActive census -> {csvPath}");
        _output.WriteLine($"NIFs scanned:        {totalNifs}");
        _output.WriteLine($"Prefiltered (ctlr):  {prefiltered}");
        _output.WriteLine($"Parsed:              {parsed} (extract failures {extractFailures}, parse failures {parseFailures})");
        _output.WriteLine($"Particle systems:    {totalSystems}");
        _output.WriteLine($"  no rate ctrl:      {noRateController}");
        _output.WriteLine($"  no bool binding:   {noBoolBinding}");
        _output.WriteLine($"  bool-bound rows:   {rows.Count - 1}");
        _output.WriteLine($"    gated-to-zero:   {gatedToZero}");
        _output.WriteLine($"    active-rate:     {activeRate}");
        _output.WriteLine($"    zero-authored:   {zeroAuthored}");
        foreach (var row in gatedRows)
        {
            _output.WriteLine($"GATED {row}");
        }

        // Census vehicle, not a gate: pass whenever the sweep actually saw particle systems.
        Assert.True(totalSystems > 0, "FO3 census parsed no particle systems — enumeration or parsing is broken.");
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
