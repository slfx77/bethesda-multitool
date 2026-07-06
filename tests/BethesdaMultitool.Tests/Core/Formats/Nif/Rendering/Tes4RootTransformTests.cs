using System.Buffers.Binary;
using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Inspection;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

/// <summary>
///     TES4-era NIFs (Oblivion, 10.x–20.0.0.5) compose the scene ROOT node's authored transform
///     under the REFR placement — ChorrolLODHouse01's root bakes a −90°-about-X Y-up→Z-up
///     correction and the RFN dungeon halls bake 90/180° Z yaws; the identity-root treatment used
///     for placed-reference bakes must NOT discard those. FO3+/FNV (20.2.0.7) keeps the
///     decompile-anchored identity-root rule (McMarranWalls wallReg / monorail curves).
/// </summary>
public sealed class Tes4RootTransformTests
{
    // 90° about Z: rows [0 -1 0] [1 0 0] [0 0 1] (NIF row-major storage).
    private static readonly float[] RotZ90 = [0, -1, 0, 1, 0, 0, 0, 0, 1];

    [Fact]
    public void Tes4Nif_RootRotation_SurvivesIdentityRootBake()
    {
        var (data, nif) = BuildSingleRootNif(tes4: true);
        var worldTransforms = new Dictionary<int, Matrix4x4>();

        NifSceneGraphWalker.ComputeWorldTransforms(
            data, nif, new Dictionary<int, List<int>> { [0] = [] }, worldTransforms,
            treatRootsAsIdentity: true);

        var m = worldTransforms[0];
        // Stored row-major rows become matrix columns in the parser's basis: check the 90°-Z shape.
        Assert.Equal(0f, m.M11, 3);
        Assert.Equal(1f, m.M12, 3);
        Assert.Equal(-1f, m.M21, 3);
        Assert.Equal(0f, m.M22, 3);
        Assert.Equal(1f, m.M33, 3);
    }

    [Fact]
    public void Fo3EraNif_RootRotation_IsStillDiscarded()
    {
        var (data, nif) = BuildSingleRootNif(tes4: false);
        var worldTransforms = new Dictionary<int, Matrix4x4>();

        NifSceneGraphWalker.ComputeWorldTransforms(
            data, nif, new Dictionary<int, List<int>> { [0] = [] }, worldTransforms,
            treatRootsAsIdentity: true);

        Assert.Equal(Matrix4x4.Identity, worldTransforms[0]);
    }

    [Fact]
    public void Tes4Nif_WithoutIdentityRootBake_AppliesRotationToo()
    {
        // The single-NIF / skinned paths (treatRootsAsIdentity: false) must be unchanged.
        var (data, nif) = BuildSingleRootNif(tes4: true);
        var worldTransforms = new Dictionary<int, Matrix4x4>();

        NifSceneGraphWalker.ComputeWorldTransforms(
            data, nif, new Dictionary<int, List<int>> { [0] = [] }, worldTransforms);

        Assert.NotEqual(Matrix4x4.Identity, worldTransforms[0]);
    }

    private static (byte[] Data, NifInfo Nif) BuildSingleRootNif(bool tes4)
    {
        // NiNode payload: NiObjectNET header + flags + translation(12) + rotation(36) + scale(4).
        // TES4 (inline strings, BsVersion 11): name SizedString(len 0) + extra(0) + controller(-1),
        // flags u16. FNV (string table, BsVersion 34): name index(-1) + extra(0) + controller(-1),
        // flags u32 (BsVersion > 26).
        var flagsSize = tes4 ? 2 : 4;
        var data = new byte[12 + flagsSize + 12 + 36 + 4];
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(0), tes4 ? 0 : -1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 0);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8), -1);

        var pos = 12 + flagsSize;
        pos += 12; // translation stays (0,0,0)
        for (var i = 0; i < 9; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(pos + i * 4), RotZ90[i]);
        }

        pos += 36;
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(pos), 1f); // scale

        var nif = new NifInfo
        {
            IsBigEndian = false,
            BinaryVersion = tes4 ? 0x14000005u : 0x14020007u,
            BsVersion = tes4 ? 11u : 34u,
            HasInlineStrings = tes4
        };
        nif.Blocks.Add(new BlockInfo { Index = 0, TypeName = "NiNode", DataOffset = 0, Size = data.Length });
        return (data, nif);
    }
}
