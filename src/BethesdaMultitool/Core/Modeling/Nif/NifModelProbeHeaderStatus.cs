namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>How far <see cref="NifModelProbeHeader.Read" /> got through a header held in a bounded prefix.</summary>
internal enum NifModelProbeHeaderStatus
{
    /// <summary>The whole header, through the groups, lies inside the prefix and is plausible.</summary>
    Complete,

    /// <summary>The prefix ended before the header did; what was read so far is kept.</summary>
    Exhausted,

    /// <summary>A header field is implausible (a count, length or type index out of its domain).</summary>
    Malformed
}
