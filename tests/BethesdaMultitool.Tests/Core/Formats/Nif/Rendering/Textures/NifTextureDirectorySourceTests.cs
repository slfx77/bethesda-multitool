using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Dds;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Textures;

/// <summary>Exercises loose texture path identity and fallback using bounded real temporary files.</summary>
public sealed class NifTextureDirectorySourceTests
{
    /// <summary>Normalized keys resolve either separator while metadata retains physical filename spelling.</summary>
    /// <param name="request">A normalized texture key.</param>
    [Theory]
    [InlineData("textures/armor/iron.dds")]
    [InlineData(@"textures\armor\iron.dds")]
    public void MixedCasePhysicalPath_LoadsAndReportsOriginalSpelling(string request)
    {
        using var directory = new TemporaryDirectory();
        var bytes = CreateDds(0xF800);
        var physical = directory.Write("Textures/Armor/IRON.DDS", bytes);
        using var source = new NifTextureDirectorySource(directory.Path);

        Assert.True(source.Exists(request));
        Assert.Equal(bytes, source.TryLoadRaw(request));
        AssertSolid(source.TryLoad(request), 255, 0);
        AssertMetadata(source, request, physical);
    }

    /// <summary>Alternate extensions affect decoded lookup and probes, retaining exact-extension raw identity.</summary>
    /// <param name="requestedExtension">The absent requested extension.</param>
    /// <param name="physicalExtension">The existing alternate extension.</param>
    [Theory]
    [InlineData("dds", "DDX")]
    [InlineData("ddx", "DDS")]
    public void MissingPrimary_DecodedLoadAndProbeUseAlternateButRawAndMetadataDoNot(
        string requestedExtension, string physicalExtension)
    {
        using var directory = new TemporaryDirectory();
        directory.Write($"Textures/IRON.{physicalExtension}", CreateDds(0x07E0));
        using var source = new NifTextureDirectorySource(directory.Path);
        var request = $"textures/iron.{requestedExtension}";

        Assert.True(source.Exists(request));
        AssertSolid(source.TryLoad(request), 0, 255);
        Assert.Null(source.TryLoadRaw(request));
        Assert.False(source.TryGetAssetMetadata(request, out _));
    }

    /// <summary>A valid primary wins; invalid primary bytes permit decoded fallback without changing raw identity.</summary>
    /// <param name="requestedExtension">The preferred extension.</param>
    /// <param name="alternateExtension">The distinct fallback extension.</param>
    [Theory]
    [InlineData("dds", "DDX")]
    [InlineData("ddx", "DDS")]
    public void PrimaryWins_AndAnUndecodablePrimaryAllowsDecodedAlternate(
        string requestedExtension, string alternateExtension)
    {
        using var directory = new TemporaryDirectory();
        var request = $"textures/iron.{requestedExtension}";
        var primaryBytes = CreateDds(0xF800);
        var primary = directory.Write($"Textures/IRON.{requestedExtension.ToUpperInvariant()}", primaryBytes);
        directory.Write($"Textures/IRON.{alternateExtension}", CreateDds(0x07E0));
        using var source = new NifTextureDirectorySource(directory.Path);

        AssertSolid(source.TryLoad(request), 255, 0);
        Assert.Equal(primaryBytes, source.TryLoadRaw(request));
        AssertMetadata(source, request, primary);

        byte[] malformed = [0, 1, 2];
        File.WriteAllBytes(primary, malformed);
        Assert.True(source.Exists(request));
        AssertSolid(source.TryLoad(request), 0, 255);
        Assert.Equal(malformed, source.TryLoadRaw(request));
        AssertMetadata(source, request, primary);
    }

    /// <summary>The same source observes misses, additions, replacements and removals without stale lookup entries.</summary>
    [Fact]
    public void RepeatedRequest_ObservesCreationReplacementAndRemovalWithoutAnIndex()
    {
        using var directory = new TemporaryDirectory();
        using var source = new NifTextureDirectorySource(directory.Path);
        const string request = "textures/iron.dds";
        AssertDeclined(source, request);

        var first = directory.Write("Textures/IRON.DDS", CreateDds(0xF800));
        AssertSolid(source.TryLoad(request), 255, 0);
        AssertMetadata(source, request, first);
        File.Delete(first);

        var replacement = directory.Write("Textures/Iron.DdS", CreateDds(0x07E0));
        AssertSolid(source.TryLoad(request), 0, 255);
        AssertMetadata(source, request, replacement);
        File.Delete(replacement);
        AssertDeclined(source, request);
    }

