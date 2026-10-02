using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Modeling.Nif;
using BethesdaMultitool.Tests.Helpers;
using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Tests.Core.Modeling.RealAsset;

/// <summary>
///     The receipts the cut-1b slice-9 document hops write (plan section 3: every control must fail, and the receipt
///     records that it did). The directory follows an environment-free convention: the repository's <c>TestOutput</c>,
///     then <c>cut1b-slice9-&lt;UTC date of the run&gt;</c>, then the hop's test class name.
/// </summary>
/// <remarks>
///     <para>
///         Layout, per hop: <c>receipt.json</c> (the hop, the run date, the tool versions, every row in key order and
///         every control), rewritten whole after each record so an interrupted run leaves a complete file for what ran;
///         <c>rows/&lt;entry&gt;_&lt;sha12&gt;.json</c> (one per manifest file); <c>controls/&lt;name&gt;.json</c> (one per
///         control, with its observed failure). Every write goes to a temporary file first and is then moved over the
///         target, so no reader sees a half-written file.
///     </para>
///     <para>
///         Nothing is written until a hop records something, so a run without <c>RUN_BUCKET_B</c> (every row skips) leaves
///         no directory behind. Records are kept per process; the rows of a hop run in one sequential collection.
///     </para>
/// </remarks>
internal static class Cut1bHopReceipt
{
    /// <summary>The prefix of the run directory under <c>TestOutput</c>.</summary>
    public const string RunDirectoryPrefix = "cut1b-slice9-";

    private static readonly Lazy<string> RunDate =
        new(static () => DateTime.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture));

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static readonly object Gate = new();

    private static readonly Dictionary<string, (SortedDictionary<string, JsonObject> Rows,
        SortedDictionary<string, JsonObject> Controls, JsonObject Tools)> Hops = new(StringComparer.Ordinal);

    /// <summary>The run directory of a hop: <c>&lt;repo&gt;/TestOutput/cut1b-slice9-&lt;date&gt;/&lt;hop&gt;</c>.</summary>
    /// <param name="hop">The hop's test class name.</param>
    /// <returns>The absolute directory path (not created).</returns>
    public static string Directory(string hop)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hop);
        return Path.Combine(SourceContract.RepoRoot, "TestOutput", RunDirectoryPrefix + RunDate.Value, hop);
    }

    /// <summary>Records one manifest file's result and rewrites the hop's receipt.</summary>
    /// <param name="hop">The hop's test class name.</param>
    /// <param name="file">The manifest row.</param>
    /// <param name="row">The row's result (copied).</param>
    public static void Row(string hop, Cut1bCoverFile file, JsonObject row)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(row);
        var record = (JsonObject)row.DeepClone();
        record["entry"] = file.Entry;
        record["sha256"] = file.Sha256;
        record["key"] = file.Key;
        var name = Safe(file.Entry) + "_" + file.Sha256[..12];
        lock (Gate)
        {
            State(hop).Rows[name] = record;
            WriteJson(Path.Combine(Directory(hop), "rows", name + ".json"), record);
            WriteReceipt(hop);
        }
    }

    /// <summary>Records one control's observed outcome and rewrites the hop's receipt.</summary>
    /// <param name="hop">The hop's test class name.</param>
    /// <param name="name">The control's name.</param>
    /// <param name="control">What the control changed, where it was run and how it failed (copied).</param>
    public static void Control(string hop, string name, JsonObject control)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(control);
        var record = (JsonObject)control.DeepClone();
        record["control"] = name;
        lock (Gate)
        {
            State(hop).Controls[name] = record;
            WriteJson(Path.Combine(Directory(hop), "controls", Safe(name) + ".json"), record);
            WriteReceipt(hop);
        }
    }

    /// <summary>
    ///     Writes one supporting artifact of a hop (for example hop A8's evaluator output for one file) under the hop's
    ///     directory, without adding it to the receipt.
    /// </summary>
    /// <param name="hop">The hop's test class name.</param>
    /// <param name="folder">The subfolder (for example <c>eval</c>).</param>
    /// <param name="file">The manifest row the artifact belongs to.</param>
    /// <param name="node">The artifact.</param>
    /// <returns>The artifact's path.</returns>
    public static string Artifact(string hop, string folder, Cut1bCoverFile file, JsonNode node)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(node);
        var path = Path.Combine(Directory(hop), Safe(folder), Safe(file.Entry) + "_" + file.Sha256[..12] + ".json");
        lock (Gate)
        {
            WriteJson(path, node);
        }

        return path;
    }

    /// <summary>Adds or replaces one tool-version entry of a hop (for example the Python and numpy versions of hop A8).</summary>
    /// <param name="hop">The hop's test class name.</param>
    /// <param name="name">The tool's name.</param>
    /// <param name="value">Its version or identity (copied).</param>
    public static void Tool(string hop, string name, JsonNode? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (Gate)
        {
            State(hop).Tools[name] = value?.DeepClone();
        }
    }

    /// <summary>The tool versions every receipt carries: the .NET runtime, the OS and the BMT and Shared assemblies.</summary>
    /// <returns>A new object.</returns>
    public static JsonObject BaseTools()
    {
        return new JsonObject
        {
            ["dotnet"] = RuntimeInformation.FrameworkDescription,
            ["os"] = RuntimeInformation.OSDescription,
            ["architecture"] = RuntimeInformation.ProcessArchitecture.ToString(),
            ["bethesdaMultitool"] = Version(typeof(NifModelAnimationReader).Assembly),
            ["sharedCore"] = Version(typeof(ScenePoseEvaluator).Assembly),
            ["testAssembly"] = Version(typeof(Cut1bHopReceipt).Assembly)
        };
    }

    private static (SortedDictionary<string, JsonObject> Rows, SortedDictionary<string, JsonObject> Controls,
        JsonObject Tools) State(string hop)
    {
        if (!Hops.TryGetValue(hop, out var state))
        {
            state = (new SortedDictionary<string, JsonObject>(StringComparer.Ordinal),
                new SortedDictionary<string, JsonObject>(StringComparer.Ordinal), BaseTools());
            Hops.Add(hop, state);
        }

        return state;
    }

    private static void WriteReceipt(string hop)
    {
        var state = State(hop);
        var rows = new JsonArray();
        foreach (var row in state.Rows.Values)
        {
            rows.Add(row.DeepClone());
        }

        var controls = new JsonArray();
        foreach (var control in state.Controls.Values)
        {
            controls.Add(control.DeepClone());
        }

        var receipt = new JsonObject
        {
            ["hop"] = hop,
            ["runUtcDate"] = RunDate.Value,
            ["writtenUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ["tools"] = state.Tools.DeepClone(),
            ["rowCount"] = state.Rows.Count,
            ["controlCount"] = state.Controls.Count,
            ["rows"] = rows,
            ["controls"] = controls
        };
        WriteJson(Path.Combine(Directory(hop), "receipt.json"), receipt);
    }

    private static void WriteJson(string path, JsonNode node)
    {
        var directory = Path.GetDirectoryName(path)!;
        System.IO.Directory.CreateDirectory(directory);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, node.ToJsonString(Indented), new UTF8Encoding(false));
        File.Move(temporary, path, true);
    }

    private static string Version(Assembly assembly)
    {
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
               ?? assembly.GetName().Version?.ToString()
               ?? "unknown";
    }

    /// <summary>A file-name-safe spelling: letters, digits, '-', '_' and '.' kept, everything else '_'.</summary>
    private static string Safe(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            builder.Append(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' ? character : '_');
        }

        return builder.Length > 120 ? builder.ToString(builder.Length - 120, 120) : builder.ToString();
    }
}
