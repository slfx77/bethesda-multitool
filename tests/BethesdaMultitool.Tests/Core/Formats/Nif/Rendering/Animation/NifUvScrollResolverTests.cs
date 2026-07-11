using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Animation;

/// <summary>
///     <see cref="NifUvScrollResolver" /> reduces a TES3 shape → NiUVController → NiUVData chain to
///     a constant UV velocity when the keys form a straight looping ramp (the Morrowind
///     waterfall/lava shape: V keys t=0→0, t=1→−4, loop ⇒ (0,−4)/sec). Everything else must return
///     false so the static bake stays in charge.
/// </summary>
public class NifUvScrollResolverTests
{
    private const ushort ActiveLoop = 0x8;          // cycle bits 1-2 = 0 (loop), bit 3 = active
    private const ushort ActiveClamp = 0x8 | 0x4;   // cycle = 2 (clamp), active

    [Fact]
    public void WaterfallShapedRamp_ResolvesConstantVelocity()
    {
        var (data, nif) = BuildChain(
            controllerFlags: ActiveLoop, frequency: 1f,
            vKeys: [(0f, 0f), (1f, -4f)]);

        Assert.True(NifUvScrollResolver.TryResolve(data, nif, shapeBlockIndex: 0, out var velocity));
        Assert.Equal(new Vector2(0f, -4f), velocity);
    }

    [Fact]
    public void Frequency_ScalesTheVelocity()
    {
        var (data, nif) = BuildChain(
            controllerFlags: ActiveLoop, frequency: 2f,
            vKeys: [(0f, 0f), (1f, -4f)]);

        Assert.True(NifUvScrollResolver.TryResolve(data, nif, 0, out var velocity));
        Assert.Equal(new Vector2(0f, -8f), velocity);
    }

    [Fact]
    public void MultiKeyRamp_WithConstantSlope_Resolves()
    {
        var (data, nif) = BuildChain(
            controllerFlags: ActiveLoop, frequency: 1f,
            vKeys: [(0f, 0f), (0.5f, -2f), (1f, -4f)]);

        Assert.True(NifUvScrollResolver.TryResolve(data, nif, 0, out var velocity));
        Assert.Equal(new Vector2(0f, -4f), velocity);
    }

    [Fact]
    public void NonConstantCurve_ReturnsFalse()
    {
        var (data, nif) = BuildChain(
            controllerFlags: ActiveLoop, frequency: 1f,
            vKeys: [(0f, 0f), (0.5f, -3f), (1f, -4f)]); // slope -6 then -2

        Assert.False(NifUvScrollResolver.TryResolve(data, nif, 0, out _));
    }

    [Fact]
    public void ClampCycle_ReturnsFalse()
    {
        var (data, nif) = BuildChain(
            controllerFlags: ActiveClamp, frequency: 1f,
            vKeys: [(0f, 0f), (1f, -4f)]);

        Assert.False(NifUvScrollResolver.TryResolve(data, nif, 0, out _));
    }

    [Fact]
    public void InactiveController_ReturnsFalse()
    {
        var (data, nif) = BuildChain(
            controllerFlags: 0x0, frequency: 1f,
            vKeys: [(0f, 0f), (1f, -4f)]);

        Assert.False(NifUvScrollResolver.TryResolve(data, nif, 0, out _));
    }

    [Fact]
    public void NoTranslationMotion_ReturnsFalse()
    {
        var (data, nif) = BuildChain(
            controllerFlags: ActiveLoop, frequency: 1f,
            vKeys: [(0f, -1f)]); // single key = no motion

        Assert.False(NifUvScrollResolver.TryResolve(data, nif, 0, out _));
    }

    // ---- synthetic chain: [0] shape → controller [1] → NiUVData [2] -----------------------------

    private static (byte[] data, NifInfo nif) BuildChain(
        ushort controllerFlags, float frequency, (float T, float V)[] vKeys)
    {
        // Legacy (4.0.0.2) NiObjectNET head: SizedString name + extra ref + controller ref.
        var shape = new List<byte>();
        AppendSizedString(shape, "Tri Waterfall 0");
        AppendInt(shape, -1); // extra data
        AppendInt(shape, 1);  // controller → block 1

        // NiUVController: base header (26) + Texture Set ushort + Data ref.
        var controller = new List<byte>();
        AppendInt(controller, -1);                    // next controller
        AppendUShort(controller, controllerFlags);
        AppendFloat(controller, frequency);
        AppendFloat(controller, 0f);                  // phase
        AppendFloat(controller, 0f);                  // start
        AppendFloat(controller, 1f);                  // stop
        AppendInt(controller, 0);                     // target (the shape)
        AppendUShort(controller, 0);                  // texture set
        AppendInt(controller, 2);                     // data → block 2

        // NiUVData: U-trans empty, V-trans keys (linear), scales empty.
        var uvData = new List<byte>();
        AppendUInt(uvData, 0);                        // U translation: none
        AppendUInt(uvData, (uint)vKeys.Length);       // V translation
        if (vKeys.Length > 0)
        {
            AppendUInt(uvData, 1);                    // LINEAR
            foreach (var (t, v) in vKeys)
            {
                AppendFloat(uvData, t);
                AppendFloat(uvData, v);
            }
        }

        AppendUInt(uvData, 0);                        // U scale: none
        AppendUInt(uvData, 0);                        // V scale: none

        return BuildNif(
            ("NiTriShape", shape.ToArray()),
            ("NiUVController", controller.ToArray()),
            ("NiUVData", uvData.ToArray()));
    }

    private static (byte[] data, NifInfo nif) BuildNif(params (string type, byte[] payload)[] blocks)
    {
        var nif = new NifInfo
        {
            IsBigEndian = false,
            BlockCount = blocks.Length,
            BinaryVersion = 0x04000002,
            HasInlineStrings = true,
        };
        using var ms = new MemoryStream();
        var offsets = new int[blocks.Length];
        for (var i = 0; i < blocks.Length; i++)
        {
            offsets[i] = (int)ms.Length;
            ms.Write(blocks[i].payload);
        }

        var data = ms.ToArray();
        for (var i = 0; i < blocks.Length; i++)
        {
            nif.Blocks.Add(new BlockInfo
            {
                Index = i,
                TypeName = blocks[i].type,
                DataOffset = offsets[i],
                Size = blocks[i].payload.Length
            });
        }

        return (data, nif);
    }

    private static void AppendSizedString(List<byte> bytes, string value)
    {
        AppendUInt(bytes, (uint)value.Length);
        bytes.AddRange(Encoding.ASCII.GetBytes(value));
    }

    private static void AppendInt(List<byte> bytes, int value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(b, value);
        bytes.AddRange(b.ToArray());
    }

    private static void AppendUInt(List<byte> bytes, uint value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, value);
        bytes.AddRange(b.ToArray());
    }

    private static void AppendUShort(List<byte> bytes, ushort value)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(b, value);
        bytes.AddRange(b.ToArray());
    }

    private static void AppendFloat(List<byte> bytes, float value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(b, value);
        bytes.AddRange(b.ToArray());
    }
}
