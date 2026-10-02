using DDXConv;
using Slfx77.Multitool.Media.Images;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     The texture source's complete image-processing surface (plan section 4, "Inspection must not"): header inspection
///     with the BC1 selector scan, and the DDX block relayout. Neither decodes a pixel. Inspection may run both (the owner
///     ruled on 2026-09-24 that the block-only relayout is not pixel decoding); no pixel decoder is reachable through this
///     seam, and tests count its calls to show what an inspection read performs.
/// </summary>
internal interface INifTextureCodec
{
    /// <summary>Inspects a DDS header and level-zero extent without decoding pixels.</summary>
    /// <exception cref="InvalidDataException">The header is malformed.</exception>
    /// <exception cref="NotSupportedException">The surface or layout is outside Shared's admitted set.</exception>
    DdsImageInfo InspectDds(ReadOnlySpan<byte> content, CancellationToken cancellationToken);

    /// <summary>Relayouts a DDX into a DDS (LZX decompression, untiling and endian swap; no pixel change).</summary>
    /// <param name="ddx">The DDX file.</param>
    /// <param name="diagnostics">A fresh per-conversion counter sink.</param>
    /// <returns>The DDS bytes.</returns>
    byte[] RelayoutDdx(byte[] ddx, DecodeDiagnostics diagnostics);
}
