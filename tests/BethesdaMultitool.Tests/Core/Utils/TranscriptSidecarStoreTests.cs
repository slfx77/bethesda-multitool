using BethesdaMultitool.Core.Utils;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Utils;

public sealed class TranscriptSidecarStoreTests
{
    private const string LocalAppData = @"C:\Users\tester\AppData\Local";

    [Fact]
    public void ResolveDirectory_InsideACorpusBuild_UsesTheSiblingTranscriptsTree()
    {
        var resolved = TranscriptSidecarStore.ResolveDirectory(
            @"C:\dev\Repo\Sample\Builds\Game (2010-7-21, X360 - Prototype)\FalloutNV\Data",
            null,
            LocalAppData);

        Assert.Equal(
            @"C:\dev\Repo\Sample\Transcripts\Game (2010-7-21, X360 - Prototype)\FalloutNV\Data",
            resolved);
    }

    [Fact]
    public void ResolveDirectory_TheBuildRootItself_MapsToItsOwnTranscriptsDirectory()
    {
        var resolved = TranscriptSidecarStore.ResolveDirectory(
            @"C:\dev\Repo\sample\builds\Partial Data Build\",
            null,
            LocalAppData);

        Assert.Equal(@"C:\dev\Repo\sample\Transcripts\Partial Data Build", resolved);
    }

    [Fact]
    public void ResolveDirectory_NeverResolvesInsideTheDataDirectory()
    {
        string[] dataDirectories =
        [
            @"C:\dev\Repo\Sample\Builds\Some Build\Data",
            @"E:\SteamLibrary\steamapps\common\Fallout New Vegas\Data",
            @"C:\Sample\Builds"
        ];

        foreach (var dataDirectory in dataDirectories)
        {
            var resolved = TranscriptSidecarStore.ResolveDirectory(dataDirectory, null, LocalAppData);
            var inside = resolved.Equals(dataDirectory, StringComparison.OrdinalIgnoreCase) ||
                         resolved.StartsWith(dataDirectory + @"\", StringComparison.OrdinalIgnoreCase);
            Assert.False(inside, $"{resolved} lies inside {dataDirectory}");
        }
    }

    [Fact]
    public void ResolveDirectory_OutsideACorpus_MirrorsTheFullPathUnderLocalAppData()
    {
        var resolved = TranscriptSidecarStore.ResolveDirectory(
            @"E:\SteamLibrary\steamapps\common\Fallout New Vegas\Data",
            null,
            LocalAppData);

        Assert.Equal(
            LocalAppData +
            @"\BethesdaAudioTranscriber\Transcripts\E\SteamLibrary\steamapps\common\Fallout New Vegas\Data",
            resolved);
    }

    [Fact]
    public void ResolveDirectory_BuildsWithoutASampleParent_IsNotACorpus()
    {
        var resolved = TranscriptSidecarStore.ResolveDirectory(@"D:\Builds\Game\Data", null, LocalAppData);

        Assert.Equal(LocalAppData + @"\BethesdaAudioTranscriber\Transcripts\D\Builds\Game\Data", resolved);
    }

    [Fact]
    public void ResolveDirectory_AnExplicitRoot_WinsOverTheCorpusRule()
    {
        var resolved = TranscriptSidecarStore.ResolveDirectory(
            @"C:\dev\Repo\Sample\Builds\Some Build\Data",
            @"D:\Store",
            LocalAppData);

        Assert.Equal(@"D:\Store\C\dev\Repo\Sample\Builds\Some Build\Data", resolved);
    }

    [Fact]
    public void ResolveDirectory_AUncPath_KeepsHostAndShareAsSegments()
    {
        var resolved = TranscriptSidecarStore.ResolveDirectory(@"\\nas\games\FNV\Data", null, LocalAppData);

        Assert.Equal(LocalAppData + @"\BethesdaAudioTranscriber\Transcripts\UNC\nas\games\FNV\Data", resolved);
    }

    [Fact]
    public void FindExisting_PrefersTheStoreAndFallsBackToALegacySidecarInTheDataDirectory()
    {
        Assert.SkipWhen(
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(TranscriptSidecarStore.RootVariable)),
            $"{TranscriptSidecarStore.RootVariable} is set on this machine and redirects the store.");
        using var fixture = new TemporaryDirectory();
        var dataDirectory = Path.Combine(fixture.Path, "Sample", "Builds", "Build", "Data");
        Directory.CreateDirectory(dataDirectory);
        var name = TranscriptSidecarStore.TranscriptFileName;

        Assert.Null(TranscriptSidecarStore.FindExisting(dataDirectory, name));

        var legacy = Path.Combine(dataDirectory, name);
        File.WriteAllText(legacy, "{}");
        Assert.Equal(legacy, TranscriptSidecarStore.FindExisting(dataDirectory, name));

        var stored = TranscriptSidecarStore.PathFor(dataDirectory, name);
        Assert.Equal(Path.Combine(fixture.Path, "Sample", "Transcripts", "Build", "Data", name), stored);
        Directory.CreateDirectory(Path.GetDirectoryName(stored)!);
        File.WriteAllText(stored, "{}");
        Assert.Equal(stored, TranscriptSidecarStore.FindExisting(dataDirectory, name));
        Assert.True(File.Exists(legacy));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = Directory.CreateDirectory(System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"transcript-sidecar-store-{Guid.NewGuid():N}")).FullName;
        }

        internal string Path { get; }

        public void Dispose()
        {
            Directory.Delete(Path, true);
        }
    }
}
