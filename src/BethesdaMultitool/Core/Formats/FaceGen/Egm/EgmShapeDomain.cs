using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Numerics;
using System.Security.Cryptography;
using BethesdaMultitool.Core.Formats.FaceGen.Tri;

namespace BethesdaMultitool.Core.Formats.FaceGen.Egm;

/// <summary>Owns complete V+K shape displacements and shaped TRI reference coordinates with explicit source/coefficient identity.</summary>
/// <remarks>The NIF neutral V prefix still needs these base displacements before expression evaluation or skinning.</remarks>
public sealed class EgmShapeDomain
{
    /// <summary>Publishes validated arrays after all arithmetic and identity checks have succeeded.</summary>
    /// <param name="tri">The parsed source TRI providing the base count and source hash.</param>
    /// <param name="egm">The parsed source EGM providing the basis and source hash.</param>
    /// <param name="symmetric">The owned copy of symmetric coefficient bits.</param>
    /// <param name="asymmetric">The owned copy of asymmetric coefficient bits.</param>
    /// <param name="displacements">The owned complete accumulated shape offsets.</param>
    /// <param name="vertices">The owned complete shaped TRI reference coordinates.</param>
    /// <param name="coefficientHash">The canonical identity of the copied coefficients and basis.</param>
    private EgmShapeDomain(TriDocument tri, EgmDocument egm, float[] symmetric, float[] asymmetric,
        Vector3[] displacements, Vector3[] vertices, string coefficientHash)
    {
        TriSourceHash = tri.SourceHash;
        EgmSourceHash = egm.SourceHash;
        BasisKey = egm.BasisKey;
        BaseVertexCount = tri.Header.VertexCount;
        SymmetricCoefficients = symmetric;
        AsymmetricCoefficients = asymmetric;
        Displacements = displacements;
        Vertices = vertices;
        CoefficientHash = coefficientHash;
    }

    /// <summary>SHA-256 of the exact source TRI document whose base and statistical coordinates were shaped.</summary>
    public string TriSourceHash { get; }
    /// <summary>SHA-256 of the exact source EGM basis data used for every displacement.</summary>
    public string EgmSourceHash { get; }
    /// <summary>The source geometry basis identity explicitly accepted by the caller.</summary>
    public uint BasisKey { get; }
    /// <summary>The V prefix corresponding to base mesh vertices; the remaining K entries are statistical targets.</summary>
    public int BaseVertexCount { get; }
    /// <summary>Owned symmetric coefficients in source mode order, without epsilon suppression or clamping.</summary>
    public ReadOnlyMemory<float> SymmetricCoefficients { get; }
    /// <summary>Owned asymmetric coefficients in source mode order, without epsilon suppression or clamping.</summary>
    public ReadOnlyMemory<float> AsymmetricCoefficients { get; }
    /// <summary>Accumulated dense XYZ displacements for every base and statistical source vertex.</summary>
    public ReadOnlyMemory<Vector3> Displacements { get; }
    /// <summary>Original TRI V+K coordinates plus the accumulated EGM displacements, suitable as an explicit statistical reference domain.</summary>
    public ReadOnlyMemory<Vector3> Vertices { get; }
    /// <summary>SHA-256 over a version tag, basis key, family lengths and canonical little-endian float32 coefficient bits.</summary>
    public string CoefficientHash { get; }

    /// <summary>Applies the V displacement prefix to a copy of explicitly supplied unshaped NIF-local neutral positions.</summary>
    /// <remarks>The input must be the original neutral pose, not a previous result; no skinning or coordinate transform is inferred.</remarks>
    /// <param name="unshapedPositions">Exactly V finite source neutral positions in the matching vertex order and coordinate frame.</param>
    /// <param name="cancellationToken">Cancellation checked before copying and per vertex.</param>
    /// <returns>A fresh shaped neutral-position array; the input and this domain remain unchanged.</returns>
    /// <exception cref="ArgumentException">The position count does not exactly match V.</exception>
    /// <exception cref="InvalidDataException">An input position or resulting coordinate is non-finite.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is requested during an authored check.</exception>
    public Vector3[] ApplyBaseDisplacements(ReadOnlySpan<Vector3> unshapedPositions, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (unshapedPositions.Length != BaseVertexCount)
        {
            throw new ArgumentException("EGM base-position input must exactly match the TRI V prefix.", nameof(unshapedPositions));
        }
        var result = unshapedPositions.ToArray();
        var deltas = Displacements.Span;
        for (var vertex = 0; vertex < result.Length; vertex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequireFinite(result[vertex]);
            if (deltas[vertex] != Vector3.Zero)
            {
                result[vertex] += deltas[vertex];
            }
            RequireFinite(result[vertex]);
        }
        return result;
    }

