using System.Security.Cryptography;
using BethesdaMultitool.Core.Modeling;
using BethesdaMultitool.Core.Vfs;
using BethesdaMultitool.Tests.Helpers;
using Xunit;
using static BethesdaMultitool.Tests.Core.Modeling.Nif.NifModelTestSupport;

namespace BethesdaMultitool.Tests.Core.Modeling.RealAsset;

/// <summary>
///     Hop A3 for the console packed geometry (plan section 6, slice 10) on hand-picked layout rows: every X360 and PS3
///     retail file listed here is read beside the PC Steam Final file of the same path and compared shape by shape by
///     <see cref="NifPackedGeometryComparison" /> (positions, normals, UVs and the tangent frame within one binary16
///     ulp and exact-or-near-zero, triangles exact, influences slot for slot, colors within one byte under the
///     platform's order). The rows cover every layout, L1 to L6, at least twice, with PS3 rows for L1, L3 and L4; the
///     whole console cover corpus (95 files) goes through the same comparison in
///     <see cref="NifConsoleGeometryOracleTests" />.
/// </summary>
/// <remarks>
///     <para>
///         Two or more files per layout (L1 to L6) from the measurement's file lists, plus PS3 files: a static L1 and a
///         skinned L3 and L4 file whose colors have R != B (the platform order is visible) and a skinned L3 and L4 file
///         whose colors are all grey. The pins are the SHA-256 of the archived bytes, computed 2026-09-24 with the
///         probe's BSA reader (tools/scripts/nif_feature_probe.py through TestOutput/packed-semantics-20260924/pkcommon.py).
///     </para>
///     <para>
///         Controls: a console position component that matched exactly with |PC value| at least 1/4 moved by a single
///         half ulp fails the exactness rule (the near-zero branch cannot rescue it) and moved by two leaves the
///         one-ulp tolerance; on a color layout whose colors discriminate the consoles (asserted first from the decoded
///         colors: some vertex's R, G, B span at least two bytes), the wrong platform's byte order fails the color
///         comparison;
///         the exact census of every channel is non-empty, so the residue rule cannot pass vacuously; and an L1 or L4
///         shape with such colors reports a positive color-order-sensitive count with ReverseEngineered provenance
///         where every L6 shape reports zero with Assumed provenance and the inferred-order diagnostic.
///     </para>
/// </remarks>
[Trait("Category", TestCategories.BucketB)]
[Collection(SequentialIntegrationGroup.Name)]
public sealed class NifPackedGeometryOracleTests
{
    private const string X360Data = "Builds/Fallout - New Vegas (2010-8-22, X360 - Final)/Data";
    private const string Ps3Data = "Builds/Fallout - New Vegas (2010-9-5, PS3 - Final)/PS3_GAME/USRDIR/DATA";
    private const string PcData = "Builds/Fallout - New Vegas (2022-5-24, Steam - Final)/Data";

