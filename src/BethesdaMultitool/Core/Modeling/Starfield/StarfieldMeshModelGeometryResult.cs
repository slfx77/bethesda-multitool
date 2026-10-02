using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Starfield;

/// <summary>The geometry of a Starfield <c>.mesh</c> document (<see cref="StarfieldMeshModelGeometry.Build" />).</summary>
/// <param name="Primary">The main primitive: every vertex, the main index list and the typed source streams.</param>
/// <param name="Lods">
///     Per LOD list in stream order, a primitive sharing every buffer of <paramref name="Primary" /> with that list's
///     triangles, or null for an empty list.
/// </param>
internal sealed record StarfieldMeshModelGeometryResult(ScenePrimitive Primary, IReadOnlyList<ScenePrimitive?> Lods);
