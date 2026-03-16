using System.Globalization;
using System.Text;
using FalloutXbox360Utils.CLI;
using FalloutXbox360Utils.Core.Formats.Dds;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Analysis;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Appearance;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Assets;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Verification;
using FalloutXbox360Utils.Tests.Core;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Nif.Rendering;

[Collection(LoggerSerialTestGroup.Name)]
public sealed class XboxNpcEgtBlendInvestigationTests(SampleFileFixture samples)
{
    private static readonly uint[] ValidationNpcFormIds =
    [
        0x00092BD2, // Craig Boone
        0x00104E84, // Sunny Smiles
        0x00112640, // Dennis Crocker
        0x000F56FD  // Violet
    ];

    [Fact]
    public void ExportFixedValidationSet_WritesCurrentAndRecoveredEgtArtifacts()
    {
        Assert.SkipWhen(samples.Xbox360FinalEsm is null, "Xbox 360 final ESM not available");

        var meshesBsa = SampleFileFixture.FindSamplePath(
            @"Sample\Full_Builds\Fallout New Vegas (360 Final)\Data\Fallout - Meshes.bsa");
        var texturesBsa = SampleFileFixture.FindSamplePath(
            @"Sample\Full_Builds\Fallout New Vegas (360 Final)\Data\Fallout - Textures.bsa");
        var textures2Bsa = SampleFileFixture.FindSamplePath(
            @"Sample\Full_Builds\Fallout New Vegas (360 Final)\Data\Fallout - Textures2.bsa");

        Assert.SkipWhen(meshesBsa is null, "Xbox 360 final meshes BSA not available");
        Assert.SkipWhen(texturesBsa is null, "Xbox 360 final textures BSA not available");

        var esm = EsmFileLoader.Load(samples.Xbox360FinalEsm!, false);
        Assert.NotNull(esm);

        var pluginName = Path.GetFileName(samples.Xbox360FinalEsm!);
        var texturePaths = textures2Bsa == null
            ? [texturesBsa!]
            : new[] { texturesBsa!, textures2Bsa! };
        var appearanceResolver = NpcAppearanceResolver.Build(esm.Data, esm.IsBigEndian);
        var shippedTextures = NpcFaceGenTextureVerifier.DiscoverShippedFaceTextures(
            texturePaths,
            pluginName);

        using var meshArchives = NpcMeshArchiveSet.Open(meshesBsa!, null);
        using var textureResolver = new NifTextureResolver(texturePaths);

        var details = new List<NpcEgtBlendInvestigationDetail>(ValidationNpcFormIds.Length);
        var egtCache = new Dictionary<string, EgtParser?>(StringComparer.OrdinalIgnoreCase);

        foreach (var formId in ValidationNpcFormIds)
        {
            var appearance = appearanceResolver.ResolveHeadOnly(formId, pluginName);
            Assert.NotNull(appearance);
            Assert.True(shippedTextures.TryGetValue(formId, out var shippedTexture));
            Assert.NotNull(shippedTexture);

            var detail = NpcEgtBlendInvestigator.InvestigateDetailed(
                appearance!,
                shippedTexture!,
                meshArchives,
                textureResolver,
                egtCache);
            Assert.True(detail.Result.Verified, detail.Result.FailureReason);
            Assert.NotNull(detail.CurrentPolicy);
            Assert.NotNull(detail.RecoveredPolicy);
            details.Add(detail);
        }

        var artifactRoot = Path.Combine(GetRepoRoot(), "artifacts", "egt-blend-investigation");
        Directory.CreateDirectory(artifactRoot);
        foreach (var detail in details)
        {
            NpcEgtBlendInvestigator.WriteArtifacts(artifactRoot, detail);
        }

        var summaryPath = Path.Combine(artifactRoot, "summary.csv");
        NpcEgtBlendInvestigator.WriteSummaryCsv(details, summaryPath);

        Assert.True(File.Exists(summaryPath));
        Assert.Contains(details, detail => detail.Result.FormId == 0x00112640 &&
                                          detail.Result.CurrentVsRecoveredAppliedDiffuseMeanAbsoluteRgbError > 0.5);
    }

