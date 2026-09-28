<#
.SYNOPSIS
  Test and publish AV1 Studio as a self-contained single-file Windows executable (dist\AV1Studio.exe).
.PARAMETER SkipTests
  Publish without running the test suite.
.EXAMPLE
  .\build.ps1
  $env:AV1STUDIO_TEST_TOOLS = "<folder with ab-av1.exe, ffmpeg.exe, ffprobe.exe>"; .\build.ps1   # also runs the end-to-end tests
#>
param([switch]$SkipTests)
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

if (-not $SkipTests) {
    dotnet test AV1Studio.sln -c Release
    if ($LASTEXITCODE -ne 0) { throw "Tests failed" }
}

dotnet publish src\AV1Studio\AV1Studio.csproj -c Release -o dist
if ($LASTEXITCODE -ne 0) { throw "Publish failed" }

$exe = Get-Item dist\AV1Studio.exe
"Published {0} ({1:N1} MB)" -f $exe.FullName, ($exe.Length / 1MB)
