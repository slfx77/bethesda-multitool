namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Export;

/// <summary>Where the normalized GLB route stopped, in the order <see cref="NifGlbExport" /> checks them.</summary>
internal enum NifGlbDeclineStage
{
    /// <summary>Nothing declined: the input is eligible for the normalized route.</summary>
    None = 0,

    /// <summary>The caller asked for the native writer, so no eligibility check ran. A decision stage only.</summary>
    Requested = 1,

    /// <summary>The NIF family (and big-endian conversion state) is not on the admission table.</summary>
    FamilyNotAdmitted = 2,

    /// <summary>The scene is outside the normalized scope, for example a skinned part.</summary>
    Scope = 3,

    /// <summary><see cref="NifNeutralSceneAdapter" /> declined with a capability reason.</summary>
    Adapter = 4,

    /// <summary>The shared glTF builder declined with <see cref="NotSupportedException" />.</summary>
    SharedBuild = 5,

    /// <summary>
    ///     The shared scene validation or build rejected the document with <see cref="InvalidDataException" />. The
    ///     corpus gate counts this stage as a failure, not a decline.
    /// </summary>
    SharedValidation = 6
}
