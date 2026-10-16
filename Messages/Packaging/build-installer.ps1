# Builds dist\MyPhone.zip: extract on another Windows 10/11 x64 PC and run Install.ps1.
#   powershell -ExecutionPolicy Bypass -File Messages\Packaging\build-installer.ps1 [-Version 1.4.0]
# Without -Version, the version comes from Identity\AppxManifest.xml.
# The identity package is signed with a self-signed certificate kept in your CurrentUser\My store
# (created on first run). Only its public .cer ships in the installer.
param([string]$Version)
$ErrorActionPreference = 'Stop'

$project = Split-Path $PSScriptRoot -Parent                     # ...\Messages
$root = Split-Path $project -Parent                             # ...\MessagesClient
$dist = Join-Path $root 'dist'
$stage = Join-Path $dist 'stage'
$resolvedRoot = [IO.Path]::GetFullPath($root).TrimEnd('\') + '\'
$resolvedStage = [IO.Path]::GetFullPath($stage)
if (-not $resolvedStage.StartsWith($resolvedRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to remove a staging directory outside $root"
}
$kits = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' -Directory |
    Where-Object { Test-Path (Join-Path $_.FullName 'x64\makeappx.exe') } | Sort-Object Name | Select-Object -Last 1
if (-not $kits) { throw 'Windows SDK tools (makeappx, makepri, signtool) not found.' }
$tools = Join-Path $kits.FullName 'x64'
if (-not $Version) {
    [xml]$identityManifest = Get-Content (Join-Path $project 'Identity\AppxManifest.xml')
    $Version = ([version]$identityManifest.Package.Identity.Version).ToString(3)
}
$packageVersion = "$Version.0"
$subject = 'CN=NativeMessages'   # Must match Identity\AppxManifest.xml Publisher and app.manifest.

if (Test-Path $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item -ItemType Directory -Force $stage, (Join-Path $stage 'setup') | Out-Null

Write-Host '1/6 Publishing self-contained app...'
$app = Join-Path $stage 'app'
$build = Join-Path $stage 'build'
dotnet publish (Join-Path $project 'Messages.csproj') -c Release -r win-x64 -p:Platform=x64 --self-contained true `
    -p:WindowsAppSDKSelfContained=false -p:Version=$Version -p:BaseOutputPath="$build\" -o $app -nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }
if (-not (Test-Path (Join-Path $app 'Backend\openmessage.exe'))) { throw 'Backend\openmessage.exe missing from publish output.' }
# Unpackaged WinUI publish leaves out the compiled XAML (.xbf) and Messages.pri, without which the
# window can't load (Microsoft.UI.Xaml.dll fails fast, 0xC000027B). Copy them from the publish build.
$built = Join-Path $build 'x64\Release\net8.0-windows10.0.26100.0\win-x64'
if (-not (Test-Path $built)) { throw "Publish build output not found: $built" }
Get-ChildItem $built -Recurse -Include *.xbf, Messages.pri | ForEach-Object {
    $relative = $_.FullName.Substring($built.Length + 1)
    $destination = Join-Path $app $relative
    New-Item -ItemType Directory -Force (Split-Path $destination) | Out-Null
    Copy-Item $_.FullName $destination -Force
}
foreach ($required in 'App.xbf', 'MainWindow.xbf', 'Themes\Metro.xbf', 'Views\CallsView.xbf', 'Messages.pri') {
    if (-not (Test-Path (Join-Path $app $required))) { throw "$required missing from the app payload." }
}

Write-Host '2/6 Preparing identity package...'
# .NET is bundled, but the Windows App SDK must come from its framework package: with package identity,
# Windows ignores a bundled (self-contained) WinUI and the app fails fast at startup (0xC0000602).
$identity = Join-Path $stage 'identity'
Copy-Item (Join-Path $project 'Identity') $identity -Recurse
$manifestPath = Join-Path $identity 'AppxManifest.xml'
$manifest = Get-Content $manifestPath -Raw

$manifest = [regex]::Replace($manifest, '(<Identity [^>]*Version=")[^"]+(")', "`${1}$packageVersion`${2}")
Set-Content -Path $manifestPath -Value $manifest -Encoding UTF8 -NoNewline
Remove-Item (Join-Path $identity 'resources.pri') -ErrorAction SilentlyContinue
$priConfig = Join-Path $stage 'priconfig.xml'
& (Join-Path $tools 'makepri.exe') createconfig /cf $priConfig /dq en-US /o | Out-Null
& (Join-Path $tools 'makepri.exe') new /pr $identity /cf $priConfig /mn $manifestPath /of (Join-Path $identity 'resources.pri') /o | Out-Null
# The installed copy keeps the same manifest and index (Explorer reads the index from the exe folder).
Copy-Item $manifestPath (Join-Path $app 'Identity\AppxManifest.xml') -Force
Copy-Item (Join-Path $identity 'resources.pri') (Join-Path $app 'resources.pri') -Force
Copy-Item (Join-Path $identity 'resources.pri') (Join-Path $app 'Identity\resources.pri') -Force

Write-Host '3/6 Signing certificate...'
$cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object {
    $_.Subject -eq $subject -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date).AddDays(30) -and
    ($_.EnhancedKeyUsageList.ObjectId -contains '1.3.6.1.5.5.7.3.3') } | Sort-Object NotAfter | Select-Object -Last 1
if (-not $cert) {
    $cert = New-SelfSignedCertificate -Type Custom -Subject $subject -FriendlyName 'My Phone package signing' `
        -KeyUsage DigitalSignature -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddYears(5) `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
}
Export-Certificate -Cert $cert -FilePath (Join-Path $stage 'setup\Messages.cer') | Out-Null

Write-Host '4/6 Packing and signing Messages.Identity.msix...'
$msix = Join-Path $stage 'setup\Messages.Identity.msix'
& (Join-Path $tools 'makeappx.exe') pack /d $identity /p $msix /nv /o | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'makeappx failed.' }
& (Join-Path $tools 'signtool.exe') sign /q /fd SHA256 /sha1 $cert.Thumbprint /s My $msix
if ($LASTEXITCODE -ne 0) { throw 'signtool failed.' }

Write-Host '5/6 Bundling Windows App Runtime...'
# Microsoft-signed runtime packages matching the SDK version; Install.ps1 adds them per user.
$runtimeDir = Get-ChildItem (Join-Path $env:USERPROFILE '.nuget\packages\microsoft.windowsappsdk.runtime') -Directory |
    Sort-Object { [version]$_.Name } | Select-Object -Last 1
$runtimeMsix = Get-ChildItem (Join-Path $runtimeDir.FullName 'tools\MSIX\win10-x64') -Filter *.msix
if (-not $runtimeMsix) { throw 'Windows App Runtime MSIX packages not found in the NuGet cache.' }
New-Item -ItemType Directory -Force (Join-Path $stage 'setup\Runtime') | Out-Null
$runtimeMsix | Copy-Item -Destination (Join-Path $stage 'setup\Runtime')
Compress-Archive -Path (Join-Path $app '*') -DestinationPath (Join-Path $stage 'setup\App.zip') -CompressionLevel Optimal
Copy-Item (Join-Path $PSScriptRoot 'Install.ps1'), (Join-Path $PSScriptRoot 'Uninstall.ps1') (Join-Path $stage 'setup')
@'
My Phone for Windows (x64)

Texts (RCS and SMS) and phone calls from your Android phone, on your PC.

Requirements
- Windows 10 version 2004 or newer for messages. Windows 11 for calls.
- An Android phone with Google Messages as its texting app.
- For calls: a PC with Bluetooth.

Install
1. Extract the entire ZIP to a folder.
2. Right-click Install.ps1 > Run with PowerShell. Or open PowerShell in the folder and run:
     powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Install.ps1
3. Accept the administrator prompt. It trusts the included signing certificate so
   Windows gives the app its Start entry, notifications, taskbar badge and calling.
4. My Phone starts.

Set up messages
- On the phone: Google Messages > profile picture > Device pairing > QR code scanner,
  then scan the code My Phone shows.

Set up calls
- Pair the phone with the PC in Settings > Bluetooth & devices > Add device.
- In My Phone choose calls, then pick your phone. Allow the requests on the phone
  (calls, contacts and call history).
- Windows lets one app handle calls per phone. If Phone Link (or another app) already
  does, remove the phone in Bluetooth settings, pair it again, then pick it in My Phone.

The ZIP includes the app, its local backend, .NET and the Windows App Runtime.
No account, messages or contacts are included. The app installs for the current
Windows user in %LOCALAPPDATA%\Programs\Messages.

To remove it: Settings > Apps > Installed apps > My Phone > Uninstall.
'@ | Set-Content -LiteralPath (Join-Path $stage 'setup\README.txt') -Encoding UTF8

Write-Host '6/6 Creating dist\MyPhone.zip...'
$zip = Join-Path $dist 'MyPhone.zip'
Remove-Item $zip -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $stage 'setup\*') -DestinationPath $zip
Write-Host ("Done: {0} ({1:N0} MB)" -f $zip, ((Get-Item $zip).Length / 1MB))
