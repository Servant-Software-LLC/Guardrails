# catches: two things, and the second is the one that matters.
#
#   (a) A harness that compiles but still cannot express a prompt-JUDGE guardrail. Today it writes a
#       trivially-passing DETERMINISTIC guardrail per task (01-ok.cmd / exit 0), so every wave-3
#       clause about how a judge resolves is unwritable. Task 06 would then burn its budget
#       discovering that its own dependency did not deliver.
#
#   (b) A harness that reaches for TierResolver to answer the question. THIS IS THE EXPENSIVE ONE.
#       A harness that asks the resolver what it WOULD have chosen produces clauses that PASS against
#       a completely unwired GuardrailRunner - proving the resolver (waves 1-2 already did) and
#       saying nothing about whether anything CALLS it. That is #382's green-light-over-a-broken-wire,
#       and it is the cheapest possible way to turn five red clauses green. Wave 2's own harness-shape
#       guardrail carries the identical prohibition; this task now EDITS that file, and wave 2's
#       guardrail never re-runs - so without this clause the prohibition would be unenforced for the
#       one task able to violate it.
#
# SOUND ABSENCE / PRESENCE ONLY (#468). The positive probes below are absence checks whose FAILURE is
# conclusive: a harness that never names the construct cannot be emitting it. Presence proves nothing
# on its own - task 06's clauses, driven through this harness, are what prove it actually works.
$ErrorActionPreference = 'Continue'
$file = 'tests/Guardrails.Integration.Tests/ModelTiering/Stage2PlanHarness.cs'
$failures = @()

if (-not (Test-Path $file)) {
    Write-Output "$file does not exist - the shared conformance harness is this task's deliverable, and task 06 builds on it"
    exit 1
}
$raw = Get-Content -Raw $file
# Comment- and string-stripped for the PROHIBITION scan (#97/#98 + #470): a name inside a comment or
# a string literal is a mention, not a consultation, and a raw scan would red-flag prose that merely
# EXPLAINS the rule - including the header this task's own prompt asks the author to write.
$code = [regex]::Replace($raw, '/\*[\s\S]*?\*/', '')
$code = [regex]::Replace($code, '(?m)//.*$', '')
$code = [regex]::Replace($code, '"""[\s\S]*?"""', '""')
$code = [regex]::Replace($code, '@"(?:[^"]|"")*"', '""')
$code = [regex]::Replace($code, '"(\\.|[^"\\])*"', '""')

if ($code -cnotmatch 'class\s+Stage2PlanHarness\b') {
    $failures += 'no `class Stage2PlanHarness` declaration - task 06 and its guardrails reference that exact type name'
}

# --- (a) the capability: a real prompt guardrail, and a ledger that distinguishes the judge --------
# Keyed on the FILE EXTENSION the harness must write, which is unambiguous and appears nowhere else
# in a harness that only ever wrote .cmd/.sh guardrails.
if ($raw -cnotmatch '\.prompt\.md') {
    $failures += 'the harness never writes a `.prompt.md` guardrail - it still emits only the deterministic 01-ok.cmd/.sh stub, so a plan spec cannot declare a prompt-JUDGE guardrail and every wave-3 conformance clause is unwritable'
}
if ($code -cnotmatch '(?i)judge') {
    $failures += 'the harness never mentions a judge in real code - the invocation ledger must DISTINGUISH a judge call from the action call and expose the runner/model/effort that carried it, or task 06 has nothing to assert on'
}

# --- (b) the prohibition, fail-on-present over STRIPPED source (#176 negative assertion) -----------
if ($code -cmatch 'TierResolver|TierResolution') {
    $failures += 'the harness CONSULTS TierResolver/TierResolution in real code - FORBIDDEN. Asking the resolver what it would have chosen makes every clause built on this harness PASS against an unwired GuardrailRunner: it proves the resolver, not the wiring. Observe the route through the JOURNAL and the CAPTURED INVOCATION instead. (Explaining the rule in a comment or a string is fine - this scan strips both.)'
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== Stage2PlanHarness judge capability: $($failures.Count) finding(s) ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    Write-Output ""
    Write-Output "This harness is the real-seam host the whole wave rests on: it drives the REAL PlanLoader/TaskExecutor/Scheduler and fakes only IPromptRunner, the process boundary. Extending it is this task's entire deliverable; authoring clauses on top of it is task 06's."
    exit 1
}
exit 0
