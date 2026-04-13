using System.Buffers.Binary;
using System.Globalization;
using FalloutXbox360Utils.CLI;
using FalloutXbox360Utils.Core.Formats.Bsa;
using FalloutXbox360Utils.Core.Formats.Dds;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Assets;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Textures;
using static EgtAnalyzer.Verification.CrossNpcRowAnalyzer;
using static EgtAnalyzer.Verification.DeltaTextureHelpers;
using static EgtAnalyzer.Verification.ExternalEgtDonorAnalyzer;
using static EgtAnalyzer.Verification.MorphPlausibilityAnalyzer;
using static EgtAnalyzer.Verification.MorphRowSimilarityAnalyzer;
using static EgtAnalyzer.Verification.MorphStructureAnalyzer;
using static EgtAnalyzer.Verification.RawDeltaFitDumper;
using static EgtAnalyzer.Verification.RawDeltaFitSolver;

namespace EgtAnalyzer.Verification;

internal static class NpcFaceGenTextureVerifier
{
    private const string FacemodsRoot = @"textures\characters\facemods\";
    private const int MorphFactorSweepMinStep = 0;
    private const int MorphFactorSweepMaxStep = 56;
    private const int TopMorphSweepCount = 5;
    private static readonly int[] LateHotspotFamilyIndices = [35, 36, 37, 38, 39, 40, 41, 42, 43, 45, 46, 49];
    private static readonly Dictionary<uint, Dictionary<int, CrossNpcRequiredRow>> InspectRequiredRows = [];
    private static readonly Dictionary<uint, InspectNpcState> InspectNpcStates = [];
    private static readonly Dictionary<uint, string> InspectCurrentEgtPaths = [];
    internal static bool EnableRawDeltaCoefficientFit { get; set; }
    internal static bool EnableResidualProjection { get; set; }
    internal static int[]? ResidualSubspaceIndices { get; set; }
    internal static int[]? InspectMorphIndices { get; set; }
    internal static bool EnableInspectMorphSummaryOnly { get; set; }
    internal static bool EnableMorphStructure { get; set; }

