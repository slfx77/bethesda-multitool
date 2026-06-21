using System.Globalization;
using BethesdaMultitool.CLI;
using BethesdaMultitool.Core.Formats.Bsa;
using BethesdaMultitool.Core.Formats.Dds;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Assets;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;
using static EgtAnalyzer.Verification.DeltaTextureHelpers;
using static EgtAnalyzer.Verification.MorphStructureAnalyzer;
using static EgtAnalyzer.Verification.RawDeltaFitDumper;
using static EgtAnalyzer.Verification.RawDeltaFitSolver;

namespace EgtAnalyzer.Verification;

internal static class NpcFaceGenTextureVerifier
{
    private const string FacemodsRoot = @"textures\characters\facemods\";
    private const int TopMorphSweepCount = 5;
    internal static bool EnableRawDeltaCoefficientFit { get; set; }
    internal static bool EnableResidualProjection { get; set; }
    internal static int[]? ResidualSubspaceIndices { get; set; }
    internal static int[]? InspectMorphIndices { get; set; }
    internal static bool EnableInspectMorphSummaryOnly { get; set; }
    internal static bool EnableMorphStructure { get; set; }

    internal static void ResetInspectMorphRunState() =>
        NpcFaceGenMorphInspector.ResetInspectMorphRunState();

