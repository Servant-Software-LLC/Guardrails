# catches: a run of this plan on a harness OLDER than the JIT-durability work it depends on, or on a
#          plan-breakdown skill too old to write state/breakdown-intent.json. Waves 2 and 3 are authored
#          JUST-IN-TIME, and the salvage of a truncated JIT breakdown depends entirely on that manifest:
#          a skill that never writes it turns a truncated wave into a WHOLESALE quarantine, silently,
#          because a skill that does not know about a file cannot warn that it skipped one. GR2064
#          reports a manifest that is present-but-unusable; nothing reports one that was never written.
#          The pair is asserted, not either half: #169 shipped a tool whose published nupkg carried
#          UNSTAMPED skills, so the tool and its skills can disagree while both report success.
$ErrorActionPreference = 'Continue'
$failures = @()
$required = [version]'1.8.0'

# --- 1. the TOOL -------------------------------------------------------------------------------
# Measured baseline 2026-08-22: `guardrails --version` prints exactly "1.8.0" on the maintainer box.
$raw = (& guardrails --version 2>&1 | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($raw)) {
    Write-Output "could not run 'guardrails --version' (exit $LASTEXITCODE). The harness executing this plan must be a released global tool on PATH; install it with 'dotnet tool install -g ServantSoftware.Guardrails'."
    exit 1
}
# Tolerate a prerelease suffix (1.9.0-preview.3) by comparing only the numeric core.
$core = ($raw -split '-')[0].Trim()
$toolVersion = $null
if (-not [version]::TryParse($core, [ref]$toolVersion)) {
    $failures += "'guardrails --version' printed '$raw', whose numeric core '$core' is not a parseable version"
} elseif ($toolVersion -lt $required) {
    $failures += "the harness is $raw, older than the required $required. Waves 2-3 are JIT-authored and depend on the v1.8.0 breakdown-durability work (#385/#402 checkpointed authoring, #489 Ctrl+C quarantine, #469 run rendering, #471 inventory-scoped revert, #472/#488 the per-wave review marker). Run 'dotnet tool update -g ServantSoftware.Guardrails'."
}

# --- 2. the INSTALLED skills, stamped at install time (#169) -----------------------------------
# The two-line `metadata: guardrails-version` block is injected by `guardrails skills install`, so a
# stamp that is absent or behind the tool means the skills on disk are not the ones this tool shipped.
foreach ($skill in @('plan-breakdown', 'guardrails-review', 'guardrails-domain-knowledge')) {
    $path = Join-Path $HOME ".claude/skills/$skill/SKILL.md"
    if (-not (Test-Path $path)) {
        $failures += "installed skill '$skill' not found at $path - run 'guardrails skills install --force', then RESTART the session"
        continue
    }
    $stampLine = Select-String -Path $path -Pattern 'guardrails-version:\s*(\S+)' | Select-Object -First 1
    if (-not $stampLine) {
        $failures += "installed skill '$skill' carries NO 'guardrails-version' stamp - it predates the #169 install-time stamping, so nothing can tell whether it matches the tool. Run 'guardrails skills install --force', then RESTART the session"
        continue
    }
    $stamped = $null
    $stampCore = (($stampLine.Matches[0].Groups[1].Value) -split '-')[0].Trim()
    if (-not [version]::TryParse($stampCore, [ref]$stamped)) {
        $failures += "installed skill '$skill' has an unparseable stamp '$($stampLine.Matches[0].Groups[1].Value)'"
    } elseif ($stamped -lt $required) {
        $failures += "installed skill '$skill' is stamped $stamped, older than the required $required - run 'guardrails skills install --force', then RESTART the session (re-installing without restarting changes the files on disk but NOT the copy a running session already loaded)"
    }
}

# --- 3. the repo's TRACKED skill source knows about the salvage manifest ------------------------
# Whichever copy a JIT breakdown ends up loading, it must know to write state/breakdown-intent.json.
# The tracked source is not install-stamped (stamping happens at install), so assert the CAPABILITY
# rather than a version. Measured baseline 2026-08-22: 4 occurrences in the tracked copy, 4 in the
# installed copy - a pre-1.8.0 copy has 0, which is the state this clause exists to catch.
$tracked = '.claude/skills/plan-breakdown/SKILL.md'
if (Test-Path $tracked) {
    $hits = @(Select-String -Path $tracked -Pattern 'breakdown-intent' -SimpleMatch)
    if ($hits.Count -lt 1) {
        $failures += "the repo's tracked $tracked never mentions 'breakdown-intent' - it predates the #385/#402 declare-the-decomposition-first rule. A JIT wave breakdown that loads THIS copy will not write the salvage manifest, so a truncated wave is quarantined wholesale instead of resuming from its valid prefix."
    }
} else {
    $failures += "$tracked not found - this preflight expects to run against the Guardrails repo itself"
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== toolchain preflight: $($failures.Count) problem(s) - fix these BEFORE the DAG runs ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    exit 1
}
exit 0
