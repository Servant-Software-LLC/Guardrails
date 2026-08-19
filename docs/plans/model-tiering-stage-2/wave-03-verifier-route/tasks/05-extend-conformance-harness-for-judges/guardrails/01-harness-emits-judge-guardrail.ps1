# catches: two things, and the second is the one that matters.
#
#   (a) A harness that compiles but still cannot express a prompt-JUDGE guardrail. Today it writes a
#       trivially-passing DETERMINISTIC guardrail per task (01-ok.cmd / exit 0), so every wave-3
#       clause about how a judge resolves is unwritable. Task 06 would then burn its budget
#       discovering that its own dependency did not deliver.
#
#   (b) A harness that reaches for TierResolver to answer the question. THIS IS THE EXPENSIVE ONE.
#       A harness that asks the resolver what it WOULD have chosen produces clauses that PASS against
#       a completely unwired GuardrailRunner - proving the resolver (waves 1-2 already did) and
#       saying nothing about whether anything CALLS it. That is #382's green-light-over-a-broken-wire,
#       and it is the cheapest possible way to turn five red clauses green. Wave 2's own harness-shape
#       guardrail carries the identical prohibition; this task now EDITS that file, and wave 2's
#       guardrail never re-runs - so without this clause the prohibition would be unenforced for the
#       one task able to violate it.
#
# TOKENS ARE MEASURED, NOT ASSUMED. An earlier draft of this guardrail keyed on `.prompt.md` with the
# comment "appears nowhere else" - it appears TWICE in this very file already (the ACTION prompt, at
# the task-folder write), so the clause was satisfied on arrival and one unused constant would have
# passed the whole task. The two identifiers below were counted across src/ and tests/ before being
# pinned in the prompt:  Stage2GuardrailSpec = 0,  JudgeGuardrail = 0.
#
# SOUND ABSENCE / PRESENCE ONLY (#468) for the structural half. Clause (c) is the BEHAVIOURAL half and
# is the one that actually proves something: it runs the existing suite through the modified harness.
$ErrorActionPreference = 'Continue'
$file = 'tests/Guardrails.Integration.Tests/ModelTiering/Stage2PlanHarness.cs'
$failures = @()

if (-not (Test-Path $file)) {
    Write-Output "$file does not exist - the shared conformance harness is this task's deliverable, and task 06 builds on it"
    exit 1
}
$raw = Get-Content -Raw $file
# Comment- and string-stripped for the PROHIBITION scan (#97/#98 + #470): a name inside a comment or
# a string literal is a mention, not a consultation, and a raw scan would red-flag prose that merely
# EXPLAINS the rule - including the header this task's own prompt asks the author to write.
$code = [regex]::Replace($raw, '/\*[\s\S]*?\*/', '')
$code = [regex]::Replace($code, '(?m)//.*$', '')
$code = [regex]::Replace($code, '"""[\s\S]*?"""', '""')
$code = [regex]::Replace($code, '@"(?:[^"]|"")*"', '""')
# The ordinary-string pattern is BUILT from [char]92 rather than written with escaped
# backslashes. Authoring it literally produced '"(\.|[^"\])*"' - an INVALID regex
# ("Unterminated [] set") that THROWS at runtime. With ErrorActionPreference='Continue' the
# throw is not fatal: the strip is silently skipped, string literals survive, and the
# [Trait("Category", "TierResolution")] attribute then trips the ban below - making the
# guardrail UNSATISFIABLE every attempt. That is the exact failure wave 2 measured and fixed;
# this form cannot regress into it.
$bs = [char]92
$code = [regex]::Replace($code, ([string][char]34 + '(' + $bs + $bs + '.|[^' + [string][char]34 + $bs + $bs + '])*' + [string][char]34), [string][char]34 + [string][char]34)   # ordinary strings - kills the Trait value

if ($code -cnotmatch 'class\s+Stage2PlanHarness\b') {
    $failures += 'no `class Stage2PlanHarness` declaration - task 06 and its guardrails reference that exact type name'
}

# --- (a) the capability, keyed on the PINNED names (measured zero before pinning) ----------------
if ($code -cnotmatch '\bStage2GuardrailSpec\b') {
    $failures += 'no Stage2GuardrailSpec in real code - that is the PINNED name for the record describing a judge guardrail on a task spec. Without a way to DECLARE one, the harness still emits only the deterministic 01-ok stub and every wave-3 conformance clause is unwritable.'
}
if ($code -cnotmatch '\bJudgeGuardrail\b') {
    $failures += 'no JudgeGuardrail member in real code - that is the PINNED name on Stage2TaskSpec carrying the judge guardrail declaration. Task 06 writes specs against this name.'
}
# The verdict contract: a fake that never reads GUARDRAILS_VERDICT_OUT writes no verdict, and a
# prompt guardrail with no verdict FAILS by contract - so every clause task 06 builds would die for a
# reason unrelated to routing. Checked over the RAW text: here the name legitimately appears as a
# string literal key into invocation.Environment, which the stripped scan would have removed.
if ($raw -cnotmatch 'GUARDRAILS_VERDICT_OUT') {
    $failures += 'the harness never mentions GUARDRAILS_VERDICT_OUT - a prompt guardrail passes or fails SOLELY by its verdict file (a missing verdict is a contractual FAIL), so the fake runner must read that env var and write the verdict. FakeClaudePlanBuilder.cs in this same project already does exactly this; follow it.'
}

