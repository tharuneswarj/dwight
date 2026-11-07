# WP-5 (Phase 7.0): assemble the Dwight Yak distribution folder and DRY-RUN `yak build` locally.
# This does NOT publish - `yak push` to the Rhino package server is a launch action (Phase 10), by hand.
#
#   Usage:  powershell -File packaging/build-yak.ps1 [-DryRun] [-NoDeploy]
#
# It builds the Release .gha, copies it plus the required loose dependency DLLs, the manifest and its icon into
# artifacts/yak/, stamps the manifest version from Dwight.csproj, and runs `yak build` from the Rhino 8
# that is installed. With -DryRun it prints every path and command it would use and runs none of them
# (no build, no copy, no Yak), so it can be checked while Rhino is open.
#
# LAST STEP, THE DEPLOY: unless -NoDeploy is given, the assembled .gha and DLLs are also copied into
#   %APPDATA%\Grasshopper\Libraries\Dwight\  and each copy is printed. The csproj's own post-build copy is gated off when
# the CI environment variable is 'true' (the gate runs every dotnet command with CI=true), so under CI=true the build
# packaged the .gha and left the Libraries folder stale; this step does not read CI. A CI runner has no Grasshopper and
# must not write to a user profile, so it passes -NoDeploy. A locked .gha (Rhino open) is an error here, not a skip.
#
# TWO TRAPS THIS SCRIPT IS BUILT AROUND (both found the hard way while building the first package):
#   1. Rhino is not always under C:\Program Files. It is found from the registry
#      (HKLM:\SOFTWARE\McNeel\Rhinoceros\8.0\Install\Path), then C:\Program Files\Rhino 8, then RHINO8_DIR.
#   2. THE 2>&1 TRAP. Under Windows PowerShell 5.1, a runner that redirects stderr with 2>&1 turns every line a
#      native command writes to stderr into an ErrorRecord, and with $ErrorActionPreference = 'Stop' the first
#      one ends the script - after a build that had succeeded. So a native command's stderr is never treated
#      as failure here. Only $LASTEXITCODE of `dotnet build` and of Yak.exe is.

param([switch]$DryRun, [switch]$NoDeploy)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$csproj = Join-Path $root 'Dwight\Dwight.csproj'
$dist = Join-Path $root 'artifacts\yak'

# Run a native command so its stderr cannot stop the script. Cmdlets elsewhere keep the preference above.
function Invoke-Native {
  param([string]$File, [string[]]$Arguments)
  $saved = $ErrorActionPreference
  $ErrorActionPreference = 'Continue'
  try { & $File @Arguments | ForEach-Object { Write-Host "$_" } }
  finally { $ErrorActionPreference = $saved }
  return $LASTEXITCODE
}

# Rhino's folder, in the order of trust: the registry, the default install, then RHINO8_DIR.
# Each candidate may be the install root or its System folder (the registry value is the System folder).
function Find-Yak {
  $candidates = @()
  try {
    $reg = (Get-ItemProperty 'HKLM:\SOFTWARE\McNeel\Rhinoceros\8.0\Install' -ErrorAction Stop).Path
    if ($reg) { $candidates += [pscustomobject]@{ Source = 'registry HKLM:\SOFTWARE\McNeel\Rhinoceros\8.0\Install\Path'; Dir = $reg } }
  } catch { }
  $candidates += [pscustomobject]@{ Source = 'default C:\Program Files\Rhino 8'; Dir = 'C:\Program Files\Rhino 8' }
  if ($env:RHINO8_DIR) { $candidates += [pscustomobject]@{ Source = 'RHINO8_DIR'; Dir = $env:RHINO8_DIR } }

  foreach ($c in $candidates) {
    foreach ($rel in @('Yak.exe', 'System\Yak.exe')) {
      $p = Join-Path $c.Dir $rel
      if (Test-Path -LiteralPath $p) { return [pscustomobject]@{ Exe = $p; Source = $c.Source } }
    }
  }
  $envText = if ($env:RHINO8_DIR) { $env:RHINO8_DIR } else { 'not set' }
  throw "Could not find System\Yak.exe: looked in the registry (HKLM:\SOFTWARE\McNeel\Rhinoceros\8.0\Install\Path), in C:\Program Files\Rhino 8, and in RHINO8_DIR ($envText)."
}

