using System.Globalization;
using System.Text;
using FalloutXbox360Utils.Core.Formats.Dds;
using FalloutXbox360Utils.Core.Formats.Esm.Analysis;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Export;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Appearance;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Assets;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Verification;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI.Rendering.Npc;

internal static class NpcEgtVerificationPipeline
{
    internal static void Run(NpcEgtVerificationSettings settings)
    {
        if (!ValidateInputPaths(settings, out var texturesBsaPaths))
        {
            return;
        }

        NpcFaceGenCoefficientMerger.RmsClampThreshold = settings.RmsClampThreshold;
        if (settings.RmsClampThreshold > 0f)
        {
            AnsiConsole.MarkupLine(
                "RMS clamp threshold: [yellow]{0:F2}[/]",
                settings.RmsClampThreshold);
        }

        AnsiConsole.MarkupLine(
            "Loading ESM: [cyan]{0}[/]",
            Path.GetFileName(settings.EsmPath));
        var esm = EsmFileLoader.Load(settings.EsmPath, false);
        if (esm == null)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Failed to load ESM file");
            return;
        }

        var pluginName = Path.GetFileName(settings.EsmPath);
        var resolver = NpcAppearanceResolver.Build(esm.Data, esm.IsBigEndian);
        var discoveredTargets = NpcFaceGenTextureVerifier.DiscoverShippedFaceTextures(
            texturesBsaPaths,
            pluginName);
        if (discoveredTargets.Count == 0)
        {
            AnsiConsole.MarkupLine(
                "[red]Error:[/] No shipped facemod textures found for plugin [cyan]{0}[/]",
                pluginName);
            return;
        }

