# Visual pack preview harness - Windows (2026-10-27 round 2). Renders every item's 4K key frames, a 1080p MP4 (needs
# ffmpeg on the PATH) and the per-frame allocation of each graphic, against the BUILT app and YOUR assets.
#
#   powershell -ExecutionPolicy Bypass -File tools\visual_pack\run.ps1 [-Only W8,V6] [-NoVideo] [-NoAlloc]
#
# Defaults: GsBin = the GpuHost Release output (build it first:
#   dotnet build src\ApexHUD.Native.GpuHost\ApexHUD.Native.GpuHost.csproj -c Release)
# Assets = that build's assets folder (fonts, maps, logos, clock\dial.png = your watch face), Bg = background stills
# (bg_trees.png, bg_crowd.png, bg_pits.png, bg_straight.png - any 3840x2160 TV grabs; plain grey when missing),
# Ibt = the Le Mans race .ibt for W11 (real sun / heading telemetry), Out = Documents\iRacing\Grandstand\preview_r2.
# It reads your real Documents\iRacing\Grandstand (sponsors.json, clock.json, terrain\) - so your sponsors, dial and
# terrain are what you see. V4 needs a partner with a logo file on the "StrategyBoard" spot (control app: Event &
# sponsors); tools\visual_pack\placeholder\orbital_logistics.png is a fictional mark you can point it at to test.
param(
  [string]$GsBin = "$PSScriptRoot\..\..\src\ApexHUD.Native.GpuHost\bin\Release\net8.0-windows",
  [string]$Assets = "",
  [string]$Bg = "",
  [string]$Ibt = "",
  [string]$Out = "$([Environment]::GetFolderPath('MyDocuments'))\iRacing\Grandstand\preview_r2",
  [string]$Only = "",
  [switch]$NoVideo, [switch]$NoAlloc
)
$ErrorActionPreference = "Stop"
$here = $PSScriptRoot
$GsBin = (Resolve-Path $GsBin).Path
if (-not $Assets) { $Assets = Join-Path $GsBin "assets" }
$run = Join-Path $here "bin\run"
dotnet build "$here\visual_pack.csproj" -c Release -v q --nologo "-p:GsBin=$GsBin" -o $run
if ($LASTEXITCODE -ne 0) { throw "harness build failed" }
if (Test-Path "$run\assets") { Remove-Item "$run\assets" -Recurse -Force }
Copy-Item $Assets "$run\assets" -Recurse
$env:OUT = $Out
if ($Bg) { $env:GS_BG = (Resolve-Path $Bg).Path }
if ($Ibt) { $env:GS_IBT = (Resolve-Path $Ibt).Path }
New-Item -ItemType Directory -Force -Path $Out | Out-Null
$a = @()
if ($Only) { $a += "--only"; $a += $Only }
if ($NoVideo) { $a += "--novideo" }
if ($NoAlloc) { $a += "--noalloc" }
dotnet "$run\visual_pack.dll" @a
Write-Host "previews in $Out"
