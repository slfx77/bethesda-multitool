namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>What a <see cref="NifModelTargetNames" /> map binds names within (plan section 1.7).</summary>
internal enum NifModelTargetScope
{
    /// <summary>
    ///     A <c>.kf</c>'s resolved skeleton: its node names only. A name it lacks is
    ///     <see cref="NifModelTargetBlock.TargetNotInSkeleton" /> (the '##' attachment nodes of the equipped weapon model).
    /// </summary>
    Skeleton,

    /// <summary>
    ///     A <c>.nif</c>'s own scene: its NiControllerManager's NiDefaultAVObjectPalette first, then its node names. A name
    ///     it lacks is <see cref="NifModelTargetBlock.TargetNotInFile" />.
    /// </summary>
    File
}
