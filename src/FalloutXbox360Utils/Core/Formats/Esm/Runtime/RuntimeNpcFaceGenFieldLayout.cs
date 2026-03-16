namespace FalloutXbox360Utils.Core.Formats.Esm;

internal readonly record struct RuntimeNpcFaceGenFieldLayout(
    int PointerOffset,
    int CountOffset,
    int? EndPointerOffset = null);