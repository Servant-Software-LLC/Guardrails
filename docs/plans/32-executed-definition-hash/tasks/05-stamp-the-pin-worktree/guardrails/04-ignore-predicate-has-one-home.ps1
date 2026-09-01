# catches: the smaller half of this stage silently not happening. Section 15.2 is a whole subsection
#          about it, and its finding is that EVERY pressure points the same way:
#
#              "IsEditorArtifact is private static inside LivePlanEditWatch.cs, which appeared in no
#               row's writeScope; HashText and TaskDefinitionFiles are forbidden by section 11, and so
#               is a new source file. Every one of those pressures points at the same escape - SKIP THE
#               IGNORE LIST - which silently un-decides section 6.2, the sharpest call in this document."
#
#          Stage 13's gate needs that predicate. If it is still private when stage 13 runs, that stage's
#          only in-scope moves are to inline a SECOND copy of the list - which section 6.2 forbids
#          because "a future addition cannot reach one and miss the other" - or to drop the filter
#          entirely, which turns the delivery gate into something that blocks an overnight run on a
#          stray .DS_Store and is disabled within a week (#229). Neither of those is visible from stage
#          13's own guardrails as a MISSING promotion; it looks like a design choice. So it is checked
#          HERE, at the stage that owns the file.
#
# WHY A SOURCE-SHAPE CHECK AND NOT A TEST (the #468 demotion order, worked): "the predicate is internal
#          rather than private" is a compile-time fact with no runtime shadow - the watch behaves
#          identically either way, and the only consumer that would notice does not exist for eight more
#          stages. A test COULD assert the widened accessibility once it exists, but no test-authoring
#          row in this plan can write it: a test naming a private member does not compile at stage 7
#          (the only test stage before this one), and stage 6 runs AFTER this stage, so it could only
#          report a defect it has no writeScope to fix - the #553 shape. Genuinely unreachable by a test
#          this plan can author, so this is the demotion order's last rung, and it ships with a committed
#          .valid/.invalid pair in ../samples/.
#
# TWO-LEVEL STRIP (section 11a): $raw is NEVER matched against and never reassigned. The REQUIRED clauses
#          read $code (comments gone, string literals INTACT - load-bearing here, because the ignore
#          patterns ARE string literals); the BAN reads $scan.
#
# MEASURED BASELINES on design/32-executed-definition-hash @1f6d54c, against
#          src/Guardrails.Core/Execution/LivePlanEditWatch.cs, case-SENSITIVE (#478):
#            internal static bool IsEditorArtifact   0   this stage's deliverable
#            private static bool IsEditorArtifact    1   EXPECTED nonzero - the CURRENT shape, which this
#                                                        stage replaces. A forbidden-present clause, and
#                                                        the one clause in this plan that is legitimately
#                                                        RED on arrival for a reason other than a missing
#                                                        deliverable: it is the before-state itself.
#            .DS_Store / Thumbs.db / .swp / .orig / .rej   1 each   EXPECTED nonzero - a tests-untouched
#                                                        REGRESSION clause. The list must survive the
#                                                        promotion unchanged; widening its accessibility
#                                                        is not licence to edit its contents, and
#                                                        changing it would move what the gate compares
#                                                        without moving what the hash covers.
$ErrorActionPreference = 'Continue'

$ws = $env:GUARDRAILS_WORKSPACE
if ([string]::IsNullOrEmpty($ws)) { $ws = (Get-Location).Path }

# GR_SUBJECT is the `guardrails samples verify` contract (Samples/SampleVerifier.cs): the sample path
# arrives as argv[0] AND in $env:GR_SUBJECT, ABSOLUTE. Joining it to the workspace would yield a nonsense
# path and PRECONDITION-fail, which reads exactly like a real finding.
#   $env:GR_SUBJECT='<plan>/tasks/05-stamp-the-pin-worktree/samples/04-ignore-predicate-has-one-home.valid.cs'   -> expect 0
#   $env:GR_SUBJECT='<plan>/tasks/05-stamp-the-pin-worktree/samples/04-ignore-predicate-has-one-home.invalid.cs' -> expect 1
# RE-RUN EVERY case after ANY edit to this file, not just the clause you touched.
$rel  = if ($env:GR_SUBJECT) { $env:GR_SUBJECT } else { 'src/Guardrails.Core/Execution/LivePlanEditWatch.cs' }
$full = if ([System.IO.Path]::IsPathRooted($rel)) { $rel } else { Join-Path $ws $rel }

