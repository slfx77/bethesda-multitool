using System.Globalization;
using BethesdaMultitool.Core.AssetBrowse;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Core.Modeling;
using BethesdaMultitool.Core.Modeling.Units;
using BethesdaMultitool.Core.Modeling.Xngine;
using BethesdaMultitool.Tests.Helpers;
using Slfx77.Multitool.Core.Assets;
using Slfx77.Multitool.Core.Models;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Modeling.RealAsset;

/// <summary>
///     Bucket B for cut-1c slice 3 (plan section 8, slice 3): the game identity and its evidence for every design and
///     proposed cover file, through the same path a <c>mesh</c> command takes (the production asset session over the
///     retail container, the container facts query, the content measurement, and the shell's install walk-up as
///     <c>bmt.classic-game</c>); the container facts against the manifest's pins (index, stored size and SHA-256,
///     segment type); no declared length for the LZSS rows; and the plan's control: an extracted ARCH3D record read
///     loose reports Unknown with the diagnostic naming <c>--game</c>.
/// </summary>
/// <remarks>
///     The expectations are per row: an archive row answers by its container (Daggerfall for ARCH3D, Battlespire for
///     3D.BSA and 3D.BS6, Redguard for ISLAND.ROB) and a loose row by the install the walk-up finds (Battlespire's
///     GAMEDATA, Redguard's 3dart under the WORLD.INI root). With an empty option bag and no container the content alone
///     must give Battlespire for the Battlespire rows, Redguard for the Redguard rows (header +20 non-zero: 5510 on
///     CRAK0001, 1714 on GR_COMP, 64 on every .3DC) and Unknown for the Daggerfall rows (+20 = 0), measured 2026-09-28
///     (slice3/measure_slice3.json). A row whose container is not on this machine skips; one that is present but
///     answers differently fails.
/// </remarks>
[Trait("Category", TestCategories.BucketB)]
[Collection(SequentialIntegrationGroup.Name)]
public sealed class Cut1cIdentityTests : IDisposable
{
    private const string SamplePrefix = "Sample/";

    private readonly List<IAsyncDisposable> _sources = [];
    private readonly List<string> _directories = [];

    /// <summary>The design and proposed rows: the plan's 11 cover files, as (row name, payload SHA-256).</summary>
    public static TheoryData<string, string> CoverRows()
    {
        var rows = new TheoryData<string, string>();
        foreach (var file in Cut1cCoverManifest.Files.Where(f =>
                     f.Role is Cut1cCoverFile.DesignRole or Cut1cCoverFile.ProposedRole))
        {
            rows.Add(file.Name, file.Sha256);
        }

        return rows;
    }