    [Theory]
    [InlineData("x360", "meshes/animobjects/aogenericmeal.nif", "L1",
        "b994fc22301e70e84c6aa0c12af768ea9673a2f9ee3e37126a923bd23b43496c",
        "c5963de8fc2cc553a408fecfadecec9ace43af2948f829f08d9650be943320f2", false)]
    [InlineData("x360", "meshes/architecture/wasteland/docks/potdockbg3way01.nif", "L1",
        "9bb9fdf1b01f9272654919f3910fb79de11f94f5dc74a1d9ba209a18110bafc0",
        "e4c5116ca978e4dbd2c085c990c34e8dc770f48cc89d6be719249996ba1f4461", true)]
    [InlineData("x360", "meshes/ammo/22lrammo.nif", "L2",
        "863d3f7459951df7ea51118c4476e0410cae5d3902d733cb98208c915410ac35",
        "36a4b9e49f8902ac8f28111c97ba82166b258f773b930baaecab36cc25887c19", false)]
    [InlineData("x360", "meshes/clutter/handradio/handradio.nif", "L2",
        "83315d517f0c7f07e46b936dcb0281be887ec89d24cd1892919be078abe8d05f",
        "67aa0f08ccb0598c82ad30f9c7d4693e05de784e8c43bbd458d7b431c3358137", false)]
    [InlineData("x360", "meshes/armor/enclavescientist/outfitf.nif", "L3",
        "c64cd01b4e52e5c3c0d1a24dae1e09e1b37bcb0508a3bfc9efac731fdc3365af",
        "c67fca71241a338b33a1616c6c982acdc9419dd9c241d5f37f123c67dd90cfb5", false)]
    [InlineData("x360", "meshes/armor/metalarmormkii/f/f_metmkii.nif", "L3,L4",
        "7366101fafe63ba6046d1f0761adf5945c35209093cb60ee72b16e51e9620d53",
        "8a1274b3f4dcaee98e492ae202e3a4b25900bf10d1a07ca04828b3b9a3c4ce73", false)]
    [InlineData("x360", "meshes/armor/enclavepowerarmor/backpack.nif", "L4",
        "9927274d3393f374556c0a49046edd93a08744bb09a5ebd38e5e0c3115571540",
        "47643712f96645334c03177acd536b53615e273d4b43b3bfc8dd167d824a2eb1", false)]
    [InlineData("x360", "meshes/dlcpitt/creatures/streettrog/streettrog.nif", "L3,L4",
        "d180b3bdcaf45a6a708c27b9757acf918c1a096bcd4be460cae63014785dd7d8",
        "912f071c55f97ef4a8162f0d4e13e78fe55c3d9ce0fe2727c98ec85e120fabf5", true)]
    [InlineData("x360", "meshes/interface/circular loading/loading01.nif", "L5",
        "e2919255b507633b8582fe176922813ee30251af651360a7eabd946bc963dd5b",
        "834811ef13b23b17788b4aa003bf0691c24282a648655f97eb291e6482901b08", false)]
    [InlineData("x360", "meshes/interface/hud/airtimer01.nif", "L5,L6",
        "125049c3eb3c8d711a9f9836210141f4ae4858fea87a28d467e295418d3c656f",
        "ff519a37373242bdfabac587276e7246d1e0b2c91bc66afc9f26b4a0341f7ef2", false)]
    [InlineData("x360", "meshes/interface/hud/stealthindicator01.nif", "L5,L6",
        "566e698f985b2a988e4787c680135a9754a6e6f365a61e0ec098b28eeed4f25c",
        "d6a9336098e6cc18422fe2d0c36e52698af3441d690ce81de3b9011accc01d6a", false)]
    [InlineData("x360", "meshes/interface/loading/loadinganim01.nif", "L5,L6",
        "8a5cb6434fe38e27c73d6128a3527aa66aa83fd7aaed039bdd00291de7d91f9f",
        "444af311a4251535b6e1560392c2e340ab9bf46299c534fd95fa80983b0dd6cf", false)]
    [InlineData("x360", "meshes/interface/pausescreen/pausescreen01.nif", "L5,L6",
        "c18d590153307165d0fb53fd0e7ac2315aebe170704f7dbfe3c0233cda62518c",
        "53f48745e5ab8bd6c1acaa09f151d5ad920ecf62ef4012376c1444d38688e3d9", false)]
    [InlineData("ps3", "meshes/architecture/wasteland/docks/potdockbg3way01.nif", "L1",
        "6eb4552597753451ebb755c61e287ebdfa798a19173fe57eb552a3745ebd19fe",
        "e4c5116ca978e4dbd2c085c990c34e8dc770f48cc89d6be719249996ba1f4461", true)]
    [InlineData("ps3", "meshes/armor/metalarmormkii/m/m_metmkii.nif", "L3,L4",
        "00679f0a54340e42af8fc65eada7c63d3ac32bb6a91b1efee58ce1c551d11e9b",
        "791be90b68737d7ba18a282ae218bf854eab09a63f21c766fb7ecb522cb9fec1", false)]
    [InlineData("ps3", "meshes/dlcpitt/creatures/streettrog/streettrog.nif", "L3,L4",
        "c4024b35891bee4f88d9a49e94aabb375102e153a0bfdee62eaf646ebea5929c",
        "912f071c55f97ef4a8162f0d4e13e78fe55c3d9ce0fe2727c98ec85e120fabf5", true)]
    public void PackedGeometry_ReproducesThePcFile_WithinHalfPrecision(string platform, string relativePath,
        string layouts, string consoleSha256, string pcSha256, bool colorsDiscriminatePlatform)
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var consoleBytes = ReadFixture(platform == "ps3" ? Ps3Data : X360Data, relativePath);
        var pcBytes = ReadFixture(PcData, relativePath);
        Assert.Equal(consoleSha256, Convert.ToHexStringLower(SHA256.HashData(consoleBytes)));
        Assert.Equal(pcSha256, Convert.ToHexStringLower(SHA256.HashData(pcBytes)));

