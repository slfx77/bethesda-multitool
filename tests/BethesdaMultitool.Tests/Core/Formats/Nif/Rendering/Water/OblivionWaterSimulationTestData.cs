using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Water;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Water;

internal static class OblivionWaterSimulationTestData
{
    internal static OblivionWaterSimulationInputs Inputs =>
        OblivionWaterSimulationInputs.Read(BethesdaGame.Oblivion, 0x18, Data(), false)!;

    internal static OblivionWaterDisplacementState State =>
        OblivionWaterDisplacementState.Create(new OblivionWaterSimulationResource(1),
            new OblivionWaterSimulationResource(2), new OblivionWaterSimulationResource(3),
            new OblivionWaterSimulationResource(4), new OblivionWaterSimulationResource(5));

    internal static byte[] Data(bool bigEndian = false)
    {
        var data = new byte[102];
        for (var index = 0; index < 10; index++)
        {
            Write(data, index, index + 1, bigEndian);
        }

        return data;
    }

    internal static void Write(byte[] data, int index, float value, bool bigEndian = false)
    {
        if (bigEndian)
        {
            BinaryPrimitives.WriteSingleBigEndian(data.AsSpan(60 + index * 4, 4), value);
        }
        else
        {
            BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(60 + index * 4, 4), value);
        }
    }

    internal static OblivionWaterDisplacementInvocation Rain(long order = 1, float amount = 1f)
    {
        return new OblivionWaterDisplacementInvocation(order, OblivionWaterSimulationMode.Rain, 0f, amount, 0,
            ImmutableArray<OblivionWaterRecordedRainSample>.Empty, null, Vector2.Zero,
            new OblivionWaterSimulationResource(8), new OblivionWaterSimulationResource(9),
            new OblivionWaterSimulationResource(6));
    }

    internal static OblivionWaterDisplacementInvocation Wading(long order = 1)
    {
        return new OblivionWaterDisplacementInvocation(order, OblivionWaterSimulationMode.Wading, 0f, 0f, 0,
            ImmutableArray<OblivionWaterRecordedRainSample>.Empty, null, Vector2.Zero,
            null, null, new OblivionWaterSimulationResource(7));
    }
}