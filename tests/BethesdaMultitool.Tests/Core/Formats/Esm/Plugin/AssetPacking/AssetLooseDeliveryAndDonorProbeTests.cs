using BethesdaMultitool.Core.Formats.Esm.Plugin.AssetPacking;
using BethesdaMultitool.Core.Formats.Esm.Reporting;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Plugin.AssetPacking;

/// <summary>
///     Two defects found 2026-08-15 while checking our output against the documented BSA
///     rules, both measured on the shipped v151 builds:
///     <list type="number">
///         <item>
///             A donor handed to us as a title root rather than a Data folder was classified
///             PC, so 82 loose <c>.xma</c> packed without conversion — Xbox audio the PC
///             engine has no decoder for.
///         </item>
///         <item>
///             46 <c>.mp3</c> were packed into a BSA, which the FNV engine cannot read.
///         </item>
///     </list>
/// </summary>
public sealed class AssetLooseDeliveryAndDonorProbeTests : IDisposable
{
    /// <summary>"TES4" byte-reversed — what a big-endian Xbox 360 plugin header reads as.</summary>
    private static readonly byte[] BigEndianEsmHeader = "4SET"u8.ToArray();

    private static readonly byte[] LittleEndianEsmHeader = "TES4"u8.ToArray();

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"loosedonor-{Guid.NewGuid():N}");

    private bool _disposed;

    public AssetLooseDeliveryAndDonorProbeTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, true);
            }
        }
        catch
        {
            // best-effort
        }

        GC.SuppressFinalize(this);
    }

    private string MakeTree(string relativeDataDir, byte[] esmHeader)
    {
        var dataDir = Path.Combine(_root, relativeDataDir);
        Directory.CreateDirectory(dataDir);
        File.WriteAllBytes(Path.Combine(dataDir, "FalloutNV.esm"), esmHeader);
        return _root;
    }

    [Fact]
    public void EsmAtTopLevel_IsDetected()
    {
        // The already-working case: the caller passed the Data folder itself.
        var folder = MakeTree(".", BigEndianEsmHeader);
        Assert.True(Xbox360FolderDetector.DetectIsXbox360Format(folder));
    }

    [Fact]
    public void EsmTwoLevelsDown_IsDetected()
    {
        // THE regression. This is the real shape of the July 21, 2010 donor:
        // "<build>\FalloutNV\Data\FalloutNV.esm". Probing only the top level returned
        // false, and every loose asset beneath it then packed unconverted.
        var folder = MakeTree(Path.Combine("FalloutNV", "Data"), BigEndianEsmHeader);
        Assert.True(Xbox360FolderDetector.DetectIsXbox360Format(folder));
    }

    [Fact]
    public void PcEsmNestedTwoLevelsDown_IsNotDetected()
    {
        // Descending must not make the probe credulous: a PC donor stays PC.
        var folder = MakeTree(Path.Combine("FalloutNV", "Data"), LittleEndianEsmHeader);
        Assert.False(Xbox360FolderDetector.DetectIsXbox360Format(folder));
    }

    [Fact]
    public void EsmBelowTheDepthBound_IsNotDetected()
    {
        // Documents the bound rather than pretending it doesn't exist: past a few levels
        // we would be walking the asset tree, which never holds a plugin or archive.
        var folder = MakeTree(
            Path.Combine("a", "b", "c", "d", "Data"), BigEndianEsmHeader);
        Assert.False(Xbox360FolderDetector.DetectIsXbox360Format(folder));
    }

    [Fact]
    public void EmptyTree_IsNotDetected()
    {
        Assert.False(Xbox360FolderDetector.DetectIsXbox360Format(_root));
    }

    [Theory]
    [InlineData("music\\endgame\\endgame_02.mp3", true)]
    [InlineData("sound\\songs\\radio\\enclave\\america.mp3", true)]
    [InlineData("sound\\fx\\amb\\wind_lp.wav", false)]
    [InlineData("sound\\voice\\x\\line.ogg", false)]
    [InlineData("textures\\armor\\x.dds", false)]
    [InlineData("meshes\\clutter\\bucket.nif", false)]
    public void RequiresLooseDelivery_IsMp3Only(string path, bool expected)
    {
        // Narrower than the "audio must be in an uncompressed BSA" rule, which BsaWriter
        // already satisfies for the audio buckets. MP3 fails from a BSA either way.
        Assert.Equal(expected, AssetPathRules.RequiresLooseDelivery(path));
    }

    [Fact]
    public void WriteLooseAssets_LaysOutDataRelativeBesideTheArchives()
    {
        var bsaPath = Path.Combine(_root, "out", "mod.bsa");
        Directory.CreateDirectory(Path.GetDirectoryName(bsaPath)!);

        var (directory, bytes) = AssetPackingService.WriteLooseAssets(
            bsaPath,
            [
                ("music\\endgame\\endgame_02.mp3", [1, 2, 3]),
                ("sound\\songs\\radio\\enclave\\america.mp3", [4, 5])
            ],
            NullConversionProgressSink.Instance);

        Assert.Equal(Path.GetFullPath(Path.Combine(_root, "out")), directory);
        Assert.Equal(5, bytes);

        // Installed to Data\, MUSC FNAM "endgame\endgame_02.mp3" resolves under Data\Music\
        // and SOUN FNAM "songs\radio\..." under Data\Sound\ — so the tree must carry those
        // roots verbatim.
        Assert.True(File.Exists(
            Path.Combine(_root, "out", "music", "endgame", "endgame_02.mp3")));
        Assert.True(File.Exists(
            Path.Combine(_root, "out", "sound", "songs", "radio", "enclave", "america.mp3")));
    }

    [Fact]
    public void WriteLooseAssets_WithNothingToWrite_ReportsNoDirectory()
    {
        var (directory, bytes) = AssetPackingService.WriteLooseAssets(
            Path.Combine(_root, "out", "mod.bsa"), [], NullConversionProgressSink.Instance);

        Assert.Null(directory);
        Assert.Equal(0, bytes);
    }

    [Fact]
    public void WriteLooseAssets_DeduplicatesRepeatedPaths()
    {
        // Two records can name the same track; the packed list carries one entry per
        // request, so the same relative path can appear twice.
        var bsaPath = Path.Combine(_root, "out", "mod.bsa");
        Directory.CreateDirectory(Path.GetDirectoryName(bsaPath)!);

        var (_, bytes) = AssetPackingService.WriteLooseAssets(
            bsaPath,
            [
                ("music\\endgame\\endgame_02.mp3", [1, 2, 3]),
                ("music\\endgame\\endgame_02.mp3", [1, 2, 3])
            ],
            NullConversionProgressSink.Instance);

        Assert.Equal(3, bytes);
    }
}