<#
.SYNOPSIS
    Makes the listening-models downloads (one zip per platform), or sets up a model folder on this PC.

.DESCRIPTION
    The converter uses the listening models when its model folder holds them (docs/cli.md#listening-models,
    docs/adr/0138-model-folder.md). A folder holds the ONNX files exported by tools/onnx-export, ONNX Runtime's native
    library for the system, its licences, and models.txt, which lists each model's SHA-256, the models set and the ONNX
    Runtime version, so the converter can check the folder before using it.

    This script runs on a developer PC, never on a CI or release machine (David, 2026-11-21): it takes ONNX Runtime from
    the local NuGet cache and the models from a folder tools/onnx-export wrote. Attach the zips to a release by hand.

    With -Install it sets up -Source itself for this PC instead: it adds ONNX Runtime, its licences and models.txt next to
    the models there.

.PARAMETER Source
    The folder with the exported models (clap.onnx). Default: LocalApplicationData/Mod2Xgm/models, where
    tools/onnx-export writes and the converter reads.

.PARAMETER OutDir
    Where the zips go. Default: artifacts/models under the repository.

.PARAMETER Rid
    The platforms to make zips for. Default: win-x64, linux-x64, osx-arm64.

.PARAMETER Install
    Set up -Source as a model folder for this PC instead of making zips.

.EXAMPLE
    pwsh tools/models-archive/New-ModelsArchive.ps1 -Install
#>
[CmdletBinding()]
param(
    [string]$Source = (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Mod2Xgm/models'),
    [string]$OutDir,
    [string[]]$Rid = @('win-x64', 'linux-x64', 'osx-arm64'),
    [switch]$Install
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
if (-not $OutDir) { $OutDir = Join-Path $repo 'artifacts/models' }

# The models set and the models it needs, as the converter has them (src/Models/ModelFolder.cs).
$code = Get-Content -Raw (Join-Path $repo 'src/Models/ModelFolder.cs')
if ($code -notmatch 'public const int ModelsSet = (\d+);') { throw 'ModelsSet not found in src/Models/ModelFolder.cs' }
$set = [int]$Matches[1]
$models = @('clap')

# ONNX Runtime: the managed package the converter is built with, and the native package of the same version (the harness
# restores it), from the local NuGet cache.
[xml]$props = Get-Content (Join-Path $repo 'Directory.Packages.props')
$version = ($props.Project.ItemGroup.PackageVersion | Where-Object { $_.Include -eq 'Microsoft.ML.OnnxRuntime.Managed' }).Version
$nuget = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget/packages' }
$ort = Join-Path $nuget "microsoft.ml.onnxruntime/$version"
if (-not (Test-Path $ort)) { throw "ONNX Runtime $version isn't in $nuget; run: dotnet restore tools/Mod2Xgm.Harness" }

$native = @{
    'win-x64'   = 'onnxruntime.dll'
    'linux-x64' = 'libonnxruntime.so'
    'osx-arm64' = 'libonnxruntime.dylib'
}

function Get-Sha256([string]$path) { (Get-FileHash -Algorithm SHA256 $path).Hash.ToLowerInvariant() }

# models.txt, in the converter's format (ModelFolder.ManifestText).
$lines = @(
    "# Mod2Xgm listening models (docs/cli.md#listening-models). Made by tools/models-archive; don't edit.",
    "set $set",
    "onnxruntime $version"
)
foreach ($m in $models) {
    $file = Join-Path $Source "$m.onnx"
    if (-not (Test-Path $file)) { throw "$file is missing: export it with tools/onnx-export" }
    $lines += "model $m $m.onnx $(Get-Sha256 $file)"
}
$manifest = ($lines -join "`n") + "`n"

$notice = @"
Mod2Xgm listening models, set $set
==================================

Unzip into the converter's model folder (docs/cli.md#listening-models in the Mod2Xgm repository).

clap.onnx
  LAION-CLAP's audio encoder (HTSAT, checkpoint 630k-audioset-best), exported to ONNX by tools/onnx-export.
  Code: https://github.com/LAION-AI/CLAP (CC0-1.0). Weights: https://huggingface.co/lukewys/laion_clap.

ONNX Runtime $version ($($native.Values -join ', '))
  https://github.com/microsoft/onnxruntime, MIT licence: LICENSE-onnxruntime.txt and ThirdPartyNotices-onnxruntime.txt.
"@

function Copy-Runtime([string]$rid, [string]$target) {
    $lib = Join-Path $ort "runtimes/$rid/native/$($native[$rid])"
    if (-not (Test-Path $lib)) { throw "$lib is missing from the ONNX Runtime package" }
    Copy-Item $lib $target -Force
    Copy-Item (Join-Path $ort 'LICENSE') (Join-Path $target 'LICENSE-onnxruntime.txt') -Force
    Copy-Item (Join-Path $ort 'ThirdPartyNotices.txt') (Join-Path $target 'ThirdPartyNotices-onnxruntime.txt') -Force
}

if ($Install) {
    $here = if ($IsWindows) { 'win-x64' } elseif ($IsMacOS) { 'osx-arm64' } else { 'linux-x64' }
    Copy-Runtime $here $Source
    Set-Content -NoNewline -Path (Join-Path $Source 'NOTICE.txt') -Value $notice
    Set-Content -NoNewline -Path (Join-Path $Source 'models.txt') -Value $manifest
    Write-Host "Set up ${Source} for ${here}: models set $set, ONNX Runtime $version"
    return
}

New-Item -ItemType Directory -Force $OutDir | Out-Null
foreach ($r in $Rid) {
    if (-not $native.ContainsKey($r)) { throw "Unknown platform $r" }
    $stage = Join-Path ([IO.Path]::GetTempPath()) "mod2xgm-models-$set-$r"
    if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
    New-Item -ItemType Directory $stage | Out-Null
    foreach ($m in $models) { Copy-Item (Join-Path $Source "$m.onnx") $stage }
    Copy-Runtime $r $stage
    Set-Content -NoNewline -Path (Join-Path $stage 'NOTICE.txt') -Value $notice
    Set-Content -NoNewline -Path (Join-Path $stage 'models.txt') -Value $manifest
    $zip = Join-Path $OutDir "mod2xgm-models-set$set-$r.zip"
    if (Test-Path $zip) { Remove-Item -Force $zip }
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal
    Remove-Item -Recurse -Force $stage
    Write-Host "Wrote $zip"
}
