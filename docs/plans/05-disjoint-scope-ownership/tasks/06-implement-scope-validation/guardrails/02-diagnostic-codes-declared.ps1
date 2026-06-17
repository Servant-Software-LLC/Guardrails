# catches: the three diagnostics being implemented under ad-hoc/wrong code strings rather than
#          the canonical GR2015/GR2016/GR2017 the design and downstream tasks (M7's clean-removal
#          grep, guardrails-review) depend on. Scoped to the one constants file this task owns.
$codes = "src/Guardrails.Core/Loading/DiagnosticCodes.cs"
$code = Get-Content $codes -Raw
foreach ($gr in @('GR2015', 'GR2016', 'GR2017')) {
    if ($code -notmatch ('"' + $gr + '"')) {
        Write-Output "$codes does not declare the $gr diagnostic-code constant - the scope diagnostics are not wired to their canonical codes."
        exit 1
    }
}
exit 0
