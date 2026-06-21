using System.CommandLine;
using EgtAnalyzer.Settings;
using EgtAnalyzer.Verification;
using BethesdaMultitool.CLI;
using BethesdaMultitool.Core;
using BethesdaMultitool.Core.Formats.Esm.Analysis;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Appearance;
using Spectre.Console;

namespace EgtAnalyzer.Commands;

internal static class VerifyEgtCommand
{
    private static readonly Logger Log = Logger.Instance;

    internal static Command Create()
    {
        var command = new Command(
            "verify-egt",
            "Regenerate NPC head FaceGen textures and compare them to shipped facemod textures");

        var meshesBsaArg = new Argument<string>("meshes-bsa")
        {
            Description = "Path to meshes BSA file"
        };
        var extraMeshesBsaOption = new Option<string[]?>("--extra-meshes-bsa")
        {
            Description = "Additional meshes BSA file(s) searched as fallback for EGT assets",
            AllowMultipleArgumentsPerToken = true
        };
        var esmOption = new Option<string>("--esm")
        {
            Description = "Path to ESM file",
            Required = true
        };
        var texturesBsaOption = new Option<string[]?>("--textures-bsa")
        {
            Description =
                "Path to texture source(s): BSA file(s) or a root texture directory (auto-detected from meshes BSA directory if omitted)",
            AllowMultipleArgumentsPerToken = true
        };
        var npcOption = new Option<string[]?>("--npc")
        {
            Description = "Limit verification to specific NPC FormIDs or EditorIDs",
            AllowMultipleArgumentsPerToken = true
        };
        var limitOption = new Option<int?>("--limit")
        {
            Description = "Maximum number of shipped facemod textures to verify"
        };
        var topOption = new Option<int>("--top")
        {
            Description = "How many worst mismatches to show in the summary",
            DefaultValueFactory = _ => 10
        };
        var reportOption = new Option<string?>("--report")
        {
            Description = "Optional CSV report output path"
        };
        var variantReportOption = new Option<string?>("--variant-report")
        {
            Description = "Optional CSV report with per-NPC alternative bake-mode metrics for clustering"
        };
        var imagesOption = new Option<string?>("--images")
        {
            Description = "Optional output directory for generated/shipped/diff PNG comparisons"
        };
        var rmsClampOption = new Option<float>("--rms-clamp")
        {
            Description =
                "RMS clamp threshold for merged FaceGen coefficients (0 = disabled). " +
                "If set, coefficients are scaled down when their RMS exceeds this value.",
            DefaultValueFactory = _ => 0f
        };
        var rawDeltaFitOption = new Option<bool>("--raw-fit-coeffs")
        {
            Description =
                "Run a least-squares fit against the shipped native delta texture under the current quantized EGT basis"
        };
        var rawFitProvenancePcaOption = new Option<bool>("--raw-fit-prov-family")
        {
            Description =
                "Scan the authored same-race/same-sex FGTS family and report the best provenance-only raw fit"
        };
        rawFitProvenancePcaOption.Aliases.Add("--raw-fit-prov-pca");
        var residualProjectionOption = new Option<bool>("--residual-projection")
        {
            Description =
                "Project the shipped-vs-generated native raw residual onto individual symmetric morph bases"
        };
        var residualSubspaceOption = new Option<int[]?>("--residual-subspace")
        {
            Description =
                "Solve a residual correction only inside the specified symmetric morph subspace, leaving other coefficients fixed",
            AllowMultipleArgumentsPerToken = true
        };
        var inspectMorphOption = new Option<int[]?>("--inspect-morph")
        {
            Description =
                "Dump raw EGT row bytes, parsed rows, and native contribution stats for specific symmetric morph indices",
            AllowMultipleArgumentsPerToken = true
        };
        var inspectMorphSummaryOnlyOption = new Option<bool>("--inspect-morph-summary-only")
        {
            Description =
                "Suppress detailed per-NPC inspect-morph dumps and keep only the final cross-NPC and external donor summaries"
        };
        var morphStructureOption = new Option<bool>("--morph-structure")
        {
            Description =
                "Dump a coefficient-free, scale-aware structure table for all 50 symmetric morphs plus residual alignment stats"
        };
        var verboseOption = new Option<bool>("-v", "--verbose")
        {
            Description = "Show verbose logging"
        };

        command.Arguments.Add(meshesBsaArg);
        command.Options.Add(extraMeshesBsaOption);
        command.Options.Add(esmOption);
        command.Options.Add(texturesBsaOption);
        command.Options.Add(npcOption);
        command.Options.Add(limitOption);
        command.Options.Add(topOption);
        command.Options.Add(reportOption);
        command.Options.Add(variantReportOption);
        command.Options.Add(imagesOption);
        command.Options.Add(rmsClampOption);
        command.Options.Add(rawDeltaFitOption);
        command.Options.Add(rawFitProvenancePcaOption);
        command.Options.Add(residualProjectionOption);
        command.Options.Add(residualSubspaceOption);
        command.Options.Add(inspectMorphOption);
        command.Options.Add(inspectMorphSummaryOnlyOption);
        command.Options.Add(morphStructureOption);
        command.Options.Add(verboseOption);

        command.SetAction((parseResult, _) =>
        {
            Log.SetVerbose(parseResult.GetValue(verboseOption));

            var settings = new NpcEgtVerificationSettings
            {
                MeshesBsaPath = parseResult.GetValue(meshesBsaArg)!,
                ExtraMeshesBsaPaths = parseResult.GetValue(extraMeshesBsaOption),
                EsmPath = parseResult.GetValue(esmOption)!,
                ExplicitTexturesBsaPaths = parseResult.GetValue(texturesBsaOption),
                NpcFilters = parseResult.GetValue(npcOption),
                Limit = parseResult.GetValue(limitOption),
                TopCount = parseResult.GetValue(topOption),
                ReportPath = parseResult.GetValue(reportOption),
                VariantReportPath = parseResult.GetValue(variantReportOption),
                ImageOutputDir = parseResult.GetValue(imagesOption),
                RmsClampThreshold = parseResult.GetValue(rmsClampOption),
                RawDeltaCoefficientFit = parseResult.GetValue(rawDeltaFitOption),
                RawFitProvenancePca = parseResult.GetValue(rawFitProvenancePcaOption),
                ResidualProjection = parseResult.GetValue(residualProjectionOption),
                ResidualSubspaceIndices = parseResult.GetValue(residualSubspaceOption),
                InspectMorphIndices = parseResult.GetValue(inspectMorphOption),
                InspectMorphSummaryOnly = parseResult.GetValue(inspectMorphSummaryOnlyOption),
                MorphStructure = parseResult.GetValue(morphStructureOption)
            };

            RunPipeline(settings);
            return Task.CompletedTask;
        });

        return command;
    }

