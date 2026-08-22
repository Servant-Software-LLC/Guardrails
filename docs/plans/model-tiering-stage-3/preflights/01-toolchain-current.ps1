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

# --- 2. the skill copy the JIT BREAKDOWN ACTUALLY INLINES ---------------------------------------
# REWRITTEN after an independent adversarial pass. The first draft checked TWO copies -
# ~/.claude/skills/ and the repo's tracked .claude/skills/ - and the JIT breakdown loads NEITHER.
# WaveBreakdownInvoker.TryLoadPlanBreakdownSkill reads
#     Path.Combine(AppContext.BaseDirectory, "skills", "plan-breakdown", "SKILL.md")
# i.e. the copy bundled BESIDE THE INSTALLED TOOL. Three copies exist on a dev box and they differ.
# A preflight whose stated purpose is "catch a plan-breakdown too old to write breakdown-intent.json"
# while checking neither load-bearing copy is decoration with false-red surface attached, so the two
# old clauses are GONE rather than kept alongside this one.
$store = Join-Path $HOME '.dotnet/tools/.store/servantsoftware.guardrails'
$bundled = @(Get-ChildItem -Path $store -Filter 'SKILL.md' -Recurse -ErrorAction SilentlyContinue |
             Where-Object { $_.FullName -match '[\\/]skills[\\/]plan-breakdown[\\/]SKILL\.md$' })

if ($bundled.Count -lt 1) {
    # Not fatal on its own: a source-built or differently-installed harness has no tool store. Say so
    # rather than failing a run for a layout this clause simply cannot see.
    Write-Output "NOTE: no bundled plan-breakdown skill found under $store - this harness was not installed as a dotnet global tool, so the JIT-breakdown skill copy could not be verified. Clause 1 (tool version) still applies."
} else {
    # Assert the CAPABILITY, not a version: the bundled copy carries no install-time stamp (stamping
    # happens when `skills install` writes to ~/.claude). Measured baseline 2026-08-22: 4 occurrences
    # in the 1.8.0 bundle; a pre-1.8.0 copy has 0, which is exactly the state this exists to catch.
    foreach ($copy in $bundled) {
        $hits = @(Select-String -Path $copy.FullName -Pattern 'breakdown-intent' -SimpleMatch)
        if ($hits.Count -lt 1) {
            $failures += "the bundled plan-breakdown skill at $($copy.FullName) never mentions 'breakdown-intent' - it predates the #385/#402 declare-the-decomposition-first rule. THIS is the copy WaveBreakdownInvoker inlines, so a JIT wave breakdown will not write the salvage manifest and a truncated wave is quarantined wholesale instead of resuming from its valid prefix."
        }
    }
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== toolchain preflight: $($failures.Count) problem(s) - fix these BEFORE the DAG runs ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    exit 1
}
exit 0