# PRECONDITION - the one legitimate early exit.
if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
    Write-Output "PRECONDITION: $rel does not exist. It is a SHIPPED file this task edits in place; guardrail 01 would have failed first if it were merely broken."
    exit 1
}

$raw  = Get-Content -Raw -LiteralPath $full                  # NEVER matched against, never reassigned
$code = [regex]::Replace($raw,  '/\*[\s\S]*?\*/', ' ')       # /* */ block comments
$code = [regex]::Replace($code, '(?m)//[^\r\n]*', ' ')       # // and /// line comments
$scan = [regex]::Replace($code, '"""[\s\S]*?"""', '""')      # C# 11 raw strings
$scan = [regex]::Replace($scan, '@"(?:[^"]|"")*"', '""')     # verbatim strings
$scan = [regex]::Replace($scan, '"(\\.|[^"\\])*"', '""')     # ordinary strings

# ACCUMULATE (#478): one distinguishable message per clause, dumped once.
$failures = @()

# --- REQUIRED: the predicate is reachable from the gate --------------------------------------------
# -cmatch: C# keywords and identifiers are case-SENSITIVE and PowerShell -match is not (taxonomy 3).
$decls = [regex]::Matches($code, '(?m)^\s*(public|internal|private|protected)\s+static\s+bool\s+IsEditorArtifact\s*\(')
if ($decls.Count -lt 1) {
    $failures += "$rel declares no 'static bool IsEditorArtifact(' at all. Section 15.2 gives this stage the promotion because it is the smallest change that gives the shared predicate a legal home: no new file, no move, no behaviour change to the watch, and the one place the list lives stays the one place a future pattern gets added."
}
elseif ($decls.Count -gt 1) {
    $failures += "$rel declares IsEditorArtifact $($decls.Count) times. There is exactly ONE home for this predicate by design - section 6.2: the gate and the watch share it 'so a future addition cannot reach one and miss the other'. A second declaration is the duplication the whole subsection exists to prevent, arriving inside the file that was supposed to prevent it."
}
elseif ($decls[0].Groups[1].Value -ceq 'private') {
    $failures += "$rel still declares IsEditorArtifact 'private static'. Stage 13's gate cannot reach it, and section 15.2 traced where that leads: stage 13's only in-scope moves become inlining a SECOND copy of the ignore list (which section 6.2 forbids) or dropping the filter entirely (which turns the delivery gate into something that blocks an overnight run on a stray .DS_Store, and is then disabled within a week). Promote it to 'internal static' - same assembly, no move, no behaviour change."
}

# --- REQUIRED: the list itself survives the promotion UNCHANGED ------------------------------------
# Reads $code, NOT $scan: these patterns ARE string literals, so stripping literals would make every
# clause here unsatisfiable by construction - the mirror dead-end #470 warns about, at the required
# polarity.
foreach ($pattern in @('.DS_Store', 'Thumbs.db', '.swp', '.orig', '.rej')) {
    if ($code -cnotmatch [regex]::Escape($pattern)) {
        $failures += "$rel no longer names the ignore pattern '$pattern'. Widening the predicate's ACCESSIBILITY is not licence to edit its CONTENTS. The list is what makes the in-run gate strictly QUIETER than the recorded hash and never noisier (section 6.2): every pattern dropped from it is one more way a stray editor artifact can block an overnight run's delivery, and every pattern added to it is one more real definition change the gate stops seeing."
    }
}

# --- FORBIDDEN: the promotion did not turn into a behaviour change ---------------------------------
if ($scan -cmatch '\bIsEditorArtifact\s*\(\s*\)') {
    $failures += "$rel calls IsEditorArtifact with no arguments. The predicate takes the absolute path of the file under consideration; a parameterless overload is a different function wearing the same name, and stage 13 would bind to whichever one it happened to resolve."
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== ignore predicate: $($failures.Count) problem(s) in $rel ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    Write-Output ""
    Write-Output "This is a one-word change plus nothing else: private static -> internal static. HashText.cs and TaskDefinitionFiles.cs remain forbidden (section 11) - the ignore list applies HERE and never there, because moving it into HashText would move every recorded definition hash in every plan."
    exit 1
}
Write-Output "Ignore predicate has one home: exactly one internal static IsEditorArtifact, with all five patterns intact."
exit 0