    private static void RunPipeline(NpcEgtVerificationSettings settings)
    {
        if (!ValidateInputPaths(settings, out var texturesBsaPaths))
        {
            return;
        }

        NpcFaceGenCoefficientMerger.RmsClampThreshold = settings.RmsClampThreshold;
        NpcFaceGenTextureVerifier.EnableRawDeltaCoefficientFit = settings.RawDeltaCoefficientFit;
        NpcFaceGenTextureVerifier.EnableResidualProjection = settings.ResidualProjection;
        NpcFaceGenTextureVerifier.ResidualSubspaceIndices = settings.ResidualSubspaceIndices;
        NpcFaceGenTextureVerifier.InspectMorphIndices = settings.InspectMorphIndices;
        NpcFaceGenTextureVerifier.EnableInspectMorphSummaryOnly = settings.InspectMorphSummaryOnly;
        NpcFaceGenTextureVerifier.EnableMorphStructure = settings.MorphStructure;
        if (settings.RmsClampThreshold > 0f)
        {
            AnsiConsole.MarkupLine(
                "RMS clamp threshold: [yellow]{0:F2}[/]",
                settings.RmsClampThreshold);
        }
        if (settings.RawDeltaCoefficientFit)
        {
            AnsiConsole.MarkupLine("Raw delta coefficient fit: [yellow]enabled[/]");
        }
        if (settings.RawFitProvenancePca)
        {
            AnsiConsole.MarkupLine("Raw fit provenance family scan: [yellow]enabled[/]");
        }
        if (settings.ResidualProjection)
        {
            AnsiConsole.MarkupLine("Residual projection: [yellow]enabled[/]");
        }
        if (settings.ResidualSubspaceIndices is { Length: > 0 })
        {
            AnsiConsole.MarkupLine(
                "Residual subspace: [yellow]{0}[/]",
                string.Join(", ", settings.ResidualSubspaceIndices.OrderBy(index => index)));
        }
        if (settings.InspectMorphIndices is { Length: > 0 })
        {
            AnsiConsole.MarkupLine(
                "Inspect morphs: [yellow]{0}[/]",
                string.Join(", ", settings.InspectMorphIndices.OrderBy(index => index)));
            if (settings.InspectMorphSummaryOnly)
            {
                AnsiConsole.MarkupLine("Inspect morph summary-only: [yellow]enabled[/]");
            }
        }
        if (settings.MorphStructure)
        {
            AnsiConsole.MarkupLine("Morph structure dump: [yellow]enabled[/]");
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

        if (settings.InspectMorphIndices is { Length: > 0 })
        {
            NpcFaceGenTextureVerifier.ResetInspectMorphRunState();
        }

        using var meshArchives = NpcMeshArchiveSet.Open(settings.MeshesBsaPath, settings.ExtraMeshesBsaPaths);
        using var textureResolver = new NifTextureResolver(texturesBsaPaths);

        var egtCache = new Dictionary<string, EgtParser?>(StringComparer.OrdinalIgnoreCase);
        var details = new List<NpcFaceGenTextureVerificationDetail>(targets.Count);
        var imageOutputDir = PrepareImageOutputDir(settings.ImageOutputDir);
        var exportedImageSets = 0;
        var rawFitNeighborCandidates = settings.RawDeltaCoefficientFit || settings.RawFitProvenancePca
            ? VerifyEgtRawFitReporter.BuildRawFitNeighborCandidates(resolver, pluginName)
            : null;

        for (var index = 0; index < targets.Count; index++)
        {
            var target = targets[index];
            var appearance = resolver.ResolveHeadOnly(target.FormId, pluginName);
            if (appearance == null)
            {
                details.Add(new NpcFaceGenTextureVerificationDetail(
                    new NpcFaceGenTextureVerificationResult
                    {
                        FormId = target.FormId,
                        PluginName = target.PluginName,
                        ShippedTexturePath = target.VirtualPath,
                        FailureReason = "npc not found in esm"
                    },
                    null,
                    null));
            }
            else
            {
                var verification = NpcFaceGenTextureVerifier.VerifyDetailed(
                    appearance,
                    target,
                    meshArchives,
                    textureResolver,
                    egtCache);
                details.Add(verification);

                if (rawFitNeighborCandidates is not null)
                {
                    if (settings.RawDeltaCoefficientFit)
                    {
                        VerifyEgtRawFitReporter.PrintRawFitNeighborSummary(
                            resolver,
                            appearance,
                            verification,
                            rawFitNeighborCandidates);
                    }

                    if (settings.RawFitProvenancePca)
                    {
                        VerifyEgtRawFitReporter.PrintRawFitProvenancePcaSummary(
                            resolver,
                            appearance,
                            verification,
                            rawFitNeighborCandidates,
                            meshArchives,
                            egtCache);
                    }
                }

                if (imageOutputDir != null &&
                    verification.Result.Verified &&
                    verification.GeneratedTexture != null &&
                    verification.ShippedTexture != null)
                {
                    VerifyEgtImageExporter.ExportComparisonImages(
                        imageOutputDir,
                        appearance,
                        verification);
                    exportedImageSets++;
                }
            }

            if (targets.Count <= 20 || (index + 1) % 25 == 0 || index == targets.Count - 1)
            {
                AnsiConsole.WriteLine(
                    $"  [{index + 1}/{targets.Count}] 0x{target.FormId:X8}");
            }
        }

        if (settings.InspectMorphIndices is { Length: > 0 })
        {
            NpcFaceGenTextureVerifier.PrintCrossNpcRequiredRowSimilaritySummary();
            NpcFaceGenTextureVerifier.PrintExternalHeadEgtRequiredRowSummary(meshArchives, egtCache);
        }

        var results = details.Select(detail => detail.Result).ToList();
        VerifyEgtSummaryReporter.PrintSummary(results, settings.TopCount);

        if (!string.IsNullOrWhiteSpace(settings.ReportPath))
        {
            VerifyEgtCsvReporter.WriteCsvReport(results, settings.ReportPath!);
            AnsiConsole.MarkupLine(
                "Wrote report: [cyan]{0}[/]",
                Path.GetFullPath(settings.ReportPath!));
        }

        if (!string.IsNullOrWhiteSpace(settings.VariantReportPath))
        {
            VerifyEgtCsvReporter.WriteVariantCsvReport(details, settings.VariantReportPath!);
            AnsiConsole.MarkupLine(
                "Wrote variant report: [cyan]{0}[/]",
                Path.GetFullPath(settings.VariantReportPath!));
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

        texturesBsaPaths = NpcTextureHelpers.ResolveTexturesBsaPaths(
            settings.MeshesBsaPath,
            settings.ExplicitTexturesBsaPaths);
        if (texturesBsaPaths.Length == 0)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] No texture sources found");
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

    private static List<ShippedNpcFaceTexture> ApplyFilters(
        IReadOnlyDictionary<uint, ShippedNpcFaceTexture> discoveredTargets,
        NpcAppearanceResolver resolver,
        string[]? npcFilters,
        int? limit)
    {
        var selected = new List<ShippedNpcFaceTexture>();

        if (npcFilters is { Length: > 0 })
        {
            var allNpcs = resolver.GetAllNpcs();
            var allNpcPairs = allNpcs.ToList();

            foreach (var filter in npcFilters)
            {
                var parsedFormId = NpcTextureHelpers.ParseFormId(filter);
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
}
