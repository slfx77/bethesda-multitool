using System.Runtime.InteropServices;
using Vortice.Direct3D12;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12;

internal enum OpaqueIndirectFallbackReason
{
    None,
    Disabled,
    NoOrdinaryDraws,
    NoSharedInstanceBlock,
    GeometryValidationEnabled,
    InsufficientRingHeadroom,
    ArgumentAllocationFailed,
    SignatureUnavailable,
}

/// <summary>
///     Pure eligibility and capacity policy for the first reference ExecuteIndirect lane. Keeping
///     the policy independent of a D3D12 device makes the exact direct-fallback boundary testable.
/// </summary>
internal static class OpaqueIndirectSubmissionPolicy
{
    internal static OpaqueIndirectFallbackReason ResolvePreallocationFallback(
        bool requested,
        bool signatureAvailable,
        int ordinaryDrawCapacity,
        bool haveSharedInstanceBlock,
        bool geometryValidationEnabled,
        ulong remainingRingBytes,
        ulong requiredRingBytes)
    {
        if (!requested)
        {
            return OpaqueIndirectFallbackReason.Disabled;
        }

        if (!signatureAvailable)
        {
            return OpaqueIndirectFallbackReason.SignatureUnavailable;
        }

        if (ordinaryDrawCapacity <= 0)
        {
            return OpaqueIndirectFallbackReason.NoOrdinaryDraws;
        }

        if (!haveSharedInstanceBlock)
        {
            return OpaqueIndirectFallbackReason.NoSharedInstanceBlock;
        }

        if (geometryValidationEnabled)
        {
            return OpaqueIndirectFallbackReason.GeometryValidationEnabled;
        }

        return remainingRingBytes < requiredRingBytes
            ? OpaqueIndirectFallbackReason.InsufficientRingHeadroom
            : OpaqueIndirectFallbackReason.None;
    }

    internal static bool IsOrdinaryLane(
        bool depthWritingBlend,
        bool usesGrassDistanceEnvelope,
        bool isDecal) =>
        !depthWritingBlend && !usesGrassDistanceEnvelope && !isDecal;

    internal static bool BeginsNewRun<T>(int pendingCount, T? pendingPso, T nextPso)
        where T : class =>
        pendingCount > 0 && !ReferenceEquals(pendingPso, nextPso);
}

/// <summary>
///     CPU-authored record consumed by the reference command signature. D3D12 consumes the first
///     60 bytes tightly in signature order; the explicit 64-byte stride leaves four bytes of legal
///     inter-command padding and prevents runtime/architecture packing from changing the ABI.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = ByteStride)]
internal struct OpaqueIndirectCommand12
{
    internal const int ByteStride = 64;

    [FieldOffset(0)]
    internal ulong PerDrawCbAddress;

    [FieldOffset(8)]
    internal VertexBufferView VertexBufferView;

    [FieldOffset(24)]
    internal IndexBufferView IndexBufferView;

    [FieldOffset(40)]
    internal OpaqueIndirectDrawIndexedArguments Draw;
}

[StructLayout(LayoutKind.Sequential)]
internal struct OpaqueIndirectDrawIndexedArguments
{
    internal uint IndexCountPerInstance;
    internal uint InstanceCount;
    internal uint StartIndexLocation;
    internal int BaseVertexLocation;
    internal uint StartInstanceLocation;
}
