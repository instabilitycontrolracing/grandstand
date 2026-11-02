<#
.SYNOPSIS
  Watch an iRacing replay on the monitor (not in VR) so Grandstand and its replay director can run on it.

.DESCRIPTION
  What iRacing documents (support.iracing.com, "VR setup" article, checked 2026-10-24):
    * VR or monitor is chosen in the iRacing UI - the device icon in the status bar at the top
      (a monitor icon = monitor; a headset icon = OpenXR / Oculus / OpenVR).
    * A start-up pop-up can ask which device to use each time the sim starts (it can be
      switched off, and back on in Settings).
    * There is NO documented command-line switch or app.ini key that forces monitor mode, and
      no documented way to open a .rpy replay file from the command line.
  So this script does NOT try to force anything and NEVER edits iRacing's files. It:
    1. lists your recent replays (Documents\iRacing\replay),
    2. opens the replay folder and starts the iRacing UI if it isn't running,
    3. prints the exact clicks to watch a replay on the monitor,
    4. reminds you to switch Grandstand's replay director on (it is off by default).

  If you ever extend this script to change an iRacing file, it must copy the file to
  <name>.grandstand-backup-<date> first (see Backup-IRacingFile below) - that rule is not optional.

.PARAMETER List
  Only list recent replays, don't start anything.

.PARAMETER Count
  How many recent replays to list (default 10).

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\watch_replay.ps1
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\watch_replay.ps1 -List -Count 20
#>
[CmdletBinding()]
param(
    [switch]$List,
    [int]$Count = 10
)

$ErrorActionPreference = 'Stop'
$docs = [Environment]::GetFolderPath('MyDocuments')
$replayDir = Join-Path $docs 'iRacing\replay'

function Backup-IRacingFile([string]$Path) {
    # Not used today: kept so any future edit of an iRacing file starts with a backup copy.
    if (-not (Test-Path $Path)) { return $null }
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $copy = "$Path.grandstand-backup-$stamp"
    Copy-Item -LiteralPath $Path -Destination $copy
    Write-Host "Backed up $Path -> $copy"
    return $copy
}

function Find-IRacingUi {
    $candidates = @(
        "${env:ProgramFiles(x86)}\iRacing\ui\iRacingUI.exe",
        "$env:ProgramFiles\iRacing\ui\iRacingUI.exe"
    )
    try {
        # the installer records its folder here on most PCs
        $reg = Get-ItemProperty 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue |
            Where-Object { $_.DisplayName -like 'iRacing*' -and $_.InstallLocation } | Select-Object -First 1
        if ($reg) { $candidates = @((Join-Path $reg.InstallLocation 'ui\iRacingUI.exe')) + $candidates }
    } catch { }
    foreach ($c in $candidates) { if ($c -and (Test-Path $c)) { return $c } }
    return $null
}

Write-Host ''
Write-Host 'GRANDSTAND - watch a replay on the monitor' -ForegroundColor Yellow
Write-Host '-------------------------------------------'

# 1. recent replays
if (Test-Path $replayDir) {
    $files = Get-ChildItem -LiteralPath $replayDir -Filter '*.rpy' -File | Sort-Object LastWriteTime -Descending | Select-Object -First $Count
    if ($files) {
        Write-Host "Recent replays in $replayDir :"
        $i = 1
        foreach ($f in $files) {
            '{0,3}. {1:yyyy-MM-dd HH:mm}  {2,7:N0} MB  {3}' -f $i, $f.LastWriteTime, ($f.Length / 1MB), $f.Name | Write-Host
            $i++
        }
    } else { Write-Host "No .rpy replays in $replayDir yet (save one from the replay screen after a race)." }
} else {
    Write-Host "Replay folder not found: $replayDir" -ForegroundColor DarkYellow
    Write-Host 'Replays are saved there when you click "Save replay" after a session.'
}
if ($List) { return }

# 2. open the folder + the iRacing UI
if (Test-Path $replayDir) { Start-Process explorer.exe $replayDir }
$uiRunning = Get-Process -Name 'iRacingUI' -ErrorAction SilentlyContinue
if (-not $uiRunning) {
    $ui = Find-IRacingUi
    if ($ui) { Write-Host "Starting the iRacing UI: $ui"; Start-Process -FilePath $ui }
    else { Write-Host 'Could not find iRacingUI.exe - start iRacing yourself.' -ForegroundColor DarkYellow }
} else { Write-Host 'The iRacing UI is already running.' }

# 3. the manual steps (iRacing has no reliable switch for these)
Write-Host ''
Write-Host 'In iRacing:' -ForegroundColor Cyan
Write-Host '  1. Look at the device icon in the status bar at the top of the iRacing UI.'
Write-Host '     If it shows a HEADSET, click it and choose the MONITOR. (A monitor icon = already right.)'
Write-Host '     If the start-up pop-up asks which device to use, pick Monitor there.'
Write-Host '  2. Open your replays (the Replays section of the UI), or double-click a .rpy in the'
Write-Host '     folder that just opened if your PC opens .rpy files with iRacing.'
Write-Host '  3. Click Watch / play. The sim opens on the monitor in replay mode.'
Write-Host '  4. Want VR back afterwards? Click the same icon and pick your headset again.'
Write-Host ''
Write-Host 'In Grandstand:' -ForegroundColor Cyan
Write-Host '  5. Open the control page (http://127.0.0.1:8790) -> On Air -> switch "Replay director" ON.'
Write-Host '     It is off by default, it lets go if you touch the camera yourself (back after 20 s),'
Write-Host '     and it switches itself off the moment you get in a car.'
Write-Host ''
Write-Host 'This script changed nothing in iRacing.' -ForegroundColor DarkGray
