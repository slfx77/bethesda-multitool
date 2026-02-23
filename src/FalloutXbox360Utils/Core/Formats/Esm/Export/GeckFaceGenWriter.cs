using System.Text;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

/// <summary>Generates GECK-style text output for FaceGen morph data (FGGS/FGGA/FGTS).</summary>
internal static class GeckFaceGenWriter
{
    /// <summary>
    ///     Count the number of active FaceGen sliders for a given morph array and append
    ///     a compact summary (e.g. "FGGS 5/50 active") to the parts list.
    /// </summary>
    internal static void AppendFaceGenSliderCount(
        List<string> parts,
        string label,
        float[]? morphs,
        Func<float[], (string Name, float Value)[]> computeFunc)
    {
        if (morphs == null || morphs.Length == 0) return;
        var sliders = computeFunc(morphs);
        var active = sliders.Count(s => Math.Abs(s.Value) > 0.01f);
        if (active > 0)
        {
            parts.Add($"{label} {active}/{sliders.Length} active");
        }
    }

    /// <summary>
    ///     Append a FaceGen control section using CTL-based projections.
    ///     Computes named slider values by projecting basis coefficients (FGGS/FGGA/FGTS)
    ///     through the si.ctl linear control direction vectors.
    ///     Controls are sorted alphabetically and grouped by facial region.
    /// </summary>
    internal static void AppendFaceGenControlSection(
        StringBuilder sb,
        string sectionLabel,
        float[]? basisValues,
        Func<float[], (string Name, float Value)[]> computeControls)
    {
        if (basisValues == null || basisValues.Length == 0)
        {
            return;
        }

        // Check if all basis values are zero
        var basisActive = 0;
        foreach (var v in basisValues)
        {
            if (Math.Abs(v) > 0.0001f)
            {
                basisActive++;
            }
        }

        if (basisActive == 0)
        {
            sb.AppendLine($"  {sectionLabel} ({basisValues.Length} basis values): all zero");
            return;
        }

        // Compute named control projections
        var controls = computeControls(basisValues);
        var activeControls = controls.Where(c => Math.Abs(c.Value) > 0.01f).ToList();
        activeControls.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        sb.AppendLine($"  {sectionLabel} ({controls.Length} controls, {activeControls.Count} active):");

        if (activeControls.Count == 0)
        {
            sb.AppendLine("    (all controls near zero)");
            return;
        }

        foreach (var (name, value) in activeControls)
        {
            sb.AppendLine($"    {name,-45} {value,+8:F4}");
        }
    }

    /// <summary>
    ///     Append raw little-endian hex bytes for a FaceGen float array.
    ///     Each float is converted to its IEEE 754 little-endian 4-byte representation
    ///     (PC-compatible format for GECK import/ESM editing).
    ///     This allows exact reproduction without floating-point rounding.
    /// </summary>
    internal static void AppendFaceGenRawHex(StringBuilder sb, string label, float[]? values)
    {
        if (values == null || values.Length == 0)
        {
            return;
        }

        // Check if all zero - skip hex if so
        var allZero = true;
        foreach (var v in values)
        {
            if (Math.Abs(v) > 0.0001f)
            {
                allZero = false;
                break;
            }
        }

        if (allZero)
        {
            return;
        }

        sb.AppendLine($"  {label} Raw Hex ({values.Length * 4} bytes, little-endian / PC):");

        // Convert each float to little-endian bytes and format as hex
        var hexLine = new StringBuilder("    ");
        for (var i = 0; i < values.Length; i++)
        {
            var bytes = BitConverter.GetBytes(values[i]);
            // BitConverter gives native endian (LE on x86); reverse if running on BE
            if (!BitConverter.IsLittleEndian)
            {
                Array.Reverse(bytes);
            }

            hexLine.Append($"{bytes[0]:X2} {bytes[1]:X2} {bytes[2]:X2} {bytes[3]:X2}");

            if (i < values.Length - 1)
            {
                hexLine.Append(' ');
            }

            // Line break every 10 floats (40 bytes) for readability
            if ((i + 1) % 10 == 0 && i < values.Length - 1)
            {
                sb.AppendLine(hexLine.ToString().TrimEnd());
                hexLine.Clear();
                hexLine.Append("    ");
            }
        }

        if (hexLine.Length > 4)
        {
            sb.AppendLine(hexLine.ToString().TrimEnd());
        }
    }
}
