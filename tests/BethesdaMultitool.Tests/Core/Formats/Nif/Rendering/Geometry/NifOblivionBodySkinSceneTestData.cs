using System.Numerics;
using System.Text;
using BethesdaMultitool.Core.Formats.Nif.Parser;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Geometry;

/// <summary>Independent static root plus disconnected bone framing, matching the retail tail layout.</summary>
internal sealed class NifOblivionBodySkinSceneTestData
{
    internal NifOblivionBodySkinSceneTestData()
    {
        Source = new NifOblivionBodySkinTestData(shapeName: "Tail");
        using var stream = new MemoryStream();
        stream.Write(Source.Data);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        WriteNode("root", "Scene Root", 2, [0]);
        WriteNode("bone", "Bip01 Tail01", 0x10, []);
        writer.Flush();
        Data = stream.ToArray();

        void WriteNode(string prefix, string name, ushort flags, int[] children)
        {
            var start = checked((int)stream.Position);
            var bytes = Encoding.ASCII.GetBytes(name);
            Mark("name"); writer.Write((uint)bytes.Length); writer.Write(bytes);
            Mark("extras"); writer.Write(0u);
            Mark("controller"); writer.Write(-1);
            Mark("flags"); writer.Write(flags);
            Mark("transform"); WriteVector(Vector3.Zero);
            WriteVector(Vector3.UnitX); WriteVector(Vector3.UnitY); WriteVector(Vector3.UnitZ);
            writer.Write(1f);
            Mark("properties"); writer.Write(0u);
            Mark("collision"); writer.Write(-1);
            Mark("children"); writer.Write((uint)children.Length);
            foreach (var child in children)
            {
                Mark("child"); writer.Write(child);
            }
            Mark("effects"); writer.Write(0u);
            Source.Info.Blocks.Add(new BlockInfo
            {
                Index = Source.Info.Blocks.Count, TypeName = "NiNode", DataOffset = start,
                Size = checked((int)stream.Position) - start
            });
            Source.Info.BlockCount++;
            void Mark(string field) => Offsets.Add(prefix + "-" + field, checked((int)stream.Position));
        }

        void WriteVector(Vector3 value)
        {
            writer.Write(value.X); writer.Write(value.Y); writer.Write(value.Z);
        }
    }

    internal NifOblivionBodySkinTestData Source { get; }
    internal byte[] Data { get; }
    internal NifInfo Info => Source.Info;
    internal Dictionary<string, int> Offsets { get; } = new(StringComparer.Ordinal);
}
