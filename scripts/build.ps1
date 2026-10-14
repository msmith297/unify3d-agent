<#
.SYNOPSIS
  Builds the web UI and the SolidWorks add-in.
.EXAMPLE
  .\scripts\build.ps1                 # Debug
  .\scripts\build.ps1 -Configuration Release
#>
param(
    [ValidateSet("Debug", "Release")][string]$Configuration = "Debug",
    # Product version (1.2.3); the updater compares it with the latest GitHub release.
    [string]$Version
)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent

Write-Host "== Building UI" -ForegroundColor Cyan
Push-Location (Join-Path $root "ui")
try {
    if (-not (Test-Path node_modules)) { npm ci; if ($LASTEXITCODE) { throw "npm ci failed" } }
    npm run build; if ($LASTEXITCODE) { throw "UI build failed" }
} finally { Pop-Location }

$out = Join-Path $root "src\SwAgent\bin\$Configuration\net48"

# A DLL that some process still has loaded can't be overwritten, but it can be renamed. This happens when
# SolidWorks is running, and also after it hangs on exit: Windows Error Reporting keeps a frozen SYSTEM-owned
# snapshot of SLDWORKS.exe that holds every loaded DLL and can only be killed as admin. Moving the files
# aside lets the build write fresh copies; the next SolidWorks start loads those. Old copies are removed
# once nothing holds them.
if (Test-Path $out) {
    Get-ChildItem $out -Filter *.old -File | ForEach-Object { Remove-Item $_.FullName -ErrorAction SilentlyContinue }
    $stamp = Get-Date -Format "yyyyMMddHHmmss"
    $moved = @()
    foreach ($f in Get-ChildItem $out -Filter *.dll -File) {
        try { $s = [IO.File]::Open($f.FullName, 'Open', 'ReadWrite', 'None'); $s.Close() }
        catch {
            try {
                Rename-Item $f.FullName "$($f.Name).$stamp.old" -ErrorAction Stop
                $moved += $f.FullName
            }
            catch {
                # A live SolidWorks keeps the add-in open (not just mapped), so nothing can move it. Put back what we moved.
                foreach ($m in $moved) { Rename-Item "$m.$stamp.old" (Split-Path $m -Leaf) -ErrorAction SilentlyContinue }
                throw "$($f.Name) is in use by a running SolidWorks. Close SolidWorks and build again."
            }
            Write-Host "  moved aside (in use): $($f.Name)" -ForegroundColor DarkYellow
        }
    }
}

Write-Host "== Building add-in ($Configuration)" -ForegroundColor Cyan
$msbuild = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\MSBuild.exe" | Select-Object -First 1
if (-not $msbuild) { throw "MSBuild not found. Install the Visual Studio '.NET desktop development' workload." }
& $msbuild (Join-Path $root "src\SwAgent\SwAgent.csproj") -restore -nologo -v:minimal "-p:Configuration=$Configuration" $(if ($Version) { "-p:Version=$Version" })
if ($LASTEXITCODE) { throw "Add-in build failed" }

Write-Host "Built: $out\SwAgent.dll" -ForegroundColor Green
