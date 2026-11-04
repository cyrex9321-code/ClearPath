<#
.SYNOPSIS
  Fails (non-zero exit) if any direct or transitive NuGet package in the solution has a known vulnerability.
  Also fails if the audit itself could not run (for example the NuGet source is unreachable): "no result" is not "no vulnerabilities".
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$solution = Join-Path $root 'ClearPath.slnx'

Write-Host '==> dotnet restore' -ForegroundColor Cyan
dotnet restore $solution
if ($LASTEXITCODE -ne 0) { Write-Host 'restore failed' -ForegroundColor Red; exit $LASTEXITCODE }

Write-Host '==> dotnet list package --vulnerable --include-transitive' -ForegroundColor Cyan
$output = @(dotnet list $solution package --vulnerable --include-transitive 2>&1 | ForEach-Object { "$_" })
$exit = $LASTEXITCODE
$output | ForEach-Object { Write-Host $_ }

if ($exit -ne 0) {
    Write-Host "Vulnerability audit could not complete (exit code $exit)." -ForegroundColor Red
    exit $exit
}

if (@($output | Where-Object { $_ -match 'Unable to load the service index|NU1900|NU1301|error' }).Count -gt 0) {
    Write-Host 'Vulnerability audit reported an error (see output above); treating as failure.' -ForegroundColor Red
    exit 1
}

# `dotnet list package --vulnerable` prints "... has the following vulnerable packages" per affected project,
# followed by table rows that start with "   > <package>".
if (@($output | Where-Object { $_ -match 'has the following vulnerable packages' -or $_ -match '^\s*>\s+\S+' }).Count -gt 0) {
    Write-Host 'Vulnerable packages found.' -ForegroundColor Red
    exit 1
}

Write-Host 'No vulnerable packages found.' -ForegroundColor Green
exit 0
