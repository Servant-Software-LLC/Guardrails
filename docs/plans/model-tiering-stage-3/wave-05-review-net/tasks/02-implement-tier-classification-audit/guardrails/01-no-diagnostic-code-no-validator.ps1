# catches: the audit quietly growing into the thing this wave's brief forbids - a validator check and a
#          diagnostic code. The ruling is not a preference: a code is a thing that can FAIL A BUILD, and the
#          harness does not block on a model-quality opinion (DoR 12.6). The prohibition is stated in this
#          task's prompt, and a prose-only prohibition is one an adversarial or merely lazy implementation
#          is free to ignore (#221) - so it is backed here, structurally, on the one file this task owns.
#
#          What each ban actually catches, since none of them is hypothetical: an implementation that
#          "helpfully" returns Diagnostic objects instead of findings so a caller could feed them to
#          validate; one that constructs a PlanValidator to piggy-back on its sweep; and a finding message
#          that cites a GR code, which is subtler and worse than it looks - it tells the reader the harness
#          blocks on this, which is the exact belief the ruling exists to prevent. That last one is why the
#          scan is not narrowed to code positions: a mention inside a message string IS the defect there.
#
# ANCHORED ON A USE, NOT A MENTION (#470/#76). `DiagnosticCodes.` is a member access, `new PlanValidator(`
# and `new Diagnostic(` are constructions - none of them matches the ordinary English this task's prompt
# uses to state the prohibition ("do not allocate a diagnostic code", "do not add a validator check"), so
# the prompt cannot invite the agent to write the very thing that reds it. Comments are stripped first, so
# a note explaining the ruling is never the failure.
#
# NO required-present clause, deliberately, so there is no #478 baseline to record and no collision to
# reconcile: what the implementation must DO is pinned by eleven authored tests, and a source regex
# asserting the same thing would be the proxy the demotion order (#468) exists to remove.
#
# AUTHOR-TIME PROBE (#302/#468): samples/01-no-diagnostic-code-no-validator.probe.ps1 runs this script
# against samples/*.valid.cs (expects 0) and samples/*.invalid.cs (expects non-zero).
$ErrorActionPreference = 'Continue'

$path = 'tests/Guardrails.Core.Tests/ModelTiering/TierClassificationAudit.cs'
if (-not (Test-Path $path -PathType Leaf)) {
    # PRECONDITION: the subject is gone, so every clause below would scan a null.
    Write-Output "$path does not exist - it is 01-author-tests-tier-classification-audit's stub and this task fills it in place. Do not create it somewhere else; if it is genuinely absent from your tree, that is a delivery problem upstream, not a file to re-invent here."
    exit 1
}

# Comments only, never string literals (#470, the two-level rule). A finding message that cites a code is
# itself the defect this file bans, so the literals stay in scope; only the prose that EXPLAINS the ban is
# stripped, and stripping it is what stops this guardrail from firing on its own rationale.
$text = Get-Content -Raw -Path $path
$text = ($text -replace '(?m)^\s*///.*$', '') -replace '(?m)//.*$', ''
$text = $text -replace '(?s)/\*.*?\*/', ''

$banned = @(
    @('\bGR20\d{2}\b',
      'a diagnostic code literal. No code is allocated for this finding and none may be cited: a GR code is a thing that can fail a build, and naming one in a message tells the reader the harness blocks on a model-quality opinion'),
    @('DiagnosticCodes\s*\.',
      'a member access on the diagnostic-code registry. This audit returns findings, not diagnostics - it is advisory by construction and has no path into validate'),
    @('new\s+Diagnostic\s*\(',
      'a Diagnostic being constructed. A finding is not a diagnostic; conflating them is how an advisory opinion ends up able to fail a build'),
    @('new\s+PlanValidator\s*\(',
      'a PlanValidator being constructed. The audit computes over a loaded PlanDefinition and nothing else; reaching for the validator is the first step toward becoming one')
)

$failures = @()
foreach ($ban in $banned) {
    $hits = [regex]::Matches($text, $ban[0])
    if ($hits.Count -gt 0) {
        $failures += "$path contains /$($ban[0])/ ($($hits.Count) occurrence(s)) - $($ban[1])"
    }
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== no-code / no-validator: $($failures.Count) prohibited construct(s) in the audit ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    Write-Output ""
    Write-Output "This wave ships NO validate code and NO diagnostic code - that is a settled ruling of the brief, not a preference to work around. Remove the construct; do not weaken this check."
    exit 1
}
exit 0
