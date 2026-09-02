# catches: a test file that does not actually exercise GR2060 - the hollow-red trap. This task's red is
#          a COMPILE failure (the tests reference Guardrails.Core.Loading.ProducerCoverage, which task 4
#          creates), so a bare "exit non-zero" would be satisfied by ANY breakage, and an exit of ZERO
#          would mean the tests compiled - i.e. they never referenced the type under test at all and are
#          asserting nothing. Both are caught here by requiring the SPECIFIC expected red.
#
# POLARITY NOTE: this is the INVERSE (TDD-red) check, so it does NOT re-emit failure detail (#179 covers
#          the forward direction). The usual #455 guard-first ordering assumes tests RAN; here the
#          expected red is a compile failure, so the executed count is legitimately 0 and a count-based
#          guard would invert. The discriminator is the compile error naming the missing type instead.
$ErrorActionPreference = 'Continue'
$env:DOTNET_CLI_UI_LANGUAGE = 'en'

$file = 'tests/Guardrails.Core.Tests/ProducerCoverageTests.cs'
if (-not (Test-Path -LiteralPath $file)) {
    Write-Output ('PRECONDITION: ' + $file + ' does not exist. This task authors it; there is nothing to be red about yet.')
    exit 1
}

$log = & dotnet test 'tests/Guardrails.Core.Tests/Guardrails.Core.Tests.csproj' --nologo --filter 'FullyQualifiedName~ProducerCoverageTests' 2>&1 | Out-String
$code = $LASTEXITCODE
Write-Output $log

if ($code -eq 0) {
    Write-Output ''
    Write-Output 'THE TESTS COMPILED AND PASSED, which means they are not testing GR2060. ProducerCoverage does not exist yet (task 4 creates it), so a test file that genuinely exercises it CANNOT compile. A green result here means the tests reference nothing real - the hollow-red trap. Write assertions that call ProducerCoverage directly.'
    exit 1
}

# The SPECIFIC expected red: the type under test is missing. CS0246/CS0103 name it.
if ($log -notmatch 'ProducerCoverage') {
    Write-Output ''
    Write-Output 'THE BUILD FAILED FOR THE WRONG REASON: the compiler output never mentions ProducerCoverage. The intended red is that the type under test does not exist yet; this is some other breakage, and shipping it as TDD red would certify a broken test project as a passing task. Fix the unrelated error - or, if it lives outside this task writeScope, escalate with needsHuman and stop.'
    exit 1
}

Write-Output 'TDD red confirmed: the tests reference Guardrails.Core.Loading.ProducerCoverage, which does not exist yet, so the test project does not compile. Task 4 makes this green.'
exit 0
