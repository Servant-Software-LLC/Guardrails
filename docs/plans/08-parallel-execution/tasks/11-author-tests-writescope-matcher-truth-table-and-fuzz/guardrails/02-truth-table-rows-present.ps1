# catches: a thin OR permissive-aligned proof harness. Four failure modes this gate reddens:
#          (a) the harness fails-to-compile (passing tests-fail-on-current-code vacuously) but never
#              encodes the 27-row table - assert the distinctive permissive-trap globs are present and
#              that >=27 InlineData rows exist;
#          (b) the harness encodes the trap globs but asserts them with a PERMISSIVE expectation
#              (row 1 src/Feat*/** vs src/OtherDir/Z.cs asserting true, etc.) so a naive permissive
#              matcher PASSES the table.
#          (c) THE 2ND-REVIEW BLOCKER (decoy-false-row attack, proven end-to-end): the OLD clause only
#              required a trap glob and a `false` to co-occur in ONE InlineData(...) call. An author can
#              pin each trap glob to a `false` row on a NON-discriminating path - e.g.
#              InlineData("src/Feat*/**", "zzz/nope.cs", false) - where the path's first literal segment
#              (zzz) mismatches so BOTH a correct AND a permissive matcher return false (they AGREE, so
#              the row does not discriminate). The decoy table then passes the old gate, a permissive
#              WriteScope.cs in task 12 passes 03-matcher-tests-pass, and a green write-scope CHECK ships
#              over real out-of-scope writes - the keystone false-green this whole plan exists to prevent.
#              CLOSURE: require each trap glob in its EXACT §2.1(d) DISCRIMINATING row - glob AND its
#              canonical path AND `false`, all literal - so the row is one where the CORRECT matcher
#              returns false but a permissive (prefix/suffix/second-*-discarding) matcher returns true.
#              The four canonical discriminating rows (read from §2.1(d) of 08-parallel-execution.md):
#                row  1: src/Feat*/**  vs  src/OtherDir/Z.cs   -> false (literal prefix Feat must match)
#                row 19: src/*Tests/** vs  src/UnitTestsExtra/X.cs -> false (literal SUFFIX Tests at end)
#                row 21: src/*-*.cs    vs  src/foobar.cs       -> false (literal - between the two *s)
#                row 23: a/**/b/**     vs  a/x/c/y.cs          -> false (literal b between the two **s)
#              Pinning these canonical paths (not any false-agreeing decoy path) is what makes the table
#              REJECT a permissive matcher, so task 12's tests-pass then fails for a permissive WriteScope.
#              (Behavioral alternative considered & rejected: a TEMP permissive WriteScope.cs stub +
#              dotnet test asserting RED is the §2.1(e) milestone, but at guardrail-author time the test
#              file is freshly written by an agent whose IsInScope/Overlaps CALL SHAPES are unknown, so a
#              signature-mismatch COMPILE failure is indistinguishable from a genuine table-rejection -
#              the gate's pass condition (non-zero exit) would itself false-green on a compile error the
#              gate cannot prevent. The canonical-triple pin below is fully deterministic and read-only
#              and closes the exact demonstrated decoy. Lead may revisit the behavioral gate if the
#              authored call shape is later fixed.)
#          (d) the fuzz harness is vacuous - assert BOTH §2.2 property method names exist (not the bare
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

# (c) THE 2ND-REVIEW BLOCKER FIX: each permissive-trap glob must appear in its EXACT §2.1(d)
#     DISCRIMINATING row - glob AND canonical path AND false, all literal, on a SINGLE non-commented
#     line. A decoy-false-row on a non-discriminating path (e.g. "zzz/nope.cs") no longer satisfies the
#     gate, because the canonical path is hard-required. Each canonical path is a literal with NO ')' in
#     it, so [^)]* between the literals cannot leap an InlineData boundary. Comment-evasion is rejected:
#     a line whose first non-whitespace is '//' does not count (a commented-out row is not an encoded
#     test). We scan line-by-line so the canonical glob+path+false must all sit on ONE real InlineData
#     line. Match is OrdinalIgnoreCase to tolerate the SegmentComparison-style casing the table uses.
$lines = $text -split "`r?`n"

# glob-label -> @(escaped-glob, escaped-canonical-path) ; both must appear, then a literal false, on the
# SAME uncommented InlineData line.
$canonical = @(
    @{ Label = 'src/Feat*/** vs src/OtherDir/Z.cs (row 1)';        Glob = 'src/Feat\*/\*\*';   Path = 'src/OtherDir/Z\.cs' },
    @{ Label = 'src/*Tests/** vs src/UnitTestsExtra/X.cs (row 19)'; Glob = 'src/\*Tests/\*\*';  Path = 'src/UnitTestsExtra/X\.cs' },
    @{ Label = 'src/*-*.cs vs src/foobar.cs (row 21)';             Glob = 'src/\*-\*\.cs';     Path = 'src/foobar\.cs' },
    @{ Label = 'a/**/b/** vs a/x/c/y.cs (row 23)';                 Glob = 'a/\*\*/b/\*\*';     Path = 'a/x/c/y\.cs' }
)

$notPinned = @()
foreach ($row in $canonical) {
    # InlineData( ...<glob>... <path>... , false )  - glob before path (the §2.1(d) row order:
    # scope glob first, then path), no ')' before the close, trailing false ($false tolerated).
    $pattern = "InlineData\(\s*`"[^`")]*$($row.Glob)[^`")]*`"\s*,\s*`"[^`")]*$($row.Path)[^`")]*`"\s*,\s*\`$?false\s*\)"
    $found = $false
    foreach ($line in $lines) {
        if ($line -match '^\s*//') { continue }      # comment-evasion: a commented row is not encoded
        if ($line -match $pattern) { $found = $true; break }
    }
    if (-not $found) { $notPinned += $row.Label }
}
if ($notPinned.Count -gt 0) {
    Write-Output ("WriteScopeMatcherTests does not pin the §2.1(d) DISCRIMINATING row(s): [" + ($notPinned -join '; ') + "]. Each trap glob must appear with its EXACT canonical path asserting IsInScope==false, on one uncommented InlineData line. A decoy-false-row on a non-discriminating path (where a correct AND a permissive matcher both return false) does NOT make the table reject a permissive matcher - the canonical path is what discriminates. Add the exact §2.1(d) rows: src/Feat*/** vs src/OtherDir/Z.cs; src/*Tests/** vs src/UnitTestsExtra/X.cs; src/*-*.cs vs src/foobar.cs; a/**/b/** vs a/x/c/y.cs - each ending in false.")
    exit 1
}

# (d) BOTH seeded fuzz properties must be real methods, not the bare token 'Overlaps'.
foreach ($prop in @('MembershipImpliesOverlap', 'OverlapsCompleteness')) {
    if ($text -notmatch $prop) {
        Write-Output "WriteScopeMatcherTests is missing the §2.2 fuzz property '$prop' - a 5-row/vacuous-fuzz harness must not pass the matcher milestone (both MembershipImpliesOverlap AND OverlapsCompleteness are required)"
        exit 1
    }
}

exit 0
