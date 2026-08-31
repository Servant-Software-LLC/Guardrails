# catches: THE mis-keying this whole stage exists to prevent - pin 1 or pin 2 asserting GR2068 when
#          the answer is GR2069. Neither plan-28 failure is a GR2068: in run 1 tests/** was reachable
#          by the test-authoring tasks, in run 3 PlanLoader.cs was reachable by task 21, so in both
#          cases the row was reachable ACROSS the plan and unreachable by the ONE task that owned it -
#          the split condition, exactly. GR2069 carries 100% of #553's motivating value; GR2068 is a
#          stale-path lint that catches neither failure. A pin keyed to GR2068 here is red today
#          (nothing emits either code), goes green never, and reads in review as if it were the
#          acceptance criterion being met.
#
#          It also catches the compile-shaped version of the same slip: naming
#          DiagnosticCodes.HandoffPathUnreachable / .HandoffRowSplitAcrossTasks / HandoffScopeCoverage
#          instead of the string literals. Those are stage 5's deliverables (plan section 7), and a
#          pin that names them cannot compile today - which is what would otherwise push an author to
#          "fix" it by widening into src/**, an out-of-scope edit that burns a retry.
#
# WHY A SOURCE CHECK AND NOT A TEST (the #468 demotion order, worked): the property is "pin 1's
#          ASSERTION names GR2069". Nothing observes it at runtime - today BOTH keyings are red
#          (no code emits either), and after stage 5 a GR2068-keyed pin 1 is simply a failing test
#          whose failure reads as "stage 5 is wrong" rather than "pin 1 is mis-keyed". The per-test
#          red census (guardrail 02) cannot see it either, for the same reason: both keyings are
#          Failed. So there is no runtime proxy, and this is a structural fact about the test file.
#          It ships with a committed .valid/.invalid sample pair in ../samples/.
#
# THE BRACE SCAN, and why it is not a proximity window: a proximity window (#478's warning) would
#          match a pre-existing line and is unfalsifiable about WHICH method it read. This extracts
#          each pinned method's body by matching braces, over a scan copy in which comments are
#          blanked and BRACES INSIDE STRING LITERALS are neutralized - both length-preserving, so the
#          offsets still index the original text. Length preservation is load-bearing: these fixtures
#          build guardrails.json and task.json content, so unneutralized literal braces would
#          desynchronize the depth count and silently slice the wrong region.
#
# MEASURED BASELINES on master @1490d2a, against the exact subject each clause scans (#478):
#          the target file does not exist, so every clause is 0. After this task authors it, the two
#          required clauses must be 1 each and the three bans must stay 0.
$ErrorActionPreference = 'Continue'

$ws = $env:GUARDRAILS_WORKSPACE
if ([string]::IsNullOrEmpty($ws)) { $ws = (Get-Location).Path }

$rel  = 'tests/Guardrails.Core.Tests/Loading/HandoffScopeCoverageTests.cs'
$full = Join-Path $ws $rel

# PRECONDITION - the one legitimate early exit: without the subject every clause below is meaningless.
if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
    Write-Output "PRECONDITION: $rel does not exist. This task authors it; guardrail 01 would have failed first if it were merely broken, so an absent file means the deliverable was not written."
    exit 1
}

$raw = Get-Content -Raw -LiteralPath $full     # NEVER matched against directly, never reassigned

# --- the length-preserving scan copy: comments blanked, literal braces neutralized ----------------
$blankKeepingNewlines = { param($m) $m.Value -replace '[^\r\n]', ' ' }
$neutralizeBraces     = { param($m) $m.Value -replace '[{}]', '_' }

$scan = [regex]::Replace($raw,  '/\*[\s\S]*?\*/',   $blankKeepingNewlines)   # /* */ blocks
$scan = [regex]::Replace($scan, '(?m)//[^\r\n]*',   $blankKeepingNewlines)   # // and /// lines
$scan = [regex]::Replace($scan, '"""[\s\S]*?"""',   $neutralizeBraces)       # C# 11 raw strings
$scan = [regex]::Replace($scan, '@"(?:[^"]|"")*"',  $neutralizeBraces)       # verbatim strings
$scan = [regex]::Replace($scan, '"(\\.|[^"\\])*"',  $neutralizeBraces)       # ordinary strings
if ($scan.Length -ne $raw.Length) {
    Write-Output "PRECONDITION: the scan copy ($($scan.Length) chars) desynchronized from the source ($($raw.Length) chars), so every offset below would slice the wrong region. This guardrail is defective for this input - report it rather than reshaping the test file."
    exit 1
}

# Returns BOTH slices of the named method's body, or $null when the method is not found or its braces
# do not balance. Offsets come from $scan; the two slices are taken at the SAME offsets from $scan
# (comments blanked, literal braces neutralized) and from $raw (untouched).
#
# Returning both is load-bearing, and returning only .Raw was a measured BLOCKER: the required clauses
# below match a diagnostic code, and read against raw source a single COMMENT inside pin 1's body -
# `// the split condition here is GR2069, per plan 31 section 4.6` - satisfied the clause while the
# assertion still read d.Code == "GR2068". The guardrail flipped from exit 1 to exit 0 and printed
# "pins 1 and 2 both assert GR2069". A string literal did it too. That is not self-correcting
# downstream: task 05 requires this suite green, cannot edit the test, and its cheapest route to green
# would be to implement HandoffScopeCoverage so a SPLIT row emits GR2068 - inverted semantics on a
# fully green run.
function Get-MethodBody([string]$name) {
    $sig = [regex]::Match($scan, '(?<![A-Za-z0-9_])' + [regex]::Escape($name) + '\s*\(')
    if (-not $sig.Success) { return $null }
    $open = $scan.IndexOf('{', $sig.Index)
    if ($open -lt 0) { return $null }
    $depth = 0
    for ($i = $open; $i -lt $scan.Length; $i++) {
        if     ($scan[$i] -eq '{') { $depth++ }
        elseif ($scan[$i] -eq '}') {
            $depth--
            if ($depth -eq 0) {
                $len = $i - $open + 1
                return @{ Scan = $scan.Substring($open, $len); Raw = $raw.Substring($open, $len) }
            }
        }
    }
    return $null
}

