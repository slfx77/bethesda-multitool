namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;

/// <summary>
///     The field an NiMaterialColorController stores after its NiSingleInterpController header at 20.2.0.7 (nif.xml:
///     Target Color, MaterialColor ushort since 10.1.0.0), exactly as stored. Read by
///     <see cref="NifPropertyControllerReader.TryReadMaterialColorView" />.
/// </summary>
/// <param name="TargetColor">The stored Target Color word (nif.xml MaterialColor: 0 AMBIENT, 1 DIFFUSE, 2 SPECULAR, 3 SELF_ILLUM).</param>
internal readonly record struct NifMaterialColorControllerView(ushort TargetColor);
