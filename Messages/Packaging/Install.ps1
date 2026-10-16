# Installs My Phone for the current user from an extracted Messages.zip.
# Run: powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Install.ps1
#  1. Trusts the Messages signing certificate for package installs (one admin prompt, TrustedPeople store only).
#  2. Copies the app to %LOCALAPPDATA%\Programs\Messages.
#  3. Installs the Windows App Runtime it needs.
#  4. Registers the signed identity package, which gives the app its Start entry, native taskbar badge and notifications.
#  5. Adds an Apps & Features entry and starts Messages.
$ErrorActionPreference = 'Stop'
$source = $PSScriptRoot
$target = Join-Path $env:LOCALAPPDATA 'Programs\Messages'
$packageName = 'NativeMessages.App'
Add-Type -AssemblyName PresentationFramework

function Fail($message) {
    [System.Windows.MessageBox]::Show($message, 'My Phone setup', 'OK', 'Error') | Out-Null
    exit 1
}

try {
    # --- 1. Certificate ---
    $certPath = Join-Path $source 'Messages.cer'
    $cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 $certPath
    $trusted = Test-Path "Cert:\LocalMachine\TrustedPeople\$($cert.Thumbprint)"
    if (-not $trusted) {
        $import = "Import-Certificate -FilePath '$certPath' -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null"
        try { Start-Process powershell.exe -Verb RunAs -Wait -WindowStyle Hidden -ArgumentList '-NoProfile', '-Command', $import }
        catch { }  # Declined: the app still installs, just without package identity.
        $trusted = Test-Path "Cert:\LocalMachine\TrustedPeople\$($cert.Thumbprint)"
    }

    # --- 2. Files ---
    Get-Process Messages, openmessage -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($target, [StringComparison]::OrdinalIgnoreCase) } |
        ForEach-Object { $_.CloseMainWindow() | Out-Null; Start-Sleep -Milliseconds 800; if (-not $_.HasExited) { $_ | Stop-Process -Force } }
    Get-AppxPackage $packageName -ErrorAction SilentlyContinue | Remove-AppxPackage -ErrorAction SilentlyContinue
    if (Test-Path $target) { Remove-Item -LiteralPath $target -Recurse -Force }
    New-Item -ItemType Directory -Force $target | Out-Null
    Expand-Archive -LiteralPath (Join-Path $source 'App.zip') -DestinationPath $target -Force
    Copy-Item (Join-Path $source 'Uninstall.ps1') $target -Force

    # --- 3. Windows App Runtime (Microsoft-signed, per user; skipped where a newer one is installed) ---
    foreach ($name in 'Microsoft.WindowsAppRuntime.2.msix', 'Microsoft.WindowsAppRuntime.Main.2.msix',
                      'Microsoft.WindowsAppRuntime.Singleton.2.msix', 'Microsoft.WindowsAppRuntime.DDLM.2.msix') {
        $msix = Join-Path $source "Runtime\$name"
        if (-not (Test-Path $msix)) { continue }
        try { Add-AppxPackage -Path $msix -ErrorAction Stop }
        catch {
            # 0x80073D06: a higher version is already installed, which is fine.
            if ($_.Exception.HResult -ne -2147009274 -and $_.Exception.Message -notmatch '0x80073D06') { throw "Windows App Runtime ($name): $($_.Exception.Message)" }
        }
    }

    # --- 4. Identity ---
    $identity = $false
    if ($trusted) {
        try {
            Add-AppxPackage -Path (Join-Path $source 'Messages.Identity.msix') -ExternalLocation $target
            $identity = $true
        } catch { $identityError = $_.Exception.Message }
    }
    $shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'My Phone.lnk'
    Remove-Item (Join-Path ([Environment]::GetFolderPath('Programs')) 'Messages.lnk') -Force -ErrorAction SilentlyContinue
    if (-not $identity) {
        # Without identity there is no package Start entry, so add a classic shortcut.
        $shell = New-Object -ComObject WScript.Shell
        $link = $shell.CreateShortcut($shortcut)
        $link.TargetPath = Join-Path $target 'Messages.exe'
        $link.WorkingDirectory = $target
        $link.Save()
    }

    # --- 5. Apps & Features ---
    $version = (Get-Item (Join-Path $target 'Messages.exe')).VersionInfo.ProductVersion
    $key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\NativeMessages'
    New-Item -Path $key -Force | Out-Null
    $size = [int]((Get-ChildItem $target -Recurse -File | Measure-Object Length -Sum).Sum / 1KB)
    $values = @{
        DisplayName = 'My Phone'; Publisher = 'My Phone'; DisplayVersion = $version
        DisplayIcon = (Join-Path $target 'Messages.exe'); InstallLocation = $target
        UninstallString = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$target\Uninstall.ps1`""
        QuietUninstallString = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$target\Uninstall.ps1`" -Quiet"
    }
    foreach ($name in $values.Keys) { Set-ItemProperty -Path $key -Name $name -Value $values[$name] }
    Set-ItemProperty -Path $key -Name EstimatedSize -Value $size -Type DWord
    Set-ItemProperty -Path $key -Name NoModify -Value 1 -Type DWord
    Set-ItemProperty -Path $key -Name NoRepair -Value 1 -Type DWord

    Start-Process (Join-Path $target 'Messages.exe')
    if (-not $identity) {
        $why = if (-not $trusted) { 'the certificate prompt was declined' } else { $identityError }
        [System.Windows.MessageBox]::Show("My Phone is installed, but without its Windows app identity ($why), so the taskbar badge is off. Run Setup again and accept the prompt to enable it.", 'My Phone setup', 'OK', 'Warning') | Out-Null
    }
}
catch { Fail "My Phone could not be installed: $($_.Exception.Message)" }
