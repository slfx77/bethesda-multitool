using System.Numerics;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Export;

/// <summary>One triangle corner's decoded accessor values in its primitive's local space.</summary>
/// <param name="Position">The decoded POSITION value.</param>
/// <param name="Normal">The decoded NORMAL value.</param>
/// <param name="TexCoord">The decoded TEXCOORD_0 value, or the origin when the primitive has none.</param>
/// <param name="ColorMinimum">The lowest input color the encoder could have written as this COLOR_0 value.</param>
/// <param name="ColorMaximum">The highest input color the encoder could have written as this COLOR_0 value.</param>
/// <param name="Tangent">The decoded TANGENT value, or null when the primitive has none.</param>
internal readonly record struct NifGlbDecodedCorner(
    Vector3 Position,
    Vector3 Normal,
    Vector2 TexCoord,
    Vector4 ColorMinimum,
    Vector4 ColorMaximum,
    Vector4? Tangent);
