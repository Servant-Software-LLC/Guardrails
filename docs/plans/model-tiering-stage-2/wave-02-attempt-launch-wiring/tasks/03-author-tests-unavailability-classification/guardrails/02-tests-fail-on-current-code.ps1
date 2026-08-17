# catches: a suite that only asserts what the shipped classifier ALREADY does - which would answer
#          the DoR 6.3 open question "no change needed" by construction rather than by measurement,
#          and leave a real DNS failure burning the retry budget instead of riding the #115 pause.
#          The classifier today carries "connection refused"/"connection reset"/"connection error"
#          but NO DNS, TLS-handshake or missing-executable shape, so a suite covering the prompt's
#          five groups MUST contain at least one genuine red.
# INVERSE guardrail: a NON-zero `dotnet test` exit is SUCCESS here. The sibling 01-build-passes has
# already established that the project compiles, so a non-zero exit now unambiguously means the tests
# RAN and FAILED. No #179 re-emit: the failures are the intended outcome, not a diagnosis.
$env:DOTNET_CLI_UI_LANGUAGE = 'en'    # the run summary the guard reads is LOCALIZED (#455)
$filter = 'Category=TierResolution&FullyQualifiedName~ConnectionUnavailabilityClassificationTests'
$out = dotnet test tests/Guardrails.Core.Tests --filter $filter --nologo 2>&1
$testExit = $LASTEXITCODE                                  # capture BEFORE any other statement
$out | ForEach-Object { Write-Output $_ }

# INVERSE polarity => ZERO-MATCH GUARD FIRST (#455): a crashed or never-started test host also exits
# non-zero, and certifying that as "TDD red" would send task 04 after nothing. Keyed on the EXECUTED
# count (Passed+Failed); "Total:" counts [Skip]ped tests.
$ran = ([regex]::Matches(($out | Out-String), '(?:Passed|Failed):\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Sum).Sum
if ($ran -lt 1) {
    Write-Output "ZERO tests executed - this guardrail certified nothing. The --filter '$filter' matched no tests (wrong class name, or the class-level [Trait(`"Category`", `"TierResolution`")] is missing), is malformed, or every matched test is [Skip]ped."
    exit 1
}

if ($testExit -eq 0) {
    Write-Output ""
    Write-Output "every ConnectionUnavailabilityClassificationTests test PASSED on the current code - so the suite measures nothing new and the 6.3 open question was answered by omission. The shipped ClaudeSignalClassifier has no DNS (getaddrinfo/ENOTFOUND/'Could not resolve host'), no TLS-handshake and no missing-executable shape: cover those with realistic verbatim error text and at least one must go red. Keep the negative control (an ordinary failure that merely mentions a network word must stay Error) - it is what stops task 04 from passing everything by matching 'connect'."
    exit 1
}
exit 0
