# catches: "drive the REAL GitWorktreeProvider" honored in prose only (review 2026-09-13, A-W4; #221).
#          The census scripts read only TRX names and outcomes, so a suite built over a double is red
#          before task 17 and green after, and passes both censuses while proving nothing about git:
#          FakeWorktreeProvider hardcodes FastForwarded and never refuses, RecordingWorktreeProvider's
#          integration path is not a directory, and a hand-written IWorktreeProvider wrapper can return
#          BranchMoved with whatever detail text the test then asserts, so the halt would be pinned
#          against a refusal the real provider never produces.
#
#          REQUIRES `new GitWorktreeProvider(` in code. REJECTS, outside comments, what tasks 07 and 30
#          reject (each a measured way to put a double in front of the Scheduler, verification 2026-09-13):
#            - FakeWorktreeProvider or RecordingWorktreeProvider in code;
#            - a type whose base list names IWorktreeProvider, or a using-alias (global or not) of
#              IWorktreeProvider, which would hide such a declaration;
#            - DispatchProxy, which builds an IWorktreeProvider at runtime with no declaration to find;
#            - a string literal naming any provider type other than GitWorktreeProvider or IWorktreeProvider
#              (a reflective load such as Type.GetType("...FakeWorktreeProvider..."), or "Fake" +
#              "WorktreeProvider");
#            - IWorktreeProvider as the first type argument of a generic that is not a delegate, Lazy, Task,
#              ValueTask, a collection, IsAssignableFrom or IsType (Mock<IWorktreeProvider>,
#              Substitute.For<IWorktreeProvider>, DispatchProxy.Create<IWorktreeProvider, T>).
#          No wrapper is needed here: every refusal these tests provoke (a switched checkout, a conflicting
#          commit, a commit that lands after the trial was built) is made by a SCRIPT the fixture writes,
#          the WaveExecutionRunTests pattern.
#
#          SCAN ORDER (design 38 section 4.1, #561): ONE left-to-right lexer pass matches string, character
#          and comment tokens together, so whichever starts first wins. A '//' or '/*' inside a literal never
#          opens a comment, and a quote inside a comment or a character literal never opens a string. Only
#          string literals in code are kept for the name check; one inside a comment is never inspected,
#          the same effect as tasks 07 and 30's replace-then-strip passes.
#
#          BOUNDARY: a static text check. Code inside an interpolation hole, and a provider double defined in
#          ANOTHER test file, are not seen; a real provider constructed and then ignored still passes. The
#          censuses carry the behavioral weight.
$ErrorActionPreference = 'Stop'

$file = 'tests/Guardrails.Integration.Tests/WaveDelivery/BranchMovedHaltTests.cs'
if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
    Write-Output "PRECONDITION: $file does not exist - the suite was never written."
    exit 1
}

$raw = Get-Content -Raw -LiteralPath $file

# The lexer's alternatives, in order: raw strings, verbatim strings, regular strings, character literals,
# block comments, line comments.
$lexer = @(
    '(?<raw>\$*"{3,}[\s\S]*?"{3,})',
    '(?<verb>(?:\$@|@\$|@)"(?:[^"]|"")*")',
    '(?<str>\$?"(?:\\.|[^"\\\r\n])*")',
    '(?<chr>''(?:\\.|[^''\\\r\n])'')',
    '(?<block>/\*[\s\S]*?\*/)',
    '(?<line>//[^\r\n]*)'
) -join '|'

$literals = [System.Collections.Generic.List[string]]::new()
$code = [regex]::Replace($raw, $lexer, {
    param($m)
    if ($m.Groups['raw'].Success -or $m.Groups['verb'].Success -or $m.Groups['str'].Success) {
        $literals.Add($m.Value)
        return '""'
    }
    if ($m.Groups['chr'].Success) { return "' '" }
    # A comment keeps its line breaks, so line-anchored text after it stays on its own line.
    return ' ' + ($m.Value -replace '[^\n]', '')
})

$problems = @()
if ($code -notmatch 'new\s+GitWorktreeProvider\s*\(') {
    $problems += "MISSING: no 'new GitWorktreeProvider(' in code. These tests must drive the real provider over temp repos."
}
foreach ($double in @('FakeWorktreeProvider', 'RecordingWorktreeProvider')) {
    if ($code -match ('\b' + $double + '\b')) {
        $problems += "BANNED: '$double' is used in code. It cannot express a refusal; drive GitWorktreeProvider."
    }
}
$decl = [regex]::Match($code, '\b(class|record|struct|interface)\s+\w+\s*(<[^>]*>)?\s*(\([^)]*\))?\s*:[^{;]*\bIWorktreeProvider\b')
if ($decl.Success) {
    $problems += "BANNED: a type implements IWorktreeProvider: '$(($decl.Value -replace '\s+', ' ').Trim())'. Provoke each refusal with a script the fixture writes."
}
$alias = [regex]::Match($code, '\busing\s+\w+\s*=\s*[\w.:]*\bIWorktreeProvider\b')
if ($alias.Success) {
    $problems += "BANNED: a using-alias of IWorktreeProvider: '$(($alias.Value -replace '\s+', ' ').Trim())'. An alias hides a declaration that implements it; name the interface directly."
}
if ($code -match '\bDispatchProxy\b') {
    $problems += "BANNED: DispatchProxy builds an IWorktreeProvider double at runtime. Drive GitWorktreeProvider."
}
$allowedHeads = @('Func', 'Action', 'Lazy', 'Task', 'ValueTask', 'IEnumerable', 'IReadOnlyList', 'IReadOnlyCollection',
                  'List', 'IList', 'ICollection', 'IsAssignableFrom', 'IsType')
foreach ($g in [regex]::Matches($code, '\b([A-Za-z_]\w*)\s*<\s*IWorktreeProvider\b')) {
    if ($allowedHeads -cnotcontains $g.Groups[1].Value) {
        $problems += "BANNED: IWorktreeProvider handed to '$($g.Groups[1].Value)<...>', which is how a mocking library builds a double. Only a delegate, Lazy, Task, a collection, IsAssignableFrom or IsType may carry it."
    }
}
$allowedNames = @('GitWorktreeProvider', 'IWorktreeProvider')
foreach ($text in $literals) {
    foreach ($word in [regex]::Matches($text, '\w*WorktreeProvider\b')) {
        if ($allowedNames -cnotcontains $word.Value) {
            $shown = ($text -replace '\s+', ' ')
            if ($shown.Length -gt 90) { $shown = $shown.Substring(0, 90) + '...' }
            $problems += "BANNED: a string literal names the provider type '$($word.Value)': $shown. Loading a provider by name is a double by another route; the only provider names a test needs are GitWorktreeProvider and IWorktreeProvider."
        }
    }
}

if ($problems.Count -gt 0) {
    $problems | Select-Object -Unique | ForEach-Object { Write-Output $_ }
    exit 1
}

Write-Output "Real provider: $file constructs GitWorktreeProvider and uses no provider double."
exit 0
