using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Parser;

/// <summary>
///     Pins the NiTextureEffect stream layouts the reader models. The legacy fixture reproduces the
///     byte-verified retail Morrowind block (<c>meshes\a\a_glass_boots_gnd.nif</c> block 16, offset
///     0x3C68, 181 bytes: TextureType 2 ENVIRONMENT_MAP, CoordGen 2 SPHERE_MAP, Source Texture ref
///     17, trailing PS2 K default −75) — the fixture length is asserted at exactly 181 so any layout
///     drift breaks loudly. The TES4-era fixture exercises the 20.0.0.5 arm (no Velocity, Collision
///     Object ref, Switch State byte, block-ref affected-node list, no PS2 shorts). Retail TES4
///     itself authors NO NiTextureEffect blocks (9,612-NIF sweep 2026-08-19); the TES4-era arm
///     serves modded / runtime-captured content that uses the identical layout.
/// </summary>
public sealed class NifTextureEffectReaderTests
{
    private const uint Tes3Version = 0x04000002; // Morrowind 4.0.0.2
    private const uint Tes4Version = 0x14000005; // Oblivion 20.0.0.5
    private const uint Tes4BsVersion = 11;

    /// <summary>The affected-node set every fixture in this class authors.</summary>
    private static readonly int[] ExpectedAffectedNodes = [4, 9];

