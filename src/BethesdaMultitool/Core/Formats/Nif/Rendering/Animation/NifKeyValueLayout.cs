namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;

/// <summary>
///     The value type <c>T</c> of a NIF key group (nif.xml <c>Key&lt;T&gt;</c> and <c>QuatKey</c>). It fixes the width of
///     every stored value, and with the key type it fixes each key's stride (<see cref="NifKeyGroupReader.GetStride" />).
/// </summary>
internal enum NifKeyValueLayout : byte
{
    /// <summary>One byte (NiBoolData, NiVisData). It has no float bits; its Quadratic tangents are bytes too.</summary>
    Byte,

    /// <summary>One float (NiFloatData, a transform's scale, each XYZ-Euler axis).</summary>
    Float,

    /// <summary>A Vector3 stored x, y, z (NiPosData, a transform's translation).</summary>
    Vector3,

    /// <summary>A Color4 stored r, g, b, a (NiColorData).</summary>
    Color4,

    /// <summary>A quaternion stored w, x, y, z (QuatKey, a transform's rotation). A QuatKey never carries tangents.</summary>
    Quaternion
}
