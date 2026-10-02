namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

/// <summary>Reports physical block admission pressure separately from malformed geometry or a native failure.</summary>
#pragma warning disable RCS1194 // Internal admission failures require byte counts; general exception constructors would lose recovery data.
internal sealed class GpuGeometryBudgetExceededException : InvalidOperationException
{
    /// <summary>Records the measured block charge and the arena's fixed physical ceiling.</summary>
    /// <param name="requestedBytes">Device-reported allocation size for the complete block.</param>
    /// <param name="maximumBytes">Configured physical budget, including blocks awaiting actual release.</param>
    internal GpuGeometryBudgetExceededException(long requestedBytes, long maximumBytes)
        : base($"Geometry block allocation of {requestedBytes} bytes exceeds available backing within the {maximumBytes}-byte arena budget.")
    {
        RequestedBytes = requestedBytes;
        MaximumBytes = maximumBytes;
    }

    /// <summary>Gets the new block's device-reported size, including allocation alignment.</summary>
    internal long RequestedBytes { get; }

    /// <summary>Gets the arena's complete physical ceiling.</summary>
    internal long MaximumBytes { get; }
}
#pragma warning restore RCS1194
