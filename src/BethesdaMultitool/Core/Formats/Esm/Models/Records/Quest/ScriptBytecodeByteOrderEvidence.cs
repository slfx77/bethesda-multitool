namespace BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;

/// <summary>
///     Why a script's compiled bytecode (SCDA) is read in the byte order recorded beside it
///     (<see cref="ScriptRecord.IsBigEndianBytecode" />). The bytecode order is a property of the
///     payload, not of the record container: an Xbox 360 ESM is a big-endian container whose
///     serialized SCDA is little-endian, because the engine swaps SCDA when it loads it. A runtime
///     Script object read from an Xbox 360 memory dump has already been swapped and is big-endian.
/// </summary>
public enum ScriptBytecodeByteOrderEvidence
{
    /// <summary>No SCDA, or fewer than 4 bytes: nothing to decide, so the caller's default stands.</summary>
    NotApplicable,

    /// <summary>
    ///     The payload opens with the ScriptName statement, <c>1D 00 00 00</c> (little-endian) or
    ///     <c>00 1D 00 00</c> (big-endian). The two are mutually exclusive, and the other order would
    ///     read opcode 0x1D00, which no command table defines.
    /// </summary>
    ScriptNameAnchor,

    /// <summary>Exactly one byte order walks the whole payload without a decoder diagnostic.</summary>
    SingleCleanWalk,

    /// <summary>
    ///     Both orders walk cleanly; the chosen one names strictly more of its statement opcodes
    ///     (a 4-byte inline call such as <c>23 12 00 00</c> is ForceTerminalBack little-endian and an
    ///     unknown 0x2312 big-endian, and neither walk reports a diagnostic).
    /// </summary>
    FewerUnknownOpcodes,

    /// <summary>Neither order is clean, but only the chosen one reaches the end of the payload.</summary>
    WalkedToEndInOneOrder,

    /// <summary>
    ///     The walks could not decide, and the payload is serialized (on-disk) data, which is
    ///     little-endian on every measured platform, so little-endian was used.
    /// </summary>
    AmbiguousSerializedDefault,

    /// <summary>
    ///     The walks could not decide, and the payload came from a memory dump, so the container's
    ///     byte order was kept. Dump fragments were not measured; this preserves the prior behaviour.
    /// </summary>
    AmbiguousContainerFallback,

    /// <summary>
    ///     The bytecode was adopted from a runtime Script object, which the Xbox 360 engine has
    ///     already swapped to big-endian.
    /// </summary>
    RuntimeScriptObject
}