    internal static void ResetInspectMorphRunState()
    {
        InspectRequiredRows.Clear();
        InspectNpcStates.Clear();
        InspectCurrentEgtPaths.Clear();
    }

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
                        DumpMorphInspection(
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

                DumpMorphCoefficientSweep(
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
                        DumpMorphCoefficientSweep(
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

    private static void DumpMorphCoefficientSweep(
        NpcAppearance appearance,
        EgtParser egt,
        float[] coefficients,
        DecodedTexture shipped,
        int morphIndex,
        double baselineMae,
        int baselineMaxError,
        double baselineMouthMae)
    {
        if (morphIndex < 0 ||
            morphIndex >= coefficients.Length ||
            morphIndex >= egt.SymmetricMorphs.Length)
        {
            return;
        }

        var currentCoefficient = coefficients[morphIndex];
        if (MathF.Abs(currentCoefficient) < 0.01f)
        {
            return;
        }

        var bestFactor = 1f;
        var bestCoefficient = currentCoefficient;
        var bestMae = baselineMae;
        var bestMax = baselineMaxError;
        var bestMouthMae = baselineMouthMae;

        for (var step = MorphFactorSweepMinStep; step <= MorphFactorSweepMaxStep; step++)
        {
            var factor = step / 32f;
            var candidateCoefficients = (float[])coefficients.Clone();
            candidateCoefficients[morphIndex] = currentCoefficient * factor;

            var generated = FaceGenTextureMorpher.BuildNativeDeltaTexture(
                egt, candidateCoefficients,
                FaceGenTextureMorpher.TextureAccumulationMode.EngineTruncated256);
            if (generated == null)
            {
                continue;
            }

            var sweepMetrics = NpcTextureComparison.CompareRgb(
                generated.Pixels, shipped.Pixels,
                generated.Width, generated.Height);
            var mouthMae = GetRegionMae(generated, shipped, "mouth");

            if (sweepMetrics.MeanAbsoluteRgbError < bestMae - 1e-9 ||
                (Math.Abs(sweepMetrics.MeanAbsoluteRgbError - bestMae) <= 1e-9 &&
                 Math.Abs(factor - 1f) < Math.Abs(bestFactor - 1f)))
            {
                bestMae = sweepMetrics.MeanAbsoluteRgbError;
                bestFactor = factor;
                bestCoefficient = candidateCoefficients[morphIndex];
                bestMax = sweepMetrics.MaxAbsoluteRgbError;
                bestMouthMae = mouthMae;
            }
        }

        Console.WriteLine(
            $"  MORPH-SWEEP 0x{appearance.NpcFormId:X8}: " +
            $"[{morphIndex:D2}] currentCoeff={currentCoefficient:F4} currentScale={egt.SymmetricMorphs[morphIndex].Scale:F4} " +
            $"bestFactor={bestFactor:F5} bestCoeff={bestCoefficient:F4} " +
            $"bestMAE={bestMae:F4} (Δ={bestMae - baselineMae:+0.0000;-0.0000}) max={bestMax} " +
            $"mouthMAE={bestMouthMae:F4}");
    }

    private static void DumpMorphInspection(
        NpcAppearance appearance,
        EgtParser egt,
        string egtPath,
        NpcMeshArchiveSet meshArchives,
        float[] currentCoefficients,
        IReadOnlyList<int> morphIndices,
        (float[] R, float[] G, float[] B) currentNative,
        (float[] R, float[] G, float[] B) shippedDecoded)
    {
        InspectCurrentEgtPaths[appearance.NpcFormId] = egtPath;
        var namedRegions = GetNamedRegions(egt.Cols, egt.Rows)
            .ToDictionary(region => region.Name, region => region, StringComparer.OrdinalIgnoreCase);
        var eyes = namedRegions["eyes"];
        var mouth = namedRegions["mouth"];
        var currentRawMae = CompareFloatDeltaRgb(currentNative, shippedDecoded).MeanAbsoluteRgbError;
        var currentEyesRawMae = GetRegionRawMae(currentNative, shippedDecoded, egt.Cols, eyes.X, eyes.Y, eyes.W, eyes.H);
        var currentMouthRawMae = GetRegionRawMae(currentNative, shippedDecoded, egt.Cols, mouth.X, mouth.Y, mouth.W, mouth.H);
        var npcState = new InspectNpcState(
            egt.Cols, egt.Rows, currentNative, shippedDecoded,
            currentRawMae, currentEyesRawMae, currentMouthRawMae,
            new Dictionary<int, InspectMorphState>());
        InspectNpcStates[appearance.NpcFormId] = npcState;

        if (EnableInspectMorphSummaryOnly)
        {
            foreach (var morphIndex in morphIndices.Where(index => index >= 0).Distinct().OrderBy(index => index))
            {
                if (morphIndex >= egt.SymmetricMorphs.Length) continue;
                var morph = egt.SymmetricMorphs[morphIndex];
                var coeff = morphIndex < currentCoefficients.Length ? currentCoefficients[morphIndex] : 0f;
                var coeff256 = (int)(coeff * 256f);
                var scale256 = (int)(morph.Scale * 256f);
                var contributionFactor = coeff256 * scale256 / 65536f;
                npcState.Morphs[morphIndex] = new InspectMorphState(morphIndex, morph, contributionFactor);
                CrossSearchRequiredRows(egt, morphIndex, morph, coeff256, currentNative, shippedDecoded, appearance.NpcFormId);
            }
            return;
        }

        if (!meshArchives.TryExtractFile(egtPath, out var rawEgtData, out var rawArchivePath))
        {
            Console.WriteLine($"  MORPH-INSPECT 0x{appearance.NpcFormId:X8}: raw EGT extract failed for {egtPath}");
            return;
        }

        var rowStride = AlignTo(egt.Cols, 8);
        var channelSize = rowStride * egt.Rows;
        var morphSize = 4 + (3 * channelSize);

        Console.WriteLine(
            $"  MORPH-INSPECT 0x{appearance.NpcFormId:X8}: source={Path.GetFileName(rawArchivePath)} " +
            $"egt={egtPath} cols={egt.Cols} rows={egt.Rows} rowStride={rowStride}");

        foreach (var morphIndex in morphIndices.Where(index => index >= 0).Distinct().OrderBy(index => index))
        {
            if (morphIndex >= egt.SymmetricMorphs.Length)
            {
                Console.WriteLine($"    [{morphIndex:D2}] skipped: index outside symmetric range ({egt.SymmetricMorphs.Length})");
                continue;
            }

            var morphDataOffset = 64 + (morphIndex * morphSize);
            if (morphDataOffset + morphSize > rawEgtData.Length)
            {
                Console.WriteLine($"    [{morphIndex:D2}] skipped: raw offset 0x{morphDataOffset:X} exceeds file length {rawEgtData.Length}");
                continue;
            }

            var morph = egt.SymmetricMorphs[morphIndex];
            var rawScale = BinaryPrimitives.ReadSingleLittleEndian(rawEgtData.AsSpan(morphDataOffset, 4));
            var coeff = morphIndex < currentCoefficients.Length ? currentCoefficients[morphIndex] : 0f;
            var coeff256 = (int)(coeff * 256f);
            var scale256 = (int)(morph.Scale * 256f);
            var contributionFactor = coeff256 * scale256 / 65536f;
            npcState.Morphs[morphIndex] = new InspectMorphState(morphIndex, morph, contributionFactor);
            var stats = ComputeMorphContributionStats(egt, morph, contributionFactor);
            var residualAlignment = ComputeMorphResidualAlignment(egt, morph, currentNative, shippedDecoded);

            Console.WriteLine(
                $"    [{morphIndex:D2}] coeff={coeff,9:F4} coeff256={coeff256,6} " +
                $"scale={morph.Scale,9:F6} rawScale={rawScale,9:F6} scale256={scale256,6} " +
                $"factor={contributionFactor,10:F6}");
            Console.WriteLine(
                $"         wholeAbsMean=({stats.WholeMeanAbsR:F4}, {stats.WholeMeanAbsG:F4}, {stats.WholeMeanAbsB:F4}) " +
                $"wholeMax=({stats.WholeMaxAbsR:F2}, {stats.WholeMaxAbsG:F2}, {stats.WholeMaxAbsB:F2}) " +
                $"eyesAbsMean={stats.EyesMeanAbsRgb:F4} mouthAbsMean={stats.MouthMeanAbsRgb:F4}");
            Console.WriteLine(
                $"         residualProj256 whole={residualAlignment.WholeProjection256,8:F2} " +
                $"eyes={residualAlignment.EyesProjection256,8:F2} mouth={residualAlignment.MouthProjection256,8:F2} " +
                $"cos whole={residualAlignment.WholeCosine,7:F4} eyes={residualAlignment.EyesCosine,7:F4} mouth={residualAlignment.MouthCosine,7:F4}");

            var contentPlausibility = ComputeMorphContentPlausibility(egt, morph, coeff256, currentNative, shippedDecoded);
            var gainPlausibility = ComputeMorphGainPlausibility(egt, morph, coeff256, currentNative, shippedDecoded);
            var affinePlausibility = ComputeMorphAffinePlausibility(egt, morph, coeff256, currentNative, shippedDecoded);
            var rowSimilarity = ComputeMorphRowSimilarityStats(egt, morph, coeff256, currentNative, shippedDecoded);
            var nearestOtherRow = ComputeMorphNearestOtherRowStats(egt, morphIndex, morph, coeff256, currentNative, shippedDecoded);
            var nearestOtherRowRgb = ComputeMorphNearestOtherRowPerChannelStats(egt, morphIndex, morph, coeff256, currentNative, shippedDecoded);

            if (contentPlausibility != null)
            {
                Console.WriteLine(
                    $"         rowBacksolve factor={contentPlausibility.Factor,10:F6} inRange={contentPlausibility.InRangePercent,6:F1}% " +
                    $"mean|Δrow|={contentPlausibility.MeanAbsRequiredByteDelta,7:F2} max|Δrow|={contentPlausibility.MaxAbsRequiredByteDelta,7:F2} " +
                    $"meanClip={contentPlausibility.MeanAbsClipByte,7:F2} maxClip={contentPlausibility.MaxAbsClipByte,7:F2}");
                Console.WriteLine(
                    $"         rowClampRawMAE={contentPlausibility.CorrectedRawMetrics.MeanAbsoluteRgbError:F4} " +
                    $"(Δ={contentPlausibility.CorrectedRawMetrics.MeanAbsoluteRgbError - currentRawMae:+0.0000;-0.0000}) " +
                    $"eyesRawMAE={contentPlausibility.CorrectedEyesRawMae:F4} (Δ={contentPlausibility.CorrectedEyesRawMae - currentEyesRawMae:+0.0000;-0.0000}) " +
                    $"mouthRawMAE={contentPlausibility.CorrectedMouthRawMae:F4} (Δ={contentPlausibility.CorrectedMouthRawMae - currentMouthRawMae:+0.0000;-0.0000})");
            }
            if (gainPlausibility != null)
            {
                Console.WriteLine(
                    $"         gainFit gain={gainPlausibility.Gain,11:F6} inRange={gainPlausibility.InRangePercent,6:F1}% " +
                    $"mean|Δrow|={gainPlausibility.MeanAbsByteDelta,7:F2} max|Δrow|={gainPlausibility.MaxAbsByteDelta,7:F2} " +
                    $"meanClip={gainPlausibility.MeanAbsClipByte,7:F2} maxClip={gainPlausibility.MaxAbsClipByte,7:F2}");
                Console.WriteLine(
                    $"         gainRawMAE={gainPlausibility.CorrectedRawMetrics.MeanAbsoluteRgbError:F4} " +
                    $"(Δ={gainPlausibility.CorrectedRawMetrics.MeanAbsoluteRgbError - currentRawMae:+0.0000;-0.0000}) " +
                    $"eyesRawMAE={gainPlausibility.CorrectedEyesRawMae:F4} (Δ={gainPlausibility.CorrectedEyesRawMae - currentEyesRawMae:+0.0000;-0.0000}) " +
                    $"mouthRawMAE={gainPlausibility.CorrectedMouthRawMae:F4} (Δ={gainPlausibility.CorrectedMouthRawMae - currentMouthRawMae:+0.0000;-0.0000})");
            }
            if (affinePlausibility != null)
            {
                Console.WriteLine(
                    $"         affineFit a={affinePlausibility.Scale,11:F6} b={affinePlausibility.Bias,8:F3} " +
                    $"inRange={affinePlausibility.InRangePercent,6:F1}% mean|Δrow|={affinePlausibility.MeanAbsByteDelta,7:F2} " +
                    $"max|Δrow|={affinePlausibility.MaxAbsByteDelta,7:F2} meanClip={affinePlausibility.MeanAbsClipByte,7:F2} maxClip={affinePlausibility.MaxAbsClipByte,7:F2}");
                Console.WriteLine(
                    $"         affineRawMAE={affinePlausibility.CorrectedRawMetrics.MeanAbsoluteRgbError:F4} " +
                    $"(Δ={affinePlausibility.CorrectedRawMetrics.MeanAbsoluteRgbError - currentRawMae:+0.0000;-0.0000}) " +
                    $"eyesRawMAE={affinePlausibility.CorrectedEyesRawMae:F4} (Δ={affinePlausibility.CorrectedEyesRawMae - currentEyesRawMae:+0.0000;-0.0000}) " +
                    $"mouthRawMAE={affinePlausibility.CorrectedMouthRawMae:F4} (Δ={affinePlausibility.CorrectedMouthRawMae - currentMouthRawMae:+0.0000;-0.0000})");
            }
            if (rowSimilarity != null)
            {
                Console.WriteLine(
                    $"         rowSpace cos={rowSimilarity.Cosine,7:F4} corr={rowSimilarity.Correlation,7:F4} " +
                    $"targetMAE={rowSimilarity.TargetMae,7:F2} gainFitMAE={rowSimilarity.GainFitMae,7:F2} " +
                    $"affineFitMAE={rowSimilarity.AffineFitMae,7:F2} gainExpl={rowSimilarity.GainExplainedPercent,6:F1}% " +
                    $"affExpl={rowSimilarity.AffineExplainedPercent,6:F1}%");
            }
            if (rowSimilarity != null && nearestOtherRow != null)
            {
                var affineVsSelfPercent = rowSimilarity.AffineFitMae <= 1e-9
                    ? 0d : Math.Max(0d, 100d * (1d - (nearestOtherRow.Stats.AffineFitMae / rowSimilarity.AffineFitMae)));
                Console.WriteLine(
                    $"         rowNearest other=[{nearestOtherRow.MorphIndex:D2}] cos={nearestOtherRow.Stats.Cosine,7:F4} " +
                    $"corr={nearestOtherRow.Stats.Correlation,7:F4} affineFitMAE={nearestOtherRow.Stats.AffineFitMae,7:F2} " +
                    $"affExpl={nearestOtherRow.Stats.AffineExplainedPercent,6:F1}% vsSelf={affineVsSelfPercent,6:F1}% " +
                    $"a={nearestOtherRow.Stats.AffineScale,8:F3} b={nearestOtherRow.Stats.AffineBias,8:F3}");
            }
            if (rowSimilarity != null && nearestOtherRowRgb != null)
            {
                var mixVsSelfPercent = rowSimilarity.AffineFitMae <= 1e-9
                    ? 0d : Math.Max(0d, 100d * (1d - (nearestOtherRowRgb.MixedStats.AffineFitMae / rowSimilarity.AffineFitMae)));
                var mixVsWholePercent = nearestOtherRow == null || nearestOtherRow.Stats.AffineFitMae <= 1e-9
                    ? 0d : Math.Max(0d, 100d * (1d - (nearestOtherRowRgb.MixedStats.AffineFitMae / nearestOtherRow.Stats.AffineFitMae)));
                var split = new HashSet<int>
                {
                    nearestOtherRowRgb.Red.MorphIndex,
                    nearestOtherRowRgb.Green.MorphIndex,
                    nearestOtherRowRgb.Blue.MorphIndex
                }.Count;
                Console.WriteLine(
                    $"         rowNearestRGB " +
                    $"R=[{nearestOtherRowRgb.Red.MorphIndex:D2}] mae={nearestOtherRowRgb.Red.Stats.AffineFitMae,6:F2} vsSelf={nearestOtherRowRgb.Red.VsSelfPercent,5:F1}% | " +
                    $"G=[{nearestOtherRowRgb.Green.MorphIndex:D2}] mae={nearestOtherRowRgb.Green.Stats.AffineFitMae,6:F2} vsSelf={nearestOtherRowRgb.Green.VsSelfPercent,5:F1}% | " +
                    $"B=[{nearestOtherRowRgb.Blue.MorphIndex:D2}] mae={nearestOtherRowRgb.Blue.Stats.AffineFitMae,6:F2} vsSelf={nearestOtherRowRgb.Blue.VsSelfPercent,5:F1}% | " +
                    $"mixAffineMAE={nearestOtherRowRgb.MixedStats.AffineFitMae,6:F2} mixVsSelf={mixVsSelfPercent,5:F1}% " +
                    $"vsWhole={mixVsWholePercent,5:F1}% split={split}");
            }

            var rOffset = morphDataOffset + 4;
            var gOffset = rOffset + channelSize;
            var bOffset = gOffset + channelSize;
            DumpMorphChannelInspection("R", rawEgtData, rOffset, rowStride, egt.Cols, egt.Rows, morph.DeltaR);
            DumpMorphChannelInspection("G", rawEgtData, gOffset, rowStride, egt.Cols, egt.Rows, morph.DeltaG);
            DumpMorphChannelInspection("B", rawEgtData, bOffset, rowStride, egt.Cols, egt.Rows, morph.DeltaB);

            CrossSearchRequiredRows(egt, morphIndex, morph, coeff256, currentNative, shippedDecoded, appearance.NpcFormId);
        }
    }

    private static void CrossSearchRequiredRows(
        EgtParser egt,
        int sourceMorphIndex,
        EgtMorph sourceMorph,
        int current256,
        (float[] R, float[] G, float[] B) currentNative,
        (float[] R, float[] G, float[] B) shippedDecoded,
        uint npcFormId)
    {
        var scale256 = (int)(sourceMorph.Scale * 256f);
        var factor = current256 * scale256 / 65536f;
        if (Math.Abs(factor) <= 1e-9f)
        {
            return;
        }

        var pixelCount = egt.Cols * egt.Rows;
        var requiredR = new sbyte[pixelCount];
        var requiredG = new sbyte[pixelCount];
        var requiredB = new sbyte[pixelCount];

        for (var i = 0; i < pixelCount; i++)
        {
            requiredR[i] = (sbyte)Math.Clamp(
                (int)MathF.Round(sourceMorph.DeltaR[i] + ((shippedDecoded.R[i] - currentNative.R[i]) / factor)),
                -128, 127);
            requiredG[i] = (sbyte)Math.Clamp(
                (int)MathF.Round(sourceMorph.DeltaG[i] + ((shippedDecoded.G[i] - currentNative.G[i]) / factor)),
                -128, 127);
            requiredB[i] = (sbyte)Math.Clamp(
                (int)MathF.Round(sourceMorph.DeltaB[i] + ((shippedDecoded.B[i] - currentNative.B[i]) / factor)),
                -128, 127);
        }

        if (!InspectRequiredRows.TryGetValue(npcFormId, out var npcRows))
        {
            npcRows = new Dictionary<int, CrossNpcRequiredRow>();
            InspectRequiredRows[npcFormId] = npcRows;
        }

        npcRows[sourceMorphIndex] = new CrossNpcRequiredRow(sourceMorphIndex, requiredR, requiredG, requiredB);

        if (EnableInspectMorphSummaryOnly)
        {
            return;
        }

        var channelNames = new[] { "R", "G", "B" };
        var requiredChannels = new[] { requiredR, requiredG, requiredB };

        for (var reqCh = 0; reqCh < 3; reqCh++)
        {
            var required = requiredChannels[reqCh];
            var bestMorphIndex = -1;
            var bestChannelIndex = -1;
            var bestMae = double.MaxValue;
            var bestCosine = 0d;
            var bestFlipped = false;

            for (var candidateMorphIdx = 0; candidateMorphIdx < egt.SymmetricMorphs.Length; candidateMorphIdx++)
            {
                var candidate = egt.SymmetricMorphs[candidateMorphIdx];
                var candidateChannels = new[] { candidate.DeltaR, candidate.DeltaG, candidate.DeltaB };

                for (var candCh = 0; candCh < 3; candCh++)
                {
                    var candData = candidateChannels[candCh];
                    CompareRowCandidate(required, candData, pixelCount, egt.Cols, egt.Rows, false,
                        out var mae, out var cosine);
                    if (mae < bestMae)
                    {
                        bestMae = mae; bestCosine = cosine;
                        bestMorphIndex = candidateMorphIdx; bestChannelIndex = candCh; bestFlipped = false;
                    }

                    CompareRowCandidate(required, candData, pixelCount, egt.Cols, egt.Rows, true,
                        out mae, out cosine);
                    if (mae < bestMae)
                    {
                        bestMae = mae; bestCosine = cosine;
                        bestMorphIndex = candidateMorphIdx; bestChannelIndex = candCh; bestFlipped = true;
                    }
                }
            }

            var currentChannel = reqCh switch
            {
                0 => sourceMorph.DeltaR,
                1 => sourceMorph.DeltaG,
                _ => sourceMorph.DeltaB,
            };
            var currentMae = ComputeSbyteMae(required, currentChannel, pixelCount);

            Console.WriteLine(
                $"         crossSearch {channelNames[reqCh]}: " +
                $"best=[{bestMorphIndex:D2}].{channelNames[bestChannelIndex]} " +
                $"mae={bestMae:F2} cos={bestCosine:F4} flip={bestFlipped} " +
                $"currentMae={currentMae:F2} " +
                $"isSelf={bestMorphIndex == sourceMorphIndex && bestChannelIndex == reqCh && !bestFlipped}");
        }
    }

    internal static void PrintCrossNpcRequiredRowSimilaritySummary()
    {
        if (InspectRequiredRows.Count < 2)
        {
            return;
        }

        var orderedNpcIds = InspectRequiredRows.Keys.OrderBy(id => id).ToArray();
        for (var leftIndex = 0; leftIndex < orderedNpcIds.Length; leftIndex++)
        {
            var leftNpcId = orderedNpcIds[leftIndex];
            var leftRows = InspectRequiredRows[leftNpcId];

            for (var rightIndex = leftIndex + 1; rightIndex < orderedNpcIds.Length; rightIndex++)
            {
                var rightNpcId = orderedNpcIds[rightIndex];
                var rightRows = InspectRequiredRows[rightNpcId];

                Console.WriteLine($"  TARGETROW-XNPC 0x{leftNpcId:X8} -> 0x{rightNpcId:X8}:");

                foreach (var sourceRow in leftRows.Values.OrderBy(row => row.MorphIndex))
                {
                    CrossNpcRequiredRowSimilarity? sameStats = null;
                    if (rightRows.TryGetValue(sourceRow.MorphIndex, out var sameTarget))
                    {
                        sameStats = ComputeCrossNpcRequiredRowSimilarity(sourceRow, sameTarget);
                    }

                    CrossNpcRequiredRow? bestTarget = null;
                    CrossNpcRequiredRowSimilarity? bestStats = null;
                    foreach (var candidate in rightRows.Values)
                    {
                        var candidateStats = ComputeCrossNpcRequiredRowSimilarity(sourceRow, candidate);
                        if (bestStats == null || candidateStats.AffineFitMae < bestStats.AffineFitMae)
                        {
                            bestTarget = candidate;
                            bestStats = candidateStats;
                        }
                    }

                    if (bestTarget == null || bestStats == null) continue;

                    var vsSame = sameStats == null || sameStats.AffineFitMae <= 1e-9
                        ? 0d : Math.Max(0d, 100d * (1d - (bestStats.AffineFitMae / sameStats.AffineFitMae)));
                    var sameText = sameStats == null
                        ? "same=[--] unavailable"
                        : $"same=[{sourceRow.MorphIndex:D2}] cos={sameStats.Cosine,7:F4} corr={sameStats.Correlation,7:F4} " +
                          $"mae={sameStats.MeanAbsoluteDifference,7:F2} affineMAE={sameStats.AffineFitMae,7:F2}";

                    Console.WriteLine(
                        $"    [{sourceRow.MorphIndex:D2}] {sameText} | " +
                        $"nearest=[{bestTarget.MorphIndex:D2}] cos={bestStats.Cosine,7:F4} corr={bestStats.Correlation,7:F4} " +
                        $"mae={bestStats.MeanAbsoluteDifference,7:F2} affineMAE={bestStats.AffineFitMae,7:F2} vsSame={vsSame,6:F1}%");
                }
            }
        }
    }

    internal static void PrintExternalHeadEgtRequiredRowSummary(
        NpcMeshArchiveSet meshArchives,
        Dictionary<string, EgtParser?> egtCache)
    {
        if (InspectRequiredRows.Count == 0) return;

        var excludedPaths = InspectCurrentEgtPaths.Values
            .Select(NormalizeArchiveVirtualPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidatePaths = EnumerateExternalHeadEgtPaths(meshArchives)
            .Where(path => !excludedPaths.Contains(path))
            .ToArray();
        if (candidatePaths.Length == 0)
        {
            Console.WriteLine("  TARGETROW-EGTEXT: no external head EGT candidates");
            return;
        }

        var candidates = new List<ExternalHeadEgtCandidate>(candidatePaths.Length);
        foreach (var candidatePath in candidatePaths)
        {
            if (!egtCache.TryGetValue(candidatePath, out var candidateEgt))
            {
                candidateEgt = NpcMeshHelpers.LoadEgtFromBsa(candidatePath, meshArchives);
                egtCache[candidatePath] = candidateEgt;
            }
            if (candidateEgt == null || candidateEgt.SymmetricMorphs.Length == 0) continue;
            candidates.Add(new ExternalHeadEgtCandidate(candidatePath, candidateEgt));
        }

        if (candidates.Count == 0)
        {
            Console.WriteLine("  TARGETROW-EGTEXT: external head EGT candidates failed to load");
            return;
        }

        Console.WriteLine($"  TARGETROW-EGTEXT candidates={candidates.Count} excluded={excludedPaths.Count}:");

        var sharedBlend2Fit = default(ExternalDonorBlendFit);
        var sharedBlend2BiasFit = default(ExternalDonorBlendFit);
        var sharedBlendMorphIndex = 37;
        var sharedBlendRows = InspectRequiredRows.Values
            .SelectMany(rows => rows.Values)
            .Where(row => row.MorphIndex == sharedBlendMorphIndex)
            .ToArray();
        var sharedBlendDonor37 = FindExternalHeadEgtCandidateByFileName(candidates, "headchildfemale.egt");
        var sharedBlendDonor41 = FindExternalHeadEgtCandidateByFileName(candidates, "headchild.egt");
        if (sharedBlendRows.Length >= 2 &&
            sharedBlendDonor37 != null && sharedBlendDonor41 != null &&
            sharedBlendMorphIndex >= 0 && sharedBlendMorphIndex < sharedBlendDonor37.Egt.SymmetricMorphs.Length &&
            41 < sharedBlendDonor41.Egt.SymmetricMorphs.Length)
        {
            sharedBlend2Fit = FitExternalDonorBlendRows(
                sharedBlendRows, sharedBlendDonor37.Egt.SymmetricMorphs[sharedBlendMorphIndex],
                sharedBlendDonor41.Egt.SymmetricMorphs[41], includeBias: false);
            sharedBlend2BiasFit = FitExternalDonorBlendRows(
                sharedBlendRows, sharedBlendDonor37.Egt.SymmetricMorphs[sharedBlendMorphIndex],
                sharedBlendDonor41.Egt.SymmetricMorphs[41], includeBias: true);
        }

        foreach (var npcFormId in InspectRequiredRows.Keys.OrderBy(id => id))
        {
            if (!InspectNpcStates.TryGetValue(npcFormId, out var npcState)) continue;
            var currentPath = InspectCurrentEgtPaths.TryGetValue(npcFormId, out var currentEgtPath) ? currentEgtPath : "<unknown>";
            Console.WriteLine($"    0x{npcFormId:X8} current={currentPath}");

            foreach (var sourceRow in InspectRequiredRows[npcFormId].Values.OrderBy(row => row.MorphIndex))
            {
                npcState.Morphs.TryGetValue(sourceRow.MorphIndex, out var sourceMorphState);
                var best37 = FindBestExternalHeadEgtRowMatch(sourceRow, candidates, 37);
                var bestLate = FindBestExternalHeadEgtRowMatch(sourceRow, candidates, LateHotspotFamilyIndices);
                var best37Apply = sourceMorphState == null || best37 == null ? null : ComputeExternalDonorApplyStats(npcState, sourceMorphState, best37.Morph);
                var bestLateApply = sourceMorphState == null || bestLate == null ? null : ComputeExternalDonorApplyStats(npcState, sourceMorphState, bestLate.Morph);
                var blend2 = sourceMorphState == null || best37 == null || bestLate == null ? null
                    : ComputeExternalDonorBlendStats(npcState, sourceMorphState, sourceRow, best37, bestLate, includeBias: false);
                var blend2Bias = sourceMorphState == null || best37 == null || bestLate == null ? null
                    : ComputeExternalDonorBlendStats(npcState, sourceMorphState, sourceRow, best37, bestLate, includeBias: true);
                var sharedBlend2 = sourceMorphState == null || sourceRow.MorphIndex != sharedBlendMorphIndex || sharedBlend2Fit == null ? null
                    : ComputeExternalDonorBlendApplyStats(npcState, sourceMorphState, sharedBlend2Fit);
                var sharedBlend2Bias = sourceMorphState == null || sourceRow.MorphIndex != sharedBlendMorphIndex || sharedBlend2BiasFit == null ? null
                    : ComputeExternalDonorBlendApplyStats(npcState, sourceMorphState, sharedBlend2BiasFit);

                var best37Text = best37 == null ? "best37=none"
                    : $"best37={best37.Path}[{best37.MorphIndex:D2}] cos={best37.Stats.Cosine,7:F4} corr={best37.Stats.Correlation,7:F4} affineMAE={best37.Stats.AffineFitMae,7:F2}";
                var bestLateText = bestLate == null ? "bestLate=none"
                    : $"bestLate={bestLate.Path}[{bestLate.MorphIndex:D2}] cos={bestLate.Stats.Cosine,7:F4} corr={bestLate.Stats.Correlation,7:F4} affineMAE={bestLate.Stats.AffineFitMae,7:F2}";
                var vs37 = best37 == null || best37.Stats.AffineFitMae <= 1e-9 || bestLate == null
                    ? 0d : Math.Max(0d, 100d * (1d - (bestLate.Stats.AffineFitMae / best37.Stats.AffineFitMae)));

                Console.WriteLine($"      [{sourceRow.MorphIndex:D2}] {best37Text} | {bestLateText} vs37={vs37,6:F1}%");
                if (best37Apply != null)
                    Console.WriteLine($"           apply37 rawMAE={best37Apply.RawMetrics.MeanAbsoluteRgbError:F4} (Δ={best37Apply.RawMetrics.MeanAbsoluteRgbError - npcState.CurrentRawMae:+0.0000;-0.0000}) eyes={best37Apply.EyesRawMae:F4} (Δ={best37Apply.EyesRawMae - npcState.CurrentEyesRawMae:+0.0000;-0.0000}) mouth={best37Apply.MouthRawMae:F4} (Δ={best37Apply.MouthRawMae - npcState.CurrentMouthRawMae:+0.0000;-0.0000})");
                if (bestLateApply != null)
                    Console.WriteLine($"           applyLate rawMAE={bestLateApply.RawMetrics.MeanAbsoluteRgbError:F4} (Δ={bestLateApply.RawMetrics.MeanAbsoluteRgbError - npcState.CurrentRawMae:+0.0000;-0.0000}) eyes={bestLateApply.EyesRawMae:F4} (Δ={bestLateApply.EyesRawMae - npcState.CurrentEyesRawMae:+0.0000;-0.0000}) mouth={bestLateApply.MouthRawMae:F4} (Δ={bestLateApply.MouthRawMae - npcState.CurrentMouthRawMae:+0.0000;-0.0000})");
                if (blend2 != null)
                {
                    Console.WriteLine($"           blend2 a={blend2.CoefficientA,7:F4} b={blend2.CoefficientB,7:F4} rowMAE={blend2.RowMae,7:F2}");
                    Console.WriteLine($"           applyBlend2 rawMAE={blend2.ApplyStats.RawMetrics.MeanAbsoluteRgbError:F4} (Δ={blend2.ApplyStats.RawMetrics.MeanAbsoluteRgbError - npcState.CurrentRawMae:+0.0000;-0.0000}) eyes={blend2.ApplyStats.EyesRawMae:F4} (Δ={blend2.ApplyStats.EyesRawMae - npcState.CurrentEyesRawMae:+0.0000;-0.0000}) mouth={blend2.ApplyStats.MouthRawMae:F4} (Δ={blend2.ApplyStats.MouthRawMae - npcState.CurrentMouthRawMae:+0.0000;-0.0000})");
                }
                if (blend2Bias != null)
                {
                    Console.WriteLine($"           blend2b a={blend2Bias.CoefficientA,7:F4} b={blend2Bias.CoefficientB,7:F4} bias={blend2Bias.Bias,7:F4} rowMAE={blend2Bias.RowMae,7:F2}");
                    Console.WriteLine($"           applyBlend2b rawMAE={blend2Bias.ApplyStats.RawMetrics.MeanAbsoluteRgbError:F4} (Δ={blend2Bias.ApplyStats.RawMetrics.MeanAbsoluteRgbError - npcState.CurrentRawMae:+0.0000;-0.0000}) eyes={blend2Bias.ApplyStats.EyesRawMae:F4} (Δ={blend2Bias.ApplyStats.EyesRawMae - npcState.CurrentEyesRawMae:+0.0000;-0.0000}) mouth={blend2Bias.ApplyStats.MouthRawMae:F4} (Δ={blend2Bias.ApplyStats.MouthRawMae - npcState.CurrentMouthRawMae:+0.0000;-0.0000})");
                }
                if (sharedBlend2 != null)
                {
                    Console.WriteLine($"           sharedBlend2 a={sharedBlend2.CoefficientA,7:F4} b={sharedBlend2.CoefficientB,7:F4} rowMAE={sharedBlend2.RowMae,7:F2}");
                    Console.WriteLine($"           applySharedBlend2 rawMAE={sharedBlend2.ApplyStats.RawMetrics.MeanAbsoluteRgbError:F4} (Δ={sharedBlend2.ApplyStats.RawMetrics.MeanAbsoluteRgbError - npcState.CurrentRawMae:+0.0000;-0.0000}) eyes={sharedBlend2.ApplyStats.EyesRawMae:F4} (Δ={sharedBlend2.ApplyStats.EyesRawMae - npcState.CurrentEyesRawMae:+0.0000;-0.0000}) mouth={sharedBlend2.ApplyStats.MouthRawMae:F4} (Δ={sharedBlend2.ApplyStats.MouthRawMae - npcState.CurrentMouthRawMae:+0.0000;-0.0000})");
                }
                if (sharedBlend2Bias != null)
                {
                    Console.WriteLine($"           sharedBlend2b a={sharedBlend2Bias.CoefficientA,7:F4} b={sharedBlend2Bias.CoefficientB,7:F4} bias={sharedBlend2Bias.Bias,7:F4} rowMAE={sharedBlend2Bias.RowMae,7:F2}");
                    Console.WriteLine($"           applySharedBlend2b rawMAE={sharedBlend2Bias.ApplyStats.RawMetrics.MeanAbsoluteRgbError:F4} (Δ={sharedBlend2Bias.ApplyStats.RawMetrics.MeanAbsoluteRgbError - npcState.CurrentRawMae:+0.0000;-0.0000}) eyes={sharedBlend2Bias.ApplyStats.EyesRawMae:F4} (Δ={sharedBlend2Bias.ApplyStats.EyesRawMae - npcState.CurrentEyesRawMae:+0.0000;-0.0000}) mouth={sharedBlend2Bias.ApplyStats.MouthRawMae:F4} (Δ={sharedBlend2Bias.ApplyStats.MouthRawMae - npcState.CurrentMouthRawMae:+0.0000;-0.0000})");
                }
            }
        }
    }
}
