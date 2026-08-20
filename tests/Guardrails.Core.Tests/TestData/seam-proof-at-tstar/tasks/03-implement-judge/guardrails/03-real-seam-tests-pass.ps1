# catches: a component that passes its unit tests against a faked IPromptRunner but is
#          broken through the real ClaudePromptRunner (passing-but-blind) - CriticalityJudge
#          green against a fake IPromptRunner but throwing on the real ClaudePromptRunner
#          StreamLogPath, so it safe-defaults to Escalate 100% of the time.
#
#          Drives the REAL adapter over a stub CLI process (bucket E, one real level down),
#          never a fake of the in-process IPromptRunner seam itself, and asserts an effect
#          only the production implementation emits (the stream-log FILE on disk).
#
# FIXTURE: loaded by the placement meta-test, never executed. A real emission carries the
#          dotnet.md 10e `dotnet test --filter` body in the 4.2 capture-then-re-emit form.
exit 0
