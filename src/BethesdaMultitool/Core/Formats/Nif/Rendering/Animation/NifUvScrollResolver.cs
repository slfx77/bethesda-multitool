using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;

/// <summary>
///     Resolves a shape's TES3-era <c>NiUVController</c> chain to a CONSTANT UV scroll velocity
///     (UV units/second) when the authored keys form a straight looping ramp — the shape every
///     Morrowind waterfall/lava scroller uses (e.g. <c>ex_vivec_waterfall_05.nif</c>: V keys
///     t=0→0, t=1→−4, loop ⇒ (0,−4)/sec). Non-constant curves and scale animation return false and
///     leave the existing static-bake path untouched (labeled scope limit — a constant offset in a
///     per-draw shader constant is all the renderer applies).
/// </summary>
internal static class NifUvScrollResolver
{
    private const float SlopeTolerance = 1e-4f;
    private const int MaxControllerChain = 8;

    internal static bool TryResolve(byte[] data, NifInfo nif, int shapeBlockIndex, out Vector2 velocity)
    {
        velocity = Vector2.Zero;
        if (shapeBlockIndex < 0 || shapeBlockIndex >= nif.Blocks.Count)
        {
            return false;
        }

        var be = nif.IsBigEndian;
        var shapeBlock = nif.Blocks[shapeBlockIndex];
        var controllerRef = NifBinaryCursor.ReadNiObjectNETControllerRef(
            data,
            shapeBlock.DataOffset,
            shapeBlock.DataOffset + shapeBlock.Size,
            be,
            nif.HasInlineStrings,
            nif.BinaryVersion);

        for (var hop = 0; hop < MaxControllerChain && controllerRef >= 0 && controllerRef < nif.Blocks.Count; hop++)
        {
            var controllerBlock = nif.Blocks[controllerRef];
            if (!NifTimeControllerReader.TryRead(data, controllerBlock, be, out var header))
            {
                return false;
            }

            if (controllerBlock.TypeName == "NiUVController" &&
                TryResolveController(data, nif, controllerBlock, header, be, out velocity))
            {
                return true;
            }

            controllerRef = header.NextControllerRef;
        }

        return false;
    }

    private static bool TryResolveController(
        byte[] data, NifInfo nif, BlockInfo controllerBlock, NifTimeControllerHeader header, bool be,
        out Vector2 velocity)
    {
        velocity = Vector2.Zero;

        // A clamped controller plays once and stops — that's a transition, not a scroll. Reverse
        // (ping-pong) isn't a constant velocity either. The engine's scrollers author LOOP.
        if (!header.IsActive || header.CycleType != NifCycleType.Loop)
        {
            return false;
        }

        // NiUVController type-specific fields: Texture Set (ushort) @26, Data ref @28.
        if (controllerBlock.Size < NifTimeControllerHeader.HeaderSize + 6)
        {
            return false;
        }

        var dataRef = BinaryUtils.ReadInt32(
            data, controllerBlock.DataOffset + NifTimeControllerHeader.HeaderSize + 2, be);
        if (dataRef < 0 || dataRef >= nif.Blocks.Count || nif.Blocks[dataRef].TypeName != "NiUVData")
        {
            return false;
        }

        var uvData = NifUvDataReader.TryRead(data, nif.Blocks[dataRef], be);
        if (uvData is null)
        {
            return false;
        }

        // Animated scale isn't expressible as a constant offset — bail to the static bake.
        if (HasScaleAnimation(uvData.UScaleKeys) || HasScaleAnimation(uvData.VScaleKeys))
        {
            return false;
        }

        if (!TryConstantSlope(uvData.UTranslationKeys, out var uSlope) ||
            !TryConstantSlope(uvData.VTranslationKeys, out var vSlope))
        {
            return false;
        }

#pragma warning disable S1244 // 0 is the authored "unset" frequency sentinel; exact comparison intended
        velocity = new Vector2(uSlope, vSlope) * (header.Frequency == 0f ? 1f : header.Frequency);
#pragma warning restore S1244
        return velocity != Vector2.Zero;
    }

    private static bool HasScaleAnimation(NifFloatKey[] keys)
    {
        for (var i = 1; i < keys.Length; i++)
        {
            if (MathF.Abs(keys[i].Value - keys[0].Value) > SlopeTolerance)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     A key ramp is a constant velocity when every successive segment has the same slope. Empty
    ///     and single-key channels are "no motion" (slope 0, still valid — the OTHER channel may
    ///     carry the scroll).
    /// </summary>
    private static bool TryConstantSlope(NifFloatKey[] keys, out float slope)
    {
        slope = 0f;
        if (keys.Length < 2)
        {
            return true;
        }

        var totalTime = keys[^1].Time - keys[0].Time;
        if (totalTime <= 0f)
        {
            return false;
        }

        slope = (keys[^1].Value - keys[0].Value) / totalTime;
        for (var i = 1; i < keys.Length; i++)
        {
            var dt = keys[i].Time - keys[i - 1].Time;
            if (dt <= 0f)
            {
                return false;
            }

            var segmentSlope = (keys[i].Value - keys[i - 1].Value) / dt;
            if (MathF.Abs(segmentSlope - slope) > SlopeTolerance * MathF.Max(1f, MathF.Abs(slope)))
            {
                return false;
            }
        }

        return true;
    }
}
