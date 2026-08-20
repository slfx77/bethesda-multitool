using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Materials;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Materials;

/// <summary>
///     Synthetic-graph coverage for the NiTextureEffect environment resolver: a TextureType-2 /
///     CG_SPHERE_MAP effect hosted by a node (via the CHILDREN list — Bethesda's TES3 exporter
///     leaves the Effects array empty, byte-verified on <c>a_glass_boots_gnd.nif</c> — or via the
///     Effects list proper) applies its NiSourceTexture path to every descendant shape, gated on
///     Switch State and scoped by a non-empty affected-node list.
/// </summary>
public sealed class NifTextureEffectEnvironmentPolicyTests
{
    private const uint Tes4Version = 0x14000005; // Oblivion 20.0.0.5
    private const uint Tes4BsVersion = 11;
    private const string SpherePath = @"textures\environment\envmapwindows.dds";

    [Fact]
    public void ChildHostedEnvironmentEffect_AppliesToSiblingShapes()
    {
        var (data, nif) = BuildNif(switchState: true, affectedNodes: [], textureType: 2, coordGen: 2);
        var nodeChildren = new Dictionary<int, List<int>> { [0] = [1, 3] };

        var map = NifTextureEffectEnvironmentPolicy.ResolveShapeEnvironmentMaps(
            data, nif, nodeChildren, [3]);

        Assert.NotNull(map);
        Assert.Equal(SpherePath, map[3]);
    }

    [Fact]
    public void EffectsListHostedEnvironmentEffect_AppliesToChildShapes()
    {
        // Effect ref only in the NiNode Effects array (well-formed Gamebryo), not in Children.
        var (data, nif) = BuildNif(
            switchState: true, affectedNodes: [], textureType: 2, coordGen: 2,
            effectInChildren: false);
        var nodeChildren = new Dictionary<int, List<int>> { [0] = [3] };

        var map = NifTextureEffectEnvironmentPolicy.ResolveShapeEnvironmentMaps(
            data, nif, nodeChildren, [3]);

        Assert.NotNull(map);
        Assert.Equal(SpherePath, map[3]);
    }

    [Fact]
    public void SwitchedOffEffect_IsIgnored()
    {
        var (data, nif) = BuildNif(switchState: false, affectedNodes: [], textureType: 2, coordGen: 2);
        var nodeChildren = new Dictionary<int, List<int>> { [0] = [1, 3] };

        Assert.Null(NifTextureEffectEnvironmentPolicy.ResolveShapeEnvironmentMaps(
            data, nif, nodeChildren, [3]));
    }

    [Fact]
    public void NonEnvironmentOrNonSphereEffects_AreIgnored()
    {
        // Projected light (type 0) and a world-perspective env (coordgen 1) both fail the filter.
        foreach (var (textureType, coordGen) in new[] { (0u, 2u), (2u, 1u) })
        {
            var (data, nif) = BuildNif(true, [], textureType, coordGen);
            var nodeChildren = new Dictionary<int, List<int>> { [0] = [1, 3] };

            Assert.Null(NifTextureEffectEnvironmentPolicy.ResolveShapeEnvironmentMaps(
                data, nif, nodeChildren, [3]));
        }
    }

    [Fact]
    public void AffectedNodeScope_RestrictsToListedSubtrees()
    {
        // Effect scoped to node 4; the shape hangs under node 0 only → not applied.
        var (data, nif) = BuildNif(switchState: true, affectedNodes: [4], textureType: 2, coordGen: 2);
        var nodeChildren = new Dictionary<int, List<int>> { [0] = [1, 3] };

        Assert.Null(NifTextureEffectEnvironmentPolicy.ResolveShapeEnvironmentMaps(
            data, nif, nodeChildren, [3]));

        // Scoped to the hosting node itself (an ancestor of the shape) → applied.
        var (scopedData, scopedNif) = BuildNif(
            switchState: true, affectedNodes: [0], textureType: 2, coordGen: 2);
        var scopedMap = NifTextureEffectEnvironmentPolicy.ResolveShapeEnvironmentMaps(
            scopedData, scopedNif, new Dictionary<int, List<int>> { [0] = [1, 3] }, [3]);

        Assert.NotNull(scopedMap);
        Assert.Equal(SpherePath, scopedMap[3]);
    }

    [Fact]
    public void NifWithoutTextureEffects_ResolvesNull()
    {
        var (data, nif) = BuildNif(true, [], 2, 2);
        nif.Blocks[1].TypeName = "NiPointLight"; // no NiTextureEffect anywhere → fast negative

        Assert.Null(NifTextureEffectEnvironmentPolicy.ResolveShapeEnvironmentMaps(
            data, nif, new Dictionary<int, List<int>> { [0] = [1, 3] }, [3]));
    }

