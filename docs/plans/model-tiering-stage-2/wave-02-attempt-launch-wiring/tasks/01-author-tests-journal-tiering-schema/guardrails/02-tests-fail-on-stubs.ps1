# catches: a test-author task that wrote TAUTOLOGIES - tests that already pass against the shipped
#          journal, proving nothing about the DoR 12.4 delta. The stub these must land on is real and
#          specific: JournalJson.OutcomeToken's switch ends in `_ => throw new JsonException(...)`,
#          so a serialized AttemptOutcome.NoRoute THROWS today, and no TierSource token mapping
#          exists at all. If every authored test passes, the delta was not encoded.
# INVERSE guardrail: a NON-zero `dotnet test` exit is SUCCESS here. The sibling 01-build-passes has
# already established that the project compiles, so a non-zero exit now unambiguously means the tests
# RAN and FAILED - which is TDD red (#155). No #179 failure-detail re-emit: the failures are the
# intended outcome, not a diagnosis.
$env:DOTNET_CLI_UI_LANGUAGE = 'en'    # the run summary the guard reads is LOCALIZED (#455)
$filter = 'Category=TierResolution&FullyQualifiedName~JournalTieringSchemaTests'
$out = dotnet test tests/Guardrails.Core.Tests --filter $filter --nologo 2>&1
$testExit = $LASTEXITCODE                                  # capture BEFORE any other statement
$out | ForEach-Object { Write-Output $_ }

# INVERSE polarity => ZERO-MATCH GUARD FIRST (#455): here a non-zero exit is the SUCCESS signal, so
# a crashed or never-started test host would otherwise be certified as "TDD red". Keyed on the
# EXECUTED count (Passed+Failed); "Total:" counts [Skip]ped tests, so a fully-skipped class would
# clear a Total-keyed guard.
$ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 1) {
    Write-Output "ZERO tests executed - this guardrail certified nothing. The --filter '$filter' matched no tests (wrong class name, or the [Trait(`"Category`", `"TierResolution`")] attribute is missing), is malformed, or every matched test is [Skip]ped."
    exit 1
}

if ($testExit -eq 0) {
    Write-Output ""
    Write-Output "every JournalTieringSchemaTests test PASSED against the shipped journal - so they do not encode the 12.4 delta. At minimum the no-route token test must be RED: JournalJson.OutcomeToken has no AttemptOutcome.NoRoute arm and its `_ =>` throws, and there is no TierSource token mapping. Assert through the SHIPPED reader/writer, not a private JsonSerializer options bag."
    exit 1
}
exit 0
