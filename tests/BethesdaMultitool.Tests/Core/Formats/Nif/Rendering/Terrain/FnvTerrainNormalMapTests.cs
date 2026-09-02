using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Tests.Core.Formats.Esm;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Terrain;

/// <summary>
///     Focused contracts for the recovered Fallout: New Vegas layered LAND normal-map pass. The
///     renderer is Windows-only, so executable CPU vectors independently reproduce the recovered
///     decode/blend equations while source and shader-compilation checks protect the D3D12 wiring.
/// </summary>
[Trait("Category", TestCategories.ShaderCompile)]
public sealed class FnvTerrainNormalMapTests
{
    public enum BrokenNormalChain
    {
        MissingLtex,
        MissingTnam,
        MissingTxst,
        NullTx01,
        EmptyTx01,
        WhitespaceTx01
    }

    private const uint LtexFormId = 0x100;
    private const uint TxstFormId = 0x200;
    private const string NormalPath = @"textures\landscape\nvdesertgrass_n.dds";

    [Fact]
    public void ResolveNormal_WalksLtexTnamToTxstSlotOne()
    {
        var ltex = new Dictionary<uint, LandscapeTextureRecord>
        {
            [LtexFormId] = new()
            {
                FormId = LtexFormId,
                TextureSetFormId = TxstFormId,
                IconPath = @"legacy\must-not-be-used.dds"
            }
        };
        var txst = new Dictionary<uint, TextureSetRecord>
        {
            [TxstFormId] = new()
            {
                FormId = TxstFormId,
                DiffuseTexture = @"textures\landscape\nvdesertgrass.dds",
                NormalTexture = NormalPath,
                EnvironmentTexture = @"textures\landscape\wrong-slot.dds"
            }
        };

        Assert.Equal(NormalPath, LandscapeTexturePathResolver.ResolveNormal(LtexFormId, ltex, txst));
    }

    [Theory]
    [InlineData(BrokenNormalChain.MissingLtex)]
    [InlineData(BrokenNormalChain.MissingTnam)]
    [InlineData(BrokenNormalChain.MissingTxst)]
    [InlineData(BrokenNormalChain.NullTx01)]
    [InlineData(BrokenNormalChain.EmptyTx01)]
    [InlineData(BrokenNormalChain.WhitespaceTx01)]
    public void ResolveNormal_BrokenOrMissingChainReturnsNullWithoutIconGuess(BrokenNormalChain broken)
    {
        var ltex = new Dictionary<uint, LandscapeTextureRecord>();
        var txst = new Dictionary<uint, TextureSetRecord>();

        if (broken != BrokenNormalChain.MissingLtex)
        {
            ltex[LtexFormId] = new LandscapeTextureRecord
            {
                FormId = LtexFormId,
                TextureSetFormId = broken == BrokenNormalChain.MissingTnam ? null : TxstFormId,
                IconPath = @"legacy\must-not-be-used.dds"
            };
        }

        if (broken is not (BrokenNormalChain.MissingLtex or
            BrokenNormalChain.MissingTnam or BrokenNormalChain.MissingTxst))
        {
            txst[TxstFormId] = new TextureSetRecord
            {
                FormId = TxstFormId,
                NormalTexture = broken switch
                {
                    BrokenNormalChain.NullTx01 => null,
                    BrokenNormalChain.EmptyTx01 => string.Empty,
                    BrokenNormalChain.WhitespaceTx01 => "   ",
                    _ => throw new ArgumentOutOfRangeException(nameof(broken))
                }
            };
        }

        Assert.Null(LandscapeTexturePathResolver.ResolveNormal(LtexFormId, ltex, txst));
    }

    [Fact]
    public void EngineDefaultSentinelUsesTheFNVProfileNormalWhileMissingAuthoredNormalStaysFlat()
    {
        Assert.Equal(0u, CellLayerWeightTable.EngineDefaultSentinelFormId);
        Assert.Equal(
            @"textures\landscape\DirtWasteland01_N.dds",
            EngineDefaultLandscapeTexture.NormalFor(BethesdaGame.FalloutNewVegas));

        Assert.Null(LandscapeTexturePathResolver.ResolveNormal(
            CellLayerWeightTable.EngineDefaultSentinelFormId,
            new Dictionary<uint, LandscapeTextureRecord>(),
            new Dictionary<uint, TextureSetRecord>()));
    }

