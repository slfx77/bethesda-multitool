namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     The console a big-endian NIF was shipped for. The two FNV consoles share every packed layout, every stream
///     encoding and the same header key; the only measured difference is the memory order of a packed D3DCOLOR vertex
///     color (X360: A, R, G, B; PS3: A, G, B, R), and no byte of the file states which console wrote it, so the platform
///     is an app option (<see cref="BethesdaModelRegistration.PlatformOption" />), never a detection. The platform also
///     selects the skin lanes: the X360 engine's derived fourth weight (<see cref="NifPackedEngineLanes" />, from the
///     X360 shaders) or, on the PS3, whose skinning shaders have not been examined, the stored halves.
/// </summary>
internal enum NifPackedPlatform
{
    /// <summary>Xbox 360: a packed vertex color is stored A, R, G, B (a big-endian D3DCOLOR).</summary>
    X360,

    /// <summary>PlayStation 3: a packed vertex color is stored A, G, B, R.</summary>
    Ps3
}
