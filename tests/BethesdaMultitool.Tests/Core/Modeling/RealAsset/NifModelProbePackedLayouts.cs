using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using static BethesdaMultitool.Tests.Core.Modeling.RealAsset.NifModelOracleSupport;

namespace BethesdaMultitool.Tests.Core.Modeling.RealAsset;

/// <summary>
///     The six measured console packed layouts, transcribed from TestOutput/packed-semantics-20260924/README.md
///     ("Layout table for the C# decoder"; stream type 16 = half4, 14 = half2, 28 = four bytes, 3 = float3), and the
///     match of a probe expectation's packed stream table (<c>packed[block].streams</c>: type, unit size, block
///     offset, stride) against them. Every retail table has one data block and one stride shared by every stream.
/// </summary>
internal static class NifModelProbePackedLayouts
{
    /// <summary>The measured layouts in the README's order.</summary>
    public static IReadOnlyList<NifModelProbePackedLayout> Known { get; } =
    [
        new NifModelProbePackedLayout("L1", 40, [(16, 8, 0), (16, 8, 8), (28, 4, 16), (14, 4, 20), (16, 8, 24), (16, 8, 32)],
            true, false),
        new NifModelProbePackedLayout("L2", 36, [(16, 8, 0), (16, 8, 8), (14, 4, 16), (16, 8, 20), (16, 8, 28)], false,
            false),
        new NifModelProbePackedLayout("L3", 48,
            [(16, 8, 0), (16, 8, 8), (28, 4, 16), (16, 8, 20), (14, 4, 28), (16, 8, 32), (16, 8, 40)], false, true),
        new NifModelProbePackedLayout("L4", 52,
            [(16, 8, 0), (16, 8, 8), (28, 4, 16), (16, 8, 20), (28, 4, 28), (14, 4, 32), (16, 8, 36), (16, 8, 44)], true,
            true),
        new NifModelProbePackedLayout("L5", 48, [(16, 8, 0), (3, 12, 8), (14, 4, 20), (3, 12, 24), (3, 12, 36)], false,
            false),
        new NifModelProbePackedLayout("L6", 52, [(16, 8, 0), (3, 12, 8), (28, 4, 20), (14, 4, 24), (3, 12, 28), (3, 12, 40)],
            true, false)
    ];

    /// <summary>The layout whose stream table (type, unit size, offset per stream, one stride) the probe's equals, or null.</summary>
    public static NifModelProbePackedLayout? Match(JsonObject packedProbe)
    {
        var (streams, stride) = Table(packedProbe);
        if (stride is null)
        {
            return null;
        }

        foreach (var layout in Known)
        {
            if (layout.Stride == stride && layout.Streams.SequenceEqual(streams))
            {
                return layout;
            }
        }

        return null;
    }

    /// <summary>The probe's stream table as text, for a message: each (type, unit size, offset) and the stride(s).</summary>
    public static string KeyOf(JsonObject packedProbe)
    {
        var text = new StringBuilder();
        var strides = new SortedSet<long>();
        foreach (var stream in packedProbe["streams"]!.AsArray().OfType<JsonObject>())
        {
            text.Append(CultureInfo.InvariantCulture,
                $"({Long(stream["type"])},{Long(stream["unitSize"])}@{Long(stream["blockOffset"])})");
            strides.Add(Long(stream["stride"]));
        }

        text.Append(CultureInfo.InvariantCulture, $" stride {string.Join("/", strides)}");
        return text.ToString();
    }

    /// <summary>The probe's (type, unit size, offset) sequence and its single stride, or a null stride when they differ.</summary>
    private static (List<(uint Type, uint UnitSize, uint Offset)> Streams, uint? Stride) Table(JsonObject packedProbe)
    {
        var streams = new List<(uint, uint, uint)>();
        uint? stride = null;
        var consistent = true;
        foreach (var stream in packedProbe["streams"]!.AsArray().OfType<JsonObject>())
        {
            streams.Add(((uint)Long(stream["type"]), (uint)Long(stream["unitSize"]), (uint)Long(stream["blockOffset"])));
            var declared = (uint)Long(stream["stride"]);
            if (stride is null)
            {
                stride = declared;
            }
            else if (stride != declared)
            {
                consistent = false;
            }
        }

        return (streams, consistent ? stride : null);
    }
}