# The harness must actually WRITE a prompt guardrail, keyed on the FILENAME and nothing else.
#
# THIS CLAUSE FALSE-RED'D A CORRECT IMPLEMENTATION and cost this task two attempts. Its first form
# demanded the literal "guardrails" and prompt.md inside ONE STATEMENT, to stop a dotall window
# matching CreateDirectory(..."guardrails")) against the pre-existing "action.prompt.md" two lines
# later. But the natural implementation hoists the directory into a local -
#     string guardrailsDir = Path.Combine(taskDir, "guardrails");
#     File.WriteAllText(Path.Combine(guardrailsDir, basename + ".prompt.md"), body);
# - which puts a semicolon between them. The agent wrote exactly that, correctly, and was told it
# had never written a .prompt.md at all. A guardrail must never constrain the SHAPE of correct code.
#
# The filename is the honest discriminator: the ONLY pre-existing prompt.md in this file is
# action.prompt.md (twice - the manifest path and the write), so a negative lookbehind for the
# `action.` prefix distinguishes the new guardrail file from the existing action file no matter how
# the path is assembled. Measured on the untouched harness: 0 matches.
if ($raw -cnotmatch '(?<!action\.)prompt\.md') {
    $failures += 'the harness never writes a .prompt.md INTO a task guardrails/ folder - it still emits only the deterministic 01-ok.cmd/.sh stub, so a plan spec cannot declare a prompt-JUDGE guardrail and every wave-3 conformance clause is unwritable. (The "action.prompt.md" it already writes is the ACTION, at the task root - not a guardrail.)'
}

# --- (b) the prohibition, fail-on-present over STRIPPED source (#176 negative assertion) ---------
if ($code -cmatch 'TierResolver|TierResolution') {
    $failures += 'the harness CONSULTS TierResolver/TierResolution in real code - FORBIDDEN. Asking the resolver what it would have chosen makes every clause built on this harness PASS against an unwired GuardrailRunner: it proves the resolver, not the wiring. Observe the route through the JOURNAL and the CAPTURED INVOCATION instead. (Explaining the rule in a comment or a string is fine - this scan strips both.)'
}

if ($failures.Count -gt 0) {
    Write-Output ""
    Write-Output "=== Stage2PlanHarness judge capability: $($failures.Count) finding(s) ==="
    $failures | ForEach-Object { Write-Output "  - $_" }
    Write-Output ""
    Write-Output "This harness is the real-seam host the whole wave rests on: it drives the REAL PlanLoader/TaskExecutor/Scheduler and fakes only IPromptRunner, the process boundary. Extending it is this task's entire deliverable; authoring clauses on top of it is task 06's."
    exit 1
}

# --- (c) the BEHAVIOURAL half: the existing suite still passes THROUGH the modified harness -------
# Everything above is structure and can be satisfied by declarations that do nothing. This clause is
# what makes the task's deliverable real: wave 2's conformance facts run on this exact host, so a
# harness that compiles but no longer DRIVES the seam correctly fails here. Scoped to the existing
# conformance class by name (#455) - not the project, not the trait.
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
Write-Output "Running the existing Stage2 conformance suite through the modified harness..."
$testOut = dotnet test tests/Guardrails.Integration.Tests --filter 'FullyQualifiedName~Stage2ConformanceTests' --nologo 2>&1
$testExit = $LASTEXITCODE
if ($testExit -ne 0) {
    # #179: the WHY must reach the ~60-line retry-feedback tail, so re-emit the failure detail LAST.
    $detail = ($testOut | Out-String) -split "`r?`n" | Where-Object {
        $_ -match '\[FAIL\]|Assert\.|Expected:|Actual:|error CS|Exception|at Guardrails'
    }
    Write-Output ""
    Write-Output "=== the existing Stage2 conformance suite BROKE on the modified harness (exit $testExit) ==="
    Write-Output "This task EXTENDS the harness; it does not replace it. Every wave-2 clause runs on this host."
    Write-Output ""
    if ($detail) { $detail | Select-Object -Last 40 | ForEach-Object { Write-Output $_ } }
    else { ($testOut | Out-String) -split "`r?`n" | Select-Object -Last 40 | ForEach-Object { Write-Output $_ } }
    exit 1
}
Write-Output "existing conformance suite still green through the extended harness."
exit 0
