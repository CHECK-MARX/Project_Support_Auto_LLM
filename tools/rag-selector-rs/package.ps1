param(
    [string]$OutputRoot = (Join-Path $PSScriptRoot '..\..\artifacts\rag-selector\win-x64')
)

$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot 'target\release\rag-selector-rs.exe'
if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
    throw "Release executable not found. Run 'cargo build --release' first: $source"
}

$destinationFolder = Join-Path $OutputRoot 'tools\rag-selector'
New-Item -ItemType Directory -Path $destinationFolder -Force | Out-Null
$destination = Join-Path $destinationFolder 'rag-selector-rs.exe'
Copy-Item -LiteralPath $source -Destination $destination -Force
Write-Output $destination
