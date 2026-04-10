using System.Globalization;
using System.Buffers.Binary;
using static EgtAnalyzer.Verification.LinearAlgebraUtils;
using static EgtAnalyzer.Verification.MorphCorrectionHelpers;
using FalloutXbox360Utils.CLI;
using FalloutXbox360Utils.Core.Formats.Bsa;
using FalloutXbox360Utils.Core.Formats.Dds;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Assets;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Textures;

namespace EgtAnalyzer.Verification;

internal static class NpcFaceGenTextureVerifier
{
    private const string FacemodsRoot = @"textures\characters\facemods\";
    private const int MorphFactorSweepMinStep = 0;
    private const int MorphFactorSweepMaxStep = 56;
    private const int TopMorphSweepCount = 5;
    private const int MorphInspectionRowSampleCount = 16;
    private const int TopResidualProjectionCount = 10;
    private const int TopRegionRawFitCount = 5;
    private const string ExternalBlendPrimaryFileName = "headchildfemale.egt";
    private const int ExternalBlendPrimaryMorphIndex = 37;
    private const string ExternalBlendSecondaryFileName = "headchild.egt";
    private const int ExternalBlendSecondaryMorphIndex = 41;
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
            // DIAGNOSTIC: dump coefficients
            DumpCoefficients(appearance, egt);
            var coeffs = appearance.FaceGenTextureCoeffs ?? [];

            // Test: NPC-only coefficients (no race merge), race-only, and merged
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

            // Channel permutation test: try all 6 orderings of EGT R/G/B channels
            Console.WriteLine($"  CHANNEL-PERMUTATION 0x{appearance.NpcFormId:X8}:");
            (string Label, int Ri, int Gi, int Bi)[] permutations =
            [
                ("RGB", 0, 1, 2), // current assumption
                ("RBG", 0, 2, 1),
                ("GRB", 1, 0, 2),
                ("GBR", 1, 2, 0),
                ("BRG", 2, 0, 1),
                ("BGR", 2, 1, 0)
            ];
            foreach (var (permLabel, ri, gi, bi) in permutations)
            {
                // Build a permuted EGT by swapping channel assignments on each morph
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

            // Compare accumulation/encoding variants to isolate rounding sensitivity.
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

                // Per-region signed analysis
                DumpRegionMetrics(genQuantized, shippedDecodedTexture);

                var nativeBuffers = FaceGenTextureMorpher.BuildNativeDeltaBuffers(
                    egt,
                    coeffs,
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
                            appearance,
                            egt,
                            egtPath,
                            meshArchives,
                            coeffs,
                            InspectMorphIndices,
                            nativeBuffers.Value,
                            shippedDecoded);
                    }

                    if (EnableMorphStructure)
                    {
                        DumpMorphStructureSummary(
                            appearance,
                            egt,
                            coeffs,
                            nativeBuffers.Value,
                            shippedDecoded);
                    }

                    IReadOnlyList<ResidualProjectionRow>? residualProjectionRows = null;

                    if (EnableResidualProjection)
                    {
                        residualProjectionRows = DumpResidualProjectionSummary(
                            appearance,
                            egt,
                            coeffs,
                            nativeBuffers.Value,
                            shippedDecoded);
                    }

                    if (EnableRawDeltaCoefficientFit)
                    {
                        rawDeltaCoefficientFit = DumpRawDeltaCoefficientFit(
                            appearance,
                            egt,
                            coeffs,
                            shippedDecodedTexture,
                            shippedDecoded,
                            rawVsShipped,
                            residualProjectionRows);
                        DumpRegionalRawDeltaFits(
                            appearance,
                            egt,
                            coeffs,
                            genQuantized,
                            shippedDecodedTexture,
                            nativeBuffers.Value,
                            shippedDecoded);
                    }

                    if (ResidualSubspaceIndices is { Length: > 0 })
                    {
                        DumpResidualSubspaceFit(
                            appearance,
                            egt,
                            coeffs,
                            shippedDecodedTexture,
                            nativeBuffers.Value,
                            shippedDecoded,
                            rawVsShipped,
                            ResidualSubspaceIndices);
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

                // Per-morph ablation: remove one morph at a time, measure MAE change
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
                    // Show all morphs with nonzero coefficient, not just large deltas
                    if (MathF.Abs(coeffs[mi]) > 0.01f)
                    {
                        ablationRows.Add(new MorphAblationRow(
                            mi,
                            coeffs[mi],
                            egt.SymmetricMorphs[mi].Scale,
                            ablatedMetrics.MeanAbsoluteRgbError,
                            delta));
                        Console.WriteLine(
                            $"    [{mi:D2}] MAE={ablatedMetrics.MeanAbsoluteRgbError:F4} (Δ={delta:+0.0000;-0.0000}) max={ablatedMetrics.MaxAbsoluteRgbError}  coeff={coeffs[mi]:F4} scale={egt.SymmetricMorphs[mi].Scale:F4}  sR={ablatedSigned.MeanSignedRedError:F3} sG={ablatedSigned.MeanSignedGreenError:F3} sB={ablatedSigned.MeanSignedBlueError:F3}");
                    }
                }

