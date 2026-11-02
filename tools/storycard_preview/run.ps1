# Story card layouts preview (#349) - Windows. Renders every story card in classic + the four right-third layouts,
# with the build's assets (fonts, logos, maps) and your Documents\iRacing\Grandstand (sponsors.json, corner_logos -
# your michelin.png). Output: Documents\iRacing\Grandstand\preview_cards\card_layouts_real
#   powershell -ExecutionPolicy Bypass -File tools\storycard_preview\run.ps1 [-Only RaceSoFar,PitStops] [-Layouts classic,flip]
param(
  [string]$GsBin = "$PSScriptRoot\..\..\src\ApexHUD.Native.GpuHost\bin\Release\net8.0-windows",
  [string]$Only = "",
  [string]$Layouts = "",
  [string]$Out = "$([Environment]::GetFolderPath('MyDocuments'))\iRacing\Grandstand\preview_cards\card_layouts_real"
)
$ErrorActionPreference = "Stop"
$GsBin = (Resolve-Path $GsBin).Path
$run = Join-Path $PSScriptRoot "bin\run"
dotnet build "$PSScriptRoot\storycard_preview.csproj" -c Release -v q --nologo "-p:GsBin=$GsBin" -o $run
if ($LASTEXITCODE -ne 0) { throw "build failed" }
if (Test-Path "$run\assets") { Remove-Item "$run\assets" -Recurse -Force }
Copy-Item (Join-Path $GsBin "assets") "$run\assets" -Recurse
New-Item -ItemType Directory -Force -Path $Out | Out-Null
$a = @($Out)
if ($Only) { $a += "--only"; $a += $Only }
if ($Layouts) { $a += "--layouts"; $a += $Layouts }
dotnet "$run\storycard_preview.dll" @a
Write-Host "previews in $Out"
