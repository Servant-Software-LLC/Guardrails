# catches: THREE distinct half-jobs, each of which compiles, passes every unit test this pair owns,
#          and ships the defect anyway.
#   (1) The live task row getting a Model COLUMN that nothing ever fills - "declaration is not
#       behaviour" (#468) in its most literal form. A header alone renders an always-empty column for
#       the life of the run, which is worse than no column: it answers "which model ran?" with a
#       blank, and a blank in a live table reads as "still resolving".
#   (2) The opposite half - ModelCell implemented and unit-tested but never wired into the table, the
#       unwired-factory failure (#120) at cell granularity.
#   (3) THE ONE THIS PLAN'S REDESIGN ADDED, and the most expensive to discover late: the column wired
#       ONLY to AttemptModelResolved, the post-action event. That mutant declares the column, calls
#       ModelCell, renders a populated cell, and is STILL BROKEN, because AttemptModelResolved cannot
#       fire until the runner has reported what it ran on - MEASURED at 14m02s and longer per attempt
#       on docs/plans/24-plan-source-provenance/state/run.json. The cell would read its placeholder
#       for the entire attempt and fill in at the moment the row settles, i.e. exactly when the
#       operator no longer needs it live. That is docs/plans/29-model-visibility-ux.md section 1.1 in
#       one sentence, and clause 3 below is the only deterministic check that sees it.
#
# WHY THIS IS A SOURCE GREP AND NOT A TEST (#468 demotion gate, rung 3 - stated because an
# unexplained source-shape check on a behavioural claim is itself a finding):
#   The live table is a PRIVATE Spectre `Table` field. Constructing a LiveRunObserver to observe it is
#   not an option: the constructor immediately calls AnsiConsole.Live(_table).StartAsync(...) and
#   starts a 1-second Timer, and Spectre's live-display lock is PROCESS-WIDE - this repo has already
#   had to serialize its live-display tests for exactly that reason (b43232d), and a suite that
#   constructs one in parallel corrupts unrelated tests' output. RebuildRows() and Update() are
#   private, and LiveTableRows.Plan() carries no cell content at all (it returns row-KIND records). So
#   there is no runtime observation of this property, at any cost this plan can pay.
#   What IS behavioural was demoted into a test instead: the pure ModelCell formatter is driven
#   directly by ModelInRowTests (guardrail 02) across the six section 4.2 states the signature can
#   express - the three row-build states and the three resolved ones. (Section 4.2's `no route` cell
#   is NOT among them and no test asserts it: TaskExecutor settles a no-route attempt and RETURNS
#   before the raise site section 4.3 pins, so the event cannot carry that state. Stated here so the
#   absence reads as a decision rather than an oversight.)
#   This file covers only the wiring the formatter cannot see.
#   HONEST RESIDUAL, stated rather than implied: a regex sees that the column is declared, that
#   ModelCell is CALLED somewhere in this file, and that the launch-time event is DECLARED here rather
#   than inherited as the interface's empty default. It does NOT prove the call's result lands in the
#   Model cell of the right row, and it does NOT prove the route handler's body writes anything.
#   /guardrails-review should re-check that residual by reading the diff. The design names this exact
#   gap and accepts it (section 6, "You have not proved the column is populated").
#
# Author-time smoke test (#302), re-runnable (#468) - run from the repo root:
#   $env:GR_SUBJECT='docs/plans/27-operator-visibility/tasks/06-render-model-in-row-and-index/samples/03-live-table-has-a-populated-model-column.valid.cs';   ./docs/plans/27-operator-visibility/tasks/06-render-model-in-row-and-index/guardrails/03-live-table-has-a-populated-model-column.ps1  # expect 0
#   $env:GR_SUBJECT='docs/plans/27-operator-visibility/tasks/06-render-model-in-row-and-index/samples/03-live-table-has-a-populated-model-column.invalid.cs'; ./docs/plans/27-operator-visibility/tasks/06-render-model-in-row-and-index/guardrails/03-live-table-has-a-populated-model-column.ps1  # expect 1
#
# baseline counts - RE-MEASURED 2026-08-29 against this exact subject
# (src/Guardrails.Cli/Ui/LiveRunObserver.cs) with the same case sensitivity and the same strip level
# each clause actually uses, at the two different moments the clauses see. Not copied, not inherited:
#   AddColumn\s*\(\s*(new\s+TableColumn\s*\(\s*)?"Model"   0  on the untouched tree.
#   AddColumn\s*\(\s*"Model"\s*\)                          0  on the untouched tree - the OLD, narrow
#                                                             form, kept here as the record of why it
#                                                             had to be widened: the recommended
#                                                             construction is now
#                                                             AddColumn(new TableColumn("Model").Width(8)),
#                                                             which the old pattern does NOT match, so
#                                                             a correct implementation would have
#                                                             red-failed it. Both spellings are
#                                                             accepted below; neither is pre-satisfied.
#   AddColumn\s*\(\s*"                                     3  the POSITIVE CONTROL for clause 1 -
#                                                             AddColumn("Task"), AddColumn("Status"),
#                                                             AddColumn("Detail"). A zero on the
#                                                             Model patterns is a measurement, not a
#                                                             search that never opened the file.
#   ModelCell\s*\(                                         1  ON THE TREE THIS TASK SEES - not on
#                                                             today's tree, where it is 0. Task 05
#                                                             (this task's dependency) writes the stub
#                                                             DECLARATION
#                                                             `public static string ModelCell(string?,
#                                                             string?, bool, bool, bool) => throw ...`,
#                                                             which matches. That is why the clause is
#                                                             a FLOOR of 2 (declaration + at least one
#                                                             call) and not a presence check: a
#                                                             presence check would be pre-satisfied by
#                                                             the ancestor's stub, the #478 defect
#                                                             exactly. 1 < 2, so the floor is not
#                                                             pre-cleared.
#   void\s+AttemptRouteResolved\s*\(                       0  on the untouched tree AND on the tree
#                                                             this task sees. Task 04 adds the member
#                                                             to IRunObserver.cs and forwards it from
#                                                             the two DECORATORS; no task before this
#                                                             one writes it into LiveRunObserver.cs,
#                                                             which is the leaf observer. So clause 3
#                                                             is not pre-satisfied by an ancestor
#                                                             either.
#
# WHY THE FLOOR IS NOT RAISED TO 3, although the design names three write moments (the RebuildRows
# seed, the route event and the model event). A correct implementation may legitimately store the
# per-task cell state and funnel all three through ONE private writer that calls ModelCell once - that
# shape yields exactly 2 occurrences, and a floor of 3 would red-fail it. A check no correct
# implementation can pass is worse than a weaker one (GR2055's polarity).
$f = if ($env:GR_SUBJECT) { $env:GR_SUBJECT } else { "src/Guardrails.Cli/Ui/LiveRunObserver.cs" }

