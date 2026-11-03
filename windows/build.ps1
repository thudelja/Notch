<#
.SYNOPSIS
    Builds SD Backup for Windows, and optionally installs or uninstalls it for the current user.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\build.ps1 -Install
    Runs the tests, builds dist\<runtime>\SDBackup.exe, installs it to %LOCALAPPDATA%\Programs\SD Backup,
    adds a Start menu shortcut and starts it.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\build.ps1
    Builds only.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\build.ps1 -Uninstall
    Removes the app, its shortcut and its "Start with Windows" entry. Your projects and settings are kept.
#>
[CmdletBinding()]
param(
    [switch]$Install,
    [switch]$Uninstall,
    [switch]$SkipTests,
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$installFolder = Join-Path $env:LOCALAPPDATA 'Programs\SD Backup'
$shortcutPath = Join-Path ([Environment]::GetFolderPath('Programs')) 'SD Backup.lnk'

function Stop-SDBackup {
    Get-Process SDBackup -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 500
}

if ($Uninstall) {
    Write-Host '==> Uninstalling SD Backup' -ForegroundColor Cyan
    Stop-SDBackup
    Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'SD Backup' -ErrorAction SilentlyContinue
    Remove-Item $shortcutPath -ErrorAction SilentlyContinue
    Remove-Item $installFolder -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "Removed. Settings and import records are still in $env:APPDATA\SD Backup; delete that folder to remove them too." -ForegroundColor Green
    return
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "The .NET SDK isn't installed. Install it with:  winget install Microsoft.DotNet.SDK.10   then open a new PowerShell window."
}
if (-not $Runtime) {
    $Runtime = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'win-arm64' } else { 'win-x64' }
}

if (-not $SkipTests) {
    Write-Host '==> Running tests' -ForegroundColor Cyan
    dotnet test tests/SDBackup.Core.Tests -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
}

Write-Host "==> Building SD Backup ($Runtime)" -ForegroundColor Cyan
$dist = Join-Path $PSScriptRoot "dist\$Runtime"
dotnet publish src/SDBackup.App/SDBackup.App.csproj -c Release -r $Runtime --self-contained true -p:PublishSingleFile=true -o $dist --nologo
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
$exe = Join-Path $dist 'SDBackup.exe'
Write-Host "Built $exe" -ForegroundColor Green

if ($Install) {
    Write-Host "==> Installing to $installFolder" -ForegroundColor Cyan
    Stop-SDBackup
    New-Item -ItemType Directory -Force -Path $installFolder | Out-Null
    $installedExe = Join-Path $installFolder 'SDBackup.exe'
    Copy-Item $exe $installedExe -Force

    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $installedExe
    $shortcut.WorkingDirectory = $installFolder
    $shortcut.Description = 'Import SD cards into project folders'
    $shortcut.Save()

    Start-Process $installedExe
    Write-Host 'Installed and started. Look for the SD card icon next to the clock (click ^ if Windows has hidden it).' -ForegroundColor Green
}
