using BethesdaMultitool.Core.Utils;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Utils;

public sealed class AtomicFileWriterTests
{
    [Fact]
    public async Task WriteAsync_ReplacesTheTargetOnlyAfterTheTemporaryFileCompletes()
    {
        using var fixture = new TemporaryDirectory();
        var targetPath = Path.Combine(fixture.Path, "map.png");
        await File.WriteAllTextAsync(
            targetPath,
            "old",
            TestContext.Current.CancellationToken);

        await AtomicFileWriter.WriteAsync(
            targetPath,
            (temporaryPath, _) => File.WriteAllTextAsync(
                temporaryPath,
                "new",
                TestContext.Current.CancellationToken),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(
            "new",
            await File.ReadAllTextAsync(targetPath, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(fixture.Path, "*.tmp"));
    }

    [Fact]
    public async Task WriteAsync_WriterFailurePreservesThePreviousTargetAndCleansTheTemporaryFile()
    {
        using var fixture = new TemporaryDirectory();
        var targetPath = Path.Combine(fixture.Path, "map.png");
        await File.WriteAllTextAsync(
            targetPath,
            "old",
            TestContext.Current.CancellationToken);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AtomicFileWriter.WriteAsync(
                targetPath,
                async (temporaryPath, _) =>
                {
                    await File.WriteAllTextAsync(
                        temporaryPath,
                        "partial",
                        TestContext.Current.CancellationToken);
                    throw new InvalidOperationException("synthetic writer failure");
                },
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal("synthetic writer failure", exception.Message);
        Assert.Equal(
            "old",
            await File.ReadAllTextAsync(targetPath, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(fixture.Path, "*.tmp"));
    }

    [Fact]
    public async Task WriteAsync_CancellationAfterWritingPreservesThePreviousTarget()
    {
        using var fixture = new TemporaryDirectory();
        using var cancellationSource = new CancellationTokenSource();
        var targetPath = Path.Combine(fixture.Path, "map.png");
        await File.WriteAllTextAsync(
            targetPath,
            "old",
            TestContext.Current.CancellationToken);
        var observed = new List<AtomicFileWritePhaseTiming>();

#pragma warning disable xUnit1051 // This test must cancel the token supplied to the subject.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            AtomicFileWriter.WriteAsync(
                targetPath,
                async (temporaryPath, _) =>
                {
                    await File.WriteAllTextAsync(
                        temporaryPath,
                        "new",
                        TestContext.Current.CancellationToken);
                    await cancellationSource.CancelAsync();
                },
                cancellationToken: cancellationSource.Token,
                phaseObserver: observed.Add));
#pragma warning restore xUnit1051

        Assert.Equal(
            "old",
            await File.ReadAllTextAsync(targetPath, TestContext.Current.CancellationToken));
        Assert.Empty(observed);
        Assert.Empty(Directory.GetFiles(fixture.Path, "*.tmp"));
    }

    [Fact]
    public async Task WriteAsync_SuccessInvalidatesTheCompanionOnlyAtPublication()
    {
        using var fixture = new TemporaryDirectory();
        var targetPath = Path.Combine(fixture.Path, "map_r0_c0.png");
        var companionPath = Path.Combine(fixture.Path, "map_manifest.json");
        await File.WriteAllTextAsync(
            targetPath,
            "old tile",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            companionPath,
            "old manifest",
            TestContext.Current.CancellationToken);

        await AtomicFileWriter.WriteAsync(
            targetPath,
            (temporaryPath, _) => File.WriteAllTextAsync(
                temporaryPath,
                "new tile",
                TestContext.Current.CancellationToken),
            companionPath,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(
            "new tile",
            await File.ReadAllTextAsync(targetPath, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(companionPath));
        Assert.Empty(Directory.GetFiles(fixture.Path, "*.tmp"));
    }

    [Fact]
    public async Task WriteAsync_ObserverReportsOnlyTheActualMovesInOrder()
    {
        using var fixture = new TemporaryDirectory();
        var targetPath = Path.Combine(fixture.Path, "map_r0_c0.png");
        var companionPath = Path.Combine(fixture.Path, "map_manifest.json");
        await File.WriteAllTextAsync(
            targetPath,
            "old tile",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            companionPath,
            "old manifest",
            TestContext.Current.CancellationToken);
        var observed = new List<AtomicFileWritePhaseTiming>();

        await AtomicFileWriter.WriteAsync(
            targetPath,
            (temporaryPath, _) => File.WriteAllTextAsync(
                temporaryPath,
                "new tile",
                TestContext.Current.CancellationToken),
            companionPath,
            cancellationToken: TestContext.Current.CancellationToken,
            phaseObserver: observed.Add);

        Assert.Collection(
            observed,
            staging =>
            {
                Assert.Equal(AtomicFileWritePhase.CompanionStagingMove, staging.Phase);
                Assert.True(staging.Succeeded);
                Assert.True(staging.WallMilliseconds >= 0d);
            },
            publication =>
            {
                Assert.Equal(AtomicFileWritePhase.TargetPublishMove, publication.Phase);
                Assert.True(publication.Succeeded);
                Assert.True(publication.WallMilliseconds >= 0d);
            });
    }

    [Fact]
    public async Task WriteAsync_ObserverOmitsCompanionPhaseWhenNoCompanionWasMoved()
    {
        using var fixture = new TemporaryDirectory();
        var targetPath = Path.Combine(fixture.Path, "map_r0_c0.png");
        var absentCompanionPath = Path.Combine(fixture.Path, "map_manifest.json");
        var observed = new List<AtomicFileWritePhaseTiming>();

        await AtomicFileWriter.WriteAsync(
            targetPath,
            (temporaryPath, _) => File.WriteAllTextAsync(
                temporaryPath,
                "new tile",
                TestContext.Current.CancellationToken),
            absentCompanionPath,
            cancellationToken: TestContext.Current.CancellationToken,
            phaseObserver: observed.Add);

        var publication = Assert.Single(observed);
        Assert.Equal(AtomicFileWritePhase.TargetPublishMove, publication.Phase);
        Assert.True(publication.Succeeded);
    }

    [Fact]
    public async Task WriteAsync_ObserverFailureCannotChangePublicationOrCompanionInvalidation()
    {
        using var fixture = new TemporaryDirectory();
        var targetPath = Path.Combine(fixture.Path, "map_r0_c0.png");
        var companionPath = Path.Combine(fixture.Path, "map_manifest.json");
        await File.WriteAllTextAsync(
            targetPath,
            "old tile",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            companionPath,
            "old manifest",
            TestContext.Current.CancellationToken);

        await AtomicFileWriter.WriteAsync(
            targetPath,
            (temporaryPath, _) => File.WriteAllTextAsync(
                temporaryPath,
                "new tile",
                TestContext.Current.CancellationToken),
            companionPath,
            cancellationToken: TestContext.Current.CancellationToken,
            phaseObserver: _ => throw new InvalidOperationException("synthetic observer failure"));

        Assert.Equal(
            "new tile",
            await File.ReadAllTextAsync(targetPath, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(companionPath));
        Assert.Empty(Directory.GetFiles(fixture.Path, "*.tmp"));
    }

    [Fact]
    public async Task WriteAsync_TargetPublicationFailureRestoresTheStagedCompanion()
    {
        using var fixture = new TemporaryDirectory();
        var targetPath = Path.Combine(fixture.Path, "target-directory");
        var companionPath = Path.Combine(fixture.Path, "map_manifest.json");
        Directory.CreateDirectory(targetPath);
        await File.WriteAllTextAsync(
            companionPath,
            "old manifest",
            TestContext.Current.CancellationToken);

        var observed = new List<AtomicFileWritePhaseTiming>();
        var publicationFailure = await Record.ExceptionAsync(() => AtomicFileWriter.WriteAsync(
            targetPath,
            (temporaryPath, _) => File.WriteAllTextAsync(
                temporaryPath,
                "new tile",
                TestContext.Current.CancellationToken),
            companionPath,
            cancellationToken: TestContext.Current.CancellationToken,
            phaseObserver: observed.Add));
        Assert.True(
            publicationFailure is IOException or UnauthorizedAccessException,
            $"Expected a filesystem publication failure, got {publicationFailure?.GetType().FullName ?? "none"}.");

        Assert.Equal(
            "old manifest",
            await File.ReadAllTextAsync(companionPath, TestContext.Current.CancellationToken));
        Assert.Collection(
            observed,
            staging =>
            {
                Assert.Equal(AtomicFileWritePhase.CompanionStagingMove, staging.Phase);
                Assert.True(staging.Succeeded);
            },
            publication =>
            {
                Assert.Equal(AtomicFileWritePhase.TargetPublishMove, publication.Phase);
                Assert.False(publication.Succeeded);
            });
        Assert.Empty(Directory.GetFiles(fixture.Path, "*.tmp"));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = Directory.CreateDirectory(System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"atomic-file-writer-{Guid.NewGuid():N}")).FullName;
        }

        internal string Path { get; }

        public void Dispose()
        {
            Directory.Delete(Path, true);
        }
    }
}