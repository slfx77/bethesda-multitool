namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>Which tier of a <see cref="NifModelTargetNames" /> map a name was found in.</summary>
internal enum NifModelTargetSource
{
    /// <summary>The name was not found in either tier (or no name was given).</summary>
    None,

    /// <summary>The NiControllerManager's NiDefaultAVObjectPalette (a <c>.nif</c>'s first tier).</summary>
    Palette,

    /// <summary>A placed node's own NiObjectNET Name, the header string-table entry's exact bytes.</summary>
    ObjectName
}
