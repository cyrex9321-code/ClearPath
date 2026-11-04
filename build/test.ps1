<#
.SYNOPSIS
  Restore, build (Release) and run every ClearPath test project. Exits non-zero on any failure.
.EXAMPLE
  powershell -File build/test.ps1
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$solution = Join-Path $root 'ClearPath.slnx'

function Invoke-Step([string] $name, [scriptblock] $body) {
    Write-Host "==> $name" -ForegroundColor Cyan
    & $body
    if ($LASTEXITCODE -ne 0) {
        Write-Host "FAILED: $name (exit code $LASTEXITCODE)" -ForegroundColor Red
        exit $LASTEXITCODE
    }
}

Push-Location $root
try {
    Invoke-Step 'dotnet restore' { dotnet restore $solution }
    Invoke-Step 'dotnet build'   { dotnet build $solution -c $Configuration --no-restore }
    # Microsoft.Testing.Platform runner (enabled in global.json): runs every test project in the solution.
    Invoke-Step 'dotnet test'    { dotnet test --solution $solution -c $Configuration --no-build }

    Write-Host 'All tests passed.' -ForegroundColor Green
    exit 0
}
finally {
    Pop-Location
}
