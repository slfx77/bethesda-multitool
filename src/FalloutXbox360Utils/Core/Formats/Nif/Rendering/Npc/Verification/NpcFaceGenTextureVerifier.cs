using System.Globalization;
using FalloutXbox360Utils.CLI;
using FalloutXbox360Utils.Core.Formats.Bsa;
using FalloutXbox360Utils.Core.Formats.Dds;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Assets;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Textures;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Verification;

internal static class NpcFaceGenTextureVerifier
{
    private const string FacemodsRoot = @"textures\characters\facemods\";

    internal static IReadOnlyDictionary<uint, ShippedNpcFaceTexture> DiscoverShippedFaceTextures(
        IEnumerable<string> textureBsaPaths,
        string pluginName)
    {
        var discovered = new Dictionary<uint, ShippedNpcFaceTexture>();

        foreach (var textureBsaPath in textureBsaPaths)
        {
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

        if (!fileName.EndsWith("_0" + extension, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var formIdText = fileName[..^($"{extension}".Length + 2)];
        if (!uint.TryParse(
                formIdText,
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out var formId))
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
            egt = NpcRenderHelpers.LoadEgtFromBsa(egtPath, meshArchives);
            egtCache[egtPath] = egt;
        }

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
                    FaceGenTextureMorpher.TextureAccumulationMode.EngineQuantized256);
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
                    FaceGenTextureMorpher.TextureAccumulationMode.EngineQuantized256);
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

            var genQuantized = FaceGenTextureMorpher.BuildNativeDeltaTexture(
                egt, coeffs,
                FaceGenTextureMorpher.TextureAccumulationMode.EngineQuantized256);
            if (genQuantized != null)
            {
                var qMetrics = NpcTextureComparison.CompareRgb(
                    genQuantized.Pixels, shippedDecodedTexture.Pixels,
                    genQuantized.Width, genQuantized.Height);
                Console.WriteLine(
                    $"  DIAG 0x{appearance.NpcFormId:X8}: Quantized MAE={qMetrics.MeanAbsoluteRgbError:F4} max={qMetrics.MaxAbsoluteRgbError}");

                // Per-region signed analysis
                DumpRegionMetrics(appearance.NpcFormId, genQuantized, shippedDecodedTexture);

                // Per-morph ablation: remove one morph at a time, measure MAE change
                Console.WriteLine($"  MORPH-ABLATION 0x{appearance.NpcFormId:X8}:");
                var fullMae = qMetrics.MeanAbsoluteRgbError;
                for (var mi = 0; mi < Math.Min(coeffs.Length, egt.SymmetricMorphs.Length); mi++)
                {
                    var ablatedCoeffs = (float[])coeffs.Clone();
                    ablatedCoeffs[mi] = 0f;
                    var ablated = FaceGenTextureMorpher.BuildNativeDeltaTexture(
                        egt, ablatedCoeffs,
                        FaceGenTextureMorpher.TextureAccumulationMode.EngineQuantized256);
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
                        Console.WriteLine(
                            $"    [{mi:D2}] MAE={ablatedMetrics.MeanAbsoluteRgbError:F4} (Δ={delta:+0.0000;-0.0000}) max={ablatedMetrics.MaxAbsoluteRgbError}  coeff={coeffs[mi]:F4} scale={egt.SymmetricMorphs[mi].Scale:F4}  sR={ablatedSigned.MeanSignedRedError:F3} sG={ablatedSigned.MeanSignedGreenError:F3} sB={ablatedSigned.MeanSignedBlueError:F3}");
                    }
                }
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
                PixelsWithRgbErrorAbove8 = metrics.PixelsWithRgbErrorAbove8
            },
            generatedTexture,
            shippedDecodedTexture);
    }

    private static string? GetHeadTexturePath(string? headDiffuseOverride)
    {
        if (string.IsNullOrWhiteSpace(headDiffuseOverride))
        {
            return null;
        }

        return NifTexturePathUtility.Normalize(headDiffuseOverride);
    }

    internal sealed record NpcFaceGenTextureVerificationResult
    {
        public required uint FormId { get; init; }
        public required string PluginName { get; init; }
        public string? EditorId { get; init; }
        public string? FullName { get; init; }
        public required string ShippedTexturePath { get; init; }
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
        public string? FailureReason { get; init; }

        public bool Verified => FailureReason == null;
        public bool ExactMatch => Verified && PixelsWithAnyRgbDifference == 0;
    }

    internal sealed record NpcFaceGenTextureVerificationDetail(
        NpcFaceGenTextureVerificationResult Result,
        DecodedTexture? GeneratedTexture,
        DecodedTexture? ShippedTexture);

    internal sealed record ShippedNpcFaceTexture(
        uint FormId,
        string PluginName,
        string VirtualPath,
        string? ArchivePath);

    private static void DumpRegionMetrics(
        uint formId,
        DecodedTexture generated,
        DecodedTexture shipped,
        string prefix = "    REGION")
    {
        // Regions defined as (name, x, y, w, h) in pixel coords for 256x256
        var w = generated.Width;
        var h = generated.Height;
        (string Name, int X, int Y, int W, int H)[] regions =
        [
            ("eyes", 72 * w / 256, 64 * w / 256, 112 * w / 256, 40 * h / 256),
            ("left_eye", 76 * w / 256, 68 * h / 256, 40 * w / 256, 28 * h / 256),
            ("right_eye", 140 * w / 256, 68 * h / 256, 40 * w / 256, 28 * h / 256),
            ("mouth", 88 * w / 256, 120 * h / 256, 80 * w / 256, 56 * h / 256),
            ("nose", 104 * w / 256, 88 * h / 256, 48 * w / 256, 36 * h / 256),
            ("forehead", 80 * w / 256, 24 * h / 256, 96 * w / 256, 40 * h / 256),
            ("background", 0, 0, 40 * w / 256, 40 * h / 256),
            ("whole", 0, 0, w, h)
        ];

        foreach (var (name, rx, ry, rw, rh) in regions)
        {
            var genCrop = NpcTextureComparison.Crop(generated, rx, ry, rw, rh);
            var shipCrop = NpcTextureComparison.Crop(shipped, rx, ry, rw, rh);
            var signed = NpcTextureComparison.CompareSignedRgb(
                genCrop.Pixels, shipCrop.Pixels, rw, rh);
            var unsigned = NpcTextureComparison.CompareRgb(
                genCrop.Pixels, shipCrop.Pixels, rw, rh);
            Console.WriteLine(
                $"{prefix} {name,12}: MAE={unsigned.MeanAbsoluteRgbError:F3} max={unsigned.MaxAbsoluteRgbError,3}  signedR={signed.MeanSignedRedError,7:F3} signedG={signed.MeanSignedGreenError,7:F3} signedB={signed.MeanSignedBlueError,7:F3}  absR={signed.MeanAbsoluteRedError:F3} absG={signed.MeanAbsoluteGreenError:F3} absB={signed.MeanAbsoluteBlueError:F3}");
        }
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
}
