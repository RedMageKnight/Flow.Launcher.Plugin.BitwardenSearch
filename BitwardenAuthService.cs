using System;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Security;
using System.Net;

namespace Flow.Launcher.Plugin.BitwardenSearch
{
    public class BitwardenAuthService
    {
        public static async Task<bool> LoginWithApiKey(string clientId, SecureString clientSecret)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                
                // First, check if already logged in
                var checkLoginProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "bw",
                        Arguments = "login --check",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };

                checkLoginProcess.Start();
                string checkOutput = await checkLoginProcess.StandardOutput.ReadToEndAsync();
                await checkLoginProcess.WaitForExitAsync(cts.Token);

                if (checkOutput.Trim().Equals("You are logged in!", StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Log("Already logged in to Bitwarden CLI", LogLevel.Info);
                    return true;
                }

                // If not logged in, proceed with login
                var startInfo = new ProcessStartInfo
                {
                    FileName = "bw",
                    Arguments = "login --apikey",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                startInfo.EnvironmentVariables["BW_CLIENTID"] = clientId;
                startInfo.EnvironmentVariables["BW_CLIENTSECRET"] = new NetworkCredential(string.Empty, clientSecret).Password;

                using var loginProcess = new Process { StartInfo = startInfo };

                Logger.Log("Starting Bitwarden CLI login process", LogLevel.Debug);
                loginProcess.Start();

                string output = await loginProcess.StandardOutput.ReadToEndAsync();
                string error = await loginProcess.StandardError.ReadToEndAsync();

                await loginProcess.WaitForExitAsync(cts.Token);

                if (!string.IsNullOrEmpty(error))
                {
                    if (error.Contains("You are already logged in"))
                    {
                        Logger.Log("Already logged in to Bitwarden CLI", LogLevel.Info);
                        return true;
                    }
                    if (error.Contains("TypeError: Cannot read properties of null (reading 'profile')"))
                    {
                        Logger.Log("Bitwarden CLI state error detected. Please reinstall the Bitwarden CLI.", LogLevel.Error);
                        throw new Exception("Bitwarden CLI state error. Please reinstall the CLI.");
                    }
                    Logger.Log("Error occurred during login", LogLevel.Error);
                    return false;
                }

                bool loginSuccessful = output.Contains("You are logged in!");
                Logger.Log(loginSuccessful ? "Login successful" : "Login failed", LogLevel.Info);
                return loginSuccessful;
            }
            catch (OperationCanceledException)
            {
                Logger.Log("Login operation timed out", LogLevel.Error);
                return false;
            }
            catch (Exception ex)
            {
                Logger.LogError("Exception during login process", ex);
                return false;
            }
        }
    }
}