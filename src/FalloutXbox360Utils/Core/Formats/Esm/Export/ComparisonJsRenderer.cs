namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

/// <summary>Client-side JavaScript for JSON-driven HTML comparison pages.</summary>
internal static class ComparisonJsRenderer
{
    private const string ScriptResourceName = "FalloutXbox360Utils.comparison-renderer.js";
    private static readonly Lazy<string> LazyScript = new(LoadScript);

    internal static string Script => LazyScript.Value;

    private static string LoadScript()
    {
        var assembly = typeof(ComparisonJsRenderer).Assembly;
        using var stream = assembly.GetManifestResourceStream(ScriptResourceName)
                           ?? throw new InvalidOperationException(
                               $"Embedded resource '{ScriptResourceName}' not found in assembly.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}