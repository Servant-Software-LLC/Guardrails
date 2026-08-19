# catches: the SSOT edit being skipped, or made so thinly that Invariant 4 is satisfied in name only.
#          Wave 2 shipped without its SSOT task at all - the breakdown that owed it truncated - and
#          the omission surfaced only at the plan terminal gate, after every task had run, where
#          there is no retry (#474). This guardrail is that lesson made mechanical.
#
# ==> WHY ONLY FOUR CLAUSES. An earlier draft had six. Run against the REAL 431 KB SSOT, four of the
#     six could not fail: `bumped` (3 occurrences), `minTier` (5), `advisor` (19) and a
#     frontmatter-near-tier probe were ALREADY satisfied by text waves 1-2 landed, and a fifth probe
#     demanding one particular phrasing of D24a false-RED'd on a rule the document ALREADY states in
#     its own words ("the ACTOR's rung, bumped one STRENGTH rank when the actor is weak") - the
#     name-locking-a-free-choice failure. A clause that cannot fail is theatre, and a clause that
#     fails on correct prose is worse. Every token below was MEASURED at ZERO occurrences before this
#     wave, so each can only be satisfied by wave 3 actually landing its delta.
#
#     What this guardrail therefore does NOT check, stated so the gap is visible rather than assumed
#     covered: that the floor, the advisory and the frontmatter key are documented WELL. They are
#     already documented at all, which is the only thing a token probe can establish. The prompt asks
#     for more; a human reading the diff is what verifies it.
$ErrorActionPreference = 'Continue'
$file = 'docs/plans/02-schemas-and-contracts.md'
$failures = @()

if (-not (Test-Path $file)) {
    Write-Output "$file does not exist - the SSOT is the one file this task owns"
    exit 1
}
$content = Get-Content -Raw $file

foreach ($probe in @(
    @{ Pattern = '(?:"judge"\s*:|AttemptJudge)';
       What  = 'the judge provenance object (DoR 12.4) - the schema object wave 3 adds. Without it the journal delta is undocumented, and the journal is where every downstream consumer reads the judge route. Documented as the WIRE KEY ("judge": { ... }) the way its siblings are; the C# type name is also accepted but is NOT the house style here' },
    @{ Token = 'equal-and-weak';
       What  = 'DoR 6.5 rule 4''s distinction - equal-and-STRONG needs no bump (Opus judging Opus is a real check) while equal-and-WEAK does (one blind spot talking to itself). The SSOT documents the bump but not the case that decides when it fires' },
    @{ Token = 'D32';
       What  = "the PLACEMENT ruling - the judge object hangs off provenance, not the attempt record, because AttemptProvenance is the only member that reaches BOTH record construction paths. Shape without placement is what let #475 ship: a member declared, read, and populated by nothing." }
    @{ Token = 'D29';
       What  = 'the carve-out that a PINNED costly actor licenses a costly judge bump, while the default pointer does not. It is the one place the costly floor yields, so an undocumented D29 reads as a floor violation to the next person who finds it in the code' },
    @{ Token = 'D27';
       What  = 'the decision that tiering.verifier.minTier is a FLOOR rather than a default. The knob is already documented; that it only ever RAISES and never lowers is the property a reader needs and the one this wave is the first to depend on' })) {
    # A probe may specify a literal Token (escaped) or a Pattern (regex). The judge object uses a
    # Pattern because the SSOT documents journal objects by their WIRE KEY, never by their C# record
    # name: AttemptProvenance and AttemptUsage appear ZERO times in this document, while the same
    # objects are documented as "provenance": { and "usage": {. An earlier form of this probe
    # demanded the literal 'AttemptJudge' and rejected a correct delta written in the document's own
    # house style, twice - the guardrail was constraining VOCABULARY rather than outcome.
    $rx = if ($probe.ContainsKey('Pattern')) { $probe.Pattern } else { [regex]::Escape($probe.Token) }
    if ($content -cnotmatch $rx) {
        $name = if ($probe.ContainsKey('Pattern')) { $probe.What } else { "'$($probe.Token)' - $($probe.What)" }
        $failures += "the SSOT never documents $name"
    }
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== SSOT judge deltas: $($failures.Count) finding(s) ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    Write-Output ""
    Write-Output "Invariant 4: a schema change lands in the SSOT in the SAME change as the code that motivates it. Every other task in this wave has already shipped its code; this task is what stops those changes from being claims that live outside the schema and decay with nothing noticing."
    exit 1
}
exit 0
