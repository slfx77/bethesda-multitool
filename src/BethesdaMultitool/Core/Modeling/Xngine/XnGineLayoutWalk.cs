namespace BethesdaMultitool.Core.Modeling.Xngine;

/// <summary>
///     The outcome of walking an XnGine <c>.3D</c> record's plane list under one plane-header size (8 bytes, the
///     Daggerfall and Redguard layout; 10 bytes, the Battlespire layout). The walk is the census's acceptance rule
///     (cut-1c plan section 6.1): the point and normal lists fit, the plane list offset lies inside the record, every
///     plane header and corner fits, and every corner addresses one of the header's points.
/// </summary>
internal enum XnGineLayoutWalk
{
    /// <summary>The whole plane list walks under this header size.</summary>
    Walks,

    /// <summary>A list does not fit, a corner addresses no point, or the walk runs past the end of a complete record.</summary>
    Fails,

    /// <summary>The walk ran past the end of an INCOMPLETE prefix (a probe candidate) before it could fail or finish.</summary>
    Incomplete
}
