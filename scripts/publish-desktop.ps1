<#
.SYNOPSIS
  Publishes the Edzio Windows desktop app as a single-file executable.

.DESCRIPTION
  Runs the pre-publish checklist (build + test, and a warning for uncommitted
  changes) and then publishes src/Edzio.Desktop as a single-file exe.

  By default the output is framework-dependent (requires the .NET 10 Desktop
  Runtime on the target machine). Use -SelfContained to bundle the runtime
  (roughly 2x larger, no runtime install needed on the target).

  Symbol files (*.pdb) are removed from the publish folder by default for a
  clean distribution. Use -IncludeSymbols to keep them (useful for crash
  reports).

.PARAMETER Configuration
  Build configuration. Defaults to Release.

.PARAMETER Runtime
  Target runtime identifier. Defaults to win-x64.

.PARAMETER SelfContained
  Bundle the .NET runtime into the output so the target needs no runtime install.

.PARAMETER IncludeSymbols
  Keep *.pdb files in the publish output instead of deleting them.

.PARAMETER SkipTests
  Skip the `dotnet test` step of the pre-publish checklist.

.EXAMPLE
  ./scripts/publish-desktop.ps1

.EXAMPLE
  ./scripts/publish-desktop.ps1 -SelfContained -IncludeSymbols

.EXAMPLE
  ./scripts/publish-desktop.ps1 -SkipTests
#>
[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [switch]$SelfContained,
    [switch]$IncludeSymbols,
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"

# Fixed target framework for the Windows desktop head.
$Framework = "net10.0-windows10.0.19041.0"

# Resolve repo root as the parent of this script's directory so the script
# works regardless of the current working directory.
$repoRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $repoRoot "Edzio.slnx"
$project  = Join-Path $repoRoot "src/Edzio.Desktop/Edzio.Desktop.csproj"

function Write-Step($message) {
    Write-Host ""
    Write-Host "==> $message" -ForegroundColor Cyan
}

# --- Pre-publish checklist -------------------------------------------------

Write-Step "Building solution (dotnet build Edzio.slnx)"
dotnet build $solution -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE." }

if ($SkipTests) {
    Write-Host "Skipping tests (-SkipTests)." -ForegroundColor Yellow
} else {
    Write-Step "Running tests (dotnet test Edzio.slnx)"
    dotnet test $solution -c $Configuration
    if ($LASTEXITCODE -ne 0) { throw "Tests failed with exit code $LASTEXITCODE." }
}

Write-Step "Checking for uncommitted changes"
$gitStatus = git -C $repoRoot status --porcelain
if ($gitStatus) {
    Write-Host "Warning: working tree has uncommitted changes:" -ForegroundColor Yellow
    $gitStatus | ForEach-Object { Write-Host "  $_" -ForegroundColor Yellow }
} else {
    Write-Host "Working tree is clean." -ForegroundColor Green
}

# --- Publish ---------------------------------------------------------------

$selfContainedValue = if ($SelfContained) { "true" } else { "false" }

Write-Step "Publishing Edzio.Desktop ($Framework / $Runtime, self-contained=$selfContainedValue)"
dotnet publish $project `
    -f $Framework `
    -r $Runtime `
    -c $Configuration `
    /p:PublishSingleFile=true `
    --self-contained $selfContainedValue
if ($LASTEXITCODE -ne 0) { throw "Publish failed with exit code $LASTEXITCODE." }

$publishDir = Join-Path $repoRoot "src/Edzio.Desktop/bin/$Configuration/$Framework/$Runtime/publish"

# --- Post-publish ----------------------------------------------------------

if (-not $IncludeSymbols) {
    Write-Step "Removing debug symbols (*.pdb) for clean distribution"
    Get-ChildItem -LiteralPath $publishDir -Filter *.pdb -File -ErrorAction SilentlyContinue |
        ForEach-Object {
            Remove-Item -LiteralPath $_.FullName -Force
            Write-Host "  removed $($_.Name)"
        }
}

Write-Step "Done"
Write-Host "Output: $publishDir" -ForegroundColor Green
if ($SelfContained) {
    Write-Host "Self-contained build: no runtime install required on the target." -ForegroundColor Green
} else {
    Write-Host "Framework-dependent build: target machine needs the .NET 10 Desktop Runtime" -ForegroundColor Yellow
    Write-Host "  https://dotnet.microsoft.com/download/dotnet/10.0" -ForegroundColor Yellow
}
