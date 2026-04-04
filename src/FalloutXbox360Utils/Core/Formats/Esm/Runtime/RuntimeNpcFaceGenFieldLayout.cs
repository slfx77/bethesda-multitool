namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime;

internal readonly record struct RuntimeNpcFaceGenFieldLayout(
    int PointerOffset,
    int CountOffset,
    int? EndPointerOffset = null);
