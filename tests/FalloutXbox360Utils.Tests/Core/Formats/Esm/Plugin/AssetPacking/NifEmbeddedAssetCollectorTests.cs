using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.AssetPacking;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Plugin.AssetPacking;

/// <summary>
///     NIF stores texture paths inside <c>BSShaderTextureSet</c> blocks as <c>SizedString</c>
///     (uint32 length + ASCII payload, no null terminator). The DMP raw-byte scanner gated
///     on null terminators and missed these — that gap is what made Ulysses' converted
///     outfit render with stale memory textures. These tests pin down the binary scanner
///     so the regression can't come back.
/// </summary>
public class NifEmbeddedAssetCollectorTests
{
    [Fact]
    public void ScanBytes_FindsTexturePathInsideSizedStringPayload()
    {
        // Build a fake BSShaderTextureSet-style block: <count><len1><path1><len2><path2>.
        // No null terminators between strings — exactly the format that defeated the
        // DMP scanner.
        var path1 = "textures\\armor\\ulysses\\UHair_D.dds";
        var path2 = "textures\\armor\\ulysses\\UHair_N.dds";
        var bytes = BuildPackedNifBlock(path1, path2);

        var found = NifEmbeddedAssetCollector.ScanBytes(bytes);

        Assert.Contains("textures\\armor\\ulysses\\uhair_d.dds", found);
        Assert.Contains("textures\\armor\\ulysses\\uhair_n.dds", found);
    }

    [Fact]
    public void ScanBytes_RecognizesXboxStyleDdxExtension()
    {
        // Xbox 360 NIFs occasionally embed the .ddx extension instead of .dds. The
        // converter rewrites the actual texture path to .dds at pack time, but we still
        // need to collect the request so the resolver can find the source .ddx and do the
        // extension swap.
        var path = "textures\\armor\\ulysses\\ulyssesbase_d.ddx";
        var bytes = BuildPackedNifBlock(path);

        var found = NifEmbeddedAssetCollector.ScanBytes(bytes);

        Assert.Contains("textures\\armor\\ulysses\\ulyssesbase_d.ddx", found);
    }

    [Fact]
    public void ScanBytes_AcceptsForwardSlashSeparator()
    {
        // Some prototype meshes were authored with forward slashes; the collector
        // normalizes them to backslashes.
        var path = "textures/gore/MeatCapGore01.dds";
        var bytes = BuildPackedNifBlock(path);

        var found = NifEmbeddedAssetCollector.ScanBytes(bytes);

        Assert.Contains("textures\\gore\\meatcapgore01.dds", found);
    }

    [Fact]
    public void ScanBytes_IgnoresBareFilenameWithoutKnownPrefix()
    {
        // A short ASCII run that ends in .dds but doesn't contain a textures\ / meshes\
        // / data\... prefix is treated as noise. Without this guard, garbage bytes that
        // happen to be printable would be normalized into textures\<garbage>.dds and
        // balloon the request set.
        var bytes = Encoding.ASCII.GetBytes("\0\0\0foo.dds\0\0\0");

        var found = NifEmbeddedAssetCollector.ScanBytes(bytes);

        Assert.Empty(found);
    }

    [Fact]
    public void ScanBytes_StopsRunAtNonPrintableByte()
    {
        // A real path interrupted mid-stream by a non-printable byte must not be matched —
        // it indicates the bytes aren't actually contiguous string data.
        var bytes = new byte[]
        {
            (byte)'t', (byte)'e', (byte)'x', (byte)'t', (byte)'u', (byte)'r', (byte)'e', (byte)'s',
            (byte)'\\', (byte)'a', (byte)'r', (byte)'m', (byte)'o', (byte)'r', (byte)'\\',
            0x07, // bell — terminates the run
            (byte)'.', (byte)'d', (byte)'d', (byte)'s'
        };

        var found = NifEmbeddedAssetCollector.ScanBytes(bytes);

        Assert.Empty(found);
    }

    [Fact]
    public void ScanBytes_DeduplicatesRepeatedReferences()
    {
        // BSShaderTextureSet blocks frequently repeat the same diffuse/normal pair across
        // multiple submeshes (e.g. ulysses.nif references MeatCapGore01.dds in 4 separate
        // shader sets for the 4 gore caps). The collector should return each unique path
        // exactly once.
        var path = "textures\\gore\\MeatCapGore01.dds";
        var bytes = BuildPackedNifBlock(path, path, path, path);

        var found = NifEmbeddedAssetCollector.ScanBytes(bytes);

        Assert.Single(found);
        Assert.Contains("textures\\gore\\meatcapgore01.dds", found);
    }

    [Fact]
    public void ScanBytes_HandlesEmptyInput()
    {
        var found = NifEmbeddedAssetCollector.ScanBytes(ReadOnlySpan<byte>.Empty);

        Assert.Empty(found);
    }

    [Fact]
    public void ScanBytes_SplitsFusedPathsWhenLengthPrefixByteIsPrintable()
    {
        // Real-NIF hazard: a 4-byte LE SizedString length whose low byte happens to be
        // an ASCII letter (lengths in [97..122] or [65..90]) attaches that byte to the
        // previous run, so a naive scanner would fuse two adjacent paths through the
        // length-prefix byte and miss them both. The fix is to split each run on every
        // .dds/.ddx/.nif/etc. boundary inside it. Concretely: a 100-byte path whose
        // length=100=0x64 produces a LE prefix `64 00 00 00` where the leading `d` byte
        // is printable but the next three zeros aren't — so the previous run ends with
        // a trailing `d`, corrupting its extension to `.ddsd`.
        var path1 = "textures\\armor\\foo.dds";
        var lengthPrefixByteThatIsPrintable = new byte[] { (byte)'d' };
        var path2 = "textures\\armor\\bar.dds";
        using var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes(path1));
        ms.Write(lengthPrefixByteThatIsPrintable);
        ms.Write(Encoding.ASCII.GetBytes(path2));
        var bytes = ms.ToArray();

        var found = NifEmbeddedAssetCollector.ScanBytes(bytes);

        Assert.Contains("textures\\armor\\foo.dds", found);
        Assert.Contains("textures\\armor\\bar.dds", found);
    }

    /// <summary>
    ///     Synthesize a packed NIF-style payload: 4-byte little-endian count, then each
    ///     path as a 4-byte little-endian length + ASCII payload (no null terminator).
    ///     This is the layout BSShaderTextureSet uses inside a converted PC NIF — and the
    ///     reason the legacy DMP scanner missed it.
    /// </summary>
    private static byte[] BuildPackedNifBlock(params string[] paths)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        writer.Write((uint)paths.Length);
        foreach (var path in paths)
        {
            writer.Write((uint)path.Length);
            writer.Write(Encoding.ASCII.GetBytes(path));
        }

        return ms.ToArray();
    }
}
