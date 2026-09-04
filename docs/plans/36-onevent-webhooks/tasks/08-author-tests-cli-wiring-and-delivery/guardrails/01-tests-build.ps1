# catches: a test file or a CLI stub that does not COMPILE. Guardrail 02 reads the runner's own TRX, and
#          a test host that never started produces no TRX at all - so without this the real failure
#          surfaces as "the test run did not happen" instead of the compiler's own message, aimed at the
#          one artifact a retry agent is allowed to edit.
#
#          The project built here is deliberately tests/Guardrails.Integration.Tests, not
#          src/Guardrails.Cli: this task edits BOTH RunCommand.cs (the BuildObserverChain signature) and
#          a file in the Integration test project that drives it. The Integration project carries a
#          ProjectReference to Guardrails.Cli AND Guardrails.Core, so building it compiles this task's
#          production edit and everything downstream of it in one pass. Building the Cli alone would
#          leave the new test file UNCOMPILED, and a signature change that breaks it would sail past
#          this check and surface only when guardrail 02 tries to run - the #176
#          transitive-compile-dependency trap.
#
# -v q is correct HERE (a dotnet BUILD): it strips restore/banner chatter and leaves the compiler errors.
# It is NOT carried onto the dotnet test in guardrail 02 - there it would delete the very block the #179
# re-emit exists to surface.
# Measured baseline (#478): n/a - exit-code check, no required-present clause.
dotnet build tests/Guardrails.Integration.Tests --nologo -v q 2>&1 | ForEach-Object { Write-Output $_ }
if ($LASTEXITCODE -ne 0) {
    # The escape here is a BACKTICK, not a backslash: PowerShell reads \" as a literal backslash followed
    # by a string TERMINATOR, so the C-style form silently splits this message into fragments. Measured
    # on the author-time smoke test - the message printed mangled across four lines while still exiting 1,
    # which is the quietest possible way for retry feedback to become unreadable.
    Write-Output "tests/Guardrails.Integration.Tests does not build - WebhookDeliveryTests.cs does not compile, or the two parameters you added to RunCommand.BuildObserverChain broke one of its call sites. Both files are IN your write scope; fix them against the compiler errors above. If the error names a symbol owned by a file you may NOT edit (RunEventStream.cs, WebhookEventSink.cs), that is an upstream delivery problem: write {`"needsHuman`": `"<what is missing>`"} to the state-out path rather than editing it."
    exit 1
}
exit 0
