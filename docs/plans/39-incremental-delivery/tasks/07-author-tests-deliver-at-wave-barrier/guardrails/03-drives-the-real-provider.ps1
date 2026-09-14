# catches: a barrier-delivery test that proves a DOUBLE was called instead of proving the delivery
#          happened (review 2026-09-13, finding A-W4). The red census and task 08's forward census read
#          only test NAMES and OUTCOMES: a row over a provider double that records PromoteTrialDelivery
#          calls is red before task 08 and green after, and passes both while nothing ever reaches git.
#          The prompt requires the REAL Scheduler over the REAL GitWorktreeProvider; this turns that
#          requirement from prose into a check (#221).
#
#          ACCEPTS either house pattern for the real composition: `new GitWorktreeProvider(` (the Scheduler
#          constructed directly, as WaveExecutionRunTests does), or the real `run` command in process
#          (`RunCommand.Create(` or `BuildRootCommand(`, which build the provider through SchedulerFactory).
#          REJECTS any use of FakeWorktreeProvider or RecordingWorktreeProvider, any type declared to
#          implement IWorktreeProvider (a decorator is exactly the shape that lets a row assert calls
#          instead of effects), and IWorktreeProvider as a generic type argument (a mocking library's
#          double). Every task 07 row is observable through git and through files the fixture's own
#          scripts write, so a legitimate test needs none of these.
#
#          SCAN ORDER (design 38 section 4.1, #561): string and character literals are blanked FIRST, then
#          comments are stripped, so a literal that spells '//' (a URL) or '/*' (a glob) can never be read
#          as a comment and blank real code with it, and a name mentioned inside a literal or a comment
#          neither satisfies nor trips a check.
#
#          BOUNDARY: code inside an interpolation hole of a blanked string is not seen. A real provider that
#          is constructed but never used still passes. This proves the file is BUILT on the real
#          composition, not that every row uses it.
$ErrorActionPreference = 'Stop'

$file = 'tests/Guardrails.Integration.Tests/WaveDelivery/WaveBarrierDeliveryTests.cs'
if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
    Write-Output "PRECONDITION: $file does not exist - there is nothing to check. Write the test file first."
    exit 1
}

$raw = Get-Content -Raw -LiteralPath $file

# Literals first: raw strings, verbatim strings, character literals (before regular strings, so a '"' char
# cannot open one), then regular strings.
$code = [regex]::Replace($raw, '(?s)"{3,}.*?"{3,}', '""')
$code = [regex]::Replace($code, '@"(?:[^"]|"")*"', '""')
$code = [regex]::Replace($code, "'(?:\\.|[^'\\\r\n])'", "''")
$code = [regex]::Replace($code, '"(?:\\.|[^"\\\r\n])*"', '""')
# Then comments: block, then line.
$code = [regex]::Replace($code, '(?s)/\*.*?\*/', '')
$code = [regex]::Replace($code, '(?m)//.*$', '')

$problems = @()

$real = ($code -match 'new\s+GitWorktreeProvider\s*\(') -or
        ($code -match '\bRunCommand\s*\.\s*Create\s*\(') -or
        ($code -match '\bBuildRootCommand\s*\(')
if (-not $real) {
    $problems += "No real composition: the file neither constructs 'new GitWorktreeProvider(' nor runs the real command in process ('RunCommand.Create(' or 'BuildRootCommand('). Drive the REAL Scheduler over the REAL provider."
}

foreach ($double in @('FakeWorktreeProvider', 'RecordingWorktreeProvider')) {
    if ($code -match ('\b' + $double + '\b')) {
        $problems += "The file uses $double. A barrier-delivery row over a double proves the double was called, not that the user's branch moved."
    }
}

# A type whose base list names IWorktreeProvider: `class X : IWorktreeProvider`, `record X(...) : IWorktreeProvider`,
# `class X<T> : Base, IWorktreeProvider`. Anchored on the declaration so a variable or parameter typed
# IWorktreeProvider does not match.
$decl = [regex]::Match($code, '\b(class|record|struct|interface)\s+\w+\s*(<[^>]*>)?\s*(\([^)]*\))?\s*:[^{;]*\bIWorktreeProvider\b')
if ($decl.Success) {
    $problems += "The file declares a type implementing IWorktreeProvider: '$(($decl.Value -replace '\s+', ' ').Trim())'. Inject mid-run changes from the run's own task or gate scripts instead; a decorator lets a row assert provider calls rather than git effects."
}

if ($code -match '<\s*IWorktreeProvider\s*>') {
    $problems += "The file uses IWorktreeProvider as a generic type argument (a mocking library's double). Drive the REAL provider."
}

if ($problems.Count -gt 0) {
    Write-Output "=== Real-provider check FAILED for $file ==="
    $problems | ForEach-Object { Write-Output $_ }
    exit 1
}

Write-Output "Real-provider check: $file is built on the real composition and declares no provider double."
exit 0