    /// <summary>Ambiguous case-sensitive files decline; case-insensitive files preserve their unique positive control.</summary>
    [Fact]
    public void FileCaseAliases_DeclineAmbiguityEvenWithExactSpellingOrValidAlternate()
    {
        using var directory = new TemporaryDirectory();
        var physical = directory.Write("IRON.DDS", CreateDds(0xF800));
        var distinct = TryCreateDistinctFile(System.IO.Path.Combine(directory.Path, "iron.dds"), CreateDds(0x07E0));
        directory.Write("IRON.DDX", CreateDds(0x07E0));
        using var source = new NifTextureDirectorySource(directory.Path);

        TestContext.Current.TestOutputHelper?.WriteLine($"Distinct file case aliases supported: {distinct}");
        if (distinct)
        {
            AssertDeclined(source, "IRON.DDS");
            AssertDeclined(source, "iron.dds");
        }
        else
        {
            AssertSolid(source.TryLoad("iron.dds"), 255, 0);
            AssertMetadata(source, "iron.dds", physical);
        }
    }

    /// <summary>Ambiguous directory aliases cannot elect one subtree, even when the request uses exact case.</summary>
    [Fact]
    public void DirectoryCaseAliases_DeclineAmbiguityInsteadOfSelectingOneTree()
    {
        using var directory = new TemporaryDirectory();
        var physical = directory.Write("Textures/IRON.DDS", CreateDds(0xF800));
        Directory.CreateDirectory(System.IO.Path.Combine(directory.Path, "textures"));
        var aliases = Directory.EnumerateDirectories(directory.Path)
            .Count(path => string.Equals(System.IO.Path.GetFileName(path), "textures", StringComparison.OrdinalIgnoreCase));
        using var source = new NifTextureDirectorySource(directory.Path);

        TestContext.Current.TestOutputHelper?.WriteLine($"Distinct directory case aliases supported: {aliases == 2}");
        if (aliases == 2)
        {
            directory.Write("textures/IRON.DDS", CreateDds(0x07E0));
            AssertDeclined(source, "Textures/IRON.DDS");
            AssertDeclined(source, "textures/iron.dds");
        }
        else
        {
            Assert.Equal(1, aliases);
            AssertSolid(source.TryLoad("textures/iron.dds"), 255, 0);
            AssertMetadata(source, "textures/iron.dds", physical);
        }
    }

    /// <summary>Nonrelative syntax and parents cannot use the caller's root as a gateway to other files.</summary>
    /// <param name="request">One rejected game-key syntax.</param>
    [Theory]
    [InlineData("../Outside.DDS")]
    [InlineData(@"sub\..\..\Outside.DDS")]
    [InlineData("/Outside.DDS")]
    [InlineData(@"\Outside.DDS")]
    [InlineData(@"\\server\share\Outside.DDS")]
    [InlineData(@"C:\Outside.DDS")]
    [InlineData("C:Outside.DDS")]
    [InlineData("outside\0.dds")]
    public void NonRelativeOrParentPath_DeclinesWithoutReadingOutsideRoot(string request)
    {
        using var directory = new TemporaryDirectory();
        directory.Write("Outside.DDS", CreateDds(0xF800));
        var root = Directory.CreateDirectory(System.IO.Path.Combine(directory.Path, "Data")).FullName;
        using var source = new NifTextureDirectorySource(root);

        AssertDeclined(source, request);
        AssertDeclined(source, System.IO.Path.Combine(directory.Path, "Outside.DDS"));
    }

    /// <summary>Lexical current-directory segments and repeated interior separators retain data-relative lookup.</summary>
    [Fact]
    public void CurrentDirectorySegmentsAndRepeatedSeparators_RetainRootRelativeLookup()
    {
        using var directory = new TemporaryDirectory();
        var physical = directory.Write("Textures/Armor/IRON.DDS", CreateDds(0xF800));
        using var source = new NifTextureDirectorySource(directory.Path);
        const string request = @".\textures//./armor\\iron.dds";

        AssertSolid(source.TryLoad(request), 255, 0);
        AssertMetadata(source, request, physical);
    }

    /// <summary>A match cannot bypass complete uniqueness verification when the remaining budget is insufficient.</summary>
    [Fact]
    public void EntryBudget_RequiresCompleteSiblingEnumerationBeforeSuccess()
    {
        using var directory = new TemporaryDirectory();
        directory.Write("IRON.DDS", CreateDds(0xF800));
        directory.Write("other-a.bin", []);
        directory.Write("other-b.bin", []);
        directory.Write("other-c.bin", []);
        using var insufficient = new NifTextureDirectorySource(directory.Path, maximumEntryVisits: 3);
        using var sufficient = new NifTextureDirectorySource(directory.Path, maximumEntryVisits: 4);

        AssertDeclined(insufficient, "iron.dds");
        AssertSolid(sufficient.TryLoad("iron.dds"), 255, 0);
        Assert.True(sufficient.Exists("iron.dds"));
        Assert.NotNull(sufficient.TryLoadRaw("iron.dds"));
        Assert.True(sufficient.TryGetAssetMetadata("iron.dds", out _));
    }