# 1. version from the csproj (the single source of truth).
[xml]$proj = Get-Content $csproj
$version = ($proj.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
if (-not $version) { throw "Could not read <Version> from $csproj" }
Write-Host "Dwight version $version"

# 2. find Yak before building anything, so a missing Rhino costs seconds and not a Release build.
$found = Find-Yak
Write-Host "Yak: $($found.Exe)   (found by $($found.Source))"

$binDir = Join-Path $root 'Dwight\bin\Release\net7.0-windows'
$deployDir = Join-Path $env:APPDATA 'Grasshopper\Libraries\Dwight'

if ($DryRun) {
  Write-Host '-DryRun: nothing below is run.'
  Write-Host "  would run:      dotnet build `"$csproj`" -c Release"
  Write-Host "  would clean:    $dist"
  Write-Host "  would copy:     $binDir\*.gha and $binDir\*.dll  ->  $dist"
  Write-Host "  would stamp:    version: $version  into  $(Join-Path $PSScriptRoot 'manifest.yml')  ->  $(Join-Path $dist 'manifest.yml')"
  Write-Host "  would copy:     $(Join-Path $PSScriptRoot 'icon.png')  ->  $(Join-Path $dist 'icon.png')"
  Write-Host "  would run:      `"$($found.Exe)`" build --platform win   (in $dist)"
  if ($NoDeploy) { Write-Host '  would NOT deploy: -NoDeploy.' } else { Write-Host "  would deploy:   $dist\*.gha and $dist\*.dll  ->  $deployDir" }
  Write-Host '  would NOT push: `yak push` is a launch step, by hand.'
  return
}

# 3. clean Release build. Only the exit code counts.
$code = Invoke-Native 'dotnet' @('build', $csproj, '-c', 'Release')
if ($code -ne 0) { throw "Release build failed (dotnet build exit code $code)" }

# 4. assemble the distribution folder: the .gha + the loose runtime DLLs it deploys alongside.
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Path $dist | Out-Null
Copy-Item (Join-Path $binDir '*.gha') $dist
Copy-Item (Join-Path $binDir '*.dll') $dist   # GhJSON + System.Text.Json etc. (Costura embeds the rest)

# 5. the manifest, with the version stamped from the csproj, and the icon its `icon:` line names, beside it.
$manifest = Get-Content (Join-Path $PSScriptRoot 'manifest.yml') -Raw
$manifest = $manifest -replace '(?m)^version:.*$', "version: $version"
Set-Content -Path (Join-Path $dist 'manifest.yml') -Value $manifest -Encoding utf8
Copy-Item (Join-Path $PSScriptRoot 'icon.png') $dist

Write-Host "Assembled Yak distribution folder: $dist"
Get-ChildItem $dist | ForEach-Object { Write-Host "  $($_.Name)" }

# 6. `yak build --platform win`: the distribution tag is rh8_24-win, not -any. Dwight is Windows only (WebView2, WinForms);
# 1.0.0 and 1.0.1 were built as -any, so the package server and food4Rhino listed them for Rhino 8 Mac too (7 November).
# Only the exit code counts.
Write-Host "Running: `"$($found.Exe)`" build --platform win   (in $dist)"
Push-Location $dist
try { $code = Invoke-Native $found.Exe @('build', '--platform', 'win') } finally { Pop-Location }
if ($code -ne 0) { throw "yak build failed (Yak.exe exit code $code)" }
Write-Host 'yak build complete - the .yak package is in artifacts/yak/. NOT pushed (publish is a launch step).'

# 7. the deploy: put the built .gha and its loose DLLs where Grasshopper loads them from, and say what was copied.
if ($NoDeploy) {
  Write-Host '-NoDeploy: the Libraries folder was not touched.'
} else {
  if (-not (Test-Path -LiteralPath $deployDir)) { New-Item -ItemType Directory -Path $deployDir | Out-Null }
  foreach ($f in Get-ChildItem $dist -File | Where-Object { $_.Extension -in '.gha', '.dll' }) {
    $target = Join-Path $deployDir $f.Name
    Copy-Item -LiteralPath $f.FullName -Destination $target -Force -ErrorAction Stop
    $h = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.Substring(0, 16)
    Write-Host ("Deployed {0}  ({1} bytes, sha256 {2})  ->  {3}" -f $f.Name, $f.Length, $h, $deployDir)
  }
}
