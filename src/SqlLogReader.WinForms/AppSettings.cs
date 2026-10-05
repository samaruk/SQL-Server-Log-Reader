using System.Text.Json;

namespace SqlLogReader.WinForms;

/// <summary>Connection details remembered between runs (never the password).</summary>
internal sealed class AppSettings
{
    public string Server { get; set; } = "localhost";
    public bool WindowsAuthentication { get; set; } = true;
    public string Login { get; set; } = "";
    public string Database { get; set; } = "";
    public bool TrustServerCertificate { get; set; } = true;

    private static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SqlLogReader", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // fall back to defaults
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // not important
        }
    }
}