                DumpMorphCoefficientSweep(
                    appearance,
                    egt,
                    coeffs,
                    shippedDecodedTexture,
                    0,
                    fullMae,
                    qMetrics.MaxAbsoluteRgbError,
                    baselineMouthMae);

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
                            appearance,
                            egt,
                            coeffs,
                            shippedDecodedTexture,
                            ablationRow.MorphIndex,
                            fullMae,
                            qMetrics.MaxAbsoluteRgbError,
                            baselineMouthMae);
                    }
                }
            }

            // DXT compression floor: roundtrip our generated texture through BC1
            // to measure the irreducible MAE from DXT block compression
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

                // DXT floor under max saturation — how much error does DXT alone
                // contribute when hue differences are amplified?
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
            generatedTexture.Pixels,
            shippedDecodedTexture.Pixels,
            generatedTexture.Width,
            generatedTexture.Height);
        var affineFit = NpcTextureComparison.FitPerChannelAffineRgb(
            generatedTexture.Pixels,
            shippedDecodedTexture.Pixels,
            generatedTexture.Width,
            generatedTexture.Height);
        var affineFitTexture = DecodedTexture.FromBaseLevel(
            NpcTextureComparison.ApplyPerChannelAffineFit(generatedTexture.Pixels, affineFit),
            generatedTexture.Width,
            generatedTexture.Height);

        var ssim = NpcTextureComparison.ComputeSsim(
            generatedTexture.Pixels,
            shippedDecodedTexture.Pixels,
            generatedTexture.Width,
            generatedTexture.Height);

        var ssimNorm = NpcTextureComparison.ComputeSsim(
            generatedTexture.Pixels,
            shippedDecodedTexture.Pixels,
            generatedTexture.Width,
            generatedTexture.Height,
            true);

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
            generatedTexture.Pixels,
            shippedDecodedTexture.Pixels,
            generatedTexture.Width,
            generatedTexture.Height);
        Console.WriteLine(
            $"  SSIM-MAXSAT 0x{appearance.NpcFormId:X8}: " +
            $"R={ssimSat.SsimRed:F6} G={ssimSat.SsimGreen:F6} B={ssimSat.SsimBlue:F6} " +
            $"rgb_mean={ssimSat.SsimRgbMean:F6}");

        var maxSatMetrics = NpcTextureComparison.CompareRgbMaxSaturation(
            generatedTexture.Pixels,
            shippedDecodedTexture.Pixels,
            generatedTexture.Width,
            generatedTexture.Height);
        Console.WriteLine(
            $"  MAE-MAXSAT 0x{appearance.NpcFormId:X8}: " +
            $"MAE={maxSatMetrics.MeanAbsoluteRgbError:F4} " +
            $"RMSE={maxSatMetrics.RootMeanSquareRgbError:F4} " +
            $"max={maxSatMetrics.MaxAbsoluteRgbError} " +
            $">1={maxSatMetrics.PixelsWithRgbErrorAbove1} " +
            $">4={maxSatMetrics.PixelsWithRgbErrorAbove4} " +
            $">8={maxSatMetrics.PixelsWithRgbErrorAbove8}");

        // Per-region max-saturation MAE breakdown
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

    private static void DumpRegionMetrics(
        DecodedTexture generated,
        DecodedTexture shipped,
        string prefix = "    REGION",
        bool maxSaturation = false)
    {
        foreach (var (name, rx, ry, rw, rh) in GetNamedRegions(generated.Width, generated.Height))
        {
            var genCrop = NpcTextureComparison.Crop(generated, rx, ry, rw, rh);
            var shipCrop = NpcTextureComparison.Crop(shipped, rx, ry, rw, rh);

            if (maxSaturation)
            {
                var unsigned = NpcTextureComparison.CompareRgbMaxSaturation(
                    genCrop.Pixels, shipCrop.Pixels, rw, rh);
                Console.WriteLine(
                    $"{prefix} {name,12}: MAE={unsigned.MeanAbsoluteRgbError:F3} RMSE={unsigned.RootMeanSquareRgbError:F3} max={unsigned.MaxAbsoluteRgbError,3} >4={unsigned.PixelsWithRgbErrorAbove4} >8={unsigned.PixelsWithRgbErrorAbove8}");
            }
            else
            {
                var signed = NpcTextureComparison.CompareSignedRgb(
                    genCrop.Pixels, shipCrop.Pixels, rw, rh);
                var unsigned = NpcTextureComparison.CompareRgb(
                    genCrop.Pixels, shipCrop.Pixels, rw, rh);
                Console.WriteLine(
                    $"{prefix} {name,12}: MAE={unsigned.MeanAbsoluteRgbError:F3} max={unsigned.MaxAbsoluteRgbError,3}  signedR={signed.MeanSignedRedError,7:F3} signedG={signed.MeanSignedGreenError,7:F3} signedB={signed.MeanSignedBlueError,7:F3}  absR={signed.MeanAbsoluteRedError:F3} absG={signed.MeanAbsoluteGreenError:F3} absB={signed.MeanAbsoluteBlueError:F3}");
            }
        }
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
            var factor = step / 32f; // [0.00, 1.75] in 1/32 increments
            var candidateCoefficients = (float[])coefficients.Clone();
            candidateCoefficients[morphIndex] = currentCoefficient * factor;

            var generated = FaceGenTextureMorpher.BuildNativeDeltaTexture(
                egt,
                candidateCoefficients,
                FaceGenTextureMorpher.TextureAccumulationMode.EngineTruncated256);
            if (generated == null)
            {
                continue;
            }

            var metrics = NpcTextureComparison.CompareRgb(
                generated.Pixels,
                shipped.Pixels,
                generated.Width,
                generated.Height);
            var mouthMae = GetRegionMae(generated, shipped, "mouth");

            if (metrics.MeanAbsoluteRgbError < bestMae - 1e-9 ||
                (Math.Abs(metrics.MeanAbsoluteRgbError - bestMae) <= 1e-9 &&
                 Math.Abs(factor - 1f) < Math.Abs(bestFactor - 1f)))
            {
                bestMae = metrics.MeanAbsoluteRgbError;
                bestFactor = factor;
                bestCoefficient = candidateCoefficients[morphIndex];
                bestMax = metrics.MaxAbsoluteRgbError;
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
        var currentEyesRawMae = GetRegionRawMae(
            currentNative,
            shippedDecoded,
            egt.Cols,
            eyes.X,
            eyes.Y,
            eyes.W,
            eyes.H);
        var currentMouthRawMae = GetRegionRawMae(
            currentNative,
            shippedDecoded,
            egt.Cols,
            mouth.X,
            mouth.Y,
            mouth.W,
            mouth.H);
        var npcState = new InspectNpcState(
            egt.Cols,
            egt.Rows,
            currentNative,
            shippedDecoded,
            currentRawMae,
            currentEyesRawMae,
            currentMouthRawMae,
            new Dictionary<int, InspectMorphState>());
        InspectNpcStates[appearance.NpcFormId] = npcState;

        if (EnableInspectMorphSummaryOnly)
        {
            foreach (var morphIndex in morphIndices
                         .Where(index => index >= 0)
                         .Distinct()
                         .OrderBy(index => index))
            {
                if (morphIndex >= egt.SymmetricMorphs.Length)
                {
                    continue;
                }

                var morph = egt.SymmetricMorphs[morphIndex];
                var coeff = morphIndex < currentCoefficients.Length ? currentCoefficients[morphIndex] : 0f;
                var coeff256 = (int)(coeff * 256f);
                var scale256 = (int)(morph.Scale * 256f);
                var contributionFactor = coeff256 * scale256 / 65536f;
                npcState.Morphs[morphIndex] = new InspectMorphState(morphIndex, morph, contributionFactor);

                CrossSearchRequiredRows(
                    egt,
                    morphIndex,
                    morph,
                    coeff256,
                    currentNative,
                    shippedDecoded,
                    appearance.NpcFormId);
            }

            return;
        }

        if (!meshArchives.TryExtractFile(egtPath, out var rawEgtData, out var rawArchivePath))
        {
            Console.WriteLine(
                $"  MORPH-INSPECT 0x{appearance.NpcFormId:X8}: raw EGT extract failed for {egtPath}");
            return;
        }

        var rowStride = AlignTo(egt.Cols, 8);
        var channelSize = rowStride * egt.Rows;
        var morphSize = 4 + (3 * channelSize);

        Console.WriteLine(
            $"  MORPH-INSPECT 0x{appearance.NpcFormId:X8}: source={Path.GetFileName(rawArchivePath)} " +
            $"egt={egtPath} cols={egt.Cols} rows={egt.Rows} rowStride={rowStride}");

        foreach (var morphIndex in morphIndices
                     .Where(index => index >= 0)
                     .Distinct()
                     .OrderBy(index => index))
        {
            if (morphIndex >= egt.SymmetricMorphs.Length)
            {
                Console.WriteLine(
                    $"    [{morphIndex:D2}] skipped: index outside symmetric range ({egt.SymmetricMorphs.Length})");
                continue;
            }

            var morphDataOffset = 64 + (morphIndex * morphSize);
            if (morphDataOffset + morphSize > rawEgtData.Length)
            {
                Console.WriteLine(
                    $"    [{morphIndex:D2}] skipped: raw offset 0x{morphDataOffset:X} exceeds file length {rawEgtData.Length}");
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

            var contentPlausibility = ComputeMorphContentPlausibility(
                egt,
                morph,
                coeff256,
                currentNative,
                shippedDecoded);
            var gainPlausibility = ComputeMorphGainPlausibility(
                egt,
                morph,
                coeff256,
                currentNative,
                shippedDecoded);
            var affinePlausibility = ComputeMorphAffinePlausibility(
                egt,
                morph,
                coeff256,
                currentNative,
                shippedDecoded);
            var rowSimilarity = ComputeMorphRowSimilarityStats(
                egt,
                morph,
                coeff256,
                currentNative,
                shippedDecoded);
            var nearestOtherRow = ComputeMorphNearestOtherRowStats(
                egt,
                morphIndex,
                morph,
                coeff256,
                currentNative,
                shippedDecoded);
            var nearestOtherRowRgb = ComputeMorphNearestOtherRowPerChannelStats(
                egt,
                morphIndex,
                morph,
                coeff256,
                currentNative,
                shippedDecoded);
            if (contentPlausibility != null)
            {
                Console.WriteLine(
                    $"         rowBacksolve factor={contentPlausibility.Factor,10:F6} " +
                    $"inRange={contentPlausibility.InRangePercent,6:F1}% " +
                    $"mean|Δrow|={contentPlausibility.MeanAbsRequiredByteDelta,7:F2} " +
                    $"max|Δrow|={contentPlausibility.MaxAbsRequiredByteDelta,7:F2} " +
                    $"meanClip={contentPlausibility.MeanAbsClipByte,7:F2} " +
                    $"maxClip={contentPlausibility.MaxAbsClipByte,7:F2}");
                Console.WriteLine(
                    $"         rowClampRawMAE={contentPlausibility.CorrectedRawMetrics.MeanAbsoluteRgbError:F4} " +
                    $"(Δ={contentPlausibility.CorrectedRawMetrics.MeanAbsoluteRgbError - currentRawMae:+0.0000;-0.0000}) " +
                    $"eyesRawMAE={contentPlausibility.CorrectedEyesRawMae:F4} " +
                    $"(Δ={contentPlausibility.CorrectedEyesRawMae - currentEyesRawMae:+0.0000;-0.0000}) " +
                    $"mouthRawMAE={contentPlausibility.CorrectedMouthRawMae:F4} " +
                    $"(Δ={contentPlausibility.CorrectedMouthRawMae - currentMouthRawMae:+0.0000;-0.0000})");
            }
            if (gainPlausibility != null)
            {
                Console.WriteLine(
                    $"         gainFit gain={gainPlausibility.Gain,11:F6} " +
                    $"inRange={gainPlausibility.InRangePercent,6:F1}% " +
                    $"mean|Δrow|={gainPlausibility.MeanAbsByteDelta,7:F2} " +
                    $"max|Δrow|={gainPlausibility.MaxAbsByteDelta,7:F2} " +
                    $"meanClip={gainPlausibility.MeanAbsClipByte,7:F2} " +
                    $"maxClip={gainPlausibility.MaxAbsClipByte,7:F2}");
                Console.WriteLine(
                    $"         gainRawMAE={gainPlausibility.CorrectedRawMetrics.MeanAbsoluteRgbError:F4} " +
                    $"(Δ={gainPlausibility.CorrectedRawMetrics.MeanAbsoluteRgbError - currentRawMae:+0.0000;-0.0000}) " +
                    $"eyesRawMAE={gainPlausibility.CorrectedEyesRawMae:F4} " +
                    $"(Δ={gainPlausibility.CorrectedEyesRawMae - currentEyesRawMae:+0.0000;-0.0000}) " +
                    $"mouthRawMAE={gainPlausibility.CorrectedMouthRawMae:F4} " +
                    $"(Δ={gainPlausibility.CorrectedMouthRawMae - currentMouthRawMae:+0.0000;-0.0000})");
            }
            if (affinePlausibility != null)
            {
                Console.WriteLine(
                    $"         affineFit a={affinePlausibility.Scale,11:F6} " +
                    $"b={affinePlausibility.Bias,8:F3} " +
                    $"inRange={affinePlausibility.InRangePercent,6:F1}% " +
                    $"mean|Δrow|={affinePlausibility.MeanAbsByteDelta,7:F2} " +
                    $"max|Δrow|={affinePlausibility.MaxAbsByteDelta,7:F2} " +
                    $"meanClip={affinePlausibility.MeanAbsClipByte,7:F2} " +
                    $"maxClip={affinePlausibility.MaxAbsClipByte,7:F2}");
                Console.WriteLine(
                    $"         affineRawMAE={affinePlausibility.CorrectedRawMetrics.MeanAbsoluteRgbError:F4} " +
                    $"(Δ={affinePlausibility.CorrectedRawMetrics.MeanAbsoluteRgbError - currentRawMae:+0.0000;-0.0000}) " +
                    $"eyesRawMAE={affinePlausibility.CorrectedEyesRawMae:F4} " +
                    $"(Δ={affinePlausibility.CorrectedEyesRawMae - currentEyesRawMae:+0.0000;-0.0000}) " +
                    $"mouthRawMAE={affinePlausibility.CorrectedMouthRawMae:F4} " +
                    $"(Δ={affinePlausibility.CorrectedMouthRawMae - currentMouthRawMae:+0.0000;-0.0000})");
            }
            if (rowSimilarity != null)
            {
                Console.WriteLine(
                    $"         rowSpace cos={rowSimilarity.Cosine,7:F4} " +
                    $"corr={rowSimilarity.Correlation,7:F4} " +
                    $"targetMAE={rowSimilarity.TargetMae,7:F2} " +
                    $"gainFitMAE={rowSimilarity.GainFitMae,7:F2} " +
                    $"affineFitMAE={rowSimilarity.AffineFitMae,7:F2} " +
                    $"gainExpl={rowSimilarity.GainExplainedPercent,6:F1}% " +
                    $"affExpl={rowSimilarity.AffineExplainedPercent,6:F1}%");
            }
            if (rowSimilarity != null && nearestOtherRow != null)
            {
                var affineVsSelfPercent = rowSimilarity.AffineFitMae <= 1e-9
                    ? 0d
                    : Math.Max(0d, 100d * (1d - (nearestOtherRow.Stats.AffineFitMae / rowSimilarity.AffineFitMae)));
                Console.WriteLine(
                    $"         rowNearest other=[{nearestOtherRow.MorphIndex:D2}] " +
                    $"cos={nearestOtherRow.Stats.Cosine,7:F4} " +
                    $"corr={nearestOtherRow.Stats.Correlation,7:F4} " +
                    $"affineFitMAE={nearestOtherRow.Stats.AffineFitMae,7:F2} " +
                    $"affExpl={nearestOtherRow.Stats.AffineExplainedPercent,6:F1}% " +
                    $"vsSelf={affineVsSelfPercent,6:F1}% " +
                    $"a={nearestOtherRow.Stats.AffineScale,8:F3} " +
                    $"b={nearestOtherRow.Stats.AffineBias,8:F3}");
            }
            if (rowSimilarity != null && nearestOtherRowRgb != null)
            {
                var mixVsSelfPercent = rowSimilarity.AffineFitMae <= 1e-9
                    ? 0d
                    : Math.Max(0d, 100d * (1d - (nearestOtherRowRgb.MixedStats.AffineFitMae / rowSimilarity.AffineFitMae)));
                var mixVsWholePercent = nearestOtherRow == null || nearestOtherRow.Stats.AffineFitMae <= 1e-9
                    ? 0d
                    : Math.Max(0d, 100d * (1d - (nearestOtherRowRgb.MixedStats.AffineFitMae / nearestOtherRow.Stats.AffineFitMae)));
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

            CrossSearchRequiredRows(
                egt,
                morphIndex,
                morph,
                coeff256,
                currentNative,
                shippedDecoded,
                appearance.NpcFormId);
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

        npcRows[sourceMorphIndex] = new CrossNpcRequiredRow(
            sourceMorphIndex,
            requiredR,
            requiredG,
            requiredB);

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
                        bestMae = mae;
                        bestCosine = cosine;
                        bestMorphIndex = candidateMorphIdx;
                        bestChannelIndex = candCh;
                        bestFlipped = false;
                    }

                    CompareRowCandidate(required, candData, pixelCount, egt.Cols, egt.Rows, true,
                        out mae, out cosine);
                    if (mae < bestMae)
                    {
                        bestMae = mae;
                        bestCosine = cosine;
                        bestMorphIndex = candidateMorphIdx;
                        bestChannelIndex = candCh;
                        bestFlipped = true;
                    }
                }
            }

            var selfMae = ComputeSbyteMae(required, requiredChannels[reqCh], pixelCount);
            _ = selfMae; // self is always 0 since we're comparing to ourselves

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

    private static void CompareRowCandidate(
        sbyte[] required,
        sbyte[] candidate,
        int pixelCount,
        int cols,
        int rows,
        bool flipCandidate,
        out double mae,
        out double cosine)
    {
        double sumAbsDiff = 0d;
        double sumXY = 0d;
        double sumXX = 0d;
        double sumYY = 0d;

        for (var row = 0; row < rows; row++)
        {
            var candidateRow = flipCandidate ? (rows - 1 - row) : row;
            for (var col = 0; col < cols; col++)
            {
                var reqVal = (double)required[row * cols + col];
                var candVal = (double)candidate[candidateRow * cols + col];
                sumAbsDiff += Math.Abs(reqVal - candVal);
                sumXY += reqVal * candVal;
                sumXX += reqVal * reqVal;
                sumYY += candVal * candVal;
            }
        }

        mae = sumAbsDiff / pixelCount;
        cosine = (Math.Abs(sumXX) <= 1e-12 || Math.Abs(sumYY) <= 1e-12)
            ? 0d
            : sumXY / Math.Sqrt(sumXX * sumYY);
    }

    private static double ComputeSbyteMae(sbyte[] a, sbyte[] b, int count)
    {
        double sum = 0d;
        for (var i = 0; i < count; i++)
        {
            sum += Math.Abs(a[i] - b[i]);
        }

        return sum / count;
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

                Console.WriteLine(
                    $"  TARGETROW-XNPC 0x{leftNpcId:X8} -> 0x{rightNpcId:X8}:");

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

                    if (bestTarget == null || bestStats == null)
                    {
                        continue;
                    }

                    var vsSame = sameStats == null || sameStats.AffineFitMae <= 1e-9
                        ? 0d
                        : Math.Max(0d, 100d * (1d - (bestStats.AffineFitMae / sameStats.AffineFitMae)));

                    var sameText = sameStats == null
                        ? "same=[--] unavailable"
                        : $"same=[{sourceRow.MorphIndex:D2}] cos={sameStats.Cosine,7:F4} corr={sameStats.Correlation,7:F4} " +
                          $"mae={sameStats.MeanAbsoluteDifference,7:F2} affineMAE={sameStats.AffineFitMae,7:F2}";

                    Console.WriteLine(
                        $"    [{sourceRow.MorphIndex:D2}] {sameText} | " +
                        $"nearest=[{bestTarget.MorphIndex:D2}] cos={bestStats.Cosine,7:F4} corr={bestStats.Correlation,7:F4} " +
                        $"mae={bestStats.MeanAbsoluteDifference,7:F2} affineMAE={bestStats.AffineFitMae,7:F2} " +
                        $"vsSame={vsSame,6:F1}%");
                }
            }
        }
    }

    internal static void PrintExternalHeadEgtRequiredRowSummary(
        NpcMeshArchiveSet meshArchives,
        Dictionary<string, EgtParser?> egtCache)
    {
        if (InspectRequiredRows.Count == 0)
        {
            return;
        }

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

            if (candidateEgt == null || candidateEgt.SymmetricMorphs.Length == 0)
            {
                continue;
            }

            candidates.Add(new ExternalHeadEgtCandidate(candidatePath, candidateEgt));
        }

        if (candidates.Count == 0)
        {
            Console.WriteLine("  TARGETROW-EGTEXT: external head EGT candidates failed to load");
            return;
        }

        Console.WriteLine(
            $"  TARGETROW-EGTEXT candidates={candidates.Count} excluded={excludedPaths.Count}:");

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
            sharedBlendDonor37 != null &&
            sharedBlendDonor41 != null &&
            sharedBlendMorphIndex >= 0 &&
            sharedBlendMorphIndex < sharedBlendDonor37.Egt.SymmetricMorphs.Length &&
            41 >= 0 &&
            41 < sharedBlendDonor41.Egt.SymmetricMorphs.Length)
        {
            sharedBlend2Fit = FitExternalDonorBlendRows(
                sharedBlendRows,
                sharedBlendDonor37.Egt.SymmetricMorphs[sharedBlendMorphIndex],
                sharedBlendDonor41.Egt.SymmetricMorphs[41],
                includeBias: false);
            sharedBlend2BiasFit = FitExternalDonorBlendRows(
                sharedBlendRows,
                sharedBlendDonor37.Egt.SymmetricMorphs[sharedBlendMorphIndex],
                sharedBlendDonor41.Egt.SymmetricMorphs[41],
                includeBias: true);
        }

        foreach (var npcFormId in InspectRequiredRows.Keys.OrderBy(id => id))
        {
            if (!InspectNpcStates.TryGetValue(npcFormId, out var npcState))
            {
                continue;
            }

            var currentPath = InspectCurrentEgtPaths.TryGetValue(npcFormId, out var currentEgtPath)
                ? currentEgtPath
                : "<unknown>";
            Console.WriteLine(
                $"    0x{npcFormId:X8} current={currentPath}");

            foreach (var sourceRow in InspectRequiredRows[npcFormId].Values.OrderBy(row => row.MorphIndex))
            {
                npcState.Morphs.TryGetValue(sourceRow.MorphIndex, out var sourceMorphState);
                var best37 = FindBestExternalHeadEgtRowMatch(sourceRow, candidates, 37);
                var bestLate = FindBestExternalHeadEgtRowMatch(sourceRow, candidates, LateHotspotFamilyIndices);
                var best37Apply = sourceMorphState == null || best37 == null
                    ? null
                    : ComputeExternalDonorApplyStats(npcState, sourceMorphState, best37.Morph);
                var bestLateApply = sourceMorphState == null || bestLate == null
                    ? null
                    : ComputeExternalDonorApplyStats(npcState, sourceMorphState, bestLate.Morph);
                var blend2 = sourceMorphState == null || best37 == null || bestLate == null
                    ? null
                    : ComputeExternalDonorBlendStats(
                        npcState,
                        sourceMorphState,
                        sourceRow,
                        best37,
                        bestLate,
                        includeBias: false);
                var blend2Bias = sourceMorphState == null || best37 == null || bestLate == null
                    ? null
                    : ComputeExternalDonorBlendStats(
                        npcState,
                        sourceMorphState,
                        sourceRow,
                        best37,
                        bestLate,
                        includeBias: true);
                var sharedBlend2 = sourceMorphState == null ||
                                   sourceRow.MorphIndex != sharedBlendMorphIndex ||
                                   sharedBlend2Fit == null
                    ? null
                    : ComputeExternalDonorBlendApplyStats(
                        npcState,
                        sourceMorphState,
                        sharedBlend2Fit);
                var sharedBlend2Bias = sourceMorphState == null ||
                                       sourceRow.MorphIndex != sharedBlendMorphIndex ||
                                       sharedBlend2BiasFit == null
                    ? null
                    : ComputeExternalDonorBlendApplyStats(
                        npcState,
                        sourceMorphState,
                        sharedBlend2BiasFit);

                var best37Text = best37 == null
                    ? "best37=none"
                    : $"best37={best37.Path}[{best37.MorphIndex:D2}] " +
                      $"cos={best37.Stats.Cosine,7:F4} corr={best37.Stats.Correlation,7:F4} " +
                      $"affineMAE={best37.Stats.AffineFitMae,7:F2}";
                var bestLateText = bestLate == null
                    ? "bestLate=none"
                    : $"bestLate={bestLate.Path}[{bestLate.MorphIndex:D2}] " +
                      $"cos={bestLate.Stats.Cosine,7:F4} corr={bestLate.Stats.Correlation,7:F4} " +
                      $"affineMAE={bestLate.Stats.AffineFitMae,7:F2}";
                var vs37 = best37 == null || best37.Stats.AffineFitMae <= 1e-9 || bestLate == null
                    ? 0d
                    : Math.Max(0d, 100d * (1d - (bestLate.Stats.AffineFitMae / best37.Stats.AffineFitMae)));

                Console.WriteLine(
                    $"      [{sourceRow.MorphIndex:D2}] {best37Text} | {bestLateText} vs37={vs37,6:F1}%");
                if (best37Apply != null)
                {
                    Console.WriteLine(
                        $"           apply37 rawMAE={best37Apply.RawMetrics.MeanAbsoluteRgbError:F4} " +
                        $"(Δ={best37Apply.RawMetrics.MeanAbsoluteRgbError - npcState.CurrentRawMae:+0.0000;-0.0000}) " +
                        $"eyes={best37Apply.EyesRawMae:F4} " +
                        $"(Δ={best37Apply.EyesRawMae - npcState.CurrentEyesRawMae:+0.0000;-0.0000}) " +
                        $"mouth={best37Apply.MouthRawMae:F4} " +
                        $"(Δ={best37Apply.MouthRawMae - npcState.CurrentMouthRawMae:+0.0000;-0.0000})");
                }

                if (bestLateApply != null)
                {
                    Console.WriteLine(
                        $"           applyLate rawMAE={bestLateApply.RawMetrics.MeanAbsoluteRgbError:F4} " +
                        $"(Δ={bestLateApply.RawMetrics.MeanAbsoluteRgbError - npcState.CurrentRawMae:+0.0000;-0.0000}) " +
                        $"eyes={bestLateApply.EyesRawMae:F4} " +
                        $"(Δ={bestLateApply.EyesRawMae - npcState.CurrentEyesRawMae:+0.0000;-0.0000}) " +
                        $"mouth={bestLateApply.MouthRawMae:F4} " +
                        $"(Δ={bestLateApply.MouthRawMae - npcState.CurrentMouthRawMae:+0.0000;-0.0000})");
                }

                if (blend2 != null)
                {
                    Console.WriteLine(
                        $"           blend2 a={blend2.CoefficientA,7:F4} b={blend2.CoefficientB,7:F4} " +
                        $"rowMAE={blend2.RowMae,7:F2}");
                    Console.WriteLine(
                        $"           applyBlend2 rawMAE={blend2.ApplyStats.RawMetrics.MeanAbsoluteRgbError:F4} " +
                        $"(Δ={blend2.ApplyStats.RawMetrics.MeanAbsoluteRgbError - npcState.CurrentRawMae:+0.0000;-0.0000}) " +
                        $"eyes={blend2.ApplyStats.EyesRawMae:F4} " +
                        $"(Δ={blend2.ApplyStats.EyesRawMae - npcState.CurrentEyesRawMae:+0.0000;-0.0000}) " +
                        $"mouth={blend2.ApplyStats.MouthRawMae:F4} " +
                        $"(Δ={blend2.ApplyStats.MouthRawMae - npcState.CurrentMouthRawMae:+0.0000;-0.0000})");
                }

                if (blend2Bias != null)
                {
                    Console.WriteLine(
                        $"           blend2b a={blend2Bias.CoefficientA,7:F4} b={blend2Bias.CoefficientB,7:F4} " +
                        $"bias={blend2Bias.Bias,7:F4} rowMAE={blend2Bias.RowMae,7:F2}");
                    Console.WriteLine(
                        $"           applyBlend2b rawMAE={blend2Bias.ApplyStats.RawMetrics.MeanAbsoluteRgbError:F4} " +
                        $"(Δ={blend2Bias.ApplyStats.RawMetrics.MeanAbsoluteRgbError - npcState.CurrentRawMae:+0.0000;-0.0000}) " +
                        $"eyes={blend2Bias.ApplyStats.EyesRawMae:F4} " +
                        $"(Δ={blend2Bias.ApplyStats.EyesRawMae - npcState.CurrentEyesRawMae:+0.0000;-0.0000}) " +
                        $"mouth={blend2Bias.ApplyStats.MouthRawMae:F4} " +
                        $"(Δ={blend2Bias.ApplyStats.MouthRawMae - npcState.CurrentMouthRawMae:+0.0000;-0.0000})");
                }

                if (sharedBlend2 != null)
                {
                    Console.WriteLine(
                        $"           sharedBlend2 a={sharedBlend2.CoefficientA,7:F4} b={sharedBlend2.CoefficientB,7:F4} " +
                        $"rowMAE={sharedBlend2.RowMae,7:F2}");
                    Console.WriteLine(
                        $"           applySharedBlend2 rawMAE={sharedBlend2.ApplyStats.RawMetrics.MeanAbsoluteRgbError:F4} " +
                        $"(Δ={sharedBlend2.ApplyStats.RawMetrics.MeanAbsoluteRgbError - npcState.CurrentRawMae:+0.0000;-0.0000}) " +
                        $"eyes={sharedBlend2.ApplyStats.EyesRawMae:F4} " +
                        $"(Δ={sharedBlend2.ApplyStats.EyesRawMae - npcState.CurrentEyesRawMae:+0.0000;-0.0000}) " +
                        $"mouth={sharedBlend2.ApplyStats.MouthRawMae:F4} " +
                        $"(Δ={sharedBlend2.ApplyStats.MouthRawMae - npcState.CurrentMouthRawMae:+0.0000;-0.0000})");
                }

                if (sharedBlend2Bias != null)
                {
                    Console.WriteLine(
                        $"           sharedBlend2b a={sharedBlend2Bias.CoefficientA,7:F4} b={sharedBlend2Bias.CoefficientB,7:F4} " +
                        $"bias={sharedBlend2Bias.Bias,7:F4} rowMAE={sharedBlend2Bias.RowMae,7:F2}");
                    Console.WriteLine(
                        $"           applySharedBlend2b rawMAE={sharedBlend2Bias.ApplyStats.RawMetrics.MeanAbsoluteRgbError:F4} " +
                        $"(Δ={sharedBlend2Bias.ApplyStats.RawMetrics.MeanAbsoluteRgbError - npcState.CurrentRawMae:+0.0000;-0.0000}) " +
                        $"eyes={sharedBlend2Bias.ApplyStats.EyesRawMae:F4} " +
                        $"(Δ={sharedBlend2Bias.ApplyStats.EyesRawMae - npcState.CurrentEyesRawMae:+0.0000;-0.0000}) " +
                        $"mouth={sharedBlend2Bias.ApplyStats.MouthRawMae:F4} " +
                        $"(Δ={sharedBlend2Bias.ApplyStats.MouthRawMae - npcState.CurrentMouthRawMae:+0.0000;-0.0000})");
                }
            }
        }
    }

    private static IReadOnlyList<string> EnumerateExternalHeadEgtPaths(NpcMeshArchiveSet meshArchives)
    {
        var discovered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var archivePath in meshArchives.ArchivePaths)
        {
            var archive = BsaParser.Parse(archivePath);
            foreach (var file in archive.AllFiles)
            {
                var normalized = NormalizeArchiveVirtualPath(file.FullPath);
                if (!normalized.EndsWith(".egt", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var fileName = Path.GetFileName(normalized);
                if (fileName?.Contains("head", StringComparison.OrdinalIgnoreCase) != true)
                {
                    continue;
                }

                discovered.Add(normalized);
            }
        }

        return discovered.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static ExternalHeadEgtRowMatch? FindBestExternalHeadEgtRowMatch(
        CrossNpcRequiredRow sourceRow,
        IReadOnlyList<ExternalHeadEgtCandidate> candidates,
        int morphIndex)
    {
        CrossNpcRequiredRowSimilarity? bestStats = null;
        ExternalHeadEgtCandidate? bestCandidate = null;

        foreach (var candidate in candidates)
        {
            if (morphIndex < 0 || morphIndex >= candidate.Egt.SymmetricMorphs.Length)
            {
                continue;
            }

            var candidateRow = CreateCrossNpcRequiredRow(candidate.Path, morphIndex, candidate.Egt.SymmetricMorphs[morphIndex]);
            if (!AreComparableRows(sourceRow, candidateRow))
            {
                continue;
            }

            var stats = ComputeCrossNpcRequiredRowSimilarity(sourceRow, candidateRow);
            if (bestStats == null || stats.AffineFitMae < bestStats.AffineFitMae)
            {
                bestStats = stats;
                bestCandidate = candidate;
            }
        }

        return bestStats == null || bestCandidate == null
            ? null
            : new ExternalHeadEgtRowMatch(
                Path.GetFileName(bestCandidate.Path) ?? bestCandidate.Path,
                bestCandidate.Path,
                morphIndex,
                bestStats,
                bestCandidate.Egt.SymmetricMorphs[morphIndex]);
    }

    private static ExternalHeadEgtRowMatch? FindBestExternalHeadEgtRowMatch(
        CrossNpcRequiredRow sourceRow,
        IReadOnlyList<ExternalHeadEgtCandidate> candidates,
        IReadOnlyList<int> morphIndices)
    {
        ExternalHeadEgtRowMatch? bestMatch = null;

        foreach (var morphIndex in morphIndices)
        {
            var candidateMatch = FindBestExternalHeadEgtRowMatch(sourceRow, candidates, morphIndex);
            if (candidateMatch == null)
            {
                continue;
            }

            if (bestMatch == null || candidateMatch.Stats.AffineFitMae < bestMatch.Stats.AffineFitMae)
            {
                bestMatch = candidateMatch;
            }
        }

        return bestMatch;
    }

    private static CrossNpcRequiredRow CreateCrossNpcRequiredRow(
        string sourcePath,
        int morphIndex,
        EgtMorph morph)
    {
        return new CrossNpcRequiredRow(
            morphIndex,
            morph.DeltaR,
            morph.DeltaG,
            morph.DeltaB,
            sourcePath);
    }

    private static bool AreComparableRows(
        CrossNpcRequiredRow source,
        CrossNpcRequiredRow target)
    {
        return source.RequiredR.Length == target.RequiredR.Length &&
               source.RequiredG.Length == target.RequiredG.Length &&
               source.RequiredB.Length == target.RequiredB.Length;
    }

    private static string NormalizeArchiveVirtualPath(string path)
    {
        return path
            .Replace('/', '\\')
            .TrimStart('\\');
    }

    private static ExternalDonorApplyStats? ComputeExternalDonorApplyStats(
        InspectNpcState npcState,
        InspectMorphState sourceMorphState,
        EgtMorph donorMorph)
    {
        if (Math.Abs(sourceMorphState.Factor) <= 1e-9f)
        {
            return null;
        }

        var pixelCount = npcState.Cols * npcState.Rows;
        if (sourceMorphState.SourceMorph.DeltaR.Length != pixelCount ||
            donorMorph.DeltaR.Length != pixelCount ||
            sourceMorphState.SourceMorph.DeltaG.Length != pixelCount ||
            donorMorph.DeltaG.Length != pixelCount ||
            sourceMorphState.SourceMorph.DeltaB.Length != pixelCount ||
            donorMorph.DeltaB.Length != pixelCount)
        {
            return null;
        }

        var correctedR = new float[pixelCount];
        var correctedG = new float[pixelCount];
        var correctedB = new float[pixelCount];
        for (var pixelIndex = 0; pixelIndex < pixelCount; pixelIndex++)
        {
            correctedR[pixelIndex] = npcState.CurrentNative.R[pixelIndex] +
                ((donorMorph.DeltaR[pixelIndex] - sourceMorphState.SourceMorph.DeltaR[pixelIndex]) * sourceMorphState.Factor);
            correctedG[pixelIndex] = npcState.CurrentNative.G[pixelIndex] +
                ((donorMorph.DeltaG[pixelIndex] - sourceMorphState.SourceMorph.DeltaG[pixelIndex]) * sourceMorphState.Factor);
            correctedB[pixelIndex] = npcState.CurrentNative.B[pixelIndex] +
                ((donorMorph.DeltaB[pixelIndex] - sourceMorphState.SourceMorph.DeltaB[pixelIndex]) * sourceMorphState.Factor);
        }

        var corrected = (correctedR, correctedG, correctedB);
        var rawMetrics = CompareFloatDeltaRgb(corrected, npcState.ShippedDecoded);
        var regions = GetNamedRegions(npcState.Cols, npcState.Rows)
            .ToDictionary(region => region.Name, region => region, StringComparer.OrdinalIgnoreCase);
        var eyes = regions["eyes"];
        var mouth = regions["mouth"];

        return new ExternalDonorApplyStats(
            rawMetrics,
            GetRegionRawMae(corrected, npcState.ShippedDecoded, npcState.Cols, eyes.X, eyes.Y, eyes.W, eyes.H),
            GetRegionRawMae(corrected, npcState.ShippedDecoded, npcState.Cols, mouth.X, mouth.Y, mouth.W, mouth.H));
    }

    private static ExternalDonorBlendStats? ComputeExternalDonorBlendStats(
        InspectNpcState npcState,
        InspectMorphState sourceMorphState,
        CrossNpcRequiredRow sourceRow,
        ExternalHeadEgtRowMatch donorA,
        ExternalHeadEgtRowMatch donorB,
        bool includeBias)
    {
        var fit = FitExternalDonorBlendRow(sourceRow, donorA.Morph, donorB.Morph, includeBias);
        if (fit == null)
        {
            return null;
        }

        var blendedMorph = new EgtMorph
        {
            Scale = sourceMorphState.SourceMorph.Scale,
            DeltaR = fit.DeltaR,
            DeltaG = fit.DeltaG,
            DeltaB = fit.DeltaB
        };
        var applyStats = ComputeExternalDonorApplyStats(npcState, sourceMorphState, blendedMorph);
        return applyStats == null
            ? null
            : new ExternalDonorBlendStats(
                fit.CoefficientA,
                fit.CoefficientB,
                fit.Bias,
                fit.RowMae,
                applyStats);
    }

    private static ExternalDonorBlendStats? ComputeExternalDonorBlendApplyStats(
        InspectNpcState npcState,
        InspectMorphState sourceMorphState,
        ExternalDonorBlendFit fit)
    {
        var blendedMorph = new EgtMorph
        {
            Scale = sourceMorphState.SourceMorph.Scale,
            DeltaR = fit.DeltaR,
            DeltaG = fit.DeltaG,
            DeltaB = fit.DeltaB
        };
        var applyStats = ComputeExternalDonorApplyStats(npcState, sourceMorphState, blendedMorph);
        return applyStats == null
            ? null
            : new ExternalDonorBlendStats(
                fit.CoefficientA,
                fit.CoefficientB,
                fit.Bias,
                fit.RowMae,
                applyStats);
    }

    private static ExternalDonorBlendFit? FitExternalDonorBlendRow(
        CrossNpcRequiredRow sourceRow,
        EgtMorph donorA,
        EgtMorph donorB,
        bool includeBias)
    {
        return FitExternalDonorBlendRows([sourceRow], donorA, donorB, includeBias);
    }

    private static ExternalDonorBlendFit? FitExternalDonorBlendRows(
        IReadOnlyList<CrossNpcRequiredRow> sourceRows,
        EgtMorph donorA,
        EgtMorph donorB,
        bool includeBias)
    {
        if (sourceRows.Count == 0)
        {
            return null;
        }

        var pixelCount = sourceRows[0].RequiredR.Length;
        if (sourceRows[0].RequiredG.Length != pixelCount ||
            sourceRows[0].RequiredB.Length != pixelCount ||
            donorA.DeltaR.Length != pixelCount ||
            donorA.DeltaG.Length != pixelCount ||
            donorA.DeltaB.Length != pixelCount ||
            donorB.DeltaR.Length != pixelCount ||
            donorB.DeltaG.Length != pixelCount ||
            donorB.DeltaB.Length != pixelCount)
        {
            return null;
        }

        double sum11 = 0d;
        double sum12 = 0d;
        double sum22 = 0d;
        double sum1 = 0d;
        double sum2 = 0d;
        double sumY = 0d;
        double sum1Y = 0d;
        double sum2Y = 0d;

        static void AccumulateChannel(
            sbyte[] target,
            sbyte[] left,
            sbyte[] right,
            ref double sum11,
            ref double sum12,
            ref double sum22,
            ref double sum1,
            ref double sum2,
            ref double sumY,
            ref double sum1Y,
            ref double sum2Y)
        {
            for (var i = 0; i < target.Length; i++)
            {
                var x1 = (double)left[i];
                var x2 = (double)right[i];
                var y = (double)target[i];
                sum11 += x1 * x1;
                sum12 += x1 * x2;
                sum22 += x2 * x2;
                sum1 += x1;
                sum2 += x2;
                sumY += y;
                sum1Y += x1 * y;
                sum2Y += x2 * y;
            }
        }

        foreach (var sourceRow in sourceRows)
        {
            if (sourceRow.RequiredR.Length != pixelCount ||
                sourceRow.RequiredG.Length != pixelCount ||
                sourceRow.RequiredB.Length != pixelCount)
            {
                return null;
            }

            AccumulateChannel(sourceRow.RequiredR, donorA.DeltaR, donorB.DeltaR,
                ref sum11, ref sum12, ref sum22, ref sum1, ref sum2, ref sumY, ref sum1Y, ref sum2Y);
            AccumulateChannel(sourceRow.RequiredG, donorA.DeltaG, donorB.DeltaG,
                ref sum11, ref sum12, ref sum22, ref sum1, ref sum2, ref sumY, ref sum1Y, ref sum2Y);
            AccumulateChannel(sourceRow.RequiredB, donorA.DeltaB, donorB.DeltaB,
                ref sum11, ref sum12, ref sum22, ref sum1, ref sum2, ref sumY, ref sum1Y, ref sum2Y);
        }

        var sampleCount = pixelCount * 3 * sourceRows.Count;

        double[] solution;
        if (includeBias)
        {
            if (!TrySolveLinearSystem(
                    new[,]
                    {
                        { sum11, sum12, sum1 },
                        { sum12, sum22, sum2 },
                        { sum1, sum2, sampleCount }
                    },
                    [sum1Y, sum2Y, sumY],
                    out solution))
            {
                return null;
            }
        }
        else
        {
            if (!TrySolveLinearSystem(
                    new[,]
                    {
                        { sum11, sum12 },
                        { sum12, sum22 }
                    },
                    [sum1Y, sum2Y],
                    out solution))
            {
                return null;
            }
        }

        var coefficientA = solution[0];
        var coefficientB = solution[1];
        var bias = includeBias ? solution[2] : 0d;
        var blendedR = new sbyte[pixelCount];
        var blendedG = new sbyte[pixelCount];
        var blendedB = new sbyte[pixelCount];
        double sumAbs = 0d;

        static sbyte BlendSample(double coefficientA, double coefficientB, double bias, sbyte left, sbyte right)
        {
            return (sbyte)Math.Clamp(
                (int)Math.Round((coefficientA * left) + (coefficientB * right) + bias),
                -128,
                127);
        }

        for (var i = 0; i < pixelCount; i++)
        {
            blendedR[i] = BlendSample(coefficientA, coefficientB, bias, donorA.DeltaR[i], donorB.DeltaR[i]);
            blendedG[i] = BlendSample(coefficientA, coefficientB, bias, donorA.DeltaG[i], donorB.DeltaG[i]);
            blendedB[i] = BlendSample(coefficientA, coefficientB, bias, donorA.DeltaB[i], donorB.DeltaB[i]);
        }

        foreach (var sourceRow in sourceRows)
        {
            for (var i = 0; i < pixelCount; i++)
            {
                sumAbs += Math.Abs(blendedR[i] - sourceRow.RequiredR[i]);
                sumAbs += Math.Abs(blendedG[i] - sourceRow.RequiredG[i]);
                sumAbs += Math.Abs(blendedB[i] - sourceRow.RequiredB[i]);
            }
        }

        return new ExternalDonorBlendFit(
            coefficientA,
            coefficientB,
            bias,
            sumAbs / sampleCount,
            blendedR,
            blendedG,
            blendedB);
    }

    private static ExternalHeadEgtCandidate? FindExternalHeadEgtCandidateByFileName(
        IReadOnlyList<ExternalHeadEgtCandidate> candidates,
        string fileName)
    {
        foreach (var candidate in candidates)
        {
            var candidateFileName = Path.GetFileName(candidate.Path);
            if (string.Equals(candidateFileName, fileName, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool TrySolveLinearSystem(
        double[,] matrix,
        double[] rightHandSide,
        out double[] solution)
    {
        var size = rightHandSide.Length;
        solution = new double[size];
        if (matrix.GetLength(0) != size || matrix.GetLength(1) != size)
        {
            return false;
        }

        var workingMatrix = (double[,])matrix.Clone();
        var workingRhs = (double[])rightHandSide.Clone();

        for (var column = 0; column < size; column++)
        {
            var pivotRow = column;
            var pivotAbs = Math.Abs(workingMatrix[pivotRow, column]);
            for (var row = column + 1; row < size; row++)
            {
                var candidateAbs = Math.Abs(workingMatrix[row, column]);
                if (candidateAbs > pivotAbs)
                {
                    pivotAbs = candidateAbs;
                    pivotRow = row;
                }
            }

            if (pivotAbs <= 1e-9)
            {
                return false;
            }

            if (pivotRow != column)
            {
                for (var swapColumn = column; swapColumn < size; swapColumn++)
                {
                    (workingMatrix[column, swapColumn], workingMatrix[pivotRow, swapColumn]) =
                        (workingMatrix[pivotRow, swapColumn], workingMatrix[column, swapColumn]);
                }

                (workingRhs[column], workingRhs[pivotRow]) = (workingRhs[pivotRow], workingRhs[column]);
            }

            var pivot = workingMatrix[column, column];
            for (var row = column + 1; row < size; row++)
            {
                var factor = workingMatrix[row, column] / pivot;
                if (Math.Abs(factor) <= 1e-12)
                {
                    continue;
                }

                workingMatrix[row, column] = 0d;
                for (var eliminationColumn = column + 1; eliminationColumn < size; eliminationColumn++)
                {
                    workingMatrix[row, eliminationColumn] -= factor * workingMatrix[column, eliminationColumn];
                }

                workingRhs[row] -= factor * workingRhs[column];
            }
        }

        for (var row = size - 1; row >= 0; row--)
        {
            var value = workingRhs[row];
            for (var column = row + 1; column < size; column++)
            {
                value -= workingMatrix[row, column] * solution[column];
            }

            var pivot = workingMatrix[row, row];
            if (Math.Abs(pivot) <= 1e-9)
            {
                return false;
            }

            solution[row] = value / pivot;
        }

        return true;
    }

    private static CrossNpcRequiredRowSimilarity ComputeCrossNpcRequiredRowSimilarity(
        CrossNpcRequiredRow source,
        CrossNpcRequiredRow target)
    {
        var channelLength = source.RequiredR.Length;
        var vectorLength = channelLength * 3;

        double dot = 0d;
        double sumSourceSq = 0d;
        double sumTargetSq = 0d;
        double sumSource = 0d;
        double sumTarget = 0d;
        double sumSourceTimesTarget = 0d;
        double sumAbs = 0d;

        static void AccumulateChannel(
            sbyte[] sourceChannel,
            sbyte[] targetChannel,
            ref double dot,
            ref double sumSourceSq,
            ref double sumTargetSq,
            ref double sumSource,
            ref double sumTarget,
            ref double sumSourceTimesTarget,
            ref double sumAbs)
        {
            for (var i = 0; i < sourceChannel.Length; i++)
            {
                var x = (double)sourceChannel[i];
                var y = targetChannel[i];
                dot += x * y;
                sumSourceSq += x * x;
                sumTargetSq += y * y;
                sumSource += x;
                sumTarget += y;
                sumSourceTimesTarget += x * y;
                sumAbs += Math.Abs(y - x);
            }
        }

        AccumulateChannel(source.RequiredR, target.RequiredR, ref dot, ref sumSourceSq, ref sumTargetSq, ref sumSource, ref sumTarget, ref sumSourceTimesTarget, ref sumAbs);
        AccumulateChannel(source.RequiredG, target.RequiredG, ref dot, ref sumSourceSq, ref sumTargetSq, ref sumSource, ref sumTarget, ref sumSourceTimesTarget, ref sumAbs);
        AccumulateChannel(source.RequiredB, target.RequiredB, ref dot, ref sumSourceSq, ref sumTargetSq, ref sumSource, ref sumTarget, ref sumSourceTimesTarget, ref sumAbs);

        var cosine = sumSourceSq <= 1e-12 || sumTargetSq <= 1e-12
            ? 0d
            : dot / Math.Sqrt(sumSourceSq * sumTargetSq);

        var count = (double)vectorLength;
        var meanSource = sumSource / count;
        var meanTarget = sumTarget / count;
        var varianceSource = sumSourceSq - (sumSource * meanSource);
        var varianceTarget = sumTargetSq - (sumTarget * meanTarget);
        var covariance = sumSourceTimesTarget - (sumSource * meanTarget);
        var correlation = varianceSource <= 1e-12 || varianceTarget <= 1e-12
            ? 0d
            : covariance / Math.Sqrt(varianceSource * varianceTarget);

        var meanAbsoluteDifference = sumAbs / count;
        var affineScale = 0d;
        var affineBias = meanTarget;
        var affineFitMae = meanAbsoluteDifference;
        if (Math.Abs(varianceSource) > 1e-12)
        {
            affineScale = covariance / varianceSource;
            affineBias = meanTarget - (affineScale * meanSource);

            double sumAffineAbs = 0d;

            static void AccumulateAffineError(
                sbyte[] sourceChannel,
                sbyte[] targetChannel,
                double affineScale,
                double affineBias,
                ref double sumAffineAbs)
            {
                for (var i = 0; i < sourceChannel.Length; i++)
                {
                    sumAffineAbs += Math.Abs((affineScale * sourceChannel[i]) + affineBias - targetChannel[i]);
                }
            }

            AccumulateAffineError(source.RequiredR, target.RequiredR, affineScale, affineBias, ref sumAffineAbs);
            AccumulateAffineError(source.RequiredG, target.RequiredG, affineScale, affineBias, ref sumAffineAbs);
            AccumulateAffineError(source.RequiredB, target.RequiredB, affineScale, affineBias, ref sumAffineAbs);
            affineFitMae = sumAffineAbs / count;
        }

        return new CrossNpcRequiredRowSimilarity(
            cosine,
            correlation,
            meanAbsoluteDifference,
            affineFitMae,
            affineScale,
            affineBias);
    }

    private static MorphContentPlausibilityStats? ComputeMorphContentPlausibility(
        EgtParser egt,
        EgtMorph morph,
        int current256,
        (float[] R, float[] G, float[] B) currentNative,
        (float[] R, float[] G, float[] B) shippedDecoded)
    {
        var scale256 = (int)(morph.Scale * 256f);
        var factor = current256 * scale256 / 65536f;
        if (Math.Abs(factor) <= 1e-9f)
        {
            return null;
        }

        var pixelCount = egt.Cols * egt.Rows;
        var correctedR = new float[pixelCount];
        var correctedG = new float[pixelCount];
        var correctedB = new float[pixelCount];
        double sumAbsRequiredByteDelta = 0d;
        double sumAbsClipByte = 0d;
        var maxAbsRequiredByteDelta = 0f;
        var maxAbsClipByte = 0f;
        var inRangeCount = 0;
        var sampleCount = pixelCount * 3;

        for (var pixelIndex = 0; pixelIndex < pixelCount; pixelIndex++)
        {
            var residualR = shippedDecoded.R[pixelIndex] - currentNative.R[pixelIndex];
            var residualG = shippedDecoded.G[pixelIndex] - currentNative.G[pixelIndex];
            var residualB = shippedDecoded.B[pixelIndex] - currentNative.B[pixelIndex];

            ApplyMorphContentCorrection(
                morph.DeltaR[pixelIndex],
                residualR,
                factor,
                out correctedR[pixelIndex],
                currentNative.R[pixelIndex],
                ref sumAbsRequiredByteDelta,
                ref sumAbsClipByte,
                ref maxAbsRequiredByteDelta,
                ref maxAbsClipByte,
                ref inRangeCount);
            ApplyMorphContentCorrection(
                morph.DeltaG[pixelIndex],
                residualG,
                factor,
                out correctedG[pixelIndex],
                currentNative.G[pixelIndex],
                ref sumAbsRequiredByteDelta,
                ref sumAbsClipByte,
                ref maxAbsRequiredByteDelta,
                ref maxAbsClipByte,
                ref inRangeCount);
            ApplyMorphContentCorrection(
                morph.DeltaB[pixelIndex],
                residualB,
                factor,
                out correctedB[pixelIndex],
                currentNative.B[pixelIndex],
                ref sumAbsRequiredByteDelta,
                ref sumAbsClipByte,
                ref maxAbsRequiredByteDelta,
                ref maxAbsClipByte,
                ref inRangeCount);
        }

        var correctedRawMetrics = CompareFloatDeltaRgb(
            (correctedR, correctedG, correctedB),
            shippedDecoded);
        var regions = GetNamedRegions(egt.Cols, egt.Rows)
            .ToDictionary(region => region.Name, region => region, StringComparer.OrdinalIgnoreCase);
        var eyes = regions["eyes"];
        var mouth = regions["mouth"];

        return new MorphContentPlausibilityStats(
            factor,
            sampleCount == 0 ? 0d : inRangeCount * 100d / sampleCount,
            sampleCount == 0 ? 0d : sumAbsRequiredByteDelta / sampleCount,
            maxAbsRequiredByteDelta,
            sampleCount == 0 ? 0d : sumAbsClipByte / sampleCount,
            maxAbsClipByte,
            correctedRawMetrics,
            GetRegionRawMae((correctedR, correctedG, correctedB), shippedDecoded, egt.Cols, eyes.X, eyes.Y, eyes.W, eyes.H),
            GetRegionRawMae((correctedR, correctedG, correctedB), shippedDecoded, egt.Cols, mouth.X, mouth.Y, mouth.W, mouth.H));
    }

    private static MorphGainPlausibilityStats? ComputeMorphGainPlausibility(
        EgtParser egt,
        EgtMorph morph,
        int current256,
        (float[] R, float[] G, float[] B) currentNative,
        (float[] R, float[] G, float[] B) shippedDecoded)
    {
        var scale256 = (int)(morph.Scale * 256f);
        var factor = current256 * scale256 / 65536f;
        if (Math.Abs(factor) <= 1e-9f)
        {
            return null;
        }

        double numerator = 0d;
        double denominator = 0d;
        var pixelCount = egt.Cols * egt.Rows;
        for (var pixelIndex = 0; pixelIndex < pixelCount; pixelIndex++)
        {
            AccumulateMorphGainFit(
                morph.DeltaR[pixelIndex],
                shippedDecoded.R[pixelIndex] - currentNative.R[pixelIndex],
                factor,
                ref numerator,
                ref denominator);
            AccumulateMorphGainFit(
                morph.DeltaG[pixelIndex],
                shippedDecoded.G[pixelIndex] - currentNative.G[pixelIndex],
                factor,
                ref numerator,
                ref denominator);
            AccumulateMorphGainFit(
                morph.DeltaB[pixelIndex],
                shippedDecoded.B[pixelIndex] - currentNative.B[pixelIndex],
                factor,
                ref numerator,
                ref denominator);
        }

        if (denominator < 1e-12)
        {
            return null;
        }

        var gain = 1d + (numerator / denominator);
        var correctedR = new float[pixelCount];
        var correctedG = new float[pixelCount];
        var correctedB = new float[pixelCount];
        double sumAbsByteDelta = 0d;
        double sumAbsClipByte = 0d;
        var maxAbsByteDelta = 0f;
        var maxAbsClipByte = 0f;
        var inRangeCount = 0;
        var sampleCount = pixelCount * 3;

        for (var pixelIndex = 0; pixelIndex < pixelCount; pixelIndex++)
        {
            ApplyMorphGainCorrection(
                morph.DeltaR[pixelIndex],
                factor,
                gain,
                out correctedR[pixelIndex],
                currentNative.R[pixelIndex],
                ref sumAbsByteDelta,
                ref sumAbsClipByte,
                ref maxAbsByteDelta,
                ref maxAbsClipByte,
                ref inRangeCount);
            ApplyMorphGainCorrection(
                morph.DeltaG[pixelIndex],
                factor,
                gain,
                out correctedG[pixelIndex],
                currentNative.G[pixelIndex],
                ref sumAbsByteDelta,
                ref sumAbsClipByte,
                ref maxAbsByteDelta,
                ref maxAbsClipByte,
                ref inRangeCount);
            ApplyMorphGainCorrection(
                morph.DeltaB[pixelIndex],
                factor,
                gain,
                out correctedB[pixelIndex],
                currentNative.B[pixelIndex],
                ref sumAbsByteDelta,
                ref sumAbsClipByte,
                ref maxAbsByteDelta,
                ref maxAbsClipByte,
                ref inRangeCount);
        }

        var correctedRawMetrics = CompareFloatDeltaRgb(
            (correctedR, correctedG, correctedB),
            shippedDecoded);
        var regions = GetNamedRegions(egt.Cols, egt.Rows)
            .ToDictionary(region => region.Name, region => region, StringComparer.OrdinalIgnoreCase);
        var eyes = regions["eyes"];
        var mouth = regions["mouth"];

        return new MorphGainPlausibilityStats(
            gain,
            sampleCount == 0 ? 0d : inRangeCount * 100d / sampleCount,
            sampleCount == 0 ? 0d : sumAbsByteDelta / sampleCount,
            maxAbsByteDelta,
            sampleCount == 0 ? 0d : sumAbsClipByte / sampleCount,
            maxAbsClipByte,
            correctedRawMetrics,
            GetRegionRawMae((correctedR, correctedG, correctedB), shippedDecoded, egt.Cols, eyes.X, eyes.Y, eyes.W, eyes.H),
            GetRegionRawMae((correctedR, correctedG, correctedB), shippedDecoded, egt.Cols, mouth.X, mouth.Y, mouth.W, mouth.H));
    }

    private static MorphAffinePlausibilityStats? ComputeMorphAffinePlausibility(
        EgtParser egt,
        EgtMorph morph,
        int current256,
        (float[] R, float[] G, float[] B) currentNative,
        (float[] R, float[] G, float[] B) shippedDecoded)
    {
        var scale256 = (int)(morph.Scale * 256f);
        var factor = current256 * scale256 / 65536f;
        if (Math.Abs(factor) <= 1e-9f)
        {
            return null;
        }

        double sumX = 0d;
        double sumY = 0d;
        double sumXX = 0d;
        double sumXY = 0d;
        var sampleCount = egt.Cols * egt.Rows * 3;

        for (var pixelIndex = 0; pixelIndex < egt.Cols * egt.Rows; pixelIndex++)
        {
            AccumulateMorphAffineFit(
                morph.DeltaR[pixelIndex],
                shippedDecoded.R[pixelIndex] - currentNative.R[pixelIndex],
                factor,
                ref sumX,
                ref sumY,
                ref sumXX,
                ref sumXY);
            AccumulateMorphAffineFit(
                morph.DeltaG[pixelIndex],
                shippedDecoded.G[pixelIndex] - currentNative.G[pixelIndex],
                factor,
                ref sumX,
                ref sumY,
                ref sumXX,
                ref sumXY);
            AccumulateMorphAffineFit(
                morph.DeltaB[pixelIndex],
                shippedDecoded.B[pixelIndex] - currentNative.B[pixelIndex],
                factor,
                ref sumX,
                ref sumY,
                ref sumXX,
                ref sumXY);
        }

        if (sampleCount == 0)
        {
            return null;
        }

        var count = (double)sampleCount;
        var meanX = sumX / count;
        var meanY = sumY / count;
        var varianceX = sumXX - (sumX * meanX);
        if (Math.Abs(varianceX) <= 1e-12)
        {
            return null;
        }

        var covarianceXY = sumXY - (sumX * meanY);
        var scale = covarianceXY / varianceX;
        var bias = meanY - (scale * meanX);

        var correctedR = new float[egt.Cols * egt.Rows];
        var correctedG = new float[egt.Cols * egt.Rows];
        var correctedB = new float[egt.Cols * egt.Rows];
        double sumAbsByteDelta = 0d;
        double sumAbsClipByte = 0d;
        var maxAbsByteDelta = 0f;
        var maxAbsClipByte = 0f;
        var inRangeCount = 0;

        for (var pixelIndex = 0; pixelIndex < egt.Cols * egt.Rows; pixelIndex++)
        {
            ApplyMorphAffineCorrection(
                morph.DeltaR[pixelIndex],
                factor,
                scale,
                bias,
                out correctedR[pixelIndex],
                currentNative.R[pixelIndex],
                ref sumAbsByteDelta,
                ref sumAbsClipByte,
                ref maxAbsByteDelta,
                ref maxAbsClipByte,
                ref inRangeCount);
            ApplyMorphAffineCorrection(
                morph.DeltaG[pixelIndex],
                factor,
                scale,
                bias,
                out correctedG[pixelIndex],
                currentNative.G[pixelIndex],
                ref sumAbsByteDelta,
                ref sumAbsClipByte,
                ref maxAbsByteDelta,
                ref maxAbsClipByte,
                ref inRangeCount);
            ApplyMorphAffineCorrection(
                morph.DeltaB[pixelIndex],
                factor,
                scale,
                bias,
                out correctedB[pixelIndex],
                currentNative.B[pixelIndex],
                ref sumAbsByteDelta,
                ref sumAbsClipByte,
                ref maxAbsByteDelta,
                ref maxAbsClipByte,
                ref inRangeCount);
        }

        var correctedRawMetrics = CompareFloatDeltaRgb(
            (correctedR, correctedG, correctedB),
            shippedDecoded);
        var regions = GetNamedRegions(egt.Cols, egt.Rows)
            .ToDictionary(region => region.Name, region => region, StringComparer.OrdinalIgnoreCase);
        var eyes = regions["eyes"];
        var mouth = regions["mouth"];

        return new MorphAffinePlausibilityStats(
            scale,
            bias,
            inRangeCount * 100d / sampleCount,
            sumAbsByteDelta / sampleCount,
            maxAbsByteDelta,
            sumAbsClipByte / sampleCount,
            maxAbsClipByte,
            correctedRawMetrics,
            GetRegionRawMae((correctedR, correctedG, correctedB), shippedDecoded, egt.Cols, eyes.X, eyes.Y, eyes.W, eyes.H),
            GetRegionRawMae((correctedR, correctedG, correctedB), shippedDecoded, egt.Cols, mouth.X, mouth.Y, mouth.W, mouth.H));
    }

    private static MorphRowSimilarityStats? ComputeMorphRowSimilarityStats(
        EgtParser egt,
        EgtMorph morph,
        int current256,
        (float[] R, float[] G, float[] B) currentNative,
        (float[] R, float[] G, float[] B) shippedDecoded)
    {
        var scale256 = (int)(morph.Scale * 256f);
        var factor = current256 * scale256 / 65536f;
        if (Math.Abs(factor) <= 1e-9f)
        {
            return null;
        }

        return ComputeMorphRowSimilarityStatsCore(
            egt,
            morph,
            morph,
            factor,
            currentNative,
            shippedDecoded);
    }

    private static MorphNearestOtherRowStats? ComputeMorphNearestOtherRowStats(
        EgtParser egt,
        int sourceMorphIndex,
        EgtMorph sourceMorph,
        int current256,
        (float[] R, float[] G, float[] B) currentNative,
        (float[] R, float[] G, float[] B) shippedDecoded)
    {
        var scale256 = (int)(sourceMorph.Scale * 256f);
        var factor = current256 * scale256 / 65536f;
        if (Math.Abs(factor) <= 1e-9f)
        {
            return null;
        }

        MorphNearestOtherRowStats? best = null;
        for (var candidateIndex = 0; candidateIndex < egt.SymmetricMorphs.Length; candidateIndex++)
        {
            if (candidateIndex == sourceMorphIndex)
            {
                continue;
            }

            var candidateStats = ComputeMorphRowSimilarityStatsCore(
                egt,
                sourceMorph,
                egt.SymmetricMorphs[candidateIndex],
                factor,
                currentNative,
                shippedDecoded);
            if (candidateStats == null)
            {
                continue;
            }

            if (best == null || candidateStats.AffineFitMae < best.Stats.AffineFitMae)
            {
                best = new MorphNearestOtherRowStats(candidateIndex, candidateStats);
            }
        }

        return best;
    }

    private static MorphNearestOtherRowStats? ComputeMorphNearestOtherChannelStats(
        EgtParser egt,
        int sourceMorphIndex,
        sbyte[] sourceChannel,
        float factor,
        float[] currentChannel,
        float[] shippedChannel,
        Func<EgtMorph, sbyte[]> channelSelector)
    {
        MorphNearestOtherRowStats? best = null;
        for (var candidateIndex = 0; candidateIndex < egt.SymmetricMorphs.Length; candidateIndex++)
        {
            if (candidateIndex == sourceMorphIndex)
            {
                continue;
            }

            var candidateStats = ComputeMorphChannelRowSimilarityStatsCore(
                sourceChannel,
                channelSelector(egt.SymmetricMorphs[candidateIndex]),
                factor,
                currentChannel,
                shippedChannel);
            if (candidateStats == null)
            {
                continue;
            }

            if (best == null || candidateStats.AffineFitMae < best.Stats.AffineFitMae)
            {
                best = new MorphNearestOtherRowStats(candidateIndex, candidateStats);
            }
        }

        return best;
    }

    private static MorphNearestOtherRowPerChannelStats? ComputeMorphNearestOtherRowPerChannelStats(
        EgtParser egt,
        int sourceMorphIndex,
        EgtMorph sourceMorph,
        int current256,
        (float[] R, float[] G, float[] B) currentNative,
        (float[] R, float[] G, float[] B) shippedDecoded)
    {
        var scale256 = (int)(sourceMorph.Scale * 256f);
        var factor = current256 * scale256 / 65536f;
        if (Math.Abs(factor) <= 1e-9f)
        {
            return null;
        }

        var selfRed = ComputeMorphChannelSimilarityStatsCore(
            sourceMorph.DeltaR,
            sourceMorph.DeltaR,
            currentNative.R,
            shippedDecoded.R,
            factor);
        var selfGreen = ComputeMorphChannelSimilarityStatsCore(
            sourceMorph.DeltaG,
            sourceMorph.DeltaG,
            currentNative.G,
            shippedDecoded.G,
            factor);
        var selfBlue = ComputeMorphChannelSimilarityStatsCore(
            sourceMorph.DeltaB,
            sourceMorph.DeltaB,
            currentNative.B,
            shippedDecoded.B,
            factor);
        if (selfRed == null || selfGreen == null || selfBlue == null)
        {
            return null;
        }

        MorphNearestOtherChannelCandidate? bestRed = null;
        MorphNearestOtherChannelCandidate? bestGreen = null;
        MorphNearestOtherChannelCandidate? bestBlue = null;

        for (var candidateIndex = 0; candidateIndex < egt.SymmetricMorphs.Length; candidateIndex++)
        {
            if (candidateIndex == sourceMorphIndex)
            {
                continue;
            }

            var candidateMorph = egt.SymmetricMorphs[candidateIndex];
            var redStats = ComputeMorphChannelSimilarityStatsCore(
                sourceMorph.DeltaR,
                candidateMorph.DeltaR,
                currentNative.R,
                shippedDecoded.R,
                factor);
            var greenStats = ComputeMorphChannelSimilarityStatsCore(
                sourceMorph.DeltaG,
                candidateMorph.DeltaG,
                currentNative.G,
                shippedDecoded.G,
                factor);
            var blueStats = ComputeMorphChannelSimilarityStatsCore(
                sourceMorph.DeltaB,
                candidateMorph.DeltaB,
                currentNative.B,
                shippedDecoded.B,
                factor);

            if (redStats != null)
            {
                var vsSelf = selfRed.AffineFitMae <= 1e-9
                    ? 0d
                    : Math.Max(0d, 100d * (1d - (redStats.AffineFitMae / selfRed.AffineFitMae)));
                var candidate = new MorphNearestOtherChannelCandidate(candidateIndex, redStats, vsSelf);
                if (bestRed == null || candidate.Stats.AffineFitMae < bestRed.Stats.AffineFitMae)
                {
                    bestRed = candidate;
                }
            }

            if (greenStats != null)
            {
                var vsSelf = selfGreen.AffineFitMae <= 1e-9
                    ? 0d
                    : Math.Max(0d, 100d * (1d - (greenStats.AffineFitMae / selfGreen.AffineFitMae)));
                var candidate = new MorphNearestOtherChannelCandidate(candidateIndex, greenStats, vsSelf);
                if (bestGreen == null || candidate.Stats.AffineFitMae < bestGreen.Stats.AffineFitMae)
                {
                    bestGreen = candidate;
                }
            }

            if (blueStats != null)
            {
                var vsSelf = selfBlue.AffineFitMae <= 1e-9
                    ? 0d
                    : Math.Max(0d, 100d * (1d - (blueStats.AffineFitMae / selfBlue.AffineFitMae)));
                var candidate = new MorphNearestOtherChannelCandidate(candidateIndex, blueStats, vsSelf);
                if (bestBlue == null || candidate.Stats.AffineFitMae < bestBlue.Stats.AffineFitMae)
                {
                    bestBlue = candidate;
                }
            }
        }

        if (bestRed == null || bestGreen == null || bestBlue == null)
        {
            return null;
        }

        var mixedCandidate = new EgtMorph
        {
            Scale = sourceMorph.Scale,
            DeltaR = egt.SymmetricMorphs[bestRed.MorphIndex].DeltaR,
            DeltaG = egt.SymmetricMorphs[bestGreen.MorphIndex].DeltaG,
            DeltaB = egt.SymmetricMorphs[bestBlue.MorphIndex].DeltaB,
        };
        var mixedStats = ComputeMorphRowSimilarityStatsCore(
            egt,
            sourceMorph,
            mixedCandidate,
            factor,
            currentNative,
            shippedDecoded);
        if (mixedStats == null)
        {
            return null;
        }

        return new MorphNearestOtherRowPerChannelStats(
            bestRed,
            bestGreen,
            bestBlue,
            mixedStats);
    }

    private static MorphRowSimilarityStats? ComputeMorphRowSimilarityStatsCore(
        EgtParser egt,
        EgtMorph sourceMorph,
        EgtMorph candidateMorph,
        float factor,
        (float[] R, float[] G, float[] B) currentNative,
        (float[] R, float[] G, float[] B) shippedDecoded)
    {

        double sumX = 0d;
        double sumY = 0d;
        double sumXX = 0d;
        double sumYY = 0d;
        double sumXY = 0d;
        var sampleCount = egt.Cols * egt.Rows * 3;

        for (var pixelIndex = 0; pixelIndex < egt.Cols * egt.Rows; pixelIndex++)
        {
            AccumulateMorphRowSample(
                sourceMorph.DeltaR[pixelIndex],
                candidateMorph.DeltaR[pixelIndex],
                shippedDecoded.R[pixelIndex] - currentNative.R[pixelIndex],
                factor,
                ref sumX,
                ref sumY,
                ref sumXX,
                ref sumYY,
                ref sumXY);
            AccumulateMorphRowSample(
                sourceMorph.DeltaG[pixelIndex],
                candidateMorph.DeltaG[pixelIndex],
                shippedDecoded.G[pixelIndex] - currentNative.G[pixelIndex],
                factor,
                ref sumX,
                ref sumY,
                ref sumXX,
                ref sumYY,
                ref sumXY);
            AccumulateMorphRowSample(
                sourceMorph.DeltaB[pixelIndex],
                candidateMorph.DeltaB[pixelIndex],
                shippedDecoded.B[pixelIndex] - currentNative.B[pixelIndex],
                factor,
                ref sumX,
                ref sumY,
                ref sumXX,
                ref sumYY,
                ref sumXY);
        }

        if (sampleCount == 0 || Math.Abs(sumXX) <= 1e-12 || Math.Abs(sumYY) <= 1e-12)
        {
            return null;
        }

        var count = (double)sampleCount;
        var meanX = sumX / count;
        var meanY = sumY / count;
        var covarianceXY = sumXY - (sumX * meanY);
        var varianceX = sumXX - (sumX * meanX);
        var varianceY = sumYY - (sumY * meanY);
        var gain = sumXY / sumXX;
        var affineScale = Math.Abs(varianceX) <= 1e-12 ? 0d : covarianceXY / varianceX;
        var affineBias = meanY - (affineScale * meanX);
        var cosine = sumXY / Math.Sqrt(sumXX * sumYY);
        var correlation = Math.Abs(varianceX) <= 1e-12 || Math.Abs(varianceY) <= 1e-12
            ? 0d
            : covarianceXY / Math.Sqrt(varianceX * varianceY);

        double targetMae = 0d;
        double gainFitMae = 0d;
        double affineFitMae = 0d;

        for (var pixelIndex = 0; pixelIndex < egt.Cols * egt.Rows; pixelIndex++)
        {
            AccumulateMorphRowSimilarityResidual(
                sourceMorph.DeltaR[pixelIndex],
                candidateMorph.DeltaR[pixelIndex],
                shippedDecoded.R[pixelIndex] - currentNative.R[pixelIndex],
                factor,
                gain,
                affineScale,
                affineBias,
                ref targetMae,
                ref gainFitMae,
                ref affineFitMae);
            AccumulateMorphRowSimilarityResidual(
                sourceMorph.DeltaG[pixelIndex],
                candidateMorph.DeltaG[pixelIndex],
                shippedDecoded.G[pixelIndex] - currentNative.G[pixelIndex],
                factor,
                gain,
                affineScale,
                affineBias,
                ref targetMae,
                ref gainFitMae,
                ref affineFitMae);
            AccumulateMorphRowSimilarityResidual(
                sourceMorph.DeltaB[pixelIndex],
                candidateMorph.DeltaB[pixelIndex],
                shippedDecoded.B[pixelIndex] - currentNative.B[pixelIndex],
                factor,
                gain,
                affineScale,
                affineBias,
                ref targetMae,
                ref gainFitMae,
                ref affineFitMae);
        }

        targetMae /= sampleCount;
        gainFitMae /= sampleCount;
        affineFitMae /= sampleCount;

        return new MorphRowSimilarityStats(
            cosine,
            correlation,
            targetMae,
            gainFitMae,
            affineFitMae,
            targetMae <= 1e-9 ? 0d : Math.Max(0d, 100d * (1d - (gainFitMae / targetMae))),
            targetMae <= 1e-9 ? 0d : Math.Max(0d, 100d * (1d - (affineFitMae / targetMae))),
            gain,
            affineScale,
            affineBias);
    }

    private static MorphRowSimilarityStats? ComputeMorphChannelRowSimilarityStatsCore(
        sbyte[] sourceDelta,
        sbyte[] candidateDelta,
        float factor,
        float[] currentChannel,
        float[] shippedChannel)
    {
        var channelStats = ComputeMorphChannelSimilarityStatsCore(
            sourceDelta,
            candidateDelta,
            currentChannel,
            shippedChannel,
            factor);
        if (channelStats == null)
        {
            return null;
        }

        return new MorphRowSimilarityStats(
            channelStats.Cosine,
            channelStats.Correlation,
            channelStats.TargetMae,
            0d,
            channelStats.AffineFitMae,
            0d,
            channelStats.AffineExplainedPercent,
            0d,
            channelStats.AffineScale,
            channelStats.AffineBias);
    }

    private static MorphChannelSimilarityStats? ComputeMorphChannelSimilarityStatsCore(
        sbyte[] sourceDelta,
        sbyte[] candidateDelta,
        float[] currentChannel,
        float[] shippedChannel,
        float factor)
    {
        if (sourceDelta.Length != candidateDelta.Length ||
            sourceDelta.Length != currentChannel.Length ||
            sourceDelta.Length != shippedChannel.Length)
        {
            return null;
        }

        double sumX = 0d;
        double sumY = 0d;
        double sumXX = 0d;
        double sumYY = 0d;
        double sumXY = 0d;
        var sampleCount = sourceDelta.Length;

        for (var pixelIndex = 0; pixelIndex < sampleCount; pixelIndex++)
        {
            var x = (double)candidateDelta[pixelIndex];
            var y = sourceDelta[pixelIndex] + ((shippedChannel[pixelIndex] - currentChannel[pixelIndex]) / factor);
            sumX += x;
            sumY += y;
            sumXX += x * x;
            sumYY += y * y;
            sumXY += x * y;
        }

        if (sampleCount == 0 || Math.Abs(sumXX) <= 1e-12 || Math.Abs(sumYY) <= 1e-12)
        {
            return null;
        }

        var count = (double)sampleCount;
        var meanX = sumX / count;
        var meanY = sumY / count;
        var covarianceXY = sumXY - (sumX * meanY);
        var varianceX = sumXX - (sumX * meanX);
        var varianceY = sumYY - (sumY * meanY);
        var affineScale = Math.Abs(varianceX) <= 1e-12 ? 0d : covarianceXY / varianceX;
        var affineBias = meanY - (affineScale * meanX);
        var cosine = sumXY / Math.Sqrt(sumXX * sumYY);
        var correlation = Math.Abs(varianceX) <= 1e-12 || Math.Abs(varianceY) <= 1e-12
            ? 0d
            : covarianceXY / Math.Sqrt(varianceX * varianceY);

        double targetMae = 0d;
        double affineFitMae = 0d;
        for (var pixelIndex = 0; pixelIndex < sampleCount; pixelIndex++)
        {
            var x = (double)candidateDelta[pixelIndex];
            var y = sourceDelta[pixelIndex] + ((shippedChannel[pixelIndex] - currentChannel[pixelIndex]) / factor);
            targetMae += Math.Abs(y - x);
            affineFitMae += Math.Abs(y - ((affineScale * x) + affineBias));
        }

        targetMae /= sampleCount;
        affineFitMae /= sampleCount;

        return new MorphChannelSimilarityStats(
            cosine,
            correlation,
            targetMae,
            affineFitMae,
            targetMae <= 1e-9 ? 0d : Math.Max(0d, 100d * (1d - (affineFitMae / targetMae))),
            affineScale,
            affineBias);
    }

    private static void DumpMorphChannelInspection(
        string label,
        byte[] rawEgtData,
        int channelOffset,
        int rowStride,
        int cols,
        int rows,
        sbyte[] parsedChannel)
    {
        var sampleCount = Math.Min(cols, MorphInspectionRowSampleCount);
        var rawTop = ReadRawChannelRowBytes(rawEgtData, channelOffset, rowStride, 0, sampleCount);
        var rawBottom = ReadRawChannelRowBytes(rawEgtData, channelOffset, rowStride, rows - 1, sampleCount);
        var parsedTop = ReadParsedChannelRow(parsedChannel, cols, 0, sampleCount);
        var parsedBottom = ReadParsedChannelRow(parsedChannel, cols, rows - 1, sampleCount);
        var topMatches = RawFileRowMatchesParsed(rawEgtData, channelOffset, rowStride, 0, parsedChannel, cols, rows - 1);
        var bottomMatches = RawFileRowMatchesParsed(rawEgtData, channelOffset, rowStride, rows - 1, parsedChannel, cols, 0);

        Console.WriteLine($"         {label} rawTop[{sampleCount}]      = {FormatByteSamples(rawTop)}");
        Console.WriteLine($"         {label} rawBottom[{sampleCount}]   = {FormatByteSamples(rawBottom)}");
        Console.WriteLine($"         {label} parsedTop[{sampleCount}]   = {FormatSbyteSamples(parsedTop)}");
        Console.WriteLine($"         {label} parsedBottom[{sampleCount}] = {FormatSbyteSamples(parsedBottom)}");
        Console.WriteLine(
            $"         {label} map rawTop->parsedBottom={topMatches} rawBottom->parsedTop={bottomMatches}");
    }

    private static byte[] ReadRawChannelRowBytes(
        byte[] rawEgtData,
        int channelOffset,
        int rowStride,
        int fileRow,
        int sampleCount)
    {
        var rowOffset = channelOffset + (fileRow * rowStride);
        var result = new byte[sampleCount];
        Array.Copy(rawEgtData, rowOffset, result, 0, sampleCount);
        return result;
    }

    private static sbyte[] ReadParsedChannelRow(
        sbyte[] parsedChannel,
        int cols,
        int row,
        int sampleCount)
    {
        var rowOffset = row * cols;
        var result = new sbyte[sampleCount];
        Array.Copy(parsedChannel, rowOffset, result, 0, sampleCount);
        return result;
    }

    private static bool RawFileRowMatchesParsed(
        byte[] rawEgtData,
        int channelOffset,
        int rowStride,
        int fileRow,
        sbyte[] parsedChannel,
        int cols,
        int parsedRow)
    {
        var fileOffset = channelOffset + (fileRow * rowStride);
        var parsedOffset = parsedRow * cols;
        for (var index = 0; index < cols; index++)
        {
            if (unchecked((sbyte)rawEgtData[fileOffset + index]) != parsedChannel[parsedOffset + index])
            {
                return false;
            }
        }

        return true;
    }

    private static string FormatByteSamples(IEnumerable<byte> values)
    {
        return string.Join(" ", values.Select(value => value.ToString("X2", CultureInfo.InvariantCulture)));
    }

    private static string FormatSbyteSamples(IEnumerable<sbyte> values)
    {
        return string.Join(" ", values.Select(value => value.ToString(CultureInfo.InvariantCulture)));
    }

    private static MorphContributionStats ComputeMorphContributionStats(
        EgtParser egt,
        EgtMorph morph,
        float contributionFactor)
    {
        var wholeR = ComputeWholeChannelAbsStats(morph.DeltaR, contributionFactor);
        var wholeG = ComputeWholeChannelAbsStats(morph.DeltaG, contributionFactor);
        var wholeB = ComputeWholeChannelAbsStats(morph.DeltaB, contributionFactor);
        var eyes = GetNamedRegions(egt.Cols, egt.Rows).First(region => region.Name == "eyes");
        var mouth = GetNamedRegions(egt.Cols, egt.Rows).First(region => region.Name == "mouth");

        return new MorphContributionStats(
            wholeR.MeanAbs,
            wholeG.MeanAbs,
            wholeB.MeanAbs,
            wholeR.MaxAbs,
            wholeG.MaxAbs,
            wholeB.MaxAbs,
            ComputeRegionMeanAbsRgb(morph, contributionFactor, egt.Cols, eyes.X, eyes.Y, eyes.W, eyes.H),
            ComputeRegionMeanAbsRgb(morph, contributionFactor, egt.Cols, mouth.X, mouth.Y, mouth.W, mouth.H));
    }

    private static (float MeanAbs, float MaxAbs) ComputeWholeChannelAbsStats(
        sbyte[] channel,
        float contributionFactor)
    {
        if (channel.Length == 0 || contributionFactor == 0f)
        {
            return (0f, 0f);
        }

        double sumAbs = 0;
        var maxAbs = 0f;
        for (var index = 0; index < channel.Length; index++)
        {
            var value = MathF.Abs(channel[index] * contributionFactor);
            sumAbs += value;
            if (value > maxAbs)
            {
                maxAbs = value;
            }
        }

        return ((float)(sumAbs / channel.Length), maxAbs);
    }

    private static float ComputeRegionMeanAbsRgb(
        EgtMorph morph,
        float contributionFactor,
        int width,
        int x,
        int y,
        int regionWidth,
        int regionHeight)
    {
        if (contributionFactor == 0f || regionWidth <= 0 || regionHeight <= 0)
        {
            return 0f;
        }

        double sumAbs = 0;
        var samples = 0;
        for (var row = y; row < y + regionHeight; row++)
        {
            for (var col = x; col < x + regionWidth; col++)
            {
                var index = row * width + col;
                sumAbs += MathF.Abs(morph.DeltaR[index] * contributionFactor);
                sumAbs += MathF.Abs(morph.DeltaG[index] * contributionFactor);
                sumAbs += MathF.Abs(morph.DeltaB[index] * contributionFactor);
                samples += 3;
            }
        }

        return samples == 0 ? 0f : (float)(sumAbs / samples);
    }

    private static MorphResidualAlignmentStats ComputeMorphResidualAlignment(
        EgtParser egt,
        EgtMorph morph,
        (float[] R, float[] G, float[] B) currentNative,
        (float[] R, float[] G, float[] B) shippedDecoded)
    {
        var scale256 = (int)(morph.Scale * 256f);
        var basisFactor = scale256 / 65536f;
        if (basisFactor == 0f)
        {
            return new MorphResidualAlignmentStats(0, 0, 0, 0, 0, 0);
        }

        var eyes = GetNamedRegions(egt.Cols, egt.Rows).First(region => region.Name == "eyes");
        var mouth = GetNamedRegions(egt.Cols, egt.Rows).First(region => region.Name == "mouth");

        var whole = ComputeResidualProjectionStats(
            morph,
            basisFactor,
            egt.Cols,
            0,
            0,
            egt.Cols,
            egt.Rows,
            currentNative,
            shippedDecoded);
        var eyesStats = ComputeResidualProjectionStats(
            morph,
            basisFactor,
            egt.Cols,
            eyes.X,
            eyes.Y,
            eyes.W,
            eyes.H,
            currentNative,
            shippedDecoded);
        var mouthStats = ComputeResidualProjectionStats(
            morph,
            basisFactor,
            egt.Cols,
            mouth.X,
            mouth.Y,
            mouth.W,
            mouth.H,
            currentNative,
            shippedDecoded);

        return new MorphResidualAlignmentStats(
            whole.Projection256,
            eyesStats.Projection256,
            mouthStats.Projection256,
            whole.Cosine,
            eyesStats.Cosine,
            mouthStats.Cosine);
    }

    private static void DumpMorphStructureSummary(
        NpcAppearance appearance,
        EgtParser egt,
        float[] currentCoefficients,
        (float[] R, float[] G, float[] B) currentNative,
        (float[] R, float[] G, float[] B) shippedDecoded)
    {
        var regions = GetNamedRegions(egt.Cols, egt.Rows)
            .ToDictionary(region => region.Name, region => region, StringComparer.OrdinalIgnoreCase);
        var eyes = regions["eyes"];
        var mouth = regions["mouth"];
        var nose = regions["nose"];
        var forehead = regions["forehead"];
        var rows = new List<MorphStructureRow>(egt.SymmetricMorphs.Length);

        for (var morphIndex = 0; morphIndex < egt.SymmetricMorphs.Length; morphIndex++)
        {
            var morph = egt.SymmetricMorphs[morphIndex];
            var scale256 = (int)(morph.Scale * 256f);
            var basisFactor = scale256 / 65536f;
            var stats = ComputeMorphContributionStats(egt, morph, basisFactor);
            var residualAlignment = ComputeMorphResidualAlignment(egt, morph, currentNative, shippedDecoded);
            var current256 = morphIndex < currentCoefficients.Length
                ? (int)(currentCoefficients[morphIndex] * 256f)
                : 0;
            var wholeAbsMeanRgb = (stats.WholeMeanAbsR + stats.WholeMeanAbsG + stats.WholeMeanAbsB) / 3f;
            var noseAbsMeanRgb = ComputeRegionMeanAbsRgb(
                morph,
                basisFactor,
                egt.Cols,
                nose.X,
                nose.Y,
                nose.W,
                nose.H);
            var foreheadAbsMeanRgb = ComputeRegionMeanAbsRgb(
                morph,
                basisFactor,
                egt.Cols,
                forehead.X,
                forehead.Y,
                forehead.W,
                forehead.H);

            rows.Add(new MorphStructureRow(
                morphIndex,
                current256,
                scale256,
                wholeAbsMeanRgb,
                stats.EyesMeanAbsRgb,
                stats.MouthMeanAbsRgb,
                noseAbsMeanRgb,
                foreheadAbsMeanRgb,
                residualAlignment.WholeProjection256,
                residualAlignment.EyesProjection256,
                residualAlignment.MouthProjection256,
                residualAlignment.WholeCosine,
                residualAlignment.EyesCosine,
                residualAlignment.MouthCosine));
        }

        Console.WriteLine($"  MORPH-STRUCTURE-TOP 0x{appearance.NpcFormId:X8}:");
        foreach (var row in rows
                     .OrderByDescending(item => item.FaceLocalizedRatio)
                     .ThenByDescending(item => item.WholeAbsMeanRgb)
                     .ThenBy(item => item.Index)
                     .Take(12))
        {
            Console.WriteLine(
                $"    [{row.Index:D2}] current256={row.Current256,6} scale256={row.Scale256,4} " +
                $"whole={row.WholeAbsMeanRgb,7:F4} eyes={row.EyesAbsMeanRgb,7:F4} mouth={row.MouthAbsMeanRgb,7:F4} " +
                $"ratio={row.FaceLocalizedRatio,6:F2} projW={row.WholeProjection256,8:F1} " +
                $"projE={row.EyesProjection256,8:F1} projM={row.MouthProjection256,8:F1}");
        }

        Console.WriteLine($"  MORPH-STRUCTURE-ALL 0x{appearance.NpcFormId:X8}:");
        foreach (var row in rows.OrderBy(item => item.Index))
        {
            Console.WriteLine(
                $"    [{row.Index:D2}] current256={row.Current256,6} scale256={row.Scale256,4} " +
                $"whole={row.WholeAbsMeanRgb,7:F4} eyes={row.EyesAbsMeanRgb,7:F4} mouth={row.MouthAbsMeanRgb,7:F4} " +
                $"nose={row.NoseAbsMeanRgb,7:F4} forehead={row.ForeheadAbsMeanRgb,7:F4} ratio={row.FaceLocalizedRatio,6:F2} " +
                $"cosW={row.WholeCosine,6:F3} cosE={row.EyesCosine,6:F3} cosM={row.MouthCosine,6:F3}");
        }
    }

    private static ResidualProjectionStats ComputeResidualProjectionStats(
        EgtMorph morph,
        float basisFactor,
        int width,
        int x,
        int y,
        int regionWidth,
        int regionHeight,
        (float[] R, float[] G, float[] B) currentNative,
        (float[] R, float[] G, float[] B) shippedDecoded)
    {
        double dotBasisResidual = 0;
        double dotBasisBasis = 0;
        double dotResidualResidual = 0;

        for (var row = y; row < y + regionHeight; row++)
        {
            for (var col = x; col < x + regionWidth; col++)
            {
                var index = row * width + col;
                var basisR = morph.DeltaR[index] * basisFactor;
                var basisG = morph.DeltaG[index] * basisFactor;
                var basisB = morph.DeltaB[index] * basisFactor;

                var residualR = shippedDecoded.R[index] - currentNative.R[index];
                var residualG = shippedDecoded.G[index] - currentNative.G[index];
                var residualB = shippedDecoded.B[index] - currentNative.B[index];

                dotBasisResidual += (basisR * residualR) + (basisG * residualG) + (basisB * residualB);
                dotBasisBasis += (basisR * basisR) + (basisG * basisG) + (basisB * basisB);
                dotResidualResidual += (residualR * residualR) + (residualG * residualG) + (residualB * residualB);
            }
        }

        if (dotBasisBasis <= 0d)
        {
            return new ResidualProjectionStats(0, 0);
        }

        var projection256 = dotBasisResidual / dotBasisBasis;
        var cosine = dotResidualResidual <= 0d
            ? 0d
            : dotBasisResidual / Math.Sqrt(dotBasisBasis * dotResidualResidual);

        return new ResidualProjectionStats(projection256, cosine);
    }

    private static int AlignTo(int value, int alignment)
    {
        return (value + alignment - 1) & ~(alignment - 1);
    }

    private static IReadOnlyList<ResidualProjectionRow> DumpResidualProjectionSummary(
        NpcAppearance appearance,
        EgtParser egt,
        float[] currentCoefficients,
        (float[] R, float[] G, float[] B) currentNative,
        (float[] R, float[] G, float[] B) shippedDecoded)
    {
        var eyes = GetNamedRegions(egt.Cols, egt.Rows).First(region => region.Name == "eyes");
        var mouth = GetNamedRegions(egt.Cols, egt.Rows).First(region => region.Name == "mouth");
        var whole = (Name: "whole", X: 0, Y: 0, W: egt.Cols, H: egt.Rows);
        var rows = new List<ResidualProjectionRow>();

        for (var morphIndex = 0; morphIndex < egt.SymmetricMorphs.Length; morphIndex++)
        {
            var morph = egt.SymmetricMorphs[morphIndex];
            var scale256 = (int)(morph.Scale * 256f);
            if (scale256 == 0)
            {
                continue;
            }

            var current256 = morphIndex < currentCoefficients.Length
                ? (int)(currentCoefficients[morphIndex] * 256f)
                : 0;
            var wholeDelta256 = SolveRegionCoefficientDelta256(
                morph,
                scale256,
                egt.Cols,
                currentNative,
                shippedDecoded,
                whole.X,
                whole.Y,
                whole.W,
                whole.H);
            var eyesDelta256 = SolveRegionCoefficientDelta256(
                morph,
                scale256,
                egt.Cols,
                currentNative,
                shippedDecoded,
                eyes.X,
                eyes.Y,
                eyes.W,
                eyes.H);
            var mouthDelta256 = SolveRegionCoefficientDelta256(
                morph,
                scale256,
                egt.Cols,
                currentNative,
                shippedDecoded,
                mouth.X,
                mouth.Y,
                mouth.W,
                mouth.H);

            rows.Add(new ResidualProjectionRow(
                morphIndex,
                current256,
                wholeDelta256,
                eyesDelta256,
                mouthDelta256));
        }

        Console.WriteLine($"  RAWRESID-PROJ 0x{appearance.NpcFormId:X8}:");
        foreach (var row in rows
                     .OrderByDescending(item => item.MaxAbsDelta256)
                     .ThenBy(item => item.MorphIndex)
                     .Take(TopResidualProjectionCount))
        {
            Console.WriteLine(
                $"    [{row.MorphIndex:D2}] current256={row.Current256,6} " +
                $"wholeΔ={row.WholeDelta256,6} eyesΔ={row.EyesDelta256,6} mouthΔ={row.MouthDelta256,6} " +
                $"dominant={row.DominantRegion}");
        }

        return rows;
    }

    private static int SolveRegionCoefficientDelta256(
        EgtMorph morph,
        int scale256,
        int width,
        (float[] R, float[] G, float[] B) currentNative,
        (float[] R, float[] G, float[] B) shippedDecoded,
        int x,
        int y,
        int regionWidth,
        int regionHeight)
    {
        var basisScale = scale256 / 65536f;
        double numerator = 0;
        double denominator = 0;

        for (var row = y; row < y + regionHeight; row++)
        {
            for (var col = x; col < x + regionWidth; col++)
            {
                var index = row * width + col;
                var basisR = morph.DeltaR[index] * basisScale;
                var basisG = morph.DeltaG[index] * basisScale;
                var basisB = morph.DeltaB[index] * basisScale;
                var residualR = shippedDecoded.R[index] - currentNative.R[index];
                var residualG = shippedDecoded.G[index] - currentNative.G[index];
                var residualB = shippedDecoded.B[index] - currentNative.B[index];

                numerator += (residualR * basisR) + (residualG * basisG) + (residualB * basisB);
                denominator += (basisR * basisR) + (basisG * basisG) + (basisB * basisB);
            }
        }

        if (denominator < 1e-12)
        {
            return 0;
        }

        return (int)Math.Round(numerator / denominator, MidpointRounding.AwayFromZero);
    }

    private static (float[] R, float[] G, float[] B) DecodeEncodedDeltaTextureToFloatBuffers(
        DecodedTexture texture,
        float decodeBias = 255f,
        bool flipX = false,
        bool flipY = false,
        bool invert = false)
    {
        var pixelCount = texture.Width * texture.Height;
        var r = new float[pixelCount];
        var g = new float[pixelCount];
        var b = new float[pixelCount];

        for (var y = 0; y < texture.Height; y++)
        {
            var sourceY = flipY ? texture.Height - 1 - y : y;
            for (var x = 0; x < texture.Width; x++)
            {
                var sourceX = flipX ? texture.Width - 1 - x : x;
                var sourceIndex = sourceY * texture.Width + sourceX;
                var destinationIndex = y * texture.Width + x;
                var offset = sourceIndex * 4;

                var dr = texture.Pixels[offset] * 2f - decodeBias;
                var dg = texture.Pixels[offset + 1] * 2f - decodeBias;
                var db = texture.Pixels[offset + 2] * 2f - decodeBias;
                if (invert)
                {
                    dr = -dr;
                    dg = -dg;
                    db = -db;
                }

                r[destinationIndex] = dr;
                g[destinationIndex] = dg;
                b[destinationIndex] = db;
            }
        }

        return (r, g, b);
    }

    private static FloatDeltaRgbComparisonMetrics CompareFloatDeltaRgb(
        (float[] R, float[] G, float[] B) left,
        (float[] R, float[] G, float[] B) right)
    {
        var count = left.R.Length;
        double sumAbs = 0;
        double sumSq = 0;
        double sumSignedR = 0;
        double sumSignedG = 0;
        double sumSignedB = 0;
        var maxAbs = 0f;

        for (var index = 0; index < count; index++)
        {
            var diffR = left.R[index] - right.R[index];
            var diffG = left.G[index] - right.G[index];
            var diffB = left.B[index] - right.B[index];

            sumSignedR += diffR;
            sumSignedG += diffG;
            sumSignedB += diffB;

            sumAbs += Math.Abs(diffR) + Math.Abs(diffG) + Math.Abs(diffB);
            sumSq += diffR * diffR + diffG * diffG + diffB * diffB;
            maxAbs = Math.Max(maxAbs, Math.Max(Math.Abs(diffR), Math.Max(Math.Abs(diffG), Math.Abs(diffB))));
        }

        var sampleCount = count * 3d;
        return new FloatDeltaRgbComparisonMetrics(
            sumAbs / sampleCount,
            Math.Sqrt(sumSq / sampleCount),
            maxAbs,
            sumSignedR / count,
            sumSignedG / count,
            sumSignedB / count);
    }

    private static double GetRegionRawMae(
        (float[] R, float[] G, float[] B) left,
        (float[] R, float[] G, float[] B) right,
        int width,
        int x,
        int y,
        int regionWidth,
        int regionHeight)
    {
        double sumAbs = 0d;
        var samples = 0;
        for (var row = y; row < y + regionHeight; row++)
        {
            for (var col = x; col < x + regionWidth; col++)
            {
                var index = row * width + col;
                sumAbs += Math.Abs(left.R[index] - right.R[index]);
                sumAbs += Math.Abs(left.G[index] - right.G[index]);
                sumAbs += Math.Abs(left.B[index] - right.B[index]);
                samples += 3;
            }
        }

        return samples == 0 ? 0d : sumAbs / samples;
    }

    private static RawDeltaCoefficientFitResult? DumpRawDeltaCoefficientFit(
        NpcAppearance appearance,
        EgtParser egt,
        float[] currentCoefficients,
        DecodedTexture shippedEncodedTexture,
        (float[] R, float[] G, float[] B) shippedDecoded,
        FloatDeltaRgbComparisonMetrics currentRawMetrics,
        IReadOnlyList<ResidualProjectionRow>? residualProjectionRows)
    {
        var fit = SolveQuantizedRawDeltaCoefficientFit(egt, shippedDecoded, currentCoefficients);
        if (fit == null)
        {
            return null;
        }

        var quantizedCoefficients = fit.QuantizedCoefficient256
            .Select(v => v / 256f)
            .ToArray();
        var fittedTexture = FaceGenTextureMorpher.BuildNativeDeltaTexture(
            egt,
            quantizedCoefficients,
            FaceGenTextureMorpher.TextureAccumulationMode.EngineTruncated256,
            FaceGenTextureMorpher.DeltaTextureEncodingMode.EngineCompressed255HalfTruncate);
        if (fittedTexture == null)
        {
            return null;
        }

        var fittedRgbMetrics = NpcTextureComparison.CompareRgb(
            fittedTexture.Pixels,
            shippedEncodedTexture.Pixels,
            fittedTexture.Width,
            fittedTexture.Height);

        Console.WriteLine(
            $"  RAWFIT 0x{appearance.NpcFormId:X8}: " +
            $"rawMAE={currentRawMetrics.MeanAbsoluteRgbError:F4} " +
            $"fitRawMAE={fit.FittedRawMetrics.MeanAbsoluteRgbError:F4} " +
            $"fitRawRMSE={fit.FittedRawMetrics.RootMeanSquareRgbError:F4} " +
            $"fitRgbMAE={fittedRgbMetrics.MeanAbsoluteRgbError:F4} " +
            $"fitRgbMax={fittedRgbMetrics.MaxAbsoluteRgbError}");

        var floatOracleTexture = DecodedTexture.FromBaseLevel(
            EncodeEngineCompressedDeltaPixels(
                fit.FloatOracleBuffers.R,
                fit.FloatOracleBuffers.G,
                fit.FloatOracleBuffers.B,
                egt.Cols,
                egt.Rows),
            egt.Cols,
            egt.Rows);
        var floatOracleRgbMetrics = NpcTextureComparison.CompareRgb(
            floatOracleTexture.Pixels,
            shippedEncodedTexture.Pixels,
            floatOracleTexture.Width,
            floatOracleTexture.Height);

        Console.WriteLine(
            $"  RAWFIT-FLOAT-ORACLE 0x{appearance.NpcFormId:X8}: " +
            $"fitRawMAE={fit.FloatOracleRawMetrics.MeanAbsoluteRgbError:F4} " +
            $"fitRawRMSE={fit.FloatOracleRawMetrics.RootMeanSquareRgbError:F4} " +
            $"fitRgbMAE={floatOracleRgbMetrics.MeanAbsoluteRgbError:F4} " +
            $"fitRgbMax={floatOracleRgbMetrics.MaxAbsoluteRgbError}");

        if (ResidualSubspaceIndices is { Length: > 0 })
        {
            var subspaceFit = SolveQuantizedRawDeltaResidualSubspaceFit(
                egt,
                currentCoefficients,
                shippedDecoded,
                ResidualSubspaceIndices);
            if (subspaceFit != null)
            {
                var subspaceTexture = FaceGenTextureMorpher.BuildNativeDeltaTexture(
                    egt,
                    subspaceFit.AbsoluteCoefficients,
                    FaceGenTextureMorpher.TextureAccumulationMode.EngineTruncated256,
                    FaceGenTextureMorpher.DeltaTextureEncodingMode.EngineCompressed255HalfTruncate);
                if (subspaceTexture != null)
                {
                    var subspaceRgbMetrics = NpcTextureComparison.CompareRgb(
                        subspaceTexture.Pixels,
                        shippedEncodedTexture.Pixels,
                        subspaceTexture.Width,
                        subspaceTexture.Height);

                    Console.WriteLine(
                        $"  RAWFIT-SUBSPACE 0x{appearance.NpcFormId:X8}: " +
                        $"fitRawMAE={subspaceFit.FittedRawMetrics.MeanAbsoluteRgbError:F4} " +
                        $"fitRawRMSE={subspaceFit.FittedRawMetrics.RootMeanSquareRgbError:F4} " +
                        $"fitRgbMAE={subspaceRgbMetrics.MeanAbsoluteRgbError:F4} " +
                        $"fitRgbMax={subspaceRgbMetrics.MaxAbsoluteRgbError}");

                    Console.WriteLine($"  RAWFIT-SUBSPACE-TOP 0x{appearance.NpcFormId:X8}:");
                    foreach (var row in subspaceFit.Rows
                                 .OrderByDescending(item => Math.Abs(item.Delta256))
                                 .ThenBy(item => item.Index)
                                 .Take(TopRegionRawFitCount))
                    {
                        Console.WriteLine(
                            $"    [{row.Index:D2}] current256={row.Current256,6} fit256={row.Fit256,6} " +
                            $"delta256={row.Delta256,6:+#;-#;0} current={row.CurrentCoeff,8:F4} fit={row.FitCoeff,8:F4}");
                    }
                }
            }
        }

        var channelFreeFit = SolveQuantizedRawDeltaChannelFreeCoefficientFit(egt, shippedDecoded, currentCoefficients);
        if (channelFreeFit != null)
        {
            var channelFreeTexture = DecodedTexture.FromBaseLevel(
                EncodeEngineCompressedDeltaPixels(
                    channelFreeFit.FittedR,
                    channelFreeFit.FittedG,
                    channelFreeFit.FittedB,
                    egt.Cols,
                    egt.Rows),
                egt.Cols,
                egt.Rows);
            var channelFreeRgbMetrics = NpcTextureComparison.CompareRgb(
                channelFreeTexture.Pixels,
                shippedEncodedTexture.Pixels,
                channelFreeTexture.Width,
                channelFreeTexture.Height);

            Console.WriteLine(
                $"  RAWFIT-RGBFREE 0x{appearance.NpcFormId:X8}: " +
                $"fitRawMAE={channelFreeFit.FittedRawMetrics.MeanAbsoluteRgbError:F4} " +
                $"fitRawRMSE={channelFreeFit.FittedRawMetrics.RootMeanSquareRgbError:F4} " +
                $"fitRgbMAE={channelFreeRgbMetrics.MeanAbsoluteRgbError:F4} " +
                $"fitRgbMax={channelFreeRgbMetrics.MaxAbsoluteRgbError}");

            var rankedChannelDelta = channelFreeFit.QuantizedCoefficient256R
                .Select((valueR, index) =>
                {
                    var current256 = index < currentCoefficients.Length
                        ? (int)(currentCoefficients[index] * 256f)
                        : 0;
                    var fit256G = channelFreeFit.QuantizedCoefficient256G[index];
                    var fit256B = channelFreeFit.QuantizedCoefficient256B[index];
                    var deltaR = valueR - current256;
                    var deltaG = fit256G - current256;
                    var deltaB = fit256B - current256;
                    return new
                    {
                        Index = index,
                        Current256 = current256,
                        Fit256R = valueR,
                        Fit256G = fit256G,
                        Fit256B = fit256B,
                        DeltaMagnitude = Math.Max(Math.Abs(deltaR), Math.Max(Math.Abs(deltaG), Math.Abs(deltaB)))
                    };
                })
                .OrderByDescending(x => x.DeltaMagnitude)
                .ThenBy(x => x.Index)
                .Take(10)
                .ToArray();

            Console.WriteLine($"  RAWFIT-RGBFREE-TOP 0x{appearance.NpcFormId:X8}:");
            foreach (var row in rankedChannelDelta)
            {
                Console.WriteLine(
                    $"    [{row.Index:D2}] current256={row.Current256,6} " +
                    $"fitR={row.Fit256R,6} fitG={row.Fit256G,6} fitB={row.Fit256B,6}");
            }
        }

        var rankedDelta = fit.QuantizedCoefficient256
            .Select((value, index) =>
            {
                var current256 = index < currentCoefficients.Length
                    ? (int)(currentCoefficients[index] * 256f)
                    : 0;
                var delta256 = value - current256;
                return new
                {
                    Index = index,
                    Current256 = current256,
                    Fit256 = value,
                    Delta256 = delta256,
                    CurrentCoeff = index < currentCoefficients.Length ? currentCoefficients[index] : 0f,
                    FitCoeff = value / 256f
                };
            })
            .OrderByDescending(x => Math.Abs(x.Delta256))
            .ThenBy(x => x.Index)
            .Take(10)
            .ToArray();

        Console.WriteLine($"  RAWFIT-TOP 0x{appearance.NpcFormId:X8}:");
        foreach (var row in rankedDelta)
        {
            Console.WriteLine(
                $"    [{row.Index:D2}] current256={row.Current256,6} fit256={row.Fit256,6} " +
                $"delta256={row.Delta256,6:+#;-#;0} current={row.CurrentCoeff,8:F4} fit={row.FitCoeff,8:F4}");
        }

        DumpHotspotSubspaceFit(
            appearance,
            egt,
            currentCoefficients,
            shippedEncodedTexture,
            shippedDecoded,
            residualProjectionRows,
            currentRawMetrics);

        return fit;
    }

    internal static void DumpRawFitProvenancePcaSummary(
        NpcAppearance appearance,
        EgtParser egt,
        DecodedTexture shippedEncodedTexture,
        IReadOnlyList<float[]> familyCoefficients,
        int[]? rawFitQuantizedCoefficient256 = null)
    {
        var currentCoefficients = appearance.FaceGenTextureCoeffs ?? [];
        var count = Math.Min(currentCoefficients.Length, egt.SymmetricMorphs.Length);
        if (count == 0)
        {
            return;
        }

        var shippedDecoded = DecodeEncodedDeltaTextureToFloatBuffers(shippedEncodedTexture);
        var currentNative = FaceGenTextureMorpher.BuildNativeDeltaBuffers(
            egt,
            currentCoefficients,
            FaceGenTextureMorpher.TextureAccumulationMode.EngineTruncated256);
        if (currentNative == null)
        {
            return;
        }

        var currentRawMetrics = CompareFloatDeltaRgb(currentNative.Value, shippedDecoded);
        var rawFitQuantized = rawFitQuantizedCoefficient256 is { Length: > 0 }
            ? rawFitQuantizedCoefficient256.Take(count).ToArray()
            : null;
        FloatDeltaRgbComparisonMetrics rawFitRawMetrics;

        if (rawFitQuantized == null || rawFitQuantized.Length != count)
        {
            var rawFit = SolveQuantizedRawDeltaCoefficientFit(egt, shippedDecoded, currentCoefficients);
            if (rawFit == null)
            {
                return;
            }

            rawFitQuantized = rawFit.QuantizedCoefficient256.Take(count).ToArray();
            rawFitRawMetrics = rawFit.FittedRawMetrics;
        }
        else
        {
            var rawFitCoefficients = rawFitQuantized
                .Select(value => value / 256f)
                .ToArray();
            var rawFitNative = FaceGenTextureMorpher.BuildNativeDeltaBuffers(
                egt,
                rawFitCoefficients,
                FaceGenTextureMorpher.TextureAccumulationMode.EngineTruncated256);
            if (rawFitNative == null)
            {
                return;
            }

            rawFitRawMetrics = CompareFloatDeltaRgb(rawFitNative.Value, shippedDecoded);
        }

        var bestFamilyQuantized = Array.Empty<int>();
        FloatDeltaRgbComparisonMetrics? bestFamilyRawMetrics = null;
        DecodedTexture? bestFamilyTexture = null;
        var usableFamilyCount = 0;

        foreach (var candidate in familyCoefficients)
        {
            if (candidate.Length < count)
            {
                continue;
            }

            usableFamilyCount++;
            var candidateQuantized = candidate
                .Take(count)
                .Select(value => (int)Math.Round(value * 256f, MidpointRounding.AwayFromZero))
                .ToArray();
            var candidateCoefficients = candidateQuantized
                .Select(value => value / 256f)
                .ToArray();
            var candidateNative = FaceGenTextureMorpher.BuildNativeDeltaBuffers(
                egt,
                candidateCoefficients,
                FaceGenTextureMorpher.TextureAccumulationMode.EngineTruncated256);
            if (candidateNative == null)
            {
                continue;
            }

            var candidateRawMetrics = CompareFloatDeltaRgb(candidateNative.Value, shippedDecoded);
            if (bestFamilyRawMetrics != null &&
                (candidateRawMetrics.MeanAbsoluteRgbError > bestFamilyRawMetrics.MeanAbsoluteRgbError ||
                 (Math.Abs(candidateRawMetrics.MeanAbsoluteRgbError - bestFamilyRawMetrics.MeanAbsoluteRgbError) <= 1e-9 &&
                  candidateRawMetrics.RootMeanSquareRgbError >= bestFamilyRawMetrics.RootMeanSquareRgbError)))
            {
                continue;
            }

            var candidateTexture = FaceGenTextureMorpher.BuildNativeDeltaTexture(
                egt,
                candidateCoefficients,
                FaceGenTextureMorpher.TextureAccumulationMode.EngineTruncated256,
                FaceGenTextureMorpher.DeltaTextureEncodingMode.EngineCompressed255HalfTruncate);
            if (candidateTexture == null)
            {
                continue;
            }

            bestFamilyQuantized = candidateQuantized;
            bestFamilyRawMetrics = candidateRawMetrics;
            bestFamilyTexture = candidateTexture;
        }

        if (bestFamilyRawMetrics == null || bestFamilyTexture == null)
        {
            return;
        }

        var rawFitTexture = FaceGenTextureMorpher.BuildNativeDeltaTexture(
            egt,
            rawFitQuantized.Select(value => value / 256f).ToArray(),
            FaceGenTextureMorpher.TextureAccumulationMode.EngineTruncated256,
            FaceGenTextureMorpher.DeltaTextureEncodingMode.EngineCompressed255HalfTruncate);
        if (rawFitTexture == null)
        {
            return;
        }

        var familyRgbMetrics = NpcTextureComparison.CompareRgb(
            bestFamilyTexture.Pixels,
            shippedEncodedTexture.Pixels,
            bestFamilyTexture.Width,
            bestFamilyTexture.Height);
        var rawFitRgbMetrics = NpcTextureComparison.CompareRgb(
            rawFitTexture.Pixels,
            shippedEncodedTexture.Pixels,
            rawFitTexture.Width,
            rawFitTexture.Height);

        var denominator = currentRawMetrics.MeanAbsoluteRgbError - rawFitRawMetrics.MeanAbsoluteRgbError;
        var explainedShare = Math.Abs(denominator) <= 1e-9
            ? 0d
            : (currentRawMetrics.MeanAbsoluteRgbError - bestFamilyRawMetrics.MeanAbsoluteRgbError) / denominator;

        Console.WriteLine(
            $"  RAWFIT-PROV-FAMILY 0x{appearance.NpcFormId:X8}: " +
            $"family={usableFamilyCount} " +
            $"currentRawMAE={currentRawMetrics.MeanAbsoluteRgbError:F4} " +
            $"familyRawMAE={bestFamilyRawMetrics.MeanAbsoluteRgbError:F4} " +
            $"rawFitRawMAE={rawFitRawMetrics.MeanAbsoluteRgbError:F4} " +
            $"familyRgbMAE={familyRgbMetrics.MeanAbsoluteRgbError:F4} " +
            $"rawFitRgbMAE={rawFitRgbMetrics.MeanAbsoluteRgbError:F4} " +
            $"explained={explainedShare * 100d:F1}%");

        Console.WriteLine($"  RAWFIT-PROV-FAMILY-HOTSPOT 0x{appearance.NpcFormId:X8}:");
        foreach (var morphIndex in LateHotspotFamilyIndices)
        {
            if (morphIndex >= count)
            {
                continue;
            }

            var current256 = (int)Math.Round(currentCoefficients[morphIndex] * 256f, MidpointRounding.AwayFromZero);
            var family256 = bestFamilyQuantized[morphIndex];
            var rawFit256 = rawFitQuantized[morphIndex];
            Console.WriteLine(
                $"    [{morphIndex:D2}] current256={current256,6} family256={family256,6} rawFit256={rawFit256,6} " +
                $"deltaFam={family256 - current256,6:+#;-#;0} deltaRaw={rawFit256 - current256,6:+#;-#;0}");
        }
    }

    private static RawDeltaChannelFreeFitResult? SolveQuantizedRawDeltaChannelFreeCoefficientFit(
        EgtParser egt,
        (float[] R, float[] G, float[] B) shippedDecoded,
        float[] currentCoefficients)
    {
        var count = Math.Min(currentCoefficients.Length, egt.SymmetricMorphs.Length);
        if (count == 0)
        {
            return null;
        }

        var pixelCount = egt.Cols * egt.Rows;
        var basisR = new float[count][];
        var basisG = new float[count][];
        var basisB = new float[count][];

        for (var morphIndex = 0; morphIndex < count; morphIndex++)
        {
            var morph = egt.SymmetricMorphs[morphIndex];
            var scale256 = (int)(morph.Scale * 256f);
            var scaleFactor = scale256 / 65536f;

            var vectorR = new float[pixelCount];
            var vectorG = new float[pixelCount];
            var vectorB = new float[pixelCount];
            if (scale256 != 0)
            {
                for (var pixelIndex = 0; pixelIndex < pixelCount; pixelIndex++)
                {
                    vectorR[pixelIndex] = morph.DeltaR[pixelIndex] * scaleFactor;
                    vectorG[pixelIndex] = morph.DeltaG[pixelIndex] * scaleFactor;
                    vectorB[pixelIndex] = morph.DeltaB[pixelIndex] * scaleFactor;
                }
            }

            basisR[morphIndex] = vectorR;
            basisG[morphIndex] = vectorG;
            basisB[morphIndex] = vectorB;
        }

        var solvedR = SolveChannelFit(basisR, shippedDecoded.R);
        var solvedG = SolveChannelFit(basisG, shippedDecoded.G);
        var solvedB = SolveChannelFit(basisB, shippedDecoded.B);
        if (solvedR == null || solvedG == null || solvedB == null)
        {
            return null;
        }

        var quantizedR = solvedR
            .Select(v => (int)Math.Round(v, MidpointRounding.AwayFromZero))
            .ToArray();
        var quantizedG = solvedG
            .Select(v => (int)Math.Round(v, MidpointRounding.AwayFromZero))
            .ToArray();
        var quantizedB = solvedB
            .Select(v => (int)Math.Round(v, MidpointRounding.AwayFromZero))
            .ToArray();

        var fitR = new float[pixelCount];
        var fitG = new float[pixelCount];
        var fitB = new float[pixelCount];
        for (var morphIndex = 0; morphIndex < count; morphIndex++)
        {
            var weightR = quantizedR[morphIndex];
            var weightG = quantizedG[morphIndex];
            var weightB = quantizedB[morphIndex];
            var vectorR = basisR[morphIndex];
            var vectorG = basisG[morphIndex];
            var vectorB = basisB[morphIndex];

            for (var pixelIndex = 0; pixelIndex < pixelCount; pixelIndex++)
            {
                fitR[pixelIndex] += vectorR[pixelIndex] * weightR;
                fitG[pixelIndex] += vectorG[pixelIndex] * weightG;
                fitB[pixelIndex] += vectorB[pixelIndex] * weightB;
            }
        }

        var fittedRawMetrics = CompareFloatDeltaRgb((fitR, fitG, fitB), shippedDecoded);
        return new RawDeltaChannelFreeFitResult(
            quantizedR,
            quantizedG,
            quantizedB,
            fitR,
            fitG,
            fitB,
            fittedRawMetrics);
    }

    private static void DumpHotspotSubspaceFit(
        NpcAppearance appearance,
        EgtParser egt,
        float[] currentCoefficients,
        DecodedTexture shippedEncodedTexture,
        (float[] R, float[] G, float[] B) shippedDecoded,
        IReadOnlyList<ResidualProjectionRow>? residualProjectionRows,
        FloatDeltaRgbComparisonMetrics currentRawMetrics)
    {
        if (residualProjectionRows == null || residualProjectionRows.Count == 0)
        {
            return;
        }

        var hotspotIndices = residualProjectionRows
            .OrderByDescending(row => row.MaxAbsDelta256)
            .ThenBy(row => row.MorphIndex)
            .Take(8)
            .Select(row => row.MorphIndex)
            .OrderBy(index => index)
            .ToArray();
        if (hotspotIndices.Length == 0)
        {
            return;
        }

        var currentNative = FaceGenTextureMorpher.BuildNativeDeltaBuffers(
            egt,
            currentCoefficients,
            FaceGenTextureMorpher.TextureAccumulationMode.EngineTruncated256);
        if (currentNative == null)
        {
            return;
        }

        var deltaFit = SolveQuantizedRawResidualDeltaFit(
            egt,
            currentNative.Value,
            shippedDecoded,
            hotspotIndices);
        if (deltaFit == null)
        {
            return;
        }

        var adjustedCoefficients = (float[])currentCoefficients.Clone();
        foreach (var (morphIndex, delta256) in hotspotIndices.Zip(deltaFit.DeltaCoefficient256))
        {
            if (morphIndex < adjustedCoefficients.Length)
            {
                adjustedCoefficients[morphIndex] += delta256 / 256f;
            }
        }

        var fittedTexture = FaceGenTextureMorpher.BuildNativeDeltaTexture(
            egt,
            adjustedCoefficients,
            FaceGenTextureMorpher.TextureAccumulationMode.EngineTruncated256,
            FaceGenTextureMorpher.DeltaTextureEncodingMode.EngineCompressed255HalfTruncate);
        if (fittedTexture == null)
        {
            return;
        }

        var fittedRgbMetrics = NpcTextureComparison.CompareRgb(
            fittedTexture.Pixels,
            shippedEncodedTexture.Pixels,
            fittedTexture.Width,
            fittedTexture.Height);
        var currentGenerated = FaceGenTextureMorpher.BuildNativeDeltaTexture(
            egt,
            currentCoefficients,
            FaceGenTextureMorpher.TextureAccumulationMode.EngineTruncated256,
            FaceGenTextureMorpher.DeltaTextureEncodingMode.EngineCompressed255HalfTruncate);
        var currentEyesMae = currentGenerated != null ? GetRegionMae(currentGenerated, shippedEncodedTexture, "eyes") : 0d;
        var currentMouthMae = currentGenerated != null ? GetRegionMae(currentGenerated, shippedEncodedTexture, "mouth") : 0d;
        var fittedEyesMae = GetRegionMae(fittedTexture, shippedEncodedTexture, "eyes");
        var fittedMouthMae = GetRegionMae(fittedTexture, shippedEncodedTexture, "mouth");

        Console.WriteLine(
            $"  RAWFIT-HOTSPOT8 0x{appearance.NpcFormId:X8}: " +
            $"indices=[{string.Join(", ", hotspotIndices.Select(index => index.ToString("D2", CultureInfo.InvariantCulture)))}] " +
            $"rawMAE={currentRawMetrics.MeanAbsoluteRgbError:F4} " +
            $"fitRawMAE={deltaFit.FittedResidualMetrics.MeanAbsoluteRgbError:F4} " +
            $"fitRgbMAE={fittedRgbMetrics.MeanAbsoluteRgbError:F4} " +
            $"eyesMAE={fittedEyesMae:F4} (Δ={fittedEyesMae - currentEyesMae:+0.0000;-0.0000}) " +
            $"mouthMAE={fittedMouthMae:F4} (Δ={fittedMouthMae - currentMouthMae:+0.0000;-0.0000})");

        Console.WriteLine($"  RAWFIT-HOTSPOT8-DELTA 0x{appearance.NpcFormId:X8}:");
        foreach (var (morphIndex, delta256) in hotspotIndices.Zip(deltaFit.DeltaCoefficient256))
        {
            var current256 = morphIndex < currentCoefficients.Length
                ? (int)(currentCoefficients[morphIndex] * 256f)
                : 0;
            Console.WriteLine(
                $"    [{morphIndex:D2}] current256={current256,6} delta256={delta256,6:+#;-#;0} " +
                $"new256={current256 + delta256,6}");
        }
    }

    private static void DumpRegionalRawDeltaFits(
        NpcAppearance appearance,
        EgtParser egt,
        float[] currentCoefficients,
        DecodedTexture currentGeneratedTexture,
        DecodedTexture shippedEncodedTexture,
        (float[] R, float[] G, float[] B) currentNative,
        (float[] R, float[] G, float[] B) shippedDecoded)
    {
        var fit = SolveQuantizedRawDeltaCoefficientFit(egt, shippedDecoded, currentCoefficients);
        if (fit == null)
        {
            return;
        }

        var fittedCoefficients = fit.QuantizedCoefficient256
            .Select(value => value / 256f)
            .ToArray();
        var fittedTexture = FaceGenTextureMorpher.BuildNativeDeltaTexture(
            egt,
            fittedCoefficients,
            FaceGenTextureMorpher.TextureAccumulationMode.EngineTruncated256,
            FaceGenTextureMorpher.DeltaTextureEncodingMode.EngineCompressed255HalfTruncate);
        if (fittedTexture == null)
        {
            return;
        }

        Console.WriteLine($"  RAWFIT-REGION 0x{appearance.NpcFormId:X8}:");
        foreach (var regionName in new[] { "whole", "eyes", "mouth", "nose", "forehead" })
        {
            var currentMae = GetRegionMae(currentGeneratedTexture, shippedEncodedTexture, regionName);
            var fittedMae = GetRegionMae(fittedTexture, shippedEncodedTexture, regionName);
            Console.WriteLine(
                $"    {regionName,-8} currentMAE={currentMae:F4} fitMAE={fittedMae:F4} " +
                $"delta={fittedMae - currentMae:+0.0000;-0.0000}");
        }

        var currentRawWhole = CompareFloatDeltaRgb(currentNative, shippedDecoded);
        var fittedNative = FaceGenTextureMorpher.BuildNativeDeltaBuffers(
            egt,
            fittedCoefficients,
            FaceGenTextureMorpher.TextureAccumulationMode.EngineTruncated256);
        if (fittedNative != null)
        {
            var fittedRawWhole = CompareFloatDeltaRgb(fittedNative.Value, shippedDecoded);
            Console.WriteLine(
                $"    rawWhole  currentMAE={currentRawWhole.MeanAbsoluteRgbError:F4} " +
                $"fitMAE={fittedRawWhole.MeanAbsoluteRgbError:F4} " +
                $"delta={fittedRawWhole.MeanAbsoluteRgbError - currentRawWhole.MeanAbsoluteRgbError:+0.0000;-0.0000}");
        }
    }

    private static void DumpResidualSubspaceFit(
        NpcAppearance appearance,
        EgtParser egt,
        float[] currentCoefficients,
        DecodedTexture shippedEncodedTexture,
        (float[] R, float[] G, float[] B) currentNative,
        (float[] R, float[] G, float[] B) shippedDecoded,
        FloatDeltaRgbComparisonMetrics currentRawMetrics,
        IReadOnlyList<int> residualSubspaceIndices)
    {
        var fit = SolveQuantizedRawDeltaResidualSubspaceFit(
            egt,
            currentCoefficients,
            shippedDecoded,
            residualSubspaceIndices);
        if (fit == null)
        {
            return;
        }

        var fittedTexture = FaceGenTextureMorpher.BuildNativeDeltaTexture(
            egt,
            fit.AbsoluteCoefficients,
            FaceGenTextureMorpher.TextureAccumulationMode.EngineTruncated256,
            FaceGenTextureMorpher.DeltaTextureEncodingMode.EngineCompressed255HalfTruncate);
        if (fittedTexture == null)
        {
            return;
        }

        var fittedRgbMetrics = NpcTextureComparison.CompareRgb(
            fittedTexture.Pixels,
            shippedEncodedTexture.Pixels,
            fittedTexture.Width,
            fittedTexture.Height);
        var currentGenerated = FaceGenTextureMorpher.BuildNativeDeltaTexture(
            egt,
            currentCoefficients,
            FaceGenTextureMorpher.TextureAccumulationMode.EngineTruncated256,
            FaceGenTextureMorpher.DeltaTextureEncodingMode.EngineCompressed255HalfTruncate);
        var currentEyesMae = currentGenerated != null ? GetRegionMae(currentGenerated, shippedEncodedTexture, "eyes") : 0d;
        var currentMouthMae = currentGenerated != null ? GetRegionMae(currentGenerated, shippedEncodedTexture, "mouth") : 0d;
        var fittedEyesMae = GetRegionMae(fittedTexture, shippedEncodedTexture, "eyes");
        var fittedMouthMae = GetRegionMae(fittedTexture, shippedEncodedTexture, "mouth");

        Console.WriteLine(
            $"  RAWFIT-SUBSPACE-EXPL 0x{appearance.NpcFormId:X8}: " +
            $"indices=[{string.Join(", ", fit.Rows.Select(row => row.Index.ToString("D2", CultureInfo.InvariantCulture)))}] " +
            $"rawMAE={currentRawMetrics.MeanAbsoluteRgbError:F4} " +
            $"fitRawMAE={fit.FittedRawMetrics.MeanAbsoluteRgbError:F4} " +
            $"fitRgbMAE={fittedRgbMetrics.MeanAbsoluteRgbError:F4} " +
            $"eyesMAE={fittedEyesMae:F4} (Δ={fittedEyesMae - currentEyesMae:+0.0000;-0.0000}) " +
            $"mouthMAE={fittedMouthMae:F4} (Δ={fittedMouthMae - currentMouthMae:+0.0000;-0.0000})");

        Console.WriteLine($"  RAWFIT-SUBSPACE-EXPL-TOP 0x{appearance.NpcFormId:X8}:");
        foreach (var row in fit.Rows
                     .OrderByDescending(item => Math.Abs(item.Delta256))
                     .ThenBy(item => item.Index)
                     .Take(TopRegionRawFitCount))
        {
            Console.WriteLine(
                $"    [{row.Index:D2}] current256={row.Current256,6} fit256={row.Fit256,6} " +
                $"delta256={row.Delta256,6:+#;-#;0} current={row.CurrentCoeff,8:F4} fit={row.FitCoeff,8:F4}");
        }

        var fittedNative = FaceGenTextureMorpher.BuildNativeDeltaBuffers(
            egt,
            fit.AbsoluteCoefficients,
            FaceGenTextureMorpher.TextureAccumulationMode.EngineTruncated256);
        if (fittedNative != null)
        {
            var currentResidual = CompareFloatDeltaRgb(currentNative, shippedDecoded);
            var fittedResidual = CompareFloatDeltaRgb(fittedNative.Value, shippedDecoded);
            Console.WriteLine(
                $"    rawWhole  currentMAE={currentResidual.MeanAbsoluteRgbError:F4} " +
                $"fitMAE={fittedResidual.MeanAbsoluteRgbError:F4} " +
                $"delta={fittedResidual.MeanAbsoluteRgbError - currentResidual.MeanAbsoluteRgbError:+0.0000;-0.0000}");
        }
    }

    private static double[]? SolveChannelFit(float[][] basis, float[] target)
    {
        var count = basis.Length;
        var ata = new double[count, count];
        var aty = new double[count];
        for (var i = 0; i < count; i++)
        {
            aty[i] = DotProduct(basis[i], target);
            for (var j = i; j < count; j++)
            {
                var dot = DotProduct(basis[i], basis[j]);
                ata[i, j] = dot;
                ata[j, i] = dot;
            }
        }

        var diagonalMean = 0.0;
        for (var i = 0; i < count; i++)
        {
            diagonalMean += ata[i, i];
        }

        diagonalMean = diagonalMean > 0 ? diagonalMean / count : 1.0;
        var regularization = diagonalMean * 1e-8;
        for (var i = 0; i < count; i++)
        {
            ata[i, i] += regularization;
        }

        return SolveLinearSystem(ata, aty);
    }

    private static byte[] EncodeEngineCompressedDeltaPixels(
        float[] nativeR,
        float[] nativeG,
        float[] nativeB,
        int width,
        int height)
    {
        var pixels = new byte[width * height * 4];
        for (var index = 0; index < width * height; index++)
        {
            var pixelOffset = index * 4;
            pixels[pixelOffset] = EncodeEngineCompressedChannelTruncate(nativeR[index]);
            pixels[pixelOffset + 1] = EncodeEngineCompressedChannelTruncate(nativeG[index]);
            pixels[pixelOffset + 2] = EncodeEngineCompressedChannelTruncate(nativeB[index]);
            pixels[pixelOffset + 3] = 255;
        }

        return pixels;
    }

    private static byte EncodeEngineCompressedChannelTruncate(float delta)
    {
        var clamped = Math.Clamp(delta, -255f, 255f);
        var integral = MathF.Truncate(clamped);
        var encoded = (integral + 255f) * 0.5f;
        if (encoded <= 0f)
        {
            return 0;
        }

        if (encoded >= 255f)
        {
            return 255;
        }

        return (byte)encoded;
    }

    private static RawDeltaLinearFitSolution? SolveRawDeltaCoefficientFitLinearSystem(
        EgtParser egt,
        (float[] R, float[] G, float[] B) shippedDecoded,
        int count)
    {
        if (count <= 0)
        {
            return null;
        }

        var pixelCount = egt.Cols * egt.Rows;
        var sampleCount = pixelCount * 3;
        var target = new float[sampleCount];
        for (var index = 0; index < pixelCount; index++)
        {
            var baseOffset = index * 3;
            target[baseOffset] = shippedDecoded.R[index];
            target[baseOffset + 1] = shippedDecoded.G[index];
            target[baseOffset + 2] = shippedDecoded.B[index];
        }

        var basis = new float[count][];
        for (var morphIndex = 0; morphIndex < count; morphIndex++)
        {
            var morph = egt.SymmetricMorphs[morphIndex];
            var scale256 = (int)(morph.Scale * 256f);
            var scaleFactor = scale256 / 65536f;
            var vector = new float[sampleCount];

            if (scale256 != 0)
            {
                for (var pixelIndex = 0; pixelIndex < pixelCount; pixelIndex++)
                {
                    var baseOffset = pixelIndex * 3;
                    vector[baseOffset] = morph.DeltaR[pixelIndex] * scaleFactor;
                    vector[baseOffset + 1] = morph.DeltaG[pixelIndex] * scaleFactor;
                    vector[baseOffset + 2] = morph.DeltaB[pixelIndex] * scaleFactor;
                }
            }

            basis[morphIndex] = vector;
        }

        var ata = new double[count, count];
        var aty = new double[count];
        for (var i = 0; i < count; i++)
        {
            aty[i] = DotProduct(basis[i], target);
            for (var j = i; j < count; j++)
            {
                var dot = DotProduct(basis[i], basis[j]);
                ata[i, j] = dot;
                ata[j, i] = dot;
            }
        }

        var diagonalMean = 0.0;
        for (var i = 0; i < count; i++)
        {
            diagonalMean += ata[i, i];
        }

        diagonalMean = diagonalMean > 0 ? diagonalMean / count : 1.0;
        var regularization = diagonalMean * 1e-8;
        for (var i = 0; i < count; i++)
        {
            ata[i, i] += regularization;
        }

        var solved = SolveLinearSystem(ata, aty);
        return solved == null ? null : new RawDeltaLinearFitSolution(basis, solved);
    }

    private static RawDeltaPixelBuffers AccumulateRawFitBuffers(
        IReadOnlyList<float[]> basis,
        IReadOnlyList<double> weights,
        int pixelCount)
    {
        var fitR = new float[pixelCount];
        var fitG = new float[pixelCount];
        var fitB = new float[pixelCount];
        for (var morphIndex = 0; morphIndex < basis.Count; morphIndex++)
        {
            var weight = (float)weights[morphIndex];
            if (Math.Abs(weight) <= 1e-12f)
            {
                continue;
            }

            var vector = basis[morphIndex];
            for (var pixelIndex = 0; pixelIndex < pixelCount; pixelIndex++)
            {
                var baseOffset = pixelIndex * 3;
                fitR[pixelIndex] += vector[baseOffset] * weight;
                fitG[pixelIndex] += vector[baseOffset + 1] * weight;
                fitB[pixelIndex] += vector[baseOffset + 2] * weight;
            }
        }

        return new RawDeltaPixelBuffers(fitR, fitG, fitB);
    }

    private static RawDeltaCoefficientFitResult? SolveQuantizedRawDeltaCoefficientFit(
        EgtParser egt,
        (float[] R, float[] G, float[] B) shippedDecoded,
        float[] currentCoefficients)
    {
        var count = Math.Min(currentCoefficients.Length, egt.SymmetricMorphs.Length);
        if (count == 0)
        {
            return null;
        }

        var linearFit = SolveRawDeltaCoefficientFitLinearSystem(egt, shippedDecoded, count);
        if (linearFit == null)
        {
            return null;
        }

        var quantizedCoefficient256 = linearFit.SolvedCoefficient256
            .Select(v => (int)Math.Round(v, MidpointRounding.AwayFromZero))
            .ToArray();

        var pixelCount = egt.Cols * egt.Rows;
        var floatOracleBuffers = AccumulateRawFitBuffers(
            linearFit.Basis,
            linearFit.SolvedCoefficient256,
            pixelCount);
        var quantizedBuffers = AccumulateRawFitBuffers(
            linearFit.Basis,
            Array.ConvertAll(quantizedCoefficient256, static value => (double)value),
            pixelCount);

        var fittedRawMetrics = CompareFloatDeltaRgb(
            (quantizedBuffers.R, quantizedBuffers.G, quantizedBuffers.B),
            shippedDecoded);
        var floatOracleRawMetrics = CompareFloatDeltaRgb(
            (floatOracleBuffers.R, floatOracleBuffers.G, floatOracleBuffers.B),
            shippedDecoded);
        return new RawDeltaCoefficientFitResult(
            quantizedCoefficient256,
            fittedRawMetrics,
            floatOracleRawMetrics,
            floatOracleBuffers);
    }

    private static RawDeltaResidualSubspaceFitResult? SolveQuantizedRawDeltaResidualSubspaceFit(
        EgtParser egt,
        float[] currentCoefficients,
        (float[] R, float[] G, float[] B) shippedDecoded,
        IReadOnlyList<int> residualSubspaceIndices)
    {
        var filteredIndices = residualSubspaceIndices
            .Where(index => index >= 0 && index < Math.Min(currentCoefficients.Length, egt.SymmetricMorphs.Length))
            .Distinct()
            .OrderBy(index => index)
            .ToArray();
        if (filteredIndices.Length == 0)
        {
            return null;
        }

        var currentNative = FaceGenTextureMorpher.BuildNativeDeltaBuffers(
            egt,
            currentCoefficients,
            FaceGenTextureMorpher.TextureAccumulationMode.EngineTruncated256);
        if (currentNative == null)
        {
            return null;
        }

        var deltaFit = SolveQuantizedRawResidualDeltaFit(
            egt,
            currentNative.Value,
            shippedDecoded,
            filteredIndices);
        if (deltaFit == null)
        {
            return null;
        }

        var absoluteCoefficients = (float[])currentCoefficients.Clone();
        var rows = new List<RawDeltaResidualSubspaceRow>(filteredIndices.Length);
        foreach (var (morphIndex, delta256) in filteredIndices.Zip(deltaFit.DeltaCoefficient256))
        {
            var current256 = morphIndex < currentCoefficients.Length
                ? (int)(currentCoefficients[morphIndex] * 256f)
                : 0;
            var fit256 = current256 + delta256;
            var fitCoeff = fit256 / 256f;
            if (morphIndex < absoluteCoefficients.Length)
            {
                absoluteCoefficients[morphIndex] = fitCoeff;
            }

            rows.Add(new RawDeltaResidualSubspaceRow(
                morphIndex,
                current256,
                fit256,
                delta256,
                morphIndex < currentCoefficients.Length ? currentCoefficients[morphIndex] : 0f,
                fitCoeff));
        }

        var fittedNative = FaceGenTextureMorpher.BuildNativeDeltaBuffers(
            egt,
            absoluteCoefficients,
            FaceGenTextureMorpher.TextureAccumulationMode.EngineTruncated256);
        if (fittedNative == null)
        {
            return null;
        }

        var fittedRawMetrics = CompareFloatDeltaRgb(fittedNative.Value, shippedDecoded);
        return new RawDeltaResidualSubspaceFitResult(absoluteCoefficients, fittedRawMetrics, rows);
    }

    private static HotspotDeltaFitResult? SolveQuantizedRawResidualDeltaFit(
        EgtParser egt,
        (float[] R, float[] G, float[] B) currentNative,
        (float[] R, float[] G, float[] B) shippedDecoded,
        IReadOnlyList<int> hotspotIndices)
    {
        if (hotspotIndices.Count == 0)
        {
            return null;
        }

        var pixelCount = egt.Cols * egt.Rows;
        var sampleCount = pixelCount * 3;
        var target = new float[sampleCount];
        for (var pixelIndex = 0; pixelIndex < pixelCount; pixelIndex++)
        {
            var baseOffset = pixelIndex * 3;
            target[baseOffset] = shippedDecoded.R[pixelIndex] - currentNative.R[pixelIndex];
            target[baseOffset + 1] = shippedDecoded.G[pixelIndex] - currentNative.G[pixelIndex];
            target[baseOffset + 2] = shippedDecoded.B[pixelIndex] - currentNative.B[pixelIndex];
        }

        var basis = new float[hotspotIndices.Count][];
        for (var hotspotOrder = 0; hotspotOrder < hotspotIndices.Count; hotspotOrder++)
        {
            var morphIndex = hotspotIndices[hotspotOrder];
            var morph = egt.SymmetricMorphs[morphIndex];
            var scale256 = (int)(morph.Scale * 256f);
            var scaleFactor = scale256 / 65536f;
            var vector = new float[sampleCount];
            if (scale256 != 0)
            {
                for (var pixelIndex = 0; pixelIndex < pixelCount; pixelIndex++)
                {
                    var baseOffset = pixelIndex * 3;
                    vector[baseOffset] = morph.DeltaR[pixelIndex] * scaleFactor;
                    vector[baseOffset + 1] = morph.DeltaG[pixelIndex] * scaleFactor;
                    vector[baseOffset + 2] = morph.DeltaB[pixelIndex] * scaleFactor;
                }
            }

            basis[hotspotOrder] = vector;
        }

        var ata = new double[hotspotIndices.Count, hotspotIndices.Count];
        var aty = new double[hotspotIndices.Count];
        for (var i = 0; i < hotspotIndices.Count; i++)
        {
            aty[i] = DotProduct(basis[i], target);
            for (var j = i; j < hotspotIndices.Count; j++)
            {
                var dot = DotProduct(basis[i], basis[j]);
                ata[i, j] = dot;
                ata[j, i] = dot;
            }
        }

        var diagonalMean = 0.0;
        for (var i = 0; i < hotspotIndices.Count; i++)
        {
            diagonalMean += ata[i, i];
        }

        diagonalMean = diagonalMean > 0 ? diagonalMean / hotspotIndices.Count : 1.0;
        var regularization = diagonalMean * 1e-8;
        for (var i = 0; i < hotspotIndices.Count; i++)
        {
            ata[i, i] += regularization;
        }

        var solved = SolveLinearSystem(ata, aty);
        if (solved == null)
        {
            return null;
        }

        var quantizedDelta256 = solved
            .Select(value => (int)Math.Round(value, MidpointRounding.AwayFromZero))
            .ToArray();

        var fitR = new float[pixelCount];
        var fitG = new float[pixelCount];
        var fitB = new float[pixelCount];
        for (var hotspotOrder = 0; hotspotOrder < hotspotIndices.Count; hotspotOrder++)
        {
            var weight = quantizedDelta256[hotspotOrder];
            if (weight == 0)
            {
                continue;
            }

            var vector = basis[hotspotOrder];
            for (var pixelIndex = 0; pixelIndex < pixelCount; pixelIndex++)
            {
                var baseOffset = pixelIndex * 3;
                fitR[pixelIndex] += vector[baseOffset] * weight;
                fitG[pixelIndex] += vector[baseOffset + 1] * weight;
                fitB[pixelIndex] += vector[baseOffset + 2] * weight;
            }
        }

        var fittedResidualMetrics = CompareFloatDeltaRgb(
            (fitR, fitG, fitB),
            DecodeResidualTarget(target, pixelCount));
        return new HotspotDeltaFitResult(quantizedDelta256, fittedResidualMetrics);
    }

    private static (float[] R, float[] G, float[] B) DecodeResidualTarget(float[] target, int pixelCount)
    {
        var r = new float[pixelCount];
        var g = new float[pixelCount];
        var b = new float[pixelCount];
        for (var pixelIndex = 0; pixelIndex < pixelCount; pixelIndex++)
        {
            var baseOffset = pixelIndex * 3;
            r[pixelIndex] = target[baseOffset];
            g[pixelIndex] = target[baseOffset + 1];
            b[pixelIndex] = target[baseOffset + 2];
        }

        return (r, g, b);
    }

    private static List<double[]> BuildCenteredFamilyDifferenceVectors(
        IReadOnlyList<float[]> familyCoefficients,
        float[] currentCoefficients,
        int count)
    {
        var vectors = new List<double[]>(familyCoefficients.Count);
        foreach (var candidate in familyCoefficients)
        {
            if (candidate.Length < count)
            {
                continue;
            }

            var vector = new double[count];
            var sumSq = 0d;
            for (var index = 0; index < count; index++)
            {
                var delta = candidate[index] - currentCoefficients[index];
                vector[index] = delta;
                sumSq += delta * delta;
            }

            if (sumSq > 1e-12)
            {
                vectors.Add(vector);
            }
        }

        return vectors;
    }

    private static double[,] BuildCovarianceMatrix(
        IReadOnlyList<double[]> differenceVectors,
        int count)
    {
        var covariance = new double[count, count];
        foreach (var vector in differenceVectors)
        {
            for (var row = 0; row < count; row++)
            {
                var rowValue = vector[row];
                if (Math.Abs(rowValue) < 1e-18)
                {
                    continue;
                }

                for (var col = row; col < count; col++)
                {
                    covariance[row, col] += rowValue * vector[col];
                }
            }
        }

        var scale = 1d / differenceVectors.Count;
        for (var row = 0; row < count; row++)
        {
            for (var col = row; col < count; col++)
            {
                covariance[row, col] *= scale;
                covariance[col, row] = covariance[row, col];
            }
        }

        return covariance;
    }

    private static PrincipalComponentSet? ComputeTopPrincipalComponents(
        double[,] covariance,
        int maxComponentCount)
    {
        var size = covariance.GetLength(0);
        if (size == 0 || maxComponentCount <= 0)
        {
            return null;
        }

        var trace = 0d;
        for (var index = 0; index < size; index++)
        {
            trace += covariance[index, index];
        }

        if (trace <= 1e-12)
        {
            return null;
        }

        var eigenvalues = new List<double>(maxComponentCount);
        var eigenvectors = new List<double[]>(maxComponentCount);
        for (var component = 0; component < maxComponentCount; component++)
        {
            var vector = CreatePrincipalComponentSeed(size, component);
            Orthogonalize(vector, eigenvectors);
            var norm = VectorNorm(vector);
            if (norm <= 1e-12)
            {
                break;
            }

            ScaleVector(vector, 1d / norm);

            for (var iteration = 0; iteration < 128; iteration++)
            {
                var next = MultiplyMatrixVector(covariance, vector);
                Orthogonalize(next, eigenvectors);
                var nextNorm = VectorNorm(next);
                if (nextNorm <= 1e-12)
                {
                    vector = Array.Empty<double>();
                    break;
                }

                ScaleVector(next, 1d / nextNorm);
                var delta = VectorDifferenceNormSquared(next, vector);
                var negDelta = VectorSumNormSquared(next, vector);
                vector = next;
                if (Math.Min(delta, negDelta) <= 1e-18)
                {
                    break;
                }
            }

            if (vector.Length == 0)
            {
                break;
            }

            var eigenvalue = Math.Max(0d, RayleighQuotient(covariance, vector));
            if (eigenvalue <= trace * 1e-9)
            {
                break;
            }

            eigenvalues.Add(eigenvalue);
            eigenvectors.Add(vector);
        }

        if (eigenvalues.Count == 0)
        {
            return null;
        }

        return new PrincipalComponentSet(eigenvalues.ToArray(), eigenvectors.ToArray());
    }

    private static int SelectPrincipalComponentCount(
        IReadOnlyList<double> eigenvalues,
        int minPreferredCount,
        int maxCount)
    {
        if (eigenvalues.Count == 0 || maxCount <= 0)
        {
            return 0;
        }

        var totalVariance = eigenvalues.Sum();
        if (totalVariance <= 0d)
        {
            return 0;
        }

        var limit = Math.Min(maxCount, eigenvalues.Count);
        var minCount = Math.Min(minPreferredCount, limit);
        var cumulative = 0d;
        var selected = 0;
        while (selected < limit)
        {
            cumulative += eigenvalues[selected];
            selected++;
            if (selected >= minCount && cumulative / totalVariance >= 0.90d)
            {
                break;
            }
        }

        return selected;
    }

    private static AxisProjectionRange[] ComputeAxisProjectionRanges(
        IReadOnlyList<double[]> axisCoefficients,
        IReadOnlyList<double[]> differenceVectors)
    {
        var ranges = new AxisProjectionRange[axisCoefficients.Count];
        for (var axisIndex = 0; axisIndex < axisCoefficients.Count; axisIndex++)
        {
            var axis = axisCoefficients[axisIndex];
            var min = 0d;
            var max = 0d;
            var initialized = false;
            foreach (var difference in differenceVectors)
            {
                var projection = DotProduct(axis, difference);
                if (!initialized)
                {
                    min = projection;
                    max = projection;
                    initialized = true;
                    continue;
                }

                min = Math.Min(min, projection);
                max = Math.Max(max, projection);
            }

            ranges[axisIndex] = new AxisProjectionRange(min, max);
        }

        return ranges;
    }

    private static float[] BuildResidualTargetVector(
        (float[] R, float[] G, float[] B) currentNative,
        (float[] R, float[] G, float[] B) shippedDecoded)
    {
        var pixelCount = currentNative.R.Length;
        var target = new float[pixelCount * 3];
        for (var pixelIndex = 0; pixelIndex < pixelCount; pixelIndex++)
        {
            var baseOffset = pixelIndex * 3;
            target[baseOffset] = shippedDecoded.R[pixelIndex] - currentNative.R[pixelIndex];
            target[baseOffset + 1] = shippedDecoded.G[pixelIndex] - currentNative.G[pixelIndex];
            target[baseOffset + 2] = shippedDecoded.B[pixelIndex] - currentNative.B[pixelIndex];
        }

        return target;
    }

    private static float[][] BuildCoefficientAxisPixelBasis(
        EgtParser egt,
        IReadOnlyList<double[]> axisCoefficients,
        int count)
    {
        var pixelCount = egt.Cols * egt.Rows;
        var basis = new float[axisCoefficients.Count][];

        for (var axisIndex = 0; axisIndex < axisCoefficients.Count; axisIndex++)
        {
            var vector = new float[pixelCount * 3];
            var axis = axisCoefficients[axisIndex];
            for (var morphIndex = 0; morphIndex < count; morphIndex++)
            {
                var morphWeight = axis[morphIndex];
                if (Math.Abs(morphWeight) <= 1e-12)
                {
                    continue;
                }

                var morph = egt.SymmetricMorphs[morphIndex];
                var scale256 = (int)(morph.Scale * 256f);
                if (scale256 == 0)
                {
                    continue;
                }

                var scaleFactor = (float)(morphWeight * (scale256 / 65536f));
                for (var pixelIndex = 0; pixelIndex < pixelCount; pixelIndex++)
                {
                    var baseOffset = pixelIndex * 3;
                    vector[baseOffset] += morph.DeltaR[pixelIndex] * scaleFactor;
                    vector[baseOffset + 1] += morph.DeltaG[pixelIndex] * scaleFactor;
                    vector[baseOffset + 2] += morph.DeltaB[pixelIndex] * scaleFactor;
                }
            }

            basis[axisIndex] = vector;
        }

        return basis;
    }

    private static double[]? SolveAxisWeights(
        IReadOnlyList<float[]> axisBasis,
        float[] targetResidual)
    {
        if (axisBasis.Count == 0)
        {
            return null;
        }

        var count = axisBasis.Count;
        var ata = new double[count, count];
        var aty = new double[count];
        for (var row = 0; row < count; row++)
        {
            aty[row] = DotProduct(axisBasis[row], targetResidual);
            for (var col = row; col < count; col++)
            {
                var dot = DotProduct(axisBasis[row], axisBasis[col]);
                ata[row, col] = dot;
                ata[col, row] = dot;
            }
        }

        var diagonalMean = 0d;
        for (var index = 0; index < count; index++)
        {
            diagonalMean += ata[index, index];
        }

        diagonalMean = diagonalMean > 0d ? diagonalMean / count : 1d;
        var regularization = diagonalMean * 1e-8;
        for (var index = 0; index < count; index++)
        {
            ata[index, index] += regularization;
        }

        return SolveLinearSystem(ata, aty);
    }

    private static int[] BuildQuantizedCoefficientVector(
        float[] currentCoefficients,
        IReadOnlyList<double[]> axisCoefficients,
        IReadOnlyList<double> axisWeights,
        int count)
    {
        var quantized = new int[count];
        for (var morphIndex = 0; morphIndex < count; morphIndex++)
        {
            var coefficient = morphIndex < currentCoefficients.Length
                ? currentCoefficients[morphIndex]
                : 0f;
            for (var axisIndex = 0; axisIndex < axisCoefficients.Count; axisIndex++)
            {
                coefficient += (float)(axisCoefficients[axisIndex][morphIndex] * axisWeights[axisIndex]);
            }

            quantized[morphIndex] = (int)Math.Round(coefficient * 256f, MidpointRounding.AwayFromZero);
        }

        return quantized;
    }

    private static void DumpAffineFitRegionMetrics(
        DecodedTexture generated,
        DecodedTexture shipped)
    {
        foreach (var (name, rx, ry, rw, rh) in GetNamedRegions(generated.Width, generated.Height))
        {
            var genCrop = NpcTextureComparison.Crop(generated, rx, ry, rw, rh);
            var shipCrop = NpcTextureComparison.Crop(shipped, rx, ry, rw, rh);
            var affineFit = NpcTextureComparison.FitPerChannelAffineRgb(
                genCrop.Pixels,
                shipCrop.Pixels,
                rw,
                rh);

            Console.WriteLine(
                $"    AFFINE {name,12}: " +
                $"rawMAE={affineFit.RawMetrics.MeanAbsoluteRgbError:F3} " +
                $"fitMAE={affineFit.FittedMetrics.MeanAbsoluteRgbError:F3} " +
                $"fitMax={affineFit.FittedMetrics.MaxAbsoluteRgbError,3} " +
                $"sR={affineFit.Red.Scale,6:F3} bR={affineFit.Red.Bias,7:F3} " +
                $"sG={affineFit.Green.Scale,6:F3} bG={affineFit.Green.Bias,7:F3} " +
                $"sB={affineFit.Blue.Scale,6:F3} bB={affineFit.Blue.Bias,7:F3}");
        }
    }

    private static double GetRegionMae(
        DecodedTexture generated,
        DecodedTexture shipped,
        string regionName)
    {
        var region = GetNamedRegions(generated.Width, generated.Height)
            .First(namedRegion => namedRegion.Name == regionName);
        var generatedCrop = NpcTextureComparison.Crop(generated, region.X, region.Y, region.W, region.H);
        var shippedCrop = NpcTextureComparison.Crop(shipped, region.X, region.Y, region.W, region.H);
        return NpcTextureComparison.CompareRgb(
            generatedCrop.Pixels,
            shippedCrop.Pixels,
            region.W,
            region.H).MeanAbsoluteRgbError;
    }

    private static (string Name, int X, int Y, int W, int H)[] GetNamedRegions(int width, int height)
    {
        return
        [
            ("eyes", 72 * width / 256, 64 * width / 256, 112 * width / 256, 40 * height / 256),
            ("left_eye", 76 * width / 256, 68 * height / 256, 40 * width / 256, 28 * height / 256),
            ("right_eye", 140 * width / 256, 68 * height / 256, 40 * width / 256, 28 * height / 256),
            ("mouth", 88 * width / 256, 120 * height / 256, 80 * width / 256, 56 * height / 256),
            ("nose", 104 * width / 256, 88 * height / 256, 48 * width / 256, 36 * height / 256),
            ("forehead", 80 * width / 256, 24 * height / 256, 96 * width / 256, 40 * height / 256),
            ("background", 0, 0, 40 * width / 256, 40 * height / 256),
            ("whole", 0, 0, width, height)
        ];
    }

    private static void DumpCoefficients(NpcAppearance appearance, EgtParser egt)
    {
        var npcCoeffs = appearance.NpcFaceGenTextureCoeffs;
        var raceCoeffs = appearance.RaceFaceGenTextureCoeffs;
        var mergedCoeffs = appearance.FaceGenTextureCoeffs;

        Console.WriteLine($"  COEFF 0x{appearance.NpcFormId:X8} ({appearance.EditorId}):");
        Console.WriteLine($"    NPC FGTS:  {(npcCoeffs != null ? $"{npcCoeffs.Length} floats" : "null")}");
        Console.WriteLine($"    Race FGTS: {(raceCoeffs != null ? $"{raceCoeffs.Length} floats" : "null")}");
        Console.WriteLine($"    Merged:    {(mergedCoeffs != null ? $"{mergedCoeffs.Length} floats" : "null")}");

        if (mergedCoeffs != null)
        {
            // Show top 10 strongest merged coefficients
            var ranked = mergedCoeffs
                .Select((c, i) => (Index: i, Coeff: c, AbsCoeff: MathF.Abs(c),
                    Scale: i < egt.SymmetricMorphs.Length ? egt.SymmetricMorphs[i].Scale : 0f))
                .OrderByDescending(x => x.AbsCoeff * MathF.Abs(x.Scale))
                .Take(10)
                .ToArray();

            Console.WriteLine("    Top 10 (by |coeff*scale|):");
            foreach (var r in ranked)
            {
                var npcVal = npcCoeffs != null && r.Index < npcCoeffs.Length ? npcCoeffs[r.Index] : 0f;
                var raceVal = raceCoeffs != null && r.Index < raceCoeffs.Length ? raceCoeffs[r.Index] : 0f;
                Console.WriteLine(
                    $"      [{r.Index:D2}] merged={r.Coeff,8:F4}  npc={npcVal,8:F4}  race={raceVal,8:F4}  scale={r.Scale,8:F4}  |c*s|={r.AbsCoeff * MathF.Abs(r.Scale):F4}");
            }
        }

        // Also dump all 50 merged coefficients in a compact line
        if (mergedCoeffs is { Length: > 0 })
        {
            Console.Write("    All merged: [");
            for (var i = 0; i < mergedCoeffs.Length; i++)
            {
                if (i > 0) Console.Write(", ");
                Console.Write($"{mergedCoeffs[i]:F4}");
            }

            Console.WriteLine("]");
        }
    }

    private sealed record MorphAblationRow(
        int MorphIndex,
        float Coefficient,
        float Scale,
        double Mae,
        double DeltaMae);

    private sealed record ResidualProjectionRow(
        int MorphIndex,
        int Current256,
        int WholeDelta256,
        int EyesDelta256,
        int MouthDelta256)
    {
        public int MaxAbsDelta256 =>
            Math.Max(
                Math.Abs(WholeDelta256),
                Math.Max(Math.Abs(EyesDelta256), Math.Abs(MouthDelta256)));

        public string DominantRegion
        {
            get
            {
                var wholeAbs = Math.Abs(WholeDelta256);
                var eyesAbs = Math.Abs(EyesDelta256);
                var mouthAbs = Math.Abs(MouthDelta256);
                if (wholeAbs >= eyesAbs && wholeAbs >= mouthAbs)
                {
                    return "whole";
                }

                return eyesAbs >= mouthAbs ? "eyes" : "mouth";
            }
        }
    }

    internal sealed record NpcFaceGenTextureVerificationResult
    {
        public required uint FormId { get; init; }
        public required string PluginName { get; init; }
        public string? EditorId { get; init; }
        public string? FullName { get; init; }
        public required string ShippedTexturePath { get; init; }
        public string? ShippedSourcePath { get; init; }
        public string? ShippedSourceFormat { get; init; }
        public string? BaseTexturePath { get; init; }
        public string? EgtPath { get; init; }
        public string? ComparisonMode { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }
        public double MeanAbsoluteRgbError { get; init; }
        public double RootMeanSquareRgbError { get; init; }
        public int MaxAbsoluteRgbError { get; init; }
        public int PixelsWithAnyRgbDifference { get; init; }
        public int PixelsWithRgbErrorAbove1 { get; init; }
        public int PixelsWithRgbErrorAbove2 { get; init; }
        public int PixelsWithRgbErrorAbove4 { get; init; }
        public int PixelsWithRgbErrorAbove8 { get; init; }
        public double SsimLuminance { get; init; }
        public double SsimRgbMean { get; init; }
        public double SsimNormalizedLuminance { get; init; }
        public double SsimNormalizedRgbMean { get; init; }
        public double SsimMaxSatRgbMean { get; init; }
        public double AffineFitMeanAbsoluteRgbError { get; init; }
        public double AffineFitRootMeanSquareRgbError { get; init; }
        public int AffineFitMaxAbsoluteRgbError { get; init; }
        public double AffineFitScaleRed { get; init; }
        public double AffineFitScaleGreen { get; init; }
        public double AffineFitScaleBlue { get; init; }
        public double AffineFitBiasRed { get; init; }
        public double AffineFitBiasGreen { get; init; }
        public double AffineFitBiasBlue { get; init; }
        public string? FailureReason { get; init; }

        public bool Verified => FailureReason == null;
        public bool ExactMatch => Verified && PixelsWithAnyRgbDifference == 0;
    }

    internal sealed record NpcFaceGenTextureVerificationDetail(
        NpcFaceGenTextureVerificationResult Result,
        DecodedTexture? GeneratedTexture,
        DecodedTexture? ShippedTexture,
        IReadOnlyList<DiagnosticVariantMetric>? DiagnosticVariants = null,
        DecodedTexture? AffineFitTexture = null,
        int[]? RawFitQuantizedCoefficient256 = null);

    internal sealed record DiagnosticVariantMetric(
        string Mode,
        double MeanAbsoluteRgbError,
        double RootMeanSquareRgbError,
        int MaxAbsoluteRgbError);

    private sealed record FloatDeltaRgbComparisonMetrics(
        double MeanAbsoluteRgbError,
        double RootMeanSquareRgbError,
        float MaxAbsoluteRgbError,
        double MeanSignedRedError,
        double MeanSignedGreenError,
        double MeanSignedBlueError);

    private sealed record MorphContributionStats(
        float WholeMeanAbsR,
        float WholeMeanAbsG,
        float WholeMeanAbsB,
        float WholeMaxAbsR,
        float WholeMaxAbsG,
        float WholeMaxAbsB,
        float EyesMeanAbsRgb,
        float MouthMeanAbsRgb);

    private sealed record ResidualProjectionStats(
        double Projection256,
        double Cosine);

    private sealed record MorphResidualAlignmentStats(
        double WholeProjection256,
        double EyesProjection256,
        double MouthProjection256,
        double WholeCosine,
        double EyesCosine,
        double MouthCosine);

    private sealed record MorphStructureRow(
        int Index,
        int Current256,
        int Scale256,
        float WholeAbsMeanRgb,
        float EyesAbsMeanRgb,
        float MouthAbsMeanRgb,
        float NoseAbsMeanRgb,
        float ForeheadAbsMeanRgb,
        double WholeProjection256,
        double EyesProjection256,
        double MouthProjection256,
        double WholeCosine,
        double EyesCosine,
        double MouthCosine)
    {
        public double FaceLocalizedRatio =>
            WholeAbsMeanRgb <= 0f ? 0d : (EyesAbsMeanRgb + MouthAbsMeanRgb) / WholeAbsMeanRgb;
    }

    private sealed record RawDeltaCoefficientFitResult(
        int[] QuantizedCoefficient256,
        FloatDeltaRgbComparisonMetrics FittedRawMetrics,
        FloatDeltaRgbComparisonMetrics FloatOracleRawMetrics,
        RawDeltaPixelBuffers FloatOracleBuffers);

    private sealed record RawDeltaLinearFitSolution(
        float[][] Basis,
        double[] SolvedCoefficient256);

    private sealed record RawDeltaPixelBuffers(
        float[] R,
        float[] G,
        float[] B);

    private sealed record RawDeltaResidualSubspaceRow(
        int Index,
        int Current256,
        int Fit256,
        int Delta256,
        float CurrentCoeff,
        float FitCoeff);

    private sealed record RawDeltaResidualSubspaceFitResult(
        float[] AbsoluteCoefficients,
        FloatDeltaRgbComparisonMetrics FittedRawMetrics,
        IReadOnlyList<RawDeltaResidualSubspaceRow> Rows);

    private sealed record HotspotDeltaFitResult(
        int[] DeltaCoefficient256,
        FloatDeltaRgbComparisonMetrics FittedResidualMetrics);

    private sealed record MorphContentPlausibilityStats(
        float Factor,
        double InRangePercent,
        double MeanAbsRequiredByteDelta,
        float MaxAbsRequiredByteDelta,
        double MeanAbsClipByte,
        float MaxAbsClipByte,
        FloatDeltaRgbComparisonMetrics CorrectedRawMetrics,
        double CorrectedEyesRawMae,
        double CorrectedMouthRawMae);

    private sealed record MorphGainPlausibilityStats(
        double Gain,
        double InRangePercent,
        double MeanAbsByteDelta,
        float MaxAbsByteDelta,
        double MeanAbsClipByte,
        float MaxAbsClipByte,
        FloatDeltaRgbComparisonMetrics CorrectedRawMetrics,
        double CorrectedEyesRawMae,
        double CorrectedMouthRawMae);

    private sealed record MorphAffinePlausibilityStats(
        double Scale,
        double Bias,
        double InRangePercent,
        double MeanAbsByteDelta,
        float MaxAbsByteDelta,
        double MeanAbsClipByte,
        float MaxAbsClipByte,
        FloatDeltaRgbComparisonMetrics CorrectedRawMetrics,
        double CorrectedEyesRawMae,
        double CorrectedMouthRawMae);

    private sealed record MorphRowSimilarityStats(
        double Cosine,
        double Correlation,
        double TargetMae,
        double GainFitMae,
        double AffineFitMae,
        double GainExplainedPercent,
        double AffineExplainedPercent,
        double Gain,
        double AffineScale,
        double AffineBias);

    private sealed record MorphNearestOtherRowStats(
        int MorphIndex,
        MorphRowSimilarityStats Stats);

    private sealed record MorphChannelSimilarityStats(
        double Cosine,
        double Correlation,
        double TargetMae,
        double AffineFitMae,
        double AffineExplainedPercent,
        double AffineScale,
        double AffineBias);

    private sealed record MorphNearestOtherChannelCandidate(
        int MorphIndex,
        MorphChannelSimilarityStats Stats,
        double VsSelfPercent);

    private sealed record MorphNearestOtherRowPerChannelStats(
        MorphNearestOtherChannelCandidate Red,
        MorphNearestOtherChannelCandidate Green,
        MorphNearestOtherChannelCandidate Blue,
        MorphRowSimilarityStats MixedStats);

    private sealed record CrossNpcRequiredRow(
        int MorphIndex,
        sbyte[] RequiredR,
        sbyte[] RequiredG,
        sbyte[] RequiredB,
        string? SourcePath = null);

    private sealed record CrossNpcRequiredRowSimilarity(
        double Cosine,
        double Correlation,
        double MeanAbsoluteDifference,
        double AffineFitMae,
        double AffineScale,
        double AffineBias);

    private sealed record ExternalHeadEgtCandidate(
        string Path,
        EgtParser Egt);

    private sealed record ExternalHeadEgtRowMatch(
        string Path,
        string FullPath,
        int MorphIndex,
        CrossNpcRequiredRowSimilarity Stats,
        EgtMorph Morph);

    private sealed record InspectNpcState(
        int Cols,
        int Rows,
        (float[] R, float[] G, float[] B) CurrentNative,
        (float[] R, float[] G, float[] B) ShippedDecoded,
        double CurrentRawMae,
        double CurrentEyesRawMae,
        double CurrentMouthRawMae,
        Dictionary<int, InspectMorphState> Morphs);

    private sealed record InspectMorphState(
        int MorphIndex,
        EgtMorph SourceMorph,
        float Factor);

    private sealed record ExternalDonorApplyStats(
        FloatDeltaRgbComparisonMetrics RawMetrics,
        double EyesRawMae,
        double MouthRawMae);

    private sealed record ExternalDonorBlendFit(
        double CoefficientA,
        double CoefficientB,
        double Bias,
        double RowMae,
        sbyte[] DeltaR,
        sbyte[] DeltaG,
        sbyte[] DeltaB);

    private sealed record ExternalDonorBlendStats(
        double CoefficientA,
        double CoefficientB,
        double Bias,
        double RowMae,
        ExternalDonorApplyStats ApplyStats);

    private sealed record PrincipalComponentSet(
        double[] Eigenvalues,
        double[][] Eigenvectors);

    private sealed record AxisProjectionRange(
        double Min,
        double Max);

    private sealed record RawDeltaChannelFreeFitResult(
        int[] QuantizedCoefficient256R,
        int[] QuantizedCoefficient256G,
        int[] QuantizedCoefficient256B,
        float[] FittedR,
        float[] FittedG,
        float[] FittedB,
        FloatDeltaRgbComparisonMetrics FittedRawMetrics);

    internal sealed record ShippedNpcFaceTexture(
        uint FormId,
        string PluginName,
        string VirtualPath,
        string? ArchivePath);
}
