# catches: a journal change that is not actually ADDITIVE. Three shapes:
#            1. a recorder parameter that is not optional, breaking every existing call site;
#            2. the new field serialized unconditionally, so an unedited run's run.json is no longer
#               byte-identical - which breaks P10, breaks section 6.3's "gate fired => field present,
#               gate silent => field absent" rule, and (via section 6.6) makes the drift-accept refusal
#               fire for ordinary editor-artifact drift, which section 12 puts explicitly OUT of scope;
#            3. a change to the EXISTING DefinitionHash property or its preserve-on-null idiom, which
#               would move recorded hashes and owe the migration wave this plan is designed to avoid.
#
#          Section 6.3 is precise about the trigger and an earlier draft got it wrong three different
#          ways in three sections: "Its presence is driven by the GATE VERDICT, never by hash
#          inequality." Keyed on inequality, a stray .DS_Store writes the field on a green, delivering
#          run - and section 6.6's drift-accept refusal then keys off it and fires for ordinary artifact
#          drift. The field's WRITER is stage 13; this stage only makes it possible.
#
#          Re-emits the assertion/exception lines at the END so they reach the harness retry-feedback
#          tail (#179).
$ErrorActionPreference = 'Continue'

# The summary line the zero-match guard reads is LOCALIZED (a German-culture box prints 'gesamt:' and no
# 'Total:'), which would invert the guard into an unconditional failure. Pin it BEFORE the run (#455).
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

# TWO entries, and the split is the point.
#   1. The shipped journal suites. An OPTIONAL field and an OPTIONAL parameter must break nothing;
#      these are where a broken serializer or a changed recorder signature surfaces.
#   2. ONE METHOD from stage 10's class, selected by its fully-qualified METHOD name. The rest of
#      that class is legitimately RED until stage 13, so the class-level filter would fail this stage
#      for a reason it cannot fix - but P10 is a SILENCE pin that is green today and must stay green,
#      and it is the only behavioural check on the one risky property this stage has: that the new
#      field is OMITTED from run.json when null. A field written unconditionally turns P10 red here,
#      three stages before anyone would otherwise notice.
# Discriminating: 'RunJournalTests' is not a substring of 'RunJournalDefinitionHashTests' or
# 'RunJournalDriftAcceptTests' or 'RunJournalDeliveryTests', and none of them contains it.
$suites = @(
    @{ Project = 'tests/Guardrails.Core.Tests'
       Filter  = '(FullyQualifiedName~RunJournalTests|FullyQualifiedName~RunJournalDefinitionHashTests|FullyQualifiedName~RunJournalDriftAcceptTests|FullyQualifiedName~RunJournalDeliveryTests)'
       Hint    = 'A shipped journal suite is red. This stage is PURE DATA SHAPE: an optional nullable field with JsonIgnore WhenWritingNull, and one optional defaulted parameter on each of RecordAttempt, RecordSettle and RecordSettleWithAttempt. If a recorder signature broke a call site, the parameter is not optional. If a serialization pin broke, the field is being written when null.' }
    @{ Project = 'tests/Guardrails.Core.Tests'
       Filter  = 'FullyQualifiedName~ExecutedDefinitionDivergenceTests.AnUneditedRun_WritesNoDivergenceKeyAndNoDivergenceDecision'
       Hint    = 'P10 is the silence pin: an unedited run gains NO new run.json key and NO new decisions[] entry, asserted on the FULL lists. It is green today and must stay green. A red here means the new journal field is being SERIALIZED when null - it needs JsonIgnore with Condition = JsonIgnoreCondition.WhenWritingNull, exactly as the sibling DefinitionHash property already carries. Section 6.3: the field presence is driven by the GATE VERDICT, never by hash inequality, and an unedited run run.json must be byte-identical.' }
)

# ACCUMULATE (#478): one distinguishable message per suite, dumped once at the end.
$failures = @()

foreach ($suite in $suites) {
    # NO -v q on a TEST command: it suppresses the Error Message/Expected/Actual/Stack Trace block,
    # leaving only the [FAIL] line for the re-emit below to find - defeating #179 by the flag alone
    # (#462).
    $out = & dotnet test $suite.Project --filter $suite.Filter --nologo 2>&1
    $testExit = $LASTEXITCODE                              # capture BEFORE any other statement
    $out | ForEach-Object { Write-Output $_ }

    # EXIT CODE FIRST, guard second (#455 forward polarity): a test host that never ran exits NON-zero
    # with no summary at all, so checking the exit code first reports its real error instead of blaming
    # the filter - a confident misdiagnosis pointing at the one artifact a retry agent may NOT edit here.
    if ($testExit -ne 0) {
        $detail = $out |
            Select-String -Pattern '\[FAIL\]|Error Message:|Assert\.|Exception|Stack Trace:|Expected:|Actual:' |
            ForEach-Object { $_.Line } |
            Select-Object -First 40                        # bound the block so it fits the ~60-line tail
        Write-Output ""
        Write-Output "=== $($suite.Project) failure details (re-emitted so they land in the harness feedback tail) ==="
        if ($detail) { $detail | ForEach-Object { Write-Output $_ } }
        else { Write-Output "(no assertion/exception lines matched - inspect the full log above)" }
        $failures += "$($suite.Project) is red under filter '$($suite.Filter)'. $($suite.Hint)"
        continue
    }

    # ZERO-MATCH GUARD (#455): exit 0 alone does NOT mean tests passed - a --filter that matches nothing,
    # or is malformed, also exits 0. Key on the EXECUTED count (Passed+Failed); 'Total:' would also count
    # [Skip]ped tests, so a fully-skipped selection would clear a Total-keyed guard.
    $ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
            ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
    if ($ran -lt 1) {
        $failures += "$($suite.Project) exited 0 but executed ZERO tests under filter '$($suite.Filter)' - this guardrail certified nothing. The filter matched no tests, is malformed, or every match is [Skip]ped."
    }
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== $($failures.Count) suite(s) not green ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    exit 1
}
Write-Output "Record is additive: the shipped journal suites are green and an unedited run still writes no divergence key and no divergence decision."
exit 0
