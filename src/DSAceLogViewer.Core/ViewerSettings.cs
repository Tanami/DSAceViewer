using System.Text.Json;

namespace DSAceLogViewer.Core;

public sealed record ViewerSettings
{
    public string? CustomLogPath { get; init; }
    public string Encoding { get; init; } = "Auto";
    public bool ShowLineNumbers { get; init; }
    public bool ShowEntryType { get; init; }
    public bool WordWrap { get; init; } = true;
    public double LogFontSize { get; init; } = 14;
    public string EffectivePath => string.IsNullOrWhiteSpace(CustomLogPath) ? LogDiscovery.FindDefault() : CustomLogPath;
    public static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ZDF", "DSAceLogViewer", "settings.json");

    public static ViewerSettings Load(string? settingsPath = null)
    {
        try
        {
            var settings = JsonSerializer.Deserialize<ViewerSettings>(File.ReadAllText(settingsPath ?? SettingsPath));
            return settings is null ? new() : settings with
            {
                Encoding = settings.Encoding is "Auto" or "UTF-8" or "Windows-1252" ? settings.Encoding : "Auto",
                LogFontSize = double.IsFinite(settings.LogFontSize) ? Math.Clamp(settings.LogFontSize, 10, 24) : 14
            };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    public void Save(string? settingsPath = null)
    {
        var path = settingsPath ?? SettingsPath;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new { CustomLogPath, Encoding, ShowLineNumbers, ShowEntryType, WordWrap, LogFontSize }, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, true);
    }
}

public static class LogDiscovery
{
    public static string FindDefault() => Candidates().FirstOrDefault(File.Exists) ?? Candidates().First();

    public static IEnumerable<string> Candidates()
    {
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var root in new[] { roaming, local })
        {
            foreach (var relative in new[] { "Discovery Freelancer", "Discovery Freelancer\\My Games\\Freelancer", "Discovery Freelancer\\Freelancer", "Discovery", "Freelancer", "Freelancer\\Accts" })
                yield return Path.Combine(root, relative, "DSAce.log");
            // Launcher versions may create folders such as "Discovery Freelancer 5.3.2".
            string[] folders;
            try { folders = Directory.GetDirectories(root, "Discovery Freelancer*").Order(StringComparer.OrdinalIgnoreCase).ToArray(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { folders = []; }
            foreach (var folder in folders)
            {
                yield return Path.Combine(folder, "DSAce.log");
                yield return Path.Combine(folder, "EXE", "DSAce.log");
            }
        }
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        yield return Path.Combine(documents, "My Games", "Freelancer", "DSAce.log");
        yield return Path.Combine(documents, "My Games", "Discovery Freelancer", "DSAce.log");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents", "My Games", "Freelancer", "DSAce.log");
        yield return Path.Combine(AppContext.BaseDirectory, "DSAce.log");
    }
}
