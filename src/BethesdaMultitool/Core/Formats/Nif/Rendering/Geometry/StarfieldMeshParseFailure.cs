namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Geometry;

/// <summary>
///     Where and why <see cref="StarfieldMeshFile.Decode" /> stopped:
///     the field it was reading, the byte offset of that field, and the rule the bytes broke. The decoder itself still
///     returns null (its contract never throws); a caller that must report corrupt input names the field and offset.
/// </summary>
/// <param name="Field">The field or section being read, spelled as <see cref="StarfieldMeshSection.Name" /> spells it.</param>
/// <param name="Offset">The byte offset at which the field starts.</param>
/// <param name="Reason">The broken rule, for example "runs past the end of the stream".</param>
internal sealed record StarfieldMeshParseFailure(string Field, int Offset, string Reason)
{
    /// <inheritdoc />
    public override string ToString()
    {
        return $"{Field} at 0x{Offset:X}: {Reason}";
    }
}