    [Fact]
    public void Parse_LegacyMorrowindLayout_ReadsEnvironmentSphereMapAndSourceRef()
    {
        var bytes = BuildLegacyEffect(
            NifTextureEffectReader.TextureTypeEnvironmentMap,
            NifTextureEffectReader.CoordGenTypeSphereMap,
            17);
        // Byte-parity pin against the retail block (0x3C68, size 181, empty name, 0 affected).
        Assert.Equal(181, bytes.Length);

        var block = MakeBlock("NiTextureEffect", bytes.Length);
        var info = NifTextureEffectReader.Parse(
            bytes, block, 0, Tes3Version, false, true);

        Assert.NotNull(info);
        Assert.True(info.Value.SwitchState); // field absent pre-10.1.0.106 → defaults enabled
        Assert.Empty(info.Value.AffectedNodes); // legacy pointer hashes are never surfaced as refs
        Assert.Equal(NifTextureEffectReader.TextureTypeEnvironmentMap, info.Value.TextureType);
        Assert.Equal(NifTextureEffectReader.CoordGenTypeSphereMap, info.Value.CoordGenType);
        Assert.Equal(17, info.Value.SourceTextureRef);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Parse_Tes4EraLayout_ReadsSwitchStateAffectedNodesAndFields(bool switchState)
    {
        var bytes = BuildTes4Effect(
            switchState,
            [4, 9],
            NifTextureEffectReader.TextureTypeEnvironmentMap,
            NifTextureEffectReader.CoordGenTypeSphereMap,
            2);

        var block = MakeBlock("NiTextureEffect", bytes.Length);
        var info = NifTextureEffectReader.Parse(
            bytes, block, Tes4BsVersion, Tes4Version, false, true);

        Assert.NotNull(info);
        Assert.Equal(switchState, info.Value.SwitchState);
        Assert.Equal(ExpectedAffectedNodes, info.Value.AffectedNodes);
        Assert.Equal(NifTextureEffectReader.TextureTypeEnvironmentMap, info.Value.TextureType);
        Assert.Equal(NifTextureEffectReader.CoordGenTypeSphereMap, info.Value.CoordGenType);
        Assert.Equal(2, info.Value.SourceTextureRef);
    }

    [Fact]
    public void Parse_NonEnvironmentTypes_AreStillReadFaithfully()
    {
        // Projected light (0) with world-parallel coordinates (0) — the policy filters these; the
        // reader itself must report them as authored.
        var bytes = BuildTes4Effect(
            true, [], 0, 0, 5);

        var info = NifTextureEffectReader.Parse(
            bytes, MakeBlock("NiTextureEffect", bytes.Length), Tes4BsVersion, Tes4Version,
            false, true);

        Assert.NotNull(info);
        Assert.Equal(0u, info.Value.TextureType);
        Assert.Equal(0u, info.Value.CoordGenType);
        Assert.Equal(5, info.Value.SourceTextureRef);
    }

    [Fact]
    public void Parse_TruncatedBlock_ReturnsNull()
    {
        var bytes = BuildTes4Effect(
            true,
            [],
            NifTextureEffectReader.TextureTypeEnvironmentMap,
            NifTextureEffectReader.CoordGenTypeSphereMap,
            2);
        var truncated = bytes[..(bytes.Length - 20)]; // lose coordgen/source tail

        Assert.Null(NifTextureEffectReader.Parse(
            truncated, MakeBlock("NiTextureEffect", truncated.Length), Tes4BsVersion, Tes4Version,
            false, true));
    }

    private static BlockInfo MakeBlock(string typeName, int size)
    {
        return new BlockInfo { Index = 0, TypeName = typeName, DataOffset = 0, Size = size };
    }

    // Morrowind 4.0.0.2: SizedString name + single Extra Data ref + Controller ref, ushort Flags,
    // T/R/S, Velocity, Properties count, Has Bounding Volume (bool32), Num Affected Nodes +
    // pointer hashes, then the NiTextureEffect fields including the legacy PS2/plane tail.
    private static byte[] BuildLegacyEffect(uint textureType, uint coordGen, int sourceRef)
    {
        var bytes = new List<byte>();
        AppendUInt(bytes, 0); // name length 0 (inline SizedString)
        AppendInt(bytes, -1); // Extra Data ref (legacy single ref)
        AppendInt(bytes, -1); // Controller ref
        bytes.Add(4);
        bytes.Add(0); // Flags (ushort)
        AppendZeros(bytes, 12); // Translation
        AppendIdentityMatrix(bytes);
        AppendFloat(bytes, 1f); // Scale
        AppendZeros(bytes, 12); // Velocity (until 4.2.2.0)
        AppendUInt(bytes, 0); // Num Properties
        AppendUInt(bytes, 0); // Has Bounding Volume (bool32)
        AppendUInt(bytes, 0); // Num Affected Nodes (pointer hashes)
        AppendIdentityMatrix(bytes); // Model Projection Matrix
        AppendZeros(bytes, 12); // Model Projection Translation
        AppendUInt(bytes, 2); // Texture Filtering (FILTER_TRILERP)
        AppendUInt(bytes, 3); // Texture Clamping (WRAP_S_WRAP_T)
        AppendUInt(bytes, textureType);
        AppendUInt(bytes, coordGen);
        AppendInt(bytes, sourceRef);
        bytes.Add(0); // Enable Plane
        AppendZeros(bytes, 16); // Plane
        AppendUInt(bytes, 0xFFB5_0000); // PS2 L = 0, PS2 K = -75 (retail default)
        bytes.Add(0);
        bytes.Add(0); // Unknown Short (until 4.1.0.12)
        return [.. bytes];
    }

    // Oblivion 20.0.0.5 / BS 11: SizedString name + Extra Data List + Controller, ushort Flags,
    // T/R/S (no Velocity), Properties count, Collision Object ref, Switch State byte, block-ref
    // affected-node list, then the NiTextureEffect fields (no Max Anisotropy below 20.5.0.4; the
    // parser never reads past the Source Texture ref, so the fixture ends there).
    private static byte[] BuildTes4Effect(
        bool switchState, int[] affectedNodes, uint textureType, uint coordGen, int sourceRef)
    {
        var bytes = new List<byte>();
        AppendUInt(bytes, 0); // name length 0 (inline SizedString)
        AppendUInt(bytes, 0); // Num Extra Data List
        AppendInt(bytes, -1); // Controller ref
        bytes.Add(4);
        bytes.Add(0); // Flags (ushort — BS stream 11 <= 26)
        AppendZeros(bytes, 12); // Translation
        AppendIdentityMatrix(bytes);
        AppendFloat(bytes, 1f); // Scale
        AppendUInt(bytes, 0); // Num Properties
        AppendInt(bytes, -1); // Collision Object ref
        bytes.Add(switchState ? (byte)1 : (byte)0); // Switch State (since 10.1.0.106)
        AppendUInt(bytes, (uint)affectedNodes.Length);
        foreach (var nodeRef in affectedNodes)
        {
            AppendInt(bytes, nodeRef);
        }

        AppendIdentityMatrix(bytes); // Model Projection Matrix
        AppendZeros(bytes, 12); // Model Projection Translation
        AppendUInt(bytes, 2); // Texture Filtering
        AppendUInt(bytes, 3); // Texture Clamping
        AppendUInt(bytes, textureType);
        AppendUInt(bytes, coordGen);
        AppendInt(bytes, sourceRef);
        return [.. bytes];
    }

    private static void AppendIdentityMatrix(List<byte> bytes)
    {
        for (var row = 0; row < 3; row++)
        {
            for (var col = 0; col < 3; col++)
            {
                AppendFloat(bytes, row == col ? 1f : 0f);
            }
        }
    }

    private static void AppendZeros(List<byte> bytes, int count)
    {
        for (var i = 0; i < count; i++)
        {
            bytes.Add(0);
        }
    }

    private static void AppendFloat(List<byte> bytes, float value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(buffer, value);
        bytes.AddRange(buffer.ToArray());
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