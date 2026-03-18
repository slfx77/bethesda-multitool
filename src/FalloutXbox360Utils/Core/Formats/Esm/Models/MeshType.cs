namespace FalloutXbox360Utils.Core.Formats.Esm.Models;

/// <summary>Source mesh type.</summary>
public enum MeshType
{
    /// <summary>NiTriShapeData — indexed triangle list.</summary>
    TriShape,

    /// <summary>NiTriStripsData — triangle strips (converted to triangle list on export).</summary>
    TriStrips
}
