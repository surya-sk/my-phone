# Removes My Phone for the current user. Your paired session and message cache in
# %LOCALAPPDATA%\NativeMessages are kept unless you pass -RemoveData.
param([switch]$Quiet, [switch]$RemoveData)
$ErrorActionPreference = 'SilentlyContinue'
$target = Join-Path $env:LOCALAPPDATA 'Programs\Messages'
Add-Type -AssemblyName PresentationFramework

if (-not $Quiet) {
    $answer = [System.Windows.MessageBox]::Show('Remove My Phone from this PC? Your pairing and messages stay on this PC unless you delete %LOCALAPPDATA%\NativeMessages.', 'Uninstall My Phone', 'OKCancel', 'Question')
    if ($answer -ne 'OK') { exit 0 }
}

Get-Process Messages, openmessage |
    Where-Object { $_.Path -and $_.Path.StartsWith($target, [StringComparison]::OrdinalIgnoreCase) } |
    ForEach-Object { $_.CloseMainWindow() | Out-Null; Start-Sleep -Milliseconds 800; if (-not $_.HasExited) { $_ | Stop-Process -Force } }
Get-AppxPackage NativeMessages.App | Remove-AppxPackage
foreach ($lnk in 'My Phone.lnk', 'Messages.lnk') { Remove-Item (Join-Path ([Environment]::GetFolderPath('Programs')) $lnk) -Force }
Remove-Item 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\NativeMessages' -Recurse -Force
if ($RemoveData) { Remove-Item (Join-Path $env:LOCALAPPDATA 'NativeMessages') -Recurse -Force }
# This script lives in the folder being removed; PowerShell has already read it, so deleting is safe.
Remove-Item -LiteralPath $target -Recurse -Force
