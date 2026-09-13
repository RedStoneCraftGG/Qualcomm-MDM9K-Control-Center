using Microsoft.Win32;
using System;
using System.Diagnostics;

namespace ModemController
{
    public static class StartupService
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "Qualcomm MDM9K 4G Control Center";

        public static bool IsEnabled()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
                return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Startup] Failed to read startup state: {ex.Message}");
                return false;
            }
        }

        public static bool SetEnabled(bool enabled)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true)
                    ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);

                if (key == null)
                    return false;

                if (!enabled)
                {
                    key.DeleteValue(ValueName, false);
                    return true;
                }

                string executable = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
                if (string.IsNullOrWhiteSpace(executable))
                    return false;

                key.SetValue(ValueName, $"\"{executable}\" --startup", RegistryValueKind.String);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Startup] Failed to update startup state: {ex.Message}");
                return false;
            }
        }
    }
}