    [Fact]
    public void TwoLayerVector_DecodesAccumulatesAndNormalizesWithTheSameWeights()
    {
        var actual = BlendPackedNormals(
            [new Vector3(0.8f, 0.6f, 0.9f), new Vector3(0.4f, 0.8f, 0.875f)],
            [0.25f, 0.75f]);

        AssertVector(new Vector3(0f, 0.54835695f, 0.83624434f), actual);
    }

    [Fact]
    public void FiveLayerVector_UsesEveryLayerBeyondTheLegacyFourSlotCeiling()
    {
        var actual = BlendPackedNormals(
            [
                new Vector3(0.75f, 0.55f, 0.93f),
                new Vector3(0.30f, 0.65f, 0.925f),
                new Vector3(0.60f, 0.25f, 0.91f),
                new Vector3(0.45f, 0.80f, 0.89f),
                new Vector3(0.65f, 0.60f, 0.95f)
            ],
            [0.05f, 0.10f, 0.20f, 0.25f, 0.40f]);

        AssertVector(new Vector3(0.137737f, 0.18938838f, 0.97219366f), actual);

        var firstFourOnly = BlendPackedNormals(
            [
                new Vector3(0.75f, 0.55f, 0.93f),
                new Vector3(0.30f, 0.65f, 0.925f),
                new Vector3(0.60f, 0.25f, 0.91f),
                new Vector3(0.45f, 0.80f, 0.89f)
            ],
            [0.05f, 0.10f, 0.20f, 0.25f]);
        Assert.True(Vector3.Distance(actual, firstFourOnly) > 0.10f);
    }

    [Fact]
    public void SixLayerVector_UsesEveryLayerAndKeepsDirectXGreenUnchanged()
    {
        var packed = new Vector3?[]
        {
            new(0.20f, 0.55f, 0.89f),
            new(0.70f, 0.75f, 0.875f),
            new(0.60f, 0.35f, 0.95f),
            new(0.45f, 0.85f, 0.85f),
            new(0.75f, 0.30f, 0.88f),
            new(0.375f, 0.60f, 0.96f)
        };
        var weights = new[] { 0.05f, 0.10f, 0.15f, 0.20f, 0.20f, 0.30f };

        var actual = BlendPackedNormals(packed, weights);
        AssertVector(new Vector3(0.05431496f, 0.15690988f, 0.9861182f), actual);
        Assert.True(actual.Y > 0f);

        var incorrectlyGreenFlipped = BlendPackedNormals(
            packed.Select(sample => sample is Vector3 value
                ? new Vector3(value.X, 1f - value.Y, value.Z)
                : (Vector3?)null).ToArray(),
            weights);
        Assert.True(Vector3.Distance(actual, incorrectlyGreenFlipped) > 0.30f);
    }

    [Fact]
    public void MissingAndPackedFlatNormalsAreExactTangentSpaceIdentity()
    {
        var actual = BlendPackedNormals(
            [null, new Vector3(0.5f, 0.5f, 1.0f), null],
            [0.2f, 0.3f, 0.5f]);

        Assert.Equal(Vector3.UnitZ, actual);
    }

    [Fact]
    public void Bc5DecodeReconstructsPositiveZWhileRetailRgbDecodePreservesAuthoredBlue()
    {
        var packed = new Vector3(0.75f, 0.25f, 0.0f);

        var retailRgb = DecodePackedNormal(packed, false);
        var bc5 = DecodePackedNormal(packed, true);

        Assert.Equal(new Vector3(0.5f, -0.5f, -1.0f), retailRgb);
        Assert.InRange(MathF.Abs(bc5.X - 0.5f), 0f, 1e-6f);
        Assert.InRange(MathF.Abs(bc5.Y + 0.5f), 0f, 1e-6f);
        Assert.InRange(MathF.Abs(bc5.Z - 0.70710677f), 0f, 1e-6f);
        Assert.True(bc5.Z > 0f);
    }

