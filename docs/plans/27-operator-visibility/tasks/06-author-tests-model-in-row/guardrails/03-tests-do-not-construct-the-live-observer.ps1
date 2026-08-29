# catches: a test that CONSTRUCTS a LiveRunObserver, or reflects into its privates, to "just check the
#          table directly" - the two things this task's action prompt forbids in prose and which,
#          until this file existed, NOTHING backed (#221: a prohibition with no structural guardrail is
#          free for a lazy or adversarial implementation to ignore).
#
# WHY IT IS NOT COSMETIC, and why the damage lands somewhere else entirely.
#   LiveRunObserver's constructor immediately calls AnsiConsole.Live(_table).StartAsync(...) and starts
#   a one-second Timer, and SPECTRE'S LIVE-DISPLAY LOCK IS PROCESS-WIDE. This repo has ALREADY had to
#   serialize its live-display tests for exactly that reason - commit b43232d, "serialize the
#   LiveDisplay tests - Spectre's exclusivity lock is PROCESS-wide". A construction inside a suite that
#   xUnit runs in parallel therefore does not fail HERE. It corrupts the output of whatever unrelated
#   test happened to be holding, or wanting, that lock, and it surfaces as a FLAKE at the 7-15 minute
#   terminal Integration gate, attributed to whichever test ran last. That is the most expensive shape
#   of failure this plan can produce: non-deterministic, late, and misattributed - and by then this
#   task is long green and merged.
#   The prompt's other prohibition - no reflection probe of the private table - has the same root: the
#   only reason to reach for reflection here is to observe the table, and observing the table means
#   constructing the observer. Both are banned, and the pure ModelCell / ModelCellFromRoute seams exist
#   precisely so neither is needed.
#
# THIS IS A FORBIDDEN-PRESENT CHECK, so it is GREEN ON ARRIVAL BY DESIGN and is NOT censused as
# pre-satisfied (#478's explicit carve-out: "a forbidden-present clause is *supposed* to be green
# before its task"). Its subject does not exist on the untouched tree at all - ModelInRowTests.cs is
# this task's own deliverable - so the precondition below fires today. That is the correct baseline for
# a check on a not-yet-authored file, and it is stated rather than left to be re-derived.
#
# #470 RECONCILIATION - run both directions before trusting this file, because a forbidden token that
# collides with something required is unsatisfiable BY CONSTRUCTION and dead-ends every attempt with
# coherent, actionable, wrong feedback:
#   guardrail <-> itself : the ONE required-present literal here is the bare identifier
#                          `LiveRunObserver`, and it trips NONE of the three bans below - each of them
#                          requires either a preceding `new ` plus a call paren, or a `typeof(...)` and
#                          a dotted reflection call, or a quoted private member name. The tests are
#                          REQUIRED to write `LiveRunObserver.ModelCell(...)` and
#                          `LiveRunObserver.AttemptModelSummary(...)`, and both are fine.
#   guardrail <-> prompt : MEASURED against tasks/06-author-tests-model-in-row/action.prompt.md -
#                          `new LiveRunObserver(` 0, `typeof(LiveRunObserver).Get` 0,
#                          `GetMethod("RebuildRows"` 0. The prompt says "do NOT construct a
#                          LiveRunObserver" in English; it never hands the agent the banned form as
#                          vocabulary.
#
# AND ONE BAN IS DELIBERATELY *NOT* WRITTEN, which is the more important half of the reconciliation.
# A blanket ban on `BindingFlags` was considered and REJECTED on evidence. This task's prompt tells the
# agent to read and mirror tests/Guardrails.Integration.Tests/ModelTiering/AttemptModelForwardingTests.cs,
# and that file's third test is a REFLECTION SWEEP using `BindingFlags` over every forwarding observer
# TYPE in the Cli assembly - a sweep that constructs nothing, touches no live region, and is a genuinely
# good check. Banning the token would be the #470 prompt<->guardrail collision exactly: the prompt
# points at a file containing the banned word and says "mirror it". So the bans below are anchored on
# the DANGEROUS USE - construction, and reflection aimed INTO LiveRunObserver - never on the mention of
# a reflection API (#76: anchor on a use, not a word).
#
# Author-time smoke test (#302), re-runnable (#468) - the subject is this task's own deliverable, so
# both samples are hand-synthesized. Run from the repo root:
#   $env:GR_SUBJECT='docs/plans/27-operator-visibility/tasks/06-author-tests-model-in-row/samples/03-tests-do-not-construct-the-live-observer.valid.cs';   ./docs/plans/27-operator-visibility/tasks/06-author-tests-model-in-row/guardrails/03-tests-do-not-construct-the-live-observer.ps1  # expect 0
#   $env:GR_SUBJECT='docs/plans/27-operator-visibility/tasks/06-author-tests-model-in-row/samples/03-tests-do-not-construct-the-live-observer.invalid.cs'; ./docs/plans/27-operator-visibility/tasks/06-author-tests-model-in-row/guardrails/03-tests-do-not-construct-the-live-observer.ps1  # expect 1
# The VALID half is the one that pays here and it was written to be hostile: it uses BindingFlags four
# times, calls typeof(LiveRunObserver).Assembly, constructs BOTH decorators, and writes the literal
# phrase "new LiveRunObserver(...)" inside a doc comment - every one of which a coarser ban would have
# false-RED on. MEASURED: exit 0. The INVALID half trips all three clauses at once, one message each.
$f = if ($env:GR_SUBJECT) { $env:GR_SUBJECT } else { "tests/Guardrails.Integration.Tests/ModelTiering/ModelInRowTests.cs" }

