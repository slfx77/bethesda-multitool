// Schema-driven NIF block conversion using nif.xml definitions
// This reduces errors by reading structure definitions directly from the authoritative source

using System.Globalization;
using System.Xml.Linq;

namespace Xbox360MemoryCarver.Core.Formats.Nif;

/// <summary>
///     Represents a field in a NIF type definition.
/// </summary>
public sealed class NifFieldDef
{
    public required string Name { get; init; }
    public required string Type { get; init; }
    public string? Template { get; init; }
    public string? Length { get; init; } // Array count expression (e.g., "Num Vertices")
    public string? Width { get; init; } // Second dimension for 2D arrays (e.g., "Num Vertices" for UV Sets)
    public string? Condition { get; init; } // Runtime condition (cond)
    public string? VersionCond { get; init; } // Version condition (vercond)
    public string? Since { get; init; } // Minimum version
    public string? Until { get; init; } // Maximum version
    public string? OnlyT { get; init; } // Only for specific block types (onlyT)
    public string? Arg { get; init; } // Template argument for #ARG# substitution

    public override string ToString()
    {
        return $"{Name}: {Type}" + (Length != null ? $"[{Length}]" : "") + (Width != null ? $"[{Width}]" : "");
    }
}

/// <summary>
///     Represents a basic type definition (uint, ushort, float, etc.).
/// </summary>
public sealed class NifBasicType
{
    public required string Name { get; init; }
    public int Size { get; init; }
    public bool IsIntegral { get; init; }
    public bool IsGeneric { get; init; } // Ref, Ptr - need block remapping

    public override string ToString()
    {
        return $"{Name} ({Size} bytes)";
    }
}

/// <summary>
///     Represents a struct (compound type) definition.
/// </summary>
public sealed class NifStructDef
{
    public required string Name { get; init; }
    public int? FixedSize { get; init; } // Some structs have known fixed size
    public List<NifFieldDef> Fields { get; init; } = [];

    public override string ToString()
    {
        return $"struct {Name} ({Fields.Count} fields)";
    }
}

/// <summary>
///     Represents an enum/bitflags definition.
/// </summary>
public sealed class NifEnumDef
{
    public required string Name { get; init; }
    public required string Storage { get; init; } // Underlying type (uint, ushort, byte)

    public override string ToString()
    {
        return $"enum {Name} : {Storage}";
    }
}

/// <summary>
///     Represents a NiObject (block type) definition.
/// </summary>
public sealed class NifObjectDef
{
    public required string Name { get; init; }
    public string? Inherit { get; init; }
    public bool IsAbstract { get; init; }
    public List<NifFieldDef> Fields { get; init; } = [];

    // Resolved at runtime - includes inherited fields
    public List<NifFieldDef> AllFields { get; set; } = [];

    public override string ToString()
    {
        return $"niobject {Name}" + (Inherit != null ? $" : {Inherit}" : "") +
               $" ({Fields.Count} own, {AllFields.Count} total)";
    }
}

/// <summary>
///     Parses nif.xml and provides type definitions for schema-driven conversion.
/// </summary>
public sealed class NifSchema
{
    // Static cached instance - schema never changes at runtime
    private static readonly Lazy<NifSchema> CachedEmbeddedSchema = new(LoadEmbeddedInternal);

    private readonly Dictionary<string, NifBasicType> _basicTypes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, NifEnumDef> _enums = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, NifObjectDef> _objects = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, NifStructDef> _structs = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, NifBasicType> BasicTypes => _basicTypes;
    public IReadOnlyDictionary<string, NifStructDef> Structs => _structs;
    public IReadOnlyDictionary<string, NifEnumDef> Enums => _enums;
    public IReadOnlyDictionary<string, NifObjectDef> Objects => _objects;

    /// <summary>
    ///     Gets the cached embedded schema (loaded once per application lifetime).
    /// </summary>
    public static NifSchema LoadEmbedded()
    {
        return CachedEmbeddedSchema.Value;
    }

    /// <summary>
    ///     Internal implementation that loads schema from the embedded nif.xml resource.
    /// </summary>
    private static NifSchema LoadEmbeddedInternal()
    {
        var assembly = typeof(NifSchema).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
                               .FirstOrDefault(n => n.EndsWith("nif.xml", StringComparison.OrdinalIgnoreCase))
                           ?? throw new InvalidOperationException("Embedded nif.xml resource not found");

        using var stream = assembly.GetManifestResourceStream(resourceName)
                           ?? throw new InvalidOperationException($"Failed to load resource: {resourceName}");

        return LoadFromStream(stream);
    }

    /// <summary>
    ///     Loads schema from a file path.
    /// </summary>
    public static NifSchema LoadFromFile(string path)
    {
        using var stream = File.OpenRead(path);
        return LoadFromStream(stream);
    }

