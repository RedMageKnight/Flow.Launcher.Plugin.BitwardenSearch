# Define paths
$flowLauncherPluginsPath = "$env:APPDATA\FlowLauncher\Plugins"
$flowLauncherSettingsPath = "$env:APPDATA\FlowLauncher\Settings\Plugins"
$flowLauncherLogsPath = "$env:APPDATA\FlowLauncher\Logs\1.18.0"
$bitwardenCliPath = "$env:APPDATA\Bitwarden CLI"

# Start logging
Start-Transcript -Path "C:\Temp\pre-build-cleanup.log" -Append

# Remove Bitwarden plugin folder
Get-ChildItem $flowLauncherPluginsPath -Directory | Where-Object { $_.Name -like "Bitwarden-*" } | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
if ($?) { Write-Host "Bitwarden plugin folder removed successfully." }
else { Write-Host "Failed to remove Bitwarden plugin folder or it doesn't exist." }

# Remove Bitwarden settings folder
Remove-Item -Path "$flowLauncherSettingsPath\Flow.Launcher.Plugin.BitwardenSearch" -Recurse -Force -ErrorAction SilentlyContinue
if ($?) { Write-Host "Bitwarden settings folder removed successfully." }
else { Write-Host "Failed to remove Bitwarden settings folder or it doesn't exist." }

# Remove log files
Remove-Item -Path "$flowLauncherLogsPath\*.txt" -Force -ErrorAction SilentlyContinue
if ($?) { Write-Host "Log files removed successfully." }
else { Write-Host "Failed to remove log files or they don't exist." }

# Remove Bitwarden CLI folder
Remove-Item -Path $bitwardenCliPath -Recurse -Force -ErrorAction SilentlyContinue
if ($?) { Write-Host "Bitwarden CLI folder removed successfully." }
else { Write-Host "Failed to remove Bitwarden CLI folder or it doesn't exist." }

# Remove Windows Credential Manager entry
try {
    $credential = Get-StoredCredential -Target "BitwardenFlowPlugin" -ErrorAction Stop
    if ($credential) {
        Remove-StoredCredential -Target "BitwardenFlowPlugin" -ErrorAction Stop
        Write-Host "Credential for BitwardenFlowPlugin removed successfully."
    } else {
        Write-Host "No credential found for BitwardenFlowPlugin."
    }
} catch {
    Write-Host "Failed to remove credential: $_"
    Write-Host "You may need to manually remove the 'BitwardenFlowPlugin' credential from Windows Credential Manager."
}

Write-Host "Cleanup completed."
Stop-Transcript