        var options = new Dictionary<string, string> { [BethesdaModelRegistration.PlatformOption] = platform };
        var console = Read(consoleBytes, options, path: relativePath).Document;
        var pc = Read(pcBytes, path: relativePath).Document;
        var result = NifPackedGeometryComparison.Compare(console, pc, pcBytes, relativePath, platform);
        Assert.Equal(layouts.Split(',').Order(), result.Layouts.Order());
        Assert.Equal(0, result.InlineShapes);
        TestContext.Current.TestOutputHelper?.WriteLine(result.Summary(relativePath, platform));

        // Control 1: one matched packed position component moved by one half ulp fails the exactness rule, by two
        // the one-ulp tolerance.
        NifPackedGeometryComparison.AssertMovedVertexControl(result, relativePath);

        // Control 2: on a color layout whose colors discriminate the consoles, the other platform's order fails. The
        // flag is claimed only where the decoded colors can tell the orders apart under the one-byte comparison
        // tolerance: a grey vertex reads the same under A,R,G,B and A,G,B,R, and a vertex whose R, G, B differ by a
        // single byte is order-sensitive to the reader yet invisible to the tolerance.
        if (colorsDiscriminatePlatform)
        {
            Assert.True(result.OrderDiscriminating > 0,
                $"{relativePath}: no decoded vertex color spans two bytes, so no vertex color can tell the platforms apart");
            var other = new Dictionary<string, string>
            {
                [BethesdaModelRegistration.PlatformOption] = platform == "ps3" ? "x360" : "ps3"
            };
            var misread = Read(consoleBytes, other, path: relativePath).Document;
            Assert.True(NifPackedGeometryComparison.CountColorDisagreements(misread, pc) > 0,
                $"{relativePath}: the wrong platform's color order was not detected");
        }
    }

    /// <summary>
    ///     nv_ncr_flag, the gate file, under the X360: the comparison checks its engine lanes bit for bit against the
    ///     engine rule over the PC twin's partition, and the joint-0 control (the derived weight moved from its slot-3 bone
    ///     to joint 0) fails on exactly the 110 vertices, 55 per skinned shape, whose derived weight is nonzero on a
    ///     slot-3 bone other than joint 0, none of them unobservable (measured by
    ///     measurement/engine_lane_control_reach.py, TestOutput/gap-impl-20260927/console-weights). All 242 skinned packed
    ///     vertices are compared as engine lanes. Control: the comparison takes the platform from the caller, not from the
    ///     reader's facts, so the same X360 document compared as a PS3 read fails on its engine lanes.
    /// </summary>
    [Fact]
    public void EngineLanes_TheFlagsDerivedWeightsSitOnTheirSlot3Bones()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        const string relativePath = "meshes/clutter/flags/nv_ncr_flag.nif";
        var consoleBytes = ReadFixture(X360Data, relativePath);
        var pcBytes = ReadFixture(PcData, relativePath);
        Assert.Equal("1795f49c5f3557b5ceaf0d917291e6f0d63424f8a8750fd141e1f918d2ee1996",
            Convert.ToHexStringLower(SHA256.HashData(consoleBytes)));
        Assert.Equal("6118b65095d24b1435954a42d93e2fb75edc326f51605e2acf0f61442efad5fe",
            Convert.ToHexStringLower(SHA256.HashData(pcBytes)));
        var pc = Read(pcBytes, path: relativePath).Document;

        var x360 = new Dictionary<string, string> { [BethesdaModelRegistration.PlatformOption] = "x360" };
        var console = Read(consoleBytes, x360, path: relativePath).Document;
        var result = NifPackedGeometryComparison.Compare(console, pc, pcBytes, relativePath, "x360");
        TestContext.Current.TestOutputHelper?.WriteLine(result.Summary(relativePath, "x360"));
        Assert.Equal(242, result.Tally.EngineLanes);
        Assert.Equal(110, result.Tally.EngineLaneJointControls);
        Assert.Equal(0, result.Tally.EngineLaneJointUnobservable);

        var error = Assert.ThrowsAny<Exception>(() => NifPackedGeometryComparison.Compare(console, pc, pcBytes, relativePath, "ps3"));
        Assert.Contains("typed the 'engine' lanes", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Reads a fixture from a build's Data folder (loose files over archives).</summary>
    private static byte[] ReadFixture(string dataDirectory, string relativePath)
    {
        var data = RealAssetPaths.SampleDirectory(dataDirectory);
        Assert.SkipWhen(data is null, RealAssetPaths.SkipMessage(dataDirectory));
        using var files = GameFileSystem.OpenDataFolder(data);
        var bytes = files.TryReadAllBytes(relativePath);
        Assert.True(bytes is not null, $"The named retail fixture is missing from {data}: {relativePath}");
        return bytes;
    }
}
