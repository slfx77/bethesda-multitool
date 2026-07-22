using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

public sealed class FnvLocalLightAssociationObservationTests
{
    [Fact]
    public void Unknown_DoesNotMasqueradeAsAZeroLightAssociation()
    {
        var key = new FnvGeometryLightAssociationKey(0x00123456, 17);

        var observation = FnvLocalLightAssociationObservation.CreateUnknown(
            key,
            "retail property association not captured");

        Assert.False(FnvLocalLightAssociationObservation.CanDriveRendering);
        Assert.Equal(FnvLocalLightAssociationKnowledge.Unknown, observation.Knowledge);
        Assert.False(observation.AssociationKnown);
        Assert.False(observation.AssociationOrderKnown);
        Assert.Null(observation.AssociatedLocalLightCount);
        Assert.Null(observation.OrderedEmitterReferenceFormIds);
        Assert.Equal(key, observation.Key);
    }

    [Fact]
    public void KnownEmpty_RecordsAProvenZeroWithoutLosingTriStateMeaning()
    {
        var observation = FnvLocalLightAssociationObservation.CreateKnownEmpty(
            new FnvGeometryLightAssociationKey(0x00123456, 17),
            "captured property list was empty");

        Assert.Equal(FnvLocalLightAssociationKnowledge.KnownEmpty, observation.Knowledge);
        Assert.True(observation.AssociationKnown);
        Assert.True(observation.AssociationOrderKnown);
        Assert.Equal(0, observation.AssociatedLocalLightCount);
        Assert.True(observation.OrderedEmitterReferenceFormIds.HasValue);
        Assert.Empty(observation.OrderedEmitterReferenceFormIds.Value);
    }

    [Fact]
    public void KnownOrdered_PreservesPlacedEmitterIdentityOrderAndDuplicates()
    {
        uint[] source =
        [
            0x00100001,
            0x00100002,
            0x00100001,
            0x00100003,
            0x00100004
        ];
        var observation = FnvLocalLightAssociationObservation.CreateKnownOrdered(
            new FnvGeometryLightAssociationKey(0x00123456, 17),
            source,
            "captured active property traversal");
        source[0] = 0x00FFFFFF;

        Assert.Equal(FnvLocalLightAssociationKnowledge.KnownOrdered, observation.Knowledge);
        Assert.True(observation.AssociationKnown);
        Assert.True(observation.AssociationOrderKnown);
        Assert.Equal(5, observation.AssociatedLocalLightCount);
        Assert.True(observation.OrderedEmitterReferenceFormIds.HasValue);
        Assert.Equal(
            [0x00100001u, 0x00100002u, 0x00100001u, 0x00100003u, 0x00100004u],
            observation.OrderedEmitterReferenceFormIds.Value);
    }

    [Fact]
    public void Contract_HasNoProductionConsumerWhileRenderingIsDisabled()
    {
        Assert.False(FnvLocalLightAssociationObservation.CanDriveRendering);
        var root = SourceContract.RepoRoot;
        var sourceRoot = Path.Combine(root, "src");
        var contractPath = Path.GetFullPath(Path.Combine(
            sourceRoot,
            "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering",
            "FnvLocalLightAssociationObservation.cs"));
        var consumers = Directory
            .EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetExtension(path) is ".cs" or ".hlsl")
            .Where(path => !Path.GetFullPath(path).Equals(
                contractPath, StringComparison.OrdinalIgnoreCase))
            .Where(path => File.ReadAllText(path).Contains(
                nameof(FnvLocalLightAssociationObservation),
                StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(root, path))
            .ToArray();

        Assert.Empty(consumers);
    }

    [Fact]
    public void Key_RequiresAPlacedGeometryAndStableSourceShape()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new FnvGeometryLightAssociationKey(0, 17));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new FnvGeometryLightAssociationKey(0x00123456, -1));
    }

    [Fact]
    public void KnownOrdered_RejectsEmptyOrInvalidEmitterLists()
    {
        var key = new FnvGeometryLightAssociationKey(0x00123456, 17);

        Assert.Throws<ArgumentNullException>(() =>
            FnvLocalLightAssociationObservation.CreateKnownOrdered(
                key,
                null!,
                "capture"));
        Assert.Throws<ArgumentException>(() =>
            FnvLocalLightAssociationObservation.CreateKnownOrdered(
                key,
                [],
                "capture"));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FnvLocalLightAssociationObservation.CreateKnownOrdered(
                key,
                [0x00100001, 0],
                "capture"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Observation_RequiresExplicitEvidenceLabel(string? evidenceSource)
    {
        var key = new FnvGeometryLightAssociationKey(0x00123456, 17);

        Assert.Throws<ArgumentException>(() =>
            FnvLocalLightAssociationObservation.CreateUnknown(key, evidenceSource!));
        Assert.Throws<ArgumentException>(() =>
            FnvLocalLightAssociationObservation.CreateKnownEmpty(key, evidenceSource!));
        Assert.Throws<ArgumentException>(() =>
            FnvLocalLightAssociationObservation.CreateKnownOrdered(
                key,
                [0x00100001],
                evidenceSource!));
    }
}
