using BethesdaMultitool.Core.Formats.Nif.Rendering.Water;
using Vortice.Direct3D12;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12;

internal enum OblivionWaterDisplacementReplayObservationKind
{
    UploadTransferred,
    InitialClearRecorded,
    PassRecorded
}

/// <summary>
///     Instance-local, optional replay observation after the named operation returns. Resource is
///     borrowed only during the callback; the observer may not retire it or leave its state changed.
///     A texture copy must restore RenderTarget before returning. Constants on PassRecorded are
///     exactly the values passed to SetGraphicsRoot32BitConstants before the actual draw call.
///     Recording is not submission: the consumer must join the recorder's successful fence before
///     publishing a readback. An observer exception aborts this recorder transaction normally.
/// </summary>
internal sealed record OblivionWaterDisplacementReplayObservation12(
    OblivionWaterDisplacementReplayObservationKind Kind,
    long InvocationOrder,
    int PassIndex,
    OblivionWaterSimulationResource Identity,
    ID3D12Resource Resource,
    OblivionWaterSimulationPass? Pass = null,
    OblivionWaterDisplacementConstants? Constants = null,
    ID3D12Resource? Input0Resource = null,
    ID3D12Resource? Input1Resource = null);
