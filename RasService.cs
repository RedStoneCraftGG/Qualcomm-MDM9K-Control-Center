using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace ModemController
{
    public class RasService
    {
        public static RasService Instance { get; } = new RasService();
        public string ProfileName { get; set; } = "Modem-4G";


        public string GetPbkFilePath()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string folder = Path.Combine(appData, "ModemController");
            Directory.CreateDirectory(folder);
            return Path.Combine(folder, "modem.pbk");
        }


        public async Task<bool> IsProfileExistsAsync()
        {
            return await Task.Run(() =>
            {
                string pbkPath = GetPbkFilePath();
                if (File.Exists(pbkPath))
                {
                    string content = File.ReadAllText(pbkPath);
                    if (content.Contains($"[{ProfileName}]")) return true;
                }

                try
                {
                    var psi = new ProcessStartInfo("rasdial")
                    {
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using var proc = Process.Start(psi);
                    if (proc != null)
                    {
                        string output = proc.StandardOutput.ReadToEnd();
                        proc.WaitForExit();
                        return output.Contains(ProfileName);
                    }
                }
                catch { }

                return false;
            });
        }


        public string GetSavedPhoneNumber()
        {
            try
            {
                string pbkPath = GetPbkFilePath();
                if (File.Exists(pbkPath))
                {
                    string content = File.ReadAllText(pbkPath);
                    var match = Regex.Match(content, @"DialNumber=(.+)");
                    if (match.Success)
                    {
                        return match.Groups[1].Value.Trim();
                    }
                }
            }
            catch { }

            return "*99***1#";
        }


        public async Task<(bool Success, string Message)> RecreateProfileDetailedAsync(string phoneNumber = "*99***1#")
        {
            return await Task.Run(() =>
            {
                try
                {
                    DeleteProfileInternal();


                    string psScript = $"Add-RasPhonebookEntry -Name '{ProfileName}' -PhoneNumber '{phoneNumber}' -DeviceType Modem -ErrorAction Stop";

                    var psi = new ProcessStartInfo("powershell", $"-NoProfile -ExecutionPolicy Bypass -Command \"{psScript}\"")
                    {
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    using var proc = Process.Start(psi);
                    proc?.WaitForExit();

                    if (proc != null && proc.ExitCode == 0)
                    {
                        return (true, $"Profile updated ({phoneNumber})");
                    }


                    string pbkPath = GetPbkFilePath();
                    string pbkContent = $@"[{ProfileName}]
Encoding=1
PBVersion=6
Type=1
AutoLogon=0
UseCountryAndAreaCodes=0
DialNumber={phoneNumber}
Device=modem
";
                    File.WriteAllText(pbkPath, pbkContent);
                    return (true, $"Profile updated via Local PBK ({phoneNumber})");
                }
                catch (Exception ex)
                {
                    return (false, $"Failed to create profile: {ex.Message}");
                }
            });
        }

        private void DeleteProfileInternal()
        {
            try
            {
                var psi = new ProcessStartInfo("rasdial", $"\"{ProfileName}\" /DELETE")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                proc?.WaitForExit();
            }
            catch { }
        }


        public async Task<(bool Success, string Message)> ConnectAsync()
        {
            return await Task.Run(() =>
            {
                try
                {
                    string pbkPath = GetPbkFilePath();


                    var psi = new ProcessStartInfo("rasdial", $"\"{ProfileName}\"")
                    {
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    using var proc = Process.Start(psi);
                    if (proc == null) return (false, "Failed to start rasdial.");

                    string output = proc.StandardOutput.ReadToEnd();
                    string error = proc.StandardError.ReadToEnd();
                    proc.WaitForExit();


                    if (proc.ExitCode != 0 && (output.Contains("623") || error.Contains("623")) && File.Exists(pbkPath))
                    {
                        var fallbackPsi = new ProcessStartInfo("rasdial", $"\"{ProfileName}\" /PHONEBOOK:\"{pbkPath}\"")
                        {
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };

                        using var fallbackProc = Process.Start(fallbackPsi);
                        if (fallbackProc != null)
                        {
                            output = fallbackProc.StandardOutput.ReadToEnd();
                            error = fallbackProc.StandardError.ReadToEnd();
                            fallbackProc.WaitForExit();

                            if (fallbackProc.ExitCode == 0 || output.Contains("Command completed successfully") || output.Contains("Successfully connected"))
                            {
                                return (true, "Connected to the Internet!");
                            }
                        }
                    }

                    if (proc.ExitCode == 0 || output.Contains("Command completed successfully") || output.Contains("Successfully connected"))
                    {
                        return (true, "Connected to the Internet!");
                    }

                    string fullErr = string.IsNullOrWhiteSpace(error) ? output : error;
                    return (false, fullErr.Trim());
                }
                catch (Exception ex)
                {
                    return (false, $"Error Rasdial: {ex.Message}");
                }
            });
        }


        public async Task<bool> DisconnectAsync(ModemAtService modemService)
        {
            return await Task.Run(async () =>
            {
                try
                {
                    string pbkPath = GetPbkFilePath();


                    string args = File.Exists(pbkPath)
                        ? $"\"{ProfileName}\" /DISCONNECT /PHONEBOOK:\"{pbkPath}\""
                        : $"\"{ProfileName}\" /DISCONNECT";

                    var psi = new ProcessStartInfo("rasdial", args)
                    {
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    using (var proc = Process.Start(psi))
                    {
                        proc?.WaitForExit();
                    }


                    var forcePsi = new ProcessStartInfo("rasdial", "/DISCONNECT")
                    {
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    using (var forceProc = Process.Start(forcePsi))
                    {
                        forceProc?.WaitForExit();
                    }
                }
                catch { }


                await Task.Delay(300);
                if (modemService != null)
                {
                    await modemService.ForceHardwareHangupAsync();
                }

                return true;
            });
        }
    }
}
