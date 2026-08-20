using System.Globalization;

namespace BethesdaMultitool.Core.Formats.SpeedTree;

/// <summary>
///     Diagnostic dump of the builder's RNG draw sequence, in the SAME column format as the runtime
///     <c>SptMeshDumper</c> generation trace (<c>spt_gen_*.csv</c>: <c>seq,kind,callsite,state,a,b,c</c>),
///     so the engine's <c>CIdvRandom::GetUniform</c>/<c>Reseed</c> stream and <see cref="SptGeometryBuilder" />'s
///     <c>SptRandom.Range</c>/<c>Reseed</c> stream can be diffed line-for-line. The first divergence is the
///     point where the builder's generation calls the RNG a different number of times / in a different
///     order than the engine — the cause of the residual leaf candidate-count gap (1666 vs the engine's
///     1664 for WastelandShrub).
/// </summary>
/// <remarks>
///     Gated by the <c>SPT_RNG_TRACE</c> environment variable. Set it to <c>1</c> to write to
///     <c>%TEMP%/spt_gen_builder_&lt;seed&gt;.csv</c>, or to an explicit file path to control the location.
///     Disabled (returns null) when the variable is unset, so production renders pay nothing.
/// </remarks>
internal sealed class SptRngTrace : IDisposable
{
    private readonly StreamWriter _writer;
    private long _seq;

    private SptRngTrace(StreamWriter writer)
    {
        _writer = writer;
        _writer.WriteLine("seq,kind,callsite,state,a,b,c");
    }

    /// <summary>
    ///     Short label (e.g. <c>"branch L1"</c>, <c>"child c2"</c>) the builder sets so each row maps
    ///     to its generation phase — the C# analog of the engine trace's call-site VA. Free-form; never
    ///     contains a comma.
    /// </summary>
    public string Phase { get; set; } = "";

    public void Dispose()
    {
        _writer.Flush();
        _writer.Dispose();
    }

    /// <summary>
    ///     Open a trace if <c>SPT_RNG_TRACE</c> is set, else return null. <paramref name="seed" /> only
    ///     names the default temp file.
    /// </summary>
    public static SptRngTrace? FromEnvironment(uint seed)
    {
        var value = Environment.GetEnvironmentVariable("SPT_RNG_TRACE");
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var path = value is "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(Path.GetTempPath(), $"spt_gen_builder_{seed}.csv")
            : value;

        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            return new SptRngTrace(new StreamWriter(path, false));
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    ///     A logical uniform draw — the analog of the engine's <c>GetUniform(min, max)</c>.
    ///     <paramref name="state" /> is the Park-Miller state BEFORE the draw (matches the engine's
    ///     <c>_DAT_011f9120</c>).
    /// </summary>
    public void Uniform(int state, float min, float max, float result)
    {
        _writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{_seq++},U,{Phase},{state},{min:R},{max:R},{result:R}"));
    }

    /// <summary>
    ///     A reseed — the analog of the engine's <c>CIdvRandom::Reseed(seed)</c>.
    ///     <paramref name="oldState" /> is the state before the reseed overwrote it.
    /// </summary>
    public void Reseed(uint seed, int oldState)
    {
        _writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{_seq++},R,{Phase},{oldState},{seed},0,0"));
    }

    /// <summary>
    ///     A leaf-placement decision — the analog of the engine's <c>RoomForLeaf</c> "L" rows
    ///     (ordering will NOT match the engine, which interleaves placement with generation; the builder
    ///     places all leaves after generation — these are for accept-count and spacing comparison only).
    /// </summary>
    public void Leaf(int index, float spacing, bool accepted)
    {
        _writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{_seq++},L,{Phase},0,{index},{spacing:R},{(accepted ? 1 : 0)}"));
    }
}
