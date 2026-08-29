# catches: THREE distinct half-jobs, each of which compiles, passes every unit test this pair owns,
#          and ships the defect anyway.
#   (1) The live task row getting a Model COLUMN that nothing ever fills - "declaration is not
#       behaviour" (#468) in its most literal form. A header alone renders an always-empty column for
#       the life of the run, which is worse than no column: it answers "which model ran?" with a
#       blank, and a blank in a live table reads as "still resolving".
#       CLAUSE 5 is what delivers this one, and it was added because /guardrails-review MEASURED that
#       the header of this very file CLAIMED defect (1) and did not catch it: a mutant that declared
#       the column at Width(8), called `_ = ModelCell(...)` and discarded the result, dropped the
#       cell-3 write and seeded the row with string.Empty exited 0 against clauses 1-3.
#   (2) The opposite half - ModelCell implemented and unit-tested but never wired into the table, the
#       unwired-factory failure (#120) at cell granularity.
#   (3) THE ONE THIS PLAN'S REDESIGN ADDED, and the most expensive to discover late: the column wired
#       ONLY to AttemptModelResolved, the post-action event. That mutant declares the column, calls
#       ModelCell, renders a populated cell, and is STILL BROKEN, because AttemptModelResolved cannot
#       fire until the runner has reported what it ran on - MEASURED at 14m02s and longer per attempt
#       on docs/plans/24-plan-source-provenance/state/run.json. The cell would read its placeholder
#       for the entire attempt and fill in at the moment the row settles, i.e. exactly when the
#       operator no longer needs it live. That is docs/plans/29-model-visibility-ux.md section 1.1 in
#       one sentence.
#       THE PREVIOUS VERSION OF THIS FILE DID NOT CATCH (3) EITHER, and the way it failed is the
#       reason clause 4 now exists. Its check for (3) was "AttemptRouteResolved is declared and its
#       body is not literally `{}`" - which is satisfied by a handler containing one
#       AnsiConsole.MarkupLine and a TODO comment, feeding the cell from AttemptModelResolved as
#       before. That mutant is docs/plans/29-model-visibility-ux.md section 1.1 shipped VERBATIM (the
#       cell reads "(medium)" for the whole 14-minute attempt, which is the entire defect the event
#       was introduced to fix), and it exited 0 - MEASURED. Worse, the committed .invalid.cs sample
#       carried exactly that handler body, so the pair never exercised the mutant it most needed to.
#       An "is the body non-empty" test cannot distinguish a handler that DOES something from one
#       that does something IRRELEVANT. Clause 3 is KEPT (a missing or `{}` handler is still the
#       cheapest miss and deserves its own message), and clause 4 is added beside it as a check on the
#       OUTCOME the handler must produce: it must call ModelCellFromRoute, the pure translation seam
#       that exists for exactly this purpose. Clause 3 asks "is there a handler"; clause 4 asks "does
#       it do the job".
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
#   AND THE PURE SEAM IS WHERE MOST OF THE WIRING WENT (#468, the demotion applied in the direction it
#   is meant to be applied). The route handler's real job is a TRANSLATION - `climbed` is
#   `requestedTier is not null`, because requestedTier is written ONLY on a section 6.2 climb, so its
#   PRESENCE is the signal - and a translation living inline in a handler on an unconstructable type is
#   unreachable from any test. 06-author-tests-model-in-row therefore declares
#   `ModelCellFromRoute(string runner, string? tier, string? requestedTier)` as a second stub and pins
#   it with an AGREEMENT property test (it must equal ModelCell(runner, tier, requestedTier is not
#   null, false, false) for every input in the domain). That turns the untestable hop into a testable
#   pure function and shrinks what NO test can reach to two statements: call it, write the result into
#   the cell. Clause 4 requires the call; clause 5 requires the cell write.
#   HONEST RESIDUALS, stated rather than implied, and both narrower than before:
#     * A regex still cannot prove the ModelCellFromRoute result computed in the ROUTE handler is the
#       value that reaches cell 3. A handler that calls the seam and DISCARDS the result while the cell
#       is written from AttemptModelResolved satisfies clauses 4 and 5 separately. That shape is no
#       longer the lazy path (the prompt hands the agent the one-liner that does both), but it is not
#       excluded - /guardrails-review should re-check it by reading the diff.
#     * A regex cannot prove the cell index is the RIGHT row. Clause 5 sees a write to column 3.
#   The design names this family of gaps and accepts it (section 6, "You have not proved the column is
#   populated") - but "accepted" now means these two, not the whole of defects (1) and (3).
#
# Author-time smoke test (#302), re-runnable (#468) - run from the repo root. THREE samples, because
# there are two distinct wrong implementations and they fail different clauses:
#   samples/03-live-table-has-a-populated-model-column.valid.cs               -> expect 0
#   samples/03-live-table-has-a-populated-model-column.invalid.cs             -> expect 1 (column declared, cell never filled)
#   samples/03-live-table-has-a-populated-model-column.invalid-inert-route-handler.cs
#                                                                             -> expect 1 (clause 4: the route
#                                                                                handler is declared, non-empty and
#                                                                                INERT; the cell is fed from the
#                                                                                post-action event, which is section
#                                                                                1.1 shipped verbatim)
# e.g.
#   $env:GR_SUBJECT='docs/plans/27-operator-visibility/tasks/07-render-model-in-row-and-index/samples/03-live-table-has-a-populated-model-column.valid.cs';   ./docs/plans/27-operator-visibility/tasks/07-render-model-in-row-and-index/guardrails/03-live-table-has-a-populated-model-column.ps1  # expect 0
#   $env:GR_SUBJECT='docs/plans/27-operator-visibility/tasks/07-render-model-in-row-and-index/samples/03-live-table-has-a-populated-model-column.invalid.cs'; ./docs/plans/27-operator-visibility/tasks/07-render-model-in-row-and-index/guardrails/03-live-table-has-a-populated-model-column.ps1  # expect 1
# RE-RUN ALL THREE after ANY edit to this file, not just the clause you touched.
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
#                                                             today's tree, where it is 0. Task
#                                                             06-author-tests-model-in-row
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
#   ModelCellFromRoute\s*\(                                1  ON THE TREE THIS TASK SEES; 0 on today's
#                                                             tree. Same reasoning and same floor as
#                                                             ModelCell above: 06-author-tests-model-in-row
#                                                             writes the stub DECLARATION
#                                                             `public static string
#                                                             ModelCellFromRoute(string, string?,
#                                                             string?) => throw ...`, so >= 1 would be
#                                                             pre-satisfied by an ancestor (#478) and
#                                                             the clause is a FLOOR of 2. Note
#                                                             `ModelCellFromRoute(` does NOT match
#                                                             `ModelCell\s*\(` - that pattern requires
#                                                             the paren immediately after `ModelCell` -
#                                                             so the two floors are independent and
#                                                             neither inflates the other. VERIFIED by
#                                                             running both patterns over the samples.
#   void\s+AttemptRouteResolved\s*\(                       0  on the untouched tree AND on the tree
#                                                             this task sees. Task
#                                                             05-raise-attempt-route-resolved adds the member
#                                                             to IRunObserver.cs and forwards it from
#                                                             the two DECORATORS; no task before this
#                                                             one writes it into LiveRunObserver.cs,
#                                                             which is the leaf observer. So clause 3
#                                                             is not pre-satisfied by an ancestor
#                                                             either.
#   clause 5's FULL alternation, verbatim                  0  on the untouched tree - NOT pre-satisfied.
#     UpdateCell\s*\(\s*[^,]+,\s*                             The POSITIVE CONTROL that proves this zero
#       (?:3\b|(?:[A-Za-z_][A-Za-z0-9_.]*)?                   is a measurement rather than a search that
#             [Mm]odel[A-Za-z0-9_.]*)\s*,                     never opened the file: `UpdateCell\s*\(`
#                                                             measures 9, of which FIVE write cell index
#                                                             1 and FOUR write cell index 2 - 9 = 5 + 4,
#                                                             every one accounted for, none at index 3.
#                                                             The named-constant half measures 0 too, so
#                                                             accepting it costs nothing.
#     Ten hand-written probes were run against the pattern itself, and they are the reason it is
#     written the way it is. ACCEPTED (1 hit each): `UpdateCell(row, 3, ...)`,
#     `UpdateCell(row, ModelColumnIndex, ...)`, `UpdateCell(row, _modelColumnIndex, ...)`,
#     `UpdateCell(row, Columns.ModelIndex, ...)`, `UpdateCell(RowOf(taskId), 3, ...)`.
#     REJECTED (0 hits each): `UpdateCell(row, StatusColumnIndex, ...)` - refactoring an existing STATUS
#     write must not satisfy a MODEL clause - and indices 1, 2, 13 and 30 (the `\b` is what stops a
#     3-prefixed index from matching).
#
# WHY NEITHER FLOOR IS RAISED TO 3, although the design names three write moments (the RebuildRows
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
# The FLOOR of 2, not a presence check: the declaration itself matches `ModelCell(`, and
# 06-author-tests-model-in-row has
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
# in the header, the whole reason 05-raise-attempt-route-resolved exists, and invisible to guardrail 01
# and guardrail 02 alike.
# Two parts, because the laziest way past part (a) is an empty body:
$declaresRoute = [regex]::IsMatch($scan, 'void\s+AttemptRouteResolved\s*\(')
if (-not $declaresRoute) {
    $failures += "$f does not DECLARE AttemptRouteResolved - so the launch-time route disclosure 05-raise-attempt-route-resolved added resolves to IRunObserver's empty default body and never reaches this observer. The Model column would then be fed ONLY by AttemptModelResolved, which cannot fire until the runner has reported what it ran on (MEASURED at 14m02s and longer per attempt on docs/plans/24-plan-source-provenance/state/run.json) - so the cell reads its placeholder for the whole attempt and fills in exactly when the operator no longer needs it live. Declare 'public void AttemptRouteResolved(TaskNode task, int attempt, string runner, string model, string? tier, string? requestedTier)' and write the cell from it; keep AttemptModelResolved as the confirmation or correction."
}
elseif ($scan -match 'void\s+AttemptRouteResolved\s*\([^)]*\)\s*\{\s*\}') {
    $failures += "$f declares AttemptRouteResolved with an EMPTY BODY - which is exactly as useless as not declaring it, and harder to spot in review because the member is there. The launch-time route is the ONLY source that can fill the Model cell while the attempt is still running. Write the cell from it."
}

