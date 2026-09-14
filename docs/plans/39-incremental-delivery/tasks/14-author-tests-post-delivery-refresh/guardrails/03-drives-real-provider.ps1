# catches: "drive the REAL GitWorktreeProvider" honored in prose only (review 2026-09-13, A-W4; #221).
#          The census scripts read only TRX names and outcomes, so a suite built over a double is red
#          before task 15 and green after, and passes both censuses while proving nothing about git:
#          FakeWorktreeProvider hardcodes FastForwarded and makes no commits, RecordingWorktreeProvider's
#          integration path is not a directory, and a hand-written IWorktreeProvider wrapper can return
#          any result it likes, including a refresh that never touched a repo.
#
#          The file must construct the real provider, and may not use either house double or declare or
#          mock an IWorktreeProvider. No wrapper is needed here: every mid-run change these tests make
#          (the teammate commit, the failing gates, the file that blocks a refresh) is a SCRIPT the
#          fixture writes, the WaveExecutionRunTests pattern. String literals are neutralized and then
#          comments are stripped, so a comment explaining why the fake cannot be used neither trips the
#          ban nor satisfies the requirement.
#
#          BOUNDARY: a static text check. It cannot see a test that constructs the real provider and
#          then ignores it; the red and forward censuses still carry the behavioral weight.
$ErrorActionPreference = 'Stop'

$file = 'tests/Guardrails.Integration.Tests/WaveDelivery/PostDeliveryRefreshTests.cs'
if (-not (Test-Path -LiteralPath $file)) {
    Write-Output "PRECONDITION: $file does not exist - the suite was never written."
    exit 1
}

$text = Get-Content -Raw -LiteralPath $file
# String literals FIRST (raw, then verbatim, then regular), so a literal that spells a comment delimiter
# (a URL, a glob) is never mistaken for a comment (GR2037, #561); THEN block comments, then line comments.
$code = [regex]::Replace($text, '"""[\s\S]*?"""', '""')
$code = [regex]::Replace($code, '@"(?:[^"]|"")*"', '""')
$code = [regex]::Replace($code, '"(?:\\.|[^"\\\r\n])*"', '""')
$code = [regex]::Replace($code, '(?s)/\*.*?\*/', '')
$code = [regex]::Replace($code, '(?m)//.*$', '')

$problems = @()
if ($code -notmatch 'new\s+GitWorktreeProvider\s*\(') {
    $problems += "MISSING: no 'new GitWorktreeProvider(' outside comments. These tests must drive the real provider over temp repos."
}
foreach ($banned in @('FakeWorktreeProvider', 'RecordingWorktreeProvider')) {
    if ($code -match "\b$banned\b") {
        $problems += "BANNED: '$banned' is used outside comments. It cannot express a refresh; drive GitWorktreeProvider."
    }
}
if ($code -match ':\s*IWorktreeProvider\b' -or $code -match '<\s*IWorktreeProvider\s*>') {
    $problems += "BANNED: a class implementing, or a mock of, IWorktreeProvider. A wrapper can return any result it likes; make each mid-run change with a script the fixture writes."
}

if ($problems.Count -gt 0) {
    $problems | ForEach-Object { Write-Output $_ }
    exit 1
}

Write-Output "Real provider: $file constructs GitWorktreeProvider and uses no provider double."
exit 0
