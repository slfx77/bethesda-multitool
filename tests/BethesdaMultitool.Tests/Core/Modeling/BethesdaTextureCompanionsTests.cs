using BethesdaMultitool.Core.Formats.Bsa;
using BethesdaMultitool.Core.Modeling;
using BethesdaMultitool.Tests.Core.Modeling.Nif;
using BethesdaMultitool.Tests.Helpers;
using Slfx77.Multitool.Core.Assets;
using Slfx77.Multitool.Core.Models.Sources;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Modeling;

/// <summary>
///     BMT's texture companion resolver over a Bethesda data folder (plan section 4, "Resolution"): loose files shadow
///     archives with the provenance of the layer that actually supplied the bytes, <c>.dds</c> falls back to
///     <c>.ddx</c>, occurrences open the bytes they were resolved to, and the data root is inferred from the input. The
///     archive is a real synthetic BSA written by BMT's <see cref="BsaWriter" /> into a private temporary directory.
/// </summary>
public sealed class BethesdaTextureCompanionsTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("bmt-texture-companions-").FullName;

    /// <summary>Removes the temporary directory.</summary>
    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, true);
        }
        catch (IOException)
        {
            // A leftover temporary directory is harmless; the test result stands.
        }
    }

    /// <summary>
    ///     A loose texture shadows the same path in a BSA, and its provenance is the loose root. Control: with the loose
    ///     copy removed the archive's bytes resolve, with the archive as provenance.
    /// </summary>
    [Fact]
    public async Task LooseFileShadowsTheArchive()
    {
        var data = Path.Combine(_directory, "Data");
        Directory.CreateDirectory(Path.Combine(data, "textures", "t"));
        byte[] loose = [1, 2, 3, 4];
        byte[] packed = [9, 8, 7, 6, 5];
        var loosePath = Path.Combine(data, "textures", "t", "a.dds");
        await File.WriteAllBytesAsync(loosePath, loose, TestContext.Current.CancellationToken);
        using (var writer = new BsaWriter(false))
        {
            writer.AddFile(@"textures\t\a.dds", packed);
            writer.Write(Path.Combine(data, "Test - Textures.bsa"));
        }

        var (shadowBytes, shadowProvenance) = await ResolveAsync(data, @"textures\t\a.dds");
        File.Delete(loosePath);
        var (archiveBytes, archiveProvenance) = await ResolveAsync(data, @"textures\t\a.dds");

        Assert.Equal(loose, shadowBytes);
        Assert.Equal(Path.GetFullPath(data), shadowProvenance);
        Assert.Equal(packed, archiveBytes);
        Assert.EndsWith("Test - Textures.bsa", archiveProvenance, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     A <c>.dds</c> path absent everywhere falls back to the same path as <c>.ddx</c> (the Xbox spelling). Control:
    ///     when the <c>.dds</c> exists it wins.
    /// </summary>
    [Theory]
    [InlineData(false, "textures/t/a.ddx")]
    [InlineData(true, "textures/t/a.dds")]
    public async Task DdsFallsBackToDdx(bool ddsPresent, string expected)
    {
        var files = ddsPresent
            ? new[] { (@"textures\t\a.ddx", new byte[] { 1 }), (@"textures\t\a.dds", new byte[] { 2 }) }
            : [(@"textures\t\a.ddx", new byte[] { 1 })];
        await using var companions = NifModelTestSupport.Companions(files);
        var (owner, input) = NifModelTestSupport.Open([0]);
        await using var ownerInput = input;

        var matches = await companions.ResolveAsync(owner, @"Textures\T\A.dds", TestContext.Current.CancellationToken);

        Assert.Equal(expected, Assert.Single(matches).Reference.Path);
    }

    /// <summary>
    ///     Resolution reads no bytes, so a resolved texture that is never opened (refused by a budget, ambiguous) costs no
    ///     memory; one file reached through two spellings (<c>.dds</c> falling back to <c>.ddx</c>, and <c>.ddx</c>) is
    ///     one reference, read once when opened. Control: a different file is a different reference.
    /// </summary>
    [Fact]
    public async Task Resolution_ReadsNothing_AndOneFileIsOneReference()
    {
        var files = new MemoryGameFileSystem("memory-data", (@"textures\t\a.ddx", [1]), (@"textures\t\b.ddx", [2]));
        await using var companions = new BethesdaTextureCompanions(files, "memory-data");
        var (owner, input) = NifModelTestSupport.Open([0]);
        await using var ownerInput = input;

        var viaDds = Assert.Single(await companions.ResolveAsync(owner, @"textures\t\a.dds",
            TestContext.Current.CancellationToken));
        var viaDdx = Assert.Single(await companions.ResolveAsync(owner, @"Textures\T\A.ddx",
            TestContext.Current.CancellationToken));
        var other = Assert.Single(await companions.ResolveAsync(owner, @"textures\t\b.ddx",
            TestContext.Current.CancellationToken));

        Assert.Equal(0, files.Reads);
        Assert.Equal(viaDds.Reference, viaDdx.Reference);
        Assert.NotEqual(viaDds.Reference, other.Reference);
        Assert.Equal([1], await ReadAllAsync(viaDdx));
        Assert.Equal(1, files.Reads);
    }

    /// <summary>
    ///     A resolved occurrence opens the bytes it was resolved to, even twice; a reference with an unknown occurrence
    ///     or from another source is refused. A missing file, and a name that cannot form a relative virtual path
    ///     (a <c>..</c> segment), resolve to nothing.
    /// </summary>
    [Fact]
    public async Task Occurrences_OpenTheirOwnBytes_AndForeignReferencesAreRefused()
    {
        byte[] bytes = [4, 5, 6];
        await using var companions = NifModelTestSupport.Companions((@"textures\a.dds", bytes));
        var (owner, input) = NifModelTestSupport.Open([0]);
        await using var ownerInput = input;
        var item = Assert.Single(await companions.ResolveAsync(owner, @"textures\a.dds",
            TestContext.Current.CancellationToken));

        Assert.Equal(bytes, await ReadAllAsync(item));
        Assert.Equal(bytes, await ReadAllAsync(item));
        Assert.Equal("memory-data", item.Entry.Provenance);
        await Assert.ThrowsAsync<ArgumentException>(async () => await companions.OpenReadAsync(
            new AssetReference(companions.Id, "textures/a.dds", "999"), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(async () => await companions.OpenReadAsync(
            new AssetReference("other", "textures/a.dds"), TestContext.Current.CancellationToken));
        Assert.Empty(await companions.ResolveAsync(owner, @"textures\missing.dds",
            TestContext.Current.CancellationToken));
        Assert.Empty(await companions.ResolveAsync(owner, @"textures\..\a.dds",
            TestContext.Current.CancellationToken));
    }

    /// <summary>
    ///     The data root is the nearest ancestor holding a <c>textures</c> folder. Control: without one, nothing is
    ///     inferred.
    /// </summary>
    [Fact]
    public void TryInferDataRoot_FindsTheNearestTexturesAncestor()
    {
        var data = Path.Combine(_directory, "Game", "Data");
        var meshes = Path.Combine(data, "meshes", "clutter");
        Directory.CreateDirectory(meshes);
        var model = Path.Combine(meshes, "cup.nif");
        File.WriteAllBytes(model, [0]);

        var before = BethesdaTextureCompanions.TryInferDataRoot(model, out var none);
        Directory.CreateDirectory(Path.Combine(data, "textures"));
        var after = BethesdaTextureCompanions.TryInferDataRoot(model, out var root);
        var fromDirectory = BethesdaTextureCompanions.TryInferDataRoot(meshes, out var directoryRoot);

        Assert.False(before && none.StartsWith(_directory, StringComparison.OrdinalIgnoreCase));
        Assert.True(after);
        Assert.Equal(Path.GetFullPath(data), root);
        Assert.True(fromDirectory);
        Assert.Equal(Path.GetFullPath(data), directoryRoot);
    }

    private static async Task<(byte[] Bytes, string? Provenance)> ResolveAsync(string data, string name)
    {
        await using var companions = BethesdaTextureCompanions.OpenDataRoots([data]);
        var (owner, input) = NifModelTestSupport.Open([0]);
        await using var ownerInput = input;
        var item = Assert.Single(await companions.ResolveAsync(owner, name, TestContext.Current.CancellationToken));
        return (await ReadAllAsync(item), item.Entry.Provenance);
    }

    private static async Task<byte[]> ReadAllAsync(ModelSourceItem item)
    {
        await using var stream = await item.OpenReadAsync(TestContext.Current.CancellationToken);
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy, TestContext.Current.CancellationToken);
        return copy.ToArray();
    }
}
