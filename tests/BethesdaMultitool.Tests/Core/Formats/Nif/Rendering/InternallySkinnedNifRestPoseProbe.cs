using BethesdaMultitool.Core.Formats.Bsa.Extraction;
using BethesdaMultitool.Core.Formats.Bsa.Parsing;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

/// <summary>
///     Guards the internally-skinned rest-pose flip ("banner renders face-up, detached from its
///     post"): NIFs whose skinned shapes reference bone nodes INSIDE the file (Morrowind banners,
///     FNV cloth flags) must skin against their own authored node transforms — the rest pose —
///     instead of rendering raw bind-pose geometry. Reads real game BSAs; skips when absent (CI).
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
public class InternallySkinnedNifRestPoseProbe
{
    private const string MorrowindBsa = @"E:\SteamLibrary\SteamApps\common\Morrowind\Data Files\Morrowind.bsa";
    private static readonly string FnvMeshesBsa = SampleBsaLocator.ResolveFnvMeshesBsa();

    [Fact]
    public void Banner_DetectsInternalSkin_AndRestPoseDiffersFromBindPose()
    {
        var (data, nif) = Load(MorrowindBsa, @"meshes\f\furn_banner_tavern_01.nif");

        var signature = NifAnimationDetector.Detect(data, nif);
        Assert.True(signature.HasInternalSkin);
        Assert.True(signature.HasNodeKeyframeTracks);

        // Rest-pose skinning (skipSkinning:false, no external transforms → the NIF's own bone
        // nodes) must move the cloth off its raw bind-pose authoring.
        var restPose = NifGeometryExtractor.Extract(data, nif, skipSkinning: false);
        var bindPose = NifGeometryExtractor.Extract(data, nif, skipSkinning: true);
        Assert.NotNull(restPose);
        Assert.NotNull(bindPose);
        Assert.True(restPose.WasSkinned, "banner shape should have been skinned");

        var rest = Assert.Single(restPose.Submeshes);
        var bind = Assert.Single(bindPose.Submeshes);
        Assert.Equal(bind.Positions.Length, rest.Positions.Length);
        var maxDelta = 0f;
        for (var i = 0; i < rest.Positions.Length; i++)
        {
            maxDelta = MathF.Max(maxDelta, MathF.Abs(rest.Positions[i] - bind.Positions[i]));
        }

        Assert.True(maxDelta > 1f, $"rest pose should differ from bind pose (max delta {maxDelta})");
    }

    [Fact]
    public void FnvClothFlag_StillDecodes_WithProxyShapesDropped()
    {
        // The known consumer of the old bind-pose behavior: FNV animated cloth flags. The flip must
        // keep them decodable (now rest-pose skinned) with the per-bone proxy boxes still dropped —
        // proxies are unskinned shapes parented to bone nodes and carry no diffuse texture.
        var (data, nif) = Load(FnvMeshesBsa, @"meshes\clutter\flags\nv_ncr_flag_s.nif");

        var signature = NifAnimationDetector.Detect(data, nif);
        Assert.True(signature.HasInternalSkin);

        var model = NifGeometryExtractor.Extract(
            data, nif, skipSkinning: false, treatRootsAsIdentity: true, dropBoneAttachedShapes: true);
        Assert.NotNull(model);
        Assert.True(model.WasSkinned);
        Assert.NotEmpty(model.Submeshes);
        Assert.All(model.Submeshes, s => Assert.False(string.IsNullOrEmpty(s.DiffuseTexturePath)));
    }

    private static (byte[] data, NifInfo nif) Load(string bsaPath, string meshPath)
    {
        BucketBTestGuard.SkipUnlessEnabled();
        Assert.SkipUnless(File.Exists(bsaPath), $"{Path.GetFileName(bsaPath)} not present (dev-machine-only asset).");

        using var extractor = new BsaExtractor(bsaPath);
        var archive = BsaParser.Parse(bsaPath);
        var file = archive.AllFiles.First(f => string.Equals(f.FullPath, meshPath, StringComparison.OrdinalIgnoreCase));
        var data = extractor.ExtractFile(file);

        var nif = NifParser.Parse(data);
        Assert.NotNull(nif);
        return (data, nif);
    }
}