<#
  Grandstand - beta installer (2026-10-24). No admin rights needed.

    Program folder : %LOCALAPPDATA%\Programs\Grandstand
    Your settings  : Documents\iRacing\Grandstand, Documents\iRacing\ApexHUD\maps,
                     %LOCALAPPDATA%\Grandstand, %LOCALAPPDATA%\ApexHUD   (never touched by this installer)

  Usual way: unzip Grandstand-<version>-win-x64.zip and double-click "Install Grandstand.cmd".
  Run it again with a newer zip to update.

  Options: -Source <folder with Grandstand.exe>  -InstallDir <path>  -NoShortcuts  -Yes
#>
[CmdletBinding()]
param(
    [string]$Source = '',
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\Grandstand'),
    [switch]$NoShortcuts,
    [switch]$Yes
)
# Windows PowerShell 5.1 can leave $PSScriptRoot empty while reading the parameters, so work it out here
$Here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
if (-not $Source) { $Source = Join-Path (Split-Path -Parent $Here) 'Grandstand' }

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
$AppName = 'Grandstand'
$ExeName = 'Grandstand.exe'

New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
$LogFile = Join-Path $InstallDir 'install_log.txt'
Add-Content -Path $LogFile -Value ("`r`n===== install started {0} (from {1}) =====" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Source)
function Write-Log([string]$t) { Add-Content -Path $LogFile -Value ("{0}  {1}" -f (Get-Date -Format 'HH:mm:ss'), $t) }
function Step([string]$t) { Write-Host ''; Write-Host "==> $t" -ForegroundColor Cyan; Write-Log "==> $t" }
function Ok([string]$t)   { Write-Host "    OK  $t" -ForegroundColor Green; Write-Log "OK  $t" }
function Warn([string]$t) { Write-Host "    !!  $t" -ForegroundColor Yellow; Write-Log "WARN  $t" }
function Fail([string]$t) { Write-Host ''; Write-Host "STOPPED: $t" -ForegroundColor Red; Write-Log "FAILED  $t"; Write-Host "Log: $LogFile"; exit 1 }
function Ask-YesNo([string]$q, [bool]$d = $false) {
    if ($Yes) { return $d }
    $h = '[y/N]'; if ($d) { $h = '[Y/n]' }
    $a = Read-Host "$q $h"; if ([string]::IsNullOrWhiteSpace($a)) { return $d }
    return $a.Trim().ToLower().StartsWith('y')
}
function New-Shortcut([string]$Path, [string]$Target, [string]$Arguments, [string]$WorkDir, [string]$Icon, [string]$Description) {
    $ws = New-Object -ComObject WScript.Shell
    $s = $ws.CreateShortcut($Path)
    $s.TargetPath = $Target; $s.Arguments = $Arguments; $s.WorkingDirectory = $WorkDir
    if ($Icon) { $s.IconLocation = "$Icon,0" }
    $s.Description = $Description; $s.Save()
}

Write-Host ''
Write-Host "  $AppName - beta installer" -ForegroundColor White
Write-Host "  Installing to: $InstallDir"
if (-not (Test-Path (Join-Path $Source $ExeName))) { Fail "Can't find $ExeName in $Source. Unzip the whole Grandstand zip first, then run the installer from it." }
$Version = 'unknown'
$vf = Join-Path (Split-Path -Parent $Here) 'VERSION'
if (Test-Path $vf) { $Version = "$(Get-Content $vf -TotalCount 1)".Trim() }
Write-Host "  Version:       $Version"

# 1. close a running copy (only the one in the program folder)
$root = $InstallDir.TrimEnd('\') + '\'
$procs = @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
    try { $_.Path -and $_.Path.StartsWith($root, [StringComparison]::OrdinalIgnoreCase) } catch { $false } })
if ($procs.Count -gt 0) {
    if (Ask-YesNo "  $AppName is running. Close it now?" $true) { $procs | ForEach-Object { try { Stop-Process -Id $_.Id -Force } catch { } }; Start-Sleep 2 }
    else { Fail "Close $AppName and run the installer again." }
}

# 2. copy (mirror: an update removes files the new version no longer has; the uninstaller and log stay)
Step 'Copying Grandstand'
$robo = Join-Path $env:SystemRoot 'System32\robocopy.exe'
$ErrorActionPreference = 'Continue'
& $robo $Source $InstallDir /MIR /XF install_log.txt uninstall.ps1 'Uninstall Grandstand.cmd' VERSION /NFL /NDL /NJH /NJS /NP /R:2 /W:2 2>&1 | ForEach-Object { Add-Content $LogFile "      $_" }
$code = $LASTEXITCODE
$ErrorActionPreference = 'Stop'
if ($code -ge 8) { Fail "Copying failed (robocopy code $code) - is Grandstand still open?" }
Set-Content -Path (Join-Path $InstallDir 'VERSION') -Value $Version -Encoding ASCII
Ok 'files copied'

