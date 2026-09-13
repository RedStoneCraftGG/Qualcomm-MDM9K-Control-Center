using System;
using System.IO;
using System.Text.Json;

namespace ModemController
{
    public sealed class AppSettings
    {
        private static readonly object Sync = new();
        private static AppSettings? _current;

        public static AppSettings Current
        {
            get
            {
                lock (Sync)
                    return _current ??= Load();
            }
        }

        public bool StartAtStartup { get; set; }
        public bool MinimizeToTray { get; set; }
        public bool CloseToTray { get; set; }

        private static string SettingsPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Qualcomm MDM9K 4G Control Center",
            "settings.json");

        private static AppSettings Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var json = File.ReadAllText(SettingsPath);
                    return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Settings] Failed to load settings: {ex.Message}");
            }

            return new AppSettings();
        }

        public void Save()
        {
            try
            {
                string? directory = Path.GetDirectoryName(SettingsPath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                string json = JsonSerializer.Serialize(this, new JsonSerializerOptions
                {
                    WriteIndented = true
                });

                File.WriteAllText(SettingsPath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Settings] Failed to save settings: {ex.Message}");
            }
        }
    }
}
