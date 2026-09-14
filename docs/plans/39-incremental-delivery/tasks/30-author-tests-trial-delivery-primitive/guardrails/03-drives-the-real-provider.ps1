# catches: a trial-delivery test that exercises a WRAPPER instead of the real GitWorktreeProvider (review
#          2026-09-13, finding A-W4). The red census and task 31's forward census read only test NAMES and
#          OUTCOMES: a row over a type that implements CreateTrialDelivery / PromoteTrialDelivery itself is
#          red while it throws and green once it returns something plausible, and passes both censuses
#          while GitWorktreeProvider stays unimplemented. The prompt requires every row to call the members
#          on the real provider; this turns that requirement from prose into a check (#221).
#
#          REQUIRES `new GitWorktreeProvider(`. REJECTS any use of FakeWorktreeProvider or
#          RecordingWorktreeProvider, any type declared to implement IWorktreeProvider, and
#          IWorktreeProvider as a generic type argument (a mocking library's double). No task 30 row needs
#          a wrapper: each calls the three members directly on an IWorktreeProvider-typed variable holding
#          the real provider, so a wrapper could only test itself.
#
#          SCAN ORDER (design 38 section 4.1, #561): string and character literals are blanked FIRST, then
#          comments are stripped, so a literal that spells '//' (a URL) or '/*' (a glob) can never be read
#          as a comment and blank real code with it, and a name mentioned inside a literal or a comment
#          neither satisfies nor trips a check.
#
#          BOUNDARY: code inside an interpolation hole of a blanked string is not seen. A real provider that
#          is constructed but never used still passes. This proves the file is BUILT on the real provider,
#          not that every row uses it.
$ErrorActionPreference = 'Stop'

$file = 'tests/Guardrails.Integration.Tests/WaveDelivery/TrialDeliveryPrimitiveTests.cs'
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

if ($code -notmatch 'new\s+GitWorktreeProvider\s*\(') {
    $problems += "The file never constructs 'new GitWorktreeProvider('. Every row must call the trial members on the REAL provider over a temp repo."
}

foreach ($double in @('FakeWorktreeProvider', 'RecordingWorktreeProvider')) {
    if ($code -match ('\b' + $double + '\b')) {
        $problems += "The file uses $double. Its trial members are the throwing interface defaults or a double's own answers, never the git behavior these rows pin."
    }
}

# A type whose base list names IWorktreeProvider: `class X : IWorktreeProvider`, `record X(...) : IWorktreeProvider`,
# `class X<T> : Base, IWorktreeProvider`. Anchored on the declaration so a variable or parameter typed
# IWorktreeProvider does not match.
$decl = [regex]::Match($code, '\b(class|record|struct|interface)\s+\w+\s*(<[^>]*>)?\s*(\([^)]*\))?\s*:[^{;]*\bIWorktreeProvider\b')
if ($decl.Success) {
    $problems += "The file declares a type implementing IWorktreeProvider: '$(($decl.Value -replace '\s+', ' ').Trim())'. Call the members on the real GitWorktreeProvider; a wrapper tests only itself."
}

if ($code -match '<\s*IWorktreeProvider\s*>') {
    $problems += "The file uses IWorktreeProvider as a generic type argument (a mocking library's double). Call the REAL provider."
}

if ($problems.Count -gt 0) {
    Write-Output "=== Real-provider check FAILED for $file ==="
    $problems | ForEach-Object { Write-Output $_ }
    exit 1
}

Write-Output "Real-provider check: $file constructs the real GitWorktreeProvider and declares no provider double."
exit 0
