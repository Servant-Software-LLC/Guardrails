# catches: a conformance suite of TAUTOLOGIES - clauses that already pass against the UNWIRED
#          executor. That is the measured failure mode this whole gate design exists for: an earlier
#          version of the plan terminal gate went 10-of-14 GREEN against a tree holding wave 1's
#          record and NO wiring at all, because a name-shaped check cannot tell "a property with this
#          name exists" from "this value is written to per-attempt provenance". A suite that passes
#          today would reproduce exactly that, one layer up.
#
#          Note what does NOT count as coverage here: Invariant 7's clause legitimately passes on the
#          current code (everything takes the legacy path today), so a suite that only asserts THAT
#          is green and worthless. The clauses that must be RED are the ones asserting a resolved
#          route reaches provenance, a no-route settles, and the ceiling/climb are disclosed.
# INVERSE guardrail: a NON-zero `dotnet test` exit is SUCCESS here. The sibling 02-build-passes has
# already established that the project compiles, so a non-zero exit now unambiguously means the
# clauses RAN and FAILED. No #179 re-emit: the failures are the intended outcome, not a diagnosis.
$env:DOTNET_CLI_UI_LANGUAGE = 'en'    # the run summary the guard reads is LOCALIZED (#455)
$filter = 'FullyQualifiedName~Stage2ConformanceTests'
$out = dotnet test tests/Guardrails.Integration.Tests --filter $filter --nologo 2>&1
$testExit = $LASTEXITCODE                                  # capture BEFORE any other statement
$out | ForEach-Object { Write-Output $_ }

# INVERSE polarity => ZERO-MATCH GUARD FIRST (#455): here a non-zero exit is the SUCCESS signal, so a
# crashed or never-started test host would otherwise be certified as "TDD red" and the three wiring
# tasks would build on nothing. Tightened to the CLAUSE COUNT: all NINE pinned methods must have
# executed, which also catches a rename the sibling 01 guardrail's declaration check somehow allowed.
# Keyed on the EXECUTED count (Passed+Failed); "Total:" counts [Skip]ped tests.
$ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 9) {
    Write-Output "only $ran conformance clause(s) executed - this guardrail certified nothing. Nine [Fact]s are owed. Either the test host never started (read the log above), a method was renamed away from the nine pinned names, or a clause is [Skip]ped."
    exit 1
}

if ($testExit -eq 0) {
    Write-Output ""
    Write-Output "every Stage2ConformanceTests clause PASSED against the UNWIRED attempt-launch path - so the suite asserts nothing the current code does not already do. Nothing calls TierResolver from TaskExecutor yet, so at minimum these MUST be red: Resolution_RunsPerAttempt_AndReachesAttemptProvenance (provenance carries no tier/tierSource today), NoCandidateAtOrAboveRung_SettlesNoRoute_AsNeedsHuman (no no-route settle exists), and both attempt-route.log clauses (no such file is written). Assert on the JOURNAL, the captured PromptInvocation and attempt-route.log - never on TierResolver, which would pass unwired."
    exit 1
}
exit 0
