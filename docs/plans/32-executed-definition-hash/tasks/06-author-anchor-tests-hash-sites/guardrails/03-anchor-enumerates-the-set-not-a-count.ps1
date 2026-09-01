# catches: an anchor test that anchors a NUMBER instead of a SET. Section 9 records three drafts of this
#          check and the third one's defeat is the whole reason this guardrail exists:
#
#              "'TaskDefinitionHash.Compute(' appears exactly N times" - TWO separate defects. The
#              derivation gave 6 against a true 8. And a bare count is a TAUTOLOGY MAGNET: an agent that
#              meets a wrong number under retry pressure runs the grep and writes down whatever it says -
#              installing the exact anti-pattern in the guardrail whose job is to prevent one.
#
#          Guardrail 02 cannot see this. A count-shaped anchor test PASSES - that is the entire problem
#          with it - so the outcome carries no information about the form. This check reads the file the
#          stage authored and asks whether it enumerates the eight (file, member) pairs by name, and
#          whether a count assertion crept in beside them.
#
#          It also catches the quieter loss: an anchor written in only ONE direction. Asserting that the
#          eight known sites are present is easy and useless on its own - it is the reverse direction
#          ("every Compute occurrence in src maps to a known row") that catches Risk 6's seventh site
#          added later by someone who has not read the document.
#
#          AND IT CATCHES THE HOLLOW ANCHOR, which is the failure a non-authoring review MEASURED against
#          an earlier draft of this file. A class holding three string[] arrays of the right shape and a
#          single [Fact] asserting Assert.Equal(Sites.Length, Sites.Distinct().Count()) exited ZERO. Every
#          token clause above was satisfied - the eight members, the five files, the four zero-occurrence
#          files, the shape anchors - because they are all just STRINGS IN AN ARRAY, and nothing required
#          the test to open a file, walk a directory, or name the hasher. That retires Risk 6's
#          repo-lifetime tripwire AND stage 9's ninth-call-site check in one move, and it is likely rather
#          than theoretical: NO test in this repo reads src/**/*.cs as text today, so an implementer
#          transfers the row-array half of the existing anchor idiom - which is exactly the half a hollow
#          test satisfies. The five clauses below require the test to actually READ SOMETHING.
#
# WHY A SOURCE-SHAPE CHECK AND NOT A TEST (the #468 demotion order, worked): the property is "this test
#          asserts a SET rather than a COUNT". A test cannot assert that about itself, and its OUTCOME is
#          identical either way - which is precisely why the count form survived two review rounds. There
#          is no runtime observable at all. Demotion order's last rung, and it ships with a committed
#          .valid/.invalid pair in ../samples/.
#
# TWO-LEVEL STRIP (section 11a): $raw is NEVER matched against and never reassigned. The REQUIRED clauses
#          read $code (comments gone, string literals INTACT - load-bearing here, because the eight
#          (file, member) pairs live in this test AS STRING LITERALS); the BAN reads $scan, so a comment
#          explaining "a bare count is forbidden" cannot trip it.
#
# MEASURED BASELINES on design/32-executed-definition-hash @1f6d54c (#478): the subject file does not
#          exist yet - n/a, file created by this task - so every clause below measures nothing today. The
#          ambient-vocabulary test still applies and passes: none of the eight member names is a
#          namespace, a using, or a base type, so each is discriminating once the file exists.
#
#          The five READS-SOMETHING clauses are the exception worth naming, because they are the ONLY
#          clauses here whose tokens could plausibly be ambient in a test file. Each was checked against
#          that risk: 'TaskDefinitionHash' is the type under anchor and appears in no using or namespace
#          this file needs; 'EnumerateFiles|GetFiles' and 'ReadAllText|ReadAllLines' are System.IO calls
#          no row-array test has any reason to make; 'RepoRoot|TestPaths|CallerFilePath' is the repo's own
#          root-resolution idiom; and the "src/ path literal is the subject directory itself. A test that
#          carries all five is reading source. A test that carries none is the measured hollow anchor.
$ErrorActionPreference = 'Continue'

$ws = $env:GUARDRAILS_WORKSPACE
if ([string]::IsNullOrEmpty($ws)) { $ws = (Get-Location).Path }

# GR_SUBJECT is the `guardrails samples verify` contract (Samples/SampleVerifier.cs): the sample path
# arrives as argv[0] AND in $env:GR_SUBJECT, ABSOLUTE. Joining it to the workspace would yield a nonsense
# path and PRECONDITION-fail, which reads exactly like a real finding.
#   $env:GR_SUBJECT='<plan>/tasks/06-author-anchor-tests-hash-sites/samples/03-anchor-enumerates-the-set-not-a-count.valid.cs'   -> expect 0
#   $env:GR_SUBJECT='<plan>/tasks/06-author-anchor-tests-hash-sites/samples/03-anchor-enumerates-the-set-not-a-count.invalid.cs' -> expect 1
# RE-RUN EVERY case after ANY edit to this file, not just the clause you touched.
$rel  = if ($env:GR_SUBJECT) { $env:GR_SUBJECT } else { 'tests/Guardrails.Core.Tests/ExecutedDefinitionHashAnchorTests.cs' }
$full = if ([System.IO.Path]::IsPathRooted($rel)) { $rel } else { Join-Path $ws $rel }