# PRECONDITION - the only early exit: every clause below would crash on a missing subject.
if (-not (Test-Path $f)) {
    Write-Output "$f does not exist - cannot verify the live table's Model column"
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

# CLAUSE 1 reads $code, NOT $scan - a DELIBERATE deviation from the strip-everything default, stated so
# a reviewer can re-decide it. The token being required IS a string literal (a Spectre column header),
# so stripping literals would make the clause unfirable: it could never match a correct file, the
# taxonomy-13 dead-end. Comments are still stripped, so a `// AddColumn("Model")` note does not satisfy
# it. -cnotmatch: C# is case-sensitive and a `"model"` header is not what the prompt pins.
#
# THE PATTERN IS DELIBERATELY WIDER THAN THE HEADER STRING IT LOOKS LIKE. The recommended construction
# is `AddColumn(new TableColumn("Model").Width(8))` (design section 4.1 - Width(8) is measured, not a
# preference), and the bare `AddColumn("Model")` form is still a legitimate way to declare the column
# if the width is configured elsewhere. Both must pass, so the `new TableColumn(` prefix is OPTIONAL
# and the trailing `)` is NOT required - anchoring on `"Model"` is enough to distinguish this column
# from Task/Status/Detail, and requiring the close-paren immediately would red-fail the very
# construction the design recommends.
if ($code -cnotmatch 'AddColumn\s*\(\s*(new\s+TableColumn\s*\(\s*)?"Model"') {
    $failures += "$f does not add a `"Model`" column to the live table - neither AddColumn(`"Model`") nor AddColumn(new TableColumn(`"Model`")...) is present, so the run's task rows still show only Task / Status / Detail and the model the run resolved is still invisible after the task finishes (#524). Append it LAST: Update() and Tick() write hard-coded cell indices 1 and 2, so inserting a column ahead of them silently re-targets every one. The recommended construction is AddColumn(new TableColumn(`"Model`").Width(8)) - the width bound is measured (design section 4.1), and .NoWrap() is NOT wanted, because a truncated block name is a lie about which model ran."
}

# CLAUSE 2 reads $scan - `ModelCell` is a C# IDENTIFIER, so a mention inside an operator-facing message
# string must not satisfy it. ANCHORED ON THE CALL PAREN (#76 / issue #521): the earlier form of this
# rule matched a dotted NAME, and `nameof(LiveRunObserver.ModelCell)` is valid C# containing that exact
# text - measured on plan 24, a mutant whose only references were inside nameof() with ZERO invocations
# exited 0 against the name-only clause. ModelCell IS a method, so requiring the paren cannot false-red
# a correct file (requiring a paren against a PROPERTY would be the mirror mistake), and
# `nameof(...ModelCell)` is followed by `)`, never `(`, so it does not satisfy this.
# The FLOOR of 2, not a presence check: the declaration itself matches `ModelCell(`, and task 05 has
# already written that declaration, so >= 1 is pre-satisfied by an ancestor's stub. >= 2 means the
# declaration PLUS at least one call site.
$modelCellUses = ([regex]::Matches($scan, 'ModelCell\s*\(')).Count
if ($modelCellUses -lt 2) {
    $failures += "$f references ModelCell as a CALL $modelCellUses time(s); at least 2 are required (its own declaration, plus at least one call site). ModelCell is implemented and unit-tested but never invoked from this file, so the Model column renders empty for the whole run - a header with nothing under it. Call it where the row's cells are built and updated. A mention is not a call: nameof(ModelCell), a comment, or the name in a message string does NOT satisfy this."
}

# CLAUSE 3 - THE EVENT THAT FILLS THE CELL AT LAUNCH, and the clause the plan-27 redesign added. It
# reads $scan for the same reason as clause 2: this is an identifier, and the file is EXPECTED to carry
# explanatory prose naming the member.
#
# WHY A DECLARATION CHECK IS THE RIGHT SHAPE HERE, and why the compiler cannot substitute for it:
# IRunObserver.AttemptRouteResolved has a DEFAULT NO-OP BODY. LiveRunObserver is the LEAF observer, so
# an implementation that simply never declares the member COMPILES CLEANLY, satisfies the interface,
# and quietly falls back to feeding the column from AttemptModelResolved alone - which is defect (3)
# in the header, the whole reason task 04 exists, and invisible to guardrail 01 and guardrail 02 alike.
# Two parts, because the laziest way past part (a) is an empty body:
$declaresRoute = [regex]::IsMatch($scan, 'void\s+AttemptRouteResolved\s*\(')
if (-not $declaresRoute) {
    $failures += "$f does not DECLARE AttemptRouteResolved - so the launch-time route disclosure task 04 added resolves to IRunObserver's empty default body and never reaches this observer. The Model column would then be fed ONLY by AttemptModelResolved, which cannot fire until the runner has reported what it ran on (MEASURED at 14m02s and longer per attempt on docs/plans/24-plan-source-provenance/state/run.json) - so the cell reads its placeholder for the whole attempt and fills in exactly when the operator no longer needs it live. Declare 'public void AttemptRouteResolved(TaskNode task, int attempt, string runner, string model, string? tier, string? requestedTier)' and write the cell from it; keep AttemptModelResolved as the confirmation or correction."
}
elseif ($scan -match 'void\s+AttemptRouteResolved\s*\([^)]*\)\s*\{\s*\}') {
    $failures += "$f declares AttemptRouteResolved with an EMPTY BODY - which is exactly as useless as not declaring it, and harder to spot in review because the member is there. The launch-time route is the ONLY source that can fill the Model cell while the attempt is still running. Write the cell from it."
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Output $_ }
    exit 1
}
exit 0