    // Blocks: 0 = NiNode (real bytes — the resolver parses its Effects array), 1 = NiTextureEffect,
    // 2 = NiSourceTexture, 3 = NiTriShape (bytes never read by the resolver). TES4-era 20.0.0.5
    // inline-string layout throughout.
    private static (byte[] Data, NifInfo Nif) BuildNif(
        bool switchState,
        int[] affectedNodes,
        uint textureType,
        uint coordGen,
        bool effectInChildren = true)
    {
        var node = new List<byte>();
        AppendObjectNet(node);
        AppendAvObjectBase(node);
        if (effectInChildren)
        {
            AppendUInt(node, 2); // Num Children
            AppendInt(node, 1);  // the effect (TES3 exporter style)
            AppendInt(node, 3);  // the shape
            AppendUInt(node, 0); // Num Effects (empty — retail Morrowind authoring)
        }
        else
        {
            AppendUInt(node, 1); // Num Children
            AppendInt(node, 3);  // the shape only
            AppendUInt(node, 1); // Num Effects
            AppendInt(node, 1);  // the effect (well-formed Gamebryo authoring)
        }

        var effect = new List<byte>();
        AppendObjectNet(effect);
        AppendAvObjectBase(effect);
        effect.Add(switchState ? (byte)1 : (byte)0); // Switch State (since 10.1.0.106)
        AppendUInt(effect, (uint)affectedNodes.Length);
        foreach (var nodeRef in affectedNodes)
        {
            AppendInt(effect, nodeRef);
        }

        for (var i = 0; i < 12; i++)
        {
            AppendUInt(effect, 0); // Model Projection Matrix (36) + Translation (12)
        }

        AppendUInt(effect, 2); // Texture Filtering
        AppendUInt(effect, 3); // Texture Clamping
        AppendUInt(effect, textureType);
        AppendUInt(effect, coordGen);
        AppendInt(effect, 2);  // Source Texture ref → block 2

        var source = new List<byte>();
        AppendObjectNet(source);
        source.Add(1); // Use External
        AppendUInt(source, (uint)SpherePath.Length);
        source.AddRange(Encoding.ASCII.GetBytes(SpherePath));

        var shape = new List<byte> { 0 }; // placeholder — the resolver never reads shape bytes

        var data = new byte[node.Count + effect.Count + source.Count + shape.Count];
        node.CopyTo(data, 0);
        effect.CopyTo(data, node.Count);
        source.CopyTo(data, node.Count + effect.Count);
        shape.CopyTo(data, node.Count + effect.Count + source.Count);

        var nif = new NifInfo
        {
            BinaryVersion = Tes4Version,
            BsVersion = Tes4BsVersion,
            HasInlineStrings = true,
            BlockCount = 4
        };
        nif.Blocks.Add(new BlockInfo
        {
            Index = 0, TypeName = "NiNode", DataOffset = 0, Size = node.Count
        });
        nif.Blocks.Add(new BlockInfo
        {
            Index = 1, TypeName = "NiTextureEffect", DataOffset = node.Count, Size = effect.Count
        });
        nif.Blocks.Add(new BlockInfo
        {
            Index = 2, TypeName = "NiSourceTexture", DataOffset = node.Count + effect.Count,
            Size = source.Count
        });
        nif.Blocks.Add(new BlockInfo
        {
            Index = 3, TypeName = "NiTriShape",
            DataOffset = node.Count + effect.Count + source.Count, Size = shape.Count
        });
        return (data, nif);
    }

    private static void AppendObjectNet(List<byte> bytes)
    {
        AppendUInt(bytes, 0); // name length 0 (inline SizedString)
        AppendUInt(bytes, 0); // Num Extra Data List
        AppendInt(bytes, -1); // Controller ref
    }

    // NiAVObject base, 20.0.0.5 / BS 11: Flags ushort + T/R/S + Num Properties + Collision ref.
    private static void AppendAvObjectBase(List<byte> bytes)
    {
        bytes.Add(4);
        bytes.Add(0); // Flags
        for (var i = 0; i < 13; i++)
        {
            AppendUInt(bytes, 0); // Translation (12) + Rotation (36) + Scale (4) = 52 bytes
        }

        AppendUInt(bytes, 0); // Num Properties
        AppendInt(bytes, -1); // Collision Object ref
    }

    private static void AppendUInt(List<byte> bytes, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        bytes.AddRange(buffer.ToArray());
    }

    private static void AppendInt(List<byte> bytes, int value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
        bytes.AddRange(buffer.ToArray());
    }
}