# CLAUSE 4 - THE PURE SEAM THE ROUTE HANDLER MUST GO THROUGH, and the clause that turns clause 3 from a
# vocabulary check into a capability check. Clause 3 asks "is a handler DECLARED and is its body not
# literally empty" - and MEASURED, a handler containing one AnsiConsole.MarkupLine and a TODO satisfies
# both while the cell keeps being fed from the post-action event. "Non-empty" and "does the job" are
# different properties, and only the second one matters.
# ModelCellFromRoute is the pure translation seam 06-author-tests-model-in-row declares and pins with an
# AGREEMENT property test. It exists for exactly ONE caller - the route handler - because its arguments
# ARE that handler's arguments: (runner, tier, requestedTier). AttemptModelResolved has no runner and no
# tier, so it cannot naturally call this. Requiring the CALL therefore requires the translation the
# handler exists to perform, and a hollow implementation of the seam is already impossible: the census
# in 06's guardrail 02 proved the pinned test RED against the stub, and guardrail 02 here proves it
# GREEN afterwards.
# FLOOR OF 2 for the same #478 reason as clause 2: the ancestor's stub declaration is 1.
$fromRouteUses = ([regex]::Matches($scan, 'ModelCellFromRoute\s*\(')).Count
if ($fromRouteUses -lt 2) {
    $failures += "$f references ModelCellFromRoute as a CALL $fromRouteUses time(s); at least 2 are required (its own declaration, plus at least one call site). This is the clause that separates a route handler which DOES THE JOB from one that merely EXISTS: a handler declared with a non-empty body that logs a line and leaves the cell to AttemptModelResolved passes every other clause here and ships docs/plans/29-model-visibility-ux.md section 1.1 VERBATIM - the cell reads its placeholder for the whole 14-minute attempt, which is the entire defect the launch event was introduced to fix. ModelCellFromRoute(runner, tier, requestedTier) is the pure seam authored for exactly this call: its arguments ARE the route handler's arguments, and it is what carries the rule that requestedTier's PRESENCE is the section 6.2 climb signal. Call it from AttemptRouteResolved and write its result into the row's Model cell. A mention is not a call: nameof(ModelCellFromRoute), a comment, or the name in a message string does NOT satisfy this."
}

