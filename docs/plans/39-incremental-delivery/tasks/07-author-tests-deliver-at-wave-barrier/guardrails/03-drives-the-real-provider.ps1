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
#          REJECTS, each a measured way to put a double in front of the Scheduler (verification 2026-09-13):
#            - FakeWorktreeProvider or RecordingWorktreeProvider in code;
#            - a type declared to implement IWorktreeProvider, directly or under a using-alias;
#            - DispatchProxy, which builds an IWorktreeProvider at runtime with no declaration to find;
#            - a string literal naming any provider type other than GitWorktreeProvider or IWorktreeProvider
#              (a reflective load such as Type.GetType("...FakeWorktreeProvider..."));
#            - IWorktreeProvider handed to a generic that is not a plain container or delegate
#              (Mock<IWorktreeProvider>, Substitute.For<IWorktreeProvider>). Func<IWorktreeProvider>, Lazy<>,
#              Task<> and the collection interfaces are allowed: a helper returning the real provider is fine.
#          Every task 07 row is observable through git and through files the fixture's own scripts write, so
#          a legitimate test needs none of these.
#
#          SCAN ORDER (design 38 section 4.1, #561): string and character literals are replaced FIRST, then
#          comments are stripped, so a literal that spells '//' (a URL) or '/*' (a glob) can never be read
#          as a comment and blank real code with it. Each string literal is swapped for a __LIT_n__ token and
#          its text kept, so a literal that survives the comment pass (one in code, not inside a comment)
#          can still be inspected.
#
#          BOUNDARY: code inside an interpolation hole of a replaced string is not seen. A real provider that
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
# cannot open one), then regular strings. String literals become __LIT_n__ tokens; their text is kept.
$literals = [System.Collections.Generic.List[string]]::new()
$keep = { param($m) $literals.Add($m.Value); ' __LIT_' + ($literals.Count - 1) + '__ ' }
$code = [regex]::Replace($raw, '(?s)"{3,}.*?"{3,}', $keep)
$code = [regex]::Replace($code, '@"(?:[^"]|"")*"', $keep)
$code = [regex]::Replace($code, "'(?:\\.|[^'\\\r\n])'", "''")
$code = [regex]::Replace($code, '"(?:\\.|[^"\\\r\n])*"', $keep)
# Then comments: block, then line. A token inside a comment goes with it.
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

$alias = [regex]::Match($code, '\busing\s+\w+\s*=\s*[\w.:]*\bIWorktreeProvider\b')
if ($alias.Success) {
    $problems += "The file aliases IWorktreeProvider: '$(($alias.Value -replace '\s+', ' ').Trim())'. An alias hides a declaration that implements it; use the real provider."
}

if ($code -match '\bDispatchProxy\b') {
    $problems += "The file uses DispatchProxy, which builds an IWorktreeProvider double at runtime. Drive the REAL provider."
}

$allowedHeads = @('Func', 'Action', 'Lazy', 'Task', 'ValueTask', 'IEnumerable', 'IReadOnlyList', 'IReadOnlyCollection',
                  'List', 'IList', 'ICollection', 'IsAssignableFrom', 'IsType')
foreach ($g in [regex]::Matches($code, '\b([A-Za-z_]\w*)\s*<\s*IWorktreeProvider\b')) {
    if ($allowedHeads -cnotcontains $g.Groups[1].Value) {
        $problems += "The file hands IWorktreeProvider to '$($g.Groups[1].Value)<...>', which is how a mocking library builds a double. Drive the REAL provider; only a delegate, Lazy, Task or collection of IWorktreeProvider is allowed."
    }
}

$allowedNames = @('GitWorktreeProvider', 'IWorktreeProvider')
foreach ($token in [regex]::Matches($code, '__LIT_(\d+)__')) {
    $text = $literals[[int]$token.Groups[1].Value]
    foreach ($word in [regex]::Matches($text, '\w*WorktreeProvider\b')) {
        if ($allowedNames -cnotcontains $word.Value) {
            $shown = if ($text.Length -gt 90) { $text.Substring(0, 90) + '...' } else { $text }
            $problems += "A string literal names the provider type '$($word.Value)': $shown. Loading a provider by name (for example with Type.GetType) is a double by another route; the only provider names a test needs are GitWorktreeProvider and IWorktreeProvider."
        }
    }
}

if ($problems.Count -gt 0) {
    Write-Output "=== Real-provider check FAILED for $file ==="
    $problems | Select-Object -Unique | ForEach-Object { Write-Output $_ }
    exit 1
}

Write-Output "Real-provider check: $file is built on the real composition and uses no provider double."
exit 0
