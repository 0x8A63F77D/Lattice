<#
.SYNOPSIS
    Build the portable Windows distributable for Lattice (issue #56):
    a self-contained single-file Lattice.exe, zipped.

.DESCRIPTION
    Publishes a self-contained, single-file executable (no .NET install needed
    on the target) and zips the output as Lattice-<rid>.zip. The .exe icon is
    already embedded via <ApplicationIcon> (#53). UNSIGNED for v1 — no
    Authenticode cert — so users see a SmartScreen "unknown publisher" prompt
    (More info -> Run anyway); documented in packaging/README.md.

.PARAMETER Rid
    .NET runtime identifier. Default: win-x64.

.PARAMETER Version
    Version to stamp into the assembly + zip name. Defaults to $env:LATTICE_VERSION,
    then 0.0.0.

.EXAMPLE
    pwsh packaging/windows/build-zip.ps1 -Rid win-x64 -Version 0.1.0
#>
param(
    [string]$Rid = "win-x64",
    [string]$Version = $env:LATTICE_VERSION
)

$ErrorActionPreference = "Stop"
if ([string]::IsNullOrWhiteSpace($Version)) { $Version = "0.0.0" }

$RepoRoot = (Resolve-Path "$PSScriptRoot/../..").Path
$Project  = Join-Path $RepoRoot "src/Lattice.App/Lattice.App.csproj"
$OutDir   = Join-Path $RepoRoot "artifacts/windows/$Rid"
$Publish  = Join-Path $OutDir "publish"
$Zip      = Join-Path $OutDir "Lattice-$Rid.zip"

Write-Host "==> Publishing Lattice ($Rid, version $Version)"
if (Test-Path $Publish) { Remove-Item -Recurse -Force $Publish }
if (Test-Path $Zip) { Remove-Item -Force $Zip }
New-Item -ItemType Directory -Force -Path $Publish | Out-Null

# Single-file self-contained is the csproj default for a per-RID publish; pass
# it explicitly here so the script is self-describing. Symbol/XML suppression
# lives in the csproj RID-publish group (DebugType=none etc.), so it needn't be
# repeated here.
dotnet publish $Project -c Release -r $Rid --self-contained true `
    -p:PublishSingleFile=true -p:Version=$Version `
    -o $Publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

# SkiaSharp/HarfBuzzSharp ship native .pdb symbols next to their win-x64 .dll.
# These are native package assets (not reference-related files), so the csproj
# publish knobs don't drop them — sweep any stray loose .pdb so the zip carries
# only the runnable single-file exe.
Get-ChildItem -Path $Publish -Filter *.pdb -File -Recurse | Remove-Item -Force

Write-Host "==> Zipping to $Zip"
Compress-Archive -Path (Join-Path $Publish '*') -DestinationPath $Zip -Force

Write-Host "==> Done: $Zip"
