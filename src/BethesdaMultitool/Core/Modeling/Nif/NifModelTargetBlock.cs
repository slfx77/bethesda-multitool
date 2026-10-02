namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     Why a controlled block's target name does not bind to a document node (plan section 1.7, owner rulings D3 and D12).
///     Every reason is fail closed: the track stays native state with the reason
///     <see cref="NifModelTargetNames.Reason" /> gives, and no other node is substituted.
/// </summary>
internal enum NifModelTargetBlock
{
    /// <summary>Not blocked: the name bound to exactly one block that is placed at least once.</summary>
    None = 0,

    /// <summary>The controlled block's Node Name is the NULL string (index -1).</summary>
    NoTargetName,

    /// <summary>The controlled block's Node Name index lies outside the header string table.</summary>
    UnresolvedTargetName,

    /// <summary>
    ///     'ambiguous target' (D12): the tier that holds the name gives it to more than one block (a skeleton that repeats
    ///     a node name, or a palette that names two objects alike).
    /// </summary>
    AmbiguousTarget,

    /// <summary>
    ///     'target not in the resolved skeleton (attachment node)' (D3): the <c>.kf</c>'s skeleton has no node with exactly
    ///     these bytes.
    /// </summary>
    TargetNotInSkeleton,

    /// <summary>The <c>.nif</c> has neither a palette entry nor a placed node with exactly these bytes.</summary>
    TargetNotInFile,

    /// <summary>
    ///     The name binds to a block that produced no document node (a palette entry naming no object, or a block the
    ///     scene graph never reaches), so there is no occurrence to drive.
    /// </summary>
    TargetNotPlaced,

    /// <summary>
    ///     The NiControllerManager names an object palette that did not decode completely, so the first tier cannot be
    ///     consulted and falling through to node names could bind a different block than the engine would.
    /// </summary>
    PaletteUnreadable
}
