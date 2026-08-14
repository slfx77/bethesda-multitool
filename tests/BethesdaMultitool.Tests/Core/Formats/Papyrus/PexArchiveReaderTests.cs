using BethesdaMultitool.Core.Formats.Bsa;
using BethesdaMultitool.Core.Formats.Papyrus;
using BethesdaMultitool.Tests.Core.Formats.Bsa;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Papyrus;

public sealed class PexArchiveReaderTests
{
    [Fact]
    public void Open_Bsa_FiltersFindsAndParsesPapyrusScripts()
    {
        var archivePath = Path.Combine(Path.GetTempPath(), $"pex_archive_{Guid.NewGuid():N}.bsa");
        var pexBytes = PexParserTests.BuildFixture(PexGameId.Skyrim, 2);
        try
        {
            using (var writer = new BsaWriter(false, embedFileNames: false))
            {
                writer.AddFile("scripts\\Example.pex", pexBytes);
                writer.AddFile("meshes\\NotAScript.nif", "not-a-pex"u8.ToArray());
                writer.Write(archivePath);
            }

            using var archive = PexArchiveReader.Open(archivePath);

            Assert.False(archive.IsBa2);
            var entry = Assert.Single(archive.Entries);
            Assert.Equal("scripts\\Example.pex", entry.VirtualPath, true);
            Assert.Same(entry, archive.Find("scripts/Example.pex"));
            Assert.Same(entry, archive.Find("example"));

            var parsed = archive.Parse(entry);
            Assert.Equal(PexGameId.Skyrim, parsed.Header.GameId);
            Assert.Equal("ExampleScript", Assert.Single(parsed.Objects).Name.Value);
        }
        finally
        {
            File.Delete(archivePath);
        }
    }

    [Fact]
    public void Open_Ba2_FiltersAndParsesPapyrusScripts()
    {
        var archivePath = Path.Combine(Path.GetTempPath(), $"pex_archive_{Guid.NewGuid():N}.ba2");
        var pexBytes = PexParserTests.BuildFixture(PexGameId.Fallout4, 9);
        File.WriteAllBytes(
            archivePath,
            ArchiveReaderTests.BuildGnrlBa2(
                0x504558u,
                "scripts\\Example.pex",
                pexBytes));
        try
        {
            using var archive = PexArchiveReader.Open(archivePath);

            Assert.True(archive.IsBa2);
            var entry = Assert.Single(archive.Entries);
            Assert.Equal(pexBytes, archive.Extract(entry));
            Assert.Equal(PexGameId.Fallout4, archive.Parse(entry).Header.GameId);
        }
        finally
        {
            File.Delete(archivePath);
        }
    }

    [Fact]
    public void Open_Ba2_ParsesFallout76ObjectTailAndRawFunctionFlags()
    {
        var archivePath = Path.Combine(Path.GetTempPath(), $"pex_archive_{Guid.NewGuid():N}.ba2");
        var pexBytes = PexParserTests.BuildFixture(
            PexGameId.Fallout76,
            15,
            functionFlags: 0x28,
            stateNameIndex: 4,
            trailingStringReferences: [4]);
        File.WriteAllBytes(
            archivePath,
            ArchiveReaderTests.BuildGnrlBa2(
                0x504558u,
                "scripts\\Example76.pex",
                pexBytes));
        try
        {
            using var archive = PexArchiveReader.Open(archivePath);

            var file = archive.Parse(Assert.Single(archive.Entries));
            var obj = Assert.Single(file.Objects);
            Assert.True(obj.HasFallout76TrailingStateReferenceTable);
            Assert.Equal(["Auto"], obj.Fallout76TrailingStateReferences.Select(x => x.Value));
            Assert.Equal(
                (byte)0x28,
                Assert.Single(Assert.Single(obj.States).Functions).RawFlags);
        }
        finally
        {
            File.Delete(archivePath);
        }
    }

    [Fact]
    public void Find_AmbiguousSubstring_RequiresAUniqueSelector()
    {
        var archivePath = Path.Combine(Path.GetTempPath(), $"pex_archive_{Guid.NewGuid():N}.bsa");
        var pexBytes = PexParserTests.BuildFixture(PexGameId.Skyrim, 2);
        try
        {
            using (var writer = new BsaWriter(false, embedFileNames: false))
            {
                writer.AddFile("scripts\\ExampleOne.pex", pexBytes);
                writer.AddFile("scripts\\ExampleTwo.pex", pexBytes);
                writer.Write(archivePath);
            }

            using var archive = PexArchiveReader.Open(archivePath);

            var exception = Assert.Throws<InvalidOperationException>(() => archive.Find("example"));
            Assert.Contains("more than one", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(archivePath);
        }
    }
}