    [Fact]
    public void TerrainBasisMapsTangentAxesOntoTheExpectedNonFlatHeightfieldSlopes()
    {
        var geometricNormal = new Vector3(0.3f, -0.4f, 0.8660254f);

        var flat = TerrainTangentToWorld(Vector3.UnitZ, geometricNormal);
        var tangentX = TerrainTangentToWorld(Vector3.UnitX, geometricNormal);
        var tangentY = TerrainTangentToWorld(Vector3.UnitY, geometricNormal);

        AssertVector(new Vector3(0.3f, -0.4f, 0.8660254f), flat);
        AssertVector(new Vector3(0.9449111f, 0f, -0.32732683f), tangentX);
        AssertVector(new Vector3(0.13093075f, 0.9165152f, 0.37796453f), tangentY);
        Assert.InRange(MathF.Abs(Vector3.Dot(flat, tangentX)), 0f, 1e-5f);
        Assert.InRange(MathF.Abs(Vector3.Dot(flat, tangentY)), 0f, 1e-5f);
        AssertVector(tangentY, Vector3.Normalize(Vector3.Cross(flat, tangentX)));
    }

    [Fact]
    public void TerrainShaderCompilesAndCarriesSixteenWeightedFNVNormalSlotsThroughTheBasis()
    {
        var source = ReadEmbeddedShader("terrain_textured.frag.hlsl");
        var perCell = Slice(source, "cbuffer PerCell : register(b1)", "cbuffer PerMode : register(b2)");
        var decode = Slice(source, "float3 DecodeTerrainNormal(", "struct PSInput");
        var main = source[source.IndexOf("float4 main(PSInput input)", StringComparison.Ordinal)..];

        var diffuseIndices = perCell.IndexOf("uint4 uTextureIndices[4];", StringComparison.Ordinal);
        var normalIndices = perCell.IndexOf("uint4 uNormalTextureIndices[4];", StringComparison.Ordinal);
        var decodeMetadata = perCell.IndexOf("uint4 uNormalDecodeMetadata;", StringComparison.Ordinal);
        Assert.True(diffuseIndices >= 0);
        Assert.True(normalIndices > diffuseIndices);
        Assert.True(decodeMetadata > normalIndices);

        Assert.Contains("static const uint MissingNormalTextureIndex = 0xffffffffu;", source,
            StringComparison.Ordinal);
        Assert.Contains("float3 decoded = float3(0.0, 0.0, 1.0);", decode,
            StringComparison.Ordinal);
        Assert.Contains("if (textureIndex != MissingNormalTextureIndex)", decode,
            StringComparison.Ordinal);
        Assert.Contains("return decoded;", decode, StringComparison.Ordinal);
        Assert.Contains("float3 packed = textures[NonUniformResourceIndex(textureIndex)].Sample(sDiffuse, uv).rgb;",
            decode, StringComparison.Ordinal);
        Assert.Contains("(uNormalDecodeMetadata.x & (1u << slot)) != 0u", decode,
            StringComparison.Ordinal);
        Assert.Contains("(uNormalDecodeMetadata.y & (1u << slot)) != 0u", decode,
            StringComparison.Ordinal);
        Assert.Contains("float2 xy = signedBc5 ? packed.rg : packed.rg * 2.0 - 1.0;", decode,
            StringComparison.Ordinal);
        Assert.Contains("float3(xy, sqrt(saturate(1.0 - dot(xy, xy))))", decode,
            StringComparison.Ordinal);
        Assert.Contains(": packed * 2.0 - 1.0;", decode, StringComparison.Ordinal);
        Assert.DoesNotContain("1.0 - packed.g", decode, StringComparison.Ordinal);
        Assert.DoesNotContain("mapN.y = -mapN.y", decode, StringComparison.Ordinal);

        Assert.Contains("bool useTerrainNormals = uDebugMode_UvScale_Pad.w > 0.5;", main,
            StringComparison.Ordinal);
        Assert.Contains("float wt = weights[g][c];", main, StringComparison.Ordinal);
        Assert.Contains("color += wt * textures[NonUniformResourceIndex(ti)]", main,
            StringComparison.Ordinal);
        Assert.Contains("tangentNormalSum += wt * DecodeTerrainNormal(", main,
            StringComparison.Ordinal);
        Assert.Contains("uNormalTextureIndices[g][c]", main, StringComparison.Ordinal);
        Assert.Contains("uint slot = (uint)(g * 4 + c);", main, StringComparison.Ordinal);
        Assert.Contains("uNormalTextureIndices[g][c], slot, input.vWorldUv", main,
            StringComparison.Ordinal);
        Assert.Contains("tangentNormalSum * rsqrt(normalLengthSquared)", main,
            StringComparison.Ordinal);

        Assert.Contains("float3 N = normalize(geometricNormal);", decode, StringComparison.Ordinal);
        Assert.Contains("float3 T = float3(N.z, 0.0, -N.x);", decode, StringComparison.Ordinal);
        Assert.Contains("float3 B = normalize(cross(N, T));", decode, StringComparison.Ordinal);
        Assert.Contains(
            "return normalize(tangentNormal.x * T + tangentNormal.y * B + tangentNormal.z * N);",
            decode,
            StringComparison.Ordinal);
        Assert.True(
            main.IndexOf("normal = TerrainTangentToWorld(tangentNormal, normal);", StringComparison.Ordinal) <
            main.IndexOf("float3 shade = AtmosphereLight(normal, input.vWorldPos, sunShadow);",
                StringComparison.Ordinal));

        CompileTerrainFragmentShader(source);
    }

