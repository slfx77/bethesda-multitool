namespace BethesdaMultitool.Core.Formats.Esm.Script;

/// <summary>
///     What a piece of script source text IS, as far as the input can prove. Produced only by
///     <see cref="ScriptSourceProvenance" />, which needs to know whether the input is a memory dump:
///     <c>ScriptSourceTextOrigin.None</c> means "authored SCTX" in a plugin
///     but "text of unattributed origin" in a dump, and the record alone cannot tell the two apart.
/// </summary>
public enum ScriptTextKind
{
    /// <summary>No source text is present in this input.</summary>
    None,

    /// <summary>SCTX stored in a plugin record without a recognized reconstruction marker; authorship is unverified.</summary>
    AuthoredPlugin,

    /// <summary>SCTX recovered from a memory-dump fragment of the same record.</summary>
    RecoveredDmpFragment,

    /// <summary>SCTX recovered from the same runtime Script object in a memory dump.</summary>
    RecoveredRuntimeObject,

    /// <summary>Source text found in a memory dump whose origin was not attributed.</summary>
    UnattributedDumpText,

    /// <summary>
    ///     Text BethesdaMultitool decompiled from the record's SCDA bytecode. A reconstruction, never
    ///     original source, even when a converter emitted it as SCTX.
    /// </summary>
    ReconstructedDecompiled,

    /// <summary>SCTX was captured but withheld because source or executable validation failed.</summary>
    WithheldCaptured
}