# CLAUSE 5 - THE CELL WRITE, which is defect (1) in this file's own catches header and which this file
# did NOT deliver until /guardrails-review MEASURED the gap. A column DECLARED, ModelCell CALLED and the
# result DISCARDED (`_ = ModelCell(...)`), no write to cell index 3 and the row seeded with
# string.Empty, exited 0 against clauses 1-3 - a Model column that nothing ever fills, which is the
# exact sentence the header promised to catch.
# TWO SPELLINGS ARE ACCEPTED, and neither is pre-satisfied (both measured 0). The literal index 3 is the
# house form - all nine existing UpdateCell calls in this file use a literal (five write 1, four write
# 2) - and the prompt pins the Model column as index 3 because it is appended LAST. A named constant
# (any identifier containing "Model", e.g. ModelColumn or _modelCellIndex) is the one other shape a
# careful implementation might reach for, so it is accepted too: refusing it would be a check a correct
# implementation can fail, which is worse than the marginal strength it would buy. An identifier NOT
# containing "Model" is deliberately not accepted - that would let a refactor of an existing STATUS cell
# write satisfy this clause without touching the model at all.
# Note this cannot be satisfied by RebuildRows' AddRow seeding: AddRow adds a whole row, it does not
# update a cell, and RebuildRows runs only at construction and at a wave collapse - so it can never
# reflect an event that arrives mid-run (this file's own comment says so: "Everything that then repeats
# per second goes through UpdateCell, never here").
# The identifier alternative's leading segment is OPTIONAL - `(?:[A-Za-z_][A-Za-z0-9_.]*)?` - and that
# is not cosmetic. Written as a REQUIRED prefix it could never match `ModelColumnIndex`, the single most
# likely constant name, because the required first character consumed the `M`. MEASURED: 0 hits against
# `UpdateCell(row, ModelColumnIndex, ...)`. That is a clause that can never fire (taxonomy 13), and it
# was invisible to the invalid sample - only running the VALID half of a hand-written probe exposes it.
$cellWrite = 'UpdateCell\s*\(\s*[^,]+,\s*(?:3\b|(?:[A-Za-z_][A-Za-z0-9_.]*)?[Mm]odel[A-Za-z0-9_.]*)\s*,'
if ($scan -cnotmatch $cellWrite) {
    $failures += "$f never WRITES the Model cell - there is no UpdateCell(<row>, 3, ...) (nor an UpdateCell whose column index is a Model-named constant) anywhere in this file. The column can be declared at Width(8), ModelCell and ModelCellFromRoute can both be called, and the table still renders an always-empty fourth column for the life of the run - which is worse than no column at all, because a blank cell in a live table reads as 'still resolving' about a task that is running healthily on a route the harness resolved before it launched. The Model column is appended LAST, so its cell index is 3; write it the way this file already writes cells 1 and 2 (nine existing UpdateCell calls), from the same place you call ModelCellFromRoute. Computing the cell and discarding it - `_ = ModelCell(...)` - is the measured mutant this clause exists for."
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Output $_ }
    exit 1
}
exit 0
