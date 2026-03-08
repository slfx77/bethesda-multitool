namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Appearance;

internal static class NpcFaceGenCoefficientMerger
{
    internal static float[]? Merge(float[]? npcCoefficients, float[]? raceCoefficients)
    {
        if (npcCoefficients == null)
        {
            return raceCoefficients;
        }

        if (raceCoefficients == null)
        {
            return npcCoefficients;
        }

        var count = Math.Min(npcCoefficients.Length, raceCoefficients.Length);
        var merged = new float[count];
        for (var i = 0; i < count; i++)
        {
            merged[i] = npcCoefficients[i] + raceCoefficients[i];
        }

        return merged;
    }
}
