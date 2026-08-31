# catches: a forward carry that does not actually reach the next attempt. After this task, ALL of
#          #554's pins must be green - the four Core ones this task owns (the size-routed recovery
#          choice, the writeScope caveat, the derived ref name, and the silence when there is no
#          patch), plus I5 (the escalation Context), plus the eight stage 2 already turned green. This
#          is the whole EscalationSalvageTests class in both projects, forward.
#
#          The specific wrong implementation it catches is plan section 3.5 clarification 2's: adding ONE
#          MORE PATH BULLET to PromptComposer's existing flat list. That "names it" and changes
#          nothing an agent does - and C1/C2 assert the ROUTING (the size-routed choice and the
#          writeScope caveat), not the presence of a path.
#
#          It also catches the C4 regression in the other direction: an UNGATED call to
#          AppendSalvageSection renders a recovery block for a prior attempt that left no patch, which
#          is the empty-diff noise the plan's section 11 risk table says is worse than silence.
#
# The filter names THIS pair's OWN test class (#455). The plan introduces no plan-wide trait, so the
# class term stands alone - which is shape 3 of the four sanctioned forms, not an omission.
$ErrorActionPreference = 'Continue'

# The run summary the zero-match guard reads is LOCALIZED (a German-culture box prints 'gesamt:' and
# no 'Total:'), which would invert the guard into an unconditional failure. Pin it FIRST (#455).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$filter = 'FullyQualifiedName~EscalationSalvageTests'
$projects = @('tests/Guardrails.Core.Tests', 'tests/Guardrails.Integration.Tests')

# ACCUMULATE: one message per broken project, dumped once at the end.
$failures = @()

foreach ($project in $projects) {
    # NO -v q on a TEST command: it suppresses the whole Error Message / Expected / Actual / Stack
    # Trace block, leaving only "[FAIL] <name>" for the re-emit below to find - which defeats #179 by
    # the flag alone (#462).
    $out = & dotnet test $project --filter $filter --nologo 2>&1
    $testExit = $LASTEXITCODE                     # capture BEFORE any other statement
    $out | ForEach-Object { Write-Output $_ }     # full log first, for the attempt's saved output

    # EXIT CODE FIRST, guard second (#455 forward polarity): a test host that never ran exits NON-zero
    # with no summary at all. Guard-first would swallow that and report "the filter matched ZERO
    # tests - check the class name" - a confident misdiagnosis pointing at the one artifact a retry
    # agent may edit, except here it may NOT (the tests are outside this task's writeScope), so it
    # would send the agent straight into an out-of-scope edit.
    if ($testExit -ne 0) {
        $detail = $out |
            Select-String -Pattern '\[FAIL\]|Error Message:|Assert\.|Exception|Stack Trace:|Expected:|Actual:' |
            ForEach-Object { $_.Line } |
            Select-Object -First 40                # bound the block so it fits the ~60-line tail
        Write-Output ""
        Write-Output "=== $project failure details (re-emitted so they land in the harness feedback tail) ==="
        if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
        else { Write-Output "(no assertion/exception lines matched - inspect the full log above)" }
        $failures += "$project : EscalationSalvageTests is RED. Fix the implementation in your four files - the tests are outside your writeScope and editing one fails the task immediately."
        continue
    }

    # ZERO-MATCH GUARD (#455): exit 0 alone does NOT mean tests passed - a --filter matching nothing,
    # or a malformed one, also exits 0. Key on the EXECUTED count (Passed + Failed); 'Total:' would
    # also count [Skip]ped tests, so a fully-skipped class would clear a Total-keyed guard.
    $ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
            ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
    if ($ran -lt 1) {
        $failures += "$project : exit 0 but ZERO tests executed. The --filter '$filter' matched nothing, is malformed, or every matched test is [Skip]ped - this guardrail certified nothing. The class is task 01's deliverable; if it is genuinely absent, escalate rather than writing it."
    }
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== forward carry: $($failures.Count) project(s) not green ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    exit 1
}
Write-Output "Forward carry green: EscalationSalvageTests passes in both projects - all thirteen #554 pins."
exit 0
