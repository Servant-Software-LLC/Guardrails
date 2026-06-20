# catches: a thin OR permissive-aligned proof harness. Three failure modes this gate reddens:
#          (a) the harness fails-to-compile (passing tests-fail-on-current-code vacuously) but never
#              encodes the 27-row table - assert the distinctive permissive-trap globs are present and
#              that >=27 InlineData rows exist;
#          (b) the harness encodes the trap globs but asserts them with a PERMISSIVE expectation
#              (row 1 src/Feat*/** vs src/OtherDir/Z.cs asserting true, etc.) so a naive permissive
#              matcher PASSES the table. The old bare-"a false exists somewhere" clause did NOT close
#              this: a table whose trap rows assert TRUE plus one unrelated false row passed it, and
#              paired with a permissive WriteScope.cs in task 12, 03-matcher-tests-pass went green and
#              the permissive matcher shipped. We now PIN each trap glob to a false InlineData row -
#              the trap globs are exactly the §2.1 divergence rows whose CORRECT IsInScope is false but
#              whose permissive (prefix/suffix-discarding) result is true, so pinning them false makes
#              the table reject a permissive matcher (task 12's tests-pass then fails for it);
#          (c) the fuzz harness is vacuous - assert BOTH §2.2 property method names exist (not the bare
#              token "Overlaps"): MembershipImpliesOverlap AND OverlapsCompleteness.
#          Scoped to the one file this task owns (grep-scope rule). This is the §2.1(e) "RED against a
#          naive permissive matcher" milestone gate's structural backstop.
$file = "tests/Guardrails.Core.Tests/WriteScopeMatcherTests.cs"
$text = Get-Content $file -Raw

# (a-1) Distinctive globs that only appear if the trap rows (1,18/19,20/21,22/23) are encoded.
$markers = @('Feat\*', '\*Tests', '\*-\*', 'a/\*\*/b/\*\*')
$missing = @()
foreach ($m in $markers) {
    if ($text -notmatch $m) { $missing += $m }
}
if ($missing.Count -gt 0) {
    Write-Output "WriteScopeMatcherTests is missing the permissive-trap glob(s) [$($missing -join ', ')] - the 27-row truth table is not fully encoded"
    exit 1
}

# (a-2) The full 27-row table must be present: at least 27 InlineData rows.
$inlineCount = ([regex]::Matches($text, 'InlineData')).Count
if ($inlineCount -lt 27) {
    Write-Output "WriteScopeMatcherTests has only $inlineCount InlineData row(s) - the §2.1(d) truth table has 27 rows; a thinned table is not the regression floor"
    exit 1
}

# (b) THE BLOCKER FIX: each permissive-trap glob must appear in an InlineData(...) row that asserts
#     IsInScope == false. The trap glob's CANONICAL §2.1(d) row is a false row (Feat*->row 1,
#     *Tests->row 19, *-*->row 21, a/**/b/**->row 23); their permissive result is true. A table that
#     asserts these true (or omits the false row) would pass a permissive matcher - that is exactly the
#     hole the reviewer gamed. Match an InlineData(...) call CONTAINING the trap glob AND ending in a
#     false argument (allow $false / whitespace). A literal ')' inside a path would defeat [^)]* - none
#     of the §2.1 paths contain one, and a trap row that genuinely needed one would simply have to be
#     written without it; this stays deterministic and read-only.
# Glob -> escaped-substring that must appear inside the trap row's InlineData literal.
$trapRows = @{
    'Feat*'     = 'Feat\*'
    '*Tests'    = '\*Tests'
    '*-*'       = '\*-\*'
    'a/**/b/**' = 'a/\*\*/b/\*\*'
}
$notPinned = @()
foreach ($glob in $trapRows.Keys) {
    $escaped = $trapRows[$glob]
    # InlineData( ...<trap glob>... , [$]?false )   on a single line, no ')' before the close.
    $pattern = "InlineData\([^)]*$escaped[^)]*,\s*\`$?false\s*\)"
    if ($text -notmatch $pattern) {
        $notPinned += $glob
    }
}
if ($notPinned.Count -gt 0) {
    Write-Output "WriteScopeMatcherTests does not pin trap glob(s) [$($notPinned -join ', ')] to a FALSE InlineData row - their §2.1(d) canonical expectation is IsInScope==false; without a false row a permissive (prefix/suffix-discarding) matcher passes the table. Add the canonical false row for each."
    exit 1
}

# (c) BOTH seeded fuzz properties must be real methods, not the bare token 'Overlaps'.
foreach ($prop in @('MembershipImpliesOverlap', 'OverlapsCompleteness')) {
    if ($text -notmatch $prop) {
        Write-Output "WriteScopeMatcherTests is missing the §2.2 fuzz property '$prop' - a 5-row/vacuous-fuzz harness must not pass the matcher milestone (both MembershipImpliesOverlap AND OverlapsCompleteness are required)"
        exit 1
    }
}

exit 0