    /// <summary>
    ///     Phase 1 DDX isolation: compare Xbox DDX-decoded facemods against PC DDS facemods
    ///     and our generated EGT delta textures. Measures how much of the mismatch is DDX
    ///     lossy compression vs actual blend/coefficient error.
    /// </summary>
    [Fact]
    public void DdxIsolation_CompareXboxDdxVsPcDdsVsGenerated()
    {
        Assert.SkipWhen(samples.Xbox360FinalEsm is null, "Xbox 360 final ESM not available");

        // Xbox 360 BSAs
        var meshesBsa = SampleFileFixture.FindSamplePath(
            @"Sample\Full_Builds\Fallout New Vegas (360 Final)\Data\Fallout - Meshes.bsa");
        var xboxTexturesBsa = SampleFileFixture.FindSamplePath(
            @"Sample\Full_Builds\Fallout New Vegas (360 Final)\Data\Fallout - Textures.bsa");
        var xboxTextures2Bsa = SampleFileFixture.FindSamplePath(
            @"Sample\Full_Builds\Fallout New Vegas (360 Final)\Data\Fallout - Textures2.bsa");

        // PC extracted textures directory (contains DDS facemods)
        var pcTexturesDir = SampleFileFixture.FindSamplePath(
            @"Sample\Textures\textures_pc\textures\characters\facemods\falloutnv.esm\00092bd2_0.dds");
        // Walk up to the textures_pc root
        var pcTexturesRoot = pcTexturesDir != null
            ? Path.GetFullPath(Path.Combine(Path.GetDirectoryName(pcTexturesDir)!, "..", "..", "..", ".."))
            : null;

        Assert.SkipWhen(meshesBsa is null, "Xbox 360 final meshes BSA not available");
        Assert.SkipWhen(xboxTexturesBsa is null, "Xbox 360 final textures BSA not available");
        Assert.SkipWhen(pcTexturesRoot is null || !Directory.Exists(pcTexturesRoot),
            "PC final extracted textures not available");

        var esm = EsmFileLoader.Load(samples.Xbox360FinalEsm!, false);
        Assert.NotNull(esm);

        var pluginName = Path.GetFileName(samples.Xbox360FinalEsm!);
        var xboxTexturePaths = xboxTextures2Bsa == null
            ? [xboxTexturesBsa!]
            : new[] { xboxTexturesBsa!, xboxTextures2Bsa! };

        var appearanceResolver = NpcAppearanceResolver.Build(esm.Data, esm.IsBigEndian);

        // Discover shipped textures from Xbox BSAs
        var xboxShippedTextures = NpcFaceGenTextureVerifier.DiscoverShippedFaceTextures(
            xboxTexturePaths, pluginName);

        using var meshArchives = NpcMeshArchiveSet.Open(meshesBsa!, null);
        using var xboxTextureResolver = new NifTextureResolver(xboxTexturePaths);
        using var pcTextureResolver = new NifTextureResolver(pcTexturesRoot!);

        var egtCache = new Dictionary<string, EgtParser?>(StringComparer.OrdinalIgnoreCase);
        var artifactRoot = Path.Combine(GetRepoRoot(), "artifacts", "egt-ddx-isolation");
        Directory.CreateDirectory(artifactRoot);

        var sb = new StringBuilder();
        sb.AppendLine("form_id,editor_id,full_name," +
                      "xbox_vs_pc_mae,xbox_vs_pc_rmse,xbox_vs_pc_max_abs,xbox_vs_pc_pixels_gt1,xbox_vs_pc_pixels_gt4," +
                      "xbox_vs_gen_mae,xbox_vs_gen_rmse,xbox_vs_gen_max_abs,xbox_vs_gen_pixels_gt1,xbox_vs_gen_pixels_gt4," +
                      "pc_vs_gen_mae,pc_vs_gen_rmse,pc_vs_gen_max_abs,pc_vs_gen_pixels_gt1,pc_vs_gen_pixels_gt4," +
                      "ddx_fraction_of_total,comparison_mode,width,height");

        foreach (var formId in ValidationNpcFormIds)
        {
            var appearance = appearanceResolver.ResolveHeadOnly(formId, pluginName);
            Assert.NotNull(appearance);

            if (!xboxShippedTextures.TryGetValue(formId, out var xboxShipped))
            {
                continue;
            }

            var comparison = BuildDdxIsolationComparison(
                appearance!, xboxShipped, xboxTextureResolver, pcTextureResolver,
                meshArchives, egtCache);

            AppendDdxIsolationCsvRow(sb, comparison);
            WriteDdxIsolationArtifacts(artifactRoot, comparison);
        }

        var csvPath = Path.Combine(artifactRoot, "ddx_isolation_summary.csv");
        File.WriteAllText(csvPath, sb.ToString(), Encoding.UTF8);
        Assert.True(File.Exists(csvPath));

        // Also dump raw coefficient arrays for each NPC (Phase 4 audit)
        foreach (var formId in ValidationNpcFormIds)
        {
            var appearance = appearanceResolver.ResolveHeadOnly(formId, pluginName);
            if (appearance is null)
            {
                continue;
            }

            var coeffSb = new StringBuilder();
            coeffSb.AppendLine($"# Coefficient dump for 0x{formId:X8} ({appearance.EditorId})");
            coeffSb.AppendLine();

            DumpCoeffArray(coeffSb, "FaceGenTextureCoeffs (pre-merged)", appearance.FaceGenTextureCoeffs);
            DumpCoeffArray(coeffSb, "NpcFaceGenTextureCoeffs (NPC-only)", appearance.NpcFaceGenTextureCoeffs);
            DumpCoeffArray(coeffSb, "RaceFaceGenTextureCoeffs (race-only)", appearance.RaceFaceGenTextureCoeffs);

            var merged = NpcFaceGenCoefficientMerger.Merge(
                appearance.NpcFaceGenTextureCoeffs,
                appearance.RaceFaceGenTextureCoeffs);
            DumpCoeffArray(coeffSb, "Recovered merged (npc + race)", merged);

            var coeffPath = Path.Combine(artifactRoot,
                $"{formId:X8}_{appearance.EditorId ?? "unknown"}", "coefficients.txt");
            File.WriteAllText(coeffPath, coeffSb.ToString(), Encoding.UTF8);
        }
    }