    /// <summary>The alternate extension cannot obtain a fresh enumeration budget after a primary miss.</summary>
    [Fact]
    public void AlternateExtension_SharesTheSameEntryBudget()
    {
        using var directory = new TemporaryDirectory();
        directory.Write("IRON.DDX", CreateDds(0x07E0));
        directory.Write("other.bin", []);
        using var insufficient = new NifTextureDirectorySource(directory.Path, maximumEntryVisits: 3);
        using var sufficient = new NifTextureDirectorySource(directory.Path, maximumEntryVisits: 4);

        AssertDeclined(insufficient, "iron.dds");
        Assert.True(sufficient.Exists("iron.dds"));
        AssertSolid(sufficient.TryLoad("iron.dds"), 0, 255);
    }

    /// <summary>Probes actual filesystem case behavior without overwriting the original test control.</summary>
    /// <param name="path">A differently cased candidate path.</param>
    /// <param name="bytes">The distinct candidate bytes.</param>
    /// <returns>True if the filesystem admitted a second file.</returns>
    private static bool TryCreateDistinctFile(string path, byte[] bytes)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Write(bytes);
            return true;
        }
        catch (IOException) when (File.Exists(path))
        {
            // A case-insensitive filesystem aliases the original file: never overwrite the positive control.
            return false;
        }
    }

    /// <summary>Verifies all source entry points decline a refused or absent identity.</summary>
    /// <param name="source">The source under test.</param>
    /// <param name="request">The requested key.</param>
    private static void AssertDeclined(NifTextureDirectorySource source, string request)
    {
        Assert.False(source.Exists(request));
        Assert.Null(source.TryLoad(request));
        Assert.Null(source.TryLoadRaw(request));
        Assert.False(source.TryGetAssetMetadata(request, out _));
    }

    /// <summary>Compares provenance to the exact physical file instead of a normalized key.</summary>
    /// <param name="source">The source under test.</param>
    /// <param name="request">The requested key.</param>
    /// <param name="physical">The independently written physical path.</param>
    private static void AssertMetadata(NifTextureDirectorySource source, string request, string physical)
    {
        Assert.True(source.TryGetAssetMetadata(request, out var metadata));
        var file = new FileInfo(physical);
        Assert.Equal(file.FullName, metadata.SourcePath);
        Assert.Equal(file.Length, metadata.SourceLength);
        Assert.Equal(file.LastWriteTimeUtc.Ticks, metadata.SourceLastWriteUtcTicks);
    }

    /// <summary>Checks every base-level pixel against the independently authored solid BC1 color.</summary>
    /// <param name="texture">The decoded texture.</param>
    /// <param name="red">Expected red channel.</param>
    /// <param name="green">Expected green channel.</param>
    private static void AssertSolid(DecodedTexture? texture, byte red, byte green)
    {
        Assert.NotNull(texture);
        Assert.Equal(4, texture.Width);
        Assert.Equal(4, texture.Height);
        Assert.Equal(64, texture.Pixels.Length);
        for (var offset = 0; offset < texture.Pixels.Length; offset += 4)
        {
            Assert.Equal(red, texture.Pixels[offset]);
            Assert.Equal(green, texture.Pixels[offset + 1]);
            Assert.Equal((byte)0, texture.Pixels[offset + 2]);
            Assert.Equal((byte)255, texture.Pixels[offset + 3]);
        }
    }

    /// <summary>Authors a complete minimal BC1 DDS with one independently chosen RGB565 endpoint.</summary>
    /// <param name="color">The opaque RGB565 endpoint.</param>
    /// <returns>A bounded 136-byte DDS fixture.</returns>
    private static byte[] CreateDds(ushort color)
    {
        // One solid 4x4 BC1 block. DDS bytes in a .DDX file test extension routing, not DDX codec fidelity.
        var data = new byte[136];
        "DDS "u8.CopyTo(data);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 124);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), 0x1 | 0x2 | 0x4 | 0x1000 | 0x20000);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(28), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(76), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(80), 4);
        "DXT1"u8.CopyTo(data.AsSpan(84));
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(128), color);
        return data;
    }

    /// <summary>Owns only one newly created test directory and its synthetic fixture files.</summary>
    private sealed class TemporaryDirectory : IDisposable
    {
        /// <summary>The platform-native temporary root; Linux validation should place this on ext4.</summary>
        internal string Path { get; } = Directory.CreateTempSubdirectory("bmt-nif-directory-").FullName;

        /// <summary>Writes a synthetic file and returns its original physical spelling.</summary>
        /// <param name="relative">A test-owned relative path.</param>
        /// <param name="bytes">The bounded fixture bytes.</param>
        /// <returns>The written physical path.</returns>
        internal string Write(string relative, byte[] bytes)
        {
            var physical = System.IO.Path.Combine(Path, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(physical)!);
            File.WriteAllBytes(physical, bytes);
            return physical;
        }

        /// <summary>Deletes only this test-owned temporary directory after all source calls have completed.</summary>
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
