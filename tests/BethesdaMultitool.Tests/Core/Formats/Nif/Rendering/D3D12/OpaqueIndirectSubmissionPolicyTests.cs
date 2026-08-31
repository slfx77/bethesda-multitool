using System.Runtime.InteropServices;
using BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.D3D12;

public sealed class OpaqueIndirectSubmissionPolicyTests
{
    [Fact]
    public void PreconditionsFailClosedAtEachCorrectnessBoundary()
    {
        Assert.Equal(
            OpaqueIndirectFallbackReason.Disabled,
            Resolve(requested: false));
        Assert.Equal(
            OpaqueIndirectFallbackReason.SignatureUnavailable,
            Resolve(signatureAvailable: false));
        Assert.Equal(
            OpaqueIndirectFallbackReason.NoOrdinaryDraws,
            Resolve(ordinaryDrawCapacity: 0));
        Assert.Equal(
            OpaqueIndirectFallbackReason.NoSharedInstanceBlock,
            Resolve(haveSharedInstanceBlock: false));
        Assert.Equal(
            OpaqueIndirectFallbackReason.GeometryValidationEnabled,
            Resolve(geometryValidationEnabled: true));
        Assert.Equal(
            OpaqueIndirectFallbackReason.InsufficientRingHeadroom,
            Resolve(remainingRingBytes: 511, requiredRingBytes: 512));
        Assert.Equal(OpaqueIndirectFallbackReason.None, Resolve());
    }

    [Theory]
    [InlineData(false, false, false, true)]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(true, true, true, false)]
    public void OrdinaryLaneExcludesEveryOrderSensitiveLane(
        bool depthWritingBlend,
        bool grass,
        bool decal,
        bool expected)
    {
        Assert.Equal(
            expected,
            OpaqueIndirectSubmissionPolicy.IsOrdinaryLane(depthWritingBlend, grass, decal));
    }

    [Fact]
    public void RunBoundaryUsesPsoReferenceIdentity()
    {
        var psoA = new object();
        var valueEqualButDistinctPso = new string('a', 1);
        var anotherValueEqualPso = new string('a', 1);

        Assert.False(OpaqueIndirectSubmissionPolicy.BeginsNewRun(0, psoA, new object()));
        Assert.False(OpaqueIndirectSubmissionPolicy.BeginsNewRun(3, psoA, psoA));
        Assert.True(OpaqueIndirectSubmissionPolicy.BeginsNewRun(
            3,
            valueEqualButDistinctPso,
            anotherValueEqualPso));
    }

    [Fact]
    public void CommandRecordPinsTheNativeSignatureLayout()
    {
        Assert.Equal(64, Marshal.SizeOf<OpaqueIndirectCommand12>());
        Assert.Equal(0, Marshal.OffsetOf<OpaqueIndirectCommand12>(
            nameof(OpaqueIndirectCommand12.PerDrawCbAddress)).ToInt32());
        Assert.Equal(8, Marshal.OffsetOf<OpaqueIndirectCommand12>(
            nameof(OpaqueIndirectCommand12.VertexBufferView)).ToInt32());
        Assert.Equal(24, Marshal.OffsetOf<OpaqueIndirectCommand12>(
            nameof(OpaqueIndirectCommand12.IndexBufferView)).ToInt32());
        Assert.Equal(40, Marshal.OffsetOf<OpaqueIndirectCommand12>(
            nameof(OpaqueIndirectCommand12.Draw)).ToInt32());
        Assert.Equal(20, Marshal.SizeOf<OpaqueIndirectDrawIndexedArguments>());
    }

    [Fact]
    public void RendererKeepsSpecialAndPerBatchInstancePathsDirectAndFlushesBothBoundaries()
    {
        var source = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
            "ReferenceRenderer12.cs");
        var loop = SourceContract.Extract(source, "private void DrawOpaqueBatches(", "private void DrawBlended(");

        Assert.Contains("haveSharedBlock,", loop, StringComparison.Ordinal);
        Assert.Contains("GeometryArenaDiagnostics.Enabled,", loop, StringComparison.Ordinal);
        Assert.Contains("_opaqueIndirectSignature is not null,", loop, StringComparison.Ordinal);
        Assert.Contains("var submitIndirect = useOpaqueIndirect && drawCount > 0 && ordinaryLane;", loop,
            StringComparison.Ordinal);
        Assert.Contains("if (!submitIndirect && drawCount > 0", loop, StringComparison.Ordinal);
        Assert.Contains("cmd.DrawIndexedInstanced", loop, StringComparison.Ordinal);
        Assert.Contains("cmd.ExecuteIndirect(", loop, StringComparison.Ordinal);
        Assert.Contains("startInstance += (uint)(drawCount + shadowCount);", loop, StringComparison.Ordinal);
        Assert.True(Count(loop, "FlushOpaqueIndirectRun(") >= 2,
            "The pending run must flush on a lane/PSO boundary and again at loop exit.");

        SourceContract.AssertOrder(
            loop,
            "FlushOpaqueIndirectRun(",
            "if (!submitIndirect && drawCount > 0",
            "var command = new OpaqueIndirectCommand12",
            "if (_mirrorCaptureArmed",
            "startInstance += (uint)(drawCount + shadowCount);",
            "LastStats.ReferenceOpaqueUniquePsos = _opaqueSubmissionPsos.Count;");

        Assert.Contains(
            "private readonly ID3D12CommandSignature? _opaqueIndirectSignature;",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "if (OpaqueIndirectRequested || StaticOpaquePacketRequested)",
            source,
            StringComparison.Ordinal);
        Assert.Contains("catch (Exception ex) when (ex is not OutOfMemoryException)", source,
            StringComparison.Ordinal);
        Assert.Contains("_opaqueIndirectSignature?.Dispose();", source, StringComparison.Ordinal);
    }

    private static OpaqueIndirectFallbackReason Resolve(
        bool requested = true,
        bool signatureAvailable = true,
        int ordinaryDrawCapacity = 1,
        bool haveSharedInstanceBlock = true,
        bool geometryValidationEnabled = false,
        ulong remainingRingBytes = 512,
        ulong requiredRingBytes = 512) =>
        OpaqueIndirectSubmissionPolicy.ResolvePreallocationFallback(
            requested,
            signatureAvailable,
            ordinaryDrawCapacity,
            haveSharedInstanceBlock,
            geometryValidationEnabled,
            remainingRingBytes,
            requiredRingBytes);

    private static int Count(string source, string value)
    {
        var count = 0;
        var start = 0;
        while ((start = source.IndexOf(value, start, StringComparison.Ordinal)) >= 0)
        {
            count++;
            start += value.Length;
        }

        return count;
    }
}