    public void Dispose()
    {
        foreach (var source in _sources)
        {
            source.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        foreach (var directory in _directories.Where(Directory.Exists))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [MemberData(nameof(CoverRows))]
    public async Task EveryCoverFile_IsIdentifiedWithItsEvidence(string name, string sha256)
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var file = Cut1cCoverManifest.Require(name);
        Assert.Equal(sha256, file.Sha256);
        var fixture = Cut1cFixtureResolver.TryResolveContainer(file, out var reason);
        Assert.SkipWhen(fixture is null && reason.StartsWith(Cut1cFixtureResolver.AbsentPrefix, StringComparison.Ordinal),
            $"{file}: {reason}. " + RealAssetPaths.SkipMessage("the cut-1c cover corpus"));
        Assert.True(fixture is not null, $"{file}: {reason}");
        var location = Locate(file.Source);
        Assert.True(location is not null, $"{file}: the source {file.Source} resolved bytes but no path.");

        var content = XnGineContentFacts.Measure(fixture.Bytes);
        Assert.True(content.IsMeshTag, $"{file}: tag {content.Tag}");
        Assert.Equal(file.Tag, content.Tag);

        var loose = string.Equals(file.Container, Cut1cCoverFile.LooseContainer, StringComparison.Ordinal);
        var (source, reference) = loose ? OpenLoose(location) : OpenArchive(location, file);
        var facts = ClassicContainerFacts.TryQuery(source, reference);
        var options = ShellOptions(location);
        var identity = XnGineGameIdentity.Resolve(options, facts, content);

        // The identity and its evidence, the way the shell would establish it.
        Assert.Equal(file.Game, identity.Game.ToString());
        Assert.True(identity.IsEstablished);
        Assert.Equal(loose ? XnGineGameIdentityStep.Install : XnGineGameIdentityStep.Container, identity.Step);
        Assert.Equal(XnGineGameIdentity.LayoutOf(identity.Game), identity.Layout);
        Assert.StartsWith(file.Game + " per ", identity.Evidence, StringComparison.Ordinal);
        Assert.DoesNotContain(identity.Diagnostics, d => d.Code == XnGineGameIdentity.StepRefutedDiagnostic);
        Assert.Equal(PinnedMetersPerUnit(file.Game), identity.Units.MetersPerUnit);
        Assert.Equal(SceneValueProvenance.Assumed, identity.Units.Provenance);

        // The container facts against the manifest's pins.
        if (loose)
        {
            Assert.Null(facts);
            Assert.Contains(BethesdaModelRegistration.ClassicGameOption + "=" + file.Game, identity.Evidence,
                StringComparison.Ordinal);
        }
        else
        {
            Assert.NotNull(facts);
            Assert.Equal(file.Index, facts.EntryIndex);
            Assert.Equal(file.IsCompressed, facts.IsCompressed);
            Assert.Equal(file.StoredSize ?? file.Size, facts.StoredSize);
            Assert.Equal(file.Entry, facts.EntryName);
            Assert.Equal(ContainerKindOf(file.Container), facts.Kind);
            Assert.Contains(facts.ContainerName, identity.Evidence, StringComparison.Ordinal);
            var stored = facts.ReadStoredBytes();
            if (file.IsCompressed)
            {
                Assert.Equal(file.StoredSha256, Cut1cFixtureResolver.Sha256(stored));
                Assert.NotEqual(file.Sha256, Cut1cFixtureResolver.Sha256(stored));
            }
            else
            {
                Assert.Equal(file.Sha256, Cut1cFixtureResolver.Sha256(stored));
            }

            if (facts.Kind == ClassicContainerKind.NumberedXnGineBsa)
            {
                Assert.Equal(uint.Parse(file.Entry, CultureInfo.InvariantCulture), facts.EntryId);
                Assert.Equal(facts.EntryId, identity.ObjectId);
            }
            else
            {
                Assert.Null(identity.ObjectId);
            }

            if (facts.Kind == ClassicContainerKind.RedguardRob)
            {
                Assert.Equal((uint?)file.SegmentType, facts.SegmentType);
                Assert.Equal(ClassicContainerFacts.RobSegmentHeaderLength, facts.SegmentHeader.Length);
                Assert.Equal(file.Entry, System.Text.Encoding.ASCII.GetString(facts.SegmentHeader.Span.Slice(4, 8)).TrimEnd('\0'));
            }

            // The LZSS rows declare no length; the others declare the payload size.
            var entry = await ClassicContainerFixture.FindEntryAsync(source, reference.Path);
            if (file.IsCompressed)
            {
                Assert.Null(entry.Length);
                Assert.NotEqual(file.StoredSize, entry.Length);
            }
            else
            {
                Assert.Equal(file.Size, entry.Length);
            }
        }

        // The content alone: Battlespire and Redguard rows answer; Daggerfall rows stay Unknown with the diagnostic.
        var byContent = XnGineGameIdentity.Resolve(new Dictionary<string, string>(StringComparer.Ordinal), null, content);
        switch (file.Game)
        {
            case "Daggerfall":
                Assert.Equal(0, content.HeaderPlus20);
                Assert.Equal(BethesdaGame.Unknown, byContent.Game);
                Assert.Contains(byContent.Diagnostics, d =>
                    d.Code == XnGineGameIdentity.AmbiguousDiagnostic && d.Message.Contains("--game", StringComparison.Ordinal));
                break;
            case "Battlespire":
                Assert.True(content.WalksWithBattlespireLayout && !content.WalksWithDaggerfallLayout);
                Assert.Equal(BethesdaGame.Battlespire, byContent.Game);
                Assert.Equal(XnGineGameIdentityStep.Content, byContent.Step);
                break;
            case "Redguard":
                Assert.True(content.WalksWithDaggerfallLayout && !content.WalksWithBattlespireLayout);
                Assert.NotEqual(0, content.HeaderPlus20);
                Assert.Equal(BethesdaGame.Redguard, byContent.Game);
                Assert.Equal(XnGineGameIdentityStep.Content, byContent.Step);
                break;
            default:
                Assert.Fail($"{file}: the manifest names an unexpected game '{file.Game}'.");
                break;
        }
    }

    /// <summary>
    ///     The plan's control: an ARCH3D record extracted loose (as <c>archive extract</c> writes it, named by its id,
    ///     in a directory with no install markers) has no container, no install and +20 = 0, so it reports Unknown with
    ///     the diagnostic naming <c>--game</c> and the guard units; either <c>--game</c> value then settles it, which is
    ///     why the chain must not guess between them.
    /// </summary>
    [Fact]
    public void ExtractedArch3dRecord_ReadLoose_ReportsUnknownWithTheGameDiagnostic()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var file = Cut1cCoverManifest.RequireControl("arch3d-extracted-loose-ambiguity");
        var fixture = Cut1cFixtureResolver.TryResolveContainer(file, out var reason);
        Assert.SkipWhen(fixture is null && reason.StartsWith(Cut1cFixtureResolver.AbsentPrefix, StringComparison.Ordinal),
            $"{file}: {reason}. " + RealAssetPaths.SkipMessage("the cut-1c cover corpus"));
        Assert.True(fixture is not null, $"{file}: {reason}");

        var directory = Path.Combine(Path.GetTempPath(), "cut1c-extracted-" + Guid.NewGuid().ToString("N"));
        _directories.Add(directory);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, file.Entry);
        File.WriteAllBytes(path, fixture.Bytes);

