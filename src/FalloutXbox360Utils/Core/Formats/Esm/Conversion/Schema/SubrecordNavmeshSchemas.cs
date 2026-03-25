using F = FalloutXbox360Utils.Core.Formats.Esm.Conversion.Schema.SubrecordField;

namespace FalloutXbox360Utils.Core.Formats.Esm.Conversion.Schema;

/// <summary>
///     Navmesh-related schemas.
/// </summary>
internal static class SubrecordNavmeshSchemas
{
    /// <summary>
    ///     Register navmesh-related schemas.
    /// </summary>
    internal static void Register(Dictionary<SubrecordSchemaRegistry.SchemaKey, SubrecordSchema> schemas)
    {
        // NVVX - Navmesh Vertices (array of Vec3, 12 bytes each)
        schemas[new SubrecordSchemaRegistry.SchemaKey("NVVX")] = SubrecordSchema.FloatArray;

        // NVTR - Navmesh Triangles (16 bytes each)
        schemas[new SubrecordSchemaRegistry.SchemaKey("NVTR")] = new SubrecordSchema(
            F.UInt16("Vertex0"), F.UInt16("Vertex1"), F.UInt16("Vertex2"),
            F.Int16("Edge01"), F.Int16("Edge12"), F.Int16("Edge20"),
            F.UInt16("Flags"), F.UInt16("CoverFlags"))
        {
            ExpectedSize = -1,
            Description = "Navmesh Triangles Array"
        };

        // NVCA - Cover Triangles (array of uint16)
        schemas[new SubrecordSchemaRegistry.SchemaKey("NVCA")] = new SubrecordSchema(F.UInt16("Triangle"))
        {
            ExpectedSize = -1,
            Description = "Navmesh Cover Triangles"
        };

        // NVDP - Navmesh Door Links (8 bytes each)
        schemas[new SubrecordSchemaRegistry.SchemaKey("NVDP")] = new SubrecordSchema(
            F.FormId("DoorRef"),
            F.UInt16("Triangle"),
            F.Padding(2))
        {
            ExpectedSize = -1,
            Description = "Navmesh Door Links Array"
        };

        // NVEX - Navmesh Edge Links (10 bytes each)
        schemas[new SubrecordSchemaRegistry.SchemaKey("NVEX")] = new SubrecordSchema(
            F.UInt32("Type"),
            F.FormId("Navmesh"),
            F.UInt16("Triangle"))
        {
            ExpectedSize = -1,
            Description = "Navmesh Edge Links Array"
        };

        // DATA - NAVM (20 bytes)
        schemas[new SubrecordSchemaRegistry.SchemaKey("DATA", "NAVM", 20)] = new SubrecordSchema(
            F.FormId("Cell"),
            F.UInt32("VertexCount"),
            F.UInt32("TriangleCount"),
            F.UInt32("EdgeLinkCount"),
            F.UInt32("DoorLinkCount"))
        {
            Description = "Navmesh Data"
        };

        // DATA - NAVM (24 bytes) - alternate size
        schemas[new SubrecordSchemaRegistry.SchemaKey("DATA", "NAVM", 24)] = new SubrecordSchema(
            F.FormId("Cell"),
            F.UInt32("VertexCount"),
            F.UInt32("TriangleCount"),
            F.UInt32("EdgeLinkCount"),
            F.UInt32("DoorLinkCount"),
            F.UInt32("Unknown"))
        {
            Description = "Navmesh Data (24 bytes)"
        };
    }
}
