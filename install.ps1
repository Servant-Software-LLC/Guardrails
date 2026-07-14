<#
  install.ps1 — Windows bootstrap for Guardrails (NO .NET required).

  Downloads a prebuilt self-contained binary + bundled skills from the GitHub
  Release, installs under %LOCALAPPDATA%\Programs\Guardrails, adds it to the
  user PATH, and deploys the agent skills.

  Usage:
    irm https://raw.githubusercontent.com/Servant-Software-LLC/Guardrails/master/install.ps1 | iex
    .\install.ps1                  # latest release
    .\install.ps1 1.0.0-preview.1  # specific version
#>
[CmdletBinding()]
param([string]$Version)

$ErrorActionPreference = "Stop"
$Repo    = "Servant-Software-LLC/Guardrails"
$HomeDir = Join-Path $env:LOCALAPPDATA "Programs\Guardrails"

# --- 1. RID (win-x64 today; extend if you publish win-arm64) ----------------
$rid = "win-x64"

# --- 2. resolve version (latest release, prereleases included) --------------
if (-not $Version) {
  $rel = Invoke-RestMethod "https://api.github.com/repos/$Repo/releases" -Headers @{ "User-Agent" = "guardrails-install" }
  $Version = ($rel | Select-Object -First 1).tag_name
}
$Version = $Version -replace '^v',''
$tag   = "v$Version"
$asset = "guardrails-$Version-$rid.zip"
$url   = "https://github.com/$Repo/releases/download/$tag/$asset"
Write-Host "Installing Guardrails $Version ($rid)`n  from $url"

# --- 3. download, verify, extract -------------------------------------------
$tmp = New-Item -ItemType Directory -Path (Join-Path $env:TEMP ([guid]::NewGuid()))
try {
  $zip = Join-Path $tmp $asset
  Invoke-WebRequest $url -OutFile $zip

  try {
    Invoke-WebRequest "$url.sha256" -OutFile "$zip.sha256"
    $expected = (Get-Content "$zip.sha256").Split(" ")[0]
    $actual   = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()
    if ($expected -ne $actual) { throw "checksum mismatch for $asset" }
    Write-Host "  checksum OK"
  } catch { Write-Host "  (checksum skipped)" }

  if (Test-Path $HomeDir) { Remove-Item $HomeDir -Recurse -Force }
  New-Item -ItemType Directory -Path $HomeDir | Out-Null
  Expand-Archive -Path $zip -DestinationPath $HomeDir -Force
} finally { Remove-Item $tmp -Recurse -Force }

# --- 4. PATH ----------------------------------------------------------------
$userPath = [Environment]::GetEnvironmentVariable("Path", "User")
if ($userPath -notlike "*$HomeDir*") {
  [Environment]::SetEnvironmentVariable("Path", "$userPath;$HomeDir", "User")
  $env:Path = "$env:Path;$HomeDir"
  Write-Host "Added $HomeDir to your user PATH (restart shells to pick it up)."
}

# --- 5. deploy skills + next steps ------------------------------------------
Write-Host "`nInstalling bundled skills..."
& (Join-Path $HomeDir "guardrails.exe") skills install --force

Write-Host "`nGuardrails $Version is installed at $HomeDir"
Write-Host "Next steps:"
Write-Host "  1. Restart Claude Code so it picks up the new skills."
Write-Host "  2. In your work repo:  /plan-breakdown path/to/your-plan.md"
Write-Host "  3. Then:               guardrails run path/to/your-plan"
