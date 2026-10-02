namespace BethesdaMultitool.Core.Modeling.Xngine;

/// <summary>
///     One triangle of an XnGine plane, as three of the plane's SOURCE corner ordinals (0 to n-1, in the order the plane
///     list stores them), in the reference orientation <c>(K0, K(i), K(i+1))</c> the legacy decomposer emits before its
///     exporter swaps the second and third corner (cut-1c plan section 3.2). The document writes each triangle reversed,
///     <c>(A, C, B)</c>, to compensate for the Y negation.
/// </summary>
/// <param name="A">The first corner: the fan apex (the first kept corner of an n-gon; c0 of a 3-corner plane).</param>
/// <param name="B">The second corner in the reference orientation.</param>
/// <param name="C">The third corner in the reference orientation.</param>
internal readonly record struct XnGineCornerTriangle(int A, int B, int C);
