using BethesdaMultitool.Core.Formats.DiscImage.Chd;

namespace BethesdaMultitool.Tests.Helpers;

/// <summary>
///     Opens a disc image's LOGICAL bytes whatever container holds them: a plain
///     <c>FileStream</c> for a raw <c>.iso</c>, a decoding <see cref="ChdStream" /> for the
///     <c>.chd</c> the corpus stores it as.
///     <para>
///         ⚠ A test that asserts something about the DISC — a volume descriptor at sector 16, a
///         directory table that tiles — must read the disc, not the file. Handing a <c>.chd</c> path
///         to <c>new FileStream</c> reads the container's own header and map instead, and every such
///         assertion then fails for a reason that has nothing to do with what it is testing.
///     </para>
/// </summary>
internal static class DiscImageStreams
{
    public static Stream Open(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return ChdFile.IsChd(path)
            ? new ChdStream(ChdFile.Open(path), ownsFile: true)
            : new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    }
}
