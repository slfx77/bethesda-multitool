namespace BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;

/// <summary>
///     Resolves a local's displayed type without changing its stored SLSD integer flag.
///     Reference locals share non-integer storage with floats; only an SCRV entry in
///     the owning script's ordered reference table identifies them here. Names and
///     reconstructed source are not evidence of a reference declaration.
/// </summary>
internal static class ScriptVariableTypeResolver
{
    private const uint LocalReferenceMarker = 0x80000000;

    internal static ScriptVariableTypeDescription Resolve(
        ScriptVariableInfo variable, IEnumerable<uint> referencedObjects)
    {
        var isReference = variable.Index < LocalReferenceMarker
                          && referencedObjects.Contains(LocalReferenceMarker | variable.Index);
        if (!isReference)
        {
            return new ScriptVariableTypeDescription(variable.TypeName, "slsd-storage-flag");
        }

        var evidence = variable.Type == 0
            ? "scrv-local-reference"
            : "scrv-local-reference; conflicts-with-integer-storage";
        return new ScriptVariableTypeDescription("ref", evidence);
    }

    internal static string FormatDeclaration(ScriptVariableInfo variable, IEnumerable<uint> referencedObjects,
        bool alignType = false)
    {
        var resolved = Resolve(variable, referencedObjects);
        var type = alignType ? resolved.Name.PadRight(5) : resolved.Name;
        var declaration = $"{type} {variable.Name ?? "(unnamed)"}";
        return resolved.Name == "ref"
            ? $"{declaration} ({resolved.Evidence}; storage type byte {variable.Type})"
            : declaration;
    }
}

internal readonly record struct ScriptVariableTypeDescription(string Name, string Evidence);
