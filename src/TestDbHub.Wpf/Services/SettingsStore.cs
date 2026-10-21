using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TestDbHub.Core;

namespace TestDbHub.Wpf.Services;

/// <summary>
/// Connection settings in %AppData%\TestDbHub\settings.json. Passwords are
/// encrypted with Windows DPAPI for the current user.
/// </summary>
public static class SettingsStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TestDbHub", "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private sealed class StoredSettings
    {
        public ConnectionSettings Settings { get; set; } = new();
        public string? MySqlPasswordProtected { get; set; }
        public string? HeliosPasswordProtected { get; set; }
    }

    public static ConnectionSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new ConnectionSettings();
            var stored = JsonSerializer.Deserialize<StoredSettings>(File.ReadAllText(FilePath)) ?? new StoredSettings();
            stored.Settings.MySqlPassword = Unprotect(stored.MySqlPasswordProtected);
            stored.Settings.HeliosPassword = Unprotect(stored.HeliosPasswordProtected);
            return stored.Settings;
        }
        catch (Exception ex) when (ex is IOException or JsonException or CryptographicException or UnauthorizedAccessException)
        {
            return new ConnectionSettings();
        }
    }

    public static void Save(ConnectionSettings settings)
    {
        var copy = JsonSerializer.Deserialize<ConnectionSettings>(JsonSerializer.Serialize(settings))!;
        copy.MySqlPassword = "";
        copy.HeliosPassword = "";
        var stored = new StoredSettings
        {
            Settings = copy,
            MySqlPasswordProtected = Protect(settings.MySqlPassword),
            HeliosPasswordProtected = Protect(settings.HeliosPassword),
        };
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(stored, JsonOptions));
    }

    private static string? Protect(string value) =>
        string.IsNullOrEmpty(value) ? null
            : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));

    private static string Unprotect(string? value) =>
        string.IsNullOrEmpty(value) ? ""
            : Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser));
}