# PRECONDITION - the one legitimate early exit: without the subject every clause below is meaningless.
if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
    Write-Output "PRECONDITION: $rel does not exist. It is this task's primary deliverable; guardrail 01 would have failed first if it were merely broken."
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

# --- REQUIRED: all eight (file, member) pairs are named ---------------------------------------------
# The eight surviving call sites of section 4.3's taxonomy, verified against the tree. Note two of them
# are in Guardrails.Cli rather than Guardrails.Core - a set anchored only over Core would miss them, and
# that is a real omission the pair-by-pair form catches and a count never could.
$sites = @(
    @{ File = 'Scheduler.cs';            Member = 'DetectDefinitionDrift' },
    @{ File = 'Scheduler.cs';            Member = 'BuildResolvedTasks' },
    @{ File = 'Scheduler.cs';            Member = 'ConsumePendingAnswers' },
    @{ File = 'Scheduler.cs';            Member = 'ClassifyTaskGateAsync' },
    @{ File = 'DryRun.cs';               Member = 'IsDrifted' },
    @{ File = 'DefinitionDriftProbe.cs'; Member = 'Evaluate' },
    @{ File = 'RunReset.cs';             Member = 'SafeComputeHash' },
    @{ File = 'WaveDefinitionHash.cs';   Member = 'Compute' }
)

foreach ($site in $sites) {
    # -cmatch: C# identifiers are case-SENSITIVE and PowerShell -match is not, so a case-insensitive
    # require-present clause false-GREENS on text C# would never compile (taxonomy 3).
    if ($code -cnotmatch [regex]::Escape($site.Member)) {
        $failures += "$rel never names the member '$($site.Member)' (expected in $($site.File)). Section 9 pins the enumerated SET of eight surviving TaskDefinitionHash.Compute call sites BY FILE AND MEMBER. A set that omits one is not a set - it is a smaller count with extra typing."
    }
}

foreach ($file in @('Scheduler.cs', 'DryRun.cs', 'DefinitionDriftProbe.cs', 'RunReset.cs', 'WaveDefinitionHash.cs')) {
    if ($code -cnotmatch [regex]::Escape($file)) {
        $failures += "$rel never names the file '$file'. Two of the eight sites (DryRun.cs, DefinitionDriftProbe.cs) live in Guardrails.Cli, not Guardrails.Core - an anchor that enumerated only Core would miss both, which a count-shaped assertion could never reveal."
    }
}

# --- REQUIRED: the four zero-occurrence files are named too ------------------------------------------
foreach ($file in @('AttemptJournaler.cs', 'TaskExecutor.cs', 'TaskNode.cs', 'WaveNode.cs')) {
    if ($code -cnotmatch [regex]::Escape($file)) {
        $failures += "$rel never names '$file'. Section 9: 'And ZERO in AttemptJournaler.cs, TaskExecutor.cs, TaskNode.cs, WaveNode.cs.' Those four are the write sites this plan removed and the two model types that must never mention a hasher at all; naming them is what makes a regression there fail loudly rather than quietly."
    }
}

# --- REQUIRED: the three remaining section 9 anchors are present -------------------------------------
foreach ($anchor in @('DefinitionHashAtLoad', 'DefinitionFilesAtLoad')) {
    if ($code -cnotmatch [regex]::Escape($anchor)) {
        $failures += "$rel never mentions $anchor. Section 9 asks for THREE more anchors beside the call-site set: the declaration shape (both captures are bodiless auto-properties and the model types name no hasher), the no-disk-fallback line check, and the no-identity-rebinding clone. All three are about these two members."
    }
}
# Anchor 3's exclusion, named. Section 9 states the no-fallback anchor as "no line in src contains both
# DefinitionHashAtLoad and Compute(" - which is UNSATISFIABLE as written, because section 5.2's own
# prescribed implementation is one line carrying both. PlanLoader.cs is the single capture site and the
# one place the pairing is correct; the anchor must exclude it, and the prompt pins this token so the
# clause and the prompt agree by construction rather than by luck.
if ($code -cnotmatch '\bPlanLoader\b') {
    $failures += "$rel never mentions PlanLoader. Section 9's no-disk-fallback anchor reads 'no line in src contains both DefinitionHashAtLoad and Compute(' - and as literally written it is UNSATISFIABLE, because section 5.2's own prescribed capture is exactly such a line inside PlanLoader.LoadTask. The anchor must EXCLUDE PlanLoader.cs and say why, or it false-reds a correct tree forever."
}

# ANCHOR 4 IS NOT CHECKED HERE, and that is a deliberate omission rather than an oversight (#478's
# ambient-vocabulary test). Every token it could key on - 'Directory', 'Action', 'with' - is ambient
# vocabulary in a C# test file: System.IO.Directory alone would satisfy a bare 'Directory' clause, so the
# check would be green on arrival and certify nothing. Anchor 4 is carried by the action prompt and by
# the anchor test itself; a weak clause here would be worse than none, because it would sit exactly where
# a reviewer looks for evidence.

