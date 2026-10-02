namespace BethesdaMultitool.Core.Formats.Nif.Decoding;

/// <summary>
///     A bulk array of elements made of float components (<c>float</c>, <c>Vector3</c>, <c>Vector4</c>,
///     <c>Color3</c>, <c>Color4</c>, <c>TexCoord</c>), stored as the components' IEEE bits in element-major order so
///     every value is exact.
/// </summary>
internal sealed class NifFloatArrayValue : NifValue
{
    private readonly uint[] _bits;

    /// <summary>Creates a bulk float array.</summary>
    /// <param name="typeName">The declared element type.</param>
    /// <param name="componentsPerElement">Floats per element (1 for float, 3 for Vector3...).</param>
    /// <param name="bits">The component bits, element-major, already in host order.</param>
    public NifFloatArrayValue(string typeName, int componentsPerElement, uint[] bits)
        : base(typeName)
    {
        if (componentsPerElement <= 0 || bits.Length % componentsPerElement != 0)
        {
            throw new ArgumentException(
                $"{bits.Length} components do not divide into elements of {componentsPerElement}.", nameof(bits));
        }

        ComponentsPerElement = componentsPerElement;
        _bits = bits;
    }

    /// <inheritdoc />
    public override NifValueKind Kind => NifValueKind.FloatArray;

    /// <summary>Floats per element.</summary>
    public int ComponentsPerElement { get; }

    /// <summary>The number of elements.</summary>
    public int Count => _bits.Length / ComponentsPerElement;

    /// <summary>All component bits, element-major.</summary>
    public ReadOnlySpan<uint> Bits => _bits;

    /// <summary>The bits of one component.</summary>
    public uint GetBits(int element, int component = 0)
    {
        ValidateComponent(component);
        return _bits[element * ComponentsPerElement + component];
    }

    /// <summary>The value of one component.</summary>
    public float Get(int element, int component = 0)
    {
        return BitConverter.UInt32BitsToSingle(GetBits(element, component));
    }

    /// <summary>A copy of all components as floats, element-major.</summary>
    public float[] ToSingleArray()
    {
        var result = new float[_bits.Length];
        for (var i = 0; i < _bits.Length; i++)
        {
            result[i] = BitConverter.UInt32BitsToSingle(_bits[i]);
        }

        return result;
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return $"{TypeName}[{Count}]";
    }

    private void ValidateComponent(int component)
    {
        if ((uint)component >= (uint)ComponentsPerElement)
        {
            throw new ArgumentOutOfRangeException(nameof(component), component,
                $"{TypeName} has {ComponentsPerElement} components.");
        }
    }
}
