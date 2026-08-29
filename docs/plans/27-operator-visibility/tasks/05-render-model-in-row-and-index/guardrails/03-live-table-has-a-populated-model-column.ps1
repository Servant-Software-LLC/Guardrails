# catches: the live task row getting a Model COLUMN that nothing ever fills - "declaration is not
#          behaviour" (#468) in its most literal form. A header alone renders an always-empty column
#          for the life of the run, which is worse than no column: it answers "which model ran?" with
#          a blank, and a blank in a live table reads as "still resolving". It also catches the
#          opposite half-job - ModelCell implemented and unit-tested but never wired into the table,
#          the unwired-factory failure (#120) at cell granularity.
#
# WHY THIS IS A SOURCE GREP AND NOT A TEST (#468 demotion gate, rung 3 - stated because an
# unexplained source-shape check on a behavioural claim is itself a finding):
#   The live table is a PRIVATE Spectre `Table` field. Constructing a LiveRunObserver to observe it is
#   not an option: the constructor immediately calls AnsiConsole.Live(_table).StartAsync(...) and
#   starts a 1-second Timer, and Spectre's live-display lock is PROCESS-WIDE - this repo has already
#   had to serialize its live-display tests for exactly that reason, and a suite that constructs one
#   in parallel corrupts unrelated tests' output. RebuildRows() and Update() are private, and
#   LiveTableRows.Plan() carries no cell content at all (it returns row-KIND records). So there is no
#   runtime observation of this property, at any cost this plan can pay.
#   What IS behavioural was demoted into a test instead: the pure ModelCell formatter is driven
#   directly by ModelInRowTests (guardrail 02). This file covers only the wiring the formatter cannot
#   see.
#   HONEST RESIDUAL, stated rather than implied: a regex sees that the column is declared and that
#   ModelCell is CALLED somewhere in this file. It does NOT prove the call's result lands in the Model
#   cell of the right row. /guardrails-review should re-check that residual by reading the diff.
#
# Author-time smoke test (#302), re-runnable (#468) - run from the repo root:
#   $env:GR_SUBJECT='docs/plans/27-operator-visibility/tasks/05-render-model-in-row-and-index/samples/03-live-table-has-a-populated-model-column.valid.cs';   ./docs/plans/27-operator-visibility/tasks/05-render-model-in-row-and-index/guardrails/03-live-table-has-a-populated-model-column.ps1  # expect 0
#   $env:GR_SUBJECT='docs/plans/27-operator-visibility/tasks/05-render-model-in-row-and-index/samples/03-live-table-has-a-populated-model-column.invalid.cs'; ./docs/plans/27-operator-visibility/tasks/05-render-model-in-row-and-index/guardrails/03-live-table-has-a-populated-model-column.ps1  # expect 1
#
# baseline counts - MEASURED with the same case sensitivity as each clause's operator, over this exact
# subject, at the two different moments the two clauses actually see:
#   AddColumn\s*\(\s*"Model"\s*\)   0  on the untouched tree (the table declares exactly
#                                     AddColumn("Task"), AddColumn("Status"), AddColumn("Detail")).
#                                     No ancestor task writes this token into this subject: task 04's
#                                     writeScope covers this file but its prompt adds ONLY the
#                                     `ModelCell` stub, and no other task in plan 25 names this file.
#   ModelCell\s*\(                 1  ON THE TREE THIS TASK SEES - not on today's tree, where it is 0.
#                                     Task 10 (this task's dependency) writes the stub DECLARATION
#                                     `public static string ModelCell(string?, string?) => throw ...`,
#                                     which matches. That is why the clause is a FLOOR of 2
#                                     (declaration + at least one call) and not a presence check: a
#                                     presence check would be pre-satisfied by the ancestor's stub,
#                                     the #478 defect exactly. 1 < 2, so the floor is not pre-cleared.
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
if ($code -cnotmatch 'AddColumn\s*\(\s*"Model"\s*\)') {
    $failures += "$f does not add a `"Model`" column to the live table - AddColumn(`"Model`") is absent, so the run's task rows still show only Task / Status / Detail and the model the run resolved is still invisible after the task finishes (#524). Append it LAST: Update() and Tick() write hard-coded cell indices 1 and 2, so inserting a column ahead of them silently re-targets every one."
}

# CLAUSE 2 reads $scan - `ModelCell` is a C# IDENTIFIER, so a mention inside an operator-facing message
# string must not satisfy it. ANCHORED ON THE CALL PAREN (#76 / issue #521): the earlier form of this
# rule matched a dotted NAME, and `nameof(LiveRunObserver.ModelCell)` is valid C# containing that exact
# text - measured on plan 24, a mutant whose only references were inside nameof() with ZERO invocations
# exited 0 against the name-only clause. ModelCell IS a method, so requiring the paren cannot false-red
# a correct file (requiring a paren against a PROPERTY would be the mirror mistake), and
# `nameof(...ModelCell)` is followed by `)`, never `(`, so it does not satisfy this.
# The FLOOR of 2, not a presence check: the declaration itself matches `ModelCell(`, and task 04 has
# already written that declaration, so >= 1 is pre-satisfied by an ancestor's stub. >= 2 means the
# declaration PLUS at least one call site.
$modelCellUses = ([regex]::Matches($scan, 'ModelCell\s*\(')).Count
if ($modelCellUses -lt 2) {
    $failures += "$f references ModelCell as a CALL $modelCellUses time(s); at least 2 are required (its own declaration, plus at least one call site). ModelCell is implemented and unit-tested but never invoked from this file, so the Model column renders empty for the whole run - a header with nothing under it. Call it where the row's cells are built/updated. A mention is not a call: nameof(ModelCell), a comment, or the name in a message string does NOT satisfy this."
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Output $_ }
    exit 1
}
exit 0
