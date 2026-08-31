using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Materials;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Materials;

/// <summary>
///     Pins the untextured-shape material-diffuse policy (OB-1): legacy (BsVersion &lt; 26)
///     NiMaterialProperty diffuse becomes the base color of untextured shapes — the confirmed case
///     is SewerExitGateExterior01's NiTriStrips 'Plane02' whose material 'black' authors
///     Ambient/Diffuse (0,0,0) but rendered lit near-white over the opaque-white fallback pixel.
///     Also source-pins the two invariants that keep the fix real: the decoded-mesh disk cache
///     version bump (the cache is consulted BEFORE decoding — without the bump the fix is a silent
///     no-op for every warm-cached mesh) and the bind-site scope (only the empty-DiffuseTexturePath
///     branch may synthesize a solid; textured shapes stay byte-identical).
/// </summary>
public sealed class NifMaterialDiffusePolicyTests
{
    [Fact]
    public void ResolveUntexturedBaseColor_NullMaterial_IsWhite()
    {
        // No NiMaterialProperty ⇒ the legacy white-fallback color. Null must NEVER read as black,
        // or every material-less untextured shape in every game would tint black.
        Assert.Equal(Vector3.One, NifMaterialDiffusePolicy.ResolveUntexturedBaseColor(null));
    }

    [Fact]
    public void ResolveUntexturedBaseColor_BlackMaterial_IsBlack()
    {
        // The confirmed sewer-exit-door case: material 'black', Diffuse (0,0,0).
        Assert.Equal(
            Vector3.Zero,
            NifMaterialDiffusePolicy.ResolveUntexturedBaseColor(Vector3.Zero));
    }

    [Fact]
    public void ResolveUntexturedBaseColor_ColoredMaterial_IsThatColorClamped()
    {
        var colored = new Vector3(0.25f, 0.5f, 0.75f);
        Assert.Equal(colored, NifMaterialDiffusePolicy.ResolveUntexturedBaseColor(colored));

        // Out-of-range authored values clamp to the displayable range.
        Assert.Equal(
            new Vector3(1f, 0f, 1f),
            NifMaterialDiffusePolicy.ResolveUntexturedBaseColor(new Vector3(2f, -1f, 1f)));
    }

    [Theory]
    [InlineData(0u)] // Morrowind/vanilla Gamebryo era
    [InlineData(11u)] // Oblivion era (the confirmed SewerExitGateExterior01 stream)
    [InlineData(25u)]
    public void Carry_LegacyStream_KeepsTheAuthoredDiffuse(uint bsVersion)
    {
        var authored = (R: 0.1f, G: 0.2f, B: 0.3f);
        Assert.Equal(
            authored, NifMaterialDiffusePolicy.Carry(bsVersion, authored));
        Assert.Null(NifMaterialDiffusePolicy.Carry(bsVersion, null));
    }

    [Theory]
    [InlineData(26u)] // First Bethesda stream without ambient/diffuse lanes
    [InlineData(34u)] // FO3/FNV
    [InlineData(83u)] // Skyrim
    public void Carry_BethesdaStream_IsNull(uint bsVersion)
    {
        // FO3+ NiMaterialProperty has no ambient/diffuse lanes; the version guard itself is the
        // era scope — no game check sits on top of it.
        Assert.Null(NifMaterialDiffusePolicy.Carry(bsVersion, (0.1f, 0.2f, 0.3f)));
    }

    [Fact]
    public void SyntheticTextureKey_AndPixel_EncodeTheResolvedColor()
    {
        Assert.Equal(
            "synthetic:nimaterial-diffuse:000000",
            NifMaterialDiffusePolicy.SyntheticTextureKey(Vector3.Zero));
        Assert.Equal(
            "synthetic:nimaterial-diffuse:FF8000",
            NifMaterialDiffusePolicy.SyntheticTextureKey(new Vector3(1f, 0.502f, 0f)));

        Assert.Equal(
            new byte[] { 0, 0, 0, 255 },
            NifMaterialDiffusePolicy.ToRgbaPixel(Vector3.Zero));
        Assert.Equal(
            new byte[] { 255, 128, 0, 255 },
            NifMaterialDiffusePolicy.ToRgbaPixel(new Vector3(1f, 0.502f, 0f)));
    }

    [Fact]
    public void DiskCache_DecoderVersion_WasBumpedForMaterialDiffuse()
    {
        // The decoded-mesh disk cache serializes positionally and is consulted BEFORE decoding;
        // without the v72 bump this fix was a silent no-op for every warm-cached mesh. The current
        // version may advance for later payload/decoder changes, but the v72 history must remain documented.
        var source = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Gpu", "D3D12",
            "ReferenceDecodedMeshDiskCache12.cs");

        // Assert the INVARIANT the comment above states, not the incidental current number. Pinning
        // the exact value made every unrelated payload bump fail this test — which is how it broke
        // on the v80 vertex-colour change, a change with nothing to do with material diffuse.
        const string marker = "internal const int DecoderVersion = ";
        var start = source.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, "DecoderVersion declaration not found");
        var end = source.IndexOf(';', start);
        var version = int.Parse(
            source[(start + marker.Length)..end].Trim(),
            System.Globalization.CultureInfo.InvariantCulture);

        Assert.True(version >= 72,
            $"DecoderVersion is {version}; the v72 material-diffuse bump must not be reverted");
        Assert.Contains("v72: untextured legacy (BsVersion < 26)", source, StringComparison.Ordinal);
        Assert.Contains("MaterialDiffuse", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MeshCache_SynthesizesTheDiffuse_OnlyInTheEmptyDiffusePathBranch()
    {
        var source = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
            "ReferenceMeshCache12.cs");

        // Exactly one synthetic bind in the whole mesh cache, and it sits inside the
        // empty-DiffuseTexturePath branch, before the material-less WhitePixel fallback and the
        // textured GetOrUpload route. Texture-lifetime wrappers around either acquisition are not
        // part of this policy contract.
        Assert.Equal(1, SourceContract.CountOccurrences(source, "GetOrCreateSynthetic"));
        SourceContract.AssertOrder(
            source,
            "GpuTextureCache12.Entry diffuse;",
            "if (string.IsNullOrEmpty(sub.DiffuseTexturePath))",
            "sub.MaterialDiffuse is { } materialDiffuse",
            "GetOrCreateSynthetic",
            "diffuse = _textureCache.WhitePixel;",
            "GetOrUpload(diffusePath)");
    }
}
