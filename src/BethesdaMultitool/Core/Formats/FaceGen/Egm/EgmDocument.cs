using System.Collections.ObjectModel;

namespace BethesdaMultitool.Core.Formats.FaceGen.Egm;

/// <summary>A complete little-endian FREGM002 document with geometry-basis identity and both source mode families.</summary>
public sealed class EgmDocument
{
    /// <summary>Publishes owned source arrays without discarding the basis key or statistical-domain suffix.</summary>
    /// <param name="vertexCount">The validated complete domain size.</param>
    /// <param name="basisKey">The opaque source geometry-basis word.</param>
    /// <param name="reserved">The parser-owned forty reserved bytes, transferred without copying.</param>
    /// <param name="symmetric">The parser-owned symmetric mode array, wrapped without copying.</param>
    /// <param name="asymmetric">The parser-owned asymmetric mode array, wrapped without copying.</param>
    /// <param name="sourceHash">Uppercase SHA-256 of the complete encoded document.</param>
    /// <param name="encodedSize">The exactly consumed source length in bytes.</param>
    internal EgmDocument(int vertexCount, uint basisKey, byte[] reserved, EgmBasisMode[] symmetric,
        EgmBasisMode[] asymmetric, string sourceHash, int encodedSize)
    {
        VertexCount = vertexCount;
        BasisKey = basisKey;
        Reserved = reserved;
        SymmetricModes = Array.AsReadOnly(symmetric);
        AsymmetricModes = Array.AsReadOnly(asymmetric);
        SourceHash = sourceHash;
        EncodedSize = encodedSize;
    }

    /// <summary>Complete source domain size; exact TRI binding requires this to equal V+K.</summary>
    public int VertexCount { get; }
    /// <summary>Original geometry basis version at byte 20, retained as an opaque identity.</summary>
    public uint BasisKey { get; }
    /// <summary>All forty original reserved bytes following the basis key.</summary>
    public ReadOnlyMemory<byte> Reserved { get; }
    /// <summary>Symmetric principal-component modes in source order.</summary>
    public ReadOnlyCollection<EgmBasisMode> SymmetricModes { get; }
    /// <summary>Asymmetric principal-component modes in source order.</summary>
    public ReadOnlyCollection<EgmBasisMode> AsymmetricModes { get; }
    /// <summary>Uppercase SHA-256 over the exact complete source file.</summary>
    public string SourceHash { get; }
    /// <summary>The exactly consumed encoded file length.</summary>
    public int EncodedSize { get; }
}
