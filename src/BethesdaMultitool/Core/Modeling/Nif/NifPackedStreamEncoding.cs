namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     How the bytes of one packed channel are decoded (measured 2026-09-24, TestOutput/packed-semantics-20260924).
///     Every multi-byte value is big-endian on both consoles.
/// </summary>
internal enum NifPackedStreamEncoding
{
    /// <summary>
    ///     Four IEEE binary16 values; x, y, z are halves 0..2 (widened to float32 exactly) and half 3 is a constant 1.0
    ///     that carries nothing (it is not the bitangent sign; measured 1.0 on 100% of 473,323 X360 and 63,152 PS3
    ///     vertices per stream) and is only counted.
    /// </summary>
    HalfVector3,

    /// <summary>Two binary16 values (u, v), widened exactly.</summary>
    HalfVector2,

    /// <summary>Three big-endian float32 values, exact copies of the PC floats.</summary>
    FloatVector3,

    /// <summary>
    ///     Four bytes, each divided by 255, in the platform's memory order (<see cref="NifPackedPlatform" />): X360 A, R,
    ///     G, B (a big-endian D3DCOLOR), PS3 A, G, B, R.
    /// </summary>
    D3DColor,

    /// <summary>
    ///     Four binary16 bone weights for partition slots 0..3, stored slot for slot; a slot-3 value of exactly 1.0 whose
    ///     three siblings sum to 1 within 2e-3 is a sentinel and reads as 0. No renormalization.
    /// </summary>
    HalfWeights4,

    /// <summary>
    ///     Four bone indices as one big-endian uint32 whose low byte is slot 0: slot k is byte [3 - k]. Each indexes the
    ///     owning partition's bone list.
    /// </summary>
    UByte4Reversed
}
