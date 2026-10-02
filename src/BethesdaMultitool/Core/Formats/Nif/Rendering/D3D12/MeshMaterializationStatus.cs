namespace BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12;

/// <summary>Distinguishes usable geometry, legitimate empty input and materialization requiring a later retry.</summary>
internal enum MeshMaterializationStatus
{
    /// <summary>The complete mesh is ready for its caller's publication path.</summary>
    Success,
    /// <summary>The decoded input contains no renderable geometry.</summary>
    RenderEmpty,
    /// <summary>A fallible acquisition failed without establishing empty input.</summary>
    RetryableFailure,
    /// <summary>A complete physical arena block could not be admitted within its byte ceiling.</summary>
    PhysicalBudgetDeferred
}