    /// <summary>Computes a fresh complete domain with exact coefficient family lengths and an explicit expected basis key.</summary>
    /// <remarks>This diagnostic linear operation preserves tiny finite coefficients; it does not silently adopt the legacy renderer's epsilon policy.</remarks>
    /// <param name="tri">The selected source TRI whose complete V+K coordinates are to be shaped.</param>
    /// <param name="egm">The corresponding EGM basis in the same vertex order and coordinate frame.</param>
    /// <param name="expectedBasisKey">The caller's explicitly verified basis identity; counts alone cannot establish correspondence.</param>
    /// <param name="symmetricCoefficients">Exactly one finite coefficient per symmetric mode, copied in source order.</param>
    /// <param name="asymmetricCoefficients">Exactly one finite coefficient per asymmetric mode, copied in source order.</param>
    /// <param name="cancellationToken">Cancellation checked before allocation and throughout domain evaluation.</param>
    /// <returns>An owned result retaining copied coefficients, full displacements, shaped reference vertices and source identities.</returns>
    /// <exception cref="ArgumentNullException">Either source document is null.</exception>
    /// <exception cref="ArgumentException">A coefficient family has the wrong length or contains non-finite values.</exception>
    /// <exception cref="InvalidDataException">The basis/domain counts differ or shape arithmetic becomes non-finite.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is requested during an authored check.</exception>
    public static EgmShapeDomain Create(TriDocument tri, EgmDocument egm, uint expectedBasisKey,
        ReadOnlySpan<float> symmetricCoefficients, ReadOnlySpan<float> asymmetricCoefficients,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tri);
        ArgumentNullException.ThrowIfNull(egm);
        cancellationToken.ThrowIfCancellationRequested();
        if (egm.BasisKey != expectedBasisKey)
        {
            throw new InvalidDataException("EGM geometry basis identity does not match the caller's explicit expected basis.");
        }
        if (egm.VertexCount != tri.Header.VertexCount + tri.Header.StatisticalVertexCount)
        {
            throw new InvalidDataException("EGM vertex domain must exactly equal the selected TRI V+K domain.");
        }
        if (symmetricCoefficients.Length != egm.SymmetricModes.Count || asymmetricCoefficients.Length != egm.AsymmetricModes.Count)
        {
            throw new ArgumentException("EGM coefficients must exactly match both source mode-family lengths.");
        }
        var symmetric = symmetricCoefficients.ToArray();
        var asymmetric = asymmetricCoefficients.ToArray();
        ValidateCoefficients(symmetric);
        ValidateCoefficients(asymmetric);
        var displacements = new Vector3[egm.VertexCount];
        Accumulate(displacements, egm.SymmetricModes, symmetric, cancellationToken);
        Accumulate(displacements, egm.AsymmetricModes, asymmetric, cancellationToken);
        var vertices = new Vector3[egm.VertexCount];
        tri.Vertices.Span.CopyTo(vertices);
        tri.StatisticalVertices.Span.CopyTo(vertices.AsSpan(tri.Header.VertexCount));
        for (var vertex = 0; vertex < vertices.Length; vertex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequireFinite(displacements[vertex]);
            if (displacements[vertex] != Vector3.Zero)
            {
                vertices[vertex] += displacements[vertex];
            }
            RequireFinite(vertices[vertex]);
        }
        var coefficientHash = HashCoefficients(egm.BasisKey, symmetric, asymmetric);
        cancellationToken.ThrowIfCancellationRequested();
        return new EgmShapeDomain(tri, egm, symmetric, asymmetric, displacements, vertices, coefficientHash);
    }

    /// <summary>Accumulates one family in source order, multiplying scale and coefficient before each packed component.</summary>
    /// <param name="target">The private complete displacement array to update before publication.</param>
    /// <param name="modes">One validated source family over the complete target domain.</param>
    /// <param name="coefficients">A finite, exactly matching coefficient span in source order.</param>
    /// <param name="cancellationToken">Cancellation checked per mode and every 4096 vertices.</param>
    /// <exception cref="InvalidDataException">Multiplying a source scale by its coefficient is non-finite.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is requested during an authored check.</exception>
    private static void Accumulate(Vector3[] target, ReadOnlyCollection<EgmBasisMode> modes, ReadOnlySpan<float> coefficients,
        CancellationToken cancellationToken)
    {
        for (var modeIndex = 0; modeIndex < modes.Count; modeIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var coefficient = coefficients[modeIndex];
#pragma warning disable S1244 // Only exact zero is inactive; epsilon suppression would discard finite diagnostic coefficients.
            if (coefficient == 0)
#pragma warning restore S1244
            {
                continue;
            }
            var mode = modes[modeIndex];
            var weightedScale = mode.Scale * coefficient;
            if (!float.IsFinite(weightedScale))
            {
                throw new InvalidDataException("EGM coefficient and scale overflow finite arithmetic.");
            }
            var values = mode.PackedDeltas.Span;
            for (var vertex = 0; vertex < target.Length; vertex++)
            {
                if ((vertex & 4095) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
                var offset = vertex * 3;
                target[vertex] += new Vector3(values[offset] * weightedScale,
                    values[offset + 1] * weightedScale, values[offset + 2] * weightedScale);
            }
        }
    }

    /// <summary>Rejects non-finite coefficients before any persistent result can be published.</summary>
    /// <param name="coefficients">One copied coefficient family to inspect without modifying it.</param>
    /// <exception cref="ArgumentException">A coefficient is NaN or infinite.</exception>
    private static void ValidateCoefficients(ReadOnlySpan<float> coefficients)
    {
        foreach (var coefficient in coefficients)
        {
            if (!float.IsFinite(coefficient))
            {
                throw new ArgumentException("EGM shape coefficients must be finite.");
            }
        }
    }

    /// <summary>Rejects non-finite displacement sums or shaped coordinates.</summary>
    /// <param name="value">The computed or caller-supplied XYZ vector to validate.</param>
    /// <exception cref="InvalidDataException">Any component is NaN or infinite.</exception>
    private static void RequireFinite(Vector3 value)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z))
        {
            throw new InvalidDataException("EGM shape evaluation produced a non-finite coordinate.");
        }
    }

    /// <summary>Hashes canonical coefficient identity without allocating a buffer proportional to vertex count.</summary>
    /// <param name="basisKey">The explicitly accepted source geometry-basis identity.</param>
    /// <param name="symmetric">The copied symmetric coefficient bits in source order.</param>
    /// <param name="asymmetric">The copied asymmetric coefficient bits in source order.</param>
    /// <returns>Uppercase SHA-256 of the version tag, basis, lengths and little-endian coefficient bits.</returns>
    private static string HashCoefficients(uint basisKey, ReadOnlySpan<float> symmetric, ReadOnlySpan<float> asymmetric)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("EGMCOEF1"u8);
        Span<byte> words = stackalloc byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(words, basisKey);
        BinaryPrimitives.WriteInt32LittleEndian(words[4..], symmetric.Length);
        BinaryPrimitives.WriteInt32LittleEndian(words[8..], asymmetric.Length);
        hash.AppendData(words);
        AppendCoefficientBits(hash, symmetric);
        AppendCoefficientBits(hash, asymmetric);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    /// <summary>Appends exact little-endian IEEE coefficient bits in source family order.</summary>
    /// <param name="hash">The caller-owned active hash state; this method neither finalizes nor disposes it.</param>
    /// <param name="coefficients">The finite source family to append without modifying it.</param>
    private static void AppendCoefficientBits(IncrementalHash hash, ReadOnlySpan<float> coefficients)
    {
        Span<byte> bytes = stackalloc byte[4];
        foreach (var coefficient in coefficients)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes, coefficient);
            hash.AppendData(bytes);
        }
    }
}
