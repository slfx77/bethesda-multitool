namespace BethesdaMultitool.Core.Minidump;

/// <summary>
///     Turns an MSVC RTTI type-descriptor name into a readable class name.
///     <para>
///         <see cref="RttiReader.DemangleName" /> truncates at the first <c>@@</c>, which is correct
///         only for unqualified names: a nested class arrives as <c>.?AVInner@Outer@@</c> and came
///         out as the literal <c>Inner@Outer</c>, which then failed to join against any PDB class
///         name. Reversing the fragments — MSVC encodes qualified names innermost-first — recovers
///         <c>Outer::Inner</c> and was measured to add 4.1 points of instance-weighted join rate
///         against the PDB type inventory on xex44.
///     </para>
///     <para>
///         Templates are deliberately NOT decoded. Their arguments are themselves mangled names
///         containing <c>@</c> separators, so the fragment split would produce nonsense; and the
///         cvdump type inventory they would be joined against truncates its own
///         <c>class name = …</c> field at the first comma, so multi-argument templates cannot be
///         matched even with a perfect demangler. They keep the same raw form the old helper
///         produced, so nothing regresses.
///     </para>
/// </summary>
public static class MsvcNameDemangler
{
    /// <summary>
    ///     Demangle <paramref name="mangledName" />, or null if it is not a class/struct type
    ///     descriptor name.
    /// </summary>
    public static string? Demangle(string? mangledName)
    {
        if (string.IsNullOrEmpty(mangledName))
        {
            return null;
        }

        if (!mangledName.StartsWith(".?AV", StringComparison.Ordinal) &&
            !mangledName.StartsWith(".?AU", StringComparison.Ordinal))
        {
            return null;
        }

        var body = mangledName[4..];
        var terminator = body.IndexOf("@@", StringComparison.Ordinal);
        if (terminator <= 0)
        {
            return null;
        }

        var scope = body[..terminator];
        if (!CanSplitScope(scope))
        {
            return scope;
        }

        var fragments = scope.Split('@');
        if (fragments.Length == 1)
        {
            return fragments[0];
        }

        Array.Reverse(fragments);
        return string.Join("::", fragments);
    }

    /// <summary>
    ///     Whether the qualified-name fragments can be reversed safely. Template names
    ///     (<c>?$Name@args</c>) and numeric back-references carry their own <c>@</c> structure that
    ///     a plain split would destroy, so those are returned verbatim instead.
    /// </summary>
    private static bool CanSplitScope(string scope)
    {
        if (scope.Contains("?$", StringComparison.Ordinal) ||
            scope.Contains("@@", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var fragment in scope.Split('@'))
        {
            // Empty fragment: malformed for our purposes. Single digit: a back-reference to an
            // earlier fragment, which needs a real demangler's substitution table.
            if (fragment.Length == 0 || (fragment.Length == 1 && char.IsAsciiDigit(fragment[0])))
            {
                return false;
            }
        }

        return true;
    }
}