# 3. uninstaller, shortcuts, Apps & features entry
Step 'Shortcuts and uninstaller'
$exe = Join-Path $InstallDir $ExeName
Copy-Item (Join-Path $Here 'grandstand_uninstall.ps1') (Join-Path $InstallDir 'uninstall.ps1') -Force
$uninstCmd = Join-Path $InstallDir 'Uninstall Grandstand.cmd'
$cmdText = "@echo off`r`n" +
           "rem Copies the uninstaller out of the program folder first, so the whole folder can be removed.`r`n" +
           "copy /y `"%~dp0uninstall.ps1`" `"%TEMP%\gs_uninstall.ps1`" >nul`r`n" +
           "cd /d `"%TEMP%`" & powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"%TEMP%\gs_uninstall.ps1`" -InstallDir `"%~dp0.`" & pause & exit /b`r`n"
[IO.File]::WriteAllText($uninstCmd, $cmdText, [Text.Encoding]::ASCII)

$startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
$desktop = [Environment]::GetFolderPath('Desktop')
if (-not $NoShortcuts) {
    New-Shortcut (Join-Path $startMenu "$AppName.lnk") $exe '' $InstallDir $exe 'Grandstand - TV-style broadcast graphics for iRacing'
    New-Shortcut (Join-Path $desktop "$AppName.lnk")   $exe '' $InstallDir $exe 'Grandstand - TV-style broadcast graphics for iRacing'
    New-Shortcut (Join-Path $startMenu "Uninstall $AppName.lnk") $uninstCmd '' $env:TEMP '' "Remove $AppName"
    # 2026-11-05: Grandstand opens its own app window now - the old browser shortcut to the control page is removed
    Remove-Item (Join-Path $startMenu 'Grandstand Control.url') -Force -ErrorAction SilentlyContinue
    Ok 'Start Menu and Desktop shortcuts made'
}
try {
    $key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Grandstand'
    New-Item -Path $key -Force | Out-Null
    Set-ItemProperty $key DisplayName $AppName
    Set-ItemProperty $key DisplayVersion $Version
    Set-ItemProperty $key Publisher 'Grandstand'
    Set-ItemProperty $key InstallLocation $InstallDir
    Set-ItemProperty $key DisplayIcon $exe
    Set-ItemProperty $key UninstallString "`"$uninstCmd`""
    Set-ItemProperty $key NoModify 1 -Type DWord
    Set-ItemProperty $key NoRepair 1 -Type DWord
    Ok 'listed in Settings > Apps'
} catch { Warn "Couldn't add the Apps & features entry: $($_.Exception.Message)" }

# 4. Trading Paints (2026-11-05): needed for the real car liveries on the graphics - Grandstand runs without it
Step 'Trading Paints (needed for some features)'
$tp = $null
try {
    $tp = Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*', 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*', 'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue |
          Where-Object { $_.PSObject.Properties['DisplayName'] -and "$($_.DisplayName)" -match 'Trading Paints' } | Select-Object -First 1
} catch { }
$tpNeeds = @(
    'the real car liveries in the battle box',
    'the starting grid''s front-row hero cards and the pre-race cards',
    'the car pictures on story cards and the pit lane card',
    'the car in the onboard view'
)
if ($tp) {
    Ok 'Trading Paints is installed - real car liveries will show on the graphics'
} else {
    Warn 'Trading Paints is NOT installed. Grandstand works without it, but these show plain cars instead of real liveries:'
    foreach ($n in $tpNeeds) { Write-Host "          - $n" -ForegroundColor Yellow; Write-Log "      needs Trading Paints: $n" }
    Write-Host '          Get it free at https://www.tradingpaints.com and keep it running while you race.' -ForegroundColor Yellow
    if (-not $Yes -and (Ask-YesNo '  Open the Trading Paints download page now?' $false)) { Start-Process 'https://www.tradingpaints.com/install' }
}

Write-Host ''
Write-Host "  All done. Start '$AppName' from the Desktop or the Start Menu." -ForegroundColor Green
Write-Host '  It opens in its own window. To control it from another screen or a tablet, open http://127.0.0.1:8790/ there.'
Write-Log '===== install finished ====='
exit 0
