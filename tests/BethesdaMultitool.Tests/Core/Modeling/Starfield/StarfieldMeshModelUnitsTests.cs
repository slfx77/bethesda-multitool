using System.Numerics;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Core.Modeling;
using BethesdaMultitool.Core.Modeling.Nif;
using BethesdaMultitool.Core.Modeling.Starfield;
using BethesdaMultitool.Tests.Helpers;
using Slfx77.Multitool.Core.Models;
using Xunit;
using static BethesdaMultitool.Tests.Core.Modeling.Starfield.StarfieldMeshModelTestSupport;

namespace BethesdaMultitool.Tests.Core.Modeling.Starfield;

/// <summary>
///     Units, basis and format metadata (cut-2 plan sections 3.1 and 4.2, decisions D8 and D12, slice 3): the literal
///     Starfield row, the <c>--game</c> rule, the NIF basis object, and the catalog description.
/// </summary>
public class StarfieldMeshModelUnitsTests
{
    [Fact]
    public void Units_AreTheStarfieldRow_OneMeterPerUnit_Assumed()
    {
        var units = Read(StarfieldMeshTestBuilder.Quad().Build()).Document.Units!;

        // A literal pin, not the profile read back: the design's units row says 1.0, Assumed.
        Assert.Equal(1.0, units.MetersPerUnit);
        Assert.Equal(SceneValueProvenance.Assumed, units.Provenance);
        Assert.StartsWith(StarfieldMeshModelUnits.ImpliedGameEvidence, units.Evidence, StringComparison.Ordinal);
        Assert.Contains(GameProfiles.For(BethesdaGame.Starfield).Units.Evidence, units.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void TheGameOption_NamesStarfieldOrNothing()
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [BethesdaModelRegistration.GameOption] = "starfield",
            [BethesdaModelRegistration.GameEvidenceOption] = "--game starfield"
        };

        var named = StarfieldMeshModelUnits.Resolve(options);

        Assert.Equal(1.0, named.MetersPerUnit);
        Assert.Contains("bmt.game=starfield (--game starfield)", named.Evidence, StringComparison.Ordinal);
        Assert.Same(StarfieldMeshModelUnits.Default, StarfieldMeshModelUnits.Resolve(Game("auto")));
        Assert.Same(StarfieldMeshModelUnits.Default,
            StarfieldMeshModelUnits.Resolve(new Dictionary<string, string>(StringComparer.Ordinal)));
        // Control: a game the file cannot belong to throws, NIF-era and XnGine alike.
        Assert.Throws<ArgumentException>(() => StarfieldMeshModelUnits.Resolve(Game("fnv")));
        Assert.Throws<ArgumentException>(() => StarfieldMeshModelUnits.Resolve(Game("Fallout4")));
        Assert.Throws<ArgumentException>(() => StarfieldMeshModelUnits.Resolve(Game("redguard")));
    }

    [Fact]
    public void TheBasis_IsTheNifBasisObject()
    {
        var document = Read(StarfieldMeshTestBuilder.Quad().Build()).Document;

        Assert.Same(NifModelUnits.Basis, document.SourceBasis);
        Assert.Equal(Vector3.UnitZ, document.SourceBasis!.Up);
        Assert.Equal(Vector3.UnitY, document.SourceBasis.Forward);
    }

    [Fact]
    public void TheFormatMetadata_DescribesTheReader()
    {
        var reader = new StarfieldMeshModelReader();
        var metadata = reader.FormatMetadata;

        Assert.Equal("bmt.starfield.mesh", reader.FormatId);
        Assert.Equal(StarfieldMeshModelFormatMetadata.DisplayName, metadata.DisplayName);
        Assert.Equal(
            new[]
            {
                StarfieldMeshModelFormatMetadata.TailVariant, StarfieldMeshModelFormatMetadata.TaillessVariant,
                StarfieldMeshModelFormatMetadata.Version1Variant, StarfieldMeshModelFormatMetadata.Version0Variant
            }, metadata.SupportedVariants);
        Assert.Equal(1.0, metadata.DefaultUnits.MetersPerUnit);
        Assert.Equal(SceneValueProvenance.Assumed, metadata.DefaultUnits.Provenance);
        var ids = metadata.AdmissionRules.Select(static r => r.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("tail-rule", ids);
        Assert.Contains("recognition", ids);
        Assert.Contains("corrupt-input", ids);
        Assert.Contains(BethesdaModelRegistration.GameOption, metadata.UnitPolicy, StringComparison.Ordinal);
        Assert.Equal(16 * 1024 * 1024, StarfieldMeshModelReader.MaximumSourceBytes);
    }
}