        var (source, reference) = OpenLoose(path);
        var facts = ClassicContainerFacts.TryQuery(source, reference);
        var options = ShellOptions(path);
        var content = XnGineContentFacts.Measure(fixture.Bytes);
        var identity = XnGineGameIdentity.Resolve(options, facts, content);

        Assert.Null(facts);
        Assert.False(options.ContainsKey(BethesdaModelRegistration.ClassicGameOption),
            $"the temp directory {directory} lies inside a classic install " +
            $"({options.GetValueOrDefault(BethesdaModelRegistration.ClassicGameEvidenceOption)}), so the walk-up " +
            "answers and this control cannot run there; it needs a directory with no install markers within reach.");
        Assert.True(content.WalksWithDaggerfallLayout && !content.WalksWithBattlespireLayout);
        Assert.Equal(0, content.HeaderPlus20);
        Assert.Equal(BethesdaGame.Unknown, identity.Game);
        Assert.Equal(XnGineGameIdentityStep.None, identity.Step);
        var ambiguous = Assert.Single(identity.Diagnostics, d => d.Code == XnGineGameIdentity.AmbiguousDiagnostic);
        Assert.Contains("--game", ambiguous.Message, StringComparison.Ordinal);
        Assert.Equal(1.0, identity.Units.MetersPerUnit);
        Assert.Equal(SceneValueProvenance.Unknown, identity.Units.Provenance);

        // Both --game values are accepted for these bytes, so the chain cannot know which factor applies.
        foreach (var game in new[] { "daggerfall", "redguard" })
        {
            var settled = XnGineGameIdentity.Resolve(
                new Dictionary<string, string> { [BethesdaModelRegistration.GameOption] = game }, facts, content);
            Assert.Equal(XnGineGameIdentityStep.GameOption, settled.Step);
            Assert.Equal(game, settled.Game.ToString().ToLowerInvariant());
        }

        // And the two factors differ by 2x, which is the halving the plan's rule prevents.
        Assert.Equal(2.0, ClassicModelUnits.DaggerfallMetersPerUnit / ClassicModelUnits.RedguardMetersPerUnit);
    }

    /// <summary>
    ///     The manifest game's unit factor as a literal pin (design section 4.1), independent of
    ///     <see cref="ClassicModelUnits.For" /> and of the identity's own <c>Units</c> wiring, so a chain that bound
    ///     another row to the game it answered fails here instead of agreeing with itself.
    /// </summary>
    private static double PinnedMetersPerUnit(string game)
    {
        return game switch
        {
            "Daggerfall" => 1.0 / 10240,
            "Battlespire" => 1.0 / 16384,
            "Redguard" => 1.0 / 20480,
            _ => throw new ArgumentOutOfRangeException(nameof(game), game, "Not an XnGine game.")
        };
    }

    /// <summary>The shell's option bag for an input path: the install walk-up as bmt.classic-game, or nothing.</summary>
    private static IReadOnlyDictionary<string, string> ShellOptions(string path)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        if (ClassicGameLocator.DetectRootForFile(path) is { } detected)
        {
            options[BethesdaModelRegistration.ClassicGameOption] = detected.Profile.Game.ToString();
            options[BethesdaModelRegistration.ClassicGameEvidenceOption] = "install root " + detected.Root;
        }

        return options;
    }

    private (IAssetSource Source, AssetReference Reference) OpenLoose(string path)
    {
        var folder = new FolderAssetSource(Path.GetDirectoryName(path)!);
        _sources.Add(folder);
        return (folder, new AssetReference(folder.Id, Path.GetFileName(path)));
    }

    private (IAssetSource Source, AssetReference Reference) OpenArchive(string location, Cut1cCoverFile file)
    {
        var source = new BethesdaBrowseSource(
            AssetBrowseSession.TryOpenGameArchive(location) ?? AssetBrowseSession.OpenArchive(location));
        _sources.Add(source);
        var entryPath = string.Equals(file.Container, Cut1cCoverFile.RobContainer, StringComparison.Ordinal)
            ? file.Entry + ".3D"
            : file.Entry;
        return (source, new AssetReference(source.Id, entryPath));
    }

    private static ClassicContainerKind ContainerKindOf(string container)
    {
        return container switch
        {
            Cut1cCoverFile.Arch3dContainer => ClassicContainerKind.NumberedXnGineBsa,
            Cut1cCoverFile.XnGineBsaContainer => ClassicContainerKind.NamedXnGineBsa,
            Cut1cCoverFile.RobContainer => ClassicContainerKind.RedguardRob,
            _ => throw new ArgumentOutOfRangeException(nameof(container), container, "Not an archive container.")
        };
    }

    /// <summary>A manifest source string (Sample/...) as a file on this machine, or null.</summary>
    private static string? Locate(string source)
    {
        return source.StartsWith(SamplePrefix, StringComparison.Ordinal)
            ? RealAssetPaths.SampleFile(source[SamplePrefix.Length..])
            : null;
    }
}
