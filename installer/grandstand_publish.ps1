<#
  Grandstand - build the beta release (run on the OWNER's PC, from the Grandstand git folder). beta (2026-10-24)

  1. dotnet publish (Release, win-x64, self-contained: testers need no .NET installed) into dist\Grandstand
  2. copies the WHOLE assets\ folder next to the exe (the app loads everything from <exe folder>\assets:
     Assets.cs, ControlServer.cs, TrackMapStore.cs) and checks control\ (the control page) is there.
     EXCEPT assets\clock\dial.png: that is the owner's personal dial and is never shipped (2026-10-24);
     the release uses the bundled homage dials (assets\clock\homage_*.png) instead.
     EXCEPT every other PERSONAL asset (2026-10-24, see $Personal below): the IMSA footage rollers,
     the car press photos, the owner's own sponsors, assets\audio, underscore dev folders and fonts
     without a confirmed licence. The release draws its own replacements (procedural class transition,
     drawn car panels, neutral sponsor defaults, assets\audio_public). A final check FAILS the publish
     if any of them is still in the package.
  3. zips it with the installer: dist\Grandstand-<version>-win-x64.zip  (+ dist\Grandstand-SHA256SUMS.txt)

  Usage:
    powershell -NoProfile -ExecutionPolicy Bypass -File installer\grandstand_publish.ps1
    ... -Version 0.1.0-beta.2 -Assets "D:\art\grandstand-assets"
#>
[CmdletBinding()]
param(
    [string]$Repo = '',
    [string]$Out = '',
    [string]$Version = '',
    [string]$Assets = '',
    [string]$Dotnet = 'dotnet'
)
# Windows PowerShell 5.1 can leave $PSScriptRoot empty while reading the parameters, so work it out here
$Here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
if (-not $Repo) { $Repo = Split-Path -Parent $Here }

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
if (-not $Out) { $Out = Join-Path $Repo 'dist' }
if (-not $Assets) { $Assets = Join-Path $Repo 'assets' }
function Step([string]$t) { Write-Host ''; Write-Host "==> $t" -ForegroundColor Cyan }
function Fail([string]$t) { Write-Host ''; Write-Host "BUILD FAILED: $t" -ForegroundColor Red; exit 1 }
function Ok([string]$t)   { Write-Host "    OK  $t" -ForegroundColor Green }

# ---------------------------------------------------------------- personal assets (2026-10-24)
# Paths are relative to the app folder, '/' separated, matched case-insensitively. Keep in step with .gitignore.
$Personal = @(
    @{ Re = '^assets/transitions/frames/';                  Why = 'real IMSA broadcast footage (class-transition rollers) - the release draws its own animated transition' },
    @{ Re = '^assets/logos/sponsors/car_[^/]*_panel\.png$'; Why = 'press photographs of the cars - the release draws its own car panels' },
    @{ Re = '^assets/logos/sponsors/car_bmw_m_hybrid_v8\.png$'; Why = 'press photograph of a car - the release draws its own car panels' },
    # 2026-11-02 (Angelo): his own brands (A&B General Contracting, StormExhibit) ship in the public release's basic theme
    @{ Re = '^assets/logos/sponsors/gofast_logo\.png$';     Why = "the owner's personal sponsor - the release ships neutral sponsor defaults" },
    @{ Re = '^assets/logos/sponsors/tradingpaints_logo\.png$'; Why = "the owner's personal sponsor - the release ships neutral sponsor defaults" },
    @{ Re = '^assets/audio/';                               Why = 'sounds of unconfirmed origin - the release uses assets/audio_public (original, CC0)' },
    @{ Re = '^assets/(.*/)?_[^/]*/';                        Why = 'dev scratch folder (underscore-prefixed)' },
    @{ Re = '^assets/(.*/)?_[^/]*$';                        Why = 'dev scratch file (underscore-prefixed)' },
    @{ Re = '^assets/logos/sponsors/(?!README\.txt$|ab_roof_|storm_radar_|AB_GC_Logo\.png$)[^/]+$'; Why = "sponsor folder is the owner's own partners - the release ships only the README (neutral defaults are drawn)" },
    @{ Re = '^assets/clock/dial\.png$';                     Why = "the owner's personal clock dial - the release uses the homage dials" },
    @{ Re = '^assets/fonts/(.*/)?RaycerRay';                Why = 'font without a confirmed licence - the outro falls back to Barlow Condensed (OFL)' },
    # 2026-10-28: Angelo - the car-maker, series, class, track and iRacing logos SHIP with Grandstand.
    @{ Re = '(^|/)corner_logos/';                           Why = "the owner's corner brand pictures (Michelin, Dunlop, PlayStation, brand lettering) - never shipped" },
    @{ Re = '(^|/)michelin_run/';                           Why = "the owner's Michelin run frames - never shipped" },
    @{ Re = '(^|/)glyphs [^/]+/';                           Why = "brand lettering cut from logos - never shipped" },
    @{ Re = '(^|/)sponsors\.json$';                        Why = "a sponsor pack (per user, in Documents\iRacing\Grandstand) - never bundled" }
)
function Why-Personal([string]$rel) {
    $r = $rel -replace '\\', '/'
    foreach ($p in $Personal) { if ($r -match $p.Re) { return $p.Why } }
    return $null
}
function Remove-Personal([string]$root) {
    $out = @()
    if (-not (Test-Path $root)) { return $out }
    $base = (Resolve-Path $root).Path.TrimEnd('\', '/')
    foreach ($f in @(Get-ChildItem $root -Recurse -File)) {
        $rel = $f.FullName.Substring($base.Length + 1)
        $why = Why-Personal $rel
        if ($why) { Remove-Item -LiteralPath $f.FullName -Force; $out += [pscustomobject]@{ Path = $rel; Why = $why } }
    }
    # empty folders left behind (transitions\frames\gt3, audio, _preview ...)
    Get-ChildItem $root -Recurse -Directory | Sort-Object { $_.FullName.Length } -Descending | ForEach-Object {
        if (@(Get-ChildItem -LiteralPath $_.FullName -Force).Count -eq 0) { Remove-Item -LiteralPath $_.FullName -Force }
    }
    return $out
}
function Assert-NoPersonal([string[]]$rels, [string]$where) {
    $bad = @()
    foreach ($r in $rels) { $why = Why-Personal $r; if ($why) { $bad += "      $r  <- $why" } }
    if ($bad.Count -gt 0) {
        Write-Host ''
        Write-Host "  $($bad.Count) personal file(s) found in $($where):" -ForegroundColor Red
        $bad | Select-Object -First 40 | ForEach-Object { Write-Host $_ -ForegroundColor Red }
        Fail "personal assets must never ship (see the list above; rules in installer\grandstand_publish.ps1 and .gitignore)"
    }
    Ok "final check: no personal assets in $where"
}
function Check-FontLicences([string]$root) {
    # every font that ships needs its licence next to it (fonts\<family>\OFL.txt) or in assets\licenses\OFL-<Family>.txt
    $lic = Join-Path $root 'assets\licenses'
    $missing = @()
    foreach ($f in @(Get-ChildItem (Join-Path $root 'assets\fonts') -Recurse -File -Include '*.ttf', '*.otf' -ErrorAction SilentlyContinue)) {
        $family = ($f.BaseName -split '-')[0]
        $here = @(Get-ChildItem (Join-Path $f.DirectoryName '*') -File -Include 'OFL*.txt', 'LICENSE*' -ErrorAction SilentlyContinue).Count -gt 0
        $there = Test-Path (Join-Path $lic "OFL-$family.txt")
        if (-not ($here -or $there)) { $missing += $f.Name }
    }
    if ($missing.Count -gt 0) { Fail ("font(s) without a licence file: " + (($missing | Select-Object -Unique) -join ', ') + " (add assets\licenses\OFL-<Family>.txt or leave the font out)") }
    Ok 'every bundled font has its licence (SIL OFL 1.1)'
}
function Public-Summary([string]$root) {
    $a = Join-Path $root 'assets'
    function Count([string]$sub, [string]$filter = '*') { @(Get-ChildItem (Join-Path $a $sub) -Recurse -File -Filter $filter -ErrorAction SilentlyContinue).Count }
    Write-Host ''
    Write-Host '  Public assets in this release:' -ForegroundColor Cyan
    Write-Host ("    class transitions : built-in animated (drawn live, no files); users can add their own rollers in Documents\iRacing\Grandstand\transitions")
    Write-Host ("    car panels        : drawn by Grandstand (class colour + maker logo); users can add screenshots in Documents\iRacing\Grandstand\cars")
    Write-Host ("    sounds            : {0} original sounds in assets\audio_public (tools\make_sfx.py, CC0)" -f (Count 'audio_public' '*.wav'))
    Write-Host ("    sponsors          : neutral defaults (YOUR BRAND HERE); {0} file(s) in assets\logos\sponsors" -f (Count 'logos\sponsors'))
    Write-Host ("    fonts             : {0} file(s), licences: {1}" -f (Count 'fonts'), ((@(Get-ChildItem (Join-Path $a 'licenses') -File -ErrorAction SilentlyContinue) | ForEach-Object { $_.Name }) -join ', '))
    Write-Host ("    clock dials       : {0} homage dial(s)" -f (Count 'clock' 'homage_*.png'))
    $logoDirs = @(Get-ChildItem (Join-Path $a 'logos') -Directory -ErrorAction SilentlyContinue | ForEach-Object { "$($_.Name) ($(@(Get-ChildItem $_.FullName -Recurse -File).Count))" })
    Write-Host ("    logos             : " + ($logoDirs -join ', ') + ' (car makers, series, classes, tracks, iRacing - ship with Grandstand, owner decision 2026-10-28)')
    $other = @(Get-ChildItem $a -Directory | Where-Object { $_.Name -notin @('audio_public', 'logos', 'fonts', 'licenses', 'clock') } | ForEach-Object { "$($_.Name) ($(@(Get-ChildItem $_.FullName -Recurse -File).Count))" })
    Write-Host ("    other             : " + ($other -join ', '))
}

$proj = Join-Path $Repo 'src\ApexHUD.Native.GpuHost\ApexHUD.Native.GpuHost.csproj'
if (-not (Test-Path $proj)) { Fail "No GpuHost project at $proj (use -Repo)." }
if (-not $Version) {
    $vf = Join-Path $Repo 'VERSION'
    if (Test-Path $vf) { $Version = "$(Get-Content $vf -TotalCount 1)".Trim() }
    if (-not $Version) { $Version = '0.1.0-beta.1' }
}
$app = Join-Path $Out 'Grandstand'
Write-Host "  Grandstand $Version  ->  $app"

# ---------------------------------------------------------------- 1. publish
Step 'dotnet publish (Release, win-x64, self-contained)'
if (Test-Path $app) { Remove-Item $app -Recurse -Force }
New-Item -ItemType Directory -Force -Path $app | Out-Null
$ErrorActionPreference = 'Continue'
& $Dotnet publish $proj -c Release -r win-x64 --self-contained true -o $app --nologo -v q "-p:Version=$($Version -replace '-.*$', '')" "-p:InformationalVersion=$Version"
$code = $LASTEXITCODE
$ErrorActionPreference = 'Stop'
if ($code -ne 0) { Fail "dotnet publish failed (exit $code). Is the .NET 8 SDK installed? (https://dotnet.microsoft.com/download/dotnet/8.0)" }
$exe = Join-Path $app 'Grandstand.exe'
if (-not (Test-Path $exe)) { Fail "publish finished but $exe is missing" }
Ok 'published'

# ---------------------------------------------------------------- 2. assets + control page
Step "Assets from $Assets"
if (-not (Test-Path (Join-Path $Assets 'clock\homage_ivory.png'))) { Fail "No assets folder at $Assets (use -Assets)." }
$robo = Join-Path $env:SystemRoot 'System32\robocopy.exe'
$ErrorActionPreference = 'Continue'
& $robo $Assets (Join-Path $app 'assets') /E /XF '*.psd' '*.ai' '*.xcf' '*.kra' '*.bak' '*.tmp' 'Thumbs.db' 'desktop.ini' 'car_*_panel.png' 'car_bmw_m_hybrid_v8.png' 'gofast_*' 'tradingpaints_*' '_*' 'RaycerRay*' 'sponsors.json' /XD '_*' 'backup' 'backups' (Join-Path $Assets 'transitions\frames') (Join-Path $Assets 'audio') 'corner_logos' 'michelin_run' /NFL /NDL /NJH /NJS /NP /R:2 /W:2
$code = $LASTEXITCODE
$ErrorActionPreference = 'Stop'
if ($code -ge 8) { Fail "copying the assets failed (robocopy code $code)" }
# personal dial - never ship (dotnet publish also copies it as Content when it is on this PC)
$personalDial = Join-Path $app 'assets\clock\dial.png'
if (Test-Path $personalDial) {
    Remove-Item $personalDial -Force
    Ok 'left out assets\clock\dial.png (personal dial - never shipped; the release uses the homage dials)'
} else {
    Ok 'no personal dial in the build (assets\clock\dial.png) - good'
}
# every other personal asset (2026-10-24). dotnet publish copies some of them as Content when they are on
# this PC, so they are removed from the build here, and checked once more on the finished package below.
$left = @(Remove-Personal $app)
if ($left.Count -gt 0) {
    foreach ($g in ($left | Group-Object Why)) { Ok ("left out {0} file(s): {1}" -f $g.Count, $g.Name) }
} else { Ok 'no personal assets in the build - good' }
Check-FontLicences $app
foreach ($need in @('assets\clock\homage_ivory.png', 'assets\fonts', 'assets\audio_public\story_exit.wav', 'assets\licenses', 'control\index.html')) {
    if (-not (Test-Path (Join-Path $app $need))) {
        if ($need -like 'control*') {
            Copy-Item (Join-Path $Repo 'src\ApexHUD.Native.GpuHost\control') (Join-Path $app 'control') -Recurse -Force
        }
        if (-not (Test-Path (Join-Path $app $need))) { Fail "the build has no $need" }
    }
}
$folders = Get-ChildItem (Join-Path $app 'assets') -Directory | ForEach-Object {
    $n = @(Get-ChildItem $_.FullName -Recurse -File).Count; "$($_.Name) ($n)" }
Ok ("assets: " + ($folders -join ', '))
Public-Summary $app
$sizeMB = [math]::Round(((Get-ChildItem $app -Recurse -File | Measure-Object Length -Sum).Sum) / 1MB, 1)
Ok "dist\Grandstand is $sizeMB MB"

# ---------------------------------------------------------------- 3. package
Step 'Packaging'
$pkgName = "Grandstand-$Version"
# an old zip must not survive a failed check below (2026-10-24)
foreach ($old in @((Join-Path $Out "$pkgName-win-x64.zip"), (Join-Path $Out 'Grandstand-SHA256SUMS.txt'))) { if (Test-Path $old) { Remove-Item $old -Force } }
$stage = Join-Path $Out "_stage\$pkgName"
if (Test-Path (Join-Path $Out '_stage')) { Remove-Item (Join-Path $Out '_stage') -Recurse -Force }
New-Item -ItemType Directory -Force -Path (Join-Path $stage 'installer') | Out-Null
Copy-Item (Join-Path $Here 'grandstand_install.ps1')   (Join-Path $stage 'installer') -Force
Copy-Item (Join-Path $Here 'grandstand_uninstall.ps1') (Join-Path $stage 'installer') -Force
Copy-Item (Join-Path $Here 'Install Grandstand.cmd')    $stage -Force
Set-Content -Path (Join-Path $stage 'VERSION') -Value $Version -Encoding ASCII
$ErrorActionPreference = 'Continue'
& $robo $app (Join-Path $stage 'Grandstand') /E /NFL /NDL /NJH /NJS /NP /R:2 /W:2
$code = $LASTEXITCODE
$ErrorActionPreference = 'Stop'
if ($code -ge 8) { Fail "staging failed (robocopy code $code)" }

# final check (2026-10-24): nothing personal may be in the package. Fails the publish, saying what and why.
Assert-NoPersonal (Get-ChildItem (Join-Path $stage 'Grandstand') -Recurse -File | ForEach-Object {
    $_.FullName.Substring((Join-Path $stage 'Grandstand').TrimEnd('\').Length + 1) }) 'the staged package'

$zipPath = Join-Path $Out "$pkgName-win-x64.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$base = Split-Path -Parent $stage
$fs = [IO.File]::Open($zipPath, [IO.FileMode]::CreateNew)
$zip = New-Object IO.Compression.ZipArchive($fs, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($f in (Get-ChildItem $stage -Recurse -File)) {
        $rel = $f.FullName.Substring($base.TrimEnd('\').Length + 1) -replace '\\', '/'
        $e = $zip.CreateEntry($rel, [IO.Compression.CompressionLevel]::Optimal)
        $es = $e.Open(); $src = [IO.File]::OpenRead($f.FullName)
        try { $src.CopyTo($es) } finally { $src.Dispose(); $es.Dispose() }
    }
} finally { $zip.Dispose(); $fs.Dispose() }
# ... and the zip itself, entry by entry (paths inside it start with Grandstand-<version>/Grandstand/)
$zr = [IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    $prefix = "$pkgName/Grandstand/"
    $entries = @($zr.Entries | Where-Object { $_.FullName.StartsWith($prefix) } | ForEach-Object { $_.FullName.Substring($prefix.Length) })
} finally { $zr.Dispose() }
Assert-NoPersonal $entries 'the zip'
Remove-Item (Join-Path $Out '_stage') -Recurse -Force
$hash = (Get-FileHash -Algorithm SHA256 $zipPath).Hash.ToLower()
[IO.File]::WriteAllText((Join-Path $Out 'Grandstand-SHA256SUMS.txt'), "$hash  $(Split-Path -Leaf $zipPath)`n", [Text.Encoding]::ASCII)
Ok "$zipPath ($([math]::Round((Get-Item $zipPath).Length / 1MB, 1)) MB)"
Write-Host "    sha256 $hash"
Write-Host ''
Write-Host '  Reminder: the logos in assets\logos (car makers, series, classes, iRacing, tracks) are other companies''' -ForegroundColor Yellow
Write-Host '  marks (small badges, accepted for the beta). Footage, car photos, the owner''s sponsors and the old sounds are' -ForegroundColor Yellow
Write-Host '  NOT in this package (checked above).' -ForegroundColor Yellow
exit 0
