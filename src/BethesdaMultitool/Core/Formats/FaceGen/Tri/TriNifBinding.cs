using System.Numerics;
using System.Security.Cryptography;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering;

namespace BethesdaMultitool.Core.Formats.FaceGen.Tri;

/// <summary>Binds an explicitly selected legacy NIF geometry-data block to a source TRI document.</summary>
public static class TriNifBinding
{
    /// <summary>Maximum supported NIF byte size for this bounded head-geometry inspection adapter.</summary>
    public const int MaximumNifBytes = 64 * 1024 * 1024;

    /// <summary>Parses source bytes, hashes them and validates exact indexed topology in the geometry block's local frame.</summary>
    /// <remarks>The explicit V+K reference domain must already carry any intended actor shape statistics; the adapter does not invent them.</remarks>
    public static TriGeometryBinding Bind(byte[] nifBytes, string sourceName, int geometryBlockIndex,
        TriDocument document, ReadOnlySpan<Vector3> statisticalReferenceDomain, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nifBytes);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        cancellationToken.ThrowIfCancellationRequested();
        if (nifBytes.Length > MaximumNifBytes)
        {
            throw new NotSupportedException($"TRI NIF binding exceeds the {MaximumNifBytes}-byte source limit.");
        }
        var nif = NifParser.Parse(nifBytes) ?? throw new InvalidDataException("TRI binding source is not a supported NIF.");
        if ((uint)geometryBlockIndex >= (uint)nif.Blocks.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(geometryBlockIndex));
        }
        var block = nif.Blocks[geometryBlockIndex];
        if (nif.IsBigEndian || block.TypeName is not ("NiTriShapeData" or "NiTriStripsData"))
        {
            throw new NotSupportedException("TRI binding currently requires an explicit little-endian NiTriShapeData or NiTriStripsData block.");
        }
        var mesh = block.TypeName == "NiTriShapeData"
            ? NifBlockParsers.ExtractTriShapeData(nifBytes, block, false, nif.BsVersion, nif.BinaryVersion, Matrix4x4.Identity)
            : NifBlockParsers.ExtractTriStripsData(nifBytes, block, false, nif.BsVersion, nif.BinaryVersion, Matrix4x4.Identity);
        if (mesh is null || mesh.Positions.Length != checked(document.Header.VertexCount * 3))
        {
            throw new InvalidDataException("Selected NIF geometry does not provide the exact TRI base vertex domain.");
        }
        var positions = new Vector3[document.Header.VertexCount];
        for (var vertex = 0; vertex < positions.Length; vertex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            positions[vertex] = new Vector3(mesh.Positions[vertex * 3], mesh.Positions[vertex * 3 + 1], mesh.Positions[vertex * 3 + 2]);
        }
        var triangles = new int[mesh.Triangles.Length];
        for (var index = 0; index < triangles.Length; index++)
        {
            triangles[index] = mesh.Triangles[index];
        }
        var hash = Convert.ToHexString(SHA256.HashData(nifBytes));
        return TriGeometryBinding.Create(document, sourceName, hash, geometryBlockIndex, positions, triangles,
            statisticalReferenceDomain, cancellationToken);
    }
}
