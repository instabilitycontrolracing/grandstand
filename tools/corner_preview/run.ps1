# Windows (Angelo's PC): corner theme previews with the REAL corner_logos (Documents\iRacing\Grandstand\corner_logos) and
# the real assets. Writes tools\corner_preview\out\corner_themes_before_after.png (+ -Each: one PNG per corner/variant).
#   powershell -ExecutionPolicy Bypass -File tools\corner_preview\run.ps1 [-Each]
param([string]$GsBin = "$PSScriptRoot\..\..\src\ApexHUD.Native.GpuHost\bin\Release\net8.0-windows",
      [string]$Assets = "$PSScriptRoot\..\..\src\ApexHUD.Native.GpuHost\bin\Release\net8.0-windows\assets",
      [string]$Out = "$PSScriptRoot\out", [switch]$Each)
$ErrorActionPreference = "Stop"
Push-Location $PSScriptRoot
# the Windows SkiaSharp native comes with the app's own package set; the Linux one in the csproj is ignored on Windows
dotnet build -c Release -v q --nologo "-p:GsBin=$GsBin" -o bin\run | Out-Null
if (Test-Path bin\run\assets) { Remove-Item bin\run\assets -Recurse -Force }
Copy-Item $Assets bin\run\assets -Recurse
$a = @("--out", $Out); if ($Each) { $a += "--each" }
dotnet bin\run\corner_preview.dll @a
Pop-Location