    internal static IReadOnlyDictionary<uint, ShippedNpcFaceTexture> DiscoverShippedFaceTextures(
        IEnumerable<string> textureBsaPaths,
        string pluginName)
    {
        var discovered = new Dictionary<uint, ShippedNpcFaceTexture>();

        foreach (var textureBsaPath in textureBsaPaths)
        {
            if (Directory.Exists(textureBsaPath))
            {
                foreach (var filePath in Directory.EnumerateFiles(textureBsaPath, "*.*", SearchOption.AllDirectories))
                {
                    var extension = Path.GetExtension(filePath);
                    if (!extension.Equals(".dds", StringComparison.OrdinalIgnoreCase) &&
                        !extension.Equals(".ddx", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var relativePath = Path.GetRelativePath(textureBsaPath, filePath)
                        .Replace(Path.DirectorySeparatorChar, '\\')
                        .Replace(Path.AltDirectorySeparatorChar, '\\');
                    if (!TryParseShippedFaceTexture(relativePath, out var parsed) ||
                        !string.Equals(parsed.PluginName, pluginName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!discovered.ContainsKey(parsed.FormId))
                    {
                        discovered.Add(
                            parsed.FormId,
                            parsed with { ArchivePath = textureBsaPath });
                    }
                }

                continue;
            }

            var archive = BsaParser.Parse(textureBsaPath);
            foreach (var file in archive.AllFiles)
            {
                if (!TryParseShippedFaceTexture(file.FullPath, out var parsed) ||
                    !string.Equals(parsed.PluginName, pluginName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!discovered.ContainsKey(parsed.FormId))
                {
                    discovered.Add(
                        parsed.FormId,
                        parsed with { ArchivePath = textureBsaPath });
                }
            }
        }

        return discovered;
    }

    internal static bool TryParseShippedFaceTexture(
        string virtualPath,
        out ShippedNpcFaceTexture shippedTexture)
    {
        shippedTexture = null!;

        var normalized = NifTexturePathUtility.Normalize(virtualPath);
        if (!normalized.StartsWith(FacemodsRoot, StringComparison.Ordinal))
        {
            return false;
        }

        var remainder = normalized[FacemodsRoot.Length..];
        var slashIndex = remainder.IndexOf('\\');
        if (slashIndex <= 0 || slashIndex == remainder.Length - 1)
        {
            return false;
        }

        var pluginName = remainder[..slashIndex];
        var fileName = remainder[(slashIndex + 1)..];
        var extension = Path.GetExtension(fileName);
        if (!extension.Equals(".ddx", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".dds", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (!stem.EndsWith("_0", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!TryParseShippedFaceTextureFormId(stem, out var formId))
        {
            return false;
        }

        shippedTexture = new ShippedNpcFaceTexture(
            formId,
            pluginName,
            normalized,
            null);
        return true;
    }

    private static bool TryParseShippedFaceTextureFormId(string stem, out uint formId)
    {
        formId = 0;

        var parts = stem.Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 2 &&
            parts[1] == "0" &&
            uint.TryParse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out formId))
        {
            return true;
        }

        if (parts.Length == 3 &&
            parts[2] == "0" &&
            parts[0].Length == 9 &&
            (parts[0][0] == 'm' || parts[0][0] == 'M' || parts[0][0] == 'f' || parts[0][0] == 'F') &&
            uint.TryParse(parts[0][1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _) &&
            uint.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out formId))
        {
            return true;
        }

        return false;
    }

    internal static NpcFaceGenTextureVerificationResult Verify(
        NpcAppearance appearance,
        ShippedNpcFaceTexture shippedTexture,
        NpcMeshArchiveSet meshArchives,
        NifTextureResolver textureResolver,
        Dictionary<string, EgtParser?> egtCache)
    {
        return VerifyDetailed(
            appearance,
            shippedTexture,
            meshArchives,
            textureResolver,
            egtCache).Result;
    }

    internal static NpcFaceGenTextureVerificationDetail VerifyDetailed(
        NpcAppearance appearance,
        ShippedNpcFaceTexture shippedTexture,
        NpcMeshArchiveSet meshArchives,
        NifTextureResolver textureResolver,
        Dictionary<string, EgtParser?> egtCache)
    {
        var baseTexturePath = GetHeadTexturePath(appearance.HeadDiffuseOverride);
        var egtPath = appearance.BaseHeadNifPath != null
            ? Path.ChangeExtension(appearance.BaseHeadNifPath, ".egt")
            : null;

        var result = new NpcFaceGenTextureVerificationResult
        {
            FormId = appearance.NpcFormId,
            PluginName = shippedTexture.PluginName,
            EditorId = appearance.EditorId,
            FullName = appearance.FullName,
            ShippedTexturePath = shippedTexture.VirtualPath,
            ShippedSourcePath = shippedTexture.ArchivePath,
            ShippedSourceFormat = Path.GetExtension(shippedTexture.VirtualPath),
            BaseTexturePath = baseTexturePath,
            EgtPath = egtPath
        };

        if (appearance.BaseHeadNifPath == null)
        {
            return new NpcFaceGenTextureVerificationDetail(
                result with { FailureReason = "missing base head nif path" },
                null,
                null);
        }

        if (baseTexturePath == null)
        {
            return new NpcFaceGenTextureVerificationDetail(
                result with { FailureReason = "missing head diffuse texture path" },
                null,
                null);
        }

        if (egtPath == null)
        {
            return new NpcFaceGenTextureVerificationDetail(
                result with { FailureReason = "missing head egt path" },
                null,
                null);
        }

        if (!egtCache.TryGetValue(egtPath, out var egt))
        {
            egt = NpcMeshHelpers.LoadEgtFromBsa(egtPath, meshArchives);
            egtCache[egtPath] = egt;
        }

        RawDeltaCoefficientFitResult? rawDeltaCoefficientFit = null;

        if (egt == null)
        {
            return new NpcFaceGenTextureVerificationDetail(
                result with { FailureReason = $"egt not found: {egtPath}" },
                null,
                null);
        }

        var shippedDecodedTexture = textureResolver.GetTexture(shippedTexture.VirtualPath);
        if (shippedDecodedTexture == null)
        {
            return new NpcFaceGenTextureVerificationDetail(
                result with { FailureReason = $"shipped texture not found: {shippedTexture.VirtualPath}" },
                null,
                null);
        }

        var diagnosticVariants = new List<DiagnosticVariantMetric>();

        DecodedTexture? generatedTexture;
        string comparisonMode;
        if (shippedDecodedTexture.Width == egt.Cols &&
            shippedDecodedTexture.Height == egt.Rows)
        {
            comparisonMode = "native_egt";
            DumpCoefficients(appearance, egt);
            var coeffs = appearance.FaceGenTextureCoeffs ?? [];

            var npcOnly = appearance.NpcFaceGenTextureCoeffs ?? new float[50];
            var raceOnly = appearance.RaceFaceGenTextureCoeffs ?? new float[50];
            foreach (var (label, testCoeffs) in new[]
                     {
                         ("merged", coeffs),
                         ("npc_only", npcOnly),
                         ("race_only", raceOnly)
                     })
            {
                var testGen = FaceGenTextureMorpher.BuildNativeDeltaTexture(
                    egt, testCoeffs,
                    FaceGenTextureMorpher.TextureAccumulationMode.EngineTruncated256);
                if (testGen != null)
                {
                    var testMetrics = NpcTextureComparison.CompareRgb(
                        testGen.Pixels, shippedDecodedTexture.Pixels,
                        testGen.Width, testGen.Height);
                    Console.WriteLine(
                        $"  COEFFSRC={label,10} 0x{appearance.NpcFormId:X8}: MAE={testMetrics.MeanAbsoluteRgbError:F4} max={testMetrics.MaxAbsoluteRgbError}");
                }
            }

            Console.WriteLine($"  CHANNEL-PERMUTATION 0x{appearance.NpcFormId:X8}:");
            (string Label, int Ri, int Gi, int Bi)[] permutations =
            [
                ("RGB", 0, 1, 2),
                ("RBG", 0, 2, 1),
                ("GRB", 1, 0, 2),
                ("GBR", 1, 2, 0),
                ("BRG", 2, 0, 1),
                ("BGR", 2, 1, 0)
            ];
            foreach (var (permLabel, ri, gi, bi) in permutations)
            {
                var permMorphs = new EgtMorph[egt.SymmetricMorphs.Length];
                for (var mi = 0; mi < egt.SymmetricMorphs.Length; mi++)
                {
                    var orig = egt.SymmetricMorphs[mi];
                    var channels = new[] { orig.DeltaR, orig.DeltaG, orig.DeltaB };
                    permMorphs[mi] = new EgtMorph
                    {
                        Scale = orig.Scale,
                        DeltaR = channels[ri],
                        DeltaG = channels[gi],
                        DeltaB = channels[bi]
                    };
                }

                var permEgt = EgtParser.CreateFromMorphs(egt.Cols, egt.Rows, permMorphs);
                var permGen = FaceGenTextureMorpher.BuildNativeDeltaTexture(
                    permEgt, coeffs,
                    FaceGenTextureMorpher.TextureAccumulationMode.EngineTruncated256);
                if (permGen != null)
                {
                    var permMetrics = NpcTextureComparison.CompareRgb(
                        permGen.Pixels, shippedDecodedTexture.Pixels,
                        permGen.Width, permGen.Height);
                    var permSigned = NpcTextureComparison.CompareSignedRgb(
                        permGen.Pixels, shippedDecodedTexture.Pixels,
                        permGen.Width, permGen.Height);
                    Console.WriteLine(
                        $"    {permLabel}: MAE={permMetrics.MeanAbsoluteRgbError:F4} max={permMetrics.MaxAbsoluteRgbError}  sR={permSigned.MeanSignedRedError:F3} sG={permSigned.MeanSignedGreenError:F3} sB={permSigned.MeanSignedBlueError:F3}");
                }
            }

            foreach (var (accMode, encMode, modeLabel) in new[]
                     {
                         (FaceGenTextureMorpher.TextureAccumulationMode.CurrentFloat,
                             FaceGenTextureMorpher.DeltaTextureEncodingMode.EngineCompressed255Half,
                             "Float+EngineFloor"),
                         (FaceGenTextureMorpher.TextureAccumulationMode.CurrentFloat,
                             FaceGenTextureMorpher.DeltaTextureEncodingMode.EngineCompressed255HalfTruncate,
                             "Float+EngineTrunc"),
                         (FaceGenTextureMorpher.TextureAccumulationMode.EngineTruncated256,
                             FaceGenTextureMorpher.DeltaTextureEncodingMode.EngineCompressed255Half,
                             "Truncated256+Floor"),
                         (FaceGenTextureMorpher.TextureAccumulationMode.EngineTruncated256,
                             FaceGenTextureMorpher.DeltaTextureEncodingMode.EngineCompressed255HalfTruncate,
                             "Truncated256+Trunc"),
                         (FaceGenTextureMorpher.TextureAccumulationMode.EngineQuantized256Double,
                             FaceGenTextureMorpher.DeltaTextureEncodingMode.EngineCompressed255Half,
                             "QuantizedDouble+Floor"),
                         (FaceGenTextureMorpher.TextureAccumulationMode.EngineQuantized256Double,
                             FaceGenTextureMorpher.DeltaTextureEncodingMode.EngineCompressed255HalfTruncate,
                             "QuantizedDouble+Trunc"),
                         (FaceGenTextureMorpher.TextureAccumulationMode.EngineQuantizedCombined256,
                             FaceGenTextureMorpher.DeltaTextureEncodingMode.EngineCompressed255Half,
                             "Combined256+Floor"),
                         (FaceGenTextureMorpher.TextureAccumulationMode.EngineQuantizedCombined256,
                             FaceGenTextureMorpher.DeltaTextureEncodingMode.EngineCompressed255HalfTruncate,
                             "Combined256+Trunc"),
                         (FaceGenTextureMorpher.TextureAccumulationMode.EngineQuantizedCombined65536,
                             FaceGenTextureMorpher.DeltaTextureEncodingMode.EngineCompressed255Half,
                             "Combined65536+Floor"),
                         (FaceGenTextureMorpher.TextureAccumulationMode.EngineQuantizedCombined65536,
                             FaceGenTextureMorpher.DeltaTextureEncodingMode.EngineCompressed255HalfTruncate,
                             "Combined65536+Trunc"),
                         (FaceGenTextureMorpher.TextureAccumulationMode.EngineQuantized256,
                             FaceGenTextureMorpher.DeltaTextureEncodingMode.EngineCompressed255HalfTruncate,
                             "Quantized+EngineTrunc"),
                         (FaceGenTextureMorpher.TextureAccumulationMode.CurrentFloat,
                             FaceGenTextureMorpher.DeltaTextureEncodingMode.Centered128,
                             "Float+Centered128"),
                         (FaceGenTextureMorpher.TextureAccumulationMode.EngineQuantized256,
                             FaceGenTextureMorpher.DeltaTextureEncodingMode.Centered128,
                             "Quantized+Centered128")
                     })
            {
                var testGen = FaceGenTextureMorpher.BuildNativeDeltaTexture(
                    egt, coeffs, accMode, encMode);
                if (testGen != null)
                {
                    var testMetrics = NpcTextureComparison.CompareRgb(
                        testGen.Pixels, shippedDecodedTexture.Pixels,
                        testGen.Width, testGen.Height);
                    diagnosticVariants.Add(new DiagnosticVariantMetric(
                        modeLabel,
                        testMetrics.MeanAbsoluteRgbError,
                        testMetrics.RootMeanSquareRgbError,
                        testMetrics.MaxAbsoluteRgbError));
                    Console.WriteLine(
                        $"  DIAG 0x{appearance.NpcFormId:X8}: {modeLabel,-25} MAE={testMetrics.MeanAbsoluteRgbError:F4} max={testMetrics.MaxAbsoluteRgbError}");
                }
            }

            var genQuantized = FaceGenTextureMorpher.BuildNativeDeltaTexture(
                egt, coeffs,
                FaceGenTextureMorpher.TextureAccumulationMode.EngineTruncated256);
            if (genQuantized != null)
            {
                var qMetrics = NpcTextureComparison.CompareRgb(
                    genQuantized.Pixels, shippedDecodedTexture.Pixels,
                    genQuantized.Width, genQuantized.Height);
                diagnosticVariants.Add(new DiagnosticVariantMetric(
                    "Quantized+EngineFloor",
                    qMetrics.MeanAbsoluteRgbError,
                    qMetrics.RootMeanSquareRgbError,
                    qMetrics.MaxAbsoluteRgbError));
                Console.WriteLine(
                    $"  DIAG 0x{appearance.NpcFormId:X8}: Quantized MAE={qMetrics.MeanAbsoluteRgbError:F4} max={qMetrics.MaxAbsoluteRgbError}");

                DumpRegionMetrics(genQuantized, shippedDecodedTexture);

                var nativeBuffers = FaceGenTextureMorpher.BuildNativeDeltaBuffers(
                    egt, coeffs,
                    FaceGenTextureMorpher.TextureAccumulationMode.EngineTruncated256);
                if (nativeBuffers != null)
                {
                    var shippedDecoded = DecodeEncodedDeltaTextureToFloatBuffers(shippedDecodedTexture);
                    var generatedDecoded = DecodeEncodedDeltaTextureToFloatBuffers(genQuantized);
                    var rawVsShipped = CompareFloatDeltaRgb(nativeBuffers.Value, shippedDecoded);
                    var encodeLoss = CompareFloatDeltaRgb(nativeBuffers.Value, generatedDecoded);

                    Console.WriteLine(
                        $"  RAWDELTA 0x{appearance.NpcFormId:X8}: " +
                        $"native-vs-shipped MAE={rawVsShipped.MeanAbsoluteRgbError:F4} " +
                        $"RMSE={rawVsShipped.RootMeanSquareRgbError:F4} " +
                        $"max={rawVsShipped.MaxAbsoluteRgbError:F3} " +
                        $"sR={rawVsShipped.MeanSignedRedError:F3} " +
                        $"sG={rawVsShipped.MeanSignedGreenError:F3} " +
                        $"sB={rawVsShipped.MeanSignedBlueError:F3}");
                    Console.WriteLine(
                        $"  RAWDELTA-ENCODELOSS 0x{appearance.NpcFormId:X8}: " +
                        $"native-vs-generatedDecode MAE={encodeLoss.MeanAbsoluteRgbError:F4} " +
                        $"RMSE={encodeLoss.RootMeanSquareRgbError:F4} " +
                        $"max={encodeLoss.MaxAbsoluteRgbError:F3}");

                    if (InspectMorphIndices is { Length: > 0 })
                    {
                        NpcFaceGenMorphInspector.DumpMorphInspection(
                            appearance, egt, egtPath, meshArchives,
                            coeffs, InspectMorphIndices,
                            nativeBuffers.Value, shippedDecoded);
                    }

                    if (EnableMorphStructure)
                    {
                        DumpMorphStructureSummary(
                            appearance, egt, coeffs,
                            nativeBuffers.Value, shippedDecoded);
                    }

                    IReadOnlyList<ResidualProjectionRow>? residualProjectionRows = null;

                    if (EnableResidualProjection)
                    {
                        residualProjectionRows = DumpResidualProjectionSummary(
                            appearance, egt, coeffs,
                            nativeBuffers.Value, shippedDecoded);
                    }

                    if (EnableRawDeltaCoefficientFit)
                    {
                        rawDeltaCoefficientFit = DumpRawDeltaCoefficientFit(
                            appearance, egt, coeffs,
                            shippedDecodedTexture, shippedDecoded,
                            rawVsShipped, residualProjectionRows,
                            ResidualSubspaceIndices);
                        DumpRegionalRawDeltaFits(
                            appearance, egt, coeffs,
                            genQuantized, shippedDecodedTexture,
                            nativeBuffers.Value, shippedDecoded);
                    }

                    if (ResidualSubspaceIndices is { Length: > 0 })
                    {
                        DumpResidualSubspaceFit(
                            appearance, egt, coeffs,
                            shippedDecodedTexture,
                            nativeBuffers.Value, shippedDecoded,
                            rawVsShipped, ResidualSubspaceIndices);
                    }

                    var rawDeltaVariants = new List<(string Label, FloatDeltaRgbComparisonMetrics Metrics)>();
                    foreach (var (rawMode, rawLabel) in new[]
                             {
                                 (FaceGenTextureMorpher.TextureAccumulationMode.CurrentFloat, "CurrentFloat"),
                                 (FaceGenTextureMorpher.TextureAccumulationMode.EngineTruncated256, "Truncated256"),
                                 (FaceGenTextureMorpher.TextureAccumulationMode.EngineQuantized256Double,
                                     "Quantized256Double"),
                                 (FaceGenTextureMorpher.TextureAccumulationMode.EngineQuantizedCombined256,
                                     "Combined256"),
                                 (FaceGenTextureMorpher.TextureAccumulationMode.EngineQuantizedCombined65536,
                                     "Combined65536")
                             })
                    {
                        var rawBuffers = FaceGenTextureMorpher.BuildNativeDeltaBuffers(egt, coeffs, rawMode);
                        if (rawBuffers == null)
                        {
                            continue;
                        }

                        rawDeltaVariants.Add((rawLabel, CompareFloatDeltaRgb(rawBuffers.Value, shippedDecoded)));
                    }

                    Console.WriteLine($"  RAWDELTA-TOP 0x{appearance.NpcFormId:X8}:");
                    foreach (var (label, rawMetrics) in rawDeltaVariants.OrderBy(v => v.Metrics.MeanAbsoluteRgbError))
                    {
                        Console.WriteLine(
                            $"    {label,-18} MAE={rawMetrics.MeanAbsoluteRgbError:F4} " +
                            $"RMSE={rawMetrics.RootMeanSquareRgbError:F4} max={rawMetrics.MaxAbsoluteRgbError:F3} " +
                            $"sR={rawMetrics.MeanSignedRedError:F3} sG={rawMetrics.MeanSignedGreenError:F3} sB={rawMetrics.MeanSignedBlueError:F3}");
                    }

                    var rawInterpretationVariants = new List<(string Label, FloatDeltaRgbComparisonMetrics Metrics)>
                    {
                        ("Baseline", rawVsShipped),
                        ("BiasMinus254", CompareFloatDeltaRgb(
                            nativeBuffers.Value,
                            DecodeEncodedDeltaTextureToFloatBuffers(shippedDecodedTexture, 254f))),
                        ("BiasMinus256", CompareFloatDeltaRgb(
                            nativeBuffers.Value,
                            DecodeEncodedDeltaTextureToFloatBuffers(shippedDecodedTexture, 256f))),
                        ("FlipY", CompareFloatDeltaRgb(
                            nativeBuffers.Value,
                            DecodeEncodedDeltaTextureToFloatBuffers(shippedDecodedTexture, 255f, flipY: true))),
                        ("FlipX", CompareFloatDeltaRgb(
                            nativeBuffers.Value,
                            DecodeEncodedDeltaTextureToFloatBuffers(shippedDecodedTexture, 255f, flipX: true))),
                        ("FlipXY", CompareFloatDeltaRgb(
                            nativeBuffers.Value,
                            DecodeEncodedDeltaTextureToFloatBuffers(shippedDecodedTexture, 255f, flipX: true, flipY: true))),
                        ("Invert", CompareFloatDeltaRgb(
                            nativeBuffers.Value,
                            DecodeEncodedDeltaTextureToFloatBuffers(shippedDecodedTexture, 255f, invert: true))),
                        ("InvertFlipY", CompareFloatDeltaRgb(
                            nativeBuffers.Value,
                            DecodeEncodedDeltaTextureToFloatBuffers(shippedDecodedTexture, 255f, flipY: true, invert: true)))
                    };

                    Console.WriteLine($"  RAWDELTA-INTERP 0x{appearance.NpcFormId:X8}:");
                    foreach (var (label, interpMetrics) in rawInterpretationVariants
                                 .OrderBy(v => v.Metrics.MeanAbsoluteRgbError))
                    {
                        Console.WriteLine(
                            $"    {label,-12} MAE={interpMetrics.MeanAbsoluteRgbError:F4} " +
                            $"RMSE={interpMetrics.RootMeanSquareRgbError:F4} max={interpMetrics.MaxAbsoluteRgbError:F3} " +
                            $"sR={interpMetrics.MeanSignedRedError:F3} sG={interpMetrics.MeanSignedGreenError:F3} sB={interpMetrics.MeanSignedBlueError:F3}");
                    }
                }

                Console.WriteLine($"  MORPH-ABLATION 0x{appearance.NpcFormId:X8}:");
                var fullMae = qMetrics.MeanAbsoluteRgbError;
                var baselineMouthMae = GetRegionMae(genQuantized, shippedDecodedTexture, "mouth");
                var ablationRows = new List<MorphAblationRow>();
                for (var mi = 0; mi < Math.Min(coeffs.Length, egt.SymmetricMorphs.Length); mi++)
                {
                    var ablatedCoeffs = (float[])coeffs.Clone();
                    ablatedCoeffs[mi] = 0f;
                    var ablated = FaceGenTextureMorpher.BuildNativeDeltaTexture(
                        egt, ablatedCoeffs,
                        FaceGenTextureMorpher.TextureAccumulationMode.EngineTruncated256);
                    if (ablated == null) continue;
                    var ablatedMetrics = NpcTextureComparison.CompareRgb(
                        ablated.Pixels, shippedDecodedTexture.Pixels,
                        ablated.Width, ablated.Height);
                    var ablatedSigned = NpcTextureComparison.CompareSignedRgb(
                        ablated.Pixels, shippedDecodedTexture.Pixels,
                        ablated.Width, ablated.Height);
                    var delta = ablatedMetrics.MeanAbsoluteRgbError - fullMae;
                    if (MathF.Abs(coeffs[mi]) > 0.01f)
                    {
                        ablationRows.Add(new MorphAblationRow(
                            mi, coeffs[mi], egt.SymmetricMorphs[mi].Scale,
                            ablatedMetrics.MeanAbsoluteRgbError, delta));
                        Console.WriteLine(
                            $"    [{mi:D2}] MAE={ablatedMetrics.MeanAbsoluteRgbError:F4} (Δ={delta:+0.0000;-0.0000}) max={ablatedMetrics.MaxAbsoluteRgbError}  coeff={coeffs[mi]:F4} scale={egt.SymmetricMorphs[mi].Scale:F4}  sR={ablatedSigned.MeanSignedRedError:F3} sG={ablatedSigned.MeanSignedGreenError:F3} sB={ablatedSigned.MeanSignedBlueError:F3}");
                    }
                }

                NpcFaceGenMorphSweepDumper.DumpMorphCoefficientSweep(
                    appearance, egt, coeffs, shippedDecodedTexture,
                    0, fullMae, qMetrics.MaxAbsoluteRgbError, baselineMouthMae);

                var topAblations = ablationRows
                    .Where(row => row.DeltaMae > 0.05d)
                    .OrderByDescending(row => row.DeltaMae)
                    .ThenBy(row => row.MorphIndex)
                    .Take(TopMorphSweepCount)
                    .ToArray();
                if (topAblations.Length > 0)
                {
                    Console.WriteLine($"  MORPH-SWEEP-TOP 0x{appearance.NpcFormId:X8}:");
                    foreach (var ablationRow in topAblations)
                    {
                        NpcFaceGenMorphSweepDumper.DumpMorphCoefficientSweep(
                            appearance, egt, coeffs, shippedDecodedTexture,
                            ablationRow.MorphIndex, fullMae,
                            qMetrics.MaxAbsoluteRgbError, baselineMouthMae);
                    }
                }
            }

            if (genQuantized != null)
            {
                var roundtrippedPixels = Bc1Codec.RoundTrip(
                    genQuantized.Pixels, genQuantized.Width, genQuantized.Height);
                var dxtFloorMetrics = NpcTextureComparison.CompareRgb(
                    genQuantized.Pixels, roundtrippedPixels,
                    genQuantized.Width, genQuantized.Height);
                var dxtVsShippedMetrics = NpcTextureComparison.CompareRgb(
                    roundtrippedPixels, shippedDecodedTexture.Pixels,
                    genQuantized.Width, genQuantized.Height);
                Console.WriteLine(
                    $"  DXT-FLOOR 0x{appearance.NpcFormId:X8}: " +
                    $"BC1 roundtrip MAE={dxtFloorMetrics.MeanAbsoluteRgbError:F4} " +
                    $"RMSE={dxtFloorMetrics.RootMeanSquareRgbError:F4} " +
                    $"max={dxtFloorMetrics.MaxAbsoluteRgbError}  |  " +
                    $"BC1-vs-shipped MAE={dxtVsShippedMetrics.MeanAbsoluteRgbError:F4} " +
                    $"max={dxtVsShippedMetrics.MaxAbsoluteRgbError}");

                var dxtFloorMaxSat = NpcTextureComparison.CompareRgbMaxSaturation(
                    genQuantized.Pixels, roundtrippedPixels,
                    genQuantized.Width, genQuantized.Height);
                var dxtVsShippedMaxSat = NpcTextureComparison.CompareRgbMaxSaturation(
                    roundtrippedPixels, shippedDecodedTexture.Pixels,
                    genQuantized.Width, genQuantized.Height);
                Console.WriteLine(
                    $"  DXT-FLOOR-MAXSAT 0x{appearance.NpcFormId:X8}: " +
                    $"BC1 roundtrip MAE={dxtFloorMaxSat.MeanAbsoluteRgbError:F4} " +
                    $"max={dxtFloorMaxSat.MaxAbsoluteRgbError}  |  " +
                    $"BC1-vs-shipped MAE={dxtVsShippedMaxSat.MeanAbsoluteRgbError:F4} " +
                    $"max={dxtVsShippedMaxSat.MaxAbsoluteRgbError}");
            }

            generatedTexture = genQuantized;
        }
        else
        {
            comparisonMode = "upscaled_egt";
            generatedTexture = FaceGenTextureMorpher.BuildUpscaledDeltaTexture(
                egt,
                appearance.FaceGenTextureCoeffs ?? [],
                shippedDecodedTexture.Width,
                shippedDecodedTexture.Height);
        }

        if (generatedTexture is null)
        {
            return new NpcFaceGenTextureVerificationDetail(
                result with { FailureReason = "generated texture morph returned null" },
                null,
                shippedDecodedTexture);
        }

        if (generatedTexture.Width != shippedDecodedTexture.Width ||
            generatedTexture.Height != shippedDecodedTexture.Height)
        {
            return new NpcFaceGenTextureVerificationDetail(
                result with
                {
                    ComparisonMode = comparisonMode,
                    Width = shippedDecodedTexture.Width,
                    Height = shippedDecodedTexture.Height,
                    FailureReason =
                    $"size mismatch: generated egt {generatedTexture.Width}x{generatedTexture.Height}, shipped {shippedDecodedTexture.Width}x{shippedDecodedTexture.Height}"
                },
                generatedTexture,
                shippedDecodedTexture);
        }

        var metrics = NpcTextureComparison.CompareRgb(
            generatedTexture.Pixels, shippedDecodedTexture.Pixels,
            generatedTexture.Width, generatedTexture.Height);
        var affineFit = NpcTextureComparison.FitPerChannelAffineRgb(
            generatedTexture.Pixels, shippedDecodedTexture.Pixels,
            generatedTexture.Width, generatedTexture.Height);
        var affineFitTexture = DecodedTexture.FromBaseLevel(
            NpcTextureComparison.ApplyPerChannelAffineFit(generatedTexture.Pixels, affineFit),
            generatedTexture.Width, generatedTexture.Height);

        var ssim = NpcTextureComparison.ComputeSsim(
            generatedTexture.Pixels, shippedDecodedTexture.Pixels,
            generatedTexture.Width, generatedTexture.Height);
        var ssimNorm = NpcTextureComparison.ComputeSsim(
            generatedTexture.Pixels, shippedDecodedTexture.Pixels,
            generatedTexture.Width, generatedTexture.Height, true);

        Console.WriteLine(
            $"  AFFINE 0x{appearance.NpcFormId:X8}: " +
            $"scaleR={affineFit.Red.Scale:F4} biasR={affineFit.Red.Bias:F3} " +
            $"scaleG={affineFit.Green.Scale:F4} biasG={affineFit.Green.Bias:F3} " +
            $"scaleB={affineFit.Blue.Scale:F4} biasB={affineFit.Blue.Bias:F3} " +
            $"rawMAE={affineFit.RawMetrics.MeanAbsoluteRgbError:F4} " +
            $"fitMAE={affineFit.FittedMetrics.MeanAbsoluteRgbError:F4} " +
            $"fitRMSE={affineFit.FittedMetrics.RootMeanSquareRgbError:F4} " +
            $"fitMax={affineFit.FittedMetrics.MaxAbsoluteRgbError}");
        Console.WriteLine(
            $"  SSIM 0x{appearance.NpcFormId:X8}: " +
            $"lum={ssim.SsimLuminance:F6} " +
            $"R={ssim.SsimRed:F6} G={ssim.SsimGreen:F6} B={ssim.SsimBlue:F6} " +
            $"rgb_mean={ssim.SsimRgbMean:F6}");
        Console.WriteLine(
            $"  SSIM-NORM 0x{appearance.NpcFormId:X8}: " +
            $"lum={ssimNorm.SsimLuminance:F6} " +
            $"R={ssimNorm.SsimRed:F6} G={ssimNorm.SsimGreen:F6} B={ssimNorm.SsimBlue:F6} " +
            $"rgb_mean={ssimNorm.SsimRgbMean:F6}");

        var ssimSat = NpcTextureComparison.ComputeSsimMaxSaturation(
            generatedTexture.Pixels, shippedDecodedTexture.Pixels,
            generatedTexture.Width, generatedTexture.Height);
        Console.WriteLine(
            $"  SSIM-MAXSAT 0x{appearance.NpcFormId:X8}: " +
            $"R={ssimSat.SsimRed:F6} G={ssimSat.SsimGreen:F6} B={ssimSat.SsimBlue:F6} " +
            $"rgb_mean={ssimSat.SsimRgbMean:F6}");

        var maxSatMetrics = NpcTextureComparison.CompareRgbMaxSaturation(
            generatedTexture.Pixels, shippedDecodedTexture.Pixels,
            generatedTexture.Width, generatedTexture.Height);
        Console.WriteLine(
            $"  MAE-MAXSAT 0x{appearance.NpcFormId:X8}: " +
            $"MAE={maxSatMetrics.MeanAbsoluteRgbError:F4} " +
            $"RMSE={maxSatMetrics.RootMeanSquareRgbError:F4} " +
            $"max={maxSatMetrics.MaxAbsoluteRgbError} " +
            $">1={maxSatMetrics.PixelsWithRgbErrorAbove1} " +
            $">4={maxSatMetrics.PixelsWithRgbErrorAbove4} " +
            $">8={maxSatMetrics.PixelsWithRgbErrorAbove8}");

        DumpRegionMetrics(generatedTexture, shippedDecodedTexture,
            "    REGION-MAXSAT", maxSaturation: true);
        DumpAffineFitRegionMetrics(generatedTexture, shippedDecodedTexture);

        return new NpcFaceGenTextureVerificationDetail(
            result with
            {
                ComparisonMode = comparisonMode,
                Width = generatedTexture.Width,
                Height = generatedTexture.Height,
                MeanAbsoluteRgbError = metrics.MeanAbsoluteRgbError,
                RootMeanSquareRgbError = metrics.RootMeanSquareRgbError,
                MaxAbsoluteRgbError = metrics.MaxAbsoluteRgbError,
                PixelsWithAnyRgbDifference = metrics.PixelsWithAnyRgbDifference,
                PixelsWithRgbErrorAbove1 = metrics.PixelsWithRgbErrorAbove1,
                PixelsWithRgbErrorAbove2 = metrics.PixelsWithRgbErrorAbove2,
                PixelsWithRgbErrorAbove4 = metrics.PixelsWithRgbErrorAbove4,
                PixelsWithRgbErrorAbove8 = metrics.PixelsWithRgbErrorAbove8,
                SsimLuminance = ssim.SsimLuminance,
                SsimRgbMean = ssim.SsimRgbMean,
                SsimNormalizedLuminance = ssimNorm.SsimLuminance,
                SsimNormalizedRgbMean = ssimNorm.SsimRgbMean,
                SsimMaxSatRgbMean = ssimSat.SsimRgbMean,
                AffineFitMeanAbsoluteRgbError = affineFit.FittedMetrics.MeanAbsoluteRgbError,
                AffineFitRootMeanSquareRgbError = affineFit.FittedMetrics.RootMeanSquareRgbError,
                AffineFitMaxAbsoluteRgbError = affineFit.FittedMetrics.MaxAbsoluteRgbError,
                AffineFitScaleRed = affineFit.Red.Scale,
                AffineFitScaleGreen = affineFit.Green.Scale,
                AffineFitScaleBlue = affineFit.Blue.Scale,
                AffineFitBiasRed = affineFit.Red.Bias,
                AffineFitBiasGreen = affineFit.Green.Bias,
                AffineFitBiasBlue = affineFit.Blue.Bias
            },
            generatedTexture,
            shippedDecodedTexture,
            diagnosticVariants,
            affineFitTexture,
            rawDeltaCoefficientFit?.QuantizedCoefficient256);
    }

    private static string? GetHeadTexturePath(string? headDiffuseOverride)
    {
        if (string.IsNullOrWhiteSpace(headDiffuseOverride))
        {
            return null;
        }

        return NifTexturePathUtility.Normalize(headDiffuseOverride);
    }

    internal static void PrintCrossNpcRequiredRowSimilaritySummary() =>
        NpcFaceGenMorphInspector.PrintCrossNpcRequiredRowSimilaritySummary();

    internal static void PrintExternalHeadEgtRequiredRowSummary(
        NpcMeshArchiveSet meshArchives,
        Dictionary<string, EgtParser?> egtCache) =>
        NpcFaceGenMorphInspector.PrintExternalHeadEgtRequiredRowSummary(meshArchives, egtCache);
}
