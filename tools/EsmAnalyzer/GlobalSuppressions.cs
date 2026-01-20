// This file is used by Code Analysis to maintain SuppressMessage
// attributes that are applied to this project.
// Project-level suppressions either have no target or are given
// a specific target and scoped to a namespace, type, member, etc.

using System.Diagnostics.CodeAnalysis;

// CA1305: Locale-sensitive string formatting is acceptable for this console tool
// as all output is for diagnostic purposes and doesn't need to be locale-invariant
[assembly: SuppressMessage("Globalization", "CA1305:Specify IFormatProvider",
    Justification = "Console diagnostic tool - locale-sensitive formatting is acceptable")]

// CA1834: StringBuilder.Append(char) optimization is not critical for this diagnostic tool
[assembly: SuppressMessage("Performance", "CA1834:Consider using 'StringBuilder.Append(char)' when applicable",
    Justification = "Not performance-critical for diagnostic tool")]