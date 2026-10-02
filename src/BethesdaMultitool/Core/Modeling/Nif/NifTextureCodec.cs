using DDXConv;
using Slfx77.Multitool.Media.Images;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     The production <see cref="INifTextureCodec" />: Shared's <see cref="DdsImageDecoder.Inspect" /> and DDXConv's
///     in-memory <see cref="DdxParser.ConvertDdxToDds(byte[], ConversionOptions?)" /> with a caller-supplied
///     <see cref="DecodeDiagnostics" /> (<c>ConversionOptions.cs:18</c>).
/// </summary>
internal sealed class NifTextureCodec : INifTextureCodec
{
    private NifTextureCodec()
    {
    }

    /// <summary>The shared stateless instance.</summary>
    public static NifTextureCodec Instance { get; } = new();

    /// <inheritdoc />
    public DdsImageInfo InspectDds(ReadOnlySpan<byte> content, CancellationToken cancellationToken)
    {
        return DdsImageDecoder.Inspect(content, cancellationToken);
    }

    /// <inheritdoc />
    public byte[] RelayoutDdx(byte[] ddx, DecodeDiagnostics diagnostics)
    {
        ArgumentNullException.ThrowIfNull(ddx);
        ArgumentNullException.ThrowIfNull(diagnostics);
        return new DdxParser().ConvertDdxToDds(ddx, new ConversionOptions { Diagnostics = diagnostics });
    }
}
