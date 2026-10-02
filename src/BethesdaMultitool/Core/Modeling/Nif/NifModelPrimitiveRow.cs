using System.Text.Json.Nodes;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     The native facts of one emitted primitive, written as a <see cref="NifModelGeometryReader.PrimitiveKind" /> row
///     that targets primitive 0 of <paramref name="MeshIndex" /> and is located at the data block.
/// </summary>
/// <param name="MeshIndex">The document mesh (each carries exactly one primitive).</param>
/// <param name="DataBlockIndex">The NiTriShapeData or NiTriStripsData block the primitive came from.</param>
/// <param name="Payload">The facts; owned by this row.</param>
internal sealed record NifModelPrimitiveRow(int MeshIndex, int DataBlockIndex, JsonObject Payload);
