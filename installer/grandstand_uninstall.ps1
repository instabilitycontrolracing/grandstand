<#
  Grandstand - uninstaller (beta, 2026-10-24)
  Removes the program folder, shortcuts and the Apps & features entry. Asks before deleting your settings.
#>
[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\Grandstand'),
    [switch]$Yes,              # don't ask: removes the program, KEEPS your settings
    [switch]$DeleteUserData
)
Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
$AppName = 'Grandstand'
$InstallDir = [IO.Path]::GetFullPath($InstallDir.TrimEnd('\', '.'))
Set-Location $env:TEMP
function Ask-YesNo([string]$q, [bool]$d = $false) {
    if ($Yes) { return $d }
    $h = '[y/N]'; if ($d) { $h = '[Y/n]' }
    $a = Read-Host "$q $h"; if ([string]::IsNullOrWhiteSpace($a)) { return $d }
    return $a.Trim().ToLower().StartsWith('y')
}

Write-Host ''
Write-Host "  $AppName - uninstall" -ForegroundColor White
Write-Host "  Program folder: $InstallDir"
if (-not (Test-Path (Join-Path $InstallDir 'Grandstand.exe'))) {
    Write-Host "  That folder doesn't look like a $AppName install - nothing was deleted." -ForegroundColor Yellow; exit 1
}
if ($InstallDir -ieq $env:LOCALAPPDATA -or $InstallDir -ieq $env:USERPROFILE -or $InstallDir.Length -lt 8 -or
    $InstallDir -like '*\src\*' -or (Test-Path (Join-Path $InstallDir '.git'))) {
    Write-Host "  Refusing to delete $InstallDir (that is not a folder the installer made)." -ForegroundColor Yellow; exit 1
}
if (-not (Ask-YesNo "  Remove $AppName from this PC?" $true)) { Write-Host '  Nothing was changed.'; exit 0 }

$root = $InstallDir.TrimEnd('\') + '\'
$procs = @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
    try { $_.Path -and $_.Path.StartsWith($root, [StringComparison]::OrdinalIgnoreCase) } catch { $false } })
if ($procs.Count -gt 0) { Write-Host "  Closing $AppName..."; $procs | ForEach-Object { try { Stop-Process -Id $_.Id -Force } catch { } }; Start-Sleep 2 }

$docs = [Environment]::GetFolderPath('MyDocuments')
$userData = @((Join-Path $docs 'iRacing\Grandstand'), (Join-Path $docs 'iRacing\ApexHUD\maps'),
              (Join-Path $env:LOCALAPPDATA 'Grandstand'), (Join-Path $env:LOCALAPPDATA 'ApexHUD\globe')) | Where-Object { Test-Path $_ }
$deleteData = $false
if (@($userData).Count -gt 0) {
    if ($Yes) { $deleteData = [bool]$DeleteUserData }
    else {
        Write-Host ''
        Write-Host '  Your Grandstand settings, sponsor packs, cards and recorded track maps are in:'
        $userData | ForEach-Object { Write-Host "    $_" }
        $deleteData = Ask-YesNo '  Delete them too? Say no if you might install again.' $false
    }
}

$startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
$desktop = [Environment]::GetFolderPath('Desktop')
foreach ($l in @((Join-Path $startMenu "$AppName.lnk"), (Join-Path $startMenu "Uninstall $AppName.lnk"),
                 (Join-Path $startMenu 'Grandstand Control.url'), (Join-Path $desktop "$AppName.lnk"))) {
    if (Test-Path $l) { Remove-Item $l -Force }
}
$key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Grandstand'
if (Test-Path $key) { Remove-Item $key -Recurse -Force }

Write-Host '  Removing the program folder...'
try { Remove-Item $InstallDir -Recurse -Force } catch {
    Start-Sleep 3
    try { Remove-Item $InstallDir -Recurse -Force } catch { Write-Host "  Some files couldn't be removed. Delete this folder by hand: $InstallDir" -ForegroundColor Yellow }
}
if ($deleteData) { $userData | ForEach-Object { Remove-Item $_ -Recurse -Force }; Write-Host '  Your settings were deleted.' }
elseif (@($userData).Count -gt 0) { Write-Host '  Your settings were kept.' }
Write-Host ''
Write-Host "  $AppName has been removed." -ForegroundColor Green
exit 0
