namespace BethesdaMultitool.Core.RuntimeSession;

/// <summary>Session-local settings for continuous game updates and owned investigation scripts.</summary>
internal static class RuntimeLiveConfiguration
{
    internal static async Task<IReadOnlyList<(string Source, string Target)>> StageAsync(
        string profileRoot, string gameCopy, string documentsCopy, CancellationToken token)
    {
        var generated = Path.Combine(profileRoot, "live-config");
        Directory.CreateDirectory(generated);
        var result = new List<(string, string)>();
        var names = new List<string> { "Fallout.ini", "FalloutPrefs.ini", "nvse_config.ini" };
        if (File.Exists(Path.Combine(documentsCopy, "FalloutCustom.ini"))) names.Add("FalloutCustom.ini");
        foreach (var name in names)
        {
            var target = name == "nvse_config.ini"
                ? Path.Combine(gameCopy, "Data", "NVSE", name) : Path.Combine(documentsCopy, name);
            var content = File.Exists(target) ? await File.ReadAllTextAsync(target, token) : "";
            if (name == "nvse_config.ini")
                content = SetValue(content, "RELEASE", "bNoScriptRunnerCaching", "1");
            else
            {
                content = SetValue(content, "General", "SLocalSavePath", RuntimeLiveSaveIsolation.IniValue(profileRoot));
                content = SetValue(content, "General", "bAlwaysActive", "1");
                content = SetValue(content, "Display", "bFull Screen", "0");
                content = SetValue(content, "Display", "iSize W", "1280");
                content = SetValue(content, "Display", "iSize H", "720");
                content = SetValue(content, "Controls", "bBackground Mouse", "1");
                content = SetValue(content, "Controls", "bBackground Keyboard", "1");
                content = SetValue(content, "Controls", "bUse Joystick", "0");
            }
            var source = Path.Combine(generated, name);
            await File.WriteAllTextAsync(source, content, token);
            result.Add((source, target));
        }
        return result;
    }

    internal static string SetValue(string content, string section, string key, string value)
    {
        var lines = content.Length == 0 ? [] : content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        var inSection = false;
        var foundSection = false;
        var written = false;
        var output = new List<string>();
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                if (inSection && !written) { output.Add($"{key}={value}"); written = true; }
                inSection = trimmed[1..^1].Equals(section, StringComparison.OrdinalIgnoreCase);
                foundSection |= inSection;
            }
            var equals = trimmed.IndexOf('=');
            if (inSection && equals > 0 && trimmed[..equals].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                if (!written) { output.Add($"{key}={value}"); written = true; }
                continue;
            }
            output.Add(line);
        }
        if (!written)
        {
            if (!foundSection) output.Add($"[{section}]");
            output.Add($"{key}={value}");
        }
        return string.Join("\r\n", output).TrimEnd('\r', '\n') + "\r\n";
    }
}