    [Fact]
    public void PerCellNormalIndicesPreserveDiffuseOffsetsAndStillConsumeOneRingStride()
    {
        var cachedCell = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
            "CachedCellMesh12.cs");
        var renderer = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
            "TerrainRenderer12.cs");
        var ring = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Gpu", "D3D12",
            "GpuRingBuffer12.cs");

        Assert.Equal(16, CellTerrainTextureSet.MaxSlots);
        Assert.Contains("[StructLayout(LayoutKind.Sequential)]", cachedCell, StringComparison.Ordinal);
        var diffuseField = cachedCell.IndexOf(
            "public fixed uint Index[CellTerrainTextureSet.MaxSlots];", StringComparison.Ordinal);
        var normalField = cachedCell.IndexOf(
            "public fixed uint NormalIndex[CellTerrainTextureSet.MaxSlots];", StringComparison.Ordinal);
        var metadataField = cachedCell.IndexOf(
            "public fixed uint NormalDecodeMetadata[4];", StringComparison.Ordinal);
        Assert.True(diffuseField >= 0);
        Assert.True(normalField > diffuseField);
        Assert.True(metadataField > normalField);

        var perDrawBytes = ReadUnsignedConstant(renderer, "PerDrawByteSize");
        var ringStride = ReadUnsignedConstant(ring, "CbAlignment");
        var diffuseBytes = (uint)(CellTerrainTextureSet.MaxSlots * sizeof(uint));
        var normalOffset = diffuseBytes;
        var metadataOffset = normalOffset + diffuseBytes;

        Assert.Equal(64u, diffuseBytes);
        Assert.Equal(64u, normalOffset);
        Assert.Equal(128u, metadataOffset);
        Assert.Equal(144u, perDrawBytes);
        Assert.Equal(256u, ringStride);
        Assert.True(perDrawBytes <= ringStride);
        Assert.Equal(1u, DivideRoundUp(64u, ringStride));
        Assert.Equal(1u, DivideRoundUp(perDrawBytes, ringStride));

        Assert.Contains(
            "TryAllocate(_recorder.FrameIndex, PerDrawByteSize, out var perDrawAlloc, GpuRingBuffer12.CbAlignment)",
            renderer,
            StringComparison.Ordinal);
        Assert.Contains("*(TerrainTextureIndices*)perDrawAlloc.CpuPtr = textureIndices;", renderer,
            StringComparison.Ordinal);
        Assert.Contains(
            "public required GpuTextureCache12.Entry?[]? NormalTextureEntries { get; init; }",
            cachedCell,
            StringComparison.Ordinal);
        Assert.Equal(2,
            renderer.Split("NormalTextureEntries = normalTextureEntries,").Length - 1);

        var shader = ReadEmbeddedShader("terrain_textured.frag.hlsl");
        var perCell = Slice(shader, "cbuffer PerCell : register(b1)", "cbuffer PerMode : register(b2)");
        Assert.True(
            perCell.IndexOf("uint4 uTextureIndices[4];", StringComparison.Ordinal) <
            perCell.IndexOf("uint4 uNormalTextureIndices[4];", StringComparison.Ordinal));
        Assert.True(
            perCell.IndexOf("uint4 uNormalTextureIndices[4];", StringComparison.Ordinal) <
            perCell.IndexOf("uint4 uNormalDecodeMetadata;", StringComparison.Ordinal));
    }

    [Fact]
    public void TerrainNormalHostRoutingIsFNVOnlyAndPopulatesEverySlotWithAFlatSafeFallback()
    {
        var resolver = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
            "TerrainTextureResolver12.cs");
        var renderer = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
            "TerrainRenderer12.cs");
        var textureCache = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Gpu", "D3D12",
            "GpuTextureCache12.cs");

        var enabledProperty = Slice(
            resolver,
            "public bool LandscapeNormalMappingEnabled =>",
            "public GpuTextureCache12.Entry? EngineDefaultNormal");
        // FO3 parity 2026-08-10: the landscape shader family is byte-identical between FO3 and
        // FNV (all 16 packages), so the pass is enabled for the classic pair and no one else.
        Assert.Contains(
            "_game is BethesdaGame.FalloutNewVegas",
            enabledProperty,
            StringComparison.Ordinal);
        Assert.Contains(
            "or BethesdaGame.Fallout3;",
            enabledProperty,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Oblivion", enabledProperty, StringComparison.Ordinal);
        Assert.DoesNotContain("Skyrim", enabledProperty, StringComparison.Ordinal);
        Assert.DoesNotContain("Fallout4", enabledProperty, StringComparison.Ordinal);

        var normalResolver = Slice(
            resolver,
            "public GpuTextureCache12.Entry? ResolveLandscapeNormal(uint ltexFormId)",
            "public uint? ResolveNormalMapBindlessIndex");
        Assert.Contains("if (!LandscapeNormalMappingEnabled) return null;", normalResolver,
            StringComparison.Ordinal);
        Assert.Contains(
            "LandscapeTexturePathResolver.ResolveNormal(ltexFormId, _ltexByFormId, _txstByFormId)",
            normalResolver,
            StringComparison.Ordinal);
        Assert.Contains("path is null ? null : _textureCache.GetOrUpload(path, true)",
            normalResolver,
            StringComparison.Ordinal);

        var slotRouting = Slice(
            renderer,
            "private TerrainTextureIndices ResolveSlotTextureIndices(",
            "private long StartTiming()");
        Assert.Contains(
            "for (var slot = 0; slot < CellTerrainTextureSet.MaxSlots; slot++)",
            slotRouting,
            StringComparison.Ordinal);
        Assert.Equal(2,
            slotRouting.Split("indices.NormalIndex[slot] =").Length - 1);
        Assert.Contains("? _textureResolver.ResolveLandscapeNormal(set!.SlotFormIds[slot])", slotRouting,
            StringComparison.Ordinal);
        Assert.Contains(": _textureResolver.EngineDefaultNormal;", slotRouting, StringComparison.Ordinal);
        Assert.Contains("indices.NormalIndex[slot] = normalEntry?.BindlessIndex ?? uint.MaxValue;",
            slotRouting,
            StringComparison.Ordinal);
        Assert.Contains("normalTextureEntries![slot] = normalEntry;", slotRouting,
            StringComparison.Ordinal);
        Assert.Contains("normalTextureEntries = _textureResolver.LandscapeNormalMappingEnabled",
            slotRouting,
            StringComparison.Ordinal);
        Assert.Contains("indices.NormalIndex[slot] = uint.MaxValue;", slotRouting,
            StringComparison.Ordinal);

        var drawCell = Slice(renderer, "private void DrawCell(", "private CachedCellMesh12? GetOrUploadMesh(");
        Assert.Contains("BuildNormalBc5Mask(entry.NormalTextureEntries)", drawCell,
            StringComparison.Ordinal);
        Assert.Contains("BuildNormalBc5SignedMask(entry.NormalTextureEntries)", drawCell,
            StringComparison.Ordinal);
        var decodeMask = Slice(renderer, "private static uint BuildNormalBc5Mask(", "private long StartTiming()");
        Assert.Contains(
            "normalTextureEntries[slot]?.NormalDecodeMode == GpuNormalDecodeMode.Bc5ReconstructZ",
            decodeMask,
            StringComparison.Ordinal);
        Assert.Contains(
            "normalTextureEntries[slot]?.NormalDecodeMode == GpuNormalDecodeMode.Bc5SignedReconstructZ",
            decodeMask,
            StringComparison.Ordinal);
        Assert.Contains("mask |= 1u << slot;", decodeMask, StringComparison.Ordinal);

        Assert.Equal(16u, ReadUnsignedConstant(renderer, "PerModeByteSize"));
        var perModeUpload = Slice(renderer, "// Per-mode CB (b2):", "cmd.IASetPrimitiveTopology(");
        Assert.Contains("_textureResolver.LandscapeNormalMappingEnabled ? 1f : 0f",
            perModeUpload,
            StringComparison.Ordinal);

        var cacheRoute = Slice(
            textureCache,
            "public Entry GetOrUpload(string path, bool isNormalMap = false)",
            "internal static string NormalizeCacheKey");
        Assert.Contains("var fallback = isNormalMap ? FlatNormal : WhitePixel;", cacheRoute,
            StringComparison.Ordinal);
        Assert.Contains("var entry = _solidTextureFactory.CreatePlaceholder(fallback, cacheKey);", cacheRoute,
            StringComparison.Ordinal);
        // The initializer routes through CreatePinnedSolid (which counts pinned bytes into the
        // resident total) — the pinned fact is the flat-normal COLOUR (128,128,255 = +Z identity).
        Assert.Contains(
            "public Entry FlatNormal => _flatNormal ??= CreatePinnedSolid(128, 128, 255, 255);",
            textureCache,
            StringComparison.Ordinal);
    }

    private static Vector3 BlendPackedNormals(
        Vector3?[] packedNormals,
        float[] weights)
    {
        Assert.Equal(packedNormals.Length, weights.Length);
        var sum = Vector3.Zero;
        for (var i = 0; i < weights.Length; i++)
        {
            // Exact recovered DirectX decode. A missing TX01 contributes flat identity and does not
            // steal or alter the diffuse layer's independently authored blend weight.
            var decoded = packedNormals[i] is Vector3 packed
                ? packed * 2f - Vector3.One
                : Vector3.UnitZ;
            sum += decoded * weights[i];
        }

        return sum.LengthSquared() > 1e-8f ? Vector3.Normalize(sum) : Vector3.UnitZ;
    }

    private static Vector3 DecodePackedNormal(Vector3 packed, bool reconstructZ)
    {
        var xy = new Vector2(packed.X * 2f - 1f, packed.Y * 2f - 1f);
        return reconstructZ
            ? new Vector3(xy, MathF.Sqrt(MathF.Max(1f - Vector2.Dot(xy, xy), 0f)))
            : packed * 2f - Vector3.One;
    }

    private static Vector3 TerrainTangentToWorld(Vector3 tangentNormal, Vector3 geometricNormal)
    {
        tangentNormal = tangentNormal.LengthSquared() > 1e-8f
            ? Vector3.Normalize(tangentNormal)
            : Vector3.UnitZ;
        var normal = Vector3.Normalize(geometricNormal);
        var tangent = new Vector3(normal.Z, 0f, -normal.X);
        tangent = tangent.LengthSquared() > 1e-8f ? Vector3.Normalize(tangent) : Vector3.UnitX;
        var bitangent = Vector3.Normalize(Vector3.Cross(normal, tangent));
        return Vector3.Normalize(
            tangentNormal.X * tangent + tangentNormal.Y * bitangent + tangentNormal.Z * normal);
    }

    private static void AssertVector(Vector3 expected, Vector3 actual)
    {
        Assert.InRange(MathF.Abs(expected.X - actual.X), 0f, 1e-5f);
        Assert.InRange(MathF.Abs(expected.Y - actual.Y), 0f, 1e-5f);
        Assert.InRange(MathF.Abs(expected.Z - actual.Z), 0f, 1e-5f);
        Assert.InRange(actual.Length(), 0.99999f, 1.00001f);
    }

    /// <summary>
    ///     Compiles possibly-mutated terrain shader text through the PRODUCTION compiler.
    ///     <para>
    ///         Now GATED. This was the one place that ran a real 373-line FXC compile in the DEFAULT
    ///         suite, with its own always-on flag constant and its own <c>Compiler.Compile</c> call —
    ///         a fourth variant of the flag decision, and by far the most expensive single operation
    ///         in a run that is supposed to stay fast. Gating it costs nothing in coverage: CI now
    ///         sets <c>RUN_SHADER_COMPILE_TESTS=1</c>, and <c>EveryShaderPermutationCompiles</c>
    ///         compiles the unmutated shader regardless.
    ///     </para>
    /// </summary>
    private static void CompileTerrainFragmentShader(string source)
    {
        ShaderCompileTestGuard.SkipUnlessEnabled();
        var bytecode = GpuShaderCompiler12.CompileSource(
            source, "terrain_textured.frag.hlsl", "main", "ps_5_1");
        Assert.NotEmpty(bytecode);
    }

    private static string ReadEmbeddedShader(string name)
    {
        return GpuShaderCompiler12.ReadSource(name);
    }

    private static string Slice(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing source marker: {startMarker}");
        Assert.True(end > start, $"Missing source marker after {startMarker}: {endMarker}");
        return source[start..end];
    }

    private static uint ReadUnsignedConstant(string source, string name)
    {
        var match = Regex.Match(
            source,
            $@"\b(?:public|private)\s+const\s+uint\s+{Regex.Escape(name)}\s*=\s*(?<value>\d+)\s*;",
            RegexOptions.CultureInvariant);
        Assert.True(match.Success, $"Could not locate uint constant {name}.");
        return uint.Parse(match.Groups["value"].Value, CultureInfo.InvariantCulture);
    }

    private static uint DivideRoundUp(uint value, uint alignment)
    {
        return (value + alignment - 1) / alignment;
    }
}

