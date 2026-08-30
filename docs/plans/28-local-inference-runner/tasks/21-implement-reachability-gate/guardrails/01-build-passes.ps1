# catches: a task that leaves the solution non-compiling. Cheapest gate, run first: every other
#          guardrail on this task is meaningless over a tree that does not build, and for this task a compile failure means the extracted frontmatter helper did not replace both call sites cleanly.
$ErrorActionPreference = 'Continue'

$log = & dotnet build Guardrails.sln -c Debug --nologo -v q 2>&1 | Out-String
$code = $LASTEXITCODE

Write-Output $log

if ($code -ne 0) {
    Write-Output ""
    Write-Output "=== Compiler errors (re-emitted at the end so the WHY reaches the retry feedback) ==="
    foreach ($line in ($log -split "`r?`n")) {
        if ($line -match 'error [A-Z]+[0-9]+') { Write-Output $line }
    }
    Write-Output ""
    Write-Output "The solution does not build. Fix the compiler errors above before anything else on this task can be verified."
    exit 1
}

Write-Output "Solution builds."
exit 0
