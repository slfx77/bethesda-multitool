using BethesdaMultitool.Core.Modeling;
using BethesdaMultitool.Core.Modeling.Nif;
using BethesdaMultitool.Core.Modeling.Redguard;
using BethesdaMultitool.Core.Modeling.Shadowkey;
using BethesdaMultitool.Core.Modeling.Starfield;
using BethesdaMultitool.Core.Modeling.Xngine;
using BethesdaMultitool.Tests.Core.Modeling.Nif;
using BethesdaMultitool.Tests.Helpers;
using Slfx77.Multitool.Core.Assets;
using Slfx77.Multitool.Core.Models;
using Slfx77.Multitool.Core.Models.Sources;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Modeling;

/// <summary>
///     BMT's model composition root: the explicit reader registration, selection through Shared's registry (with the
///     probe evidence Shared's info formatter prints), and a document that passes Shared's structure validation.
/// </summary>
public class BethesdaModelRegistrationTests
{
    private static byte[] TwoNodes()
    {
        var builder = new NifTestFileBuilder(false, 34);
        NifModelTestSupport.AddNode(builder, builder.AddString("Scene Root"), [1]);
        NifModelTestSupport.AddNode(builder, -1, []);
        return builder.Build();
    }

    private static ModelSourceCandidate Candidate(byte[] bytes)
    {
        return new ModelSourceCandidate(new AssetEntry(new AssetReference("memory", "any/name.dat"), bytes.Length),
            bytes, true);
    }

    /// <summary>
    ///     The registry holds the NIF reader, the Starfield .mesh reader (cut 2), since cut-1c slice 5 the XnGine .3D
    ///     reader and since slice 6 the Redguard .3DC reader, in that order; NIF content selects the NIF reader alone (the
    ///     Starfield and both XnGine probes answer NotAModel for it).
    /// </summary>
    [Fact]
    public void CreateReaders_RegistersTheNifStarfieldAndXnGineReaders_AndSelectsTheNifReaderForNifContent()
    {
        var registry = BethesdaModelRegistration.CreateReaders();

        Assert.Equal(
            new[]
            {
                "bmt.nif", "bmt.starfield.mesh", "bmt.xngine.3d", "bmt.redguard.3dc", "bmt.shadowkey.mesh",
                "bmt.shadowkey.zone"
            },
            registry.Registrations.Select(r => r.FormatId).ToArray());
        Assert.IsType<ShadowkeyMeshModelReader>(registry.GetReader("bmt.shadowkey.mesh"));
        Assert.IsType<ShadowkeyZoneModelReader>(registry.GetReader("bmt.shadowkey.zone"));
        Assert.IsType<StarfieldMeshModelReader>(registry.GetReader("bmt.starfield.mesh"));
        Assert.IsType<XnGineModelReader>(registry.GetReader("bmt.xngine.3d"));
        Assert.IsType<Redguard3DcModelReader>(registry.GetReader("bmt.redguard.3dc"));
        var reader = Assert.IsType<NifModelReader>(registry.GetReader("bmt.nif"));
        Assert.True(reader.SupportsInspectionWithoutPixelDecoding);

        var selection = registry.Probe(Candidate(TwoNodes()));
        Assert.Equal(ModelSourceSelectionKind.Supported, selection.Kind);
        Assert.Same(reader, selection.Reader);
        // A NIF's first dword ("Game") is far above the Starfield probe's legal 0..2, so only the NIF reader matches.
        Assert.Single(selection.Matches);
        Assert.Equal("NIF 20.2.0.7, user 11, BS 34, little-endian", selection.Matches[0].Result.Evidence!.Description);

        // Control: non-NIF content selects nothing.
        var png = registry.Probe(Candidate([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]));
        Assert.Equal(ModelSourceSelectionKind.NotAModel, png.Kind);
    }

    [Fact]
    public void RegisteredReader_WithTheRegistrationsCache_ProducesAStructurallyValidDocument()
    {
        var bytes = TwoNodes();
        var registry = BethesdaModelRegistration.CreateReaders();
        var reader = registry.Probe(Candidate(bytes)).Reader!;
        var (item, input) = NifModelTestSupport.Open(bytes);
        using var owned = input;

        var cache = BethesdaModelRegistration.CreateCache();
        var result = reader.Read(item, new ModelReadContext(item, input, cache, purpose: ModelReadPurpose.Inspection),
            CancellationToken.None);

        Assert.IsType<NifModelReadCache>(cache);
        Assert.Equal(item.Reference, result.Coverage.Source);
        Assert.Equal(2, result.Coverage.TotalCount);
        SceneValidation.ValidateStructure(result.Document);
    }

    [Fact]
    public void AppOptionKeys_AreTheDocumentedNames()
    {
        Assert.Equal("bmt.game", BethesdaModelRegistration.GameOption);
        Assert.Equal("bmt.game-evidence", BethesdaModelRegistration.GameEvidenceOption);
        Assert.Equal("bmt.platform", BethesdaModelRegistration.PlatformOption);
        Assert.Equal("bmt.skeleton", BethesdaModelRegistration.SkeletonOption);
    }

    /// <summary>
    ///     Cut-1b slice 10: the workflow's option bag carries the skeleton path exactly as given and omits a blank one.
    ///     Control: without it the bag has no skeleton key, so a .kf read walks up.
    /// </summary>
    [Fact]
    public void AppOptions_CarryTheSkeletonPathExactly()
    {
        const string path = @"C:\data\meshes\Creatures\Skeleton.nif";

        var with = BethesdaModelWorkflow.CreateAppOptions("fnv", skeleton: path);
        var blank = BethesdaModelWorkflow.CreateAppOptions("fnv", skeleton: " ");
        var without = BethesdaModelWorkflow.CreateAppOptions("fnv");

        Assert.Equal(path, with[BethesdaModelRegistration.SkeletonOption]);
        Assert.Equal(path, NifModelSkeletonOption.Resolve(with));
        Assert.False(blank.ContainsKey(BethesdaModelRegistration.SkeletonOption));
        Assert.Null(NifModelSkeletonOption.Resolve(blank));
        Assert.False(without.ContainsKey(BethesdaModelRegistration.SkeletonOption));
        Assert.Null(NifModelSkeletonOption.Resolve(without));
    }
}
