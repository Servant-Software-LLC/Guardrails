# catches: a reply-channel wire that does not compile — an ActionRunner change that breaks the sole
#          PromptComposer.ComposeAction call site, a PromptComposer signature change that breaks another
#          consumer, an AnswerFileConsumer misuse, or an unused local/field (the repo builds with
#          TreatWarningsAsErrors=true, so an unused variable is a hard failure). A whole-SOLUTION build so a
#          Core change that breaks the CLI or a test project is caught here, cheaply, before the tests run.
dotnet build Guardrails.sln -c Debug --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Output "the solution does not build — the Scheduler resume / ActionRunner→PromptComposer injection wire has a compile error (a broken ComposeAction call site, an unused local under TreatWarningsAsErrors, or a missing/misused symbol). Fix it before the classify-then-act facts can run."
    exit 1
}
exit 0
