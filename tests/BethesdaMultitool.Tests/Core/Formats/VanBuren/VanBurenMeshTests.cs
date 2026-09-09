using System.Text;
using BethesdaMultitool.Core.Formats.VanBuren;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.VanBuren;

/// <summary>
///     Vectors for Van Buren <c>B3D</c> mesh identification, shaped after the 3,915 payloads
///     measured 2026-09-06: 3,912 carry exactly two nodes with <c>Scene Root</c> first.
/// </summary>
public sealed class VanBurenMeshTests
{
    private static byte[] Mesh(byte firstOpcode, params string[] nodes)
    {
        var b = new List<byte>(Encoding.ASCII.GetBytes(VanBurenMesh.Signature));
        b.Add(firstOpcode);
        b.AddRange([0x03, 0x00, 0x00, 0x80, 0x3F]); // an opcode-03 float, as retail carries
        foreach (var node in nodes)
        {
            b.AddRange([0x0A, 0x0C]);
            b.AddRange(BitConverter.GetBytes((ushort)node.Length));
            b.AddRange(Encoding.ASCII.GetBytes(node));
        }

        b.AddRange([0, 0, 0, 0]);
        return [.. b];
    }

    [Fact]
    public void Parse_TakesTheMeshNameFromTheNodeAfterSceneRoot()
    {
        var mesh = VanBurenMesh.Parse(Mesh(28, VanBurenMesh.SceneRoot, "CR_Cougar"), "Critters.grp/00007");

        Assert.Equal(28, mesh.FirstOpcode);
        Assert.Equal([VanBurenMesh.SceneRoot, "CR_Cougar"], mesh.Nodes);
        Assert.Equal("CR_Cougar", mesh.MeshName);
    }

    [Fact]
    public void MeshName_IsNullWhenThePayloadLacksTheTwoNodeShape()
    {
        // ⚠ 3 of the 3,915 retail meshes have no nodes at all (all opcode 53, all in Critters.grp).
        // They are reported as unnamed rather than guessed at.
        Assert.Null(VanBurenMesh.Parse(Mesh(53), "odd").MeshName);
        Assert.Null(VanBurenMesh.Parse(Mesh(28, "Only One"), "odd").MeshName);
    }

    [Fact]
    public void MeshName_RequiresSceneRootToComeFirst()
    {
        // The rule is not "the second node" — it is "the node after Scene Root", which held on
        // 3,912/3,912. A payload that names something else first is not the shape we measured.
        Assert.Null(VanBurenMesh.Parse(Mesh(28, "Something Else", "CR_Rat"), "odd").MeshName);
    }

    [Fact]
    public void Parse_IgnoresMarkerBytesThatDoNotIntroduceAPrintableName()
    {
        // 0A 0C occurs in binary payload data too; a length that does not lead to printable ASCII
        // must be skipped rather than taken as a node.
        var b = new List<byte>(Encoding.ASCII.GetBytes(VanBurenMesh.Signature)) { 28 };
        b.AddRange([0x0A, 0x0C, 0x04, 0x00, 0xFF, 0xFE, 0x01, 0x02]); // marker, but binary payload
        b.AddRange([0x0A, 0x0C]);
        b.AddRange(BitConverter.GetBytes((ushort)VanBurenMesh.SceneRoot.Length));
        b.AddRange(Encoding.ASCII.GetBytes(VanBurenMesh.SceneRoot));
        b.AddRange([0x0A, 0x0C]);
        b.AddRange(BitConverter.GetBytes((ushort)6));
        b.AddRange("CR_Bat"u8);

        var mesh = VanBurenMesh.Parse([.. b], "x");
        Assert.Equal("CR_Bat", mesh.MeshName);
    }

    [Fact]
    public void Parse_RejectsAPayloadWithoutTheSignature()
    {
        Assert.Throws<InvalidDataException>(() => VanBurenMesh.Parse("EEN2....."u8.ToArray(), "not a mesh"));
    }

    [Fact]
    public void IsMesh_ChecksTheSignature()
    {
        Assert.True(VanBurenMesh.IsMesh(Mesh(28, VanBurenMesh.SceneRoot, "X")));
        Assert.False(VanBurenMesh.IsMesh("B3D 1.0 "u8.ToArray()));
    }
}