/// <summary>
///     Retail semantic census: every LTEX referenced by an authored PC-final LAND layer must retain
///     the shipped TNAM → TXST TX01 chain. This deliberately proves record semantics rather than
///     depending on a particular local BSA installation.
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class FnvTerrainNormalMapRetailTests(
    SampleFileFixture samples,
    ITestOutputHelper output)
{
    // 5,523 → 6,022 (2026-08-17): the parser's cell passes used to REPLACE cells[i] with `with`-
    // clones (terrain attach, enrichment, dedup) while other views/maps kept the pre-clone
    // instances, silently dropping ~499 cells' attached LandVisualData from the surviving graph.
    // Those passes now mutate in place, so every LAND-bearing tile keeps its authored layers —
    // the LTEX/normal-path pins below are unchanged, and the missing-list assert stays empty,
    // i.e. the extra cells resolve through the same authored LTEX set.
    private const int ExpectedLandCellCount = 6_022;
    private const int ExpectedLandUsedLtexCount = 81;
    private const int ExpectedDistinctNormalPathCount = 57;

    [Fact]
    public void EveryPcFinalLandUsedLtexResolvesItsAuthoredSlotOneNormal()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        Assert.SkipWhen(samples.PcFinalEsm is null, "PC final FalloutNV.esm not available");

        var collection = PcFinalEsmPipelineCache.GetOrBuild(samples.PcFinalEsm!).Collection;
        var landCellRecords = collection.Cells
            .Where(cell =>
                !cell.IsInterior &&
                !cell.IsPersistentCell &&
                cell.GridX.HasValue &&
                cell.GridY.HasValue &&
                cell.WorldspaceFormId.HasValue &&
                cell.LandVisualData?.TextureLayers.Any(layer =>
                    layer.TextureFormId != CellLayerWeightTable.EngineDefaultSentinelFormId) == true)
            .ToArray();
        // TerrainRenderer12 identifies a selected worldspace's cell by grid coordinate, so pin the
        // same worldspace/grid tile identity rather than raw CELL FormIDs (PC-final contains a small
        // number of duplicate records at one tile). The layer union still examines every retained
        // record so a texture reference cannot disappear behind de-duplication order. Persistent
        // containers and engine-default-only cells are not authored terrain draws.
        var distinctLandCellCount = landCellRecords
            .Select(cell => (
                Worldspace: cell.WorldspaceFormId!.Value,
                GridX: cell.GridX!.Value,
                GridY: cell.GridY!.Value))
            .Distinct()
            .Count();
        var usedLtexFormIds = landCellRecords
            .SelectMany(cell => cell.LandVisualData!.TextureLayers)
            .Select(layer => layer.TextureFormId)
            .Where(formId => formId != CellLayerWeightTable.EngineDefaultSentinelFormId)
            .Distinct()
            .Order()
            .ToArray();
        var ltexByFormId = collection.LandTextures
            .GroupBy(record => record.FormId)
            .ToDictionary(group => group.Key, group => group.Last());
        var txstByFormId = collection.TextureSets
            .GroupBy(record => record.FormId)
            .ToDictionary(group => group.Key, group => group.Last());

        var missing = new List<string>();
        var normalPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ltexFormId in usedLtexFormIds)
        {
            if (!ltexByFormId.TryGetValue(ltexFormId, out var ltex))
            {
                missing.Add($"LTEX 0x{ltexFormId:X8}: record missing");
                continue;
            }

            if (ltex.TextureSetFormId is not uint txstFormId)
            {
                missing.Add($"LTEX 0x{ltexFormId:X8}: TNAM missing");
                continue;
            }

            if (!txstByFormId.TryGetValue(txstFormId, out var txst))
            {
                missing.Add($"LTEX 0x{ltexFormId:X8}: TXST 0x{txstFormId:X8} missing");
                continue;
            }

            var resolved = LandscapeTexturePathResolver.ResolveNormal(
                ltexFormId,
                ltexByFormId,
                txstByFormId);
            if (resolved is null)
            {
                missing.Add($"LTEX 0x{ltexFormId:X8}: TXST 0x{txstFormId:X8} TX01 missing/blank");
                continue;
            }

            Assert.Equal(txst.NormalTexture, resolved);
            normalPaths.Add(resolved);
        }

        output.WriteLine(
            $"PC-final LAND normal census: {distinctLandCellCount:N0} unique cells " +
            $"({landCellRecords.Length:N0} records), " +
            $"{usedLtexFormIds.Length:N0} LTEX records, {normalPaths.Count:N0} distinct TX01 paths.");
        if (missing.Count > 0)
        {
            output.WriteLine(string.Join(Environment.NewLine, missing));
        }

        Assert.Equal(ExpectedLandCellCount, distinctLandCellCount);
        Assert.Equal(ExpectedLandUsedLtexCount, usedLtexFormIds.Length);
        Assert.Equal(ExpectedDistinctNormalPathCount, normalPaths.Count);
        Assert.Empty(missing);
    }
}
