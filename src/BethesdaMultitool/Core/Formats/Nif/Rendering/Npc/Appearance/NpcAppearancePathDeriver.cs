using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Appearance;

/// <summary>
///     Prefixes relative asset paths with the canonical <c>meshes\</c> / <c>textures\</c> roots used by Bethesda
///     archives.
/// </summary>
internal static class NpcAppearancePathDeriver
{
    internal static string? AsMeshPath(string? relativePath)
    {
        return relativePath != null ? "meshes\\" + relativePath : null;
    }

    internal static string? AsTexturePath(string? relativePath)
    {
        return relativePath != null ? "textures\\" + relativePath : null;
    }

    internal static string BuildFaceGenNifPath(string pluginName, uint formId)
    {
        return $"meshes\\characters\\facegendata\\facegeom\\{pluginName}\\{formId:X8}.nif";
    }

    /// <summary>
    ///     Builds the authored TES4 FaceGenMap0 path. Oblivion stores the editor-baked, 128-centered
    ///     per-NPC color delta under <c>textures\faces\&lt;plugin&gt;\&lt;formid&gt;_0.dds</c>.
    /// </summary>
    internal static string? BuildAuthoredFaceGenMap0Path(
        BethesdaGame game,
        string? pluginName,
        uint formId)
    {
        if (game != BethesdaGame.Oblivion || string.IsNullOrWhiteSpace(pluginName))
        {
            return null;
        }

        var normalizedPluginName = pluginName.Trim().Replace('/', '\\').TrimEnd('\\');
        var separatorIndex = normalizedPluginName.LastIndexOf('\\');
        if (separatorIndex >= 0)
        {
            normalizedPluginName = normalizedPluginName[(separatorIndex + 1)..];
        }

        return normalizedPluginName.Length == 0
            ? null
            : $"textures\\faces\\{normalizedPluginName}\\{formId:X8}_0.dds";
    }

    internal static string? DeriveHeadTriPath(string? headNifPath)
    {
        return headNifPath != null ? Path.ChangeExtension(headNifPath, ".tri") : null;
    }

    internal static string? DeriveHandTexturePath(
        string? bodyTexturePath,
        bool isFemale)
    {
        if (bodyTexturePath == null)
        {
            return null;
        }

        // The body texture path is an engine path (backslash separated on every host).
        var directory = EnginePath.DirectoryName(bodyTexturePath);
        var handFileName = isFemale ? "HandFemale.dds" : "HandMale.dds";
        var handPath = directory.Length == 0
            ? handFileName
            : directory + "\\" + handFileName;
        return AsTexturePath(handPath);
    }

    internal static (string? BodyEgt, string? LeftHandEgt, string? RightHandEgt)
        DeriveBodyEgtPaths(string? headNifPath, bool isFemale)
    {
        if (headNifPath == null)
        {
            return (null, null, null);
        }

        var headFileName = Path.GetFileNameWithoutExtension(headNifPath);
        if (headFileName == null)
        {
            return (null, null, null);
        }

        string bodyEgtName;
        string handVariant;
        var lowerHeadName = headFileName.ToLowerInvariant();

        if (lowerHeadName.Contains("ghoul"))
        {
            bodyEgtName = "upperbodyhumanghoul.egt";
            handVariant = isFemale ? "ghoulfemale" : "ghoul";
        }
        else if (lowerHeadName.Contains("old"))
        {
            bodyEgtName = isFemale
                ? "upperbodyhumanoldfemale.egt"
                : "upperbodyhumanold.egt";
            handVariant = isFemale ? "oldfemale" : "old";
        }
        else if (lowerHeadName.Contains("child"))
        {
            bodyEgtName = isFemale
                ? "upperbodychildfemale.egt"
                : "upperbodychild.egt";
            handVariant = isFemale ? "childfemale" : "child";
        }
        else
        {
            bodyEgtName = "body.egt";
            handVariant = isFemale ? "female" : "male";
        }

        const string bodyDirectory = "meshes\\characters\\_male\\";
        return (
            bodyDirectory + bodyEgtName,
            bodyDirectory + "lefthand" + handVariant + ".egt",
            bodyDirectory + "righthand" + handVariant + ".egt");
    }
}
