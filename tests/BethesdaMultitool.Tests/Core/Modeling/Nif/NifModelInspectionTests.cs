using BethesdaMultitool.Core.Modeling.Nif;
using BethesdaMultitool.Tests.Helpers;
using DDXConv;
using Slfx77.Multitool.Core.Models;
using Slfx77.Multitool.Core.Models.Sources;
using Slfx77.Multitool.Media.Images;
using Xunit;
using static BethesdaMultitool.Tests.Core.Modeling.Nif.NifModelTextureSourceTests;

namespace BethesdaMultitool.Tests.Core.Modeling.Nif;

/// <summary>
///     Inspection never reaches a pixel decoder (plan section 4, "Inspection must not"). All of the reader's image work
///     goes through <see cref="INifTextureCodec" />, whose only operations are header inspection (with the BC1 selector
///     scan) and the DDX block relayout the owner allowed on 2026-09-24; a counting spy shows exactly those ran, once per
///     texture, and the standard payload is the relayout's bytes unchanged (nothing was decoded and re-encoded). Control:
///     a spy whose relayout throws changes the document, so the relayout demonstrably goes through the seam.
/// </summary>
public class NifModelInspectionTests
{
    [Fact]
    public void Inspection_RunsOnlyHeaderInspectionAndTheRelayout()
    {
        var nif = PerPixel(0, @"textures\t\base.dds", "", @"textures\t\glow.dds");
        var dds = SyntheticDds.Dxt1Single(SyntheticDds.TransparentDxt1Block);
        var ddx = SyntheticDdxFiles.Build("3XDR", 64, 64, SyntheticDdxFiles.Dxt1,
            SyntheticDdxFiles.IndexStampedBlocks(256, 8));
        var inspectionSpy = new CountingCodec();
        var conversionSpy = new CountingCodec();

        var inspection = ReadTextures(nif, ModelReadPurpose.Inspection, inspectionSpy,
            (@"textures\t\base.dds", dds), (@"textures\t\glow.ddx", ddx)).Document;
        var conversion = ReadTextures(nif, ModelReadPurpose.Conversion, conversionSpy,
            (@"textures\t\base.dds", dds), (@"textures\t\glow.ddx", ddx)).Document;

        Assert.Equal(2, inspectionSpy.Inspections);
        Assert.Equal(1, inspectionSpy.Relayouts);
        Assert.Equal(conversionSpy.Inspections, inspectionSpy.Inspections);
        Assert.Equal(conversionSpy.Relayouts, inspectionSpy.Relayouts);
        var glow = inspection.Images[1].Source!;
        Assert.Equal(inspectionSpy.LastRelayout, glow.StandardPayload!.Payload.CopyContent());
        Assert.Equal(dds, inspection.Images[0].Source!.Original!.CopyContent());
        Assert.Equal(SceneTextureChannels.Rgba, inspection.Images[0].Source!.Descriptor.Channels);
        Assert.Equal(conversion.Images.Select(i => i.Source!.StandardPayload?.Payload.Sha256),
            inspection.Images.Select(i => i.Source!.StandardPayload?.Payload.Sha256));
        SceneValidation.ValidateStructure(inspection);
    }

    /// <summary>Control: a relayout that throws through the seam leaves the DDX without any produced payload.</summary>
    [Fact]
    public void Control_ARelayoutThatThrowsThroughTheSeam_ChangesTheDocument()
    {
        var nif = PerPixel(0, @"textures\t\glow.dds");
        var ddx = SyntheticDdxFiles.Build("3XDR", 64, 64, SyntheticDdxFiles.Dxt1,
            SyntheticDdxFiles.IndexStampedBlocks(256, 8));
        var spy = new CountingCodec(true);

        var document = ReadTextures(nif, ModelReadPurpose.Inspection, spy, (@"textures\t\glow.ddx", ddx)).Document;
        var source = Assert.Single(document.Images).Source!;

        Assert.Equal(1, spy.Relayouts);
        Assert.Null(source.StandardPayload);
        Assert.Null(source.Derivation);
        Assert.Contains(document.Diagnostics, d => d.Code == NifModelTextureSource.DdxRelayoutDiagnostic);
    }

    /// <summary>Counts the seam's calls and forwards them to the production codec (or fails the relayout).</summary>
    private sealed class CountingCodec(bool failRelayout = false) : INifTextureCodec
    {
        public int Inspections { get; private set; }

        public int Relayouts { get; private set; }

        public byte[]? LastRelayout { get; private set; }

        public DdsImageInfo InspectDds(ReadOnlySpan<byte> content, CancellationToken cancellationToken)
        {
            Inspections++;
            return NifTextureCodec.Instance.InspectDds(content, cancellationToken);
        }

        public byte[] RelayoutDdx(byte[] ddx, DecodeDiagnostics diagnostics)
        {
            Relayouts++;
            if (failRelayout)
            {
                throw new InvalidOperationException("The seam control refuses the relayout.");
            }

            LastRelayout = NifTextureCodec.Instance.RelayoutDdx(ddx, diagnostics);
            return LastRelayout;
        }
    }
}
