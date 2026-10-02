namespace BethesdaMultitool.Core.Formats.Nif.Decoding;

/// <summary>The component storage of a bulk-decoded array element.</summary>
internal enum NifBulkComponent
{
    /// <summary>IEEE binary32 components (float, Vector3, Color4, TexCoord...).</summary>
    Float32,

    /// <summary>ushort components (ushort, Triangle).</summary>
    UInt16,

    /// <summary>Single-byte components (byte, char).</summary>
    UInt8
}