# --- REQUIRED: the test READS SOMETHING ---------------------------------------------------------------
# MEASURED, not reasoned: without these five clauses a class of three string[] arrays and one
# Assert.Equal(Sites.Length, Sites.Distinct().Count()) exits ZERO against this guardrail. Every token
# clause above is satisfied by strings in an array. These five are what make the difference between an
# anchor test and a list of names that happens to be shaped like one.
#
# Reads $code (comments gone, string literals INTACT) - load-bearing, because the path literal and the
# member names this anchor pins ARE string literals; stripping them would make every clause here
# unsatisfiable, the mirror dead-end #470 warns about at the required polarity.
$readsSomething = [ordered]@{
    '\bTaskDefinitionHash\b' =
        "never names TaskDefinitionHash. The anchor's whole subject is where that call is made; a test that cannot name it is matching row strings against each other, not against src/."
    '\b(RepoRoot|TestPaths|CallerFilePath)\b' =
        "resolves no repo root (expected RepoRoot, TestPaths or CallerFilePath). The repo's anchor idiom resolves the root from the test file's OWN CallerFilePath - never AppContext.BaseDirectory, never a walk-up search for .git. Without a root the test cannot address src/ at all."
    '\b(EnumerateFiles|GetFiles)\b' =
        "enumerates no files. Anchor 1's SECOND direction - every TaskDefinitionHash.Compute occurrence anywhere in src maps to a known row - is the one that catches Risk 6's seventh site added later, and it is impossible without walking the tree. A test asserting only that the eight KNOWN rows are present is the useless half."
    '\b(ReadAllText|ReadAllLines)\b' =
        "reads no file contents. Every anchor in section 9 is a claim about the TEXT of src/ - the call sites, the declaration shape, the no-disk-fallback line check, the clone check. None of them can be evaluated without reading a file."
    '"src[/\\]' =
        'contains no "src/ path literal. The subject of every anchor is the src tree; a test that never addresses it is anchoring nothing. (Both separators are accepted - the repo builds these paths with Path.Combine and with literals.)'
}
foreach ($pattern in $readsSomething.Keys) {
    # -cmatch: C# identifiers are case-SENSITIVE and PowerShell -match is not, so a case-insensitive
    # require-present clause false-GREENS on text C# would never compile (taxonomy 3).
    if ($code -cnotmatch $pattern) {
        $failures += "$rel $($readsSomething[$pattern]) MEASURED: a class of three string[] arrays and one Assert.Equal(Sites.Length, Sites.Distinct().Count()) passes every OTHER clause in this guardrail and exits 0. These five clauses exist because that mutant was run, not because a hollow anchor seemed possible."
    }
}

# --- FORBIDDEN: a bare COUNT assertion ---------------------------------------------------------------
# Reads $scan (comments AND string literals gone), so a comment explaining WHY a count is forbidden -
# which a good implementer will want to write, and which this plan asks for - cannot trip the ban, and
# neither can a failure message that quotes the rule.
#
# Anchored NARROWLY, and the narrowing is measured rather than stylistic. A first draft banned any
# 'Assert.Equal(<digits>,' and any line ending in '.Count)'. Both false-red a CORRECT anchor test: the
# repo's anchor idiom asserts set HYGIENE with things like Assert.Equal(rows.Length, rows.Distinct().
# Count()), and 'Assert.Equal(0, unexpected.Count)' is the honest way to say "no occurrence outside the
# set" - which is the very direction section 9 asks for. What is actually forbidden is asserting that the
# NUMBER OF OCCURRENCES equals a nonzero literal, so the ban requires a nonzero expected value AND a
# counting noun on the other side.
$countForms = @(
    'Assert\s*\.\s*Equal\s*\(\s*[1-9]\d*\s*,[^)\r\n]*\b(Count|Compute|Matches|Occurrences|Length)\b',
    'Assert\s*\.\s*True\s*\([^)\r\n]*\.\s*Count\s*==\s*[1-9]\d*'
)
foreach ($form in $countForms) {
    if ($scan -cmatch $form) {
        $failures += "$rel contains a COUNT-shaped assertion (matched: $form). Section 9 forbids it in as many words: 'a bare count is a tautology magnet - an agent that meets a wrong number under retry pressure runs the grep and writes down whatever it says, installing the exact anti-pattern in the guardrail whose job is to prevent one.' The number the defeated draft used was 6 against a true 8. Assert the enumerated SET, by file and member, in BOTH directions - every known row present, and every occurrence in src mapping to a known row - so the failure NAMES the offending site."
    }
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== anchor shape: $($failures.Count) problem(s) in $rel ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    Write-Output ""
    Write-Output "This test is the only artifact in the plan whose value is repo-lifetime rather than run-lifetime (Risk 6: a seventh site added later by someone who has not read this document). A count cannot carry that; a named set can."
    exit 1
}
Write-Output "Anchor shape sound: all eight sites named by file and member, the four zero-occurrence files named, the shape anchors present, the test actually reads src/ (root, enumeration, file read, path literal, hasher named), and no count-shaped assertion."
exit 0
