<#
.SYNOPSIS
    One-line bootstrap for Guardrails on Windows: install the `guardrails` .NET tool,
    then install its bundled Claude Code skills.

.DESCRIPTION
    Idempotent. Installs ServantSoftware.Guardrails as a global .NET tool (or updates it if
    already installed), then runs `guardrails skills install` to copy the bundled skills
    (plan-breakdown, guardrails-review, guardrails-domain-knowledge) into ~/.claude/skills.

.PARAMETER Source
    Optional. A local folder (e.g. a freshly-packed ./nupkg) to install FROM instead of
    NuGet.org. Used to test a package before it is published. When given, it is passed as
    `--add-source`. When omitted, the tool is installed from NuGet.org with --prerelease.

.EXAMPLE
    irm https://raw.githubusercontent.com/Servant-Software-LLC/Guardrails/master/install.ps1 | iex

.EXAMPLE
    # Test a locally-packed package before publishing:
    dotnet pack src/Guardrails.Cli -c Release -o nupkg
    ./install.ps1 -Source ./nupkg
#>
[CmdletBinding()]
param(
    [string] $Source
)

$ErrorActionPreference = 'Stop'

$PackageId = 'ServantSoftware.Guardrails'

# --- 1. dotnet must be on PATH ------------------------------------------------
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host 'ERROR: the .NET SDK (`dotnet`) was not found on PATH.' -ForegroundColor Red
    Write-Host 'Install the .NET 8 SDK, then re-run this script:'
    Write-Host '    winget install Microsoft.DotNet.SDK.8'
    Write-Host '  or download it from https://dotnet.microsoft.com/download/dotnet/8.0'
    exit 1
}

# --- 2. Build the install/update argument list --------------------------------
# `dotnet tool list -g` exits 0 and lists installed tools; we match our package id
# (case-insensitively) to decide install vs update — this is what makes the script idempotent.
$installed = (dotnet tool list --global 2>$null) -match [regex]::Escape($PackageId)
$verb = if ($installed) { 'update' } else { 'install' }

$toolArgs = @('tool', $verb, '--global', $PackageId, '--prerelease')
if ($Source) {
    # Local-folder (or alternate feed) install — how we test before the package is on NuGet.org.
    $resolved = (Resolve-Path $Source).Path
    $toolArgs += @('--add-source', $resolved)
    Write-Host "Installing $PackageId from source: $resolved"
} else {
    Write-Host "Installing $PackageId from NuGet.org"
}

Write-Host "  dotnet $($toolArgs -join ' ')"
& dotnet @toolArgs
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: 'dotnet tool $verb' failed (exit $LASTEXITCODE)." -ForegroundColor Red
    exit $LASTEXITCODE
}

# --- 3. Install the bundled skills --------------------------------------------
# `guardrails` is now on PATH (the global tools dir is on PATH for new shells; it is also
# reachable in this session via the dotnet tool shim once installed).
# --force so re-running the bootstrap to UPGRADE actually refreshes the deployed skills
# (skills install skips existing folders by default — that would leave stale skills on upgrade).
Write-Host ''
Write-Host 'Installing bundled skills...'
& guardrails skills install --force
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: 'guardrails skills install' failed (exit $LASTEXITCODE)." -ForegroundColor Red
    Write-Host 'If `guardrails` was not found, open a new terminal so the .NET tools dir is on PATH, then run:'
    Write-Host '    guardrails skills install'
    exit $LASTEXITCODE
}

# --- 4. Next steps ------------------------------------------------------------
Write-Host ''
Write-Host 'Guardrails is installed.' -ForegroundColor Green
Write-Host 'Next steps:'
Write-Host '  1. Restart Claude Code (or start a new session) so it picks up the new skills.'
Write-Host '  2. In your work repo, run  /plan-breakdown path/to/your-plan.md  to generate a task folder.'
Write-Host '  3. Then  guardrails run path/to/your-plan  to execute it to green.'
