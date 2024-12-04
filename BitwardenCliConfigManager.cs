using System;
using System.IO;
using System.Diagnostics;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Threading.Tasks;

namespace Flow.Launcher.Plugin.BitwardenSearch
{
    public class BitwardenCliConfig
    {
        public string? BaseUrl { get; set; }
        public string? IdentityUrl { get; set; }
        public string? ApiUrl { get; set; }
        public string? NotificationsUrl { get; set; }
        public string? WebVaultUrl { get; set; }
        public string? IconsUrl { get; set; }
        public string? KeysUrl { get; set; }
    }

    public class BitwardenCliStatus
    {
        [JsonProperty("serverUrl")]
        public string? ServerUrl { get; set; }
        
        [JsonProperty("lastSync")]
        public DateTime? LastSync { get; set; }
        
        [JsonProperty("status")]
        public string? Status { get; set; }
    }

    public class BitwardenCliConfigManager
    {
        private const string DEFAULT_SERVER_URL = "https://vault.bitwarden.com";

        public static async Task<string?> GetCurrentServerUrl()
        {
            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "bw",
                        Arguments = "status",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };

                Logger.Log("Checking current server configuration", LogLevel.Debug);
                process.Start();
                
                var output = await process.StandardOutput.ReadToEndAsync();
                var error = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();

                if (!string.IsNullOrWhiteSpace(error))
                {
                    Logger.Log($"Error getting server status: {error}", LogLevel.Warning);
                    return null;
                }

                if (string.IsNullOrWhiteSpace(output))
                {
                    Logger.Log("No status output received", LogLevel.Warning);
                    return null;
                }

                var status = JsonConvert.DeserializeObject<BitwardenCliStatus>(output);
                return status?.ServerUrl;
            }
            catch (Exception ex)
            {
                Logger.LogError("Failed to get server status", ex);
                return null;
            }
        }

        private static async Task<bool> LogoutIfRequired()
        {
            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "bw",
                        Arguments = "logout",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };

                Logger.Log("Logging out of current session", LogLevel.Info);
                process.Start();
                var error = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();

                // If we get "You are not logged in", that's fine - continue with server change
                if (!string.IsNullOrEmpty(error) && !error.Contains("You are not logged in"))
                {
                    Logger.Log($"Error during logout: {error}", LogLevel.Warning);
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                Logger.LogError("Failed to logout", ex);
                return false;
            }
        }

        private static void StopBitwardenServer()
        {
            try
            {
                var processes = Process.GetProcessesByName("bw");
                foreach (var process in processes)
                {
                    try
                    {
                        if (!process.HasExited)
                        {
                            process.Kill();
                            process.WaitForExit(5000); // Wait up to 5 seconds for the process to exit
                            Logger.Log($"Successfully killed Bitwarden CLI process with PID {process.Id}", LogLevel.Debug);
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError($"Failed to kill Bitwarden CLI process with PID {process.Id}", ex);
                    }
                    finally
                    {
                        process.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("Error while stopping Bitwarden server processes", ex);
            }
        }

        public static async Task<bool> ResetToDefaultServer()
        {
            try
            {
                Logger.Log("Resetting to default Bitwarden server", LogLevel.Info);
                
                // First logout
                if (!await LogoutIfRequired())
                    return false;

                return await ExecuteConfigCommand("server", BitwardenConstants.DEFAULT_SERVER_URL);
            }
            catch (Exception ex)
            {
                Logger.LogError("Failed to reset to default server", ex);
                return false;
            }
        }
        
        public static async Task<bool> ConfigureServer(BitwardenCliConfig config)
        {
            try
            {
                // Stop any running Bitwarden server processes
                StopBitwardenServer();

                // Always logout first before changing server configuration
                if (!await LogoutIfRequired())
                {
                    Logger.Log("Failed to logout before server configuration change", LogLevel.Error);
                    return false;
                }

                // Clear session environment variable
                Environment.SetEnvironmentVariable("BW_SESSION", null);

                if (config.BaseUrl == null)
                {
                    // If BaseUrl is null, reset to default Bitwarden server
                    return await ExecuteConfigCommand("server", BitwardenConstants.DEFAULT_SERVER_URL);
                }

                // Configure custom server
                Logger.Log($"Configuring custom server: {config.BaseUrl}", LogLevel.Info);
                if (!await ExecuteConfigCommand("server", config.BaseUrl))
                    return false;

                // Configure optional URLs if provided
                if (!string.IsNullOrWhiteSpace(config.IdentityUrl))
                    await ExecuteConfigCommand("server.identity", config.IdentityUrl);

                if (!string.IsNullOrWhiteSpace(config.ApiUrl))
                    await ExecuteConfigCommand("server.api", config.ApiUrl);

                if (!string.IsNullOrWhiteSpace(config.NotificationsUrl))
                    await ExecuteConfigCommand("server.notifications", config.NotificationsUrl);

                if (!string.IsNullOrWhiteSpace(config.WebVaultUrl))
                    await ExecuteConfigCommand("server.webVault", config.WebVaultUrl);

                if (!string.IsNullOrWhiteSpace(config.IconsUrl))
                    await ExecuteConfigCommand("server.icons", config.IconsUrl);

                if (!string.IsNullOrWhiteSpace(config.KeysUrl))
                    await ExecuteConfigCommand("server.keys", config.KeysUrl);

                // Ensure there's a delay before any subsequent operations
                await Task.Delay(1000);

                return true;
            }
            catch (Exception ex)
            {
                Logger.LogError("Failed to configure server", ex);
                return false;
            }
        }

        private static async Task<bool> ExecuteConfigCommand(string setting, string value)
        {
            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "bw",
                        Arguments = $"config {setting} {value}",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };

                Logger.Log($"Executing config command: {setting} {value}", LogLevel.Debug);
                process.Start();
                
                var error = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();

                if (!string.IsNullOrEmpty(error))
                {
                    Logger.Log($"Error during config command: {error}", LogLevel.Warning);
                    return false;
                }

                return process.ExitCode == 0;
            }
            catch (Exception ex)
            {
                Logger.LogError($"Failed to execute config command", ex);
                return false;
            }
        }
    }
}