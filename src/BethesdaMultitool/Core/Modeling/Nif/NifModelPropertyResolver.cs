using BethesdaMultitool.Core.Formats.Nif.Decoding;
using BethesdaMultitool.Core.Formats.Nif.Schema;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     Computes the effective property set of a node occurrence, Gamebryo-style (plan section 3, "Materials and
///     properties"): the occurrence's own NiAVObject Properties first, then each ancestor occurrence's up to the root, and
///     the nearest property of each <see cref="NifPropertySlot" /> wins. Occurrences, not blocks, are walked, so one shape
///     instanced under two parents can inherit two different sets.
/// </summary>
/// <remarks>
///     Within one object's Properties list the first property of a slot is kept and any later one is reported as a
///     <see cref="NifModelDuplicateProperty" /> (Assumed). Null entries are skipped. A link that does not name an
///     NiProperty block is corrupt input.
/// </remarks>
internal sealed class NifModelPropertyResolver
{
    private readonly int[] _blockByNode;
    private readonly int[] _parentByNode;
    private readonly Dictionary<int, IReadOnlyList<int>> _propertiesByBlock = [];
    private readonly NifModelReadState _state;

    /// <summary>Indexes the walked hierarchy: each node's block and parent occurrence.</summary>
    public NifModelPropertyResolver(NifModelReadState state, NifModelNodeGraph graph)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(graph);
        _state = state;
        var count = graph.Nodes.Count;
        _blockByNode = new int[count];
        _parentByNode = new int[count];
        Array.Fill(_parentByNode, -1);
        for (var block = 0; block < graph.OccurrencesByBlock.Count; block++)
        {
            foreach (var node in graph.OccurrencesByBlock[block])
            {
                _blockByNode[node] = block;
            }
        }

        for (var node = 0; node < count; node++)
        {
            foreach (var child in graph.Nodes[node].Children)
            {
                _parentByNode[child] = node;
            }
        }
    }

    /// <summary>The block a node occurrence was built from.</summary>
    public int BlockOf(int nodeIndex)
    {
        return _blockByNode[nodeIndex];
    }

    /// <summary>The effective property set of one node occurrence.</summary>
    /// <exception cref="InvalidDataException">A Properties link names a block that is not an NiProperty.</exception>
    public NifModelPropertySet Resolve(int nodeIndex)
    {
        var effective = new Dictionary<string, NifModelEffectiveProperty>(StringComparer.Ordinal);
        var duplicates = new List<NifModelDuplicateProperty>();
        for (var node = nodeIndex; node >= 0; node = _parentByNode[node])
        {
            var owner = _blockByNode[node];
            foreach (var property in PropertiesOf(owner))
            {
                var type = _state.Blocks[property].Type;
                var slot = SlotOf(_state.Schema, type);
                var slotName = SlotName(slot, type);
                if (!effective.TryGetValue(slotName, out var kept))
                {
                    effective.Add(slotName, new NifModelEffectiveProperty(slot, slotName, property, owner));
                }
                else if (kept.OwnerBlockIndex == owner && kept.BlockIndex != property)
                {
                    duplicates.Add(new NifModelDuplicateProperty(slotName, owner, kept.BlockIndex, property));
                }
            }
        }

        return new NifModelPropertySet(effective.Values, duplicates);
    }

    /// <summary>The slot a property type fills, by nif.xml inheritance.</summary>
    public static NifPropertySlot SlotOf(NifSchema schema, string type)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(type);
        if (schema.Inherits(type, "NiAlphaProperty"))
        {
            return NifPropertySlot.Alpha;
        }

        if (schema.Inherits(type, "NiMaterialProperty"))
        {
            return NifPropertySlot.Material;
        }

        if (schema.Inherits(type, "NiShadeProperty"))
        {
            return NifPropertySlot.Shade;
        }

        if (schema.Inherits(type, "NiStencilProperty"))
        {
            return NifPropertySlot.Stencil;
        }

        if (schema.Inherits(type, "NiTexturingProperty"))
        {
            return NifPropertySlot.Texturing;
        }

        if (schema.Inherits(type, "NiVertexColorProperty"))
        {
            return NifPropertySlot.VertexColor;
        }

        if (schema.Inherits(type, "NiZBufferProperty"))
        {
            return NifPropertySlot.ZBuffer;
        }

        if (schema.Inherits(type, "NiFogProperty"))
        {
            return NifPropertySlot.Fog;
        }

        if (schema.Inherits(type, "NiSpecularProperty"))
        {
            return NifPropertySlot.Specular;
        }

        if (schema.Inherits(type, "NiWireframeProperty"))
        {
            return NifPropertySlot.Wireframe;
        }

        return schema.Inherits(type, "NiDitherProperty") ? NifPropertySlot.Dither : NifPropertySlot.Other;
    }

    /// <summary>The key name of a slot: its camel-case name, or <c>other:{Type}</c>.</summary>
    public static string SlotName(NifPropertySlot slot, string type)
    {
        return slot switch
        {
            NifPropertySlot.Alpha => "alpha",
            NifPropertySlot.Material => "material",
            NifPropertySlot.Shade => "shade",
            NifPropertySlot.Stencil => "stencil",
            NifPropertySlot.Texturing => "texturing",
            NifPropertySlot.VertexColor => "vertexColor",
            NifPropertySlot.ZBuffer => "zBuffer",
            NifPropertySlot.Fog => "fog",
            NifPropertySlot.Specular => "specular",
            NifPropertySlot.Wireframe => "wireframe",
            NifPropertySlot.Dither => "dither",
            _ => "other:" + type
        };
    }

    /// <summary>The non-null Properties links of one NiAVObject block, in stored order.</summary>
    private IReadOnlyList<int> PropertiesOf(int block)
    {
        if (_propertiesByBlock.TryGetValue(block, out var cached))
        {
            return cached;
        }

        var decoded = _state.Blocks[block];
        var result = new List<int>();
        if (decoded.Root.TryGet("Properties", out var value))
        {
            if (value is not NifArrayValue array)
            {
                throw new InvalidDataException(
                    $"NIF block {block} ({decoded.Type}) decoded Properties as {value.Kind}, not an array.");
            }

            for (var ordinal = 0; ordinal < array.Count; ordinal++)
            {
                if (array.Items[ordinal] is not NifRefValue link)
                {
                    throw new InvalidDataException(
                        $"NIF block {block} ({decoded.Type}) Properties[{ordinal}] is not a block reference.");
                }

                if (link.IsNone)
                {
                    continue;
                }

                if ((uint)link.Index >= (uint)_state.Blocks.Count ||
                    !_state.Schema.Inherits(_state.Blocks[link.Index].Type, "NiProperty"))
                {
                    throw new InvalidDataException(
                        $"NIF block {block} ({decoded.Type}) Properties[{ordinal}] = {link.Index} is not an NiProperty block.");
                }

                result.Add(link.Index);
            }
        }

        _propertiesByBlock.Add(block, result);
        return result;
    }
}