# ACCUMULATE (#478): one distinguishable message per clause, dumped once - never an exit-1 chain that
# reports one gap per attempt.
$failures = @()

# --- REQUIRED: pins 1 and 2 assert GR2069 --------------------------------------------------------
$mustKeyGr2069 = @(
    @{ Pin  = 'pin 1 (the REAL plan-28 run-3 row-7 catch)'
       Name = 'Row7WhoseOwningTaskHoldsOnlyTwoOfFourPaths_EmitsGR2069NamingTheCoveringTask'
       Why  = "PlanLoader.cs was reachable by task 21, so row 7 was never UNREACHABLE - it was reachable across the plan and unreachable by the one task that owned it. That is GR2069, the split condition." },
    @{ Pin  = 'pin 2 (the REAL plan-28 run-1 row-1 catch)'
       Name = 'Row1WithoutTheTestGlobEmitsGR2069_AndIsSilentOnceTheGlobIsAdded'
       Why  = "tests/** was reachable by the test-authoring tasks, so row 1 was never UNREACHABLE either. That is GR2069." }
)
foreach ($p in $mustKeyGr2069) {
    $body = Get-MethodBody $p.Name
    if ($null -eq $body) {
        $failures += "$($p.Pin): no method named '$($p.Name)' with a balanced body was found in $rel. The census (guardrail 02) pins this exact name; the two must agree."
        continue
    }
    # Read .Scan (comments BLANKED), not .Raw - a comment naming the code satisfied this clause and
    # flipped the guardrail green over a body still asserting GR2068 (see Get-MethodBody's header).
    # And require the QUOTED token: a bare prose mention of GR2069 in a surviving string is not an
    # assertion. String-literal TEXT survives into .Scan (only braces inside literals are
    # neutralized), so a real `d.Code == "GR2069"` still matches.
    #
    # There is deliberately NO ban on GR2068 appearing in these bodies. A good author may strengthen
    # pin 1 with `Assert.DoesNotContain(diagnostics, d => d.Code == "GR2068")` - the codes are
    # mutually exclusive per row (pin 3a), so that is a CORRECT assertion, and banning it would
    # false-red the better implementation.
    if ($body.Scan -cnotmatch '"GR2069"') {
        $extra = if ($body.Scan -cmatch '"GR2068"') { " Its body asserts the quoted GR2068 and never the quoted GR2069 - that is the mis-keying." } else { "" }
        $failures += "$($p.Pin): its body never asserts the quoted token ""GR2069"".$extra $($p.Why) A pin keyed to GR2068 here is red today, green never, and reads in review as the acceptance criterion being met. Naming GR2069 in a comment does NOT satisfy this - the assertion has to carry it."
    }
}

# --- FORBIDDEN: the pins key on LITERALS, not on stage 5's not-yet-written symbols ----------------
# These read the comment-blanked, brace-neutralized copy, whose string-literal TEXT is otherwise
# intact - so a symbol used in code is caught and a symbol named in a comment is not (#470/#76).
$bannedSymbols = @(
    @{ Token = 'HandoffPathUnreachable'
       Why   = 'DiagnosticCodes.HandoffPathUnreachable is stage 5''s deliverable and does not compile today. Assert on the string literal GR2068 instead; stage 5 carries its own pin that the constant equals that literal.' },
    @{ Token = 'HandoffRowSplitAcrossTasks'
       Why   = 'DiagnosticCodes.HandoffRowSplitAcrossTasks is stage 5''s deliverable and does not compile today. Assert on the string literal GR2069 instead.' },
    @{ Token = 'HandoffScopeCoverage'
       Why   = 'The HandoffScopeCoverage type is stage 5''s deliverable and does not compile today. Drive PlanValidator.Validate and assert on the diagnostic list it returns.' }
)
foreach ($b in $bannedSymbols) {
    # HandoffScopeCoverageTests contains 'HandoffScopeCoverage' as a prefix, so the trailing
    # look-ahead is what keeps the class's OWN name from tripping its own ban.
    $pattern = '(?<![A-Za-z0-9_])' + [regex]::Escape($b.Token) + '(?![A-Za-z0-9_])'
    if ($scan -cmatch $pattern) {
        $failures += "$rel names '$($b.Token)' in code. $($b.Why)"
    }
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== pin keying: $($failures.Count) problem(s) ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    Write-Output ""
    Write-Output "Read plan 31 section 4.6 and the box under it: 'Both catches land on GR2069, and that must not be glossed.' Do NOT resolve any of this by editing src/** - that is outside this task's writeScope."
    exit 1
}
Write-Output "Pin keying sound: pins 1 and 2 both assert GR2069, and no pin names a stage-5 symbol."
exit 0