    private static DdxIsolationComparison BuildDdxIsolationComparison(
        NpcAppearance appearance,
        NpcFaceGenTextureVerifier.ShippedNpcFaceTexture xboxShipped,
        NifTextureResolver xboxTextureResolver,
        NifTextureResolver pcTextureResolver,
        NpcMeshArchiveSet meshArchives,
        Dictionary<string, EgtParser?> egtCache)
    {
        var xboxFacemod = xboxTextureResolver.GetTexture(xboxShipped.VirtualPath)!;
        var pcFacemod = pcTextureResolver.GetTexture(xboxShipped.VirtualPath)
                        ?? pcTextureResolver.GetTexture(
                            Path.ChangeExtension(xboxShipped.VirtualPath, ".dds"))!;

        var egtPath = Path.ChangeExtension(appearance.BaseHeadNifPath!, ".egt");
        if (!egtCache.TryGetValue(egtPath, out var egt))
        {
            egt = NpcRenderHelpers.LoadEgtFromBsa(egtPath, meshArchives);
            egtCache[egtPath] = egt;
        }

        var recoveredCoeffs = NpcFaceGenCoefficientMerger.Merge(
                                  appearance.NpcFaceGenTextureCoeffs,
                                  appearance.RaceFaceGenTextureCoeffs) ?? [];

        var comparisonMode = xboxFacemod.Width == egt!.Cols && xboxFacemod.Height == egt.Rows
            ? "native_egt"
            : "upscaled_egt";

        var generatedEgt = comparisonMode == "native_egt"
            ? FaceGenTextureMorpher.BuildNativeDeltaTexture(
                egt, recoveredCoeffs,
                FaceGenTextureMorpher.TextureAccumulationMode.EngineQuantized256,
                FaceGenTextureMorpher.DeltaTextureEncodingMode.EngineCompressed255Half)!
            : FaceGenTextureMorpher.BuildUpscaledDeltaTexture(
                egt, recoveredCoeffs,
                xboxFacemod.Width, xboxFacemod.Height,
                FaceGenTextureMorpher.TextureAccumulationMode.EngineQuantized256,
                FaceGenTextureMorpher.DeltaTextureEncodingMode.EngineCompressed255Half)!;

        var w = xboxFacemod.Width;
        var h = xboxFacemod.Height;

        return new DdxIsolationComparison(
            appearance,
            xboxFacemod,
            pcFacemod,
            generatedEgt,
            NpcTextureComparison.CompareRgb(xboxFacemod.Pixels, pcFacemod.Pixels, w, h),
            NpcTextureComparison.CompareRgb(xboxFacemod.Pixels, generatedEgt.Pixels, w, h),
            NpcTextureComparison.CompareRgb(pcFacemod.Pixels, generatedEgt.Pixels, w, h),
            comparisonMode);
    }

