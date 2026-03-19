namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Appearance;

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

        var directory = Path.GetDirectoryName(bodyTexturePath);
        var handFileName = isFemale ? "HandFemale.dds" : "HandMale.dds";
        var handPath = directory != null
            ? Path.Combine(directory, handFileName)
            : handFileName;
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