# PRECONDITION - the only early exit: every clause below would crash on a missing subject. Before this
# task's action runs, this fires - the file is what the task is for.
if (-not (Test-Path $f)) {
    Write-Output "$f does not exist - it is this task's primary deliverable and cannot be checked until it is written"
    exit 1
}

# THE TWO-VARIABLE RULE (catalogue): one strip, two levels, and each clause reads the level it needs.
$raw  = Get-Content $f -Raw                                  # NEVER matched against
$code = [regex]::Replace($raw,  '/\*[\s\S]*?\*/', '')        # /* */ block comments
$code = [regex]::Replace($code, '(?m)//.*$', '')             # // line comments
$scan = [regex]::Replace($code, '"""[\s\S]*?"""', '""')      # C# 11 raw strings
$scan = [regex]::Replace($scan, '@"(?:[^"]|"")*"', '""')     # verbatim strings
$scan = [regex]::Replace($scan, '"(\\.|[^"\\])*"', '""')     # ordinary strings

$failures = @()

# CLAUSE 0 - the POSITIVE CONTROL, and it is NOT a finding about the prohibitions. Every clause below
# passes by finding NOTHING, and a scan that never reached the file finds nothing too (#500: a
# zero-match probe has two readings and only one of them is a measurement). The tests are required to
# drive LiveRunObserver.ModelCell and LiveRunObserver.ModelCellFromRoute, so the bare identifier must
# be present; if it is not, the bans below certified a file nobody read. Reported separately so a retry
# agent reads "the scan found nothing at all", not "you violated a prohibition".
if ($scan -cnotmatch 'LiveRunObserver') {
    $failures += "$f never names LiveRunObserver outside a comment or a string. The Group A cell tests are required to drive LiveRunObserver.ModelCell(...) and LiveRunObserver.ModelCellFromRoute(...) directly, so this file cannot be correct without it - and until it is present, the three prohibition checks below are scanning a file that says nothing and would report a clean bill of health for any content at all."
}

# CLAUSE 1 - THE BAN THAT MATTERS. Anchored on the CONSTRUCTION (#76): `new` + the type + the call
# paren. A doc comment explaining why the type must not be constructed, and the words in a message
# string, both survive the strip and neither satisfies this.
if ($scan -cmatch 'new\s+LiveRunObserver\s*\(') {
    $failures += "$f CONSTRUCTS a LiveRunObserver. Its constructor immediately starts an AnsiConsole.Live region and a one-second Timer, and Spectre's live-display lock is PROCESS-WIDE - this repo already had to serialize its live-display tests for exactly that (commit b43232d). Constructing one inside a suite xUnit runs in parallel does not fail here: it corrupts an UNRELATED test's output and surfaces as a flake at the 7-15 minute terminal Integration gate, attributed to whatever ran last. Test the pure seams instead - LiveRunObserver.ModelCell(...) and LiveRunObserver.ModelCellFromRoute(...) are static and exist for exactly this reason. Constructing the two DECORATORS (OnTheFlyDiagramObserver, OnTheFlyLogSiteObserver) for the Group B forwarding pin is fine and is NOT what this bans."
}

# CLAUSE 2 - reflection aimed INTO LiveRunObserver. Note what this does NOT ban: `typeof(LiveRunObserver).Assembly`,
# which is how AttemptModelForwardingTests enumerates forwarding observer TYPES without touching an
# instance - a legitimate pattern this task's prompt points the agent at.
if ($scan -cmatch 'typeof\s*\(\s*LiveRunObserver\s*\)\s*\.\s*Get(?:Method|Field|Property|Member)') {
    $failures += "$f uses reflection to reach INTO LiveRunObserver (typeof(LiveRunObserver).GetMethod/GetField/GetProperty/GetMember). The only reason to do that here is to observe the private Spectre table - and observing the table means constructing the observer, which is banned above for a process-wide-lock reason. The table's contents are proven by a structural guardrail on the implementation task, and the formatter is proven by driving the pure static seams. (typeof(LiveRunObserver).Assembly is NOT banned: enumerating forwarding observer TYPES, the way AttemptModelForwardingTests does, constructs nothing.)"
}

# CLAUSE 3 reads $code, NOT $scan - a DELIBERATE deviation, stated so a reviewer can re-decide it. The
# thing being banned IS a string literal (a private member name passed to reflection), so stripping
# literals would make this clause unfirable: it could never match, the taxonomy-13 dead-end. Comments
# are still stripped, so prose naming RebuildRows - which the prompt itself does - does not trip it.
if ($code -cmatch 'Get(?:Method|Field|Property)\s*\(\s*"(?:RebuildRows|Update|Tick|_table|_rowByKey)"') {
    $failures += "$f reflects on one of LiveRunObserver's private members by name (RebuildRows / Update / Tick / _table / _rowByKey). Those are private precisely because they are only meaningful on a constructed, running live region - see the ban above. Drive the pure static seams instead; the wiring between them and the table is proven structurally on the implementation task, and that residual is stated there rather than papered over with a reflection probe here."
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Output $_ }
    exit 1
}
exit 0