    private static void AppendDdxIsolationCsvRow(StringBuilder sb, DdxIsolationComparison c)
    {
        var ddxFraction = c.XboxVsGen.MeanAbsoluteRgbError > 0
            ? c.XboxVsPc.MeanAbsoluteRgbError / c.XboxVsGen.MeanAbsoluteRgbError
            : 0.0;

        sb.Append(Csv(c.Appearance.NpcFormId.ToString("X8"))).Append(',');
        sb.Append(Csv(c.Appearance.EditorId)).Append(',');
        sb.Append(Csv(c.Appearance.FullName)).Append(',');
        AppendMetricsCsv(sb, c.XboxVsPc);
        AppendMetricsCsv(sb, c.XboxVsGen);
        AppendMetricsCsv(sb, c.PcVsGen);
        sb.Append(ddxFraction.ToString("F4", CultureInfo.InvariantCulture)).Append(',');
        sb.Append(Csv(c.ComparisonMode)).Append(',');
        sb.Append(c.XboxFacemod.Width).Append(',');
        sb.Append(c.XboxFacemod.Height).AppendLine();
    }

    private static void AppendMetricsCsv(StringBuilder sb, NpcTextureComparison.RgbComparisonMetrics m)
    {
        sb.Append(m.MeanAbsoluteRgbError.ToString("F6", CultureInfo.InvariantCulture)).Append(',');
        sb.Append(m.RootMeanSquareRgbError.ToString("F6", CultureInfo.InvariantCulture)).Append(',');
        sb.Append(m.MaxAbsoluteRgbError).Append(',');
        sb.Append(m.PixelsWithRgbErrorAbove1).Append(',');
        sb.Append(m.PixelsWithRgbErrorAbove4).Append(',');
    }

    private static void WriteDdxIsolationArtifacts(string artifactRoot, DdxIsolationComparison c)
    {
        var w = c.XboxFacemod.Width;
        var h = c.XboxFacemod.Height;
        var npcDir = Path.Combine(artifactRoot,
            $"{c.Appearance.NpcFormId:X8}_{c.Appearance.EditorId ?? "unknown"}");
        Directory.CreateDirectory(npcDir);

        PngWriter.SaveRgba(c.XboxFacemod.Pixels, w, h, Path.Combine(npcDir, "xbox_ddx_facemod.png"));
        PngWriter.SaveRgba(c.PcFacemod.Pixels, w, h, Path.Combine(npcDir, "pc_dds_facemod.png"));
        PngWriter.SaveRgba(c.GeneratedEgt.Pixels, w, h, Path.Combine(npcDir, "generated_egt.png"));

        SaveDiff(c.XboxFacemod.Pixels, c.PcFacemod.Pixels, w, h,
            Path.Combine(npcDir, "diff_xbox_vs_pc.png"));
        SaveDiff(c.XboxFacemod.Pixels, c.GeneratedEgt.Pixels, w, h,
            Path.Combine(npcDir, "diff_xbox_vs_generated.png"));
        SaveDiff(c.PcFacemod.Pixels, c.GeneratedEgt.Pixels, w, h,
            Path.Combine(npcDir, "diff_pc_vs_generated.png"));
    }

    private static void SaveDiff(byte[] left, byte[] right, int w, int h, string path)
    {
        PngWriter.SaveRgba(NpcTextureComparison.BuildDiffPixels(left, right), w, h, path);
    }

    private sealed record DdxIsolationComparison(
        NpcAppearance Appearance,
        DecodedTexture XboxFacemod,
        DecodedTexture PcFacemod,
        DecodedTexture GeneratedEgt,
        NpcTextureComparison.RgbComparisonMetrics XboxVsPc,
        NpcTextureComparison.RgbComparisonMetrics XboxVsGen,
        NpcTextureComparison.RgbComparisonMetrics PcVsGen,
        string ComparisonMode);

    private static void DumpCoeffArray(StringBuilder sb, string label, float[]? coeffs)
    {
        sb.AppendLine($"## {label}");
        if (coeffs is null || coeffs.Length == 0)
        {
            sb.AppendLine("  (null or empty)");
            sb.AppendLine();
            return;
        }

        for (var i = 0; i < coeffs.Length; i++)
        {
            var quantized = (int)(coeffs[i] * 256f);
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"  [{i,2}] = {coeffs[i],12:F6}  (quantized256 = {quantized,6})");
        }

        sb.AppendLine();
    }

    private static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var escaped = value.Replace("\"", "\"\"");
        return $"\"{escaped}\"";
    }

    private static string GetRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10; i++)
        {
            if (File.Exists(Path.Combine(dir, "FalloutXbox360Utils.slnx")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir)!;
        }

        return Directory.GetCurrentDirectory();
    }
}