        var targets = ApplyFilters(
            discoveredTargets,
            resolver,
            settings.NpcFilters,
            settings.Limit);
        if (targets.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] No NPC targets selected for verification");
            return;
        }

        AnsiConsole.MarkupLine(
            "Verifying [green]{0}[/] shipped facemod texture(s) for [cyan]{1}[/]",
            targets.Count,
            pluginName);

        using var meshArchives = NpcMeshArchiveSet.Open(settings.MeshesBsaPath, settings.ExtraMeshesBsaPaths);
        using var textureResolver = new NifTextureResolver(texturesBsaPaths);

        var egtCache = new Dictionary<string, EgtParser?>(StringComparer.OrdinalIgnoreCase);
        var results = new List<NpcFaceGenTextureVerifier.NpcFaceGenTextureVerificationResult>(targets.Count);
        var imageOutputDir = PrepareImageOutputDir(settings.ImageOutputDir);
        var exportedImageSets = 0;

        for (var index = 0; index < targets.Count; index++)
        {
            var target = targets[index];
            var appearance = resolver.ResolveHeadOnly(target.FormId, pluginName);
            if (appearance == null)
            {
                results.Add(new NpcFaceGenTextureVerifier.NpcFaceGenTextureVerificationResult
                {
                    FormId = target.FormId,
                    PluginName = target.PluginName,
                    ShippedTexturePath = target.VirtualPath,
                    FailureReason = "npc not found in esm"
                });
            }
            else
            {
                var verification = NpcFaceGenTextureVerifier.VerifyDetailed(
                    appearance,
                    target,
                    meshArchives,
                    textureResolver,
                    egtCache);
                results.Add(verification.Result);

                if (imageOutputDir != null &&
                    verification.Result.Verified &&
                    verification.GeneratedTexture != null &&
                    verification.ShippedTexture != null)
                {
                    ExportComparisonImages(
                        imageOutputDir,
                        appearance,
                        verification.Result,
                        verification.GeneratedTexture,
                        verification.ShippedTexture);
                    exportedImageSets++;
                }
            }

            if (targets.Count <= 20 || (index + 1) % 25 == 0 || index == targets.Count - 1)
            {
                AnsiConsole.WriteLine(
                    $"  [{index + 1}/{targets.Count}] 0x{target.FormId:X8}");
            }
        }

        PrintSummary(results, settings.TopCount);

        if (!string.IsNullOrWhiteSpace(settings.ReportPath))
        {
            WriteCsvReport(results, settings.ReportPath!);
            AnsiConsole.MarkupLine(
                "Wrote report: [cyan]{0}[/]",
                Path.GetFullPath(settings.ReportPath!));
        }

        if (imageOutputDir != null)
        {
            AnsiConsole.MarkupLine(
                "Wrote [green]{0}[/] comparison image set(s) to [cyan]{1}[/]",
                exportedImageSets,
                imageOutputDir);
        }
    }

    private static bool ValidateInputPaths(
        NpcEgtVerificationSettings settings,
        out string[] texturesBsaPaths)
    {
        texturesBsaPaths = Array.Empty<string>();

        if (!File.Exists(settings.MeshesBsaPath))
        {
            AnsiConsole.MarkupLine(
                "[red]Error:[/] Meshes BSA not found: {0}",
                settings.MeshesBsaPath);
            return false;
        }

        if (settings.ExtraMeshesBsaPaths is { Length: > 0 })
        {
            foreach (var extraMeshesBsaPath in settings.ExtraMeshesBsaPaths)
            {
                if (!File.Exists(extraMeshesBsaPath))
                {
                    AnsiConsole.MarkupLine(
                        "[red]Error:[/] Extra meshes BSA not found: {0}",
                        extraMeshesBsaPath);
                    return false;
                }
            }
        }

        if (!File.Exists(settings.EsmPath))
        {
            AnsiConsole.MarkupLine(
                "[red]Error:[/] ESM file not found: {0}",
                settings.EsmPath);
            return false;
        }

        texturesBsaPaths = NpcRenderHelpers.ResolveTexturesBsaPaths(
            settings.MeshesBsaPath,
            settings.ExplicitTexturesBsaPaths);
        if (texturesBsaPaths.Length == 0)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] No texture BSA files found");
            return false;
        }

        return true;
    }

    private static string? PrepareImageOutputDir(string? imageOutputDir)
    {
        if (string.IsNullOrWhiteSpace(imageOutputDir))
        {
            return null;
        }

        var fullPath = Path.GetFullPath(imageOutputDir);
        Directory.CreateDirectory(fullPath);
        return fullPath;
    }

    private static List<NpcFaceGenTextureVerifier.ShippedNpcFaceTexture> ApplyFilters(
        IReadOnlyDictionary<uint, NpcFaceGenTextureVerifier.ShippedNpcFaceTexture> discoveredTargets,
        NpcAppearanceResolver resolver,
        string[]? npcFilters,
        int? limit)
    {
        var selected = new List<NpcFaceGenTextureVerifier.ShippedNpcFaceTexture>();

        if (npcFilters is { Length: > 0 })
        {
            var allNpcs = resolver.GetAllNpcs();
            var allNpcPairs = allNpcs.ToList();

            foreach (var filter in npcFilters)
            {
                var parsedFormId = NpcRenderHelpers.ParseFormId(filter);
                if (parsedFormId.HasValue)
                {
                    if (discoveredTargets.TryGetValue(parsedFormId.Value, out var target))
                    {
                        selected.Add(target);
                    }

                    continue;
                }

                var match = allNpcPairs.FirstOrDefault(pair =>
                    string.Equals(pair.Value.EditorId, filter, StringComparison.OrdinalIgnoreCase));
                if (match.Value != null &&
                    discoveredTargets.TryGetValue(match.Key, out var editorTarget))
                {
                    selected.Add(editorTarget);
                }
            }
        }
        else
        {
            selected.AddRange(discoveredTargets.Values);
        }

        var deduped = selected
            .GroupBy(target => target.FormId)
            .Select(group => group.First())
            .OrderBy(target => target.FormId)
            .ToList();

        if (limit.HasValue)
        {
            deduped = deduped.Take(limit.Value).ToList();
        }

        return deduped;
    }

    private static void PrintSummary(
        List<NpcFaceGenTextureVerifier.NpcFaceGenTextureVerificationResult> results,
        int topCount)
    {
        var verified = results.Where(result => result.Verified).ToList();
        var failed = results.Where(result => !result.Verified).ToList();

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Summary[/]");
        AnsiConsole.MarkupLine("  Verified: [green]{0}[/]", verified.Count);
        AnsiConsole.MarkupLine("  Failed:   [red]{0}[/]", failed.Count);

        if (verified.Count > 0)
        {
            AnsiConsole.MarkupLine("  Exact RGB matches: [green]{0}[/]", verified.Count(result => result.ExactMatch));
            AnsiConsole.MarkupLine(
                "  Mean MAE(RGB): [cyan]{0:F4}[/]",
                verified.Average(result => result.MeanAbsoluteRgbError));
            AnsiConsole.MarkupLine(
                "  Mean RMSE(RGB): [cyan]{0:F4}[/]",
                verified.Average(result => result.RootMeanSquareRgbError));
            AnsiConsole.MarkupLine(
                "  Worst MAE(RGB): [yellow]{0:F4}[/]",
                verified.Max(result => result.MeanAbsoluteRgbError));
            AnsiConsole.MarkupLine(
                "  Worst max channel error: [yellow]{0}[/]",
                verified.Max(result => result.MaxAbsoluteRgbError));
        }

        if (failed.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold]Failures[/]");
            foreach (var group in failed
                         .GroupBy(result => result.FailureReason ?? "unknown")
                         .OrderByDescending(group => group.Count()))
            {
                AnsiConsole.MarkupLine(
                    "  [red]{0}[/]: {1}",
                    Markup.Escape(group.Key),
                    group.Count());
            }
        }

        if (verified.Count == 0)
        {
            return;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Worst Divergences[/]");
        var table = new Table();
        table.AddColumn("FormID");
        table.AddColumn("EditorID");
        table.AddColumn("Mode");
        table.AddColumn(new TableColumn("MAE").RightAligned());
        table.AddColumn(new TableColumn("RMSE").RightAligned());
        table.AddColumn(new TableColumn("Max").RightAligned());
        table.AddColumn(new TableColumn(">4 px").RightAligned());
        table.Border = TableBorder.Simple;

        foreach (var result in verified
                     .OrderByDescending(item => item.MeanAbsoluteRgbError)
                     .ThenByDescending(item => item.MaxAbsoluteRgbError)
                     .Take(Math.Max(1, topCount)))
        {
            table.AddRow(
                $"0x{result.FormId:X8}",
                result.EditorId ?? result.FullName ?? "?",
                result.ComparisonMode ?? "?",
                result.MeanAbsoluteRgbError.ToString("F4"),
                result.RootMeanSquareRgbError.ToString("F4"),
                result.MaxAbsoluteRgbError.ToString(CultureInfo.InvariantCulture),
                result.PixelsWithRgbErrorAbove4.ToString("N0", CultureInfo.InvariantCulture));
        }

        AnsiConsole.Write(table);
    }

    private static void ExportComparisonImages(
        string rootDir,
        NpcAppearance appearance,
        NpcFaceGenTextureVerifier.NpcFaceGenTextureVerificationResult result,
        DecodedTexture generatedTexture,
        DecodedTexture shippedTexture)
    {
        var npcDir = Path.Combine(rootDir, BuildImageDirectoryName(appearance));
        if (Directory.Exists(npcDir))
        {
            Directory.Delete(npcDir, true);
        }

        Directory.CreateDirectory(npcDir);

        PngWriter.SaveRgba(
            generatedTexture.Pixels,
            generatedTexture.Width,
            generatedTexture.Height,
            Path.Combine(npcDir, "generated_egt.png"));
        PngWriter.SaveRgba(
            shippedTexture.Pixels,
            shippedTexture.Width,
            shippedTexture.Height,
            Path.Combine(npcDir, "shipped_egt.png"));
        PngWriter.SaveRgba(
            NpcTextureComparison.BuildDiffPixels(generatedTexture.Pixels, shippedTexture.Pixels),
            generatedTexture.Width,
            generatedTexture.Height,
            Path.Combine(npcDir, "diff_egt.png"));
        PngWriter.SaveRgba(
            NpcTextureComparison.BuildAmplifiedDiffPixels(generatedTexture.Pixels, shippedTexture.Pixels, 10),
            generatedTexture.Width,
            generatedTexture.Height,
            Path.Combine(npcDir, "diff_egt_10x.png"));
        PngWriter.SaveRgba(
            NpcTextureComparison.BuildSignedBiasPixels(generatedTexture.Pixels, shippedTexture.Pixels),
            generatedTexture.Width,
            generatedTexture.Height,
            Path.Combine(npcDir, "diff_egt_signed.png"));

        var metadata = new StringBuilder();
        metadata.AppendLine($"form_id=0x{result.FormId:X8}");
        metadata.AppendLine($"plugin_name={result.PluginName}");
        metadata.AppendLine($"editor_id={result.EditorId ?? string.Empty}");
        metadata.AppendLine($"full_name={result.FullName ?? string.Empty}");
        metadata.AppendLine("generated_kind=egt_delta");
        metadata.AppendLine("shipped_kind=egt_delta");
        metadata.AppendLine($"comparison_mode={result.ComparisonMode ?? string.Empty}");
        metadata.AppendLine($"width={result.Width}");
        metadata.AppendLine($"height={result.Height}");
        metadata.AppendLine($"mae_rgb={result.MeanAbsoluteRgbError.ToString("F6", CultureInfo.InvariantCulture)}");
        metadata.AppendLine($"rmse_rgb={result.RootMeanSquareRgbError.ToString("F6", CultureInfo.InvariantCulture)}");
        metadata.AppendLine($"max_abs_rgb={result.MaxAbsoluteRgbError.ToString(CultureInfo.InvariantCulture)}");
        metadata.AppendLine(
            $"pixels_any_diff={result.PixelsWithAnyRgbDifference.ToString(CultureInfo.InvariantCulture)}");
        metadata.AppendLine($"pixels_gt1={result.PixelsWithRgbErrorAbove1.ToString(CultureInfo.InvariantCulture)}");
        metadata.AppendLine($"pixels_gt2={result.PixelsWithRgbErrorAbove2.ToString(CultureInfo.InvariantCulture)}");
        metadata.AppendLine($"pixels_gt4={result.PixelsWithRgbErrorAbove4.ToString(CultureInfo.InvariantCulture)}");
        metadata.AppendLine($"pixels_gt8={result.PixelsWithRgbErrorAbove8.ToString(CultureInfo.InvariantCulture)}");
        metadata.AppendLine($"shipped_texture={result.ShippedTexturePath}");
        metadata.AppendLine($"base_texture={result.BaseTexturePath ?? string.Empty}");
        metadata.AppendLine($"egt_path={result.EgtPath ?? string.Empty}");
        File.WriteAllText(Path.Combine(npcDir, "metadata.txt"), metadata.ToString(), Encoding.UTF8);
    }

    private static string BuildImageDirectoryName(NpcAppearance appearance)
    {
        var safeName = NpcExportFileNaming.SanitizeStem(appearance.EditorId) ??
                       NpcExportFileNaming.SanitizeStem(appearance.FullName);
        return string.IsNullOrWhiteSpace(safeName)
            ? $"{appearance.NpcFormId:X8}"
            : $"{appearance.NpcFormId:X8}_{safeName}";
    }

    private static void WriteCsvReport(
        IEnumerable<NpcFaceGenTextureVerifier.NpcFaceGenTextureVerificationResult> results,
        string reportPath)
    {
        var fullPath = Path.GetFullPath(reportPath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var sb = new StringBuilder();
        sb.AppendLine(
            "form_id,plugin_name,editor_id,full_name,verified,failure_reason,comparison_mode,width,height,mae_rgb,rmse_rgb,max_abs_rgb,pixels_any_diff,pixels_gt1,pixels_gt2,pixels_gt4,pixels_gt8,shipped_texture,base_texture,egt_path");

        foreach (var result in results.OrderBy(item => item.FormId))
        {
            sb.Append(Csv(result.FormId.ToString("X8", CultureInfo.InvariantCulture))).Append(',');
            sb.Append(Csv(result.PluginName)).Append(',');
            sb.Append(Csv(result.EditorId)).Append(',');
            sb.Append(Csv(result.FullName)).Append(',');
            sb.Append(Csv(result.Verified ? "true" : "false")).Append(',');
            sb.Append(Csv(result.FailureReason)).Append(',');
            sb.Append(Csv(result.ComparisonMode)).Append(',');
            sb.Append(Csv(result.Width == 0 ? null : result.Width.ToString(CultureInfo.InvariantCulture))).Append(',');
            sb.Append(Csv(result.Height == 0 ? null : result.Height.ToString(CultureInfo.InvariantCulture)))
                .Append(',');
            sb.Append(Csv(result.Verified
                ? result.MeanAbsoluteRgbError.ToString("F6", CultureInfo.InvariantCulture)
                : null)).Append(',');
            sb.Append(Csv(result.Verified
                ? result.RootMeanSquareRgbError.ToString("F6", CultureInfo.InvariantCulture)
                : null)).Append(',');
            sb.Append(Csv(result.Verified ? result.MaxAbsoluteRgbError.ToString(CultureInfo.InvariantCulture) : null))
                .Append(',');
            sb.Append(Csv(result.Verified
                ? result.PixelsWithAnyRgbDifference.ToString(CultureInfo.InvariantCulture)
                : null)).Append(',');
            sb.Append(Csv(result.Verified
                ? result.PixelsWithRgbErrorAbove1.ToString(CultureInfo.InvariantCulture)
                : null)).Append(',');
            sb.Append(Csv(result.Verified
                ? result.PixelsWithRgbErrorAbove2.ToString(CultureInfo.InvariantCulture)
                : null)).Append(',');
            sb.Append(Csv(result.Verified
                ? result.PixelsWithRgbErrorAbove4.ToString(CultureInfo.InvariantCulture)
                : null)).Append(',');
            sb.Append(Csv(result.Verified
                ? result.PixelsWithRgbErrorAbove8.ToString(CultureInfo.InvariantCulture)
                : null)).Append(',');
            sb.Append(Csv(result.ShippedTexturePath)).Append(',');
            sb.Append(Csv(result.BaseTexturePath)).Append(',');
            sb.Append(Csv(result.EgtPath)).AppendLine();
        }

        File.WriteAllText(fullPath, sb.ToString(), Encoding.UTF8);
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
}