    /// <summary>
    ///     Loads schema from a stream.
    /// </summary>
    public static NifSchema LoadFromStream(Stream stream)
    {
        var schema = new NifSchema();
        var doc = XDocument.Load(stream);
        var root = doc.Root ?? throw new InvalidDataException("Invalid nif.xml: no root element");

        // Parse basic types
        foreach (var basic in root.Elements("basic"))
        {
            var name = basic.Attribute("name")?.Value;
            if (name == null)
            {
                continue;
            }

            var sizeStr = basic.Attribute("size")?.Value;
            var size = sizeStr != null ? int.Parse(sizeStr, CultureInfo.InvariantCulture) : 0;

            // Special case: bool changes size based on version
            if (name == "bool")
            {
                size = 1; // Use modern size
            }

            schema._basicTypes[name] = new NifBasicType
            {
                Name = name,
                Size = size,
                IsIntegral = basic.Attribute("integral")?.Value == "true",
                IsGeneric = basic.Attribute("generic")?.Value == "true"
            };
        }

        // Parse enums, bitflags, and bitfields (they all use 'storage' for underlying type)
        foreach (var elem in root.Elements("enum")
                     .Concat(root.Elements("bitflags"))
                     .Concat(root.Elements("bitfield")))
        {
            var name = elem.Attribute("name")?.Value;
            var storage = elem.Attribute("storage")?.Value;
            if (name == null || storage == null)
            {
                continue;
            }

            schema._enums[name] = new NifEnumDef
            {
                Name = name,
                Storage = storage
            };
        }

        // Parse structs
        foreach (var structElem in root.Elements("struct"))
        {
            var name = structElem.Attribute("name")?.Value;
            if (name == null)
            {
                continue;
            }

            var sizeStr = structElem.Attribute("size")?.Value;

            schema._structs[name] = new NifStructDef
            {
                Name = name,
                FixedSize = sizeStr != null ? int.Parse(sizeStr, CultureInfo.InvariantCulture) : null,
                Fields = ParseFields(structElem)
            };
        }

        // Parse niobjects
        foreach (var objElem in root.Elements("niobject"))
        {
            var name = objElem.Attribute("name")?.Value;
            if (name == null)
            {
                continue;
            }

            schema._objects[name] = new NifObjectDef
            {
                Name = name,
                Inherit = objElem.Attribute("inherit")?.Value,
                IsAbstract = objElem.Attribute("abstract")?.Value == "true",
                Fields = ParseFields(objElem)
            };
        }

        // Resolve inheritance for all objects
        foreach (var obj in schema._objects.Values)
        {
            schema.ResolveInheritance(obj);
        }

        return schema;
    }

    private static List<NifFieldDef> ParseFields(XElement parent)
    {
        var fields = new List<NifFieldDef>();

        foreach (var field in parent.Elements("field"))
        {
            var name = field.Attribute("name")?.Value;
            var type = field.Attribute("type")?.Value;
            if (name == null || type == null)
            {
                continue;
            }

            fields.Add(new NifFieldDef
            {
                Name = name,
                Type = type,
                Template = field.Attribute("template")?.Value,
                Length = field.Attribute("length")?.Value,
                Width = field.Attribute("width")?.Value,
                Condition = field.Attribute("cond")?.Value,
                VersionCond = field.Attribute("vercond")?.Value,
                Since = field.Attribute("since")?.Value,
                Until = field.Attribute("until")?.Value,
                OnlyT = field.Attribute("onlyT")?.Value,
                Arg = field.Attribute("arg")?.Value
            });
        }

        return fields;
    }

    private void ResolveInheritance(NifObjectDef obj)
    {
        if (obj.AllFields.Count > 0)
        {
            return; // Already resolved
        }

        var allFields = new List<NifFieldDef>();

        // Get parent fields first
        if (obj.Inherit != null && _objects.TryGetValue(obj.Inherit, out var parent))
        {
            ResolveInheritance(parent);
            allFields.AddRange(parent.AllFields);
        }

        // Add own fields
        allFields.AddRange(obj.Fields);

        obj.AllFields = allFields;
    }

    /// <summary>
    ///     Gets the size of a type in bytes, or null if dynamic/unknown.
    /// </summary>
    public int? GetTypeSize(string typeName)
    {
        // Check basic types
        if (_basicTypes.TryGetValue(typeName, out var basic))
        {
            return basic.Size > 0 ? basic.Size : null;
        }

        // Check enums (use storage type size)
        if (_enums.TryGetValue(typeName, out var enumDef))
        {
            return GetTypeSize(enumDef.Storage);
        }

        // Check structs with fixed size
        if (_structs.TryGetValue(typeName, out var structDef) && structDef.FixedSize.HasValue)
        {
            return structDef.FixedSize;
        }

        return null;
    }

    /// <summary>
    ///     Returns true if the type is a Ref or Ptr that needs block index remapping.
    /// </summary>
    public static bool IsBlockReference(string typeName)
    {
        return typeName is "Ref" or "Ptr";
    }

    /// <summary>
    ///     Gets the object definition for a block type.
    /// </summary>
    public NifObjectDef? GetObject(string typeName)
    {
        return _objects.GetValueOrDefault(typeName);
    }

    /// <summary>
    ///     Gets the struct definition for a compound type.
    /// </summary>
    public NifStructDef? GetStruct(string typeName)
    {
        return _structs.GetValueOrDefault(typeName);
    }

    /// <summary>
    ///     Checks if a block type inherits from (or is) another type.
    /// </summary>
    public bool Inherits(string blockType, string ancestorType)
    {
        if (string.Equals(blockType, ancestorType, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!_objects.TryGetValue(blockType, out var obj))
        {
            return false;
        }

        // Walk up the inheritance chain
        var current = obj;
        while (current.Inherit != null)
        {
            if (string.Equals(current.Inherit, ancestorType, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!_objects.TryGetValue(current.Inherit, out current))
            {
                break;
            }
        }

        return false;
    }
}
