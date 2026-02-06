using System.Diagnostics.CodeAnalysis;

namespace FalloutXbox360Utils.Core.Utils;

/// <summary>
///     Utility for finding tool executables and workspace paths.
/// </summary>
public static class ToolPathFinder
{
    /// <summary>
    ///     Finds the workspace root directory by searching for .sln or .slnx files.
    /// </summary>
    /// <param name="startDir">Directory to start searching from.</param>
    /// <returns>The workspace root path, or null if not found.</returns>
    public static string? FindWorkspaceRoot(string startDir)
    {
        var dir = startDir;
        while (!string.IsNullOrEmpty(dir))
        {
            if (Directory.GetFiles(dir, "*.slnx").Length > 0 || Directory.GetFiles(dir, "*.sln").Length > 0)
            {
                return dir;
            }

            var parent = Directory.GetParent(dir);
            if (parent == null)
            {
                break;
            }

            dir = parent.FullName;
        }

        return null;
    }

    /// <summary>
    ///     Gets the directory containing the currently executing assembly.
    /// </summary>
    [RequiresAssemblyFiles()]
    public static string GetAssemblyDirectory()
    {
        return Path.GetDirectoryName(typeof(ToolPathFinder).Assembly.Location) ?? string.Empty;
    }
}
